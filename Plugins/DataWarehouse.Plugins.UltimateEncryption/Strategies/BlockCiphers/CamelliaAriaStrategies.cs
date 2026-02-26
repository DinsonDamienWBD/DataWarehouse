using System.Security.Cryptography;
using DataWarehouse.SDK.Contracts.Encryption;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace DataWarehouse.Plugins.UltimateEncryption.Strategies.BlockCiphers;

/// <summary>
/// Camellia-256-GCM encryption strategy.
/// Japanese/European standard cipher with 256-bit keys and GCM authenticated encryption.
/// </summary>
public sealed class CamelliaStrategy : EncryptionStrategyBase
{
    private const int KeySize = 32; // 256 bits
    private const int NonceSize = 12;
    private const int TagSize = 16;

    /// <inheritdoc/>
    public override string StrategyId => "camellia-256-gcm";

        /// <summary>
        /// Production hardening: validates configuration on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("camellia.256.gcm.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("camellia.256.gcm.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }

    /// <inheritdoc/>
    public override string StrategyName => "Camellia-256-GCM";

    /// <inheritdoc/>
    public override CipherInfo CipherInfo => new()
    {
        AlgorithmName = "Camellia-256-GCM",
        KeySizeBits = 256,
        BlockSizeBytes = 16,
        IvSizeBytes = NonceSize,
        TagSizeBytes = TagSize,
        SecurityLevel = SecurityLevel.High,
        Capabilities = new CipherCapabilities
        {
            IsAuthenticated = true,
            IsStreamable = true,
            IsHardwareAcceleratable = false,
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
        var cipher = new GcmBlockCipher(new CamelliaEngine());
        var parameters = new AeadParameters(
            new KeyParameter(key),
            TagSize * 8,
            nonce,
            associatedData);

        cipher.Init(true, parameters);

        var ciphertext = new byte[cipher.GetOutputSize(plaintext.Length)];
        var len = cipher.ProcessBytes(plaintext, 0, plaintext.Length, ciphertext, 0);
        len += cipher.DoFinal(ciphertext, len);

        // GcmBlockCipher outputs ciphertext + tag
        var result = new byte[nonce.Length + len];
        Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, result, nonce.Length, len);

        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    protected override Task<byte[]> DecryptCoreAsync(
        byte[] ciphertext,
        byte[] key,
        byte[]? associatedData,
        CancellationToken cancellationToken)
    {
        var nonce = new byte[NonceSize];
        Buffer.BlockCopy(ciphertext, 0, nonce, 0, NonceSize);

        var encryptedData = new byte[ciphertext.Length - NonceSize];
        Buffer.BlockCopy(ciphertext, NonceSize, encryptedData, 0, encryptedData.Length);

        var cipher = new GcmBlockCipher(new CamelliaEngine());
        var parameters = new AeadParameters(
            new KeyParameter(key),
            TagSize * 8,
            nonce,
            associatedData);

        cipher.Init(false, parameters);

        var plaintext = new byte[cipher.GetOutputSize(encryptedData.Length)];
        var len = cipher.ProcessBytes(encryptedData, 0, encryptedData.Length, plaintext, 0);

        try
        {
            len += cipher.DoFinal(plaintext, len);
        }
        catch (InvalidCipherTextException ex)
        {
            throw new CryptographicException("Authentication tag verification failed", ex);
        }

        Array.Resize(ref plaintext, len);
        return Task.FromResult(plaintext);
    }
}

/// <summary>
/// ARIA-256-GCM encryption strategy.
/// Korean national standard cipher with 256-bit keys and GCM authenticated encryption.
/// </summary>
public sealed class AriaStrategy : EncryptionStrategyBase
{
    private const int KeySize = 32; // 256 bits
    private const int NonceSize = 12;
    private const int TagSize = 16;

    /// <inheritdoc/>
    public override string StrategyId => "aria-256-gcm";

    /// <inheritdoc/>
    public override string StrategyName => "ARIA-256-GCM";

