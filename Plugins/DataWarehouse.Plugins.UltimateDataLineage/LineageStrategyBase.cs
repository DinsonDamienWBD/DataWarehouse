using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataLineage;

/// <summary>
/// Defines the category of lineage strategy.
/// </summary>
public enum LineageCategory
{
    /// <summary>Tracking data origins.</summary>
    Origin,
    /// <summary>Tracking data transformations.</summary>
    Transformation,
    /// <summary>Tracking data consumption.</summary>
    Consumption,
    /// <summary>Impact analysis strategies.</summary>
    Impact,
    /// <summary>Visualization strategies.</summary>
    Visualization,
    /// <summary>Provenance chain strategies.</summary>
    Provenance,
    /// <summary>Audit trail strategies.</summary>
    Audit,
    /// <summary>Compliance tracking.</summary>
    Compliance
}

/// <summary>
/// Represents a lineage node in the graph.
/// </summary>
public sealed record LineageNode
{
    /// <summary>Unique node identifier.</summary>
    public required string NodeId { get; init; }
    /// <summary>Display name of the node.</summary>
    public required string Name { get; init; }
    /// <summary>Type of node (dataset, transformation, consumer).</summary>
    public required string NodeType { get; init; }
    /// <summary>Source system or plugin.</summary>
    public string? SourceSystem { get; init; }
    /// <summary>Path or location.</summary>
    public string? Path { get; init; }
    /// <summary>Schema information.</summary>
    public Dictionary<string, string>? Schema { get; init; }
    /// <summary>Creation timestamp.</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    /// <summary>Last modified timestamp.</summary>
    public DateTime ModifiedAt { get; init; } = DateTime.UtcNow;
    /// <summary>Custom metadata.</summary>
    public Dictionary<string, object>? Metadata { get; init; }
    /// <summary>Tags for categorization.</summary>
    public string[]? Tags { get; init; }
}

