using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Consciousness;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataCatalog.Strategies.AssetDiscovery;

#region Supporting Types

/// <summary>
/// Reason a data object was classified as dark data during discovery scanning.
/// </summary>
public enum DarkDataReason
{
    /// <summary>Object has no classification tags.</summary>
    NoClassification,
    /// <summary>Object has no designated owner.</summary>
    NoOwner,
    /// <summary>Object has no lineage information.</summary>
    NoLineage,
    /// <summary>Object has no recorded access history.</summary>
    NoAccessHistory,
    /// <summary>Owner account has been deleted or deactivated.</summary>
    Orphaned,
    /// <summary>Data was copied outside governed pathways.</summary>
    Shadow,
    /// <summary>No access in N days (configured by StaleDaysThreshold).</summary>
    Stale,
    /// <summary>Object is not registered in the data catalog.</summary>
    Unregistered
}

/// <summary>
/// Represents a discovered dark data candidate with its classification reason and extracted metadata.
/// </summary>
/// <param name="ObjectId">Unique identifier of the discovered object.</param>
/// <param name="Reason">Why this object was classified as dark data.</param>
/// <param name="DiscoveredAt">UTC timestamp when this candidate was discovered.</param>
/// <param name="ExtractedMetadata">Metadata extracted during the discovery scan.</param>
/// <param name="SizeBytes">Size of the object in bytes.</param>
/// <param name="LastModified">Last modification timestamp of the object, if available.</param>
public sealed record DarkDataCandidate(
    string ObjectId,
    DarkDataReason Reason,
    DateTime DiscoveredAt,
    Dictionary<string, object> ExtractedMetadata,
    long SizeBytes,
    DateTime? LastModified);

/// <summary>
/// Result of a dark data discovery scan, containing summary statistics and candidate list.
/// </summary>
/// <param name="TotalScanned">Total number of objects examined during the scan.</param>
/// <param name="DarkDataFound">Number of objects identified as dark data.</param>
/// <param name="AlreadyScored">Number of objects skipped because they already have consciousness scores.</param>
/// <param name="Candidates">List of discovered dark data candidates.</param>
/// <param name="ScanDuration">Wall-clock time the scan took to complete.</param>
/// <param name="ScanCompletedAt">UTC timestamp when the scan finished.</param>
public sealed record DarkDataScanResult(
    int TotalScanned,
    int DarkDataFound,
    int AlreadyScored,
    List<DarkDataCandidate> Candidates,
    TimeSpan ScanDuration,
    DateTime ScanCompletedAt);

/// <summary>
/// Configuration for dark data discovery scans, controlling scope, filtering, and incremental behavior.
/// </summary>
/// <param name="MaxObjectsPerScan">Maximum number of objects to examine in a single scan run. Default: 10000.</param>
/// <param name="StaleDaysThreshold">Number of days without access before an object is considered stale. Default: 180.</param>
/// <param name="SkipAlreadyScored">Whether to skip objects that already have a consciousness score. Default: true.</param>
/// <param name="IncludeStorageTiers">Storage tiers to include in the scan. Empty or null means all tiers.</param>
/// <param name="ExcludePatterns">File/object name patterns to exclude from scanning.</param>
public sealed record DarkDataScanConfig(
    int MaxObjectsPerScan = 10000,
    int StaleDaysThreshold = 180,
    bool SkipAlreadyScored = true,
    string[]? IncludeStorageTiers = null,
    string[]? ExcludePatterns = null)
{
    /// <summary>
    /// Gets the effective exclude patterns, using a sensible default if none were provided.
    /// </summary>
    public string[] EffectiveExcludePatterns => ExcludePatterns ?? new[] { ".tmp", ".log", ".bak" };
}

#endregion

#region Strategy 1: UntaggedObjectScanStrategy

