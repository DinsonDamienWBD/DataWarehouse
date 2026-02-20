using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.BlockAllocation;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.VirtualDiskEngine.Metadata;

/// <summary>
/// Implementation of <see cref="IInodeTable"/> that persists inodes to disk blocks.
/// Maintains a bounded in-memory cache for performance.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 33: VDE Inode system (VDE-02)")]
public sealed class InodeTable : IInodeTable, IDisposable
{
    private const int MaxCachedInodes = 10000;
    private const int RootInodeNumber = 1;

    private readonly IBlockDevice _device;
    private readonly IBlockAllocator _allocator;
    private readonly long _inodeTableStartBlock;
    private readonly long _inodeTableBlockCount;
    private readonly BoundedDictionary<long, Inode> _cache = new BoundedDictionary<long, Inode>(1000);
    private readonly BoundedDictionary<long, SemaphoreSlim> _inodeLocks = new BoundedDictionary<long, SemaphoreSlim>(1000);
    private readonly SemaphoreSlim _tableMetadataLock = new(1, 1);

    private long _nextInodeNumber;
    private bool _disposed;
    private Inode? _rootInode;

    /// <summary>
    /// Creates a new inode table.
    /// </summary>
    /// <param name="device">Block device for I/O.</param>
    /// <param name="allocator">Block allocator for data blocks.</param>
    /// <param name="inodeTableStartBlock">First block of the inode table region.</param>
    /// <param name="inodeTableBlockCount">Number of blocks in the inode table region.</param>
    public InodeTable(
        IBlockDevice device,
        IBlockAllocator allocator,
        long inodeTableStartBlock,
        long inodeTableBlockCount)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(allocator);

