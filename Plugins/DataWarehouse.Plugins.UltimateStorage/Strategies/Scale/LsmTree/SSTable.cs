using System;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Scale.LsmTree
{
    /// <summary>
    /// Metadata for a Sorted String Table (SSTable) file.
    /// </summary>
    public sealed record SSTable
    {
        /// <summary>
        /// Level in the LSM-Tree (0 = most recent, higher = older/compacted).
        /// </summary>
        public int Level { get; init; }

        /// <summary>
        /// Absolute file path to the SSTable.
        /// </summary>
        public string FilePath { get; init; } = string.Empty;

        /// <summary>
        /// Number of entries in the SSTable.
        /// </summary>
        public long EntryCount { get; init; }

        /// <summary>
        /// Creation timestamp.
        /// </summary>
        public DateTime CreatedAt { get; init; }

        /// <summary>
        /// File size in bytes.
        /// </summary>
        public long FileSize { get; init; }

        /// <summary>
        /// First key in the SSTable (for range queries).
        /// </summary>
        public byte[]? FirstKey { get; init; }

        /// <summary>
        /// Last key in the SSTable (for range queries).
        /// </summary>
        public byte[]? LastKey { get; init; }
    }
}
