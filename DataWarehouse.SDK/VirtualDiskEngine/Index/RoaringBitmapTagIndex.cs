using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Numerics;
using System.Text;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Index;

/// <summary>
/// Persistent inverted tag index using roaring bitmaps. Maps (tagKey, tagValue)
/// pairs to sets of inode numbers via space-efficient roaring bitmaps stored
/// in the VDE Tag Index (TAGI) region.
/// Provides O(1) AND/OR/NOT set operations at ~2 bytes per element.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: VOPT-21 persistent roaring bitmap tag index")]
public sealed class RoaringBitmapTagIndex
{
    /// <summary>
    /// Header layout: [EntryCount:4][BucketCount:4][Reserved:8] = 16 bytes.
    /// Followed by hash table entries, each: [XxHash64Key:8][BlockStart:4][BlockCount:4] = 16 bytes.
    /// </summary>
    private const int HeaderSize = 16;
    private const int HashEntrySize = 16;

    private readonly IBlockDevice _device;
    private readonly long _regionStart;
    private readonly long _regionBlockCount;
    private readonly int _blockSize;

    /// <summary>In-memory index: XxHash64(key+value) -> RoaringBitmap of inode numbers.</summary>
    private readonly ConcurrentDictionary<ulong, RoaringBitmap> _index = new();

    /// <summary>Reverse map: hash -> (key, value) for persistence.</summary>
    private readonly ConcurrentDictionary<ulong, (string Key, string Value)> _tagNames = new();

