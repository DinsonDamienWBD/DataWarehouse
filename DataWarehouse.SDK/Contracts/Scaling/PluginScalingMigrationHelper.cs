// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using DataWarehouse.SDK.Contracts.Persistence;

namespace DataWarehouse.SDK.Contracts.Scaling
{
    /// <summary>
    /// Plugin type classification for default cache sizing heuristics.
    /// Used by <see cref="PluginScalingMigrationHelper"/> to determine appropriate
    /// default cache sizes when no explicit <see cref="ScalingLimits"/> are configured.
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 88-13: Plugin type classification for cache sizing")]
    public enum PluginCategory
    {
        /// <summary>Storage plugins (e.g., UltimateStorage, UltimateBlockchain). Default: 100K entries.</summary>
        Storage,

        /// <summary>Security plugins (e.g., UltimateEncryption, UltimateAccessControl). Default: 50K entries.</summary>
        Security,

        /// <summary>Data intelligence plugins (e.g., UltimateIntelligence, UltimateDataLineage). Default: 100K entries.</summary>
        DataIntelligence,

        /// <summary>Compute plugins (e.g., UltimateCompute, UltimateServerless). Default: 10K entries.</summary>
        Compute,

        /// <summary>All other plugins. Default: 50K entries.</summary>
        General
    }

    /// <summary>
    /// Result of a <see cref="PluginScalingMigrationHelper.MigrateConcurrentDictionary{TKey,TValue}"/> operation.
    /// Captures migration audit trail information for diagnostics.
    /// </summary>
    /// <param name="PluginId">The plugin that was migrated.</param>
    /// <param name="StoreName">The name of the entity store that was migrated.</param>
    /// <param name="EntriesMigrated">Number of entries copied from the source dictionary.</param>
    /// <param name="ElapsedMilliseconds">Wall-clock time for the migration in milliseconds.</param>
    /// <param name="Timestamp">UTC timestamp when the migration completed.</param>
    [SdkCompatibility("6.0.0", Notes = "Phase 88-13: Migration audit trail record")]
    public record MigrationAuditEntry(
        string PluginId,
        string StoreName,
        int EntriesMigrated,
        long ElapsedMilliseconds,
        DateTime Timestamp);

    /// <summary>
    /// Reusable helper that standardizes the migration of unbounded <see cref="ConcurrentDictionary{TKey,TValue}"/>
    /// entity stores to <see cref="BoundedCache{TKey,TValue}"/> with write-through to
    /// <see cref="IPersistentBackingStore"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This helper provides factory methods for creating pre-configured <see cref="BoundedCache{TKey,TValue}"/>
    /// instances with sensible defaults per plugin type, a hot-migration method for copying entries from
    /// existing <see cref="ConcurrentDictionary{TKey,TValue}"/> instances, and default JSON serialization
    /// helpers for backing store integration.
    /// </para>
    /// <para>
    /// <b>Default sizing by plugin type:</b>
    /// <list type="bullet">
    /// <item><description><see cref="PluginCategory.Storage"/>: 100,000 entries</description></item>
    /// <item><description><see cref="PluginCategory.Security"/>: 50,000 entries</description></item>
    /// <item><description><see cref="PluginCategory.DataIntelligence"/>: 100,000 entries</description></item>
    /// <item><description><see cref="PluginCategory.Compute"/>: 10,000 entries</description></item>
    /// <item><description><see cref="PluginCategory.General"/>: 50,000 entries</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// All created caches use LRU eviction by default, write-through persistence via
    /// <see cref="IPersistentBackingStore"/> when a backing store is provided, and
    /// JSON serialization for <c>byte[]</c> conversion.
    /// </para>
    /// </remarks>
    [SdkCompatibility("6.0.0", Notes = "Phase 88-13: Universal plugin migration helper")]
    public static class PluginScalingMigrationHelper
    {
        private static readonly ConcurrentBag<MigrationAuditEntry> _auditLog = new();
        private static readonly JsonSerializerOptions _jsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        /// <summary>
        /// Gets default max entries for the specified plugin category.
        /// </summary>
        /// <param name="category">The plugin category.</param>
        /// <returns>Default max entry count for the category.</returns>
        public static int GetDefaultMaxEntries(PluginCategory category) => category switch
        {
            PluginCategory.Storage => 100_000,
            PluginCategory.Security => 50_000,
            PluginCategory.DataIntelligence => 100_000,
            PluginCategory.Compute => 10_000,
            _ => 50_000
        };