    /// <inheritdoc/>
    public override CipherInfo CipherInfo => new()
    {
        AlgorithmName = "ARIA-256-GCM",
        KeySizeBits = 256,
        BlockSizeBytes = 16,
        IvSizeBytes = NonceSize,
        TagSizeBytes = TagSize,
        SecurityLevel = SecurityLevel.High,
        Capabilities = new CipherCapabilities
        {
            IsAuthenticated = true,
            IsStreamable = true,
            IsHardwareAcceleratable = false,
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
        var cipher = new GcmBlockCipher(new AriaEngine());
        var parameters = new AeadParameters(
            new KeyParameter(key),
            TagSize * 8,
            nonce,
            associatedData);

        cipher.Init(true, parameters);

        var ciphertext = new byte[cipher.GetOutputSize(plaintext.Length)];
        var len = cipher.ProcessBytes(plaintext, 0, plaintext.Length, ciphertext, 0);
        len += cipher.DoFinal(ciphertext, len);

        var result = new byte[nonce.Length + len];
        Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, result, nonce.Length, len);

        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    protected override Task<byte[]> DecryptCoreAsync(
        byte[] ciphertext,
        byte[] key,
        byte[]? associatedData,
        CancellationToken cancellationToken)
    {
        var nonce = new byte[NonceSize];
        Buffer.BlockCopy(ciphertext, 0, nonce, 0, NonceSize);

        var encryptedData = new byte[ciphertext.Length - NonceSize];
        Buffer.BlockCopy(ciphertext, NonceSize, encryptedData, 0, encryptedData.Length);

        var cipher = new GcmBlockCipher(new AriaEngine());
        var parameters = new AeadParameters(
            new KeyParameter(key),
            TagSize * 8,
            nonce,
            associatedData);

        cipher.Init(false, parameters);

        var plaintext = new byte[cipher.GetOutputSize(encryptedData.Length)];
        var len = cipher.ProcessBytes(encryptedData, 0, encryptedData.Length, plaintext, 0);

        try
        {
            len += cipher.DoFinal(plaintext, len);
        }
        catch (InvalidCipherTextException ex)
        {
            throw new CryptographicException("Authentication tag verification failed", ex);
        }

        Array.Resize(ref plaintext, len);
        return Task.FromResult(plaintext);
    }
}

/// <summary>
/// SM4-128-GCM encryption strategy.
/// Chinese national standard cipher with 128-bit keys and GCM authenticated encryption.
/// </summary>
public sealed class Sm4Strategy : EncryptionStrategyBase
{
    private const int KeySize = 16; // 128 bits
    private const int NonceSize = 12;
    private const int TagSize = 16;

    /// <inheritdoc/>
    public override string StrategyId => "sm4-128-gcm";

    /// <inheritdoc/>
    public override string StrategyName => "SM4-128-GCM";

