// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using System.Security.Cryptography;
using Org.BouncyCastle.Crypto.Digests;

namespace DataWarehouse.Plugins.UltimateDataIntegrity.Hashing;

/// <summary>
/// Interface for hash algorithm providers supporting multiple computation modes.
/// </summary>
public interface IHashProvider
{
    /// <summary>
    /// Gets the algorithm name for identification.
    /// </summary>
    string AlgorithmName { get; }

    /// <summary>
    /// Gets the hash output size in bytes.
    /// </summary>
    int HashSizeBytes { get; }

    /// <summary>
    /// Computes a hash from a span of bytes.
    /// </summary>
    /// <param name="data">The data to hash.</param>
    /// <returns>The computed hash.</returns>
    byte[] ComputeHash(ReadOnlySpan<byte> data);

    /// <summary>
    /// Computes a hash from a stream synchronously.
    /// </summary>
    /// <param name="data">The stream to hash.</param>
    /// <returns>The computed hash.</returns>
    byte[] ComputeHash(Stream data);

    /// <summary>
    /// Computes a hash from a stream asynchronously.
    /// </summary>
    /// <param name="data">The stream to hash.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The computed hash.</returns>
    Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default);
}

#region T4.16 - SHA-3 Variants

/// <summary>
/// SHA3-256 hash provider using BouncyCastle implementation.
/// Provides 256-bit (32 byte) hash output using the NIST SHA-3 standard.
/// </summary>
public class Sha3_256Provider : IHashProvider
{
    /// <inheritdoc />
    public string AlgorithmName => "SHA3-256";

    /// <inheritdoc />
    public int HashSizeBytes => 32;

    /// <inheritdoc />
    public byte[] ComputeHash(ReadOnlySpan<byte> data)
    {
        var digest = new Sha3Digest(256);
        digest.BlockUpdate(data.ToArray(), 0, data.Length);
        var result = new byte[HashSizeBytes];
        digest.DoFinal(result, 0);
        return result;
    }

    /// <inheritdoc />
    public byte[] ComputeHash(Stream data)
    {
        var digest = new Sha3Digest(256);
        var buffer = new byte[8192];
        int bytesRead;
        while ((bytesRead = data.Read(buffer, 0, buffer.Length)) > 0)
        {
            digest.BlockUpdate(buffer, 0, bytesRead);
        }
        var result = new byte[HashSizeBytes];
        digest.DoFinal(result, 0);
        return result;
    }

    /// <inheritdoc />
    public async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default)
    {
        var digest = new Sha3Digest(256);
        var buffer = new byte[8192];
        int bytesRead;
        while ((bytesRead = await data.ReadAsync(buffer, ct)) > 0)
        {
            digest.BlockUpdate(buffer, 0, bytesRead);
        }
        var result = new byte[HashSizeBytes];
        digest.DoFinal(result, 0);
        return result;
    }
}

/// <summary>
/// SHA3-384 hash provider using BouncyCastle implementation.
/// Provides 384-bit (48 byte) hash output using the NIST SHA-3 standard.
/// </summary>
public class Sha3_384Provider : IHashProvider
{
    /// <inheritdoc />
    public string AlgorithmName => "SHA3-384";

    /// <inheritdoc />
    public int HashSizeBytes => 48;

    /// <inheritdoc />
    public byte[] ComputeHash(ReadOnlySpan<byte> data)
    {
        var digest = new Sha3Digest(384);
        digest.BlockUpdate(data.ToArray(), 0, data.Length);
        var result = new byte[HashSizeBytes];
        digest.DoFinal(result, 0);
        return result;
    }

    /// <inheritdoc />
    public byte[] ComputeHash(Stream data)
    {
        var digest = new Sha3Digest(384);
        var buffer = new byte[8192];
        int bytesRead;
        while ((bytesRead = data.Read(buffer, 0, buffer.Length)) > 0)
        {
            digest.BlockUpdate(buffer, 0, bytesRead);
        }
        var result = new byte[HashSizeBytes];
        digest.DoFinal(result, 0);
        return result;
    }

    /// <inheritdoc />
    public async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default)
    {
        var digest = new Sha3Digest(384);
        var buffer = new byte[8192];
        int bytesRead;
        while ((bytesRead = await data.ReadAsync(buffer, ct)) > 0)
        {
            digest.BlockUpdate(buffer, 0, bytesRead);
        }
        var result = new byte[HashSizeBytes];
        digest.DoFinal(result, 0);
        return result;
    }
}

