using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Compliance;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.SovereigntyMesh;

// ==================================================================================
// Sovereignty Enforcement Interceptor: Pipeline interceptor that checks sovereignty
// zone policies BEFORE storage routing allows write/read operations. Delegates
// enforcement decisions to ZoneEnforcerStrategy and translates zone actions into
// pipeline-level Block/Proceed/Conditional/PendApproval/Quarantine outcomes.
// ==================================================================================

/// <summary>
/// Pipeline interceptor that enforces sovereignty zone policies on write and read paths.
/// <para>
/// Sits between the application layer and <c>FederatedObjectStorage</c> to ensure that
/// sovereignty is checked BEFORE data movement occurs. Each intercepted operation is
/// evaluated by the <see cref="ZoneEnforcerStrategy"/> and translated into an
/// <see cref="InterceptionResult"/> that downstream routing respects.
/// </para>
/// <para>
/// Tracks interception statistics (total, blocked, allowed, conditional) via
/// <see cref="Interlocked"/> operations for thread safety.
/// </para>
/// </summary>
public sealed class SovereigntyEnforcementInterceptor : ComplianceStrategyBase
{
    private readonly ZoneEnforcerStrategy _zoneEnforcer;
    private readonly DeclarativeZoneRegistry _registry;

    private long _interceptionsTotal;
    private long _interceptionsBlocked;
    private long _interceptionsAllowed;
    private long _interceptionsConditional;

    /// <summary>Default home jurisdiction when source is not specified.</summary>
    private const string DefaultHomeJurisdiction = "US";

    /// <inheritdoc/>
    public override string StrategyId => "sovereignty-enforcement-interceptor";

    /// <inheritdoc/>
    public override string StrategyName => "Sovereignty Enforcement Interceptor";

    /// <inheritdoc/>
    public override string Framework => "SovereigntyMesh";

    /// <summary>
    /// Creates a new sovereignty enforcement interceptor.
    /// </summary>
    /// <param name="zoneEnforcer">Zone enforcer for policy evaluation.</param>
    /// <param name="registry">Zone registry for jurisdiction-to-zone lookups.</param>
    public SovereigntyEnforcementInterceptor(ZoneEnforcerStrategy zoneEnforcer, DeclarativeZoneRegistry registry)
    {
        _zoneEnforcer = zoneEnforcer ?? throw new ArgumentNullException(nameof(zoneEnforcer));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    // ==================================================================================
    // Public interception API
    // ==================================================================================

    /// <summary>
    /// Intercepts a write (store) operation, checking sovereignty zone enforcement
    /// before allowing data to be placed in the destination location.
    /// </summary>
    /// <param name="objectId">Identifier of the data object being written.</param>
    /// <param name="destinationLocation">Jurisdiction or location code of the write target.</param>
    /// <param name="passport">Optional compliance passport for the data object.</param>
    /// <param name="context">
    /// Additional context. Reads <c>SourceLocation</c> for the origin jurisdiction;
    /// defaults to system home jurisdiction if absent.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="InterceptionResult"/> indicating whether the write may proceed.</returns>
    public async Task<InterceptionResult> InterceptWriteAsync(
        string objectId,
        string destinationLocation,
        CompliancePassport? passport,
        IReadOnlyDictionary<string, object> context,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _interceptionsTotal);
            IncrementCounter("enforcement_interceptor.write");

        var sourceLocation = ResolveString(context, "SourceLocation") ?? DefaultHomeJurisdiction;

        // Same jurisdiction => no cross-border concern
        if (string.Equals(sourceLocation, destinationLocation, StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _interceptionsAllowed);
            return InterceptionResult.Proceed();
        }

        return await EvaluateAndTranslateAsync(objectId, sourceLocation, destinationLocation, passport, ct);
    }

    /// <summary>
    /// Intercepts a read operation, checking sovereignty zone enforcement before
    /// allowing data to be read by a requestor in a different jurisdiction.
    /// </summary>
    /// <param name="objectId">Identifier of the data object being read.</param>
    /// <param name="requestorLocation">Jurisdiction or location code of the requestor.</param>
    /// <param name="passport">Optional compliance passport for the data object.</param>
    /// <param name="context">
    /// Additional context. Reads <c>DataLocation</c> for where the data currently resides.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="InterceptionResult"/> indicating whether the read may proceed.</returns>
    public async Task<InterceptionResult> InterceptReadAsync(
        string objectId,
        string requestorLocation,
        CompliancePassport? passport,
        IReadOnlyDictionary<string, object> context,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _interceptionsTotal);
            IncrementCounter("enforcement_interceptor.read");

