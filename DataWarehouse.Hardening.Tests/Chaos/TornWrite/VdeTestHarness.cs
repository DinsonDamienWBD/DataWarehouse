using DataWarehouse.SDK.VirtualDiskEngine;
using DataWarehouse.SDK.VirtualDiskEngine.Journal;

namespace DataWarehouse.Hardening.Tests.Chaos.TornWrite;

/// <summary>
/// VDE pipeline test setup with injectable crash points.
/// Boots a minimal VDE pipeline with real WAL and in-memory block device.
/// Provides methods to write known data patterns, crash mid-write, then
/// remount and verify recovery.
///
/// The harness manages two block devices:
/// - Data device: where application blocks are stored
/// - WAL device: dedicated device for write-ahead log entries
///
/// Both use in-memory backing that preserves state across simulated crashes,
/// enabling recovery verification without filesystem I/O.
///
/// Report: "Stage 3 - Steps 3-4 - Torn-Write Recovery"
/// </summary>
public sealed class VdeTestHarness : IAsyncDisposable
{
    /// <summary>Block size used across the test harness.</summary>
    public const int TestBlockSize = 4096;

    /// <summary>Number of data blocks in the test device.</summary>
    public const long DataBlockCount = 256;

    /// <summary>Number of blocks reserved for the WAL (header + data).</summary>
    public const long WalBlockCount = 64;

    /// <summary>Start block of the WAL area within the WAL device.</summary>
    public const long WalStartBlock = 0;

    /// <summary>Known data pattern for verification: repeating 0xDEADBEEF.</summary>
    internal static readonly byte[] DeadBeefPattern = CreatePattern(0xDE, 0xAD, 0xBE, 0xEF, TestBlockSize);

    /// <summary>Alternative pattern for multi-write tests: repeating 0xCAFEBABE.</summary>
    internal static readonly byte[] CafeBabePattern = CreatePattern(0xCA, 0xFE, 0xBA, 0xBE, TestBlockSize);

    /// <summary>Zero pattern representing clean/unwritten state.</summary>
    public static readonly byte[] ZeroPattern = new byte[TestBlockSize];

    private CrashableBlockDevice? _dataDevice;
    private CrashableBlockDevice? _walDevice;
    private WriteAheadLog? _wal;
    private readonly CrashSimulator _simulator;
    private bool _disposed;

    /// <summary>Gets the crash simulator controlling this harness.</summary>
    public CrashSimulator Simulator => _simulator;

    /// <summary>Gets the current data block device (may be null if not mounted).</summary>
    public CrashableBlockDevice? DataDevice => _dataDevice;

    /// <summary>Gets the current WAL instance (may be null if not mounted).</summary>
    public WriteAheadLog? Wal => _wal;

    /// <summary>
    /// Creates a new VDE test harness with the given crash simulator.
    /// Call <see cref="MountAsync"/> to initialize the VDE pipeline.
    /// </summary>
    public VdeTestHarness(CrashSimulator simulator)
    {
        _simulator = simulator ?? throw new ArgumentNullException(nameof(simulator));
    }

    /// <summary>
    /// Initializes (mounts) a fresh VDE pipeline with empty data and WAL.
    /// </summary>
    public async Task MountAsync(CancellationToken ct = default)
    {
        _dataDevice = new CrashableBlockDevice(TestBlockSize, DataBlockCount, _simulator, DecoratorStage.File);
        _walDevice = new CrashableBlockDevice(TestBlockSize, WalBlockCount, _simulator, DecoratorStage.Wal);
        _wal = await WriteAheadLog.CreateAsync(_walDevice, WalStartBlock, WalBlockCount, ct);
    }

    /// <summary>
    /// Remounts the VDE pipeline after a simulated crash, using the same underlying
    /// block devices (preserving any partial writes) but creating a fresh WAL instance
    /// that reads the existing WAL state for recovery.
    /// </summary>
    public async Task RemountAsync(CancellationToken ct = default)
    {
        // Dispose the old WAL (not the device -- it keeps its data)
        if (_wal != null)
        {
            await _wal.DisposeAsync();
        }

        // Reset the simulator so recovery operations don't get crashed
        _simulator.Reset();

        // Create new crash-free devices wrapping the same data
        // (The crashable device already preserves data in-memory)

        // Open existing WAL from the device that survived the crash
        _wal = await WriteAheadLog.OpenAsync(_walDevice!, WalStartBlock, WalBlockCount, ct);
    }

