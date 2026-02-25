using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Storage.Placement;
using Xunit;

namespace DataWarehouse.Tests.Storage.ZeroGravity;

/// <summary>
/// Tests for gravity-aware placement optimizer.
/// Verifies: gravity scoring dimensions, composite calculation, batch scoring,
/// placement optimization with gravity influence, and weight normalization/presets.
/// </summary>
public sealed class GravityOptimizerTests
{
    #region Helpers

    private static NodeDescriptor CreateNode(string id, string zone, string rack, string host, double weight = 1.0)
    {
        return new NodeDescriptor(
            NodeId: id,
            Zone: zone,
            Rack: rack,
            Host: host,
            Tags: new Dictionary<string, string>(),
            StorageClasses: new[] { StorageClass.SSD },
            CapacityBytes: 1_000_000_000_000L,
            UsedBytes: 0L,
            Weight: weight);
    }

    private static GravityAwarePlacementOptimizer CreateOptimizer(
        GravityScoringWeights? weights = null,
        Func<string, CancellationToken, Task<AccessMetrics>>? accessProvider = null,
        Func<string, CancellationToken, Task<ColocationMetrics>>? colocationProvider = null,
        Func<string, CancellationToken, Task<CostMetrics>>? costProvider = null,
        Func<string, CancellationToken, Task<LatencyMetrics>>? latencyProvider = null,
        Func<string, CancellationToken, Task<ComplianceMetrics>>? complianceProvider = null)
    {
        return new GravityAwarePlacementOptimizer(
            crushAlgorithm: new CrushPlacementAlgorithm(),
            weights: weights,
            accessMetricsProvider: accessProvider,
            colocationProvider: colocationProvider,
            costProvider: costProvider,
            latencyProvider: latencyProvider,
            complianceProvider: complianceProvider);
    }

    #endregion

    #region ComputeGravity

    [Fact]
    public async Task ComputeGravity_HighAccess_HighScore()
    {
        var optimizer = CreateOptimizer(
            accessProvider: (_, _) => Task.FromResult(new AccessMetrics
            {
                ReadsPerHour = 500,
                WritesPerHour = 500,
                LastAccessUtc = DateTimeOffset.UtcNow
            }),
            colocationProvider: (_, _) => Task.FromResult(new ColocationMetrics
            {
                ColocatedDependencies = 5
            }),
            costProvider: (_, _) => Task.FromResult(new CostMetrics
            {
                CurrentNodeId = "node-1",
                EgressCostPerGB = 0.09m
            }),
            latencyProvider: (_, _) => Task.FromResult(new LatencyMetrics
            {
                CurrentLatencyMs = 10.0
            }),
            complianceProvider: (_, _) => Task.FromResult(new ComplianceMetrics
            {
                InComplianceRegion = true
            }));

        var score = await optimizer.ComputeGravityAsync("hot-object");

        // High access (1000/hr), good colocation, compliance, low latency -> high composite
        Assert.True(score.CompositeScore > 0.5,
            $"Expected composite > 0.5, got {score.CompositeScore:F3}");
        Assert.Equal(1000, score.AccessFrequency);
    }

    [Fact]
    public async Task ComputeGravity_NoAccess_LowScore()
    {
        var optimizer = CreateOptimizer(
            accessProvider: (_, _) => Task.FromResult(new AccessMetrics
            {
                ReadsPerHour = 0,
                WritesPerHour = 0
            }),
            colocationProvider: (_, _) => Task.FromResult(new ColocationMetrics
            {
                ColocatedDependencies = 0
            }),
            costProvider: (_, _) => Task.FromResult(new CostMetrics
            {
                EgressCostPerGB = 0m
            }),
            latencyProvider: (_, _) => Task.FromResult(new LatencyMetrics
            {
                CurrentLatencyMs = 0
            }),
            complianceProvider: (_, _) => Task.FromResult(new ComplianceMetrics
            {
                InComplianceRegion = false
            }));

        var score = await optimizer.ComputeGravityAsync("cold-object");

        // Zero access, no colocation, no compliance, no cost -> very low composite
        Assert.True(score.CompositeScore < 0.3,
            $"Expected composite < 0.3, got {score.CompositeScore:F3}");
    }

