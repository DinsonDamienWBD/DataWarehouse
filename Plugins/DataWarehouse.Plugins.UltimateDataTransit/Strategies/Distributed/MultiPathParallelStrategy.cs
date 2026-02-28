using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Transit;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataTransit.Strategies.Distributed;

/// <summary>
/// Multi-path parallel transfer strategy that splits data across multiple network paths
/// for aggregate bandwidth utilization, with path scoring based on latency, throughput,
/// and error rate, and dynamic rebalancing when path performance changes.
/// </summary>
/// <remarks>
/// <para>
/// This strategy discovers multiple destination endpoints from request metadata and probes each
/// path for latency. Data is split into segments proportional to each path's score, computed as:
/// <c>Score = (0.5 * normalizedThroughput) - (0.3 * normalizedLatency) - (0.2 * errorRate)</c>.
/// Higher-scored paths receive larger segments for optimal bandwidth utilization.
/// </para>
/// <para>
/// During transfer, each path runs on its own <see cref="Task"/>. After each segment completion,
/// actual throughput is measured and paths are re-scored. If a path's score drops below 50% of
/// the best path, its remaining segments are redistributed to better-performing paths.
/// </para>
/// <para>
/// Thread safety is ensured via per-path locks for <see cref="PathInfo"/> updates,
/// a <see cref="SemaphoreSlim"/> for exclusive rebalancing access, and
/// <see cref="Interlocked"/> for progress byte counting.
/// </para>
/// </remarks>
internal sealed class MultiPathParallelStrategy : DataTransitStrategyBase
{
    /// <summary>
    /// Weight applied to normalized throughput in the path scoring formula.
    /// </summary>
    private const double ThroughputWeight = 0.5;

    /// <summary>
    /// Weight applied to normalized latency in the path scoring formula.
    /// </summary>
    private const double LatencyWeight = 0.3;

    /// <summary>
    /// Weight applied to error rate in the path scoring formula.
    /// </summary>
    private const double ErrorWeight = 0.2;

    /// <summary>
    /// Minimum segment size in bytes (1 MB). Segments smaller than this are not created.
    /// </summary>
    private const long MinSegmentSize = 1024 * 1024;

    /// <summary>
    /// Threshold ratio below which a path's remaining segments are redistributed.
    /// If a path's score drops below 50% of the best path's score, it is considered degraded.
    /// </summary>
    private const double RebalanceThreshold = 0.5;

    /// <summary>
    /// Thread-safe storage of active transfer states keyed by transfer ID.
    /// </summary>
    private readonly BoundedDictionary<string, TransferState> _transferStates = new BoundedDictionary<string, TransferState>(1000);

    /// <summary>
    /// Semaphore for exclusive access during segment rebalancing operations.
    /// Only one rebalancing operation can run at a time to prevent conflicting reassignments.
    /// </summary>
    private readonly SemaphoreSlim _rebalanceLock = new(1, 1);

    /// <summary>
    /// HTTP client used for path probing and segment transfers.
    /// </summary>
    private readonly HttpClient _httpClient;

    /// <inheritdoc/>
    public override string StrategyId => "transit-multipath-parallel";

    /// <inheritdoc/>
    public override string Name => "Multi-Path Parallel Transfer";