        var dataLocation = ResolveString(context, "DataLocation");

        if (string.IsNullOrEmpty(dataLocation))
        {
            // Cannot determine data location; allow by default
            Interlocked.Increment(ref _interceptionsAllowed);
            return InterceptionResult.Proceed();
        }

        // Same jurisdiction => allow
        if (string.Equals(dataLocation, requestorLocation, StringComparison.OrdinalIgnoreCase))
        {
            Interlocked.Increment(ref _interceptionsAllowed);
            return InterceptionResult.Proceed();
        }

        return await EvaluateAndTranslateAsync(objectId, dataLocation, requestorLocation, passport, ct);
    }

    /// <summary>
    /// Returns interception statistics.
    /// </summary>
    public InterceptionStatistics GetInterceptionStatistics()
    {
        return new InterceptionStatistics
        {
            InterceptionsTotal = Interlocked.Read(ref _interceptionsTotal),
            InterceptionsBlocked = Interlocked.Read(ref _interceptionsBlocked),
            InterceptionsAllowed = Interlocked.Read(ref _interceptionsAllowed),
            InterceptionsConditional = Interlocked.Read(ref _interceptionsConditional)
        };
    }

    // ==================================================================================
    // ComplianceStrategyBase override
    // ==================================================================================

    /// <inheritdoc/>
    protected override async Task<ComplianceResult> CheckComplianceCoreAsync(
        ComplianceContext context, CancellationToken cancellationToken)
    {
            IncrementCounter("enforcement_interceptor.check");

        var violations = new List<ComplianceViolation>();
        var recommendations = new List<string>();

        var sourceLocation = context.SourceLocation;
        var destLocation = context.DestinationLocation;

        if (string.IsNullOrEmpty(sourceLocation) || string.IsNullOrEmpty(destLocation))
        {
            return new ComplianceResult
            {
                IsCompliant = true,
                Framework = Framework,
                Status = ComplianceStatus.NotApplicable,
                Violations = violations,
                Recommendations = new[] { "Source and/or destination location not specified; interception skipped" }
            };
        }

        // Same location => compliant
        if (string.Equals(sourceLocation, destLocation, StringComparison.OrdinalIgnoreCase))
        {
            return new ComplianceResult
            {
                IsCompliant = true,
                Framework = Framework,
                Status = ComplianceStatus.Compliant,
                Violations = violations,
                Recommendations = recommendations
            };
        }

        // Run interception as a write check
        var ctxDict = new Dictionary<string, object> { ["SourceLocation"] = sourceLocation };
        foreach (var attr in context.Attributes)
            ctxDict[attr.Key] = attr.Value;

        var result = await InterceptWriteAsync(
            context.ResourceId ?? "unknown", destLocation, null, ctxDict, cancellationToken);

        if (result.Action == InterceptionAction.Block)
        {
            violations.Add(new ComplianceViolation
            {
                Code = "SEI-001",
                Description = result.Reason ?? "Sovereignty enforcement interceptor blocked cross-jurisdiction transfer",
                Severity = ViolationSeverity.High,
                AffectedResource = context.ResourceId,
                Remediation = "Obtain zone approval or ensure passport covers required regulations"
            });
        }
        else if (result.Action == InterceptionAction.ProceedWithCondition)
        {
            recommendations.Add($"Conditional transfer: {string.Join(", ", result.Conditions ?? Array.Empty<string>())}");
        }
        else if (result.Action == InterceptionAction.PendApproval)
        {
            recommendations.Add("Transfer is pending approval from zone authority");
        }
        else if (result.Action == InterceptionAction.Quarantine)
        {
            violations.Add(new ComplianceViolation
            {
                Code = "SEI-002",
                Description = "Data quarantined pending sovereignty review",
                Severity = ViolationSeverity.Medium,
                AffectedResource = context.ResourceId,
                Remediation = "Submit data for sovereignty review and await clearance"
            });
        }

        var hasHighViolations = violations.Any(v => v.Severity >= ViolationSeverity.High);


        var isCompliant = !hasHighViolations;

        return new ComplianceResult
        {
            IsCompliant = isCompliant,
            Framework = Framework,
            Status = violations.Count == 0 ? ComplianceStatus.Compliant
                : hasHighViolations ? ComplianceStatus.NonCompliant
                : ComplianceStatus.PartiallyCompliant,
            Violations = violations,
            Recommendations = recommendations,
            Metadata = new Dictionary<string, object>
            {
                ["InterceptionAction"] = result.Action.ToString(),
                ["HasConditions"] = result.Conditions?.Count > 0
            }
        };
    }

    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("enforcement_interceptor.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("enforcement_interceptor.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }

    // ==================================================================================
    // Private helpers
    // ==================================================================================

    /// <summary>
    /// Resolves zones for source and destination jurisdictions, runs enforcement,
    /// and translates the <see cref="ZoneEnforcementResult"/> into an <see cref="InterceptionResult"/>.
    /// </summary>
    private async Task<InterceptionResult> EvaluateAndTranslateAsync(
        string objectId,
        string sourceJurisdiction,
        string destinationJurisdiction,
        CompliancePassport? passport,
        CancellationToken ct)
    {
        // Find zones for both jurisdictions
        var sourceZones = await _registry.GetZonesForJurisdictionAsync(sourceJurisdiction, ct);
        var destZones = await _registry.GetZonesForJurisdictionAsync(destinationJurisdiction, ct);

        if (sourceZones.Count == 0 && destZones.Count == 0)
        {
            // No zones govern either jurisdiction; allow
            Interlocked.Increment(ref _interceptionsAllowed);
            return InterceptionResult.Proceed();
        }

        // Evaluate all zone pairs and take the most restrictive result
        ZoneEnforcementResult? worstResult = null;

        // If we have zones on both sides, check cross-zone
        if (sourceZones.Count > 0 && destZones.Count > 0)
        {
            foreach (var srcZone in sourceZones.Where(z => z.IsActive))
            {
                foreach (var dstZone in destZones.Where(z => z.IsActive))
                {
                    var result = await _zoneEnforcer.EnforceAsync(objectId, srcZone.ZoneId, dstZone.ZoneId, passport, ct);
                    worstResult = GetMoreRestrictive(worstResult, result);
                }
            }
        }
        else
        {
            // Only one side has zones; use the first zone from whichever side exists
            // and synthesize a "no-zone" partner
            var existingZones = sourceZones.Count > 0 ? sourceZones : destZones;
            var isSource = sourceZones.Count > 0;

            foreach (var zone in existingZones.Where(z => z.IsActive))
            {
                // Enforce with a synthetic "unzoned" partner
                var srcId = isSource ? zone.ZoneId : "unzoned";
                var dstId = isSource ? "unzoned" : zone.ZoneId;

                // We can still evaluate the zone's own rules via the enforcer
                var result = await _zoneEnforcer.EnforceAsync(objectId, srcId, dstId, passport, ct);
                worstResult = GetMoreRestrictive(worstResult, result);
            }
        }

        if (worstResult == null)
        {
            Interlocked.Increment(ref _interceptionsAllowed);
            return InterceptionResult.Proceed();
        }

        return TranslateToInterceptionResult(worstResult);
    }

    /// <summary>
    /// Translates a <see cref="ZoneEnforcementResult"/> into an <see cref="InterceptionResult"/>.
    /// </summary>
    private InterceptionResult TranslateToInterceptionResult(ZoneEnforcementResult enforcement)
    {
        switch (enforcement.Action)
        {
            case ZoneAction.Allow:
                Interlocked.Increment(ref _interceptionsAllowed);
                return InterceptionResult.Proceed(enforcementDetail: enforcement);

            case ZoneAction.Deny:
                Interlocked.Increment(ref _interceptionsBlocked);
                return InterceptionResult.Block(
                    enforcement.DenialReason ?? "Sovereignty zone policy denies this transfer",
                    enforcement);

            case ZoneAction.RequireEncryption:
                Interlocked.Increment(ref _interceptionsConditional);
                return InterceptionResult.ProceedWithConditions(
                    new[] { "encrypt" },
                    enforcement);

            case ZoneAction.RequireApproval:
                Interlocked.Increment(ref _interceptionsConditional);
                return InterceptionResult.CreatePendApproval(enforcement);

            case ZoneAction.RequireAnonymization:
                Interlocked.Increment(ref _interceptionsConditional);
                return InterceptionResult.ProceedWithConditions(
                    new[] { "anonymize" },
                    enforcement);

            case ZoneAction.Quarantine:
                Interlocked.Increment(ref _interceptionsBlocked);
                return InterceptionResult.CreateQuarantine(enforcement);

            default:
                Interlocked.Increment(ref _interceptionsAllowed);
                return InterceptionResult.Proceed(enforcementDetail: enforcement);
        }
    }

    private static ZoneEnforcementResult GetMoreRestrictive(ZoneEnforcementResult? current, ZoneEnforcementResult candidate)
    {
        if (current == null) return candidate;

        var currentSeverity = GetActionSeverity(current.Action);
        var candidateSeverity = GetActionSeverity(candidate.Action);

        return candidateSeverity > currentSeverity ? candidate : current;
    }

    private static int GetActionSeverity(ZoneAction action) => action switch
    {
        ZoneAction.Allow => 0,
        ZoneAction.RequireApproval => 1,
        ZoneAction.RequireEncryption => 2,
        ZoneAction.RequireAnonymization => 3,
        ZoneAction.Quarantine => 4,
        ZoneAction.Deny => 5,
        _ => 0
    };

    private static string? ResolveString(IReadOnlyDictionary<string, object> context, string key)
    {
        if (context.TryGetValue(key, out var value) && value is string str && !string.IsNullOrWhiteSpace(str))
            return str;
        return null;
    }
}