/// <summary>
/// SHA3-512 hash provider using BouncyCastle implementation.
/// Provides 512-bit (64 byte) hash output using the NIST SHA-3 standard.
/// </summary>
public class Sha3_512Provider : IHashProvider
{
    /// <inheritdoc />
    public string AlgorithmName => "SHA3-512";

    /// <inheritdoc />
    public int HashSizeBytes => 64;

    /// <inheritdoc />
    public byte[] ComputeHash(ReadOnlySpan<byte> data)
    {
        var digest = new Sha3Digest(512);
        digest.BlockUpdate(data.ToArray(), 0, data.Length);
        var result = new byte[HashSizeBytes];
        digest.DoFinal(result, 0);
        return result;
    }

    /// <inheritdoc />
    public byte[] ComputeHash(Stream data)
    {
        var digest = new Sha3Digest(512);
        var buffer = new byte[8192];
        int bytesRead;
        while ((bytesRead = data.Read(buffer, 0, buffer.Length)) > 0)
        {
            digest.BlockUpdate(buffer, 0, bytesRead);
        }
        var result = new byte[HashSizeBytes];
        digest.DoFinal(result, 0);
        return result;
    }

    /// <inheritdoc />
    public async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default)
    {
        var digest = new Sha3Digest(512);
        var buffer = new byte[8192];
        int bytesRead;
        while ((bytesRead = await data.ReadAsync(buffer, ct)) > 0)
        {
            digest.BlockUpdate(buffer, 0, bytesRead);
        }
        var result = new byte[HashSizeBytes];
        digest.DoFinal(result, 0);
        return result;
    }
}

#endregion

#region T4.17 - Keccak Variants

/// <summary>
/// Keccak-256 hash provider using BouncyCastle implementation.
/// Original Keccak submission (pre-NIST padding), used by Ethereum.
/// </summary>
public class Keccak256Provider : IHashProvider
{
    /// <inheritdoc />
    public string AlgorithmName => "Keccak-256";

    /// <inheritdoc />
    public int HashSizeBytes => 32;

    /// <inheritdoc />
    public byte[] ComputeHash(ReadOnlySpan<byte> data)
    {
        var digest = new KeccakDigest(256);
        digest.BlockUpdate(data.ToArray(), 0, data.Length);
        var result = new byte[HashSizeBytes];
        digest.DoFinal(result, 0);
        return result;
    }

    /// <inheritdoc />
    public byte[] ComputeHash(Stream data)
    {
        var digest = new KeccakDigest(256);
        var buffer = new byte[8192];
        int bytesRead;
        while ((bytesRead = data.Read(buffer, 0, buffer.Length)) > 0)
        {
            digest.BlockUpdate(buffer, 0, bytesRead);
        }
        var result = new byte[HashSizeBytes];
        digest.DoFinal(result, 0);
        return result;
    }

    /// <inheritdoc />
    public async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default)
    {
        var digest = new KeccakDigest(256);
        var buffer = new byte[8192];
        int bytesRead;
        while ((bytesRead = await data.ReadAsync(buffer, ct)) > 0)
        {
            digest.BlockUpdate(buffer, 0, bytesRead);
        }
        var result = new byte[HashSizeBytes];
        digest.DoFinal(result, 0);
        return result;
    }
}

/// <summary>
/// Keccak-384 hash provider using BouncyCastle implementation.
/// Original Keccak submission (pre-NIST padding).
/// </summary>
public class Keccak384Provider : IHashProvider
{
    /// <inheritdoc />
    public string AlgorithmName => "Keccak-384";

    /// <inheritdoc />
    public int HashSizeBytes => 48;

    /// <inheritdoc />
    public byte[] ComputeHash(ReadOnlySpan<byte> data)
    {
        var digest = new KeccakDigest(384);
        digest.BlockUpdate(data.ToArray(), 0, data.Length);
        var result = new byte[HashSizeBytes];
        digest.DoFinal(result, 0);
        return result;
    }

    /// <inheritdoc />
    public byte[] ComputeHash(Stream data)
    {
        var digest = new KeccakDigest(384);
        var buffer = new byte[8192];
        int bytesRead;
        while ((bytesRead = data.Read(buffer, 0, buffer.Length)) > 0)
        {
            digest.BlockUpdate(buffer, 0, bytesRead);
        }
        var result = new byte[HashSizeBytes];
        digest.DoFinal(result, 0);
        return result;
    }

