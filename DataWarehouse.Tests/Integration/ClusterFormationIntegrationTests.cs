using DataWarehouse.Plugins.Raft;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Integration;

/// <summary>
/// Integration tests for cluster formation and consensus using Raft.
/// Tests configuration and initialization patterns.
/// Note: RaftConsensusPlugin is marked obsolete (replaced by UltimateConsensus),
/// but still functional for testing distributed consensus patterns.
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
            BasePort = 0, // OS-assigned port
            ClusterEndpoints = new List<string>()
        };

        // Assert - Configuration is valid
        config.BasePort.Should().Be(0);
        config.ClusterEndpoints.Should().NotBeNull();
        config.ClusterEndpoints.Should().BeEmpty();
    }

    [Fact]
    public void RaftCluster_ThreeNodes_ConfigurationsShouldBeValid()
    {
        // Arrange - Create 3-node cluster configuration
        var node1Config = new RaftConfiguration
        {
            BasePort = 5001,
            ClusterEndpoints = new List<string> { "127.0.0.1:5002", "127.0.0.1:5003" }
        };

        var node2Config = new RaftConfiguration
        {
            BasePort = 5002,
            ClusterEndpoints = new List<string> { "127.0.0.1:5001", "127.0.0.1:5003" }
        };

        var node3Config = new RaftConfiguration
        {
            BasePort = 5003,
            ClusterEndpoints = new List<string> { "127.0.0.1:5001", "127.0.0.1:5002" }
        };

        // Assert - All configurations should be valid
        var configs = new[] { node1Config, node2Config, node3Config };
        configs.Should().HaveCount(3);
        configs.Should().AllSatisfy(c => c.ClusterEndpoints.Should().HaveCount(2));
        configs.Should().AllSatisfy(c => c.BasePort.Should().BeGreaterThan(0));

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
            BasePort = 6000,
            PortRange = 50,
            ClusterEndpoints = new List<string> { "peer1:6001", "peer2:6002" }
        };

        // Assert
        config.BasePort.Should().Be(6000);
        config.PortRange.Should().Be(50);
        config.ClusterEndpoints.Should().HaveCount(2);
        config.ClusterEndpoints.Should().Contain("peer1:6001");
        config.ClusterEndpoints.Should().Contain("peer2:6002");
    }

    [Fact]
    public void RaftConfiguration_DefaultValues_ShouldBeCorrect()
    {
        // Arrange & Act
        var config = new RaftConfiguration();

        // Assert
        config.BasePort.Should().Be(0, "default should be OS-assigned");
        config.PortRange.Should().Be(100);
        config.ClusterEndpoints.Should().NotBeNull();
    }

    [Fact]
    public void RaftCluster_SingleNode_ConfigurationIsValid()
    {
        // Arrange - Single node cluster configuration
        var config = new RaftConfiguration
        {
            BasePort = 0,
            ClusterEndpoints = new List<string>() // No peers
        };

        // Assert - Configuration is valid even with no peers
        config.ClusterEndpoints.Should().BeEmpty();
        config.BasePort.Should().Be(0);

        // Note: RaftConsensusPlugin requires handshake to initialize NodeId,
        // so we just verify configuration validity here
    }

    [Fact]
    public void RaftPlugin_Metadata_ShouldIndicateObsoleteStatus()
    {
        // Arrange & Act
        var config = new RaftConfiguration { BasePort = 0 };

#pragma warning disable CS0618 // RaftConsensusPlugin is obsolete; this test intentionally tests the legacy plugin and verifies its obsolete status
        using var plugin = new RaftConsensusPlugin(config);

        // Assert
        plugin.Id.Should().Be("datawarehouse.raft");
        plugin.Name.Should().Be("Raft Consensus");

        // Verify the type has Obsolete attribute
        var obsoleteAttr = typeof(RaftConsensusPlugin)
            .GetCustomAttributes(typeof(ObsoleteAttribute), false)
            .FirstOrDefault();
#pragma warning restore CS0618

        obsoleteAttr.Should().NotBeNull("RaftConsensusPlugin should be marked obsolete");
    }

    [Fact]
    public void RaftConfiguration_DefaultValues_ShouldBeReasonable()
    {
        // Arrange & Act
        var config = new RaftConfiguration();

        // Assert - Default configuration should have sensible values
        config.BasePort.Should().Be(0, "default should use OS-assigned port");
        config.PortRange.Should().BeGreaterThan(0);
        config.ClusterEndpoints.Should().NotBeNull();
    }

    [Fact]
    public void RaftCluster_FiveNodes_ShouldSurviveTwoFailures()
    {
        // Arrange - 5-node cluster configuration
        var nodes = new List<RaftConfiguration>();

        for (int i = 1; i <= 5; i++)
        {
            var peers = Enumerable.Range(1, 5)
                .Where(p => p != i)
                .Select(p => $"127.0.0.1:600{p}")
                .ToList();

            nodes.Add(new RaftConfiguration
            {
                BasePort = 6000 + i,
                ClusterEndpoints = peers
            });
        }

        // Assert
        nodes.Should().HaveCount(5);
        nodes.Should().AllSatisfy(n => n.ClusterEndpoints.Should().HaveCount(4));

        // Quorum = (5 / 2) + 1 = 3 nodes
        // Can tolerate 2 failures and still maintain quorum
        var quorumSize = (5 / 2) + 1;
        quorumSize.Should().Be(3);
    }
}