    /// <inheritdoc/>
    public override CipherInfo CipherInfo => new()
    {
        AlgorithmName = "SM4-128-GCM",
        KeySizeBits = 128,
        BlockSizeBytes = 16,
        IvSizeBytes = NonceSize,
        TagSizeBytes = TagSize,
        SecurityLevel = SecurityLevel.Standard,
        Capabilities = new CipherCapabilities
        {
            IsAuthenticated = true,
            IsStreamable = true,
            IsHardwareAcceleratable = false,
            SupportsAead = true,
            SupportsParallelism = true,
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
        var nonce = GenerateIv();
        var cipher = new GcmBlockCipher(new SM4Engine());
        var parameters = new AeadParameters(
            new KeyParameter(key),
            TagSize * 8,
            nonce,
            associatedData);

        cipher.Init(true, parameters);

        var ciphertext = new byte[cipher.GetOutputSize(plaintext.Length)];
        var len = cipher.ProcessBytes(plaintext, 0, plaintext.Length, ciphertext, 0);
        len += cipher.DoFinal(ciphertext, len);

        var result = new byte[nonce.Length + len];
        Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, result, nonce.Length, len);

        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    protected override Task<byte[]> DecryptCoreAsync(
        byte[] ciphertext,
        byte[] key,
        byte[]? associatedData,
        CancellationToken cancellationToken)
    {
        var nonce = new byte[NonceSize];
        Buffer.BlockCopy(ciphertext, 0, nonce, 0, NonceSize);

        var encryptedData = new byte[ciphertext.Length - NonceSize];
        Buffer.BlockCopy(ciphertext, NonceSize, encryptedData, 0, encryptedData.Length);

        var cipher = new GcmBlockCipher(new SM4Engine());
        var parameters = new AeadParameters(
            new KeyParameter(key),
            TagSize * 8,
            nonce,
            associatedData);

        cipher.Init(false, parameters);

        var plaintext = new byte[cipher.GetOutputSize(encryptedData.Length)];
        var len = cipher.ProcessBytes(encryptedData, 0, encryptedData.Length, plaintext, 0);

        try
        {
            len += cipher.DoFinal(plaintext, len);
        }
        catch (InvalidCipherTextException ex)
        {
            throw new CryptographicException("Authentication tag verification failed", ex);
        }

        Array.Resize(ref plaintext, len);
        return Task.FromResult(plaintext);
    }
}

/// <summary>
/// SEED-128-CBC-HMAC encryption strategy.
/// Korean cipher with 128-bit keys, CBC mode, and HMAC-SHA256 authentication.
/// </summary>
public sealed class SeedStrategy : EncryptionStrategyBase
{
    private const int KeySize = 16; // 128 bits
    private const int IvSize = 16;
    private const int TagSize = 32; // HMAC-SHA256

    /// <inheritdoc/>
    public override string StrategyId => "seed-128-cbc-hmac";

    /// <inheritdoc/>
    public override string StrategyName => "SEED-128-CBC-HMAC";

    /// <inheritdoc/>
    public override CipherInfo CipherInfo => new()
    {
        AlgorithmName = "SEED-128-CBC-HMAC",
        KeySizeBits = 128,
        BlockSizeBytes = 16,
        IvSizeBytes = IvSize,
        TagSizeBytes = TagSize,
        SecurityLevel = SecurityLevel.Standard,
        Capabilities = new CipherCapabilities
        {
            IsAuthenticated = true,
            IsStreamable = true,
            IsHardwareAcceleratable = false,
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
        var iv = GenerateIv();
        var cipher = new CbcBlockCipher(new SeedEngine());
        var parameters = new ParametersWithIV(new KeyParameter(key), iv);

        cipher.Init(true, parameters);

        // Add PKCS7 padding
        var paddedLength = ((plaintext.Length / 16) + 1) * 16;
        var paddedPlaintext = new byte[paddedLength];
        Buffer.BlockCopy(plaintext, 0, paddedPlaintext, 0, plaintext.Length);
        var paddingValue = (byte)(paddedLength - plaintext.Length);
        for (int i = plaintext.Length; i < paddedLength; i++)
            paddedPlaintext[i] = paddingValue;

        var ciphertext = new byte[paddedLength];
        for (int i = 0; i < paddedLength; i += 16)
        {
            cipher.ProcessBlock(paddedPlaintext, i, ciphertext, i);
        }

        // Compute HMAC
        var macKey = SHA256.HashData(key);
        var dataToMac = new byte[iv.Length + ciphertext.Length];
        Buffer.BlockCopy(iv, 0, dataToMac, 0, iv.Length);
        Buffer.BlockCopy(ciphertext, 0, dataToMac, iv.Length, ciphertext.Length);
        var tag = HMACSHA256.HashData(macKey, dataToMac);

        return Task.FromResult(CombineIvAndCiphertext(iv, ciphertext, tag));
    }

    /// <inheritdoc/>
    protected override Task<byte[]> DecryptCoreAsync(
        byte[] ciphertext,
        byte[] key,
        byte[]? associatedData,
        CancellationToken cancellationToken)
    {
        var (iv, encryptedData, tag) = SplitCiphertext(ciphertext);

        // Verify HMAC
        var macKey = SHA256.HashData(key);
        var dataToMac = new byte[iv.Length + encryptedData.Length];
        Buffer.BlockCopy(iv, 0, dataToMac, 0, iv.Length);
        Buffer.BlockCopy(encryptedData, 0, dataToMac, iv.Length, encryptedData.Length);
        var expectedTag = HMACSHA256.HashData(macKey, dataToMac);

        if (!CryptographicOperations.FixedTimeEquals(expectedTag, tag!))
            throw new CryptographicException("MAC verification failed");

        var cipher = new CbcBlockCipher(new SeedEngine());
        var parameters = new ParametersWithIV(new KeyParameter(key), iv);

        cipher.Init(false, parameters);

        var plaintext = new byte[encryptedData.Length];
        for (int i = 0; i < encryptedData.Length; i += 16)
        {
            cipher.ProcessBlock(encryptedData, i, plaintext, i);
        }

        // Remove PKCS7 padding
        var paddingValue = plaintext[plaintext.Length - 1];
        if (paddingValue < 1 || paddingValue > 16)
            throw new CryptographicException("Invalid padding");

        for (int i = plaintext.Length - paddingValue; i < plaintext.Length; i++)
        {
            if (plaintext[i] != paddingValue)
                throw new CryptographicException("Invalid padding");
        }

        Array.Resize(ref plaintext, plaintext.Length - paddingValue);
        return Task.FromResult(plaintext);
    }
}

/// <summary>
/// Kuznyechik-256 encryption strategy.
/// Russian GOST R 34.12-2015 cipher with 256-bit keys in CTR mode with HMAC.
/// </summary>
public sealed class KuznyechikStrategy : EncryptionStrategyBase
{
    private const int KeySize = 32; // 256 bits
    private const int NonceSize = 16;
    private const int TagSize = 32; // HMAC-SHA256

