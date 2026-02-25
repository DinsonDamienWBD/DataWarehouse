namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Scale.LsmTree
{
    /// <summary>
    /// Configuration options for the LSM-Tree engine.
    /// </summary>
    public sealed record LsmTreeOptions
    {
        /// <summary>
        /// Maximum size of MemTable in bytes before flushing to disk (default 4MB).
        /// </summary>
        public long MemTableMaxSize { get; init; } = 4 * 1024 * 1024;

        /// <summary>
        /// Number of Level 0 SSTables before triggering compaction (default 4).
        /// </summary>
        public int Level0CompactionThreshold { get; init; } = 4;

        /// <summary>
        /// Maximum number of levels in the LSM-Tree (default 7).
        /// </summary>
        public int MaxLevels { get; init; } = 7;

        /// <summary>
        /// Block size for SSTable data blocks in bytes (default 4096).
        /// </summary>
        public int BlockSize { get; init; } = 4096;

        /// <summary>
        /// Whether to enable background compaction (default true).
        /// </summary>
        public bool EnableBackgroundCompaction { get; init; } = true;
    }
}
