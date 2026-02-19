using System.Collections.Concurrent;
using System.Diagnostics;
using DataWarehouse.SDK.Contracts.Consciousness;

namespace DataWarehouse.Plugins.UltimateDataLineage.Strategies;

/// <summary>
/// Represents a node in the consciousness-aware lineage graph, enriched with consciousness score data.
/// </summary>
/// <param name="ObjectId">Unique identifier of the data object.</param>
/// <param name="Score">The consciousness score for this object, if available.</param>
/// <param name="Depth">BFS depth from the root node.</param>
/// <param name="UpstreamIds">Identifiers of upstream (source) objects.</param>
/// <param name="DownstreamIds">Identifiers of downstream (dependent) objects.</param>
public sealed record ConsciousnessLineageNode(
    string ObjectId,
    ConsciousnessScore? Score,
    int Depth,
    List<string> UpstreamIds,
    List<string> DownstreamIds);

/// <summary>
/// Result of a consciousness-aware impact analysis, detailing how many downstream objects
/// are affected by changes to a source object and their grade distribution.
/// </summary>
/// <param name="SourceObjectId">The object whose impact is being analyzed.</param>
/// <param name="TotalAffected">Total number of affected downstream objects.</param>
/// <param name="DirectDependents">Count of objects directly downstream (1 hop).</param>
/// <param name="TransitiveDependents">Count of objects indirectly downstream (2+ hops).</param>
/// <param name="AffectedByGrade">Distribution of affected objects by consciousness grade.</param>
/// <param name="AffectedNodes">Full list of affected nodes with consciousness data.</param>
/// <param name="TraversalTime">Time taken to perform the BFS traversal.</param>
public sealed record ConsciousnessImpactResult(
    string SourceObjectId,
    int TotalAffected,
    int DirectDependents,
    int TransitiveDependents,
    Dictionary<ConsciousnessGrade, int> AffectedByGrade,
    List<ConsciousnessLineageNode> AffectedNodes,
    TimeSpan TraversalTime);

/// <summary>
/// Describes a propagation event where a consciousness score change at a source object
/// affects a downstream dependent through the lineage graph.
/// </summary>
/// <param name="SourceObjectId">The object whose score changed.</param>
/// <param name="AffectedObjectId">The downstream object affected by propagation.</param>
/// <param name="OldScore">The old composite score of the affected object's lineage dimension.</param>
/// <param name="NewScore">The new composite score after propagation.</param>
/// <param name="OldGrade">The grade before propagation.</param>
/// <param name="NewGrade">The grade after propagation.</param>
/// <param name="HopDistance">Number of hops from the source to the affected object.</param>
public sealed record LineagePropagationEvent(
    string SourceObjectId,
    string AffectedObjectId,
    double OldScore,
    double NewScore,
    ConsciousnessGrade OldGrade,
    ConsciousnessGrade NewGrade,
    int HopDistance);

/// <summary>
/// Multi-hop BFS lineage traversal strategy that goes beyond single-hop to discover
/// the full upstream or downstream dependency tree with cycle detection and configurable max depth.
/// </summary>
/// <remarks>
/// Addresses the STATE.md limitation of single-hop lineage. Uses a standard BFS queue with
/// a visited set to prevent cycles in the lineage graph. Each discovered node is enriched
/// with its consciousness score from the metadata store.
/// </remarks>
internal sealed class MultiHopLineageBfsStrategy : LineageStrategyBase
{
    private readonly ConcurrentDictionary<string, ConsciousnessScore?> _scoreCache = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _upstreamLinks = new();
    private readonly ConcurrentDictionary<string, HashSet<string>> _downstreamLinks = new();

    /// <inheritdoc/>
    public override string StrategyId => "consciousness-multihop-bfs";

    /// <inheritdoc/>
    public override string DisplayName => "Multi-Hop Consciousness BFS";

    /// <inheritdoc/>
    public override LineageCategory Category => LineageCategory.Impact;

