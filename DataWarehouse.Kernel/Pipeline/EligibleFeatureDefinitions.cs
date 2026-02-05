using DataWarehouse.SDK.Contracts.Pipeline;

namespace DataWarehouse.Kernel.Pipeline
{
    /// <summary>
    /// Describes a feature that can be configured through the pipeline policy system.
    /// </summary>
    public class EligibleFeature
    {
        /// <summary>Unique feature identifier.</summary>
        public string FeatureId { get; init; } = string.Empty;

        /// <summary>Pipeline stage type this feature controls.</summary>
        public string StageType { get; init; } = string.Empty;

        /// <summary>Human-readable display name.</summary>
        public string DisplayName { get; init; } = string.Empty;

        /// <summary>Detailed description of what this feature does.</summary>
        public string Description { get; init; } = string.Empty;

        /// <summary>Default strategy/algorithm name.</summary>
        public string DefaultStrategyName { get; init; } = string.Empty;

        /// <summary>List of valid strategy names that can be selected.</summary>
        public string[] ValidStrategies { get; init; } = [];

        /// <summary>Whether this feature can be completely disabled.</summary>
        public bool SupportsDisable { get; init; }

        /// <summary>Minimum policy level at which this feature can be configured.</summary>
        public PolicyLevel MinimumPolicyLevel { get; init; }

        /// <summary>Default parameters for this feature.</summary>
        public Dictionary<string, object> DefaultParameters { get; init; } = new();
    }

    /// <summary>
    /// Defines the eligible features that can be configured through the pipeline policy system.
    /// Each feature maps to a pipeline stage type with default settings and valid options.
    /// </summary>
    public static class EligibleFeatureDefinitions
    {
        /// <summary>
        /// Encryption algorithm selection per level.
        /// </summary>
        public static readonly EligibleFeature Encryption = new()
        {
            FeatureId = "encryption",
            StageType = "Encryption",
            DisplayName = "Encryption Algorithm",
            Description = "Select the encryption algorithm (AES-256-GCM, ChaCha20-Poly1305, Serpent, etc.) per policy level. " +
                          "Provides data confidentiality and integrity through authenticated encryption.",
            DefaultStrategyName = "AES-256-GCM",
            ValidStrategies = [
                // AES family
                "AES-256-GCM", "AES-256-CBC", "AES-256-CTR", "AES-256-CCM", "AES-256-OCB",
                "AES-256-SIV", "AES-256-GCM-SIV", "AES-256-XTS", "AES-256-KW",
                "AES-192-GCM", "AES-192-CBC", "AES-128-GCM", "AES-128-CBC",
                // ChaCha/Salsa family
                "ChaCha20-Poly1305", "XChaCha20-Poly1305", "Salsa20",
                // Alternative block ciphers
                "Serpent-256-GCM", "Serpent-256-CBC", "Twofish-256-GCM", "Twofish-256-CBC",
                "Camellia-256-GCM", "Camellia-256-CBC", "ARIA-256-GCM", "SM4-GCM",
                "SEED-CBC", "Kuznyechik-GCM", "Magma-CBC",
                // Legacy (for compatibility)
                "Blowfish-CBC", "IDEA-CBC", "CAST5-CBC", "CAST6-CBC", "RC5-CBC", "RC6-CBC",
                "3DES-CBC", "DES-CBC",
                // AEAD constructs
                "Deoxys-II", "Ascon-128", "AEGIS-256",
                // Post-quantum encryption
                "ML-KEM-768", "ML-KEM-1024", "NTRU-HPS-2048", "SABER-KEM",
                "Classic-McEliece-348864", "FrodoKEM-640", "BIKE-L3", "HQC-256",
                // Hybrid encryption
                "Hybrid-AES256-MLKEM768", "Hybrid-ChaCha20-MLKEM1024",
                // Disk encryption
                "XTS-AES-256", "Adiantum", "ESSIV-AES-256",
                // Format-preserving encryption
                "FF1-AES", "FF3-1-AES",
                // Homomorphic encryption
                "SEAL-BFV", "SEAL-CKKS", "TFHE", "OpenFHE-BFV"
            ],
            SupportsDisable = false, // Encryption should not be disabled for security
            MinimumPolicyLevel = PolicyLevel.Instance,
            DefaultParameters = new Dictionary<string, object>
            {
                ["keySize"] = 256,
                ["mode"] = "GCM",
                ["tagSize"] = 16
            }
        };