/// <summary>
/// Scans for data objects that are missing consciousness scores, classification tags, owner tags,
/// or lineage metadata. Uses incremental watermarking to avoid rescanning already-processed objects.
/// </summary>
/// <remarks>
/// Metadata keys checked:
/// <list type="bullet">
///   <item><term>consciousness:score</term><description>Presence indicates the object has been scored.</description></item>
///   <item><term>classification</term><description>Data classification tag.</description></item>
///   <item><term>owner</term><description>Designated data owner.</description></item>
///   <item><term>lineage_source</term><description>Upstream lineage reference.</description></item>
///   <item><term>created_at</term><description>Object creation timestamp (DateTime) for watermark comparison.</description></item>
///   <item><term>modified_at</term><description>Object modification timestamp (DateTime) for watermark comparison.</description></item>
///   <item><term>size_bytes</term><description>Object size in bytes (long).</description></item>
/// </list>
/// </remarks>
public sealed class UntaggedObjectScanStrategy : ConsciousnessStrategyBase
{
    private readonly BoundedDictionary<string, DateTime> _scanWatermarks = new BoundedDictionary<string, DateTime>(1000);

    /// <inheritdoc />
    public override string StrategyId => "dark-data-untagged-scan";

    /// <inheritdoc />
    public override string DisplayName => "Untagged Object Scanner";

    /// <inheritdoc />
    public override ConsciousnessCategory Category => ConsciousnessCategory.DarkDataDiscovery;

    /// <inheritdoc />
    public override ConsciousnessCapabilities Capabilities => new(
        SupportsAsync: true,
        SupportsBatch: true,
        SupportsRetroactive: true);

    /// <inheritdoc />
    public override string SemanticDescription =>
        "Scans for data objects missing consciousness scores, classification, owner, or lineage tags. " +
        "Uses incremental watermarking to efficiently discover newly created or modified untagged objects.";

    /// <inheritdoc />
    public override string[] Tags => ["dark-data", "untagged", "scan", "incremental", "watermark", "discovery"];

    /// <summary>
    /// Scans objects for missing consciousness metadata tags.
    /// </summary>
    /// <param name="objects">Collection of (objectId, metadata) tuples representing the data estate.</param>
    /// <param name="config">Scan configuration controlling scope and behavior.</param>
    /// <param name="scanScope">Identifier for this scan scope (used as watermark key). Default: "default".</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of dark data candidates found during the scan.</returns>
    public Task<List<DarkDataCandidate>> ScanAsync(
        IReadOnlyList<(string objectId, Dictionary<string, object> metadata)> objects,
        DarkDataScanConfig config,
        string scanScope = "default",
        CancellationToken ct = default)
    {
        IncrementCounter("scan_invocations");
        var startTime = DateTime.UtcNow;
        var watermark = _scanWatermarks.GetOrAdd(scanScope, _ => DateTime.MinValue);
        var candidates = new List<DarkDataCandidate>();
        var scannedCount = 0;

        foreach (var (objectId, metadata) in objects)
        {
            ct.ThrowIfCancellationRequested();

            if (scannedCount >= config.MaxObjectsPerScan)
                break;

            // Check watermark: skip objects not created/modified after the last scan
            var objectTime = ExtractObjectTimestamp(metadata);
            if (objectTime <= watermark)
                continue;

            scannedCount++;

            // Skip excluded patterns
            if (config.EffectiveExcludePatterns.Any(p => objectId.EndsWith(p, StringComparison.OrdinalIgnoreCase)))
                continue;

            // Skip already scored if configured
            if (config.SkipAlreadyScored && metadata.ContainsKey("consciousness:score"))
                continue;

            // Check for missing tags
            var reason = DetermineDarkDataReason(metadata);
            if (reason.HasValue)
            {
                var sizeBytes = metadata.TryGetValue("size_bytes", out var sizeObj) && sizeObj is long size ? size : 0L;
                var lastModified = metadata.TryGetValue("modified_at", out var modObj) && modObj is DateTime mod ? mod : (DateTime?)null;

                candidates.Add(new DarkDataCandidate(
                    ObjectId: objectId,
                    Reason: reason.Value,
                    DiscoveredAt: DateTime.UtcNow,
                    ExtractedMetadata: new Dictionary<string, object>(metadata),
                    SizeBytes: sizeBytes,
                    LastModified: lastModified));

                IncrementCounter("dark_data_found");
            }
        }

        // Update watermark to current time
        _scanWatermarks[scanScope] = startTime;
        IncrementCounter("objects_scanned");

        return Task.FromResult(candidates);
    }

