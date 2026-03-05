using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Moonshots;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Plugins.UltimateDataGovernance.Moonshots.HealthProbes;

/// <summary>
/// Aggregates all 10 moonshot health probes into a unified health view.
/// Runs probes in parallel (they are fully independent), updates the moonshot
/// registry with each report, and supports periodic background health checks
/// respecting each probe's individual <see cref="IMoonshotHealthProbe.HealthCheckInterval"/>.
/// </summary>
public sealed class MoonshotHealthAggregator
{
    private readonly IReadOnlyDictionary<MoonshotId, IMoonshotHealthProbe> _probes;
    private readonly IMoonshotRegistry _registry;
    private readonly ILogger<MoonshotHealthAggregator> _logger;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<MoonshotId, DateTimeOffset> _lastCheckTimes = new();

    /// <summary>
    /// Creates a new aggregator from the set of registered health probes.
    /// </summary>
    /// <param name="probes">All registered moonshot health probes (typically 10).</param>
    /// <param name="registry">The moonshot registry to update with health reports.</param>
    /// <param name="logger">Logger for aggregator diagnostics.</param>
    public MoonshotHealthAggregator(
        IEnumerable<IMoonshotHealthProbe> probes,
        IMoonshotRegistry registry,
        ILogger<MoonshotHealthAggregator> logger)
    {
        ArgumentNullException.ThrowIfNull(probes);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(logger);

        var probeDict = new Dictionary<MoonshotId, IMoonshotHealthProbe>();
        foreach (var probe in probes)
        {
            probeDict[probe.MoonshotId] = probe;
        }

        _probes = probeDict;
        _registry = registry;
        _logger = logger;
    }

    /// <summary>
    /// Runs all 10 health probes in parallel and returns the combined results.
    /// Updates the <see cref="IMoonshotRegistry"/> with each report and adjusts
    /// moonshot status (Ready, Degraded, or Faulted) accordingly.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All health reports from the parallel probe execution.</returns>
    public async Task<IReadOnlyList<MoonshotHealthReport>> CheckAllAsync(CancellationToken ct)
    {
        // P2-2259: Warn when no probes are registered so operators know monitoring is non-functional.
        if (_probes.Count == 0)
        {
            _logger.LogWarning("CheckAllAsync called with 0 registered health probes — health monitoring is non-functional. Register probes via IServiceCollection before starting.");
            return Array.Empty<MoonshotHealthReport>();
        }

        var sw = Stopwatch.StartNew();
        _logger.LogInformation("Starting health check for {ProbeCount} moonshot probes", _probes.Count);

        // Run all probes in parallel -- they are independent
        var tasks = _probes.Values.Select(probe => RunProbeAsync(probe, ct)).ToArray();
        var reports = await Task.WhenAll(tasks);

        // Update registry with each report
        var readyCount = 0;
        var degradedCount = 0;
        var faultedCount = 0;

        foreach (var report in reports)
        {
            _registry.UpdateHealthReport(report.Id, report);
            _lastCheckTimes[report.Id] = report.CheckedAt;

            var status = report.Readiness switch
            {
                MoonshotReadiness.Ready => MoonshotStatus.Ready,
                MoonshotReadiness.Degraded => MoonshotStatus.Degraded,
                _ => MoonshotStatus.Faulted
            };

            _registry.UpdateStatus(report.Id, status);

            switch (status)
            {
                case MoonshotStatus.Ready: readyCount++; break;
                case MoonshotStatus.Degraded: degradedCount++; break;
                default: faultedCount++; break;
            }
        }

        sw.Stop();
        _logger.LogInformation(
            "{ReadyCount}/{Total} moonshots ready, {DegradedCount} degraded, {FaultedCount} faulted (checked in {Duration}ms)",
            readyCount, reports.Length, degradedCount, faultedCount, sw.ElapsedMilliseconds);

        return reports;
    }

