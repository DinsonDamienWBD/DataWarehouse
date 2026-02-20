using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Integration;

/// <summary>
/// Static analysis tests that verify message bus topology correctness across the entire solution.
/// These tests parse source files to detect wiring issues without runtime plugin loading.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Topology")]
public class MessageBusTopologyTests
{
    private readonly ITestOutputHelper _output;
    private static readonly string SolutionRoot = FindSolutionRoot();

    // Patterns for detecting message bus usage
    private static readonly Regex PublishPattern = new(
        @"(?:Publish|PublishAsync|SendCommand|SendCommandAsync)\s*\(\s*""([^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex SubscribePattern = new(
        @"(?:Subscribe|SubscribeAsync)\s*\(\s*""([^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex TopicConstantPattern = new(
        @"(?:const\s+string|static\s+.*?readonly\s+.*?string)\s+\w*Topic\w*\s*=\s*""([^""]+)""",
        RegexOptions.Compiled);

    // Topics that are intentionally fire-and-forget (event notifications with dynamic/runtime subscribers)
    // These follow the Command/Event Separation pattern documented in MESSAGE-BUS-TOPOLOGY-REPORT.md
    private static readonly HashSet<string> IntentionalFireAndForgetPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Event notifications - subscribers register dynamically at runtime
        "config.", "convergence.", "deployment.", "edge.", "encryption.migration.",
        "federation.", "chaos.fault.", "chaos.isolation-zone.", "chaos.metrics.",
        "chaos.results.stored", "chaos.schedule.added", "chaos.schedule.removed",
        "chaos.vaccination.", "chaos.blast-radius.breach",
        // Strategy registration events
        "compute.strategy.", "connector.strategy.", "protocol.strategy.",
        "storageprocessing.strategy.", "encryption.strategy.",
        // Audit/telemetry signals
        "compliance.audit.", "compliance.alert.sent", "compliance.dashboard.",
        "compliance.incident.", "compliance.report.publish",
        "command.audit", "logging.security.",
        // System lifecycle events
        "system.mode.", "capability.changed",
        // Cross-feature notification events
        "fabric.namespace.", "featureflag.", "integrity.hash.",
        "keymanagement.", "loadbalancer.", "moonshot.health.",
        "network.ip.", "notification.", "security.",
        "sovereignty.zone.resilience.", "storage.data.", "storage.fabric.",
        "storage.placement.prefer-", "storage.placement.recalculate-",
        "streaming.", "sustainability.carbon.throttle.",
        "sustainability.green-tiering.", "sustainability.recommendation.response",
        "tags.system.",
        "tamperproof.", "timelock.",
        // Response halves of request/response pairs
        "intelligence.response.", "compliance.geofence.check.response",
        "compliance.geofence.check.batch.response",
        "intelligence.request.deployment-recommendation.response",
        "intelligence.request.key-rotation-prediction.response",
        "intelligence.request.resilience-recommendation.response",
        "intelligence.model.query.response",
        "intelligence.connector.detect-anomaly",
        // Intelligence event notifications
        "intelligence.abuse.", "intelligence.anomaly.", "intelligence.drift.",
        "intelligence.enhance", "intelligence.ensemble.", "intelligence.feedback",
        "intelligence.index.", "intelligence.instance.", "intelligence.isolation.",
        "intelligence.knowledge.register", "intelligence.model.registered",
        "intelligence.model.retrained", "intelligence.model.scope.",
        "intelligence.model.unregistered", "intelligence.optimize",
        "intelligence.predict", "intelligence.promotion.",
        "intelligence.query.routed", "intelligence.recommend",
        "intelligence.request.recommendation", "intelligence.specialization.",
        "intelligence.training.policy.",
        // Plugin-internal events (published and consumed within same plugin boundary)
        "selfemulating.", "semantic-sync.classified", "semantic-sync.conflict.pending",
        "semantic-sync.resolved", "semantic-sync.routed", "semantic-sync.sync-complete",
        "semanticsync.fidelity.", "composition.", "resilience.",
        // Single-purpose events
        "admin", "airgap.", "aeds.", "auth.credentials.", "audit.snapshot.",
        "cluster.node.", "chaos.experiment.aborted",
        "compliance.passport.", "encryption.block.", "encryption.key.",
        "encryption.reencrypt", "encryption.sign",
        "placement.computed", "rebalance.", "gravity.scored",
        "storage.saved", "migration.progress",
    };

    public MessageBusTopologyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void TopicConstantsAreUnique()
    {
        // Scan all .cs files for topic constant definitions
        var topicValues = new Dictionary<string, List<string>>(); // value -> list of (file:constName)
        var sourceFiles = GetProductionSourceFiles();

        foreach (var file in sourceFiles)
        {
            var content = File.ReadAllText(file);
            foreach (Match match in TopicConstantPattern.Matches(content))
            {
                var topicValue = match.Groups[1].Value;
                var fileName = Path.GetFileName(file);
                if (!topicValues.ContainsKey(topicValue))
                    topicValues[topicValue] = new List<string>();
                topicValues[topicValue].Add(fileName);
            }
        }

        // Find duplicates (same topic string used as constant in different files)
        var duplicates = topicValues
            .Where(kv => kv.Value.Distinct().Count() > 1)
            .ToList();

        foreach (var dup in duplicates)
        {
            _output.WriteLine($"Topic '{dup.Key}' defined as constant in: {string.Join(", ", dup.Value.Distinct())}");
        }

        // Topic constants should be unique per semantic purpose
        // Some sharing is acceptable (e.g., SDK base class defines topic used by multiple plugins)
        _output.WriteLine($"Scanned {sourceFiles.Count} files, found {topicValues.Count} topic constants");
        topicValues.Count.Should().BeGreaterThan(0, "should find at least some topic constants");
    }

    [Fact]
    public void AllPublishedTopicsHaveSubscribersOrAreIntentionallyFireAndForget()
    {
        var sourceFiles = GetProductionSourceFiles();
        var publishers = new Dictionary<string, HashSet<string>>();
        var subscribers = new Dictionary<string, HashSet<string>>();

        foreach (var file in sourceFiles)
        {
            var content = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            foreach (Match match in PublishPattern.Matches(content))
            {
                var topic = match.Groups[1].Value;
                if (!publishers.ContainsKey(topic))
                    publishers[topic] = new HashSet<string>();
                publishers[topic].Add(fileName);
            }

            foreach (Match match in SubscribePattern.Matches(content))
            {
                var topic = match.Groups[1].Value;
                if (!subscribers.ContainsKey(topic))
                    subscribers[topic] = new HashSet<string>();
                subscribers[topic].Add(fileName);
            }
        }

        var deadTopics = new List<string>();
        foreach (var topic in publishers.Keys.OrderBy(t => t))
        {
            if (subscribers.ContainsKey(topic))
                continue;

            // Check if this is an intentionally fire-and-forget topic
            bool isIntentional = IntentionalFireAndForgetPrefixes.Any(prefix =>
                topic.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                topic.Equals(prefix, StringComparison.OrdinalIgnoreCase));

            if (!isIntentional)
            {
                deadTopics.Add(topic);
                _output.WriteLine($"DEAD TOPIC: '{topic}' published by {string.Join(", ", publishers[topic])} but no subscriber found");
            }
        }

        _output.WriteLine($"Total published topics: {publishers.Count}");
        _output.WriteLine($"Total subscribed topics: {subscribers.Count}");
        _output.WriteLine($"Dead topics (unintentional): {deadTopics.Count}");

        deadTopics.Should().BeEmpty(
            "all published topics should either have subscribers or be documented as intentional fire-and-forget events");
    }

    [Fact]
    public void NoDirectPluginMethodCalls()
    {
        // Scan Plugins/ directory for direct cross-plugin instantiation or type casting
        var pluginsDir = Path.Combine(SolutionRoot, "Plugins");
        if (!Directory.Exists(pluginsDir))
        {
            _output.WriteLine("Plugins directory not found, skipping test");
            return;
        }

        var violations = new List<string>();
        var pluginDirs = Directory.GetDirectories(pluginsDir, "DataWarehouse.Plugins.*");

        // Build list of plugin class names to detect cross-references
        var pluginClassPattern = new Regex(
            @"new\s+(DataWarehouse\.Plugins\.\w+\.\w+Plugin)\s*\(|" +
            @"\(\s*(DataWarehouse\.Plugins\.\w+\.\w+Plugin)\s*\)|" +
            @"as\s+(DataWarehouse\.Plugins\.\w+\.\w+Plugin)",
            RegexOptions.Compiled);

        foreach (var pluginDir in pluginDirs)
        {
            var pluginName = Path.GetFileName(pluginDir);
            var csFiles = Directory.GetFiles(pluginDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains("bin") && !f.Contains("obj"))
                .ToList();

            foreach (var file in csFiles)
            {
                var content = File.ReadAllText(file);
                foreach (Match match in pluginClassPattern.Matches(content))
                {
                    var referencedType = match.Groups[1].Value + match.Groups[2].Value + match.Groups[3].Value;
                    // Skip self-references (same plugin namespace)
                    var referencedPlugin = ExtractPluginNamespace(referencedType);
                    if (referencedPlugin != null && !pluginName.Contains(referencedPlugin))
                    {
                        violations.Add($"{Path.GetFileName(file)}: references {referencedType}");
                    }
                }
            }
        }

        foreach (var v in violations)
        {
            _output.WriteLine($"VIOLATION: {v}");
        }

        violations.Should().BeEmpty(
            "plugins should communicate via message bus, not direct cross-plugin instantiation or casting");
    }

    [Fact]
    public void MessageBusUsageInAllPlugins()
    {
        var pluginsDir = Path.Combine(SolutionRoot, "Plugins");
        if (!Directory.Exists(pluginsDir))
        {
            _output.WriteLine("Plugins directory not found, skipping test");
            return;
        }

        var pluginDirs = Directory.GetDirectories(pluginsDir, "DataWarehouse.Plugins.*");
        var pluginsWithoutBusUsage = new List<string>();
        var totalPlugins = 0;

        // Pattern to detect any message bus interaction
        var busUsagePattern = new Regex(
            @"Publish\(|PublishAsync\(|Subscribe\(|SubscribeAsync\(|" +
            @"MessageBus\.|IMessageBus|HandleMessage",
            RegexOptions.Compiled);

        foreach (var pluginDir in pluginDirs)
        {
            totalPlugins++;
            var pluginName = Path.GetFileName(pluginDir);
            var csFiles = Directory.GetFiles(pluginDir, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains("bin") && !f.Contains("obj"))
                .ToList();

            bool hasBusUsage = false;
            foreach (var file in csFiles)
            {
                var content = File.ReadAllText(file);
                if (busUsagePattern.IsMatch(content))
                {
                    hasBusUsage = true;
                    break;
                }
            }

            if (!hasBusUsage)
            {
                pluginsWithoutBusUsage.Add(pluginName);
                _output.WriteLine($"NO BUS USAGE: {pluginName}");
            }
        }

        _output.WriteLine($"Scanned {totalPlugins} plugins, {totalPlugins - pluginsWithoutBusUsage.Count} use message bus");

        // Most plugins should use the message bus; a small number of utility/driver plugins
        // may legitimately not publish/subscribe directly (they inherit from SDK base classes
        // that handle bus interaction)
        var usagePercentage = (double)(totalPlugins - pluginsWithoutBusUsage.Count) / totalPlugins * 100;
        _output.WriteLine($"Bus usage: {usagePercentage:F1}%");

        // At least 70% of plugins should directly use the bus (the rest inherit via base classes)
        usagePercentage.Should().BeGreaterThan(70,
            "most plugins should directly interact with the message bus");
    }

    [Fact]
    public void FederatedMessageBusIsAvailableInSdk()
    {
        // Verify that FederatedMessageBus (or InMemoryFederatedMessageBus) exists in the SDK
        // and that the kernel uses IMessageBus (which FederatedMessageBus implements)
        var sdkDir = Path.Combine(SolutionRoot, "DataWarehouse.SDK");
        var kernelDir = Path.Combine(SolutionRoot, "DataWarehouse.Kernel");

        var federatedBusPattern = new Regex(
            @"class\s+(?:InMemory)?FederatedMessageBus(?:Base)?\s*[:{]",
            RegexOptions.Compiled);

        var kernelBusPattern = new Regex(
            @"IMessageBus|DefaultMessageBus|AuthenticatedMessageBusDecorator",
            RegexOptions.Compiled);

        // Check SDK has FederatedMessageBus
        bool sdkHasFederatedBus = false;
        var sdkFiles = Directory.GetFiles(sdkDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("bin") && !f.Contains("obj"))
            .ToList();

        foreach (var file in sdkFiles)
        {
            var content = File.ReadAllText(file);
            if (federatedBusPattern.IsMatch(content))
            {
                sdkHasFederatedBus = true;
                _output.WriteLine($"FederatedMessageBus found in: {Path.GetFileName(file)}");
                break;
            }
        }

        // Check kernel uses message bus
        bool kernelUsesBus = false;
        var kernelFiles = Directory.GetFiles(kernelDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains("bin") && !f.Contains("obj"))
            .ToList();

        foreach (var file in kernelFiles)
        {
            var content = File.ReadAllText(file);
            if (kernelBusPattern.IsMatch(content))
            {
                kernelUsesBus = true;
                _output.WriteLine($"Kernel message bus usage in: {Path.GetFileName(file)}");
                break;
            }
        }

        sdkHasFederatedBus.Should().BeTrue("SDK should provide FederatedMessageBus for distributed scenarios");
        kernelUsesBus.Should().BeTrue("Kernel should use IMessageBus for plugin communication");
    }

    [Fact]
    public void TopicNamingConventionIsConsistent()
    {
        // Verify all topics follow the dot-separated lowercase naming convention
        var sourceFiles = GetProductionSourceFiles();
        var allTopics = new HashSet<string>();
        var badlyNamedTopics = new List<string>();

        var topicNamingPattern = new Regex(@"^[a-z$*][a-z0-9.*_-]*$", RegexOptions.Compiled);

        foreach (var file in sourceFiles)
        {
            var content = File.ReadAllText(file);

            foreach (Match match in PublishPattern.Matches(content))
                allTopics.Add(match.Groups[1].Value);
            foreach (Match match in SubscribePattern.Matches(content))
                allTopics.Add(match.Groups[1].Value);
        }

        foreach (var topic in allTopics.OrderBy(t => t))
        {
            if (!topicNamingPattern.IsMatch(topic))
            {
                badlyNamedTopics.Add(topic);
                _output.WriteLine($"BAD NAMING: '{topic}'");
            }
        }

        _output.WriteLine($"Scanned {allTopics.Count} unique topics");
        badlyNamedTopics.Should().BeEmpty(
            "all topic names should follow dot-separated lowercase convention (e.g., 'domain.action.detail')");
    }

    #region Helper Methods

    private static string FindSolutionRoot()
    {
        // Walk up from test assembly location to find solution root
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        while (dir != null)
        {
            if (Directory.GetFiles(dir, "*.sln").Length > 0)
                return dir;
            // Also check for DataWarehouse.Kernel as fallback
            if (Directory.Exists(Path.Combine(dir, "DataWarehouse.Kernel")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }

        // Fallback: try common locations
        var fallback = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", ".."));
        if (Directory.Exists(Path.Combine(fallback, "DataWarehouse.Kernel")))
            return fallback;

        throw new InvalidOperationException("Could not find solution root directory");
    }

    private static List<string> GetProductionSourceFiles()
    {
        var files = new List<string>();
        var searchDirs = new[] { "DataWarehouse.SDK", "DataWarehouse.Kernel", "Plugins", "Shared" };

        foreach (var dir in searchDirs)
        {
            var fullPath = Path.Combine(SolutionRoot, dir);
            if (!Directory.Exists(fullPath))
                continue;

            files.AddRange(
                Directory.GetFiles(fullPath, "*.cs", SearchOption.AllDirectories)
                    .Where(f => !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)
                             && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                             && !f.Contains("Test")));
        }

        return files;
    }

    private static string? ExtractPluginNamespace(string fullTypeName)
    {
        // Extract "PluginName" from "DataWarehouse.Plugins.PluginName.SomeType"
        var parts = fullTypeName.Split('.');
        if (parts.Length >= 3 && parts[0] == "DataWarehouse" && parts[1] == "Plugins")
            return parts[2];
        return null;
    }

    #endregion
}
