using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace DataWarehouse.Plugins.Encryption;

#region Enums

/// <summary>
/// Specifies the encryption algorithm to use.
/// </summary>
public enum EncryptionAlgorithm
{
    /// <summary>AES-256 with GCM mode (default, authenticated).</summary>
    Aes256Gcm,
    /// <summary>AES-256 with CBC mode and HMAC authentication.</summary>
    Aes256CbcHmac,
    /// <summary>ChaCha20-Poly1305 (fast on systems without AES-NI).</summary>
    ChaCha20Poly1305,
    /// <summary>XChaCha20-Poly1305 (extended nonce variant).</summary>
    XChaCha20Poly1305,
    /// <summary>Twofish cipher in CTR mode with HMAC.</summary>
    Twofish,
    /// <summary>Serpent cipher in CTR mode with HMAC.</summary>
    Serpent
}

/// <summary>
/// Specifies the key derivation function to use.
/// </summary>
public enum KeyDerivationFunction
{
    /// <summary>PBKDF2 with SHA-256.</summary>
    Pbkdf2Sha256,
    /// <summary>PBKDF2 with SHA-512.</summary>
    Pbkdf2Sha512,
    /// <summary>Argon2id (memory-hard, recommended).</summary>
    Argon2id,
    /// <summary>scrypt (memory-hard).</summary>
    Scrypt
}

#endregion

#region Interfaces

/// <summary>
/// Interface for encryption providers.
/// </summary>
public interface IEncryptionProvider
{
    /// <summary>Gets the algorithm name.</summary>
    string AlgorithmName { get; }

    /// <summary>Gets the key size in bits.</summary>
    int KeySizeBits { get; }

    /// <summary>Gets the nonce/IV size in bytes.</summary>
    int NonceSizeBytes { get; }

    /// <summary>Encrypts plaintext.</summary>
    EncryptedPayload Encrypt(byte[] plaintext, byte[] key);

    /// <summary>Decrypts ciphertext.</summary>
    byte[] Decrypt(EncryptedPayload payload, byte[] key);
}

/// <summary>
/// Interface for key derivation functions.
/// </summary>
public interface IKeyDerivationFunction
{
    Task<byte[]> DeriveKeyAsync(string password, byte[] salt, int keyLength, CancellationToken ct);
}

/// <summary>
/// Interface for password hashers.
/// </summary>
public interface IPasswordHasher
{
    /// <summary>Hashes a password.</summary>
    string HashPassword(string password);

    /// <summary>Verifies a password.</summary>
    bool VerifyPassword(string password, string hash);
}

#endregion

#region Records and Classes

/// <summary>
/// Encrypted payload with metadata.
/// </summary>
public sealed record EncryptedPayload
{
    public required string Algorithm { get; init; }
    public required byte[] Nonce { get; init; }
    public required byte[] Ciphertext { get; init; }
    public required byte[] Tag { get; init; }
    public DateTime EncryptedAt { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>
/// Information about encryption configuration.
/// </summary>
public sealed record EncryptionInfo
{
    public EncryptionAlgorithm Algorithm { get; init; }
    public KeyDerivationFunction KeyDerivation { get; init; }
    public int KeySizeBits { get; init; }
    public bool HardwareAccelerationEnabled { get; init; }
    public bool HardwareAccelerationAvailable { get; init; }
}

/// <summary>
/// Configuration for extended encryption manager.
/// </summary>
public sealed record EncryptionManagerConfig
{
    /// <summary>Gets or sets the encryption algorithm.</summary>
    public EncryptionAlgorithm Algorithm { get; init; } = EncryptionAlgorithm.Aes256Gcm;

    /// <summary>Gets or sets the key derivation function.</summary>
    public KeyDerivationFunction KeyDerivation { get; init; } = KeyDerivationFunction.Argon2id;

    /// <summary>Gets or sets PBKDF2 iterations (if using PBKDF2).</summary>
    public int Pbkdf2Iterations { get; init; } = 600_000;

    /// <summary>Gets or sets Argon2 memory cost in KB.</summary>
    public int Argon2MemoryKb { get; init; } = 65536;

    /// <summary>Gets or sets Argon2 time cost (iterations).</summary>
    public int Argon2TimeCost { get; init; } = 3;

    /// <summary>Gets or sets Argon2 parallelism.</summary>
    public int Argon2Parallelism { get; init; } = 4;

    /// <summary>Gets or sets scrypt N parameter (CPU/memory cost).</summary>
    public int ScryptN { get; init; } = 1 << 20;

    /// <summary>Gets or sets scrypt r parameter (block size).</summary>
    public int ScryptR { get; init; } = 8;

    /// <summary>Gets or sets scrypt p parameter (parallelism).</summary>
    public int ScryptP { get; init; } = 1;

    /// <summary>Gets or sets whether to use hardware acceleration if available.</summary>
    public bool UseHardwareAcceleration { get; init; } = true;
}

/// <summary>
/// Exception thrown when encryption operations fail.
/// </summary>
public sealed class EncryptionException : Exception
{
    public EncryptionException(string message) : base(message) { }
    public EncryptionException(string message, Exception inner) : base(message, inner) { }
}

#endregion

#region Zero-Knowledge Encryption Types

/// <summary>
/// Client key for zero-knowledge encryption.
/// </summary>
public sealed class ClientKey
{
    public required string UserId { get; init; }
    public required byte[] EncryptionKey { get; init; }
    public required byte[] AuthenticationKey { get; init; }
    public required string VerificationToken { get; init; }
}

/// <summary>
/// Encrypted client data for zero-knowledge storage.
/// </summary>
public record EncryptedClientData
{
    public required byte[] Nonce { get; init; }
    public required byte[] Ciphertext { get; init; }
    public required byte[] Tag { get; init; }
    public required byte[] Hmac { get; init; }
    public DateTime EncryptedAt { get; init; }
}

/// <summary>
/// Result of key sharing operation.
/// </summary>
public record KeyShareResult
{
    public required byte[] EphemeralPublicKey { get; init; }
    public required byte[] EncryptedKey { get; init; }
    public required byte[] Nonce { get; init; }
    public required byte[] Tag { get; init; }
}

/// <summary>
/// Configuration for zero-knowledge encryption.
/// </summary>
public sealed class ZeroKnowledgeConfig
{
    public int KeyLength { get; set; } = 32;
    public int Iterations { get; set; } = 100_000;
}

#endregion