        /// <summary>
        /// Creates a pre-configured <see cref="BoundedCache{TKey,TValue}"/> for an entity store
        /// with write-through persistence, LRU eviction, and default sizing based on plugin type.
        /// </summary>
        /// <typeparam name="TKey">Entity key type. Must be non-null.</typeparam>
        /// <typeparam name="TValue">Entity value type.</typeparam>
        /// <param name="pluginId">The unique identifier of the plugin owning this store.</param>
        /// <param name="storeName">A human-readable name for the store (used in backing store path and audit logs).</param>
        /// <param name="options">
        /// Optional pre-built options. When <c>null</c>, defaults are computed from the plugin ID
        /// and a <see cref="PluginCategory.General"/> sizing heuristic.
        /// </param>
        /// <returns>A fully configured <see cref="BoundedCache{TKey,TValue}"/>.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="pluginId"/> or <paramref name="storeName"/> is null.</exception>
        public static BoundedCache<TKey, TValue> CreateBoundedEntityStore<TKey, TValue>(
            string pluginId,
            string storeName,
            BoundedCacheOptions<TKey, TValue>? options = null) where TKey : notnull
        {
            ArgumentNullException.ThrowIfNull(pluginId);
            ArgumentNullException.ThrowIfNull(storeName);

            if (options == null)
            {
                options = new BoundedCacheOptions<TKey, TValue>
                {
                    MaxEntries = GetDefaultMaxEntries(PluginCategory.General),
                    EvictionPolicy = CacheEvictionMode.LRU,
                    BackingStorePath = $"dw://cache/{pluginId}/{storeName}",
                    KeyToString = static k => k?.ToString() ?? "null",
                    Serializer = static v => JsonSerializer.SerializeToUtf8Bytes(v, _jsonOptions),
                    Deserializer = static b => JsonSerializer.Deserialize<TValue>(b, _jsonOptions)!,
                    WriteThrough = true
                };
            }
            else
            {
                // Ensure backing store path is set if not provided
                options.BackingStorePath ??= $"dw://cache/{pluginId}/{storeName}";
                options.KeyToString ??= static k => k?.ToString() ?? "null";
                options.Serializer ??= static v => JsonSerializer.SerializeToUtf8Bytes(v, _jsonOptions);
                options.Deserializer ??= static b => JsonSerializer.Deserialize<TValue>(b, _jsonOptions)!;
            }

            return new BoundedCache<TKey, TValue>(options);
        }

        /// <summary>
        /// Creates a pre-configured <see cref="BoundedCache{TKey,TValue}"/> with an explicit
        /// maximum entry count and eviction mode.
        /// </summary>
        /// <typeparam name="TKey">Entity key type. Must be non-null.</typeparam>
        /// <typeparam name="TValue">Entity value type.</typeparam>
        /// <param name="pluginId">The unique identifier of the plugin owning this store.</param>
        /// <param name="storeName">A human-readable name for the store.</param>
        /// <param name="maxEntries">Maximum number of entries before eviction.</param>
        /// <param name="mode">Eviction policy. Default: <see cref="CacheEvictionMode.LRU"/>.</param>
        /// <returns>A fully configured <see cref="BoundedCache{TKey,TValue}"/>.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="pluginId"/> or <paramref name="storeName"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxEntries"/> is less than 1.</exception>
        public static BoundedCache<TKey, TValue> CreateBoundedEntityStore<TKey, TValue>(
            string pluginId,
            string storeName,
            int maxEntries,
            CacheEvictionMode mode = CacheEvictionMode.LRU) where TKey : notnull
        {
            ArgumentNullException.ThrowIfNull(pluginId);
            ArgumentNullException.ThrowIfNull(storeName);
            if (maxEntries < 1)
                throw new ArgumentOutOfRangeException(nameof(maxEntries), "Must be at least 1.");

            var options = new BoundedCacheOptions<TKey, TValue>
            {
                MaxEntries = maxEntries,
                EvictionPolicy = mode,
                BackingStorePath = $"dw://cache/{pluginId}/{storeName}",
                KeyToString = static k => k?.ToString() ?? "null",
                Serializer = static v => JsonSerializer.SerializeToUtf8Bytes(v, _jsonOptions),
                Deserializer = static b => JsonSerializer.Deserialize<TValue>(b, _jsonOptions)!,
                WriteThrough = true
            };

            return new BoundedCache<TKey, TValue>(options);
        }

