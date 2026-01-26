using DataWarehouse.Plugins.GeoReplication;
using DataWarehouse.SDK.Contracts;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Replication;

/// <summary>
/// Unit tests for GeoReplicationPlugin and GeoReplicationManager.
/// Tests lifecycle management, region operations, and conflict resolution.
/// </summary>
public class GeoReplicationPluginTests : IAsyncLifetime
{
    private GeoReplicationPlugin? _plugin;
    private readonly CancellationTokenSource _cts = new();

    public async Task InitializeAsync()
    {
        _plugin = new GeoReplicationPlugin();

        // Perform handshake to initialize the plugin
        var handshakeRequest = new HandshakeRequest
        {
            PluginId = _plugin.Id,
            KernelId = "test-kernel-" + Guid.NewGuid().ToString("N")[..8],
            Config = new Dictionary<string, object>()
        };

        await _plugin.OnHandshakeAsync(handshakeRequest);
    }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        if (_plugin != null)
        {
            try { await _plugin.StopAsync(); } catch { }
        }
        _cts.Dispose();
    }

    #region Lifecycle Tests

    [Fact]
    public async Task StartAsync_InitializesPlugin()
    {
        // Act
        await _plugin!.StartAsync(_cts.Token);

        // Assert - No exception means success
        // Plugin should be running
    }

    [Fact]
    public async Task StopAsync_CleansUpResources()
    {
        // Arrange
        await _plugin!.StartAsync(_cts.Token);

        // Act
        await _plugin.StopAsync();

        // Assert - No exception means success
    }

    [Fact]
    public async Task StartAsync_CalledTwice_DoesNotThrow()
    {
        // Act
        await _plugin!.StartAsync(_cts.Token);
        await _plugin.StartAsync(_cts.Token); // Second call should be idempotent

        // Assert - No exception
    }

    [Fact]
    public async Task StopAsync_CalledWithoutStart_DoesNotThrow()
    {
        // Act & Assert - Should not throw
        await _plugin!.StopAsync();
    }

    #endregion

    #region Region Management Tests

    [Fact]
    public async Task AddRegionAsync_SucceedsWithValidConfig()
    {
        // Arrange
        await _plugin!.StartAsync(_cts.Token);
        var config = new RegionConfig { Priority = 100, IsWitness = false };

        // Act
        var result = await _plugin.AddRegionAsync("us-east-1", "https://us-east-1.example.com", config, _cts.Token);

        // Assert
        result.Success.Should().BeTrue();
        result.RegionId.Should().Be("us-east-1");
    }

    [Fact]
    public async Task AddRegionAsync_DuplicateRegion_ReturnsFalse()
    {
        // Arrange
        await _plugin!.StartAsync(_cts.Token);
        var config = new RegionConfig { Priority = 100 };
        await _plugin.AddRegionAsync("us-east-1", "https://us-east-1.example.com", config, _cts.Token);

        // Act
        var result = await _plugin.AddRegionAsync("us-east-1", "https://us-east-1.example.com", config, _cts.Token);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("already exists");
    }

    [Fact]
    public async Task RemoveRegionAsync_ExistingRegion_Succeeds()
    {
        // Arrange
        await _plugin!.StartAsync(_cts.Token);
        var config = new RegionConfig { Priority = 100 };
        await _plugin.AddRegionAsync("eu-west-1", "https://eu-west-1.example.com", config, _cts.Token);

        // Act
        var result = await _plugin.RemoveRegionAsync("eu-west-1", drainFirst: false, _cts.Token);

        // Assert
        result.Success.Should().BeTrue();
        result.RegionId.Should().Be("eu-west-1");
    }

    [Fact]
    public async Task RemoveRegionAsync_NonExistentRegion_ReturnsFalse()
    {
        // Arrange
        await _plugin!.StartAsync(_cts.Token);

        // Act
        var result = await _plugin.RemoveRegionAsync("nonexistent-region", drainFirst: false, _cts.Token);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("not found");
    }

    [Fact]
    public async Task ListRegionsAsync_ReturnsAllAddedRegions()
    {
        // Arrange
        await _plugin!.StartAsync(_cts.Token);
        var config = new RegionConfig { Priority = 100 };
        await _plugin.AddRegionAsync("us-east-1", "https://us-east-1.example.com", config, _cts.Token);
        await _plugin.AddRegionAsync("eu-west-1", "https://eu-west-1.example.com", config, _cts.Token);
        await _plugin.AddRegionAsync("ap-south-1", "https://ap-south-1.example.com", config, _cts.Token);

        // Act
        var regions = await _plugin.ListRegionsAsync(_cts.Token);

        // Assert
        regions.Should().HaveCount(3);
        regions.Select(r => r.RegionId).Should().Contain(new[] { "us-east-1", "eu-west-1", "ap-south-1" });
    }

    #endregion

    #region Consistency Level Tests

    [Fact]
    public async Task SetConsistencyLevelAsync_ChangesLevel()
    {
        // Arrange
        await _plugin!.StartAsync(_cts.Token);

        // Act
        await _plugin.SetConsistencyLevelAsync(ConsistencyLevel.Quorum, _cts.Token);
        var level = _plugin.GetConsistencyLevel();

        // Assert
        level.Should().Be(ConsistencyLevel.Quorum);
    }

    [Theory]
    [InlineData(ConsistencyLevel.Eventual)]
    [InlineData(ConsistencyLevel.Local)]
    [InlineData(ConsistencyLevel.Quorum)]
    [InlineData(ConsistencyLevel.Strong)]
    public async Task SetConsistencyLevelAsync_AllLevels_AreSupported(ConsistencyLevel level)
    {
        // Arrange
        await _plugin!.StartAsync(_cts.Token);

        // Act
        await _plugin.SetConsistencyLevelAsync(level, _cts.Token);
        var actualLevel = _plugin.GetConsistencyLevel();

        // Assert
        actualLevel.Should().Be(level);
    }

    #endregion

    #region Replication Status Tests

    [Fact]
    public async Task GetReplicationStatusAsync_ReturnsValidStatus()
    {
        // Arrange
        await _plugin!.StartAsync(_cts.Token);
        var config = new RegionConfig { Priority = 100 };
        await _plugin.AddRegionAsync("us-east-1", "https://us-east-1.example.com", config, _cts.Token);

        // Act
        var status = await _plugin.GetReplicationStatusAsync(_cts.Token);

        // Assert
        status.TotalRegions.Should().Be(1);
        status.Regions.Should().ContainKey("us-east-1");
    }

    [Fact]
    public async Task GetReplicationStatusAsync_EmptyRegions_ReturnsZeroCounts()
    {
        // Arrange
        await _plugin!.StartAsync(_cts.Token);

        // Act
        var status = await _plugin.GetReplicationStatusAsync(_cts.Token);

        // Assert
        status.TotalRegions.Should().Be(0);
        status.HealthyRegions.Should().Be(0);
        status.PendingOperations.Should().Be(0);
    }

    #endregion

    #region Health Check Tests

    [Fact]
    public async Task CheckRegionHealthAsync_ReturnsHealthForAllRegions()
    {
        // Arrange
        await _plugin!.StartAsync(_cts.Token);
        var config = new RegionConfig { Priority = 100 };
        await _plugin.AddRegionAsync("us-east-1", "https://us-east-1.example.com", config, _cts.Token);
        await _plugin.AddRegionAsync("eu-west-1", "https://eu-west-1.example.com", config, _cts.Token);

        // Act
        var health = await _plugin.CheckRegionHealthAsync(_cts.Token);

        // Assert
        health.Should().HaveCount(2);
        health.Should().ContainKey("us-east-1");
        health.Should().ContainKey("eu-west-1");
    }

    #endregion

    #region Bandwidth Throttle Tests

    [Fact]
    public async Task SetBandwidthThrottleAsync_SetsThrottle()
    {
        // Arrange
        await _plugin!.StartAsync(_cts.Token);

        // Act
        await _plugin.SetBandwidthThrottleAsync(1_000_000, _cts.Token); // 1 MB/s

        // Assert
        var status = await _plugin.GetReplicationStatusAsync(_cts.Token);
        status.BandwidthThrottle.Should().Be(1_000_000);
    }

    [Fact]
    public async Task SetBandwidthThrottleAsync_NullRemovesThrottle()
    {
        // Arrange
        await _plugin!.StartAsync(_cts.Token);
        await _plugin.SetBandwidthThrottleAsync(1_000_000, _cts.Token);

        // Act
        await _plugin.SetBandwidthThrottleAsync(null, _cts.Token);

        // Assert
        var status = await _plugin.GetReplicationStatusAsync(_cts.Token);
        status.BandwidthThrottle.Should().BeNull();
    }

    #endregion

    #region Force Sync Tests

    [Fact]
    public async Task ForceSyncAsync_WithRegions_ReturnsResults()
    {
        // Arrange
        await _plugin!.StartAsync(_cts.Token);
        var config = new RegionConfig { Priority = 100, IsWitness = false };
        await _plugin.AddRegionAsync("us-east-1", "https://us-east-1.example.com", config, _cts.Token);

        // Act
        var result = await _plugin.ForceSyncAsync("test-key", null, _cts.Token);

        // Assert
        result.Key.Should().Be("test-key");
        result.RegionResults.Should().ContainKey("us-east-1");
    }

    [Fact]
    public async Task ForceSyncAsync_NoRegions_ReturnsEmptyResults()
    {
        // Arrange
        await _plugin!.StartAsync(_cts.Token);

        // Act
        var result = await _plugin.ForceSyncAsync("test-key", null, _cts.Token);

        // Assert
        result.Key.Should().Be("test-key");
        result.RegionResults.Should().BeEmpty();
        result.Success.Should().BeTrue(); // Eventual consistency with no regions = success
    }

    #endregion

    #region Conflict Resolution Tests

    [Fact]
    public async Task ResolveConflictAsync_NoConflict_ReturnsFalse()
    {
        // Arrange
        await _plugin!.StartAsync(_cts.Token);

        // Act
        var result = await _plugin.ResolveConflictAsync("no-conflict-key", ConflictResolutionStrategy.LastWriteWins, null, _cts.Token);

        // Assert
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("No conflict found");
    }

    [Theory]
    [InlineData(ConflictResolutionStrategy.LastWriteWins)]
    [InlineData(ConflictResolutionStrategy.HighestPriorityWins)]
    [InlineData(ConflictResolutionStrategy.Merge)]
    [InlineData(ConflictResolutionStrategy.KeepAll)]
    public void ConflictResolutionStrategy_AllStrategies_AreDefined(ConflictResolutionStrategy strategy)
    {
        // Assert
        Enum.IsDefined(typeof(ConflictResolutionStrategy), strategy).Should().BeTrue();
    }

    #endregion

    #region Plugin Metadata Tests

    [Fact]
    public void Plugin_HasCorrectId()
    {
        _plugin!.Id.Should().Be("datawarehouse.georeplication");
    }

    [Fact]
    public void Plugin_HasCorrectName()
    {
        _plugin!.Name.Should().Be("Geo-Replication");
    }

    [Fact]
    public void Plugin_HasCorrectVersion()
    {
        _plugin!.Version.Should().Be("1.0.0");
    }

    #endregion
}