    [Fact]
    public async Task ComputeGravity_ComplianceRegion_FullWeight()
    {
        // Use compliance-first weights
        var optimizer = CreateOptimizer(
            weights: GravityScoringWeights.ComplianceFirst,
            complianceProvider: (_, _) => Task.FromResult(new ComplianceMetrics
            {
                InComplianceRegion = true
            }));

        var score = await optimizer.ComputeGravityAsync("compliance-object");

        // Compliance region = 1.0 * 0.60 weight = 0.60 contribution
        // With default values for other dimensions, should be significant
        Assert.True(score.ComplianceWeight == 1.0,
            $"Expected compliance weight 1.0, got {score.ComplianceWeight}");
    }

    [Fact]
    public async Task ComputeGravity_NoProviders_ReturnsDefaultScore()
    {
        // No metric providers configured at all
        var optimizer = CreateOptimizer();

        var score = await optimizer.ComputeGravityAsync("default-object");

        // Should return mid-range defaults without throwing
        Assert.NotNull(score);
        Assert.Equal("default-object", score.ObjectKey);
        // Default composite should be between 0 and 1
        Assert.InRange(score.CompositeScore, 0.0, 1.0);
    }

    #endregion

    #region Batch Scoring

    [Fact]
    public async Task BatchScoring_ProcessesInParallel()
    {
        var optimizer = CreateOptimizer(
            accessProvider: (key, _) => Task.FromResult(new AccessMetrics
            {
                ReadsPerHour = key.GetHashCode() % 100,
                WritesPerHour = 0
            }));

        var keys = Enumerable.Range(0, 100).Select(i => $"batch-obj-{i}").ToList();

        var scores = await optimizer.ComputeGravityBatchAsync(keys);

        Assert.Equal(100, scores.Count);
        Assert.All(scores, s => Assert.InRange(s.CompositeScore, 0.0, 1.0));
    }

    #endregion

    #region OptimizePlacement

    [Fact]
    public async Task OptimizePlacement_HighGravity_PrefersCurrentNode()
    {
        var nodes = new List<NodeDescriptor>
        {
            CreateNode("node-current", "zone-a", "rack-1", "host-1"),
            CreateNode("node-other-1", "zone-b", "rack-2", "host-2"),
            CreateNode("node-other-2", "zone-c", "rack-3", "host-3"),
        };

        var optimizer = CreateOptimizer();
        var target = new PlacementTarget("sticky-object", 1024, 3);

        // Create a high gravity score with a known current node
        var gravity = new DataGravityScore(
            ObjectKey: "sticky-object",
            CurrentNode: "node-current",
            AccessFrequency: 1000,
            LastAccessUtc: DateTimeOffset.UtcNow,
            ColocatedDependencies: 5,
            EgressCostPerGB: 0.09m,
            LatencyMs: 5.0,
            ComplianceWeight: 1.0,
            CompositeScore: 0.85); // > 0.7 threshold

        var decision = await optimizer.OptimizePlacementAsync(target, nodes, gravity);

        // If current node is in the CRUSH set, it should be promoted to primary
        if (decision.TargetNodes.Any(n => n.NodeId == "node-current"))
        {
            Assert.Equal("node-current", decision.PrimaryNode.NodeId);
        }
    }

    #endregion

    #region Scoring Weights

    [Fact]
    public void ScoringWeights_Normalization_SumsToOne()
    {
        var weights = new GravityScoringWeights
        {
            AccessFrequency = 3.0,
            Colocation = 2.0,
            EgressCost = 1.5,
            Latency = 1.5,
            Compliance = 2.0
        };

        var normalized = weights.Normalize();
        double sum = normalized.AccessFrequency + normalized.Colocation
            + normalized.EgressCost + normalized.Latency + normalized.Compliance;

        Assert.Equal(1.0, sum, precision: 10);
    }

    [Fact]
    public void ScoringWeights_Presets_AreValid()
    {
        Assert.True(GravityScoringWeights.Default.IsValid());
        Assert.True(GravityScoringWeights.CostOptimized.IsValid());
        Assert.True(GravityScoringWeights.PerformanceOptimized.IsValid());
        Assert.True(GravityScoringWeights.ComplianceFirst.IsValid());
    }

    #endregion
}
