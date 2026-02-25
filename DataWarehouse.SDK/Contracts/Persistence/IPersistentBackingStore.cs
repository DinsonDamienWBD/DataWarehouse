using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Persistence
{
    /// <summary>
    /// Backend contract for persistent state storage.
    /// Implementations back <see cref="DefaultPluginStateStore"/> with a concrete medium
    /// (filesystem, database, distributed cache, object store, etc.).
    /// <para>
    /// This interface operates on raw paths in the <c>dw://</c> scheme.
    /// The path format used by the plugin state subsystem is:
    /// <c>dw://internal/plugin-state/{pluginId}/{key}</c>
    /// </para>
    /// <para>
    /// Implementations registered with the message bus will receive routed requests
    /// from <see cref="DefaultPluginStateStore"/> via the topics
    /// <c>dw.internal.plugin-state.*</c>.
    /// </para>
    /// </summary>
    public interface IPersistentBackingStore
    {
        /// <summary>
        /// Writes raw data to the given path.
        /// Overwrites any existing data at that path.
        /// </summary>
        /// <param name="path">Fully qualified storage path (e.g., <c>dw://internal/plugin-state/myPlugin/config</c>).</param>
        /// <param name="data">Raw binary payload.</param>
        /// <param name="ct">Cancellation token.</param>
        Task WriteAsync(string path, byte[] data, CancellationToken ct = default);

        /// <summary>
        /// Reads raw data from the given path.
        /// Returns <c>null</c> if no data exists at that path.
        /// </summary>
        /// <param name="path">Fully qualified storage path.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Stored bytes, or <c>null</c> if not found.</returns>
        Task<byte[]?> ReadAsync(string path, CancellationToken ct = default);

        /// <summary>
        /// Deletes the data at the given path.
        /// No-ops silently if the path does not exist.
        /// </summary>
        /// <param name="path">Fully qualified storage path.</param>
        /// <param name="ct">Cancellation token.</param>
        Task DeleteAsync(string path, CancellationToken ct = default);

        /// <summary>
        /// Lists all paths that begin with the given prefix.
        /// Returns an empty collection when no matches exist.
        /// </summary>
        /// <param name="prefix">Path prefix to match (e.g., <c>dw://internal/plugin-state/myPlugin/</c>).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Read-only list of matching paths.</returns>
        Task<IReadOnlyList<string>> ListAsync(string prefix, CancellationToken ct = default);

        /// <summary>
        /// Checks whether data exists at the given path.
        /// </summary>
        /// <param name="path">Fully qualified storage path.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns><c>true</c> if data exists at <paramref name="path"/>; <c>false</c> otherwise.</returns>
        Task<bool> ExistsAsync(string path, CancellationToken ct = default);
    }
}