// ==================================================================================
// Supporting types
// ==================================================================================

/// <summary>
/// Action a pipeline interceptor takes after sovereignty evaluation.
/// </summary>
public enum InterceptionAction
{
    /// <summary>Allow the operation to proceed normally.</summary>
    Proceed,

    /// <summary>Block the operation entirely.</summary>
    Block,

    /// <summary>Allow the operation to proceed if specified conditions are met.</summary>
    ProceedWithCondition,

    /// <summary>Operation is pending approval from a zone authority.</summary>
    PendApproval,

    /// <summary>Data is quarantined for further review.</summary>
    Quarantine
}

/// <summary>
/// Result of a sovereignty enforcement interception on the write/read pipeline.
/// </summary>
public sealed record InterceptionResult
{
    /// <summary>The interception action to take.</summary>
    public required InterceptionAction Action { get; init; }

    /// <summary>Reason for blocking or conditioning the operation, if applicable.</summary>
    public string? Reason { get; init; }

    /// <summary>Conditions that must be met before the operation can proceed.</summary>
    public IReadOnlyList<string>? Conditions { get; init; }

    /// <summary>The underlying zone enforcement result, for audit and downstream use.</summary>
    public ZoneEnforcementResult? EnforcementDetail { get; init; }

    /// <summary>Timestamp when this interception was evaluated.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    // --- Factory methods ---

