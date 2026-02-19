using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;

namespace DataWarehouse.SDK.Contracts.Encryption
{
    /// <summary>
    /// Represents the phases of a cryptographic algorithm migration lifecycle.
    /// Tracks progression from initial assessment through double-encryption transition
    /// to final cutover and cleanup.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 59: Cryptographic commitment schemes")]
    public enum MigrationPhase
    {
        /// <summary>Migration plan created but not yet started.</summary>
        NotStarted,

        /// <summary>Assessing objects that need migration, estimating scope.</summary>
        Assessment,

        /// <summary>Objects are being encrypted with both source and target algorithms.</summary>
        DoubleEncryption,

        /// <summary>Verifying that double-encrypted objects can be decrypted by both algorithms.</summary>
        Verification,

        /// <summary>Switching primary decryption to the target algorithm.</summary>
        CutOver,

        /// <summary>Removing source algorithm ciphertext from double-encrypted objects.</summary>
        Cleanup,

        /// <summary>Migration completed successfully.</summary>
        Complete,

        /// <summary>Migration failed and requires intervention.</summary>
        Failed,

        /// <summary>Migration was rolled back to the source algorithm.</summary>
        RolledBack
    }

    /// <summary>
    /// Categorizes cryptographic algorithms by their function.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 59: Cryptographic commitment schemes")]
    public enum AlgorithmCategory
    {
        /// <summary>Symmetric block/stream ciphers (AES, ChaCha20).</summary>
        SymmetricEncryption,

        /// <summary>Asymmetric/public-key encryption (RSA, ML-KEM).</summary>
        AsymmetricEncryption,

        /// <summary>Key encapsulation/exchange mechanisms (X25519, ML-KEM).</summary>
        KeyExchange,

        /// <summary>Digital signature algorithms (ECDSA, ML-DSA, SLH-DSA).</summary>
        DigitalSignature,

        /// <summary>Cryptographic hash functions (SHA-256, SHA-3, SHAKE).</summary>
        HashFunction,

        /// <summary>Key derivation functions (HKDF, PBKDF2, Argon2).</summary>
        KeyDerivation
    }

    /// <summary>
    /// Describes a cryptographic algorithm's properties, security characteristics,
    /// deprecation status, and mapping to encryption strategy implementations.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 59: Cryptographic commitment schemes")]
    public record AlgorithmProfile
    {
        /// <summary>Unique algorithm identifier (e.g., "ml-kem-768", "aes-256-gcm").</summary>
        public required string AlgorithmId { get; init; }

        /// <summary>Human-readable algorithm name (e.g., "ML-KEM-768 (Kyber)").</summary>
        public required string AlgorithmName { get; init; }

        /// <summary>Functional category of this algorithm.</summary>
        public required AlgorithmCategory Category { get; init; }

        /// <summary>Security level classification.</summary>
        public required SecurityLevel SecurityLevel { get; init; }

        /// <summary>Whether this algorithm is resistant to quantum computing attacks.</summary>
        public bool IsPostQuantum { get; init; }

        /// <summary>FIPS standard reference (e.g., "FIPS 203", "FIPS 204"), if applicable.</summary>
        public string? FipsReference { get; init; }

        /// <summary>NIST security level (1-5), if applicable for post-quantum algorithms.</summary>
        public int? NistLevel { get; init; }

        /// <summary>Key size in bits.</summary>
        public required int KeySizeBits { get; init; }

        /// <summary>Signature size in bits, if applicable for signature algorithms.</summary>
        public int? SignatureSizeBits { get; init; }

        /// <summary>Whether this algorithm is deprecated and should not be used for new data.</summary>
        public bool IsDeprecated { get; init; }

        /// <summary>Date when this algorithm was or will be deprecated.</summary>
        public DateTimeOffset? DeprecationDate { get; init; }

        /// <summary>Algorithm ID of the recommended replacement, if deprecated.</summary>
        public string? ReplacedBy { get; init; }

        /// <summary>Maps to EncryptionStrategyBase.StrategyId for encryption operations.</summary>
        public required string StrategyId { get; init; }
    }

    /// <summary>
    /// Defines a migration plan for transitioning objects from one cryptographic algorithm
    /// to another, supporting double-encryption for zero-downtime transitions.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 59: Cryptographic commitment schemes")]
    public class MigrationPlan
    {
        /// <summary>Unique identifier for this migration plan.</summary>
        public required string PlanId { get; init; }

        /// <summary>The algorithm currently protecting the data.</summary>
        public required AlgorithmProfile SourceAlgorithm { get; init; }

        /// <summary>The target algorithm to migrate to.</summary>
        public required AlgorithmProfile TargetAlgorithm { get; init; }

