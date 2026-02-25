using DataWarehouse.SDK.Utilities;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Persistence
{
    /// <summary>
    /// Default implementation of <see cref="IPluginStateStore"/> that routes all storage
    /// operations through the message bus to whatever storage backend is currently configured.
    /// <para>
    /// Topics used:
    /// <list type="table">
    ///   <listheader><term>Topic</term><description>Operation</description></listheader>
    ///   <item><term>dw.internal.plugin-state.write</term><description>Save data</description></item>
    ///   <item><term>dw.internal.plugin-state.read</term><description>Load data</description></item>
    ///   <item><term>dw.internal.plugin-state.delete</term><description>Delete entry</description></item>
    ///   <item><term>dw.internal.plugin-state.list</term><description>List keys</description></item>
    ///   <item><term>dw.internal.plugin-state.exists</term><description>Check existence</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Each message carries a <c>Path</c> key in its payload using the canonical format
    /// <c>dw://internal/plugin-state/{pluginId}/{key}</c>.
    /// </para>
    /// <para>
    /// An optional <see cref="IPersistentBackingStore"/> can be supplied as a direct-access
    /// fallback for environments where the message bus is not yet available (e.g., early boot,
    /// integration tests, or edge scenarios).
    /// </para>
    /// </summary>
    public sealed class DefaultPluginStateStore : IPluginStateStore
    {
        // Message bus topic constants
        private const string TopicWrite  = "dw.internal.plugin-state.write";
        private const string TopicRead   = "dw.internal.plugin-state.read";
        private const string TopicDelete = "dw.internal.plugin-state.delete";
        private const string TopicList   = "dw.internal.plugin-state.list";
        private const string TopicExists = "dw.internal.plugin-state.exists";

        private readonly IMessageBus _messageBus;
        private readonly IPersistentBackingStore? _backingStore;

        /// <summary>
        /// Initializes a new instance of <see cref="DefaultPluginStateStore"/> that routes
        /// all operations through the message bus.
        /// </summary>
        /// <param name="messageBus">
        /// Message bus instance used to route storage operations. Must not be <c>null</c>.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="messageBus"/> is <c>null</c>.
        /// </exception>
        public DefaultPluginStateStore(IMessageBus messageBus)
            : this(messageBus, backingStore: null)
        {
        }

        /// <summary>
        /// Initializes a new instance of <see cref="DefaultPluginStateStore"/> that routes
        /// operations through the message bus and uses <paramref name="backingStore"/> as a
        /// direct fallback when the bus returns no handler or is unavailable.
        /// </summary>
        /// <param name="messageBus">Message bus instance. Must not be <c>null</c>.</param>
        /// <param name="backingStore">
        /// Optional direct backing store used as a fallback.
        /// Pass <c>null</c> to disable the fallback path.
        /// </param>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="messageBus"/> is <c>null</c>.
        /// </exception>
        public DefaultPluginStateStore(IMessageBus messageBus, IPersistentBackingStore? backingStore)
        {
            _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
            _backingStore = backingStore;
        }

        // -----------------------------------------------------------------------
        // IPluginStateStore implementation
        // -----------------------------------------------------------------------

        /// <inheritdoc/>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="pluginId"/> or <paramref name="key"/> is null or whitespace.
        /// </exception>
        /// <exception cref="ArgumentNullException">
        /// Thrown when <paramref name="data"/> is <c>null</c>.
        /// </exception>
        public async Task SaveAsync(string pluginId, string key, byte[] data, CancellationToken ct = default)
        {
            ValidatePluginId(pluginId);
            ValidateKey(key);
            if (data is null) throw new ArgumentNullException(nameof(data));

            var path = IPluginStateStore.BuildPath(pluginId, key);

            var entry = new PluginStateEntry
            {
                Key = key,
                Data = data,
                LastModified = DateTime.UtcNow
            };

            var message = new PluginMessage
            {
                Type = TopicWrite,
                Payload = new Dictionary<string, object>
                {
                    ["Path"]     = path,
                    ["PluginId"] = pluginId,
                    ["Key"]      = key,
                    ["Entry"]    = entry,
                    ["Data"]     = data
                }
            };

            try
            {
                await _messageBus.PublishAndWaitAsync(TopicWrite, message, ct);
            }
            catch (Exception) when (_backingStore != null)
            {
                await _backingStore.WriteAsync(path, data, ct);
            }
        }

        /// <inheritdoc/>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="pluginId"/> or <paramref name="key"/> is null or whitespace.
        /// </exception>
        public async Task<byte[]?> LoadAsync(string pluginId, string key, CancellationToken ct = default)
        {
            ValidatePluginId(pluginId);
            ValidateKey(key);

            var path = IPluginStateStore.BuildPath(pluginId, key);

            var message = new PluginMessage
            {
                Type = TopicRead,
                Payload = new Dictionary<string, object>
                {
                    ["Path"]     = path,
                    ["PluginId"] = pluginId,
                    ["Key"]      = key
                }
            };

            try
            {
                var response = await _messageBus.SendAsync(TopicRead, message, ct);

                if (response.Success)
                {
                    if (response.Payload is byte[] bytes)
                        return bytes;

                    // Handler responded but returned null (key not found)
                    if (response.Payload is null)
                        return null;
                }

                // Bus returned failure or non-byte payload — try fallback
                if (_backingStore != null)
                    return await _backingStore.ReadAsync(path, ct);

                return null;
            }
            catch (Exception) when (_backingStore != null)
            {
                return await _backingStore.ReadAsync(path, ct);
            }
        }

        /// <inheritdoc/>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="pluginId"/> or <paramref name="key"/> is null or whitespace.
        /// </exception>
        public async Task DeleteAsync(string pluginId, string key, CancellationToken ct = default)
        {
            ValidatePluginId(pluginId);
            ValidateKey(key);

            var path = IPluginStateStore.BuildPath(pluginId, key);

            var message = new PluginMessage
            {
                Type = TopicDelete,
                Payload = new Dictionary<string, object>
                {
                    ["Path"]     = path,
                    ["PluginId"] = pluginId,
                    ["Key"]      = key
                }
            };

            try
            {
                await _messageBus.PublishAndWaitAsync(TopicDelete, message, ct);
            }
            catch (Exception) when (_backingStore != null)
            {
                await _backingStore.DeleteAsync(path, ct);
            }
        }

        /// <inheritdoc/>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="pluginId"/> is null or whitespace.
        /// </exception>
        public async Task<IReadOnlyList<string>> ListKeysAsync(string pluginId, CancellationToken ct = default)
        {
            ValidatePluginId(pluginId);

            var prefix = $"dw://internal/plugin-state/{pluginId}/";

            var message = new PluginMessage
            {
                Type = TopicList,
                Payload = new Dictionary<string, object>
                {
                    ["Prefix"]   = prefix,
                    ["PluginId"] = pluginId
                }
            };

            try
            {
                var response = await _messageBus.SendAsync(TopicList, message, ct);

                if (response.Success)
                {
                    if (response.Payload is IReadOnlyList<string> readOnlyList)
                        return readOnlyList;

                    if (response.Payload is string[] arr)
                        return arr;
                }

                // Fallback
                if (_backingStore != null)
                {
                    var paths = await _backingStore.ListAsync(prefix, ct);
                    return ExtractKeysFromPaths(paths, prefix);
                }

                return Array.Empty<string>();
            }
            catch (Exception) when (_backingStore != null)
            {
                var paths = await _backingStore.ListAsync(prefix, ct);
                return ExtractKeysFromPaths(paths, prefix);
            }
        }

        /// <inheritdoc/>
        /// <exception cref="ArgumentException">
        /// Thrown when <paramref name="pluginId"/> or <paramref name="key"/> is null or whitespace.
        /// </exception>
        public async Task<bool> ExistsAsync(string pluginId, string key, CancellationToken ct = default)
        {
            ValidatePluginId(pluginId);
            ValidateKey(key);

            var path = IPluginStateStore.BuildPath(pluginId, key);

            var message = new PluginMessage
            {
                Type = TopicExists,
                Payload = new Dictionary<string, object>
                {
                    ["Path"]     = path,
                    ["PluginId"] = pluginId,
                    ["Key"]      = key
                }
            };

            try
            {
                var response = await _messageBus.SendAsync(TopicExists, message, ct);

                if (response.Success && response.Payload is bool exists)
                    return exists;

                // Fallback
                if (_backingStore != null)
                    return await _backingStore.ExistsAsync(path, ct);

                return false;
            }
            catch (Exception) when (_backingStore != null)
            {
                return await _backingStore.ExistsAsync(path, ct);
            }
        }

        // -----------------------------------------------------------------------
        // Private helpers
        // -----------------------------------------------------------------------

        /// <summary>
        /// Validates a plugin ID argument, throwing <see cref="ArgumentException"/>
        /// when the value is null or consists solely of whitespace.
        /// </summary>
        private static void ValidatePluginId(string pluginId)
        {
            if (string.IsNullOrWhiteSpace(pluginId))
                throw new ArgumentException("Plugin ID must not be null or empty.", nameof(pluginId));
        }

        /// <summary>
        /// Validates a state key argument, throwing <see cref="ArgumentException"/>
        /// when the value is null or consists solely of whitespace.
        /// </summary>
        private static void ValidateKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Key must not be null or empty.", nameof(key));
        }

        /// <summary>
        /// Strips the path prefix from a list of full storage paths and returns only the key
        /// segment for each entry.
        /// </summary>
        private static IReadOnlyList<string> ExtractKeysFromPaths(IReadOnlyList<string> paths, string prefix)
        {
            var keys = new List<string>(paths.Count);
            foreach (var p in paths)
            {
                keys.Add(p.StartsWith(prefix, StringComparison.Ordinal)
                    ? p.Substring(prefix.Length)
                    : p);
            }
            return keys;
        }
    }
}
