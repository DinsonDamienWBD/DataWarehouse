using System;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Scale.LsmTree
{
    /// <summary>
    /// Represents a write-ahead log entry.
    /// </summary>
    public record WalEntry
    {
        /// <summary>The key for this WAL entry. Must not be null.</summary>
        public byte[] Key { get; }

        /// <summary>The value for this WAL entry. Null represents a tombstone (delete).</summary>
        public byte[]? Value { get; }

        /// <summary>The operation type.</summary>
        public WalOp Op { get; }

        /// <summary>
        /// Initializes a new WAL entry.
        /// </summary>
        /// <param name="key">Entry key. Must not be null.</param>
        /// <param name="value">Entry value or null for tombstone.</param>
        /// <param name="op">Operation type.</param>
        public WalEntry(byte[] key, byte[]? value, WalOp op)
        {
            Key = key ?? throw new ArgumentNullException(nameof(key), "WalEntry key must not be null.");
            Value = value;
            Op = op;
        }
    };

    /// <summary>
    /// Write-ahead log operation types.
    /// </summary>
    public enum WalOp : byte
    {
        /// <summary>Put or update operation.</summary>
        Put = 1,

        /// <summary>Delete operation (tombstone).</summary>
        Delete = 2
    }
}
