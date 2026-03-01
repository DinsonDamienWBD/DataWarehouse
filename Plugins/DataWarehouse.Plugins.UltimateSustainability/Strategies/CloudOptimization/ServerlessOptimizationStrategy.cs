namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.CloudOptimization;

/// <summary>
/// Optimizes serverless function deployments for energy efficiency.
/// Manages cold starts, memory allocation, and execution patterns.
/// </summary>
public sealed class ServerlessOptimizationStrategy : SustainabilityStrategyBase
{
    private readonly Dictionary<string, ServerlessFunction> _functions = new();
    // Finding 4446: Queue for O(1) dequeue when capping execution history.
    private readonly Queue<FunctionExecution> _executions = new();
    private readonly object _lock = new();

    /// <inheritdoc/>
    public override string StrategyId => "serverless-optimization";
    /// <inheritdoc/>
    public override string DisplayName => "Serverless Optimization";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.CloudOptimization;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.RealTimeMonitoring | SustainabilityCapabilities.PredictiveAnalytics;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Optimizes serverless functions for energy efficiency through memory tuning and cold start reduction.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "serverless", "lambda", "functions", "faas", "cloud", "cold-start" };

    /// <summary>Cold start threshold (percentage of invocations).</summary>
    public double ColdStartAlertThreshold { get; set; } = 20;
    /// <summary>Target memory efficiency (execution time / allocated memory).</summary>
    public double TargetMemoryEfficiency { get; set; } = 0.7;

    /// <summary>Registers a serverless function.</summary>
    public void RegisterFunction(string functionId, string name, string runtime, int memoryMb, string provider)
    {
        lock (_lock)
        {
            _functions[functionId] = new ServerlessFunction
            {
                FunctionId = functionId,
                Name = name,
                Runtime = runtime,
                MemoryMb = memoryMb,
                Provider = provider
            };
        }
    }

    /// <summary>Records a function execution.</summary>
    public void RecordExecution(string functionId, double durationMs, double billedDurationMs, int memoryUsedMb, bool wasColdStart)
    {
        lock (_lock)
        {
            if (!_functions.TryGetValue(functionId, out var function)) return;

            var execution = new FunctionExecution
            {
                FunctionId = functionId,
                Timestamp = DateTimeOffset.UtcNow,
                DurationMs = durationMs,
                BilledDurationMs = billedDurationMs,
                MemoryUsedMb = memoryUsedMb,
                WasColdStart = wasColdStart,
                MemoryEfficiency = (double)memoryUsedMb / function.MemoryMb
            };

            _executions.Enqueue(execution);
            if (_executions.Count > 50000) _executions.Dequeue(); // O(1) vs O(n) RemoveAt(0)

            function.InvocationCount++;
            if (wasColdStart) function.ColdStartCount++;
        }

        RecordOptimizationAction();
        EvaluateOptimizations(functionId);
    }

    /// <summary>Gets optimization recommendations for a function.</summary>
    public IReadOnlyList<ServerlessRecommendation> GetRecommendations(string functionId)
    {
        var recommendations = new List<ServerlessRecommendation>();

        // Finding 4446: snapshot data under brief lock, compute LINQ outside lock.
        ServerlessFunction? function;
        List<FunctionExecution> executions;
        lock (_lock)
        {
            if (!_functions.TryGetValue(functionId, out function)) return recommendations;
            executions = _executions.Where(e => e.FunctionId == functionId).ToList();
        }

        if (executions.Count < 10) return recommendations;

        // Check cold start rate
        var coldStartRate = (double)function.ColdStartCount / function.InvocationCount * 100;
        if (coldStartRate > ColdStartAlertThreshold)
        {
            recommendations.Add(new ServerlessRecommendation
            {
                FunctionId = functionId,
                Type = ServerlessRecommendationType.ReduceColdStarts,
                Description = $"Cold start rate at {coldStartRate:F0}%. Consider provisioned concurrency or warming.",
                Priority = 7,
                EstimatedSavingsMs = executions.Where(e => e.WasColdStart).Average(e => e.DurationMs) * 0.5
            });
        }

        // Check memory efficiency
        var avgMemoryEfficiency = executions.Average(e => e.MemoryEfficiency);
        if (avgMemoryEfficiency < 0.5)
        {
            var currentMem = function.MemoryMb;
            var recommendedMem = (int)(currentMem * avgMemoryEfficiency * 1.5);
            recommendedMem = Math.Max(128, (recommendedMem / 64) * 64); // Round to 64MB

            recommendations.Add(new ServerlessRecommendation
            {
                FunctionId = functionId,
                Type = ServerlessRecommendationType.ReduceMemory,
                Description = $"Avg memory usage {avgMemoryEfficiency * 100:F0}%. Reduce from {currentMem}MB to {recommendedMem}MB.",
                Priority = 6,
                RecommendedMemoryMb = recommendedMem,
                EstimatedCostSavingsPercent = (1 - (double)recommendedMem / currentMem) * 100
            });
        }
        else if (avgMemoryEfficiency > 0.9)
        {
            recommendations.Add(new ServerlessRecommendation
            {
                FunctionId = functionId,
                Type = ServerlessRecommendationType.IncreaseMemory,
                Description = $"Memory usage at {avgMemoryEfficiency * 100:F0}%. Increase to avoid OOM and improve CPU allocation.",
                Priority = 5,
                RecommendedMemoryMb = (int)(function.MemoryMb * 1.5),
                EstimatedCostSavingsPercent = 0
            });
        }

        // Check execution patterns
        var avgDuration = executions.Average(e => e.DurationMs);
        var maxDuration = executions.Max(e => e.DurationMs);
        if (maxDuration > avgDuration * 3)
        {
            recommendations.Add(new ServerlessRecommendation
            {
                FunctionId = functionId,
                Type = ServerlessRecommendationType.OptimizeCode,
                Description = $"High variance in execution time (avg {avgDuration:F0}ms, max {maxDuration:F0}ms). Review for optimization.",
                Priority = 4,
                EstimatedSavingsMs = maxDuration - avgDuration
            });
        }

        return recommendations.OrderByDescending(r => r.Priority).ToList();
    }

    /// <summary>Gets function statistics.</summary>
    public ServerlessFunctionStats GetFunctionStats(string functionId)
    {
        // Finding 4446: snapshot under brief lock, compute stats outside.
        ServerlessFunction? function;
        List<FunctionExecution> executions;
        lock (_lock)
        {
            if (!_functions.TryGetValue(functionId, out function))
                return new ServerlessFunctionStats { FunctionId = functionId };
            executions = _executions.Where(e => e.FunctionId == functionId).ToList();
        }

        if (!executions.Any())
            return new ServerlessFunctionStats { FunctionId = functionId, FunctionName = function.Name };

        return new ServerlessFunctionStats
        {
            FunctionId = functionId,
            FunctionName = function.Name,
            TotalInvocations = function.InvocationCount,
            ColdStartCount = function.ColdStartCount,
            ColdStartRate = (double)function.ColdStartCount / function.InvocationCount * 100,
            AvgDurationMs = executions.Average(e => e.DurationMs),
            P95DurationMs = executions.OrderBy(e => e.DurationMs).Skip((int)(executions.Count * 0.95)).FirstOrDefault()?.DurationMs ?? 0,
            AvgMemoryEfficiency = executions.Average(e => e.MemoryEfficiency) * 100
        };
    }

    private void EvaluateOptimizations(string functionId)
    {
        ClearRecommendations();
        var recs = GetRecommendations(functionId);

        if (recs.Any())
        {
            var topRec = recs.First();
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-{functionId}-{topRec.Type}",
                Type = topRec.Type.ToString(),
                Priority = topRec.Priority,
                Description = topRec.Description,
                CanAutoApply = topRec.Type == ServerlessRecommendationType.ReduceMemory
            });
        }
    }
}

