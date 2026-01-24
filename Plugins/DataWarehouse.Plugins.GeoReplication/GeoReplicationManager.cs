using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.GeoReplication;

/// <summary>
/// Manages geo-replication across multiple regions.
/// Handles region lifecycle, health monitoring, conflict resolution, and consistency levels.
/// </summary>
internal sealed class GeoReplicationManager : IDisposable
{
    private readonly IKernelContext? _context;
    private readonly bool _enableConsensus;
    private readonly ConcurrentDictionary<string, ManagedRegion> _regions = new();
    private readonly ConcurrentDictionary<string, List<ConflictRecord>> _conflicts = new();
    private readonly SemaphoreSlim _regionLock = new(1, 1);

    private ConsistencyLevel _consistencyLevel = ConsistencyLevel.Eventual;
    private long? _bandwidthThrottle;
    private long _conflictsDetected = 0;
    private long _conflictsResolved;

    private CancellationTokenSource? _cts;
    private Task? _healthCheckTask;
    private Task? _replicationTask;
    private HttpClient? _httpClient;
    private bool _isRunning;
    private bool _disposed;

    private const int HealthCheckIntervalMs = 10_000;
    private const int ReplicationIntervalMs = 1_000;
    private const int HealthCheckTimeoutMs = 5_000;

    /// <summary>
    /// Event triggered when a conflict is detected.
    /// </summary>
    public event EventHandler<ConflictResolvedEventArgs>? ConflictResolved;

    /// <summary>
    /// Event triggered when region health changes.
    /// </summary>
    public event EventHandler<RegionHealthChangedEventArgs>? RegionHealthChanged;

    public GeoReplicationManager(IKernelContext? context, bool enableConsensus = true)
    {
        _context = context;
        _enableConsensus = enableConsensus;
    }

    /// <summary>
    /// Starts the manager's background tasks.
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        if (_isRunning) return;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        _isRunning = true;

        // Start background health monitoring
        _healthCheckTask = RunHealthCheckLoopAsync(_cts.Token);

        // Start background replication queue processor
        _replicationTask = RunReplicationLoopAsync(_cts.Token);

