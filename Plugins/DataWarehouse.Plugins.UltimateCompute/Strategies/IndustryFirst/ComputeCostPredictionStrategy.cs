using System.Collections.Concurrent;
using System.Text;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.IndustryFirst;

/// <summary>
/// Predicts and tracks compute cost using historical execution data with linear regression
/// on (input_size, runtime_type, duration) tuples. Models cost per vCPU-second and provides
/// pre-execution cost estimates for budget-aware scheduling.
/// </summary>
/// <remarks>
/// <para>
/// Maintains a sliding window of execution records per strategy. Uses ordinary least squares
/// regression to model: predicted_duration = a * input_size + b, then multiplies by
/// cost_per_vcpu_second to produce dollar estimates. Actual execution is delegated to a shell
/// script with cost metadata injected as environment variables.
/// </para>
/// </remarks>
internal sealed class ComputeCostPredictionStrategy : ComputeRuntimeStrategyBase
{
    private readonly ConcurrentDictionary<string, List<ExecutionRecord>> _history = new();
    private const int MaxHistoryPerRuntime = 1000;
    private const double DefaultCostPerVcpuSecond = 0.0000125; // ~$0.045/hr

    /// <inheritdoc/>
    public override string StrategyId => "compute.industryfirst.costprediction";
    /// <inheritdoc/>
    public override string StrategyName => "Compute Cost Prediction";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Custom;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: false, SupportsSandboxing: false,
        MaxMemoryBytes: 8L * 1024 * 1024 * 1024, MaxExecutionTime: TimeSpan.FromHours(2),
        SupportedLanguages: ["any"], SupportsMultiThreading: true,
        SupportsAsync: true, SupportsNetworkAccess: false, SupportsFileSystemAccess: true,
        MaxConcurrentTasks: 16, MemoryIsolation: MemoryIsolationLevel.Process);
    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.Custom];

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var inputSize = task.InputData.Length > 0 ? task.InputData.Length : task.Code.Length;
            var runtimeKey = task.Language ?? "unknown";
            var costPerVcpuSec = DefaultCostPerVcpuSecond;
            if (task.Metadata?.TryGetValue("cost_per_vcpu_sec", out var cpv) == true && cpv is double cpvd)
                costPerVcpuSec = cpvd;

            // Pre-execution cost prediction using linear regression
            var prediction = PredictCost(runtimeKey, inputSize, costPerVcpuSec);

            // Check budget limit
            if (task.Metadata?.TryGetValue("budget_limit", out var bl) == true && bl is double budgetLimit)
            {
                if (prediction.estimatedCost > budgetLimit)
                    throw new InvalidOperationException(
                        $"Predicted cost ${prediction.estimatedCost:F6} exceeds budget limit ${budgetLimit:F6}. " +
                        $"Predicted duration: {prediction.estimatedDuration:F1}s for {inputSize} bytes input.");
            }

            // Execute the actual computation
            var codePath = Path.GetTempFileName() + ".sh";
            try
            {
                await File.WriteAllBytesAsync(codePath, task.Code.ToArray(), cancellationToken);

                var env = new Dictionary<string, string>(task.Environment ?? new Dictionary<string, string>())
                {
                    ["COST_PREDICTED_DURATION"] = prediction.estimatedDuration.ToString("F3"),
                    ["COST_PREDICTED_COST"] = prediction.estimatedCost.ToString("F8"),
                    ["COST_PER_VCPU_SEC"] = costPerVcpuSec.ToString("F10"),
                    ["COST_INPUT_SIZE"] = inputSize.ToString(),
                    ["COST_CONFIDENCE"] = prediction.confidence.ToString("F3")
                };

                var timeout = GetEffectiveTimeout(task);
                var result = await RunProcessAsync("sh", $"\"{codePath}\"",
                    stdin: task.InputData.Length > 0 ? task.GetInputDataAsString() : null,
                    environment: env, timeout: timeout, cancellationToken: cancellationToken);

                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"Compute cost prediction execution failed: {result.StandardError}");

                var actualDuration = result.Elapsed.TotalSeconds;
                var actualCost = actualDuration * costPerVcpuSec;

                // Record execution for future predictions
                RecordExecution(runtimeKey, inputSize, actualDuration, actualCost);

                var accuracy = prediction.estimatedDuration > 0
                    ? Math.Max(0, 1.0 - Math.Abs(actualDuration - prediction.estimatedDuration) / prediction.estimatedDuration)
                    : 0.0;

                var logs = new StringBuilder();
                logs.AppendLine($"CostPrediction: runtime={runtimeKey}, input={inputSize}B");
                logs.AppendLine($"  Predicted: {prediction.estimatedDuration:F3}s, ${prediction.estimatedCost:F8} (confidence={prediction.confidence:F3})");
                logs.AppendLine($"  Actual:    {actualDuration:F3}s, ${actualCost:F8}");
                logs.AppendLine($"  Accuracy:  {accuracy:P1}");
                logs.AppendLine($"  History:   {GetHistoryCount(runtimeKey)} records for '{runtimeKey}'");

                return (EncodeOutput(result.StandardOutput), logs.ToString());
            }
            finally
            {
                try { File.Delete(codePath); } catch { }
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Predicts execution cost using linear regression on historical data.
    /// </summary>
    private (double estimatedDuration, double estimatedCost, double confidence) PredictCost(
        string runtimeKey, long inputSize, double costPerVcpuSec)
    {
        if (!_history.TryGetValue(runtimeKey, out var records) || records.Count < 2)
        {
            // Not enough data: use heuristic (1KB/sec baseline)
            var heuristicDuration = Math.Max(0.1, inputSize / (1024.0 * 1024.0));
            return (heuristicDuration, heuristicDuration * costPerVcpuSec, 0.1);
        }

        // Ordinary Least Squares: duration = a * input_size + b
        var n = records.Count;
        double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;

        foreach (var record in records)
        {
            sumX += record.InputSize;
            sumY += record.Duration;
            sumXY += record.InputSize * record.Duration;
            sumX2 += record.InputSize * record.InputSize;
        }

        var denominator = n * sumX2 - sumX * sumX;
        double a, b;
        if (Math.Abs(denominator) < 1e-10)
        {
            a = 0;
            b = sumY / n;
        }
        else
        {
            a = (n * sumXY - sumX * sumY) / denominator;
            b = (sumY - a * sumX) / n;
        }

        var estimatedDuration = Math.Max(0.01, a * inputSize + b);
        var estimatedCost = estimatedDuration * costPerVcpuSec;

        // Compute R-squared for confidence
        var meanY = sumY / n;
        double ssRes = 0, ssTot = 0;
        foreach (var record in records)
        {
            var predicted = a * record.InputSize + b;
            ssRes += (record.Duration - predicted) * (record.Duration - predicted);
            ssTot += (record.Duration - meanY) * (record.Duration - meanY);
        }

        var rSquared = ssTot > 0 ? Math.Max(0, 1.0 - ssRes / ssTot) : 0.0;
        var confidence = Math.Min(1.0, rSquared * Math.Min(1.0, n / 20.0)); // Scale by sample size

        return (estimatedDuration, estimatedCost, confidence);
    }

    /// <summary>
    /// Records an execution for future cost prediction.
    /// </summary>
    private void RecordExecution(string runtimeKey, long inputSize, double duration, double cost)
    {
        var record = new ExecutionRecord(inputSize, duration, cost, DateTime.UtcNow);
        _history.AddOrUpdate(runtimeKey,
            _ => [record],
            (_, list) =>
            {
                list.Add(record);
                if (list.Count > MaxHistoryPerRuntime)
                    list.RemoveRange(0, list.Count - MaxHistoryPerRuntime);
                return list;
            });
    }

    /// <summary>
    /// Gets the number of historical records for a runtime key.
    /// </summary>
    private int GetHistoryCount(string runtimeKey) =>
        _history.TryGetValue(runtimeKey, out var records) ? records.Count : 0;

    /// <summary>Historical execution record for regression.</summary>
    private record ExecutionRecord(long InputSize, double Duration, double Cost, DateTime Timestamp);
}