        /// <summary>
        /// Compression algorithm selection per level.
        /// </summary>
        public static readonly EligibleFeature Compression = new()
        {
            FeatureId = "compression",
            StageType = "Compression",
            DisplayName = "Compression Algorithm",
            Description = "Select the compression algorithm (Zstd, Brotli, LZ4, etc.) per policy level. " +
                          "Reduces storage space and network bandwidth requirements.",
            DefaultStrategyName = "Zstd",
            ValidStrategies = [
                // LZ family
                "Zstd", "LZ4", "GZip", "Deflate", "Snappy", "LZO", "LZ77", "LZ78",
                "LZMA", "LZMA2", "LZFSE", "LZH", "LZX",
                // Transform-based
                "Brotli", "BZip2", "BWT", "MTF",
                // Context mixing
                "ZPAQ", "PAQ8", "CMix", "NNZ", "PPM", "PPMd",
                // Entropy coders
                "Huffman", "Arithmetic", "ANS", "FSE", "rANS", "RLE",
                // Delta encoding
                "Delta", "Xdelta", "Bsdiff", "VCDIFF", "Zdelta",
                // Domain-specific
                "FLAC", "APNG", "WebP", "JPEG-XL", "AVIF", "DNA", "TimeSeries",
                // Archive formats
                "ZIP", "7z", "RAR", "TAR", "XZ",
                // Emerging
                "Density", "Lizard", "Oodle", "Zling", "Gipfeli"
            ],
            SupportsDisable = true, // Compression can be disabled for already-compressed data
            MinimumPolicyLevel = PolicyLevel.Instance,
            DefaultParameters = new Dictionary<string, object>
            {
                ["level"] = 3, // Balanced compression level
                ["threads"] = 1
            }
        };

        /// <summary>
        /// Pipeline stage ordering per level.
        /// </summary>
        public static readonly EligibleFeature StageOrdering = new()
        {
            FeatureId = "stage-ordering",
            StageType = "Configuration",
            DisplayName = "Stage Execution Order",
            Description = "Define the order in which pipeline stages are executed. " +
                          "Common orders: Compression→Encryption, Encryption→Compression (for algorithms that compress encrypted data).",
            DefaultStrategyName = "Compression-First",
            ValidStrategies = [
                "Compression-First", // Compress then Encrypt (most common)
                "Encryption-First", // Encrypt then Compress (for special algorithms)
                "Parallel-Stages", // Execute compatible stages in parallel
                "Custom-Order" // User-defined ordering
            ],
            SupportsDisable = false,
            MinimumPolicyLevel = PolicyLevel.Instance,
            DefaultParameters = new Dictionary<string, object>
            {
                ["order"] = new[] { "Compression", "Encryption", "RAID", "Integrity" }
            }
        };

        /// <summary>
        /// RAID/sharding mode per level.
        /// </summary>
        public static readonly EligibleFeature RaidMode = new()
        {
            FeatureId = "raid-mode",
            StageType = "RAID",
            DisplayName = "RAID/Erasure Coding Mode",
            Description = "Select data redundancy and distribution strategy (RAID 0/1/5/6/10, Reed-Solomon, etc.). " +
                          "Provides fault tolerance and performance optimization.",
            DefaultStrategyName = "RAID5",
            ValidStrategies = [
                "RAID0", "RAID1", "RAID5", "RAID6", "RAID10", "RAID50", "RAID60",
                "Reed-Solomon", "Cauchy-RS", "Vandermonde-RS",
                "Fountain-Codes", "Raptor-Codes", "LT-Codes",
                "No-RAID" // Single copy, no redundancy
            ],
            SupportsDisable = true,
            MinimumPolicyLevel = PolicyLevel.Instance,
            DefaultParameters = new Dictionary<string, object>
            {
                ["dataShards"] = 4,
                ["parityShards"] = 2
            }
        };

