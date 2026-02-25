using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Compliance;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.SovereigntyMesh;

// ==================================================================================
// Zone Enforcer Strategy: IZoneEnforcer implementation that evaluates cross-zone
// data movement with bi-directional egress/ingress checks, passport awareness,
// enforcement result caching (5-min TTL), and a full audit trail.
// ==================================================================================

/// <summary>
/// Enforces sovereignty zone policies for cross-zone data transfers.
/// <para>
/// Implements <see cref="IZoneEnforcer"/> alongside <see cref="ComplianceStrategyBase"/>.
/// Evaluates both source zone egress and destination zone ingress rules, checks passport
/// regulation coverage to reduce enforcement severity, caches enforcement results with a
/// 5-minute TTL, and maintains a per-object audit trail of all enforcement decisions.
/// </para>
/// <para>
/// Delegates zone CRUD operations to <see cref="DeclarativeZoneRegistry"/> for single
/// source of truth on zone definitions.
/// </para>
/// </summary>
public sealed class ZoneEnforcerStrategy : ComplianceStrategyBase, IZoneEnforcer
{
    private readonly DeclarativeZoneRegistry _registry;

    private readonly BoundedDictionary<string, CachedEnforcementResult> _enforcementCache = new BoundedDictionary<string, CachedEnforcementResult>(1000);
    private readonly BoundedDictionary<string, List<EnforcementAuditEntry>> _auditTrail = new BoundedDictionary<string, List<EnforcementAuditEntry>>(1000);

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    /// <inheritdoc/>
    public override string StrategyId => "sovereignty-zone-enforcer";

    /// <inheritdoc/>
    public override string StrategyName => "Sovereignty Zone Enforcer";

    /// <inheritdoc/>
    public override string Framework => "SovereigntyMesh";

