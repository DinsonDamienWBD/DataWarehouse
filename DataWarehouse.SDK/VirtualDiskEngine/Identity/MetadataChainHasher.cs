using System.Security.Cryptography;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Identity;

/// <summary>
/// Computes a rolling SHA-256 chain hash across all metadata region header blocks.
/// The chain hash covers every metadata region's first block in ascending block order,
/// chaining: hash_n = SHA256(hash_{n-1} || block_content_n).
/// Superblock and data regions are excluded (the superblock has its own HMAC seal).
/// Any single-byte modification to any metadata region header is detected.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 74: VDE Identity (VTMP-04, VTMP-06)")]
public static class MetadataChainHasher
{
    /// <summary>Size of the SHA-256 chain hash in bytes.</summary>
    public const int HashSize = 32;

    /// <summary>
    /// Region names excluded from the metadata chain hash.
    /// Superblock regions have their own HMAC seal; data regions are not metadata.
    /// </summary>
    private static readonly HashSet<string> ExcludedRegions = new(StringComparer.OrdinalIgnoreCase)
    {
        "PrimarySuperblock",
        "MirrorSuperblock",
        "DataRegion"
    };

    /// <summary>
    /// Computes the rolling SHA-256 chain hash over all metadata region header blocks.
    /// </summary>
    /// <param name="vdeStream">Seekable stream of the VDE file.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="regions">
    /// Region directory mapping region name to (StartBlock, BlockCount).
    /// Regions named "PrimarySuperblock", "MirrorSuperblock", and "DataRegion" are skipped.
    /// </param>
    /// <returns>
    /// A tuple of (chainHash, generation, timestamp) where chainHash is the 32-byte
    /// rolling hash, generation is incremented, and timestamp is the current UTC ticks.
    /// </returns>
    public static (byte[] ChainHash, long Generation, long Timestamp) ComputeChainHash(
        Stream vdeStream,
        int blockSize,
        IReadOnlyDictionary<string, (long StartBlock, long BlockCount)> regions)
    {
        ArgumentNullException.ThrowIfNull(vdeStream);
        ArgumentNullException.ThrowIfNull(regions);

        if (!vdeStream.CanSeek)
            throw new ArgumentException("Stream must be seekable.", nameof(vdeStream));

        // Sort metadata regions by start block for deterministic ordering
        var sortedRegions = regions
            .Where(kvp => !ExcludedRegions.Contains(kvp.Key) && kvp.Value.BlockCount > 0)
            .OrderBy(kvp => kvp.Value.StartBlock)
            .ToList();

        // Initial hash: 32 zero bytes
        byte[] previousHash = new byte[HashSize];
        byte[] blockBuffer = new byte[blockSize];

        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        foreach (var (_, (startBlock, _)) in sortedRegions)
        {
            // Read the first (header) block of each metadata region
            long byteOffset = startBlock * blockSize;
            vdeStream.Seek(byteOffset, SeekOrigin.Begin);

            int bytesRead = 0;
            while (bytesRead < blockSize)
            {
                int read = vdeStream.Read(blockBuffer, bytesRead, blockSize - bytesRead);
                if (read == 0)
                    throw new EndOfStreamException(
                        $"Unexpected end of VDE stream at block {startBlock} (offset {byteOffset + bytesRead}).");
                bytesRead += read;
            }

            // Chain: currentHash = SHA256(previousHash || blockBytes)
            sha256.AppendData(previousHash);
            sha256.AppendData(blockBuffer);
            previousHash = sha256.GetHashAndReset();
        }

        return (previousHash, 1L, DateTimeOffset.UtcNow.Ticks);
    }

    /// <summary>
    /// Validates the metadata chain hash by recomputing it and comparing with the stored hash
    /// using constant-time comparison.
    /// </summary>
    /// <param name="vdeStream">Seekable stream of the VDE file.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="regions">Region directory mapping region name to (StartBlock, BlockCount).</param>
    /// <param name="storedHash">The previously stored 32-byte chain hash to compare against.</param>
    /// <returns>True if the recomputed chain hash matches the stored hash; false otherwise.</returns>
    public static bool ValidateChainHash(
        Stream vdeStream,
        int blockSize,
        IReadOnlyDictionary<string, (long StartBlock, long BlockCount)> regions,
        ReadOnlySpan<byte> storedHash)
    {
        var (computed, _, _) = ComputeChainHash(vdeStream, blockSize, regions);
        return CryptographicOperations.FixedTimeEquals(computed, storedHash);
    }

    /// <summary>
    /// Validates the metadata chain hash, throwing <see cref="VdeTamperDetectedException"/>
    /// if there is a mismatch.
    /// </summary>
    /// <param name="vdeStream">Seekable stream of the VDE file.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="regions">Region directory mapping region name to (StartBlock, BlockCount).</param>
    /// <param name="storedHash">The previously stored 32-byte chain hash to compare against.</param>
    /// <exception cref="VdeTamperDetectedException">
    /// The recomputed chain hash does not match the stored value.
    /// </exception>
    public static void ValidateOrThrow(
        Stream vdeStream,
        int blockSize,
        IReadOnlyDictionary<string, (long StartBlock, long BlockCount)> regions,
        ReadOnlySpan<byte> storedHash)
    {
        if (!ValidateChainHash(vdeStream, blockSize, regions, storedHash))
        {
            throw new VdeTamperDetectedException(
                "Metadata chain hash mismatch: one or more metadata regions have been modified " +
                "since last integrity checkpoint");
        }
    }
}
