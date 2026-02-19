using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DataWarehouse.SDK.Contracts.Encryption
{
    /// <summary>
    /// Extended algorithm profile for post-quantum cryptographic algorithms.
    /// Adds implementation-specific details for BouncyCastle and .NET runtime bindings.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 59: Cryptographic commitment schemes")]
    public record PqcAlgorithmInfo
    {
        /// <summary>The base algorithm profile with standard properties.</summary>
        public required AlgorithmProfile Profile { get; init; }

        /// <summary>BouncyCastle algorithm type name for interop (e.g., "MLKemParameters.ML_KEM_768").</summary>
        public string? BouncyCastleType { get; init; }

        /// <summary>.NET runtime type name if natively supported (e.g., "System.Security.Cryptography.MLKem").</summary>
        public string? DotNetType { get; init; }

        /// <summary>Parameter set name as defined in the FIPS standard (e.g., "ML-KEM-768").</summary>
        public required string ParameterSetName { get; init; }
    }

    /// <summary>
    /// Static registry of all supported post-quantum cryptographic algorithms.
    /// Pre-populated with NIST FIPS 203 (ML-KEM), FIPS 204 (ML-DSA), and FIPS 205 (SLH-DSA)
    /// algorithms with accurate key sizes, signature sizes, and security levels.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This registry serves as the authoritative catalog of PQC algorithms in the DataWarehouse system.
    /// It provides:
    /// </para>
    /// <list type="bullet">
    ///   <item>Algorithm lookup by ID with full metadata</item>
    ///   <item>Recommended algorithm selection by NIST security level</item>
    ///   <item>Migration path recommendations from classical to PQC algorithms</item>
    ///   <item>Deprecation status tracking</item>
    /// </list>
    /// <para>
    /// Key sizes are specified in bits and match the official NIST specifications:
    /// ML-KEM-768 public key = 1184 bytes (9472 bits), ML-KEM-1024 public key = 1568 bytes (12544 bits),
    /// ML-DSA-65 signature = 3309 bytes (26472 bits), SLH-DSA-SHAKE-128f signature = 17088 bytes (136704 bits).
    /// </para>
    /// </remarks>
    [SdkCompatibility("5.0.0", Notes = "Phase 59: Cryptographic commitment schemes")]
    public static class PqcAlgorithmRegistry
    {
        private static readonly Dictionary<string, PqcAlgorithmInfo> _algorithms = BuildAlgorithmDictionary();

        /// <summary>
        /// Gets all registered PQC algorithms indexed by algorithm ID.
        /// </summary>
        public static IReadOnlyDictionary<string, PqcAlgorithmInfo> Algorithms { get; } = new ReadOnlyDictionary<string, PqcAlgorithmInfo>(_algorithms);

        private static Dictionary<string, PqcAlgorithmInfo> BuildAlgorithmDictionary() => new(StringComparer.OrdinalIgnoreCase)
        {
                // FIPS 203: ML-KEM (Module Lattice-based Key Encapsulation Mechanism, formerly CRYSTALS-Kyber)
                ["ml-kem-512"] = new PqcAlgorithmInfo
                {
                    Profile = new AlgorithmProfile
                    {
                        AlgorithmId = "ml-kem-512",
                        AlgorithmName = "ML-KEM-512 (Kyber)",
                        Category = AlgorithmCategory.KeyExchange,
                        SecurityLevel = SecurityLevel.QuantumSafe,
                        IsPostQuantum = true,
                        FipsReference = "FIPS 203",
                        NistLevel = 1,
                        KeySizeBits = 6400, // 800 bytes public key
                        StrategyId = "ml-kem-512"
                    },
                    BouncyCastleType = "MLKemParameters.ML_KEM_512",
                    DotNetType = null,
                    ParameterSetName = "ML-KEM-512"
                },
                ["ml-kem-768"] = new PqcAlgorithmInfo
                {
                    Profile = new AlgorithmProfile
                    {
                        AlgorithmId = "ml-kem-768",
                        AlgorithmName = "ML-KEM-768 (Kyber)",
                        Category = AlgorithmCategory.KeyExchange,
                        SecurityLevel = SecurityLevel.QuantumSafe,
                        IsPostQuantum = true,
                        FipsReference = "FIPS 203",
                        NistLevel = 3,
                        KeySizeBits = 9472, // 1184 bytes public key
                        StrategyId = "ml-kem-768"
                    },
                    BouncyCastleType = "MLKemParameters.ML_KEM_768",
                    DotNetType = null,
                    ParameterSetName = "ML-KEM-768"
                },
                ["ml-kem-1024"] = new PqcAlgorithmInfo
                {
                    Profile = new AlgorithmProfile
                    {
                        AlgorithmId = "ml-kem-1024",
                        AlgorithmName = "ML-KEM-1024 (Kyber)",
                        Category = AlgorithmCategory.KeyExchange,
                        SecurityLevel = SecurityLevel.QuantumSafe,
                        IsPostQuantum = true,
                        FipsReference = "FIPS 203",
                        NistLevel = 5,
                        KeySizeBits = 12544, // 1568 bytes public key
                        StrategyId = "ml-kem-1024"
                    },
                    BouncyCastleType = "MLKemParameters.ML_KEM_1024",
                    DotNetType = null,
                    ParameterSetName = "ML-KEM-1024"
                },

                // FIPS 204: ML-DSA (Module Lattice-based Digital Signature Algorithm, formerly CRYSTALS-Dilithium)
                ["ml-dsa-44"] = new PqcAlgorithmInfo
                {
                    Profile = new AlgorithmProfile
                    {
                        AlgorithmId = "ml-dsa-44",
                        AlgorithmName = "ML-DSA-44 (Dilithium)",
                        Category = AlgorithmCategory.DigitalSignature,
                        SecurityLevel = SecurityLevel.QuantumSafe,
                        IsPostQuantum = true,
                        FipsReference = "FIPS 204",
                        NistLevel = 2,
                        KeySizeBits = 10048, // 1256 bytes public key
                        SignatureSizeBits = 19616, // 2452 bytes signature
                        StrategyId = "ml-dsa-44"
                    },
                    BouncyCastleType = "MLDsaParameters.ML_DSA_44",
                    DotNetType = null,
                    ParameterSetName = "ML-DSA-44"
                },
                ["ml-dsa-65"] = new PqcAlgorithmInfo
                {
                    Profile = new AlgorithmProfile
                    {
                        AlgorithmId = "ml-dsa-65",
                        AlgorithmName = "ML-DSA-65 (Dilithium)",
                        Category = AlgorithmCategory.DigitalSignature,
                        SecurityLevel = SecurityLevel.QuantumSafe,
                        IsPostQuantum = true,
                        FipsReference = "FIPS 204",
                        NistLevel = 3,
                        KeySizeBits = 15616, // 1952 bytes public key
                        SignatureSizeBits = 26472, // 3309 bytes signature
                        StrategyId = "ml-dsa-65"
                    },
                    BouncyCastleType = "MLDsaParameters.ML_DSA_65",
                    DotNetType = null,
                    ParameterSetName = "ML-DSA-65"
                },
                ["ml-dsa-87"] = new PqcAlgorithmInfo
                {
                    Profile = new AlgorithmProfile
                    {
                        AlgorithmId = "ml-dsa-87",
                        AlgorithmName = "ML-DSA-87 (Dilithium)",
                        Category = AlgorithmCategory.DigitalSignature,
                        SecurityLevel = SecurityLevel.QuantumSafe,
                        IsPostQuantum = true,
                        FipsReference = "FIPS 204",
                        NistLevel = 5,
                        KeySizeBits = 20736, // 2592 bytes public key
                        SignatureSizeBits = 36352, // 4544 bytes signature
                        StrategyId = "ml-dsa-87"
                    },
                    BouncyCastleType = "MLDsaParameters.ML_DSA_87",
                    DotNetType = null,
                    ParameterSetName = "ML-DSA-87"
                },

                // FIPS 205: SLH-DSA (Stateless Hash-based Digital Signature Algorithm, formerly SPHINCS+)
                ["slh-dsa-shake-128f"] = new PqcAlgorithmInfo
                {
                    Profile = new AlgorithmProfile
                    {
                        AlgorithmId = "slh-dsa-shake-128f",
                        AlgorithmName = "SLH-DSA-SHAKE-128f (SPHINCS+)",
                        Category = AlgorithmCategory.DigitalSignature,
                        SecurityLevel = SecurityLevel.QuantumSafe,
                        IsPostQuantum = true,
                        FipsReference = "FIPS 205",
                        NistLevel = 1,
                        KeySizeBits = 256, // 32 bytes public key
                        SignatureSizeBits = 136704, // 17088 bytes signature
                        StrategyId = "slh-dsa-shake-128f"
                    },
                    BouncyCastleType = "SLHDSAParameters.SLH_DSA_SHAKE_128F",
                    DotNetType = null,
                    ParameterSetName = "SLH-DSA-SHAKE-128f"
                },
                ["slh-dsa-shake-192f"] = new PqcAlgorithmInfo
                {
                    Profile = new AlgorithmProfile
                    {
                        AlgorithmId = "slh-dsa-shake-192f",
                        AlgorithmName = "SLH-DSA-SHAKE-192f (SPHINCS+)",
                        Category = AlgorithmCategory.DigitalSignature,
                        SecurityLevel = SecurityLevel.QuantumSafe,
                        IsPostQuantum = true,
                        FipsReference = "FIPS 205",
                        NistLevel = 3,
                        KeySizeBits = 384, // 48 bytes public key
                        SignatureSizeBits = 280064, // 35008 bytes signature
                        StrategyId = "slh-dsa-shake-192f"
                    },
                    BouncyCastleType = "SLHDSAParameters.SLH_DSA_SHAKE_192F",
                    DotNetType = null,
                    ParameterSetName = "SLH-DSA-SHAKE-192f"
                },
                ["slh-dsa-shake-256f"] = new PqcAlgorithmInfo
                {
                    Profile = new AlgorithmProfile
                    {
                        AlgorithmId = "slh-dsa-shake-256f",
                        AlgorithmName = "SLH-DSA-SHAKE-256f (SPHINCS+)",
                        Category = AlgorithmCategory.DigitalSignature,
                        SecurityLevel = SecurityLevel.QuantumSafe,
                        IsPostQuantum = true,
                        FipsReference = "FIPS 205",
                        NistLevel = 5,
                        KeySizeBits = 512, // 64 bytes public key
                        SignatureSizeBits = 395264, // 49408 bytes signature
                        StrategyId = "slh-dsa-shake-256f"
                    },
                    BouncyCastleType = "SLHDSAParameters.SLH_DSA_SHAKE_256F",
                    DotNetType = null,
                    ParameterSetName = "SLH-DSA-SHAKE-256f"
                },

                // Hybrid: classical + PQC combined for defense-in-depth
                ["hybrid-x25519-kyber768"] = new PqcAlgorithmInfo
                {
                    Profile = new AlgorithmProfile
                    {
                        AlgorithmId = "hybrid-x25519-kyber768",
                        AlgorithmName = "Hybrid X25519 + ML-KEM-768",
                        Category = AlgorithmCategory.KeyExchange,
                        SecurityLevel = SecurityLevel.QuantumSafe,
                        IsPostQuantum = true,
                        FipsReference = null, // Hybrid not yet standardized
                        NistLevel = 3,
                        KeySizeBits = 9728, // X25519 (256 bits) + ML-KEM-768 (9472 bits)
                        StrategyId = "hybrid-x25519-kyber768"
                    },
                    BouncyCastleType = null,
                    DotNetType = null,
                    ParameterSetName = "X25519-ML-KEM-768"
                }
        };

        /// <summary>
        /// Gets the recommended Key Encapsulation Mechanism for a given NIST security level.
        /// </summary>
        /// <param name="nistLevel">NIST security level (1, 3, or 5).</param>
        /// <returns>The recommended KEM algorithm, or null if no match found.</returns>
        public static PqcAlgorithmInfo? GetRecommendedKem(int nistLevel)
        {
            return nistLevel switch
            {
                1 => _algorithms.GetValueOrDefault("ml-kem-512"),
                3 => _algorithms.GetValueOrDefault("ml-kem-768"),
                5 => _algorithms.GetValueOrDefault("ml-kem-1024"),
                _ => null
            };
        }

        /// <summary>
        /// Gets the recommended digital signature algorithm for a given NIST security level.
        /// Prefers ML-DSA over SLH-DSA for performance; SLH-DSA is recommended when
        /// hash-based security assumptions are preferred over lattice-based.
        /// </summary>
        /// <param name="nistLevel">NIST security level (1-5).</param>
        /// <returns>The recommended signature algorithm, or null if no match found.</returns>
        public static PqcAlgorithmInfo? GetRecommendedSignature(int nistLevel)
        {
            return nistLevel switch
            {
                1 or 2 => _algorithms.GetValueOrDefault("ml-dsa-44"),
                3 => _algorithms.GetValueOrDefault("ml-dsa-65"),
                4 or 5 => _algorithms.GetValueOrDefault("ml-dsa-87"),
                _ => null
            };
        }

        /// <summary>
        /// Gets the recommended PQC replacement for a classical algorithm.
        /// Maps common classical algorithms to their post-quantum equivalents.
        /// </summary>
        /// <param name="classicalAlgorithm">Classical algorithm identifier (e.g., "rsa-2048", "ecdsa-p256", "x25519").</param>
        /// <returns>The recommended PQC replacement, or null if no mapping exists.</returns>
        public static PqcAlgorithmInfo? GetMigrationPath(string classicalAlgorithm)
        {
            if (string.IsNullOrWhiteSpace(classicalAlgorithm))
                return null;

            var normalized = classicalAlgorithm.ToLowerInvariant().Trim();

            // Key exchange / encryption migrations
            if (normalized.Contains("x25519") || normalized.Contains("ecdh-p256") || normalized.Contains("dh-"))
                return _algorithms.GetValueOrDefault("ml-kem-768");

            if (normalized.Contains("ecdh-p384"))
                return _algorithms.GetValueOrDefault("ml-kem-768");

            if (normalized.Contains("ecdh-p521"))
                return _algorithms.GetValueOrDefault("ml-kem-1024");

            if (normalized.Contains("rsa-2048") || normalized.Contains("rsa-3072"))
                return _algorithms.GetValueOrDefault("ml-kem-768");

            if (normalized.Contains("rsa-4096") || normalized.Contains("rsa-8192"))
                return _algorithms.GetValueOrDefault("ml-kem-1024");

            // Signature migrations
            if (normalized.Contains("ecdsa-p256") || normalized.Contains("ed25519"))
                return _algorithms.GetValueOrDefault("ml-dsa-44");

            if (normalized.Contains("ecdsa-p384"))
                return _algorithms.GetValueOrDefault("ml-dsa-65");

            if (normalized.Contains("ecdsa-p521"))
                return _algorithms.GetValueOrDefault("ml-dsa-87");

            if (normalized.Contains("rsa") && (normalized.Contains("sign") || normalized.Contains("pss")))
                return _algorithms.GetValueOrDefault("ml-dsa-65");

            return null;
        }

        /// <summary>
        /// Checks whether a given algorithm is marked as deprecated in the registry.
        /// </summary>
        /// <param name="algorithmId">Algorithm identifier to check.</param>
        /// <returns>True if the algorithm is deprecated, false if not deprecated or not found.</returns>
        public static bool IsDeprecated(string algorithmId)
        {
            if (string.IsNullOrWhiteSpace(algorithmId))
                return false;

            return _algorithms.TryGetValue(algorithmId, out var info)
                && info.Profile.IsDeprecated;
        }
    }
}