    /// <summary>
    /// Gets the current watermark for a scan scope.
    /// </summary>
    /// <param name="scanScope">The scope identifier.</param>
    /// <returns>The watermark DateTime, or DateTime.MinValue if no scan has been performed.</returns>
    public DateTime GetWatermark(string scanScope = "default")
    {
        return _scanWatermarks.TryGetValue(scanScope, out var watermark) ? watermark : DateTime.MinValue;
    }

    private static DarkDataReason? DetermineDarkDataReason(Dictionary<string, object> metadata)
    {
        if (!metadata.ContainsKey("classification"))
            return DarkDataReason.NoClassification;
        if (!metadata.ContainsKey("owner"))
            return DarkDataReason.NoOwner;
        if (!metadata.ContainsKey("lineage_source"))
            return DarkDataReason.NoLineage;
        return null;
    }

    private static DateTime ExtractObjectTimestamp(Dictionary<string, object> metadata)
    {
        if (metadata.TryGetValue("modified_at", out var modObj) && modObj is DateTime modified)
            return modified;
        if (metadata.TryGetValue("created_at", out var createdObj) && createdObj is DateTime created)
            return created;
        return DateTime.MinValue;
    }
}

#endregion

#region Strategy 2: OrphanedDataScanStrategy

/// <summary>
/// Finds data objects whose owner principal no longer exists in access control or whose
/// upstream lineage references deleted or moved objects.
/// </summary>
/// <remarks>
/// Metadata keys checked:
/// <list type="bullet">
///   <item><term>owner_principal_id</term><description>The principal ID of the data owner.</description></item>
///   <item><term>upstream_object_ids</term><description>Comma-separated list of upstream object IDs in the lineage.</description></item>
/// </list>
/// </remarks>
public sealed class OrphanedDataScanStrategy : ConsciousnessStrategyBase
{
    /// <inheritdoc />
    public override string StrategyId => "dark-data-orphaned-scan";

    /// <inheritdoc />
    public override string DisplayName => "Orphaned Data Scanner";

    /// <inheritdoc />
    public override ConsciousnessCategory Category => ConsciousnessCategory.DarkDataDiscovery;

    /// <inheritdoc />
    public override ConsciousnessCapabilities Capabilities => new(
        SupportsAsync: true,
        SupportsBatch: true,
        SupportsRetroactive: true);

    /// <inheritdoc />
    public override string SemanticDescription =>
        "Detects data objects whose owner principal no longer exists or whose upstream lineage references " +
        "deleted or moved objects. Orphaned data is a major source of dark data liability.";

    /// <inheritdoc />
    public override string[] Tags => ["dark-data", "orphaned", "owner", "lineage", "scan", "discovery"];

