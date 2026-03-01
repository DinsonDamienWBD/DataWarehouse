using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataLineage.Strategies;

/// <summary>
/// Lineage versioning strategy that tracks lineage graph changes over time.
/// Supports snapshots, diffs, and temporal queries.
/// </summary>
public sealed class LineageVersioningStrategy : LineageStrategyBase
{
    private readonly BoundedDictionary<string, List<LineageSnapshot>> _snapshots = new BoundedDictionary<string, List<LineageSnapshot>>(1000);
    private int _globalVersion;

    public override string StrategyId => "lineage-versioning";
    public override string DisplayName => "Lineage Versioning";
    public override LineageCategory Category => LineageCategory.Provenance;
    public override LineageStrategyCapabilities Capabilities => new()
    {
        SupportsUpstream = true, SupportsDownstream = true,
        SupportsTransformations = true, SupportsSchemaEvolution = true,
        SupportsImpactAnalysis = false, SupportsVisualization = true,
        SupportsRealTime = true
    };
    public override string SemanticDescription =>
        "Lineage versioning with graph snapshots, temporal queries, and diff comparison.";
    public override string[] Tags => ["versioning", "snapshots", "temporal", "diff"];

    /// <summary>
    /// Creates a snapshot of the current lineage graph for a data object.
    /// </summary>
    public LineageSnapshot CreateSnapshot(string dataObjectId, SimpleLineageGraph graph, string? description = null)
    {
        var version = Interlocked.Increment(ref _globalVersion);
        var snapshot = new LineageSnapshot
        {
            SnapshotId = $"snap-{version:D6}",
            DataObjectId = dataObjectId,
            Version = version,
            Graph = graph,
            CreatedAt = DateTimeOffset.UtcNow,
            Description = description
        };

        _snapshots.AddOrUpdate(
            dataObjectId,
            _ => new List<LineageSnapshot> { snapshot },
            (_, list) => { lock (list) { list.Add(snapshot); } return list; });

        return snapshot;
    }

    /// <summary>
    /// Gets a lineage graph at a specific point in time.
    /// </summary>
    public LineageSnapshot? GetAtTime(string dataObjectId, DateTimeOffset timestamp)
    {
        if (!_snapshots.TryGetValue(dataObjectId, out var snapshots))
            return null;

        lock (snapshots)
        {
            return snapshots.LastOrDefault(s => s.CreatedAt <= timestamp);
        }
    }

    /// <summary>
    /// Gets version history for a data object.
    /// </summary>
    public IReadOnlyList<LineageSnapshot> GetHistory(string dataObjectId) =>
        _snapshots.TryGetValue(dataObjectId, out var snapshots)
            ? snapshots.AsReadOnly()
            : Array.Empty<LineageSnapshot>();
}

/// <summary>
/// Enhanced blast radius strategy with BFS traversal for impact analysis.
/// Calculates affected downstream nodes from a changed node.
/// </summary>
public sealed class EnhancedBlastRadiusStrategy : LineageStrategyBase
{
    public override string StrategyId => "enhanced-blast-radius";
    public override string DisplayName => "Enhanced Blast Radius Analyzer";
    public override LineageCategory Category => LineageCategory.Impact;
    public override LineageStrategyCapabilities Capabilities => new()
    {
        SupportsUpstream = true, SupportsDownstream = true,
        SupportsTransformations = true, SupportsSchemaEvolution = false,
        SupportsImpactAnalysis = true, SupportsVisualization = true,
        SupportsRealTime = true
    };
    public override string SemanticDescription =>
        "Enhanced blast radius with BFS traversal, criticality scoring, " +
        "and multi-hop impact propagation analysis.";
    public override string[] Tags => ["blast-radius", "bfs", "impact", "criticality"];

