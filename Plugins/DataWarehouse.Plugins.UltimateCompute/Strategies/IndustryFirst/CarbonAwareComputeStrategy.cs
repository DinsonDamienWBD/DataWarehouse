using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.IndustryFirst;

/// <summary>
/// Carbon-aware compute strategy that queries carbon intensity data (via WattTime/Electricity
/// Maps API pattern) and defers non-urgent tasks to low-carbon periods. Tracks cumulative
/// gCO2e emissions per computation for sustainability reporting.
/// </summary>
/// <remarks>
/// <para>
/// The strategy checks the current carbon intensity for the configured region. If intensity
/// exceeds a configurable threshold and the task is deferrable (not marked urgent), execution
/// is delayed until intensity drops below the threshold or a maximum deferral period elapses.
/// All executions record their estimated carbon footprint based on power consumption (TDP)
/// multiplied by duration and grid carbon intensity.
/// </para>
/// </remarks>
internal sealed class CarbonAwareComputeStrategy : ComputeRuntimeStrategyBase
{
    private readonly ConcurrentDictionary<string, CarbonRecord> _emissions = new();
    private readonly ConcurrentDictionary<string, double> _regionIntensity = new();
    private const double DefaultCarbonIntensity = 400.0; // gCO2e/kWh (global average)
    private const double DefaultTdpWatts = 65.0; // Average server CPU TDP
    private const double MaxDeferralMinutes = 60.0;

