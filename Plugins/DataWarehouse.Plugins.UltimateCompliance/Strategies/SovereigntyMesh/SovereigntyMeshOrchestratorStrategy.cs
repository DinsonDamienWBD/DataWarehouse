using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Compliance;
using DataWarehouse.Plugins.UltimateCompliance.Strategies.Passport;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.SovereigntyMesh;

/// <summary>
/// Orchestrates all sovereignty mesh operations through a single unified API.
/// <para>
/// Implements <see cref="ISovereigntyMesh"/> by coordinating seven sub-strategies:
/// zone enforcement, passport issuance, passport verification, passport lifecycle,
/// cross-border transfer protocol, zero-knowledge verification, and the declarative
/// zone registry. This is the primary entry point for kernel and plugin consumers
/// of sovereignty operations.
/// </para>
/// </summary>
public sealed class SovereigntyMeshOrchestratorStrategy : ComplianceStrategyBase, ISovereigntyMesh
{
    // ==================================================================================
    // Message bus topic constants for event-driven integration
    // ==================================================================================

    /// <summary>Topic published when a new compliance passport is issued.</summary>
    public const string TopicPassportIssued = "compliance.passport.issued";

    /// <summary>Topic published when a compliance passport is permanently revoked.</summary>
    public const string TopicPassportRevoked = "compliance.passport.revoked";

    /// <summary>Topic published when a compliance passport expires.</summary>
    public const string TopicPassportExpired = "compliance.passport.expired";

    /// <summary>Topic published when a sovereignty zone violation is detected.</summary>
    public const string TopicSovereigntyViolation = "compliance.sovereignty.violation";

    /// <summary>Topic published when a cross-border transfer is negotiated.</summary>
    public const string TopicCrossBorderTransfer = "compliance.crossborder.transfer";

    /// <summary>Topic published when a sovereignty zone is activated.</summary>
    public const string TopicZoneActivated = "compliance.zone.activated";

    /// <summary>Topic published when a sovereignty zone is deactivated.</summary>
    public const string TopicZoneDeactivated = "compliance.zone.deactivated";

    // ==================================================================================
    // Sub-strategies (initialized in InitializeAsync)
    // ==================================================================================

    private DeclarativeZoneRegistry _zoneRegistry = null!;
    private ZoneEnforcerStrategy _zoneEnforcer = null!;
    private CrossBorderTransferProtocolStrategy _crossBorderProtocol = null!;
    private PassportIssuanceStrategy _passportIssuance = null!;
    private PassportVerificationApiStrategy _passportVerification = null!;
    private ZeroKnowledgePassportVerificationStrategy _zkVerification = null!;
    private PassportLifecycleStrategy _lifecycleManager = null!;

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _meshInitialized;

    /// <inheritdoc/>
    public override string StrategyId => "sovereignty-mesh-orchestrator";

    /// <inheritdoc/>
    public override string StrategyName => "Sovereignty Mesh Orchestrator";

    /// <inheritdoc/>
    public override string Framework => "SovereigntyMesh";

    // ==================================================================================
    // ISovereigntyMesh properties
    // ==================================================================================

    /// <inheritdoc/>
    public IZoneEnforcer ZoneEnforcer => _zoneEnforcer;

    /// <inheritdoc/>
    public ICrossBorderProtocol CrossBorderProtocol => _crossBorderProtocol;

    // ==================================================================================
    // Initialization
    // ==================================================================================

    /// <inheritdoc/>
    public override async Task InitializeAsync(
        Dictionary<string, object> configuration,
        CancellationToken cancellationToken = default)
    {
        await base.InitializeAsync(configuration, cancellationToken);

        if (_meshInitialized) return;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_meshInitialized) return;

            // 1. Zone Registry
            _zoneRegistry = new DeclarativeZoneRegistry();
            await _zoneRegistry.InitializeAsync(new Dictionary<string, object>(Configuration), cancellationToken);

            // 2. Zone Enforcer (depends on registry)
            _zoneEnforcer = new ZoneEnforcerStrategy(_zoneRegistry);
            await _zoneEnforcer.InitializeAsync(new Dictionary<string, object>(Configuration), cancellationToken);

