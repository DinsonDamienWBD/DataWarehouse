using DataWarehouse.SDK.Contracts;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.Integrity;

/// <summary>
/// Health status of the storage system based on corruption detection.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 33: Virtual Disk Engine (VDE-04 Checksumming)")]
public enum HealthStatus
{
    /// <summary>No corruption detected.</summary>
    Healthy,

    /// <summary>Corruption detected but data may be recoverable via redundancy.</summary>
    Degraded,

    /// <summary>Unrecoverable corruption detected.</summary>
    Critical
}

/// <summary>
/// Represents a detected corruption event.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 33: Virtual Disk Engine (VDE-04 Checksumming)")]
public sealed record CorruptionEvent
{
    /// <summary>Block number where corruption was detected.</summary>
    public required long BlockNumber { get; init; }

    /// <summary>Expected checksum value from checksum table.</summary>
    public required ulong ExpectedChecksum { get; init; }

    /// <summary>Actual checksum computed from block data.</summary>
    public required ulong ActualChecksum { get; init; }

    /// <summary>Timestamp when corruption was detected (UTC).</summary>
    public required DateTimeOffset DetectedAtUtc { get; init; }

    /// <summary>Human-readable description of the corruption.</summary>
    public required string Description { get; init; }
}

/// <summary>
/// Result of a corruption scan operation.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 33: Virtual Disk Engine (VDE-04 Checksumming)")]
public sealed record CorruptionScanResult
{
    /// <summary>Total number of blocks scanned.</summary>
    public required long TotalBlocksScanned { get; init; }

    /// <summary>Number of corrupted blocks found.</summary>
    public required long CorruptedBlocks { get; init; }

    /// <summary>Number of verified (non-corrupted) blocks.</summary>
    public required long VerifiedBlocks { get; init; }

    /// <summary>Duration of the scan operation.</summary>
    public required TimeSpan Duration { get; init; }

    /// <summary>List of all corruption events detected during scan.</summary>
    public required IReadOnlyList<CorruptionEvent> Events { get; init; }
}

/// <summary>
/// Detects and reports data corruption by verifying block checksums.
/// Provides batch scanning and real-time verification capabilities.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 33: Virtual Disk Engine (VDE-04 Checksumming)")]
public sealed class CorruptionDetector
{
    private const int MaxRecentEvents = 1000;

    private readonly IBlockChecksummer _checksummer;
    private readonly IBlockDevice _device;
    private readonly long _dataStartBlock;
    private readonly long _dataBlockCount;

    // Thread-safe event storage
    private readonly ConcurrentBag<CorruptionEvent> _recentEvents = new();
    private long _totalCorruptionCount;

    /// <summary>
    /// Event raised when corruption is detected during read verification.
    /// </summary>
    public event Action<CorruptionEvent>? OnCorruptionDetected;

    /// <summary>
    /// Gets the list of recent corruption events (bounded to last 1000).
    /// </summary>
    public IReadOnlyList<CorruptionEvent> RecentCorruptionEvents => _recentEvents.Take(MaxRecentEvents).ToList();

    /// <summary>
    /// Gets the total number of corruptions detected since initialization.
    /// </summary>
    public long TotalCorruptionCount => Interlocked.Read(ref _totalCorruptionCount);

    /// <summary>
    /// Initializes a new corruption detector.
    /// </summary>
    /// <param name="checksummer">Block checksummer for verification.</param>
    /// <param name="device">Block device for reading data blocks.</param>
    /// <param name="dataStartBlock">Starting block of data area.</param>
    /// <param name="dataBlockCount">Number of blocks in data area.</param>
    public CorruptionDetector(IBlockChecksummer checksummer, IBlockDevice device, long dataStartBlock, long dataBlockCount)
    {
        ArgumentNullException.ThrowIfNull(checksummer);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentOutOfRangeException.ThrowIfLessThan(dataBlockCount, 0);

        _checksummer = checksummer;
        _device = device;
        _dataStartBlock = dataStartBlock;
        _dataBlockCount = dataBlockCount;
    }

