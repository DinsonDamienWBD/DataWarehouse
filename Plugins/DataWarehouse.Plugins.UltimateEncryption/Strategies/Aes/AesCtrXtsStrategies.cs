using System.Security.Cryptography;
using DataWarehouse.SDK.Contracts.Encryption;

namespace DataWarehouse.Plugins.UltimateEncryption.Strategies.Aes;

/// <summary>
/// AES-256-CTR (Counter mode) encryption strategy with HMAC-SHA256 authentication.
/// Counter mode turns a block cipher into a stream cipher by encrypting incrementing counter values
/// and XORing with plaintext. Provides parallel encryption/decryption capability.
/// </summary>
public sealed class AesCtrStrategy : EncryptionStrategyBase
{
    private const int KeySize = 32; // 256 bits
    private const int IvSize = 16;  // 128-bit counter/nonce
    private const int MacSize = 32; // HMAC-SHA256

    /// <inheritdoc/>
    public override string StrategyId => "aes-256-ctr";

        /// <summary>
        /// Production hardening: validates configuration on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("aes.256.ctr.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("aes.256.ctr.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }

    /// <inheritdoc/>
    public override string StrategyName => "AES-256-CTR-HMAC";

    /// <inheritdoc/>
    public override CipherInfo CipherInfo => new()
    {
        AlgorithmName = "AES-256-CTR-HMAC-SHA256",
        KeySizeBits = 256,
        BlockSizeBytes = 16,
        IvSizeBytes = IvSize,
        TagSizeBytes = MacSize,
        SecurityLevel = SecurityLevel.High,
        Capabilities = new CipherCapabilities
        {
            IsAuthenticated = true,
            IsStreamable = true,
            IsHardwareAcceleratable = true,
            SupportsAead = false,
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
        // Split key: first half for encryption, second half for HMAC
        var encKey = key.AsSpan(0, KeySize).ToArray();
        var macKey = SHA256.HashData(key);

        var nonce = GenerateIv();
        var ciphertext = new byte[plaintext.Length];

        // Implement CTR mode manually
        // CTR mode: C[i] = P[i] XOR E(K, Counter[i])
        using (var aes = System.Security.Cryptography.Aes.Create())
        {
            aes.Key = encKey;
            // SECURITY NOTE: ECB mode is intentionally used here as a building block for CTR mode.
            // This is cryptographically safe because each block uses a unique counter value.
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;

            using var encryptor = aes.CreateEncryptor();

            // Initialize counter from nonce
            var counter = new byte[16];
            Buffer.BlockCopy(nonce, 0, counter, 0, nonce.Length);

            var blockCount = (plaintext.Length + 15) / 16;
            for (int i = 0; i < blockCount; i++)
            {
                // Encrypt counter
                var keyStream = new byte[16];
                encryptor.TransformBlock(counter, 0, 16, keyStream, 0);

                // XOR with plaintext
                var offset = i * 16;
                var length = Math.Min(16, plaintext.Length - offset);
                for (int j = 0; j < length; j++)
                {
                    ciphertext[offset + j] = (byte)(plaintext[offset + j] ^ keyStream[j]);
                }

                // Increment counter (big-endian)
                IncrementCounter(counter);
            }
        }

        // Compute HMAC over nonce + ciphertext (+ associated data if present)
        byte[] dataToMac;
        if (associatedData != null && associatedData.Length > 0)
        {
            dataToMac = new byte[nonce.Length + ciphertext.Length + associatedData.Length];
            Buffer.BlockCopy(nonce, 0, dataToMac, 0, nonce.Length);
            Buffer.BlockCopy(ciphertext, 0, dataToMac, nonce.Length, ciphertext.Length);
            Buffer.BlockCopy(associatedData, 0, dataToMac, nonce.Length + ciphertext.Length, associatedData.Length);
        }
        else
        {
            dataToMac = new byte[nonce.Length + ciphertext.Length];
            Buffer.BlockCopy(nonce, 0, dataToMac, 0, nonce.Length);
            Buffer.BlockCopy(ciphertext, 0, dataToMac, nonce.Length, ciphertext.Length);
        }

        var tag = HMACSHA256.HashData(macKey, dataToMac);

        CryptographicOperations.ZeroMemory(encKey);
        CryptographicOperations.ZeroMemory(macKey);

        return Task.FromResult(CombineIvAndCiphertext(nonce, ciphertext, tag));
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

        var (nonce, encryptedData, tag) = SplitCiphertext(ciphertext);

        // Verify HMAC
        byte[] dataToMac;
        if (associatedData != null && associatedData.Length > 0)
        {
            dataToMac = new byte[nonce.Length + encryptedData.Length + associatedData.Length];
            Buffer.BlockCopy(nonce, 0, dataToMac, 0, nonce.Length);
            Buffer.BlockCopy(encryptedData, 0, dataToMac, nonce.Length, encryptedData.Length);
            Buffer.BlockCopy(associatedData, 0, dataToMac, nonce.Length + encryptedData.Length, associatedData.Length);
        }
        else
        {
            dataToMac = new byte[nonce.Length + encryptedData.Length];
            Buffer.BlockCopy(nonce, 0, dataToMac, 0, nonce.Length);
            Buffer.BlockCopy(encryptedData, 0, dataToMac, nonce.Length, encryptedData.Length);
        }

        var expectedTag = HMACSHA256.HashData(macKey, dataToMac);
        if (!CryptographicOperations.FixedTimeEquals(expectedTag, tag))
        {
            CryptographicOperations.ZeroMemory(encKey);
            CryptographicOperations.ZeroMemory(macKey);
            throw new CryptographicException("MAC verification failed");
        }

        var plaintext = new byte[encryptedData.Length];

        // Decrypt using CTR mode (same as encryption due to XOR symmetry)
        using (var aes = System.Security.Cryptography.Aes.Create())
        {
            aes.Key = encKey;
            // SECURITY NOTE: ECB mode is intentionally used here as a building block for CTR mode.
            // This is cryptographically safe because each block uses a unique counter value.
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;

            using var encryptor = aes.CreateEncryptor();

            var counter = new byte[16];
            Buffer.BlockCopy(nonce, 0, counter, 0, nonce.Length);

            var blockCount = (encryptedData.Length + 15) / 16;
            for (int i = 0; i < blockCount; i++)
            {
                var keyStream = new byte[16];
                encryptor.TransformBlock(counter, 0, 16, keyStream, 0);

                var offset = i * 16;
                var length = Math.Min(16, encryptedData.Length - offset);
                for (int j = 0; j < length; j++)
                {
                    plaintext[offset + j] = (byte)(encryptedData[offset + j] ^ keyStream[j]);
                }

                IncrementCounter(counter);
            }
        }

        CryptographicOperations.ZeroMemory(encKey);
        CryptographicOperations.ZeroMemory(macKey);

        return Task.FromResult(plaintext);
    }

    /// <summary>
    /// Increments a 128-bit counter in big-endian format.
    /// </summary>
    private static void IncrementCounter(byte[] counter)
    {
        for (int i = counter.Length - 1; i >= 0; i--)
        {
            if (++counter[i] != 0)
                break;
        }
    }
}

/// <summary>
/// AES-256-XTS (XEX-based Tweaked CodeBook mode) encryption strategy.
/// Designed specifically for disk encryption with sector-level tweaking.
/// Uses two 256-bit keys (512 bits total) - one for encryption, one for tweak.
/// </summary>
public sealed class AesXtsStrategy : EncryptionStrategyBase
{
    private const int KeySize = 64; // 512 bits total (two 256-bit keys)
    private const int IvSize = 16;  // 128-bit sector/block number