        /// <summary>
        /// Deduplication mode per level.
        /// </summary>
        public static readonly EligibleFeature Deduplication = new()
        {
            FeatureId = "deduplication",
            StageType = "Deduplication",
            DisplayName = "Deduplication Strategy",
            Description = "Select deduplication strategy (file-level, block-level, variable-length chunks). " +
                          "Eliminates duplicate data to save storage space.",
            DefaultStrategyName = "Variable-Block",
            ValidStrategies = [
                "File-Level", // Deduplicate entire files
                "Fixed-Block", // Fixed-size block deduplication
                "Variable-Block", // Content-aware variable-length chunks (most efficient)
                "Fingerprint-Only", // Store fingerprints for detection without actual dedup
                "Disabled" // No deduplication
            ],
            SupportsDisable = true,
            MinimumPolicyLevel = PolicyLevel.Instance,
            DefaultParameters = new Dictionary<string, object>
            {
                ["minChunkSize"] = 4096,
                ["maxChunkSize"] = 131072,
                ["averageChunkSize"] = 16384
            }
        };

        /// <summary>
        /// Integrity checking per level.
        /// </summary>
        public static readonly EligibleFeature IntegrityCheck = new()
        {
            FeatureId = "integrity-check",
            StageType = "Integrity",
            DisplayName = "Data Integrity Verification",
            Description = "Select checksum/hash algorithm for data integrity verification (SHA-256, BLAKE3, etc.). " +
                          "Detects data corruption and tampering.",
            DefaultStrategyName = "BLAKE3",
            ValidStrategies = [
                "BLAKE3", "SHA-256", "SHA-512", "SHA3-256", "SHA3-512",
                "BLAKE2b", "BLAKE2s", "xxHash", "CRC32", "Adler32",
                "MD5" // Legacy, not recommended
            ],
            SupportsDisable = false, // Integrity checks should always be enabled
            MinimumPolicyLevel = PolicyLevel.Instance,
            DefaultParameters = new Dictionary<string, object>
            {
                ["algorithm"] = "BLAKE3",
                ["verifyOnRead"] = true
            }
        };

        /// <summary>
        /// Tiering policy per level.
        /// </summary>
        public static readonly EligibleFeature TieringPolicy = new()
        {
            FeatureId = "tiering-policy",
            StageType = "Tiering",
            DisplayName = "Storage Tier Policy",
            Description = "Define automatic data movement between storage tiers (Hot/Warm/Cold/Archive) based on access patterns. " +
                          "Optimizes cost and performance.",
            DefaultStrategyName = "Auto-Tiering",
            ValidStrategies = [
                "Hot-Only", // Keep all data in hot tier
                "Auto-Tiering", // Automatic tier movement based on access
                "Time-Based", // Tier based on age
                "Access-Pattern", // Tier based on access frequency
                "Manual" // No automatic tiering
            ],
            SupportsDisable = true,
            MinimumPolicyLevel = PolicyLevel.Instance,
            DefaultParameters = new Dictionary<string, object>
            {
                ["hotToWarmDays"] = 7,
                ["warmToColdDays"] = 30,
                ["coldToArchiveDays"] = 90
            }
        };

        /// <summary>
        /// Replication mode per level.
        /// </summary>
        public static readonly EligibleFeature ReplicationMode = new()
        {
            FeatureId = "replication-mode",
            StageType = "Replication",
            DisplayName = "Data Replication Strategy",
            Description = "Select replication strategy (sync/async, cross-region, etc.) for data durability and availability.",
            DefaultStrategyName = "Async-3-Replica",
            ValidStrategies = [
                "Sync-3-Replica", // Synchronous 3-way replication
                "Async-3-Replica", // Asynchronous 3-way replication
                "Geo-Replica", // Cross-region replication
                "Single-Copy", // No replication
                "Quorum-Based" // Quorum-based consistency
            ],
            SupportsDisable = true,
            MinimumPolicyLevel = PolicyLevel.Instance,
            DefaultParameters = new Dictionary<string, object>
            {
                ["replicas"] = 3,
                ["mode"] = "async"
            }
        };