    /// <summary>
    /// Creates a new zone enforcer backed by the given zone registry.
    /// </summary>
    /// <param name="registry">
    /// The <see cref="DeclarativeZoneRegistry"/> that holds all zone definitions.
    /// </param>
    public ZoneEnforcerStrategy(DeclarativeZoneRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    // ==================================================================================
    // IZoneEnforcer implementation
    // ==================================================================================

    /// <inheritdoc/>
    public async Task<ZoneEnforcementResult> EnforceAsync(
        string objectId,
        string sourceZoneId,
        string destinationZoneId,
        CompliancePassport? passport,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
            IncrementCounter("zone_enforcer.enforce");

        if (string.IsNullOrWhiteSpace(objectId))
            throw new ArgumentNullException(nameof(objectId));
        if (string.IsNullOrWhiteSpace(sourceZoneId))
            throw new ArgumentNullException(nameof(sourceZoneId));
        if (string.IsNullOrWhiteSpace(destinationZoneId))
            throw new ArgumentNullException(nameof(destinationZoneId));

        // 1. Same zone => always allow (intra-zone movement)
        if (string.Equals(sourceZoneId, destinationZoneId, StringComparison.OrdinalIgnoreCase))
        {
            var intraResult = CreateResult(true, ZoneAction.Allow, sourceZoneId, destinationZoneId);
            RecordAudit(objectId, sourceZoneId, destinationZoneId, ZoneAction.Allow, passport?.PassportId, "Intra-zone: always allowed");
            return intraResult;
        }

        // 2. Check cache
        var cacheKey = BuildCacheKey(objectId, sourceZoneId, destinationZoneId, passport?.PassportId);
        if (_enforcementCache.TryGetValue(cacheKey, out var cached) && !cached.IsExpired)
        {
            IncrementCounter("zone_enforcer.cache_hit");
            return cached.Result;
        }

        // 3. Resolve zones
        var sourceZone = await _registry.GetZoneAsync(sourceZoneId, ct);
        var destZone = await _registry.GetZoneAsync(destinationZoneId, ct);

        if (sourceZone == null && destZone == null)
        {
            // Neither zone exists; allow by default
            var noZoneResult = CreateResult(true, ZoneAction.Allow, sourceZoneId, destinationZoneId,
                denialReason: null, requiredActions: null);
            RecordAudit(objectId, sourceZoneId, destinationZoneId, ZoneAction.Allow, passport?.PassportId,
                "Neither source nor destination zone found; defaulting to allow");
            return noZoneResult;
        }

        // Build evaluation context from passport metadata (tags)
        var context = BuildEvaluationContext(passport);

        // 4. Evaluate source zone egress rules
        ZoneAction sourceAction = ZoneAction.Allow;
        string? sourceDenial = null;
        if (sourceZone != null && sourceZone.IsActive)
        {
            sourceAction = await sourceZone.EvaluateAsync(objectId, passport, context, ct);
            if (sourceAction == ZoneAction.Deny)
            {
                sourceDenial = $"Source zone '{sourceZoneId}' denies egress";
            }
        }

        // 5. Check passport coverage against source zone regulations
        if (sourceZone != null && passport != null && passport.IsValid())
        {
            var allSourceCovered = sourceZone.RequiredRegulations
                .All(r => passport.CoversRegulation(r));
            if (allSourceCovered && sourceAction != ZoneAction.Deny)
            {
                // Full passport coverage de-escalates source action
                sourceAction = DeescalateAction(sourceAction);
            }
        }

        // If source denies egress, block immediately
        if (sourceAction == ZoneAction.Deny)
        {
            var deniedResult = CreateResult(false, ZoneAction.Deny, sourceZoneId, destinationZoneId,
                sourceDenial ?? $"Source zone '{sourceZoneId}' denies data egress");
            CacheResult(cacheKey, deniedResult);
            RecordAudit(objectId, sourceZoneId, destinationZoneId, ZoneAction.Deny, passport?.PassportId,
                deniedResult.DenialReason ?? "Source egress denied");
            return deniedResult;
        }

        // 6. Evaluate destination zone ingress rules
        ZoneAction destAction = ZoneAction.Allow;
        var requiredActions = new List<string>();
        if (destZone != null && destZone.IsActive)
        {
            destAction = await destZone.EvaluateAsync(objectId, passport, context, ct);
            if (destAction == ZoneAction.Deny)
            {
                var destDenied = CreateResult(false, ZoneAction.Deny, sourceZoneId, destinationZoneId,
                    $"Destination zone '{destinationZoneId}' denies data ingress");
                CacheResult(cacheKey, destDenied);
                RecordAudit(objectId, sourceZoneId, destinationZoneId, ZoneAction.Deny, passport?.PassportId,
                    destDenied.DenialReason ?? "Destination ingress denied");
                return destDenied;
            }

            // Collect required actions from destination zone
            CollectRequiredActions(destAction, destinationZoneId, requiredActions);
        }

        // 7. Check passport coverage against destination zone regulations
        if (destZone != null && passport != null && passport.IsValid())
        {
            var allDestCovered = destZone.RequiredRegulations
                .All(r => passport.CoversRegulation(r));
            if (allDestCovered && destAction != ZoneAction.Deny)
            {
                destAction = DeescalateAction(destAction);
            }
        }

        // 8. Collect required actions from source zone
        CollectRequiredActions(sourceAction, sourceZoneId, requiredActions);

        // 9. Determine final action: most restrictive wins
        var finalAction = GetMostRestrictive(sourceAction, destAction);
        var allowed = finalAction != ZoneAction.Deny;

        var finalResult = CreateResult(
            allowed,
            finalAction,
            sourceZoneId,
            destinationZoneId,
            denialReason: allowed ? null : $"Cross-zone transfer denied by combined zone policies",
            requiredActions: requiredActions.Count > 0 ? requiredActions : null);

        // 10. Cache and audit
        CacheResult(cacheKey, finalResult);
        RecordAudit(objectId, sourceZoneId, destinationZoneId, finalAction, passport?.PassportId,
            $"Final decision: {finalAction}" + (requiredActions.Count > 0 ? $" (actions: {string.Join(", ", requiredActions)})" : ""));

        return finalResult;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ISovereigntyZone>> GetZonesForJurisdictionAsync(string jurisdictionCode, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
            IncrementCounter("zone_enforcer.get_zones_for_jurisdiction");
        var zones = await _registry.GetZonesForJurisdictionAsync(jurisdictionCode, ct);
        return zones.Cast<ISovereigntyZone>().ToList();
    }

    /// <inheritdoc/>
    public async Task<ISovereigntyZone?> GetZoneAsync(string zoneId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
            IncrementCounter("zone_enforcer.get_zone");
        return await _registry.GetZoneAsync(zoneId, ct);
    }

    /// <inheritdoc/>
    public async Task RegisterZoneAsync(ISovereigntyZone zone, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
            IncrementCounter("zone_enforcer.register_zone");

        if (zone is SovereigntyZone concreteZone)
        {
            await _registry.RegisterZoneAsync(concreteZone, ct);
        }
        else
        {
            // Wrap the interface into the concrete type via builder
            var built = new SovereigntyZoneBuilder()
                .WithId(zone.ZoneId)
                .WithName(zone.Name)
                .InJurisdictions(zone.Jurisdictions.ToArray())
                .Build();

            // Copy regulations and rules via reflection-free approach: rebuild fully
            var builder = new SovereigntyZoneBuilder()
                .WithId(zone.ZoneId)
                .WithName(zone.Name)
                .InJurisdictions(zone.Jurisdictions.ToArray());

            foreach (var reg in zone.RequiredRegulations)
                builder.Requiring(reg);

            foreach (var rule in zone.ActionRules)
                builder.WithRule(rule.Key, rule.Value);

            builder.Active(zone.IsActive);

            await _registry.RegisterZoneAsync(builder.Build(), ct);
        }
    }

    /// <inheritdoc/>
    public async Task DeactivateZoneAsync(string zoneId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
            IncrementCounter("zone_enforcer.deactivate_zone");
        await _registry.DeactivateZoneAsync(zoneId, ct);

        // Invalidate any cached results involving this zone
        InvalidateCacheForZone(zoneId);
    }

    // ==================================================================================
    // Audit trail
    // ==================================================================================

    /// <summary>
    /// Retrieves the enforcement audit trail for a specific object.
    /// </summary>
    /// <param name="objectId">Identifier of the data object.</param>
    /// <returns>Audit entries for the object, ordered most recent first.</returns>
    public IReadOnlyList<EnforcementAuditEntry> GetEnforcementAuditAsync(string objectId)
    {
        if (_auditTrail.TryGetValue(objectId, out var entries))
        {
            lock (entries)
            {
                return entries.OrderByDescending(e => e.Timestamp).ToList();
            }
        }

        return Array.Empty<EnforcementAuditEntry>();
    }

    // ==================================================================================
    // ComplianceStrategyBase override
    // ==================================================================================

    /// <inheritdoc/>
    protected override async Task<ComplianceResult> CheckComplianceCoreAsync(
        ComplianceContext context, CancellationToken cancellationToken)
    {
            IncrementCounter("zone_enforcer.check");

        var sourceLocation = context.SourceLocation;
        var destLocation = context.DestinationLocation;
        var violations = new List<ComplianceViolation>();
        var recommendations = new List<string>();

        if (string.IsNullOrEmpty(sourceLocation) || string.IsNullOrEmpty(destLocation))
        {
            return new ComplianceResult
            {
                IsCompliant = true,
                Framework = Framework,
                Status = ComplianceStatus.NotApplicable,
                Violations = violations,
                Recommendations = new[] { "Source and/or destination location not specified; enforcement skipped" }
            };
        }

        // Find zones for source and destination jurisdictions
        var sourceZones = await _registry.GetZonesForJurisdictionAsync(sourceLocation, cancellationToken);
        var destZones = await _registry.GetZonesForJurisdictionAsync(destLocation, cancellationToken);

        if (sourceZones.Count == 0 && destZones.Count == 0)
        {
            recommendations.Add("No sovereignty zones found for either jurisdiction");
            return new ComplianceResult
            {
                IsCompliant = true,
                Framework = Framework,
                Status = ComplianceStatus.Compliant,
                Violations = violations,
                Recommendations = recommendations
            };
        }

        // Run enforcement for each source->dest zone pair
        var resourceId = context.ResourceId ?? "unknown";
        var worstAction = ZoneAction.Allow;

        foreach (var srcZone in sourceZones.Where(z => z.IsActive))
        {
            foreach (var dstZone in destZones.Where(z => z.IsActive))
            {
                var result = await EnforceAsync(resourceId, srcZone.ZoneId, dstZone.ZoneId, null, cancellationToken);
                if (!result.Allowed)
                {
                    violations.Add(new ComplianceViolation
                    {
                        Code = "ZE-001",
                        Description = result.DenialReason ?? $"Cross-zone transfer denied: {srcZone.ZoneId} -> {dstZone.ZoneId}",
                        Severity = ViolationSeverity.High,
                        AffectedResource = resourceId,
                        Remediation = result.RequiredActions != null
                            ? $"Required actions: {string.Join(", ", result.RequiredActions)}"
                            : "Obtain zone approval or use approved transfer mechanisms"
                    });
                }

                worstAction = GetMostRestrictive(worstAction, result.Action);
            }
        }

        var isCompliant = !violations.Any(v => v.Severity >= ViolationSeverity.High);

        return new ComplianceResult
        {
            IsCompliant = isCompliant,
            Framework = Framework,
            Status = violations.Count == 0 ? ComplianceStatus.Compliant
                : violations.Any(v => v.Severity >= ViolationSeverity.High) ? ComplianceStatus.NonCompliant
                : ComplianceStatus.PartiallyCompliant,
            Violations = violations,
            Recommendations = recommendations,
            Metadata = new Dictionary<string, object>
            {
                ["SourceZones"] = sourceZones.Select(z => z.ZoneId).ToArray(),
                ["DestinationZones"] = destZones.Select(z => z.ZoneId).ToArray(),
                ["WorstAction"] = worstAction.ToString()
            }
        };
    }

    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("zone_enforcer.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("zone_enforcer.shutdown");
        _enforcementCache.Clear();
        return base.ShutdownAsyncCore(cancellationToken);
    }

    // ==================================================================================
    // Private helpers
    // ==================================================================================

    /// <summary>
    /// Ordered severity levels from least to most restrictive.
    /// </summary>
    private static readonly ZoneAction[] SeverityOrder =
    {
        ZoneAction.Allow,
        ZoneAction.RequireApproval,
        ZoneAction.RequireEncryption,
        ZoneAction.RequireAnonymization,
        ZoneAction.Quarantine,
        ZoneAction.Deny
    };

    private static ZoneAction GetMostRestrictive(ZoneAction a, ZoneAction b)
    {
        var idxA = Array.IndexOf(SeverityOrder, a);
        var idxB = Array.IndexOf(SeverityOrder, b);
        return idxA >= idxB ? a : b;
    }

    private static ZoneAction DeescalateAction(ZoneAction action)
    {
        var index = Array.IndexOf(SeverityOrder, action);
        return index <= 0 ? SeverityOrder[0] : SeverityOrder[index - 1];
    }

    private static void CollectRequiredActions(ZoneAction action, string zoneId, List<string> actions)
    {
        switch (action)
        {
            case ZoneAction.RequireEncryption:
                actions.Add($"encrypt-data[{zoneId}]");
                break;
            case ZoneAction.RequireApproval:
                actions.Add($"obtain-approval[{zoneId}]");
                break;
            case ZoneAction.RequireAnonymization:
                actions.Add($"anonymize-pii[{zoneId}]");
                break;
            case ZoneAction.Quarantine:
                actions.Add($"quarantine-review[{zoneId}]");
                break;
        }
    }

    private static IReadOnlyDictionary<string, object> BuildEvaluationContext(CompliancePassport? passport)
    {
        var ctx = new Dictionary<string, object>();

        if (passport?.Metadata != null)
        {
            foreach (var kvp in passport.Metadata)
            {
                ctx[kvp.Key] = kvp.Value;
            }
        }

        return ctx;
    }

    private static string BuildCacheKey(string objectId, string sourceZoneId, string destZoneId, string? passportId)
    {
        return $"{objectId}:{sourceZoneId}:{destZoneId}:{passportId ?? "none"}";
    }

    private void CacheResult(string cacheKey, ZoneEnforcementResult result)
    {
        _enforcementCache[cacheKey] = new CachedEnforcementResult(result, DateTimeOffset.UtcNow.Add(CacheTtl));

        // Opportunistic eviction of expired entries (every 50 cache writes)
        if (_enforcementCache.Count % 50 == 0)
        {
            EvictExpiredCacheEntries();
        }
    }

    private void EvictExpiredCacheEntries()
    {
        foreach (var key in _enforcementCache.Keys)
        {
            if (_enforcementCache.TryGetValue(key, out var entry) && entry.IsExpired)
            {
                _enforcementCache.TryRemove(key, out _);
            }
        }
    }

    private void InvalidateCacheForZone(string zoneId)
    {
        foreach (var key in _enforcementCache.Keys)
        {
            if (key.Contains(zoneId, StringComparison.OrdinalIgnoreCase))
            {
                _enforcementCache.TryRemove(key, out _);
            }
        }
    }

    private void RecordAudit(
        string objectId, string sourceZoneId, string destZoneId,
        ZoneAction decision, string? passportId, string details)
    {
        var entry = new EnforcementAuditEntry
        {
            ObjectId = objectId,
            SourceZoneId = sourceZoneId,
            DestZoneId = destZoneId,
            Decision = decision,
            PassportId = passportId,
            Timestamp = DateTimeOffset.UtcNow,
            Details = details
        };

        var trail = _auditTrail.GetOrAdd(objectId, _ => new List<EnforcementAuditEntry>());
        lock (trail)
        {
            trail.Add(entry);

            // Retain last 500 entries per object
            if (trail.Count > 500)
            {
                trail.RemoveAt(0);
            }
        }
    }

    private static ZoneEnforcementResult CreateResult(
        bool allowed, ZoneAction action, string sourceZoneId, string destZoneId,
        string? denialReason = null, IReadOnlyList<string>? requiredActions = null)
    {
        return new ZoneEnforcementResult
        {
            Allowed = allowed,
            Action = action,
            SourceZoneId = sourceZoneId,
            DestinationZoneId = destZoneId,
            DenialReason = denialReason,
            RequiredActions = requiredActions
        };
    }

    // ==================================================================================
    // Supporting types
    // ==================================================================================

    /// <summary>
    /// Wrapper for caching enforcement results with TTL expiry.
    /// </summary>
    private sealed record CachedEnforcementResult(ZoneEnforcementResult Result, DateTimeOffset ExpiresAt)
    {
        public bool IsExpired => DateTimeOffset.UtcNow >= ExpiresAt;
    }
}

/// <summary>
/// Audit entry recording an enforcement decision for cross-zone data movement.
/// </summary>
public sealed record EnforcementAuditEntry
{
    /// <summary>Identifier of the data object evaluated.</summary>
    public required string ObjectId { get; init; }

    /// <summary>Source zone in the enforcement evaluation.</summary>
    public required string SourceZoneId { get; init; }

    /// <summary>Destination zone in the enforcement evaluation.</summary>
    public required string DestZoneId { get; init; }

    /// <summary>Enforcement decision applied.</summary>
    public required ZoneAction Decision { get; init; }

    /// <summary>Passport identifier used during evaluation, if any.</summary>
    public string? PassportId { get; init; }

    /// <summary>Timestamp of the enforcement decision.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Human-readable details of the decision.</summary>
    public required string Details { get; init; }
}