/// <summary>
/// Represents an edge in the lineage graph.
/// </summary>
public sealed record LineageEdge
{
    /// <summary>Unique edge identifier.</summary>
    public required string EdgeId { get; init; }
    /// <summary>Source node ID.</summary>
    public required string SourceNodeId { get; init; }
    /// <summary>Target node ID.</summary>
    public required string TargetNodeId { get; init; }
    /// <summary>Type of relationship.</summary>
    public required string EdgeType { get; init; }
    /// <summary>Transformation details if applicable.</summary>
    public string? TransformationDetails { get; init; }
    /// <summary>SQL or code that produced this edge.</summary>
    public string? TransformationCode { get; init; }
    /// <summary>Edge creation timestamp.</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    /// <summary>Custom metadata.</summary>
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Represents the complete lineage graph for a dataset.
/// </summary>
public sealed record LineageGraph
{
    /// <summary>Root dataset ID.</summary>
    public required string RootNodeId { get; init; }
    /// <summary>All nodes in the graph.</summary>
    public required IReadOnlyList<LineageNode> Nodes { get; init; }
    /// <summary>All edges in the graph.</summary>
    public required IReadOnlyList<LineageEdge> Edges { get; init; }
    /// <summary>Graph generation timestamp.</summary>
    public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;
    /// <summary>Total depth of the graph.</summary>
    public int Depth { get; init; }
    /// <summary>Total upstream count.</summary>
    public int UpstreamCount { get; init; }
    /// <summary>Total downstream count.</summary>
    public int DownstreamCount { get; init; }
}

/// <summary>
/// Represents an impact analysis result.
/// </summary>
public sealed record ImpactAnalysisResult
{
    /// <summary>Source node that changed.</summary>
    public required string SourceNodeId { get; init; }
    /// <summary>Type of change.</summary>
    public required string ChangeType { get; init; }
    /// <summary>Directly impacted nodes.</summary>
    public required IReadOnlyList<string> DirectlyImpacted { get; init; }
    /// <summary>Indirectly impacted nodes.</summary>
    public required IReadOnlyList<string> IndirectlyImpacted { get; init; }
    /// <summary>Total impact score (0-100).</summary>
    public int ImpactScore { get; init; }
    /// <summary>Analysis timestamp.</summary>
    public DateTime AnalyzedAt { get; init; } = DateTime.UtcNow;
    /// <summary>Recommendations.</summary>
    public IReadOnlyList<string>? Recommendations { get; init; }
}

/// <summary>
/// Represents a provenance record.
/// </summary>
public sealed record ProvenanceRecord
{
    /// <summary>Record identifier.</summary>
    public required string RecordId { get; init; }
    /// <summary>Data object ID.</summary>
    public required string DataObjectId { get; init; }
    /// <summary>Operation type (create, read, update, delete, transform).</summary>
    public required string Operation { get; init; }
    /// <summary>Actor who performed the operation.</summary>
    public required string Actor { get; init; }
    /// <summary>Timestamp of operation.</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    /// <summary>Source data objects if transformation.</summary>
    public IReadOnlyList<string>? SourceObjects { get; init; }
    /// <summary>Transformation applied.</summary>
    public string? Transformation { get; init; }
    /// <summary>Hash of data before operation.</summary>
    public string? BeforeHash { get; init; }
    /// <summary>Hash of data after operation.</summary>
    public string? AfterHash { get; init; }
    /// <summary>Custom metadata.</summary>
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Capabilities of a lineage strategy.
/// </summary>
public sealed record LineageStrategyCapabilities
{
    /// <summary>Whether strategy supports upstream tracking.</summary>
    public required bool SupportsUpstream { get; init; }
    /// <summary>Whether strategy supports downstream tracking.</summary>
    public required bool SupportsDownstream { get; init; }
    /// <summary>Whether strategy supports transformation tracking.</summary>
    public required bool SupportsTransformations { get; init; }
    /// <summary>Whether strategy supports schema evolution.</summary>
    public required bool SupportsSchemaEvolution { get; init; }
    /// <summary>Whether strategy supports impact analysis.</summary>
    public required bool SupportsImpactAnalysis { get; init; }
    /// <summary>Whether strategy supports visualization export.</summary>
    public required bool SupportsVisualization { get; init; }
    /// <summary>Whether strategy supports real-time tracking.</summary>
    public required bool SupportsRealTime { get; init; }
}

/// <summary>
/// Interface for lineage strategies.
/// </summary>
public interface ILineageStrategy
{
    /// <summary>Unique identifier.</summary>
    string StrategyId { get; }
    /// <summary>Human-readable display name.</summary>
    string DisplayName { get; }
    /// <summary>Category of this strategy.</summary>
    LineageCategory Category { get; }
    /// <summary>Capabilities of this strategy.</summary>
    LineageStrategyCapabilities Capabilities { get; }
    /// <summary>Semantic description for AI discovery.</summary>
    string SemanticDescription { get; }
    /// <summary>Tags for categorization.</summary>
    string[] Tags { get; }
    /// <summary>Tracks a lineage event.</summary>
    Task TrackAsync(ProvenanceRecord record, CancellationToken ct = default);
    /// <summary>Gets upstream lineage.</summary>
    Task<LineageGraph> GetUpstreamAsync(string nodeId, int maxDepth = 10, CancellationToken ct = default);
    /// <summary>Gets downstream lineage.</summary>
    Task<LineageGraph> GetDownstreamAsync(string nodeId, int maxDepth = 10, CancellationToken ct = default);
    /// <summary>Performs impact analysis.</summary>
    Task<ImpactAnalysisResult> AnalyzeImpactAsync(string nodeId, string changeType, CancellationToken ct = default);
    /// <summary>Initializes the strategy.</summary>
    Task InitializeAsync(CancellationToken ct = default);
    /// <summary>Disposes of the strategy resources.</summary>
    Task DisposeAsync();
}

/// <summary>
/// Abstract base class for lineage strategies.
/// Inherits lifecycle, counters, health caching, and dispose from StrategyBase.
/// </summary>
public abstract class LineageStrategyBase : StrategyBase, ILineageStrategy
{
    protected readonly BoundedDictionary<string, LineageNode> _nodes = new BoundedDictionary<string, LineageNode>(1000);
    protected readonly BoundedDictionary<string, LineageEdge> _edges = new BoundedDictionary<string, LineageEdge>(1000);
    protected readonly BoundedDictionary<string, List<ProvenanceRecord>> _provenance = new BoundedDictionary<string, List<ProvenanceRecord>>(1000);