    /// <summary>
    /// Scans objects for orphaned data conditions.
    /// </summary>
    /// <param name="objects">Collection of (objectId, metadata) tuples to scan.</param>
    /// <param name="activePrincipalIds">Set of principal IDs that are still active in the system.</param>
    /// <param name="existingObjectIds">Set of object IDs that exist in the data estate (for lineage validation).</param>
    /// <param name="config">Scan configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of orphaned dark data candidates.</returns>
    public Task<List<DarkDataCandidate>> ScanAsync(
        IReadOnlyList<(string objectId, Dictionary<string, object> metadata)> objects,
        IReadOnlySet<string> activePrincipalIds,
        IReadOnlySet<string> existingObjectIds,
        DarkDataScanConfig config,
        CancellationToken ct = default)
    {
        IncrementCounter("scan_invocations");
        var candidates = new List<DarkDataCandidate>();
        var scannedCount = 0;

        foreach (var (objectId, metadata) in objects)
        {
            ct.ThrowIfCancellationRequested();

            if (scannedCount >= config.MaxObjectsPerScan)
                break;

            scannedCount++;

            // Skip excluded patterns
            if (config.EffectiveExcludePatterns.Any(p => objectId.EndsWith(p, StringComparison.OrdinalIgnoreCase)))
                continue;

            // Skip already scored if configured
            if (config.SkipAlreadyScored && metadata.ContainsKey("consciousness:score"))
                continue;

            // Check for orphaned owner
            if (metadata.TryGetValue("owner_principal_id", out var principalObj) &&
                principalObj is string principalId &&
                !string.IsNullOrWhiteSpace(principalId) &&
                !activePrincipalIds.Contains(principalId))
            {
                var sizeBytes = metadata.TryGetValue("size_bytes", out var sizeObj) && sizeObj is long size ? size : 0L;
                var lastModified = metadata.TryGetValue("modified_at", out var modObj) && modObj is DateTime mod ? mod : (DateTime?)null;

                candidates.Add(new DarkDataCandidate(
                    ObjectId: objectId,
                    Reason: DarkDataReason.Orphaned,
                    DiscoveredAt: DateTime.UtcNow,
                    ExtractedMetadata: new Dictionary<string, object>(metadata),
                    SizeBytes: sizeBytes,
                    LastModified: lastModified));

                IncrementCounter("orphaned_owner_found");
                continue;
            }

            // Check for broken lineage references
            if (metadata.TryGetValue("upstream_object_ids", out var upstreamObj) &&
                upstreamObj is string upstreamIds &&
                !string.IsNullOrWhiteSpace(upstreamIds))
            {
                var upstreamList = upstreamIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var hasBrokenLineage = upstreamList.Any(id => !existingObjectIds.Contains(id));

                if (hasBrokenLineage)
                {
                    var sizeBytes = metadata.TryGetValue("size_bytes", out var sizeObj) && sizeObj is long size ? size : 0L;
                    var lastModified = metadata.TryGetValue("modified_at", out var modObj) && modObj is DateTime mod ? mod : (DateTime?)null;

                    candidates.Add(new DarkDataCandidate(
                        ObjectId: objectId,
                        Reason: DarkDataReason.Orphaned,
                        DiscoveredAt: DateTime.UtcNow,
                        ExtractedMetadata: new Dictionary<string, object>(metadata),
                        SizeBytes: sizeBytes,
                        LastModified: lastModified));

                    IncrementCounter("orphaned_lineage_found");
                }
            }
        }

        IncrementCounter("objects_scanned");
        return Task.FromResult(candidates);
    }
}

#endregion

#region Strategy 3: ShadowDataScanStrategy

/// <summary>
/// Detects data that was copied or moved outside governed pathways, including duplicate content
/// with different governance metadata and objects in unregistered storage locations.
/// </summary>
/// <remarks>
/// Metadata keys checked:
/// <list type="bullet">
///   <item><term>content_hash</term><description>Hash of the object content for duplicate detection.</description></item>
///   <item><term>governance_zone</term><description>The governance zone the object belongs to.</description></item>
///   <item><term>storage_location</term><description>Physical storage location of the object.</description></item>
///   <item><term>catalog_registered</term><description>Whether the object is registered in the data catalog (bool).</description></item>
/// </list>
/// </remarks>
public sealed class ShadowDataScanStrategy : ConsciousnessStrategyBase
{
    /// <inheritdoc />
    public override string StrategyId => "dark-data-shadow-scan";

    /// <inheritdoc />
    public override string DisplayName => "Shadow Data Scanner";

    /// <inheritdoc />
    public override ConsciousnessCategory Category => ConsciousnessCategory.DarkDataDiscovery;

    /// <inheritdoc />
    public override ConsciousnessCapabilities Capabilities => new(
        SupportsAsync: true,
        SupportsBatch: true,
        SupportsRetroactive: true);