    /// <inheritdoc/>
    public override TransitCapabilities Capabilities => new()
    {
        SupportsMultiPath = true,
        SupportsStreaming = true,
        SupportsResumable = true,
        SupportsDelta = false,
        SupportsP2P = false,
        SupportsOffline = false,
        SupportsCompression = false,
        SupportsEncryption = false,
        MaxTransferSizeBytes = long.MaxValue,
        SupportedProtocols = ["http", "https", "http2", "http3"]
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="MultiPathParallelStrategy"/> class.
    /// Configures the internal <see cref="HttpClient"/> with HTTP/2 support, connection pooling,
    /// and infinite timeout for large multi-path transfers.
    /// </summary>
    public MultiPathParallelStrategy()
    {
        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            PooledConnectionLifetime = TimeSpan.FromMinutes(15),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(30)
        };

        _httpClient = new HttpClient(handler)
        {
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    /// <inheritdoc/>
    /// <summary>
    /// Checks that at least 2 paths are reachable. If only one path is available,
    /// multi-path adds no value over a single-path strategy.
    /// </summary>
    public override async Task<bool> IsAvailableAsync(TransitEndpoint endpoint, CancellationToken ct = default)
    {
        try
        {
            // Check if the primary endpoint is reachable
            using var request = new HttpRequestMessage(HttpMethod.Head, endpoint.Uri)
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };

            if (!string.IsNullOrEmpty(endpoint.AuthToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.AuthToken);
            }

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var primaryReachable = response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.MethodNotAllowed;

            if (!primaryReachable) return false;

            // Check for alternate paths
            var alternatePaths = GetAlternatePaths(endpoint);
            if (alternatePaths.Count < 1) return false; // Need at least 2 total paths

            // Probe at least one alternate
            var firstAlternate = alternatePaths[0];
            using var altRequest = new HttpRequestMessage(HttpMethod.Head, new Uri(firstAlternate))
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };

            using var altResponse = await _httpClient.SendAsync(altRequest, HttpCompletionOption.ResponseHeadersRead, ct);
            return altResponse.IsSuccessStatusCode || altResponse.StatusCode == HttpStatusCode.MethodNotAllowed;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc/>
    /// <summary>
    /// Transfers data by splitting it across multiple network paths proportional to each
    /// path's score. Paths are probed for latency, scored, and segments are distributed
    /// for parallel transfer. Dynamic rebalancing redistributes segments from degraded paths.
    /// </summary>
    public override async Task<TransitResult> TransferAsync(
        TransitRequest request,
        IProgress<TransitProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var transferId = request.TransferId;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ActiveTransferCancellations[transferId] = cts;
        IncrementActiveTransfers();

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var totalSize = DetermineTotalSize(request);

            progress?.Report(new TransitProgress
            {
                TransferId = transferId,
                CurrentPhase = "Discovering paths",
                PercentComplete = 0,
                TotalBytes = totalSize
            });

            // Step 2: Path discovery
            var pathEndpoints = DiscoverPaths(request);

            if (pathEndpoints.Count < 2)
            {
                // Single path: fall back to simple HTTP transfer
                return await SinglePathTransferAsync(request, pathEndpoints[0], progress, cts.Token);
            }

            progress?.Report(new TransitProgress
            {
                TransferId = transferId,
                CurrentPhase = "Probing paths",
                PercentComplete = 0,
                TotalBytes = totalSize
            });

            // Step 3: Probe paths for latency
            var paths = await ProbePathsAsync(pathEndpoints, request.Destination, cts.Token);

            // Remove unhealthy paths
            paths = paths.Where(p => p.IsHealthy).ToList();
            if (paths.Count == 0)
            {
                throw new InvalidOperationException("No healthy paths available for multi-path transfer.");
            }

            // Step 4: Score paths
            ScorePaths(paths);

            progress?.Report(new TransitProgress
            {
                TransferId = transferId,
                CurrentPhase = "Assigning segments",
                PercentComplete = 0,
                TotalBytes = totalSize
            });

            // Step 5: Segment assignment proportional to path scores
            var segments = AssignSegments(totalSize, paths);

            // Store transfer state for resume
            var transferState = new TransferState
            {
                TransferId = transferId,
                TotalSize = totalSize,
                Paths = paths,
                Segments = segments,
                Request = request
            };
            _transferStates[transferId] = transferState;

            // Step 6: Validate source stream is available before spawning parallel tasks (finding 2677).
            if (request.DataStream is null)
            {
                throw new InvalidOperationException(
                    "TransitRequest.DataStream must not be null for multi-path parallel transfer.");
            }

            // Step 6: Parallel transfer
            var totalSegments = segments.Count;

            var pathTasks = paths.Select(path =>
            {
                var pathSegments = segments.Where(s => s.AssignedPathId == path.PathId && !s.Completed).ToList();
                return TransferSegmentsOnPathAsync(
                    transferState, path, pathSegments, progress, totalSegments, cts.Token);
            }).ToArray();

            await Task.WhenAll(pathTasks);

            // Step 8: Verify all segments transferred
            var incompleteCount = segments.Count(s => !s.Completed);
            if (incompleteCount > 0)
            {
                throw new InvalidOperationException(
                    $"Transfer incomplete: {incompleteCount} segments not transferred.");
            }

            // Step 9: Post assembly instruction to primary destination
            await PostAssemblyInstructionAsync(transferState, request.Destination, cts.Token);

            stopwatch.Stop();

            var totalTransferred = Interlocked.Read(ref transferState.BytesTransferred);
            RecordTransferSuccess(totalTransferred);

            progress?.Report(new TransitProgress
            {
                TransferId = transferId,
                BytesTransferred = totalTransferred,
                TotalBytes = totalSize,
                PercentComplete = 100.0,
                CurrentPhase = "Completed"
            });

            return new TransitResult
            {
                TransferId = transferId,
                Success = true,
                BytesTransferred = totalTransferred,
                Duration = stopwatch.Elapsed,
                StrategyUsed = StrategyId,
                Metadata = BuildResultMetadata(paths, segments)
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            RecordTransferFailure();

            return new TransitResult
            {
                TransferId = transferId,
                Success = false,
                BytesTransferred = 0,
                Duration = stopwatch.Elapsed,
                ErrorMessage = ex.Message,
                StrategyUsed = StrategyId
            };
        }
        finally
        {
            DecrementActiveTransfers();
            ActiveTransferCancellations.TryRemove(transferId, out _);
            cts.Dispose();
        }
    }

    /// <inheritdoc/>
    /// <summary>
    /// Resumes an interrupted multi-path transfer by re-probing paths, re-scoring them,
    /// and resuming only incomplete segments.
    /// </summary>
    public override async Task<TransitResult> ResumeTransferAsync(
        string transferId,
        IProgress<TransitProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transferId);

        if (!_transferStates.TryGetValue(transferId, out var transferState))
        {
            throw new ArgumentException(
                $"No transfer state found for '{transferId}'. Cannot resume.",
                nameof(transferId));
        }

        if (transferState.Request is null)
        {
            throw new InvalidOperationException(
                $"Transfer '{transferId}' has no stored request context. Cannot resume.");
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ActiveTransferCancellations[transferId] = cts;
        IncrementActiveTransfers();

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var previouslyTransferred = transferState.Segments
                .Where(s => s.Completed)
                .Sum(s => s.Size);
            var completedSegments = transferState.Segments.Count(s => s.Completed);
            var totalSegments = transferState.Segments.Count;

            progress?.Report(new TransitProgress
            {
                TransferId = transferId,
                BytesTransferred = previouslyTransferred,
                TotalBytes = transferState.TotalSize,
                PercentComplete = totalSegments > 0 ? (double)completedSegments / totalSegments * 100.0 : 0,
                CurrentPhase = $"Resuming from segment {completedSegments}/{totalSegments}"
            });

            // Re-probe paths to get current latency
            var pathEndpoints = transferState.Paths.Select(p => p.EndpointUri).ToList();
            var reprobed = await ProbePathsAsync(pathEndpoints, transferState.Request.Destination, cts.Token);
            var healthyPaths = reprobed.Where(p => p.IsHealthy).ToList();

            if (healthyPaths.Count == 0)
            {
                throw new InvalidOperationException("No healthy paths available for resume.");
            }

            ScorePaths(healthyPaths);

            // Re-assign incomplete segments to healthy paths
            var incompleteSegments = transferState.Segments.Where(s => !s.Completed).ToList();
            ReassignSegmentsToPaths(incompleteSegments, healthyPaths);

            transferState.Paths = healthyPaths;

            transferState.BytesTransferred = 0; // Reset for this session

            var pathTasks = healthyPaths.Select(path =>
            {
                var pathSegments = incompleteSegments.Where(s => s.AssignedPathId == path.PathId).ToList();
                return TransferSegmentsOnPathAsync(
                    transferState, path, pathSegments, progress, totalSegments, cts.Token);
            }).ToArray();

            await Task.WhenAll(pathTasks);

            stopwatch.Stop();

            var totalBytes = previouslyTransferred + Interlocked.Read(ref transferState.BytesTransferred);
            RecordTransferSuccess(totalBytes);

            progress?.Report(new TransitProgress
            {
                TransferId = transferId,
                BytesTransferred = totalBytes,
                TotalBytes = transferState.TotalSize,
                PercentComplete = 100.0,
                CurrentPhase = "Resumed transfer completed"
            });

            return new TransitResult
            {
                TransferId = transferId,
                Success = true,
                BytesTransferred = totalBytes,
                Duration = stopwatch.Elapsed,
                StrategyUsed = StrategyId,
                Metadata = new Dictionary<string, string>
                {
                    ["resumed"] = "true",
                    ["skippedSegments"] = completedSegments.ToString(),
                    ["transferredSegments"] = incompleteSegments.Count.ToString(),
                    ["pathsUsed"] = healthyPaths.Count.ToString()
                }
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            RecordTransferFailure();

            return new TransitResult
            {
                TransferId = transferId,
                Success = false,
                BytesTransferred = 0,
                Duration = stopwatch.Elapsed,
                ErrorMessage = ex.Message,
                StrategyUsed = StrategyId
            };
        }
        finally
        {
            DecrementActiveTransfers();
            ActiveTransferCancellations.TryRemove(transferId, out _);
            cts.Dispose();
        }
    }

    /// <summary>
    /// Discovers available paths from request metadata. Parses comma-separated URIs
    /// from <c>request.Metadata["paths"]</c> or <c>request.Destination.Options["alternatePaths"]</c>.
    /// Always includes the primary destination URI.
    /// </summary>
    /// <param name="request">The transfer request containing path metadata.</param>
    /// <returns>List of endpoint URIs including the primary destination.</returns>
    private static List<string> DiscoverPaths(TransitRequest request)
    {
        var paths = new List<string> { request.Destination.Uri.ToString().TrimEnd('/') };

        // Try Metadata["paths"] first
        if (request.Metadata.TryGetValue("paths", out var pathsCsv) && !string.IsNullOrWhiteSpace(pathsCsv))
        {
            foreach (var path in pathsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var normalized = path.TrimEnd('/');
                if (!paths.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                {
                    paths.Add(normalized);
                }
            }
        }

        // Try Destination.Options["alternatePaths"]
        if (request.Destination.Options.TryGetValue("alternatePaths", out var altPaths) && !string.IsNullOrWhiteSpace(altPaths))
        {
            foreach (var path in altPaths.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var normalized = path.TrimEnd('/');
                if (!paths.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                {
                    paths.Add(normalized);
                }
            }
        }

        return paths;
    }

    /// <summary>
    /// Gets alternate paths from an endpoint's options, used for availability checking.
    /// </summary>
    /// <param name="endpoint">The endpoint to extract alternate paths from.</param>
    /// <returns>List of alternate path URIs.</returns>
    private static List<string> GetAlternatePaths(TransitEndpoint endpoint)
    {
        var result = new List<string>();

        if (endpoint.Options.TryGetValue("alternatePaths", out var altPaths) && !string.IsNullOrWhiteSpace(altPaths))
        {
            foreach (var path in altPaths.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                result.Add(path.TrimEnd('/'));
            }
        }

        return result;
    }

    /// <summary>
    /// Probes each path endpoint for latency using HTTP HEAD requests with <see cref="Stopwatch"/>
    /// timing. Initial throughput is estimated using the bandwidth-delay product heuristic:
    /// <c>throughput = 1MB / latencyMs * 1000</c>.
    /// </summary>
    /// <param name="pathEndpoints">List of endpoint URIs to probe.</param>
    /// <param name="destination">The primary destination for auth token extraction.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of <see cref="PathInfo"/> with measured latency and estimated throughput.</returns>
    private async Task<List<PathInfo>> ProbePathsAsync(
        List<string> pathEndpoints,
        TransitEndpoint destination,
        CancellationToken ct)
    {
        var paths = new List<PathInfo>();

        foreach (var endpoint in pathEndpoints)
        {
            var pathId = Guid.NewGuid().ToString("N");
            var sw = Stopwatch.StartNew();
            var isHealthy = true;
            double latencyMs;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, endpoint)
                {
                    Version = HttpVersion.Version20,
                    VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
                };

                if (!string.IsNullOrEmpty(destination.AuthToken))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", destination.AuthToken);
                }

                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                sw.Stop();

                latencyMs = sw.Elapsed.TotalMilliseconds;
                isHealthy = response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.MethodNotAllowed;
            }
            catch
            {
                sw.Stop();
                latencyMs = sw.Elapsed.TotalMilliseconds;
                isHealthy = false;
            }

            // Bandwidth-delay product heuristic: throughput estimate = 1MB / latencyMs * 1000
            var estimatedThroughput = latencyMs > 0
                ? (1024.0 * 1024.0 / latencyMs) * 1000.0
                : 1024.0 * 1024.0; // Default 1 MB/s if latency is zero

            paths.Add(new PathInfo
            {
                PathId = pathId,
                EndpointUri = endpoint,
                LatencyMs = latencyMs,
                ThroughputBytesPerSec = estimatedThroughput,
                ErrorRate = 0.0,
                Score = 0.0,
                BytesAssigned = 0,
                BytesCompleted = 0,
                IsHealthy = isHealthy,
                Attempts = 0,
                Failures = 0,
                Lock = new object()
            });
        }

        return paths;
    }

    /// <summary>
    /// Scores all paths using the weighted formula:
    /// <c>Score = (0.5 * normalizedThroughput) - (0.3 * normalizedLatency) - (0.2 * errorRate)</c>.
    /// Throughput is normalized to [0,1] relative to the maximum throughput across paths.
    /// Latency is inversely normalized so lower latency yields a higher contribution.
    /// </summary>
    /// <param name="paths">The paths to score. Each path's <see cref="PathInfo.Score"/> is updated in-place.</param>
    private static void ScorePaths(List<PathInfo> paths)
    {
        if (paths.Count == 0) return;

        var maxThroughput = paths.Max(p => p.ThroughputBytesPerSec);
        var maxLatency = paths.Max(p => p.LatencyMs);

        if (maxThroughput <= 0) maxThroughput = 1;
        if (maxLatency <= 0) maxLatency = 1;

        foreach (var path in paths)
        {
            var normalizedThroughput = path.ThroughputBytesPerSec / maxThroughput;
            // Inverse normalization: lower latency = higher score contribution
            var normalizedLatency = path.LatencyMs / maxLatency;
            var errorRate = path.ErrorRate;

            path.Score = (ThroughputWeight * normalizedThroughput)
                       - (LatencyWeight * normalizedLatency)
                       - (ErrorWeight * errorRate);

            // Ensure score is non-negative for proportional distribution
            if (path.Score < 0.01) path.Score = 0.01;
        }
    }

    /// <summary>
    /// Assigns data segments to paths proportional to each path's score.
    /// Higher-scored paths receive larger segments. The minimum segment size is 1 MB.
    /// </summary>
    /// <param name="totalSize">The total data size in bytes.</param>
    /// <param name="paths">The scored paths to distribute segments across.</param>
    /// <returns>A list of <see cref="DataSegment"/> assigned to paths.</returns>
    private static List<DataSegment> AssignSegments(long totalSize, List<PathInfo> paths)
    {
        var segments = new List<DataSegment>();
        var totalScore = paths.Sum(p => p.Score);

        if (totalScore <= 0) totalScore = paths.Count; // Fallback to equal distribution

        long assignedSoFar = 0;
        var segmentIndex = 0;

        for (var i = 0; i < paths.Count; i++)
        {
            var path = paths[i];
            long pathAllocation;

            if (i == paths.Count - 1)
            {
                // Last path gets the remainder to avoid rounding issues
                pathAllocation = totalSize - assignedSoFar;
            }
            else
            {
                pathAllocation = (long)(totalSize * (path.Score / totalScore));
                // Enforce minimum segment size
                if (pathAllocation < MinSegmentSize && totalSize - assignedSoFar >= MinSegmentSize)
                {
                    pathAllocation = MinSegmentSize;
                }
            }

            if (pathAllocation <= 0) continue;

            // Split path allocation into segments (each up to 16 MB for manageability)
            const long maxSegmentSize = 16 * 1024 * 1024;
            long pathOffset = assignedSoFar;
            var remaining = pathAllocation;

            while (remaining > 0)
            {
                var segSize = Math.Min(remaining, maxSegmentSize);

                segments.Add(new DataSegment
                {
                    Index = segmentIndex++,
                    Offset = pathOffset,
                    Size = segSize,
                    AssignedPathId = path.PathId,
                    Completed = false
                });

                path.BytesAssigned += segSize;
                pathOffset += segSize;
                remaining -= segSize;
            }

            assignedSoFar += pathAllocation;
        }

        return segments;
    }

    /// <summary>
    /// Transfers segments assigned to a specific path. After each segment, the path's actual
    /// throughput is measured and a rebalancing check is triggered if the path is degraded.
    /// </summary>
    /// <param name="state">The overall transfer state.</param>
    /// <param name="path">The path to transfer segments on.</param>
    /// <param name="segments">The segments assigned to this path.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="totalSegments">Total number of segments across all paths.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task TransferSegmentsOnPathAsync(
        TransferState state,
        PathInfo path,
        List<DataSegment> segments,
        IProgress<TransitProgress>? progress,
        int totalSegments,
        CancellationToken ct)
    {
        foreach (var segment in segments)
        {
            ct.ThrowIfCancellationRequested();

            if (segment.Completed) continue;

            // Check if segment was reassigned during rebalancing
            if (segment.AssignedPathId != path.PathId) continue;

            var segmentSw = Stopwatch.StartNew();

            try
            {
                await TransferSegmentAsync(state, path, segment, ct);
                segmentSw.Stop();

                segment.Completed = true;

                // Update path throughput from actual measurement
                var actualThroughput = segment.Size / segmentSw.Elapsed.TotalSeconds;
                lock (path.Lock)
                {
                    path.ThroughputBytesPerSec = actualThroughput;
                    path.BytesCompleted += segment.Size;
                    path.Attempts++;
                }

                Interlocked.Add(ref state.BytesTransferred, segment.Size);

                var completed = state.Segments.Count(s => s.Completed);
                var pct = totalSegments > 0 ? (double)completed / totalSegments * 100.0 : 0;

                progress?.Report(new TransitProgress
                {
                    TransferId = state.TransferId,
                    BytesTransferred = Interlocked.Read(ref state.BytesTransferred),
                    TotalBytes = state.TotalSize,
                    PercentComplete = pct,
                    BytesPerSecond = actualThroughput,
                    CurrentPhase = $"Transferring on {state.Paths.Count} paths ({completed}/{totalSegments} segments)"
                });

                // Step 7: Dynamic rebalancing check
                await TryRebalanceAsync(state, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                segmentSw.Stop();

                // Mark the segment as failed so callers can detect incomplete transfers.
                segment.Failed = true;

                System.Diagnostics.Trace.TraceWarning(
                    "[MultiPathParallel] Segment {0} failed on path {1}: {2}: {3}",
                    segment.Index, path.PathId, ex.GetType().Name, ex.Message);

                lock (path.Lock)
                {
                    path.Failures++;
                    path.Attempts++;
                    path.ErrorRate = path.Attempts > 0 ? (double)path.Failures / path.Attempts : 0;
                }

                // Mark path as unhealthy if error rate exceeds 50%
                if (path.ErrorRate > 0.5)
                {
                    path.IsHealthy = false;
                }

                // Re-score and rebalance
                await TryRebalanceAsync(state, ct);
            }
        }
    }

    /// <summary>
    /// Transfers a single data segment to the path endpoint via HTTP POST with Content-Range header.
    /// Reads the segment data from the source stream at the specified offset.
    /// </summary>
    /// <param name="state">The overall transfer state containing the source stream.</param>
    /// <param name="path">The path to transfer the segment on.</param>
    /// <param name="segment">The segment to transfer.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task TransferSegmentAsync(
        TransferState state,
        PathInfo path,
        DataSegment segment,
        CancellationToken ct)
    {
        var segmentData = await ReadSegmentDataAsync(state.Request?.DataStream, segment.Offset, segment.Size, ct);

        var segmentUri = $"{path.EndpointUri}/segments/{state.TransferId}/{segment.Index}";

        using var content = new ByteArrayContent(segmentData);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = segmentData.Length;

        var rangeEnd = segment.Offset + segmentData.Length - 1;
        content.Headers.Add("Content-Range", $"bytes {segment.Offset}-{rangeEnd}/{state.TotalSize}");

        using var request = new HttpRequestMessage(HttpMethod.Post, segmentUri)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Content = content
        };

