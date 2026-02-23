using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.FileExtension;

/// <summary>
/// Implements the 5-step DWVD content detection priority chain (FEXT-05).
/// Steps: magic bytes -> version -> namespace anchor -> feature flags -> integrity seal.
/// Each step adds approximately 0.2 to the cumulative confidence score.
/// All pure methods (except file I/O overloads) are allocation-free on the hot path.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 79: DWVD content detector (FEXT-05)")]
public static class DwvdContentDetector
{
    /// <summary>Maximum bytes to read for content detection (one max-size block).</summary>
    private const int MaxDetectionSize = FormatConstants.MaxBlockSize;

    /// <summary>Confidence increment per detection step.</summary>
    private const float StepConfidence = 0.2f;

    /// <summary>
    /// All known incompatible feature flag bits. Used to detect unknown bits
    /// that would indicate an unrecognized format extension.
    /// </summary>
    private const IncompatibleFeatureFlags AllKnownIncompatibleFlags =
        IncompatibleFeatureFlags.EncryptionEnabled |
        IncompatibleFeatureFlags.WormRegionActive |
        IncompatibleFeatureFlags.RaidEnabled |
        IncompatibleFeatureFlags.ComputeCacheActive |
        IncompatibleFeatureFlags.FabricLinksActive |
        IncompatibleFeatureFlags.ConsensusLogActive;

    /// <summary>
    /// Primary detection entry point. Reads the provided byte data and applies
    /// the 5-step priority chain to determine if it is a valid DWVD file.
    /// Reads up to one block (max 64 KB).
    /// </summary>
    /// <param name="data">The raw bytes to analyze (typically the first block of a file).</param>
    /// <returns>A <see cref="ContentDetectionResult"/> with per-step results and confidence.</returns>
    public static ContentDetectionResult DetectFromBytes(ReadOnlySpan<byte> data)
    {
        // Need at least 16 bytes for magic signature
        if (data.Length < FormatConstants.MagicSignatureSize)
            return ContentDetectionResult.NotDwvd();

        // ── Step 1: Magic (bytes 0-3) ──────────────────────────────────
        MagicSignature magic;
        try
        {
            magic = MagicSignature.Deserialize(data);
        }
        catch
        {
            return ContentDetectionResult.NotDwvd();
        }

        if (!magic.Validate())
            return ContentDetectionResult.NotDwvd();

        // Magic is valid: base confidence 0.2
        float confidence = StepConfidence;
        var highestStep = ContentDetectionStep.Magic;
        bool hasValidVersion = false;
        bool hasValidNamespace = false;
        bool hasValidFlags = false;
        bool hasValidSeal = false;
        byte majorVersion = magic.MajorVersion;
        byte minorVersion = magic.MinorVersion;
        ushort specRevision = magic.SpecRevision;

        // ── Step 2: Version (bytes 4-7) ────────────────────────────────
        if (majorVersion >= 1 && majorVersion <= 255 && specRevision > 0)
        {
            hasValidVersion = true;
            confidence += StepConfidence;
            highestStep = ContentDetectionStep.Version;
        }

        // ── Step 3: Namespace anchor (bytes 8-15) ──────────────────────
        // MagicSignature.Validate() already checks the namespace anchor, but
        // we confirm it explicitly via the deserialized NamespaceAnchor field.
        // The Validate() method checks FormatIdentifier AND NamespaceAnchor,
        // so if we reached here, the anchor is valid.
        hasValidNamespace = true;
        confidence += StepConfidence;
        highestStep = ContentDetectionStep.Namespace;

        // ── Step 4: Feature flags (bytes 16-31) ────────────────────────
        // Need at least 32 bytes (16 magic + 16 FormatVersionInfo)
        if (data.Length >= FormatConstants.MagicSignatureSize + FormatVersionInfo.SerializedSize)
        {
            try
            {
                var versionInfo = FormatVersionInfo.Deserialize(
                    data.Slice(FormatConstants.MagicSignatureSize));

                // Check that no unknown incompatible feature bits are set
                var unknownBits = versionInfo.IncompatibleFeatures & ~AllKnownIncompatibleFlags;
                if (unknownBits == 0)
                {
                    hasValidFlags = true;
                    confidence += StepConfidence;
                    highestStep = ContentDetectionStep.Flags;
                }
            }
            catch
            {
                // Deserialization failed; flags step does not pass
            }
        }

        // ── Step 5: Integrity seal (requires full superblock) ──────────
        // Read block size from offset 0x34 and check if we have enough data
        const int blockSizeOffset = 0x34;
        if (data.Length >= blockSizeOffset + 4)
        {
            int blockSize = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(blockSizeOffset));
            if (blockSize >= FormatConstants.MinBlockSize &&
                blockSize <= FormatConstants.MaxBlockSize &&
                data.Length >= blockSize)
            {
                try
                {
                    var superblock = SuperblockV2.Deserialize(data, blockSize);

                    // Check that the integrity seal is non-zero
                    if (superblock.HeaderIntegritySeal is { Length: > 0 } seal &&
                        !IsAllZero(seal))
                    {
                        hasValidSeal = true;
                        confidence += StepConfidence;
                        highestStep = ContentDetectionStep.Seal;
                    }
                }
                catch
                {
                    // Superblock deserialization failed; seal step does not pass
                }
            }
        }