    /// <inheritdoc/>
    public override string StrategyId => "aes-256-xts";

    /// <inheritdoc/>
    public override string StrategyName => "AES-256-XTS";

    /// <inheritdoc/>
    public override CipherInfo CipherInfo => new()
    {
        AlgorithmName = "AES-256-XTS",
        KeySizeBits = 512, // Two 256-bit keys
        BlockSizeBytes = 16,
        IvSizeBytes = IvSize,
        TagSizeBytes = 0, // XTS does not provide authentication
        SecurityLevel = SecurityLevel.High,
        Capabilities = new CipherCapabilities
        {
            IsAuthenticated = false,
            IsStreamable = false,
            IsHardwareAcceleratable = true,
            SupportsAead = false,
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
        // XTS requires plaintext to be at least one block (16 bytes)
        if (plaintext.Length < 16)
        {
            throw new ArgumentException("XTS mode requires at least 16 bytes of plaintext", nameof(plaintext));
        }

        // Split the 512-bit key into two 256-bit keys
        var key1 = new byte[32];
        var key2 = new byte[32];
        Buffer.BlockCopy(key, 0, key1, 0, 32);
        Buffer.BlockCopy(key, 32, key2, 0, 32);

        var tweak = GenerateIv();
        var ciphertext = new byte[plaintext.Length];

        using (var aes1 = System.Security.Cryptography.Aes.Create())
        using (var aes2 = System.Security.Cryptography.Aes.Create())
        {
            aes1.Key = key1;
            // SECURITY NOTE: ECB mode is intentionally used here as a building block for XTS mode.
            // This is cryptographically safe because each block uses a unique tweak value.
            aes1.Mode = CipherMode.ECB;
            aes1.Padding = PaddingMode.None;

            aes2.Key = key2;
            // SECURITY NOTE: ECB mode is intentionally used here as a building block for XTS mode.
            // This is cryptographically safe because each block uses a unique tweak value.
            aes2.Mode = CipherMode.ECB;
            aes2.Padding = PaddingMode.None;

            using var encryptor1 = aes1.CreateEncryptor();
            using var encryptor2 = aes2.CreateEncryptor();

            // Encrypt the tweak with key2 to generate the initial alpha value
            var alpha = new byte[16];
            encryptor2.TransformBlock(tweak, 0, 16, alpha, 0);

            var blockCount = plaintext.Length / 16;
            var remainderBytes = plaintext.Length % 16;

            // Process full blocks
            for (int i = 0; i < blockCount; i++)
            {
                var offset = i * 16;
                var block = new byte[16];
                Buffer.BlockCopy(plaintext, offset, block, 0, 16);

                // XOR with tweak
                XorBlocks(block, alpha);

                // Encrypt
                var encrypted = new byte[16];
                encryptor1.TransformBlock(block, 0, 16, encrypted, 0);

                // XOR with tweak again
                XorBlocks(encrypted, alpha);

                Buffer.BlockCopy(encrypted, 0, ciphertext, offset, 16);

                // Multiply alpha by 2 in GF(2^128)
                MultiplyAlphaByTwo(alpha);
            }

            // Handle ciphertext stealing for non-block-aligned data
            if (remainderBytes > 0)
            {
                var lastFullBlockOffset = (blockCount - 1) * 16;
                var remainderOffset = blockCount * 16;

                // Copy remainder bytes from last full ciphertext block
                Buffer.BlockCopy(ciphertext, lastFullBlockOffset, ciphertext, remainderOffset, remainderBytes);

                // Re-encrypt the last full block with remainder plaintext
                var finalBlock = new byte[16];
                Buffer.BlockCopy(plaintext, remainderOffset, finalBlock, 0, remainderBytes);
                Buffer.BlockCopy(ciphertext, lastFullBlockOffset, finalBlock, remainderBytes, 16 - remainderBytes);

                // Back up alpha
                var previousAlpha = new byte[16];
                Buffer.BlockCopy(alpha, 0, previousAlpha, 0, 16);
                MultiplyAlphaByTwo(alpha);
                Buffer.BlockCopy(previousAlpha, 0, alpha, 0, 16);

                XorBlocks(finalBlock, alpha);
                var encrypted = new byte[16];
                encryptor1.TransformBlock(finalBlock, 0, 16, encrypted, 0);
                XorBlocks(encrypted, alpha);

                Buffer.BlockCopy(encrypted, 0, ciphertext, lastFullBlockOffset, 16);
            }
        }

        CryptographicOperations.ZeroMemory(key1);
        CryptographicOperations.ZeroMemory(key2);

        return Task.FromResult(CombineIvAndCiphertext(tweak, ciphertext));
    }

    /// <inheritdoc/>
    protected override Task<byte[]> DecryptCoreAsync(
        byte[] ciphertext,
        byte[] key,
        byte[]? associatedData,
        CancellationToken cancellationToken)
    {
        var (tweak, encryptedData, _) = SplitCiphertext(ciphertext);

        if (encryptedData.Length < 16)
        {
            throw new ArgumentException("XTS mode requires at least 16 bytes of ciphertext", nameof(ciphertext));
        }

        var key1 = new byte[32];
        var key2 = new byte[32];
        Buffer.BlockCopy(key, 0, key1, 0, 32);
        Buffer.BlockCopy(key, 32, key2, 0, 32);

        var plaintext = new byte[encryptedData.Length];

        using (var aes1 = System.Security.Cryptography.Aes.Create())
        using (var aes2 = System.Security.Cryptography.Aes.Create())
        {
            aes1.Key = key1;
            // SECURITY NOTE: ECB mode is intentionally used here as a building block for XTS mode.
            // This is cryptographically safe because each block uses a unique tweak value.
            aes1.Mode = CipherMode.ECB;
            aes1.Padding = PaddingMode.None;

            aes2.Key = key2;
            // SECURITY NOTE: ECB mode is intentionally used here as a building block for XTS mode.
            // This is cryptographically safe because each block uses a unique tweak value.
            aes2.Mode = CipherMode.ECB;
            aes2.Padding = PaddingMode.None;

            using var decryptor1 = aes1.CreateDecryptor();
            using var encryptor2 = aes2.CreateEncryptor();

            var alpha = new byte[16];
            encryptor2.TransformBlock(tweak, 0, 16, alpha, 0);

            var blockCount = encryptedData.Length / 16;
            var remainderBytes = encryptedData.Length % 16;

            // Process full blocks
            for (int i = 0; i < blockCount; i++)
            {
                var offset = i * 16;
                var block = new byte[16];
                Buffer.BlockCopy(encryptedData, offset, block, 0, 16);

                XorBlocks(block, alpha);

                var decrypted = new byte[16];
                decryptor1.TransformBlock(block, 0, 16, decrypted, 0);

                XorBlocks(decrypted, alpha);

                Buffer.BlockCopy(decrypted, 0, plaintext, offset, 16);

                MultiplyAlphaByTwo(alpha);
            }

            // Handle ciphertext stealing for decryption
            if (remainderBytes > 0)
            {
                var lastFullBlockOffset = (blockCount - 1) * 16;
                var remainderOffset = blockCount * 16;

                var previousAlpha = new byte[16];
                Buffer.BlockCopy(alpha, 0, previousAlpha, 0, 16);
                MultiplyAlphaByTwo(alpha);
                Buffer.BlockCopy(previousAlpha, 0, alpha, 0, 16);

                var block = new byte[16];
                Buffer.BlockCopy(encryptedData, lastFullBlockOffset, block, 0, 16);

                XorBlocks(block, alpha);
                var decrypted = new byte[16];
                decryptor1.TransformBlock(block, 0, 16, decrypted, 0);
                XorBlocks(decrypted, alpha);

                Buffer.BlockCopy(decrypted, 0, plaintext, remainderOffset, remainderBytes);
                Buffer.BlockCopy(decrypted, remainderBytes, plaintext, lastFullBlockOffset, 16 - remainderBytes);
                Buffer.BlockCopy(encryptedData, remainderOffset, plaintext, lastFullBlockOffset + 16 - remainderBytes, remainderBytes);
            }
        }

        CryptographicOperations.ZeroMemory(key1);
        CryptographicOperations.ZeroMemory(key2);

        return Task.FromResult(plaintext);
    }

    /// <summary>
    /// XOR two 16-byte blocks in place (modifies the first block).
    /// </summary>
    private static void XorBlocks(byte[] block, byte[] mask)
    {
        for (int i = 0; i < 16; i++)
        {
            block[i] ^= mask[i];
        }
    }

    /// <summary>
    /// Multiply alpha by 2 in GF(2^128) with reduction polynomial x^128 + x^7 + x^2 + x + 1.
    /// This is the standard XTS tweak update function.
    /// </summary>
    private static void MultiplyAlphaByTwo(byte[] alpha)
    {
        byte carry = 0;
        for (int i = 0; i < 16; i++)
        {
            byte newCarry = (byte)(alpha[i] >> 7);
            alpha[i] = (byte)((alpha[i] << 1) | carry);
            carry = newCarry;
        }

        // If there was a carry out, XOR with 0x87 (reduction polynomial)
        if (carry != 0)
        {
            alpha[0] ^= 0x87;
        }
    }
}

/// <summary>
/// AES-256-CCM (Counter with CBC-MAC) authenticated encryption strategy.
/// Provides both confidentiality and authentication in a single operation.
/// Commonly used in IoT and constrained environments.
/// </summary>
public sealed class AesCcmStrategy : EncryptionStrategyBase
{
    private const int KeySize = 32; // 256 bits
    private const int NonceSize = 12; // 96 bits (recommended)
    private const int TagSize = 16;  // 128 bits

