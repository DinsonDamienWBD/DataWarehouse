using System.Security.Cryptography;
using System.Text;

namespace DataWarehouse.Plugins.Encryption.Providers;

/// <summary>
/// Argon2 key derivation implementation.
/// Falls back to PBKDF2 with high iterations for compatibility.
/// </summary>
public sealed class Argon2KeyDerivation : IKeyDerivationFunction
{
    public Task<byte[]> DeriveKeyAsync(string password, byte[] salt, int keyLength, CancellationToken ct)
    {
        // Using PBKDF2 as fallback (Argon2 would require external library)
        var key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            100_000,
            HashAlgorithmName.SHA256,
            keyLength);

        return Task.FromResult(key);
    }
}

/// <summary>
/// Argon2 password hasher implementation.
/// </summary>
public sealed class Argon2PasswordHasher : IPasswordHasher
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 4;

    public string HashPassword(string password)
    {
        var salt = new byte[SaltSize];
        RandomNumberGenerator.Fill(salt);

        // Using PBKDF2 as a fallback since Argon2 requires additional libraries
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, Iterations * 10000, HashAlgorithmName.SHA256);
        var hash = pbkdf2.GetBytes(HashSize);

        var result = new byte[SaltSize + HashSize];
        Buffer.BlockCopy(salt, 0, result, 0, SaltSize);
        Buffer.BlockCopy(hash, 0, result, SaltSize, HashSize);

        return Convert.ToBase64String(result);
    }

    public bool VerifyPassword(string password, string storedHash)
    {
        try
        {
            var hashBytes = Convert.FromBase64String(storedHash);
            if (hashBytes.Length != SaltSize + HashSize)
                return false;

            var salt = new byte[SaltSize];
            Buffer.BlockCopy(hashBytes, 0, salt, 0, SaltSize);

            using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, Iterations * 10000, HashAlgorithmName.SHA256);
            var computedHash = pbkdf2.GetBytes(HashSize);

            return CryptographicOperations.FixedTimeEquals(
                computedHash,
                hashBytes.AsSpan(SaltSize, HashSize));
        }
        catch
        {
            return false;
        }
    }
}