    /// <inheritdoc />
    public async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default)
    {
        var digest = new KeccakDigest(384);
        var buffer = new byte[8192];
        int bytesRead;
        while ((bytesRead = await data.ReadAsync(buffer, ct)) > 0)
        {
            digest.BlockUpdate(buffer, 0, bytesRead);
        }
        var result = new byte[HashSizeBytes];
        digest.DoFinal(result, 0);
        return result;
    }
}

/// <summary>
/// Keccak-512 hash provider using BouncyCastle implementation.
/// Original Keccak submission (pre-NIST padding).
/// </summary>
public class Keccak512Provider : IHashProvider
{
    /// <inheritdoc />
    public string AlgorithmName => "Keccak-512";

    /// <inheritdoc />
    public int HashSizeBytes => 64;

    /// <inheritdoc />
    public byte[] ComputeHash(ReadOnlySpan<byte> data)
    {
        var digest = new KeccakDigest(512);
        digest.BlockUpdate(data.ToArray(), 0, data.Length);
        var result = new byte[HashSizeBytes];
        digest.DoFinal(result, 0);
        return result;
    }

    /// <inheritdoc />
    public byte[] ComputeHash(Stream data)
    {
        var digest = new KeccakDigest(512);
        var buffer = new byte[8192];
        int bytesRead;
        while ((bytesRead = data.Read(buffer, 0, buffer.Length)) > 0)
        {
            digest.BlockUpdate(buffer, 0, bytesRead);
        }
        var result = new byte[HashSizeBytes];
        digest.DoFinal(result, 0);
        return result;
    }

    /// <inheritdoc />
    public async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default)
    {
        var digest = new KeccakDigest(512);
        var buffer = new byte[8192];
        int bytesRead;
        while ((bytesRead = await data.ReadAsync(buffer, ct)) > 0)
        {
            digest.BlockUpdate(buffer, 0, bytesRead);
        }
        var result = new byte[HashSizeBytes];
        digest.DoFinal(result, 0);
        return result;
    }
}

#endregion

#region T4.18 - HMAC Variants

/// <summary>
/// HMAC-SHA256 provider using System.Security.Cryptography.
/// Provides keyed hash authentication using SHA-256.
/// </summary>
public class HmacSha256Provider : IHashProvider
{
    private readonly byte[] _key;

    /// <summary>
    /// Initializes a new HMAC-SHA256 provider with the specified key.
    /// </summary>
    /// <param name="key">The secret key for HMAC computation.</param>
    public HmacSha256Provider(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length == 0)
            throw new ArgumentException("Key cannot be empty.", nameof(key));
        _key = (byte[])key.Clone();
    }

    /// <inheritdoc />
    public string AlgorithmName => "HMAC-SHA256";

    /// <inheritdoc />
    public int HashSizeBytes => 32;

    /// <inheritdoc />
    public byte[] ComputeHash(ReadOnlySpan<byte> data)
    {
        using var hmac = new HMACSHA256(_key);
        return hmac.ComputeHash(data.ToArray());
    }

    /// <inheritdoc />
    public byte[] ComputeHash(Stream data)
    {
        using var hmac = new HMACSHA256(_key);
        return hmac.ComputeHash(data);
    }

    /// <inheritdoc />
    public async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default)
    {
        using var hmac = new HMACSHA256(_key);
        return await hmac.ComputeHashAsync(data, ct);
    }
}

/// <summary>
/// HMAC-SHA384 provider using System.Security.Cryptography.
/// Provides keyed hash authentication using SHA-384.
/// </summary>
public class HmacSha384Provider : IHashProvider
{
    private readonly byte[] _key;

    /// <summary>
    /// Initializes a new HMAC-SHA384 provider with the specified key.
    /// </summary>
    /// <param name="key">The secret key for HMAC computation.</param>
    public HmacSha384Provider(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length == 0)
            throw new ArgumentException("Key cannot be empty.", nameof(key));
        _key = (byte[])key.Clone();
    }

    /// <inheritdoc />
    public string AlgorithmName => "HMAC-SHA384";

    /// <inheritdoc />
    public int HashSizeBytes => 48;

    /// <inheritdoc />
    public byte[] ComputeHash(ReadOnlySpan<byte> data)
    {
        using var hmac = new HMACSHA384(_key);
        return hmac.ComputeHash(data.ToArray());
    }

    /// <inheritdoc />
    public byte[] ComputeHash(Stream data)
    {
        using var hmac = new HMACSHA384(_key);
        return hmac.ComputeHash(data);
    }

    /// <inheritdoc />
    public async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default)
    {
        using var hmac = new HMACSHA384(_key);
        return await hmac.ComputeHashAsync(data, ct);
    }
}