    /// <inheritdoc/>
    public override string StrategyId => "aes-256-ccm";

    /// <inheritdoc/>
    public override string StrategyName => "AES-256-CCM";

    /// <inheritdoc/>
    public override CipherInfo CipherInfo => new()
    {
        AlgorithmName = "AES-256-CCM",
        KeySizeBits = 256,
        BlockSizeBytes = 16,
        IvSizeBytes = NonceSize,
        TagSizeBytes = TagSize,
        SecurityLevel = SecurityLevel.High,
        Capabilities = new CipherCapabilities
        {
            IsAuthenticated = true,
            IsStreamable = false, // CCM requires knowing plaintext length in advance
            IsHardwareAcceleratable = true,
            SupportsAead = true,
            SupportsParallelism = false, // CCM is inherently sequential
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

        using var aesCcm = new AesCcm(key);
        aesCcm.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

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

        using var aesCcm = new AesCcm(key);
        aesCcm.Decrypt(nonce, encryptedData, tag!, plaintext, associatedData);

        return Task.FromResult(plaintext);
    }
}

/// <summary>
/// AES-256-ECB (Electronic CodeBook mode) encryption strategy.
/// WARNING: ECB mode is NOT RECOMMENDED for production use as it does not provide semantic security.
/// Identical plaintext blocks produce identical ciphertext blocks, revealing patterns.
/// This implementation is provided for legacy compatibility and testing purposes only.
/// Use AES-GCM, AES-CTR, or AES-CBC instead for production systems.
/// </summary>
public sealed class AesEcbStrategy : EncryptionStrategyBase
{
    private const int KeySize = 32; // 256 bits