    /// <summary>
    /// Calculates blast radius using BFS from a modified node.
    /// Returns all affected downstream nodes with distance and criticality.
    /// </summary>
    public BlastRadiusResult CalculateBlastRadius(string sourceNodeId, SimpleLineageGraph graph, int maxDepth = 10)
    {
        var visited = new HashSet<string>();
        var queue = new Queue<(string NodeId, int Depth)>();
        var affectedNodes = new List<AffectedNode>();

        queue.Enqueue((sourceNodeId, 0));
        visited.Add(sourceNodeId);

        while (queue.Count > 0)
        {
            var (currentId, depth) = queue.Dequeue();
            if (depth > maxDepth) continue;

            var node = graph.Nodes.FirstOrDefault(n => n.Id == currentId);
            if (node == null) continue;

            if (currentId != sourceNodeId)
            {
                affectedNodes.Add(new AffectedNode
                {
                    NodeId = currentId,
                    NodeName = node.Name,
                    NodeType = node.Type,
                    Distance = depth,
                    Criticality = CalculateCriticality(node, depth),
                    PropagationPath = BuildPath(graph, sourceNodeId, currentId, visited)
                });
            }

            // Find downstream edges
            var downstreamEdges = graph.Edges
                .Where(e => e.SourceId == currentId && !visited.Contains(e.TargetId));

            foreach (var edge in downstreamEdges)
            {
                visited.Add(edge.TargetId);
                queue.Enqueue((edge.TargetId, depth + 1));
            }
        }

        return new BlastRadiusResult
        {
            SourceNodeId = sourceNodeId,
            TotalAffected = affectedNodes.Count,
            MaxDepth = affectedNodes.Count > 0 ? affectedNodes.Max(n => n.Distance) : 0,
            AffectedNodes = affectedNodes.OrderBy(n => n.Distance).ThenByDescending(n => n.Criticality).ToList(),
            CriticalCount = affectedNodes.Count(n => n.Criticality >= 0.8),
            HighCount = affectedNodes.Count(n => n.Criticality >= 0.5 && n.Criticality < 0.8),
            MediumCount = affectedNodes.Count(n => n.Criticality >= 0.3 && n.Criticality < 0.5),
            LowCount = affectedNodes.Count(n => n.Criticality < 0.3)
        };
    }

    private static double CalculateCriticality(SimpleLineageNode node, int distance)
    {
        // Base criticality from node type
        var baseCriticality = node.Type switch
        {
            "production" => 1.0,
            "staging" => 0.7,
            "analytics" => 0.5,
            "archive" => 0.3,
            _ => 0.5
        };

        // Decay by distance (further = less critical)
        var distanceDecay = 1.0 / (1.0 + distance * 0.2);

        return baseCriticality * distanceDecay;
    }

    private static string[] BuildPath(SimpleLineageGraph graph, string from, string to, HashSet<string> visited)
    {
        // P2-2371: BFS path reconstruction from 'from' to 'to' using the graph's edges.
        // Build a parent map so we can reconstruct the shortest path after BFS completes.
        var parent = new Dictionary<string, string?> { [from] = null };
        var bfsQueue = new Queue<string>();
        bfsQueue.Enqueue(from);

        while (bfsQueue.Count > 0)
        {
            var current = bfsQueue.Dequeue();
            if (current == to) break;

            foreach (var edge in graph.Edges)
            {
                if (edge.SourceId != current) continue;
                if (parent.ContainsKey(edge.TargetId)) continue;
                parent[edge.TargetId] = current;
                bfsQueue.Enqueue(edge.TargetId);
            }
        }

        // Walk back from 'to' to 'from' via parent links
        if (!parent.ContainsKey(to))
            return [from, to]; // no path found — fall back to direct edge

        var path = new List<string>();
        var node = to;
        while (node != null)
        {
            path.Add(node);
            node = parent.TryGetValue(node, out var p) ? p : null;
        }
        path.Reverse();
        return path.ToArray();
    }
}

/// <summary>
/// Lineage diff strategy for comparing lineage graphs at two points in time.
/// </summary>
public sealed class LineageDiffStrategy : LineageStrategyBase
{
    public override string StrategyId => "lineage-diff";
    public override string DisplayName => "Lineage Diff";
    public override LineageCategory Category => LineageCategory.Provenance;
    public override LineageStrategyCapabilities Capabilities => new()
    {
        SupportsUpstream = true, SupportsDownstream = true,
        SupportsTransformations = true, SupportsSchemaEvolution = true,
        SupportsImpactAnalysis = true, SupportsVisualization = true,
        SupportsRealTime = false
    };
    public override string SemanticDescription =>
        "Compare lineage graphs at two points in time, showing added, removed, and modified nodes and edges.";
    public override string[] Tags => ["diff", "comparison", "temporal", "changes"];

