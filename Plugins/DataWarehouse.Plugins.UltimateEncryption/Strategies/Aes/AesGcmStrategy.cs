using System.Security.Cryptography;
using DataWarehouse.SDK.Contracts.Encryption;

namespace DataWarehouse.Plugins.UltimateEncryption.Strategies.Aes;

/// <summary>
/// AES-256-GCM encryption strategy.
/// Provides authenticated encryption with 256-bit keys and 128-bit authentication tags.
/// </summary>
public sealed class AesGcmStrategy : EncryptionStrategyBase
{
    private const int KeySize = 32; // 256 bits
    private const int NonceSize = 12;
    private const int TagSize = 16;

    /// <inheritdoc/>
    public override string StrategyId => "aes-256-gcm";

    /// <inheritdoc/>
    public override string StrategyName => "AES-256-GCM";

    /// <inheritdoc/>
    public override CipherInfo CipherInfo => new()
    {
        AlgorithmName = "AES-256-GCM",
        KeySizeBits = 256,
        BlockSizeBytes = 16,
        IvSizeBytes = NonceSize,
        TagSizeBytes = TagSize,
        SecurityLevel = SecurityLevel.High,
        Capabilities = new CipherCapabilities
        {
            IsAuthenticated = true,
            IsStreamable = true,
            IsHardwareAcceleratable = true,
            SupportsAead = true,
            SupportsParallelism = true,
            MinimumSecurityLevel = SecurityLevel.High
        }
    };

    /// <inheritdoc/>
    protected override Task<byte[]> EncryptCoreAsync(
        byte[] plaintext,
        byte[] key,
        byte[]? associatedData,
        CancellationToken cancellationToken)
    {
        var nonce = GenerateIv();
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aesGcm = new AesGcm(key, TagSize);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

        return Task.FromResult(CombineIvAndCiphertext(nonce, ciphertext, tag));
    }

    /// <inheritdoc/>
    protected override Task<byte[]> DecryptCoreAsync(
        byte[] ciphertext,
        byte[] key,
        byte[]? associatedData,
        CancellationToken cancellationToken)
    {
        var (nonce, encryptedData, tag) = SplitCiphertext(ciphertext);
        var plaintext = new byte[encryptedData.Length];

        using var aesGcm = new AesGcm(key, TagSize);
        aesGcm.Decrypt(nonce, encryptedData, tag!, plaintext, associatedData);

        return Task.FromResult(plaintext);
    }
}

/// <summary>
/// AES-128-GCM encryption strategy.
/// </summary>
public sealed class Aes128GcmStrategy : EncryptionStrategyBase
{
    private const int KeySize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    /// <inheritdoc/>
    public override string StrategyId => "aes-128-gcm";

    /// <inheritdoc/>
    public override string StrategyName => "AES-128-GCM";

    /// <inheritdoc/>
    public override CipherInfo CipherInfo => new()
    {
        AlgorithmName = "AES-128-GCM",
        KeySizeBits = 128,
        BlockSizeBytes = 16,
        IvSizeBytes = NonceSize,
        TagSizeBytes = TagSize,
        SecurityLevel = SecurityLevel.Standard,
        Capabilities = CipherCapabilities.AeadCipher
    };

    /// <inheritdoc/>
    protected override Task<byte[]> EncryptCoreAsync(byte[] plaintext, byte[] key, byte[]? associatedData, CancellationToken ct)
    {
        var nonce = GenerateIv();
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aesGcm = new AesGcm(key, TagSize);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

        return Task.FromResult(CombineIvAndCiphertext(nonce, ciphertext, tag));
    }

    /// <inheritdoc/>
    protected override Task<byte[]> DecryptCoreAsync(byte[] ciphertext, byte[] key, byte[]? associatedData, CancellationToken ct)
    {
        var (nonce, encryptedData, tag) = SplitCiphertext(ciphertext);
        var plaintext = new byte[encryptedData.Length];

        using var aesGcm = new AesGcm(key, TagSize);
        aesGcm.Decrypt(nonce, encryptedData, tag!, plaintext, associatedData);

        return Task.FromResult(plaintext);
    }
}

/// <summary>
/// AES-192-GCM encryption strategy.
/// </summary>
public sealed class Aes192GcmStrategy : EncryptionStrategyBase
{
    private const int KeySize = 24;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    /// <inheritdoc/>
    public override string StrategyId => "aes-192-gcm";

    /// <inheritdoc/>
    public override string StrategyName => "AES-192-GCM";

    /// <inheritdoc/>
    public override CipherInfo CipherInfo => new()
    {
        AlgorithmName = "AES-192-GCM",
        KeySizeBits = 192,
        BlockSizeBytes = 16,
        IvSizeBytes = NonceSize,
        TagSizeBytes = TagSize,
        SecurityLevel = SecurityLevel.High,
        Capabilities = CipherCapabilities.AeadCipher
    };

    /// <inheritdoc/>
    protected override Task<byte[]> EncryptCoreAsync(byte[] plaintext, byte[] key, byte[]? associatedData, CancellationToken ct)
    {
        var nonce = GenerateIv();
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var aesGcm = new AesGcm(key, TagSize);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

        return Task.FromResult(CombineIvAndCiphertext(nonce, ciphertext, tag));
    }

    /// <inheritdoc/>
    protected override Task<byte[]> DecryptCoreAsync(byte[] ciphertext, byte[] key, byte[]? associatedData, CancellationToken ct)
    {
        var (nonce, encryptedData, tag) = SplitCiphertext(ciphertext);
        var plaintext = new byte[encryptedData.Length];

        using var aesGcm = new AesGcm(key, TagSize);
        aesGcm.Decrypt(nonce, encryptedData, tag!, plaintext, associatedData);

        return Task.FromResult(plaintext);
    }
}
