using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Compliance;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.SovereigntyMesh;

/// <summary>
/// Pre-routing sovereignty check for FederatedObjectStorage integration.
/// <para>
/// Sits between the kernel's routing layer and storage backends. Before data is routed
/// to any storage backend, this strategy checks sovereignty constraints by extracting
/// passport data from object tags, mapping storage backends to jurisdictions, and calling
/// the sovereignty mesh orchestrator. Denied destinations are blocked with compliant
/// alternatives suggested. Decisions are cached with a configurable TTL.
/// </para>
/// </summary>
public sealed class SovereigntyRoutingStrategy : ComplianceStrategyBase
{
    // ==================================================================================
    // Nested types
    // ==================================================================================

    /// <summary>
    /// Result of a sovereignty routing evaluation.
    /// </summary>
    public sealed record RoutingDecision
    {
        /// <summary>Whether the routing to the intended destination is allowed.</summary>
        public required bool Allowed { get; init; }

        /// <summary>If the original location is denied, a compliant alternative backend.</summary>
        public string? RecommendedLocation { get; init; }

        /// <summary>Locations that sovereignty denies for this object.</summary>
        public IReadOnlyList<string>? BlockedLocations { get; init; }

        /// <summary>Locations that sovereignty allows for this object.</summary>
        public IReadOnlyList<string>? AllowedLocations { get; init; }

        /// <summary>Human-readable reason for the decision.</summary>
        public string? Reason { get; init; }

        /// <summary>The underlying zone enforcement result, if sovereignty was evaluated.</summary>
        public ZoneEnforcementResult? EnforcementResult { get; init; }
    }

    // ==================================================================================
    // Cache entry
    // ==================================================================================

    private sealed class CacheEntry
    {
        public required RoutingDecision Decision { get; init; }
        public required long CreatedAtTicks { get; init; }
    }

    // ==================================================================================
    // State
    // ==================================================================================

    private readonly BoundedDictionary<string, CacheEntry> _routingCache = new BoundedDictionary<string, CacheEntry>(1000);
    private readonly BoundedDictionary<string, string> _backendJurisdictionMap = new BoundedDictionary<string, string>(1000);

    private ISovereigntyMesh? _sovereigntyMesh;
    private string _defaultJurisdiction = "US";
    private TimeSpan _cacheTtl = TimeSpan.FromMinutes(10);

    // Statistics
    private long _routingChecksTotal;
    private long _routingAllowed;
    private long _routingDenied;
    private long _routingRedirected;
    private long _cacheHits;

    // ==================================================================================
    // Strategy identity
    // ==================================================================================

    /// <inheritdoc/>
    public override string StrategyId => "sovereignty-routing";

    /// <inheritdoc/>
    public override string StrategyName => "Sovereignty-Aware Routing";

    /// <inheritdoc/>
    public override string Framework => "SovereigntyMesh";

    // ==================================================================================
    // Initialization
    // ==================================================================================

