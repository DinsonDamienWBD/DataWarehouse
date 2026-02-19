using System.Security.Cryptography;
using DataWarehouse.SDK.Contracts;
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
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        // Validate crypto parameters
        if (NonceSize != 12)
            throw new ArgumentException("AES-GCM requires 12-byte nonce");

        if (TagSize != 16)
            throw new ArgumentException("AES-GCM requires 16-byte authentication tag");

        if (KeySize != 32)
            throw new ArgumentException("AES-256-GCM requires 32-byte key (256 bits)");

        // Verify AesGcm is available on this platform
        try
        {
            var testKey = new byte[KeySize];
            using var testGcm = new AesGcm(testKey, TagSize);
        }
        catch (PlatformNotSupportedException ex)
        {
            throw new InvalidOperationException("AES-GCM is not supported on this platform", ex);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Checks AES-GCM availability via encrypt+decrypt round-trip test.
    /// </summary>
    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default)
    {
        return await GetCachedHealthAsync(async _ =>
        {
            try
            {
                // Test encrypt+decrypt round-trip
                var testKey = RandomNumberGenerator.GetBytes(KeySize);
                var testPlaintext = new byte[] { 1, 2, 3, 4, 5 };
                var testNonce = RandomNumberGenerator.GetBytes(NonceSize);
                var testCiphertext = new byte[testPlaintext.Length];
                var testTag = new byte[TagSize];
                var testDecrypted = new byte[testPlaintext.Length];

                using var aesGcm = new AesGcm(testKey, TagSize);
                aesGcm.Encrypt(testNonce, testPlaintext, testCiphertext, testTag);
                aesGcm.Decrypt(testNonce, testCiphertext, testTag, testDecrypted);

                var success = testPlaintext.SequenceEqual(testDecrypted);

                return new StrategyHealthCheckResult(
                    success,
                    success ? "AES-256-GCM operational" : "Round-trip test failed");
            }
            catch (PlatformNotSupportedException)
            {
                return new StrategyHealthCheckResult(false, "AES-GCM not available on this platform");
            }
            catch (Exception ex)
            {
                return new StrategyHealthCheckResult(false, $"Health check failed: {ex.Message}");
            }
        }, TimeSpan.FromSeconds(60), ct);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        // No persistent resources to clean up
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async ValueTask DisposeAsyncCore()
    {
        // No async disposal needed
        await base.DisposeAsyncCore();
    }

    /// <inheritdoc/>
    protected override Task<byte[]> EncryptCoreAsync(
        byte[] plaintext,
        byte[] key,
        byte[]? associatedData,
        CancellationToken cancellationToken)
    {
        try
        {
            var nonce = GenerateIv();
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagSize];

            using var aesGcm = new AesGcm(key, TagSize);
            aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

            IncrementCounter("aesgcm.encrypt");
            return Task.FromResult(CombineIvAndCiphertext(nonce, ciphertext, tag));
        }
        catch (CryptographicException ex)
        {
            throw new CryptographicException($"AES-256-GCM encryption failed (key size: {key.Length} bytes)", ex);
        }
    }

    /// <inheritdoc/>
    protected override Task<byte[]> DecryptCoreAsync(
        byte[] ciphertext,
        byte[] key,
        byte[]? associatedData,
        CancellationToken cancellationToken)
    {
        try
        {
            var (nonce, encryptedData, tag) = SplitCiphertext(ciphertext);
            var plaintext = new byte[encryptedData.Length];

            using var aesGcm = new AesGcm(key, TagSize);
            aesGcm.Decrypt(nonce, encryptedData, tag!, plaintext, associatedData);

            IncrementCounter("aesgcm.decrypt");
            return Task.FromResult(plaintext);
        }
        catch (CryptographicException ex)
        {
            throw new CryptographicException($"AES-256-GCM decryption failed (key size: {key.Length} bytes)", ex);
        }
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
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        if (NonceSize != 12)
            throw new ArgumentException("AES-GCM requires 12-byte nonce");

        if (TagSize != 16)
            throw new ArgumentException("AES-GCM requires 16-byte authentication tag");

        if (KeySize != 16)
            throw new ArgumentException("AES-128-GCM requires 16-byte key (128 bits)");

        try
        {
            var testKey = new byte[KeySize];
            using var testGcm = new AesGcm(testKey, TagSize);
        }
        catch (PlatformNotSupportedException ex)
        {
            throw new InvalidOperationException("AES-GCM is not supported on this platform", ex);
        }

        return Task.CompletedTask;
    }

    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default)
    {
        return await GetCachedHealthAsync(async _ =>
        {
            try
            {
                var testKey = RandomNumberGenerator.GetBytes(KeySize);
                var testPlaintext = new byte[] { 1, 2, 3, 4, 5 };
                var testNonce = RandomNumberGenerator.GetBytes(NonceSize);
                var testCiphertext = new byte[testPlaintext.Length];
                var testTag = new byte[TagSize];
                var testDecrypted = new byte[testPlaintext.Length];

                using var aesGcm = new AesGcm(testKey, TagSize);
                aesGcm.Encrypt(testNonce, testPlaintext, testCiphertext, testTag);
                aesGcm.Decrypt(testNonce, testCiphertext, testTag, testDecrypted);

                var success = testPlaintext.SequenceEqual(testDecrypted);

                return new StrategyHealthCheckResult(
                    success,
                    success ? "AES-128-GCM operational" : "Round-trip test failed");
            }
            catch (PlatformNotSupportedException)
            {
                return new StrategyHealthCheckResult(false, "AES-GCM not available on this platform");
            }
            catch (Exception ex)
            {
                return new StrategyHealthCheckResult(false, $"Health check failed: {ex.Message}");
            }
        }, TimeSpan.FromSeconds(60), ct);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async ValueTask DisposeAsyncCore()
    {
        await base.DisposeAsyncCore();
    }

    /// <inheritdoc/>
    protected override Task<byte[]> EncryptCoreAsync(byte[] plaintext, byte[] key, byte[]? associatedData, CancellationToken ct)
    {
        try
        {
            var nonce = GenerateIv();
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagSize];

            using var aesGcm = new AesGcm(key, TagSize);
            aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

            IncrementCounter("aesgcm.encrypt");
            return Task.FromResult(CombineIvAndCiphertext(nonce, ciphertext, tag));
        }
        catch (CryptographicException ex)
        {
            throw new CryptographicException($"AES-128-GCM encryption failed (key size: {key.Length} bytes)", ex);
        }
    }

    /// <inheritdoc/>
    protected override Task<byte[]> DecryptCoreAsync(byte[] ciphertext, byte[] key, byte[]? associatedData, CancellationToken ct)
    {
        try
        {
            var (nonce, encryptedData, tag) = SplitCiphertext(ciphertext);
            var plaintext = new byte[encryptedData.Length];

            using var aesGcm = new AesGcm(key, TagSize);
            aesGcm.Decrypt(nonce, encryptedData, tag!, plaintext, associatedData);

            IncrementCounter("aesgcm.decrypt");
            return Task.FromResult(plaintext);
        }
        catch (CryptographicException ex)
        {
            throw new CryptographicException($"AES-128-GCM decryption failed (key size: {key.Length} bytes)", ex);
        }
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
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        if (NonceSize != 12)
            throw new ArgumentException("AES-GCM requires 12-byte nonce");

        if (TagSize != 16)
            throw new ArgumentException("AES-GCM requires 16-byte authentication tag");

        if (KeySize != 24)
            throw new ArgumentException("AES-192-GCM requires 24-byte key (192 bits)");

        try
        {
            var testKey = new byte[KeySize];
            using var testGcm = new AesGcm(testKey, TagSize);
        }
        catch (PlatformNotSupportedException ex)
        {
            throw new InvalidOperationException("AES-GCM is not supported on this platform", ex);
        }

        return Task.CompletedTask;
    }

    public async Task<StrategyHealthCheckResult> CheckHealthAsync(CancellationToken ct = default)
    {
        return await GetCachedHealthAsync(async _ =>
        {
            try
            {
                var testKey = RandomNumberGenerator.GetBytes(KeySize);
                var testPlaintext = new byte[] { 1, 2, 3, 4, 5 };
                var testNonce = RandomNumberGenerator.GetBytes(NonceSize);
                var testCiphertext = new byte[testPlaintext.Length];
                var testTag = new byte[TagSize];
                var testDecrypted = new byte[testPlaintext.Length];

                using var aesGcm = new AesGcm(testKey, TagSize);
                aesGcm.Encrypt(testNonce, testPlaintext, testCiphertext, testTag);
                aesGcm.Decrypt(testNonce, testCiphertext, testTag, testDecrypted);

                var success = testPlaintext.SequenceEqual(testDecrypted);

                return new StrategyHealthCheckResult(
                    success,
                    success ? "AES-192-GCM operational" : "Round-trip test failed");
            }
            catch (PlatformNotSupportedException)
            {
                return new StrategyHealthCheckResult(false, "AES-GCM not available on this platform");
            }
            catch (Exception ex)
            {
                return new StrategyHealthCheckResult(false, $"Health check failed: {ex.Message}");
            }
        }, TimeSpan.FromSeconds(60), ct);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override async ValueTask DisposeAsyncCore()
    {
        await base.DisposeAsyncCore();
    }

    /// <inheritdoc/>
    protected override Task<byte[]> EncryptCoreAsync(byte[] plaintext, byte[] key, byte[]? associatedData, CancellationToken ct)
    {
        try
        {
            var nonce = GenerateIv();
            var ciphertext = new byte[plaintext.Length];
            var tag = new byte[TagSize];

            using var aesGcm = new AesGcm(key, TagSize);
            aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

            IncrementCounter("aesgcm.encrypt");
            return Task.FromResult(CombineIvAndCiphertext(nonce, ciphertext, tag));
        }
        catch (CryptographicException ex)
        {
            throw new CryptographicException($"AES-192-GCM encryption failed (key size: {key.Length} bytes)", ex);
        }
    }

    /// <inheritdoc/>
    protected override Task<byte[]> DecryptCoreAsync(byte[] ciphertext, byte[] key, byte[]? associatedData, CancellationToken ct)
    {
        try
        {
            var (nonce, encryptedData, tag) = SplitCiphertext(ciphertext);
            var plaintext = new byte[encryptedData.Length];

            using var aesGcm = new AesGcm(key, TagSize);
            aesGcm.Decrypt(nonce, encryptedData, tag!, plaintext, associatedData);

            IncrementCounter("aesgcm.decrypt");
            return Task.FromResult(plaintext);
        }
        catch (CryptographicException ex)
        {
            throw new CryptographicException($"AES-192-GCM decryption failed (key size: {key.Length} bytes)", ex);
        }
    }
}
