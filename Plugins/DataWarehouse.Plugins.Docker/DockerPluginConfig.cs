using System.Net;

namespace DataWarehouse.Plugins.Docker
{
    /// <summary>
    /// Configuration options for the Docker integration plugin.
    /// Provides settings for volume driver, log driver, and secret backend components.
    /// </summary>
    public sealed class DockerPluginConfig
    {
        /// <summary>
        /// Default base path for Docker plugin data.
        /// </summary>
        public static readonly string DefaultBasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "DataWarehouse", "docker");

        /// <summary>
        /// Base path for volume storage.
        /// </summary>
        public string VolumeBasePath { get; init; } =
            Path.Combine(DefaultBasePath, "volumes");

        /// <summary>
        /// Base path for container log storage.
        /// </summary>
        public string LogBasePath { get; init; } =
            Path.Combine(DefaultBasePath, "logs");

        /// <summary>
        /// Base path for secret storage.
        /// </summary>
        public string SecretBasePath { get; init; } =
            Path.Combine(DefaultBasePath, "secrets");

        /// <summary>
        /// IP address to listen on for Docker plugin API.
        /// Defaults to localhost for security.
        /// </summary>
        public IPAddress? ListenAddress { get; init; } = IPAddress.Loopback;

        /// <summary>
        /// Port to listen on for Docker plugin API.
        /// </summary>
        public int ListenPort { get; init; } = 9876;

        /// <summary>
        /// Shutdown timeout in seconds.
        /// </summary>
        public int ShutdownTimeoutSeconds { get; init; } = 30;

        /// <summary>
        /// Storage backend type for volumes.
        /// </summary>
        public DockerStorageBackend StorageBackend { get; init; } = DockerStorageBackend.Local;

        /// <summary>
        /// S3 bucket name when using S3 storage backend.
        /// </summary>
        public string? S3Bucket { get; init; }

        /// <summary>
        /// S3 endpoint URL for S3-compatible storage.
        /// </summary>
        public string? S3Endpoint { get; init; }

        /// <summary>
        /// AWS region for S3 storage.
        /// </summary>
        public string? S3Region { get; init; }

        /// <summary>
        /// S3 access key (use IAM roles in production).
        /// </summary>
        public string? S3AccessKey { get; init; }

        /// <summary>
        /// S3 secret key (use IAM roles in production).
        /// </summary>
        public string? S3SecretKey { get; init; }

        /// <summary>
        /// Default storage tier for new volumes.
        /// </summary>
        public DockerStorageTier DefaultStorageTier { get; init; } = DockerStorageTier.Hot;

        /// <summary>
        /// Enable encryption for volumes by default.
        /// </summary>
        public bool DefaultVolumeEncryption { get; init; } = false;

        /// <summary>
        /// Log flush interval in seconds.
        /// </summary>
        public int LogFlushIntervalSeconds { get; init; } = 5;

        /// <summary>
        /// Maximum log file size before rotation (in bytes).
        /// </summary>
        public int MaxLogFileSize { get; init; } = 10 * 1024 * 1024; // 10MB

        /// <summary>
        /// Maximum number of rotated log files to keep.
        /// </summary>
        public int MaxLogFiles { get; init; } = 5;

        /// <summary>
        /// Compress rotated log files.
        /// </summary>
        public bool CompressRotatedLogs { get; init; } = true;

        /// <summary>
        /// Master encryption key for secrets (optional, will be derived if not set).
        /// In production, use a proper key management system.
        /// </summary>
        public string? SecretEncryptionKey { get; init; }

        /// <summary>
        /// Enable audit logging for secret access.
        /// </summary>
        public bool EnableSecretAuditLogging { get; init; } = true;

        /// <summary>
        /// Enable rootless mode compatibility.
        /// </summary>
        public bool RootlessMode { get; init; } = false;

        /// <summary>
        /// Respect cgroup memory limits.
        /// </summary>
        public bool RespectCgroupLimits { get; init; } = true;