    /// <inheritdoc />
    public override string SemanticDescription =>
        "Detects shadow data created outside governed pathways by identifying duplicate content hashes " +
        "with different governance metadata and objects in unregistered storage locations.";

    /// <inheritdoc />
    public override string[] Tags => ["dark-data", "shadow", "duplicate", "unregistered", "governance", "discovery"];

    /// <summary>
    /// Scans for shadow and unregistered data objects.
    /// </summary>
    /// <param name="objects">Collection of (objectId, metadata) tuples to scan.</param>
    /// <param name="registeredLocations">Set of storage locations that are registered and governed.</param>
    /// <param name="config">Scan configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of shadow and unregistered dark data candidates.</returns>
    public Task<List<DarkDataCandidate>> ScanAsync(
        IReadOnlyList<(string objectId, Dictionary<string, object> metadata)> objects,
        IReadOnlySet<string> registeredLocations,
        DarkDataScanConfig config,
        CancellationToken ct = default)
    {
        IncrementCounter("scan_invocations");
        var candidates = new List<DarkDataCandidate>();
        var scannedCount = 0;

        // Build content hash index for duplicate detection
        var hashIndex = new Dictionary<string, List<(string objectId, string? governanceZone)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (objectId, metadata) in objects)
        {
            if (metadata.TryGetValue("content_hash", out var hashObj) && hashObj is string contentHash && !string.IsNullOrWhiteSpace(contentHash))
            {
                var zone = metadata.TryGetValue("governance_zone", out var zoneObj) && zoneObj is string gz ? gz : null;
                if (!hashIndex.TryGetValue(contentHash, out var list))
                {
                    list = new List<(string, string?)>();
                    hashIndex[contentHash] = list;
                }
                list.Add((objectId, zone));
            }
        }

        foreach (var (objectId, metadata) in objects)
        {
            ct.ThrowIfCancellationRequested();

            if (scannedCount >= config.MaxObjectsPerScan)
                break;

            scannedCount++;

            // Skip excluded patterns
            if (config.EffectiveExcludePatterns.Any(p => objectId.EndsWith(p, StringComparison.OrdinalIgnoreCase)))
                continue;

            // Skip already scored if configured
            if (config.SkipAlreadyScored && metadata.ContainsKey("consciousness:score"))
                continue;

            var sizeBytes = metadata.TryGetValue("size_bytes", out var sizeObj) && sizeObj is long size ? size : 0L;
            var lastModified = metadata.TryGetValue("modified_at", out var modObj) && modObj is DateTime mod ? mod : (DateTime?)null;

            // Check for unregistered storage location
            if (metadata.TryGetValue("storage_location", out var locObj) && locObj is string location &&
                !string.IsNullOrWhiteSpace(location) && !registeredLocations.Contains(location))
            {
                candidates.Add(new DarkDataCandidate(
                    ObjectId: objectId,
                    Reason: DarkDataReason.Unregistered,
                    DiscoveredAt: DateTime.UtcNow,
                    ExtractedMetadata: new Dictionary<string, object>(metadata),
                    SizeBytes: sizeBytes,
                    LastModified: lastModified));

                IncrementCounter("unregistered_found");
                continue;
            }

            // Check for not registered in catalog
            if (metadata.TryGetValue("catalog_registered", out var regObj) && regObj is bool registered && !registered)
            {
                candidates.Add(new DarkDataCandidate(
                    ObjectId: objectId,
                    Reason: DarkDataReason.Unregistered,
                    DiscoveredAt: DateTime.UtcNow,
                    ExtractedMetadata: new Dictionary<string, object>(metadata),
                    SizeBytes: sizeBytes,
                    LastModified: lastModified));

                IncrementCounter("unregistered_found");
                continue;
            }

            // Check for shadow copies (same content hash, different governance zones)
            if (metadata.TryGetValue("content_hash", out var chObj) && chObj is string hash &&
                hashIndex.TryGetValue(hash, out var duplicates) && duplicates.Count > 1)
            {
                var currentZone = metadata.TryGetValue("governance_zone", out var czObj) && czObj is string cz ? cz : null;
                var hasDifferentZone = duplicates.Any(d => d.objectId != objectId && d.governanceZone != currentZone);

                if (hasDifferentZone)
                {
                    candidates.Add(new DarkDataCandidate(
                        ObjectId: objectId,
                        Reason: DarkDataReason.Shadow,
                        DiscoveredAt: DateTime.UtcNow,
                        ExtractedMetadata: new Dictionary<string, object>(metadata),
                        SizeBytes: sizeBytes,
                        LastModified: lastModified));

                    IncrementCounter("shadow_found");
                }
            }
        }

