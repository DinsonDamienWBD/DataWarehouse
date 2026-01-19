using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Contracts
{
    /// <summary>
    /// The base contract for ALL plugins (Crypto, Compression, Features).
    /// </summary>
    public interface IPlugin
    {
        /// <summary>
        /// Unique Plugin ID
        /// </summary>
        string Id { get; }

        /// <summary>
        /// Gets the category of the plugin.
        /// </summary>
        PluginCategory Category { get; }

        /// <summary>
        /// Human-readable Name.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Semantic Version.
        /// </summary>
        string Version { get; }

        // New message handler
        Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request);

        // Optional: For plugins that need external signals
        Task OnMessageAsync(PluginMessage message);
    }

    /// <summary>
    /// Exposed by the Kernel to allow plugins to log or access core services.
    /// </summary>
    public interface IKernelContext
    {
        /// <summary>
        /// Gets the detected operating environment (Laptop, Server, etc.).
        /// </summary>
        OperatingMode Mode { get; }

        /// <summary>
        /// The root directory of the Data Warehouse instance.
        /// </summary>
        string RootPath { get; }

        /// <summary>
        /// Log information.
        /// </summary>
        /// <param name="message">The message to log.</param>
        void LogInfo(string message);

        /// <summary>
        /// Log error.
        /// </summary>
        /// <param name="message">The error message.</param>
        /// <param name="ex">The optional exception.</param>
        void LogError(string message, Exception? ex = null);

        /// <summary>
        /// Log warning.
        /// </summary>
        /// <param name="message">The warning message.</param>
        void LogWarning(string message);

        /// <summary>
        /// Log debug info.
        /// </summary>
        /// <param name="message">The debug message.</param>
        void LogDebug(string message);

        /// <summary>
        /// Retrieves a specific type of plugin.
        /// </summary>
        /// <typeparam name="T">The plugin interface type.</typeparam>
        /// <returns>The best matching plugin or null.</returns>
        T? GetPlugin<T>() where T : class, IPlugin;

        /// <summary>
        /// Retrieves all plugins of a specific type.
        /// </summary>
        /// <typeparam name="T">The plugin interface type.</typeparam>
        /// <returns>A collection of matching plugins.</returns>
        System.Collections.Generic.IEnumerable<T> GetPlugins<T>() where T : class, IPlugin;

        /// <summary>
        /// Gets the kernel storage service for persisting plugin data.
        /// </summary>
        IKernelStorageService Storage { get; }
    }

    /// <summary>
    /// Storage service interface for plugins to persist data via the kernel.
    /// This provides real storage persistence instead of mock in-memory storage.
    /// </summary>
    public interface IKernelStorageService
    {
        /// <summary>
        /// Saves data to the specified storage path.
        /// </summary>
        /// <param name="path">The storage path (e.g., "blobs/my-blob-id").</param>
        /// <param name="data">The data stream to save.</param>
        /// <param name="metadata">Optional metadata to associate with the stored data.</param>
        /// <param name="ct">Cancellation token.</param>
        Task SaveAsync(string path, Stream data, IDictionary<string, string>? metadata = null, CancellationToken ct = default);

        /// <summary>
        /// Saves byte array data to the specified storage path.
        /// </summary>
        /// <param name="path">The storage path.</param>
        /// <param name="data">The byte array to save.</param>
        /// <param name="metadata">Optional metadata.</param>
        /// <param name="ct">Cancellation token.</param>
        Task SaveAsync(string path, byte[] data, IDictionary<string, string>? metadata = null, CancellationToken ct = default);

        /// <summary>
        /// Loads data from the specified storage path.
        /// </summary>
        /// <param name="path">The storage path.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The data stream, or null if not found.</returns>
        Task<Stream?> LoadAsync(string path, CancellationToken ct = default);

        /// <summary>
        /// Loads data as byte array from the specified storage path.
        /// </summary>
        /// <param name="path">The storage path.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The byte array, or null if not found.</returns>
        Task<byte[]?> LoadBytesAsync(string path, CancellationToken ct = default);

        /// <summary>
        /// Deletes data at the specified storage path.
        /// </summary>
        /// <param name="path">The storage path.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if deleted, false if not found.</returns>
        Task<bool> DeleteAsync(string path, CancellationToken ct = default);

        /// <summary>
        /// Checks if data exists at the specified storage path.
        /// </summary>
        /// <param name="path">The storage path.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if exists, false otherwise.</returns>
        Task<bool> ExistsAsync(string path, CancellationToken ct = default);

        /// <summary>
        /// Lists all items under the specified prefix.
        /// </summary>
        /// <param name="prefix">The path prefix to list.</param>
        /// <param name="limit">Maximum number of items to return.</param>
        /// <param name="offset">Number of items to skip.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Collection of storage item info.</returns>
        Task<IReadOnlyList<StorageItemInfo>> ListAsync(string prefix, int limit = 100, int offset = 0, CancellationToken ct = default);

        /// <summary>
        /// Gets metadata for the specified storage path.
        /// </summary>
        /// <param name="path">The storage path.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The metadata dictionary, or null if not found.</returns>
        Task<IDictionary<string, string>?> GetMetadataAsync(string path, CancellationToken ct = default);
    }

    /// <summary>
    /// Information about a stored item.
    /// </summary>
    public sealed class StorageItemInfo
    {
        /// <summary>
        /// The storage path of the item.
        /// </summary>
        public required string Path { get; init; }

        /// <summary>
        /// Size of the item in bytes.
        /// </summary>
        public long SizeBytes { get; init; }

        /// <summary>
        /// When the item was created.
        /// </summary>
        public DateTime CreatedAt { get; init; }

        /// <summary>
        /// When the item was last modified.
        /// </summary>
        public DateTime ModifiedAt { get; init; }

        /// <summary>
        /// Content type (MIME type) if known.
        /// </summary>
        public string? ContentType { get; init; }

        /// <summary>
        /// Item metadata.
        /// </summary>
        public IDictionary<string, string>? Metadata { get; init; }
    }
}