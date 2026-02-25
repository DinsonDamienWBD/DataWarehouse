using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Identity;

/// <summary>
/// Validates and enforces VDE-within-VDE nesting depth limits. VDE files can be nested
/// up to <see cref="MaxNestingDepth"/> levels deep (a VDE containing a VDE containing a VDE).
/// Nesting depth is tracked in the ExtendedMetadata FabricNamespaceRoot field (byte 0)
/// as a convention, and can also be detected heuristically by scanning for DWVD magic signatures.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 74: VDE Identity -- nesting validator (VTMP-10)")]
public static class VdeNestingValidator
{
    /// <summary>Maximum allowed VDE nesting depth (3 levels: outer, inner, innermost).</summary>
    public const int MaxNestingDepth = 3;

    /// <summary>"DWVD" as big-endian uint32 for magic signature detection.</summary>
    private const uint DwvdMagicUint = 0x44575644;

    /// <summary>
    /// Validates that the specified nesting depth does not exceed the maximum allowed depth.
    /// </summary>
    /// <param name="currentDepth">The current nesting depth to validate (1-based: 1 = no nesting).</param>
    /// <returns>True if the depth is within the allowed limit; false otherwise.</returns>
    public static bool ValidateNestingDepth(int currentDepth)
    {
        return currentDepth <= MaxNestingDepth;
    }

    /// <summary>
    /// Validates the nesting depth and throws <see cref="VdeNestingDepthExceededException"/>
    /// if the depth exceeds the maximum.
    /// </summary>
    /// <param name="currentDepth">The current nesting depth to validate (1-based).</param>
    /// <exception cref="VdeNestingDepthExceededException">
    /// Thrown when <paramref name="currentDepth"/> exceeds <see cref="MaxNestingDepth"/>.
    /// </exception>
    public static void ValidateOrThrow(int currentDepth)
    {
        if (currentDepth > MaxNestingDepth)
        {
            throw new VdeNestingDepthExceededException(
                $"VDE nesting depth {currentDepth} exceeds maximum allowed depth of {MaxNestingDepth}. " +
                $"VDE files can be nested up to {MaxNestingDepth} levels deep.");
        }
    }

    /// <summary>
    /// Heuristically detects the nesting depth of a VDE by scanning for DWVD magic signatures
    /// in the data region. This scans block-aligned positions looking for the 4-byte magic
    /// "DWVD" (0x44575644) that starts every VDE file.
    /// </summary>
    /// <remarks>
    /// This is a best-effort heuristic scan. Production code should prefer
    /// <see cref="GetStoredNestingDepth"/> which reads the tracked depth from metadata.
    /// The scan reads block 0 positions within the stream to detect nested VDE headers.
    /// </remarks>
    /// <param name="outerVdeStream">The stream of the outer VDE file.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <returns>
    /// The maximum detected nesting depth (1 = no nesting detected, 2 = one inner VDE, etc.).
    /// </returns>
    public static int DetectNestingDepth(Stream outerVdeStream, int blockSize)
    {
        if (outerVdeStream is null) throw new ArgumentNullException(nameof(outerVdeStream));
        if (blockSize < FormatConstants.MinBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize), $"Block size must be at least {FormatConstants.MinBlockSize} bytes.");

        int depth = 1; // The outer VDE itself is depth 1
        var magicBuffer = new byte[4];

        // Scan the data region for nested VDE magic signatures.
        // Region directory ends at block 10 (blocks 8-9), data starts after that.
        // We scan each block-aligned position in the data region.
        long dataStartOffset = (FormatConstants.RegionDirectoryStartBlock + FormatConstants.RegionDirectoryBlocks) * blockSize;
        long streamLength = outerVdeStream.Length;

        int currentDepth = ScanForNestedMagic(outerVdeStream, magicBuffer, dataStartOffset, streamLength, blockSize);
        depth = Math.Max(depth, 1 + currentDepth);

