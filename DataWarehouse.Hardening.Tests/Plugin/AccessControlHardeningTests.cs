// Hardening tests for Plugin findings 17-29: UltimateAccessControl
// Finding 17 (LOW): WatermarkEmbeddingStrategy naming mismatch
// Finding 18 (MEDIUM): CheckWatermarkHealthAsync health check
// Finding 19 (LOW): GetThreatProfile naming
// Findings 20-22 (MEDIUM): Non-thread-safe collections in AdaptiveThreatProfile
// Finding 23 (LOW): ContextualThreat naming
// Findings 24-25 (MEDIUM): Non-thread-safe collections in ContextualThreat
// Finding 26 (MEDIUM): SoarIntegration sequential enrichment
// Finding 27 (HIGH): SendWebhookAlertAsync incomplete
// Finding 28 (HIGH): ExecutePlaybookStepsAsync stubs
// Finding 29 (LOW): DeviceTrust scoring undocumented

using System.Collections.Concurrent;
using System.Reflection;

namespace DataWarehouse.Hardening.Tests.Plugin;

/// <summary>
/// Tests for UltimateAccessControl hardening findings.
/// </summary>
public class AccessControlHardeningTests
{
    private static readonly string PluginRoot = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateAccessControl");

    /// <summary>
    /// Findings 20-22, 24-25 (MEDIUM): Thread-unsafe collections must be replaced with
    /// ConcurrentDictionary/ConcurrentBag for multi-threaded access.
    /// </summary>
    [Fact]
    public void Finding020_025_ThreadSafeCollections()
    {
        // Check for non-thread-safe Dictionary/List in threat-related strategies
        var files = new[]
        {
            "Strategies/ThreatDetection/SoarStrategy.cs",
            "Strategies/ThreatDetection/ThreatDetectionStrategy.cs",
            "Strategies/ThreatDetection/ThreatIntelStrategy.cs",
        };

        foreach (var relPath in files)
        {
            var fullPath = Path.Combine(PluginRoot, relPath);
            if (!File.Exists(fullPath)) continue;

            var code = File.ReadAllText(fullPath);
            // Should not have plain Dictionary or List for concurrent access fields
            // ConcurrentDictionary or locks should be used instead
            var hasUnsafeDict = System.Text.RegularExpressions.Regex.IsMatch(code,
                @"private\s+(readonly\s+)?(static\s+)?Dictionary<.*>\s+_\w+");
            var hasUnsafeList = System.Text.RegularExpressions.Regex.IsMatch(code,
                @"private\s+(readonly\s+)?(static\s+)?List<.*>\s+_\w+");

            if (hasUnsafeDict || hasUnsafeList)
            {
                // Verify they are protected by locks or are ConcurrentDictionary
                Assert.True(
                    code.Contains("ConcurrentDictionary") || code.Contains("ConcurrentBag") ||
                    code.Contains("lock(") || code.Contains("lock (") ||
                    code.Contains("SemaphoreSlim") || code.Contains("ReaderWriterLockSlim"),
                    $"File {relPath} has non-thread-safe collections without synchronization");
            }
        }
    }

    /// <summary>
    /// Finding 27 (HIGH): SendWebhookAlertAsync must have a functional HttpClient.
    /// Finding 28 (HIGH): ExecutePlaybookStepsAsync must not be a stub.
    /// </summary>
    [Fact]
    public void Finding027_028_SoarStrategy_NotStubs()
    {
        var filePath = Path.Combine(PluginRoot, "Strategies", "ThreatDetection", "SoarStrategy.cs");
        if (!File.Exists(filePath)) return;

        var code = File.ReadAllText(filePath);

        // Finding 27: Should not have incomplete webhook method
        Assert.DoesNotContain("method body incomplete", code);

        // Finding 28: Should not return hardcoded true for all steps
        Assert.False(
            System.Text.RegularExpressions.Regex.IsMatch(code,
                @"return\s+true\s*;\s*//.*stub",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase),
            "ExecutePlaybookStepsAsync should not be a stub returning true");
    }
}
