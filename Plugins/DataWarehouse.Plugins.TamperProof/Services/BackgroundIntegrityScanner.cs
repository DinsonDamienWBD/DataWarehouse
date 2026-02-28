// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.TamperProof;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.TamperProof.Services;

/// <summary>
/// Interface for background integrity scanning service.
/// Continuously scans storage for integrity violations and emits events when detected.
/// </summary>
public interface IBackgroundIntegrityScanner
{
    /// <summary>
    /// Starts the background scanning process.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Stops the background scanning process.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task StopAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the current status of the scanner.
    /// </summary>
    ScannerStatus GetStatus();

    /// <summary>
    /// Scans a specific block for integrity violations.
    /// </summary>
    /// <param name="blockId">The block ID (object ID) to scan.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ScanResult> ScanBlockAsync(Guid blockId, CancellationToken ct = default);

    /// <summary>
    /// Runs a full scan of all blocks in storage.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task<FullScanResult> RunFullScanAsync(CancellationToken ct = default);

    /// <summary>
    /// Event raised when an integrity violation is detected.
    /// </summary>
    event EventHandler<IntegrityViolationEventArgs>? ViolationDetected;
}

/// <summary>
/// Background service that continuously scans for integrity violations.
/// Implements batched scanning to avoid memory pressure and supports resumable scans.
/// </summary>
public class BackgroundIntegrityScanner : IBackgroundIntegrityScanner, IDisposable, IAsyncDisposable
{
    private readonly RecoveryService _recoveryService;
    private readonly IStorageProvider _metadataStorage;
    private readonly IStorageProvider _dataStorage;
    private readonly ILogger<BackgroundIntegrityScanner> _logger;
    private readonly TimeSpan _scanInterval;
    private readonly int _batchSize;

    private CancellationTokenSource? _cts;
    private Task? _scanTask;
    private readonly object _statusLock = new();
    private bool _disposed;

    // Scanner state
    private bool _isRunning;
    private DateTime? _lastScanStarted;
    private DateTime? _lastScanCompleted;
    private long _blocksScanned;
    private long _violationsFound;
    private double _scanProgressPercent;
    private Guid? _lastScannedBlockId;

    // Block tracking for resumable scans
    private readonly BoundedDictionary<Guid, DateTime> _scannedBlocks = new BoundedDictionary<Guid, DateTime>(1000);
    private readonly ConcurrentQueue<Guid> _pendingBlocks = new();

    /// <inheritdoc/>
    public event EventHandler<IntegrityViolationEventArgs>? ViolationDetected;

