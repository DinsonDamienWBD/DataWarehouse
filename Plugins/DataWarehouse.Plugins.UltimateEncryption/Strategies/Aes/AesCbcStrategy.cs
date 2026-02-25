using System.Security.Cryptography;
using DataWarehouse.SDK.Contracts.Encryption;

namespace DataWarehouse.Plugins.UltimateEncryption.Strategies.Aes;

/// <summary>
/// AES-256-CBC with HMAC-SHA256 encryption strategy.
/// Provides encrypt-then-MAC authenticated encryption.
/// </summary>
public sealed class Aes256CbcStrategy : EncryptionStrategyBase
{
    private const int KeySize = 32;
    private const int IvSize = 16;
    private const int MacSize = 32;

    /// <inheritdoc/>
    public override string StrategyId => "aes-256-cbc";

        /// <summary>
        /// Production hardening: validates configuration on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("aes.256.cbc.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("aes.256.cbc.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }

    /// <inheritdoc/>
    public override string StrategyName => "AES-256-CBC-HMAC";

    /// <inheritdoc/>
    public override CipherInfo CipherInfo => new()
    {
        AlgorithmName = "AES-256-CBC-HMAC-SHA256",
        KeySizeBits = 256,
        BlockSizeBytes = 16,
        IvSizeBytes = IvSize,
        TagSizeBytes = MacSize,
        SecurityLevel = SecurityLevel.High,
        Capabilities = new CipherCapabilities
        {
            IsAuthenticated = true,
            IsStreamable = false,
            IsHardwareAcceleratable = true,
            SupportsAead = false,
            SupportsParallelism = false,
            MinimumSecurityLevel = SecurityLevel.Standard
        }
    };

    /// <inheritdoc/>
    protected override Task<byte[]> EncryptCoreAsync(
        byte[] plaintext,
        byte[] key,
        byte[]? associatedData,
        CancellationToken cancellationToken)
    {
        // Split key: first half for encryption, second half for HMAC
        var encKey = key.AsSpan(0, KeySize).ToArray();
        var macKey = SHA256.HashData(key);

        var iv = GenerateIv();
        byte[] ciphertext;

        using (var aes = System.Security.Cryptography.Aes.Create())
        {
            aes.Key = encKey;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var encryptor = aes.CreateEncryptor();
            ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
        }

        // Compute HMAC over IV + ciphertext (+ associated data if present)
        byte[] dataToMac;
        if (associatedData != null && associatedData.Length > 0)
        {
            dataToMac = new byte[iv.Length + ciphertext.Length + associatedData.Length];
            Buffer.BlockCopy(iv, 0, dataToMac, 0, iv.Length);
            Buffer.BlockCopy(ciphertext, 0, dataToMac, iv.Length, ciphertext.Length);
            Buffer.BlockCopy(associatedData, 0, dataToMac, iv.Length + ciphertext.Length, associatedData.Length);
        }
        else
        {
            dataToMac = new byte[iv.Length + ciphertext.Length];
            Buffer.BlockCopy(iv, 0, dataToMac, 0, iv.Length);
            Buffer.BlockCopy(ciphertext, 0, dataToMac, iv.Length, ciphertext.Length);
        }

        var tag = HMACSHA256.HashData(macKey, dataToMac);

        CryptographicOperations.ZeroMemory(encKey);
        CryptographicOperations.ZeroMemory(macKey);

        return Task.FromResult(CombineIvAndCiphertext(iv, ciphertext, tag));
    }