/// <summary>
/// HMAC-SHA512 provider using System.Security.Cryptography.
/// Provides keyed hash authentication using SHA-512.
/// </summary>
public class HmacSha512Provider : IHashProvider
{
    private readonly byte[] _key;

    /// <summary>
    /// Initializes a new HMAC-SHA512 provider with the specified key.
    /// </summary>
    /// <param name="key">The secret key for HMAC computation.</param>
    public HmacSha512Provider(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length == 0)
            throw new ArgumentException("Key cannot be empty.", nameof(key));
        _key = (byte[])key.Clone();
    }

    /// <inheritdoc />
    public string AlgorithmName => "HMAC-SHA512";

    /// <inheritdoc />
    public int HashSizeBytes => 64;

    /// <inheritdoc />
    public byte[] ComputeHash(ReadOnlySpan<byte> data)
    {
        using var hmac = new HMACSHA512(_key);
        return hmac.ComputeHash(data.ToArray());
    }

    /// <inheritdoc />
    public byte[] ComputeHash(Stream data)
    {
        using var hmac = new HMACSHA512(_key);
        return hmac.ComputeHash(data);
    }

    /// <inheritdoc />
    public async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default)
    {
        using var hmac = new HMACSHA512(_key);
        return await hmac.ComputeHashAsync(data, ct);
    }
}

/// <summary>
/// HMAC-SHA3-256 provider using BouncyCastle.
/// Provides keyed hash authentication using SHA3-256.
/// </summary>
public class HmacSha3_256Provider : IHashProvider
{
    private readonly byte[] _key;
    private const int BlockSize = 136; // SHA3-256 block size in bytes

    /// <summary>
    /// Initializes a new HMAC-SHA3-256 provider with the specified key.
    /// </summary>
    /// <param name="key">The secret key for HMAC computation.</param>
    public HmacSha3_256Provider(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length == 0)
            throw new ArgumentException("Key cannot be empty.", nameof(key));
        _key = (byte[])key.Clone();
    }

    /// <inheritdoc />
    public string AlgorithmName => "HMAC-SHA3-256";

    /// <inheritdoc />
    public int HashSizeBytes => 32;

    /// <inheritdoc />
    public byte[] ComputeHash(ReadOnlySpan<byte> data)
    {
        return ComputeHmac(data.ToArray());
    }

    /// <inheritdoc />
    public byte[] ComputeHash(Stream data)
    {
        using var ms = new MemoryStream(4096);
        data.CopyTo(ms);
        return ComputeHmac(ms.ToArray());
    }

    /// <inheritdoc />
    public async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default)
    {
        using var ms = new MemoryStream(4096);
        await data.CopyToAsync(ms, ct);
        return ComputeHmac(ms.ToArray());
    }

    private byte[] ComputeHmac(byte[] message)
    {
        // HMAC = H((K' XOR opad) || H((K' XOR ipad) || message))
        // K' = K if len(K) <= block size, else K' = H(K)
        byte[] keyPrime;
        if (_key.Length > BlockSize)
        {
            var digest = new Sha3Digest(256);
            digest.BlockUpdate(_key, 0, _key.Length);
            keyPrime = new byte[HashSizeBytes];
            digest.DoFinal(keyPrime, 0);
        }
        else
        {
            keyPrime = new byte[BlockSize];
            Array.Copy(_key, keyPrime, _key.Length);
        }

        // Pad key to block size
        var paddedKey = new byte[BlockSize];
        Array.Copy(keyPrime, paddedKey, Math.Min(keyPrime.Length, BlockSize));

        // ipad = 0x36 repeated, opad = 0x5c repeated
        var iKeyPad = new byte[BlockSize];
        var oKeyPad = new byte[BlockSize];
        for (int i = 0; i < BlockSize; i++)
        {
            iKeyPad[i] = (byte)(paddedKey[i] ^ 0x36);
            oKeyPad[i] = (byte)(paddedKey[i] ^ 0x5c);
        }

        // Inner hash: H((K' XOR ipad) || message)
        var innerDigest = new Sha3Digest(256);
        innerDigest.BlockUpdate(iKeyPad, 0, BlockSize);
        innerDigest.BlockUpdate(message, 0, message.Length);
        var innerHash = new byte[HashSizeBytes];
        innerDigest.DoFinal(innerHash, 0);

        // Outer hash: H((K' XOR opad) || innerHash)
        var outerDigest = new Sha3Digest(256);
        outerDigest.BlockUpdate(oKeyPad, 0, BlockSize);
        outerDigest.BlockUpdate(innerHash, 0, innerHash.Length);
        var result = new byte[HashSizeBytes];
        outerDigest.DoFinal(result, 0);

        return result;
    }
}

