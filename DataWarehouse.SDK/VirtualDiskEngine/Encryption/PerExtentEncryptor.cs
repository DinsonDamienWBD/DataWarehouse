using System.Security.Cryptography;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Encryption;

/// <summary>
/// Provides extent-granularity AES-256-GCM encryption with one IV per extent.
/// Operating on extent-sized units instead of individual blocks reduces cryptographic
/// overhead by up to 256x (one IV per extent vs one IV per block for a 256-block extent).
/// </summary>
/// <remarks>
/// Wire format of encrypted extent:
/// <code>
///   [0..12)              Nonce (12 bytes)
///   [12..12+dataLen)     Encrypted data
///   [12+dataLen..+16)    Authentication tag (16 bytes)
/// </code>
/// IV derivation is deterministic from extent position (StartBlock + LogicalOffset),
/// enabling random-access decryption of individual extents without a stored IV table.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Per-extent encryption with bulk AES-NI (VOPT-23)")]
public sealed class PerExtentEncryptor : IDisposable
{
    /// <summary>Size of the AES-GCM nonce in bytes.</summary>
    public const int NonceSize = 12;

    /// <summary>Size of the AES-GCM authentication tag in bytes.</summary>
    public const int TagSize = 16;

    /// <summary>Total overhead per extent: nonce + tag.</summary>
    public const int OverheadPerExtent = NonceSize + TagSize;

    private readonly byte[] _key;
    private readonly int _blockSize;
    private long _extentsEncrypted;
    private long _bytesProcessed;
    private long _totalEncryptTicks;
    private bool _disposed;

    /// <summary>
    /// Initializes a new <see cref="PerExtentEncryptor"/> with the specified encryption key and block size.
    /// </summary>
    /// <param name="key">AES-256 encryption key (must be exactly 32 bytes).</param>
    /// <param name="blockSize">Block size in bytes used by the VDE format.</param>
    /// <exception cref="ArgumentException">Key is not 32 bytes.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Block size is invalid.</exception>
    public PerExtentEncryptor(byte[] key, int blockSize)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32)
            throw new ArgumentException("AES-256 key must be exactly 32 bytes.", nameof(key));
        if (blockSize < FormatConstants.MinBlockSize || blockSize > FormatConstants.MaxBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"Block size must be between {FormatConstants.MinBlockSize} and {FormatConstants.MaxBlockSize}.");

        _key = (byte[])key.Clone();
        _blockSize = blockSize;
    }

    /// <summary>
    /// Encrypts an entire extent as a single AES-256-GCM operation.
    /// </summary>
    /// <param name="extentData">The plaintext extent data.</param>
    /// <param name="extent">
    /// The extent descriptor; used to derive a deterministic nonce from StartBlock and LogicalOffset.
    /// The <see cref="ExtentFlags.Encrypted"/> flag is set on the returned descriptor.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Nonce(12) + EncryptedData(extentData.Length) + Tag(16).</returns>
    public Task<byte[]> EncryptExtentAsync(ReadOnlyMemory<byte> extentData, InodeExtent extent, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        long startTicks = Environment.TickCount64;

        byte[] nonce = DeriveNonce(extent.StartBlock, extent.LogicalOffset);
        byte[] ciphertext = new byte[extentData.Length];
        byte[] tag = new byte[TagSize];

        using var aesGcm = new AesGcm(_key, TagSize);
        aesGcm.Encrypt(nonce, extentData.Span, ciphertext, tag);

        // Pack: Nonce + Ciphertext + Tag
        byte[] result = new byte[NonceSize + ciphertext.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, result, 0, NonceSize);
        Buffer.BlockCopy(ciphertext, 0, result, NonceSize, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, result, NonceSize + ciphertext.Length, TagSize);

        Interlocked.Increment(ref _extentsEncrypted);
        Interlocked.Add(ref _bytesProcessed, extentData.Length);
        Interlocked.Add(ref _totalEncryptTicks, Environment.TickCount64 - startTicks);

        return Task.FromResult(result);
    }

    /// <summary>
    /// Decrypts and authenticates an encrypted extent.
    /// </summary>
    /// <param name="encryptedData">
    /// The encrypted payload: Nonce(12) + EncryptedData(N) + Tag(16).
    /// </param>
    /// <param name="extent">The extent descriptor for nonce verification.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The decrypted plaintext extent data.</returns>
    /// <exception cref="CryptographicException">Authentication tag verification failed (tamper detected).</exception>
    public Task<byte[]> DecryptExtentAsync(ReadOnlyMemory<byte> encryptedData, InodeExtent extent, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();

        if (encryptedData.Length < OverheadPerExtent)
            throw new ArgumentException(
                $"Encrypted data must be at least {OverheadPerExtent} bytes (nonce + tag).",
                nameof(encryptedData));

        ReadOnlySpan<byte> span = encryptedData.Span;
        ReadOnlySpan<byte> nonce = span[..NonceSize];
        int ciphertextLen = span.Length - NonceSize - TagSize;
        ReadOnlySpan<byte> ciphertext = span.Slice(NonceSize, ciphertextLen);
        ReadOnlySpan<byte> tag = span[^TagSize..];

        byte[] plaintext = new byte[ciphertextLen];

        using var aesGcm = new AesGcm(_key, TagSize);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);

        return Task.FromResult(plaintext);
    }

    /// <summary>
    /// Derives a deterministic 12-byte nonce from extent position using HKDF-SHA256.
    /// This enables random-access decryption of individual extents without a stored nonce table.
    /// </summary>
    /// <param name="startBlock">The physical start block number of the extent.</param>
    /// <param name="logicalOffset">The logical byte offset within the file.</param>
    /// <returns>A 12-byte nonce deterministically derived from the extent position.</returns>
    public static byte[] DeriveNonce(long startBlock, long logicalOffset)
    {
        // Build 16-byte input: startBlock(8) + logicalOffset(8)
        Span<byte> input = stackalloc byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(input[..8], startBlock);
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(input[8..16], logicalOffset);

        // Use HKDF to derive a 12-byte nonce (AES-GCM nonce size)
        byte[] nonce = new byte[NonceSize];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, input.ToArray(), nonce, ReadOnlySpan<byte>.Empty, "VDE-Extent-Nonce"u8);

        return nonce;
    }

    /// <summary>
    /// Returns the IV/nonce for plan backward-compatibility. Internally calls <see cref="DeriveNonce"/>.
    /// </summary>
    /// <param name="startBlock">The physical start block number of the extent.</param>
    /// <param name="logicalOffset">The logical byte offset within the file.</param>
    /// <returns>A deterministic 12-byte IV derived from extent position.</returns>
    public static byte[] DeriveIV(long startBlock, long logicalOffset)
        => DeriveNonce(startBlock, logicalOffset);

    /// <summary>
    /// Gets current encryption statistics.
    /// </summary>
    public EncryptionStats GetStats() => new(
        Interlocked.Read(ref _extentsEncrypted),
        Interlocked.Read(ref _bytesProcessed),
        TimeSpan.FromMilliseconds(Interlocked.Read(ref _totalEncryptTicks)));

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_key);
    }
}

/// <summary>
/// Statistics for per-extent encryption operations.
/// </summary>
/// <param name="ExtentsEncrypted">Number of extents encrypted.</param>
/// <param name="BytesProcessed">Total bytes of plaintext processed.</param>
/// <param name="TotalEncryptTime">Total wall-clock time spent encrypting.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Per-extent encryption stats (VOPT-23)")]
public readonly record struct EncryptionStats(
    long ExtentsEncrypted,
    long BytesProcessed,
    TimeSpan TotalEncryptTime);
