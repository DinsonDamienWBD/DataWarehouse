using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Kernel.Configuration
{
    /// <summary>
    /// Configuration options for the DataWarehouse Kernel.
    /// </summary>
    public class KernelConfiguration
    {
        /// <summary>
        /// Unique identifier for this kernel instance.
        /// Auto-generated if not specified.
        /// </summary>
        public string? KernelId { get; set; }

        /// <summary>
        /// Operating mode determines plugin selection and optimization strategy.
        /// Default: Workstation
        /// </summary>
        public OperatingMode OperatingMode { get; set; } = OperatingMode.Workstation;

        /// <summary>
        /// Root path for data storage.
        /// Default: Current directory
        /// </summary>
        public string? RootPath { get; set; }

        /// <summary>
        /// Paths to load plugins from.
        /// </summary>
        public List<string> PluginPaths { get; set; } = new();

        /// <summary>
        /// Pipeline configuration for transformation order.
        /// Default: Compress -> Encrypt
        /// </summary>
        public PipelineConfiguration? PipelineConfiguration { get; set; }

        /// <summary>
        /// Use the built-in in-memory storage as default.
        /// Useful for testing and development.
        /// </summary>
        public bool UseInMemoryStorageByDefault { get; set; } = true;

        /// <summary>
        /// Automatically start feature plugins when registered.
        /// </summary>
        public bool AutoStartFeatures { get; set; } = true;

        /// <summary>
        /// Maximum concurrent background jobs.
        /// </summary>
        public int MaxBackgroundJobs { get; set; } = 10;

        /// <summary>
        /// Timeout for plugin handshake operations.
        /// </summary>
        public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Enable detailed diagnostic logging.
        /// </summary>
        public bool EnableDiagnostics { get; set; }

        /// <summary>
        /// Whether to require plugin assemblies to be signed with a strong name.
        /// Default: true (production security). Set to false only in development mode.
        /// Addresses ISO-05 (CVSS 7.7) from pentest report.
        /// </summary>
        public bool RequireSignedPluginAssemblies { get; set; } = true;
    }
}