/// <summary>
/// HMAC-SHA3-384 provider using BouncyCastle.
/// Provides keyed hash authentication using SHA3-384.
/// </summary>
public class HmacSha3_384Provider : IHashProvider
{
    private readonly byte[] _key;
    private const int BlockSize = 104; // SHA3-384 block size (rate) in bytes: (1600 - 2*384) / 8

    /// <summary>
    /// Initializes a new HMAC-SHA3-384 provider with the specified key.
    /// </summary>
    /// <param name="key">The secret key for HMAC computation.</param>
    public HmacSha3_384Provider(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length == 0)
            throw new ArgumentException("Key cannot be empty.", nameof(key));
        _key = (byte[])key.Clone();
    }

    /// <inheritdoc />
    public string AlgorithmName => "HMAC-SHA3-384";

    /// <inheritdoc />
    public int HashSizeBytes => 48;

    /// <inheritdoc />
    public byte[] ComputeHash(ReadOnlySpan<byte> data)
    {
        return ComputeHmac(data.ToArray());
    }

    /// <inheritdoc />
    public byte[] ComputeHash(Stream data)
    {
        using var ms = new MemoryStream(4096);
        data.CopyTo(ms);
        return ComputeHmac(ms.ToArray());
    }

    /// <inheritdoc />
    public async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default)
    {
        using var ms = new MemoryStream(4096);
        await data.CopyToAsync(ms, ct);
        return ComputeHmac(ms.ToArray());
    }

    private byte[] ComputeHmac(byte[] message)
    {
        // HMAC = H((K' XOR opad) || H((K' XOR ipad) || message))
        byte[] keyPrime;
        if (_key.Length > BlockSize)
        {
            var digest = new Sha3Digest(384);
            digest.BlockUpdate(_key, 0, _key.Length);
            keyPrime = new byte[HashSizeBytes];
            digest.DoFinal(keyPrime, 0);
        }
        else
        {
            keyPrime = new byte[BlockSize];
            Array.Copy(_key, keyPrime, _key.Length);
        }

        var paddedKey = new byte[BlockSize];
        Array.Copy(keyPrime, paddedKey, Math.Min(keyPrime.Length, BlockSize));

        var iKeyPad = new byte[BlockSize];
        var oKeyPad = new byte[BlockSize];
        for (int i = 0; i < BlockSize; i++)
        {
            iKeyPad[i] = (byte)(paddedKey[i] ^ 0x36);
            oKeyPad[i] = (byte)(paddedKey[i] ^ 0x5c);
        }

        // Inner hash: H((K' XOR ipad) || message)
        var innerDigest = new Sha3Digest(384);
        innerDigest.BlockUpdate(iKeyPad, 0, BlockSize);
        innerDigest.BlockUpdate(message, 0, message.Length);
        var innerHash = new byte[HashSizeBytes];
        innerDigest.DoFinal(innerHash, 0);

        // Outer hash: H((K' XOR opad) || innerHash)
        var outerDigest = new Sha3Digest(384);
        outerDigest.BlockUpdate(oKeyPad, 0, BlockSize);
        outerDigest.BlockUpdate(innerHash, 0, innerHash.Length);
        var result = new byte[HashSizeBytes];
        outerDigest.DoFinal(result, 0);

        return result;
    }
}

/// <summary>
/// HMAC-SHA3-512 provider using BouncyCastle.
/// Provides keyed hash authentication using SHA3-512.
/// </summary>
public class HmacSha3_512Provider : IHashProvider
{
    private readonly byte[] _key;
    private const int BlockSize = 72; // SHA3-512 block size (rate) in bytes: (1600 - 2*512) / 8

