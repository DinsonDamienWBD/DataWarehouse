using DataWarehouse.SDK.VirtualDiskEngine;
using DataWarehouse.SDK.VirtualDiskEngine.Journal;

namespace DataWarehouse.Hardening.Tests.Chaos.TornWrite;

/// <summary>
/// Identifies which stage of the VDE decorator chain a crash should be injected at.
/// Maps to the canonical chain: Cache - Integrity - Compression - Dedup - Encryption - WAL - RAID - File.
/// </summary>
public enum DecoratorStage
{
    /// <summary>Cache layer (L1/L2/L3 caching).</summary>
    Cache = 0,

    /// <summary>Integrity verification (checksums, tamper detection).</summary>
    Integrity = 1,

    /// <summary>Compression (LZ4, ZSTD, etc.).</summary>
    Compression = 2,

    /// <summary>Deduplication (content-addressed block dedup).</summary>
    Dedup = 3,

    /// <summary>Encryption (AES-256-GCM, etc.).</summary>
    Encryption = 4,

    /// <summary>Write-Ahead Log (WAL journaling for crash recovery).</summary>
    Wal = 5,

    /// <summary>RAID parity (mirror/stripe/parity computation).</summary>
    Raid = 6,

    /// <summary>File/block device (physical I/O).</summary>
    File = 7
}

/// <summary>
/// When during the write operation a crash should occur.
/// </summary>
public enum CrashTiming
{
    /// <summary>Crash before the write reaches the target decorator.</summary>
    BeforeWrite,

    /// <summary>Crash mid-write (partial data written).</summary>
    DuringWrite,

    /// <summary>Crash after write completes but before flush/sync.</summary>
    AfterWriteBeforeFlush
}

/// <summary>
/// Crash injection record tracking which crash point was actually triggered.
/// </summary>
public sealed class CrashRecord
{
    /// <summary>Decorator stage where the crash was injected.</summary>
    public DecoratorStage Stage { get; init; }

    /// <summary>Timing of the crash within the decorator stage.</summary>
    public CrashTiming Timing { get; init; }

    /// <summary>UTC timestamp when the crash was triggered.</summary>
    public DateTime TriggeredAt { get; init; }

    /// <summary>Block number being written when the crash occurred (-1 if N/A).</summary>
    public long BlockNumber { get; init; } = -1;

    /// <summary>Whether partial data was written before the crash.</summary>
    public bool PartialWriteOccurred { get; init; }
}

/// <summary>
/// Coyote-controlled crash injection at VDE decorator chain points.
/// Wraps a CancellationTokenSource that fires at a configurable point in the
/// decorator chain. Supports non-deterministic crash point selection via
/// Coyote's systematic testing engine for exhaustive schedule exploration.
///
/// Report: "Stage 3 - Steps 3-4 - Torn-Write Recovery"
/// </summary>
public sealed class CrashSimulator
{
    private readonly object _lock = new();
    private CancellationTokenSource? _crashCts;
    private DecoratorStage _targetStage;
    private CrashTiming _targetTiming;
    private int _writeCountBeforeCrash;
    private int _currentWriteCount;
    private bool _crashTriggered;
    private CrashRecord? _lastCrashRecord;

    /// <summary>
    /// Gets the record of the last crash that was triggered, or null if no crash has occurred.
    /// </summary>
    public CrashRecord? LastCrashRecord
    {
        get { lock (_lock) return _lastCrashRecord; }
    }

    /// <summary>
    /// Gets whether a crash has been triggered in the current simulation.
    /// </summary>
    public bool CrashTriggered
    {
        get { lock (_lock) return _crashTriggered; }
    }

    /// <summary>
    /// Configures the simulator to inject a crash at the specified decorator stage and timing.
    /// </summary>
    /// <param name="stage">Which decorator in the chain should crash.</param>
    /// <param name="timing">When during the write operation to crash.</param>
    /// <param name="writeCountBeforeCrash">Number of successful writes before the crash fires (0 = first write).</param>
    public void InjectAt(DecoratorStage stage, CrashTiming timing, int writeCountBeforeCrash = 0)
    {
        lock (_lock)
        {
            _crashCts?.Cancel();
            _crashCts?.Dispose();
            _crashCts = new CancellationTokenSource();
            _targetStage = stage;
            _targetTiming = timing;
            _writeCountBeforeCrash = writeCountBeforeCrash;
            _currentWriteCount = 0;
            _crashTriggered = false;
            _lastCrashRecord = null;
        }
    }

    /// <summary>
    /// Gets a CancellationToken that will be cancelled when the crash fires.
    /// Wire this into the IBlockDevice wrapper to simulate process crash.
    /// </summary>
    public CancellationToken CrashToken
    {
        get
        {
            lock (_lock)
            {
                return _crashCts?.Token ?? CancellationToken.None;
            }
        }
    }