        /// <summary>
        /// Unix socket path for Docker plugin communication (Linux/macOS).
        /// </summary>
        public string? UnixSocketPath { get; init; }

        /// <summary>
        /// Creates default configuration suitable for development.
        /// </summary>
        public static DockerPluginConfig Default => new();

        /// <summary>
        /// Creates configuration for S3 storage backend.
        /// </summary>
        /// <param name="bucket">S3 bucket name.</param>
        /// <param name="region">AWS region.</param>
        /// <param name="endpoint">Optional S3-compatible endpoint.</param>
        /// <returns>Configuration instance.</returns>
        public static DockerPluginConfig ForS3(string bucket, string region, string? endpoint = null)
        {
            return new DockerPluginConfig
            {
                StorageBackend = DockerStorageBackend.S3,
                S3Bucket = bucket,
                S3Region = region,
                S3Endpoint = endpoint
            };
        }

        /// <summary>
        /// Creates configuration for local storage with custom path.
        /// </summary>
        /// <param name="basePath">Base path for all Docker data.</param>
        /// <returns>Configuration instance.</returns>
        public static DockerPluginConfig ForLocalPath(string basePath)
        {
            return new DockerPluginConfig
            {
                VolumeBasePath = Path.Combine(basePath, "volumes"),
                LogBasePath = Path.Combine(basePath, "logs"),
                SecretBasePath = Path.Combine(basePath, "secrets")
            };
        }

        /// <summary>
        /// Creates configuration for production deployment.
        /// </summary>
        /// <param name="secretEncryptionKey">Master key for secret encryption.</param>
        /// <returns>Configuration instance.</returns>
        public static DockerPluginConfig ForProduction(string secretEncryptionKey)
        {
            return new DockerPluginConfig
            {
                SecretEncryptionKey = secretEncryptionKey,
                DefaultVolumeEncryption = true,
                EnableSecretAuditLogging = true,
                RespectCgroupLimits = true,
                CompressRotatedLogs = true
            };
        }

        /// <summary>
        /// Creates configuration for rootless Docker deployment.
        /// </summary>
        /// <returns>Configuration instance.</returns>
        public static DockerPluginConfig ForRootless()
        {
            var userDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return new DockerPluginConfig
            {
                VolumeBasePath = Path.Combine(userDataPath, "datawarehouse", "docker", "volumes"),
                LogBasePath = Path.Combine(userDataPath, "datawarehouse", "docker", "logs"),
                SecretBasePath = Path.Combine(userDataPath, "datawarehouse", "docker", "secrets"),
                RootlessMode = true
            };
        }
    }

    /// <summary>
    /// Storage backend types for Docker volumes.
    /// </summary>
    public enum DockerStorageBackend
    {
        /// <summary>
        /// Local filesystem storage.
        /// </summary>
        Local = 0,

        /// <summary>
        /// S3-compatible object storage.
        /// </summary>
        S3 = 1,

        /// <summary>
        /// DataWarehouse storage provider (uses configured storage plugins).
        /// </summary>
        DataWarehouse = 2
    }

    /// <summary>
    /// Health status information for the Docker plugin.
    /// </summary>
    public sealed class DockerHealthStatus
    {
        /// <summary>
        /// Overall health status.
        /// </summary>
        public bool IsHealthy { get; init; }

        /// <summary>
        /// Volume driver health status.
        /// </summary>
        public string VolumeDriverStatus { get; init; } = string.Empty;

        /// <summary>
        /// Log driver health status.
        /// </summary>
        public string LogDriverStatus { get; init; } = string.Empty;

        /// <summary>
        /// Secret backend health status.
        /// </summary>
        public string SecretBackendStatus { get; init; } = string.Empty;

        /// <summary>
        /// Current memory usage in bytes.
        /// </summary>
        public long MemoryUsageBytes { get; init; }

        /// <summary>
        /// Whether cgroup limits are being respected.
        /// </summary>
        public bool CgroupLimitsRespected { get; init; }
    }
}