    /// <inheritdoc/>
    public override LineageStrategyCapabilities Capabilities => new()
    {
        SupportsUpstream = true,
        SupportsDownstream = true,
        SupportsTransformations = false,
        SupportsSchemaEvolution = false,
        SupportsImpactAnalysis = true,
        SupportsVisualization = true,
        SupportsRealTime = true
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Multi-hop BFS lineage traversal strategy that discovers the full dependency tree " +
        "with consciousness score enrichment at each node. Supports configurable max depth, " +
        "cycle detection, and both upstream and downstream traversal directions.";

    /// <inheritdoc/>
    public override string[] Tags => ["multi-hop", "bfs", "consciousness", "traversal", "cycle-detection"];

    /// <summary>
    /// Registers a consciousness score for a data object, enabling score lookups during BFS traversal.
    /// </summary>
    /// <param name="objectId">The object identifier.</param>
    /// <param name="score">The consciousness score, or null if not yet scored.</param>
    public void RegisterScore(string objectId, ConsciousnessScore? score)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        _scoreCache[objectId] = score;
    }

    /// <inheritdoc/>
    public override Task TrackAsync(ProvenanceRecord record, CancellationToken ct = default)
    {
        base.TrackAsync(record, ct);

        AddNode(new LineageNode
        {
            NodeId = record.DataObjectId,
            Name = record.DataObjectId,
            NodeType = "dataset"
        });

        if (record.SourceObjects != null)
        {
            foreach (var source in record.SourceObjects)
            {
                AddNode(new LineageNode
                {
                    NodeId = source,
                    Name = source,
                    NodeType = "dataset"
                });

                var uplinks = _upstreamLinks.GetOrAdd(record.DataObjectId, _ => new HashSet<string>());
                var downlinks = _downstreamLinks.GetOrAdd(source, _ => new HashSet<string>());
                lock (uplinks) uplinks.Add(source);
                lock (downlinks) downlinks.Add(record.DataObjectId);

                AddEdge(new LineageEdge
                {
                    EdgeId = Guid.NewGuid().ToString("N"),
                    SourceNodeId = source,
                    TargetNodeId = record.DataObjectId,
                    EdgeType = "consciousness-bfs"
                });
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Performs a multi-hop BFS traversal from the specified object, returning all reachable
    /// nodes enriched with consciousness scores within the given depth.
    /// </summary>
    /// <param name="objectId">The starting object for traversal.</param>
    /// <param name="maxDepth">Maximum BFS depth (default 10).</param>
    /// <param name="upstream">True for upstream traversal (sources), false for downstream (dependents).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Ordered list of consciousness lineage nodes by BFS depth.</returns>
    public Task<List<ConsciousnessLineageNode>> TraverseBfsAsync(
        string objectId, int maxDepth = 10, bool upstream = false, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        if (maxDepth < 0) maxDepth = 0;

        var visited = new HashSet<string>();
        var result = new List<ConsciousnessLineageNode>();
        var queue = new Queue<(string id, int depth)>();
        queue.Enqueue((objectId, 0));

        var linkMap = upstream ? _upstreamLinks : _downstreamLinks;

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var (currentId, depth) = queue.Dequeue();
            if (depth > maxDepth || !visited.Add(currentId)) continue;

            // Look up consciousness score for this node
            _scoreCache.TryGetValue(currentId, out var score);

            // Determine upstream and downstream IDs for this node
            var nodeUpstream = new List<string>();
            var nodeDownstream = new List<string>();

            if (_upstreamLinks.TryGetValue(currentId, out var ups))
            {
                lock (ups) { nodeUpstream.AddRange(ups); }
            }
            if (_downstreamLinks.TryGetValue(currentId, out var downs))
            {
                lock (downs) { nodeDownstream.AddRange(downs); }
            }

            result.Add(new ConsciousnessLineageNode(
                ObjectId: currentId,
                Score: score,
                Depth: depth,
                UpstreamIds: nodeUpstream,
                DownstreamIds: nodeDownstream));

            // Enqueue neighbors in the traversal direction
            if (linkMap.TryGetValue(currentId, out var neighbors))
            {
                HashSet<string> snapshot;
                lock (neighbors) { snapshot = new HashSet<string>(neighbors); }

                foreach (var neighborId in snapshot)
                {
                    if (!visited.Contains(neighborId))
                    {
                        queue.Enqueue((neighborId, depth + 1));
                    }
                }
            }
        }

        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public override Task<LineageGraph> GetUpstreamAsync(string nodeId, int maxDepth = 10, CancellationToken ct = default)
    {
        var visited = new HashSet<string>();
        var nodes = new List<LineageNode>();
        var edges = new List<LineageEdge>();
        var queue = new Queue<(string id, int depth)>();
        queue.Enqueue((nodeId, 0));

        while (queue.Count > 0)
        {
            var (currentId, depth) = queue.Dequeue();
            if (depth > maxDepth || !visited.Add(currentId)) continue;

            if (_nodes.TryGetValue(currentId, out var node))
                nodes.Add(node);
            else
                nodes.Add(new LineageNode { NodeId = currentId, Name = currentId, NodeType = "dataset" });

            if (_upstreamLinks.TryGetValue(currentId, out var uplinks))
            {
                HashSet<string> snapshot;
                lock (uplinks) { snapshot = new HashSet<string>(uplinks); }

                foreach (var upId in snapshot)
                {
                    if (!visited.Contains(upId))
                    {
                        queue.Enqueue((upId, depth + 1));
                        edges.Add(new LineageEdge
                        {
                            EdgeId = $"{upId}->{currentId}",
                            SourceNodeId = upId,
                            TargetNodeId = currentId,
                            EdgeType = "consciousness-bfs"
                        });
                    }
                }
            }
        }

        return Task.FromResult(new LineageGraph
        {
            RootNodeId = nodeId,
            Nodes = nodes.AsReadOnly(),
            Edges = edges.AsReadOnly(),
            Depth = maxDepth,
            UpstreamCount = Math.Max(0, nodes.Count - 1)
        });
    }

    /// <inheritdoc/>
    public override Task<LineageGraph> GetDownstreamAsync(string nodeId, int maxDepth = 10, CancellationToken ct = default)
    {
        var visited = new HashSet<string>();
        var nodes = new List<LineageNode>();
        var edges = new List<LineageEdge>();
        var queue = new Queue<(string id, int depth)>();
        queue.Enqueue((nodeId, 0));

        while (queue.Count > 0)
        {
            var (currentId, depth) = queue.Dequeue();
            if (depth > maxDepth || !visited.Add(currentId)) continue;

            if (_nodes.TryGetValue(currentId, out var node))
                nodes.Add(node);
            else
                nodes.Add(new LineageNode { NodeId = currentId, Name = currentId, NodeType = "dataset" });

            if (_downstreamLinks.TryGetValue(currentId, out var downlinks))
            {
                HashSet<string> snapshot;
                lock (downlinks) { snapshot = new HashSet<string>(downlinks); }

                foreach (var downId in snapshot)
                {
                    if (!visited.Contains(downId))
                    {
                        queue.Enqueue((downId, depth + 1));
                        edges.Add(new LineageEdge
                        {
                            EdgeId = $"{currentId}->{downId}",
                            SourceNodeId = currentId,
                            TargetNodeId = downId,
                            EdgeType = "consciousness-bfs"
                        });
                    }
                }
            }
        }

        return Task.FromResult(new LineageGraph
        {
            RootNodeId = nodeId,
            Nodes = nodes.AsReadOnly(),
            Edges = edges.AsReadOnly(),
            Depth = maxDepth,
            DownstreamCount = Math.Max(0, nodes.Count - 1)
        });
    }
}

/// <summary>
/// Consciousness-aware lineage strategy that annotates the lineage graph with consciousness scores
/// at each node and performs impact analysis to identify critical dependency paths.
/// </summary>
/// <remarks>
/// Uses <see cref="MultiHopLineageBfsStrategy"/> for multi-hop traversal, then enriches
/// the results with consciousness grade distribution and identifies which high-value
/// downstream objects would be affected if a source object is purged or archived.
/// </remarks>
internal sealed class ConsciousnessAwareLineageStrategy : LineageStrategyBase
{
    private readonly MultiHopLineageBfsStrategy _bfsStrategy;

    /// <inheritdoc/>
    public override string StrategyId => "consciousness-aware-lineage";

    /// <inheritdoc/>
    public override string DisplayName => "Consciousness-Aware Lineage";

    /// <inheritdoc/>
    public override LineageCategory Category => LineageCategory.Impact;

    /// <inheritdoc/>
    public override LineageStrategyCapabilities Capabilities => new()
    {
        SupportsUpstream = true,
        SupportsDownstream = true,
        SupportsTransformations = false,
        SupportsSchemaEvolution = false,
        SupportsImpactAnalysis = true,
        SupportsVisualization = true,
        SupportsRealTime = true
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Consciousness-aware lineage strategy that annotates the lineage graph with consciousness " +
        "scores at each node and performs impact analysis identifying critical dependency paths. " +
        "Determines which high-value downstream objects are affected by changes to a source object.";

    /// <inheritdoc/>
    public override string[] Tags => ["consciousness", "impact-analysis", "lineage", "critical-path", "grade-distribution"];

    /// <summary>
    /// Initializes a new instance of the <see cref="ConsciousnessAwareLineageStrategy"/> class.
    /// </summary>
    /// <param name="bfsStrategy">The multi-hop BFS strategy used for graph traversal.</param>
    public ConsciousnessAwareLineageStrategy(MultiHopLineageBfsStrategy bfsStrategy)
    {
        _bfsStrategy = bfsStrategy ?? throw new ArgumentNullException(nameof(bfsStrategy));
    }

    /// <summary>
    /// Analyzes the consciousness impact of a source object by traversing all downstream dependents,
    /// computing grade distribution, and identifying critical dependency paths.
    /// </summary>
    /// <param name="objectId">The source object to analyze.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="ConsciousnessImpactResult"/> with full dependency analysis.</returns>
    public async Task<ConsciousnessImpactResult> AnalyzeImpactAsync(string objectId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);

        var stopwatch = Stopwatch.StartNew();

        // Traverse all downstream dependents using multi-hop BFS
        var allNodes = await _bfsStrategy.TraverseBfsAsync(objectId, maxDepth: 10, upstream: false, ct);
        stopwatch.Stop();

        // Exclude the root node itself from affected count
        var affectedNodes = allNodes.Where(n => n.ObjectId != objectId).ToList();

        // Compute grade distribution
        var affectedByGrade = new Dictionary<ConsciousnessGrade, int>();
        foreach (var grade in Enum.GetValues<ConsciousnessGrade>())
        {
            int count = affectedNodes.Count(n => n.Score?.Grade == grade);
            if (count > 0)
                affectedByGrade[grade] = count;
        }

        // Count unscored objects as Unknown
        int unscoredCount = affectedNodes.Count(n => n.Score == null);
        if (unscoredCount > 0)
        {
            affectedByGrade.TryGetValue(ConsciousnessGrade.Unknown, out int existing);
            affectedByGrade[ConsciousnessGrade.Unknown] = existing + unscoredCount;
        }

        // Separate direct (depth == 1) from transitive (depth > 1)
        int directDependents = affectedNodes.Count(n => n.Depth == 1);
        int transitiveDependents = affectedNodes.Count(n => n.Depth > 1);

        return new ConsciousnessImpactResult(
            SourceObjectId: objectId,
            TotalAffected: affectedNodes.Count,
            DirectDependents: directDependents,
            TransitiveDependents: transitiveDependents,
            AffectedByGrade: affectedByGrade,
            AffectedNodes: affectedNodes,
            TraversalTime: stopwatch.Elapsed);
    }

    /// <inheritdoc/>
    public override Task<LineageGraph> GetUpstreamAsync(string nodeId, int maxDepth = 10, CancellationToken ct = default)
        => _bfsStrategy.GetUpstreamAsync(nodeId, maxDepth, ct);

    /// <inheritdoc/>
    public override Task<LineageGraph> GetDownstreamAsync(string nodeId, int maxDepth = 10, CancellationToken ct = default)
        => _bfsStrategy.GetDownstreamAsync(nodeId, maxDepth, ct);
}

/// <summary>
/// Propagation strategy that spreads consciousness score changes through the lineage graph
/// to downstream dependents with exponential decay per hop.
/// </summary>
/// <remarks>
/// When a source object's consciousness score changes, this strategy computes the propagated
/// impact on all downstream dependents. Each hop reduces impact by 30% (decay factor 0.7):
/// direct dependents get full impact, 2-hop gets 70%, 3-hop gets 49%, etc.
/// Only publishes events for propagated deltas exceeding 5 points on the lineage dimension.
/// Does NOT automatically rescore objects; it publishes "consciousness.lineage.propagated" events
/// for the retroactive scorer to consume.
/// </remarks>
internal sealed class ConsciousnessImpactPropagationStrategy : LineageStrategyBase
{
    private const double DecayFactor = 0.7;
    private const double MinimumDeltaThreshold = 5.0;

    private readonly MultiHopLineageBfsStrategy _bfsStrategy;
    private readonly ConcurrentBag<LineagePropagationEvent> _propagationHistory = new();

    /// <inheritdoc/>
    public override string StrategyId => "consciousness-impact-propagation";

    /// <inheritdoc/>
    public override string DisplayName => "Consciousness Impact Propagation";

    /// <inheritdoc/>
    public override LineageCategory Category => LineageCategory.Impact;

    /// <inheritdoc/>
    public override LineageStrategyCapabilities Capabilities => new()
    {
        SupportsUpstream = false,
        SupportsDownstream = true,
        SupportsTransformations = false,
        SupportsSchemaEvolution = false,
        SupportsImpactAnalysis = true,
        SupportsVisualization = false,
        SupportsRealTime = true
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Consciousness impact propagation strategy that spreads score changes through the lineage " +
        "graph to downstream dependents with 30% decay per hop. Publishes propagation events for " +
        "significant changes (> 5 point delta) without automatically rescoring objects.";

    /// <inheritdoc/>
    public override string[] Tags => ["propagation", "consciousness", "decay", "lineage", "event-driven"];

    /// <summary>
    /// Initializes a new instance of the <see cref="ConsciousnessImpactPropagationStrategy"/> class.
    /// </summary>
    /// <param name="bfsStrategy">The multi-hop BFS strategy used for downstream traversal.</param>
    public ConsciousnessImpactPropagationStrategy(MultiHopLineageBfsStrategy bfsStrategy)
    {
        _bfsStrategy = bfsStrategy ?? throw new ArgumentNullException(nameof(bfsStrategy));
    }

    /// <summary>
    /// Gets all propagation events that have been generated by this strategy.
    /// </summary>
    public IReadOnlyList<LineagePropagationEvent> PropagationHistory =>
        _propagationHistory.ToList().AsReadOnly();

    /// <summary>
    /// Propagates a consciousness score change from a source object to all downstream dependents,
    /// applying exponential decay per hop.
    /// </summary>
    /// <param name="sourceObjectId">The object whose consciousness score changed.</param>
    /// <param name="oldScore">The previous composite score.</param>
    /// <param name="newScore">The new composite score.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of propagation events for all affected downstream objects where the propagated delta exceeds the threshold.</returns>
    public async Task<List<LineagePropagationEvent>> PropagateAsync(
        string sourceObjectId, double oldScore, double newScore, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceObjectId);

        var events = new List<LineagePropagationEvent>();
        double scoreDelta = newScore - oldScore;

        // If no meaningful change, no propagation needed
        if (Math.Abs(scoreDelta) < 0.01) return events;

        // BFS downstream from source
        var downstreamNodes = await _bfsStrategy.TraverseBfsAsync(
            sourceObjectId, maxDepth: 10, upstream: false, ct);

        foreach (var node in downstreamNodes)
        {
            ct.ThrowIfCancellationRequested();

            // Skip the source node itself
            if (node.ObjectId == sourceObjectId) continue;

            // Calculate propagated delta with exponential decay
            // hop 1 = full impact, hop 2 = 70%, hop 3 = 49%, etc.
            double decay = Math.Pow(DecayFactor, node.Depth - 1);
            double propagatedDelta = scoreDelta * decay;

            // Only create event if propagated delta exceeds threshold
            if (Math.Abs(propagatedDelta) <= MinimumDeltaThreshold) continue;

            // Compute the affected node's adjusted lineage dimension score
            double currentScore = node.Score?.CompositeScore ?? 50.0;
            double adjustedScore = Math.Clamp(currentScore + propagatedDelta, 0.0, 100.0);

            var oldGrade = ScoreToGrade(currentScore);
            var newGrade = ScoreToGrade(adjustedScore);

            var propagationEvent = new LineagePropagationEvent(
                SourceObjectId: sourceObjectId,
                AffectedObjectId: node.ObjectId,
                OldScore: currentScore,
                NewScore: adjustedScore,
                OldGrade: oldGrade,
                NewGrade: newGrade,
                HopDistance: node.Depth);

            events.Add(propagationEvent);
            _propagationHistory.Add(propagationEvent);
        }

        return events;
    }

    /// <inheritdoc/>
    public override Task<LineageGraph> GetDownstreamAsync(string nodeId, int maxDepth = 10, CancellationToken ct = default)
        => _bfsStrategy.GetDownstreamAsync(nodeId, maxDepth, ct);

    /// <summary>
    /// Converts a composite score to a consciousness grade.
    /// </summary>
    private static ConsciousnessGrade ScoreToGrade(double score) => score switch
    {
        >= 90 => ConsciousnessGrade.Enlightened,
        >= 75 => ConsciousnessGrade.Aware,
        >= 50 => ConsciousnessGrade.Dormant,
        >= 25 => ConsciousnessGrade.Dark,
        _ => ConsciousnessGrade.Unknown
    };
}