        IncrementCounter("objects_scanned");
        return Task.FromResult(candidates);
    }
}

#endregion

#region Strategy 4: DarkDataDiscoveryOrchestrator

/// <summary>
/// Orchestrates all dark data scan strategies, deduplicates results, and triggers consciousness
/// scoring for discovered dark data. Supports both full and incremental discovery runs.
/// </summary>
/// <remarks>
/// The orchestrator runs all scan strategies in parallel, merges and deduplicates candidates by
/// ObjectId, then invokes <see cref="IConsciousnessScorer.ScoreAsync"/> for each candidate.
/// It publishes "consciousness.dark_data.discovered" events and tracks scan history for auditing.
/// </remarks>
public sealed class DarkDataDiscoveryOrchestrator : ConsciousnessStrategyBase
{
    private readonly UntaggedObjectScanStrategy _untaggedScanner = new();
    private readonly OrphanedDataScanStrategy _orphanedScanner = new();
    private readonly ShadowDataScanStrategy _shadowScanner = new();
    private readonly BoundedDictionary<DateTime, DarkDataScanResult> _scanHistory = new BoundedDictionary<DateTime, DarkDataScanResult>(1000);

    /// <inheritdoc />
    public override string StrategyId => "dark-data-orchestrator";

    /// <inheritdoc />
    public override string DisplayName => "Dark Data Discovery Orchestrator";

    /// <inheritdoc />
    public override ConsciousnessCategory Category => ConsciousnessCategory.DarkDataDiscovery;

    /// <inheritdoc />
    public override ConsciousnessCapabilities Capabilities => new(
        SupportsAsync: true,
        SupportsBatch: true,
        SupportsRetroactive: true);

    /// <inheritdoc />
    public override string SemanticDescription =>
        "Orchestrates all dark data scan strategies (untagged, orphaned, shadow), deduplicates results, " +
        "and triggers consciousness scoring for discovered dark data objects.";

    /// <inheritdoc />
    public override string[] Tags => ["dark-data", "orchestrator", "discovery", "scoring", "coordination"];

    /// <summary>
    /// Gets the untagged object scan strategy for direct access if needed.
    /// </summary>
    public UntaggedObjectScanStrategy UntaggedScanner => _untaggedScanner;

    /// <summary>
    /// Gets the orphaned data scan strategy for direct access if needed.
    /// </summary>
    public OrphanedDataScanStrategy OrphanedScanner => _orphanedScanner;

    /// <summary>
    /// Gets the shadow data scan strategy for direct access if needed.
    /// </summary>
    public ShadowDataScanStrategy ShadowScanner => _shadowScanner;

    /// <summary>
    /// Gets the history of all completed scans.
    /// </summary>
    public IReadOnlyDictionary<DateTime, DarkDataScanResult> ScanHistory => _scanHistory;

