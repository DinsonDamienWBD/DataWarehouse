using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Compliance;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateCompliance.Strategies.SovereigntyMesh;

// ==================================================================================
// Declarative Zone Registry: Pre-configured sovereignty zones covering EU, Americas,
// APAC, Middle East/Africa, Industry, and Supranational regulatory regions.
// ==================================================================================

/// <summary>
/// Registry of declarative sovereignty zones extending <see cref="ComplianceStrategyBase"/>.
/// <para>
/// On initialization, registers 31 pre-configured sovereignty zones covering global
/// regulatory frameworks: EU (6), Americas (5), APAC (8), Middle East/Africa (5),
/// Industry (4), and Supranational (3). Each zone defines jurisdictions, required
/// regulations, and tag-based action rules for declarative governance enforcement.
/// </para>
/// <para>
/// Supports custom zone registration, jurisdiction-based lookups, and zone lifecycle
/// management (activation/deactivation). All operations are thread-safe via
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// </para>
/// </summary>
public sealed class DeclarativeZoneRegistry : ComplianceStrategyBase
{
    private readonly BoundedDictionary<string, SovereigntyZone> _zones = new BoundedDictionary<string, SovereigntyZone>(1000);

    /// <inheritdoc/>
    public override string StrategyId => "sovereignty-zone-registry";

    /// <inheritdoc/>
    public override string StrategyName => "Declarative Sovereignty Zone Registry";

    /// <inheritdoc/>
    public override string Framework => "SovereigntyMesh";

    /// <inheritdoc/>
    public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
    {
        RegisterEuZones();
        RegisterAmericasZones();
        RegisterApacZones();
        RegisterMiddleEastAfricaZones();
        RegisterIndustryZones();
        RegisterSupranationalZones();

        return base.InitializeAsync(configuration, cancellationToken);
    }

    // ==================================================================================
    // Public API
    // ==================================================================================

