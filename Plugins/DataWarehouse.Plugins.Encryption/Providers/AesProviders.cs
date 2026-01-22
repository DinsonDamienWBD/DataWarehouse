using System.Security.Cryptography;

namespace DataWarehouse.Plugins.Encryption.Providers;

/// <summary>
/// AES-256-GCM encryption provider (default).
/// </summary>
public sealed class Aes256GcmProvider : IEncryptionProvider
{
    public string AlgorithmName => "AES-256-GCM";
    public int KeySizeBits => 256;
    public int NonceSizeBytes => 12;

    public EncryptedPayload Encrypt(byte[] plaintext, byte[] key)
    {
        var nonce = new byte[NonceSizeBytes];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(key, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        return new EncryptedPayload
        {
            Algorithm = AlgorithmName,
            Nonce = nonce,
            Ciphertext = ciphertext,
            Tag = tag,
            EncryptedAt = DateTime.UtcNow
        };
    }

    public byte[] Decrypt(EncryptedPayload payload, byte[] key)
    {
        var plaintext = new byte[payload.Ciphertext.Length];

        using var aes = new AesGcm(key, 16);
        aes.Decrypt(payload.Nonce, payload.Ciphertext, payload.Tag, plaintext);

        return plaintext;
    }
}

/// <summary>
/// AES-256-CBC with HMAC-SHA256 encryption provider.
/// </summary>
public sealed class Aes256CbcHmacProvider : IEncryptionProvider
{
    public string AlgorithmName => "AES-256-CBC-HMAC-SHA256";
    public int KeySizeBits => 256;
    public int NonceSizeBytes => 16;

    public EncryptedPayload Encrypt(byte[] plaintext, byte[] key)
    {
        // Split key: first half for encryption, second half for HMAC
        var encKey = key.AsSpan(0, 32).ToArray();
        var macKey = key.Length > 32 ? key.AsSpan(32).ToArray() : SHA256.HashData(key);

        var iv = new byte[NonceSizeBytes];
        RandomNumberGenerator.Fill(iv);

        byte[] ciphertext;
        using (var aes = Aes.Create())
        {
            aes.Key = encKey;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var encryptor = aes.CreateEncryptor();
            ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
        }

        // Compute HMAC over IV + ciphertext
        var dataToMac = new byte[iv.Length + ciphertext.Length];
        Array.Copy(iv, dataToMac, iv.Length);
        Array.Copy(ciphertext, 0, dataToMac, iv.Length, ciphertext.Length);

        var tag = HMACSHA256.HashData(macKey, dataToMac);

        return new EncryptedPayload
        {
            Algorithm = AlgorithmName,
            Nonce = iv,
            Ciphertext = ciphertext,
            Tag = tag,
            EncryptedAt = DateTime.UtcNow
        };
    }

    public byte[] Decrypt(EncryptedPayload payload, byte[] key)
    {
        var encKey = key.AsSpan(0, 32).ToArray();
        var macKey = key.Length > 32 ? key.AsSpan(32).ToArray() : SHA256.HashData(key);

        // Verify HMAC
        var dataToMac = new byte[payload.Nonce.Length + payload.Ciphertext.Length];
        Array.Copy(payload.Nonce, dataToMac, payload.Nonce.Length);
        Array.Copy(payload.Ciphertext, 0, dataToMac, payload.Nonce.Length, payload.Ciphertext.Length);

        var expectedTag = HMACSHA256.HashData(macKey, dataToMac);
        if (!CryptographicOperations.FixedTimeEquals(expectedTag, payload.Tag))
            throw new CryptographicException("MAC verification failed");

        using var aes = Aes.Create();
        aes.Key = encKey;
        aes.IV = payload.Nonce;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(payload.Ciphertext, 0, payload.Ciphertext.Length);
    }
}
