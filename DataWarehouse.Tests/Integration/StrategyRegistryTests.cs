using System.Text.RegularExpressions;
using Xunit;

namespace DataWarehouse.Tests.Integration;

/// <summary>
/// Static analysis tests that verify strategy registration completeness across all plugins.
/// Scans .cs source files to ensure every concrete strategy class inherits from a recognized
/// domain strategy base and no deprecated bases are used.
/// </summary>
[Trait("Category", "Integration")]
public class StrategyRegistryTests
{
    private static readonly string SolutionRoot = FindSolutionRoot();
    private static readonly string PluginsRoot = Path.Combine(SolutionRoot, "Plugins");

    /// <summary>
    /// All recognized domain strategy base classes. Every concrete strategy must inherit
    /// from one of these (or a class that itself inherits from one of these).
    /// </summary>
    private static readonly HashSet<string> RecognizedBases = new(StringComparer.OrdinalIgnoreCase)
    {
        "StrategyBase",
        "EncryptionStrategyBase",
        "CompressionStrategyBase",
        "StorageStrategyBase",
        "ReplicationStrategyBase",
        "SecurityStrategyBase",
        "InterfaceStrategyBase",
        "ConnectorStrategyBase",
        "ComputeStrategyBase",
        "ObservabilityStrategyBase",
        "MediaStrategyBase",
        "StreamingStrategyBase",
        "FormatStrategyBase",
        "TransitStrategyBase",
        "DataManagementStrategyBase",
        "KeyManagementStrategyBase",
        "ComplianceStrategyBase",
        "DataProtectionStrategyBase",
        "RaidStrategyBase",
        "DatabaseStorageStrategyBase",
        "DataLakeStrategyBase",
        "DataMeshStrategyBase",
        "SemanticSyncStrategyBase",
        // Domain-specific bases that inherit from StrategyBase
        "AccessControlStrategyBase",
        "UltimateStorageStrategyBase",
        "DataCatalogStrategyBase",
        "FeatureStrategyBase",
        "DataGovernanceStrategyBase",
        "MicroservicesStrategyBase",
        "ServerlessStrategyBase",
        "KeyStoreStrategyBase",
        "DeploymentStrategyBase",
        "DataPrivacyStrategyBase",
        "EnhancedReplicationStrategyBase",
        "SustainabilityStrategyBase",
        "ComputeRuntimeStrategyBase",
        "StreamingDataStrategyBase",
        "MultiCloudStrategyBase",
        "ResilienceStrategyBase",
        "DatabaseConnectionStrategyBase",
        "DatabaseProtocolStrategyBase",
        "SdkRaidStrategyBase",
        "SaaSConnectionStrategyBase",
        "ConnectionStrategyBase",
        "StorageProcessingStrategyBase",
        "DataIntegrationStrategyBase",
        "FilesystemStrategyBase",
        "DashboardStrategyBase",
        "WorkflowStrategyBase",
        "ResourceStrategyBase",
        "AiConnectionStrategyBase",
        "ConsciousnessStrategyBase",
        "WasmLanguageStrategyBase",
        "DataFormatStrategyBase",
        "ObservabilityConnectionStrategyBase",
        "LineageStrategyBase",
        "DataQualityStrategyBase",
        "SDKPortStrategyBase",
        "ProtocolStrategyBase",
        "DashboardConnectionStrategyBase",
        "AIProviderStrategyBase",
        "MessagingConnectionStrategyBase",
        "LegacyConnectionStrategyBase",
        "FabricStrategyBase",
        "RegenerationStrategyBase",
        "IoTConnectionStrategyBase",
        "IntelligenceStrategyBase",
        "DataTransitStrategyBase",
        "TieringStrategyBase",
        "ShardingStrategyBase",
        "RtosStrategyBase",
        "EdgeIntegrationStrategyBase",
        "DomainModelStrategyBase",
        "DocGenStrategyBase",
        "DeduplicationStrategyBase",
        "IndexingStrategyBase",
        "BlockchainConnectionStrategyBase",
        "AiEnhancedStrategyBase",
        "VersioningStrategyBase",
        "RetentionStrategyBase",
        "LoadBalancingStrategyBase",
        "CachingStrategyBase",
        "VectorStoreStrategyBase",
        "TabularModelStrategyBase",
        "LongTermMemoryStrategyBase",
        "LifecycleStrategyBase",
        "HealthcareConnectionStrategyBase",
        "GraphPartitioningStrategyBase",
        "AgentStrategyBase",
        "SensorIngestionStrategyBase",
        "ProvisioningStrategyBase",
        "IoTSecurityStrategyBase",
        "IoTAnalyticsStrategyBase",
        "DeviceManagementStrategyBase",
        "DataTransformationStrategyBase",
        "KnowledgeGraphStrategyBase",
        "Pkcs11HsmStrategyBase",
        "HardwareBusStrategyBase",
        "FanOutStrategyBase",
        "ChaosVaccinationStrategyBase",
        "IoTStrategyBase",
        "BranchingStrategyBase",
        "PipelineComputeStrategyBase",
        "StorageOrchestrationStrategyBase",
    };

