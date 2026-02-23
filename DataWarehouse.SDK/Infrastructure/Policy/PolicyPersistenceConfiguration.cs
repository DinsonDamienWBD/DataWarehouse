using System;
using System.ComponentModel;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Infrastructure.Policy
{
    /// <summary>
    /// Enumerates the available persistence backends for the Policy Engine.
    /// Each backend trades off between simplicity, durability, scalability, and tamper-proofness.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 69: Policy persistence config (PERS-01)")]
    public enum PolicyPersistenceBackend
    {
        /// <summary>In-memory storage for testing and ephemeral deployments. Data is lost on process restart.</summary>
        [Description("In-memory storage for testing and ephemeral deployments; data lost on restart")]
        InMemory = 0,

        /// <summary>File-system storage for single-node deployments, typically as a VDE sidecar file.</summary>
        [Description("File-system storage for single-node deployments; VDE sidecar file")]
        File = 1,

        /// <summary>Database storage for multi-node deployments with optional replication support.</summary>
        [Description("Database storage for multi-node deployments with optional replication")]
        Database = 2,

        /// <summary>Blockchain-backed immutable storage for tamper-proof audit and compliance requirements.</summary>
        [Description("Blockchain-backed immutable storage for tamper-proof audit and compliance")]
        TamperProof = 3,

        /// <summary>Composite backend: routes policies to one backend and audit records to another.</summary>
        [Description("Composite backend: policies in one backend, audit in another")]
        Hybrid = 4
    }

    /// <summary>
    /// Configuration record that selects which persistence backend the Policy Engine uses
    /// and carries per-backend options. Consumed by each <see cref="PolicyPersistenceBase"/>
    /// implementation's constructor and by compliance validation in Plan 04.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 69: Policy persistence config (PERS-01)")]
    public sealed record PolicyPersistenceConfiguration
    {
        /// <summary>
        /// Which persistence backend implementation to use.
        /// </summary>
        public PolicyPersistenceBackend Backend { get; init; }

        /// <summary>
        /// Connection string for the Database backend. Ignored by other backends.
        /// </summary>
        public string? ConnectionString { get; init; }

        /// <summary>
        /// Directory path for the File backend. When null, policies are stored adjacent to the .dwvd file.
        /// Ignored by non-File backends.
        /// </summary>
        public string? SidecarDirectory { get; init; }

        /// <summary>
        /// For Hybrid mode: which backend stores audit records. Defaults to <see cref="PolicyPersistenceBackend.TamperProof"/>.
        /// </summary>
        public PolicyPersistenceBackend AuditBackend { get; init; } = PolicyPersistenceBackend.TamperProof;

        /// <summary>
        /// For Hybrid mode: which backend stores policies. Defaults to <see cref="PolicyPersistenceBackend.Database"/>.
        /// </summary>
        public PolicyPersistenceBackend PolicyBackend { get; init; } = PolicyPersistenceBackend.Database;

        /// <summary>
        /// Maximum number of entries held by the InMemory backend. Prevents unbounded memory growth.
        /// Default: 100,000.
        /// </summary>
        public int MaxInMemoryEntries { get; init; } = 100_000;

        /// <summary>
        /// Whether the Database backend should enable multi-node replication.
        /// </summary>
        public bool EnableReplication { get; init; }

        /// <summary>
        /// Endpoint URL for the blockchain service used by the TamperProof backend.
        /// </summary>
        public string? BlockchainEndpoint { get; init; }

        /// <summary>
        /// Active compliance frameworks (e.g., "HIPAA", "SOC2", "GDPR", "PCI-DSS") that
        /// constrain persistence behavior for compliance validation.
        /// </summary>
        public string[] ActiveComplianceFrameworks { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Creates a default configuration for the InMemory backend suitable for testing and development.
        /// </summary>
        /// <returns>A <see cref="PolicyPersistenceConfiguration"/> configured for in-memory storage.</returns>
        public static PolicyPersistenceConfiguration InMemoryDefault() => new()
        {
            Backend = PolicyPersistenceBackend.InMemory,
            MaxInMemoryEntries = 100_000
        };

        /// <summary>
        /// Creates a default configuration for the File backend suitable for single-node deployments.
        /// </summary>
        /// <returns>A <see cref="PolicyPersistenceConfiguration"/> configured for file-based storage.</returns>
        public static PolicyPersistenceConfiguration FileDefault() => new()
        {
            Backend = PolicyPersistenceBackend.File
        };

        /// <summary>
        /// Creates a default configuration for the Database backend suitable for multi-node deployments.
        /// </summary>
        /// <returns>A <see cref="PolicyPersistenceConfiguration"/> configured for database storage.</returns>
        public static PolicyPersistenceConfiguration DatabaseDefault() => new()
        {
            Backend = PolicyPersistenceBackend.Database,
            EnableReplication = true
        };

        /// <summary>
        /// Creates a default Hybrid configuration that stores policies in Database and audit in TamperProof.
        /// Suitable for production environments requiring both scalability and immutable audit trails.
        /// </summary>
        /// <returns>A <see cref="PolicyPersistenceConfiguration"/> configured for hybrid storage.</returns>
        public static PolicyPersistenceConfiguration HybridDefault() => new()
        {
            Backend = PolicyPersistenceBackend.Hybrid,
            PolicyBackend = PolicyPersistenceBackend.Database,
            AuditBackend = PolicyPersistenceBackend.TamperProof,
            EnableReplication = true
        };
    }
}