    /// <summary>
    /// Initializes a new HMAC-SHA3-512 provider with the specified key.
    /// </summary>
    /// <param name="key">The secret key for HMAC computation.</param>
    public HmacSha3_512Provider(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length == 0)
            throw new ArgumentException("Key cannot be empty.", nameof(key));
        _key = (byte[])key.Clone();
    }

    /// <inheritdoc />
    public string AlgorithmName => "HMAC-SHA3-512";

    /// <inheritdoc />
    public int HashSizeBytes => 64;

    /// <inheritdoc />
    public byte[] ComputeHash(ReadOnlySpan<byte> data)
    {
        return ComputeHmac(data.ToArray());
    }

    /// <inheritdoc />
    public byte[] ComputeHash(Stream data)
    {
        using var ms = new MemoryStream(4096);
        data.CopyTo(ms);
        return ComputeHmac(ms.ToArray());
    }

    /// <inheritdoc />
    public async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default)
    {
        using var ms = new MemoryStream(4096);
        await data.CopyToAsync(ms, ct);
        return ComputeHmac(ms.ToArray());
    }

    private byte[] ComputeHmac(byte[] message)
    {
        // HMAC = H((K' XOR opad) || H((K' XOR ipad) || message))
        byte[] keyPrime;
        if (_key.Length > BlockSize)
        {
            var digest = new Sha3Digest(512);
            digest.BlockUpdate(_key, 0, _key.Length);
            keyPrime = new byte[HashSizeBytes];
            digest.DoFinal(keyPrime, 0);
        }
        else
        {
            keyPrime = new byte[BlockSize];
            Array.Copy(_key, keyPrime, _key.Length);
        }

        var paddedKey = new byte[BlockSize];
        Array.Copy(keyPrime, paddedKey, Math.Min(keyPrime.Length, BlockSize));

        var iKeyPad = new byte[BlockSize];
        var oKeyPad = new byte[BlockSize];
        for (int i = 0; i < BlockSize; i++)
        {
            iKeyPad[i] = (byte)(paddedKey[i] ^ 0x36);
            oKeyPad[i] = (byte)(paddedKey[i] ^ 0x5c);
        }

        // Inner hash: H((K' XOR ipad) || message)
        var innerDigest = new Sha3Digest(512);
        innerDigest.BlockUpdate(iKeyPad, 0, BlockSize);
        innerDigest.BlockUpdate(message, 0, message.Length);
        var innerHash = new byte[HashSizeBytes];
        innerDigest.DoFinal(innerHash, 0);

        // Outer hash: H((K' XOR opad) || innerHash)
        var outerDigest = new Sha3Digest(512);
        outerDigest.BlockUpdate(oKeyPad, 0, BlockSize);
        outerDigest.BlockUpdate(innerHash, 0, innerHash.Length);
        var result = new byte[HashSizeBytes];
        outerDigest.DoFinal(result, 0);

        return result;
    }
}

#endregion

#region T4.19 - Salted Hash Variants

/// <summary>
/// Salted hash provider that wraps an inner hash provider and prepends salt.
/// Useful for password hashing and additional pre-image resistance.
/// </summary>
public class SaltedHashProvider : IHashProvider
{
    private readonly IHashProvider _inner;
    private readonly byte[] _salt;

