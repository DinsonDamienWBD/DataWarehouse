using System.Security.Cryptography;
using System.Text;

namespace DataWarehouse.Plugins.Encryption.Providers;

/// <summary>
/// Client-side encryption where server never sees plaintext data.
/// Provides privacy-preserving storage with server-blind operations.
/// </summary>
public sealed class ZeroKnowledgeEncryption
{
    private readonly IKeyDerivationFunction _kdf;
    private readonly ZeroKnowledgeConfig _config;

    public ZeroKnowledgeEncryption(IKeyDerivationFunction? kdf = null, ZeroKnowledgeConfig? config = null)
    {
        _kdf = kdf ?? new Argon2KeyDerivation();
        _config = config ?? new ZeroKnowledgeConfig();
    }

    /// <summary>
    /// Derives a client-side encryption key from user credentials.
    /// Server never learns the key.
    /// </summary>
    public async Task<ClientKey> DeriveClientKeyAsync(
        string userId,
        string password,
        CancellationToken ct = default)
    {
        var userIdBytes = Encoding.UTF8.GetBytes(userId);
        var salt = SHA256.HashData(userIdBytes);

        var keyMaterial = await _kdf.DeriveKeyAsync(password, salt, _config.KeyLength, ct);

        var encryptionKey = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            keyMaterial,
            _config.KeyLength,
            info: Encoding.UTF8.GetBytes("encryption"));

        var authKey = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            keyMaterial,
            _config.KeyLength,
            info: Encoding.UTF8.GetBytes("authentication"));

        var verificationToken = HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            keyMaterial,
            32,
            info: Encoding.UTF8.GetBytes("verification"));

        return new ClientKey
        {
            UserId = userId,
            EncryptionKey = encryptionKey,
            AuthenticationKey = authKey,
            VerificationToken = Convert.ToHexString(verificationToken).ToLowerInvariant()
        };
    }

    /// <summary>
    /// Encrypts data client-side. Server only stores ciphertext.
    /// </summary>
    public EncryptedClientData EncryptClientSide(ClientKey clientKey, byte[] plaintext)
    {
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(clientKey.EncryptionKey, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var hmac = HMACSHA256.HashData(clientKey.AuthenticationKey, ciphertext);

        return new EncryptedClientData
        {
            Nonce = nonce,
            Ciphertext = ciphertext,
            Tag = tag,
            Hmac = hmac,
            EncryptedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Decrypts data client-side.
    /// </summary>
    public byte[] DecryptClientSide(ClientKey clientKey, EncryptedClientData encrypted)
    {
        var expectedHmac = HMACSHA256.HashData(clientKey.AuthenticationKey, encrypted.Ciphertext);
        if (!CryptographicOperations.FixedTimeEquals(expectedHmac, encrypted.Hmac))
            throw new CryptographicException("Data integrity check failed");

        var plaintext = new byte[encrypted.Ciphertext.Length];

        using var aes = new AesGcm(clientKey.EncryptionKey, 16);
        aes.Decrypt(encrypted.Nonce, encrypted.Ciphertext, encrypted.Tag, plaintext);

        return plaintext;
    }

    /// <summary>
    /// Generates a searchable token for encrypted data.
    /// Enables server-side search without decryption.
    /// </summary>
    public string GenerateSearchToken(ClientKey clientKey, string searchTerm)
    {
        var termBytes = Encoding.UTF8.GetBytes(searchTerm.ToLowerInvariant());
        var token = HMACSHA256.HashData(clientKey.AuthenticationKey, termBytes);
        return Convert.ToHexString(token).ToLowerInvariant();
    }

    /// <summary>
    /// Enables secure key sharing using public key cryptography.
    /// </summary>
    public KeyShareResult ShareKey(ClientKey ownerKey, byte[] recipientPublicKey)
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP384);
        var ephemeralPublic = ecdh.ExportSubjectPublicKeyInfo();

        using var recipientEcdh = ECDiffieHellman.Create();
        recipientEcdh.ImportSubjectPublicKeyInfo(recipientPublicKey, out _);

        var sharedSecret = ecdh.DeriveKeyMaterial(recipientEcdh.PublicKey);

        var wrappingKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32);

        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);

        var encryptedKey = new byte[ownerKey.EncryptionKey.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(wrappingKey, 16);
        aes.Encrypt(nonce, ownerKey.EncryptionKey, encryptedKey, tag);

        return new KeyShareResult
        {
            EphemeralPublicKey = ephemeralPublic,
            EncryptedKey = encryptedKey,
            Nonce = nonce,
            Tag = tag
        };
    }
}