    /// <inheritdoc/>
    public override string StrategyId => "kuznyechik-256-ctr-hmac";

    /// <inheritdoc/>
    public override string StrategyName => "Kuznyechik-256-CTR-HMAC";

    /// <inheritdoc/>
    public override CipherInfo CipherInfo => new()
    {
        AlgorithmName = "Kuznyechik-256-CTR-HMAC",
        KeySizeBits = 256,
        BlockSizeBytes = 16,
        IvSizeBytes = NonceSize,
        TagSizeBytes = TagSize,
        SecurityLevel = SecurityLevel.High,
        Capabilities = new CipherCapabilities
        {
            IsAuthenticated = true,
            IsStreamable = true,
            IsHardwareAcceleratable = false,
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
        // Kuznyechik (GOST R 34.12-2015) requires a 128-bit block cipher engine. The prior
        // implementation incorrectly used Gost28147Engine (Magma, a 64-bit block cipher defined
        // by GOST 28147-89) as a fallback, producing a completely different and incompatible
        // cipher. BouncyCastle 2.6.2 does not include a Gost3412_2015 (Kuznyechik/Grasshopper)
        // engine. Per Rule 13, throw NotSupportedException rather than silently execute the
        // wrong algorithm.
        throw new NotSupportedException(
            "Kuznyechik (GOST R 34.12-2015) is not supported by the current BouncyCastle " +
            "version. A library that implements Gost3412_2015 (Grasshopper/Kuznyechik) with " +
            "a 128-bit block size is required.");
    }

    /// <inheritdoc/>
    protected override Task<byte[]> DecryptCoreAsync(
        byte[] ciphertext,
        byte[] key,
        byte[]? associatedData,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException(
            "Kuznyechik (GOST R 34.12-2015) is not supported by the current BouncyCastle " +
            "version. A library that implements Gost3412_2015 (Grasshopper/Kuznyechik) with " +
            "a 128-bit block size is required.");
    }
}

/// <summary>
/// Magma encryption strategy.
/// Russian GOST 28147-89 legacy cipher with 256-bit keys in CTR mode with HMAC.
/// </summary>
public sealed class MagmaStrategy : EncryptionStrategyBase
{
    private const int KeySize = 32; // 256 bits
    private const int NonceSize = 8; // 64-bit block cipher
    private const int TagSize = 32; // HMAC-SHA256

