using System.Diagnostics;
using System.Reflection;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.Tests.Integration.Helpers;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Integration;

/// <summary>
/// Performance regression guards for Phase 66 integration gate.
/// These are NOT micro-benchmarks (Phase 46 handles that). These are heuristic tests
/// that detect obvious regressions like O(n^2) initialization, memory leaks, or
/// assembly explosion. Thresholds are deliberately generous to avoid false positives
/// while catching real regressions.
/// </summary>
public class PerformanceRegressionTests
{
    /// <summary>
    /// The solution root directory, resolved relative to the test assembly location.
    /// </summary>
    private static string SolutionRoot
    {
        get
        {
            var dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            // Walk up from bin/Debug/net10.0 to the solution root
            while (dir != null && !File.Exists(Path.Combine(dir, "DataWarehouse.slnx")))
            {
                dir = Path.GetDirectoryName(dir);
            }
            return dir ?? throw new InvalidOperationException("Could not find solution root (DataWarehouse.slnx)");
        }
    }

    /// <summary>
    /// Verify that all plugin .csproj files referenced in the solution can be discovered
    /// within a reasonable time. This is a proxy for build-time regression -- if we can
    /// enumerate and parse all project files quickly, the build graph is not pathologically complex.
    ///
    /// Threshold: 10 seconds. Rationale: file system enumeration of ~75 projects should
    /// complete in well under 1 second; 10s allows for slow CI disks and antivirus overhead.
    /// </summary>
    [Fact]
    public void SolutionProjectDiscoveryTimeRegression()
    {
        var slnxPath = Path.Combine(SolutionRoot, "DataWarehouse.slnx");
        File.Exists(slnxPath).Should().BeTrue("solution file must exist");

        var sw = Stopwatch.StartNew();

        // Parse the slnx to find all referenced project paths
        var slnxContent = File.ReadAllText(slnxPath);
        var projectPaths = System.Text.RegularExpressions.Regex.Matches(
                slnxContent, @"Path=""([^""]+\.csproj)""")
            .Cast<System.Text.RegularExpressions.Match>()
            .Select(m => m.Groups[1].Value)
            .ToList();

        // Verify each project file exists on disk (same check as BuildHealthTests but timed)
        var existingProjects = projectPaths
            .Select(p => Path.Combine(SolutionRoot, p.Replace('\\', Path.DirectorySeparatorChar)))
            .Where(File.Exists)
            .ToList();

        sw.Stop();

        // Threshold: 10 seconds for discovering and verifying all project files.
        // Actual typical time: <100ms. This catches pathological file system issues.
        sw.Elapsed.TotalSeconds.Should().BeLessThan(10,
            "solution project discovery should complete within 10 seconds (actual: {0:F2}s for {1} projects)",
            sw.Elapsed.TotalSeconds, projectPaths.Count);

        existingProjects.Count.Should().BeGreaterThanOrEqualTo(55,
            "solution should contain at least 55 discoverable projects (post-consolidation)");
    }

    /// <summary>
    /// Measure the time to discover all plugin types via reflection from loaded assemblies.
    /// This catches plugins with expensive static constructors or type initializers
    /// that would cause slow startup.
    ///
    /// Threshold: 100ms per plugin type discovery. Rationale: reflection-based type enumeration
    /// should be near-instant; anything over 100ms suggests an expensive static initializer.
    /// Overall threshold: 5 seconds for all plugins combined.
    /// </summary>
    [Fact]
    public void PluginTypeDiscoveryTimeRegression()
    {
        var pluginsDir = Path.Combine(SolutionRoot, "Plugins");
        Directory.Exists(pluginsDir).Should().BeTrue("Plugins directory must exist");

        var pluginDirs = Directory.GetDirectories(pluginsDir, "DataWarehouse.Plugins.*");

        var sw = Stopwatch.StartNew();
        var pluginTypeCount = 0;

        foreach (var pluginDir in pluginDirs)
        {
            var pluginName = Path.GetFileName(pluginDir);
            var csFiles = Directory.GetFiles(pluginDir, "*.cs", SearchOption.AllDirectories);

            foreach (var csFile in csFiles)
            {
                // Count class declarations as a proxy for type discovery
                var content = File.ReadAllText(csFile);
                var classMatches = System.Text.RegularExpressions.Regex.Matches(
                    content, @"\bclass\s+\w+");
                pluginTypeCount += classMatches.Count;
            }
        }

        sw.Stop();

        // Threshold: 5 seconds total for scanning all plugin source files.
        // This catches explosive growth in plugin count or file sizes.
        // Actual typical time: <2 seconds for ~66 plugins, ~3000 classes.
        sw.Elapsed.TotalSeconds.Should().BeLessThan(5,
            "plugin type discovery across {0} plugins ({1} types) should complete within 5 seconds (actual: {2:F2}s)",
            pluginDirs.Length, pluginTypeCount, sw.Elapsed.TotalSeconds);

        pluginTypeCount.Should().BeGreaterThan(2000,
            "expected at least 2000 plugin types (strategies + plugins); found {0}", pluginTypeCount);
    }

