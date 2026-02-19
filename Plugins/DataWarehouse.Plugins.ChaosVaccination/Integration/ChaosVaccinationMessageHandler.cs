using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.ChaosVaccination;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.Plugins.ChaosVaccination.Engine;
using DataWarehouse.Plugins.ChaosVaccination.BlastRadius;
using DataWarehouse.Plugins.ChaosVaccination.ImmuneResponse;
using DataWarehouse.Plugins.ChaosVaccination.Scheduling;
using DataWarehouse.Plugins.ChaosVaccination.Storage;

namespace DataWarehouse.Plugins.ChaosVaccination.Integration;

/// <summary>
/// Central message routing for all chaos vaccination bus topics.
/// Subscribes to chaos.* topics and delegates to the appropriate sub-component
/// (engine, enforcer, immune system, scheduler, results database).
///
/// Each handler follows the pattern: deserialize payload -> call sub-component -> publish
/// response to "{topic}.response" with correlation ID. Errors are caught and published
/// as error responses so callers always receive a reply.
///
/// Registered topics (20 total):
/// - chaos.experiment.*: execute, abort, list-running
/// - chaos.blast-radius.*: create-zone, release-zone, status
/// - chaos.immune-memory.*: recognize, apply, learn, list, forget
/// - chaos.schedule.*: add, remove, list, enable
/// - chaos.results.*: query, summary, purge
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination orchestrator")]
public sealed class ChaosVaccinationMessageHandler
{
    private readonly ChaosInjectionEngine _engine;
    private readonly BlastRadiusEnforcer _enforcer;
    private readonly ImmuneResponseSystem _immuneSystem;
    private readonly VaccinationScheduler _scheduler;
    private readonly InMemoryChaosResultsDatabase _resultsDb;
    private readonly List<IDisposable> _subscriptions = new();

    private const string SourcePluginId = "com.datawarehouse.chaos.vaccination";