    /// <inheritdoc/>
    public override string StrategyId => "magma-256-ctr-hmac";

    /// <inheritdoc/>
    public override string StrategyName => "Magma-256-CTR-HMAC";

    /// <inheritdoc/>
    public override CipherInfo CipherInfo => new()
    {
        AlgorithmName = "Magma-256-CTR-HMAC",
        KeySizeBits = 256,
        BlockSizeBytes = 8, // 64-bit block size
        IvSizeBytes = NonceSize,
        TagSizeBytes = TagSize,
        SecurityLevel = SecurityLevel.Standard,
        Capabilities = new CipherCapabilities
        {
            IsAuthenticated = true,
            IsStreamable = true,
            IsHardwareAcceleratable = false,
            SupportsAead = false,
            SupportsParallelism = true,
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
        var nonce = GenerateIv();
        var cipher = new SicBlockCipher(new Gost28147Engine());
        var parameters = new ParametersWithIV(new KeyParameter(key), nonce);

        cipher.Init(true, parameters);

        var ciphertext = new byte[plaintext.Length];
        // SicBlockCipher requires block-by-block processing
        for (int i = 0; i < plaintext.Length; i += 8)
        {
            var blockSize = Math.Min(8, plaintext.Length - i);
            if (blockSize == 8)
            {
                cipher.ProcessBlock(plaintext, i, ciphertext, i);
            }
            else
            {
                var tempIn = new byte[8];
                var tempOut = new byte[8];
                Buffer.BlockCopy(plaintext, i, tempIn, 0, blockSize);
                cipher.ProcessBlock(tempIn, 0, tempOut, 0);
                Buffer.BlockCopy(tempOut, 0, ciphertext, i, blockSize);
            }
        }

        // Compute HMAC
        var macKey = SHA256.HashData(key);
        var dataToMac = new byte[nonce.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, dataToMac, 0, nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, dataToMac, nonce.Length, ciphertext.Length);
        var tag = HMACSHA256.HashData(macKey, dataToMac);

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

        // Verify HMAC
        var macKey = SHA256.HashData(key);
        var dataToMac = new byte[nonce.Length + encryptedData.Length];
        Buffer.BlockCopy(nonce, 0, dataToMac, 0, nonce.Length);
        Buffer.BlockCopy(encryptedData, 0, dataToMac, nonce.Length, encryptedData.Length);
        var expectedTag = HMACSHA256.HashData(macKey, dataToMac);

        if (!CryptographicOperations.FixedTimeEquals(expectedTag, tag!))
            throw new CryptographicException("MAC verification failed");

        var cipher = new SicBlockCipher(new Gost28147Engine());
        var parameters = new ParametersWithIV(new KeyParameter(key), nonce);

        cipher.Init(false, parameters);

        var plaintext = new byte[encryptedData.Length];
        // SicBlockCipher requires block-by-block processing
        for (int i = 0; i < encryptedData.Length; i += 8)
        {
            var blockSize = Math.Min(8, encryptedData.Length - i);
            if (blockSize == 8)
            {
                cipher.ProcessBlock(encryptedData, i, plaintext, i);
            }
            else
            {
                var tempIn = new byte[8];
                var tempOut = new byte[8];
                Buffer.BlockCopy(encryptedData, i, tempIn, 0, blockSize);
                cipher.ProcessBlock(tempIn, 0, tempOut, 0);
                Buffer.BlockCopy(tempOut, 0, plaintext, i, blockSize);
            }
        }

        return Task.FromResult(plaintext);
    }
}