    /// <summary>
    /// Deprecated/legacy base classes that should NOT be used by any strategy.
    /// </summary>
    private static readonly HashSet<string> DeprecatedBases = new(StringComparer.OrdinalIgnoreCase)
    {
        "LegacyStrategyBase",
        "IntelligenceAwareStrategyBase",
    };

    /// <summary>
    /// Infrastructure-only plugins that are not expected to contain strategy classes.
    /// These provide services, drivers, or coordination layers rather than user-selectable strategies.
    /// </summary>
    private static readonly HashSet<string> InfrastructurePlugins = new(StringComparer.OrdinalIgnoreCase)
    {
        // AdaptiveTransport consolidated into UltimateStreamingData (Phase 65.5-12)
        "AedsCore",
        // AirGapBridge consolidated into UltimateDataTransit (Phase 65.5-12)
        "AppPlatform",
        "KubernetesCsi",
        "PluginMarketplace",
        "Raft",
        "TamperProof",
        "UltimateBlockchain",
        "UltimateConsensus",
        "UltimateDataIntegrity",
        "UltimateEdgeComputing",
        "UltimateInterface",
        "UniversalFabric",
        "Virtualization.SqlOverObject",
        // Transcoding.Media uses self-contained codec strategies instantiated directly by the
        // transcoding execution engine, not via the standard DiscoverAndRegister pattern.
        "Transcoding.Media",
    };

    /// <summary>
    /// Regex matching concrete (non-abstract) class declarations that inherit from a StrategyBase type.
    /// Captures: (1) class name, (2) base class name ending in StrategyBase.
    /// </summary>
    private static readonly Regex ConcreteStrategyPattern = new(
        @"(?<!abstract\s+)class\s+(\w+)\s*:\s*(\w*StrategyBase)",
        RegexOptions.Compiled);

    /// <summary>
    /// Regex matching abstract class declarations that inherit from a StrategyBase type.
    /// </summary>
    private static readonly Regex AbstractStrategyPattern = new(
        @"abstract\s+class\s+(\w+)\s*:\s*(\w*StrategyBase)",
        RegexOptions.Compiled);

    /// <summary>
    /// Regex matching any class that inherits from a deprecated base.
    /// </summary>
    private static readonly Regex DeprecatedBasePattern = new(
        @"class\s+(\w+)\s*:\s*(LegacyStrategyBase|IntelligenceAwareStrategyBase)",
        RegexOptions.Compiled);