    /// <inheritdoc/>
    public override string StrategyId => "compute.industryfirst.carbonaware";
    /// <inheritdoc/>
    public override string StrategyName => "Carbon-Aware Compute";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Custom;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: true, SupportsSandboxing: false,
        MaxMemoryBytes: 16L * 1024 * 1024 * 1024, MaxExecutionTime: TimeSpan.FromHours(4),
        SupportedLanguages: ["any"], SupportsMultiThreading: true,
        SupportsAsync: true, SupportsNetworkAccess: true, SupportsFileSystemAccess: true,
        MaxConcurrentTasks: 16, MemoryIsolation: MemoryIsolationLevel.Process);
    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.Custom];

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            // Parse carbon configuration
            var region = "US-WECC-CISO"; // Default: California
            if (task.Metadata?.TryGetValue("carbon_region", out var cr) == true && cr is string crs)
                region = crs;

            var intensityThreshold = 200.0; // gCO2e/kWh threshold for deferral
            if (task.Metadata?.TryGetValue("carbon_threshold", out var ct) == true && ct is double ctd)
                intensityThreshold = ctd;

            var tdpWatts = DefaultTdpWatts;
            if (task.Metadata?.TryGetValue("tdp_watts", out var tw) == true && tw is double twd)
                tdpWatts = twd;

            var isUrgent = task.Metadata?.TryGetValue("urgent", out var u) == true &&
                           (u is true || (u is string us && us.Equals("true", StringComparison.OrdinalIgnoreCase)));

            var maxDeferral = TimeSpan.FromMinutes(MaxDeferralMinutes);
            if (task.Metadata?.TryGetValue("max_deferral_minutes", out var md) == true && md is double mdd)
                maxDeferral = TimeSpan.FromMinutes(mdd);

            // Query current carbon intensity
            var currentIntensity = await QueryCarbonIntensityAsync(region, cancellationToken);
            var deferred = false;
            var deferralTime = TimeSpan.Zero;

            // Deferral logic for non-urgent tasks in high-carbon periods
            if (!isUrgent && currentIntensity > intensityThreshold)
            {
                var deferralStart = DateTime.UtcNow;
                var deferralDeadline = deferralStart.Add(maxDeferral);

                while (currentIntensity > intensityThreshold && DateTime.UtcNow < deferralDeadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    deferred = true;

                    // Wait and re-check (exponential backoff: 30s, 60s, 120s, up to 300s)
                    var waitTime = TimeSpan.FromSeconds(Math.Min(300, 30 * Math.Pow(2, deferralTime.TotalMinutes / 5)));
                    await Task.Delay(waitTime, cancellationToken);

                    currentIntensity = await QueryCarbonIntensityAsync(region, cancellationToken);
                }

                deferralTime = DateTime.UtcNow - deferralStart;
            }

            // Execute the computation
            var codePath = Path.GetTempFileName() + ".sh";
            try
            {
                await File.WriteAllBytesAsync(codePath, task.Code.ToArray(), cancellationToken);

                var env = new Dictionary<string, string>(task.Environment ?? new Dictionary<string, string>())
                {
                    ["CARBON_REGION"] = region,
                    ["CARBON_INTENSITY"] = currentIntensity.ToString("F1"),
                    ["CARBON_DEFERRED"] = deferred.ToString(),
                    ["CARBON_DEFERRAL_SECONDS"] = deferralTime.TotalSeconds.ToString("F0")
                };

                var timeout = GetEffectiveTimeout(task);
                var result = await RunProcessAsync("sh", $"\"{codePath}\"",
                    stdin: task.InputData.Length > 0 ? task.GetInputDataAsString() : null,
                    environment: env, timeout: timeout, cancellationToken: cancellationToken);

                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"Carbon-aware execution failed: {result.StandardError}");

                // Calculate carbon footprint
                var durationHours = result.Elapsed.TotalHours;
                var energyKwh = (tdpWatts / 1000.0) * durationHours;
                var co2Grams = energyKwh * currentIntensity;

                // PUE (Power Usage Effectiveness) overhead for datacenter cooling etc.
                var pue = 1.2; // Industry average
                if (task.Metadata?.TryGetValue("pue", out var pueVal) == true && pueVal is double pued)
                    pue = pued;
                co2Grams *= pue;

                // Record emissions
                _emissions[task.Id] = new CarbonRecord(
                    region, currentIntensity, energyKwh, co2Grams,
                    deferred, deferralTime, DateTime.UtcNow);

                // Calculate cumulative emissions
                var totalEmissions = _emissions.Values.Sum(r => r.Co2Grams);
                var totalEnergy = _emissions.Values.Sum(r => r.EnergyKwh);
                var avgIntensity = _emissions.Values.Average(r => r.Intensity);

                var logs = new StringBuilder();
                logs.AppendLine($"CarbonAware: region={region}, intensity={currentIntensity:F1} gCO2e/kWh");
                logs.AppendLine($"  Energy: {energyKwh * 1000:F3} Wh, CO2: {co2Grams:F4} gCO2e (PUE={pue:F2})");
                logs.AppendLine($"  TDP: {tdpWatts:F0}W, Duration: {result.Elapsed.TotalSeconds:F1}s");
                if (deferred)
                    logs.AppendLine($"  Deferred: {deferralTime.TotalSeconds:F0}s (threshold={intensityThreshold:F0} gCO2e/kWh)");
                else
                    logs.AppendLine($"  Not deferred (urgent={isUrgent}, intensity={currentIntensity:F0} vs threshold={intensityThreshold:F0})");
                logs.AppendLine($"  Cumulative: {totalEmissions:F2} gCO2e across {_emissions.Count} executions, avg intensity={avgIntensity:F1}");

                return (EncodeOutput(result.StandardOutput), logs.ToString());
            }
            finally
            {
                try { File.Delete(codePath); } catch { /* Best-effort cleanup */ }
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Queries the current carbon intensity for a region.
    /// Attempts to call a carbon intensity API endpoint; falls back to cached or default values.
    /// </summary>
    private async Task<double> QueryCarbonIntensityAsync(string region, CancellationToken cancellationToken)
    {
        // Try to query carbon intensity API (WattTime/Electricity Maps pattern)
        try
        {
            var apiUrl = $"https://api.carbonintensity.org.uk/intensity";
            var result = await RunProcessAsync("curl", $"-s --max-time 5 \"{apiUrl}\"",
                timeout: TimeSpan.FromSeconds(10), cancellationToken: cancellationToken);

            if (result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                var json = JsonDocument.Parse(result.StandardOutput);
                if (json.RootElement.TryGetProperty("data", out var data) &&
                    data.ValueKind == JsonValueKind.Array)
                {
                    var firstEntry = data.EnumerateArray().FirstOrDefault();
                    if (firstEntry.TryGetProperty("intensity", out var intensity) &&
                        intensity.TryGetProperty("actual", out var actual))
                    {
                        var value = actual.GetDouble();
                        _regionIntensity[region] = value;
                        return value;
                    }
                }
            }
        }
        catch
        {
            // API unavailable, use cached or default
        }

        // Return cached value or default
        return _regionIntensity.GetValueOrDefault(region, DefaultCarbonIntensity);
    }

    /// <summary>Carbon emission record for a single execution.</summary>
    private record CarbonRecord(
        string Region, double Intensity, double EnergyKwh, double Co2Grams,
        bool WasDeferred, TimeSpan DeferralTime, DateTime Timestamp);
}
