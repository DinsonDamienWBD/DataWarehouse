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
/// AES-256-CBC with HMAC-SHA256 transit encryption strategy.
/// Provides legacy compatibility using Encrypt-then-MAC pattern.
/// Suitable for endpoints that don't support AEAD modes.
/// </summary>
public sealed class AesCbcTransitStrategy : TransitEncryptionPluginBase
{
    private const int KeySize = 32; // 256 bits for AES
    private const int HmacKeySize = 32; // 256 bits for HMAC-SHA256
    private const int IvSize = 16; // AES block size
    private const int HmacSize = 32; // SHA256 output

    /// <inheritdoc/>
    public override string Id => "transit-aes256cbc-hmac";

    /// <inheritdoc/>
    public override string Name => "AES-256-CBC with HMAC-SHA256 Transit Encryption";

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

        // Key should be KeySize + HmacKeySize (64 bytes total)
        if (key.Length != KeySize + HmacKeySize)
        {
            throw new ArgumentException(
                $"Key must be {KeySize + HmacKeySize} bytes (32 for AES + 32 for HMAC)",
                nameof(key));
        }

        // Split key into encryption key and HMAC key
        var encryptionKey = new byte[KeySize];
        var hmacKey = new byte[HmacKeySize];
        Buffer.BlockCopy(key, 0, encryptionKey, 0, KeySize);
        Buffer.BlockCopy(key, KeySize, hmacKey, 0, HmacKeySize);

        // Generate random IV
        var iv = new byte[IvSize];
        RandomNumberGenerator.Fill(iv);

        // Encrypt with AES-CBC
        byte[] ciphertext;
        using (var aes = System.Security.Cryptography.Aes.Create())
        {
            aes.KeySize = KeySize * 8;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = encryptionKey;
            aes.IV = iv;

            using var encryptor = aes.CreateEncryptor();
            ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
        }

        // Compute HMAC over IV + ciphertext + AAD (Encrypt-then-MAC)
        byte[] hmac;
        using (var hmacAlg = new HMACSHA256(hmacKey))
        {
            // Build authenticated data: IV + ciphertext
            var dataToAuthenticate = new byte[IvSize + ciphertext.Length + (aad?.Length ?? 0)];
            Buffer.BlockCopy(iv, 0, dataToAuthenticate, 0, IvSize);
            Buffer.BlockCopy(ciphertext, 0, dataToAuthenticate, IvSize, ciphertext.Length);
            if (aad != null && aad.Length > 0)
            {
                Buffer.BlockCopy(aad, 0, dataToAuthenticate, IvSize + ciphertext.Length, aad.Length);
            }

            hmac = hmacAlg.ComputeHash(dataToAuthenticate);
        }

        // Secure memory cleanup
        CryptographicOperations.ZeroMemory(encryptionKey);
        CryptographicOperations.ZeroMemory(hmacKey);

        // Combine IV + ciphertext + HMAC
        var combined = new byte[IvSize + ciphertext.Length + HmacSize];
        Buffer.BlockCopy(iv, 0, combined, 0, IvSize);
        Buffer.BlockCopy(ciphertext, 0, combined, IvSize, ciphertext.Length);
        Buffer.BlockCopy(hmac, 0, combined, IvSize + ciphertext.Length, HmacSize);

        // Build metadata
        var metadata = new Dictionary<string, object>
        {
            ["PresetId"] = preset.Id,
            ["Algorithm"] = "AES-256-CBC",
            ["MacAlgorithm"] = "HMAC-SHA256",
            ["IvSize"] = IvSize,
            ["HmacSize"] = HmacSize,
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

        if (key.Length != KeySize + HmacKeySize)
        {
            throw new ArgumentException(
                $"Key must be {KeySize + HmacKeySize} bytes",
                nameof(key));
        }

        // Validate minimum size
        var minSize = IvSize + HmacSize;
        if (ciphertext.Length < minSize)
        {
            throw new ArgumentException(
                $"Ciphertext too short. Expected at least {minSize} bytes, got {ciphertext.Length}",
                nameof(ciphertext));
        }

        // Split key
        var encryptionKey = new byte[KeySize];
        var hmacKey = new byte[HmacKeySize];
        Buffer.BlockCopy(key, 0, encryptionKey, 0, KeySize);
        Buffer.BlockCopy(key, KeySize, hmacKey, 0, HmacKeySize);

        // Extract IV, encrypted data, and HMAC
        var iv = new byte[IvSize];
        var encryptedDataLength = ciphertext.Length - IvSize - HmacSize;
        var encryptedData = new byte[encryptedDataLength];
        var receivedHmac = new byte[HmacSize];

        Buffer.BlockCopy(ciphertext, 0, iv, 0, IvSize);
        Buffer.BlockCopy(ciphertext, IvSize, encryptedData, 0, encryptedDataLength);
        Buffer.BlockCopy(ciphertext, IvSize + encryptedDataLength, receivedHmac, 0, HmacSize);

        // Extract AAD from metadata if present
        byte[]? aad = null;
        if (metadata.TryGetValue("AAD", out var aadObj) && aadObj is byte[] aadBytes)
        {
            aad = aadBytes;
        }

        // Verify HMAC (constant-time comparison)
        using (var hmacAlg = new HMACSHA256(hmacKey))
        {
            var dataToAuthenticate = new byte[IvSize + encryptedDataLength + (aad?.Length ?? 0)];
            Buffer.BlockCopy(iv, 0, dataToAuthenticate, 0, IvSize);
            Buffer.BlockCopy(encryptedData, 0, dataToAuthenticate, IvSize, encryptedDataLength);
            if (aad != null && aad.Length > 0)
            {
                Buffer.BlockCopy(aad, 0, dataToAuthenticate, IvSize + encryptedDataLength, aad.Length);
            }

            var computedHmac = hmacAlg.ComputeHash(dataToAuthenticate);

            if (!CryptographicOperations.FixedTimeEquals(computedHmac, receivedHmac))
            {
                CryptographicOperations.ZeroMemory(encryptionKey);
                CryptographicOperations.ZeroMemory(hmacKey);
                throw new CryptographicException("HMAC verification failed. Data may have been tampered with.");
            }
        }

        // Decrypt
        byte[] plaintext;
        using (var aes = System.Security.Cryptography.Aes.Create())
        {
            aes.KeySize = KeySize * 8;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = encryptionKey;
            aes.IV = iv;

            using var decryptor = aes.CreateDecryptor();
            plaintext = decryptor.TransformFinalBlock(encryptedData, 0, encryptedDataLength);
        }

        // Secure memory cleanup
        CryptographicOperations.ZeroMemory(encryptionKey);
        CryptographicOperations.ZeroMemory(hmacKey);

        return Task.FromResult(plaintext);
    }
}