/// <summary>Serverless function information.</summary>
public sealed class ServerlessFunction
{
    public required string FunctionId { get; init; }
    public required string Name { get; init; }
    public required string Runtime { get; init; }
    public required int MemoryMb { get; init; }
    public required string Provider { get; init; }
    public long InvocationCount { get; set; }
    public long ColdStartCount { get; set; }
}

/// <summary>Function execution record.</summary>
public sealed record FunctionExecution
{
    public required string FunctionId { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required double DurationMs { get; init; }
    public required double BilledDurationMs { get; init; }
    public required int MemoryUsedMb { get; init; }
    public required bool WasColdStart { get; init; }
    public required double MemoryEfficiency { get; init; }
}

/// <summary>Serverless recommendation type.</summary>
public enum ServerlessRecommendationType { ReduceColdStarts, ReduceMemory, IncreaseMemory, OptimizeCode }

/// <summary>Serverless optimization recommendation.</summary>
public sealed record ServerlessRecommendation
{
    public required string FunctionId { get; init; }
    public required ServerlessRecommendationType Type { get; init; }
    public required string Description { get; init; }
    public required int Priority { get; init; }
    public int? RecommendedMemoryMb { get; init; }
    public double EstimatedCostSavingsPercent { get; init; }
    public double EstimatedSavingsMs { get; init; }
}

/// <summary>Serverless function statistics.</summary>
public sealed record ServerlessFunctionStats
{
    public required string FunctionId { get; init; }
    public string? FunctionName { get; init; }
    public long TotalInvocations { get; init; }
    public long ColdStartCount { get; init; }
    public double ColdStartRate { get; init; }
    public double AvgDurationMs { get; init; }
    public double P95DurationMs { get; init; }
    public double AvgMemoryEfficiency { get; init; }
}
