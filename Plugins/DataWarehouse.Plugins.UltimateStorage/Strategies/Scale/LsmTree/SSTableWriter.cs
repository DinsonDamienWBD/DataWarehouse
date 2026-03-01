using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Scale.LsmTree
{
    /// <summary>
    /// Writes sorted key-value pairs to disk as an SSTable.
    /// Format: [data blocks][index block][bloom filter][footer]
    /// Footer: [index_offset:8][bloom_offset:8][entry_count:8][magic:4]
    /// </summary>
    public sealed class SSTableWriter
    {
        private const uint MagicNumber = 0x53535401; // "SST" + version 1
        private const int BlockSize = 4096;

        /// <summary>
        /// Writes sorted entries to an SSTable file.
        /// </summary>
        /// <param name="sortedEntries">Sorted key-value pairs (null value = tombstone).</param>
        /// <param name="filePath">Output file path.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>SSTable metadata.</returns>
        public static async Task<SSTable> WriteAsync(
            IEnumerable<KeyValuePair<byte[], byte[]?>> sortedEntries,
            string filePath,
            CancellationToken ct = default)
        {
            if (sortedEntries == null)
            {
                throw new ArgumentNullException(nameof(sortedEntries));
            }

            if (string.IsNullOrWhiteSpace(filePath))
            {
                throw new ArgumentException("File path cannot be null or empty", nameof(filePath));
            }

            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var stream = new FileStream(
                filePath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 8192,
                useAsync: true);

            // Materialize once so we know the entry count before building the BloomFilter.
            // This avoids the fixed capacity of 10000 which would over-allocate for small
            // SSTables and under-allocate for large ones.
            var entryList = sortedEntries.ToList();

            // Validate that entries are sorted (ascending byte-lexicographic order).
            // Unsorted input would produce a corrupt index that breaks binary search in SSTableReader.
            for (int i = 1; i < entryList.Count; i++)
            {
                var prev = entryList[i - 1].Key;
                var curr = entryList[i].Key;
                int cmp = ByteArrayComparer.Instance.Compare(prev, curr);
                if (cmp > 0)
                {
                    throw new ArgumentException(
                        $"sortedEntries must be in ascending key order. " +
                        $"Entry at index {i} is less than entry at index {i - 1}.",
                        nameof(sortedEntries));
                }
            }
            var bloomExpectedItems = Math.Max(entryList.Count, 100); // at least 100 to avoid degenerate filter

            var indexEntries = new List<(byte[] firstKey, long offset)>();
            var bloomFilter = new BloomFilter(expectedItems: bloomExpectedItems, falsePositiveRate: 0.01);
            long entryCount = 0;
            byte[]? firstKey = null;
            byte[]? lastKey = null;

            long currentBlockOffset = 0;
            byte[]? currentBlockFirstKey = null;
            var currentBlockBuffer = new MemoryStream(BlockSize);
            try
            {

            foreach (var kvp in entryList)
            {
                ct.ThrowIfCancellationRequested();

                var key = kvp.Key;
                var value = kvp.Value;

                if (firstKey == null)
                {
                    firstKey = key;
                }
                lastKey = key;

                bloomFilter.Add(key);
                entryCount++;

                // Write entry: [key_len:4][key:bytes][value_len:4][value:bytes]
                var keyLen = key.Length;
                var valueLen = value?.Length ?? -1; // -1 for tombstone

                var entrySize = 4 + keyLen + 4 + (value?.Length ?? 0);

                // Check if we need to start a new block
                if (currentBlockBuffer.Length + entrySize > BlockSize && currentBlockBuffer.Length > 0)
                {
                    // Flush current block and dispose the used MemoryStream before creating a new one.
                    await FlushBlockAsync(stream, currentBlockBuffer, currentBlockFirstKey!, indexEntries, currentBlockOffset, ct);
                    currentBlockOffset = stream.Position;
                    await currentBlockBuffer.DisposeAsync();
                    currentBlockBuffer = new MemoryStream(BlockSize);
                    currentBlockFirstKey = null;
                }

                if (currentBlockFirstKey == null)
                {
                    currentBlockFirstKey = key;
                }

                // Write to block buffer
                var keyLenBytes = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(keyLenBytes, keyLen);
                currentBlockBuffer.Write(keyLenBytes, 0, 4);
                currentBlockBuffer.Write(key, 0, keyLen);

                var valueLenBytes = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(valueLenBytes, valueLen);
                currentBlockBuffer.Write(valueLenBytes, 0, 4);

                if (value != null)
                {
                    currentBlockBuffer.Write(value, 0, value.Length);
                }
            }

            // Flush final block
            if (currentBlockBuffer.Length > 0)
            {
                await FlushBlockAsync(stream, currentBlockBuffer, currentBlockFirstKey!, indexEntries, currentBlockOffset, ct);
            }

            } // end try
            finally
            {
                await currentBlockBuffer.DisposeAsync();
            }

            // Write index block
            var indexOffset = stream.Position;
            await WriteIndexBlockAsync(stream, indexEntries, ct);

            // Write bloom filter
            var bloomOffset = stream.Position;
            await bloomFilter.SerializeAsync(stream);

            // Write footer
            var footer = new byte[28];
            BinaryPrimitives.WriteInt64LittleEndian(footer.AsSpan(0, 8), indexOffset);
            BinaryPrimitives.WriteInt64LittleEndian(footer.AsSpan(8, 8), bloomOffset);
            BinaryPrimitives.WriteInt64LittleEndian(footer.AsSpan(16, 8), entryCount);
            BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(24, 4), MagicNumber);
            await stream.WriteAsync(footer, ct);

            await stream.FlushAsync(ct);

            var fileInfo = new FileInfo(filePath);

            return new SSTable
            {
                FilePath = filePath,
                EntryCount = entryCount,
                CreatedAt = DateTime.UtcNow,
                FileSize = fileInfo.Length,
                FirstKey = firstKey,
                LastKey = lastKey
            };
        }

        private static async Task FlushBlockAsync(
            Stream stream,
            MemoryStream blockBuffer,
            byte[] firstKey,
            List<(byte[] firstKey, long offset)> indexEntries,
            long blockOffset,
            CancellationToken ct)
        {
            indexEntries.Add((firstKey, blockOffset));
            blockBuffer.Position = 0;
            await blockBuffer.CopyToAsync(stream, ct);
        }

        private static async Task WriteIndexBlockAsync(
            Stream stream,
            List<(byte[] firstKey, long offset)> indexEntries,
            CancellationToken ct)
        {
            // Index format: [entry_count:4]([key_len:4][key:bytes][offset:8])*
            var countBytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(countBytes, indexEntries.Count);
            await stream.WriteAsync(countBytes, ct);

            foreach (var (firstKey, offset) in indexEntries)
            {
                var keyLenBytes = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(keyLenBytes, firstKey.Length);
                await stream.WriteAsync(keyLenBytes, ct);

                await stream.WriteAsync(firstKey, ct);

                var offsetBytes = new byte[8];
                BinaryPrimitives.WriteInt64LittleEndian(offsetBytes, offset);
                await stream.WriteAsync(offsetBytes, ct);
            }
        }
    }
}