    /// <summary>
    /// Verifies a block on read and reports corruption if detected.
    /// Called by the VDE engine on every data block read.
    /// </summary>
    /// <param name="blockNumber">Block number being read.</param>
    /// <param name="blockData">Block data to verify.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if block is valid, false if corrupted.</returns>
    public async Task<bool> VerifyBlockOnReadAsync(long blockNumber, ReadOnlyMemory<byte> blockData, CancellationToken ct = default)
    {
        bool verified = await _checksummer.VerifyBlockAsync(blockNumber, blockData, ct);

        if (!verified)
        {
            // Corruption detected
            ulong actualChecksum = _checksummer.ComputeChecksum(blockData.Span);
            ulong expectedChecksum = await _checksummer.GetStoredChecksumAsync(blockNumber, ct);

            var corruptionEvent = new CorruptionEvent
            {
                BlockNumber = blockNumber,
                ExpectedChecksum = expectedChecksum,
                ActualChecksum = actualChecksum,
                DetectedAtUtc = DateTimeOffset.UtcNow,
                Description = $"Block {blockNumber}: checksum mismatch (expected 0x{expectedChecksum:X16}, got 0x{actualChecksum:X16})"
            };

            // Record event
            _recentEvents.Add(corruptionEvent);
            Interlocked.Increment(ref _totalCorruptionCount);

            // Trim recent events if too large
            while (_recentEvents.Count > MaxRecentEvents)
            {
                _recentEvents.TryTake(out _);
            }

            // Raise event
            OnCorruptionDetected?.Invoke(corruptionEvent);
        }

        return verified;
    }

    /// <summary>
    /// Performs a full integrity scan of all data blocks.
    /// </summary>
    /// <param name="progress">Optional progress reporter (0.0 to 1.0).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Scan results including all corruption events.</returns>
    public async Task<CorruptionScanResult> ScanAllBlocksAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        return await ScanBlockRangeAsync(_dataStartBlock, _dataBlockCount, progress, ct);
    }

    /// <summary>
    /// Performs an integrity scan of a specific block range.
    /// </summary>
    /// <param name="startBlock">Starting block number (absolute).</param>
    /// <param name="blockCount">Number of blocks to scan.</param>
    /// <param name="progress">Optional progress reporter (0.0 to 1.0).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Scan results including all corruption events.</returns>
    public async Task<CorruptionScanResult> ScanBlockRangeAsync(long startBlock, long blockCount, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var startTime = DateTimeOffset.UtcNow;
        var events = new List<CorruptionEvent>();
        long corruptedCount = 0;
        long verifiedCount = 0;

        byte[] blockBuffer = ArrayPool<byte>.Shared.Rent(_device.BlockSize);
        try
        {
            for (long i = 0; i < blockCount; i++)
            {
                ct.ThrowIfCancellationRequested();

                long blockNumber = startBlock + i;

                // Read block
                await _device.ReadBlockAsync(blockNumber, blockBuffer, ct);

                // Verify checksum
                bool verified = await _checksummer.VerifyBlockAsync(blockNumber, blockBuffer.AsMemory(0, _device.BlockSize), ct);

                if (verified)
                {
                    verifiedCount++;
                }
                else
                {
                    corruptedCount++;

                    // Create corruption event
                    ulong actualChecksum = _checksummer.ComputeChecksum(blockBuffer.AsSpan(0, _device.BlockSize));
                    ulong expectedChecksum = await _checksummer.GetStoredChecksumAsync(blockNumber, ct);

                    var corruptionEvent = new CorruptionEvent
                    {
                        BlockNumber = blockNumber,
                        ExpectedChecksum = expectedChecksum,
                        ActualChecksum = actualChecksum,
                        DetectedAtUtc = DateTimeOffset.UtcNow,
                        Description = $"Block {blockNumber}: checksum mismatch (expected 0x{expectedChecksum:X16}, got 0x{actualChecksum:X16})"
                    };

                    events.Add(corruptionEvent);
                }

                // Report progress
                progress?.Report((double)(i + 1) / blockCount);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(blockBuffer);
        }

        var duration = DateTimeOffset.UtcNow - startTime;

        return new CorruptionScanResult
        {
            TotalBlocksScanned = blockCount,
            CorruptedBlocks = corruptedCount,
            VerifiedBlocks = verifiedCount,
            Duration = duration,
            Events = events
        };
    }

    /// <summary>
    /// Gets the current health status based on corruption detection.
    /// </summary>
    /// <returns>Health status (Healthy/Degraded/Critical).</returns>
    public HealthStatus GetHealthStatus()
    {
        long count = Interlocked.Read(ref _totalCorruptionCount);

        if (count == 0)
        {
            return HealthStatus.Healthy;
        }

        // Simplified logic: any corruption is degraded
        // In production, would check for redundancy/recovery options
        // and determine if corruption is recoverable
        return HealthStatus.Degraded;
    }
}