            // 3. Cross-Border Transfer Protocol
            _crossBorderProtocol = new CrossBorderTransferProtocolStrategy();
            await _crossBorderProtocol.InitializeAsync(new Dictionary<string, object>(Configuration), cancellationToken);

            // 4. Passport Issuance
            _passportIssuance = new PassportIssuanceStrategy();
            await _passportIssuance.InitializeAsync(new Dictionary<string, object>(Configuration), cancellationToken);

            // 5. Passport Verification API
            _passportVerification = new PassportVerificationApiStrategy();
            await _passportVerification.InitializeAsync(new Dictionary<string, object>(Configuration), cancellationToken);

            // 6. Zero-Knowledge Passport Verification
            _zkVerification = new ZeroKnowledgePassportVerificationStrategy();
            await _zkVerification.InitializeAsync(new Dictionary<string, object>(Configuration), cancellationToken);

            // 7. Passport Lifecycle Manager
            _lifecycleManager = new PassportLifecycleStrategy();
            await _lifecycleManager.InitializeAsync(new Dictionary<string, object>(Configuration), cancellationToken);

            _meshInitialized = true;
            IncrementCounter("sovereignty_mesh.initialized");
        }
        finally
        {
            _initLock.Release();
        }
    }

    // ==================================================================================
    // ISovereigntyMesh methods
    // ==================================================================================

    /// <inheritdoc/>
    public async Task<CompliancePassport> IssuePassportAsync(
        string objectId,
        IReadOnlyList<string> regulations,
        CancellationToken ct)
    {
        EnsureMeshInitialized();
        ct.ThrowIfCancellationRequested();
            IncrementCounter("sovereignty_mesh.issue_passport");

        // Build compliance context from object and regulations
        var context = new ComplianceContext
        {
            OperationType = "PassportIssuance",
            DataClassification = "regulated",
            ResourceId = objectId,
            ProcessingPurposes = regulations.ToList()
        };

        // Issue the passport
        var passport = await _passportIssuance.IssuePassportAsync(objectId, regulations, context, ct);

        // Register in lifecycle manager
        await _lifecycleManager.RegisterPassportAsync(passport, ct);

        return passport;
    }

    /// <inheritdoc/>
    public async Task<PassportVerificationResult> VerifyPassportAsync(string passportId, CancellationToken ct)
    {
        EnsureMeshInitialized();
        ct.ThrowIfCancellationRequested();
            IncrementCounter("sovereignty_mesh.verify_passport");

        // Look up passport from lifecycle registry first, then issuance cache
        var registryEntry = _lifecycleManager.GetRegistryEntry(passportId);
        CompliancePassport? passport = null;

        if (registryEntry != null)
        {
            // Find the passport via the issuance cache using the object ID
            passport = _passportIssuance.GetCachedPassport(registryEntry.ObjectId);
        }

        // Also try direct passport ID lookup across all cached passports
        if (passport == null || passport.PassportId != passportId)
        {
            // The passport is not in the issuance cache by objectId, or the IDs don't match
            // Return a not-found result
            if (passport == null || passport.PassportId != passportId)
            {
                return new PassportVerificationResult
                {
                    IsValid = false,
                    Passport = new CompliancePassport
                    {
                        PassportId = passportId,
                        ObjectId = registryEntry?.ObjectId ?? "unknown",
                        Status = PassportStatus.PendingReview,
                        Scope = PassportScope.Object,
                        Entries = Array.Empty<PassportEntry>(),
                        EvidenceChain = Array.Empty<EvidenceLink>(),
                        IssuedAt = DateTimeOffset.UtcNow,
                        ExpiresAt = DateTimeOffset.UtcNow,
                        IssuerId = "unknown"
                    },
                    FailureReasons = new[] { "Passport not found" },
                    VerifiedAt = DateTimeOffset.UtcNow
                };
            }
        }

        // Verify via the verification API
        return await _passportVerification.VerifyPassportAsync(passport, ct);
    }

    /// <inheritdoc/>
    public async Task<ZoneEnforcementResult> CheckSovereigntyAsync(
        string objectId,
        string sourceLocation,
        string destLocation,
        CancellationToken ct)
    {
        EnsureMeshInitialized();
        ct.ThrowIfCancellationRequested();
            IncrementCounter("sovereignty_mesh.check_sovereignty");

        // 1. Look up zones for source and destination
        var sourceZones = await _zoneRegistry.GetZonesForJurisdictionAsync(sourceLocation, ct);
        var destZones = await _zoneRegistry.GetZonesForJurisdictionAsync(destLocation, ct);

        // 2. If no zones found for either, allow (no sovereignty constraints)
        if (sourceZones.Count == 0 && destZones.Count == 0)
        {
            return new ZoneEnforcementResult
            {
                Allowed = true,
                Action = ZoneAction.Allow,
                SourceZoneId = sourceLocation,
                DestinationZoneId = destLocation,
                DenialReason = null
            };
        }

        // 3. Look up passport for the object
        var passport = _passportIssuance.GetCachedPassport(objectId);

        // 4. Run zone enforcement for each source-dest zone pair, take most restrictive
        ZoneEnforcementResult? worstResult = null;

        var activeSourceZones = sourceZones.Where(z => z.IsActive).ToList();
        var activeDestZones = destZones.Where(z => z.IsActive).ToList();

        // If one side has no zones, use a synthetic "any" zone ID
        if (activeSourceZones.Count == 0 && activeDestZones.Count > 0)
        {
            foreach (var destZone in activeDestZones)
            {
                var result = await _zoneEnforcer.EnforceAsync(
                    objectId, sourceLocation, destZone.ZoneId, passport, ct);
                worstResult = PickMostRestrictive(worstResult, result);
            }
        }
        else if (activeDestZones.Count == 0 && activeSourceZones.Count > 0)
        {
            foreach (var srcZone in activeSourceZones)
            {
                var result = await _zoneEnforcer.EnforceAsync(
                    objectId, srcZone.ZoneId, destLocation, passport, ct);
                worstResult = PickMostRestrictive(worstResult, result);
            }
        }
        else
        {
            foreach (var srcZone in activeSourceZones)
            {
                foreach (var dstZone in activeDestZones)
                {
                    var result = await _zoneEnforcer.EnforceAsync(
                        objectId, srcZone.ZoneId, dstZone.ZoneId, passport, ct);
                    worstResult = PickMostRestrictive(worstResult, result);

                    // 5. If enforcement requires cross-border protocol, negotiate
                    if (result.Allowed && result.Action != ZoneAction.Allow && passport != null)
                    {
                        await RunCrossBorderProtocolAsync(objectId, sourceLocation, destLocation, passport, result, ct);
                    }
                }
            }
        }

        // If no enforcement pairs ran, allow
        if (worstResult == null)
        {
            return new ZoneEnforcementResult
            {
                Allowed = true,
                Action = ZoneAction.Allow,
                SourceZoneId = sourceLocation,
                DestinationZoneId = destLocation
            };
        }

        return worstResult;
    }

    // ==================================================================================
    // Additional orchestration methods
    // ==================================================================================

    /// <summary>
    /// Combines passport issuance and sovereignty check in a single call.
    /// Issues a passport if one does not already exist for the object, then checks sovereignty.
    /// </summary>
    /// <param name="objectId">Identifier of the data object.</param>
    /// <param name="sourceLocation">Source jurisdiction code.</param>
    /// <param name="destLocation">Destination jurisdiction code.</param>
    /// <param name="regulations">Regulations to certify against.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Tuple of the issued/existing passport and the sovereignty enforcement result.</returns>
    public async Task<(CompliancePassport Passport, ZoneEnforcementResult Enforcement)> CheckAndIssueAsync(
        string objectId,
        string sourceLocation,
        string destLocation,
        IReadOnlyList<string> regulations,
        CancellationToken ct)
    {
        EnsureMeshInitialized();
        ct.ThrowIfCancellationRequested();
            IncrementCounter("sovereignty_mesh.check_and_issue");

        // Issue passport if not exists
        var passport = _passportIssuance.GetCachedPassport(objectId);
        if (passport == null || !passport.IsValid())
        {
            passport = await IssuePassportAsync(objectId, regulations, ct);
        }

        // Check sovereignty
        var enforcement = await CheckSovereigntyAsync(objectId, sourceLocation, destLocation, ct);

        return (passport, enforcement);
    }

    /// <summary>
    /// Generates a zero-knowledge proof for a passport claim.
    /// </summary>
    /// <param name="passportId">Identifier of the passport to prove claims about.</param>
    /// <param name="claim">Claim to prove (e.g., "covers:GDPR", "valid", "score:>=0.8").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A cryptographic ZK proof.</returns>
    /// <exception cref="InvalidOperationException">When the passport is not found or the claim is false.</exception>
    public async Task<ZkPassportProof> GenerateZkProofAsync(string passportId, string claim, CancellationToken ct)
    {
        EnsureMeshInitialized();
        ct.ThrowIfCancellationRequested();
            IncrementCounter("sovereignty_mesh.generate_zk_proof");

        // Find the passport
        var registryEntry = _lifecycleManager.GetRegistryEntry(passportId);
        CompliancePassport? passport = null;

        if (registryEntry != null)
        {
            passport = _passportIssuance.GetCachedPassport(registryEntry.ObjectId);
        }

        if (passport == null || passport.PassportId != passportId)
        {
            throw new InvalidOperationException($"Passport '{passportId}' not found in mesh.");
        }

        return await _zkVerification.GenerateProofAsync(passport, claim, ct);
    }

    /// <summary>
    /// Verifies a zero-knowledge passport proof.
    /// </summary>
    /// <param name="proof">The ZK proof to verify.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Verification result.</returns>
    public async Task<ZkVerificationResult> VerifyZkProofAsync(ZkPassportProof proof, CancellationToken ct)
    {
        EnsureMeshInitialized();
        ct.ThrowIfCancellationRequested();
            IncrementCounter("sovereignty_mesh.verify_zk_proof");

        return await _zkVerification.VerifyProofAsync(proof, ct);
    }

    /// <summary>
    /// Returns overall mesh health status including zone, passport, and transfer metrics.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Aggregate mesh status.</returns>
    public async Task<MeshStatus> GetMeshStatusAsync(CancellationToken ct)
    {
        EnsureMeshInitialized();
        ct.ThrowIfCancellationRequested();
            IncrementCounter("sovereignty_mesh.get_status");

        var allZones = await _zoneRegistry.GetAllZonesAsync(ct);
        var totalZones = allZones.Count;
        var activeZones = allZones.Count(z => z.IsActive);

        // Get passport stats from lifecycle
        var passportStats = _lifecycleManager.GetStatistics();
        var expired = await _lifecycleManager.GetExpiredPassportsAsync(ct);

        // Get cross-border stats
        var crossBorderStats = _crossBorderProtocol.GetStatistics();

        // Get zone enforcer stats
        var enforcerStats = _zoneEnforcer.GetStatistics();

        return new MeshStatus
        {
            TotalZones = totalZones,
            ActiveZones = activeZones,
            TotalPassports = passportStats.TotalChecks,
            ValidPassports = passportStats.CompliantCount,
            ExpiredPassports = expired.Count,
            RecentTransfers = crossBorderStats.TotalChecks,
            RecentViolations = enforcerStats.NonCompliantCount,
            Timestamp = DateTimeOffset.UtcNow
        };
    }

    // ==================================================================================
    // Static factory
    // ==================================================================================

    /// <summary>
    /// Creates a default orchestrator with standard configuration.
    /// </summary>
    /// <returns>A ready-to-use <see cref="SovereigntyMeshOrchestratorStrategy"/> instance.</returns>
    public static async Task<SovereigntyMeshOrchestratorStrategy> CreateDefaultAsync(CancellationToken ct = default)
    {
        var orchestrator = new SovereigntyMeshOrchestratorStrategy();
        await orchestrator.InitializeAsync(new Dictionary<string, object>(), ct);
        return orchestrator;
    }

    // ==================================================================================
    // ComplianceStrategyBase override
    // ==================================================================================

    /// <inheritdoc/>
    protected override async Task<ComplianceResult> CheckComplianceCoreAsync(
        ComplianceContext context,
        CancellationToken cancellationToken)
    {
        EnsureMeshInitialized();
            IncrementCounter("sovereignty_mesh.check");

        var violations = new List<ComplianceViolation>();
        var recommendations = new List<string>();
        var sourceLocation = context.SourceLocation;
        var destLocation = context.DestinationLocation;

        // If no locations specified, delegate to sub-strategies
        if (string.IsNullOrWhiteSpace(sourceLocation) || string.IsNullOrWhiteSpace(destLocation))
        {
            return new ComplianceResult
            {
                IsCompliant = true,
                Framework = Framework,
                Status = ComplianceStatus.NotApplicable,
                Recommendations = new[] { "Source and/or destination location not specified; sovereignty check skipped" }
            };
        }

        // Run full sovereignty check
        var objectId = context.ResourceId ?? "unknown";
        var enforcementResult = await CheckSovereigntyAsync(objectId, sourceLocation, destLocation, cancellationToken);

        if (!enforcementResult.Allowed)
        {
            violations.Add(new ComplianceViolation
            {
                Code = "SM-001",
                Description = enforcementResult.DenialReason ?? $"Sovereignty check denied: {sourceLocation} -> {destLocation}",
                Severity = ViolationSeverity.High,
                AffectedResource = objectId,
                Remediation = enforcementResult.RequiredActions != null
                    ? $"Required actions: {string.Join(", ", enforcementResult.RequiredActions)}"
                    : "Obtain proper sovereignty clearance before proceeding"
            });
        }
        else if (enforcementResult.RequiredActions != null && enforcementResult.RequiredActions.Count > 0)
        {
            recommendations.Add(
                $"Conditional transfer: complete required actions: {string.Join(", ", enforcementResult.RequiredActions)}");
        }

        // Check passport coverage
        var passport = _passportIssuance.GetCachedPassport(objectId);
        if (passport == null)
        {
            recommendations.Add("No compliance passport found for this object; consider issuing one");
        }
        else if (!passport.IsValid())
        {
            recommendations.Add("Compliance passport exists but is not valid; consider renewal");
        }

        var isCompliant = violations.Count == 0;

        return new ComplianceResult
        {
            IsCompliant = isCompliant,
            Framework = Framework,
            Status = violations.Count == 0
                ? ComplianceStatus.Compliant
                : violations.Any(v => v.Severity >= ViolationSeverity.High)
                    ? ComplianceStatus.NonCompliant
                    : ComplianceStatus.PartiallyCompliant,
            Violations = violations,
            Recommendations = recommendations,
            Metadata = new Dictionary<string, object>
            {
                ["EnforcementAction"] = enforcementResult.Action.ToString(),
                ["SourceZone"] = enforcementResult.SourceZoneId,
                ["DestinationZone"] = enforcementResult.DestinationZoneId,
                ["HasPassport"] = passport != null,
                ["PassportValid"] = passport?.IsValid() ?? false
            }
        };
    }

    /// <summary>
    /// Aggregates statistics from all sub-strategies.
    /// </summary>
    /// <returns>Combined compliance statistics.</returns>
    public ComplianceStatistics GetAggregateStatistics()
    {
        var stats = GetStatistics();

        if (!_meshInitialized)
            return stats;

        var registryStats = _zoneRegistry.GetStatistics();
        var enforcerStats = _zoneEnforcer.GetStatistics();
        var crossBorderStats = _crossBorderProtocol.GetStatistics();
        var issuanceStats = _passportIssuance.GetStatistics();
        var verificationStats = _passportVerification.GetStatistics();
        var zkStats = _zkVerification.GetStatistics();
        var lifecycleStats = _lifecycleManager.GetStatistics();

        return new ComplianceStatistics
        {
            TotalChecks = stats.TotalChecks
                + registryStats.TotalChecks
                + enforcerStats.TotalChecks
                + crossBorderStats.TotalChecks
                + issuanceStats.TotalChecks
                + verificationStats.TotalChecks
                + zkStats.TotalChecks
                + lifecycleStats.TotalChecks,
            CompliantCount = stats.CompliantCount
                + registryStats.CompliantCount
                + enforcerStats.CompliantCount
                + crossBorderStats.CompliantCount
                + issuanceStats.CompliantCount
                + verificationStats.CompliantCount
                + zkStats.CompliantCount
                + lifecycleStats.CompliantCount,
            NonCompliantCount = stats.NonCompliantCount
                + registryStats.NonCompliantCount
                + enforcerStats.NonCompliantCount
                + crossBorderStats.NonCompliantCount
                + issuanceStats.NonCompliantCount
                + verificationStats.NonCompliantCount
                + zkStats.NonCompliantCount
                + lifecycleStats.NonCompliantCount,
            ViolationsFound = stats.ViolationsFound
                + registryStats.ViolationsFound
                + enforcerStats.ViolationsFound
                + crossBorderStats.ViolationsFound
                + issuanceStats.ViolationsFound
                + verificationStats.ViolationsFound
                + zkStats.ViolationsFound
                + lifecycleStats.ViolationsFound,
            StartTime = stats.StartTime,
            LastCheckTime = new[]
            {
                stats.LastCheckTime, registryStats.LastCheckTime, enforcerStats.LastCheckTime,
                crossBorderStats.LastCheckTime, issuanceStats.LastCheckTime, verificationStats.LastCheckTime,
                zkStats.LastCheckTime, lifecycleStats.LastCheckTime
            }.Max()
        };
    }

    // ==================================================================================
    // Private helpers
    // ==================================================================================

    /// <summary>
    /// Ordered severity levels from least to most restrictive (mirrors ZoneEnforcerStrategy).
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

    private static ZoneEnforcementResult PickMostRestrictive(
        ZoneEnforcementResult? current,
        ZoneEnforcementResult candidate)
    {
        if (current == null) return candidate;

        var currentIdx = Array.IndexOf(SeverityOrder, current.Action);
        var candidateIdx = Array.IndexOf(SeverityOrder, candidate.Action);

        return candidateIdx > currentIdx ? candidate : current;
    }

    private async Task RunCrossBorderProtocolAsync(
        string objectId,
        string sourceLocation,
        string destLocation,
        CompliancePassport passport,
        ZoneEnforcementResult enforcementResult,
        CancellationToken ct)
    {
        try
        {
            // Negotiate transfer agreement
            var agreement = await _crossBorderProtocol.NegotiateTransferAsync(
                sourceLocation, destLocation, passport, ct);

            // Evaluate the transfer
            await _crossBorderProtocol.EvaluateTransferAsync(agreement.AgreementId, ct);

            // Log the transfer
            await _crossBorderProtocol.LogTransferAsync(new CrossBorderTransferLog
            {
                TransferId = Guid.NewGuid().ToString("N"),
                ObjectId = objectId,
                SourceJurisdiction = sourceLocation,
                DestinationJurisdiction = destLocation,
                PassportId = passport.PassportId,
                Decision = agreement.Decision,
                AgreementId = agreement.AgreementId,
                Timestamp = DateTimeOffset.UtcNow,
                Metadata = new Dictionary<string, object>
                {
                    ["ZoneAction"] = enforcementResult.Action.ToString(),
                    ["LegalBasis"] = agreement.LegalBasis ?? "Unknown"
                }
            }, ct);
        }
        catch (Exception)
        {
            // Cross-border protocol failure should not block zone enforcement result.
            // The enforcement result stands independently.
            IncrementCounter("sovereignty_mesh.cross_border_error");
        }
    }

    private void EnsureMeshInitialized()
    {
        if (!_meshInitialized)
        {
            throw new InvalidOperationException(
                "SovereigntyMeshOrchestrator has not been initialized. Call InitializeAsync first.");
        }
    }
}

/// <summary>
/// Aggregate health status of the sovereignty mesh.
/// </summary>
public sealed record MeshStatus
{
    /// <summary>Total number of registered sovereignty zones.</summary>
    public required int TotalZones { get; init; }

    /// <summary>Number of currently active (enforcing) zones.</summary>
    public required int ActiveZones { get; init; }

    /// <summary>Total number of passports tracked by the mesh.</summary>
    public required long TotalPassports { get; init; }

    /// <summary>Number of currently valid passports.</summary>
    public required long ValidPassports { get; init; }

    /// <summary>Number of expired passports awaiting renewal.</summary>
    public required int ExpiredPassports { get; init; }

    /// <summary>Number of cross-border transfers processed recently.</summary>
    public required long RecentTransfers { get; init; }

    /// <summary>Number of sovereignty violations detected recently.</summary>
    public required long RecentViolations { get; init; }

    /// <summary>Timestamp when this status snapshot was taken.</summary>
    public required DateTimeOffset Timestamp { get; init; }
}
