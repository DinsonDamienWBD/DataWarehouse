using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace DataWarehouse.SDK.Contracts.Persistence
{
    /// <summary>
    /// Serializable model for a plugin state entry.
    /// Wraps raw state data with metadata for storage routing.
    /// </summary>
    public sealed class PluginStateEntry
    {
        /// <summary>
        /// The key identifying this state entry within a plugin's namespace.
        /// </summary>
        [JsonPropertyName("key")]
        public string Key { get; init; } = string.Empty;

        /// <summary>
        /// The raw binary payload of this state entry.
        /// Plugins are responsible for serializing/deserializing their domain objects.
        /// </summary>
        [JsonPropertyName("data")]
        public byte[] Data { get; init; } = Array.Empty<byte>();

        /// <summary>
        /// MIME content type of the <see cref="Data"/> payload.
        /// Defaults to <c>application/json</c>. Use <c>application/octet-stream</c>
        /// for raw binary data.
        /// </summary>
        [JsonPropertyName("contentType")]
        public string ContentType { get; init; } = "application/json";

        /// <summary>
        /// UTC timestamp of the last modification.
        /// Set by the storage layer on every write.
        /// </summary>
        [JsonPropertyName("lastModified")]
        public DateTime LastModified { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Arbitrary string metadata attached to the entry.
        /// Useful for versioning, tagging, or routing hints.
        /// </summary>
        [JsonPropertyName("metadata")]
        public Dictionary<string, string> Metadata { get; init; } = new();
    }
}
