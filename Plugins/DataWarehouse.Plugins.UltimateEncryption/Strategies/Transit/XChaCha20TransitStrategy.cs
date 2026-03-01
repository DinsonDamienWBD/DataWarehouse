using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Security.Transit;
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateEncryption.Strategies.Transit;

/// <summary>
/// XChaCha20-Poly1305 transit encryption strategy.
/// Extended nonce variant of ChaCha20-Poly1305 with 192-bit (24-byte) nonce.
/// Safer for high-volume transfers where nonce reuse risk is a concern.
/// Preferred when large amounts of data are encrypted with the same key.
/// </summary>
public sealed class XChaCha20TransitStrategy : TransitEncryptionPluginBase
{
    private const int KeySize = 32; // 256 bits
    private const int NonceSize = 24; // 192 bits (extended nonce)
    private const int TagSize = 16; // 128 bits Poly1305 tag
    private const int ChunkSize = 64 * 1024; // 64KB chunks for streaming

    /// <inheritdoc/>
    public override string Id => "transit-xchacha20poly1305";

    /// <inheritdoc/>
    public override string Name => "XChaCha20-Poly1305 Transit Encryption";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    protected override Task<(byte[] Ciphertext, Dictionary<string, object> Metadata)> EncryptDataAsync(
        byte[] plaintext,
        CipherPreset preset,
        byte[] key,
        byte[]? aad,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ArgumentNullException.ThrowIfNull(key);

        if (key.Length != KeySize)
        {
            throw new ArgumentException($"Key must be {KeySize} bytes for XChaCha20-Poly1305", nameof(key));
        }

        // Generate random extended nonce (24 bytes)
        var nonce = new byte[NonceSize];
        RandomNumberGenerator.Fill(nonce);

        // XChaCha20-Poly1305 encryption
        // Note: .NET does not have native XChaCha20-Poly1305 support yet
        // This is a simplified implementation for demonstration
        // Production code would use a library like libsodium or BouncyCastle

        var (ciphertext, tag) = EncryptXChaCha20Poly1305(plaintext, key, nonce, aad);

        // Combine nonce + ciphertext + tag
        var combined = new byte[NonceSize + ciphertext.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, combined, 0, NonceSize);
        Buffer.BlockCopy(ciphertext, 0, combined, NonceSize, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, combined, NonceSize + ciphertext.Length, TagSize);

        // Build metadata
        var metadata = new Dictionary<string, object>
        {
            ["PresetId"] = preset.Id,
            ["Algorithm"] = "XChaCha20-Poly1305",
            ["NonceSize"] = NonceSize,
            ["TagSize"] = TagSize,
            ["KeySize"] = KeySize,
            ["Compressed"] = false,
            ["EncryptedAt"] = DateTime.UtcNow
        };

        return Task.FromResult((combined, metadata));
    }

    /// <inheritdoc/>
    protected override Task<byte[]> DecryptDataAsync(
        byte[] ciphertext,
        CipherPreset preset,
        byte[] key,
        Dictionary<string, object> metadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(metadata);

        if (key.Length != KeySize)
        {
            throw new ArgumentException($"Key must be {KeySize} bytes for XChaCha20-Poly1305", nameof(key));
        }

        // Validate minimum size
        var minSize = NonceSize + TagSize;
        if (ciphertext.Length < minSize)
        {
            throw new ArgumentException(
                $"Ciphertext too short. Expected at least {minSize} bytes, got {ciphertext.Length}",
                nameof(ciphertext));
        }

        // Extract nonce, encrypted data, and tag
        var nonce = new byte[NonceSize];
        var encryptedDataLength = ciphertext.Length - NonceSize - TagSize;
        var encryptedData = new byte[encryptedDataLength];
        var tag = new byte[TagSize];

        Buffer.BlockCopy(ciphertext, 0, nonce, 0, NonceSize);
        Buffer.BlockCopy(ciphertext, NonceSize, encryptedData, 0, encryptedDataLength);
        Buffer.BlockCopy(ciphertext, NonceSize + encryptedDataLength, tag, 0, TagSize);

        // Extract AAD from metadata if present
        byte[]? aad = null;
        if (metadata.TryGetValue("AAD", out var aadObj) && aadObj is byte[] aadBytes)
        {
            aad = aadBytes;
        }

        // Decrypt
        var plaintext = DecryptXChaCha20Poly1305(encryptedData, key, nonce, tag, aad);

        return Task.FromResult(plaintext);
    }

