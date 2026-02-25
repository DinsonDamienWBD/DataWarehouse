using System.Security.Cryptography;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Identity;

/// <summary>
/// Computes and verifies an HMAC-SHA256 integrity seal over the superblock payload.
/// The seal covers all superblock content except the seal itself and the universal block trailer,
/// i.e. bytes [0..sealOffset) where sealOffset = blockSize - TrailerSize - SealSize.
/// This is the first open-time integrity gate: any modification to the superblock header
/// is detected before the VDE is used.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 74: VDE Identity (VTMP-03, VTMP-05)")]
public static class HeaderIntegritySeal
{
    /// <summary>Size of the HMAC-SHA256 seal in bytes.</summary>
    public const int SealSize = SuperblockV2.IntegritySealSize; // 32

    /// <summary>
    /// Computes the HMAC-SHA256 seal over the superblock payload bytes [0..sealOffset).
    /// </summary>
    /// <param name="superblockBlock">The full superblock block buffer.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="hmacKey">HMAC-SHA256 key material.</param>
    /// <returns>A 32-byte HMAC-SHA256 digest.</returns>
    public static byte[] ComputeSeal(ReadOnlySpan<byte> superblockBlock, int blockSize, byte[] hmacKey)
    {
        ArgumentNullException.ThrowIfNull(hmacKey);

        int sealOffset = blockSize - FormatConstants.UniversalBlockTrailerSize - SealSize;
        if (sealOffset <= 0)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                "Block size is too small to contain a seal and trailer.");

        var payload = superblockBlock.Slice(0, sealOffset);
        return HMACSHA256.HashData(hmacKey, payload);
    }

    /// <summary>
    /// Computes the HMAC-SHA256 seal and writes it into the superblock block at the seal offset.
    /// Does NOT write the universal block trailer (that happens separately).
    /// </summary>
    /// <param name="superblockBlock">The full superblock block buffer (writable).</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="hmacKey">HMAC-SHA256 key material.</param>
    public static void WriteSeal(Span<byte> superblockBlock, int blockSize, byte[] hmacKey)
    {
        ArgumentNullException.ThrowIfNull(hmacKey);

        int sealOffset = blockSize - FormatConstants.UniversalBlockTrailerSize - SealSize;
        if (sealOffset <= 0)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                "Block size is too small to contain a seal and trailer.");

        ReadOnlySpan<byte> payload = superblockBlock.Slice(0, sealOffset);
        var seal = HMACSHA256.HashData(hmacKey, payload);
        seal.AsSpan().CopyTo(superblockBlock.Slice(sealOffset, SealSize));
    }

    /// <summary>
    /// Recomputes the HMAC-SHA256 seal and compares it with the stored seal using
    /// constant-time comparison to prevent timing side-channel attacks.
    /// </summary>
    /// <param name="superblockBlock">The full superblock block buffer.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="hmacKey">HMAC-SHA256 key material.</param>
    /// <returns>True if the stored seal matches the recomputed seal; false otherwise.</returns>
    public static bool VerifySeal(ReadOnlySpan<byte> superblockBlock, int blockSize, byte[] hmacKey)
    {
        ArgumentNullException.ThrowIfNull(hmacKey);

        int sealOffset = blockSize - FormatConstants.UniversalBlockTrailerSize - SealSize;
        if (sealOffset <= 0)
            return false;

        var payload = superblockBlock.Slice(0, sealOffset);
        var computed = HMACSHA256.HashData(hmacKey, payload);
        var stored = superblockBlock.Slice(sealOffset, SealSize);

        return CryptographicOperations.FixedTimeEquals(computed, stored);
    }

    /// <summary>
    /// Verifies the HMAC-SHA256 seal, throwing <see cref="VdeTamperDetectedException"/>
    /// if verification fails.
    /// </summary>
    /// <param name="superblockBlock">The full superblock block buffer.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="hmacKey">HMAC-SHA256 key material.</param>
    /// <exception cref="VdeTamperDetectedException">The header integrity seal does not match.</exception>
    public static void VerifyOrThrow(ReadOnlySpan<byte> superblockBlock, int blockSize, byte[] hmacKey)
    {
        if (!VerifySeal(superblockBlock, blockSize, hmacKey))
        {
            throw new VdeTamperDetectedException(
                "Header integrity seal verification failed: superblock has been modified");
        }
    }
}