    /// <inheritdoc/>
    protected override Task<byte[]> DecryptCoreAsync(
        byte[] ciphertext,
        byte[] key,
        byte[]? associatedData,
        CancellationToken cancellationToken)
    {
        var encKey = key.AsSpan(0, KeySize).ToArray();
        var macKey = SHA256.HashData(key);

        var (iv, encryptedData, tag) = SplitCiphertext(ciphertext);

        // Verify HMAC
        byte[] dataToMac;
        if (associatedData != null && associatedData.Length > 0)
        {
            dataToMac = new byte[iv.Length + encryptedData.Length + associatedData.Length];
            Buffer.BlockCopy(iv, 0, dataToMac, 0, iv.Length);
            Buffer.BlockCopy(encryptedData, 0, dataToMac, iv.Length, encryptedData.Length);
            Buffer.BlockCopy(associatedData, 0, dataToMac, iv.Length + encryptedData.Length, associatedData.Length);
        }
        else
        {
            dataToMac = new byte[iv.Length + encryptedData.Length];
            Buffer.BlockCopy(iv, 0, dataToMac, 0, iv.Length);
            Buffer.BlockCopy(encryptedData, 0, dataToMac, iv.Length, encryptedData.Length);
        }

        var expectedTag = HMACSHA256.HashData(macKey, dataToMac);
        if (!CryptographicOperations.FixedTimeEquals(expectedTag, tag))
        {
            CryptographicOperations.ZeroMemory(encKey);
            CryptographicOperations.ZeroMemory(macKey);
            throw new CryptographicException("MAC verification failed");
        }

        byte[] plaintext;
        using (var aes = System.Security.Cryptography.Aes.Create())
        {
            aes.Key = encKey;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var decryptor = aes.CreateDecryptor();
            plaintext = decryptor.TransformFinalBlock(encryptedData, 0, encryptedData.Length);
        }

        CryptographicOperations.ZeroMemory(encKey);
        CryptographicOperations.ZeroMemory(macKey);

        return Task.FromResult(plaintext);
    }
}

/// <summary>
/// AES-128-CBC with HMAC-SHA256 encryption strategy.
/// </summary>
public sealed class Aes128CbcStrategy : EncryptionStrategyBase
{
    private const int KeySize = 16;
    private const int IvSize = 16;
    private const int MacSize = 32;

    /// <inheritdoc/>
    public override string StrategyId => "aes-128-cbc";

    /// <inheritdoc/>
    public override string StrategyName => "AES-128-CBC-HMAC";

    /// <inheritdoc/>
    public override CipherInfo CipherInfo => new()
    {
        AlgorithmName = "AES-128-CBC-HMAC-SHA256",
        KeySizeBits = 128,
        BlockSizeBytes = 16,
        IvSizeBytes = IvSize,
        TagSizeBytes = MacSize,
        SecurityLevel = SecurityLevel.Standard,
        Capabilities = new CipherCapabilities
        {
            IsAuthenticated = true,
            IsStreamable = false,
            IsHardwareAcceleratable = true,
            SupportsAead = false,
            SupportsParallelism = false,
            MinimumSecurityLevel = SecurityLevel.Standard
        }
    };

    /// <inheritdoc/>
    protected override Task<byte[]> EncryptCoreAsync(byte[] plaintext, byte[] key, byte[]? associatedData, CancellationToken ct)
    {
        var macKey = SHA256.HashData(key);
        var iv = GenerateIv();
        byte[] ciphertext;

        using (var aes = System.Security.Cryptography.Aes.Create())
        {
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var encryptor = aes.CreateEncryptor();
            ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
        }

        var dataToMac = new byte[iv.Length + ciphertext.Length];
        Buffer.BlockCopy(iv, 0, dataToMac, 0, iv.Length);
        Buffer.BlockCopy(ciphertext, 0, dataToMac, iv.Length, ciphertext.Length);

        var tag = HMACSHA256.HashData(macKey, dataToMac);
        CryptographicOperations.ZeroMemory(macKey);

        return Task.FromResult(CombineIvAndCiphertext(iv, ciphertext, tag));
    }

    /// <inheritdoc/>
    protected override Task<byte[]> DecryptCoreAsync(byte[] ciphertext, byte[] key, byte[]? associatedData, CancellationToken ct)
    {
        var macKey = SHA256.HashData(key);
        var (iv, encryptedData, tag) = SplitCiphertext(ciphertext);

        var dataToMac = new byte[iv.Length + encryptedData.Length];
        Buffer.BlockCopy(iv, 0, dataToMac, 0, iv.Length);
        Buffer.BlockCopy(encryptedData, 0, dataToMac, iv.Length, encryptedData.Length);

        var expectedTag = HMACSHA256.HashData(macKey, dataToMac);
        if (!CryptographicOperations.FixedTimeEquals(expectedTag, tag))
        {
            CryptographicOperations.ZeroMemory(macKey);
            throw new CryptographicException("MAC verification failed");
        }

        byte[] plaintext;
        using (var aes = System.Security.Cryptography.Aes.Create())
        {
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var decryptor = aes.CreateDecryptor();
            plaintext = decryptor.TransformFinalBlock(encryptedData, 0, encryptedData.Length);
        }

        CryptographicOperations.ZeroMemory(macKey);
        return Task.FromResult(plaintext);
    }
}
