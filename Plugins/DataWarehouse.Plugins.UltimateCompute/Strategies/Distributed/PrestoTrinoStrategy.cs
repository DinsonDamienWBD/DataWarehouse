using System.Text;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Distributed;

/// <summary>
/// Presto/Trino distributed SQL query engine strategy.
/// Uses trino CLI for query execution with catalog/schema management.
/// </summary>
internal sealed class PrestoTrinoStrategy : ComputeRuntimeStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "compute.distributed.trino";
    /// <inheritdoc/>
    public override string StrategyName => "Presto/Trino";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Custom;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: true, SupportsSandboxing: false,
        MaxMemoryBytes: 32L * 1024 * 1024 * 1024, MaxExecutionTime: TimeSpan.FromHours(4),
        SupportedLanguages: ["sql"], SupportsMultiThreading: true,
        SupportsAsync: true, SupportsNetworkAccess: true, SupportsFileSystemAccess: true,
        MaxConcurrentTasks: 100, MemoryIsolation: MemoryIsolationLevel.Process);
    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.Custom];

    /// <inheritdoc/>
    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await IsToolAvailableAsync("trino", "--version", cancellationToken);
    }

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var sqlPath = Path.GetTempFileName() + ".sql";
            try
            {
                await File.WriteAllBytesAsync(sqlPath, task.Code.ToArray(), cancellationToken);

                var server = "http://localhost:8080";
                if (task.Metadata?.TryGetValue("trino_server", out var s) == true && s is string ss)
                    server = ss;

                var catalog = "hive";
                if (task.Metadata?.TryGetValue("catalog", out var c) == true && c is string cs)
                    catalog = cs;

                var schema = "default";
                if (task.Metadata?.TryGetValue("schema", out var sc) == true && sc is string scs)
                    schema = scs;

                var outputFormat = "CSV_HEADER";
                if (task.Metadata?.TryGetValue("output_format", out var of) == true && of is string ofs)
                    outputFormat = ofs;

                var args = new StringBuilder();
                args.Append($"--server {server} ");
                args.Append($"--catalog {catalog} ");
                args.Append($"--schema {schema} ");
                args.Append($"--output-format {outputFormat} ");
                args.Append($"--file \"{sqlPath}\"");

                if (task.Metadata?.TryGetValue("session_properties", out var sp) == true && sp is Dictionary<string, object> spDict)
                {
                    foreach (var (key, value) in spDict)
                        args.Append($" --session {key}={value}");
                }

                var timeout = GetEffectiveTimeout(task, TimeSpan.FromMinutes(30));
                var result = await RunProcessAsync("trino", args.ToString(),
                    environment: task.Environment, timeout: timeout, cancellationToken: cancellationToken);

                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"Trino query failed with code {result.ExitCode}: {result.StandardError}");

                return (EncodeOutput(result.StandardOutput), $"Trino ({server}, {catalog}.{schema}) completed in {result.Elapsed.TotalMilliseconds:F0}ms\n{result.StandardError}");
            }
            finally
            {
                try { File.Delete(sqlPath); } catch { }
            }
        }, cancellationToken);
    }
}