    /// <summary>
    /// Creates a new roaring bitmap tag index backed by the given block device region.
    /// </summary>
    /// <param name="device">Block device for persistent storage.</param>
    /// <param name="tagIndexRegionStart">First block number of the TAGI region.</param>
    /// <param name="tagIndexRegionBlockCount">Number of blocks allocated to the TAGI region.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    public RoaringBitmapTagIndex(
        IBlockDevice device,
        long tagIndexRegionStart,
        long tagIndexRegionBlockCount,
        int blockSize)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _regionStart = tagIndexRegionStart;
        _regionBlockCount = tagIndexRegionBlockCount;
        _blockSize = blockSize;
    }

    /// <summary>Number of distinct (key, value) tag pairs in the index.</summary>
    public int TagCount => _index.Count;

    /// <summary>
    /// Adds an inode to the bitmap for the given tag (key, value).
    /// </summary>
    public Task AddTagAsync(string tagKey, string tagValue, long inodeNumber, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tagKey);
        ArgumentNullException.ThrowIfNull(tagValue);

        ulong hash = ComputeTagHash(tagKey, tagValue);
        var bitmap = _index.GetOrAdd(hash, _ => new RoaringBitmap());
        bitmap.Add(inodeNumber);
        _tagNames.TryAdd(hash, (tagKey, tagValue));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Removes an inode from the bitmap for the given tag (key, value).
    /// </summary>
    public Task RemoveTagAsync(string tagKey, string tagValue, long inodeNumber, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tagKey);
        ArgumentNullException.ThrowIfNull(tagValue);

        ulong hash = ComputeTagHash(tagKey, tagValue);
        if (_index.TryGetValue(hash, out var bitmap))
        {
            bitmap.Remove(inodeNumber);
            if (bitmap.Cardinality == 0)
            {
                _index.TryRemove(hash, out _);
                _tagNames.TryRemove(hash, out _);
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns the bitmap of all inodes tagged with (key, value).
    /// </summary>
    public Task<RoaringBitmap> QueryAsync(string tagKey, string tagValue, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tagKey);
        ArgumentNullException.ThrowIfNull(tagValue);

        ulong hash = ComputeTagHash(tagKey, tagValue);
        if (_index.TryGetValue(hash, out var bitmap))
            return Task.FromResult(bitmap);

        return Task.FromResult(new RoaringBitmap());
    }

    /// <summary>
    /// Returns the intersection (AND) of bitmaps for all specified tags.
    /// </summary>
    public Task<RoaringBitmap> QueryAndAsync(IReadOnlyList<(string Key, string Value)> tags, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tags);
        if (tags.Count == 0) return Task.FromResult(new RoaringBitmap());

        RoaringBitmap? result = null;
        foreach (var (key, value) in tags)
        {
            ulong hash = ComputeTagHash(key, value);
            if (!_index.TryGetValue(hash, out var bitmap))
                return Task.FromResult(new RoaringBitmap()); // empty intersection

            result = result is null ? bitmap : RoaringBitmap.And(result, bitmap);
        }

        return Task.FromResult(result ?? new RoaringBitmap());
    }

    /// <summary>
    /// Returns the union (OR) of bitmaps for all specified tags.
    /// </summary>
    public Task<RoaringBitmap> QueryOrAsync(IReadOnlyList<(string Key, string Value)> tags, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tags);
        if (tags.Count == 0) return Task.FromResult(new RoaringBitmap());

        RoaringBitmap? result = null;
        foreach (var (key, value) in tags)
        {
            ulong hash = ComputeTagHash(key, value);
            if (_index.TryGetValue(hash, out var bitmap))
                result = result is null ? bitmap : RoaringBitmap.Or(result, bitmap);
        }

        return Task.FromResult(result ?? new RoaringBitmap());
    }

    /// <summary>
    /// Persists all bitmaps to the TAGI region blocks on the device.
    /// Header maps tag hashes to block ranges via a hash table in the first N blocks.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        var entries = _index.ToArray();
        if (entries.Length == 0)
        {
            // Write empty header
            var emptyBlock = new byte[_blockSize];
            await _device.WriteBlockAsync(_regionStart, emptyBlock, ct).ConfigureAwait(false);
            return;
        }

        // Calculate hash table size: power-of-two buckets for open addressing
        int bucketCount = Math.Max(16, (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)(entries.Length * 2)));
        int hashTableBytes = bucketCount * HashEntrySize;
        int headerBlocks = (HeaderSize + hashTableBytes + _blockSize - 1) / _blockSize;

        // Serialize each bitmap and assign block ranges
        var serializedBitmaps = new List<(ulong Hash, byte[] Data)>();
        long currentBlock = headerBlocks;

        var hashTable = new (ulong Hash, int BlockStart, int BlockCount)[bucketCount];

        foreach (var (hash, bitmap) in entries)
        {
            byte[] data = bitmap.Serialize();
            serializedBitmaps.Add((hash, data));

            int blocksNeeded = (data.Length + _blockSize - 1) / _blockSize;

            // Insert into hash table via open addressing
            int slot = (int)(hash % (ulong)bucketCount);
            while (hashTable[slot].Hash != 0 && hashTable[slot].Hash != hash)
                slot = (slot + 1) % bucketCount;

            hashTable[slot] = (hash, (int)currentBlock, blocksNeeded);
            currentBlock += blocksNeeded;
        }

        // Write header blocks
        var headerBuffer = new byte[headerBlocks * _blockSize];
        BinaryPrimitives.WriteInt32LittleEndian(headerBuffer.AsSpan(0), entries.Length);
        BinaryPrimitives.WriteInt32LittleEndian(headerBuffer.AsSpan(4), bucketCount);

        int offset = HeaderSize;
        for (int i = 0; i < bucketCount; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(headerBuffer.AsSpan(offset), hashTable[i].Hash);
            BinaryPrimitives.WriteInt32LittleEndian(headerBuffer.AsSpan(offset + 8), hashTable[i].BlockStart);
            BinaryPrimitives.WriteInt32LittleEndian(headerBuffer.AsSpan(offset + 12), hashTable[i].BlockCount);
            offset += HashEntrySize;
        }

        for (int b = 0; b < headerBlocks; b++)
        {
            await _device.WriteBlockAsync(
                _regionStart + b,
                headerBuffer.AsMemory(b * _blockSize, _blockSize),
                ct).ConfigureAwait(false);
        }

        // Write bitmap data blocks
        foreach (var (hash, data) in serializedBitmaps)
        {
            // Find its block range from hash table
            int slot = (int)(hash % (ulong)bucketCount);
            while (hashTable[slot].Hash != hash)
                slot = (slot + 1) % bucketCount;

            int blockStart = hashTable[slot].BlockStart;
            int blocksNeeded = hashTable[slot].BlockCount;

            for (int b = 0; b < blocksNeeded; b++)
            {
                int dataOffset = b * _blockSize;
                int len = Math.Min(_blockSize, data.Length - dataOffset);
                var block = new byte[_blockSize];
                if (len > 0)
                    data.AsSpan(dataOffset, len).CopyTo(block);
                await _device.WriteBlockAsync(
                    _regionStart + blockStart + b,
                    block, ct).ConfigureAwait(false);
            }
        }

        // Persist tag name reverse mapping after bitmap data blocks
        // Format: [nameCount:4][hash:8][keyLen:2][keyBytes][valueLen:2][valueBytes]...
        var tagNameEntries = _tagNames.ToArray();
        using var tagNameStream = new System.IO.MemoryStream();
        var countBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(countBytes, tagNameEntries.Length);
        tagNameStream.Write(countBytes);
        foreach (var (hash, (key, value)) in tagNameEntries)
        {
            var hashBytes = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(hashBytes, hash);
            tagNameStream.Write(hashBytes);
            var keyData = Encoding.UTF8.GetBytes(key);
            var valData = Encoding.UTF8.GetBytes(value);
            var lenBuf = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(lenBuf, (ushort)keyData.Length);
            tagNameStream.Write(lenBuf);
            tagNameStream.Write(keyData);
            BinaryPrimitives.WriteUInt16LittleEndian(lenBuf, (ushort)valData.Length);
            tagNameStream.Write(lenBuf);
            tagNameStream.Write(valData);
        }
        var tagNameData = tagNameStream.ToArray();
        int tagNameBlocks = (tagNameData.Length + _blockSize - 1) / _blockSize;
        for (int b = 0; b < tagNameBlocks; b++)
        {
            var block = new byte[_blockSize];
            int srcOffset = b * _blockSize;
            int len = Math.Min(_blockSize, tagNameData.Length - srcOffset);
            if (len > 0)
                tagNameData.AsSpan(srcOffset, len).CopyTo(block);
            await _device.WriteBlockAsync(
                _regionStart + currentBlock + b,
                block, ct).ConfigureAwait(false);
        }
        // Store tag name block offset in reserved header bytes (offset 8-12)
        BinaryPrimitives.WriteInt32LittleEndian(headerBuffer.AsSpan(8), (int)currentBlock);
        BinaryPrimitives.WriteInt32LittleEndian(headerBuffer.AsSpan(12), tagNameBlocks);
        // Re-write first header block with updated reserved fields
        await _device.WriteBlockAsync(_regionStart, headerBuffer.AsMemory(0, _blockSize), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Loads bitmaps from the TAGI region blocks on the device.
    /// </summary>
    public async Task LoadAsync(CancellationToken ct)
    {
        _index.Clear();
        _tagNames.Clear();

        var headerBlock = new byte[_blockSize];
        await _device.ReadBlockAsync(_regionStart, headerBlock, ct).ConfigureAwait(false);

        int entryCount = BinaryPrimitives.ReadInt32LittleEndian(headerBlock.AsSpan(0));
        int bucketCount = BinaryPrimitives.ReadInt32LittleEndian(headerBlock.AsSpan(4));

        if (entryCount == 0 || bucketCount == 0) return;

        // Read all header blocks
        int headerBlocks = (HeaderSize + bucketCount * HashEntrySize + _blockSize - 1) / _blockSize;
        var headerBuffer = new byte[headerBlocks * _blockSize];
        for (int b = 0; b < headerBlocks; b++)
        {
            await _device.ReadBlockAsync(
                _regionStart + b,
                headerBuffer.AsMemory(b * _blockSize, _blockSize),
                ct).ConfigureAwait(false);
        }

        // Read hash table entries and load bitmaps
        int offset = HeaderSize;
        for (int i = 0; i < bucketCount; i++)
        {
            ulong hash = BinaryPrimitives.ReadUInt64LittleEndian(headerBuffer.AsSpan(offset));
            int blockStart = BinaryPrimitives.ReadInt32LittleEndian(headerBuffer.AsSpan(offset + 8));
            int blockCount = BinaryPrimitives.ReadInt32LittleEndian(headerBuffer.AsSpan(offset + 12));
            offset += HashEntrySize;

            if (hash == 0) continue;

            // Read bitmap data
            var bitmapData = new byte[blockCount * _blockSize];
            for (int b = 0; b < blockCount; b++)
            {
                await _device.ReadBlockAsync(
                    _regionStart + blockStart + b,
                    bitmapData.AsMemory(b * _blockSize, _blockSize),
                    ct).ConfigureAwait(false);
            }

            var bitmap = RoaringBitmap.Deserialize(bitmapData);
            _index[hash] = bitmap;
        }

        // Load tag name reverse mapping from reserved header fields
        int tagNameBlockStart = BinaryPrimitives.ReadInt32LittleEndian(headerBuffer.AsSpan(8));
        int tagNameBlockCount = BinaryPrimitives.ReadInt32LittleEndian(headerBuffer.AsSpan(12));
        if (tagNameBlockStart > 0 && tagNameBlockCount > 0)
        {
            var tagNameData = new byte[tagNameBlockCount * _blockSize];
            for (int b = 0; b < tagNameBlockCount; b++)
            {
                await _device.ReadBlockAsync(
                    _regionStart + tagNameBlockStart + b,
                    tagNameData.AsMemory(b * _blockSize, _blockSize),
                    ct).ConfigureAwait(false);
            }
            int tnOffset = 0;
            int nameCount = BinaryPrimitives.ReadInt32LittleEndian(tagNameData.AsSpan(tnOffset));
            tnOffset += 4;
            for (int n = 0; n < nameCount && tnOffset + 12 <= tagNameData.Length; n++)
            {
                ulong tnHash = BinaryPrimitives.ReadUInt64LittleEndian(tagNameData.AsSpan(tnOffset));
                tnOffset += 8;
                int keyLen = BinaryPrimitives.ReadUInt16LittleEndian(tagNameData.AsSpan(tnOffset));
                tnOffset += 2;
                if (tnOffset + keyLen > tagNameData.Length) break;
                string key = Encoding.UTF8.GetString(tagNameData, tnOffset, keyLen);
                tnOffset += keyLen;
                if (tnOffset + 2 > tagNameData.Length) break;
                int valLen = BinaryPrimitives.ReadUInt16LittleEndian(tagNameData.AsSpan(tnOffset));
                tnOffset += 2;
                if (tnOffset + valLen > tagNameData.Length) break;
                string value = Encoding.UTF8.GetString(tagNameData, tnOffset, valLen);
                tnOffset += valLen;
                _tagNames.TryAdd(tnHash, (key, value));
            }
        }
    }

    /// <summary>
    /// Computes XxHash64 of (key + "\0" + value) as the lookup key.
    /// </summary>
    internal static ulong ComputeTagHash(string tagKey, string tagValue)
    {
        int keyBytes = Encoding.UTF8.GetByteCount(tagKey);
        int valueBytes = Encoding.UTF8.GetByteCount(tagValue);
        int totalLen = keyBytes + 1 + valueBytes; // key + 0x00 separator + value
        Span<byte> buffer = totalLen <= 512 ? stackalloc byte[totalLen] : new byte[totalLen];
        Encoding.UTF8.GetBytes(tagKey, buffer);
        buffer[keyBytes] = 0x00;
        Encoding.UTF8.GetBytes(tagValue, buffer.Slice(keyBytes + 1));
        return XxHash64.HashToUInt64(buffer);
    }

    // ========================================================================
    // Roaring Bitmap implementation (embedded, no external dependency)
    // ========================================================================

    /// <summary>
    /// Space-efficient bitmap supporting O(1) AND/OR/NOT set operations.
    /// Divides 64-bit space into 65536-entry chunks (high 48 bits = chunk key,
    /// low 16 bits = position within chunk). Each chunk uses the most efficient
    /// container type: ArrayContainer (sparse), BitmapContainer (dense), or
    /// RunContainer (sequential ranges).
    /// </summary>
    [SdkCompatibility("6.0.0", Notes = "Phase 87: VOPT-21 embedded roaring bitmap")]
    public sealed class RoaringBitmap
    {
        /// <summary>Threshold for promoting ArrayContainer to BitmapContainer.</summary>
        internal const int ArrayToBitmapThreshold = 4096;

        /// <summary>Chunk containers keyed by the high 48 bits of the inode number.</summary>
        private readonly SortedDictionary<long, IContainer> _containers = new();

        /// <summary>Number of set bits across all containers.</summary>
        public long Cardinality
        {
            get
            {
                long count = 0;
                foreach (var c in _containers.Values)
                    count += c.Cardinality;
                return count;
            }
        }

        /// <summary>Inserts an inode number into the bitmap.</summary>
        public void Add(long inodeNumber)
        {
            long chunkKey = inodeNumber >> 16;
            ushort position = (ushort)(inodeNumber & 0xFFFF);

            if (!_containers.TryGetValue(chunkKey, out var container))
            {
                container = new ArrayContainer();
                _containers[chunkKey] = container;
            }

            container.Add(position);

            // Promote Array -> Bitmap at threshold
            if (container is ArrayContainer arr && arr.Cardinality >= ArrayToBitmapThreshold)
            {
                _containers[chunkKey] = arr.ToBitmapContainer();
            }
        }

        /// <summary>Removes an inode number from the bitmap.</summary>
        public void Remove(long inodeNumber)
        {
            long chunkKey = inodeNumber >> 16;
            ushort position = (ushort)(inodeNumber & 0xFFFF);

            if (!_containers.TryGetValue(chunkKey, out var container))
                return;

            container.Remove(position);

            // Demote Bitmap -> Array if sparse
            if (container is BitmapContainer bmp && bmp.Cardinality < ArrayToBitmapThreshold)
            {
                _containers[chunkKey] = bmp.ToArrayContainer();
            }

            // Remove empty containers
            if (container.Cardinality == 0)
                _containers.Remove(chunkKey);
        }

        /// <summary>Returns true if the inode number is in the bitmap.</summary>
        public bool Contains(long inodeNumber)
        {
            long chunkKey = inodeNumber >> 16;
            ushort position = (ushort)(inodeNumber & 0xFFFF);

            return _containers.TryGetValue(chunkKey, out var container) && container.Contains(position);
        }

        /// <summary>Returns the intersection of two bitmaps.</summary>
        public static RoaringBitmap And(RoaringBitmap a, RoaringBitmap b)
        {
            var result = new RoaringBitmap();
            foreach (var (key, containerA) in a._containers)
            {
                if (b._containers.TryGetValue(key, out var containerB))
                {
                    var andResult = containerA.And(containerB);
                    if (andResult.Cardinality > 0)
                        result._containers[key] = andResult;
                }
            }
            return result;
        }

        /// <summary>Returns the union of two bitmaps.</summary>
        public static RoaringBitmap Or(RoaringBitmap a, RoaringBitmap b)
        {
            var result = new RoaringBitmap();

            foreach (var (key, container) in a._containers)
                result._containers[key] = container.Clone();

            foreach (var (key, containerB) in b._containers)
            {
                if (result._containers.TryGetValue(key, out var existing))
                    result._containers[key] = existing.Or(containerB);
                else
                    result._containers[key] = containerB.Clone();
            }

            return result;
        }

        /// <summary>Returns the complement of a bitmap within [0, universeSize).</summary>
        public static RoaringBitmap Not(RoaringBitmap a, long universeSize)
        {
            var result = new RoaringBitmap();
            long maxChunkKey = (universeSize - 1) >> 16;

            for (long chunkKey = 0; chunkKey <= maxChunkKey; chunkKey++)
            {
                ushort maxPos = chunkKey == maxChunkKey
                    ? (ushort)((universeSize - 1) & 0xFFFF)
                    : (ushort)0xFFFF;

                if (a._containers.TryGetValue(chunkKey, out var container))
                {
                    var notResult = container.Not(maxPos);
                    if (notResult.Cardinality > 0)
                        result._containers[chunkKey] = notResult;
                }
                else
                {
                    // All bits set in this chunk up to maxPos
                    var full = new ArrayContainer();
                    for (ushort p = 0; p <= maxPos; p++)
                        full.Add(p);

                    if (full.Cardinality >= ArrayToBitmapThreshold)
                        result._containers[chunkKey] = full.ToBitmapContainer();
                    else if (full.Cardinality > 0)
                        result._containers[chunkKey] = full;
                }
            }

            return result;
        }

        /// <summary>Iterates all set inode numbers in sorted order.</summary>
        public IEnumerable<long> Enumerate()
        {
            foreach (var (chunkKey, container) in _containers)
            {
                long baseValue = chunkKey << 16;
                foreach (ushort pos in container.Enumerate())
                    yield return baseValue | pos;
            }
        }

        /// <summary>Serializes the bitmap to a byte array.</summary>
        public byte[] Serialize()
        {
            // Format: [ContainerCount:4][{ChunkKey:8, Type:1, DataLength:4, Data:N}...]
            using var ms = new MemoryStream();
            var countBuf = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(countBuf, _containers.Count);
            ms.Write(countBuf);

            foreach (var (chunkKey, container) in _containers)
            {
                var header = new byte[13]; // 8 + 1 + 4
                BinaryPrimitives.WriteInt64LittleEndian(header, chunkKey);
                header[8] = container.TypeId;
                byte[] data = container.SerializeData();
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(9), data.Length);
                ms.Write(header);
                ms.Write(data);
            }

            return ms.ToArray();
        }

        /// <summary>Deserializes a bitmap from a byte array.</summary>
        public static RoaringBitmap Deserialize(ReadOnlySpan<byte> data)
        {
            var bitmap = new RoaringBitmap();
            if (data.Length < 4) return bitmap;

            int containerCount = BinaryPrimitives.ReadInt32LittleEndian(data);
            int offset = 4;

            for (int i = 0; i < containerCount && offset + 13 <= data.Length; i++)
            {
                long chunkKey = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset));
                byte typeId = data[offset + 8];
                int dataLength = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset + 9));
                offset += 13;

                if (offset + dataLength > data.Length) break;

                IContainer container = typeId switch
                {
                    ArrayContainer.TypeIdentifier => ArrayContainer.DeserializeData(data.Slice(offset, dataLength)),
                    BitmapContainer.TypeIdentifier => BitmapContainer.DeserializeData(data.Slice(offset, dataLength)),
                    RunContainer.TypeIdentifier => RunContainer.DeserializeData(data.Slice(offset, dataLength)),
                    _ => ArrayContainer.DeserializeData(data.Slice(offset, dataLength))
                };

                bitmap._containers[chunkKey] = container;
                offset += dataLength;
            }

            return bitmap;
        }

        // ================================================================
        // Container types
        // ================================================================

        /// <summary>Container interface for chunk storage.</summary>
        internal interface IContainer
        {
            int Cardinality { get; }
            byte TypeId { get; }
            void Add(ushort position);
            void Remove(ushort position);
            bool Contains(ushort position);
            IContainer And(IContainer other);
            IContainer Or(IContainer other);
            IContainer Not(ushort maxPosition);
            IContainer Clone();
            IEnumerable<ushort> Enumerate();
            byte[] SerializeData();
        }

        /// <summary>
        /// Sorted ushort array for sparse containers (less than 4096 entries).
        /// </summary>
        internal sealed class ArrayContainer : IContainer
        {
            internal const byte TypeIdentifier = 1;
            private readonly List<ushort> _values = new();

            public int Cardinality => _values.Count;
            public byte TypeId => TypeIdentifier;

            public void Add(ushort position)
            {
                int idx = _values.BinarySearch(position);
                if (idx < 0)
                    _values.Insert(~idx, position);
            }

            public void Remove(ushort position)
            {
                int idx = _values.BinarySearch(position);
                if (idx >= 0)
                    _values.RemoveAt(idx);
            }

            public bool Contains(ushort position)
            {
                return _values.BinarySearch(position) >= 0;
            }

            public IContainer And(IContainer other)
            {
                var result = new ArrayContainer();
                foreach (ushort v in _values)
                {
                    if (other.Contains(v))
                        result._values.Add(v);
                }
                return result.Cardinality >= ArrayToBitmapThreshold
                    ? result.ToBitmapContainer()
                    : result;
            }

            public IContainer Or(IContainer other)
            {
                var result = new ArrayContainer();
                foreach (ushort v in _values) result.Add(v);
                foreach (ushort v in other.Enumerate()) result.Add(v);
                return result.Cardinality >= ArrayToBitmapThreshold
                    ? result.ToBitmapContainer()
                    : result;
            }

            public IContainer Not(ushort maxPosition)
            {
                var result = new ArrayContainer();
                int idx = 0;
                for (ushort p = 0; p <= maxPosition; p++)
                {
                    if (idx < _values.Count && _values[idx] == p)
                        idx++;
                    else
                        result._values.Add(p);

                    if (p == ushort.MaxValue) break;
                }
                return result.Cardinality >= ArrayToBitmapThreshold
                    ? result.ToBitmapContainer()
                    : result;
            }

            public IContainer Clone()
            {
                var clone = new ArrayContainer();
                clone._values.AddRange(_values);
                return clone;
            }

            public IEnumerable<ushort> Enumerate() => _values;

            public BitmapContainer ToBitmapContainer()
            {
                var bmp = new BitmapContainer();
                foreach (ushort v in _values)
                    bmp.Add(v);
                return bmp;
            }

            public byte[] SerializeData()
            {
                var data = new byte[_values.Count * 2];
                for (int i = 0; i < _values.Count; i++)
                    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(i * 2), _values[i]);
                return data;
            }

            public static ArrayContainer DeserializeData(ReadOnlySpan<byte> data)
            {
                var container = new ArrayContainer();
                for (int i = 0; i + 1 < data.Length; i += 2)
                    container._values.Add(BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(i)));
                return container;
            }
        }

        /// <summary>
        /// 8KB bitmap for dense containers (4096+ entries).
        /// </summary>
        internal sealed class BitmapContainer : IContainer
        {
            internal const byte TypeIdentifier = 2;

            /// <summary>8KB = 65536 bits, one per possible ushort value.</summary>
            private readonly byte[] _bits = new byte[8192];
            private int _cardinality;

            public int Cardinality => _cardinality;
            public byte TypeId => TypeIdentifier;

            public void Add(ushort position)
            {
                int byteIdx = position >> 3;
                int bitIdx = position & 7;
                if ((_bits[byteIdx] & (1 << bitIdx)) == 0)
                {
                    _bits[byteIdx] |= (byte)(1 << bitIdx);
                    _cardinality++;
                }
            }

            public void Remove(ushort position)
            {
                int byteIdx = position >> 3;
                int bitIdx = position & 7;
                if ((_bits[byteIdx] & (1 << bitIdx)) != 0)
                {
                    _bits[byteIdx] &= (byte)~(1 << bitIdx);
                    _cardinality--;
                }
            }

            public bool Contains(ushort position)
            {
                return (_bits[position >> 3] & (1 << (position & 7))) != 0;
            }

            public IContainer And(IContainer other)
            {
                if (other is BitmapContainer bmpOther)
                {
                    var result = new BitmapContainer();
                    for (int i = 0; i < 8192; i++)
                    {
                        result._bits[i] = (byte)(_bits[i] & bmpOther._bits[i]);
                    }
                    result.RecomputeCardinality();
                    return result._cardinality < ArrayToBitmapThreshold
                        ? result.ToArrayContainer()
                        : result;
                }

                // Mixed: iterate the smaller container
                var arrResult = new ArrayContainer();
                foreach (ushort v in other.Enumerate())
                {
                    if (Contains(v))
                        arrResult.Add(v);
                }
                return arrResult.Cardinality >= ArrayToBitmapThreshold
                    ? (IContainer)arrResult.ToBitmapContainer()
                    : arrResult;
            }

            public IContainer Or(IContainer other)
            {
                if (other is BitmapContainer bmpOther)
                {
                    var result = new BitmapContainer();
                    for (int i = 0; i < 8192; i++)
                    {
                        result._bits[i] = (byte)(_bits[i] | bmpOther._bits[i]);
                    }
                    result.RecomputeCardinality();
                    return result;
                }

                var clone = (BitmapContainer)Clone();
                foreach (ushort v in other.Enumerate())
                    clone.Add(v);
                return clone;
            }

            public IContainer Not(ushort maxPosition)
            {
                var result = new BitmapContainer();
                int fullBytes = maxPosition >= 7 ? (maxPosition + 1) / 8 : 0;
                for (int i = 0; i < fullBytes && i < 8192; i++)
                    result._bits[i] = (byte)~_bits[i];

                // Handle partial last byte
                if (maxPosition < 65535)
                {
                    int lastByteIdx = maxPosition / 8;
                    if (lastByteIdx < 8192)
                    {
                        int bitsInLastByte = (maxPosition % 8) + 1;
                        byte mask = (byte)((1 << bitsInLastByte) - 1);
                        result._bits[lastByteIdx] = (byte)(~_bits[lastByteIdx] & mask);
                    }
                }
                else
                {
                    // All 65536 bits
                    for (int i = 0; i < 8192; i++)
                        result._bits[i] = (byte)~_bits[i];
                }

                result.RecomputeCardinality();
                return result._cardinality < ArrayToBitmapThreshold
                    ? result.ToArrayContainer()
                    : result;
            }

            public IContainer Clone()
            {
                var clone = new BitmapContainer();
                Array.Copy(_bits, clone._bits, 8192);
                clone._cardinality = _cardinality;
                return clone;
            }

            public IEnumerable<ushort> Enumerate()
            {
                for (int byteIdx = 0; byteIdx < 8192; byteIdx++)
                {
                    if (_bits[byteIdx] == 0) continue;
                    for (int bit = 0; bit < 8; bit++)
                    {
                        if ((_bits[byteIdx] & (1 << bit)) != 0)
                            yield return (ushort)((byteIdx << 3) | bit);
                    }
                }
            }

            public ArrayContainer ToArrayContainer()
            {
                var arr = new ArrayContainer();
                foreach (ushort v in Enumerate())
                    arr.Add(v);
                return arr;
            }

            private void RecomputeCardinality()
            {
                _cardinality = 0;
                for (int i = 0; i < 8192; i++)
                    _cardinality += BitCount(_bits[i]);
            }

            private static int BitCount(byte b)
            {
                int count = 0;
                while (b != 0)
                {
                    count += b & 1;
                    b >>= 1;
                }
                return count;
            }

            public byte[] SerializeData()
            {
                return (byte[])_bits.Clone();
            }

            public static BitmapContainer DeserializeData(ReadOnlySpan<byte> data)
            {
                var container = new BitmapContainer();
                int len = Math.Min(data.Length, 8192);
                data.Slice(0, len).CopyTo(container._bits);
                container.RecomputeCardinality();
                return container;
            }
        }

        /// <summary>
        /// Run-length encoded container for sequential ranges.
        /// Each run is (start, length) pair representing [start, start+length] inclusive.
        /// </summary>
        internal sealed class RunContainer : IContainer
        {
            internal const byte TypeIdentifier = 3;
            private readonly List<(ushort Start, ushort Length)> _runs = new();

            public int Cardinality
            {
                get
                {
                    int count = 0;
                    foreach (var (_, len) in _runs)
                        count += len + 1;
                    return count;
                }
            }

            public byte TypeId => TypeIdentifier;

            public void Add(ushort position)
            {
                // Simple insertion; merge adjacent runs
                for (int i = 0; i < _runs.Count; i++)
                {
                    var (start, len) = _runs[i];
                    ushort end = (ushort)(start + len);

                    if (position >= start && position <= end)
                        return; // Already present

                    if (position == end + 1 && end < ushort.MaxValue)
                    {
                        _runs[i] = (start, (ushort)(len + 1));
                        // Try merge with next
                        if (i + 1 < _runs.Count && _runs[i].Start + _runs[i].Length + 1 == _runs[i + 1].Start)
                        {
                            ushort newLen = (ushort)(_runs[i].Length + 1 + _runs[i + 1].Length);
                            _runs[i] = (_runs[i].Start, newLen);
                            _runs.RemoveAt(i + 1);
                        }
                        return;
                    }

                    if (position + 1 == start)
                    {
                        _runs[i] = (position, (ushort)(len + 1));
                        // Try merge with previous
                        if (i > 0 && _runs[i - 1].Start + _runs[i - 1].Length + 1 == _runs[i].Start)
                        {
                            ushort newLen = (ushort)(_runs[i - 1].Length + 1 + _runs[i].Length);
                            _runs[i - 1] = (_runs[i - 1].Start, newLen);
                            _runs.RemoveAt(i);
                        }
                        return;
                    }

                    if (position < start)
                    {
                        _runs.Insert(i, (position, 0));
                        return;
                    }
                }

                _runs.Add((position, 0));
            }

            public void Remove(ushort position)
            {
                for (int i = 0; i < _runs.Count; i++)
                {
                    var (start, len) = _runs[i];
                    ushort end = (ushort)(start + len);

                    if (position < start) return;
                    if (position > end) continue;

                    if (start == end)
                    {
                        _runs.RemoveAt(i);
                    }
                    else if (position == start)
                    {
                        _runs[i] = ((ushort)(start + 1), (ushort)(len - 1));
                    }
                    else if (position == end)
                    {
                        _runs[i] = (start, (ushort)(len - 1));
                    }
                    else
                    {
                        // Split the run
                        _runs[i] = (start, (ushort)(position - start - 1));
                        _runs.Insert(i + 1, ((ushort)(position + 1), (ushort)(end - position - 1)));
                    }
                    return;
                }
            }

            public bool Contains(ushort position)
            {
                foreach (var (start, len) in _runs)
                {
                    if (position < start) return false;
                    if (position <= start + len) return true;
                }
                return false;
            }

            public IContainer And(IContainer other)
            {
                var result = new ArrayContainer();
                foreach (ushort v in Enumerate())
                {
                    if (other.Contains(v))
                        result.Add(v);
                }
                return result.Cardinality >= ArrayToBitmapThreshold
                    ? (IContainer)result.ToBitmapContainer()
                    : result;
            }

            public IContainer Or(IContainer other)
            {
                var result = new ArrayContainer();
                foreach (ushort v in Enumerate()) result.Add(v);
                foreach (ushort v in other.Enumerate()) result.Add(v);
                return result.Cardinality >= ArrayToBitmapThreshold
                    ? (IContainer)result.ToBitmapContainer()
                    : result;
            }

            public IContainer Not(ushort maxPosition)
            {
                var result = new ArrayContainer();
                // Add all values NOT in this container up to maxPosition
                int runIdx = 0;
                for (ushort p = 0; p <= maxPosition; p++)
                {
                    if (runIdx < _runs.Count && p >= _runs[runIdx].Start && p <= _runs[runIdx].Start + _runs[runIdx].Length)
                    {
                        // Skip: in current run
                        if (p == _runs[runIdx].Start + _runs[runIdx].Length)
                            runIdx++;
                    }
                    else
                    {
                        result.Add(p);
                    }
                    if (p == ushort.MaxValue) break;
                }
                return result.Cardinality >= ArrayToBitmapThreshold
                    ? (IContainer)result.ToBitmapContainer()
                    : result;
            }

            public IContainer Clone()
            {
                var clone = new RunContainer();
                clone._runs.AddRange(_runs);
                return clone;
            }

            public IEnumerable<ushort> Enumerate()
            {
                foreach (var (start, len) in _runs)
                {
                    for (int p = start; p <= start + len; p++)
                        yield return (ushort)p;
                }
            }

            public byte[] SerializeData()
            {
                var data = new byte[_runs.Count * 4];
                for (int i = 0; i < _runs.Count; i++)
                {
                    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(i * 4), _runs[i].Start);
                    BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(i * 4 + 2), _runs[i].Length);
                }
                return data;
            }

            public static RunContainer DeserializeData(ReadOnlySpan<byte> data)
            {
                var container = new RunContainer();
                for (int i = 0; i + 3 < data.Length; i += 4)
                {
                    ushort start = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(i));
                    ushort length = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(i + 2));
                    container._runs.Add((start, length));
                }
                return container;
            }
        }
    }
}
