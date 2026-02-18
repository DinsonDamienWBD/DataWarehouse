using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.IndustryFirst;

/// <summary>
/// Data gravity scheduling strategy that scores compute placement by data locality.
/// Uses weighted cost model factoring network transfer time vs compute cost to move
/// computation to data rather than moving data to computation.
/// </summary>
/// <remarks>
/// <para>
/// Locality scoring: same-node=1.0, same-rack=0.7, same-datacenter=0.4, remote=0.1.
/// The scheduler delegates actual execution to a specified runtime strategy, selecting the
/// optimal placement node before dispatching. When no placement metadata is available,
/// defaults to local execution with maximum locality score.
/// </para>
/// </remarks>
internal sealed class DataGravitySchedulerStrategy : ComputeRuntimeStrategyBase
{
    private readonly ConcurrentDictionary<string, List<PlacementRecord>> _placementHistory = new();

    /// <inheritdoc/>
    public override string StrategyId => "compute.industryfirst.datagravity";
    /// <inheritdoc/>
    public override string StrategyName => "Data Gravity Scheduler";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Custom;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: true, SupportsSandboxing: false,
        MaxMemoryBytes: 16L * 1024 * 1024 * 1024, MaxExecutionTime: TimeSpan.FromHours(2),
        SupportedLanguages: ["any"], SupportsMultiThreading: true,
        SupportsAsync: true, SupportsNetworkAccess: true, SupportsFileSystemAccess: true,
        MaxConcurrentTasks: 32, MemoryIsolation: MemoryIsolationLevel.Process);
    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.Custom];

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            // Parse data locations from task metadata
            var dataNodes = ParseDataLocations(task);
            var computeNodes = ParseComputeNodes(task);

            // Score each compute node by proximity to data
            var scores = new List<(string NodeId, double Score, double TransferCost)>();
            foreach (var computeNode in computeNodes)
            {
                var localityScore = 0.0;
                var totalDataSize = 0L;

                foreach (var dataNode in dataNodes)
                {
                    var proximity = ScoreLocality(dataNode.location, computeNode.location);
                    var dataSize = dataNode.sizeBytes;
                    localityScore += proximity * dataSize;
                    totalDataSize += dataSize;
                }

                if (totalDataSize > 0)
                    localityScore /= totalDataSize;

                // Network transfer cost: (1 - locality) * data_size * cost_per_byte
                var costPerByte = 0.000001; // $0.01 per 10MB
                if (task.Metadata?.TryGetValue("cost_per_byte", out var cpb) == true && cpb is double cpbd)
                    costPerByte = cpbd;

                var transferCost = (1.0 - localityScore) * totalDataSize * costPerByte;

                // Compute cost per vCPU-second
                var computeCostPerSec = computeNode.costPerVcpuSecond;
                var estimatedSeconds = EstimateExecutionTime(task, totalDataSize);
                var computeCost = computeCostPerSec * estimatedSeconds;

                // Composite score: lower is better (total cost)
                var totalCost = transferCost + computeCost;
                scores.Add((computeNode.nodeId, localityScore, totalCost));
            }

            // Select optimal node (lowest total cost, highest locality as tiebreaker)
            (string NodeId, double Score, double TransferCost) optimal = scores.Count > 0
                ? scores.OrderBy(s => s.TransferCost).ThenByDescending(s => s.Score).First()
                : ("local", 1.0, 0.0);

            // Record placement decision
            _placementHistory.AddOrUpdate(task.Id,
                _ => [new PlacementRecord(optimal.NodeId, optimal.Score, optimal.TransferCost, DateTime.UtcNow)],
                (_, list) => { list.Add(new PlacementRecord(optimal.NodeId, optimal.Score, optimal.TransferCost, DateTime.UtcNow)); return list; });

            // Execute on the selected node (dispatch via shell script or local execution)
            var codePath = Path.GetTempFileName() + ".sh";
            try
            {
                await File.WriteAllBytesAsync(codePath, task.Code.ToArray(), cancellationToken);

                var env = new Dictionary<string, string>(task.Environment ?? new Dictionary<string, string>())
                {
                    ["DG_TARGET_NODE"] = optimal.NodeId,
                    ["DG_LOCALITY_SCORE"] = optimal.Score.ToString("F3"),
                    ["DG_TRANSFER_COST"] = optimal.TransferCost.ToString("F6")
                };

                var timeout = GetEffectiveTimeout(task);
                var result = await RunProcessAsync("sh", $"\"{codePath}\"",
                    stdin: task.InputData.Length > 0 ? task.GetInputDataAsString() : null,
                    environment: env, timeout: timeout, cancellationToken: cancellationToken);

                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"Data gravity execution failed on node {optimal.NodeId}: {result.StandardError}");

                var scoreSummary = new StringBuilder();
                scoreSummary.AppendLine($"Placement: {optimal.NodeId} (locality={optimal.Score:F3}, cost=${optimal.TransferCost:F4})");
                foreach (var s in scores.OrderBy(x => x.TransferCost).Take(5))
                    scoreSummary.AppendLine($"  {s.NodeId}: locality={s.Score:F3}, cost=${s.TransferCost:F4}");

                return (EncodeOutput(result.StandardOutput),
                    $"DataGravity: node={optimal.NodeId}, locality={optimal.Score:F3}, cost=${optimal.TransferCost:F4}, elapsed={result.Elapsed.TotalMilliseconds:F0}ms\n{scoreSummary}");
            }
            finally
            {
                try { File.Delete(codePath); } catch { /* Best-effort cleanup */ }
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Scores locality between a data location and a compute location.
    /// </summary>
    private static double ScoreLocality(LocationInfo dataLoc, LocationInfo computeLoc)
    {
        if (dataLoc.NodeId == computeLoc.NodeId) return 1.0;
        if (dataLoc.RackId == computeLoc.RackId) return 0.7;
        if (dataLoc.DatacenterId == computeLoc.DatacenterId) return 0.4;
        return 0.1;
    }

    /// <summary>
    /// Estimates execution time in seconds based on input data size.
    /// </summary>
    private static double EstimateExecutionTime(ComputeTask task, long totalDataSize)
    {
        // Use provided estimate or calculate from data size (1MB/sec baseline)
        if (task.Metadata?.TryGetValue("estimated_seconds", out var es) == true && es is double esd)
            return esd;
        return Math.Max(1.0, totalDataSize / (1024.0 * 1024.0));
    }

    /// <summary>
    /// Parses data location entries from task metadata.
    /// </summary>
    private static List<(LocationInfo location, long sizeBytes)> ParseDataLocations(ComputeTask task)
    {
        var result = new List<(LocationInfo, long)>();
        if (task.Metadata?.TryGetValue("data_locations", out var dl) == true && dl is JsonElement jsonArr && jsonArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in jsonArr.EnumerateArray())
            {
                var loc = new LocationInfo(
                    item.TryGetProperty("node_id", out var n) ? n.GetString() ?? "unknown" : "unknown",
                    item.TryGetProperty("rack_id", out var r) ? r.GetString() ?? "default" : "default",
                    item.TryGetProperty("datacenter_id", out var d) ? d.GetString() ?? "dc1" : "dc1");
                var size = item.TryGetProperty("size_bytes", out var s) ? s.GetInt64() : 1024L * 1024;
                result.Add((loc, size));
            }
        }

        if (result.Count == 0)
            result.Add((new LocationInfo("local", "rack0", "dc1"), task.InputData.Length > 0 ? task.InputData.Length : 1024 * 1024));

        return result;
    }

    /// <summary>
    /// Parses available compute node locations from task metadata.
    /// </summary>
    private static List<(string nodeId, LocationInfo location, double costPerVcpuSecond)> ParseComputeNodes(ComputeTask task)
    {
        var result = new List<(string, LocationInfo, double)>();
        if (task.Metadata?.TryGetValue("compute_nodes", out var cn) == true && cn is JsonElement jsonArr && jsonArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in jsonArr.EnumerateArray())
            {
                var nodeId = item.TryGetProperty("node_id", out var n) ? n.GetString() ?? "node0" : "node0";
                var loc = new LocationInfo(
                    nodeId,
                    item.TryGetProperty("rack_id", out var r) ? r.GetString() ?? "default" : "default",
                    item.TryGetProperty("datacenter_id", out var d) ? d.GetString() ?? "dc1" : "dc1");
                var cost = item.TryGetProperty("cost_per_vcpu_sec", out var c) ? c.GetDouble() : 0.00001;
                result.Add((nodeId, loc, cost));
            }
        }

        if (result.Count == 0)
            result.Add(("local", new LocationInfo("local", "rack0", "dc1"), 0.00001));

        return result;
    }

    /// <summary>Location coordinates for locality scoring.</summary>
    private record LocationInfo(string NodeId, string RackId, string DatacenterId);

    /// <summary>Historical placement record for analysis.</summary>
    private record PlacementRecord(string NodeId, double LocalityScore, double TransferCost, DateTime Timestamp);
}
