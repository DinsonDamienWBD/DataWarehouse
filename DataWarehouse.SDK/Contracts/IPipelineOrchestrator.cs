using DataWarehouse.SDK.Primitives;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts
{
    /// <summary>
    /// Pipeline orchestrator interface for runtime-configurable transformation chains.
    /// Allows the kernel to chain operations (compress, encrypt, etc.) and supports
    /// user overrides for custom ordering.
    ///
    /// Default order: Compress → Encrypt
    /// User can override to: Encrypt → CustomCompress (for algorithms that work on encrypted data)
    /// </summary>
    public interface IPipelineOrchestrator
    {
        /// <summary>
        /// Get the current pipeline configuration.
        /// </summary>
        PipelineConfiguration GetConfiguration();

        /// <summary>
        /// Set a new pipeline configuration (user override).
        /// </summary>
        void SetConfiguration(PipelineConfiguration config);

        /// <summary>
        /// Reset to default pipeline configuration.
        /// </summary>
        void ResetToDefaults();

        /// <summary>
        /// Execute the write pipeline (user data → storage).
        /// Applies transformations in configured order.
        /// </summary>
        Task<Stream> ExecuteWritePipelineAsync(
            Stream input,
            PipelineContext context,
            CancellationToken ct = default);

        /// <summary>
        /// Execute the read pipeline (storage → user data).
        /// Applies transformations in reverse order.
        /// </summary>
        Task<Stream> ExecuteReadPipelineAsync(
            Stream input,
            PipelineContext context,
            CancellationToken ct = default);

        /// <summary>
        /// Register a pipeline stage plugin.
        /// </summary>
        void RegisterStage(IDataTransformation stage);

        /// <summary>
        /// Unregister a pipeline stage.
        /// </summary>
        void UnregisterStage(string stageId);

        /// <summary>
        /// Get all registered stages.
        /// </summary>
        IEnumerable<PipelineStageInfo> GetRegisteredStages();

        /// <summary>
        /// Validate a proposed pipeline configuration.
        /// Returns errors if stages are incompatible or missing dependencies.
        /// </summary>
        PipelineValidationResult ValidateConfiguration(PipelineConfiguration config);
    }

    /// <summary>
    /// Pipeline configuration - defines the order and settings for transformation stages.
    /// </summary>
    public class PipelineConfiguration
    {
        /// <summary>
        /// Unique identifier for this configuration.
        /// </summary>
        public string ConfigurationId { get; init; } = Guid.NewGuid().ToString();

        /// <summary>
        /// Human-readable name for this configuration.
        /// </summary>
        public string Name { get; init; } = "Default";

        /// <summary>
        /// Ordered list of stages to execute on write (user → storage).
        /// </summary>
        public List<PipelineStageConfig> WriteStages { get; init; } = new();

        /// <summary>
        /// Whether this is the default configuration.
        /// </summary>
        public bool IsDefault { get; init; }

        /// <summary>
        /// Configuration metadata.
        /// </summary>
        public Dictionary<string, object> Metadata { get; init; } = new();

        /// <summary>
        /// Create a default configuration: Compress → Encrypt
        /// </summary>
        public static PipelineConfiguration CreateDefault()
        {
            return new PipelineConfiguration
            {
                Name = "Default",
                IsDefault = true,
                WriteStages = new List<PipelineStageConfig>
                {
                    new() { StageType = "Compression", Order = 100, Enabled = true },
                    new() { StageType = "Encryption", Order = 200, Enabled = true }
                }
            };
        }
    }

    /// <summary>
    /// Configuration for a single pipeline stage.
    /// </summary>
    public class PipelineStageConfig
    {
        /// <summary>
        /// Stage type/category (e.g., "Compression", "Encryption").
        /// </summary>
        public string StageType { get; init; } = string.Empty;

        /// <summary>
        /// Specific plugin ID to use. Null means auto-select best available.
        /// </summary>
        public string? PluginId { get; init; }

        /// <summary>
        /// Execution order (lower = earlier).
        /// </summary>
        public int Order { get; init; }

        /// <summary>
        /// Whether this stage is enabled.
        /// </summary>
        public bool Enabled { get; init; } = true;

        /// <summary>
        /// Stage-specific parameters.
        /// </summary>
        public Dictionary<string, object> Parameters { get; init; } = new();
    }

    /// <summary>
    /// Information about a registered pipeline stage.
    /// </summary>
    public class PipelineStageInfo
    {
        /// <summary>
        /// Stage plugin ID.
        /// </summary>
        public string PluginId { get; init; } = string.Empty;

        /// <summary>
        /// Stage name.
        /// </summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>
        /// Stage sub-category (e.g., "Encryption", "Compression").
        /// </summary>
        public string SubCategory { get; init; } = string.Empty;

        /// <summary>
        /// Quality level (1-100).
        /// </summary>
        public int QualityLevel { get; init; }

        /// <summary>
        /// Default order in pipeline.
        /// </summary>
        public int DefaultOrder { get; init; }

        /// <summary>
        /// Whether the stage allows bypass.
        /// </summary>
        public bool AllowBypass { get; init; }

        /// <summary>
        /// Required preceding stages.
        /// </summary>
        public string[] RequiredPrecedingStages { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Incompatible stages.
        /// </summary>
        public string[] IncompatibleStages { get; init; } = Array.Empty<string>();

        /// <summary>
        /// AI-friendly description.
        /// </summary>
        public string Description { get; init; } = string.Empty;
    }

    /// <summary>
    /// Context passed to pipeline stages during execution.
    /// </summary>
    public class PipelineContext
    {
        /// <summary>
        /// Kernel context for logging and services.
        /// </summary>
        public IKernelContext? KernelContext { get; init; }

        /// <summary>
        /// Storage intent for AI-driven decisions.
        /// </summary>
        public StorageIntent? Intent { get; init; }

        /// <summary>
        /// Manifest metadata.
        /// </summary>
        public Manifest? Manifest { get; init; }

        /// <summary>
        /// Content type hint (e.g., "text/plain", "image/png").
        /// </summary>
        public string? ContentType { get; init; }

        /// <summary>
        /// Original content size in bytes.
        /// </summary>
        public long? OriginalSize { get; init; }

        /// <summary>
        /// Additional context parameters.
        /// </summary>
        public Dictionary<string, object> Parameters { get; init; } = new();

        /// <summary>
        /// Track which stages have been executed.
        /// </summary>
        public List<string> ExecutedStages { get; } = new();
    }

    /// <summary>
    /// Result of pipeline configuration validation.
    /// </summary>
    public class PipelineValidationResult
    {
        /// <summary>
        /// Whether the configuration is valid.
        /// </summary>
        public bool IsValid { get; init; }

        /// <summary>
        /// Validation errors.
        /// </summary>
        public List<string> Errors { get; init; } = new();

        /// <summary>
        /// Validation warnings (non-blocking).
        /// </summary>
        public List<string> Warnings { get; init; } = new();

        /// <summary>
        /// Suggested fixes for errors.
        /// </summary>
        public List<string> Suggestions { get; init; } = new();
    }
}