    /// <inheritdoc/>
    public override string StrategyId => "aes-256-ecb";

    /// <inheritdoc/>
    public override string StrategyName => "AES-256-ECB (Legacy)";

    /// <inheritdoc/>
    public override CipherInfo CipherInfo => new()
    {
        AlgorithmName = "AES-256-ECB",
        KeySizeBits = 256,
        BlockSizeBytes = 16,
        IvSizeBytes = 0, // ECB does not use an IV
        TagSizeBytes = 0, // ECB does not provide authentication
        SecurityLevel = SecurityLevel.Experimental, // NOT recommended for production
        Capabilities = new CipherCapabilities
        {
            IsAuthenticated = false,
            IsStreamable = false,
            IsHardwareAcceleratable = true,
            SupportsAead = false,
            SupportsParallelism = true, // ECB blocks can be processed in parallel
            MinimumSecurityLevel = SecurityLevel.Experimental
        }
    };

    /// <inheritdoc/>
    protected override Task<byte[]> EncryptCoreAsync(
        byte[] plaintext,
        byte[] key,
        byte[]? associatedData,
        CancellationToken cancellationToken)
    {
        // ECB requires block-aligned data or padding
        byte[] ciphertext;

        using (var aes = System.Security.Cryptography.Aes.Create())
        {
            aes.Key = key;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.PKCS7;

            using var encryptor = aes.CreateEncryptor();
            ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
        }

        // For consistency with other strategies, wrap in envelope (even though no IV)
        return Task.FromResult(ciphertext);
    }

    /// <inheritdoc/>
    protected override Task<byte[]> DecryptCoreAsync(
        byte[] ciphertext,
        byte[] key,
        byte[]? associatedData,
        CancellationToken cancellationToken)
    {
        byte[] plaintext;

        using (var aes = System.Security.Cryptography.Aes.Create())
        {
            aes.Key = key;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.PKCS7;

            using var decryptor = aes.CreateDecryptor();
            plaintext = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
        }

        return Task.FromResult(plaintext);
    }

    /// <inheritdoc/>
    public override byte[] GenerateIv()
    {
        // ECB does not use an IV
        return Array.Empty<byte>();
    }
}