        _device = device;
        _allocator = allocator;
        _inodeTableStartBlock = inodeTableStartBlock;
        _inodeTableBlockCount = inodeTableBlockCount;
        _nextInodeNumber = 2; // Root is 1, start from 2
    }

    /// <inheritdoc/>
    public long AllocatedInodeCount => _nextInodeNumber - 1;

    /// <inheritdoc/>
    public Inode RootInode
    {
        get
        {
            if (_rootInode == null)
            {
                throw new InvalidOperationException("Root inode not initialized. Call InitializeAsync first.");
            }
            return _rootInode;
        }
    }

    /// <summary>
    /// Initializes the inode table, creating the root directory inode if needed.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        // Try to load root inode (inode 1)
        _rootInode = await GetInodeAsync(RootInodeNumber, ct);

        if (_rootInode == null)
        {
            // Create root directory inode
            long nowUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _rootInode = new Inode
            {
                InodeNumber = RootInodeNumber,
                Type = InodeType.Directory,
                Permissions = InodePermissions.All,
                LinkCount = 2, // . and parent reference
                OwnerId = 0,
                GroupId = 0,
                Size = 2, // . and .. entries
                CreatedUtc = nowUtc,
                ModifiedUtc = nowUtc,
                AccessedUtc = nowUtc
            };

            await WriteInodeAsync(_rootInode, ct);

            // Add . and .. entries to root
            var dotEntry = new DirectoryEntry
            {
                InodeNumber = RootInodeNumber,
                Type = InodeType.Directory,
                Name = "."
            };

            var dotDotEntry = new DirectoryEntry
            {
                InodeNumber = RootInodeNumber, // Root's parent is itself
                Type = InodeType.Directory,
                Name = ".."
            };

            await AddDirectoryEntryAsync(RootInodeNumber, dotEntry, ct);
            await AddDirectoryEntryAsync(RootInodeNumber, dotDotEntry, ct);

            _nextInodeNumber = 2;
        }
        else
        {
            // Scan inode table to find highest allocated inode number
            _nextInodeNumber = await FindNextInodeNumberAsync(ct);
        }
    }

    /// <inheritdoc/>
    public async Task<Inode> AllocateInodeAsync(InodeType type, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _tableMetadataLock.WaitAsync(ct);
        long inodeNumber;
        try
        {
            inodeNumber = _nextInodeNumber++;
        }
        finally
        {
            _tableMetadataLock.Release();
        }

        long nowUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var inode = new Inode
        {
            InodeNumber = inodeNumber,
            Type = type,
            Permissions = InodePermissions.OwnerAll | InodePermissions.GroupRead | InodePermissions.OtherRead,
            LinkCount = 1,
            OwnerId = 0,
            GroupId = 0,
            Size = 0,
            CreatedUtc = nowUtc,
            ModifiedUtc = nowUtc,
            AccessedUtc = nowUtc
        };

        await WriteInodeAsync(inode, ct);
        return inode;
    }

    /// <inheritdoc/>
    public async Task<Inode?> GetInodeAsync(long inodeNumber, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Check cache first
        if (_cache.TryGetValue(inodeNumber, out var cached))
        {
            return cached;
        }

        // Read from disk
        var inode = await ReadInodeAsync(inodeNumber, ct);
        if (inode != null && inode.LinkCount > 0)
        {
            // Add to cache (with eviction if needed)
            if (_cache.Count >= MaxCachedInodes)
            {
                EvictOldestCacheEntry();
            }
            _cache[inodeNumber] = inode;
        }

        return inode?.LinkCount > 0 ? inode : null;
    }

    /// <inheritdoc/>
    public async Task UpdateInodeAsync(Inode inode, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(inode);

        var inodeLock = GetInodeLock(inode.InodeNumber);
        await inodeLock.WaitAsync(ct);
        try
        {
            inode.ModifiedUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await WriteInodeAsync(inode, ct);
            _cache[inode.InodeNumber] = inode;
        }
        finally
        {
            inodeLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task FreeInodeAsync(long inodeNumber, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var inodeLock = GetInodeLock(inodeNumber);
        await inodeLock.WaitAsync(ct);
        try
        {
            var inode = await ReadInodeAsync(inodeNumber, ct);
            if (inode != null)
            {
                inode.LinkCount = 0; // Mark as free
                await WriteInodeAsync(inode, ct);
            }

            _cache.TryRemove(inodeNumber, out _);
            _inodeLocks.TryRemove(inodeNumber, out _);
        }
        finally
        {
            inodeLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DirectoryEntry>> ReadDirectoryAsync(long directoryInodeNumber, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var inode = await GetInodeAsync(directoryInodeNumber, ct);
        if (inode == null || inode.Type != InodeType.Directory)
        {
            throw new InvalidOperationException($"Inode {directoryInodeNumber} is not a directory.");
        }

        var entries = new List<DirectoryEntry>();
        byte[] blockBuffer = ArrayPool<byte>.Shared.Rent(_device.BlockSize);
        try
        {
            // Read all data blocks
            for (int i = 0; i < inode.DirectBlockPointers.Length && inode.DirectBlockPointers[i] != 0; i++)
            {
                long blockNum = inode.DirectBlockPointers[i];
                await _device.ReadBlockAsync(blockNum, blockBuffer, ct);

                // Parse entries from block
                ParseDirectoryBlock(blockBuffer.AsSpan(0, _device.BlockSize), entries);
            }

            // Handle indirect blocks if needed
            if (inode.IndirectBlockPointer != 0)
            {
                var indirectEntries = await ReadIndirectDirectoryBlocksAsync(inode.IndirectBlockPointer, ct);
                entries.AddRange(indirectEntries);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(blockBuffer);
        }

        return entries;
    }

    /// <inheritdoc/>
    public async Task AddDirectoryEntryAsync(long directoryInodeNumber, DirectoryEntry entry, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(entry);

        var inodeLock = GetInodeLock(directoryInodeNumber);
        await inodeLock.WaitAsync(ct);
        try
        {
            var inode = await GetInodeAsync(directoryInodeNumber, ct);
            if (inode == null || inode.Type != InodeType.Directory)
            {
                throw new InvalidOperationException($"Inode {directoryInodeNumber} is not a directory.");
            }

            // Find last data block or allocate a new one
            long targetBlock = await FindOrAllocateDirectoryBlockAsync(inode, ct);

            // Read the block
            byte[] blockBuffer = ArrayPool<byte>.Shared.Rent(_device.BlockSize);
            try
            {
                await _device.ReadBlockAsync(targetBlock, blockBuffer, ct);

                // Read entry count
                int entryCount = BinaryPrimitives.ReadUInt16LittleEndian(blockBuffer.AsSpan(0, 2));
                int offset = 2;

                // Skip existing entries
                for (int i = 0; i < entryCount; i++)
                {
                    DirectoryEntry.Deserialize(blockBuffer.AsSpan(offset), out int bytesRead);
                    offset += bytesRead;
                }

                // Check if new entry fits
                int entrySize = entry.SerializedSize;
                if (offset + entrySize > _device.BlockSize)
                {
                    // Allocate new block
                    targetBlock = _allocator.AllocateBlock(ct);
                    await AddDataBlockToInodeAsync(inode, targetBlock, ct);
                    Array.Clear(blockBuffer, 0, _device.BlockSize);
                    entryCount = 0;
                    offset = 2;
                }

                // Write entry
                entry.Serialize(blockBuffer.AsSpan(offset, entrySize));
                offset += entrySize;

                // Update entry count
                BinaryPrimitives.WriteUInt16LittleEndian(blockBuffer.AsSpan(0, 2), (ushort)(entryCount + 1));

                // Write block back
                await _device.WriteBlockAsync(targetBlock, blockBuffer.AsMemory(0, _device.BlockSize), ct);

                // Update inode size
                inode.Size++;
                await UpdateInodeAsync(inode, ct);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(blockBuffer);
            }
        }
        finally
        {
            inodeLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task RemoveDirectoryEntryAsync(long directoryInodeNumber, string name, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(name);

        var inodeLock = GetInodeLock(directoryInodeNumber);
        await inodeLock.WaitAsync(ct);
        try
        {
            var inode = await GetInodeAsync(directoryInodeNumber, ct);
            if (inode == null || inode.Type != InodeType.Directory)
            {
                throw new InvalidOperationException($"Inode {directoryInodeNumber} is not a directory.");
            }

            // Read all entries
            var entries = (await ReadDirectoryAsync(directoryInodeNumber, ct)).ToList();

            // Remove the entry
            int removed = entries.RemoveAll(e => e.Name == name);
            if (removed == 0)
            {
                throw new FileNotFoundException($"Directory entry '{name}' not found.");
            }

            // Rewrite directory blocks
            await RewriteDirectoryBlocksAsync(inode, entries, ct);

            // Update inode size
            inode.Size = entries.Count;
            await UpdateInodeAsync(inode, ct);
        }
        finally
        {
            inodeLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task SetExtendedAttributeAsync(long inodeNumber, string key, byte[] value, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);

        var inodeLock = GetInodeLock(inodeNumber);
        await inodeLock.WaitAsync(ct);
        try
        {
            var inode = await GetInodeAsync(inodeNumber, ct);
            if (inode == null)
            {
                throw new InvalidOperationException($"Inode {inodeNumber} not found.");
            }

            // Load existing xattrs
            if (inode.ExtendedAttributesBlock != 0)
            {
                await LoadExtendedAttributesAsync(inode, ct);
            }

            // Set attribute
            inode.ExtendedAttributes[key] = value;

            // Persist xattrs
            await PersistExtendedAttributesAsync(inode, ct);
            await UpdateInodeAsync(inode, ct);
        }
        finally
        {
            inodeLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<byte[]?> GetExtendedAttributeAsync(long inodeNumber, string key, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(key);

        var inode = await GetInodeAsync(inodeNumber, ct);
        if (inode == null)
        {
            return null;
        }

        if (inode.ExtendedAttributesBlock != 0 && inode.ExtendedAttributes.Count == 0)
        {
            await LoadExtendedAttributesAsync(inode, ct);
        }

        return inode.ExtendedAttributes.TryGetValue(key, out var value) ? value : null;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, byte[]>> ListExtendedAttributesAsync(long inodeNumber, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var inode = await GetInodeAsync(inodeNumber, ct);
        if (inode == null)
        {
            return new Dictionary<string, byte[]>();
        }

        if (inode.ExtendedAttributesBlock != 0 && inode.ExtendedAttributes.Count == 0)
        {
            await LoadExtendedAttributesAsync(inode, ct);
        }

        return inode.ExtendedAttributes;
    }

    /// <summary>
    /// Writes an inode to disk.
    /// </summary>
    private async Task WriteInodeAsync(Inode inode, CancellationToken ct)
    {
        long blockNumber = _inodeTableStartBlock + inode.InodeNumber / Inode.InodesPerBlock;
        int offsetInBlock = (int)(inode.InodeNumber % Inode.InodesPerBlock) * Inode.InodeSize;

        byte[] blockBuffer = ArrayPool<byte>.Shared.Rent(_device.BlockSize);
        try
        {
            // Read-modify-write
            await _device.ReadBlockAsync(blockNumber, blockBuffer, ct);
            inode.Serialize(blockBuffer.AsSpan(offsetInBlock, Inode.InodeSize));
            await _device.WriteBlockAsync(blockNumber, blockBuffer.AsMemory(0, _device.BlockSize), ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(blockBuffer);
        }
    }

    /// <summary>
    /// Reads an inode from disk.
    /// </summary>
    private async Task<Inode?> ReadInodeAsync(long inodeNumber, CancellationToken ct)
    {
        long blockNumber = _inodeTableStartBlock + inodeNumber / Inode.InodesPerBlock;
        int offsetInBlock = (int)(inodeNumber % Inode.InodesPerBlock) * Inode.InodeSize;

        if (blockNumber >= _inodeTableStartBlock + _inodeTableBlockCount)
        {
            return null; // Out of bounds
        }

        byte[] blockBuffer = ArrayPool<byte>.Shared.Rent(_device.BlockSize);
        try
        {
            await _device.ReadBlockAsync(blockNumber, blockBuffer, ct);
            return Inode.Deserialize(blockBuffer.AsSpan(offsetInBlock, Inode.InodeSize));
        }
        catch
        {
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(blockBuffer);
        }
    }

    /// <summary>
    /// Finds or allocates a directory block for appending entries.
    /// </summary>
    private async Task<long> FindOrAllocateDirectoryBlockAsync(Inode inode, CancellationToken ct)
    {
        // Find last non-zero direct block pointer
        for (int i = Inode.DirectBlockCount - 1; i >= 0; i--)
        {
            if (inode.DirectBlockPointers[i] != 0)
            {
                return inode.DirectBlockPointers[i];
            }
        }

        // Allocate first block
        long newBlock = _allocator.AllocateBlock(ct);
        await AddDataBlockToInodeAsync(inode, newBlock, ct);
        return newBlock;
    }

    /// <summary>
    /// Adds a data block to an inode's block pointer list.
    /// </summary>
    private async Task AddDataBlockToInodeAsync(Inode inode, long blockNumber, CancellationToken ct)
    {
        // Find first empty direct pointer
        for (int i = 0; i < Inode.DirectBlockCount; i++)
        {
            if (inode.DirectBlockPointers[i] == 0)
            {
                inode.DirectBlockPointers[i] = blockNumber;
                await UpdateInodeAsync(inode, ct);
                return;
            }
        }

        throw new NotSupportedException($"Inode has no available direct block pointers. Indirect block support is not yet implemented. Current limit: {Inode.DirectBlockCount} direct blocks.");
    }

    /// <summary>
    /// Parses directory entries from a directory data block.
    /// </summary>
    private static void ParseDirectoryBlock(ReadOnlySpan<byte> blockData, List<DirectoryEntry> entries)
    {
        if (blockData.Length < 2) return;

        int entryCount = BinaryPrimitives.ReadUInt16LittleEndian(blockData.Slice(0, 2));
        int offset = 2;

        for (int i = 0; i < entryCount && offset < blockData.Length; i++)
        {
            try
            {
                var entry = DirectoryEntry.Deserialize(blockData.Slice(offset), out int bytesRead);
                entries.Add(entry);
                offset += bytesRead;
            }
            catch
            {
                break; // Malformed entry, stop parsing
            }
        }
    }

    /// <summary>
    /// Reads directory entries from indirect blocks.
    /// </summary>
    private async Task<List<DirectoryEntry>> ReadIndirectDirectoryBlocksAsync(long indirectBlock, CancellationToken ct)
    {
        var entries = new List<DirectoryEntry>();
        byte[] blockBuffer = ArrayPool<byte>.Shared.Rent(_device.BlockSize);
        try
        {
            await _device.ReadBlockAsync(indirectBlock, blockBuffer, ct);

            // Read block pointers from indirect block
            int pointerCount = _device.BlockSize / 8;
            for (int i = 0; i < pointerCount; i++)
            {
                long dataBlock = BinaryPrimitives.ReadInt64LittleEndian(blockBuffer.AsSpan(i * 8, 8));
                if (dataBlock == 0) break;

                await _device.ReadBlockAsync(dataBlock, blockBuffer, ct);
                ParseDirectoryBlock(blockBuffer.AsSpan(0, _device.BlockSize), entries);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(blockBuffer);
        }

        return entries;
    }

    /// <summary>
    /// Rewrites directory blocks after entry removal.
    /// </summary>
    private async Task RewriteDirectoryBlocksAsync(Inode inode, List<DirectoryEntry> entries, CancellationToken ct)
    {
        byte[] blockBuffer = ArrayPool<byte>.Shared.Rent(_device.BlockSize);
        try
        {
            int entryIndex = 0;
            int blockIndex = 0;

            while (entryIndex < entries.Count)
            {
                Array.Clear(blockBuffer, 0, _device.BlockSize);
                int offset = 2;
                int entriesInBlock = 0;

                // Pack entries into block
                while (entryIndex < entries.Count && offset + entries[entryIndex].SerializedSize <= _device.BlockSize)
                {
                    int written = entries[entryIndex].Serialize(blockBuffer.AsSpan(offset));
                    offset += written;
                    entriesInBlock++;
                    entryIndex++;
                }

                // Write entry count
                BinaryPrimitives.WriteUInt16LittleEndian(blockBuffer.AsSpan(0, 2), (ushort)entriesInBlock);

                // Get or allocate block
                long blockNum;
                if (blockIndex < Inode.DirectBlockCount && inode.DirectBlockPointers[blockIndex] != 0)
                {
                    blockNum = inode.DirectBlockPointers[blockIndex];
                }
                else
                {
                    blockNum = _allocator.AllocateBlock(ct);
                    inode.DirectBlockPointers[blockIndex] = blockNum;
                }

                await _device.WriteBlockAsync(blockNum, blockBuffer.AsMemory(0, _device.BlockSize), ct);
                blockIndex++;
            }

            // Clear remaining direct pointers
            for (int i = blockIndex; i < Inode.DirectBlockCount; i++)
            {
                inode.DirectBlockPointers[i] = 0;
            }

            await UpdateInodeAsync(inode, ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(blockBuffer);
        }
    }

    /// <summary>
    /// Loads extended attributes from overflow blocks.
    /// </summary>
    private async Task LoadExtendedAttributesAsync(Inode inode, CancellationToken ct)
    {
        if (inode.ExtendedAttributesBlock == 0) return;

        byte[] blockBuffer = ArrayPool<byte>.Shared.Rent(_device.BlockSize);
        try
        {
            await _device.ReadBlockAsync(inode.ExtendedAttributesBlock, blockBuffer, ct);

            // Deserialize JSON
            var json = System.Text.Encoding.UTF8.GetString(blockBuffer.AsSpan(0, _device.BlockSize).TrimEnd((byte)0));
            if (!string.IsNullOrWhiteSpace(json))
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, byte[]>>(json);
                if (dict != null)
                {
                    inode.ExtendedAttributes = dict;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(blockBuffer);
        }
    }

    /// <summary>
    /// Persists extended attributes to overflow blocks.
    /// </summary>
    private async Task PersistExtendedAttributesAsync(Inode inode, CancellationToken ct)
    {
        if (inode.ExtendedAttributes.Count == 0)
        {
            // Free xattr block if exists
            if (inode.ExtendedAttributesBlock != 0)
            {
                _allocator.FreeBlock(inode.ExtendedAttributesBlock);
                inode.ExtendedAttributesBlock = 0;
            }
            return;
        }

        // Allocate block if needed
        if (inode.ExtendedAttributesBlock == 0)
        {
            inode.ExtendedAttributesBlock = _allocator.AllocateBlock(ct);
        }

        byte[] blockBuffer = ArrayPool<byte>.Shared.Rent(_device.BlockSize);
        try
        {
            Array.Clear(blockBuffer, 0, _device.BlockSize);

            // Serialize to JSON
            var json = JsonSerializer.Serialize(inode.ExtendedAttributes);
            var jsonBytes = System.Text.Encoding.UTF8.GetBytes(json);

            if (jsonBytes.Length > _device.BlockSize)
            {
                throw new InvalidOperationException("Extended attributes exceed block size.");
            }

            jsonBytes.CopyTo(blockBuffer, 0);
            await _device.WriteBlockAsync(inode.ExtendedAttributesBlock, blockBuffer.AsMemory(0, _device.BlockSize), ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(blockBuffer);
        }
    }

    /// <summary>
    /// Finds the next inode number by scanning the inode table.
    /// </summary>
    private async Task<long> FindNextInodeNumberAsync(CancellationToken ct)
    {
        long maxInode = 1;
        byte[] blockBuffer = ArrayPool<byte>.Shared.Rent(_device.BlockSize);
        try
        {
            for (long blockIdx = 0; blockIdx < _inodeTableBlockCount; blockIdx++)
            {
                await _device.ReadBlockAsync(_inodeTableStartBlock + blockIdx, blockBuffer, ct);

                for (int i = 0; i < Inode.InodesPerBlock; i++)
                {
                    int offset = i * Inode.InodeSize;
                    var inode = Inode.Deserialize(blockBuffer.AsSpan(offset, Inode.InodeSize));
                    if (inode.LinkCount > 0 && inode.InodeNumber > maxInode)
                    {
                        maxInode = inode.InodeNumber;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(blockBuffer);
        }

        return maxInode + 1;
    }

    /// <summary>
    /// Gets or creates a lock for the specified inode.
    /// </summary>
    private SemaphoreSlim GetInodeLock(long inodeNumber)
    {
        return _inodeLocks.GetOrAdd(inodeNumber, _ => new SemaphoreSlim(1, 1));
    }

    /// <summary>
    /// Evicts the oldest cache entry when cache is full.
    /// </summary>
    private void EvictOldestCacheEntry()
    {
        if (_cache.IsEmpty) return;

        // Simple eviction: remove first entry
        var first = _cache.Keys.FirstOrDefault();
        if (first != default)
        {
            _cache.TryRemove(first, out _);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _tableMetadataLock.Dispose();
        foreach (var kvp in _inodeLocks)
        {
            kvp.Value.Dispose();
        }
    }
}
