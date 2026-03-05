// Hardening tests for AedsCore findings: GlobalDeduplicationPlugin
// Finding: 17 (MEDIUM)
using DataWarehouse.Plugins.AedsCore.Extensions;

namespace DataWarehouse.Hardening.Tests.AedsCore;

/// <summary>
/// Tests for GlobalDeduplicationPlugin hardening findings.
/// </summary>
public class GlobalDeduplicationPluginTests
{
    /// <summary>
    /// Finding 17: QueryServerForDedupAsync returns empty list.
    /// The method publishes a dedup-check request via message bus and returns empty list
    /// because the response comes via subscription. Without a bus, this is the expected behavior.
    /// </summary>
    [Fact]
    public async Task Finding017_QueryServerReturnsEmptyWithoutBus()
    {
        var plugin = new GlobalDeduplicationPlugin();
        var result = await plugin.QueryServerForDedupAsync("hash123");
        Assert.Empty(result);
    }

    /// <summary>
    /// Verifies local hash registration and lookup work.
    /// </summary>
    [Fact]
    public async Task LocalDedupCheckWorks()
    {
        var plugin = new GlobalDeduplicationPlugin();

        // Before registration, should not exist
        var exists = await plugin.CheckIfExistsLocallyAsync("hash456");
        Assert.False(exists);

        // Register and verify
        await plugin.RegisterContentHashAsync("hash456", "payload-001");
        exists = await plugin.CheckIfExistsLocallyAsync("hash456");
        Assert.True(exists);
    }
}
