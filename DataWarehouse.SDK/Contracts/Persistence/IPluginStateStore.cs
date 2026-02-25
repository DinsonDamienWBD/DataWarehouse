using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Persistence
{
    /// <summary>
    /// Contract for plugin state storage.
    /// Plugins use this interface to persist and retrieve their internal state
    /// across restarts without knowing which storage backend is active.
    /// <para>
    /// All operations are scoped to a <c>pluginId</c> namespace so that multiple
    /// plugins share the same underlying store without key collisions.
    /// State paths follow the convention:
    /// <c>dw://internal/plugin-state/{pluginId}/{key}</c>
    /// </para>
    /// </summary>
    public interface IPluginStateStore
    {
        /// <summary>
        /// Persists raw state data for the given plugin and key.
        /// Overwrites any previously stored value.
        /// </summary>
        /// <param name="pluginId">Unique identifier of the owning plugin.</param>
        /// <param name="key">State key within the plugin's namespace.</param>
        /// <param name="data">Raw binary payload to store.</param>
        /// <param name="ct">Cancellation token.</param>
        Task SaveAsync(string pluginId, string key, byte[] data, CancellationToken ct = default);

        /// <summary>
        /// Retrieves raw state data for the given plugin and key.
        /// Returns <c>null</c> if the key does not exist.
        /// </summary>
        /// <param name="pluginId">Unique identifier of the owning plugin.</param>
        /// <param name="key">State key within the plugin's namespace.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Stored bytes, or <c>null</c> if not found.</returns>
        Task<byte[]?> LoadAsync(string pluginId, string key, CancellationToken ct = default);

        /// <summary>
        /// Removes the state entry for the given plugin and key.
        /// No-ops silently if the key does not exist.
        /// </summary>
        /// <param name="pluginId">Unique identifier of the owning plugin.</param>
        /// <param name="key">State key to remove.</param>
        /// <param name="ct">Cancellation token.</param>
        Task DeleteAsync(string pluginId, string key, CancellationToken ct = default);

        /// <summary>
        /// Lists all state keys registered under the given plugin.
        /// Returns an empty collection when no keys exist.
        /// </summary>
        /// <param name="pluginId">Unique identifier of the owning plugin.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Read-only list of keys in the plugin's namespace.</returns>
        Task<IReadOnlyList<string>> ListKeysAsync(string pluginId, CancellationToken ct = default);

        /// <summary>
        /// Checks whether a state entry exists for the given plugin and key.
        /// </summary>
        /// <param name="pluginId">Unique identifier of the owning plugin.</param>
        /// <param name="key">State key within the plugin's namespace.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns><c>true</c> if the key exists; <c>false</c> otherwise.</returns>
        Task<bool> ExistsAsync(string pluginId, string key, CancellationToken ct = default);

        /// <summary>
        /// Builds the canonical storage path for a plugin state entry.
        /// Format: <c>dw://internal/plugin-state/{pluginId}/{key}</c>
        /// </summary>
        /// <param name="pluginId">Unique identifier of the owning plugin.</param>
        /// <param name="key">State key within the plugin's namespace.</param>
        /// <returns>Fully qualified path string.</returns>
        static string BuildPath(string pluginId, string key) =>
            $"dw://internal/plugin-state/{pluginId}/{key}";
    }
}
