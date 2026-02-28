using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Sql;

/// <summary>
/// Column type for columnar table definitions.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Columnar VDE regions (VOPT-16)")]
public enum ColumnType : byte
{
    /// <summary>32-bit signed integer (4 bytes).</summary>
    Int32 = 0,

    /// <summary>64-bit signed integer (8 bytes).</summary>
    Int64 = 1,

    /// <summary>32-bit IEEE 754 float (4 bytes).</summary>
    Float32 = 2,

    /// <summary>64-bit IEEE 754 double (8 bytes).</summary>
    Float64 = 3,

    /// <summary>Variable-length UTF-8 string (stored as fixed-width for columnar).</summary>
    String = 4,

    /// <summary>Variable-length binary data (stored as fixed-width for columnar).</summary>
    Binary = 5,

    /// <summary>Boolean (1 byte).</summary>
    Boolean = 6,

    /// <summary>Date/time as UTC ticks (8 bytes).</summary>
    DateTime = 7,
}

/// <summary>
/// Defines a column in a columnar table, including its name, type, encoding hint, and nullability.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Columnar VDE regions (VOPT-16)")]
public readonly struct ColumnDefinition
{
    /// <summary>Column name (max 128 chars).</summary>
    public string Name { get; }

    /// <summary>Column data type.</summary>
    public ColumnType Type { get; }

    /// <summary>Preferred encoding hint (may be overridden by the engine).</summary>
    public EncodingType PreferredEncoding { get; }

    /// <summary>Whether this column can contain null values.</summary>
    public bool Nullable { get; }

    /// <summary>Creates a new column definition.</summary>
    public ColumnDefinition(string name, ColumnType type, EncodingType preferredEncoding = EncodingType.Plain, bool nullable = false)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Type = type;
        PreferredEncoding = preferredEncoding;
        Nullable = nullable;
    }
}

/// <summary>
/// Manages a dedicated VDE region for columnar (column-oriented) table storage.
/// Supports creating tables, appending row groups with automatic encoding,
/// reading individual columns, and scanning with zone map filtering.
/// </summary>
/// <remarks>
/// Region layout:
/// - Block 0: Table metadata header (table name, column definitions, row group count)
/// - Blocks 1..N: Row groups, each containing encoded column chunks written sequentially
/// - Zone map entries created for each column in each row group
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 87: Columnar VDE regions (VOPT-16)")]
public sealed class ColumnarRegionEngine
{
    private readonly IBlockDevice _device;
    private readonly RegionDirectory _regionDir;
    private readonly int _blockSize;

    /// <summary>Default row group size (number of rows per group).</summary>
    public const int DefaultRowGroupSize = 65536;

    /// <summary>Configurable row group size.</summary>
    public int RowGroupSize { get; set; } = DefaultRowGroupSize;

    // In-memory table metadata cache
    private readonly ConcurrentDictionary<string, TableMetadata> _tables = new();
    private readonly object _appendLock = new();