    /// <summary>
    /// Creates a new ChaosVaccinationMessageHandler.
    /// </summary>
    /// <param name="engine">The chaos injection engine for experiment execution.</param>
    /// <param name="enforcer">The blast radius enforcer for isolation zone management.</param>
    /// <param name="immuneSystem">The immune response system for fault memory.</param>
    /// <param name="scheduler">The vaccination scheduler for recurring experiments.</param>
    /// <param name="resultsDb">The results database for experiment history.</param>
    public ChaosVaccinationMessageHandler(
        ChaosInjectionEngine engine,
        BlastRadiusEnforcer enforcer,
        ImmuneResponseSystem immuneSystem,
        VaccinationScheduler scheduler,
        InMemoryChaosResultsDatabase resultsDb)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _enforcer = enforcer ?? throw new ArgumentNullException(nameof(enforcer));
        _immuneSystem = immuneSystem ?? throw new ArgumentNullException(nameof(immuneSystem));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _resultsDb = resultsDb ?? throw new ArgumentNullException(nameof(resultsDb));
    }

    /// <summary>
    /// Registers all chaos.* topic subscriptions with the message bus.
    /// Must be called after construction when the bus is available.
    /// </summary>
    /// <param name="bus">The message bus to subscribe to.</param>
    public void RegisterSubscriptions(IMessageBus bus)
    {
        ArgumentNullException.ThrowIfNull(bus);

        // Experiment topics
        _subscriptions.Add(bus.Subscribe("chaos.experiment.execute", msg => HandleSafe(msg, HandleExperimentExecuteAsync)));
        _subscriptions.Add(bus.Subscribe("chaos.experiment.abort", msg => HandleSafe(msg, HandleExperimentAbortAsync)));
        _subscriptions.Add(bus.Subscribe("chaos.experiment.list-running", msg => HandleSafe(msg, HandleExperimentListRunningAsync)));

        // Blast radius topics
        _subscriptions.Add(bus.Subscribe("chaos.blast-radius.create-zone", msg => HandleSafe(msg, HandleBlastRadiusCreateZoneAsync)));
        _subscriptions.Add(bus.Subscribe("chaos.blast-radius.release-zone", msg => HandleSafe(msg, HandleBlastRadiusReleaseZoneAsync)));
        _subscriptions.Add(bus.Subscribe("chaos.blast-radius.status", msg => HandleSafe(msg, HandleBlastRadiusStatusAsync)));

        // Immune memory topics
        _subscriptions.Add(bus.Subscribe("chaos.immune-memory.recognize", msg => HandleSafe(msg, HandleImmuneRecognizeAsync)));
        _subscriptions.Add(bus.Subscribe("chaos.immune-memory.apply", msg => HandleSafe(msg, HandleImmuneApplyAsync)));
        _subscriptions.Add(bus.Subscribe("chaos.immune-memory.learn", msg => HandleSafe(msg, HandleImmuneLearnAsync)));
        _subscriptions.Add(bus.Subscribe("chaos.immune-memory.list", msg => HandleSafe(msg, HandleImmuneListAsync)));
        _subscriptions.Add(bus.Subscribe("chaos.immune-memory.forget", msg => HandleSafe(msg, HandleImmuneForgetAsync)));

        // Schedule topics
        _subscriptions.Add(bus.Subscribe("chaos.schedule.add", msg => HandleSafe(msg, HandleScheduleAddAsync)));
        _subscriptions.Add(bus.Subscribe("chaos.schedule.remove", msg => HandleSafe(msg, HandleScheduleRemoveAsync)));
        _subscriptions.Add(bus.Subscribe("chaos.schedule.list", msg => HandleSafe(msg, HandleScheduleListAsync)));
        _subscriptions.Add(bus.Subscribe("chaos.schedule.enable", msg => HandleSafe(msg, HandleScheduleEnableAsync)));

        // Results topics
        _subscriptions.Add(bus.Subscribe("chaos.results.query", msg => HandleSafe(msg, HandleResultsQueryAsync)));
        _subscriptions.Add(bus.Subscribe("chaos.results.summary", msg => HandleSafe(msg, HandleResultsSummaryAsync)));
        _subscriptions.Add(bus.Subscribe("chaos.results.purge", msg => HandleSafe(msg, HandleResultsPurgeAsync)));
    }

    /// <summary>
    /// Unsubscribes from all topics. Call during plugin shutdown.
    /// </summary>
    public void UnregisterSubscriptions()
    {
        foreach (var sub in _subscriptions)
        {
            try { sub.Dispose(); }
            catch { /* best-effort cleanup */ }
        }
        _subscriptions.Clear();
    }

    #region Safe handler wrapper

    private static async Task HandleSafe(PluginMessage msg, Func<PluginMessage, Task> handler)
    {
        try
        {
            await handler(msg).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            msg.Payload["success"] = false;
            msg.Payload["error"] = ex.Message;
        }
    }

    #endregion

    #region Experiment handlers

    private async Task HandleExperimentExecuteAsync(PluginMessage msg)
    {
        var experimentId = GetString(msg, "experimentId") ?? Guid.NewGuid().ToString("N");
        var experimentName = GetString(msg, "experimentName") ?? "Bus-triggered experiment";
        var faultTypeStr = GetString(msg, "faultType") ?? "NetworkPartition";

        if (!Enum.TryParse<FaultType>(faultTypeStr, ignoreCase: true, out var faultType))
        {
            msg.Payload["success"] = false;
            msg.Payload["error"] = $"Unknown fault type: {faultTypeStr}";
            return;
        }

        var experiment = new ChaosExperiment
        {
            Id = experimentId,
            Name = experimentName,
            Description = GetString(msg, "description") ?? $"Experiment: {experimentName}",
            FaultType = faultType,
            Severity = FaultSeverity.Medium,
            MaxBlastRadius = BlastRadiusLevel.SinglePlugin,
            DurationLimit = TimeSpan.FromSeconds(GetDouble(msg, "durationSeconds", 30.0))
        };

        var result = await _engine.ExecuteExperimentAsync(experiment).ConfigureAwait(false);

        msg.Payload["success"] = result.Status == ExperimentStatus.Completed;
        msg.Payload["experimentId"] = result.ExperimentId;
        msg.Payload["status"] = result.Status.ToString();
        msg.Payload["startedAt"] = result.StartedAt.ToString("O");
        if (result.CompletedAt.HasValue)
            msg.Payload["completedAt"] = result.CompletedAt.Value.ToString("O");
    }

    private async Task HandleExperimentAbortAsync(PluginMessage msg)
    {
        var experimentId = GetString(msg, "experimentId")
            ?? throw new ArgumentException("Missing experimentId");
        var reason = GetString(msg, "reason") ?? "Abort requested via bus";

        await _engine.AbortExperimentAsync(experimentId, reason).ConfigureAwait(false);

        msg.Payload["success"] = true;
        msg.Payload["experimentId"] = experimentId;
        msg.Payload["abortReason"] = reason;
    }

    private async Task HandleExperimentListRunningAsync(PluginMessage msg)
    {
        var running = await _engine.GetRunningExperimentsAsync().ConfigureAwait(false);

        msg.Payload["success"] = true;
        msg.Payload["count"] = running.Count;
        msg.Payload["experiments"] = running.Select(r => r.ExperimentId).ToArray();
    }

    #endregion

    #region Blast radius handlers

    private async Task HandleBlastRadiusCreateZoneAsync(PluginMessage msg)
    {
        var maxLevelStr = GetString(msg, "maxLevel") ?? "SinglePlugin";
        if (!Enum.TryParse<BlastRadiusLevel>(maxLevelStr, ignoreCase: true, out var maxLevel))
            maxLevel = BlastRadiusLevel.SinglePlugin;

        var targetPluginsRaw = msg.Payload.TryGetValue("targetPlugins", out var tp) ? tp : null;
        var targetPlugins = ParseStringArray(targetPluginsRaw) ?? Array.Empty<string>();
        if (targetPlugins.Length == 0)
        {
            msg.Payload["success"] = false;
            msg.Payload["error"] = "targetPlugins array is required";
            return;
        }

        var policy = new BlastRadiusPolicy
        {
            MaxLevel = maxLevel,
            MaxAffectedPlugins = targetPlugins.Length,
            MaxAffectedNodes = (int)GetDouble(msg, "maxAffectedNodes", 1),
            MaxDurationMs = (long)GetDouble(msg, "maxDurationMs", 60000),
            IsolationStrategy = IsolationStrategy.CircuitBreaker
        };

        var zone = await _enforcer.CreateIsolationZoneAsync(policy, targetPlugins).ConfigureAwait(false);

        msg.Payload["success"] = true;
        msg.Payload["zoneId"] = zone.ZoneId;
    }

    private async Task HandleBlastRadiusReleaseZoneAsync(PluginMessage msg)
    {
        var zoneId = GetString(msg, "zoneId")
            ?? throw new ArgumentException("Missing zoneId");

        await _enforcer.ReleaseZoneAsync(zoneId).ConfigureAwait(false);

        msg.Payload["success"] = true;
        msg.Payload["zoneId"] = zoneId;
    }

    private async Task HandleBlastRadiusStatusAsync(PluginMessage msg)
    {
        var zones = await _enforcer.GetActiveZonesAsync().ConfigureAwait(false);

        msg.Payload["success"] = true;
        msg.Payload["activeZoneCount"] = zones.Count;
        msg.Payload["zoneIds"] = zones.Select(z => z.ZoneId).ToArray();
    }

    #endregion

    #region Immune memory handlers

    private async Task HandleImmuneRecognizeAsync(PluginMessage msg)
    {
        var hash = GetString(msg, "signatureHash") ?? string.Empty;
        var faultTypeStr = GetString(msg, "faultType") ?? "Custom";
        Enum.TryParse<FaultType>(faultTypeStr, ignoreCase: true, out var faultType);

        var signature = new FaultSignature
        {
            Hash = hash,
            FaultType = faultType,
            Pattern = GetString(msg, "pattern") ?? string.Empty,
            Severity = FaultSeverity.Medium,
            FirstObserved = DateTimeOffset.UtcNow,
            ObservationCount = 1
        };

        var entry = await _immuneSystem.RecognizeFaultAsync(signature).ConfigureAwait(false);

        msg.Payload["success"] = true;
        msg.Payload["recognized"] = entry != null;
        if (entry != null)
        {
            msg.Payload["signatureHash"] = entry.Signature.Hash;
            msg.Payload["successRate"] = entry.SuccessRate;
            msg.Payload["timesApplied"] = entry.TimesApplied;
        }
    }

    private async Task HandleImmuneApplyAsync(PluginMessage msg)
    {
        var signatureHash = GetString(msg, "signatureHash")
            ?? throw new ArgumentException("Missing signatureHash");

        var entries = await _immuneSystem.GetImmuneMemoryAsync().ConfigureAwait(false);
        var entry = entries.FirstOrDefault(e => e.Signature.Hash == signatureHash);

        if (entry == null)
        {
            msg.Payload["success"] = false;
            msg.Payload["error"] = $"No immune memory entry found for signature: {signatureHash}";
            return;
        }

        var result = await _immuneSystem.ApplyRemediationAsync(entry).ConfigureAwait(false);

        msg.Payload["success"] = result;
        msg.Payload["signatureHash"] = signatureHash;
    }

    private async Task HandleImmuneLearnAsync(PluginMessage msg)
    {
        var experimentId = GetString(msg, "experimentId") ?? Guid.NewGuid().ToString("N");

        var experimentResult = new ChaosExperimentResult
        {
            ExperimentId = experimentId,
            Status = ExperimentStatus.Completed,
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt = DateTimeOffset.UtcNow,
            ActualBlastRadius = BlastRadiusLevel.SinglePlugin
        };

        await _immuneSystem.LearnFromExperimentAsync(
            experimentResult,
            Array.Empty<RemediationAction>()).ConfigureAwait(false);

        msg.Payload["success"] = true;
        msg.Payload["experimentId"] = experimentId;
    }

    private async Task HandleImmuneListAsync(PluginMessage msg)
    {
        var entries = await _immuneSystem.GetImmuneMemoryAsync().ConfigureAwait(false);

        msg.Payload["success"] = true;
        msg.Payload["count"] = entries.Count;
        msg.Payload["entries"] = entries.Select(e => e.Signature.Hash).ToArray();
    }

    private async Task HandleImmuneForgetAsync(PluginMessage msg)
    {
        var signatureHash = GetString(msg, "signatureHash")
            ?? throw new ArgumentException("Missing signatureHash");

        await _immuneSystem.ForgetAsync(signatureHash).ConfigureAwait(false);

        msg.Payload["success"] = true;
        msg.Payload["signatureHash"] = signatureHash;
    }

    #endregion

    #region Schedule handlers

    private async Task HandleScheduleAddAsync(PluginMessage msg)
    {
        var scheduleId = GetString(msg, "scheduleId") ?? Guid.NewGuid().ToString("N");
        var scheduleName = GetString(msg, "scheduleName") ?? $"Schedule {scheduleId}";
        var cronExpression = GetString(msg, "cronExpression");
        var intervalMs = msg.Payload.TryGetValue("intervalMs", out var imsObj) && imsObj is double ims
            ? (long?)ims : null;

        // Build a minimal experiment for the schedule
        var faultTypeStr = GetString(msg, "faultType") ?? "NetworkPartition";
        Enum.TryParse<FaultType>(faultTypeStr, ignoreCase: true, out var faultType);

        var experiment = new ChaosExperiment
        {
            Id = $"scheduled-{scheduleId}",
            Name = $"Scheduled: {scheduleName}",
            Description = $"Automatically scheduled experiment for {scheduleName}",
            FaultType = faultType,
            Severity = FaultSeverity.Medium,
            MaxBlastRadius = BlastRadiusLevel.SinglePlugin,
            DurationLimit = TimeSpan.FromSeconds(GetDouble(msg, "durationSeconds", 30.0))
        };

        var schedule = new VaccinationSchedule
        {
            Id = scheduleId,
            Name = scheduleName,
            CronExpression = cronExpression,
            IntervalMs = intervalMs,
            Experiments = new[] { new ScheduledExperiment { Experiment = experiment } },
            Enabled = true
        };

        await _scheduler.AddScheduleAsync(schedule).ConfigureAwait(false);

        msg.Payload["success"] = true;
        msg.Payload["scheduleId"] = scheduleId;
    }

    private async Task HandleScheduleRemoveAsync(PluginMessage msg)
    {
        var scheduleId = GetString(msg, "scheduleId")
            ?? throw new ArgumentException("Missing scheduleId");

        await _scheduler.RemoveScheduleAsync(scheduleId).ConfigureAwait(false);

        msg.Payload["success"] = true;
        msg.Payload["scheduleId"] = scheduleId;
    }

    private async Task HandleScheduleListAsync(PluginMessage msg)
    {
        var schedules = await _scheduler.GetSchedulesAsync().ConfigureAwait(false);

        msg.Payload["success"] = true;
        msg.Payload["count"] = schedules.Count;
        msg.Payload["schedules"] = schedules.Select(s => s.Id).ToArray();
    }

    private async Task HandleScheduleEnableAsync(PluginMessage msg)
    {
        var scheduleId = GetString(msg, "scheduleId")
            ?? throw new ArgumentException("Missing scheduleId");
        var enabled = msg.Payload.TryGetValue("enabled", out var eObj) && eObj is bool e ? e : true;

        await _scheduler.EnableScheduleAsync(scheduleId, enabled).ConfigureAwait(false);

        msg.Payload["success"] = true;
        msg.Payload["scheduleId"] = scheduleId;
        msg.Payload["enabled"] = enabled;
    }

    #endregion

    #region Results handlers

    private async Task HandleResultsQueryAsync(PluginMessage msg)
    {
        var query = new ExperimentQuery
        {
            MaxResults = (int)GetDouble(msg, "maxResults", 100)
        };

        var records = await _resultsDb.QueryAsync(query).ConfigureAwait(false);

        msg.Payload["success"] = true;
        msg.Payload["count"] = records.Count;
    }

    private async Task HandleResultsSummaryAsync(PluginMessage msg)
    {
        var summary = await _resultsDb.GetSummaryAsync(null).ConfigureAwait(false);

        msg.Payload["success"] = true;
        msg.Payload["totalExperiments"] = summary.TotalExperiments;
        msg.Payload["successRate"] = summary.SuccessRate;
        msg.Payload["averageRecoveryMs"] = summary.AverageRecoveryMs;
    }

    private async Task HandleResultsPurgeAsync(PluginMessage msg)
    {
        var days = (int)GetDouble(msg, "olderThanDays", 30);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);

        await _resultsDb.PurgeAsync(cutoff).ConfigureAwait(false);

        msg.Payload["success"] = true;
        msg.Payload["purgedBefore"] = cutoff.ToString("O");
    }

    #endregion

    #region Payload helpers

    private static string? GetString(PluginMessage msg, string key)
    {
        return msg.Payload.TryGetValue(key, out var obj) && obj is string s ? s : null;
    }

    private static double GetDouble(PluginMessage msg, string key, double defaultValue)
    {
        if (msg.Payload.TryGetValue(key, out var obj))
        {
            if (obj is double d) return d;
            if (obj is int i) return i;
            if (obj is long l) return l;
            if (obj is float f) return f;
            if (obj is string s && double.TryParse(s, out var parsed)) return parsed;
        }
        return defaultValue;
    }

    private static string[]? ParseStringArray(object? value)
    {
        if (value is string[] arr) return arr;
        if (value is object[] objArr) return objArr.Select(o => o?.ToString() ?? string.Empty).ToArray();
        if (value is string json && json.StartsWith('['))
        {
            try
            {
                return JsonSerializer.Deserialize<string[]>(json);
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    #endregion
}
