using System.Buffers.Binary;
using System.IO.Hashing;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Scale.LsmTree
{
    /// <summary>
    /// Partitions keys for distributed storage using consistent hashing.
    /// </summary>
    public static class MetadataPartitioner
    {
        /// <summary>
        /// Gets the partition index for a given key.
        /// </summary>
        /// <param name="key">Key to partition.</param>
        /// <param name="partitionCount">Total number of partitions.</param>
        /// <returns>Partition index (0 to partitionCount-1).</returns>
        public static int GetPartition(byte[] key, int partitionCount)
        {
            if (partitionCount <= 0)
            {
                return 0;
            }

            var hashBytes = XxHash64.Hash(key);
            var hash = BinaryPrimitives.ReadUInt64LittleEndian(hashBytes);
            return (int)(hash % (ulong)partitionCount);
        }
    }
}