    /// <summary>
    /// Registers a custom sovereignty zone in the registry.
    /// </summary>
    /// <param name="zone">The zone to register.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">When zone is null.</exception>
    public Task RegisterZoneAsync(SovereigntyZone zone, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (zone == null) throw new ArgumentNullException(nameof(zone));
        _zones[zone.ZoneId] = zone;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Retrieves a zone by its identifier.
    /// </summary>
    /// <param name="zoneId">Unique zone identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The zone if found; otherwise <c>null</c>.</returns>
    public Task<SovereigntyZone?> GetZoneAsync(string zoneId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        _zones.TryGetValue(zoneId, out var zone);
        return Task.FromResult<SovereigntyZone?>(zone);
    }

    /// <summary>
    /// Finds all zones that contain the specified jurisdiction code.
    /// </summary>
    /// <param name="jurisdictionCode">ISO country code or region code (case-insensitive).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>All zones governing the specified jurisdiction.</returns>
    public Task<IReadOnlyList<SovereigntyZone>> GetZonesForJurisdictionAsync(string jurisdictionCode, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var code = jurisdictionCode?.ToUpperInvariant() ?? string.Empty;

        var matchingZones = _zones.Values
            .Where(z => z.Jurisdictions.Any(j => string.Equals(j, code, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return Task.FromResult<IReadOnlyList<SovereigntyZone>>(matchingZones);
    }

    /// <summary>
    /// Returns all registered zones.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public Task<IReadOnlyList<SovereigntyZone>> GetAllZonesAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<SovereigntyZone>>(_zones.Values.ToList());
    }

    /// <summary>
    /// Deactivates a zone, preventing it from enforcing rules.
    /// The zone definition is retained but marked as inactive.
    /// </summary>
    /// <param name="zoneId">Zone identifier to deactivate.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task DeactivateZoneAsync(string zoneId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (_zones.TryGetValue(zoneId, out var zone))
        {
            zone.IsActive = false;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Gets the total number of registered zones.
    /// </summary>
    public int ZoneCount => _zones.Count;

    // ==================================================================================
    // ComplianceStrategyBase override
    // ==================================================================================

    /// <inheritdoc/>
    protected override Task<ComplianceResult> CheckComplianceCoreAsync(ComplianceContext context, CancellationToken cancellationToken)
    {
            IncrementCounter("zone_registry.check");
        var violations = new List<ComplianceViolation>();
        var recommendations = new List<string>();

        var sourceLocation = context.SourceLocation?.ToUpperInvariant();
        var destLocation = context.DestinationLocation?.ToUpperInvariant();

        // Find zones for source and destination jurisdictions
        var sourceZones = sourceLocation != null
            ? _zones.Values.Where(z => z.IsActive && z.Jurisdictions.Any(j => string.Equals(j, sourceLocation, StringComparison.OrdinalIgnoreCase))).ToList()
            : new List<SovereigntyZone>();

        var destZones = destLocation != null
            ? _zones.Values.Where(z => z.IsActive && z.Jurisdictions.Any(j => string.Equals(j, destLocation, StringComparison.OrdinalIgnoreCase))).ToList()
            : new List<SovereigntyZone>();

        // Check if source has active zones
        if (sourceLocation != null && sourceZones.Count == 0)
        {
            recommendations.Add($"No active sovereignty zones found for source jurisdiction '{sourceLocation}'");
        }

        // Check if destination has active zones
        if (destLocation != null && destZones.Count == 0)
        {
            recommendations.Add($"No active sovereignty zones found for destination jurisdiction '{destLocation}'");
        }

        // If cross-jurisdiction, verify zones exist on both sides
        if (sourceLocation != null && destLocation != null && !string.Equals(sourceLocation, destLocation, StringComparison.OrdinalIgnoreCase))
        {
            if (sourceZones.Count > 0 && destZones.Count == 0)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ZR-001",
                    Description = $"Data leaving governed zone(s) [{string.Join(", ", sourceZones.Select(z => z.ZoneId))}] to ungoverned jurisdiction '{destLocation}'",
                    Severity = ViolationSeverity.Medium,
                    Remediation = "Register a sovereignty zone for the destination jurisdiction or obtain explicit approval"
                });
            }

            // Check for conflicting zone policies
            var sourceRegulations = sourceZones.SelectMany(z => z.RequiredRegulations).Distinct().ToList();
            var destRegulations = destZones.SelectMany(z => z.RequiredRegulations).Distinct().ToList();

            var unmetRegulations = sourceRegulations.Except(destRegulations, StringComparer.OrdinalIgnoreCase).ToList();
            if (unmetRegulations.Count > 0)
            {
                violations.Add(new ComplianceViolation
                {
                    Code = "ZR-002",
                    Description = $"Destination jurisdiction does not enforce required regulations: {string.Join(", ", unmetRegulations)}",
                    Severity = ViolationSeverity.High,
                    Remediation = "Ensure destination jurisdiction enforces equivalent regulations or obtain transfer mechanism"
                });
            }
        }

        var isCompliant = !violations.Any(v => v.Severity >= ViolationSeverity.High);

        return Task.FromResult(new ComplianceResult
        {
            IsCompliant = isCompliant,
            Framework = Framework,
            Status = violations.Count == 0 ? ComplianceStatus.Compliant :
                    violations.Any(v => v.Severity >= ViolationSeverity.High) ? ComplianceStatus.NonCompliant :
                    ComplianceStatus.PartiallyCompliant,
            Violations = violations,
            Recommendations = recommendations,
            Metadata = new Dictionary<string, object>
            {
                ["TotalZones"] = _zones.Count,
                ["SourceZones"] = sourceZones.Select(z => z.ZoneId).ToArray(),
                ["DestinationZones"] = destZones.Select(z => z.ZoneId).ToArray()
            }
        });
    }

    /// <inheritdoc/>
    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("zone_registry.initialized");
        return base.InitializeAsyncCore(cancellationToken);
    }

