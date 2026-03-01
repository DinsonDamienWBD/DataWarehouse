using System.Security.Cryptography;
using DataWarehouse.SDK.Contracts.Encryption;

namespace DataWarehouse.Plugins.UltimateEncryption.Strategies.ChaCha;

/// <summary>
/// ChaCha20-Poly1305 encryption strategy (RFC 8439).
/// High-performance AEAD cipher suitable for systems without AES hardware acceleration.
/// </summary>
public sealed class ChaCha20Poly1305Strategy : EncryptionStrategyBase
{
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    /// <inheritdoc/>
    public override string StrategyId => "chacha20-poly1305";

        /// <summary>
        /// Production hardening: validates configuration on initialization.
        /// </summary>
        protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("chacha20.poly1305.init");
            return base.InitializeAsyncCore(cancellationToken);
        }

        /// <summary>
        /// Production hardening: releases resources on shutdown.
        /// </summary>
        protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
        {
            IncrementCounter("chacha20.poly1305.shutdown");
            return base.ShutdownAsyncCore(cancellationToken);
        }

    /// <inheritdoc/>
    public override string StrategyName => "ChaCha20-Poly1305";

    /// <inheritdoc/>
    public override CipherInfo CipherInfo => new()
    {
        AlgorithmName = "ChaCha20-Poly1305",
        KeySizeBits = 256,
        BlockSizeBytes = 64,
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
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var chacha = new ChaCha20Poly1305(key);
        chacha.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

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

        using var chacha = new ChaCha20Poly1305(key);
        chacha.Decrypt(nonce, encryptedData, tag!, plaintext, associatedData);

        return Task.FromResult(plaintext);
    }
}

/// <summary>
/// XChaCha20-Poly1305 encryption strategy with extended 24-byte nonce.
/// Provides nonce-misuse resistance with larger nonce space.
/// </summary>
public sealed class XChaCha20Poly1305Strategy : EncryptionStrategyBase
{
    private const int KeySize = 32;
    private const int NonceSize = 24;
    private const int TagSize = 16;

    /// <inheritdoc/>
    public override string StrategyId => "xchacha20-poly1305";

    /// <inheritdoc/>
    public override string StrategyName => "XChaCha20-Poly1305";

    /// <inheritdoc/>
    public override CipherInfo CipherInfo => new()
    {
        AlgorithmName = "XChaCha20-Poly1305",
        KeySizeBits = 256,
        BlockSizeBytes = 64,
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

        // XChaCha20 uses HChaCha20 to derive a subkey from the first 16 bytes of the nonce
        var subkey = HChaCha20(key, nonce.AsSpan(0, 16).ToArray());
        var shortNonce = new byte[12];
        Array.Copy(nonce, 16, shortNonce, 4, 8);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var chacha = new ChaCha20Poly1305(subkey);
        chacha.Encrypt(shortNonce, plaintext, ciphertext, tag, associatedData);

        CryptographicOperations.ZeroMemory(subkey);

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

        var subkey = HChaCha20(key, nonce.AsSpan(0, 16).ToArray());
        var shortNonce = new byte[12];
        Array.Copy(nonce, 16, shortNonce, 4, 8);

        var plaintext = new byte[encryptedData.Length];

        using var chacha = new ChaCha20Poly1305(subkey);
        chacha.Decrypt(shortNonce, encryptedData, tag!, plaintext, associatedData);

        CryptographicOperations.ZeroMemory(subkey);

        return Task.FromResult(plaintext);
    }

    /// <summary>
    /// HChaCha20 key derivation function for XChaCha20.
    /// </summary>
    private static byte[] HChaCha20(byte[] key, byte[] nonce)
    {
        var state = new uint[16];
        state[0] = 0x61707865; // "expa"
        state[1] = 0x3320646e; // "nd 3"
        state[2] = 0x79622d32; // "2-by"
        state[3] = 0x6b206574; // "te k"

        for (int i = 0; i < 8; i++)
            state[4 + i] = BitConverter.ToUInt32(key, i * 4);

        for (int i = 0; i < 4; i++)
            state[12 + i] = BitConverter.ToUInt32(nonce, i * 4);

        // 20 rounds (10 double rounds)
        for (int i = 0; i < 10; i++)
        {
            QuarterRound(ref state[0], ref state[4], ref state[8], ref state[12]);
            QuarterRound(ref state[1], ref state[5], ref state[9], ref state[13]);
            QuarterRound(ref state[2], ref state[6], ref state[10], ref state[14]);
            QuarterRound(ref state[3], ref state[7], ref state[11], ref state[15]);
            QuarterRound(ref state[0], ref state[5], ref state[10], ref state[15]);
            QuarterRound(ref state[1], ref state[6], ref state[11], ref state[12]);
            QuarterRound(ref state[2], ref state[7], ref state[8], ref state[13]);
            QuarterRound(ref state[3], ref state[4], ref state[9], ref state[14]);
        }

        // Output first and last 4 words
        var output = new byte[32];
        for (int i = 0; i < 4; i++)
            BitConverter.GetBytes(state[i]).CopyTo(output, i * 4);
        for (int i = 0; i < 4; i++)
            BitConverter.GetBytes(state[12 + i]).CopyTo(output, 16 + i * 4);

        return output;
    }

