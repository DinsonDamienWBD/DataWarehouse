namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Scale.LsmTree
{
    /// <summary>
    /// Represents a write-ahead log entry.
    /// </summary>
    public record WalEntry(byte[] Key, byte[]? Value, WalOp Op);

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
