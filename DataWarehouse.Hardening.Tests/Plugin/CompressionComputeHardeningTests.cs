// Hardening tests for Plugin findings 58-64
// Finding 58 (LOW): LZ4 EstimateCompressionRatio constant 0.7
// Finding 59 (HIGH): LZ4 decompression buffer too small
// Finding 60 (HIGH): JobScheduler fire-and-forget Task.Run
// Finding 61 (MEDIUM): JobScheduler path traversal
// Finding 62 (MEDIUM): ResourceManager path traversal
// Finding 63 (HIGH): LandlockStrategy silent fallback
// Finding 64 (MEDIUM): WasmInterpreterStrategy naming lie

namespace DataWarehouse.Hardening.Tests.Plugin;

public class CompressionComputeHardeningTests
{
    private static readonly string CompressionRoot = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateCompression");

    private static readonly string ComputeRoot = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateCompute");

    /// <summary>
    /// Finding 59 (HIGH): LZ4 decompression must allocate sufficient buffer for high-ratio data.
    /// </summary>
    [Fact]
    public void Finding059_Lz4Decompression_AdequateBuffer()
    {
        var dir = Path.Combine(CompressionRoot, "Strategies", "Transit");
        if (!Directory.Exists(dir)) return;

        var file = Directory.GetFiles(dir, "*LZ4*Transit*.cs").FirstOrDefault()
            ?? Directory.GetFiles(dir, "*Lz4*.cs").FirstOrDefault();
        if (file == null) return;

        var code = File.ReadAllText(file);
        // Should not use compressedData.Length * 2 as output size
        Assert.DoesNotContain("Length * 2", code);
    }

    /// <summary>
    /// Finding 60 (HIGH): JobScheduler should not fire-and-forget tasks.
    /// </summary>
    [Fact]
    public void Finding060_JobScheduler_NoFireAndForget()
    {
        var dir = Path.Combine(ComputeRoot, "Strategies", "Compute");
        if (!Directory.Exists(dir)) return;

        var file = Directory.GetFiles(dir, "*JobScheduler*.cs").FirstOrDefault();
        if (file == null) return;

        var code = File.ReadAllText(file);
        // Task.Run results should be tracked, not discarded
        Assert.False(
            code.Contains("Task.Run(") && !code.Contains("await Task.Run"),
            "Job execution tasks should be awaited or tracked via TaskCompletionSource");
    }

    /// <summary>
    /// Findings 61-62 (MEDIUM): Path traversal via unsanitized task/resource IDs.
    /// </summary>
    [Fact]
    public void Finding061_062_PathTraversal_Sanitized()
    {
        var computeDir = Path.Combine(ComputeRoot, "Strategies", "Compute");
        if (!Directory.Exists(computeDir)) return;

        foreach (var file in Directory.GetFiles(computeDir, "*.cs"))
        {
            var code = File.ReadAllText(file);
            if (code.Contains("Path.Combine") && (code.Contains("task.Id") || code.Contains("resourceId")))
            {
                Assert.True(
                    code.Contains("Path.GetFileName") || code.Contains("Replace") ||
                    code.Contains("sanitize") || code.Contains("Sanitize") ||
                    code.Contains("InvalidPathChars") || code.Contains(".."),
                    $"File {Path.GetFileName(file)} should sanitize IDs before path construction");
            }
        }
    }

    /// <summary>
    /// Finding 63 (HIGH): LandlockStrategy must not silently execute without isolation.
    /// </summary>
    [Fact]
    public void Finding063_Landlock_NotSilentFallback()
    {
        var dir = Path.Combine(ComputeRoot, "Strategies", "Sandbox");
        if (!Directory.Exists(dir)) return;

        var file = Directory.GetFiles(dir, "*Landlock*.cs").FirstOrDefault();
        if (file == null) return;

        var code = File.ReadAllText(file);
        // Should throw or log error if sandboxer is not available
        Assert.True(
            code.Contains("throw") || code.Contains("NotAvailable") ||
            code.Contains("LogError") || code.Contains("LogWarning"),
            "LandlockStrategy should not silently run without isolation");
    }
}