        /// <summary>
        /// Retention/WORM policy per level.
        /// </summary>
        public static readonly EligibleFeature RetentionPolicy = new()
        {
            FeatureId = "retention-policy",
            StageType = "Retention",
            DisplayName = "Retention and WORM Policy",
            Description = "Define retention periods and Write-Once-Read-Many (WORM) policies for compliance. " +
                          "Prevents deletion or modification of data for specified periods.",
            DefaultStrategyName = "7-Year-Retention",
            ValidStrategies = [
                "1-Year-Retention", "3-Year-Retention", "7-Year-Retention", "10-Year-Retention",
                "WORM-1-Year", "WORM-7-Year", "WORM-Permanent",
                "Legal-Hold", // Indefinite hold until released
                "No-Retention" // No retention policy
            ],
            SupportsDisable = true,
            MinimumPolicyLevel = PolicyLevel.Instance,
            DefaultParameters = new Dictionary<string, object>
            {
                ["retentionDays"] = 2555, // 7 years
                ["allowModification"] = false,
                ["allowDeletion"] = false
            }
        };

        /// <summary>
        /// Audit logging granularity per level.
        /// </summary>
        public static readonly EligibleFeature AuditLogging = new()
        {
            FeatureId = "audit-logging",
            StageType = "Audit",
            DisplayName = "Audit Logging Level",
            Description = "Configure audit log granularity (None/Basic/Detailed/Full) for compliance and security monitoring.",
            DefaultStrategyName = "Detailed",
            ValidStrategies = [
                "None", // No audit logging
                "Basic", // Log only write operations
                "Detailed", // Log all operations with metadata
                "Full", // Log everything including data access patterns
                "Compliance" // Extended logging for regulatory compliance
            ],
            SupportsDisable = true,
            MinimumPolicyLevel = PolicyLevel.Instance,
            DefaultParameters = new Dictionary<string, object>
            {
                ["logReads"] = true,
                ["logWrites"] = true,
                ["logDeletes"] = true,
                ["logMetadata"] = true
            }
        };

        /// <summary>
        /// Gets all eligible features.
        /// </summary>
        /// <returns>Read-only list of all defined features.</returns>
        public static IReadOnlyList<EligibleFeature> GetAll() => [
            Encryption,
            Compression,
            StageOrdering,
            RaidMode,
            Deduplication,
            IntegrityCheck,
            TieringPolicy,
            ReplicationMode,
            RetentionPolicy,
            AuditLogging
        ];

        /// <summary>
        /// Gets a feature by its ID.
        /// </summary>
        /// <param name="featureId">The feature identifier.</param>
        /// <returns>The feature definition, or null if not found.</returns>
        public static EligibleFeature? GetById(string featureId)
        {
            return GetAll().FirstOrDefault(f =>
                f.FeatureId.Equals(featureId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Gets all features for a specific stage type.
        /// </summary>
        /// <param name="stageType">The stage type to filter by.</param>
        /// <returns>Read-only list of features matching the stage type.</returns>
        public static IReadOnlyList<EligibleFeature> GetByStageType(string stageType)
        {
            return GetAll().Where(f =>
                f.StageType.Equals(stageType, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        /// <summary>
        /// Gets all features that can be configured at the specified policy level or below.
        /// </summary>
        /// <param name="level">The policy level.</param>
        /// <returns>Read-only list of features available at this level.</returns>
        public static IReadOnlyList<EligibleFeature> GetByPolicyLevel(PolicyLevel level)
        {
            return GetAll().Where(f => f.MinimumPolicyLevel <= level).ToList();
        }
    }
}
