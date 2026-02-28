using DataWarehouse.SDK.Contracts;
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.Journal;

/// <summary>
/// Write-ahead log implementation with circular buffer and crash recovery.
/// Ensures data modifications are logged before being applied to actual data blocks.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 33: Virtual Disk Engine (VDE-03 WAL)")]
public sealed class WriteAheadLog : IWriteAheadLog, IAsyncDisposable
{
    private readonly IBlockDevice _device;
    private readonly long _walStartBlock;
    private readonly long _walBlockCount;
    private readonly SemaphoreSlim _appendLock = new(1, 1);
    private readonly SemaphoreSlim _flushLock = new(1, 1);

    // Circular buffer state
    private long _headBlock; // Next write position (relative to walStartBlock)
    private long _tailBlock; // Oldest unreclaimed entry (relative to walStartBlock)
    private long _nextSequenceNumber;
    private long _nextTransactionId;
    private long _lastCheckpointSequence;
    private bool _needsRecovery;
    private bool _disposed;

    // WAL header block layout (block 0 of WAL area)
    // [Magic:4][HeadBlock:8][TailBlock:8][NextSequence:8][LastCheckpoint:8][NextTxId:8][Checksum:8]
    private const uint WalHeaderMagic = 0x57414C48u; // "WALH"
    private const int WalHeaderSize = 4 + 8 + 8 + 8 + 8 + 8 + 8;

    /// <inheritdoc/>
    public long CurrentSequenceNumber => Interlocked.Read(ref _nextSequenceNumber);

    /// <inheritdoc/>
    public long WalSizeBlocks => _walBlockCount - 1; // Exclude header block

    /// <inheritdoc/>
    public double WalUtilization
    {
        get
        {
            // Read head and tail atomically under the append lock to prevent a torn pair.
            // Both values must be observed consistently to compute a meaningful utilization ratio.
            long head, tail;
            _appendLock.Wait();
            try
            {
                head = _headBlock;
                tail = _tailBlock;
            }
            finally
            {
                _appendLock.Release();
            }

            long dataBlocks = _walBlockCount - 1; // Exclude header block

            if (head >= tail)
            {
                return (double)(head - tail) / dataBlocks;
            }
            else
            {
                // Wrapped around
                return (double)(dataBlocks - tail + head) / dataBlocks;
            }
        }
    }

    /// <inheritdoc/>
    public bool NeedsRecovery => _needsRecovery;

    /// <summary>
    /// Initializes a new WAL instance.
    /// </summary>
    /// <param name="device">Block device for WAL storage.</param>
    /// <param name="walStartBlock">Starting block of WAL area.</param>
    /// <param name="walBlockCount">Total number of blocks in WAL area.</param>
    public WriteAheadLog(IBlockDevice device, long walStartBlock, long walBlockCount)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(walBlockCount, 1); // Need at least header + 1 data block

        _device = device;
        _walStartBlock = walStartBlock;
        _walBlockCount = walBlockCount;

