using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.ModuleManagement;

/// <summary>
/// A mapping from a logical block address to a physical on-disk block address.
/// Only blocks that have been relocated have non-identity mappings.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Block mapping for region indirection (OMA-05)")]
public readonly record struct BlockMapping(long LogicalBlock, long PhysicalBlock, bool IsRemapped);

/// <summary>
/// In-memory indirection table mapping logical block addresses to physical block addresses.
/// Unmoved blocks have identity mapping (logical == physical) and are NOT stored in the table.
/// Only blocks that have been relocated have explicit entries.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Indirection table for online defragmentation (OMA-05)")]
public sealed class IndirectionTable
{
    // Indirection table on-disk header layout:
    // [MagicINDR:4][Version:2][EntryCount:4][Reserved:6] = 16 bytes
    private static ReadOnlySpan<byte> MagicBytes => new byte[] { 0x49, 0x4E, 0x44, 0x52 }; // "INDR"
    private const ushort CurrentVersion = 1;
    /// <summary>Size of the indirection table header in bytes.</summary>
    internal const int HeaderSize = 16;
    private const int EntrySize = 16; // [LogicalBlock:8][PhysicalBlock:8]

    private readonly Dictionary<long, long> _mappings = new();

    /// <summary>Number of non-identity (remapped) blocks in the table.</summary>
    public int RemappedBlockCount => _mappings.Count;

    /// <summary>
    /// Resolves a logical block address to its physical block address.
    /// Returns the logical block itself if no remapping exists (identity mapping).
    /// </summary>
    /// <param name="logicalBlock">The logical block address to resolve.</param>
    /// <returns>The physical block address.</returns>
    public long Resolve(long logicalBlock)
    {
        return _mappings.TryGetValue(logicalBlock, out long physical) ? physical : logicalBlock;
    }

    /// <summary>
    /// Adds or updates a mapping from a logical block to a new physical block.
    /// </summary>
    /// <param name="logicalBlock">The logical block address.</param>
    /// <param name="newPhysicalBlock">The new physical block address.</param>
    public void RemapBlock(long logicalBlock, long newPhysicalBlock)
    {
        _mappings[logicalBlock] = newPhysicalBlock;
    }

    /// <summary>
    /// Removes the mapping for a logical block (block moved back to original position).
    /// </summary>
    /// <param name="logicalBlock">The logical block address to remove from the table.</param>
    public void RemoveMapping(long logicalBlock)
    {
        _mappings.Remove(logicalBlock);
    }

    /// <summary>
    /// Returns all non-identity mappings currently in the table.
    /// </summary>
    public IReadOnlyList<BlockMapping> GetAllMappings()
    {
        var result = new List<BlockMapping>(_mappings.Count);
        foreach (var kvp in _mappings)
        {
            result.Add(new BlockMapping(kvp.Key, kvp.Value, IsRemapped: true));
        }
        return result;
    }

    /// <summary>
    /// Checks if a logical block has a non-identity mapping.
    /// </summary>
    /// <param name="logicalBlock">The logical block address to check.</param>
    /// <returns>True if the block is remapped; false if identity mapping.</returns>
    public bool HasMapping(long logicalBlock)
    {
        return _mappings.ContainsKey(logicalBlock);
    }

    /// <summary>
    /// Serializes the indirection table to a block-sized buffer.
    /// Format: [Header:16][Entry0:16][Entry1:16]...[XxHash64:8]
    /// </summary>
    /// <param name="table">The indirection table to serialize.</param>
    /// <param name="buffer">Destination buffer (must be at least one block size).</param>
    public static void Serialize(IndirectionTable table, Span<byte> buffer)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (buffer.Length < HeaderSize)
            throw new ArgumentException("Buffer too small for indirection table header.", nameof(buffer));

        buffer.Clear();

        // Header: [MagicINDR:4][Version:2][EntryCount:4][Reserved:6] = 16 bytes
        MagicBytes.CopyTo(buffer);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(4), CurrentVersion);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(6), table._mappings.Count);
        // Reserved bytes [10..16) already zeroed

        // Write entries
        int offset = HeaderSize;
        foreach (var kvp in table._mappings)
        {
            if (offset + EntrySize + 8 > buffer.Length) // +8 for final checksum
                break; // Table full; truncate

            BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), kvp.Key);
            BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset + 8), kvp.Value);
            offset += EntrySize;
        }

        // XxHash64 checksum of all data before the checksum
        int checksumOffset = buffer.Length - 8;
        ulong checksum = XxHash64.HashToUInt64(buffer.Slice(0, checksumOffset));
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.Slice(checksumOffset), checksum);
    }

    /// <summary>
    /// Deserializes an indirection table from a block-sized buffer.
    /// Returns an empty table if the buffer is invalid or uninitialized.
    /// </summary>
    /// <param name="buffer">Source buffer containing serialized indirection table.</param>
    /// <returns>The deserialized indirection table.</returns>
    public static IndirectionTable Deserialize(ReadOnlySpan<byte> buffer)
    {
        var table = new IndirectionTable();

        if (buffer.Length < HeaderSize + 8) // Header + checksum minimum
            return table;

        // Verify magic
        if (!buffer.Slice(0, 4).SequenceEqual(MagicBytes))
            return table; // Not an indirection table block; return empty

        // Verify checksum
        int checksumOffset = buffer.Length - 8;
        ulong storedChecksum = BinaryPrimitives.ReadUInt64LittleEndian(buffer.Slice(checksumOffset));
        ulong computedChecksum = XxHash64.HashToUInt64(buffer.Slice(0, checksumOffset));
        if (storedChecksum != computedChecksum)
            return table; // Corrupt data; return empty

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(4));
        if (version != CurrentVersion)
            return table; // Unknown version; return empty

        int entryCount = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(6));

        int offset = HeaderSize;
        for (int i = 0; i < entryCount && offset + EntrySize <= checksumOffset; i++)
        {
            long logical = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
            long physical = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset + 8));
            table._mappings[logical] = physical;
            offset += EntrySize;
        }

        return table;
    }
}