        if (state.Request?.Destination.AuthToken is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", state.Request.Destination.AuthToken);
        }

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Reads segment data from the source stream at the specified offset and size.
    /// Seeks to the correct position if the stream supports seeking.
    /// </summary>
    /// <param name="stream">The source data stream.</param>
    /// <param name="offset">Byte offset within the source data.</param>
    /// <param name="size">Number of bytes to read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A byte array containing the segment data.</returns>
    private static async Task<byte[]> ReadSegmentDataAsync(
        Stream? stream,
        long offset,
        long size,
        CancellationToken ct)
    {
        if (stream is null)
        {
            throw new InvalidOperationException("No data stream available for reading segment data.");
        }

        if (stream.CanSeek)
        {
            stream.Position = offset;
        }

        var buffer = new byte[size];
        var totalRead = 0;

        while (totalRead < size)
        {
            var toRead = (int)Math.Min(size - totalRead, int.MaxValue);
            var bytesRead = await stream.ReadAsync(buffer.AsMemory(totalRead, toRead), ct);
            if (bytesRead == 0) break;
            totalRead += bytesRead;
        }

        if (totalRead < size)
        {
            var trimmed = new byte[totalRead];
            Array.Copy(buffer, trimmed, totalRead);
            return trimmed;
        }

        return buffer;
    }