    /// <summary>
    /// Computes the diff between two lineage graph snapshots.
    /// </summary>
    public LineageDiffResult ComputeDiff(SimpleLineageGraph before, SimpleLineageGraph after)
    {
        var beforeNodeIds = new HashSet<string>(before.Nodes.Select(n => n.Id));
        var afterNodeIds = new HashSet<string>(after.Nodes.Select(n => n.Id));

        var addedNodes = after.Nodes.Where(n => !beforeNodeIds.Contains(n.Id)).ToList();
        var removedNodes = before.Nodes.Where(n => !afterNodeIds.Contains(n.Id)).ToList();
        var commonNodeIds = beforeNodeIds.Intersect(afterNodeIds).ToList();

        var modifiedNodes = new List<NodeModification>();
        foreach (var nodeId in commonNodeIds)
        {
            var beforeNode = before.Nodes.First(n => n.Id == nodeId);
            var afterNode = after.Nodes.First(n => n.Id == nodeId);
            if (beforeNode.Type != afterNode.Type || beforeNode.Name != afterNode.Name)
            {
                modifiedNodes.Add(new NodeModification
                {
                    NodeId = nodeId,
                    Before = beforeNode,
                    After = afterNode
                });
            }
        }

        var beforeEdgeKeys = new HashSet<string>(before.Edges.Select(e => $"{e.SourceId}->{e.TargetId}"));
        var afterEdgeKeys = new HashSet<string>(after.Edges.Select(e => $"{e.SourceId}->{e.TargetId}"));

        var addedEdges = after.Edges.Where(e => !beforeEdgeKeys.Contains($"{e.SourceId}->{e.TargetId}")).ToList();
        var removedEdges = before.Edges.Where(e => !afterEdgeKeys.Contains($"{e.SourceId}->{e.TargetId}")).ToList();

        return new LineageDiffResult
        {
            AddedNodes = addedNodes,
            RemovedNodes = removedNodes,
            ModifiedNodes = modifiedNodes,
            AddedEdges = addedEdges,
            RemovedEdges = removedEdges,
            TotalChanges = addedNodes.Count + removedNodes.Count + modifiedNodes.Count + addedEdges.Count + removedEdges.Count
        };
    }
}

/// <summary>
/// Cross-system lineage strategy for federated lineage across message bus boundaries.
/// </summary>
public sealed class CrossSystemLineageStrategy : LineageStrategyBase
{
    private readonly BoundedDictionary<string, SystemRegistration> _systems = new BoundedDictionary<string, SystemRegistration>(1000);
    private readonly BoundedDictionary<string, List<CrossSystemEdge>> _crossEdges = new BoundedDictionary<string, List<CrossSystemEdge>>(1000);

    public override string StrategyId => "cross-system-lineage";
    public override string DisplayName => "Cross-System Lineage";
    public override LineageCategory Category => LineageCategory.Origin;
    public override LineageStrategyCapabilities Capabilities => new()
    {
        SupportsUpstream = true, SupportsDownstream = true,
        SupportsTransformations = true, SupportsSchemaEvolution = false,
        SupportsImpactAnalysis = true, SupportsVisualization = true,
        SupportsRealTime = true
    };
    public override string SemanticDescription =>
        "Federated lineage tracking across system boundaries with cross-system edge management.";
    public override string[] Tags => ["federated", "cross-system", "distributed", "lineage"];

    /// <summary>
    /// Registers an external system for cross-system lineage.
    /// </summary>
    public SystemRegistration RegisterSystem(string systemId, string name, string endpoint, string? busTopicPrefix = null)
    {
        var reg = new SystemRegistration
        {
            SystemId = systemId,
            Name = name,
            Endpoint = endpoint,
            BusTopicPrefix = busTopicPrefix,
            RegisteredAt = DateTimeOffset.UtcNow,
            IsActive = true
        };
        _systems[systemId] = reg;
        return reg;
    }