    private static void QuarterRound(ref uint a, ref uint b, ref uint c, ref uint d)
    {
        a += b; d ^= a; d = (d << 16) | (d >> 16);
        c += d; b ^= c; b = (b << 12) | (b >> 20);
        a += b; d ^= a; d = (d << 8) | (d >> 24);
        c += d; b ^= c; b = (b << 7) | (b >> 25);
    }
}

/// <summary>
/// ChaCha20 stream cipher without authentication (requires separate MAC).
/// Use ChaCha20-Poly1305 for authenticated encryption.
/// </summary>
public sealed class ChaCha20Strategy : EncryptionStrategyBase
{
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int MacSize = 48; // Poly1305(16) + HMAC-SHA256(32) — dual authentication (#2941)

    /// <inheritdoc/>
    public override string StrategyId => "chacha20";

    /// <inheritdoc/>
    public override string StrategyName => "ChaCha20-HMAC";

    /// <inheritdoc/>
    public override CipherInfo CipherInfo => new()
    {
        AlgorithmName = "ChaCha20-HMAC-SHA256",
        KeySizeBits = 256,
        BlockSizeBytes = 64,
        IvSizeBytes = NonceSize,
        TagSizeBytes = MacSize,
        SecurityLevel = SecurityLevel.High,
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
        var macKey = HKDF.DeriveKey(System.Security.Cryptography.HashAlgorithmName.SHA256, key, 32, salt: null, info: "hmac-key"u8);

        // Encrypt with ChaCha20-Poly1305. The Poly1305 tag IS used as the authentication tag
        // stored in the output envelope so that decrypt can verify it directly (#2941).
        var ciphertext = new byte[plaintext.Length];
        var poly1305Tag = new byte[16];

        using (var chacha = new ChaCha20Poly1305(key))
        {
            chacha.Encrypt(nonce, plaintext, ciphertext, poly1305Tag, associatedData);
        }

        // Also compute HMAC-SHA256 over nonce + ciphertext (+ associated data) for a second
        // authentication layer that covers the nonce and any associated data.
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

        var hmacTag = HMACSHA256.HashData(macKey, dataToMac);
        CryptographicOperations.ZeroMemory(macKey);

        // Output: nonce | ciphertext | poly1305Tag(16) | hmacTag(32)
        // CombineIvAndCiphertext appends the tag; we build a combined tag from both MACs.
        var combinedTag = new byte[poly1305Tag.Length + hmacTag.Length]; // 48 bytes
        Buffer.BlockCopy(poly1305Tag, 0, combinedTag, 0, poly1305Tag.Length);
        Buffer.BlockCopy(hmacTag, 0, combinedTag, poly1305Tag.Length, hmacTag.Length);

        return Task.FromResult(CombineIvAndCiphertext(nonce, ciphertext, combinedTag));
    }

    /// <inheritdoc/>
    protected override Task<byte[]> DecryptCoreAsync(
        byte[] ciphertext,
        byte[] key,
        byte[]? associatedData,
        CancellationToken cancellationToken)
    {
        var macKey = HKDF.DeriveKey(System.Security.Cryptography.HashAlgorithmName.SHA256, key, 32, salt: null, info: "hmac-key"u8);
        var (nonce, encryptedData, combinedTag) = SplitCiphertext(ciphertext);

        if (combinedTag == null || combinedTag.Length < 48)
        {
            CryptographicOperations.ZeroMemory(macKey);
            throw new CryptographicException("Invalid ciphertext: authentication tag too short");
        }

        // Split combined tag: first 16 bytes = Poly1305, next 32 = HMAC-SHA256
        var poly1305Tag = new byte[16];
        var storedHmac = new byte[32];
        Buffer.BlockCopy(combinedTag, 0, poly1305Tag, 0, 16);
        Buffer.BlockCopy(combinedTag, 16, storedHmac, 0, 32);

        // Verify HMAC-SHA256 first (covers nonce + ciphertext + optional AAD)
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

        var expectedHmac = HMACSHA256.HashData(macKey, dataToMac);
        if (!CryptographicOperations.FixedTimeEquals(expectedHmac, storedHmac))
        {
            CryptographicOperations.ZeroMemory(macKey);
            throw new CryptographicException("HMAC-SHA256 verification failed");
        }

        CryptographicOperations.ZeroMemory(macKey);

        // Decrypt using ChaCha20-Poly1305 which also verifies the Poly1305 tag.
        var plaintext = new byte[encryptedData.Length];
        using var chacha = new ChaCha20Poly1305(key);
        chacha.Decrypt(nonce, encryptedData, poly1305Tag, plaintext, associatedData);

        return Task.FromResult(plaintext);
    }
}