        // Initialize state (will be overwritten by LoadHeaderAsync or recovery)
        _headBlock = 1; // Start after header block
        _tailBlock = 1;
        _nextSequenceNumber = 1;
        _nextTransactionId = 1;
        _lastCheckpointSequence = 0;
        _needsRecovery = false;
    }

    /// <summary>
    /// Opens an existing WAL by reading the header block.
    /// </summary>
    public static async Task<WriteAheadLog> OpenAsync(IBlockDevice device, long walStartBlock, long walBlockCount, CancellationToken ct = default)
    {
        var wal = new WriteAheadLog(device, walStartBlock, walBlockCount);
        await wal.LoadHeaderAsync(ct);
        return wal;
    }

    /// <summary>
    /// Creates a new WAL by initializing the header block.
    /// </summary>
    public static async Task<WriteAheadLog> CreateAsync(IBlockDevice device, long walStartBlock, long walBlockCount, CancellationToken ct = default)
    {
        var wal = new WriteAheadLog(device, walStartBlock, walBlockCount);
        await wal.WriteHeaderAsync(ct);
        return wal;
    }

    /// <inheritdoc/>
    public async Task<WalTransaction> BeginTransactionAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        long txId = Interlocked.Increment(ref _nextTransactionId) - 1;

        var beginEntry = new JournalEntry
        {
            SequenceNumber = -1, // Will be assigned on append
            TransactionId = txId,
            Type = JournalEntryType.BeginTransaction,
            TargetBlockNumber = -1,
            BeforeImage = null,
            AfterImage = null
        };

        await AppendEntryAsync(beginEntry, ct);

        return new WalTransaction(txId, this);
    }

    /// <inheritdoc/>
    public async Task AppendEntryAsync(JournalEntry entry, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _appendLock.WaitAsync(ct);
        try
        {
            // Check if WAL is full
            await EnsureSpaceAsync(entry.GetSerializedSize(), ct);

            // Assign sequence number
            long seq = Interlocked.Increment(ref _nextSequenceNumber) - 1;
            entry.SequenceNumber = seq;

            // Serialize entry
            int entrySize = entry.GetSerializedSize();
            byte[] buffer = ArrayPool<byte>.Shared.Rent(entrySize);
            try
            {
                int written = entry.Serialize(buffer);

                // Write entry to WAL blocks
                await WriteEntryToBlocksAsync(buffer.AsMemory(0, written), ct);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        finally
        {
            _appendLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _flushLock.WaitAsync(ct);
        try
        {
            // Write WAL header with current state
            await WriteHeaderAsync(ct);

            // Flush device
            await _device.FlushAsync(ct);
        }
        finally
        {
            _flushLock.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<JournalEntry>> ReplayAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var committedEntries = new List<JournalEntry>();
        var transactions = new Dictionary<long, List<JournalEntry>>();

        // Scan from tail to head
        long currentBlock = _tailBlock;
        int blockOffset = 0;

        byte[] blockBuffer = ArrayPool<byte>.Shared.Rent(_device.BlockSize);
        try
        {
            while (currentBlock != _headBlock)
            {
                // Read block
                long absoluteBlock = _walStartBlock + currentBlock;
                await _device.ReadBlockAsync(absoluteBlock, blockBuffer, ct);

                // Parse entries from this block
                while (blockOffset < _device.BlockSize)
                {
                    if (JournalEntry.Deserialize(blockBuffer.AsSpan(blockOffset), out var entry, out int bytesRead))
                    {
                        blockOffset += bytesRead;

                        // Track transaction state
                        if (entry.Type == JournalEntryType.BeginTransaction)
                        {
                            transactions[entry.TransactionId] = new List<JournalEntry>();
                        }
                        else if (entry.Type == JournalEntryType.CommitTransaction)
                        {
                            // Collect all entries for this committed transaction
                            if (transactions.TryGetValue(entry.TransactionId, out var txEntries))
                            {
                                committedEntries.AddRange(txEntries);
                                transactions.Remove(entry.TransactionId);
                            }
                        }
                        else if (entry.Type == JournalEntryType.AbortTransaction)
                        {
                            // Discard aborted transaction
                            transactions.Remove(entry.TransactionId);
                        }
                        else
                        {
                            // Add entry to transaction
                            if (transactions.TryGetValue(entry.TransactionId, out var txEntries))
                            {
                                txEntries.Add(entry);
                            }
                        }
                    }
                    else
                    {
                        // No more valid entries in this block
                        break;
                    }
                }

                // Move to next block
                blockOffset = 0;
                currentBlock = (currentBlock + 1) % (_walBlockCount - 1);
                if (currentBlock == 0) currentBlock = 1; // Skip header block
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(blockBuffer);
        }

        _needsRecovery = committedEntries.Count > 0;
        return committedEntries;
    }

    /// <inheritdoc/>
    public async Task CheckpointAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _flushLock.WaitAsync(ct);
        try
        {
            // Record checkpoint
            _lastCheckpointSequence = _nextSequenceNumber;

            // Advance tail to head (all entries before this are applied)
            _tailBlock = _headBlock;

            // Write updated header
            await WriteHeaderAsync(ct);

            _needsRecovery = false;
        }
        finally
        {
            _flushLock.Release();
        }
    }

    /// <summary>
    /// Writes a journal entry to WAL blocks, handling block boundaries.
    /// </summary>
    private async Task WriteEntryToBlocksAsync(ReadOnlyMemory<byte> entryData, CancellationToken ct)
    {
        int offset = 0;
        byte[] blockBuffer = ArrayPool<byte>.Shared.Rent(_device.BlockSize);
        try
        {
            while (offset < entryData.Length)
            {
                long absoluteBlock = _walStartBlock + _headBlock;

                // Read current block (may have existing data)
                await _device.ReadBlockAsync(absoluteBlock, blockBuffer, ct);

                // Each WAL data block is written from the beginning (offset 0).
                // Entries that span multiple blocks are written across consecutive whole blocks.
                const int blockOffset = 0;
                int available = _device.BlockSize - blockOffset;
                int toWrite = Math.Min(available, entryData.Length - offset);

                // Copy entry data to block
                entryData.Slice(offset, toWrite).CopyTo(blockBuffer.AsMemory(blockOffset));

                // Write block
                await _device.WriteBlockAsync(absoluteBlock, blockBuffer.AsMemory(0, _device.BlockSize), ct);

                offset += toWrite;

                // Move to next block if needed
                if (offset < entryData.Length)
                {
                    _headBlock = (_headBlock + 1) % (_walBlockCount - 1);
                    if (_headBlock == 0) _headBlock = 1; // Skip header block
                }
            }

            // Update head position
            _headBlock = (_headBlock + 1) % (_walBlockCount - 1);
            if (_headBlock == 0) _headBlock = 1;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(blockBuffer);
        }
    }

    /// <summary>
    /// Ensures there is enough space in the WAL for a new entry.
    /// Triggers automatic checkpoint if needed.
    /// </summary>
    private async Task EnsureSpaceAsync(int requiredBytes, CancellationToken ct)
    {
        int requiredBlocks = (requiredBytes + _device.BlockSize - 1) / _device.BlockSize;
        long available = GetAvailableBlocks();

        if (requiredBlocks > available)
        {
            // Need checkpoint to free space
            if (WalUtilization > 0.75)
            {
                await CheckpointAsync(ct);
            }
            else
            {
                throw new InvalidOperationException("WAL is full and checkpoint cannot free enough space.");
            }
        }
    }

    /// <summary>
    /// Gets the number of available blocks in the circular buffer.
    /// </summary>
    private long GetAvailableBlocks()
    {
        long dataBlocks = _walBlockCount - 1;
        if (_headBlock >= _tailBlock)
        {
            return dataBlocks - (_headBlock - _tailBlock);
        }
        else
        {
            return _tailBlock - _headBlock;
        }
    }

    /// <summary>
    /// Writes the WAL header block.
    /// </summary>
    private async Task WriteHeaderAsync(CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(_device.BlockSize);
        try
        {
            Array.Clear(buffer, 0, _device.BlockSize);

            int offset = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), WalHeaderMagic);
            offset += 4;

            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset, 8), _headBlock);
            offset += 8;

            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset, 8), _tailBlock);
            offset += 8;

            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset, 8), _nextSequenceNumber);
            offset += 8;

            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset, 8), _lastCheckpointSequence);
            offset += 8;

            BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(offset, 8), _nextTransactionId);
            offset += 8;

            // Compute checksum
            ulong checksum = XxHash64.HashToUInt64(buffer.AsSpan(0, offset));
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset, 8), checksum);

            // Write header block
            await _device.WriteBlockAsync(_walStartBlock, buffer.AsMemory(0, _device.BlockSize), ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Loads the WAL header block.
    /// </summary>
    private async Task LoadHeaderAsync(CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(_device.BlockSize);
        try
        {
            await _device.ReadBlockAsync(_walStartBlock, buffer, ct);

            int offset = 0;
            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset, 4));
            offset += 4;

            if (magic != WalHeaderMagic)
            {
                throw new InvalidOperationException("Invalid WAL header magic.");
            }

            _headBlock = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(offset, 8));
            offset += 8;

            _tailBlock = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(offset, 8));
            offset += 8;

            _nextSequenceNumber = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(offset, 8));
            offset += 8;

            _lastCheckpointSequence = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(offset, 8));
            offset += 8;

            _nextTransactionId = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(offset, 8));
            offset += 8;

            ulong storedChecksum = BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(offset, 8));

            // Verify checksum
            ulong computedChecksum = XxHash64.HashToUInt64(buffer.AsSpan(0, offset));
            if (storedChecksum != computedChecksum)
            {
                throw new InvalidOperationException("WAL header checksum mismatch.");
            }

            _needsRecovery = _headBlock != _tailBlock;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Flush any pending entries before disposing to prevent data loss on restart
        try
        {
            await FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WriteAheadLog.DisposeAsync] Flush during dispose failed: {ex.Message}");
        }

        _appendLock.Dispose();
        _flushLock.Dispose();
    }
}