    /// <summary>
    /// Called by the CrashableBlockDevice at each decorator boundary to check
    /// whether a crash should fire. If the current stage and write count match
    /// the configured injection point, triggers cancellation.
    /// </summary>
    /// <param name="stage">Current decorator stage being executed.</param>
    /// <param name="blockNumber">Block number being written.</param>
    /// <param name="partialWrite">Whether partial data has already been written.</param>
    /// <returns>True if crash was triggered, false otherwise.</returns>
    public bool CheckAndTrigger(DecoratorStage stage, long blockNumber, bool partialWrite = false)
    {
        lock (_lock)
        {
            if (_crashTriggered || _crashCts == null)
                return false;

            if (stage != _targetStage)
                return false;

            if (_currentWriteCount < _writeCountBeforeCrash)
            {
                _currentWriteCount++;
                return false;
            }

            _crashTriggered = true;
            _lastCrashRecord = new CrashRecord
            {
                Stage = stage,
                Timing = _targetTiming,
                TriggeredAt = DateTime.UtcNow,
                BlockNumber = blockNumber,
                PartialWriteOccurred = partialWrite
            };

            _crashCts.Cancel();
            return true;
        }
    }

    /// <summary>
    /// Selects a random crash point using Coyote non-deterministic choice or Random fallback.
    /// Used in systematic exploration tests to cover all possible crash schedules.
    /// </summary>
    /// <param name="random">Random source (Coyote-controlled or standard).</param>
    /// <returns>Tuple of (stage, timing) for the selected crash point.</returns>
    public static (DecoratorStage Stage, CrashTiming Timing) SelectRandomCrashPoint(Random random)
    {
        var stages = Enum.GetValues<DecoratorStage>();
        var timings = Enum.GetValues<CrashTiming>();

        var stage = stages[random.Next(stages.Length)];
        var timing = timings[random.Next(timings.Length)];

        return (stage, timing);
    }

    /// <summary>
    /// Resets the simulator state for a new test iteration.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _crashCts?.Cancel();
            _crashCts?.Dispose();
            _crashCts = null;
            _crashTriggered = false;
            _currentWriteCount = 0;
            _lastCrashRecord = null;
        }
    }
}

/// <summary>
/// In-memory IBlockDevice that wraps crash injection. Preserves block data state
/// across simulated "crashes" (CTS cancellation) so that recovery can read the
/// partially-written or pre-crash state of the device.
/// </summary>
public sealed class CrashableBlockDevice : IBlockDevice
{
    private readonly byte[][] _blocks;
    private readonly CrashSimulator _simulator;
    private readonly DecoratorStage _deviceStage;
    private bool _disposed;

    /// <inheritdoc/>
    public int BlockSize { get; }

    /// <inheritdoc/>
    public long BlockCount { get; }

    /// <summary>
    /// Creates a new crashable in-memory block device.
    /// </summary>
    /// <param name="blockSize">Size of each block in bytes.</param>
    /// <param name="blockCount">Number of blocks.</param>
    /// <param name="simulator">Crash simulator controlling injection.</param>
    /// <param name="deviceStage">Which decorator stage this device represents (default: File).</param>
    public CrashableBlockDevice(int blockSize, long blockCount, CrashSimulator simulator,
        DecoratorStage deviceStage = DecoratorStage.File)
    {
        BlockSize = blockSize;
        BlockCount = blockCount;
        _simulator = simulator;
        _deviceStage = deviceStage;
        _blocks = new byte[blockCount][];
        for (long i = 0; i < blockCount; i++)
        {
            _blocks[i] = new byte[blockSize];
        }
    }

    /// <inheritdoc/>
    public Task ReadBlockAsync(long blockNumber, Memory<byte> buffer, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(blockNumber);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(blockNumber, BlockCount);

        _blocks[blockNumber].AsSpan().CopyTo(buffer.Span);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task WriteBlockAsync(long blockNumber, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(blockNumber);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(blockNumber, BlockCount);

        // Check for BeforeWrite crash
        if (_simulator.CheckAndTrigger(_deviceStage, blockNumber, partialWrite: false))
        {
            throw new OperationCanceledException("Simulated crash before write", _simulator.CrashToken);
        }

        // Simulate partial write for DuringWrite timing
        var targetTiming = _simulator.LastCrashRecord?.Timing;
        if (targetTiming == null)
        {
            // Check for DuringWrite crash: write half the data then crash
            int halfSize = data.Length / 2;
            data.Span.Slice(0, halfSize).CopyTo(_blocks[blockNumber].AsSpan());

            if (_simulator.CheckAndTrigger(_deviceStage, blockNumber, partialWrite: true))
            {
                // Half the data is already written - torn write!
                throw new OperationCanceledException("Simulated crash during write", _simulator.CrashToken);
            }

            // Complete the write
            data.Span.CopyTo(_blocks[blockNumber].AsSpan());
        }
        else
        {
            // Normal write (crash already happened or different stage)
            data.Span.CopyTo(_blocks[blockNumber].AsSpan());
        }

        // Check for AfterWriteBeforeFlush crash
        if (_simulator.CheckAndTrigger(_deviceStage, blockNumber, partialWrite: false))
        {
            throw new OperationCanceledException("Simulated crash after write before flush", _simulator.CrashToken);
        }

        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task FlushAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets a snapshot of a block's current data (for verification).
    /// </summary>
    public byte[] GetBlockSnapshot(long blockNumber)
    {
        var snapshot = new byte[BlockSize];
        _blocks[blockNumber].AsSpan().CopyTo(snapshot);
        return snapshot;
    }

    /// <summary>
    /// Fills a block with a known pattern (for test setup).
    /// </summary>
    public void FillBlock(long blockNumber, byte pattern)
    {
        Array.Fill(_blocks[blockNumber], pattern);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }
}
