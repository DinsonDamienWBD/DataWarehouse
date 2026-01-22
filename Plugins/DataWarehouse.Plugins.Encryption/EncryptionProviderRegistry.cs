using DataWarehouse.Plugins.Encryption.Providers;

namespace DataWarehouse.Plugins.Encryption;

/// <summary>
/// Registry for encryption providers. Allows the Kernel to discover all available algorithms.
/// </summary>
public static class EncryptionProviderRegistry
{
    private static readonly Dictionary<EncryptionAlgorithm, Func<IEncryptionProvider>> _providerFactories = new()
    {
        { EncryptionAlgorithm.Aes256Gcm, () => new Aes256GcmProvider() },
        { EncryptionAlgorithm.Aes256CbcHmac, () => new Aes256CbcHmacProvider() },
        { EncryptionAlgorithm.ChaCha20Poly1305, () => new ChaCha20Poly1305Provider() },
        { EncryptionAlgorithm.XChaCha20Poly1305, () => new XChaCha20Poly1305Provider() },
        { EncryptionAlgorithm.Twofish, () => new TwofishProvider() },
        { EncryptionAlgorithm.Serpent, () => new SerpentProvider() }
    };

    private static readonly Dictionary<string, EncryptionAlgorithm> _algorithmNameMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "AES-256-GCM", EncryptionAlgorithm.Aes256Gcm },
        { "AES256GCM", EncryptionAlgorithm.Aes256Gcm },
        { "AES-256-CBC-HMAC-SHA256", EncryptionAlgorithm.Aes256CbcHmac },
        { "AES256CBCHMAC", EncryptionAlgorithm.Aes256CbcHmac },
        { "ChaCha20-Poly1305", EncryptionAlgorithm.ChaCha20Poly1305 },
        { "ChaCha20Poly1305", EncryptionAlgorithm.ChaCha20Poly1305 },
        { "XChaCha20-Poly1305", EncryptionAlgorithm.XChaCha20Poly1305 },
        { "XChaCha20Poly1305", EncryptionAlgorithm.XChaCha20Poly1305 },
        { "Twofish-256-CTR-HMAC", EncryptionAlgorithm.Twofish },
        { "Twofish", EncryptionAlgorithm.Twofish },
        { "Serpent-256-CTR-HMAC", EncryptionAlgorithm.Serpent },
        { "Serpent", EncryptionAlgorithm.Serpent }
    };

    /// <summary>
    /// Gets all registered encryption algorithms.
    /// </summary>
    public static IReadOnlyCollection<EncryptionAlgorithm> AvailableAlgorithms => _providerFactories.Keys;

    /// <summary>
    /// Creates an encryption provider for the specified algorithm.
    /// </summary>
    public static IEncryptionProvider CreateProvider(EncryptionAlgorithm algorithm)
    {
        if (!_providerFactories.TryGetValue(algorithm, out var factory))
            throw new ArgumentException($"Unknown encryption algorithm: {algorithm}", nameof(algorithm));

        return factory();
    }

    /// <summary>
    /// Creates an encryption provider by algorithm name.
    /// </summary>
    public static IEncryptionProvider CreateProvider(string algorithmName)
    {
        if (!_algorithmNameMap.TryGetValue(algorithmName, out var algorithm))
            throw new ArgumentException($"Unknown encryption algorithm: {algorithmName}", nameof(algorithmName));

        return CreateProvider(algorithm);
    }

    /// <summary>
    /// Tries to create an encryption provider for the specified algorithm.
    /// </summary>
    public static bool TryCreateProvider(EncryptionAlgorithm algorithm, out IEncryptionProvider? provider)
    {
        if (_providerFactories.TryGetValue(algorithm, out var factory))
        {
            provider = factory();
            return true;
        }
        provider = null;
        return false;
    }

    /// <summary>
    /// Gets information about an encryption algorithm.
    /// </summary>
    public static EncryptionInfo GetAlgorithmInfo(EncryptionAlgorithm algorithm)
    {
        var provider = CreateProvider(algorithm);
        return new EncryptionInfo
        {
            Algorithm = algorithm,
            KeySizeBits = provider.KeySizeBits,
            HardwareAccelerationAvailable = algorithm == EncryptionAlgorithm.Aes256Gcm &&
                                            System.Runtime.Intrinsics.X86.Aes.IsSupported,
            HardwareAccelerationEnabled = algorithm == EncryptionAlgorithm.Aes256Gcm
        };
    }

    /// <summary>
    /// Gets all algorithm information.
    /// </summary>
    public static IEnumerable<EncryptionInfo> GetAllAlgorithmInfo()
    {
        return AvailableAlgorithms.Select(GetAlgorithmInfo);
    }

    /// <summary>
    /// Creates a KDF instance for the specified function.
    /// </summary>
    public static IKeyDerivationFunction CreateKdf(KeyDerivationFunction kdf = KeyDerivationFunction.Argon2id)
    {
        return kdf switch
        {
            KeyDerivationFunction.Argon2id => new Argon2KeyDerivation(),
            KeyDerivationFunction.Pbkdf2Sha256 => new Argon2KeyDerivation(), // Falls back to PBKDF2 internally
            KeyDerivationFunction.Pbkdf2Sha512 => new Argon2KeyDerivation(),
            KeyDerivationFunction.Scrypt => new Argon2KeyDerivation(),
            _ => throw new ArgumentException($"Unknown KDF: {kdf}", nameof(kdf))
        };
    }

    /// <summary>
    /// Creates a password hasher instance.
    /// </summary>
    public static IPasswordHasher CreatePasswordHasher() => new Argon2PasswordHasher();

    /// <summary>
    /// Creates a zero-knowledge encryption instance.
    /// </summary>
    public static ZeroKnowledgeEncryption CreateZeroKnowledgeEncryption(
        IKeyDerivationFunction? kdf = null,
        ZeroKnowledgeConfig? config = null)
    {
        return new ZeroKnowledgeEncryption(kdf, config);
    }
}
