using System.Buffers;
using System.Buffers.Binary;
using System.IO.Hashing;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.ModuleManagement;

/// <summary>
/// A single WAL entry tracking one block-level write operation with redo/undo data.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: WAL entry for journaled region operations (OMA-01)")]
public sealed class WalEntry
{
    /// <summary>The target block number where this write applies.</summary>
    public long TargetBlock { get; }

    /// <summary>Original block data before the write (undo record).</summary>
    public byte[] OriginalData { get; }

    /// <summary>New block data to write (redo record).</summary>
    public byte[] NewData { get; }

    /// <summary>Creates a WAL entry with target block, original data, and new data.</summary>
    public WalEntry(long targetBlock, byte[] originalData, byte[] newData)
    {
        TargetBlock = targetBlock;
        OriginalData = originalData ?? throw new ArgumentNullException(nameof(originalData));
        NewData = newData ?? throw new ArgumentNullException(nameof(newData));
    }
}

/// <summary>
/// Represents a WAL transaction containing a list of block-level write entries
/// that must be committed atomically.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: WAL transaction for journaled region operations (OMA-01)")]
public sealed class WalTransaction
{
    private readonly List<WalEntry> _entries = new();

    /// <summary>The sequence number assigned to this transaction.</summary>
    public long SequenceNumber { get; }

    /// <summary>All entries in this transaction.</summary>
    public IReadOnlyList<WalEntry> Entries => _entries;

    /// <summary>True if this transaction has been committed.</summary>
    public bool IsCommitted { get; internal set; }

    /// <summary>True if this transaction has been rolled back.</summary>
    public bool IsRolledBack { get; internal set; }

    internal WalTransaction(long sequenceNumber)
    {
        SequenceNumber = sequenceNumber;
    }

    internal void AddEntry(WalEntry entry)
    {
        if (IsCommitted)
            throw new InvalidOperationException("Cannot add entries to a committed transaction.");
        if (IsRolledBack)
            throw new InvalidOperationException("Cannot add entries to a rolled back transaction.");
        _entries.Add(entry);
    }
}

/// <summary>
/// Provides WAL-journaled atomic write operations for VDE region management.
/// Writes are first recorded in the metadata WAL region as redo records, then
/// applied to target blocks. A crash at any point between commit marker and
/// completion marker allows recovery by replaying redo records.
///
/// WAL entry on-disk format per entry:
/// [TargetBlock:8][DataLength:4][Data:N][XxHash64Checksum:8]
///
/// Transaction markers:
/// - Commit marker: [0xFFFFFFFF_FFFFFFFE:8][SequenceNumber:8][EntryCount:4][XxHash64:8]
/// - Completion marker: [0xFFFFFFFF_FFFFFFFF:8][SequenceNumber:8][0x00000000:4][XxHash64:8]
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 78: WAL-journaled region writer for online module addition (OMA-01)")]
public sealed class WalJournaledRegionWriter
{
    private readonly Stream _vdeStream;
    private readonly int _blockSize;
    private readonly long _metadataWalStartBlock;
    private readonly long _metadataWalBlockCount;
    private long _currentSequence;
    private long _walWriteOffset;
    private long _totalEntries;

    /// <summary>Marker value for WAL commit records.</summary>
    private const long CommitMarker = unchecked((long)0xFFFFFFFF_FFFFFFFE);

    /// <summary>Marker value for WAL completion records.</summary>
    private const long CompletionMarker = unchecked((long)0xFFFFFFFF_FFFFFFFF);