        // Clamp confidence to 1.0
        if (confidence > 1.0f)
            confidence = 1.0f;

        var summary = $"DWVD v{majorVersion}.{minorVersion} rev{specRevision} detected " +
                      $"(confidence: {confidence:P0}, highest step: {highestStep})";

        return new ContentDetectionResult(
            isDwvd: true,
            confidence: confidence,
            majorVersion: majorVersion,
            minorVersion: minorVersion,
            specRevision: specRevision,
            detectedMimeType: DwvdMimeType.MimeType,
            highestStep: highestStep,
            hasValidMagic: true,
            hasValidVersion: hasValidVersion,
            hasValidNamespace: hasValidNamespace,
            hasValidFlags: hasValidFlags,
            hasValidSeal: hasValidSeal,
            summary: summary);
    }

    /// <summary>
    /// Detects DWVD content from a stream by reading the first block (up to 64 KB).
    /// </summary>
    /// <param name="stream">The stream to read from. Must support reading.</param>
    /// <returns>A <see cref="ContentDetectionResult"/> with per-step results and confidence.</returns>
    public static ContentDetectionResult DetectFromStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var bytesToRead = MaxDetectionSize;
        if (stream.CanSeek && stream.Length < bytesToRead)
            bytesToRead = (int)stream.Length;

        if (bytesToRead < FormatConstants.MagicSignatureSize)
            return ContentDetectionResult.NotDwvd();

        var buffer = new byte[bytesToRead];
        int totalRead = 0;
        while (totalRead < bytesToRead)
        {
            int read = stream.Read(buffer, totalRead, bytesToRead - totalRead);
            if (read == 0)
                break;
            totalRead += read;
        }

        return DetectFromBytes(buffer.AsSpan(0, totalRead));
    }

    /// <summary>
    /// Asynchronously detects DWVD content from a file by reading its header.
    /// </summary>
    /// <param name="filePath">The path to the file to analyze.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="ContentDetectionResult"/> with per-step results and confidence.</returns>
    public static async Task<ContentDetectionResult> DetectAsync(
        string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: MaxDetectionSize,
            useAsync: true);

        var bytesToRead = (int)Math.Min(stream.Length, MaxDetectionSize);
        if (bytesToRead < FormatConstants.MagicSignatureSize)
            return ContentDetectionResult.NotDwvd();

        var buffer = new byte[bytesToRead];
        int totalRead = 0;
        while (totalRead < bytesToRead)
        {
            int read = await stream.ReadAsync(
                buffer.AsMemory(totalRead, bytesToRead - totalRead), ct)
                .ConfigureAwait(false);
            if (read == 0)
                break;
            totalRead += read;
        }

        return DetectFromBytes(buffer.AsSpan(0, totalRead));
    }

    /// <summary>
    /// Quick check: determines if the first 4 bytes match the DWVD magic signature.
    /// Suitable for use in file(1) magic rules or rapid pre-filtering.
    /// </summary>
    /// <param name="first16Bytes">At least 4 bytes from the start of the file.</param>
    /// <returns>True if the magic bytes match "DWVD" (0x44575644).</returns>
    public static bool IsLikelyDwvd(ReadOnlySpan<byte> first16Bytes)
    {
        if (first16Bytes.Length < 4)
            return false;

        var identifier = BinaryPrimitives.ReadUInt32BigEndian(first16Bytes);
        return identifier == 0x44575644u;
    }

    /// <summary>
    /// Convenience overload: opens a file and reads the first 16 bytes to check
    /// for the DWVD magic signature.
    /// </summary>
    /// <param name="filePath">The path to the file to check.</param>
    /// <returns>True if the file starts with the DWVD magic bytes.</returns>
    public static bool IsLikelyDwvd(string filePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);

        Span<byte> header = stackalloc byte[16];
        using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read);

        int totalRead = 0;
        while (totalRead < 16)
        {
            int read = stream.Read(header.Slice(totalRead));
            if (read == 0)
                break;
            totalRead += read;
        }

        return IsLikelyDwvd(header.Slice(0, totalRead));
    }

    /// <summary>
    /// Checks if all bytes in the span are zero.
    /// </summary>
    private static bool IsAllZero(byte[] data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] != 0)
                return false;
        }
        return true;
    }
}