    /// <summary>
    /// Creates a new columnar region engine.
    /// </summary>
    /// <param name="device">The block device for I/O.</param>
    /// <param name="regionDir">The VDE region directory for registration.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    public ColumnarRegionEngine(IBlockDevice device, RegionDirectory regionDir, int blockSize)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _regionDir = regionDir ?? throw new ArgumentNullException(nameof(regionDir));
        _blockSize = blockSize > 0 ? blockSize : throw new ArgumentOutOfRangeException(nameof(blockSize));
    }

    /// <summary>
    /// Creates a new columnar table with the specified schema.
    /// Registers a new COLR region in the RegionDirectory and writes column metadata.
    /// </summary>
    /// <param name="tableName">Unique table name.</param>
    /// <param name="columns">Column definitions.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task CreateColumnarTableAsync(string tableName, ColumnDefinition[] columns, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(tableName)) throw new ArgumentException("Table name is required.", nameof(tableName));
        if (columns == null || columns.Length == 0) throw new ArgumentException("At least one column is required.", nameof(columns));

        // Allocate region blocks: 1 metadata block + initial space for row groups
        const int initialRegionBlocks = 64;
        long startBlock = FindFreeRegionStart(initialRegionBlocks);

        // Register in region directory
        _regionDir.AddRegion(BlockTypeTags.COLR, RegionFlags.None, startBlock, initialRegionBlocks);

        // Serialize table metadata to the first block of the region
        var metadataBlock = new byte[_blockSize];
        int offset = 0;

        // Table name: [nameLen:2LE][nameBytes:N]
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(tableName);
        BinaryPrimitives.WriteUInt16LittleEndian(metadataBlock.AsSpan(offset, 2), (ushort)nameBytes.Length);
        offset += 2;
        nameBytes.CopyTo(metadataBlock, offset);
        offset += nameBytes.Length;

        // Column count
        BinaryPrimitives.WriteUInt16LittleEndian(metadataBlock.AsSpan(offset, 2), (ushort)columns.Length);
        offset += 2;

        // Row group count (initially 0)
        BinaryPrimitives.WriteInt32LittleEndian(metadataBlock.AsSpan(offset, 4), 0);
        offset += 4;

        // Each column: [nameLen:2LE][nameBytes:N][type:1][encoding:1][nullable:1]
        foreach (var col in columns)
        {
            var colNameBytes = System.Text.Encoding.UTF8.GetBytes(col.Name);
            BinaryPrimitives.WriteUInt16LittleEndian(metadataBlock.AsSpan(offset, 2), (ushort)colNameBytes.Length);
            offset += 2;
            colNameBytes.CopyTo(metadataBlock, offset);
            offset += colNameBytes.Length;
            metadataBlock[offset++] = (byte)col.Type;
            metadataBlock[offset++] = (byte)col.PreferredEncoding;
            metadataBlock[offset++] = col.Nullable ? (byte)1 : (byte)0;
        }

        await _device.WriteBlockAsync(startBlock, metadataBlock, ct).ConfigureAwait(false);

        // Cache table metadata (NextFreeBlock starts at metadata block + 1)
        var meta = new TableMetadata(tableName, columns, startBlock, initialRegionBlocks, 0, startBlock + 1);
        _tables[tableName] = meta;
    }

    /// <summary>
    /// Appends a row group of encoded column data.
    /// Each column is encoded using the best encoding, and zone map entries are created.
    /// </summary>
    /// <param name="tableName">Target table name.</param>
    /// <param name="columnData">Array of raw column data (one byte[] per column, each rowCount * valueWidth bytes).</param>
    /// <param name="rowCount">Number of rows in this row group.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task AppendRowGroupAsync(string tableName, byte[][] columnData, int rowCount, CancellationToken ct = default)
    {
        if (!_tables.TryGetValue(tableName, out var meta))
            throw new InvalidOperationException($"Table '{tableName}' not found. Call CreateColumnarTableAsync first.");
        if (columnData.Length != meta.Columns.Length)
            throw new ArgumentException($"Expected {meta.Columns.Length} columns, got {columnData.Length}.", nameof(columnData));

        // Determine the next available block for this row group
        // Use NextFreeBlock to properly track multi-block columns
        long nextBlock = meta.NextFreeBlock;

        // Encode and write each column chunk, tracking actual block offsets
        var zoneMapEntries = new List<ZoneMapEntry>();
        long writeOffset = nextBlock;

        for (int colIdx = 0; colIdx < meta.Columns.Length; colIdx++)
        {
            var colDef = meta.Columns[colIdx];
            var rawData = columnData[colIdx];
            int valueWidth = GetValueWidth(colDef.Type);

            // Select encoding and encode
            var encoding = ColumnarEncoding.SelectBestEncoding(rawData, valueWidth);
            byte[] encodedData;

            switch (encoding)
            {
                case EncodingType.RunLength:
                    encodedData = ColumnarEncoding.EncodeRunLength(rawData, valueWidth);
                    break;
                case EncodingType.Dictionary:
                    var (dictData, indexData) = ColumnarEncoding.EncodeDictionary(rawData, valueWidth);
                    // Combine dict + index for storage: [dictLen:4LE][dict][index]
                    encodedData = new byte[4 + dictData.Length + indexData.Length];
                    BinaryPrimitives.WriteInt32LittleEndian(encodedData.AsSpan(0, 4), dictData.Length);
                    dictData.CopyTo(encodedData, 4);
                    indexData.CopyTo(encodedData, 4 + dictData.Length);
                    break;
                case EncodingType.BitPacked:
                    encodedData = ColumnarEncoding.EncodeBitPacked(rawData);
                    break;
                default:
                    encodedData = rawData;
                    break;
            }

            // Build block payload: [encoding:1][rowCount:4LE][dataLen:4LE][data]
            int headerSize = 1 + 4 + 4;
            int totalPayload = headerSize + encodedData.Length;
            int blocksNeeded = (totalPayload + _blockSize - 1) / _blockSize;
            var payload = new byte[blocksNeeded * _blockSize];

            payload[0] = (byte)encoding;
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(1, 4), rowCount);
            BinaryPrimitives.WriteInt32LittleEndian(payload.AsSpan(5, 4), encodedData.Length);
            encodedData.CopyTo(payload, headerSize);

            // Write blocks at current write offset (handles multi-block columns correctly)
            for (int b = 0; b < blocksNeeded; b++)
            {
                await _device.WriteBlockAsync(
                    writeOffset + b,
                    payload.AsMemory(b * _blockSize, _blockSize),
                    ct).ConfigureAwait(false);
            }

            // Create zone map entry for this column chunk
            var zoneEntry = BuildZoneMapEntry(rawData, valueWidth, rowCount, writeOffset, blocksNeeded);
            zoneMapEntries.Add(zoneEntry);

            writeOffset += blocksNeeded;
        }

        // Atomically update row group count in metadata (lock protects concurrent appends)
        lock (_appendLock)
        {
            meta = meta with { RowGroupCount = meta.RowGroupCount + 1, NextFreeBlock = writeOffset };
            _tables[tableName] = meta;
        }

        // Update metadata block with new row group count
        var metaBlock = new byte[_blockSize];
        await _device.ReadBlockAsync(meta.RegionStartBlock, metaBlock, ct).ConfigureAwait(false);
        int rgCountOffset = 2 + System.Text.Encoding.UTF8.GetByteCount(tableName) + 2;
        BinaryPrimitives.WriteInt32LittleEndian(metaBlock.AsSpan(rgCountOffset, 4), meta.RowGroupCount);
        await _device.WriteBlockAsync(meta.RegionStartBlock, metaBlock, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads and decodes a single column from a specific row group.
    /// </summary>
    /// <param name="tableName">Table name.</param>
    /// <param name="columnName">Column name to read.</param>
    /// <param name="rowGroupIndex">Zero-based row group index.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Decoded raw column data as a single-element array for consistency.</returns>
    public async Task<byte[][]> ReadColumnAsync(string tableName, string columnName, int rowGroupIndex, CancellationToken ct = default)
    {
        if (!_tables.TryGetValue(tableName, out var meta))
            throw new InvalidOperationException($"Table '{tableName}' not found.");

        int colIdx = Array.FindIndex(meta.Columns, c => c.Name == columnName);
        if (colIdx < 0) throw new ArgumentException($"Column '{columnName}' not found in table '{tableName}'.", nameof(columnName));
        if (rowGroupIndex < 0 || rowGroupIndex >= meta.RowGroupCount)
            throw new ArgumentOutOfRangeException(nameof(rowGroupIndex));

        // Calculate block position
        long blockOffset = meta.RegionStartBlock + 1 + rowGroupIndex * meta.Columns.Length + colIdx;

        // Read the first block to get header
        var blockData = new byte[_blockSize];
        await _device.ReadBlockAsync(blockOffset, blockData, ct).ConfigureAwait(false);

        var encoding = (EncodingType)blockData[0];
        int rowCount = BinaryPrimitives.ReadInt32LittleEndian(blockData.AsSpan(1, 4));
        int dataLen = BinaryPrimitives.ReadInt32LittleEndian(blockData.AsSpan(5, 4));
        int headerSize = 9;

        // Validate dataLen before allocation to prevent OOM from corrupt/malicious on-disk values
        const int MaxColumnarDataLen = 256 * 1024 * 1024; // 256 MB upper bound
        if (dataLen < 0 || dataLen > MaxColumnarDataLen)
            throw new InvalidDataException(
                $"Columnar region block at offset {blockOffset} has invalid dataLen={dataLen}. Data may be corrupt.");

        // Read encoded data (may span multiple blocks)
        byte[] encodedData;
        if (headerSize + dataLen <= _blockSize)
        {
            encodedData = blockData.AsSpan(headerSize, dataLen).ToArray();
        }
        else
        {
            int totalBlocks = (headerSize + dataLen + _blockSize - 1) / _blockSize;
            var allData = new byte[totalBlocks * _blockSize];
            blockData.CopyTo(allData, 0);
            for (int b = 1; b < totalBlocks; b++)
            {
                await _device.ReadBlockAsync(blockOffset + b, allData.AsMemory(b * _blockSize, _blockSize), ct).ConfigureAwait(false);
            }
            encodedData = allData.AsSpan(headerSize, dataLen).ToArray();
        }

        // Decode
        var colDef = meta.Columns[colIdx];
        int valueWidth = GetValueWidth(colDef.Type);
        byte[] decoded = DecodeColumn(encoding, encodedData, valueWidth, rowCount);

        return new[] { decoded };
    }

    /// <summary>
    /// Scans selected columns across all row groups with optional zone map filtering.
    /// Yields one array per row group, with one entry per requested column.
    /// </summary>
    /// <param name="tableName">Table name.</param>
    /// <param name="columns">Column names to read.</param>
    /// <param name="filters">Optional zone map filters (one per column, null entries skip filtering).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async sequence of column data arrays (one per matching row group).</returns>
    public async IAsyncEnumerable<byte[][]> ScanColumnsAsync(
        string tableName,
        string[] columns,
        Predicate<ZoneMapEntry>[]? filters,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_tables.TryGetValue(tableName, out var meta))
            throw new InvalidOperationException($"Table '{tableName}' not found.");

        var colIndices = new int[columns.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            colIndices[i] = Array.FindIndex(meta.Columns, c => c.Name == columns[i]);
            if (colIndices[i] < 0)
                throw new ArgumentException($"Column '{columns[i]}' not found.", nameof(columns));
        }

        for (int rg = 0; rg < meta.RowGroupCount; rg++)
        {
            ct.ThrowIfCancellationRequested();

            // Check zone map filters for this row group
            bool skipRowGroup = false;
            if (filters != null)
            {
                for (int i = 0; i < columns.Length && i < filters.Length; i++)
                {
                    if (filters[i] == null) continue;

                    long blockOffset = meta.RegionStartBlock + 1 + rg * meta.Columns.Length + colIndices[i];
                    var blockData = new byte[_blockSize];
                    await _device.ReadBlockAsync(blockOffset, blockData, ct).ConfigureAwait(false);

                    int rowCount = BinaryPrimitives.ReadInt32LittleEndian(blockData.AsSpan(1, 4));
                    var colDef = meta.Columns[colIndices[i]];
                    int valueWidth = GetValueWidth(colDef.Type);

                    // Build approximate zone map from header
                    var entry = new ZoneMapEntry(0, 0, 0, rowCount, blockOffset, 1);
                    if (!filters[i](entry))
                    {
                        skipRowGroup = true;
                        break;
                    }
                }
            }

            if (skipRowGroup) continue;

            // Read all requested columns for this row group
            var result = new byte[columns.Length][];
            for (int i = 0; i < columns.Length; i++)
            {
                var colData = await ReadColumnAsync(tableName, columns[i], rg, ct).ConfigureAwait(false);
                result[i] = colData[0];
            }

            yield return result;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Gets the value width in bytes for a column type.
    /// </summary>
    public static int GetValueWidth(ColumnType type) => type switch
    {
        ColumnType.Int32 => 4,
        ColumnType.Int64 => 8,
        ColumnType.Float32 => 4,
        ColumnType.Float64 => 8,
        ColumnType.String => 64,    // Fixed-width for columnar storage
        ColumnType.Binary => 64,    // Fixed-width for columnar storage
        ColumnType.Boolean => 1,
        ColumnType.DateTime => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(type), $"Unknown column type: {type}"),
    };

    private static byte[] DecodeColumn(EncodingType encoding, byte[] encodedData, int valueWidth, int rowCount)
    {
        return encoding switch
        {
            EncodingType.RunLength => ColumnarEncoding.DecodeRunLength(encodedData, valueWidth, rowCount),
            EncodingType.Dictionary => DecodeDictionaryColumn(encodedData, valueWidth, rowCount),
            EncodingType.BitPacked => ColumnarEncoding.DecodeBitPacked(encodedData, rowCount),
            _ => encodedData,
        };
    }

    private static byte[] DecodeDictionaryColumn(byte[] encodedData, int valueWidth, int rowCount)
    {
        int dictLen = BinaryPrimitives.ReadInt32LittleEndian(encodedData.AsSpan(0, 4));
        var dictData = encodedData.AsSpan(4, dictLen);
        var indexData = encodedData.AsSpan(4 + dictLen);
        return ColumnarEncoding.DecodeDictionary(dictData, indexData, valueWidth, rowCount);
    }

    private static ZoneMapEntry BuildZoneMapEntry(byte[] rawData, int valueWidth, int rowCount, long extentStartBlock, int extentBlockCount)
    {
        long minVal = long.MaxValue;
        long maxVal = long.MinValue;
        int nullCount = 0;

        for (int i = 0; i < rowCount; i++)
        {
            var valueSlice = rawData.AsSpan(i * valueWidth, valueWidth);

            // Check for null (all zeros in nullable context)
            bool allZero = true;
            for (int b = 0; b < valueWidth; b++)
            {
                if (valueSlice[b] != 0) { allZero = false; break; }
            }
            if (allZero) nullCount++;

            long val;
            if (valueWidth <= 8)
            {
                val = valueWidth switch
                {
                    1 => valueSlice[0],
                    2 => BinaryPrimitives.ReadInt16LittleEndian(valueSlice),
                    4 => BinaryPrimitives.ReadInt32LittleEndian(valueSlice),
                    8 => BinaryPrimitives.ReadInt64LittleEndian(valueSlice),
                    _ => valueSlice[0],
                };
            }
            else
            {
                // For strings/binary, use XxHash64 for deterministic persistent hashing
                // (HashCode is randomized per-process and unsuitable for persistent zone maps)
                val = (long)System.IO.Hashing.XxHash64.HashToUInt64(valueSlice);
            }

            if (val < minVal) minVal = val;
            if (val > maxVal) maxVal = val;
        }

        return new ZoneMapEntry(minVal, maxVal, nullCount, rowCount, extentStartBlock, extentBlockCount);
    }

    private long FindFreeRegionStart(int blocksNeeded)
    {
        // Simple strategy: find the highest used region end and allocate after it
        long highest = FormatConstants.RegionDirectoryBlocks + FormatConstants.RegionDirectoryStartBlock + 2;
        var activeRegions = _regionDir.GetActiveRegions();
        foreach (var (_, ptr) in activeRegions)
        {
            long regionEnd = ptr.StartBlock + ptr.BlockCount;
            if (regionEnd > highest) highest = regionEnd;
        }
        return highest;
    }

    // ── Table Metadata ──────────────────────────────────────────────────

    private sealed record TableMetadata(
        string Name,
        ColumnDefinition[] Columns,
        long RegionStartBlock,
        long RegionBlockCount,
        int RowGroupCount,
        long NextFreeBlock);
}