        /// <summary>
        /// Creates a pre-configured <see cref="BoundedCache{TKey,TValue}"/> with sizing
        /// determined by the plugin category.
        /// </summary>
        /// <typeparam name="TKey">Entity key type. Must be non-null.</typeparam>
        /// <typeparam name="TValue">Entity value type.</typeparam>
        /// <param name="pluginId">The unique identifier of the plugin owning this store.</param>
        /// <param name="storeName">A human-readable name for the store.</param>
        /// <param name="category">The plugin category for default sizing.</param>
        /// <param name="backingStore">Optional persistent backing store for write-through.</param>
        /// <returns>A fully configured <see cref="BoundedCache{TKey,TValue}"/>.</returns>
        public static BoundedCache<TKey, TValue> CreateBoundedEntityStore<TKey, TValue>(
            string pluginId,
            string storeName,
            PluginCategory category,
            IPersistentBackingStore? backingStore = null) where TKey : notnull
        {
            ArgumentNullException.ThrowIfNull(pluginId);
            ArgumentNullException.ThrowIfNull(storeName);

            var options = new BoundedCacheOptions<TKey, TValue>
            {
                MaxEntries = GetDefaultMaxEntries(category),
                EvictionPolicy = CacheEvictionMode.LRU,
                BackingStore = backingStore,
                BackingStorePath = $"dw://cache/{pluginId}/{storeName}",
                KeyToString = static k => k?.ToString() ?? "null",
                Serializer = static v => JsonSerializer.SerializeToUtf8Bytes(v, _jsonOptions),
                Deserializer = static b => JsonSerializer.Deserialize<TValue>(b, _jsonOptions)!,
                WriteThrough = backingStore != null
            };

            return new BoundedCache<TKey, TValue>(options);
        }

        /// <summary>
        /// Copies all entries from an existing <see cref="ConcurrentDictionary{TKey,TValue}"/> into
        /// a <see cref="BoundedCache{TKey,TValue}"/>. Intended for hot migration during plugin
        /// initialization when transitioning from unbounded to bounded storage.
        /// </summary>
        /// <typeparam name="TKey">Entity key type. Must be non-null.</typeparam>
        /// <typeparam name="TValue">Entity value type.</typeparam>
        /// <param name="source">The unbounded dictionary to migrate from.</param>
        /// <param name="target">The bounded cache to migrate into.</param>
        /// <param name="pluginId">Plugin ID for audit logging. When <c>null</c>, audit logging is skipped.</param>
        /// <param name="storeName">Store name for audit logging. When <c>null</c>, audit logging is skipped.</param>
        /// <returns>A <see cref="MigrationAuditEntry"/> containing migration metrics.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="source"/> or <paramref name="target"/> is null.</exception>
        public static MigrationAuditEntry MigrateConcurrentDictionary<TKey, TValue>(
            ConcurrentDictionary<TKey, TValue> source,
            BoundedCache<TKey, TValue> target,
            string? pluginId = null,
            string? storeName = null) where TKey : notnull
        {
            ArgumentNullException.ThrowIfNull(source);
            ArgumentNullException.ThrowIfNull(target);

            var sw = Stopwatch.StartNew();
            int count = 0;

            foreach (var kvp in source)
            {
                target.Put(kvp.Key, kvp.Value);
                count++;
            }

            sw.Stop();

            var entry = new MigrationAuditEntry(
                pluginId ?? "unknown",
                storeName ?? "unknown",
                count,
                sw.ElapsedMilliseconds,
                DateTime.UtcNow);

            _auditLog.Add(entry);

            return entry;
        }

        /// <summary>
        /// Gets all migration audit entries recorded since the process started.
        /// </summary>
        /// <returns>A read-only list of all migration audit entries.</returns>
        public static IReadOnlyList<MigrationAuditEntry> GetAuditLog()
        {
            return _auditLog.ToArray();
        }

        /// <summary>
        /// Clears the migration audit log. Primarily intended for testing.
        /// </summary>
        public static void ClearAuditLog()
        {
            while (_auditLog.TryTake(out _)) { }
        }

        /// <summary>
        /// Default JSON serializer for converting values to <c>byte[]</c>.
        /// Can be used as the <see cref="BoundedCacheOptions{TKey,TValue}.Serializer"/>
        /// for performance-critical paths that need explicit control.
        /// </summary>
        /// <typeparam name="TValue">The value type to serialize.</typeparam>
        /// <param name="value">The value to serialize.</param>
        /// <returns>UTF-8 JSON bytes.</returns>
        public static byte[] DefaultSerialize<TValue>(TValue value)
        {
            return JsonSerializer.SerializeToUtf8Bytes(value, _jsonOptions);
        }

        /// <summary>
        /// Default JSON deserializer for converting <c>byte[]</c> back to values.
        /// Can be used as the <see cref="BoundedCacheOptions{TKey,TValue}.Deserializer"/>
        /// for performance-critical paths that need explicit control.
        /// </summary>
        /// <typeparam name="TValue">The value type to deserialize.</typeparam>
        /// <param name="data">The UTF-8 JSON bytes to deserialize.</param>
        /// <returns>The deserialized value.</returns>
        public static TValue DefaultDeserialize<TValue>(byte[] data)
        {
            return JsonSerializer.Deserialize<TValue>(data, _jsonOptions)
                ?? throw new InvalidOperationException(
                    $"Deserialization returned null for type {typeof(TValue).FullName}. " +
                    "Ensure the JSON is valid and matches the expected type.");
        }
    }
}
