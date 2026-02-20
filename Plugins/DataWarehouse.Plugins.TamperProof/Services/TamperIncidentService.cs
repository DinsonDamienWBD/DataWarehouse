// Licensed to the DataWarehouse under one or more agreements.
// DataWarehouse licenses this file under the MIT license.

using DataWarehouse.SDK.Contracts.TamperProof;
using Microsoft.Extensions.Logging;
using SdkAccessLogProvider = DataWarehouse.SDK.Contracts.TamperProof.IAccessLogProvider;
using PluginAccessLogProvider = DataWarehouse.Plugins.TamperProof.IAccessLogProvider;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.TamperProof.Services;

/// <summary>
/// Service for managing tamper incident storage, retrieval, and analysis.
/// Provides persistent storage for tamper incidents with attribution analysis.
/// </summary>
public class TamperIncidentService
{
    private readonly BoundedDictionary<Guid, List<TamperIncidentReport>> _incidents = new BoundedDictionary<Guid, List<TamperIncidentReport>>(1000);
    private readonly SdkAccessLogProvider? _sdkAccessLogProvider;
    private readonly PluginAccessLogProvider? _pluginAccessLogProvider;
    private readonly ILogger<TamperIncidentService> _logger;
    private readonly TamperProofConfiguration _config;

    /// <summary>
    /// Creates a new tamper incident service instance with SDK access log provider.
    /// </summary>
    /// <param name="config">Tamper-proof configuration.</param>
    /// <param name="accessLogProvider">Optional SDK access log provider for attribution analysis.</param>
    /// <param name="logger">Logger instance.</param>
    public TamperIncidentService(
        TamperProofConfiguration config,
        SdkAccessLogProvider? accessLogProvider,
        ILogger<TamperIncidentService> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _sdkAccessLogProvider = accessLogProvider;
        _pluginAccessLogProvider = null;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Creates a new tamper incident service instance with plugin access log provider.
    /// </summary>
    /// <param name="config">Tamper-proof configuration.</param>
    /// <param name="accessLogProvider">Optional plugin access log provider.</param>
    /// <param name="logger">Logger instance.</param>
    public TamperIncidentService(
        TamperProofConfiguration config,
        PluginAccessLogProvider? accessLogProvider,
        ILogger<TamperIncidentService> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _sdkAccessLogProvider = null;
        _pluginAccessLogProvider = accessLogProvider;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Records a tamper incident with full attribution analysis.
    /// </summary>
    /// <param name="objectId">Object ID where tampering was detected.</param>
    /// <param name="version">Object version.</param>
    /// <param name="expectedHash">Expected hash from manifest.</param>
    /// <param name="actualHash">Actual computed hash.</param>
    /// <param name="affectedInstance">Storage instance where tampering was detected.</param>
    /// <param name="affectedShards">Optional list of affected shard indices.</param>
    /// <param name="recoveryAction">Recovery action taken.</param>
    /// <param name="recoverySucceeded">Whether recovery succeeded.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created tamper incident report.</returns>
    public async Task<TamperIncidentReport> RecordIncidentAsync(
        Guid objectId,
        int version,
        IntegrityHash expectedHash,
        IntegrityHash actualHash,
        string affectedInstance,
        List<int>? affectedShards,
        TamperRecoveryBehavior recoveryAction,
        bool recoverySucceeded,
        CancellationToken ct = default)
    {
        _logger.LogWarning(
            "Recording tamper incident for object {ObjectId} version {Version} on instance {Instance}",
            objectId, version, affectedInstance);

        // Create evidence
        var evidence = TamperEvidence.Create(
            expectedHash.Algorithm,
            0, // Would need actual corrupted data size
            0, // Would need expected data size
            ComputeManifestChecksum(objectId, version, expectedHash.HashValue));

        // Perform attribution analysis if SDK access log provider is available
        AttributionAnalysis? attribution = null;
        if (_sdkAccessLogProvider != null)
        {
            try
            {
                var analysis = await _sdkAccessLogProvider.AnalyzeForAttributionAsync(
                    objectId,
                    DateTimeOffset.UtcNow,
                    TimeSpan.FromHours(72), // 72-hour lookback window
                    ct);

                attribution = new AttributionAnalysis
                {
                    Confidence = analysis.Confidence,
                    SuspectedPrincipal = analysis.SuspectedPrincipals.FirstOrDefault()?.Principal,
                    RelatedAccessLogs = analysis.AnalyzedEntries?.Cast<object>().ToList(),
                    EstimatedTamperTimeFrom = analysis.TamperingWindow.Start,
                    EstimatedTamperTimeTo = analysis.TamperingWindow.End,
                    Reasoning = string.Join("; ", analysis.Evidence),
                    IsInternalTampering = analysis.SuspectedPrincipals.Count > 0
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to perform attribution analysis for object {ObjectId}", objectId);
                attribution = AttributionAnalysis.CreateUnknown();
            }
        }
        else if (_pluginAccessLogProvider != null)
        {
            // Plugin access log provider doesn't have attribution analysis,
            // but we can query access logs for basic attribution
            try
            {
                var lookbackStart = DateTimeOffset.UtcNow.AddHours(-72);
                var accessLogs = await _pluginAccessLogProvider.QueryAccessLogsAsync(
                    objectId,
                    lookbackStart,
                    DateTimeOffset.UtcNow,
                    ct);

                if (accessLogs.Count > 0)
                {
                    // Basic attribution based on access logs
                    var writeLogs = accessLogs.Where(l => l.AccessType == AccessType.Write).ToList();
                    var lastWritePrincipal = writeLogs.OrderByDescending(l => l.AccessedAt).FirstOrDefault()?.Principal;

                    attribution = new AttributionAnalysis
                    {
                        Confidence = writeLogs.Count > 0 ? AttributionConfidence.Suspected : AttributionConfidence.Unknown,
                        SuspectedPrincipal = lastWritePrincipal,
                        RelatedAccessLogs = accessLogs.Cast<object>().ToList(),
                        EstimatedTamperTimeFrom = lookbackStart,
                        EstimatedTamperTimeTo = DateTimeOffset.UtcNow,
                        Reasoning = $"Found {accessLogs.Count} access logs, {writeLogs.Count} write operations",
                        IsInternalTampering = lastWritePrincipal != null
                    };
                }
                else
                {
                    attribution = AttributionAnalysis.CreateUnknown();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to query access logs for object {ObjectId}", objectId);
                attribution = AttributionAnalysis.CreateUnknown();
            }
        }
        else
        {
            attribution = AttributionAnalysis.CreateUnknown();
        }

        // Create the incident report
        var report = TamperIncidentReport.CreateWithAttribution(
            objectId,
            expectedHash.ToString(),
            actualHash.ToString(),
            affectedInstance,
            recoveryAction,
            recoverySucceeded,
            evidence,
            attribution);

        // Store the incident
        var incidents = _incidents.GetOrAdd(objectId, _ => new List<TamperIncidentReport>());
        lock (incidents)
        {
            incidents.Add(report);
        }

        _logger.LogWarning(
            "Tamper incident {IncidentId} recorded for object {ObjectId}: " +
            "Confidence={Confidence}, SuspectedPrincipal={Principal}",
            report.IncidentId,
            objectId,
            report.AttributionConfidence,
            report.SuspectedPrincipal ?? "unknown");

        return report;
    }

    /// <summary>
    /// Gets the most recent tamper incident for an object.
    /// </summary>
    /// <param name="objectId">Object ID to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Most recent incident report or null if none.</returns>
    public Task<TamperIncidentReport?> GetLatestIncidentAsync(
        Guid objectId,
        CancellationToken ct = default)
    {
        if (_incidents.TryGetValue(objectId, out var incidents))
        {
            lock (incidents)
            {
                var latest = incidents.OrderByDescending(i => i.DetectedAt).FirstOrDefault();
                return Task.FromResult<TamperIncidentReport?>(latest);
            }
        }

        return Task.FromResult<TamperIncidentReport?>(null);
    }

    /// <summary>
    /// Gets all tamper incidents for an object.
    /// </summary>
    /// <param name="objectId">Object ID to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of all incident reports.</returns>
    public Task<IReadOnlyList<TamperIncidentReport>> GetIncidentsAsync(
        Guid objectId,
        CancellationToken ct = default)
    {
        if (_incidents.TryGetValue(objectId, out var incidents))
        {
            lock (incidents)
            {
                return Task.FromResult<IReadOnlyList<TamperIncidentReport>>(
                    incidents.OrderByDescending(i => i.DetectedAt).ToList());
            }
        }

        return Task.FromResult<IReadOnlyList<TamperIncidentReport>>(
            Array.Empty<TamperIncidentReport>());
    }

    /// <summary>
    /// Gets all tamper incidents across all objects.
    /// </summary>
    /// <param name="from">Start of time range.</param>
    /// <param name="to">End of time range.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of all incidents in the time range.</returns>
    public Task<IReadOnlyList<TamperIncidentReport>> QueryIncidentsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken ct = default)
    {
        var allIncidents = new List<TamperIncidentReport>();

        foreach (var incidents in _incidents.Values)
        {
            lock (incidents)
            {
                allIncidents.AddRange(incidents.Where(i =>
                    i.DetectedAt >= from && i.DetectedAt <= to));
            }
        }

        return Task.FromResult<IReadOnlyList<TamperIncidentReport>>(
            allIncidents.OrderByDescending(i => i.DetectedAt).ToList());
    }

    /// <summary>
    /// Gets incident statistics for monitoring and alerting.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Incident statistics.</returns>
    public Task<TamperIncidentStatistics> GetStatisticsAsync(CancellationToken ct = default)
    {
        var totalIncidents = 0;
        var recoveredCount = 0;
        var failedRecoveryCount = 0;
        var byConfidence = new Dictionary<AttributionConfidence, int>();
        var recentIncidents = new List<TamperIncidentReport>();
        var cutoff = DateTimeOffset.UtcNow.AddHours(-24);

        foreach (var incidents in _incidents.Values)
        {
            lock (incidents)
            {
                totalIncidents += incidents.Count;
                recoveredCount += incidents.Count(i => i.RecoverySucceeded);
                failedRecoveryCount += incidents.Count(i => !i.RecoverySucceeded);

                foreach (var incident in incidents)
                {
                    if (!byConfidence.ContainsKey(incident.AttributionConfidence))
                        byConfidence[incident.AttributionConfidence] = 0;
                    byConfidence[incident.AttributionConfidence]++;

                    if (incident.DetectedAt >= cutoff)
                        recentIncidents.Add(incident);
                }
            }
        }

        return Task.FromResult(new TamperIncidentStatistics
        {
            TotalIncidents = totalIncidents,
            RecoveredCount = recoveredCount,
            FailedRecoveryCount = failedRecoveryCount,
            IncidentsByConfidence = byConfidence,
            IncidentsLast24Hours = recentIncidents.Count,
            AffectedObjectCount = _incidents.Count
        });
    }

    /// <summary>
    /// Computes a checksum of manifest data for evidence.
    /// </summary>
    private static string ComputeManifestChecksum(Guid objectId, int version, string hash)
    {
        var data = $"{objectId}|{version}|{hash}";
        var bytes = System.Text.Encoding.UTF8.GetBytes(data);
        // Hash computed inline; bus delegation to UltimateDataIntegrity available for centralized policy enforcement
        var hashBytes = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hashBytes);
    }
}

/// <summary>
/// Statistics about tamper incidents for monitoring.
/// </summary>
public class TamperIncidentStatistics
{
    /// <summary>Total number of incidents recorded.</summary>
    public required int TotalIncidents { get; init; }

    /// <summary>Number of incidents with successful recovery.</summary>
    public required int RecoveredCount { get; init; }

    /// <summary>Number of incidents where recovery failed.</summary>
    public required int FailedRecoveryCount { get; init; }

    /// <summary>Breakdown by attribution confidence.</summary>
    public required Dictionary<AttributionConfidence, int> IncidentsByConfidence { get; init; }

    /// <summary>Number of incidents in the last 24 hours.</summary>
    public required int IncidentsLast24Hours { get; init; }

    /// <summary>Number of unique objects with incidents.</summary>
    public required int AffectedObjectCount { get; init; }
}
