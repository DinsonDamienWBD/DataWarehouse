using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateConnector.Strategies.CrossCutting
{
    /// <summary>
    /// Manages tags and groupings for connection handles, enabling organization by
    /// environment (dev/staging/prod), tenant, region, or arbitrary custom tags.
    /// Supports efficient querying of connections by tag intersection.
    /// </summary>
    /// <remarks>
    /// Tags are stored in a <see cref="ConcurrentDictionary{TKey,TValue}"/> keyed by
    /// connection ID, providing thread-safe access. Each connection can have multiple
    /// tags in key-value format. Tag queries support single-key lookup, multi-key
    /// intersection, and wildcard prefix matching.
    /// </remarks>
    public sealed class ConnectionTagManager
    {
        private readonly BoundedDictionary<string, ConnectionTagSet> _tags = new BoundedDictionary<string, ConnectionTagSet>(1000);

        /// <summary>
        /// Well-known tag keys for standard connection categorization.
        /// </summary>
        public static class WellKnownTags
        {
            /// <summary>Deployment environment (dev, staging, prod).</summary>
            public const string Environment = "env";
            /// <summary>Tenant identifier for multi-tenant scenarios.</summary>
            public const string Tenant = "tenant";
            /// <summary>Geographic region (us-east-1, eu-west-1).</summary>
            public const string Region = "region";
            /// <summary>Strategy identifier.</summary>
            public const string Strategy = "strategy";
            /// <summary>Connection priority (high, normal, low).</summary>
            public const string Priority = "priority";
            /// <summary>Owning team or service name.</summary>
            public const string Owner = "owner";
            /// <summary>Application or service identifier.</summary>
            public const string Application = "app";
        }

        /// <summary>
        /// Sets tags on a connection, creating the tag set if it does not exist.
        /// Existing tags with the same keys are overwritten.
        /// </summary>
        /// <param name="connectionId">Connection identifier.</param>
        /// <param name="tags">Tags to set as key-value pairs.</param>
        public void SetTags(string connectionId, Dictionary<string, string> tags)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
            ArgumentNullException.ThrowIfNull(tags);

            var tagSet = _tags.GetOrAdd(connectionId, _ => new ConnectionTagSet(connectionId));

            foreach (var (key, value) in tags)
                tagSet.Tags[key] = value;

            tagSet.LastModified = DateTimeOffset.UtcNow;
        }

        /// <summary>
        /// Sets a single tag on a connection.
        /// </summary>
        /// <param name="connectionId">Connection identifier.</param>
        /// <param name="key">Tag key.</param>
        /// <param name="value">Tag value.</param>
        public void SetTag(string connectionId, string key, string value)
        {
            var tagSet = _tags.GetOrAdd(connectionId, _ => new ConnectionTagSet(connectionId));
            tagSet.Tags[key] = value;
            tagSet.LastModified = DateTimeOffset.UtcNow;
        }

        /// <summary>
        /// Removes a tag from a connection.
        /// </summary>
        /// <param name="connectionId">Connection identifier.</param>
        /// <param name="key">Tag key to remove.</param>
        /// <returns>True if the tag was removed.</returns>
        public bool RemoveTag(string connectionId, string key)
        {
            if (!_tags.TryGetValue(connectionId, out var tagSet))
                return false;

            var removed = tagSet.Tags.TryRemove(key, out _);
            if (removed) tagSet.LastModified = DateTimeOffset.UtcNow;
            return removed;
        }

        /// <summary>
        /// Removes all tags for a connection.
        /// </summary>
        /// <param name="connectionId">Connection identifier.</param>
        /// <returns>True if the connection's tags were removed.</returns>
        public bool RemoveConnection(string connectionId) =>
            _tags.TryRemove(connectionId, out _);

        /// <summary>
        /// Gets all tags for a specific connection.
        /// </summary>
        /// <param name="connectionId">Connection identifier.</param>
        /// <returns>Read-only dictionary of tags, or empty if the connection is not tagged.</returns>
        public IReadOnlyDictionary<string, string> GetTags(string connectionId)
        {
            if (!_tags.TryGetValue(connectionId, out var tagSet))
                return new Dictionary<string, string>();

            return new Dictionary<string, string>(tagSet.Tags);
        }

        /// <summary>
        /// Queries connection IDs that have a specific tag key and value.
        /// </summary>
        /// <param name="key">Tag key to match.</param>
        /// <param name="value">Tag value to match.</param>
        /// <returns>Connection IDs matching the tag filter.</returns>
        public IReadOnlyList<string> QueryByTag(string key, string value)
        {
            return _tags
                .Where(kvp => kvp.Value.Tags.TryGetValue(key, out var v)
                    && v.Equals(value, StringComparison.OrdinalIgnoreCase))
                .Select(kvp => kvp.Key)
                .ToList();
        }

        /// <summary>
        /// Queries connection IDs that match all specified tag filters (intersection).
        /// </summary>
        /// <param name="filters">Tag filters as key-value pairs. All must match.</param>
        /// <returns>Connection IDs matching all tag filters.</returns>
        public IReadOnlyList<string> QueryByTags(Dictionary<string, string> filters)
        {
            ArgumentNullException.ThrowIfNull(filters);

            if (filters.Count == 0)
                return _tags.Keys.ToList();

            return _tags
                .Where(kvp => filters.All(f =>
                    kvp.Value.Tags.TryGetValue(f.Key, out var v)
                    && v.Equals(f.Value, StringComparison.OrdinalIgnoreCase)))
                .Select(kvp => kvp.Key)
                .ToList();
        }

        /// <summary>
        /// Queries connection IDs that have a tag key with a value starting with the given prefix.
        /// </summary>
        /// <param name="key">Tag key to match.</param>
        /// <param name="valuePrefix">Tag value prefix to match.</param>
        /// <returns>Connection IDs matching the prefix filter.</returns>
        public IReadOnlyList<string> QueryByTagPrefix(string key, string valuePrefix)
        {
            return _tags
                .Where(kvp => kvp.Value.Tags.TryGetValue(key, out var v)
                    && v.StartsWith(valuePrefix, StringComparison.OrdinalIgnoreCase))
                .Select(kvp => kvp.Key)
                .ToList();
        }

        /// <summary>
        /// Groups connections by a specific tag key.
        /// </summary>
        /// <param name="key">Tag key to group by.</param>
        /// <returns>Dictionary mapping tag values to lists of connection IDs.</returns>
        public Dictionary<string, List<string>> GroupByTag(string key)
        {
            var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var (connectionId, tagSet) in _tags)
            {
                if (tagSet.Tags.TryGetValue(key, out var value))
                {
                    if (!groups.TryGetValue(value, out var list))
                    {
                        list = new List<string>();
                        groups[value] = list;
                    }
                    list.Add(connectionId);
                }
            }

            return groups;
        }

        /// <summary>
        /// Returns the total number of tagged connections.
        /// </summary>
        public int Count => _tags.Count;

        /// <summary>
        /// Returns a summary of all tags across all connections.
        /// </summary>
        /// <returns>Dictionary mapping tag keys to their distinct values and counts.</returns>
        public Dictionary<string, Dictionary<string, int>> GetTagSummary()
        {
            var summary = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

            foreach (var (_, tagSet) in _tags)
            {
                foreach (var (key, value) in tagSet.Tags)
                {
                    if (!summary.TryGetValue(key, out var valueCounts))
                    {
                        valueCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        summary[key] = valueCounts;
                    }

                    valueCounts[value] = valueCounts.GetValueOrDefault(value) + 1;
                }
            }

            return summary;
        }

        /// <summary>
        /// Internal tag set for a single connection.
        /// </summary>
        private sealed class ConnectionTagSet
        {
            public string ConnectionId { get; }
            public BoundedDictionary<string, string> Tags { get; } = new BoundedDictionary<string, string>(1000);
            public DateTimeOffset CreatedAt { get; }
            public DateTimeOffset LastModified { get; set; }

            public ConnectionTagSet(string connectionId)
            {
                ConnectionId = connectionId;
                CreatedAt = DateTimeOffset.UtcNow;
                LastModified = CreatedAt;
            }
        }
    }
}
