using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.Journal;

/// <summary>
/// Represents an atomic transaction that groups multiple WAL operations.
/// All operations in a transaction commit or abort together.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 33: Virtual Disk Engine (VDE-03 WAL)")]
public sealed class WalTransaction : IAsyncDisposable
{
    private readonly long _transactionId;
    private readonly IWriteAheadLog _wal;
    private readonly List<JournalEntry> _pendingEntries = new();
    private bool _committed;
    private bool _aborted;
    private bool _disposed;

    /// <summary>
    /// Gets the transaction identifier.
    /// </summary>
    public long TransactionId => _transactionId;

    /// <summary>
    /// Gets whether this transaction has been committed.
    /// </summary>
    public bool IsCommitted => _committed;

    /// <summary>
    /// Gets whether this transaction has been aborted.
    /// </summary>
    public bool IsAborted => _aborted;

    internal WalTransaction(long transactionId, IWriteAheadLog wal)
    {
        _transactionId = transactionId;
        _wal = wal;
    }

    /// <summary>
    /// Logs a block write operation to this transaction.
    /// </summary>
    /// <param name="blockNumber">Block number being written.</param>
    /// <param name="beforeImage">Original block data before modification.</param>
    /// <param name="afterImage">New block data after modification.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Transaction already committed or aborted.</exception>
    public Task LogBlockWriteAsync(long blockNumber, ReadOnlyMemory<byte> beforeImage, ReadOnlyMemory<byte> afterImage, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_committed)
        {
            throw new InvalidOperationException("Cannot log operations to a committed transaction.");
        }
        if (_aborted)
        {
            throw new InvalidOperationException("Cannot log operations to an aborted transaction.");
        }

        var entry = new JournalEntry
        {
            SequenceNumber = -1, // Will be assigned by WAL on append
            TransactionId = _transactionId,
            Type = JournalEntryType.BlockWrite,
            TargetBlockNumber = blockNumber,
            BeforeImage = beforeImage.ToArray(),
            AfterImage = afterImage.ToArray()
        };

        _pendingEntries.Add(entry);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Logs a block deallocation operation to this transaction.
    /// </summary>
    /// <param name="blockNumber">Block number being freed.</param>
    /// <param name="beforeImage">Original block data before deallocation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Transaction already committed or aborted.</exception>
    public Task LogBlockFreeAsync(long blockNumber, ReadOnlyMemory<byte> beforeImage, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_committed)
        {
            throw new InvalidOperationException("Cannot log operations to a committed transaction.");
        }
        if (_aborted)
        {
            throw new InvalidOperationException("Cannot log operations to an aborted transaction.");
        }

        var entry = new JournalEntry
        {
            SequenceNumber = -1,
            TransactionId = _transactionId,
            Type = JournalEntryType.BlockFree,
            TargetBlockNumber = blockNumber,
            BeforeImage = beforeImage.ToArray(),
            AfterImage = null
        };

        _pendingEntries.Add(entry);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Logs an inode update operation to this transaction.
    /// </summary>
    /// <param name="blockNumber">Block number containing the inode.</param>
    /// <param name="beforeImage">Original inode data before update.</param>
    /// <param name="afterImage">New inode data after update.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Transaction already committed or aborted.</exception>
    public Task LogInodeUpdateAsync(long blockNumber, ReadOnlyMemory<byte> beforeImage, ReadOnlyMemory<byte> afterImage, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_committed)
        {
            throw new InvalidOperationException("Cannot log operations to a committed transaction.");
        }
        if (_aborted)
        {
            throw new InvalidOperationException("Cannot log operations to an aborted transaction.");
        }

        var entry = new JournalEntry
        {
            SequenceNumber = -1,
            TransactionId = _transactionId,
            Type = JournalEntryType.InodeUpdate,
            TargetBlockNumber = blockNumber,
            BeforeImage = beforeImage.ToArray(),
            AfterImage = afterImage.ToArray()
        };

        _pendingEntries.Add(entry);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Logs a B-Tree structural modification to this transaction.
    /// </summary>
    /// <param name="blockNumber">Block number containing the B-Tree node.</param>
    /// <param name="beforeImage">Original node data before modification.</param>
    /// <param name="afterImage">New node data after modification.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Transaction already committed or aborted.</exception>
    public Task LogBTreeModifyAsync(long blockNumber, ReadOnlyMemory<byte> beforeImage, ReadOnlyMemory<byte> afterImage, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_committed)
        {
            throw new InvalidOperationException("Cannot log operations to a committed transaction.");
        }
        if (_aborted)
        {
            throw new InvalidOperationException("Cannot log operations to an aborted transaction.");
        }

        var entry = new JournalEntry
        {
            SequenceNumber = -1,
            TransactionId = _transactionId,
            Type = JournalEntryType.BTreeModify,
            TargetBlockNumber = blockNumber,
            BeforeImage = beforeImage.ToArray(),
            AfterImage = afterImage.ToArray()
        };

        _pendingEntries.Add(entry);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Commits this transaction: writes commit marker, flushes WAL, applies after-images.
    /// The commit marker flush is the linearization point for durability.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Transaction already committed or aborted.</exception>
    public async Task CommitAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_committed)
        {
            throw new InvalidOperationException("Transaction already committed.");
        }
        if (_aborted)
        {
            throw new InvalidOperationException("Cannot commit an aborted transaction.");
        }

        // Append all pending entries to WAL
        foreach (var entry in _pendingEntries)
        {
            await _wal.AppendEntryAsync(entry, ct);
        }

        // Write commit marker
        var commitEntry = new JournalEntry
        {
            SequenceNumber = -1,
            TransactionId = _transactionId,
            Type = JournalEntryType.CommitTransaction,
            TargetBlockNumber = -1,
            BeforeImage = null,
            AfterImage = null
        };

        await _wal.AppendEntryAsync(commitEntry, ct);

        // Flush WAL to disk - this is the linearization point
        await _wal.FlushAsync(ct);

        // Now apply after-images to actual data blocks
        // (This would be done by the caller through the block device interface)
        // The WAL implementation coordinates this through its ApplyTransaction method

        _committed = true;
    }

    /// <summary>
    /// Aborts this transaction: writes abort marker and discards pending entries.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Transaction already committed or aborted.</exception>
    public async Task AbortAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_committed)
        {
            throw new InvalidOperationException("Cannot abort a committed transaction.");
        }
        if (_aborted)
        {
            return; // Already aborted
        }

        // Write abort marker
        var abortEntry = new JournalEntry
        {
            SequenceNumber = -1,
            TransactionId = _transactionId,
            Type = JournalEntryType.AbortTransaction,
            TargetBlockNumber = -1,
            BeforeImage = null,
            AfterImage = null
        };

        await _wal.AppendEntryAsync(abortEntry, ct);
        await _wal.FlushAsync(ct);

        _pendingEntries.Clear();
        _aborted = true;
    }

    /// <summary>
    /// Gets the pending entries for this transaction (internal use by WAL for commit application).
    /// </summary>
    internal IReadOnlyList<JournalEntry> GetPendingEntries() => _pendingEntries;

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Auto-abort if not committed
        if (!_committed && !_aborted)
        {
            try
            {
                await AbortAsync();
            }
            catch
            {
                // Ignore errors during auto-abort
            }
        }
    }
}