    /// <inheritdoc/>
    public abstract override string StrategyId { get; }
    /// <inheritdoc/>
    public abstract string DisplayName { get; }
    /// <inheritdoc/>
    public override string Name => DisplayName;
    /// <inheritdoc/>
    public abstract LineageCategory Category { get; }
    /// <inheritdoc/>
    public abstract LineageStrategyCapabilities Capabilities { get; }
    /// <inheritdoc/>
    public abstract string SemanticDescription { get; }
    /// <inheritdoc/>
    public abstract string[] Tags { get; }

    /// <inheritdoc/>
    protected override async Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        await InitializeCoreAsync(cancellationToken);
        IncrementCounter("initialized");
    }

    /// <inheritdoc/>
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        await DisposeCoreAsync();
        _nodes.Clear();
        _edges.Clear();
        _provenance.Clear();
    }

    /// <summary>Explicit implementation for ILineageStrategy.DisposeAsync() (Task vs ValueTask).</summary>
    Task ILineageStrategy.DisposeAsync() => ShutdownAsync();

    /// <summary>Core initialization logic.</summary>
    protected virtual Task InitializeCoreAsync(CancellationToken ct) => Task.CompletedTask;
    /// <summary>Core disposal logic.</summary>
    protected virtual Task DisposeCoreAsync() => Task.CompletedTask;

    /// <inheritdoc/>
    public virtual Task TrackAsync(ProvenanceRecord record, CancellationToken ct = default)
    {
        var list = _provenance.GetOrAdd(record.DataObjectId, _ => new List<ProvenanceRecord>());
        lock (list) { list.Add(record); }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public virtual Task<LineageGraph> GetUpstreamAsync(string nodeId, int maxDepth = 10, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        var visited = new HashSet<string>();
        var resultNodes = new List<LineageNode>();
        var resultEdges = new List<LineageEdge>();
        var queue = new Queue<(string Id, int Depth)>();
        queue.Enqueue((nodeId, 0));

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            if (!visited.Add(current) || depth > maxDepth) continue;
            if (_nodes.TryGetValue(current, out var node)) resultNodes.Add(node);
            foreach (var edge in _edges.Values.Where(e => e.TargetNodeId == current))
            {
                resultEdges.Add(edge);
                queue.Enqueue((edge.SourceNodeId, depth + 1));
            }
        }

        return Task.FromResult(new LineageGraph
        {
            RootNodeId = nodeId,
            Nodes = resultNodes.AsReadOnly(),
            Edges = resultEdges.AsReadOnly(),
            Depth = maxDepth,
            UpstreamCount = resultNodes.Count,
            DownstreamCount = 0
        });
    }

    /// <inheritdoc/>
    public virtual Task<LineageGraph> GetDownstreamAsync(string nodeId, int maxDepth = 10, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        var visited = new HashSet<string>();
        var resultNodes = new List<LineageNode>();
        var resultEdges = new List<LineageEdge>();
        var queue = new Queue<(string Id, int Depth)>();
        queue.Enqueue((nodeId, 0));

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            if (!visited.Add(current) || depth > maxDepth) continue;
            if (_nodes.TryGetValue(current, out var node)) resultNodes.Add(node);
            foreach (var edge in _edges.Values.Where(e => e.SourceNodeId == current))
            {
                resultEdges.Add(edge);
                queue.Enqueue((edge.TargetNodeId, depth + 1));
            }
        }