    private static string FindSolutionRoot()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "DataWarehouse.slnx")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        // Fallback: walk up from test assembly location
        dir = Path.GetDirectoryName(typeof(StrategyRegistryTests).Assembly.Location);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "DataWarehouse.slnx")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new InvalidOperationException(
            "Could not locate DataWarehouse.slnx. Ensure tests run from within the solution tree.");
    }

    private static IEnumerable<string> GetPluginSourceFiles()
    {
        if (!Directory.Exists(PluginsRoot))
            yield break;

        foreach (var file in Directory.EnumerateFiles(PluginsRoot, "*.cs", SearchOption.AllDirectories))
        {
            // Skip build output directories
            if (file.Contains(Path.Combine("bin", "")) || file.Contains(Path.Combine("obj", "")))
                continue;
            yield return file;
        }
    }

    private static string ExtractPluginName(string filePath)
    {
        // Extract from path: Plugins/DataWarehouse.Plugins.{Name}/...
        var relative = Path.GetRelativePath(PluginsRoot, filePath);
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (parts.Length > 0)
        {
            var dirName = parts[0];
            const string prefix = "DataWarehouse.Plugins.";
            return dirName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? dirName[prefix.Length..]
                : dirName;
        }
        return "Unknown";
    }

    [Fact]
    public void AllStrategyClassesInheritFromDomainBase()
    {
        // Arrange
        var violations = new List<string>();
        var totalStrategies = 0;

        // Act
        foreach (var file in GetPluginSourceFiles())
        {
            var content = File.ReadAllText(file);
            var matches = ConcreteStrategyPattern.Matches(content);

            foreach (Match match in matches)
            {
                // Skip if this line also has 'abstract' keyword (regex lookbehind is limited)
                var lineStart = content.LastIndexOf('\n', match.Index) + 1;
                var line = content[lineStart..match.Index];
                if (line.Contains("abstract"))
                    continue;

                totalStrategies++;
                var className = match.Groups[1].Value;
                var baseClass = match.Groups[2].Value;
                var plugin = ExtractPluginName(file);

                if (!RecognizedBases.Contains(baseClass))
                {
                    violations.Add($"{plugin}/{className} inherits from unrecognized base '{baseClass}' in {Path.GetFileName(file)}");
                }
            }
        }

        // Assert
        Assert.True(totalStrategies > 2500,
            $"Expected at least 2,500 strategy classes but found {totalStrategies}. " +
            "Source scanning may be incomplete.");

        Assert.True(violations.Count == 0,
            $"Found {violations.Count} strategies with unrecognized base classes:\n" +
            string.Join("\n", violations.Take(50)));
    }

    [Fact]
    public void NoStrategyUsesLegacyBases()
    {
        // Arrange
        var violations = new List<string>();

        // Act
        foreach (var file in GetPluginSourceFiles())
        {
            var content = File.ReadAllText(file);
            var matches = DeprecatedBasePattern.Matches(content);

            foreach (Match match in matches)
            {
                var className = match.Groups[1].Value;
                var baseClass = match.Groups[2].Value;
                var plugin = ExtractPluginName(file);
                violations.Add($"{plugin}/{className} uses deprecated base '{baseClass}' in {Path.GetFileName(file)}");
            }
        }

        // Assert
        Assert.True(violations.Count == 0,
            $"Found {violations.Count} strategies using deprecated/legacy bases:\n" +
            string.Join("\n", violations));
    }

    [Fact]
    public void StrategyNamesAreUniquePerNamespaceWithinPlugin()
    {
        // Arrange: collect strategy class names per plugin, keyed by namespace + class name
        // Strategy classes in different sub-namespaces within the same plugin are allowed to
        // share a class name (e.g., Extended.Raid50Strategy vs Nested.Raid50Strategy).
        var strategiesByPlugin = new Dictionary<string, List<(string Namespace, string ClassName, string File)>>();

        var namespacePattern = new Regex(@"namespace\s+([\w.]+)", RegexOptions.Compiled);

        foreach (var file in GetPluginSourceFiles())
        {
            var content = File.ReadAllText(file);
            var matches = ConcreteStrategyPattern.Matches(content);

            // Extract namespace from file
            var nsMatch = namespacePattern.Match(content);
            var ns = nsMatch.Success ? nsMatch.Groups[1].Value : "global";

            foreach (Match match in matches)
            {
                var lineStart = content.LastIndexOf('\n', match.Index) + 1;
                var line = content[lineStart..match.Index];
                if (line.Contains("abstract"))
                    continue;

                var className = match.Groups[1].Value;
                var plugin = ExtractPluginName(file);

                if (!strategiesByPlugin.TryGetValue(plugin, out var list))
                {
                    list = new List<(string, string, string)>();
                    strategiesByPlugin[plugin] = list;
                }
                list.Add((ns, className, Path.GetFileName(file)));
            }
        }

        // Act: find duplicates within the SAME namespace in the same plugin
        var duplicates = new List<string>();
        foreach (var (plugin, strategies) in strategiesByPlugin)
        {
            var dupes = strategies
                .GroupBy(s => $"{s.Namespace}.{s.ClassName}", StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1);

            foreach (var dupe in dupes)
            {
                var files = string.Join(", ", dupe.Select(d => d.File));
                duplicates.Add($"{plugin}: class '{dupe.Key}' appears {dupe.Count()} times in [{files}]");
            }
        }

        // Assert
        Assert.True(duplicates.Count == 0,
            $"Found {duplicates.Count} duplicate strategy class names in the same namespace:\n" +
            string.Join("\n", duplicates));
    }

    [Fact]
    public void EveryNonInfrastructurePluginHasAtLeastOneStrategy()
    {
        // Arrange
        var pluginDirs = Directory.GetDirectories(PluginsRoot, "DataWarehouse.Plugins.*");
        var pluginsWithoutStrategies = new List<string>();

        // Act
        foreach (var pluginDir in pluginDirs)
        {
            var pluginName = Path.GetFileName(pluginDir);
            const string prefix = "DataWarehouse.Plugins.";
            var shortName = pluginName.StartsWith(prefix) ? pluginName[prefix.Length..] : pluginName;

            // Skip infrastructure-only plugins
            if (InfrastructurePlugins.Contains(shortName))
                continue;

            // Check if any .cs file in this plugin contains a concrete strategy class
            var hasStrategy = false;
            foreach (var file in Directory.EnumerateFiles(pluginDir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.Combine("bin", "")) || file.Contains(Path.Combine("obj", "")))
                    continue;

                var content = File.ReadAllText(file);
                var match = ConcreteStrategyPattern.Match(content);
                if (match.Success)
                {
                    // Verify it's not abstract
                    var lineStart = content.LastIndexOf('\n', match.Index) + 1;
                    var line = content[lineStart..match.Index];
                    if (!line.Contains("abstract"))
                    {
                        hasStrategy = true;
                        break;
                    }
                }
            }

            if (!hasStrategy)
            {
                pluginsWithoutStrategies.Add(shortName);
            }
        }

        // Assert
        Assert.True(pluginsWithoutStrategies.Count == 0,
            $"Found {pluginsWithoutStrategies.Count} non-infrastructure plugins with no strategy classes:\n" +
            string.Join("\n", pluginsWithoutStrategies));
    }

    [Fact]
    public void EveryPluginWithStrategiesHasRegistrationMechanism()
    {
        // Arrange: identify plugins that have concrete strategies
        var pluginDirs = Directory.GetDirectories(PluginsRoot, "DataWarehouse.Plugins.*");
        var violations = new List<string>();

        var registrationPatterns = new Regex(
            @"DiscoverAndRegister|AutoDiscover|Assembly\.GetExecutingAssembly|" +
            @"typeof\(\w+StrategyBase\)\.IsAssignableFrom|RegisterStrategy|" +
            @"GetExportedTypes|GetTypes\(\)",
            RegexOptions.Compiled);

        // Act
        foreach (var pluginDir in pluginDirs)
        {
            var pluginName = Path.GetFileName(pluginDir);
            const string prefix = "DataWarehouse.Plugins.";
            var shortName = pluginName.StartsWith(prefix) ? pluginName[prefix.Length..] : pluginName;

            if (InfrastructurePlugins.Contains(shortName))
                continue;

            // Check if this plugin has concrete strategies
            var hasStrategies = false;
            foreach (var file in Directory.EnumerateFiles(pluginDir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.Combine("bin", "")) || file.Contains(Path.Combine("obj", "")))
                    continue;

                var content = File.ReadAllText(file);
                if (ConcreteStrategyPattern.IsMatch(content))
                {
                    hasStrategies = true;
                    break;
                }
            }

            if (!hasStrategies)
                continue;

            // This plugin has strategies -- check for registration mechanism
            var hasRegistration = false;
            foreach (var file in Directory.EnumerateFiles(pluginDir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.Combine("bin", "")) || file.Contains(Path.Combine("obj", "")))
                    continue;

                var content = File.ReadAllText(file);
                if (registrationPatterns.IsMatch(content))
                {
                    hasRegistration = true;
                    break;
                }
            }

            if (!hasRegistration)
            {
                violations.Add($"{shortName}: has strategy classes but no registration mechanism found");
            }
        }

        // Assert
        Assert.True(violations.Count == 0,
            $"Found {violations.Count} plugins with strategies but no registration:\n" +
            string.Join("\n", violations));
    }
}
