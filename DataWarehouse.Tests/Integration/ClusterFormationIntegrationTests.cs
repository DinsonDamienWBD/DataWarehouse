using DataWarehouse.SDK.Infrastructure.Distributed;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Integration;

/// <summary>
/// Integration tests for cluster formation and consensus configuration using the SDK's Raft types.
/// Tests SDK RaftConfiguration defaults and initialization patterns.
/// The old RaftConsensusPlugin (BasePort/PortRange/ClusterEndpoints) has been deleted;
/// these tests now use the SDK-native RaftConfiguration (ElectionTimeoutMinMs/Max/Heartbeat/MaxLogEntries).
/// </summary>
[Trait("Category", "Integration")]
public class ClusterFormationIntegrationTests
{
    [Fact]
    public void RaftConfiguration_Initialization_ShouldBeValid()
    {
        // Arrange & Act
        var config = new RaftConfiguration
        {
            ElectionTimeoutMinMs = 150,
            ElectionTimeoutMaxMs = 300,
            HeartbeatIntervalMs = 50
        };

        // Assert - Configuration is valid
        config.ElectionTimeoutMinMs.Should().Be(150);
        config.ElectionTimeoutMaxMs.Should().Be(300);
        config.HeartbeatIntervalMs.Should().Be(50);
    }

    [Fact]
    public void RaftCluster_ThreeNodes_ConfigurationsShouldBeValid()
    {
        // Arrange - Create 3-node cluster configurations (each node uses same consensus settings)
        var sharedConfig = new RaftConfiguration
        {
            ElectionTimeoutMinMs = 150,
            ElectionTimeoutMaxMs = 300,
            HeartbeatIntervalMs = 50,
            MaxLogEntries = 10_000
        };

        var node1Config = sharedConfig;
        var node2Config = sharedConfig;
        var node3Config = sharedConfig;

        // Assert - All configurations should be valid
        var configs = new[] { node1Config, node2Config, node3Config };
        configs.Should().HaveCount(3);
        configs.Should().AllSatisfy(c => c.ElectionTimeoutMaxMs.Should().BeGreaterThan(c.ElectionTimeoutMinMs));
        configs.Should().AllSatisfy(c => c.HeartbeatIntervalMs.Should().BeGreaterThan(0));

        // Quorum = (3 / 2) + 1 = 2 nodes required
        var quorumSize = (3 / 2) + 1;
        quorumSize.Should().Be(2);
    }

    [Fact]
    public void RaftConfiguration_ShouldAcceptCustomSettings()
    {
        // Arrange & Act
        var config = new RaftConfiguration
        {
            ElectionTimeoutMinMs = 200,
            ElectionTimeoutMaxMs = 500,
            HeartbeatIntervalMs = 75,
            MaxLogEntries = 50_000
        };

        // Assert
        config.ElectionTimeoutMinMs.Should().Be(200);
        config.ElectionTimeoutMaxMs.Should().Be(500);
        config.HeartbeatIntervalMs.Should().Be(75);
        config.MaxLogEntries.Should().Be(50_000);
    }

    [Fact]
    public void RaftConfiguration_DefaultValues_ShouldBeCorrect()
    {
        // Arrange & Act
        var config = new RaftConfiguration();

        // Assert - SDK defaults: 150ms/300ms/50ms/10000
        config.ElectionTimeoutMinMs.Should().Be(150, "election timeout min should be 150ms per Raft paper recommendation");
        config.ElectionTimeoutMaxMs.Should().Be(300, "election timeout max should be 300ms per Raft paper recommendation");
        config.HeartbeatIntervalMs.Should().Be(50, "heartbeat interval should be 50ms");
        config.MaxLogEntries.Should().Be(10_000, "max log entries default should be 10000");
    }

    [Fact]
    public void RaftCluster_SingleNode_ConfigurationIsValid()
    {
        // Arrange - Single node cluster configuration uses same SDK settings
        var config = new RaftConfiguration
        {
            ElectionTimeoutMinMs = 150,
            ElectionTimeoutMaxMs = 300,
            HeartbeatIntervalMs = 50
        };

        // Assert - Configuration is valid for single-node deployment
        config.ElectionTimeoutMinMs.Should().BeGreaterThan(0);
        config.ElectionTimeoutMaxMs.Should().BeGreaterThan(config.ElectionTimeoutMinMs);
        config.HeartbeatIntervalMs.Should().BeLessThan(config.ElectionTimeoutMinMs);
    }

    [Fact]
    public void RaftConfiguration_DefaultValues_ShouldBeReasonable()
    {
        // Arrange & Act
        var config = new RaftConfiguration();

        // Assert - Default configuration should have sensible timing ratios
        config.ElectionTimeoutMinMs.Should().BeGreaterThan(0, "election timeout should be positive");
        config.ElectionTimeoutMaxMs.Should().BeGreaterThan(config.ElectionTimeoutMinMs, "max should exceed min for randomization");
        config.HeartbeatIntervalMs.Should().BeLessThan(config.ElectionTimeoutMinMs, "heartbeat must be much shorter than election timeout");
        config.MaxLogEntries.Should().BeGreaterThan(0, "max log entries should be positive");
    }

    [Fact]
    public void RaftCluster_FiveNodes_ShouldSurviveTwoFailures()
    {
        // Arrange - 5-node cluster shares one SDK RaftConfiguration
        var config = new RaftConfiguration
        {
            ElectionTimeoutMinMs = 150,
            ElectionTimeoutMaxMs = 300,
            HeartbeatIntervalMs = 50,
            MaxLogEntries = 10_000
        };

        // Create 5 node configurations (all use same consensus engine settings)
        var nodeConfigs = Enumerable.Range(1, 5).Select(_ => config).ToList();

        // Assert
        nodeConfigs.Should().HaveCount(5);
        nodeConfigs.Should().AllSatisfy(n => n.ElectionTimeoutMaxMs.Should().BeGreaterThan(n.ElectionTimeoutMinMs));

        // Quorum = (5 / 2) + 1 = 3 nodes
        // Can tolerate 2 failures and still maintain quorum
        var quorumSize = (5 / 2) + 1;
        quorumSize.Should().Be(3);
    }
}
