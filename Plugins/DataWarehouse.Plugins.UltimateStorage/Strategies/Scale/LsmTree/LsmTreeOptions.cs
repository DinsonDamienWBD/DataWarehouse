using System;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Scale.LsmTree
{
    /// <summary>
    /// Configuration options for the LSM-Tree engine.
    /// </summary>
    public sealed record LsmTreeOptions
    {
        private long _memTableMaxSize = 4 * 1024 * 1024;
        private int _level0CompactionThreshold = 4;
        private int _maxLevels = 7;
        private int _blockSize = 4096;

        /// <summary>
        /// Maximum size of MemTable in bytes before flushing to disk (default 4MB).
        /// Must be greater than zero.
        /// </summary>
        public long MemTableMaxSize
        {
            get => _memTableMaxSize;
            init
            {
                if (value <= 0)
                    throw new ArgumentOutOfRangeException(nameof(MemTableMaxSize), value,
                        "MemTableMaxSize must be greater than zero.");
                _memTableMaxSize = value;
            }
        }

        /// <summary>
        /// Number of Level 0 SSTables before triggering compaction (default 4).
        /// Must be at least 1.
        /// </summary>
        public int Level0CompactionThreshold
        {
            get => _level0CompactionThreshold;
            init
            {
                if (value < 1)
                    throw new ArgumentOutOfRangeException(nameof(Level0CompactionThreshold), value,
                        "Level0CompactionThreshold must be at least 1.");
                _level0CompactionThreshold = value;
            }
        }

        /// <summary>
        /// Maximum number of levels in the LSM-Tree (default 7).
        /// Must be at least 2.
        /// </summary>
        public int MaxLevels
        {
            get => _maxLevels;
            init
            {
                if (value < 2)
                    throw new ArgumentOutOfRangeException(nameof(MaxLevels), value,
                        "MaxLevels must be at least 2.");
                _maxLevels = value;
            }
        }

        /// <summary>
        /// Block size for SSTable data blocks in bytes (default 4096).
        /// Must be a positive power of two between 512 and 1MB.
        /// </summary>
        public int BlockSize
        {
            get => _blockSize;
            init
            {
                if (value <= 0 || (value & (value - 1)) != 0 || value < 512 || value > 1024 * 1024)
                    throw new ArgumentOutOfRangeException(nameof(BlockSize), value,
                        "BlockSize must be a positive power of two between 512 and 1048576 (1MB).");
                _blockSize = value;
            }
        }

        /// <summary>
        /// Whether to enable background compaction (default true).
        /// </summary>
        public bool EnableBackgroundCompaction { get; init; } = true;
    }
}
