using System.Xml.Serialization;

namespace DataWarehouse.SDK.Primitives.Configuration;

/// <summary>
/// Unified configuration object for the entire DataWarehouse system.
/// Consolidates security, storage, networking, replication, encryption, compression,
/// observability, compute, resilience, deployment, data management, message bus, and plugin
/// configuration into a single object. This is the authoritative source of all system settings.
/// </summary>
/// <remarks>
/// <para>
/// The configuration is persisted as an XML file on disk. At every startup the file is loaded
/// via ConfigurationSerializer.LoadFromFile. Runtime changes are written back
/// via ConfigurationSerializer.SaveToFile so the file always reflects the live state.
/// </para>
/// <para>
/// Six preset levels are available via <see cref="ConfigurationPresets"/>: unsafe, minimal,
/// standard, secure, paranoid, and god-tier.
/// </para>
/// </remarks>
[XmlRoot("DataWarehouseConfiguration")]
public class DataWarehouseConfiguration
{
    /// <summary>Gets or sets the configuration preset name (unsafe, minimal, standard, secure, paranoid, god-tier).</summary>
    public string PresetName { get; set; } = "standard";

    /// <summary>Gets or sets the configuration version (for migration compatibility).</summary>
    public string Version { get; set; } = "3.0.0";

    /// <summary>Security-related configuration.</summary>
    public SecurityConfiguration Security { get; set; } = new();

    /// <summary>Storage-related configuration.</summary>
    public StorageConfiguration Storage { get; set; } = new();

    /// <summary>Network-related configuration.</summary>
    public NetworkConfiguration Network { get; set; } = new();

    /// <summary>Replication-related configuration.</summary>
    public ReplicationConfiguration Replication { get; set; } = new();

    /// <summary>Encryption-related configuration.</summary>
    public EncryptionConfiguration Encryption { get; set; } = new();

    /// <summary>Compression-related configuration.</summary>
    public CompressionConfiguration Compression { get; set; } = new();

    /// <summary>Observability-related configuration.</summary>
    public ObservabilityConfiguration Observability { get; set; } = new();

    /// <summary>Compute-related configuration.</summary>
    public ComputeConfiguration Compute { get; set; } = new();

    /// <summary>Resilience-related configuration.</summary>
    public ResilienceConfiguration Resilience { get; set; } = new();

    /// <summary>Deployment-related configuration.</summary>
    public DeploymentConfiguration Deployment { get; set; } = new();

    /// <summary>Data management configuration.</summary>
    public DataManagementConfiguration DataManagement { get; set; } = new();

    /// <summary>Message bus configuration.</summary>
    public MessageBusConfiguration MessageBus { get; set; } = new();

    /// <summary>Plugin system configuration.</summary>
    public PluginConfiguration Plugins { get; set; } = new();
}

/// <summary>Security configuration section.</summary>
public class SecurityConfiguration
{
    public ConfigurationItem<bool> EncryptionEnabled { get; set; } = new(true);
    public ConfigurationItem<bool> AuthEnabled { get; set; } = new(true);
    public ConfigurationItem<bool> AuditEnabled { get; set; } = new(true);
    public ConfigurationItem<bool> TlsRequired { get; set; } = new(true);
    public ConfigurationItem<bool> MfaEnabled { get; set; } = new(false);
    public ConfigurationItem<bool> RbacEnabled { get; set; } = new(true);
    public ConfigurationItem<bool> ZeroTrustMode { get; set; } = new(false);
    public ConfigurationItem<bool> FipsMode { get; set; } = new(false);
    public ConfigurationItem<bool> QuantumSafeMode { get; set; } = new(false);
    public ConfigurationItem<bool> HsmRequired { get; set; } = new(false);
    public ConfigurationItem<bool> CertificatePinning { get; set; } = new(false);
    public ConfigurationItem<bool> TamperProofLogging { get; set; } = new(false);
    public ConfigurationItem<int> PasswordMinLength { get; set; } = new(12);
    public ConfigurationItem<int> SessionTimeoutMinutes { get; set; } = new(30);
    public ConfigurationItem<string> DefaultAuthScheme { get; set; } = new("RBAC");
}