    /// <summary>
    /// Creates a cross-system lineage edge.
    /// </summary>
    public CrossSystemEdge CreateCrossEdge(
        string sourceSystemId, string sourceNodeId,
        string targetSystemId, string targetNodeId,
        string? transformationType = null)
    {
        var edge = new CrossSystemEdge
        {
            EdgeId = Guid.NewGuid().ToString("N")[..12],
            SourceSystemId = sourceSystemId,
            SourceNodeId = sourceNodeId,
            TargetSystemId = targetSystemId,
            TargetNodeId = targetNodeId,
            TransformationType = transformationType,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var key = $"{sourceSystemId}:{sourceNodeId}";
        _crossEdges.AddOrUpdate(
            key,
            _ => new List<CrossSystemEdge> { edge },
            (_, list) => { lock (list) { list.Add(edge); } return list; });

        return edge;
    }

    /// <summary>
    /// Gets all cross-system edges from a source.
    /// </summary>
    public IReadOnlyList<CrossSystemEdge> GetCrossEdges(string sourceSystemId, string sourceNodeId)
    {
        var key = $"{sourceSystemId}:{sourceNodeId}";
        return _crossEdges.TryGetValue(key, out var edges) ? edges.AsReadOnly() : Array.Empty<CrossSystemEdge>();
    }

    /// <summary>
    /// Gets all registered systems.
    /// </summary>
    public IReadOnlyList<SystemRegistration> GetSystems() =>
        _systems.Values.ToList().AsReadOnly();
}

#region Models

public sealed record LineageSnapshot
{
    public required string SnapshotId { get; init; }
    public required string DataObjectId { get; init; }
    public int Version { get; init; }
    public required SimpleLineageGraph Graph { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string? Description { get; init; }
}

public sealed record SimpleLineageGraph
{
    public List<SimpleLineageNode> Nodes { get; init; } = new();
    public List<SimpleLineageEdge> Edges { get; init; } = new();
}

public sealed record SimpleLineageNode
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Type { get; init; } = "default";
    public Dictionary<string, object> Metadata { get; init; } = new();
}

public sealed record SimpleLineageEdge
{
    public required string SourceId { get; init; }
    public required string TargetId { get; init; }
    public string? TransformationType { get; init; }
    public Dictionary<string, object> Metadata { get; init; } = new();
}

public sealed record BlastRadiusResult
{
    public required string SourceNodeId { get; init; }
    public int TotalAffected { get; init; }
    public int MaxDepth { get; init; }
    public List<AffectedNode> AffectedNodes { get; init; } = new();
    public int CriticalCount { get; init; }
    public int HighCount { get; init; }
    public int MediumCount { get; init; }
    public int LowCount { get; init; }
}

public sealed record AffectedNode
{
    public required string NodeId { get; init; }
    public string? NodeName { get; init; }
    public string? NodeType { get; init; }
    public int Distance { get; init; }
    public double Criticality { get; init; }
    public string[] PropagationPath { get; init; } = Array.Empty<string>();
}

public sealed record LineageDiffResult
{
    public List<SimpleLineageNode> AddedNodes { get; init; } = new();
    public List<SimpleLineageNode> RemovedNodes { get; init; } = new();
    public List<NodeModification> ModifiedNodes { get; init; } = new();
    public List<SimpleLineageEdge> AddedEdges { get; init; } = new();
    public List<SimpleLineageEdge> RemovedEdges { get; init; } = new();
    public int TotalChanges { get; init; }
}

public sealed record NodeModification
{
    public required string NodeId { get; init; }
    public required SimpleLineageNode Before { get; init; }
    public required SimpleLineageNode After { get; init; }
}

public sealed record SystemRegistration
{
    public required string SystemId { get; init; }
    public required string Name { get; init; }
    public required string Endpoint { get; init; }
    public string? BusTopicPrefix { get; init; }
    public DateTimeOffset RegisteredAt { get; init; }
    public bool IsActive { get; init; }
}

public sealed record CrossSystemEdge
{
    public required string EdgeId { get; init; }
    public required string SourceSystemId { get; init; }
    public required string SourceNodeId { get; init; }
    public required string TargetSystemId { get; init; }
    public required string TargetNodeId { get; init; }
    public string? TransformationType { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

#endregion
