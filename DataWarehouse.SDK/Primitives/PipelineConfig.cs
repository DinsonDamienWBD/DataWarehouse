using System;
using System.Collections.Generic;
using System.Text;

namespace DataWarehouse.SDK.Primitives
{
    /// <summary>
    /// Pipeline configuration
    /// </summary>
    public class PipelineConfig
    {
        /// <summary>
        /// Enable encryption
        /// </summary>
        public bool EnableEncryption { get; set; }

        /// <summary>
        /// Crypto provider ID
        /// </summary>
        public string CryptoProviderId { get; set; } = string.Empty;

        /// <summary>
        /// Key ID
        /// </summary>
        public string KeyId { get; set; } = string.Empty;

        /// <summary>
        /// Enable compression
        /// </summary>
        public bool EnableCompression { get; set; }

        /// <summary>
        /// Compression provider ID
        /// </summary>
        public string CompressionProviderId { get; set; } = string.Empty;

        /// <summary>
        /// Order of operations (e.g. Compress -> Encrypt)
        /// </summary>
        public List<string> TransformationOrder { get; set; } = [];

        /// <summary>
        /// Snapshot of pipeline stages that were executed when this blob was written.
        /// Used on read to reconstruct the exact reverse pipeline.
        /// </summary>
        public List<DataWarehouse.SDK.Contracts.Pipeline.PipelineStageSnapshot> ExecutedStages { get; set; } = new();

        /// <summary>
        /// Policy ID that was active when this blob was written.
        /// </summary>
        public string PolicyId { get; set; } = string.Empty;

        /// <summary>
        /// Policy version that was active when this blob was written.
        /// Used to detect stale blobs that need migration.
        /// </summary>
        public long PolicyVersion { get; set; }

        /// <summary>
        /// When this blob's pipeline was last applied.
        /// </summary>
        public DateTimeOffset WrittenAt { get; set; } = DateTimeOffset.UtcNow;
    }
}