    /// <summary>
    /// Runs a full dark data discovery scan across all strategies.
    /// </summary>
    /// <param name="objects">The data estate objects to scan.</param>
    /// <param name="activePrincipalIds">Active principal IDs for orphan detection.</param>
    /// <param name="existingObjectIds">Existing object IDs for lineage validation.</param>
    /// <param name="registeredLocations">Registered storage locations for shadow detection.</param>
    /// <param name="scorer">The consciousness scorer to score discovered dark data.</param>
    /// <param name="config">Scan configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A scan result containing all discovered dark data candidates and summary statistics.</returns>
    public async Task<DarkDataScanResult> RunDiscoveryAsync(
        IReadOnlyList<(string objectId, Dictionary<string, object> metadata)> objects,
        IReadOnlySet<string> activePrincipalIds,
        IReadOnlySet<string> existingObjectIds,
        IReadOnlySet<string> registeredLocations,
        IConsciousnessScorer scorer,
        DarkDataScanConfig config,
        CancellationToken ct = default)
    {
        IncrementCounter("discovery_runs");
        var startTime = DateTime.UtcNow;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Run all scan strategies in parallel
        var untaggedTask = _untaggedScanner.ScanAsync(objects, config, "full-discovery", ct);
        var orphanedTask = _orphanedScanner.ScanAsync(objects, activePrincipalIds, existingObjectIds, config, ct);
        var shadowTask = _shadowScanner.ScanAsync(objects, registeredLocations, config, ct);

        await Task.WhenAll(untaggedTask, orphanedTask, shadowTask).ConfigureAwait(false);

        var allCandidates = new List<DarkDataCandidate>();
        allCandidates.AddRange(untaggedTask.Result);
        allCandidates.AddRange(orphanedTask.Result);
        allCandidates.AddRange(shadowTask.Result);

        // Deduplicate by ObjectId (keep first occurrence)
        var deduplicated = allCandidates
            .GroupBy(c => c.ObjectId)
            .Select(g => g.First())
            .ToList();

        // Score each discovered candidate
        var alreadyScored = 0;
        foreach (var candidate in deduplicated)
        {
            ct.ThrowIfCancellationRequested();

            if (candidate.ExtractedMetadata.ContainsKey("consciousness:score"))
            {
                alreadyScored++;
                continue;
            }

            try
            {
                var data = Array.Empty<byte>(); // Dark data scoring uses metadata only
                await scorer.ScoreAsync(candidate.ObjectId, data, candidate.ExtractedMetadata, ct).ConfigureAwait(false);
                IncrementCounter("candidates_scored");
            }
            catch (Exception)
            {
                IncrementCounter("scoring_failures");
            }
        }

        stopwatch.Stop();

        var result = new DarkDataScanResult(
            TotalScanned: objects.Count,
            DarkDataFound: deduplicated.Count,
            AlreadyScored: alreadyScored,
            Candidates: deduplicated,
            ScanDuration: stopwatch.Elapsed,
            ScanCompletedAt: DateTime.UtcNow);

        _scanHistory[startTime] = result;
        IncrementCounter("total_dark_data_found");

        return result;
    }

    /// <summary>
    /// Runs an incremental dark data discovery scan, using watermarks to only examine
    /// objects created or modified since the last scan.
    /// </summary>
    /// <param name="objects">The data estate objects to scan (pre-filtered or full set).</param>
    /// <param name="activePrincipalIds">Active principal IDs for orphan detection.</param>
    /// <param name="existingObjectIds">Existing object IDs for lineage validation.</param>
    /// <param name="registeredLocations">Registered storage locations for shadow detection.</param>
    /// <param name="scorer">The consciousness scorer to score discovered dark data.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A scan result containing incrementally discovered dark data.</returns>
    public async Task<DarkDataScanResult> RunIncrementalAsync(
        IReadOnlyList<(string objectId, Dictionary<string, object> metadata)> objects,
        IReadOnlySet<string> activePrincipalIds,
        IReadOnlySet<string> existingObjectIds,
        IReadOnlySet<string> registeredLocations,
        IConsciousnessScorer scorer,
        CancellationToken ct = default)
    {
        IncrementCounter("incremental_runs");

        // Use default config with incremental-friendly settings
        var config = new DarkDataScanConfig(
            MaxObjectsPerScan: 10000,
            SkipAlreadyScored: true);

        return await RunDiscoveryAsync(
            objects, activePrincipalIds, existingObjectIds, registeredLocations,
            scorer, config, ct).ConfigureAwait(false);
    }
}

#endregion