/// <summary>Storage configuration section.</summary>
public class StorageConfiguration
{
    public ConfigurationItem<bool> EncryptAtRest { get; set; } = new(true);
    public ConfigurationItem<bool> EnableCompression { get; set; } = new(true);
    public ConfigurationItem<bool> EnableDeduplication { get; set; } = new(false);
    public ConfigurationItem<bool> EnableCaching { get; set; } = new(true);
    public ConfigurationItem<bool> EnableIndexing { get; set; } = new(true);
    public ConfigurationItem<bool> EnableVersioning { get; set; } = new(false);
    public ConfigurationItem<long> CacheSizeBytes { get; set; } = new(1024L * 1024 * 1024); // 1GB
    public ConfigurationItem<int> MaxConnections { get; set; } = new(100);
    public ConfigurationItem<string> DefaultBackend { get; set; } = new("FileSystem");
    public ConfigurationItem<string> DataDirectory { get; set; } = new("./data");
}

/// <summary>Network configuration section.</summary>
public class NetworkConfiguration
{
    public ConfigurationItem<bool> TlsEnabled { get; set; } = new(true);
    public ConfigurationItem<bool> Http2Enabled { get; set; } = new(true);
    public ConfigurationItem<bool> Http3Enabled { get; set; } = new(false);
    public ConfigurationItem<bool> CompressionEnabled { get; set; } = new(true);
    public ConfigurationItem<int> ListenPort { get; set; } = new(8080);
    public ConfigurationItem<int> MaxConnectionsPerEndpoint { get; set; } = new(1000);
    public ConfigurationItem<int> TimeoutSeconds { get; set; } = new(30);
    public ConfigurationItem<string> BindAddress { get; set; } = new("0.0.0.0");
}

/// <summary>Replication configuration section.</summary>
public class ReplicationConfiguration
{
    public ConfigurationItem<bool> Enabled { get; set; } = new(false);
    public ConfigurationItem<bool> MultiMasterEnabled { get; set; } = new(false);
    public ConfigurationItem<bool> CrdtEnabled { get; set; } = new(false);
    public ConfigurationItem<int> ReplicationFactor { get; set; } = new(3);
    public ConfigurationItem<int> ConsistencyLevel { get; set; } = new(1); // 1=ONE, 2=QUORUM, 3=ALL
    public ConfigurationItem<int> SyncIntervalSeconds { get; set; } = new(60);
    public ConfigurationItem<string> ConflictResolutionStrategy { get; set; } = new("LastWriteWins");
}

/// <summary>Encryption configuration section.</summary>
public class EncryptionConfiguration
{
    public ConfigurationItem<bool> Enabled { get; set; } = new(true);
    public ConfigurationItem<bool> EncryptInTransit { get; set; } = new(true);
    public ConfigurationItem<bool> EncryptAtRest { get; set; } = new(true);
    public ConfigurationItem<bool> EnvelopeEncryption { get; set; } = new(true);
    public ConfigurationItem<bool> KeyRotationEnabled { get; set; } = new(false);
    public ConfigurationItem<int> KeyRotationDays { get; set; } = new(90);
    public ConfigurationItem<string> DefaultAlgorithm { get; set; } = new("AES256-GCM");
    public ConfigurationItem<string> KeyStoreBackend { get; set; } = new("FileSystem");
}

/// <summary>Compression configuration section.</summary>
public class CompressionConfiguration
{
    public ConfigurationItem<bool> Enabled { get; set; } = new(true);
    public ConfigurationItem<bool> AutoSelect { get; set; } = new(true);
    public ConfigurationItem<int> CompressionLevel { get; set; } = new(6); // 0-9
    public ConfigurationItem<string> DefaultAlgorithm { get; set; } = new("zstd");
}