    /// <summary>
    /// Creates a new WalJournaledRegionWriter for the given VDE stream.
    /// </summary>
    /// <param name="vdeStream">The VDE stream for read/write operations.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="metadataWalStartBlock">First block of the metadata WAL region.</param>
    /// <param name="metadataWalBlockCount">Number of blocks in the metadata WAL region.</param>
    public WalJournaledRegionWriter(Stream vdeStream, int blockSize, long metadataWalStartBlock, long metadataWalBlockCount)
    {
        _vdeStream = vdeStream ?? throw new ArgumentNullException(nameof(vdeStream));
        if (blockSize < FormatConstants.MinBlockSize || blockSize > FormatConstants.MaxBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize));
        _blockSize = blockSize;
        _metadataWalStartBlock = metadataWalStartBlock;
        _metadataWalBlockCount = metadataWalBlockCount;
        _currentSequence = 0;

        // WAL write position starts after the header block
        _walWriteOffset = (metadataWalStartBlock + 1) * blockSize;
        _totalEntries = 0;

        ReadCurrentWalState();
    }

    /// <summary>
    /// Reads the current WAL header to initialize sequence numbers and write offset.
    /// </summary>
    private void ReadCurrentWalState()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            long headerOffset = _metadataWalStartBlock * _blockSize;
            _vdeStream.Seek(headerOffset, SeekOrigin.Begin);
            int totalRead = 0;
            while (totalRead < _blockSize)
            {
                int bytesRead = _vdeStream.Read(buffer, totalRead, _blockSize - totalRead);
                if (bytesRead == 0) break;
                totalRead += bytesRead;
            }

            if (totalRead >= WalHeader.SerializedSize)
            {
                var header = WalHeader.Deserialize(buffer.AsSpan(0, totalRead));
                _currentSequence = header.SequenceEnd;
                _totalEntries = header.TotalEntries;

                // Resume writing from tail offset if non-zero, otherwise after header block
                if (header.TailOffset > 0)
                    _walWriteOffset = header.TailOffset;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Begins a new WAL transaction. All subsequent Add* calls must use the returned transaction.
    /// </summary>
    /// <returns>A new WAL transaction.</returns>
    public WalTransaction BeginTransaction()
    {
        _currentSequence++;
        return new WalTransaction(_currentSequence);
    }

    /// <summary>
    /// Adds a region directory update to the transaction. Captures the current directory
    /// state as the undo record and the new state as the redo record.
    /// </summary>
    /// <param name="txn">The active transaction.</param>
    /// <param name="directory">The updated region directory to write.</param>
    public void AddRegionDirectoryUpdate(WalTransaction txn, RegionDirectory directory)
    {
        ArgumentNullException.ThrowIfNull(txn);
        ArgumentNullException.ThrowIfNull(directory);

        int totalSize = FormatConstants.RegionDirectoryBlocks * _blockSize;

        // Read current (original) directory blocks
        var originalData = ReadBlocks(FormatConstants.RegionDirectoryStartBlock, FormatConstants.RegionDirectoryBlocks);

        // Serialize new directory state
        var newData = new byte[totalSize];
        directory.Serialize(newData.AsSpan(), _blockSize);

        // Add one entry per directory block
        for (int i = 0; i < FormatConstants.RegionDirectoryBlocks; i++)
        {
            long targetBlock = FormatConstants.RegionDirectoryStartBlock + i;
            var origBlock = new byte[_blockSize];
            var newBlock = new byte[_blockSize];
            Array.Copy(originalData, i * _blockSize, origBlock, 0, _blockSize);
            Array.Copy(newData, i * _blockSize, newBlock, 0, _blockSize);
            txn.AddEntry(new WalEntry(targetBlock, origBlock, newBlock));
        }
    }

    /// <summary>
    /// Adds a region pointer table update to the transaction. Captures the current RPT
    /// state as the undo record and the new state as the redo record.
    /// </summary>
    /// <param name="txn">The active transaction.</param>
    /// <param name="table">The updated region pointer table to write.</param>
    public void AddRegionPointerTableUpdate(WalTransaction txn, RegionPointerTable table)
    {
        ArgumentNullException.ThrowIfNull(txn);
        ArgumentNullException.ThrowIfNull(table);

        // RPT is superblock block 1 (offset = 1 block from superblock start)
        long rptBlock = 1; // Block 1 in the superblock group

        var originalData = ReadBlocks(rptBlock, 1);

        var newData = new byte[_blockSize];
        RegionPointerTable.Serialize(table, newData.AsSpan(), _blockSize);

        txn.AddEntry(new WalEntry(rptBlock, originalData, newData));
    }

    /// <summary>
    /// Adds a superblock update to the transaction. Captures the current superblock
    /// state as the undo record and the new state as the redo record.
    /// </summary>
    /// <param name="txn">The active transaction.</param>
    /// <param name="superblock">The updated superblock to write.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    public void AddSuperblockUpdate(WalTransaction txn, SuperblockV2 superblock, int blockSize)
    {
        ArgumentNullException.ThrowIfNull(txn);

        // Primary superblock at block 0
        var originalData = ReadBlocks(0, 1);

        var newData = new byte[blockSize];
        SuperblockV2.Serialize(superblock, newData.AsSpan(), blockSize);

        txn.AddEntry(new WalEntry(0, originalData, newData));

        // Mirror superblock at block 4
        var mirrorOriginal = ReadBlocks(FormatConstants.SuperblockMirrorStartBlock, 1);
        var mirrorNew = new byte[blockSize];
        Array.Copy(newData, mirrorNew, blockSize);

        txn.AddEntry(new WalEntry(FormatConstants.SuperblockMirrorStartBlock, mirrorOriginal, mirrorNew));
    }

    /// <summary>
    /// Adds a bitmap block update to the transaction. Tracks the before/after state
    /// of a specific bitmap block for crash recovery.
    /// </summary>
    /// <param name="txn">The active transaction.</param>
    /// <param name="bitmapBlock">The block number of the bitmap block being modified.</param>
    /// <param name="oldBits">The original bitmap block data.</param>
    /// <param name="newBits">The updated bitmap block data.</param>
    public void AddBitmapUpdate(WalTransaction txn, long bitmapBlock, byte[] oldBits, byte[] newBits)
    {
        ArgumentNullException.ThrowIfNull(txn);
        ArgumentNullException.ThrowIfNull(oldBits);
        ArgumentNullException.ThrowIfNull(newBits);

        txn.AddEntry(new WalEntry(bitmapBlock, oldBits, newBits));
    }

    /// <summary>
    /// Commits a WAL transaction atomically. The commit protocol is:
    /// 1. Write WAL entries (redo records) to the metadata WAL region sequentially.
    /// 2. Write a WAL commit marker (indicates redo records are complete).
    /// 3. Apply all newData writes to their target blocks.
    /// 4. Write a WAL completion marker (indicates all target writes succeeded).
    /// If a crash occurs between steps 2 and 4, recovery replays the redo records.
    /// </summary>
    /// <param name="txn">The transaction to commit.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task CommitAsync(WalTransaction txn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(txn);
        if (txn.IsCommitted)
            throw new InvalidOperationException("Transaction has already been committed.");
        if (txn.IsRolledBack)
            throw new InvalidOperationException("Cannot commit a rolled-back transaction.");
        if (txn.Entries.Count == 0)
            throw new InvalidOperationException("Cannot commit an empty transaction.");

        long walCapacityBytes = _metadataWalBlockCount * _blockSize;
        long walStartByte = _metadataWalStartBlock * _blockSize;
        long walEndByte = walStartByte + walCapacityBytes;

        // Step 1: Write WAL entries (redo records)
        foreach (var entry in txn.Entries)
        {
            await WriteWalEntryAsync(entry, walEndByte, ct);
        }

        // Step 2: Write commit marker
        await WriteMarkerAsync(CommitMarker, txn.SequenceNumber, txn.Entries.Count, walEndByte, ct);

        // Step 3: Apply all new data to target blocks
        foreach (var entry in txn.Entries)
        {
            long targetOffset = entry.TargetBlock * _blockSize;
            _vdeStream.Seek(targetOffset, SeekOrigin.Begin);
            await _vdeStream.WriteAsync(entry.NewData.AsMemory(0, Math.Min(entry.NewData.Length, _blockSize)), ct);
        }
        await _vdeStream.FlushAsync(ct);

        // Step 4: Write completion marker
        await WriteMarkerAsync(CompletionMarker, txn.SequenceNumber, 0, walEndByte, ct);

        // Update WAL header
        _totalEntries += txn.Entries.Count;
        await UpdateWalHeaderAsync(txn.SequenceNumber, ct);

        txn.IsCommitted = true;
    }

    /// <summary>
    /// Rolls back a transaction by writing undo records (original data) to the target blocks.
    /// Use only if partial application has occurred and recovery is needed before commit.
    /// </summary>
    /// <param name="txn">The transaction to roll back.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task RollbackAsync(WalTransaction txn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(txn);
        if (txn.IsCommitted)
            throw new InvalidOperationException("Cannot rollback a committed transaction.");
        if (txn.IsRolledBack)
            throw new InvalidOperationException("Transaction has already been rolled back.");

        // Write undo records to restore original state (reverse order for safety)
        for (int i = txn.Entries.Count - 1; i >= 0; i--)
        {
            var entry = txn.Entries[i];
            long targetOffset = entry.TargetBlock * _blockSize;
            _vdeStream.Seek(targetOffset, SeekOrigin.Begin);
            await _vdeStream.WriteAsync(
                entry.OriginalData.AsMemory(0, Math.Min(entry.OriginalData.Length, _blockSize)), ct);
        }
        await _vdeStream.FlushAsync(ct);

        txn.IsRolledBack = true;
    }

    /// <summary>
    /// Writes a single WAL entry (redo record) to the WAL region.
    /// Format: [TargetBlock:8][DataLength:4][Data:N][XxHash64:8]
    /// </summary>
    private async Task WriteWalEntryAsync(WalEntry entry, long walEndByte, CancellationToken ct)
    {
        int dataLength = entry.NewData.Length;
        int entrySize = 8 + 4 + dataLength + 8; // TargetBlock + DataLength + Data + Checksum

        var buffer = ArrayPool<byte>.Shared.Rent(entrySize);
        try
        {
            var span = buffer.AsSpan(0, entrySize);
            BinaryPrimitives.WriteInt64LittleEndian(span, entry.TargetBlock);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(8), dataLength);
            entry.NewData.AsSpan(0, dataLength).CopyTo(span.Slice(12));

            // XxHash64 checksum of [TargetBlock + DataLength + Data]
            var checksumInput = span.Slice(0, 8 + 4 + dataLength);
            ulong checksum = XxHash64.HashToUInt64(checksumInput);
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(12 + dataLength), checksum);

            // Wrap around if needed (simple: just cap at WAL end)
            if (_walWriteOffset + entrySize > walEndByte)
            {
                // WAL is full; wrap to start after header block
                _walWriteOffset = (_metadataWalStartBlock + 1) * _blockSize;
            }

            _vdeStream.Seek(_walWriteOffset, SeekOrigin.Begin);
            await _vdeStream.WriteAsync(buffer.AsMemory(0, entrySize), ct);
            _walWriteOffset += entrySize;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Writes a transaction marker (commit or completion) to the WAL.
    /// Format: [MarkerValue:8][SequenceNumber:8][EntryCount:4][XxHash64:8]
    /// </summary>
    private async Task WriteMarkerAsync(long markerValue, long sequenceNumber, int entryCount,
        long walEndByte, CancellationToken ct)
    {
        const int markerSize = 8 + 8 + 4 + 8; // 28 bytes
        var buffer = ArrayPool<byte>.Shared.Rent(markerSize);
        try
        {
            var span = buffer.AsSpan(0, markerSize);
            BinaryPrimitives.WriteInt64LittleEndian(span, markerValue);
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(8), sequenceNumber);
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(16), entryCount);

            // Checksum of [Marker + Sequence + EntryCount]
            ulong checksum = XxHash64.HashToUInt64(span.Slice(0, 20));
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(20), checksum);

            if (_walWriteOffset + markerSize > walEndByte)
                _walWriteOffset = (_metadataWalStartBlock + 1) * _blockSize;

            _vdeStream.Seek(_walWriteOffset, SeekOrigin.Begin);
            await _vdeStream.WriteAsync(buffer.AsMemory(0, markerSize), ct);
            _walWriteOffset += markerSize;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        await _vdeStream.FlushAsync(ct);
    }

    /// <summary>
    /// Updates the WAL header block with current sequence and offset information.
    /// </summary>
    private async Task UpdateWalHeaderAsync(long newSequenceEnd, CancellationToken ct)
    {
        // Read the current WAL header
        var buffer = ArrayPool<byte>.Shared.Rent(_blockSize);
        try
        {
            long headerOffset = _metadataWalStartBlock * _blockSize;
            _vdeStream.Seek(headerOffset, SeekOrigin.Begin);

            int totalRead = 0;
            while (totalRead < _blockSize)
            {
                int bytesRead = await _vdeStream.ReadAsync(buffer.AsMemory(totalRead, _blockSize - totalRead));
                if (bytesRead == 0) break;
                totalRead += bytesRead;
            }

            // Deserialize, update, re-serialize
            WalHeader currentHeader;
            if (totalRead >= WalHeader.SerializedSize)
            {
                currentHeader = WalHeader.Deserialize(buffer.AsSpan(0, totalRead));
            }
            else
            {
                currentHeader = WalHeader.CreateMetadataWal(_metadataWalBlockCount);
            }

            var updatedHeader = new WalHeader(
                type: currentHeader.Type,
                version: currentHeader.Version,
                sequenceStart: currentHeader.SequenceStart,
                sequenceEnd: newSequenceEnd,
                headOffset: currentHeader.HeadOffset,
                tailOffset: _walWriteOffset,
                totalEntries: _totalEntries,
                maxBlocks: currentHeader.MaxBlocks,
                usedBlocks: currentHeader.UsedBlocks,
                createdTimestampUtc: currentHeader.CreatedTimestampUtc,
                lastCheckpointTimestamp: DateTimeOffset.UtcNow.Ticks,
                checkpointInterval: currentHeader.CheckpointInterval,
                isClean: false // not clean during active writes
            );

            Array.Clear(buffer, 0, _blockSize);
            WalHeader.Serialize(updatedHeader, buffer.AsSpan(0, _blockSize));

            // Write trailer for the header block
            UniversalBlockTrailer.Write(buffer.AsSpan(0, _blockSize), _blockSize,
                BlockTypeTags.MWAL, (uint)(newSequenceEnd & 0xFFFFFFFF));

            _vdeStream.Seek(headerOffset, SeekOrigin.Begin);
            await _vdeStream.WriteAsync(buffer.AsMemory(0, _blockSize), ct);
            await _vdeStream.FlushAsync(ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads one or more consecutive blocks from the VDE stream.
    /// </summary>
    private byte[] ReadBlocks(long startBlock, int blockCount)
    {
        int totalBytes = blockCount * _blockSize;
        var buffer = new byte[totalBytes];
        long offset = startBlock * _blockSize;
        _vdeStream.Seek(offset, SeekOrigin.Begin);

        int totalRead = 0;
        while (totalRead < totalBytes)
        {
            int bytesRead = _vdeStream.Read(buffer, totalRead, totalBytes - totalRead);
            if (bytesRead == 0)
                throw new InvalidDataException(
                    $"Unexpected end of stream reading blocks {startBlock}-{startBlock + blockCount - 1}.");
            totalRead += bytesRead;
        }

        return buffer;
    }
}