    /// <summary>Creates a Proceed result.</summary>
    public static InterceptionResult Proceed(ZoneEnforcementResult? enforcementDetail = null) => new()
    {
        Action = InterceptionAction.Proceed,
        EnforcementDetail = enforcementDetail
    };

    /// <summary>Creates a Block result with a reason.</summary>
    public static InterceptionResult Block(string reason, ZoneEnforcementResult? enforcementDetail = null) => new()
    {
        Action = InterceptionAction.Block,
        Reason = reason,
        EnforcementDetail = enforcementDetail
    };

    /// <summary>Creates a ProceedWithCondition result.</summary>
    public static InterceptionResult ProceedWithConditions(
        IReadOnlyList<string> conditions, ZoneEnforcementResult? enforcementDetail = null) => new()
    {
        Action = InterceptionAction.ProceedWithCondition,
        Conditions = conditions,
        EnforcementDetail = enforcementDetail
    };

    /// <summary>Creates a PendApproval result.</summary>
    public static InterceptionResult CreatePendApproval(ZoneEnforcementResult? enforcementDetail = null) => new()
    {
        Action = InterceptionAction.PendApproval,
        Reason = "Approval required from sovereignty zone authority",
        EnforcementDetail = enforcementDetail
    };

    /// <summary>Creates a Quarantine result.</summary>
    public static InterceptionResult CreateQuarantine(ZoneEnforcementResult? enforcementDetail = null) => new()
    {
        Action = InterceptionAction.Quarantine,
        Reason = "Data quarantined for sovereignty review",
        EnforcementDetail = enforcementDetail
    };
}

/// <summary>
/// Statistics for sovereignty enforcement interceptions.
/// </summary>
public sealed class InterceptionStatistics
{
    /// <summary>Total number of interceptions performed.</summary>
    public long InterceptionsTotal { get; init; }

    /// <summary>Number of interceptions that resulted in Block.</summary>
    public long InterceptionsBlocked { get; init; }

    /// <summary>Number of interceptions that resulted in Proceed.</summary>
    public long InterceptionsAllowed { get; init; }

    /// <summary>Number of interceptions that resulted in conditional/pending outcomes.</summary>
    public long InterceptionsConditional { get; init; }
}
