using DataWarehouse.SDK.Contracts;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.Journal;

/// <summary>
/// Manages checkpointing for the write-ahead log.
/// Coordinates flushing dirty data and advancing the WAL tail.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 33: Virtual Disk Engine (VDE-03 WAL)")]
public sealed class CheckpointManager
{
    private readonly WriteAheadLog _wal;
    private readonly IBlockDevice _device;
    private readonly SemaphoreSlim _checkpointLock = new(1, 1);
    private long _lastCheckpointTimestamp;
    private bool _disposed;

    /// <summary>
    /// Threshold for automatic checkpoint (75% WAL utilization).
    /// </summary>
    private const double CheckpointThreshold = 0.75;

    /// <summary>
    /// Initializes a new checkpoint manager.
    /// </summary>
    /// <param name="wal">Write-ahead log instance.</param>
    /// <param name="device">Block device for data flushing.</param>
    public CheckpointManager(WriteAheadLog wal, IBlockDevice device)
    {
        ArgumentNullException.ThrowIfNull(wal);
        ArgumentNullException.ThrowIfNull(device);

        _wal = wal;
        _device = device;
        _lastCheckpointTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    /// <summary>
    /// Gets the timestamp of the last checkpoint (Unix seconds UTC).
    /// </summary>
    public long LastCheckpointTimestamp => Interlocked.Read(ref _lastCheckpointTimestamp);

    /// <summary>
    /// Determines whether a checkpoint should be triggered based on WAL utilization.
    /// </summary>
    /// <returns>True if checkpoint is recommended, false otherwise.</returns>
    public bool ShouldCheckpoint()
    {
        return _wal.WalUtilization > CheckpointThreshold;
    }

    /// <summary>
    /// Performs a checkpoint operation:
    /// 1. Records current WAL head sequence
    /// 2. Flushes all dirty data blocks to disk
    /// 3. Advances WAL tail (reclaims log space)
    /// 4. Updates checkpoint metadata
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Checkpoint failed.</exception>
    public async Task CheckpointAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _checkpointLock.WaitAsync(ct);
        try
        {
            // 1. Record current sequence number as checkpoint target
            long targetSequence = _wal.CurrentSequenceNumber;

            // 2. Flush device to ensure all data blocks are durable
            // (In a real implementation, this would flush specific dirty pages tracked by a buffer pool)
            await _device.FlushAsync(ct);

            // 3. Advance WAL tail via checkpoint
            await _wal.CheckpointAsync(ct);

            // 4. Update checkpoint timestamp
            Interlocked.Exchange(ref _lastCheckpointTimestamp, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }
        finally
        {
            _checkpointLock.Release();
        }
    }

    /// <summary>
    /// Automatically triggers a checkpoint if WAL utilization exceeds threshold.
    /// Intended to be called after each transaction commit.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task AutoCheckpointIfNeededAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (ShouldCheckpoint())
        {
            await CheckpointAsync(ct);
        }
    }

    /// <summary>
    /// Gets the time elapsed since the last checkpoint in seconds.
    /// </summary>
    public long SecondsSinceLastCheckpoint()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long last = Interlocked.Read(ref _lastCheckpointTimestamp);
        return now - last;
    }

    /// <summary>
    /// Disposes resources used by the checkpoint manager.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _checkpointLock.Dispose();
    }
}