/// <summary>
/// Provides logical-to-physical block address translation for region indirection.
/// The indirection layer sits between callers and the raw VDE stream, transparently
/// redirecting reads and writes to the correct physical block locations.
/// Used during online defragmentation to ensure zero-downtime block relocation.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: Region indirection layer for online defragmentation (OMA-05)")]
public sealed class RegionIndirectionLayer
{
    private readonly Stream _vdeStream;
    private readonly int _blockSize;
    private readonly long _indirectionTableBlock;
    private IndirectionTable _table;

    /// <summary>
    /// Creates a new region indirection layer.
    /// </summary>
    /// <param name="vdeStream">The VDE stream for read/write operations.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="indirectionTableBlock">Block number where the indirection table is stored on disk.</param>
    public RegionIndirectionLayer(Stream vdeStream, int blockSize, long indirectionTableBlock)
    {
        _vdeStream = vdeStream ?? throw new ArgumentNullException(nameof(vdeStream));
        if (blockSize < FormatConstants.MinBlockSize || blockSize > FormatConstants.MaxBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize));
        _blockSize = blockSize;
        _indirectionTableBlock = indirectionTableBlock;
        _table = new IndirectionTable();
    }

    /// <summary>
    /// Gets the in-memory indirection table for direct inspection or modification.
    /// </summary>
    public IndirectionTable Table => _table;

    /// <summary>
    /// Reads a block using logical-to-physical address translation.
    /// </summary>
    /// <param name="logicalBlock">The logical block number to read.</param>
    /// <param name="buffer">Destination buffer (must be at least blockSize bytes).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The physical block number that was actually read from.</returns>
    public async Task<long> ReadBlockAsync(long logicalBlock, Memory<byte> buffer, CancellationToken ct)
    {
        long physicalBlock = _table.Resolve(logicalBlock);
        long offset = physicalBlock * _blockSize;

        _vdeStream.Seek(offset, SeekOrigin.Begin);
        int totalRead = 0;
        int toRead = Math.Min(buffer.Length, _blockSize);
        while (totalRead < toRead)
        {
            int bytesRead = await _vdeStream.ReadAsync(buffer.Slice(totalRead, toRead - totalRead), ct);
            if (bytesRead == 0)
                throw new InvalidDataException($"Unexpected end of stream reading physical block {physicalBlock}.");
            totalRead += bytesRead;
        }

        return physicalBlock;
    }

    /// <summary>
    /// Writes a block using logical-to-physical address translation.
    /// </summary>
    /// <param name="logicalBlock">The logical block number to write.</param>
    /// <param name="data">Source data (must be at least blockSize bytes).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task WriteBlockAsync(long logicalBlock, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        long physicalBlock = _table.Resolve(logicalBlock);
        long offset = physicalBlock * _blockSize;

        _vdeStream.Seek(offset, SeekOrigin.Begin);
        await _vdeStream.WriteAsync(data.Slice(0, Math.Min(data.Length, _blockSize)), ct);
    }

    /// <summary>
    /// Updates the indirection table to remap a logical block to a new physical location.
    /// </summary>
    /// <param name="logicalBlock">The logical block address.</param>
    /// <param name="newPhysicalBlock">The new physical block address.</param>
    public void RemapBlock(long logicalBlock, long newPhysicalBlock)
    {
        _table.RemapBlock(logicalBlock, newPhysicalBlock);
    }

    /// <summary>
    /// Persists the current in-memory indirection table to its on-disk block.
    /// Serializes the table and flushes the stream.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task PersistTableAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            IndirectionTable.Serialize(_table, buffer.AsSpan(0, _blockSize));

            long offset = _indirectionTableBlock * _blockSize;
            _vdeStream.Seek(offset, SeekOrigin.Begin);
            await _vdeStream.WriteAsync(buffer.AsMemory(0, _blockSize), ct);
            await _vdeStream.FlushAsync(ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Loads the indirection table from its on-disk block into memory.
    /// If the block is empty or invalid, creates an empty table (no remappings).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task LoadTableAsync(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            long offset = _indirectionTableBlock * _blockSize;
            _vdeStream.Seek(offset, SeekOrigin.Begin);

            int totalRead = 0;
            while (totalRead < _blockSize)
            {
                int bytesRead = await _vdeStream.ReadAsync(buffer.AsMemory(totalRead, _blockSize - totalRead), ct);
                if (bytesRead == 0) break;
                totalRead += bytesRead;
            }

            if (totalRead >= IndirectionTable.HeaderSize + 8)
            {
                _table = IndirectionTable.Deserialize(buffer.AsSpan(0, _blockSize));
            }
            else
            {
                _table = new IndirectionTable();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