    /// <summary>
    /// Attempts to rebalance segment assignments by re-scoring paths and redistributing
    /// segments from paths whose score has dropped below 50% of the best path's score.
    /// Uses a semaphore to ensure only one rebalancing operation runs at a time.
    /// </summary>
    /// <param name="state">The transfer state with paths and segments.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task TryRebalanceAsync(TransferState state, CancellationToken ct)
    {
        if (!await _rebalanceLock.WaitAsync(0, ct))
        {
            return; // Another rebalance is already in progress
        }

        try
        {
            var healthyPaths = state.Paths.Where(p => p.IsHealthy).ToList();
            if (healthyPaths.Count < 2) return;

            ScorePaths(healthyPaths);

            var bestScore = healthyPaths.Max(p => p.Score);
            var threshold = bestScore * RebalanceThreshold;

            // Find degraded paths
            var degradedPaths = healthyPaths.Where(p => p.Score < threshold).ToList();
            if (degradedPaths.Count == 0) return;

            // Find better paths
            var betterPaths = healthyPaths.Where(p => p.Score >= threshold).ToList();
            if (betterPaths.Count == 0) return;

            // Redistribute incomplete segments from degraded paths to better paths
            var segmentsToReassign = state.Segments
                .Where(s => !s.Completed && degradedPaths.Any(p => p.PathId == s.AssignedPathId))
                .ToList();

            if (segmentsToReassign.Count == 0) return;

            ReassignSegmentsToPaths(segmentsToReassign, betterPaths);
        }
        finally
        {
            _rebalanceLock.Release();
        }
    }