    /// <inheritdoc/>
    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
            IncrementCounter("zone_registry.shutdown");
        return base.ShutdownAsyncCore(cancellationToken);
    }

    // ==================================================================================
    // Pre-configured Zone Registration
    // ==================================================================================

    #region EU Zones (6)

    private static readonly string[] EuMemberStates =
    {
        "AT", "BE", "BG", "HR", "CY", "CZ", "DK", "EE", "FI", "FR",
        "DE", "GR", "HU", "IE", "IT", "LV", "LT", "LU", "MT", "NL",
        "PL", "PT", "RO", "SK", "SI", "ES", "SE"
    };

    private static readonly string[] EeaStates = EuMemberStates.Concat(new[] { "IS", "NO", "LI" }).ToArray();

    private void RegisterEuZones()
    {
        // 1. EU GDPR Zone
        _zones["eu-gdpr-zone"] = new SovereigntyZoneBuilder()
            .WithId("eu-gdpr-zone")
            .WithName("EU General Data Protection Regulation (GDPR)")
            .InJurisdictions(EuMemberStates)
            .Requiring("GDPR")
            .WithRule("pii:*", ZoneAction.RequireEncryption)
            .WithRule("classification:secret", ZoneAction.Deny)
            .Build();

        // 2. EU EEA Zone
        _zones["eu-eea-zone"] = new SovereigntyZoneBuilder()
            .WithId("eu-eea-zone")
            .WithName("European Economic Area (EEA)")
            .InJurisdictions(EeaStates)
            .Requiring("GDPR")
            .WithRule("pii:*", ZoneAction.RequireEncryption)
            .WithRule("classification:secret", ZoneAction.Deny)
            .Build();

        // 3. EU DORA Zone (Financial sector)
        _zones["eu-dora-zone"] = new SovereigntyZoneBuilder()
            .WithId("eu-dora-zone")
            .WithName("EU Digital Operational Resilience Act (DORA)")
            .InJurisdictions(EuMemberStates)
            .Requiring("DORA", "GDPR")
            .WithRule("financial:*", ZoneAction.RequireApproval)
            .Build();

        // 4. EU NIS2 Zone (Critical infrastructure)
        _zones["eu-nis2-zone"] = new SovereigntyZoneBuilder()
            .WithId("eu-nis2-zone")
            .WithName("EU Network and Information Security Directive 2 (NIS2)")
            .InJurisdictions(EuMemberStates)
            .Requiring("NIS2", "GDPR")
            .WithRule("infrastructure:*", ZoneAction.RequireEncryption)
            .Build();

        // 5. EU AI Act Zone
        _zones["eu-ai-act-zone"] = new SovereigntyZoneBuilder()
            .WithId("eu-ai-act-zone")
            .WithName("EU Artificial Intelligence Act")
            .InJurisdictions(EuMemberStates)
            .Requiring("AI_Act")
            .WithRule("ai-model:*", ZoneAction.RequireApproval)
            .WithRule("training-data:*", ZoneAction.RequireAnonymization)
            .Build();

        // 6. EU eHealth Zone
        _zones["eu-ehealth-zone"] = new SovereigntyZoneBuilder()
            .WithId("eu-ehealth-zone")
            .WithName("EU Health Data Space")
            .InJurisdictions(EuMemberStates)
            .Requiring("GDPR", "ePrivacy")
            .WithRule("health:*", ZoneAction.RequireEncryption)
            .WithRule("patient:*", ZoneAction.Deny)
            .Build();
    }

    #endregion

    #region Americas Zones (5)

    private void RegisterAmericasZones()
    {
        // 7. US HIPAA Zone
        _zones["us-hipaa-zone"] = new SovereigntyZoneBuilder()
            .WithId("us-hipaa-zone")
            .WithName("US Health Insurance Portability and Accountability Act (HIPAA)")
            .InJurisdictions("US")
            .Requiring("HIPAA")
            .WithRule("phi:*", ZoneAction.RequireEncryption)
            .WithRule("health:*", ZoneAction.RequireApproval)
            .Build();

        // 8. US FedRAMP Zone
        _zones["us-fedramp-zone"] = new SovereigntyZoneBuilder()
            .WithId("us-fedramp-zone")
            .WithName("US Federal Risk and Authorization Management Program (FedRAMP)")
            .InJurisdictions("US")
            .Requiring("FedRAMP")
            .WithRule("cui:*", ZoneAction.RequireEncryption)
            .WithRule("classified:*", ZoneAction.Deny)
            .Build();

        // 9. US CCPA Zone (California)
        _zones["us-ccpa-zone"] = new SovereigntyZoneBuilder()
            .WithId("us-ccpa-zone")
            .WithName("California Consumer Privacy Act (CCPA)")
            .InJurisdictions("US-CA")
            .Requiring("CCPA")
            .WithRule("consumer:*", ZoneAction.RequireApproval)
            .Build();

        // 10. Brazil LGPD Zone
        _zones["br-lgpd-zone"] = new SovereigntyZoneBuilder()
            .WithId("br-lgpd-zone")
            .WithName("Brazil Lei Geral de Protecao de Dados (LGPD)")
            .InJurisdictions("BR")
            .Requiring("LGPD")
            .WithRule("pii:*", ZoneAction.RequireEncryption)
            .Build();

        // 11. Canada PIPEDA Zone
        _zones["ca-pipeda-zone"] = new SovereigntyZoneBuilder()
            .WithId("ca-pipeda-zone")
            .WithName("Canada Personal Information Protection and Electronic Documents Act (PIPEDA)")
            .InJurisdictions("CA")
            .Requiring("PIPEDA")
            .WithRule("personal:*", ZoneAction.RequireApproval)
            .Build();
    }

    #endregion

    #region APAC Zones (8)

    private void RegisterApacZones()
    {
        // 12. China PIPL Zone
        _zones["cn-pipl-zone"] = new SovereigntyZoneBuilder()
            .WithId("cn-pipl-zone")
            .WithName("China Personal Information Protection Law (PIPL)")
            .InJurisdictions("CN")
            .Requiring("PIPL")
            .WithRule("pii:*", ZoneAction.Deny)
            .WithRule("personal:*", ZoneAction.RequireApproval)
            .Build();

        // 13. Japan APPI Zone
        _zones["jp-appi-zone"] = new SovereigntyZoneBuilder()
            .WithId("jp-appi-zone")
            .WithName("Japan Act on Protection of Personal Information (APPI)")
            .InJurisdictions("JP")
            .Requiring("APPI")
            .WithRule("personal:*", ZoneAction.RequireApproval)
            .Build();

        // 14. Singapore PDPA Zone
        _zones["sg-pdpa-zone"] = new SovereigntyZoneBuilder()
            .WithId("sg-pdpa-zone")
            .WithName("Singapore Personal Data Protection Act (PDPA)")
            .InJurisdictions("SG")
            .Requiring("PDPA_SG")
            .WithRule("personal:*", ZoneAction.RequireEncryption)
            .Build();

        // 15. Australia Privacy Act Zone
        _zones["au-privacy-zone"] = new SovereigntyZoneBuilder()
            .WithId("au-privacy-zone")
            .WithName("Australia Privacy Act 1988")
            .InJurisdictions("AU")
            .Requiring("Privacy_Act_AU")
            .WithRule("personal:*", ZoneAction.RequireApproval)
            .Build();

        // 16. South Korea PIPA Zone
        _zones["kr-pipa-zone"] = new SovereigntyZoneBuilder()
            .WithId("kr-pipa-zone")
            .WithName("South Korea Personal Information Protection Act (PIPA)")
            .InJurisdictions("KR")
            .Requiring("K_PIPA")
            .WithRule("personal:*", ZoneAction.RequireEncryption)
            .Build();

        // 17. India PDPB Zone
        _zones["in-pdpb-zone"] = new SovereigntyZoneBuilder()
            .WithId("in-pdpb-zone")
            .WithName("India Personal Data Protection Bill (PDPB)")
            .InJurisdictions("IN")
            .Requiring("PDPB")
            .WithRule("critical-personal:*", ZoneAction.Deny)
            .WithRule("personal:*", ZoneAction.RequireApproval)
            .Build();

        // 18. Hong Kong PDPO Zone
        _zones["hk-pdpo-zone"] = new SovereigntyZoneBuilder()
            .WithId("hk-pdpo-zone")
            .WithName("Hong Kong Personal Data (Privacy) Ordinance (PDPO)")
            .InJurisdictions("HK")
            .Requiring("PDPO_HK")
            .WithRule("personal:*", ZoneAction.RequireApproval)
            .Build();

        // 19. Taiwan PDPA Zone
        _zones["tw-pdpa-zone"] = new SovereigntyZoneBuilder()
            .WithId("tw-pdpa-zone")
            .WithName("Taiwan Personal Data Protection Act (PDPA)")
            .InJurisdictions("TW")
            .Requiring("PDPA_TW")
            .WithRule("personal:*", ZoneAction.RequireApproval)
            .Build();
    }

    #endregion

    #region Middle East / Africa Zones (5)

    private void RegisterMiddleEastAfricaZones()
    {
        // 20. UAE PDPL Zone
        _zones["ae-pdpl-zone"] = new SovereigntyZoneBuilder()
            .WithId("ae-pdpl-zone")
            .WithName("UAE Personal Data Protection Law (PDPL)")
            .InJurisdictions("AE")
            .Requiring("AE_PDPL")
            .WithRule("personal:*", ZoneAction.RequireEncryption)
            .Build();

        // 21. Saudi Arabia PDPL Zone
        _zones["sa-pdpl-zone"] = new SovereigntyZoneBuilder()
            .WithId("sa-pdpl-zone")
            .WithName("Saudi Arabia Personal Data Protection Law (PDPL)")
            .InJurisdictions("SA")
            .Requiring("SA_PDPL")
            .WithRule("personal:*", ZoneAction.RequireEncryption)
            .WithRule("government:*", ZoneAction.Deny)
            .Build();

        // 22. South Africa POPIA Zone
        _zones["za-popia-zone"] = new SovereigntyZoneBuilder()
            .WithId("za-popia-zone")
            .WithName("South Africa Protection of Personal Information Act (POPIA)")
            .InJurisdictions("ZA")
            .Requiring("POPIA")
            .WithRule("personal:*", ZoneAction.RequireApproval)
            .Build();

        // 23. Qatar PDPL Zone
        _zones["qa-pdpl-zone"] = new SovereigntyZoneBuilder()
            .WithId("qa-pdpl-zone")
            .WithName("Qatar Personal Data Privacy Law (PDPL)")
            .InJurisdictions("QA")
            .Requiring("QA_PDPL")
            .WithRule("personal:*", ZoneAction.RequireEncryption)
            .Build();

        // 24. Nigeria NDPR Zone
        _zones["ng-ndpr-zone"] = new SovereigntyZoneBuilder()
            .WithId("ng-ndpr-zone")
            .WithName("Nigeria Data Protection Regulation (NDPR)")
            .InJurisdictions("NG")
            .Requiring("NDPR")
            .WithRule("personal:*", ZoneAction.RequireApproval)
            .Build();
    }

    #endregion

    #region Industry Zones (4)

    private void RegisterIndustryZones()
    {
        // 25. Financial Global Zone (PCI DSS + SOX)
        _zones["financial-global-zone"] = new SovereigntyZoneBuilder()
            .WithId("financial-global-zone")
            .WithName("Global Financial Compliance Zone (PCI DSS + SOX)")
            .InJurisdictions("*")
            .Requiring("PCI_DSS", "SOX")
            .WithRule("payment:*", ZoneAction.RequireEncryption)
            .WithRule("financial:*", ZoneAction.RequireApproval)
            .Build();

        // 26. Healthcare Global Zone
        _zones["healthcare-global-zone"] = new SovereigntyZoneBuilder()
            .WithId("healthcare-global-zone")
            .WithName("Global Healthcare Compliance Zone")
            .InJurisdictions("*")
            .Requiring("HIPAA")
            .WithRule("health:*", ZoneAction.RequireEncryption)
            .WithRule("phi:*", ZoneAction.Deny)
            .Build();

        // 27. ISO 27001 Zone
        _zones["iso27001-zone"] = new SovereigntyZoneBuilder()
            .WithId("iso27001-zone")
            .WithName("ISO 27001 Information Security Zone")
            .InJurisdictions("*")
            .Requiring("ISO27001")
            .WithRule("classified:*", ZoneAction.RequireEncryption)
            .Build();

        // 28. WORM SEC 17a-4 Zone
        _zones["worm-sec-zone"] = new SovereigntyZoneBuilder()
            .WithId("worm-sec-zone")
            .WithName("SEC Rule 17a-4 WORM Compliance Zone")
            .InJurisdictions("*")
            .Requiring("SEC_17a4")
            .WithRule("financial-record:*", ZoneAction.Deny)
            .Build();
    }

    #endregion

    #region Supranational Zones (3)

    private void RegisterSupranationalZones()
    {
        // 29. Five Eyes Zone
        _zones["five-eyes-zone"] = new SovereigntyZoneBuilder()
            .WithId("five-eyes-zone")
            .WithName("Five Eyes Intelligence Alliance Zone")
            .InJurisdictions("US", "GB", "CA", "AU", "NZ")
            .Requiring("FVEY_Agreement")
            .WithRule("intelligence:*", ZoneAction.RequireEncryption)
            .Build();

        // 30. APEC CBPR Zone
        _zones["apec-cbpr-zone"] = new SovereigntyZoneBuilder()
            .WithId("apec-cbpr-zone")
            .WithName("APEC Cross-Border Privacy Rules (CBPR) Zone")
            .InJurisdictions("US", "MX", "JP", "CA", "SG", "KR", "AU", "TW", "PH")
            .Requiring("CBPR")
            .WithRule("personal:*", ZoneAction.RequireApproval)
            .Build();

        // 31. EU Adequacy Zone
        _zones["adequacy-zone"] = new SovereigntyZoneBuilder()
            .WithId("adequacy-zone")
            .WithName("EU Adequacy Decision Zone")
            .InJurisdictions("JP", "KR", "NZ", "GB", "CH", "IL", "AR", "UY")
            .Requiring("GDPR")
            .WithRule("pii:*", ZoneAction.Allow)
            .Build();
    }

    #endregion
}