    /// <summary>
    /// Creates a new background integrity scanner instance.
    /// </summary>
    /// <param name="recoveryService">Recovery service for shard integrity checks.</param>
    /// <param name="metadataStorage">Metadata storage for loading manifests.</param>
    /// <param name="dataStorage">Data storage for loading shards.</param>
    /// <param name="logger">Logger instance.</param>
    /// <param name="scanInterval">Interval between scans. Defaults to 5 minutes.</param>
    /// <param name="batchSize">Number of blocks to scan per batch. Defaults to 100.</param>
    public BackgroundIntegrityScanner(
        RecoveryService recoveryService,
        IStorageProvider metadataStorage,
        IStorageProvider dataStorage,
        ILogger<BackgroundIntegrityScanner> logger,
        TimeSpan? scanInterval = null,
        int batchSize = 100)
    {
        _recoveryService = recoveryService ?? throw new ArgumentNullException(nameof(recoveryService));
        _metadataStorage = metadataStorage ?? throw new ArgumentNullException(nameof(metadataStorage));
        _dataStorage = dataStorage ?? throw new ArgumentNullException(nameof(dataStorage));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _scanInterval = scanInterval ?? TimeSpan.FromMinutes(5);
        _batchSize = batchSize > 0 ? batchSize : 100;
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        lock (_statusLock)
        {
            if (_isRunning)
            {
                _logger.LogWarning("Background integrity scanner is already running");
                return;
            }

            _isRunning = true;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }

        _logger.LogInformation(
            "Starting background integrity scanner with interval {Interval} and batch size {BatchSize}",
            _scanInterval, _batchSize);

        // Observe launch-time faults by attaching a continuation that logs them (finding 1027).
        // We store the task so StopAsync can await it; the continuation fires synchronously on fault.
        var scanCts = _cts; // Capture for the continuation closure.
        _scanTask = Task.Run(() => ScanLoopAsync(scanCts.Token), scanCts.Token)
            .ContinueWith(
                t => _logger.LogError(t.Exception, "Background integrity scanner terminated with unhandled exception"),
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        _logger.LogInformation("Stopping background integrity scanner");

        CancellationTokenSource? cts;
        Task? scanTask;

        lock (_statusLock)
        {
            if (!_isRunning)
            {
                _logger.LogDebug("Background integrity scanner is not running");
                return;
            }

            cts = _cts;
            scanTask = _scanTask;
            _isRunning = false;
        }

        if (cts != null)
        {
            await cts.CancelAsync();

            if (scanTask != null)
            {
                try
                {
                    // Wait for scan task to complete with timeout
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                    await scanTask.WaitAsync(combinedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogDebug("Scan loop cancelled");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error waiting for scan loop to complete");
                }
            }

            cts.Dispose();
        }

        lock (_statusLock)
        {
            _cts = null;
            _scanTask = null;
        }

        _logger.LogInformation("Background integrity scanner stopped");
    }

    /// <inheritdoc/>
    public ScannerStatus GetStatus()
    {
        lock (_statusLock)
        {
            return new ScannerStatus(
                IsRunning: _isRunning,
                LastScanStarted: _lastScanStarted,
                LastScanCompleted: _lastScanCompleted,
                BlocksScanned: _blocksScanned,
                ViolationsFound: _violationsFound,
                ScanProgressPercent: _scanProgressPercent);
        }
    }

    /// <inheritdoc/>
    public async Task<ScanResult> ScanBlockAsync(Guid blockId, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var startTime = DateTime.UtcNow;
        _logger.LogDebug("Scanning block {BlockId}", blockId);

        try
        {
            // Load manifest for the block
            var manifest = await LoadManifestAsync(blockId, ct);
            if (manifest == null)
            {
                return new ScanResult(
                    BlockId: blockId,
                    IsValid: false,
                    Violations: new List<ShardViolation>
                    {
                        new ShardViolation(
                            ShardIndex: -1,
                            ExpectedHash: string.Empty,
                            ActualHash: string.Empty,
                            ViolationType: "ManifestNotFound")
                    },
                    ScanDuration: DateTime.UtcNow - startTime);
            }

            // Verify shard integrity
            var checkResult = await _recoveryService.VerifyShardIntegrityAsync(manifest, ct);

            var violations = new List<ShardViolation>();

            foreach (var shardResult in checkResult.ShardResults)
            {
                if (shardResult.Status != ShardStatus.Valid)
                {
                    violations.Add(new ShardViolation(
                        ShardIndex: shardResult.ShardIndex,
                        ExpectedHash: shardResult.ExpectedHash,
                        ActualHash: shardResult.ActualHash ?? string.Empty,
                        ViolationType: shardResult.Status.ToString()));
                }
            }

            var isValid = violations.Count == 0;
            var scanDuration = DateTime.UtcNow - startTime;

            if (!isValid)
            {
                _logger.LogWarning(
                    "Integrity violations found for block {BlockId}: {ViolationCount} violations",
                    blockId, violations.Count);

                // Raise violation event
                OnViolationDetected(new IntegrityViolationEventArgs
                {
                    BlockId = blockId,
                    Violations = violations,
                    DetectedAt = DateTime.UtcNow
                });
            }
            else
            {
                _logger.LogDebug("Block {BlockId} passed integrity check", blockId);
            }

            // Update scanned blocks tracking
            _scannedBlocks[blockId] = DateTime.UtcNow;

            return new ScanResult(
                BlockId: blockId,
                IsValid: isValid,
                Violations: violations,
                ScanDuration: scanDuration);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning block {BlockId}", blockId);

            return new ScanResult(
                BlockId: blockId,
                IsValid: false,
                Violations: new List<ShardViolation>
                {
                    new ShardViolation(
                        ShardIndex: -1,
                        ExpectedHash: string.Empty,
                        ActualHash: string.Empty,
                        ViolationType: $"ScanError: {ex.Message}")
                },
                ScanDuration: DateTime.UtcNow - startTime);
        }
    }

    /// <inheritdoc/>
    public async Task<FullScanResult> RunFullScanAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var startedAt = DateTime.UtcNow;
        _logger.LogInformation("Starting full integrity scan");

        lock (_statusLock)
        {
            _lastScanStarted = startedAt;
            _scanProgressPercent = 0;
        }

        var corruptedBlockDetails = new List<ScanResult>();
        long totalBlocks = 0;
        long validBlocks = 0;
        long corruptedBlocks = 0;

        try
        {
            // Discover all blocks
            var allBlockIds = await DiscoverBlocksAsync(ct);
            totalBlocks = allBlockIds.Count;

            _logger.LogInformation("Discovered {Count} blocks for full scan", totalBlocks);

            // Process in batches
            var batchCount = 0;
            for (int i = 0; i < allBlockIds.Count; i += _batchSize)
            {
                ct.ThrowIfCancellationRequested();

                var batch = allBlockIds.Skip(i).Take(_batchSize).ToList();
                batchCount++;

                _logger.LogDebug(
                    "Processing batch {BatchNumber}: {Count} blocks",
                    batchCount, batch.Count);

                foreach (var blockId in batch)
                {
                    ct.ThrowIfCancellationRequested();

                    var result = await ScanBlockAsync(blockId, ct);

                    if (result.IsValid)
                    {
                        validBlocks++;
                    }
                    else
                    {
                        corruptedBlocks++;
                        corruptedBlockDetails.Add(result);
                    }

                    // Update progress
                    lock (_statusLock)
                    {
                        _blocksScanned++;
                        if (!result.IsValid)
                        {
                            _violationsFound += result.Violations.Count;
                        }
                        _scanProgressPercent = totalBlocks > 0
                            ? (double)_blocksScanned / totalBlocks * 100
                            : 100;
                        _lastScannedBlockId = blockId;
                    }
                }

                // Small delay between batches to avoid overwhelming the system
                if (i + _batchSize < allBlockIds.Count)
                {
                    await Task.Delay(100, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Full scan cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during full scan");
            throw;
        }

        var completedAt = DateTime.UtcNow;

        lock (_statusLock)
        {
            _lastScanCompleted = completedAt;
            _scanProgressPercent = 100;
        }

        _logger.LogInformation(
            "Full scan completed: {Total} blocks, {Valid} valid, {Corrupted} corrupted, Duration: {Duration}",
            totalBlocks, validBlocks, corruptedBlocks, completedAt - startedAt);

        return new FullScanResult(
            StartedAt: startedAt,
            CompletedAt: completedAt,
            TotalBlocks: totalBlocks,
            ValidBlocks: validBlocks,
            CorruptedBlocks: corruptedBlocks,
            CorruptedBlockDetails: corruptedBlockDetails);
    }

    /// <summary>
    /// Main scan loop that runs in the background.
    /// </summary>
    private async Task ScanLoopAsync(CancellationToken ct)
    {
        _logger.LogDebug("Scan loop started");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Run a full scan
                await RunFullScanAsync(ct);

                // Wait for next scan interval
                _logger.LogDebug("Waiting {Interval} until next scan", _scanInterval);
                await Task.Delay(_scanInterval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogDebug("Scan loop cancelled");
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in scan loop, will retry after interval");

                try
                {
                    await Task.Delay(_scanInterval, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        _logger.LogDebug("Scan loop exited");
    }

    /// <summary>
    /// Discovers all block IDs from the metadata storage.
    /// </summary>
    private async Task<List<Guid>> DiscoverBlocksAsync(CancellationToken ct)
    {
        var blockIds = new List<Guid>();

        try
        {
            // Try to load block index
            var indexUri = new Uri("metadata://manifests/_block_index.json");
            try
            {
                using var stream = await _metadataStorage.LoadAsync(indexUri);
                if (stream != null)
                {
                    string indexJson;
                    using (var r = new StreamReader(stream, leaveOpen: true))
                        indexJson = await r.ReadToEndAsync(ct);
                    var index = System.Text.Json.JsonSerializer.Deserialize<BlockIndex>(indexJson);
                    if (index?.BlockIds != null)
                    {
                        blockIds.AddRange(index.BlockIds);
                        _logger.LogDebug("Loaded {Count} blocks from index", blockIds.Count);
                        return blockIds;
                    }
                }
            }
            catch
            {

                // Index doesn't exist, fall through to scanning
                System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
            }

            // Fall back to scanning metadata storage
            // In a real implementation, this would iterate through storage
            // For now, we'll use the scanned blocks dictionary as a source
            blockIds.AddRange(_scannedBlocks.Keys);

            // Also check pending blocks queue
            while (_pendingBlocks.TryDequeue(out var pendingId))
            {
                if (!blockIds.Contains(pendingId))
                {
                    blockIds.Add(pendingId);
                }
            }

            _logger.LogDebug("Discovered {Count} blocks from tracked state", blockIds.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error discovering blocks, using tracked state");
        }

        return blockIds;
    }

    /// <summary>
    /// Loads a manifest for a given block ID.
    /// </summary>
    private async Task<TamperProofManifest?> LoadManifestAsync(Guid blockId, CancellationToken ct)
    {
        try
        {
            // Try to find the latest version
            for (int version = 1; version <= 100; version++)
            {
                var uri = new Uri($"metadata://manifests/{blockId}_v{version}.json");
                try
                {
                    using var stream = await _metadataStorage.LoadAsync(uri);
                    if (stream == null)
                    {
                        // No more versions, return the last found one
                        if (version > 1)
                        {
                            var prevUri = new Uri($"metadata://manifests/{blockId}_v{version - 1}.json");
                            using var prevStream = await _metadataStorage.LoadAsync(prevUri);
                            if (prevStream != null)
                            {
                                string json;
                                using (var r = new StreamReader(prevStream, leaveOpen: true))
                                    json = await r.ReadToEndAsync(ct);
                                return System.Text.Json.JsonSerializer.Deserialize<TamperProofManifest>(json);
                            }
                        }
                        break;
                    }

                    // Continue to next version to find latest
                }
                catch (FileNotFoundException)
                {
                    if (version > 1)
                    {
                        var prevUri = new Uri($"metadata://manifests/{blockId}_v{version - 1}.json");
                        using var prevStream = await _metadataStorage.LoadAsync(prevUri);
                        if (prevStream != null)
                        {
                            string json;
                            using (var r = new StreamReader(prevStream, leaveOpen: true))
                                json = await r.ReadToEndAsync(ct);
                            return System.Text.Json.JsonSerializer.Deserialize<TamperProofManifest>(json);
                        }
                    }
                    break;
                }
            }

            // Try version 1 directly
            var v1Uri = new Uri($"metadata://manifests/{blockId}_v1.json");
            using var v1Stream = await _metadataStorage.LoadAsync(v1Uri);
            if (v1Stream != null)
            {
                string json;
                using (var r = new StreamReader(v1Stream, leaveOpen: true))
                    json = await r.ReadToEndAsync(ct);
                return System.Text.Json.JsonSerializer.Deserialize<TamperProofManifest>(json);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error loading manifest for block {BlockId}", blockId);
        }

        return null;
    }

    /// <summary>
    /// Raises the ViolationDetected event.
    /// </summary>
    protected virtual void OnViolationDetected(IntegrityViolationEventArgs e)
    {
        ViolationDetected?.Invoke(this, e);
    }

    /// <summary>
    /// Adds a block ID to the pending scan queue.
    /// Called when new blocks are written to track them for scanning.
    /// </summary>
    /// <param name="blockId">Block ID to add.</param>
    public void TrackBlock(Guid blockId)
    {
        _pendingBlocks.Enqueue(blockId);
        _logger.LogDebug("Added block {BlockId} to pending scan queue", blockId);
    }

    /// <summary>
    /// Throws if the scanner has been disposed.
    /// </summary>
    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore().ConfigureAwait(false);
        Dispose(disposing: false);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;

        if (disposing)
        {
            _cts?.Dispose();
        }

        _disposed = true;
    }

    protected virtual async ValueTask DisposeAsyncCore()
    {
        if (_disposed) return;

        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error stopping scanner during disposal");
        }

        _cts?.Dispose();
        _disposed = true;
    }
}

/// <summary>
/// Status of the background integrity scanner.
/// </summary>
/// <param name="IsRunning">Whether the scanner is currently running.</param>
/// <param name="LastScanStarted">When the last scan started.</param>
/// <param name="LastScanCompleted">When the last scan completed.</param>
/// <param name="BlocksScanned">Total number of blocks scanned.</param>
/// <param name="ViolationsFound">Total number of violations found.</param>
/// <param name="ScanProgressPercent">Current scan progress percentage (0-100).</param>
public record ScannerStatus(
    bool IsRunning,
    DateTime? LastScanStarted,
    DateTime? LastScanCompleted,
    long BlocksScanned,
    long ViolationsFound,
    double ScanProgressPercent);

/// <summary>
/// Result of scanning a single block.
/// </summary>
/// <param name="BlockId">The block ID that was scanned.</param>
/// <param name="IsValid">Whether the block passed integrity verification.</param>
/// <param name="Violations">List of violations found, if any.</param>
/// <param name="ScanDuration">How long the scan took.</param>
public record ScanResult(
    Guid BlockId,
    bool IsValid,
    IReadOnlyList<ShardViolation> Violations,
    TimeSpan ScanDuration);

/// <summary>
/// Result of a full integrity scan.
/// </summary>
/// <param name="StartedAt">When the scan started.</param>
/// <param name="CompletedAt">When the scan completed.</param>
/// <param name="TotalBlocks">Total number of blocks scanned.</param>
/// <param name="ValidBlocks">Number of blocks that passed integrity checks.</param>
/// <param name="CorruptedBlocks">Number of blocks with integrity violations.</param>
/// <param name="CorruptedBlockDetails">Details of corrupted blocks.</param>
public record FullScanResult(
    DateTime StartedAt,
    DateTime CompletedAt,
    long TotalBlocks,
    long ValidBlocks,
    long CorruptedBlocks,
    IReadOnlyList<ScanResult> CorruptedBlockDetails);

/// <summary>
/// Details of a shard integrity violation.
/// </summary>
/// <param name="ShardIndex">Index of the affected shard (-1 if not shard-specific).</param>
/// <param name="ExpectedHash">Expected hash from the manifest.</param>
/// <param name="ActualHash">Actual computed hash (empty if unavailable).</param>
/// <param name="ViolationType">Type of violation (Corrupted, Missing, Error, etc.).</param>
public record ShardViolation(
    int ShardIndex,
    string ExpectedHash,
    string ActualHash,
    string ViolationType);

/// <summary>
/// Event arguments for integrity violation detection.
/// </summary>
public class IntegrityViolationEventArgs : EventArgs
{
    /// <summary>Block ID where violations were detected.</summary>
    public Guid BlockId { get; init; }

    /// <summary>List of violations found.</summary>
    public IReadOnlyList<ShardViolation> Violations { get; init; } = [];

    /// <summary>When the violations were detected.</summary>
    public DateTime DetectedAt { get; init; }
}

/// <summary>
/// Index of all known block IDs for efficient discovery.
/// </summary>
internal class BlockIndex
{
    /// <summary>List of known block IDs.</summary>
    public List<Guid>? BlockIds { get; set; }

    /// <summary>When the index was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