    /// <inheritdoc/>
    public override Task InitializeAsync(
        Dictionary<string, object> configuration,
        CancellationToken cancellationToken = default)
    {
        base.InitializeAsync(configuration, cancellationToken);

        if (Configuration.TryGetValue("DefaultJurisdiction", out var djObj) && djObj is string dj && dj.Length > 0)
        {
            _defaultJurisdiction = dj;
        }

        if (Configuration.TryGetValue("RoutingCacheTtlMinutes", out var ttlObj) &&
            double.TryParse(ttlObj.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ttlMin) &&
            ttlMin > 0)
        {
            _cacheTtl = TimeSpan.FromMinutes(ttlMin);
        }

        // Load custom storage-to-jurisdiction mappings from configuration
        if (Configuration.TryGetValue("StorageJurisdictionMap", out var mapObj) &&
            mapObj is IDictionary<string, object> customMap)
        {
            foreach (var kvp in customMap)
            {
                _backendJurisdictionMap[kvp.Key] = kvp.Value?.ToString() ?? _defaultJurisdiction;
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Injects the sovereignty mesh instance used for sovereignty checks.
    /// Typically the <see cref="SovereigntyMeshOrchestratorStrategy"/>.
    /// </summary>
    /// <param name="mesh">The sovereignty mesh implementation.</param>
    public void SetSovereigntyMesh(ISovereigntyMesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        _sovereigntyMesh = mesh;
    }

    // ==================================================================================
    // Core routing check
    // ==================================================================================

    /// <summary>
    /// Checks whether routing an object to the intended destination is allowed by sovereignty rules.
    /// </summary>
    /// <param name="objectId">Identifier of the data object.</param>
    /// <param name="intendedDestination">The target storage backend identifier.</param>
    /// <param name="objectTags">Object tags (may contain passport tags from PassportTagIntegrationStrategy).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="RoutingDecision"/> with the sovereignty evaluation result.</returns>
    public async Task<RoutingDecision> CheckRoutingAsync(
        string objectId,
        string intendedDestination,
        IReadOnlyDictionary<string, object> objectTags,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        ArgumentNullException.ThrowIfNull(intendedDestination);
        objectTags ??= new Dictionary<string, object>();

        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _routingChecksTotal);
            IncrementCounter("sovereignty_routing.check");

        // 1. Check cache
        var cacheKey = BuildCacheKey(objectId, intendedDestination);
        if (TryGetCachedDecision(cacheKey, out var cached))
        {
            Interlocked.Increment(ref _cacheHits);
            IncrementCounter("sovereignty_routing.cache_hit");
            return cached!;
        }

        // 2. Determine jurisdictions
        var sourceJurisdiction = ExtractSourceJurisdiction(objectTags);
        var destJurisdiction = MapStorageBackendToJurisdiction(intendedDestination);

        // 3. Same jurisdiction = fast-path allow
        if (string.Equals(sourceJurisdiction, destJurisdiction, StringComparison.OrdinalIgnoreCase))
        {
            var sameJurisdictionDecision = new RoutingDecision
            {
                Allowed = true,
                Reason = $"Same jurisdiction ({sourceJurisdiction}); no sovereignty check required"
            };
            CacheDecision(cacheKey, sameJurisdictionDecision);
            Interlocked.Increment(ref _routingAllowed);
            return sameJurisdictionDecision;
        }

        // 4. Run sovereignty check via mesh
        if (_sovereigntyMesh == null)
        {
            // No mesh configured — allow with advisory
            var noMeshDecision = new RoutingDecision
            {
                Allowed = true,
                Reason = "No sovereignty mesh configured; routing allowed by default"
            };
            CacheDecision(cacheKey, noMeshDecision);
            Interlocked.Increment(ref _routingAllowed);
            return noMeshDecision;
        }

        var enforcementResult = await _sovereigntyMesh.CheckSovereigntyAsync(
            objectId, sourceJurisdiction, destJurisdiction, ct);

        // 5. Evaluate result
        RoutingDecision decision;

        if (enforcementResult.Allowed && enforcementResult.Action == ZoneAction.Allow)
        {
            decision = new RoutingDecision
            {
                Allowed = true,
                Reason = $"Sovereignty allows routing from {sourceJurisdiction} to {destJurisdiction}",
                EnforcementResult = enforcementResult
            };
            Interlocked.Increment(ref _routingAllowed);
        }
        else if (enforcementResult.Allowed && enforcementResult.Action != ZoneAction.Allow)
        {
            // Conditional allow
            decision = new RoutingDecision
            {
                Allowed = true,
                Reason = $"Conditional routing: {enforcementResult.Action} required ({sourceJurisdiction} -> {destJurisdiction})",
                EnforcementResult = enforcementResult
            };
            Interlocked.Increment(ref _routingAllowed);
        }
        else
        {
            // Denied — find alternatives
            var allowed = await FindAllowedBackendsForObject(objectId, sourceJurisdiction, objectTags, ct);
            var recommended = allowed.Count > 0 ? allowed[0] : null;

            decision = new RoutingDecision
            {
                Allowed = false,
                RecommendedLocation = recommended,
                BlockedLocations = new[] { intendedDestination },
                AllowedLocations = allowed,
                Reason = enforcementResult.DenialReason ??
                         $"Sovereignty denies routing from {sourceJurisdiction} to {destJurisdiction}",
                EnforcementResult = enforcementResult
            };
            Interlocked.Increment(ref _routingDenied);

            if (recommended != null)
            {
                Interlocked.Increment(ref _routingRedirected);
                IncrementCounter("sovereignty_routing.redirected");
            }
        }

        CacheDecision(cacheKey, decision);
        return decision;
    }

    // ==================================================================================
    // Jurisdiction mapping
    // ==================================================================================

    /// <summary>
    /// Maps a storage backend identifier to its jurisdiction code.
    /// Uses convention-based mapping with support for custom overrides from configuration.
    /// </summary>
    /// <param name="backendId">The storage backend identifier (e.g., "s3-us-east-1", "azure-eu-west").</param>
    /// <returns>The jurisdiction code (e.g., "US", "EU", "DE", "CN").</returns>
    public string MapStorageBackendToJurisdiction(string backendId)
    {
        ArgumentNullException.ThrowIfNull(backendId);

        // 1. Check custom mapping first
        if (_backendJurisdictionMap.TryGetValue(backendId, out var customJurisdiction))
        {
            return customJurisdiction;
        }

        var lower = backendId.ToLowerInvariant();

        // 2. US backends
        if (lower.StartsWith("s3-us-", StringComparison.Ordinal) ||
            lower.StartsWith("azure-us-", StringComparison.Ordinal))
        {
            return "US";
        }

        // 3. EU backends (try to extract specific country)
        if (lower.StartsWith("s3-eu-", StringComparison.Ordinal) ||
            lower.StartsWith("azure-eu-", StringComparison.Ordinal))
        {
            return ExtractEuCountry(lower) ?? "EU";
        }

        // 4. GCS backends — map by region
        if (lower.StartsWith("gcs-", StringComparison.Ordinal))
        {
            return MapGcsRegionToJurisdiction(lower);
        }

        // 5. China backends
        if (lower.Contains("-cn-", StringComparison.Ordinal) ||
            lower.StartsWith("cn-", StringComparison.Ordinal))
        {
            return "CN";
        }

        // 6. Other known prefixes
        if (lower.Contains("-jp-", StringComparison.Ordinal)) return "JP";
        if (lower.Contains("-kr-", StringComparison.Ordinal)) return "KR";
        if (lower.Contains("-au-", StringComparison.Ordinal)) return "AU";
        if (lower.Contains("-br-", StringComparison.Ordinal)) return "BR";
        if (lower.Contains("-in-", StringComparison.Ordinal)) return "IN";
        if (lower.Contains("-sg-", StringComparison.Ordinal)) return "SG";
        if (lower.Contains("-ca-", StringComparison.Ordinal)) return "CA";
        if (lower.Contains("-uk-", StringComparison.Ordinal)) return "GB";

        // 7. Default
        return _defaultJurisdiction;
    }

    /// <summary>
    /// Registers a custom backend-to-jurisdiction mapping.
    /// </summary>
    /// <param name="backendId">The storage backend identifier.</param>
    /// <param name="jurisdictionCode">The jurisdiction code.</param>
    public void RegisterBackendJurisdiction(string backendId, string jurisdictionCode)
    {
        ArgumentNullException.ThrowIfNull(backendId);
        ArgumentNullException.ThrowIfNull(jurisdictionCode);
        _backendJurisdictionMap[backendId] = jurisdictionCode;
    }

    // ==================================================================================
    // Compliant storage discovery
    // ==================================================================================

    /// <summary>
    /// Evaluates all known storage backends against sovereignty rules and returns
    /// the list of backends where the object CAN be stored.
    /// </summary>
    /// <param name="objectId">The data object identifier.</param>
    /// <param name="objectTags">Object tags containing passport and classification data.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of compliant storage backend identifiers.</returns>
    public async Task<IReadOnlyList<string>> GetCompliantStorageLocations(
        string objectId,
        IReadOnlyDictionary<string, object> objectTags,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(objectId);
        objectTags ??= new Dictionary<string, object>();
            IncrementCounter("sovereignty_routing.get_compliant");

        var sourceJurisdiction = ExtractSourceJurisdiction(objectTags);
        return await FindAllowedBackendsForObject(objectId, sourceJurisdiction, objectTags, ct);
    }

    // ==================================================================================
    // Statistics
    // ==================================================================================

    /// <summary>
    /// Returns routing-specific statistics.
    /// </summary>
    public RoutingStatistics GetRoutingStatistics()
    {
        return new RoutingStatistics
        {
            RoutingChecksTotal = Interlocked.Read(ref _routingChecksTotal),
            RoutingAllowed = Interlocked.Read(ref _routingAllowed),
            RoutingDenied = Interlocked.Read(ref _routingDenied),
            RoutingRedirected = Interlocked.Read(ref _routingRedirected),
            CacheHits = Interlocked.Read(ref _cacheHits),
            CacheSize = _routingCache.Count,
            RegisteredBackends = _backendJurisdictionMap.Count
        };
    }

    /// <summary>
    /// Evicts expired entries from the routing cache.
    /// </summary>
    /// <returns>Number of entries evicted.</returns>
    public int EvictExpiredCacheEntries()
    {
            IncrementCounter("sovereignty_routing.cache_evict");
        var now = Environment.TickCount64;
        var ttlTicks = (long)_cacheTtl.TotalMilliseconds;
        var evicted = 0;

        foreach (var kvp in _routingCache)
        {
            if (now - kvp.Value.CreatedAtTicks > ttlTicks)
            {
                if (_routingCache.TryRemove(kvp.Key, out _))
                    evicted++;
            }
        }

        return evicted;
    }

    // ==================================================================================
    // ComplianceStrategyBase override
    // ==================================================================================

    /// <inheritdoc/>
    protected override async Task<ComplianceResult> CheckComplianceCoreAsync(
        ComplianceContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
            IncrementCounter("sovereignty_routing.compliance_check");

        var objectId = context.ResourceId ?? "unknown";
        var sourceLocation = context.SourceLocation;
        var destLocation = context.DestinationLocation;

        if (string.IsNullOrWhiteSpace(destLocation))
        {
            return new ComplianceResult
            {
                IsCompliant = true,
                Framework = Framework,
                Status = ComplianceStatus.NotApplicable,
                Recommendations = new[] { "No destination location specified; routing check skipped" }
            };
        }

        // Build tags from context attributes
        var tags = new Dictionary<string, object>(context.Attributes);
        if (!string.IsNullOrWhiteSpace(sourceLocation) && !tags.ContainsKey("data.location"))
        {
            tags["data.location"] = sourceLocation;
        }

        var decision = await CheckRoutingAsync(objectId, destLocation, tags, cancellationToken);

        var violations = new List<ComplianceViolation>();
        var recommendations = new List<string>();

        if (!decision.Allowed)
        {
            violations.Add(new ComplianceViolation
            {
                Code = "SRT-001",
                Description = decision.Reason ?? $"Sovereignty routing denied for {objectId} -> {destLocation}",
                Severity = ViolationSeverity.High,
                AffectedResource = objectId,
                Remediation = decision.RecommendedLocation != null
                    ? $"Route to compliant backend: {decision.RecommendedLocation}"
                    : "No compliant storage location available; contact compliance team"
            });
        }

        if (decision.AllowedLocations is { Count: > 0 })
        {
            recommendations.Add($"Compliant storage locations: {string.Join(", ", decision.AllowedLocations)}");
        }

        return new ComplianceResult
        {
            IsCompliant = decision.Allowed,
            Framework = Framework,
            Status = decision.Allowed ? ComplianceStatus.Compliant : ComplianceStatus.NonCompliant,
            Violations = violations,
            Recommendations = recommendations,
            Metadata = new Dictionary<string, object>
            {
                ["ObjectId"] = objectId,
                ["IntendedDestination"] = destLocation,
                ["RoutingAllowed"] = decision.Allowed,
                ["RecommendedAlternative"] = decision.RecommendedLocation ?? "(none)"
            }
        };
    }

    // ==================================================================================
    // Private helpers
    // ==================================================================================

    private static string BuildCacheKey(string objectId, string destination)
        => $"{objectId}:{destination}";

    private bool TryGetCachedDecision(string cacheKey, out RoutingDecision? decision)
    {
        if (_routingCache.TryGetValue(cacheKey, out var entry))
        {
            var age = Environment.TickCount64 - entry.CreatedAtTicks;
            if (age <= (long)_cacheTtl.TotalMilliseconds)
            {
                decision = entry.Decision;
                return true;
            }

            // Expired — evict
            _routingCache.TryRemove(cacheKey, out _);
        }

        decision = null;
        return false;
    }

    private void CacheDecision(string cacheKey, RoutingDecision decision)
    {
        _routingCache[cacheKey] = new CacheEntry
        {
            Decision = decision,
            CreatedAtTicks = Environment.TickCount64
        };
    }

    private string ExtractSourceJurisdiction(IReadOnlyDictionary<string, object> objectTags)
    {
        if (objectTags.TryGetValue("data.location", out var loc) && loc is string locStr && locStr.Length > 0)
        {
            return locStr;
        }

        return _defaultJurisdiction;
    }

    private async Task<IReadOnlyList<string>> FindAllowedBackendsForObject(
        string objectId,
        string sourceJurisdiction,
        IReadOnlyDictionary<string, object> objectTags,
        CancellationToken ct)
    {
        if (_sovereigntyMesh == null)
        {
            // No mesh — all registered backends are allowed
            return _backendJurisdictionMap.Keys.ToList();
        }

        var allowed = new List<string>();

        foreach (var kvp in _backendJurisdictionMap)
        {
            var backendId = kvp.Key;
            var backendJurisdiction = kvp.Value;

            // Same jurisdiction is always allowed
            if (string.Equals(sourceJurisdiction, backendJurisdiction, StringComparison.OrdinalIgnoreCase))
            {
                allowed.Add(backendId);
                continue;
            }

            try
            {
                var result = await _sovereigntyMesh.CheckSovereigntyAsync(
                    objectId, sourceJurisdiction, backendJurisdiction, ct);

                if (result.Allowed)
                {
                    allowed.Add(backendId);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // P2-1548: Log the exception so that configuration errors are visible;
                // fail-safe by excluding backends whose sovereignty cannot be determined.
                IncrementCounter("sovereignty_routing.backend_check_error");
                System.Diagnostics.Debug.WriteLine(
                    $"[SovereigntyRouting] Backend check failed for '{backendId}' (object '{objectId}'): {ex.GetType().Name}: {ex.Message}");
            }
        }

        return allowed;
    }

    private static string? ExtractEuCountry(string lowerBackendId)
    {
        // P2-1549: Use exact word-boundary matching to prevent country code misidentification.
        // For example, "-se" must not match "southeast". A country code appears as
        // a dash-delimited token: "-<CC>" at end or "-<CC>-" in the middle.
        static bool HasCountryCode(string id, string code)
            => id.EndsWith(code, StringComparison.Ordinal) ||
               id.Contains(code + "-", StringComparison.Ordinal);

        // Try to extract country suffix: s3-eu-west-de -> DE, azure-de -> DE
        if (HasCountryCode(lowerBackendId, "-de")) return "DE";
        if (HasCountryCode(lowerBackendId, "-fr")) return "FR";
        if (HasCountryCode(lowerBackendId, "-it")) return "IT";
        if (HasCountryCode(lowerBackendId, "-es")) return "ES";
        if (HasCountryCode(lowerBackendId, "-nl")) return "NL";
        if (HasCountryCode(lowerBackendId, "-ie")) return "IE";
        if (HasCountryCode(lowerBackendId, "-se")) return "SE";
        if (HasCountryCode(lowerBackendId, "-pl")) return "PL";
        return null;
    }

    private string MapGcsRegionToJurisdiction(string lowerBackendId)
    {
        if (lowerBackendId.Contains("us-", StringComparison.Ordinal)) return "US";
        if (lowerBackendId.Contains("europe-", StringComparison.Ordinal)) return "EU";
        if (lowerBackendId.Contains("asia-east", StringComparison.Ordinal)) return "TW";
        if (lowerBackendId.Contains("asia-south", StringComparison.Ordinal)) return "IN";
        if (lowerBackendId.Contains("asia-northeast1", StringComparison.Ordinal)) return "JP";
        if (lowerBackendId.Contains("asia-northeast2", StringComparison.Ordinal)) return "JP";
        if (lowerBackendId.Contains("asia-northeast3", StringComparison.Ordinal)) return "KR";
        if (lowerBackendId.Contains("asia-southeast", StringComparison.Ordinal)) return "SG";
        if (lowerBackendId.Contains("australia-", StringComparison.Ordinal)) return "AU";
        if (lowerBackendId.Contains("southamerica-", StringComparison.Ordinal)) return "BR";
        if (lowerBackendId.Contains("northamerica-", StringComparison.Ordinal)) return "CA";
        return _defaultJurisdiction;
    }
}

/// <summary>
/// Routing-specific statistics for <see cref="SovereigntyRoutingStrategy"/>.
/// </summary>
public sealed record RoutingStatistics
{
    /// <summary>Total routing checks performed.</summary>
    public required long RoutingChecksTotal { get; init; }

    /// <summary>Number of routing checks that were allowed.</summary>
    public required long RoutingAllowed { get; init; }

    /// <summary>Number of routing checks that were denied.</summary>
    public required long RoutingDenied { get; init; }

    /// <summary>Number of denied routings that had a compliant redirect suggested.</summary>
    public required long RoutingRedirected { get; init; }

    /// <summary>Number of routing decisions served from cache.</summary>
    public required long CacheHits { get; init; }

    /// <summary>Current number of entries in the routing cache.</summary>
    public required int CacheSize { get; init; }

    /// <summary>Number of registered backend-to-jurisdiction mappings.</summary>
    public required int RegisteredBackends { get; init; }
}