    /// <summary>
    /// Runs periodic health checks in a background loop until cancellation.
    /// Each probe is checked independently based on its own <see cref="IMoonshotHealthProbe.HealthCheckInterval"/>.
    /// Due probes are run in parallel within each tick.
    /// </summary>
    /// <param name="ct">Cancellation token to stop the periodic loop.</param>
    public async Task RunPeriodicHealthChecksAsync(CancellationToken ct)
    {
        _logger.LogInformation("Starting periodic moonshot health checks for {ProbeCount} probes", _probes.Count);

        // LOW-2262: No initial full check here — _lastCheckTimes starts empty so all probes are
        // "due" on the very first loop iteration, which will run them in parallel as designed.
        // A pre-loop CheckAllAsync would double-run all probes before the periodic logic applies.

        while (!ct.IsCancellationRequested)
        {
            // Determine which probes are due for a check
            var now = DateTimeOffset.UtcNow;
            var dueProbes = new List<IMoonshotHealthProbe>();
            var shortestRemaining = TimeSpan.MaxValue;

            foreach (var (id, probe) in _probes)
            {
                var lastCheck = _lastCheckTimes.GetValueOrDefault(id, DateTimeOffset.MinValue);
                var elapsed = now - lastCheck;
                var remaining = probe.HealthCheckInterval - elapsed;

                if (remaining <= TimeSpan.Zero)
                {
                    dueProbes.Add(probe);
                }
                else if (remaining < shortestRemaining)
                {
                    shortestRemaining = remaining;
                }
            }

            if (dueProbes.Count > 0)
            {
                _logger.LogDebug("Running {DueCount} due health probes", dueProbes.Count);

                // Run due probes in parallel
                var tasks = dueProbes.Select(probe => RunProbeAsync(probe, ct)).ToArray();
                var reports = await Task.WhenAll(tasks);

                foreach (var report in reports)
                {
                    _registry.UpdateHealthReport(report.Id, report);
                    _lastCheckTimes[report.Id] = report.CheckedAt;

                    var status = report.Readiness switch
                    {
                        MoonshotReadiness.Ready => MoonshotStatus.Ready,
                        MoonshotReadiness.Degraded => MoonshotStatus.Degraded,
                        _ => MoonshotStatus.Faulted
                    };

                    _registry.UpdateStatus(report.Id, status);
                }

                // Recalculate shortest remaining after checks
                now = DateTimeOffset.UtcNow;
                shortestRemaining = TimeSpan.MaxValue;
                foreach (var (id, probe) in _probes)
                {
                    var lastCheck = _lastCheckTimes.GetValueOrDefault(id, DateTimeOffset.MinValue);
                    var remaining = probe.HealthCheckInterval - (now - lastCheck);
                    if (remaining > TimeSpan.Zero && remaining < shortestRemaining)
                        shortestRemaining = remaining;
                }
            }

            // Await the shortest remaining interval before checking again
            var waitTime = shortestRemaining == TimeSpan.MaxValue
                ? TimeSpan.FromSeconds(30)
                : shortestRemaining;

            if (waitTime < TimeSpan.FromSeconds(1))
                waitTime = TimeSpan.FromSeconds(1);

            try
            {
                await Task.Delay(waitTime, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("Periodic moonshot health checks stopped");
    }

    /// <summary>
    /// Safely runs a single probe, catching exceptions so one failing probe
    /// does not crash the entire aggregation run.
    /// </summary>
    private async Task<MoonshotHealthReport> RunProbeAsync(IMoonshotHealthProbe probe, CancellationToken ct)
    {
        try
        {
            return await probe.CheckHealthAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Let cancellation propagate
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Health probe for {MoonshotId} threw an unhandled exception", probe.MoonshotId);

            return new MoonshotHealthReport(
                probe.MoonshotId,
                MoonshotReadiness.Unknown,
                $"Health probe failed: {ex.Message}",
                new Dictionary<string, MoonshotComponentHealth>
                {
                    ["ProbeError"] = new MoonshotComponentHealth(
                        "ProbeError",
                        MoonshotReadiness.Unknown,
                        ex.Message,
                        new Dictionary<string, string> { ["exception_type"] = ex.GetType().Name })
                },
                DateTimeOffset.UtcNow,
                TimeSpan.Zero);
        }
    }
}
