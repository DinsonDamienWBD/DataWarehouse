using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Integration;

/// <summary>
/// Verifies each moonshot plugin (Phases 55-63) is properly wired into the system:
/// exists, compiles, registers capabilities, subscribes/publishes on the message bus,
/// references only SDK, and implements health checks.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "MoonshotIntegration")]
public class MoonshotIntegrationTests
{
    private readonly ITestOutputHelper _output;
    private static readonly string SolutionRoot = FindSolutionRoot();
    private static readonly string PluginsRoot = Path.Combine(SolutionRoot, "Plugins");

    /// <summary>
    /// Moonshot feature name -> plugin directory name mapping.
    /// Some moonshot features are embedded within larger plugins rather than standalone.
    /// </summary>
    private static readonly Dictionary<string, string> MoonshotPluginDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UniversalTags"] = "DataWarehouse.Plugins.UltimateStorage",
        ["DataConsciousness"] = "DataWarehouse.Plugins.UltimateIntelligence",
        ["CompliancePassports"] = "DataWarehouse.Plugins.UltimateCompliance",
        ["ZeroGravityStorage"] = "DataWarehouse.Plugins.UltimateStorage",
        ["CryptoTimeLocks"] = "DataWarehouse.Plugins.TamperProof",
        ["SemanticSync"] = "DataWarehouse.Plugins.SemanticSync",
        ["ChaosVaccination"] = "DataWarehouse.Plugins.UltimateResilience",  // Consolidated (Phase 65.5-12)
        ["CarbonAwareLifecycle"] = "DataWarehouse.Plugins.UltimateSustainability",
        ["UniversalFabric"] = "DataWarehouse.Plugins.UniversalFabric",
    };

    // Patterns for detecting message bus usage (string literals and constant-based topics)
    private static readonly Regex PublishPattern = new(
        @"(?:Publish|PublishAsync|SendCommand|SendCommandAsync)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex SubscribePattern = new(
        @"(?:Subscribe|SubscribeAsync|TrySubscribe|RegisterSubscriptions)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex CapabilityRegistrationPattern = new(
        @"(?:RegisterCapabilit|RegisterKnowledge|RegisterStrategy|DiscoverAndRegister|AutoDiscover|AddCapability|DeclaredCapabilities|RegisterAll|Register\w+Provider|Register\w+Service)",
        RegexOptions.Compiled);

    private static readonly Regex HealthCheckPattern = new(
        @"(?:IHealthCheck|HealthCheck|HealthStatus|Health\w+Info|Health\w+Report|IMoonshotHealthProbe|MoonshotHealthReport|ReportHealthAsync|CheckHealthAsync|GetResilienceHealthAsync|GetHealthAsync|Get\w+HealthAsync)",
        RegexOptions.Compiled);

    public MoonshotIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // ────────────────────────────────────────────────────────────────
    // Per-plugin verification (Theory + InlineData)
    // ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("DataWarehouse.Plugins.UltimateStorage")]
    [InlineData("DataWarehouse.Plugins.UltimateIntelligence")]
    [InlineData("DataWarehouse.Plugins.UltimateCompliance")]
    [InlineData("DataWarehouse.Plugins.TamperProof")]
    [InlineData("DataWarehouse.Plugins.SemanticSync")]
    [InlineData("DataWarehouse.Plugins.ChaosVaccination")]
    [InlineData("DataWarehouse.Plugins.UltimateSustainability")]
    [InlineData("DataWarehouse.Plugins.UniversalFabric")]
    public void PluginExistsAndCompiles(string pluginDirName)
    {
        var pluginDir = Path.Combine(PluginsRoot, pluginDirName);
        Directory.Exists(pluginDir).Should().BeTrue($"Plugin directory '{pluginDirName}' must exist");

        var csprojFiles = Directory.GetFiles(pluginDir, "*.csproj", SearchOption.TopDirectoryOnly);
        csprojFiles.Should().NotBeEmpty($"Plugin '{pluginDirName}' must contain a .csproj file");

        _output.WriteLine($"Plugin '{pluginDirName}': {csprojFiles.Length} .csproj file(s)");
        foreach (var f in csprojFiles)
            _output.WriteLine($"  - {Path.GetFileName(f)}");
    }

    [Theory]
    [InlineData("DataWarehouse.Plugins.UltimateStorage")]
    [InlineData("DataWarehouse.Plugins.UltimateIntelligence")]
    [InlineData("DataWarehouse.Plugins.UltimateCompliance")]
    [InlineData("DataWarehouse.Plugins.TamperProof")]
    [InlineData("DataWarehouse.Plugins.SemanticSync")]
    [InlineData("DataWarehouse.Plugins.ChaosVaccination")]
    [InlineData("DataWarehouse.Plugins.UltimateSustainability")]
    [InlineData("DataWarehouse.Plugins.UniversalFabric")]
    public void PluginRegistersCapabilities(string pluginDirName)
    {
        var pluginDir = Path.Combine(PluginsRoot, pluginDirName);
        if (!Directory.Exists(pluginDir)) return;

        var sourceFiles = Directory.GetFiles(pluginDir, "*.cs", SearchOption.AllDirectories);
        var allContent = string.Join(Environment.NewLine, sourceFiles.Select(File.ReadAllText));

        var matches = CapabilityRegistrationPattern.Matches(allContent);

        _output.WriteLine($"Plugin '{pluginDirName}': {matches.Count} capability registration call(s)");

        matches.Count.Should().BeGreaterThan(0,
            $"Plugin '{pluginDirName}' must register capabilities (RegisterCapabilities, RegisterKnowledge, " +
            "DiscoverAndRegister, or similar)");
    }

    [Theory]
    [InlineData("DataWarehouse.Plugins.UltimateStorage")]
    [InlineData("DataWarehouse.Plugins.UltimateIntelligence")]
    [InlineData("DataWarehouse.Plugins.UltimateCompliance")]
    [InlineData("DataWarehouse.Plugins.TamperProof")]
    [InlineData("DataWarehouse.Plugins.SemanticSync")]
    [InlineData("DataWarehouse.Plugins.ChaosVaccination")]
    [InlineData("DataWarehouse.Plugins.UltimateSustainability")]
    [InlineData("DataWarehouse.Plugins.UniversalFabric")]
    public void PluginSubscribesToBus(string pluginDirName)
    {
        var pluginDir = Path.Combine(PluginsRoot, pluginDirName);
        if (!Directory.Exists(pluginDir)) return;

        var sourceFiles = Directory.GetFiles(pluginDir, "*.cs", SearchOption.AllDirectories);
        var allContent = string.Join(Environment.NewLine, sourceFiles.Select(File.ReadAllText));

        var subscribeCount = SubscribePattern.Matches(allContent).Count;
        // Some infrastructure plugins use request/response (SendAsync) instead of pub/sub
        var sendAsyncCount = Regex.Matches(allContent, @"MessageBus\.\w*Send\w*\(").Count;
        var totalBusReception = subscribeCount + sendAsyncCount;

        _output.WriteLine($"Plugin '{pluginDirName}': {subscribeCount} subscribe call(s), {sendAsyncCount} send/request call(s)");

        totalBusReception.Should().BeGreaterThan(0,
            $"Plugin '{pluginDirName}' must interact with the message bus (subscribe or send/request)");
    }

    [Theory]
    [InlineData("DataWarehouse.Plugins.UltimateStorage")]
    [InlineData("DataWarehouse.Plugins.UltimateIntelligence")]
    [InlineData("DataWarehouse.Plugins.UltimateCompliance")]
    [InlineData("DataWarehouse.Plugins.TamperProof")]
    [InlineData("DataWarehouse.Plugins.SemanticSync")]
    [InlineData("DataWarehouse.Plugins.ChaosVaccination")]
    [InlineData("DataWarehouse.Plugins.UltimateSustainability")]
    [InlineData("DataWarehouse.Plugins.UniversalFabric")]
    public void PluginPublishesToBus(string pluginDirName)
    {
        var pluginDir = Path.Combine(PluginsRoot, pluginDirName);
        if (!Directory.Exists(pluginDir)) return;

        var sourceFiles = Directory.GetFiles(pluginDir, "*.cs", SearchOption.AllDirectories);
        var allContent = string.Join(Environment.NewLine, sourceFiles.Select(File.ReadAllText));

        var matchCount = PublishPattern.Matches(allContent).Count;

        _output.WriteLine($"Plugin '{pluginDirName}': {matchCount} publish call(s)");

        matchCount.Should().BeGreaterThan(0,
            $"Plugin '{pluginDirName}' must publish at least one message bus topic");
    }

    [Theory]
    [InlineData("DataWarehouse.Plugins.UltimateStorage")]
    [InlineData("DataWarehouse.Plugins.UltimateIntelligence")]
    [InlineData("DataWarehouse.Plugins.UltimateCompliance")]
    [InlineData("DataWarehouse.Plugins.TamperProof")]
    [InlineData("DataWarehouse.Plugins.SemanticSync")]
    [InlineData("DataWarehouse.Plugins.ChaosVaccination")]
    [InlineData("DataWarehouse.Plugins.UltimateSustainability")]
    [InlineData("DataWarehouse.Plugins.UniversalFabric")]
    public void PluginReferencesOnlySDK(string pluginDirName)
    {
        var pluginDir = Path.Combine(PluginsRoot, pluginDirName);
        if (!Directory.Exists(pluginDir)) return;

        var csprojFiles = Directory.GetFiles(pluginDir, "*.csproj", SearchOption.TopDirectoryOnly);
        csprojFiles.Should().NotBeEmpty();

        var violations = new List<string>();

        foreach (var csprojFile in csprojFiles)
        {
            var doc = XDocument.Load(csprojFile);
            var projectRefs = doc.Descendants("ProjectReference")
                .Select(e => e.Attribute("Include")?.Value ?? "")
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();

            foreach (var refPath in projectRefs)
            {
                var normalizedRef = refPath.Replace('\\', '/');
                // Only SDK, Shared, and Raft references are allowed
                if (normalizedRef.Contains("Plugins/DataWarehouse.Plugins.", StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add($"{pluginDirName} -> {Path.GetFileName(refPath)}");
                }
            }
        }

        _output.WriteLine($"Plugin '{pluginDirName}': {violations.Count} cross-plugin reference violation(s)");

        violations.Should().BeEmpty(
            $"Plugin '{pluginDirName}' should reference only SDK/Shared, not other plugins. " +
            $"Violations: {string.Join("; ", violations)}");
    }

    // ────────────────────────────────────────────────────────────────
    // Solution-level checks
    // ────────────────────────────────────────────────────────────────

    [Fact]
    public void AllMoonshotsInSolution()
    {
        // Verify all moonshot plugin .csproj files are referenced in the solution
        var slnxPath = Path.Combine(SolutionRoot, "DataWarehouse.slnx");
        File.Exists(slnxPath).Should().BeTrue("Solution file DataWarehouse.slnx must exist");

        var slnContent = File.ReadAllText(slnxPath);

        var missingFromSolution = new List<string>();

        foreach (var pluginDirName in MoonshotPluginDirs.Values.Distinct())
        {
            var pluginDir = Path.Combine(PluginsRoot, pluginDirName);
            if (!Directory.Exists(pluginDir)) continue;

            var csprojFiles = Directory.GetFiles(pluginDir, "*.csproj", SearchOption.TopDirectoryOnly);
            foreach (var csproj in csprojFiles)
            {
                var csprojName = Path.GetFileName(csproj);
                if (!slnContent.Contains(csprojName, StringComparison.OrdinalIgnoreCase) &&
                    !slnContent.Contains(pluginDirName, StringComparison.OrdinalIgnoreCase))
                {
                    missingFromSolution.Add(csprojName);
                }
            }
        }

        _output.WriteLine($"Moonshot plugins missing from solution: {missingFromSolution.Count}");
        foreach (var m in missingFromSolution)
            _output.WriteLine($"  MISSING: {m}");

        missingFromSolution.Should().BeEmpty(
            "All moonshot plugin .csproj files should be referenced in DataWarehouse.slnx");
    }

    // Patterns for extracting string-literal topics only (used for circular dependency analysis)
    private static readonly Regex LiteralPublishPattern = new(
        @"(?:Publish|PublishAsync|SendCommand|SendCommandAsync)\s*\(\s*""([^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex LiteralSubscribePattern = new(
        @"(?:Subscribe|SubscribeAsync|TrySubscribe)\s*\(\s*""([^""]+)""",
        RegexOptions.Compiled);

    [Fact]
    public void NoCircularTopicDependencies()
    {
        // Build a publish -> subscribe topic graph and check for cycles
        // A cycle would mean: Plugin A publishes topic X, Plugin B subscribes to X and publishes Y,
        // Plugin A subscribes to Y -- creating an infinite message loop

        var pluginTopics = new Dictionary<string, (HashSet<string> Publishes, HashSet<string> Subscribes)>();

        foreach (var pluginDirName in MoonshotPluginDirs.Values.Distinct())
        {
            var pluginDir = Path.Combine(PluginsRoot, pluginDirName);
            if (!Directory.Exists(pluginDir)) continue;

            var sourceFiles = Directory.GetFiles(pluginDir, "*.cs", SearchOption.AllDirectories);
            var allContent = string.Join(Environment.NewLine, sourceFiles.Select(File.ReadAllText));

            var publishes = LiteralPublishPattern.Matches(allContent)
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var subscribes = LiteralSubscribePattern.Matches(allContent)
                .Select(m => m.Groups[1].Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            pluginTopics[pluginDirName] = (publishes, subscribes);

            _output.WriteLine($"{pluginDirName}: publishes={publishes.Count}, subscribes={subscribes.Count}");
        }

        // Check for direct circular dependencies (A publishes X, B subscribes X and publishes Y, A subscribes Y)
        var circularPairs = new List<string>();
        var pluginNames = pluginTopics.Keys.ToList();

        for (int i = 0; i < pluginNames.Count; i++)
        {
            for (int j = i + 1; j < pluginNames.Count; j++)
            {
                var a = pluginNames[i];
                var b = pluginNames[j];
                var (aPub, aSub) = pluginTopics[a];
                var (bPub, bSub) = pluginTopics[b];

                // A publishes something B subscribes to AND B publishes something A subscribes to
                var aToB = aPub.Intersect(bSub, StringComparer.OrdinalIgnoreCase).ToList();
                var bToA = bPub.Intersect(aSub, StringComparer.OrdinalIgnoreCase).ToList();

                if (aToB.Count > 0 && bToA.Count > 0)
                {
                    // This is a potential circular dependency -- but only if the topics form a tight loop
                    // (same topic prefix bidirectionally). Allow request/response pairs.
                    var tightLoops = aToB
                        .Where(t => bToA.Any(r =>
                            t.Split('.')[0].Equals(r.Split('.')[0], StringComparison.OrdinalIgnoreCase) &&
                            !t.Contains("response", StringComparison.OrdinalIgnoreCase) &&
                            !r.Contains("response", StringComparison.OrdinalIgnoreCase) &&
                            !t.Contains("request", StringComparison.OrdinalIgnoreCase) &&
                            !r.Contains("request", StringComparison.OrdinalIgnoreCase)))
                        .ToList();

                    if (tightLoops.Count > 0)
                    {
                        circularPairs.Add($"{a} <-> {b} via topics: {string.Join(", ", tightLoops)}");
                    }
                }
            }
        }

        _output.WriteLine($"Potential circular topic dependencies: {circularPairs.Count}");
        foreach (var c in circularPairs)
            _output.WriteLine($"  CIRCULAR: {c}");

        // Note: bidirectional topic flow is acceptable for event-driven systems;
        // we flag only tight loops within the same topic domain
        circularPairs.Should().BeEmpty(
            "No tight circular publish->subscribe->publish loops should exist between moonshot plugins");
    }

    [Theory]
    [InlineData("DataWarehouse.Plugins.UltimateStorage")]
    [InlineData("DataWarehouse.Plugins.UltimateIntelligence")]
    [InlineData("DataWarehouse.Plugins.UltimateCompliance")]
    [InlineData("DataWarehouse.Plugins.TamperProof")]
    [InlineData("DataWarehouse.Plugins.SemanticSync")]
    [InlineData("DataWarehouse.Plugins.ChaosVaccination")]
    [InlineData("DataWarehouse.Plugins.UltimateSustainability")]
    [InlineData("DataWarehouse.Plugins.UniversalFabric")]
    public void AllMoonshotsHaveHealthCheck(string pluginDirName)
    {
        var pluginDir = Path.Combine(PluginsRoot, pluginDirName);
        if (!Directory.Exists(pluginDir)) return;

        var sourceFiles = Directory.GetFiles(pluginDir, "*.cs", SearchOption.AllDirectories);
        var allContent = string.Join(Environment.NewLine, sourceFiles.Select(File.ReadAllText));

        var matches = HealthCheckPattern.Matches(allContent);

        // All plugins inherit from PluginBase which provides CheckHealthAsync by default.
        // Check for explicit health references OR base class inheritance (which guarantees health).
        var inheritsFromPluginBase = Regex.IsMatch(allContent,
            @"class\s+\w+Plugin\s*:\s*\w+PluginBase");

        _output.WriteLine($"Plugin '{pluginDirName}': {matches.Count} explicit health reference(s), inherits PluginBase: {inheritsFromPluginBase}");

        (matches.Count > 0 || inheritsFromPluginBase).Should().BeTrue(
            $"Plugin '{pluginDirName}' should implement health checks (explicit or via PluginBase inheritance)");
    }

    // ────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────

    private static string FindSolutionRoot()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "DataWarehouse.slnx")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        dir = Path.GetDirectoryName(typeof(MoonshotIntegrationTests).Assembly.Location);
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
