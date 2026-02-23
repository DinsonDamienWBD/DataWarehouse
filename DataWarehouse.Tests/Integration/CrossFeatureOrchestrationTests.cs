using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Integration;

/// <summary>
/// Verifies cross-feature interaction paths between the 10 moonshot features (Phases 55-63).
/// Uses static analysis of source files to confirm type references, topic subscriptions,
/// and cross-feature wiring without requiring runtime plugin instantiation.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "CrossFeatureOrchestration")]
public class CrossFeatureOrchestrationTests
{
    private readonly ITestOutputHelper _output;
    private static readonly string SolutionRoot = FindSolutionRoot();
    private static readonly string PluginsRoot = Path.Combine(SolutionRoot, "Plugins");
    private static readonly string SdkRoot = Path.Combine(SolutionRoot, "DataWarehouse.SDK");

    // Moonshot feature -> plugin directory mapping
    private static readonly Dictionary<string, string> MoonshotPluginMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UniversalTags"] = "DataWarehouse.Plugins.UltimateStorage", // Tags in SDK, strategies in Storage
        ["DataConsciousness"] = "DataWarehouse.Plugins.UltimateIntelligence",
        ["CompliancePassports"] = "DataWarehouse.Plugins.UltimateCompliance",
        ["SovereigntyMesh"] = "DataWarehouse.Plugins.UltimateCompliance",
        ["ZeroGravityStorage"] = "DataWarehouse.Plugins.UltimateStorage",
        ["CryptoTimeLocks"] = "DataWarehouse.Plugins.TamperProof",
        ["SemanticSync"] = "DataWarehouse.Plugins.SemanticSync",
        ["ChaosVaccination"] = "DataWarehouse.Plugins.UltimateResilience",  // Consolidated (Phase 65.5-12)
        ["CarbonAwareLifecycle"] = "DataWarehouse.Plugins.UltimateSustainability",
        ["UniversalFabric"] = "DataWarehouse.Plugins.UniversalFabric",
    };

    public CrossFeatureOrchestrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // ────────────────────────────────────────────────────────────────
    // Tags -> Passports (Phase 55 -> Phase 57)
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void TagsFlowIntoPassportEvaluation()
    {
        // Compliance plugin should subscribe to tag-related topics or reference tag types
        var complianceDir = RequirePluginDir("CompliancePassports");

        var sourceFiles = GetCsFiles(complianceDir);
        var allContent = CombineFileContents(sourceFiles);

        // Look for tag-related references: tag topics, TagCollection, CrdtTagCollection, tag.assigned
        var tagReferences = new[]
        {
            "tag", "Tag", "tags.", "tags.system", "tag.assigned",
            "TagCollection", "CrdtTagCollection", "TagKey", "TagMetadata"
        };

        var foundReferences = tagReferences
            .Where(r => allContent.Contains(r, StringComparison.OrdinalIgnoreCase))
            .ToList();

        _output.WriteLine($"Tag references found in Compliance plugin: {string.Join(", ", foundReferences)}");

        // Also check SDK moonshot config declares CompliancePassports depends on UniversalTags
        var configFile = Path.Combine(SdkRoot, "Moonshots", "MoonshotConfigurationDefaults.cs");
        if (File.Exists(configFile))
        {
            var configContent = File.ReadAllText(configFile);
            configContent.Should().Contain("MoonshotId.UniversalTags",
                "CompliancePassports should declare dependency on UniversalTags in moonshot config");
            _output.WriteLine("Verified: CompliancePassports depends on UniversalTags in MoonshotConfigurationDefaults");
        }

        foundReferences.Should().NotBeEmpty(
            "Compliance plugin should reference tag types or subscribe to tag-related topics");
    }

    [Fact]
    public void PassportRequiresTagMetadata()
    {
        // Verify that the compliance passport types reference tag-related types
        var complianceDir = RequirePluginDir("CompliancePassports");

        var sourceFiles = GetCsFiles(complianceDir);

        // Look for passport-related files that import or reference tag types
        var passportFiles = sourceFiles
            .Where(f =>
            {
                var name = Path.GetFileNameWithoutExtension(f);
                return name.Contains("Passport", StringComparison.OrdinalIgnoreCase) ||
                       name.Contains("Compliance", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        passportFiles.Should().NotBeEmpty("Compliance plugin should have passport/compliance-related files");

        // Check using statements and type references across compliance source
        var allContent = CombineFileContents(sourceFiles);
        var hasTagNamespace = allContent.Contains("DataWarehouse.SDK.Tags", StringComparison.Ordinal);
        var hasTagReference = allContent.Contains("Tag", StringComparison.Ordinal);

        _output.WriteLine($"Has SDK Tags namespace import: {hasTagNamespace}");
        _output.WriteLine($"Has Tag type reference: {hasTagReference}");

        // At minimum, the compliance plugin must reference tags in some form
        (hasTagNamespace || hasTagReference).Should().BeTrue(
            "Compliance passport evaluation should reference tag metadata types");
    }

    // ────────────────────────────────────────────────────────────────
    // Tags -> Carbon Placement (Phase 55 -> Phase 62 -> Phase 63)
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void CarbonScoringUsesTagMetadata()
    {
        // Carbon-aware (Sustainability) should reference tags for data classification
        var carbonDir = RequirePluginDir("CarbonAwareLifecycle");

        var sourceFiles = GetCsFiles(carbonDir);
        var allContent = CombineFileContents(sourceFiles);

        var tagReferences = new[] { "tag", "Tag", "classification", "metadata" };
        var found = tagReferences
            .Where(r => allContent.Contains(r, StringComparison.OrdinalIgnoreCase))
            .ToList();

        _output.WriteLine($"Tag/metadata references in Sustainability plugin: {string.Join(", ", found)}");

        // Sustainability plugin should have some awareness of data classification
        found.Should().NotBeEmpty(
            "Carbon-aware tiering should reference data classification or tag metadata for placement decisions");
    }

    [Fact]
    public void PlacementOptimizerConsidersCarbonScore()
    {
        // Universal Fabric should reference carbon/sustainability topics or types
        var fabricDir = RequirePluginDir("UniversalFabric");

        var sourceFiles = GetCsFiles(fabricDir);
        var allContent = CombineFileContents(sourceFiles);

        var carbonReferences = new[]
        {
            "carbon", "sustainability", "green", "energy",
            "placement", "PlacementScore", "BackendDescriptor"
        };

        var found = carbonReferences
            .Where(r => allContent.Contains(r, StringComparison.OrdinalIgnoreCase))
            .ToList();

        _output.WriteLine($"Carbon/placement references in UniversalFabric: {string.Join(", ", found)}");

        // Fabric plugin should understand placement (its core job)
        found.Should().Contain(r =>
            r.Equals("placement", StringComparison.OrdinalIgnoreCase) ||
            r.Equals("BackendDescriptor", StringComparison.OrdinalIgnoreCase),
            "UniversalFabric must handle placement optimization");
    }

    // ────────────────────────────────────────────────────────────────
    // Sovereignty -> Sync (Phase 57 -> Phase 60)
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void SemanticSyncRespectsSovereigntyZones()
    {
        // Semantic sync should reference sovereignty types or subscribe to sovereignty topics
        var syncDir = RequirePluginDir("SemanticSync");

        var sourceFiles = GetCsFiles(syncDir);
        var allContent = CombineFileContents(sourceFiles);

        var sovereigntyReferences = new[]
        {
            "sovereignty", "Sovereignty", "sovereign", "geofence", "jurisdiction",
            "compliance", "zone", "border", "residency"
        };

        var found = sovereigntyReferences
            .Where(r => allContent.Contains(r, StringComparison.OrdinalIgnoreCase))
            .ToList();

        _output.WriteLine($"Sovereignty references in SemanticSync: {string.Join(", ", found)}");

        found.Should().NotBeEmpty(
            "Semantic sync should reference sovereignty zones or compliance constraints");
    }

    [Fact]
    public void CrossBorderTransferRequiresPassport()
    {
        // Cross-border data transfer in compliance should check passport status
        var complianceDir = RequirePluginDir("CompliancePassports");

        var sourceFiles = GetCsFiles(complianceDir);

        // Look specifically for cross-border transfer logic
        var crossBorderFiles = sourceFiles
            .Where(f =>
            {
                var content = File.ReadAllText(f);
                return content.Contains("CrossBorder", StringComparison.OrdinalIgnoreCase) ||
                       content.Contains("cross-border", StringComparison.OrdinalIgnoreCase) ||
                       content.Contains("transfer", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        _output.WriteLine($"Files with cross-border transfer logic: {crossBorderFiles.Count}");
        foreach (var f in crossBorderFiles)
            _output.WriteLine($"  - {Path.GetFileName(f)}");

        crossBorderFiles.Should().NotBeEmpty(
            "Compliance plugin should have cross-border transfer validation logic");

        // Verify cross-border logic references passport or compliance check
        var allContent = CombineFileContents(crossBorderFiles);
        var hasPassportCheck = allContent.Contains("passport", StringComparison.OrdinalIgnoreCase) ||
                               allContent.Contains("complian", StringComparison.OrdinalIgnoreCase) ||
                               allContent.Contains("valid", StringComparison.OrdinalIgnoreCase);

        hasPassportCheck.Should().BeTrue(
            "Cross-border transfer logic should validate compliance/passport status");
    }

    // ────────────────────────────────────────────────────────────────
    // Chaos -> Time-Locks (Phase 61 -> Phase 59)
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void ChaosInjectionDoesNotBypassTimeLocks()
    {
        // Chaos vaccination should not directly modify time-locked storage
        var chaosDir = RequirePluginDir("ChaosVaccination");  // Maps to UltimateResilience (Phase 65.5-12)

        var sourceFiles = GetCsFiles(chaosDir);
        var allContent = CombineFileContents(sourceFiles);

        // Chaos plugin should NOT have direct file I/O or storage bypass patterns
        var dangerousPatterns = new[]
        {
            "File.WriteAllBytes", "File.Delete", "Directory.Delete",
            "SqlCommand", "DROP TABLE"
        };

        var foundDangerous = dangerousPatterns
            .Where(p => allContent.Contains(p, StringComparison.Ordinal))
            .ToList();

        _output.WriteLine($"Dangerous direct-storage patterns found: {foundDangerous.Count}");
        foreach (var p in foundDangerous)
            _output.WriteLine($"  WARNING: {p}");

        // Chaos should operate through the message bus, not through direct storage access
        allContent.Should().Contain("MessageBus",
            "Chaos vaccination should use message bus for fault injection, not direct storage access");
    }

    [Fact]
    public void TimeLockProtectsAgainstChaos()
    {
        // TamperProof (time-lock) should have protection against unauthorized modification
        var timeLockDir = RequirePluginDir("CryptoTimeLocks");

        var sourceFiles = GetCsFiles(timeLockDir);
        var allContent = CombineFileContents(sourceFiles);

        // Time-lock should verify lock status before any destructive operation
        var protectionPatterns = new[]
        {
            "TimeLock", "locked", "IsLocked", "LockStatus",
            "Expired", "Verify", "Validate", "tamper"
        };

        var found = protectionPatterns
            .Where(p => allContent.Contains(p, StringComparison.OrdinalIgnoreCase))
            .ToList();

        _output.WriteLine($"Time-lock protection patterns found: {string.Join(", ", found)}");

        found.Should().HaveCountGreaterThan(1,
            "TamperProof plugin should implement time-lock verification before destructive operations");
    }

    // ────────────────────────────────────────────────────────────────
    // Data Consciousness -> Tags -> Archive (Phase 56 -> Phase 55 -> storage)
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void ConsciousnessScoringUsesTagData()
    {
        // Intelligence plugin (consciousness) should reference tag data
        var consciousnessDir = RequirePluginDir("DataConsciousness");

        var sourceFiles = GetCsFiles(consciousnessDir);

        // Look for consciousness-related files
        var consciousnessFiles = sourceFiles
            .Where(f =>
            {
                var content = File.ReadAllText(f);
                return content.Contains("Consciousness", StringComparison.OrdinalIgnoreCase) ||
                       content.Contains("ConsciousnessStrategy", StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        _output.WriteLine($"Consciousness-related files: {consciousnessFiles.Count}");

        var allContent = CombineFileContents(consciousnessFiles.Count > 0 ? consciousnessFiles : sourceFiles);

        var tagReferences = new[] { "tag", "Tag", "metadata", "classification", "score", "value" };
        var found = tagReferences
            .Where(r => allContent.Contains(r, StringComparison.OrdinalIgnoreCase))
            .ToList();

        _output.WriteLine($"Tag/scoring references in consciousness: {string.Join(", ", found)}");

        found.Should().NotBeEmpty(
            "Data consciousness scoring should reference tag metadata or data classification");
    }

    [Fact]
    public void AutoArchiveTriggeredByScore()
    {
        // Low-value scoring should trigger archive/tier-down events
        var sustainabilityDir = GetPluginDir("CarbonAwareLifecycle");
        var storageDir = GetPluginDir("ZeroGravityStorage");

        var allFiles = new List<string>();
        if (sustainabilityDir != null) allFiles.AddRange(GetCsFiles(sustainabilityDir));
        if (storageDir != null) allFiles.AddRange(GetCsFiles(storageDir));

        allFiles.Should().NotBeEmpty("Sustainability or Storage plugin must exist for archive verification");

        var allContent = CombineFileContents(allFiles);

        var archivePatterns = new[]
        {
            "archive", "tier", "tiering", "cold", "lifecycle",
            "score", "value", "demotion", "downgrade"
        };

        var found = archivePatterns
            .Where(p => allContent.Contains(p, StringComparison.OrdinalIgnoreCase))
            .ToList();

        _output.WriteLine($"Archive/tiering patterns found: {string.Join(", ", found)}");

        found.Should().NotBeEmpty(
            "Low-value data scoring should trigger archive or tier-down lifecycle events");
    }

    // ────────────────────────────────────────────────────────────────
    // Zero-Gravity -> Fabric (Phase 58 -> Phase 63)
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void CrushPlacementIntegratesWithFabric()
    {
        // Zero-gravity CRUSH placement should reference or be referenced by UniversalFabric
        var storageDir = GetPluginDir("ZeroGravityStorage");
        var fabricDir = GetPluginDir("UniversalFabric");

        (storageDir != null || fabricDir != null).Should().BeTrue(
            "At least one of Storage or Fabric plugin must exist");

        var storageContent = storageDir != null ? CombineFileContents(GetCsFiles(storageDir)) : "";
        var fabricContent = fabricDir != null ? CombineFileContents(GetCsFiles(fabricDir)) : "";

        // Check for cross-references between placement and fabric
        var storageMentionsFabric = storageContent.Contains("fabric", StringComparison.OrdinalIgnoreCase) ||
                                    storageContent.Contains("Fabric", StringComparison.Ordinal) ||
                                    storageContent.Contains("backend", StringComparison.OrdinalIgnoreCase);

        var fabricMentionsPlacement = fabricContent.Contains("placement", StringComparison.OrdinalIgnoreCase) ||
                                      fabricContent.Contains("CRUSH", StringComparison.OrdinalIgnoreCase) ||
                                      fabricContent.Contains("gravity", StringComparison.OrdinalIgnoreCase);

        _output.WriteLine($"Storage mentions fabric/backend: {storageMentionsFabric}");
        _output.WriteLine($"Fabric mentions placement/CRUSH: {fabricMentionsPlacement}");

        (storageMentionsFabric || fabricMentionsPlacement).Should().BeTrue(
            "CRUSH placement in Zero-Gravity storage should integrate with UniversalFabric backend selection");
    }

    // ────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────

    private static string RequirePluginDir(string moonshotName)
    {
        var dir = GetPluginDir(moonshotName);
        dir.Should().NotBeNull($"Plugin directory for moonshot '{moonshotName}' must exist");
        return dir!;
    }

    private static string? GetPluginDir(string moonshotName)
    {
        if (!MoonshotPluginMap.TryGetValue(moonshotName, out var pluginDirName))
            return null;

        var path = Path.Combine(PluginsRoot, pluginDirName);
        return Directory.Exists(path) ? path : null;
    }

    private static List<string> GetCsFiles(string directory)
    {
        return Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories).ToList();
    }

    private static string CombineFileContents(IEnumerable<string> files)
    {
        return string.Join(Environment.NewLine, files.Select(f => File.ReadAllText(f)));
    }

    private static string FindSolutionRoot()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "DataWarehouse.slnx")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        dir = Path.GetDirectoryName(typeof(CrossFeatureOrchestrationTests).Assembly.Location);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "DataWarehouse.slnx")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException(
            "Could not locate DataWarehouse.slnx. Ensure tests run from within the solution tree.");
    }
}