        return Task.FromResult(new LineageGraph
        {
            RootNodeId = nodeId,
            Nodes = resultNodes.AsReadOnly(),
            Edges = resultEdges.AsReadOnly(),
            Depth = maxDepth,
            UpstreamCount = 0,
            DownstreamCount = resultNodes.Count
        });
    }

    /// <inheritdoc/>
    public virtual Task<ImpactAnalysisResult> AnalyzeImpactAsync(string nodeId, string changeType, CancellationToken ct = default)
    {
        ThrowIfNotInitialized();
        var directlyImpacted = _edges.Values
            .Where(e => e.SourceNodeId == nodeId)
            .Select(e => e.TargetNodeId)
            .Distinct()
            .ToList();

        var indirectlyImpacted = new HashSet<string>();
        var queue = new Queue<string>(directlyImpacted);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var edge in _edges.Values.Where(e => e.SourceNodeId == current))
            {
                if (indirectlyImpacted.Add(edge.TargetNodeId) && !directlyImpacted.Contains(edge.TargetNodeId))
                    queue.Enqueue(edge.TargetNodeId);
            }
        }
        indirectlyImpacted.ExceptWith(directlyImpacted);

        var totalImpacted = directlyImpacted.Count + indirectlyImpacted.Count;
        var totalNodes = _nodes.Count;
        var score = totalNodes > 0 ? (int)(100.0 * totalImpacted / totalNodes) : 0;

        return Task.FromResult(new ImpactAnalysisResult
        {
            SourceNodeId = nodeId,
            ChangeType = changeType,
            DirectlyImpacted = directlyImpacted.AsReadOnly(),
            IndirectlyImpacted = indirectlyImpacted.ToList().AsReadOnly(),
            ImpactScore = Math.Min(score, 100)
        });
    }

    /// <summary>Gets all counter values.</summary>
    public IReadOnlyDictionary<string, long> GetCounters() => GetAllCounters();

    /// <summary>Adds a node to the lineage graph.</summary>
    protected void AddNode(LineageNode node) => _nodes[node.NodeId] = node;
    /// <summary>Adds an edge to the lineage graph.</summary>
    protected void AddEdge(LineageEdge edge) => _edges[edge.EdgeId] = edge;
}

/// <summary>
/// Thread-safe registry for lineage strategies.
/// </summary>
public sealed class LineageStrategyRegistry
{
    private readonly BoundedDictionary<string, ILineageStrategy> _strategies = new BoundedDictionary<string, ILineageStrategy>(1000);

    /// <summary>Registers a strategy.</summary>
    public void Register(ILineageStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        _strategies[strategy.StrategyId] = strategy;
    }

    /// <summary>Unregisters a strategy by ID.</summary>
    public bool Unregister(string strategyId) => _strategies.TryRemove(strategyId, out _);

    /// <summary>Gets a strategy by ID.</summary>
    public ILineageStrategy? Get(string strategyId) =>
        _strategies.TryGetValue(strategyId, out var strategy) ? strategy : null;

    /// <summary>Gets all registered strategies.</summary>
    public IReadOnlyCollection<ILineageStrategy> GetAll() => _strategies.Values.ToList().AsReadOnly();

    /// <summary>Gets strategies by category.</summary>
    public IReadOnlyCollection<ILineageStrategy> GetByCategory(LineageCategory category) =>
        _strategies.Values.Where(s => s.Category == category).OrderBy(s => s.DisplayName).ToList().AsReadOnly();

    /// <summary>Gets the count of registered strategies.</summary>
    public int Count => _strategies.Count;

    /// <summary>Auto-discovers and registers strategies from assemblies.</summary>
    public int AutoDiscover(params System.Reflection.Assembly[] assemblies)
    {
        var strategyType = typeof(ILineageStrategy);
        int discovered = 0;

        foreach (var assembly in assemblies)
        {
            try
            {
                var types = assembly.GetTypes()
                    .Where(t => !t.IsAbstract && !t.IsInterface && strategyType.IsAssignableFrom(t));

                foreach (var type in types)
                {
                    try
                    {
                        if (Activator.CreateInstance(type) is ILineageStrategy strategy)
                        {
                            Register(strategy);
                            discovered++;
                        }
                    }
                    catch { /* Skip types that cannot be instantiated */ }
                }
            }
            catch { /* Skip assemblies that cannot be scanned */ }
        }

        return discovered;
    }
}