        return Math.Min(depth, MaxNestingDepth + 1); // Cap to avoid infinite scan
    }

    /// <summary>
    /// Gets the stored nesting depth from the ExtendedMetadata's FabricNamespaceRoot field.
    /// Convention: byte 0 of FabricNamespaceRoot stores the nesting depth counter.
    /// </summary>
    /// <param name="metadata">The extended metadata to read from.</param>
    /// <returns>The stored nesting depth (0 if not set or no nesting).</returns>
    public static byte GetStoredNestingDepth(in ExtendedMetadata metadata)
    {
        if (metadata.FabricNamespaceRoot is null || metadata.FabricNamespaceRoot.Length == 0)
            return 0;

        return metadata.FabricNamespaceRoot[0];
    }

    /// <summary>
    /// Sets the nesting depth in a FabricNamespaceRoot byte array.
    /// Convention: byte 0 of FabricNamespaceRoot stores the nesting depth counter.
    /// </summary>
    /// <param name="fabricNamespaceRoot">
    /// The FabricNamespaceRoot buffer to modify (must be at least 1 byte).
    /// </param>
    /// <param name="depth">The nesting depth to store.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="fabricNamespaceRoot"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="fabricNamespaceRoot"/> is empty.
    /// </exception>
    public static void SetNestingDepth(byte[] fabricNamespaceRoot, byte depth)
    {
        if (fabricNamespaceRoot is null)
            throw new ArgumentNullException(nameof(fabricNamespaceRoot));
        if (fabricNamespaceRoot.Length == 0)
            throw new ArgumentException("FabricNamespaceRoot buffer must not be empty.", nameof(fabricNamespaceRoot));

        fabricNamespaceRoot[0] = depth;
    }

    /// <summary>
    /// Computes the nesting depth for an inner VDE being created inside the specified outer VDE.
    /// Returns the outer's stored depth + 1. The caller should call <see cref="ValidateOrThrow"/>
    /// with the result before proceeding.
    /// </summary>
    /// <param name="outerMetadata">The extended metadata of the outer (containing) VDE.</param>
    /// <returns>The nesting depth the inner VDE should be assigned.</returns>
    public static byte IncrementNestingForInnerVde(in ExtendedMetadata outerMetadata)
    {
        byte outerDepth = GetStoredNestingDepth(outerMetadata);
        return (byte)(outerDepth + 1);
    }

    // ── Private Helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Scans a region of a stream for DWVD magic signatures at block-aligned positions.
    /// Returns the number of nesting levels detected (0 = none found).
    /// </summary>
    private static int ScanForNestedMagic(
        Stream stream,
        byte[] magicBuffer,
        long startOffset,
        long streamLength,
        int blockSize)
    {
        // Limit scan to prevent excessive I/O on large volumes
        const int maxBlocksToScan = 4096;
        int blocksScanned = 0;

        for (long offset = startOffset; offset + 4 <= streamLength && blocksScanned < maxBlocksToScan; offset += blockSize)
        {
            stream.Seek(offset, SeekOrigin.Begin);
            int totalRead = 0;
            while (totalRead < 4)
            {
                int bytesRead = stream.Read(magicBuffer, totalRead, 4 - totalRead);
                if (bytesRead == 0) return 0;
                totalRead += bytesRead;
            }

            uint magic = BinaryPrimitives.ReadUInt32BigEndian(magicBuffer);
            if (magic == DwvdMagicUint)
            {
                // Found a nested VDE header -- check one level deeper
                // The inner VDE's data region starts after its own header blocks
                long innerDataStart = offset + ((FormatConstants.RegionDirectoryStartBlock + FormatConstants.RegionDirectoryBlocks) * blockSize);
                if (innerDataStart < streamLength)
                {
                    return 1 + ScanForNestedMagic(stream, magicBuffer, innerDataStart, streamLength, blockSize);
                }
                return 1;
            }

            blocksScanned++;
        }

        return 0;
    }
}