    /// <summary>
    /// Writes a known data pattern to the data device through the WAL transaction protocol.
    /// This is the atomic write path: WAL entry -> commit -> apply to data blocks.
    /// </summary>
    /// <param name="blockNumber">Target data block number.</param>
    /// <param name="data">Data to write (must be TestBlockSize bytes).</param>
    /// <param name="ct">Cancellation token (wired to crash simulator).</param>
    /// <returns>True if the write completed fully, false if interrupted by crash.</returns>
    public async Task<bool> WriteWithWalAsync(long blockNumber, byte[] data, CancellationToken ct = default)
    {
        if (_wal == null || _dataDevice == null)
            throw new InvalidOperationException("Harness not mounted. Call MountAsync first.");

        if (data.Length != TestBlockSize)
            throw new ArgumentException($"Data must be exactly {TestBlockSize} bytes.", nameof(data));

        try
        {
            // Read current data for before-image
            var beforeImage = new byte[TestBlockSize];
            await _dataDevice.ReadBlockAsync(blockNumber, beforeImage, ct);

            // Begin WAL transaction
            await using var txn = await _wal.BeginTransactionAsync(ct);

            // Log the write to WAL
            await txn.LogBlockWriteAsync(blockNumber, beforeImage, data, ct);

            // Commit WAL (linearization point)
            await txn.CommitAsync(ct);

            // Apply after-image to data device
            await _dataDevice.WriteBlockAsync(blockNumber, data, ct);

            // Checkpoint WAL (mark entries as applied)
            await _wal.CheckpointAsync(ct);

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// Performs crash recovery: replays committed WAL entries and applies them to data blocks.
    /// After replay, checkpoints the WAL to mark entries as applied.
    /// </summary>
    /// <returns>Number of blocks recovered from WAL replay.</returns>
    public async Task<int> RecoverAsync(CancellationToken ct = default)
    {
        if (_wal == null || _dataDevice == null)
            throw new InvalidOperationException("Harness not mounted. Call RemountAsync first.");

        var entries = await _wal.ReplayAsync(ct);
        int recoveredBlocks = 0;

        foreach (var entry in entries)
        {
            if (entry.Type == JournalEntryType.BlockWrite && entry.AfterImage != null)
            {
                // Apply the committed after-image to the data device
                await _dataDevice.WriteBlockAsync(entry.TargetBlockNumber, entry.AfterImage, ct);
                recoveredBlocks++;
            }
        }

        if (recoveredBlocks > 0)
        {
            await _wal.CheckpointAsync(ct);
        }

        return recoveredBlocks;
    }

    /// <summary>
    /// Verifies that a data block matches the expected pattern.
    /// </summary>
    /// <param name="blockNumber">Block to verify.</param>
    /// <param name="expectedData">Expected data pattern.</param>
    /// <returns>True if the block matches exactly.</returns>
    public bool VerifyBlock(long blockNumber, byte[] expectedData)
    {
        if (_dataDevice == null)
            throw new InvalidOperationException("Harness not mounted.");

        var actual = _dataDevice.GetBlockSnapshot(blockNumber);
        return actual.AsSpan().SequenceEqual(expectedData.AsSpan());
    }

    /// <summary>
    /// Verifies that a block is in one of the allowed states (either original or target data).
    /// Used after crash recovery to verify atomicity: data is either fully committed or fully rolled back.
    /// </summary>
    /// <param name="blockNumber">Block to verify.</param>
    /// <param name="originalData">Data before the write attempt.</param>
    /// <param name="targetData">Data that was being written.</param>
    /// <returns>True if block matches either original or target (atomic guarantee).</returns>
    public bool VerifyAtomicity(long blockNumber, byte[] originalData, byte[] targetData)
    {
        if (_dataDevice == null)
            throw new InvalidOperationException("Harness not mounted.");

        var actual = _dataDevice.GetBlockSnapshot(blockNumber);
        return actual.AsSpan().SequenceEqual(originalData.AsSpan()) ||
               actual.AsSpan().SequenceEqual(targetData.AsSpan());
    }

    /// <summary>
    /// Convenience method: write data, crash at configured point, remount, recover, verify.
    /// The full torn-write-and-recover lifecycle in one call.
    /// </summary>
    /// <param name="blockNumber">Target block number.</param>
    /// <param name="data">Data to write.</param>
    /// <returns>Recovery result with details about what happened.</returns>
    public async Task<RecoveryResult> WriteAndCrashAndRecoverAsync(long blockNumber, byte[] data)
    {
        var originalData = _dataDevice!.GetBlockSnapshot(blockNumber);

        // Attempt write (will crash at configured point)
        bool writeCompleted = await WriteWithWalAsync(blockNumber, data);

        if (writeCompleted)
        {
            // No crash happened -- write completed normally
            return new RecoveryResult
            {
                CrashOccurred = false,
                WriteCompleted = true,
                RecoveredBlocks = 0,
                DataConsistent = VerifyBlock(blockNumber, data)
            };
        }

        // Crash happened -- remount and recover
        await RemountAsync();
        int recovered = await RecoverAsync();

        // Verify atomicity: block must be either original or fully written
        bool isAtomic = VerifyAtomicity(blockNumber, originalData, data);

        return new RecoveryResult
        {
            CrashOccurred = true,
            WriteCompleted = false,
            RecoveredBlocks = recovered,
            DataConsistent = isAtomic,
            CrashRecord = _simulator.LastCrashRecord
        };
    }

    /// <summary>
    /// Creates a repeating byte pattern of the specified size.
    /// </summary>
    private static byte[] CreatePattern(byte b0, byte b1, byte b2, byte b3, int size)
    {
        var pattern = new byte[size];
        for (int i = 0; i < size; i += 4)
        {
            pattern[i] = b0;
            if (i + 1 < size) pattern[i + 1] = b1;
            if (i + 2 < size) pattern[i + 2] = b2;
            if (i + 3 < size) pattern[i + 3] = b3;
        }
        return pattern;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        if (_wal != null) await _wal.DisposeAsync();
        if (_dataDevice != null) await _dataDevice.DisposeAsync();
        if (_walDevice != null) await _walDevice.DisposeAsync();
    }
}

/// <summary>
/// Result of a write-crash-recover cycle.
/// </summary>
public sealed class RecoveryResult
{
    /// <summary>Whether a crash occurred during the write.</summary>
    public bool CrashOccurred { get; init; }

    /// <summary>Whether the write completed before any crash.</summary>
    public bool WriteCompleted { get; init; }

    /// <summary>Number of blocks recovered during WAL replay.</summary>
    public int RecoveredBlocks { get; init; }

    /// <summary>Whether the data is consistent after recovery (atomic: all-or-nothing).</summary>
    public bool DataConsistent { get; init; }

    /// <summary>Details of the crash that occurred, if any.</summary>
    public CrashRecord? CrashRecord { get; init; }
}