        /// <summary>Current phase of the migration.</summary>
        public MigrationPhase Phase { get; set; } = MigrationPhase.NotStarted;

        /// <summary>When the migration plan was created.</summary>
        public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

        /// <summary>When migration execution started.</summary>
        public DateTimeOffset? StartedAt { get; set; }

        /// <summary>When migration completed (successfully or with failure).</summary>
        public DateTimeOffset? CompletedAt { get; set; }

        /// <summary>Total number of objects that need to be migrated.</summary>
        public long ObjectsTotal { get; set; }

        /// <summary>Number of objects successfully migrated.</summary>
        public long ObjectsMigrated { get; set; }

        /// <summary>Number of objects that failed migration.</summary>
        public long ObjectsFailed { get; set; }

        /// <summary>Whether to use double-encryption during the transition period.</summary>
        public bool UseDoubleEncryption { get; init; } = true;

        /// <summary>
        /// Failure threshold (0.0-1.0) at which the migration automatically rolls back.
        /// For example, 0.05 means rollback if more than 5% of objects fail.
        /// </summary>
        public double RollbackOnFailureThreshold { get; init; } = 0.05;

        /// <summary>Maximum number of concurrent migration operations.</summary>
        public int MaxConcurrentMigrations { get; init; } = 4;
    }

    /// <summary>
    /// Reports the current status of a migration plan, including progress,
    /// error details, and estimated completion time.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 59: Cryptographic commitment schemes")]
    public class MigrationStatus
    {
        /// <summary>The migration plan this status applies to.</summary>
        public required string PlanId { get; init; }

        /// <summary>Current phase of the migration.</summary>
        public required MigrationPhase Phase { get; init; }

        /// <summary>Overall progress as a value between 0.0 and 1.0.</summary>
        public double Progress { get; init; }

        /// <summary>Total number of objects in the migration scope.</summary>
        public long ObjectsTotal { get; init; }

        /// <summary>Number of objects successfully migrated.</summary>
        public long ObjectsMigrated { get; init; }

        /// <summary>Number of objects that failed migration.</summary>
        public long ObjectsFailed { get; init; }

        /// <summary>Size of the current processing batch.</summary>
        public int CurrentBatchSize { get; init; }

        /// <summary>Estimated UTC time when the migration will complete.</summary>
        public DateTimeOffset? EstimatedCompletionUtc { get; init; }

        /// <summary>List of errors encountered during migration.</summary>
        public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

        /// <summary>Whether the migration can be rolled back from its current state.</summary>
        public bool IsRollbackAvailable { get; init; }
    }

    /// <summary>
    /// Contains ciphertext encrypted with two different algorithms simultaneously.
    /// Used during algorithm migration to enable zero-downtime transitions.
    /// Either algorithm can decrypt the data, allowing gradual migration of consumers.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 59: Cryptographic commitment schemes")]
    public class DoubleEncryptionEnvelope
    {
        /// <summary>Unique identifier of the encrypted object.</summary>
        public required Guid ObjectId { get; init; }

        /// <summary>Algorithm ID used for primary encryption.</summary>
        public required string PrimaryAlgorithmId { get; init; }

        /// <summary>Algorithm ID used for secondary encryption.</summary>
        public required string SecondaryAlgorithmId { get; init; }

        /// <summary>Ciphertext produced by the primary algorithm.</summary>
        public required byte[] PrimaryCiphertext { get; init; }

        /// <summary>Ciphertext produced by the secondary algorithm.</summary>
        public required byte[] SecondaryCiphertext { get; init; }

        /// <summary>When this double-encryption envelope was created.</summary>
        public DateTimeOffset TransitionCreatedAt { get; init; } = DateTimeOffset.UtcNow;

        /// <summary>When this double-encryption envelope expires and must be resolved.</summary>
        public required DateTimeOffset TransitionExpiresAt { get; init; }

        /// <summary>Additional metadata for the transition (e.g., key IDs, audit info).</summary>
        public Dictionary<string, string?> Metadata { get; init; } = new();
    }

    /// <summary>
    /// Represents a batch of objects to be migrated from one algorithm to another.
    /// Used by the crypto-agility engine to process migrations in controlled batches.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 59: Cryptographic commitment schemes")]
    public record MigrationBatch
    {
        /// <summary>Unique identifier for this batch.</summary>
        public required string BatchId { get; init; }

        /// <summary>Object identifiers to migrate in this batch.</summary>
        public required Guid[] ObjectIds { get; init; }

        /// <summary>Source algorithm identifier.</summary>
        public required string SourceAlgorithm { get; init; }

        /// <summary>Target algorithm identifier.</summary>
        public required string TargetAlgorithm { get; init; }

        /// <summary>Number of objects in this batch.</summary>
        public int BatchSize => ObjectIds.Length;
    }
}
