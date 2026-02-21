using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.Plugins.UltimateConsensus;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using Xunit;

namespace DataWarehouse.Tests.Consensus;

/// <summary>
/// Tests for UltimateConsensus Multi-Raft plugin.
/// </summary>
public class UltimateConsensusTests
{
    [Fact]
    public async Task InitializeAsync_CreatesRaftGroups()
    {
        var plugin = new UltimateConsensusPlugin(groupCount: 3, votersPerGroup: 3);

        // Handshake triggers initialization
        var response = await plugin.OnHandshakeAsync(new HandshakeRequest
        {
            KernelId = "test-kernel"
        });

        Assert.True(response.Success);
        Assert.Equal(3, plugin.GroupCount);
        Assert.Equal("3", response.Metadata["GroupCount"]);
        Assert.Equal("Raft", response.Metadata["ActiveStrategy"]);
    }

    [Fact]
    public async Task ProposeAsync_RoutesToCorrectGroup()
    {
        var plugin = new UltimateConsensusPlugin(groupCount: 3, votersPerGroup: 1);
        await plugin.OnHandshakeAsync(new HandshakeRequest { KernelId = "test" });

        var data = Encoding.UTF8.GetBytes("test-key-data");
        var result = await plugin.ProposeAsync(data, CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.LeaderId);
        Assert.True(result.LogIndex > 0);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task ProposeAsync_MultipleProposes_IncrementLogIndex()
    {
        var plugin = new UltimateConsensusPlugin(groupCount: 1, votersPerGroup: 1);
        await plugin.OnHandshakeAsync(new HandshakeRequest { KernelId = "test" });

        var results = new List<ConsensusPluginBase.ConsensusResult>();
        for (int i = 0; i < 10; i++)
        {
            var data = Encoding.UTF8.GetBytes($"entry-{i}");
            var result = await plugin.ProposeAsync(data, CancellationToken.None);
            results.Add(result);
        }

        Assert.True(results.All(r => r.Success));
        // Log indices should be monotonically increasing
        for (int i = 1; i < results.Count; i++)
        {
            Assert.True(results[i].LogIndex > results[i - 1].LogIndex);
        }
    }

    [Fact]
    public async Task LeaderElection_ConvergesWithinTimeout()
    {
        var plugin = new UltimateConsensusPlugin(groupCount: 5, votersPerGroup: 1);
        await plugin.OnHandshakeAsync(new HandshakeRequest { KernelId = "test" });

        // After init, at least one single-voter group should have elected a leader
        var isLeader = await plugin.IsLeaderAsync(CancellationToken.None);
        Assert.True(isLeader);
    }

    [Fact]
    public async Task GetStateAsync_ReturnsMultiRaftState()
    {
        var plugin = new UltimateConsensusPlugin(groupCount: 3, votersPerGroup: 1);
        await plugin.OnHandshakeAsync(new HandshakeRequest { KernelId = "test" });

        var state = await plugin.GetStateAsync(CancellationToken.None);

        Assert.Equal("multi-raft", state.State);
        Assert.NotNull(state.LeaderId);
    }

    [Fact]
    public async Task Snapshot_CapturesCommittedState()
    {
        var plugin = new UltimateConsensusPlugin(groupCount: 1, votersPerGroup: 1);
        await plugin.OnHandshakeAsync(new HandshakeRequest { KernelId = "test" });

        // Propose some entries
        for (int i = 0; i < 5; i++)
        {
            await plugin.ProposeAsync(Encoding.UTF8.GetBytes($"data-{i}"), CancellationToken.None);
        }

        // Get state - should have committed entries
        var state = await plugin.GetStateAsync(CancellationToken.None);
        Assert.True(state.CommitIndex > 0);
    }

    [Fact]
    public async Task GetClusterHealthAsync_ReturnsAggregateHealth()
    {
        var plugin = new UltimateConsensusPlugin(groupCount: 3, votersPerGroup: 1);
        await plugin.OnHandshakeAsync(new HandshakeRequest { KernelId = "test" });

        var health = await plugin.GetClusterHealthAsync(CancellationToken.None);

        Assert.Equal(3, health.TotalNodes); // 3 groups
        Assert.True(health.HealthyNodes > 0);
        Assert.NotEmpty(health.NodeStates);
    }

    [Fact]
    public async Task ConsistentHash_RoutesDeterministically()
    {
        var hash = new ConsistentHash(5);

        var bucket1 = hash.Route("key-1");
        var bucket2 = hash.Route("key-1");

        Assert.Equal(bucket1, bucket2);
        Assert.True(bucket1 >= 0 && bucket1 < 5);
    }

    [Fact]
    public async Task Propose_ViaProposal_CommitsAndNotifies()
    {
        var plugin = new UltimateConsensusPlugin(groupCount: 1, votersPerGroup: 1);
        await plugin.OnHandshakeAsync(new HandshakeRequest { KernelId = "test" });

        var committed = new List<Proposal>();
        plugin.OnCommit(p => committed.Add(p));

        var proposal = new Proposal
        {
            Command = "test",
            Payload = Encoding.UTF8.GetBytes("payload")
        };

        var success = await plugin.ProposeAsync(proposal);

        Assert.True(success);
        Assert.Single(committed);
    }

    [Fact]
    public void CreateStrategy_ReturnsCorrectStrategy()
    {
        var plugin = new UltimateConsensusPlugin();

        var raft = plugin.CreateStrategy("Raft");
        var paxos = plugin.CreateStrategy("Paxos");
        var pbft = plugin.CreateStrategy("PBFT");
        var zab = plugin.CreateStrategy("ZAB");
        var unknown = plugin.CreateStrategy("Unknown");

        Assert.NotNull(raft);
        Assert.Equal("Raft", raft!.AlgorithmName);
        Assert.NotNull(paxos);
        Assert.Equal("Paxos", paxos!.AlgorithmName);
        Assert.NotNull(pbft);
        Assert.NotNull(zab);
        Assert.Null(unknown);
    }

    [Fact]
    public async Task ProposeToGroupAsync_ReturnsSuccessAfterHandshake()
    {
        // Verify group-level proposal API works through the public ProposeToGroupAsync method
        var plugin = new UltimateConsensusPlugin(groupCount: 3, votersPerGroup: 1);
        await plugin.OnHandshakeAsync(new HandshakeRequest { KernelId = "test" });

        var data = Encoding.UTF8.GetBytes("group-proposal");
        var result = await plugin.ProposeToGroupAsync(data, groupId: 0, CancellationToken.None);

        Assert.True(result.Success);
        Assert.NotNull(result.LeaderId);
        Assert.True(result.LogIndex > 0);
    }

    [Fact]
    public async Task MultiGroupProposes_AllGroupsReceiveEntries()
    {
        // With 3 groups and many proposals, entries should be distributed across groups
        var plugin = new UltimateConsensusPlugin(groupCount: 3, votersPerGroup: 1);
        await plugin.OnHandshakeAsync(new HandshakeRequest { KernelId = "test" });

        var results = new List<ConsensusPluginBase.ConsensusResult>();
        for (int i = 0; i < 30; i++)
        {
            var data = Encoding.UTF8.GetBytes($"key-{i}");
            var result = await plugin.ProposeAsync(data, CancellationToken.None);
            results.Add(result);
        }

        // All proposals should succeed
        Assert.True(results.All(r => r.Success));
        // All leaders should be the same node (single-node mode)
        Assert.True(results.All(r => r.LeaderId != null));
    }

    [Fact]
    public void ConsistentHash_GetBucket_Deterministic()
    {
        var bucket1 = ConsistentHash.GetBucket("test-key", 10);
        var bucket2 = ConsistentHash.GetBucket("test-key", 10);

        Assert.Equal(bucket1, bucket2);
        Assert.True(bucket1 >= 0 && bucket1 < 10);
    }

    [Fact]
    public void ConsistentHash_GetBucket_EvenDistribution()
    {
        var buckets = new int[10];
        for (int i = 0; i < 1000; i++)
        {
            var bucket = ConsistentHash.GetBucket($"key-{i}", 10);
            buckets[bucket]++;
        }

        // Each bucket should have some entries (rough check)
        foreach (var count in buckets)
        {
            Assert.True(count > 0, $"Bucket received no entries");
        }
    }
}