    /// <summary>
    /// Encrypts data using XChaCha20-Poly1305.
    /// Simplified implementation - production code should use libsodium or similar.
    /// </summary>
    private static (byte[] ciphertext, byte[] tag) EncryptXChaCha20Poly1305(
        byte[] plaintext,
        byte[] key,
        byte[] nonce,
        byte[]? aad)
    {
        // Step 1: Derive subkey and subnonce using HChaCha20
        var (subkey, subnonce) = DeriveXChaChaParameters(key, nonce);

        // Step 2: Encrypt with ChaCha20-Poly1305 using derived parameters
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using var chacha = new ChaCha20Poly1305(subkey);
        chacha.Encrypt(subnonce, plaintext, ciphertext, tag, aad);

        // Secure memory cleanup
        CryptographicOperations.ZeroMemory(subkey);

        return (ciphertext, tag);
    }

    /// <summary>
    /// Decrypts data using XChaCha20-Poly1305.
    /// </summary>
    private static byte[] DecryptXChaCha20Poly1305(
        byte[] ciphertext,
        byte[] key,
        byte[] nonce,
        byte[] tag,
        byte[]? aad)
    {
        // Step 1: Derive subkey and subnonce using HChaCha20
        var (subkey, subnonce) = DeriveXChaChaParameters(key, nonce);

        // Step 2: Decrypt with ChaCha20-Poly1305 using derived parameters
        var plaintext = new byte[ciphertext.Length];

        using var chacha = new ChaCha20Poly1305(subkey);
        chacha.Decrypt(subnonce, ciphertext, tag, plaintext, aad);

        // Secure memory cleanup
        CryptographicOperations.ZeroMemory(subkey);

        return plaintext;
    }

    /// <summary>
    /// Derives subkey and subnonce from XChaCha20 extended nonce.
    /// Uses HChaCha20 for subkey derivation as required by the XChaCha20 specification.
    /// </summary>
    private static (byte[] subkey, byte[] subnonce) DeriveXChaChaParameters(byte[] key, byte[] nonce)
    {
        if (nonce.Length != NonceSize)
        {
            throw new ArgumentException($"Nonce must be {NonceSize} bytes for XChaCha20", nameof(nonce));
        }

        // XChaCha20 requires HChaCha20 for subkey derivation. HChaCha20 is a dedicated
        // construction that applies the ChaCha20 core function with the first 16 bytes of the
        // extended nonce, then discards the middle 32 bytes of the 64-byte output (keeping
        // words 0-3 and 12-15). Replacing it with HKDF-SHA256 produces a cipher that is
        // incompatible with any real XChaCha20 implementation (finding #2963).
        // BouncyCastle does not expose HChaCha20 as a standalone primitive. Per Rule 13,
        // throw NotSupportedException rather than silently produce a broken, incompatible cipher.
        throw new NotSupportedException(
            "XChaCha20 requires HChaCha20 for subkey derivation. The current BouncyCastle " +
            "version does not expose HChaCha20 as a standalone primitive. Use " +
            "ChaCha20-Poly1305 with a 12-byte nonce, or provide a library that implements " +
            "the full XChaCha20 specification (RFC draft-irtf-cfrg-xchacha).");
    }

    /// <inheritdoc/>
    public override async Task<EndpointCapabilities> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        var capabilities = new EndpointCapabilities(
            SupportedCipherPresets: new List<string>
            {
                "standard-xchacha20poly1305",
                "high-xchacha20poly1305"
            }.AsReadOnly(),
            SupportedAlgorithms: new List<string>
            {
                "XChaCha20-Poly1305"
            }.AsReadOnly(),
            PreferredPresetId: "standard-xchacha20poly1305",
            MaximumSecurityLevel: TransitSecurityLevel.High,
            SupportsTranscryption: false,
            Metadata: new Dictionary<string, object>
            {
                ["ExtendedNonce"] = true,
                ["NonceSize"] = NonceSize,
                ["SuitableForHighVolume"] = true,
                ["Type"] = "AEAD-Extended"
            }.AsReadOnly()
        );

        return Task.FromResult(capabilities);
    }
}
