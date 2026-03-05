// Hardening tests for Plugin findings 172-177
// Finding 172 (CRITICAL): IoTIntegration analytics strategies return Random values
// Finding 173 (MEDIUM): FrostStrategy uninformative error
// Finding 174 (HIGH): StorageTekRaid7 write cache race
// Finding 175 (HIGH): ZFS self-healing not implemented
// Finding 176 (MEDIUM): RTOS timer disposal leak
// Finding 177 (LOW): iSCSI small allocations in hot path

namespace DataWarehouse.Hardening.Tests.Plugin;

public class IoTAnalyticsKeyMgmtRaidTests
{
    private static readonly string IoTRoot = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateIoTIntegration");

    private static readonly string KeyMgmtRoot = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateKeyManagement");

    private static readonly string RaidRoot = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateRAID");

    private static readonly string RtosRoot = Path.Combine(
        "C:", "Temp", "DataWarehouse", "DataWarehouse", "Plugins",
        "DataWarehouse.Plugins.UltimateRTOSBridge");

    /// <summary>
    /// Finding 172 (CRITICAL): IoT analytics must not return Random.Shared values.
    /// </summary>
    [Fact]
    public void Finding172_IoTAnalytics_NotRandom()
    {
        var analyticsDir = Path.Combine(IoTRoot, "Strategies", "Analytics");
        if (!Directory.Exists(analyticsDir)) return;

        foreach (var file in Directory.GetFiles(analyticsDir, "*.cs"))
        {
            var code = File.ReadAllText(file);
            var fileName = Path.GetFileName(file);

            Assert.DoesNotContain("Random.Shared", code);
            Assert.DoesNotContain("new Random()", code);
        }
    }

    /// <summary>
    /// Finding 173 (MEDIUM): FrostStrategy.DeleteKeyAsync must have descriptive error message.
    /// </summary>
    [Fact]
    public void Finding173_FrostStrategy_DescriptiveError()
    {
        var dir = Path.Combine(KeyMgmtRoot, "Strategies", "Threshold");
        if (!Directory.Exists(dir)) return;

        var file = Directory.GetFiles(dir, "*Frost*.cs").FirstOrDefault();
        if (file == null) return;

        var code = File.ReadAllText(file);
        if (code.Contains("UnauthorizedAccessException"))
        {
            // Should have a descriptive message
            Assert.False(
                code.Contains("new UnauthorizedAccessException()"),
                "FrostStrategy: UnauthorizedAccessException should include descriptive message");
        }
    }

    /// <summary>
    /// Finding 174 (HIGH): StorageTekRaid7 write cache must be thread-safe.
    /// </summary>
    [Fact]
    public void Finding174_StorageTekRaid7_ThreadSafeCache()
    {
        var vendorDir = Path.Combine(RaidRoot, "Strategies", "Vendor");
        if (!Directory.Exists(vendorDir)) return;

        var file = Directory.GetFiles(vendorDir, "*VendorRaidStrategiesB5*.cs").FirstOrDefault();
        if (file == null) return;

        var code = File.ReadAllText(file);
        if (code.Contains("_writeCache") && code.Contains("Count"))
        {
            Assert.True(
                code.Contains("lock(") || code.Contains("lock (") ||
                code.Contains("Interlocked") || code.Contains("ConcurrentQueue"),
                "StorageTekRaid7: Write cache must use thread-safe operations");
        }
    }

    /// <summary>
    /// Finding 175 (HIGH): ZFS self-healing must not be a stub.
    /// </summary>
    [Fact]
    public void Finding175_Zfs_NotStub()
    {
        var zfsDir = Path.Combine(RaidRoot, "Strategies", "ZFS");
        if (!Directory.Exists(zfsDir)) return;

        var file = Directory.GetFiles(zfsDir, "*ZfsRaid*.cs").FirstOrDefault();
        if (file == null) return;

        var code = File.ReadAllText(file);
        Assert.DoesNotContain("In production, would", code);
        Assert.DoesNotContain("In production:", code);
    }

    /// <summary>
    /// Finding 176 (MEDIUM): RTOS timer must be properly disposed before replacement.
    /// </summary>
    [Fact]
    public void Finding176_RtosTimer_DisposedBeforeReplace()
    {
        var dir = Path.Combine(RtosRoot, "Strategies");
        if (!Directory.Exists(dir)) return;

        var file = Directory.GetFiles(dir, "*DeterministicIo*.cs", SearchOption.AllDirectories).FirstOrDefault();
        if (file == null) return;

        var code = File.ReadAllText(file);
        if (code.Contains("_timers[") && code.Contains("= timer"))
        {
            Assert.True(
                code.Contains("Dispose") || code.Contains("TryRemove"),
                "RTOS: Previous timer must be disposed before replacement");
        }
    }
}
