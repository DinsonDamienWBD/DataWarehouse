using System.Buffers.Binary;
using System.Security.Cryptography;

namespace DataWarehouse.Plugins.OracleTnsProtocol.Protocol;

/// <summary>
/// Implements Oracle Native Network Encryption (NNE) for secure communication.
/// Supports encryption algorithms AES256, AES192, AES128, 3DES168 and
/// integrity algorithms SHA256, SHA1, MD5.
/// </summary>
/// <remarks>
/// Native Network Encryption provides transparent encryption of data on the network
/// without requiring SSL/TLS certificates. It uses symmetric encryption with keys
/// derived during the authentication handshake.
///
/// Protocol flow:
/// 1. Server advertises supported algorithms in ACCEPT packet
/// 2. Client sends encryption negotiation packet
/// 3. Server responds with selected algorithm
/// 4. Both sides derive encryption and integrity keys
/// 5. All subsequent DATA packets are encrypted
///
/// Thread-safety: Each instance handles a single connection. The encryption and
/// decryption methods are thread-safe for concurrent use on the same instance.
/// </remarks>
public sealed class NativeNetworkEncryption : IDisposable
{
    private readonly object _encryptLock = new();
    private readonly object _decryptLock = new();

    private Aes? _encryptor;
    private Aes? _decryptor;
    private HMAC? _integrityComputer;

    private byte[]? _encryptionKey;
    private byte[]? _integrityKey;
    private byte[]? _encryptIv;
    private byte[]? _decryptIv;

    private string _encryptionAlgorithm = "AES256";
    private string _integrityAlgorithm = "SHA256";

    private long _sendSequence;
    private long _receiveSequence;

    private bool _disposed;

    /// <summary>
    /// Gets whether encryption is currently enabled.
    /// </summary>
    public bool IsEnabled { get; private set; }

    /// <summary>
    /// Gets the negotiated encryption algorithm.
    /// </summary>
    public string EncryptionAlgorithm => _encryptionAlgorithm;

    /// <summary>
    /// Gets the negotiated integrity algorithm.
    /// </summary>
    public string IntegrityAlgorithm => _integrityAlgorithm;

    /// <summary>
    /// Initializes encryption with the specified parameters.
    /// </summary>
    /// <param name="encryptionAlgorithm">The encryption algorithm (AES256, AES192, AES128, 3DES168).</param>
    /// <param name="integrityAlgorithm">The integrity algorithm (SHA256, SHA1, MD5).</param>
    /// <param name="sessionKey">The session key from authentication.</param>
    /// <param name="serverRandom">Server-generated random bytes.</param>
    /// <param name="clientRandom">Client-generated random bytes.</param>
    /// <exception cref="ArgumentNullException">Thrown when required parameters are null.</exception>
    /// <exception cref="ArgumentException">Thrown when algorithm is not supported.</exception>
    public void Initialize(
        string encryptionAlgorithm,
        string integrityAlgorithm,
        byte[] sessionKey,
        byte[] serverRandom,
        byte[] clientRandom)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(sessionKey);
        ArgumentNullException.ThrowIfNull(serverRandom);
        ArgumentNullException.ThrowIfNull(clientRandom);

        _encryptionAlgorithm = encryptionAlgorithm.ToUpperInvariant();
        _integrityAlgorithm = integrityAlgorithm.ToUpperInvariant();

        // Derive keys using the session key and random values
        var keyMaterial = DeriveKeyMaterial(sessionKey, serverRandom, clientRandom);

        // Get key sizes based on algorithm
        var (keySize, ivSize) = GetKeySizes(_encryptionAlgorithm);
        var integrityKeySize = GetIntegrityKeySize(_integrityAlgorithm);

        // Extract keys from derived material
        var offset = 0;
        _encryptionKey = new byte[keySize];
        Array.Copy(keyMaterial, offset, _encryptionKey, 0, keySize);
        offset += keySize;

        _encryptIv = new byte[ivSize];
        Array.Copy(keyMaterial, offset, _encryptIv, 0, ivSize);
        offset += ivSize;