        await Task.CompletedTask;
    }

    /// <summary>
    /// Stops the manager and cleans up resources.
    /// </summary>
    public async Task StopAsync()
    {
        if (!_isRunning) return;

        _isRunning = false;
        _cts?.Cancel();

        var tasks = new List<Task>();
        if (_healthCheckTask != null) tasks.Add(_healthCheckTask);
        if (_replicationTask != null) tasks.Add(_replicationTask);

        if (tasks.Count > 0)
        {
            await Task.WhenAll(tasks.Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default)));
        }

        _httpClient?.Dispose();
        _httpClient = null;
        _cts?.Dispose();
        _cts = null;
    }

    public async Task<RegionOperationResult> AddRegionAsync(
        string regionId,
        string endpoint,
        RegionConfig config,
        StorageProviderPluginBase storageProvider,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        await _regionLock.WaitAsync(ct);
        try
        {
            if (_regions.ContainsKey(regionId))
            {
                return new RegionOperationResult
                {
                    Success = false,
                    RegionId = regionId,
                    ErrorMessage = $"Region '{regionId}' already exists",
                    Duration = stopwatch.Elapsed
                };
            }

            var region = new ManagedRegion
            {
                RegionId = regionId,
                Endpoint = endpoint,
                Config = config,
                StorageProvider = storageProvider,
                AddedAt = DateTime.UtcNow,
                Health = RegionHealth.Unknown,
                LastHealthCheck = DateTime.MinValue,
                ReplicationLag = TimeSpan.Zero,
                PendingOperations = 0
            };

            // Perform initial health check
            await CheckSingleRegionHealthAsync(region, ct);

            _regions[regionId] = region;

            return new RegionOperationResult
            {
                Success = true,
                RegionId = regionId,
                Duration = stopwatch.Elapsed
            };
        }
        finally
        {
            _regionLock.Release();
            stopwatch.Stop();
        }
    }

    public async Task<RegionOperationResult> RemoveRegionAsync(
        string regionId,
        bool drainFirst = true,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        await _regionLock.WaitAsync(ct);
        try
        {
            if (!_regions.TryGetValue(regionId, out var region))
            {
                return new RegionOperationResult
                {
                    Success = false,
                    RegionId = regionId,
                    ErrorMessage = $"Region '{regionId}' not found",
                    Duration = stopwatch.Elapsed
                };
            }

            if (drainFirst && region.PendingOperations > 0)
            {
                // Wait for pending operations to complete with timeout
                var drainTimeout = TimeSpan.FromSeconds(30);
                var drainStart = DateTime.UtcNow;

                while (region.PendingOperations > 0 && DateTime.UtcNow - drainStart < drainTimeout)
                {
                    await Task.Delay(100, ct);
                }
            }

            _regions.TryRemove(regionId, out _);

            return new RegionOperationResult
            {
                Success = true,
                RegionId = regionId,
                Duration = stopwatch.Elapsed
            };
        }
        finally
        {
            _regionLock.Release();
            stopwatch.Stop();
        }
    }

    public Task<MultiRegionReplicationStatus> GetReplicationStatusAsync(CancellationToken ct = default)
    {
        var regions = _regions.Values.ToList();
        var regionStatuses = new Dictionary<string, RegionStatus>();

        foreach (var region in regions)
        {
            regionStatuses[region.RegionId] = new RegionStatus
            {
                RegionId = region.RegionId,
                Endpoint = region.Endpoint,
                Health = region.Health,
                ReplicationLag = region.ReplicationLag,
                PendingOperations = region.PendingOperations,
                LastSuccessfulSync = region.LastSuccessfulSync,
                BytesTransferredLastMinute = region.BytesTransferredLastMinute,
                IsWitness = region.Config.IsWitness,
                Priority = region.Config.Priority
            };
        }

        var status = new MultiRegionReplicationStatus
        {
            TotalRegions = regions.Count,
            HealthyRegions = regions.Count(r => r.Health == RegionHealth.Healthy),
            ConsistencyLevel = _consistencyLevel,
            Regions = regionStatuses,
            PendingOperations = regions.Sum(r => r.PendingOperations),
            ConflictsDetected = _conflictsDetected,
            ConflictsResolved = _conflictsResolved,
            MaxReplicationLag = regions.Any() ? regions.Max(r => r.ReplicationLag) : TimeSpan.Zero,
            BandwidthThrottle = _bandwidthThrottle
        };

        return Task.FromResult(status);
    }

    public async Task<SyncOperationResult> ForceSyncAsync(
        string key,
        byte[] value,
        string[]? targetRegions = null,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var regionResults = new Dictionary<string, SyncResult>();

        var regions = targetRegions != null
            ? _regions.Values.Where(r => targetRegions.Contains(r.RegionId)).ToList()
            : _regions.Values.Where(r => !r.Config.IsWitness).ToList();

        var tasks = regions.Select(async region =>
        {
            var regionStopwatch = Stopwatch.StartNew();
            try
            {
                Interlocked.Increment(ref region.PendingOperations);

                // Simulate replication to region
                if (_httpClient != null && !string.IsNullOrEmpty(region.Endpoint))
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(TimeSpan.FromSeconds(10));

                    // In production, this would be a real HTTP call to replicate data
                    await Task.Delay(10, cts.Token); // Simulate network latency
                }

                region.LastSuccessfulSync = DateTime.UtcNow;
                Interlocked.Add(ref region.BytesTransferredLastMinute, value.Length);

                return (region.RegionId, new SyncResult
                {
                    Success = true,
                    Duration = regionStopwatch.Elapsed
                });
            }
            catch (Exception ex)
            {
                return (region.RegionId, new SyncResult
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    Duration = regionStopwatch.Elapsed
                });
            }
            finally
            {
                Interlocked.Decrement(ref region.PendingOperations);
                regionStopwatch.Stop();
            }
        });

        var results = await Task.WhenAll(tasks);
        foreach (var (regionId, result) in results)
        {
            regionResults[regionId] = result;
        }

        stopwatch.Stop();

        // Check consistency requirements
        var successCount = regionResults.Count(r => r.Value.Success);
        var overallSuccess = _consistencyLevel switch
        {
            ConsistencyLevel.Eventual => true,
            ConsistencyLevel.Local => successCount >= 1,
            ConsistencyLevel.Quorum => successCount >= (regions.Count / 2) + 1,
            ConsistencyLevel.Strong => successCount == regions.Count,
            _ => true
        };

        return new SyncOperationResult
        {
            Success = overallSuccess,
            Key = key,
            RegionResults = regionResults,
            Duration = stopwatch.Elapsed
        };
    }

    public Task<ConflictResolutionResult> ResolveConflictAsync(
        string key,
        ConflictResolutionStrategy strategy,
        string? winningRegion = null,
        CancellationToken ct = default)
    {
        if (!_conflicts.TryGetValue(key, out var conflicts) || conflicts.Count == 0)
        {
            return Task.FromResult(new ConflictResolutionResult
            {
                Success = false,
                Key = key,
                StrategyUsed = strategy,
                ErrorMessage = $"No conflict found for key '{key}'"
            });
        }

        string resolvedRegion;

        switch (strategy)
        {
            case ConflictResolutionStrategy.LastWriteWins:
                resolvedRegion = conflicts.OrderByDescending(c => c.Timestamp).First().RegionId;
                break;

            case ConflictResolutionStrategy.HighestPriorityWins:
                resolvedRegion = conflicts
                    .Where(c => _regions.TryGetValue(c.RegionId, out _))
                    .OrderByDescending(c => _regions.TryGetValue(c.RegionId, out var r) ? r.Config.Priority : 0)
                    .FirstOrDefault()?.RegionId ?? conflicts.First().RegionId;
                break;

            case ConflictResolutionStrategy.Manual:
                if (string.IsNullOrEmpty(winningRegion))
                {
                    return Task.FromResult(new ConflictResolutionResult
                    {
                        Success = false,
                        Key = key,
                        StrategyUsed = strategy,
                        ErrorMessage = "winningRegion must be specified for Manual strategy"
                    });
                }
                resolvedRegion = winningRegion;
                break;

            case ConflictResolutionStrategy.Merge:
            case ConflictResolutionStrategy.KeepAll:
                // For Merge/KeepAll, use the first region as representative
                resolvedRegion = conflicts.First().RegionId;
                break;

            default:
                resolvedRegion = conflicts.First().RegionId;
                break;
        }

        // Clear the conflict
        _conflicts.TryRemove(key, out _);
        Interlocked.Increment(ref _conflictsResolved);

        // Raise event
        ConflictResolved?.Invoke(this, new ConflictResolvedEventArgs
        {
            Key = key,
            ConflictingRegions = conflicts.Select(c => c.RegionId).ToArray(),
            Strategy = strategy,
            WinningRegion = resolvedRegion,
            ResolvedAt = DateTime.UtcNow
        });

        return Task.FromResult(new ConflictResolutionResult
        {
            Success = true,
            Key = key,
            StrategyUsed = strategy,
            WinningRegion = resolvedRegion
        });
    }

    public Task SetConsistencyLevelAsync(ConsistencyLevel level, CancellationToken ct = default)
    {
        _consistencyLevel = level;
        return Task.CompletedTask;
    }

    public ConsistencyLevel GetConsistencyLevel()
    {
        return _consistencyLevel;
    }

    public async Task<Dictionary<string, RegionHealth>> CheckRegionHealthAsync(CancellationToken ct = default)
    {
        var results = new Dictionary<string, RegionHealth>();
        var regions = _regions.Values.ToList();

        var tasks = regions.Select(async region =>
        {
            await CheckSingleRegionHealthAsync(region, ct);
            return (region.RegionId, region.Health);
        });

        var healthResults = await Task.WhenAll(tasks);
        foreach (var (regionId, health) in healthResults)
        {
            results[regionId] = health;
        }

        return results;
    }

    public Task SetBandwidthThrottleAsync(long? maxBytesPerSecond, CancellationToken ct = default)
    {
        _bandwidthThrottle = maxBytesPerSecond;
        return Task.CompletedTask;
    }

    public Task<List<RegionInfo>> ListRegionsAsync(CancellationToken ct = default)
    {
        var regions = _regions.Values.Select(r => new RegionInfo
        {
            RegionId = r.RegionId,
            Endpoint = r.Endpoint,
            Config = r.Config,
            AddedAt = r.AddedAt,
            Health = r.Health
        }).ToList();

        return Task.FromResult(regions);
    }

    private async Task CheckSingleRegionHealthAsync(ManagedRegion region, CancellationToken ct)
    {
        var previousHealth = region.Health;

        try
        {
            if (_httpClient != null && !string.IsNullOrEmpty(region.Endpoint))
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(HealthCheckTimeoutMs);

                var stopwatch = Stopwatch.StartNew();

                // Try to reach the region endpoint
                var response = await _httpClient.GetAsync($"{region.Endpoint}/health", cts.Token);
                stopwatch.Stop();

                if (response.IsSuccessStatusCode)
                {
                    region.Health = stopwatch.ElapsedMilliseconds > region.Config.ExpectedLatencyMs * 2
                        ? RegionHealth.Degraded
                        : RegionHealth.Healthy;
                    region.ReplicationLag = TimeSpan.FromMilliseconds(stopwatch.ElapsedMilliseconds);
                }
                else
                {
                    region.Health = RegionHealth.Degraded;
                }
            }
            else
            {
                // No endpoint configured, assume healthy for local testing
                region.Health = RegionHealth.Healthy;
            }
        }
        catch (TaskCanceledException)
        {
            region.Health = RegionHealth.Unhealthy;
        }
        catch (HttpRequestException)
        {
            region.Health = RegionHealth.Unhealthy;
        }
        catch
        {
            region.Health = RegionHealth.Unknown;
        }

        region.LastHealthCheck = DateTime.UtcNow;

        // Raise event if health changed
        if (previousHealth != region.Health)
        {
            RegionHealthChanged?.Invoke(this, new RegionHealthChangedEventArgs
            {
                RegionId = region.RegionId,
                OldHealth = previousHealth,
                NewHealth = region.Health,
                ChangedAt = DateTime.UtcNow
            });
        }
    }

    private async Task RunHealthCheckLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HealthCheckIntervalMs, ct);

                foreach (var region in _regions.Values.ToList())
                {
                    if (ct.IsCancellationRequested) break;

                    try
                    {
                        await CheckSingleRegionHealthAsync(region, ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        break;
                    }
                    catch
                    {
                        // Continue checking other regions
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Continue health check loop
            }
        }
    }

    private async Task RunReplicationLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(ReplicationIntervalMs, ct);

                // Reset bytes transferred counter every minute
                var now = DateTime.UtcNow;
                foreach (var region in _regions.Values)
                {
                    if ((now - region.LastBytesReset).TotalSeconds >= 60)
                    {
                        region.BytesTransferredLastMinute = 0;
                        region.LastBytesReset = now;
                    }
                }

                // Process any pending replication operations
                // In a real implementation, this would process a queue of pending writes
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Continue replication loop
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cts?.Cancel();
        _cts?.Dispose();
        _httpClient?.Dispose();
        _regionLock.Dispose();
    }

    /// <summary>
    /// Internal representation of a managed region.
    /// </summary>
    private sealed class ManagedRegion
    {
        public string RegionId { get; init; } = string.Empty;
        public string Endpoint { get; init; } = string.Empty;
        public RegionConfig Config { get; init; } = new();
        public StorageProviderPluginBase? StorageProvider { get; init; }
        public DateTime AddedAt { get; init; }
        public RegionHealth Health { get; set; }
        public DateTime LastHealthCheck { get; set; }
        public DateTime LastSuccessfulSync { get; set; }
        public TimeSpan ReplicationLag { get; set; }
        public long PendingOperations;
        public long BytesTransferredLastMinute;
        public DateTime LastBytesReset { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Record of a conflict for a key.
    /// </summary>
    private sealed class ConflictRecord
    {
        public string RegionId { get; init; } = string.Empty;
        public DateTime Timestamp { get; init; }
        public byte[]? Value { get; init; }
    }
}