/// <summary>Observability configuration section.</summary>
public class ObservabilityConfiguration
{
    public ConfigurationItem<bool> MetricsEnabled { get; set; } = new(true);
    public ConfigurationItem<bool> TracingEnabled { get; set; } = new(false);
    public ConfigurationItem<bool> LoggingEnabled { get; set; } = new(true);
    public ConfigurationItem<bool> HealthCheckEnabled { get; set; } = new(true);
    public ConfigurationItem<bool> AnomalyDetectionEnabled { get; set; } = new(false);
    public ConfigurationItem<string> LogLevel { get; set; } = new("Information");
    public ConfigurationItem<int> MetricsIntervalSeconds { get; set; } = new(60);
}

/// <summary>Compute configuration section.</summary>
public class ComputeConfiguration
{
    public ConfigurationItem<bool> GpuEnabled { get; set; } = new(false);
    public ConfigurationItem<bool> ParallelProcessingEnabled { get; set; } = new(true);
    public ConfigurationItem<int> MaxWorkerThreads { get; set; } = new(0); // 0 = auto-detect
    public ConfigurationItem<int> MaxIoThreads { get; set; } = new(0); // 0 = auto-detect
    public ConfigurationItem<string> SchedulingStrategy { get; set; } = new("FairShare");
}

/// <summary>Resilience configuration section.</summary>
public class ResilienceConfiguration
{
    public ConfigurationItem<bool> CircuitBreakerEnabled { get; set; } = new(true);
    public ConfigurationItem<bool> RetryEnabled { get; set; } = new(true);
    public ConfigurationItem<bool> BulkheadEnabled { get; set; } = new(false);
    public ConfigurationItem<bool> SelfHealingEnabled { get; set; } = new(false);
    public ConfigurationItem<int> MaxRetries { get; set; } = new(3);
    public ConfigurationItem<int> CircuitBreakerThreshold { get; set; } = new(5);
    public ConfigurationItem<int> TimeoutSeconds { get; set; } = new(30);
}

/// <summary>Deployment configuration section.</summary>
public class DeploymentConfiguration
{
    public ConfigurationItem<bool> AirGapMode { get; set; } = new(false);
    public ConfigurationItem<bool> MultiCloudEnabled { get; set; } = new(false);
    public ConfigurationItem<bool> EdgeEnabled { get; set; } = new(false);
    public ConfigurationItem<bool> EmbeddedMode { get; set; } = new(false);
    public ConfigurationItem<string> EnvironmentType { get; set; } = new("Production");
}

/// <summary>Data management configuration section.</summary>
public class DataManagementConfiguration
{
    public ConfigurationItem<bool> CatalogEnabled { get; set; } = new(true);
    public ConfigurationItem<bool> GovernanceEnabled { get; set; } = new(false);
    public ConfigurationItem<bool> QualityEnabled { get; set; } = new(false);
    public ConfigurationItem<bool> LineageEnabled { get; set; } = new(false);
    public ConfigurationItem<bool> MultiTenancyEnabled { get; set; } = new(false);
}

/// <summary>Message bus configuration section.</summary>
public class MessageBusConfiguration
{
    public ConfigurationItem<bool> PersistentMessages { get; set; } = new(false);
    public ConfigurationItem<bool> OrderedDelivery { get; set; } = new(false);
    public ConfigurationItem<int> MaxQueueSize { get; set; } = new(10000);
    public ConfigurationItem<int> DeliveryTimeoutSeconds { get; set; } = new(30);
}

/// <summary>Plugin configuration section.</summary>
public class PluginConfiguration
{
    public ConfigurationItem<bool> AutoLoadPlugins { get; set; } = new(true);
    public ConfigurationItem<bool> AllowHotReload { get; set; } = new(false);
    public ConfigurationItem<int> MaxPlugins { get; set; } = new(100);
    public ConfigurationItem<string> PluginDirectory { get; set; } = new("./plugins");
}