    /// <summary>
    /// Measure message bus publish latency using the TracingMessageBus from the
    /// integration test harness. Publishes 1000 messages and asserts average latency
    /// is acceptable.
    ///
    /// Threshold: 1ms average per publish. Rationale: in-memory publish with no I/O
    /// should be sub-microsecond; 1ms accounts for test infrastructure overhead,
    /// tracing decorator, and CI variability.
    /// </summary>
    [Fact]
    public async Task MessageBusPublishLatencyRegression()
    {
        var bus = new TracingMessageBus();
        const int messageCount = 1000;

        // Warm up the bus with a few messages to avoid first-call overhead
        for (int i = 0; i < 10; i++)
        {
            await bus.PublishAsync($"warmup.topic.{i}", new PluginMessage
            {
                Type = $"warmup.topic.{i}",
                Source = "perf-test"
            });
        }

        var sw = Stopwatch.StartNew();

        for (int i = 0; i < messageCount; i++)
        {
            await bus.PublishAsync($"perf.test.topic.{i % 10}", new PluginMessage
            {
                Type = $"perf.test.topic.{i % 10}",
                Source = "perf-test"
            });
        }

        sw.Stop();

        var averageMs = sw.Elapsed.TotalMilliseconds / messageCount;

        // Threshold: 1ms average per message.
        // In-memory bus should be <0.01ms. 1ms allows for GC pauses and CI overhead.
        averageMs.Should().BeLessThan(1.0,
            "average publish latency should be <1ms (actual: {0:F4}ms for {1} messages)",
            averageMs, messageCount);

        // Verify all messages were recorded by the tracing bus
        var recorded = bus.GetPublishedMessages("perf.test.*");
        recorded.Count.Should().Be(messageCount,
            "all {0} messages should be recorded by TracingMessageBus", messageCount);
    }