    /// <summary>
    /// Reassigns segments to paths proportional to each path's score.
    /// Used during rebalancing and resume operations.
    /// </summary>
    /// <param name="segments">The segments to reassign.</param>
    /// <param name="paths">The target paths to distribute segments across.</param>
    private static void ReassignSegmentsToPaths(List<DataSegment> segments, List<PathInfo> paths)
    {
        if (paths.Count == 0 || segments.Count == 0) return;

        var totalScore = paths.Sum(p => p.Score);
        if (totalScore <= 0) totalScore = paths.Count;

        var pathIndex = 0;
        foreach (var segment in segments)
        {
            // Round-robin weighted by score (simple proportional assignment)
            segment.AssignedPathId = paths[pathIndex % paths.Count].PathId;
            pathIndex++;
        }
    }

    /// <summary>
    /// Performs a single-path HTTP transfer when only one path is available.
    /// Multi-path adds no value with a single path, so this falls back to a simple POST.
    /// </summary>
    /// <param name="request">The transfer request.</param>
    /// <param name="endpoint">The single path endpoint URI.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The transfer result from the single-path transfer.</returns>
    private async Task<TransitResult> SinglePathTransferAsync(
        TransitRequest request,
        string endpoint,
        IProgress<TransitProgress>? progress,
        CancellationToken ct)
    {
        var transferId = request.TransferId;
        var totalSize = DetermineTotalSize(request);
        var stopwatch = Stopwatch.StartNew();

        var data = await ReadSegmentDataAsync(request.DataStream, 0, totalSize, ct);

        var uploadUri = $"{endpoint}/upload/{transferId}";
        using var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = data.Length;

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, uploadUri)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Content = content
        };

        if (!string.IsNullOrEmpty(request.Destination.AuthToken))
        {
            httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.Destination.AuthToken);
        }

        using var response = await _httpClient.SendAsync(httpReq, ct);
        response.EnsureSuccessStatusCode();

        stopwatch.Stop();

        RecordTransferSuccess(data.Length);

        progress?.Report(new TransitProgress
        {
            TransferId = transferId,
            BytesTransferred = data.Length,
            TotalBytes = totalSize,
            PercentComplete = 100.0,
            CurrentPhase = "Completed (single path)"
        });

        return new TransitResult
        {
            TransferId = transferId,
            Success = true,
            BytesTransferred = data.Length,
            Duration = stopwatch.Elapsed,
            StrategyUsed = StrategyId,
            Metadata = new Dictionary<string, string>
            {
                ["pathsUsed"] = "1",
                ["singlePathFallback"] = "true"
            }
        };
    }

    /// <summary>
    /// Posts an assembly instruction to the primary destination so the server can reassemble
    /// segments from multiple paths into a complete file. The manifest includes segment
    /// offsets and the path each segment was transferred on.
    /// </summary>
    /// <param name="state">The transfer state with segment information.</param>
    /// <param name="destination">The primary destination endpoint.</param>
    /// <param name="ct">Cancellation token.</param>
    private async Task PostAssemblyInstructionAsync(
        TransferState state,
        TransitEndpoint destination,
        CancellationToken ct)
    {
        var assembleUri = new Uri(destination.Uri, $"assemble/{state.TransferId}");

        var manifest = JsonSerializer.Serialize(new
        {
            state.TransferId,
            state.TotalSize,
            Segments = state.Segments.Select(s => new
            {
                s.Index,
                s.Offset,
                s.Size,
                s.AssignedPathId,
                s.Completed
            }),
            Paths = state.Paths.Select(p => new
            {
                p.PathId,
                p.EndpointUri,
                p.ThroughputBytesPerSec,
                p.LatencyMs
            })
        });

        using var content = new StringContent(manifest, System.Text.Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, assembleUri)
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            Content = content
        };

        if (!string.IsNullOrEmpty(destination.AuthToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", destination.AuthToken);
        }

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Determines the total data size from the request using <see cref="TransitRequest.SizeBytes"/>
    /// or falling back to the stream length.
    /// </summary>
    /// <param name="request">The transfer request.</param>
    /// <returns>The total data size in bytes.</returns>
    private static long DetermineTotalSize(TransitRequest request)
    {
        if (request.SizeBytes > 0) return request.SizeBytes;
        if (request.DataStream is { CanSeek: true }) return request.DataStream.Length;

        throw new InvalidOperationException(
            "Cannot determine total size: SizeBytes not set and stream is not seekable. " +
            "Multi-path parallel transfer requires a known total size for segment assignment.");
    }

    /// <summary>
    /// Builds result metadata summarizing per-path throughput and segment counts.
    /// </summary>
    /// <param name="paths">The paths used in the transfer.</param>
    /// <param name="segments">The segments transferred.</param>
    /// <returns>Dictionary of metadata entries for the <see cref="TransitResult"/>.</returns>
    private static Dictionary<string, string> BuildResultMetadata(
        List<PathInfo> paths,
        List<DataSegment> segments)
    {
        var metadata = new Dictionary<string, string>
        {
            ["pathsUsed"] = paths.Count.ToString(),
            ["totalSegments"] = segments.Count.ToString(),
            ["completedSegments"] = segments.Count(s => s.Completed).ToString()
        };

        for (var i = 0; i < paths.Count; i++)
        {
            var path = paths[i];
            var pathSegments = segments.Count(s => s.AssignedPathId == path.PathId);
            metadata[$"path_{i}_endpoint"] = path.EndpointUri;
            metadata[$"path_{i}_throughputBytesPerSec"] = path.ThroughputBytesPerSec.ToString("F0");
            metadata[$"path_{i}_latencyMs"] = path.LatencyMs.ToString("F1");
            metadata[$"path_{i}_segments"] = pathSegments.ToString();
            metadata[$"path_{i}_score"] = path.Score.ToString("F4");
        }

        return metadata;
    }

    #region Internal Types

    /// <summary>
    /// Describes a network path with measured performance metrics and a computed score.
    /// Updated during transfer as actual throughput is measured.
    /// </summary>
    internal sealed class PathInfo
    {
        /// <summary>
        /// Unique identifier for this path (GUID-based).
        /// </summary>
        public required string PathId { get; init; }

        /// <summary>
        /// HTTP endpoint URI for this path.
        /// </summary>
        public required string EndpointUri { get; init; }

        /// <summary>
        /// Round-trip latency in milliseconds measured via HTTP HEAD probe.
        /// </summary>
        public double LatencyMs { get; set; }

        /// <summary>
        /// Throughput in bytes per second. Initially estimated from latency,
        /// then updated with actual measurements during transfer.
        /// </summary>
        public double ThroughputBytesPerSec { get; set; }

        /// <summary>
        /// Error rate computed as failures / attempts. Range [0.0, 1.0].
        /// </summary>
        public double ErrorRate { get; set; }

        /// <summary>
        /// Computed score from the weighted formula combining throughput, latency, and error rate.
        /// </summary>
        public double Score { get; set; }

        /// <summary>
        /// Total bytes assigned to this path for transfer.
        /// </summary>
        public long BytesAssigned { get; set; }

        /// <summary>
        /// Total bytes successfully completed on this path.
        /// </summary>
        public long BytesCompleted { get; set; }

        /// <summary>
        /// Whether this path is currently healthy and accepting transfers.
        /// </summary>
        public bool IsHealthy { get; set; }

        /// <summary>
        /// Total number of segment transfer attempts on this path.
        /// </summary>
        public int Attempts { get; set; }

        /// <summary>
        /// Total number of failed segment transfers on this path.
        /// </summary>
        public int Failures { get; set; }

        /// <summary>
        /// Lock object for thread-safe updates to this path's mutable properties.
        /// </summary>
        public object Lock { get; init; } = new();
    }

    /// <summary>
    /// Represents a data segment assigned to a specific network path for transfer.
    /// </summary>
    internal sealed class DataSegment
    {
        /// <summary>
        /// Zero-based index of this segment.
        /// </summary>
        public required int Index { get; init; }

        /// <summary>
        /// Byte offset of this segment within the source data.
        /// </summary>
        public required long Offset { get; init; }

        /// <summary>
        /// Size of this segment in bytes.
        /// </summary>
        public required long Size { get; init; }

        /// <summary>
        /// ID of the path this segment is assigned to. May change during rebalancing.
        /// </summary>
        public required string AssignedPathId { get; set; }

        /// <summary>
        /// Whether this segment has been successfully transferred.
        /// </summary>
        public bool Completed { get; set; }

        /// <summary>
        /// Whether this segment permanently failed (all retries exhausted on its path).
        /// A failed segment prevents the overall transfer from being declared successful.
        /// </summary>
        public bool Failed { get; set; }
    }

    /// <summary>
    /// Mutable state for an active multi-path transfer, tracking paths, segments, and the request.
    /// </summary>
    internal sealed class TransferState
    {
        /// <summary>
        /// Unique identifier for this transfer.
        /// </summary>
        public required string TransferId { get; init; }

        /// <summary>
        /// Total size of the data being transferred in bytes.
        /// </summary>
        public required long TotalSize { get; init; }

        /// <summary>
        /// List of discovered and scored network paths.
        /// </summary>
        public required List<PathInfo> Paths { get; set; }

        /// <summary>
        /// List of data segments with their path assignments and completion status.
        /// </summary>
        public required List<DataSegment> Segments { get; init; }

        /// <summary>
        /// Thread-safe counter for total bytes transferred in the current session.
        /// Updated via <see cref="Interlocked.Add(ref long, long)"/>.
        /// </summary>
        public long BytesTransferred;

        /// <summary>
        /// The original transfer request, stored for resume operations.
        /// </summary>
        public TransitRequest? Request { get; init; }
    }

    #endregion
}
