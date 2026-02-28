using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateStorage.Strategies.Scale.LsmTree
{
    /// <summary>
    /// Reads data from an SSTable file.
    /// Supports efficient lookups using index and bloom filter.
    /// </summary>
    public sealed class SSTableReader : IAsyncDisposable
    {
        private const uint MagicNumber = 0x53535401;

        private readonly string _filePath;
        private FileStream? _stream;
        private BloomFilter? _bloomFilter;
        private List<(byte[] firstKey, long offset)>? _index;
        private long _entryCount;
        private long _indexOffset; // offset where index section begins; last data block ends here
        private bool _disposed;
        // Serialize all stream seek+read operations — concurrent readers would corrupt position
        private readonly SemaphoreSlim _streamLock = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Gets the SSTable metadata.
        /// </summary>
        public SSTable? Metadata { get; private set; }

        private SSTableReader(string filePath)
        {
            _filePath = filePath;
        }

        /// <summary>
        /// Opens an SSTable file for reading.
        /// </summary>
        /// <param name="filePath">Path to the SSTable file.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Initialized SSTableReader.</returns>
        public static async Task<SSTableReader> OpenAsync(string filePath, CancellationToken ct = default)
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("SSTable file not found", filePath);
            }

            var reader = new SSTableReader(filePath);
            await reader.InitializeAsync(ct);
            return reader;
        }

        private async Task InitializeAsync(CancellationToken ct)
        {
            _stream = new FileStream(
                _filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 8192,
                useAsync: true);

            // Read footer
            if (_stream.Length < 28)
            {
                throw new InvalidDataException("Invalid SSTable file: too small");
            }

            _stream.Seek(-28, SeekOrigin.End);
            var footer = new byte[28];
            if (await _stream.ReadAsync(footer, ct) != 28)
            {
                throw new InvalidDataException("Failed to read footer");
            }

            _indexOffset = BinaryPrimitives.ReadInt64LittleEndian(footer.AsSpan(0, 8));
            var indexOffset = _indexOffset;
            var bloomOffset = BinaryPrimitives.ReadInt64LittleEndian(footer.AsSpan(8, 8));
            _entryCount = BinaryPrimitives.ReadInt64LittleEndian(footer.AsSpan(16, 8));
            var magic = BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(24, 4));

            if (magic != MagicNumber)
            {
                throw new InvalidDataException("Invalid SSTable magic number");
            }

            // Read index
            _stream.Seek(indexOffset, SeekOrigin.Begin);
            _index = await ReadIndexAsync(ct);

            // Read bloom filter
            _stream.Seek(bloomOffset, SeekOrigin.Begin);
            _bloomFilter = await BloomFilter.DeserializeAsync(_stream);

            // Read metadata
            byte[]? firstKey = _index.Count > 0 ? _index[0].firstKey : null;

            // Compute true LastKey by scanning the last block (firstKey of last index entry is only the FIRST key in that block)
            byte[]? lastKey = null;
            if (_index.Count > 0)
            {
                var lastBlockOffset = _index[^1].offset;
                // Last block ends at indexOffset (the start of the index section)
                var lastBlockSize = (int)(indexOffset - lastBlockOffset);
                if (lastBlockSize > 0)
                {
                    _stream.Seek(lastBlockOffset, SeekOrigin.Begin);
                    var lastBlockData = new byte[lastBlockSize];
                    if (await _stream.ReadAsync(lastBlockData, ct).ConfigureAwait(false) == lastBlockSize)
                    {
                        lastKey = GetLastKeyInBlock(lastBlockData) ?? _index[^1].firstKey;
                    }
                    else
                    {
                        lastKey = _index[^1].firstKey; // fallback
                    }
                }
                else
                {
                    lastKey = _index[^1].firstKey;
                }
            }

            var fileInfo = new FileInfo(_filePath);

            Metadata = new SSTable
            {
                FilePath = _filePath,
                EntryCount = _entryCount,
                CreatedAt = fileInfo.CreationTimeUtc,
                FileSize = fileInfo.Length,
                FirstKey = firstKey,
                LastKey = lastKey
            };
        }

        private async Task<List<(byte[] firstKey, long offset)>> ReadIndexAsync(CancellationToken ct)
        {
            var countBytes = new byte[4];
            if (await _stream!.ReadAsync(countBytes, ct) != 4)
            {
                throw new InvalidDataException("Failed to read index count");
            }

            var count = BinaryPrimitives.ReadInt32LittleEndian(countBytes);
            var index = new List<(byte[], long)>(count);

            for (int i = 0; i < count; i++)
            {
                var keyLenBytes = new byte[4];
                if (await _stream.ReadAsync(keyLenBytes, ct) != 4)
                {
                    throw new InvalidDataException("Failed to read index key length");
                }

                var keyLen = BinaryPrimitives.ReadInt32LittleEndian(keyLenBytes);
                var key = new byte[keyLen];
                if (await _stream.ReadAsync(key, ct) != keyLen)
                {
                    throw new InvalidDataException("Failed to read index key");
                }

                var offsetBytes = new byte[8];
                if (await _stream.ReadAsync(offsetBytes, ct) != 8)
                {
                    throw new InvalidDataException("Failed to read index offset");
                }

                var offset = BinaryPrimitives.ReadInt64LittleEndian(offsetBytes);
                index.Add((key, offset));
            }

            return index;
        }

        /// <summary>
        /// Gets the value for a given key.
        /// </summary>
        /// <param name="key">Key to look up.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Value if found, null if not found or tombstone.</returns>
        public async Task<byte[]?> GetAsync(byte[] key, CancellationToken ct = default)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SSTableReader));
            }

            // Check bloom filter first
            if (_bloomFilter != null && !_bloomFilter.MayContain(key))
            {
                return null;
            }

            // Find the block that might contain the key
            var blockIndex = FindBlockIndex(key);
            if (blockIndex == -1)
            {
                return null;
            }

            // Serialize stream access to prevent concurrent seek corruption
            await _streamLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Read and scan the block
                var blockOffset = _index![blockIndex].offset;
                var nextBlockOffset = blockIndex + 1 < _index.Count
                    ? _index[blockIndex + 1].offset
                    : _indexOffset; // Index section starts here — last data block ends at indexOffset

                var blockSize = (int)(nextBlockOffset - blockOffset);
                _stream!.Seek(blockOffset, SeekOrigin.Begin);

                var blockData = new byte[blockSize];
                if (await _stream.ReadAsync(blockData, ct).ConfigureAwait(false) != blockSize)
                {
                    throw new InvalidDataException("Failed to read block data");
                }

                return ScanBlock(blockData, key);
            }
            finally
            {
                _streamLock.Release();
            }
        }

        private int FindBlockIndex(byte[] key)
        {
            if (_index == null || _index.Count == 0)
            {
                return -1;
            }

            // Binary search for the block
            int left = 0;
            int right = _index.Count - 1;
            int result = -1;

            while (left <= right)
            {
                int mid = left + (right - left) / 2;
                var cmp = ByteArrayComparer.Instance.Compare(key, _index[mid].firstKey);

                if (cmp >= 0)
                {
                    result = mid;
                    left = mid + 1;
                }
                else
                {
                    right = mid - 1;
                }
            }

            return result;
        }

        private byte[]? ScanBlock(byte[] blockData, byte[] targetKey)
        {
            int offset = 0;

            while (offset < blockData.Length)
            {
                if (offset + 8 > blockData.Length)
                {
                    break;
                }

                var keyLen = BinaryPrimitives.ReadInt32LittleEndian(blockData.AsSpan(offset, 4));
                offset += 4;

                if (keyLen < 0 || offset + keyLen > blockData.Length)
                {
                    break;
                }

                var key = blockData.AsSpan(offset, keyLen);
                offset += keyLen;

                if (offset + 4 > blockData.Length)
                {
                    break;
                }

                var valueLen = BinaryPrimitives.ReadInt32LittleEndian(blockData.AsSpan(offset, 4));
                offset += 4;

                if (valueLen == -1)
                {
                    // Tombstone
                    if (key.SequenceEqual(targetKey))
                    {
                        return null;
                    }
                    continue;
                }

                if (valueLen < 0 || offset + valueLen > blockData.Length)
                {
                    break;
                }

                if (key.SequenceEqual(targetKey))
                {
                    return blockData.AsSpan(offset, valueLen).ToArray();
                }

                offset += valueLen;
            }

            return null;
        }

        /// <summary>
        /// Scans for all entries with keys matching the given prefix.
        /// </summary>
        /// <param name="prefix">Prefix to match.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Async enumerable of matching key-value pairs.</returns>
        public async IAsyncEnumerable<KeyValuePair<byte[], byte[]>> ScanAsync(
            byte[] prefix,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SSTableReader));
            }

            if (_index == null)
            {
                yield break;
            }

            // Scan all blocks (could be optimized with range-based index lookup)
            for (int i = 0; i < _index.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var blockOffset = _index[i].offset;
                var nextBlockOffset = i + 1 < _index.Count
                    ? _index[i + 1].offset
                    : _stream!.Length - 28;

                var blockSize = (int)(nextBlockOffset - blockOffset);
                _stream!.Seek(blockOffset, SeekOrigin.Begin);

                var blockData = new byte[blockSize];
                if (await _stream.ReadAsync(blockData, ct) != blockSize)
                {
                    yield break;
                }

                await foreach (var kvp in ScanBlockAsync(blockData, prefix, ct))
                {
                    yield return kvp;
                }
            }
        }

        private async IAsyncEnumerable<KeyValuePair<byte[], byte[]>> ScanBlockAsync(
            byte[] blockData,
            byte[] prefix,
            [EnumeratorCancellation] CancellationToken ct)
        {
            int offset = 0;

            while (offset < blockData.Length)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();

                if (offset + 8 > blockData.Length)
                {
                    break;
                }

                var keyLen = BinaryPrimitives.ReadInt32LittleEndian(blockData.AsSpan(offset, 4));
                offset += 4;

                if (keyLen < 0 || offset + keyLen > blockData.Length)
                {
                    break;
                }

                var key = blockData.AsSpan(offset, keyLen).ToArray();
                offset += keyLen;

                if (offset + 4 > blockData.Length)
                {
                    break;
                }

                var valueLen = BinaryPrimitives.ReadInt32LittleEndian(blockData.AsSpan(offset, 4));
                offset += 4;

                if (valueLen == -1)
                {
                    // Tombstone
                    continue;
                }

                if (valueLen < 0 || offset + valueLen > blockData.Length)
                {
                    break;
                }

                if (StartsWithPrefix(key, prefix))
                {
                    var value = blockData.AsSpan(offset, valueLen).ToArray();
                    yield return new KeyValuePair<byte[], byte[]>(key, value);
                }

                offset += valueLen;
            }
        }

        /// <summary>Scans a block and returns the last non-tombstone key, or null if the block is empty.</summary>
        private static byte[]? GetLastKeyInBlock(byte[] blockData)
        {
            byte[]? lastKey = null;
            int offset = 0;
            while (offset < blockData.Length)
            {
                if (offset + 4 > blockData.Length) break;
                var keyLen = BinaryPrimitives.ReadInt32LittleEndian(blockData.AsSpan(offset, 4));
                offset += 4;
                if (keyLen <= 0 || offset + keyLen > blockData.Length) break;
                var key = blockData[offset..(offset + keyLen)];
                offset += keyLen;
                if (offset + 4 > blockData.Length) break;
                var valueLen = BinaryPrimitives.ReadInt32LittleEndian(blockData.AsSpan(offset, 4));
                offset += 4;
                if (valueLen == -1)
                {
                    // Tombstone — key exists but no value
                    lastKey = key;
                    continue;
                }
                if (valueLen < 0 || offset + valueLen > blockData.Length) break;
                lastKey = key;
                offset += valueLen;
            }
            return lastKey;
        }

        private static bool StartsWithPrefix(byte[] key, byte[] prefix)
        {
            if (key.Length < prefix.Length)
            {
                return false;
            }
            return key.AsSpan(0, prefix.Length).SequenceEqual(prefix);
        }

        /// <summary>
        /// Disposes resources used by the SSTableReader.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // Acquire lock before disposing stream to prevent concurrent readers from accessing a disposed stream
            await _streamLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_stream != null)
                {
                    await _stream.DisposeAsync().ConfigureAwait(false);
                    _stream = null;
                }
            }
            finally
            {
                _streamLock.Release();
                _streamLock.Dispose();
            }
        }
    }
}
