using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Contracts
{
    /// <summary>
    /// Core interface for the DataWarehouse orchestrator (Kernel).
    ///
    /// The Kernel is the central orchestrator - it does NOT perform storage operations directly.
    /// Storage operations are handled by IStorageProvider plugins.
    /// Container/partition management is handled by IContainerManager.
    ///
    /// Responsibilities:
    /// - Plugin registration and lifecycle management
    /// - Message routing between plugins
    /// - Pipeline orchestration
    /// - Background job management
    /// </summary>
    public interface IDataWarehouse
    {
        /// <summary>
        /// Unique identifier for this kernel instance.
        /// </summary>
        string KernelId { get; }

        /// <summary>
        /// Current operating mode.
        /// </summary>
        OperatingMode OperatingMode { get; }

        /// <summary>
        /// Whether the kernel is initialized and ready.
        /// </summary>
        bool IsReady { get; }

        /// <summary>
        /// The message bus for inter-plugin communication.
        /// </summary>
        IMessageBus MessageBus { get; }

        /// <summary>
        /// The pipeline orchestrator for transformation chains.
        /// </summary>
        IPipelineOrchestrator PipelineOrchestrator { get; }

        // --- Lifecycle ---

        /// <summary>
        /// Initialize the kernel and load plugins.
        /// </summary>
        Task InitializeAsync(CancellationToken ct = default);

        /// <summary>
        /// Register a plugin with the kernel.
        /// </summary>
        Task<HandshakeResponse> RegisterPluginAsync(IPlugin plugin, CancellationToken ct = default);

        // --- Storage Providers (not storage operations) ---

        /// <summary>
        /// Set the primary storage provider.
        /// </summary>
        void SetPrimaryStorage(IStorageProvider storage);

        /// <summary>
        /// Set the cache storage provider.
        /// </summary>
        void SetCacheStorage(IStorageProvider storage);

        /// <summary>
        /// Get the primary storage provider.
        /// </summary>
        IStorageProvider? GetPrimaryStorage();

        /// <summary>
        /// Get the cache storage provider.
        /// </summary>
        IStorageProvider? GetCacheStorage();

        // --- Pipeline ---

        /// <summary>
        /// Execute the write pipeline (compress, encrypt, etc.).
        /// </summary>
        Task<Stream> ExecuteWritePipelineAsync(Stream input, PipelineContext context, CancellationToken ct = default);

        /// <summary>
        /// Execute the read pipeline (decrypt, decompress, etc.).
        /// </summary>
        Task<Stream> ExecuteReadPipelineAsync(Stream input, PipelineContext context, CancellationToken ct = default);

        /// <summary>
        /// Override pipeline configuration at runtime.
        /// </summary>
        void SetPipelineConfiguration(PipelineConfiguration config, bool persistent = true);

        // --- Plugins ---

        /// <summary>
        /// Get a plugin by type.
        /// </summary>
        T? GetPlugin<T>() where T : class, IPlugin;

        /// <summary>
        /// Get a plugin by type and ID.
        /// </summary>
        T? GetPlugin<T>(string id) where T : class, IPlugin;

        /// <summary>
        /// Get all plugins of a type.
        /// </summary>
        IEnumerable<T> GetPlugins<T>() where T : class, IPlugin;

        // --- Background Jobs ---

        /// <summary>
        /// Run a background job.
        /// </summary>
        string RunInBackground(Func<CancellationToken, Task> job, string? jobId = null);
    }
}