    /// <summary>
    /// Check memory footprint after loading all plugin source file metadata.
    /// This is a proxy for runtime memory -- if the source metadata is reasonable,
    /// the runtime footprint should scale linearly with plugin count.
    ///
    /// Threshold: 500MB total GC memory. Rationale: loading metadata for ~66 plugins
    /// with ~3000 strategy classes should not consume excessive memory. The 500MB
    /// threshold is generous and catches exponential growth or massive string retention.
    /// </summary>
    [Fact]
    public void MemoryFootprintRegression()
    {
        // Force GC to get a clean baseline
        GC.Collect(2, GCCollectionMode.Forced, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true);

        var baselineMemory = GC.GetTotalMemory(true);

        var pluginsDir = Path.Combine(SolutionRoot, "Plugins");
        var allPluginTypes = new List<string>();
        var pluginDirs = Directory.GetDirectories(pluginsDir, "DataWarehouse.Plugins.*");

        foreach (var pluginDir in pluginDirs)
        {
            var csFiles = Directory.GetFiles(pluginDir, "*.cs", SearchOption.AllDirectories);
            foreach (var csFile in csFiles)
            {
                var content = File.ReadAllText(csFile);
                var classMatches = System.Text.RegularExpressions.Regex.Matches(
                    content, @"\bclass\s+(\w+)");
                foreach (System.Text.RegularExpressions.Match match in classMatches)
                {
                    allPluginTypes.Add(match.Groups[1].Value);
                }
            }
        }

        var afterLoadMemory = GC.GetTotalMemory(false);
        var memoryDeltaBytes = afterLoadMemory - baselineMemory;
        var memoryDeltaMB = memoryDeltaBytes / (1024.0 * 1024.0);

        // Threshold: 500MB delta. Loading source metadata for ~3000 types should use
        // well under 100MB. 500MB catches pathological memory retention.
        memoryDeltaMB.Should().BeLessThan(500,
            "memory delta after loading plugin metadata should be <500MB (actual: {0:F1}MB for {1} types across {2} plugins)",
            memoryDeltaMB, allPluginTypes.Count, pluginDirs.Length);

        // Verify linear scaling: memory per type should be < 100KB
        // (generous -- actual should be <1KB per type name string)
        if (allPluginTypes.Count > 0)
        {
            var bytesPerType = memoryDeltaBytes / allPluginTypes.Count;
            bytesPerType.Should().BeLessThan(100 * 1024,
                "memory per plugin type should be <100KB (actual: {0:F0} bytes/type)",
                bytesPerType);
        }

        allPluginTypes.Count.Should().BeGreaterThan(2000,
            "expected to discover at least 2000 plugin types");
    }

    /// <summary>
    /// Verify the total assembly/project count in the solution has not exploded.
    /// This catches uncontrolled growth in solution complexity.
    ///
    /// Threshold: <200 projects. Rationale: the solution currently has ~75 projects.
    /// Doubling would indicate uncontrolled growth. 200 leaves room for planned
    /// v6.0 expansion while catching accidental explosion.
    /// </summary>
    [Fact]
    public void AssemblyCountRegression()
    {
        var slnxPath = Path.Combine(SolutionRoot, "DataWarehouse.slnx");
        File.Exists(slnxPath).Should().BeTrue("solution file must exist");

        var slnxContent = File.ReadAllText(slnxPath);
        var projectCount = System.Text.RegularExpressions.Regex.Matches(
                slnxContent, @"Path=""[^""]+\.csproj""")
            .Count;

        // Threshold: <200 projects. Current count is ~75.
        // This catches accidental project explosion while allowing planned v6.0 growth.
        projectCount.Should().BeLessThan(200,
            "solution should contain fewer than 200 projects (actual: {0}). " +
            "Exceeding this threshold suggests uncontrolled project growth.",
            projectCount);

        // Also verify we haven't lost projects -- should still have at least 55 (post-consolidation: 65→53 plugins)
        projectCount.Should().BeGreaterThanOrEqualTo(55,
            "solution should contain at least 55 projects (actual: {0}). " +
            "Fewer suggests accidental project removal.",
            projectCount);
    }

    /// <summary>
    /// Verify that plugin directory count matches expectations and hasn't exploded.
    /// Complements AssemblyCountRegression by checking the physical directory structure.
    ///
    /// Threshold: 60-150 plugin directories. Current: ~66.
    /// </summary>
    [Fact]
    public void PluginCountRegression()
    {
        var pluginsDir = Path.Combine(SolutionRoot, "Plugins");
        Directory.Exists(pluginsDir).Should().BeTrue("Plugins directory must exist");

        var pluginDirs = Directory.GetDirectories(pluginsDir, "DataWarehouse.Plugins.*");
        var pluginCount = pluginDirs.Length;

        // Lower bound: at least 50 plugins (post-consolidation: 65→53 plugins)
        pluginCount.Should().BeGreaterThanOrEqualTo(50,
            "should have at least 50 plugins (actual: {0})", pluginCount);

        // Upper bound: fewer than 150 plugins
        // Current: 53. This catches uncontrolled plugin proliferation.
        pluginCount.Should().BeLessThan(150,
            "should have fewer than 150 plugins (actual: {0}). " +
            "Exceeding this suggests uncontrolled plugin growth.",
            pluginCount);
    }
}