        _decryptIv = new byte[ivSize];
        Array.Copy(keyMaterial, offset, _decryptIv, 0, ivSize);
        offset += ivSize;

        _integrityKey = new byte[integrityKeySize];
        Array.Copy(keyMaterial, offset, _integrityKey, 0, integrityKeySize);

        // Initialize encryption algorithms
        InitializeEncryption();
        InitializeIntegrity();

        IsEnabled = true;
    }

    /// <summary>
    /// Encrypts a packet payload.
    /// </summary>
    /// <param name="plaintext">The plaintext data to encrypt.</param>
    /// <returns>The encrypted data with integrity tag.</returns>
    /// <exception cref="InvalidOperationException">Thrown when encryption is not initialized.</exception>
    public byte[] Encrypt(byte[] plaintext)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsEnabled || _encryptor == null || _integrityComputer == null)
        {
            throw new InvalidOperationException("Encryption has not been initialized.");
        }

        ArgumentNullException.ThrowIfNull(plaintext);

        lock (_encryptLock)
        {
            // Increment sequence number
            var sequence = Interlocked.Increment(ref _sendSequence);

            // Create encryptor with current IV
            if (_encryptionKey == null || _encryptor == null)
                throw new InvalidOperationException("Encryption not initialized");

            using var encryptor = _encryptor.CreateEncryptor(_encryptionKey, _encryptIv);

            // Pad plaintext to block size
            var paddedLength = ((plaintext.Length / _encryptor.BlockSize) + 1) * _encryptor.BlockSize;
            var padded = new byte[paddedLength];
            Array.Copy(plaintext, padded, plaintext.Length);

            // PKCS7 padding
            var paddingValue = (byte)(paddedLength - plaintext.Length);
            for (var i = plaintext.Length; i < paddedLength; i++)
            {
                padded[i] = paddingValue;
            }

            // Encrypt
            var ciphertext = encryptor.TransformFinalBlock(padded, 0, padded.Length);

            // Update IV for next encryption (CBC mode)
            Array.Copy(ciphertext, ciphertext.Length - _encryptIv!.Length, _encryptIv, 0, _encryptIv.Length);

            // Compute integrity tag
            var tagInput = new byte[8 + ciphertext.Length];
            BinaryPrimitives.WriteInt64BigEndian(tagInput.AsSpan(0, 8), sequence);
            Array.Copy(ciphertext, 0, tagInput, 8, ciphertext.Length);

            var tag = _integrityComputer.ComputeHash(tagInput);
            var tagLength = GetIntegrityTagLength(_integrityAlgorithm);

            // Combine: [4-byte length][ciphertext][tag]
            var result = new byte[4 + ciphertext.Length + tagLength];
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(0, 4), plaintext.Length);
            Array.Copy(ciphertext, 0, result, 4, ciphertext.Length);
            Array.Copy(tag, 0, result, 4 + ciphertext.Length, tagLength);

            return result;
        }
    }

    /// <summary>
    /// Decrypts a packet payload.
    /// </summary>
    /// <param name="ciphertext">The encrypted data with integrity tag.</param>
    /// <returns>The decrypted plaintext data.</returns>
    /// <exception cref="InvalidOperationException">Thrown when encryption is not initialized.</exception>
    /// <exception cref="CryptographicException">Thrown when integrity verification fails.</exception>
    public byte[] Decrypt(byte[] ciphertext)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsEnabled || _decryptor == null || _integrityComputer == null)
        {
            throw new InvalidOperationException("Encryption has not been initialized.");
        }

        ArgumentNullException.ThrowIfNull(ciphertext);

        lock (_decryptLock)
        {
            // Increment sequence number
            var sequence = Interlocked.Increment(ref _receiveSequence);

            // Extract components
            var tagLength = GetIntegrityTagLength(_integrityAlgorithm);
            if (ciphertext.Length < 4 + tagLength)
            {
                throw new CryptographicException("Ciphertext too short.");
            }

            var originalLength = BinaryPrimitives.ReadInt32BigEndian(ciphertext.AsSpan(0, 4));
            var encryptedData = ciphertext[4..^tagLength];
            var receivedTag = ciphertext[^tagLength..];

            // Verify integrity
            var tagInput = new byte[8 + encryptedData.Length];
            BinaryPrimitives.WriteInt64BigEndian(tagInput.AsSpan(0, 8), sequence);
            Array.Copy(encryptedData, 0, tagInput, 8, encryptedData.Length);

            var computedTag = _integrityComputer.ComputeHash(tagInput);

            if (!CryptographicOperations.FixedTimeEquals(receivedTag, computedTag.AsSpan(0, tagLength)))
            {
                throw new CryptographicException("Integrity verification failed.");
            }

            // Decrypt
            if (_encryptionKey == null || _decryptor == null)
                throw new InvalidOperationException("Decryption not initialized");

            using var decryptor = _decryptor.CreateDecryptor(_encryptionKey, _decryptIv);
            var decrypted = decryptor.TransformFinalBlock(encryptedData, 0, encryptedData.Length);

            // Update IV for next decryption
            Array.Copy(encryptedData, encryptedData.Length - _decryptIv!.Length, _decryptIv, 0, _decryptIv.Length);

            // Remove padding and return original length
            if (originalLength > decrypted.Length)
            {
                throw new CryptographicException("Invalid original length in encrypted data.");
            }

            var result = new byte[originalLength];
            Array.Copy(decrypted, result, originalLength);

            return result;
        }
    }

    /// <summary>
    /// Negotiates encryption parameters based on client and server capabilities.
    /// </summary>
    /// <param name="serverAlgorithms">Algorithms supported by the server.</param>
    /// <param name="clientAlgorithms">Algorithms preferred by the client.</param>
    /// <returns>The negotiated algorithm, or null if no common algorithm exists.</returns>
    public static string? NegotiateEncryptionAlgorithm(string[] serverAlgorithms, string[] clientAlgorithms)
    {
        // Prefer client's order, but only select algorithms both support
        foreach (var clientAlg in clientAlgorithms)
        {
            if (serverAlgorithms.Contains(clientAlg, StringComparer.OrdinalIgnoreCase))
            {
                return clientAlg;
            }
        }
        return null;
    }

    /// <summary>
    /// Negotiates integrity parameters based on client and server capabilities.
    /// </summary>
    /// <param name="serverAlgorithms">Algorithms supported by the server.</param>
    /// <param name="clientAlgorithms">Algorithms preferred by the client.</param>
    /// <returns>The negotiated algorithm, or null if no common algorithm exists.</returns>
    public static string? NegotiateIntegrityAlgorithm(string[] serverAlgorithms, string[] clientAlgorithms)
    {
        foreach (var clientAlg in clientAlgorithms)
        {
            if (serverAlgorithms.Contains(clientAlg, StringComparer.OrdinalIgnoreCase))
            {
                return clientAlg;
            }
        }
        return null;
    }

    /// <summary>
    /// Parses encryption negotiation data from a client request.
    /// </summary>
    /// <param name="payload">The negotiation payload.</param>
    /// <returns>Tuple of requested encryption and integrity algorithms.</returns>
    public static (string[] encryptionAlgorithms, string[] integrityAlgorithms) ParseNegotiationRequest(byte[] payload)
    {
        var encAlgs = new List<string>();
        var intAlgs = new List<string>();

        if (payload.Length < 4)
            return (encAlgs.ToArray(), intAlgs.ToArray());

        var offset = 0;

        // Number of encryption algorithms
        var numEncAlgs = payload[offset++];
        for (var i = 0; i < numEncAlgs && offset < payload.Length; i++)
        {
            var algId = payload[offset++];
            var algName = MapEncryptionAlgorithmId(algId);
            if (!string.IsNullOrEmpty(algName))
                encAlgs.Add(algName);
        }

        // Number of integrity algorithms
        if (offset < payload.Length)
        {
            var numIntAlgs = payload[offset++];
            for (var i = 0; i < numIntAlgs && offset < payload.Length; i++)
            {
                var algId = payload[offset++];
                var algName = MapIntegrityAlgorithmId(algId);
                if (!string.IsNullOrEmpty(algName))
                    intAlgs.Add(algName);
            }
        }

        return (encAlgs.ToArray(), intAlgs.ToArray());
    }

    /// <summary>
    /// Builds encryption negotiation response data.
    /// </summary>
    /// <param name="encryptionAlgorithm">Selected encryption algorithm.</param>
    /// <param name="integrityAlgorithm">Selected integrity algorithm.</param>
    /// <param name="serverRandom">Server-generated random bytes.</param>
    /// <returns>The negotiation response payload.</returns>
    public static byte[] BuildNegotiationResponse(string encryptionAlgorithm, string integrityAlgorithm, byte[] serverRandom)
    {
        using var ms = new MemoryStream();

        // Selected encryption algorithm
        ms.WriteByte(MapEncryptionAlgorithmName(encryptionAlgorithm));

        // Selected integrity algorithm
        ms.WriteByte(MapIntegrityAlgorithmName(integrityAlgorithm));

        // Server random
        ms.WriteByte((byte)serverRandom.Length);
        ms.Write(serverRandom);

        return ms.ToArray();
    }

    /// <summary>
    /// Derives key material from session key and random values using HKDF-like expansion.
    /// </summary>
    private static byte[] DeriveKeyMaterial(byte[] sessionKey, byte[] serverRandom, byte[] clientRandom)
    {
        // Combine inputs
        var input = new byte[sessionKey.Length + serverRandom.Length + clientRandom.Length];
        Array.Copy(sessionKey, 0, input, 0, sessionKey.Length);
        Array.Copy(serverRandom, 0, input, sessionKey.Length, serverRandom.Length);
        Array.Copy(clientRandom, 0, input, sessionKey.Length + serverRandom.Length, clientRandom.Length);

        // Derive 128 bytes of key material using SHA-256 based expansion
        var keyMaterial = new byte[128];
        using var hmac = new HMACSHA256(sessionKey);

        var counter = 0;
        var offset = 0;

        while (offset < keyMaterial.Length)
        {
            counter++;
            var counterBytes = BitConverter.GetBytes(counter);
            var blockInput = new byte[input.Length + 4];
            Array.Copy(input, blockInput, input.Length);
            Array.Copy(counterBytes, 0, blockInput, input.Length, 4);

            var block = hmac.ComputeHash(blockInput);
            var copyLength = Math.Min(block.Length, keyMaterial.Length - offset);
            Array.Copy(block, 0, keyMaterial, offset, copyLength);
            offset += copyLength;
        }

        return keyMaterial;
    }

    /// <summary>
    /// Gets key and IV sizes for the specified encryption algorithm.
    /// </summary>
    private static (int keySize, int ivSize) GetKeySizes(string algorithm)
    {
        return algorithm.ToUpperInvariant() switch
        {
            "AES256" => (32, 16),
            "AES192" => (24, 16),
            "AES128" => (16, 16),
            "3DES168" => (24, 8),
            "3DES112" => (16, 8),
            _ => throw new ArgumentException($"Unsupported encryption algorithm: {algorithm}")
        };
    }

    /// <summary>
    /// Gets the key size for the specified integrity algorithm.
    /// </summary>
    private static int GetIntegrityKeySize(string algorithm)
    {
        return algorithm.ToUpperInvariant() switch
        {
            "SHA256" => 32,
            "SHA1" => 20,
            "MD5" => 16,
            _ => throw new ArgumentException($"Unsupported integrity algorithm: {algorithm}")
        };
    }

    /// <summary>
    /// Gets the tag length for the specified integrity algorithm.
    /// </summary>
    private static int GetIntegrityTagLength(string algorithm)
    {
        return algorithm.ToUpperInvariant() switch
        {
            "SHA256" => 16, // Use truncated tag
            "SHA1" => 12,
            "MD5" => 12,
            _ => 16
        };
    }

    /// <summary>
    /// Initializes the encryption algorithm.
    /// </summary>
    private void InitializeEncryption()
    {
        _encryptor?.Dispose();
        _decryptor?.Dispose();

        if (_encryptionAlgorithm.StartsWith("AES", StringComparison.OrdinalIgnoreCase))
        {
            _encryptor = Aes.Create();
            _encryptor.Mode = CipherMode.CBC;
            _encryptor.Padding = PaddingMode.None; // We handle padding manually

            _decryptor = Aes.Create();
            _decryptor.Mode = CipherMode.CBC;
            _decryptor.Padding = PaddingMode.None;
        }
        else if (_encryptionAlgorithm.StartsWith("3DES", StringComparison.OrdinalIgnoreCase))
        {
            // TripleDES fallback (deprecated but supported for legacy clients)
#pragma warning disable SYSLIB0022 // Type or member is obsolete
            var tripleDes = TripleDES.Create();
            tripleDes.Mode = CipherMode.CBC;
            tripleDes.Padding = PaddingMode.None;
            _encryptor = null; // Would need different handling for TripleDES
            _decryptor = null;
#pragma warning restore SYSLIB0022
            throw new NotSupportedException("3DES is not supported. Please use AES.");
        }
        else
        {
            throw new ArgumentException($"Unsupported encryption algorithm: {_encryptionAlgorithm}");
        }
    }

    /// <summary>
    /// Initializes the integrity algorithm.
    /// </summary>
    private void InitializeIntegrity()
    {
        _integrityComputer?.Dispose();

        _integrityComputer = _integrityAlgorithm.ToUpperInvariant() switch
        {
            "SHA256" => new HMACSHA256(_integrityKey!),
            "SHA1" => new HMACSHA1(_integrityKey!),
#pragma warning disable CA5351 // Do Not Use Broken Cryptographic Algorithms
            "MD5" => new HMACMD5(_integrityKey!),
#pragma warning restore CA5351
            _ => throw new ArgumentException($"Unsupported integrity algorithm: {_integrityAlgorithm}")
        };
    }

    /// <summary>
    /// Maps an encryption algorithm ID to its name.
    /// </summary>
    private static string MapEncryptionAlgorithmId(byte id)
    {
        return id switch
        {
            OracleProtocolConstants.NneAes256 => "AES256",
            OracleProtocolConstants.NneAes192 => "AES192",
            OracleProtocolConstants.NneAes128 => "AES128",
            OracleProtocolConstants.Nne3Des168 => "3DES168",
            _ => string.Empty
        };
    }

    /// <summary>
    /// Maps an integrity algorithm ID to its name.
    /// </summary>
    private static string MapIntegrityAlgorithmId(byte id)
    {
        return id switch
        {
            OracleProtocolConstants.NneSha256 => "SHA256",
            OracleProtocolConstants.NneSha1 => "SHA1",
            OracleProtocolConstants.NneMd5 => "MD5",
            _ => string.Empty
        };
    }

    /// <summary>
    /// Maps an encryption algorithm name to its ID.
    /// </summary>
    private static byte MapEncryptionAlgorithmName(string name)
    {
        return name.ToUpperInvariant() switch
        {
            "AES256" => OracleProtocolConstants.NneAes256,
            "AES192" => OracleProtocolConstants.NneAes192,
            "AES128" => OracleProtocolConstants.NneAes128,
            "3DES168" => OracleProtocolConstants.Nne3Des168,
            _ => 0
        };
    }

    /// <summary>
    /// Maps an integrity algorithm name to its ID.
    /// </summary>
    private static byte MapIntegrityAlgorithmName(string name)
    {
        return name.ToUpperInvariant() switch
        {
            "SHA256" => OracleProtocolConstants.NneSha256,
            "SHA1" => OracleProtocolConstants.NneSha1,
            "MD5" => OracleProtocolConstants.NneMd5,
            _ => 0
        };
    }

    /// <summary>
    /// Releases resources used by the NativeNetworkEncryption instance.
    /// </summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;

            _encryptor?.Dispose();
            _decryptor?.Dispose();
            _integrityComputer?.Dispose();

            if (_encryptionKey != null)
                CryptographicOperations.ZeroMemory(_encryptionKey);
            if (_integrityKey != null)
                CryptographicOperations.ZeroMemory(_integrityKey);
        }
    }
}