    /// <summary>
    /// Initializes a new salted hash provider.
    /// </summary>
    /// <param name="inner">The inner hash provider to use.</param>
    /// <param name="salt">The salt to prepend to data before hashing.</param>
    public SaltedHashProvider(IHashProvider inner, byte[] salt)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(salt);
        if (salt.Length == 0)
            throw new ArgumentException("Salt cannot be empty.", nameof(salt));
        _inner = inner;
        _salt = (byte[])salt.Clone();
    }

    /// <inheritdoc />
    public string AlgorithmName => $"Salted-{_inner.AlgorithmName}";

    /// <inheritdoc />
    public int HashSizeBytes => _inner.HashSizeBytes;

    /// <inheritdoc />
    public byte[] ComputeHash(ReadOnlySpan<byte> data)
    {
        // Prepend salt to data
        var saltedData = new byte[_salt.Length + data.Length];
        _salt.CopyTo(saltedData, 0);
        data.CopyTo(saltedData.AsSpan(_salt.Length));
        return _inner.ComputeHash(saltedData);
    }

    /// <inheritdoc />
    public byte[] ComputeHash(Stream data)
    {
        // Create a concatenated stream with salt prefix
        using var saltedStream = new SaltedStream(_salt, data);
        return _inner.ComputeHash(saltedStream);
    }

    /// <inheritdoc />
    public async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default)
    {
        using var saltedStream = new SaltedStream(_salt, data);
        return await _inner.ComputeHashAsync(saltedStream, ct);
    }

    /// <summary>
    /// Internal stream that prepends salt to another stream.
    /// </summary>
    private sealed class SaltedStream : Stream
    {
        private readonly byte[] _salt;
        private readonly Stream _inner;
        private int _saltPosition;

        public SaltedStream(byte[] salt, Stream inner)
        {
            _salt = salt;
            _inner = inner;
            _saltPosition = 0;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _salt.Length + _inner.Length;
        public override long Position
        {
            get => _saltPosition < _salt.Length ? _saltPosition : _salt.Length + _inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int totalRead = 0;

            // Read from salt first
            if (_saltPosition < _salt.Length)
            {
                int saltBytesToRead = Math.Min(count, _salt.Length - _saltPosition);
                Array.Copy(_salt, _saltPosition, buffer, offset, saltBytesToRead);
                _saltPosition += saltBytesToRead;
                totalRead += saltBytesToRead;
                offset += saltBytesToRead;
                count -= saltBytesToRead;
            }

            // Then read from inner stream
            if (count > 0)
            {
                totalRead += _inner.Read(buffer, offset, count);
            }

            return totalRead;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int totalRead = 0;

            // Read from salt first
            if (_saltPosition < _salt.Length)
            {
                int saltBytesToRead = Math.Min(count, _salt.Length - _saltPosition);
                Array.Copy(_salt, _saltPosition, buffer, offset, saltBytesToRead);
                _saltPosition += saltBytesToRead;
                totalRead += saltBytesToRead;
                offset += saltBytesToRead;
                count -= saltBytesToRead;
            }

            // Then read from inner stream
            if (count > 0)
            {
                totalRead += await _inner.ReadAsync(buffer, offset, count, ct);
            }

            return totalRead;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

#endregion

#region T4.20 - Hash Provider Factory

/// <summary>
/// Factory for creating hash providers by algorithm name.
/// Supports all built-in algorithms and their HMAC/salted variants.
/// </summary>
public static class HashProviderFactory
{
    /// <summary>
    /// List of supported base algorithm names.
    /// </summary>
    public static IReadOnlyList<string> SupportedAlgorithms { get; } = new[]
    {
        "SHA256",
        "SHA384",
        "SHA512",
        "SHA3-256",
        "SHA3-384",
        "SHA3-512",
        "Keccak-256",
        "Keccak-384",
        "Keccak-512",
        "HMAC-SHA256",
        "HMAC-SHA384",
        "HMAC-SHA512",
        "HMAC-SHA3-256",
        "HMAC-SHA3-384",
        "HMAC-SHA3-512"
    };

    /// <summary>
    /// Creates a hash provider for the specified algorithm.
    /// </summary>
    /// <param name="algorithm">The algorithm name (e.g., "SHA256", "SHA3-256", "Keccak-256").</param>
    /// <returns>The hash provider instance.</returns>
    /// <exception cref="ArgumentException">Thrown if the algorithm is not supported.</exception>
    public static IHashProvider Create(string algorithm)
    {
        ArgumentNullException.ThrowIfNull(algorithm);

        return algorithm.ToUpperInvariant() switch
        {
            "SHA256" or "SHA-256" => new Sha256Provider(),
            "SHA384" or "SHA-384" => new Sha384Provider(),
            "SHA512" or "SHA-512" => new Sha512Provider(),
            "SHA3-256" or "SHA3256" => new Sha3_256Provider(),
            "SHA3-384" or "SHA3384" => new Sha3_384Provider(),
            "SHA3-512" or "SHA3512" => new Sha3_512Provider(),
            "KECCAK-256" or "KECCAK256" => new Keccak256Provider(),
            "KECCAK-384" or "KECCAK384" => new Keccak384Provider(),
            "KECCAK-512" or "KECCAK512" => new Keccak512Provider(),
            _ => throw new ArgumentException($"Unsupported hash algorithm: {algorithm}", nameof(algorithm))
        };
    }

    /// <summary>
    /// Creates an HMAC provider for the specified algorithm with the given key.
    /// </summary>
    /// <param name="algorithm">The base algorithm name (e.g., "SHA256", "SHA3-256").</param>
    /// <param name="key">The secret key for HMAC computation.</param>
    /// <returns>The HMAC hash provider instance.</returns>
    /// <exception cref="ArgumentException">Thrown if the algorithm is not supported for HMAC.</exception>
    public static IHashProvider CreateHmac(string algorithm, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(algorithm);
        ArgumentNullException.ThrowIfNull(key);

        return algorithm.ToUpperInvariant() switch
        {
            "SHA256" or "SHA-256" or "HMAC-SHA256" => new HmacSha256Provider(key),
            "SHA384" or "SHA-384" or "HMAC-SHA384" => new HmacSha384Provider(key),
            "SHA512" or "SHA-512" or "HMAC-SHA512" => new HmacSha512Provider(key),
            "SHA3-256" or "SHA3256" or "HMAC-SHA3-256" => new HmacSha3_256Provider(key),
            "SHA3-384" or "SHA3384" or "HMAC-SHA3-384" => new HmacSha3_384Provider(key),
            "SHA3-512" or "SHA3512" or "HMAC-SHA3-512" => new HmacSha3_512Provider(key),
            _ => throw new ArgumentException($"Unsupported HMAC algorithm: {algorithm}", nameof(algorithm))
        };
    }

    /// <summary>
    /// Creates a salted hash provider for the specified algorithm with the given salt.
    /// </summary>
    /// <param name="algorithm">The base algorithm name.</param>
    /// <param name="salt">The salt to prepend to data before hashing.</param>
    /// <returns>The salted hash provider instance.</returns>
    public static IHashProvider CreateSalted(string algorithm, byte[] salt)
    {
        ArgumentNullException.ThrowIfNull(algorithm);
        ArgumentNullException.ThrowIfNull(salt);

        var inner = Create(algorithm);
        return new SaltedHashProvider(inner, salt);
    }

    /// <summary>
    /// Determines if the specified algorithm is supported.
    /// </summary>
    /// <param name="algorithm">The algorithm name to check.</param>
    /// <returns>True if the algorithm is supported; otherwise, false.</returns>
    public static bool IsSupported(string algorithm)
    {
        if (string.IsNullOrEmpty(algorithm))
            return false;

        var upper = algorithm.ToUpperInvariant();
        return upper switch
        {
            "SHA256" or "SHA-256" => true,
            "SHA384" or "SHA-384" => true,
            "SHA512" or "SHA-512" => true,
            "SHA3-256" or "SHA3256" => true,
            "SHA3-384" or "SHA3384" => true,
            "SHA3-512" or "SHA3512" => true,
            "KECCAK-256" or "KECCAK256" => true,
            "KECCAK-384" or "KECCAK384" => true,
            "KECCAK-512" or "KECCAK512" => true,
            "HMAC-SHA256" => true,
            "HMAC-SHA384" => true,
            "HMAC-SHA512" => true,
            "HMAC-SHA3-256" => true,
            "HMAC-SHA3-384" => true,
            "HMAC-SHA3-512" => true,
            _ => false
        };
    }
}

#endregion

#region Native SHA-2 Providers (for factory completeness)

/// <summary>
/// SHA-256 hash provider using System.Security.Cryptography.
/// </summary>
public class Sha256Provider : IHashProvider
{
    /// <inheritdoc />
    public string AlgorithmName => "SHA-256";

    /// <inheritdoc />
    public int HashSizeBytes => 32;

    /// <inheritdoc />
    public byte[] ComputeHash(ReadOnlySpan<byte> data)
    {
        return SHA256.HashData(data);
    }

    /// <inheritdoc />
    public byte[] ComputeHash(Stream data)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(data);
    }

    /// <inheritdoc />
    public async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default)
    {
        return await SHA256.HashDataAsync(data, ct);
    }
}

/// <summary>
/// SHA-384 hash provider using System.Security.Cryptography.
/// </summary>
public class Sha384Provider : IHashProvider
{
    /// <inheritdoc />
    public string AlgorithmName => "SHA-384";

    /// <inheritdoc />
    public int HashSizeBytes => 48;

    /// <inheritdoc />
    public byte[] ComputeHash(ReadOnlySpan<byte> data)
    {
        return SHA384.HashData(data);
    }

    /// <inheritdoc />
    public byte[] ComputeHash(Stream data)
    {
        using var sha = SHA384.Create();
        return sha.ComputeHash(data);
    }

    /// <inheritdoc />
    public async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default)
    {
        return await SHA384.HashDataAsync(data, ct);
    }
}

/// <summary>
/// SHA-512 hash provider using System.Security.Cryptography.
/// </summary>
public class Sha512Provider : IHashProvider
{
    /// <inheritdoc />
    public string AlgorithmName => "SHA-512";

    /// <inheritdoc />
    public int HashSizeBytes => 64;

    /// <inheritdoc />
    public byte[] ComputeHash(ReadOnlySpan<byte> data)
    {
        return SHA512.HashData(data);
    }

    /// <inheritdoc />
    public byte[] ComputeHash(Stream data)
    {
        using var sha = SHA512.Create();
        return sha.ComputeHash(data);
    }

    /// <inheritdoc />
    public async Task<byte[]> ComputeHashAsync(Stream data, CancellationToken ct = default)
    {
        return await SHA512.HashDataAsync(data, ct);
    }
}

#endregion
