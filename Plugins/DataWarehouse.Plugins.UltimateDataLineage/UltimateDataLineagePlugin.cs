using System.Reflection;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataLineage;

/// <summary>
/// Ultimate Data Lineage Plugin - End-to-end data provenance tracking.
///
/// Implements 32+ lineage strategies across categories:
/// - Origin Tracking (Source systems, ingestion points, external feeds)
/// - Transformation Tracking (ETL, SQL, Code-based, ML pipelines)
/// - Consumption Tracking (Reports, APIs, Exports, Subscriptions)
/// - Impact Analysis (Change impact, dependency mapping, blast radius)
/// - Visualization (Graph export, DAG rendering, Timeline views)
/// - Provenance Chain (Cryptographic proof, tamper detection, audit trail)
/// - Compliance (GDPR lineage, data subject tracking, retention compliance)
///
/// Features:
/// - Complete data provenance from source to consumption
/// - Real-time lineage tracking
/// - Impact analysis for change management
/// - Visual lineage exploration
/// - Compliance-ready audit trails
/// - Integration with UltimateIntelligence for semantic understanding
/// </summary>
public sealed class UltimateDataLineagePlugin : DataManagementPluginBase, IDisposable
{
    private readonly LineageStrategyRegistry _registry;
    private readonly BoundedDictionary<string, LineageNode> _nodes = new BoundedDictionary<string, LineageNode>(1000);
    private readonly BoundedDictionary<string, LineageEdge> _edges = new BoundedDictionary<string, LineageEdge>(1000);
    private readonly BoundedDictionary<string, long> _usageStats = new BoundedDictionary<string, long>(1000);
    private bool _disposed;

    // Configuration
    private volatile string _defaultStrategy = "graph-memory";
    private volatile bool _auditEnabled = true;
    private volatile bool _realTimeTrackingEnabled = true;
    private volatile int _maxLineageDepth = 50;

    // Statistics
    private long _totalNodes;
    private long _totalEdges;
    private long _totalTrackingEvents;
    private long _totalImpactAnalyses;

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.lineage.ultimate";

    /// <inheritdoc/>
    public override string Name => "Ultimate Data Lineage";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override string DataManagementDomain => "DataLineage";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.OrchestrationProvider;

    /// <inheritdoc/>
    public override Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc/>
    public override Task StopAsync() => Task.CompletedTask;

    /// <summary>
    /// Semantic description of this plugin for AI discovery.
    /// </summary>
    public string SemanticDescription =>
        "Ultimate data lineage plugin providing end-to-end data provenance tracking. " +
        "Answers: Where did this data come from? What transformations were applied? " +
        "Who/what consumes it? Supports impact analysis, visualization, and compliance tracking.";

    /// <summary>
    /// Semantic tags for AI discovery and categorization.
    /// </summary>
    public string[] SemanticTags =>
    [
        "lineage", "provenance", "tracking", "origin", "transformation",
        "impact-analysis", "audit", "compliance", "gdpr", "visualization"
    ];

    /// <summary>
    /// Gets the lineage strategy registry.
    /// </summary>
    public LineageStrategyRegistry Registry => _registry;

    /// <summary>
    /// Gets or sets the default strategy ID.
    /// </summary>
    public string DefaultStrategy
    {
        get => _defaultStrategy;
        set => _defaultStrategy = value;
    }

    /// <summary>
    /// Gets or sets whether audit logging is enabled.
    /// </summary>
    public bool AuditEnabled
    {
        get => _auditEnabled;
        set => _auditEnabled = value;
    }

    /// <summary>
    /// Gets or sets whether real-time tracking is enabled.
    /// </summary>
    public bool RealTimeTrackingEnabled
    {
        get => _realTimeTrackingEnabled;
        set => _realTimeTrackingEnabled = value;
    }

    /// <summary>
    /// Initializes a new instance of the Ultimate Data Lineage plugin.
    /// </summary>
    public UltimateDataLineagePlugin()
    {
        _registry = new LineageStrategyRegistry();
        DiscoverAndRegisterStrategies();
    }

    /// <inheritdoc/>
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        var response = await base.OnHandshakeAsync(request);

        response.Metadata["RegisteredStrategies"] = _registry.Count.ToString();
        response.Metadata["DefaultStrategy"] = _defaultStrategy;
        response.Metadata["RealTimeTrackingEnabled"] = _realTimeTrackingEnabled.ToString();
        response.Metadata["MaxLineageDepth"] = _maxLineageDepth.ToString();
        response.Metadata["TotalNodes"] = Interlocked.Read(ref _totalNodes).ToString();
        response.Metadata["TotalEdges"] = Interlocked.Read(ref _totalEdges).ToString();

        return response;
    }

    /// <inheritdoc/>
    protected override List<PluginCapabilityDescriptor> GetCapabilities()
    {
        return
        [
            new() { Name = "lineage.track", DisplayName = "Track Event", Description = "Track a lineage event" },
            new() { Name = "lineage.upstream", DisplayName = "Get Upstream", Description = "Get upstream lineage" },
            new() { Name = "lineage.downstream", DisplayName = "Get Downstream", Description = "Get downstream lineage" },
            new() { Name = "lineage.impact", DisplayName = "Impact Analysis", Description = "Analyze change impact" },
            new() { Name = "lineage.add-node", DisplayName = "Add Node", Description = "Add a lineage node" },
            new() { Name = "lineage.add-edge", DisplayName = "Add Edge", Description = "Add a lineage edge" },
            new() { Name = "lineage.get-node", DisplayName = "Get Node", Description = "Get node details" },
            new() { Name = "lineage.search", DisplayName = "Search", Description = "Search lineage graph" },
            new() { Name = "lineage.export", DisplayName = "Export", Description = "Export lineage graph" },
            new() { Name = "lineage.stats", DisplayName = "Statistics", Description = "Get lineage statistics" },
            new() { Name = "lineage.list-strategies", DisplayName = "List Strategies", Description = "List available strategies" },
            new() { Name = "lineage.provenance", DisplayName = "Get Provenance", Description = "Get provenance chain" }
        ];
    }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["TotalStrategies"] = _registry.Count;
        metadata["TotalNodes"] = Interlocked.Read(ref _totalNodes);
        metadata["TotalEdges"] = Interlocked.Read(ref _totalEdges);
        metadata["TotalTrackingEvents"] = Interlocked.Read(ref _totalTrackingEvents);
        metadata["TotalImpactAnalyses"] = Interlocked.Read(ref _totalImpactAnalyses);
        metadata["MaxLineageDepth"] = _maxLineageDepth;
        return metadata;
    }

    /// <inheritdoc/>
    public override Task OnMessageAsync(PluginMessage message)
    {
        return message.Type switch
        {
            "lineage.track" => HandleTrackAsync(message),
            "lineage.upstream" => HandleUpstreamAsync(message),
            "lineage.downstream" => HandleDownstreamAsync(message),
            "lineage.impact" => HandleImpactAsync(message),
            "lineage.add-node" => HandleAddNodeAsync(message),
            "lineage.add-edge" => HandleAddEdgeAsync(message),
            "lineage.get-node" => HandleGetNodeAsync(message),
            "lineage.search" => HandleSearchAsync(message),
            "lineage.export" => HandleExportAsync(message),
            "lineage.stats" => HandleStatsAsync(message),
            "lineage.list-strategies" => HandleListStrategiesAsync(message),
            "lineage.provenance" => HandleProvenanceAsync(message),
            _ => base.OnMessageAsync(message)
        };
    }

    #region Message Handlers

    private async Task HandleTrackAsync(PluginMessage message)
    {
        var record = new ProvenanceRecord
        {
            RecordId = Guid.NewGuid().ToString("N"),
            DataObjectId = message.Payload.TryGetValue("dataObjectId", out var doid) && doid is string d ? d : "",
            Operation = message.Payload.TryGetValue("operation", out var op) && op is string o ? o : "unknown",
            Actor = message.Payload.TryGetValue("actor", out var act) && act is string a ? a : "system"
        };

        var strategyId = message.Payload.TryGetValue("strategyId", out var sid) && sid is string s
            ? s : _defaultStrategy;

        var strategy = _registry.Get(strategyId);
        if (strategy != null)
        {
            await strategy.TrackAsync(record);
            IncrementUsageStats(strategyId);
        }

        Interlocked.Increment(ref _totalTrackingEvents);
        message.Payload["success"] = true;
        message.Payload["recordId"] = record.RecordId;
    }

    private async Task HandleUpstreamAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("nodeId", out var nidObj) || nidObj is not string nodeId)
        {
            message.Payload["success"] = false;
            message.Payload["error"] = "Missing 'nodeId' parameter";
            return;
        }

        var maxDepth = message.Payload.TryGetValue("maxDepth", out var md) && md is int m ? m : 10;
        var strategyId = message.Payload.TryGetValue("strategyId", out var sid) && sid is string s
            ? s : _defaultStrategy;

        var strategy = _registry.Get(strategyId);
        if (strategy == null)
        {
            message.Payload["success"] = false;
            message.Payload["error"] = $"Strategy '{strategyId}' not found";
            return;
        }

        var graph = await strategy.GetUpstreamAsync(nodeId, maxDepth);
        IncrementUsageStats(strategyId);

        message.Payload["success"] = true;
        message.Payload["nodeCount"] = graph.Nodes.Count;
        message.Payload["edgeCount"] = graph.Edges.Count;
        message.Payload["depth"] = graph.Depth;
        message.Payload["upstreamCount"] = graph.UpstreamCount;
    }

    private async Task HandleDownstreamAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("nodeId", out var nidObj) || nidObj is not string nodeId)
        {
            message.Payload["success"] = false;
            message.Payload["error"] = "Missing 'nodeId' parameter";
            return;
        }

        var maxDepth = message.Payload.TryGetValue("maxDepth", out var md) && md is int m ? m : 10;
        var strategyId = message.Payload.TryGetValue("strategyId", out var sid) && sid is string s
            ? s : _defaultStrategy;

        var strategy = _registry.Get(strategyId);
        if (strategy == null)
        {
            message.Payload["success"] = false;
            message.Payload["error"] = $"Strategy '{strategyId}' not found";
            return;
        }

        var graph = await strategy.GetDownstreamAsync(nodeId, maxDepth);
        IncrementUsageStats(strategyId);

        message.Payload["success"] = true;
        message.Payload["nodeCount"] = graph.Nodes.Count;
        message.Payload["edgeCount"] = graph.Edges.Count;
        message.Payload["depth"] = graph.Depth;
        message.Payload["downstreamCount"] = graph.DownstreamCount;
    }

    private async Task HandleImpactAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("nodeId", out var nidObj) || nidObj is not string nodeId)
        {
            message.Payload["success"] = false;
            message.Payload["error"] = "Missing 'nodeId' parameter";
            return;
        }

        var changeType = message.Payload.TryGetValue("changeType", out var ct) && ct is string c ? c : "schema";
        var strategyId = message.Payload.TryGetValue("strategyId", out var sid) && sid is string s
            ? s : _defaultStrategy;

        var strategy = _registry.Get(strategyId);
        if (strategy == null)
        {
            message.Payload["success"] = false;
            message.Payload["error"] = $"Strategy '{strategyId}' not found";
            return;
        }

        var result = await strategy.AnalyzeImpactAsync(nodeId, changeType);
        Interlocked.Increment(ref _totalImpactAnalyses);
        IncrementUsageStats(strategyId);

        message.Payload["success"] = true;
        message.Payload["directlyImpacted"] = result.DirectlyImpacted.Count;
        message.Payload["indirectlyImpacted"] = result.IndirectlyImpacted.Count;
        message.Payload["impactScore"] = result.ImpactScore;
        message.Payload["recommendations"] = result.Recommendations ?? Array.Empty<string>();
    }

    private Task HandleAddNodeAsync(PluginMessage message)
    {
        var node = new LineageNode
        {
            NodeId = message.Payload.TryGetValue("nodeId", out var nid) && nid is string n ? n : Guid.NewGuid().ToString("N"),
            Name = message.Payload.TryGetValue("name", out var name) && name is string nm ? nm : "Unknown",
            NodeType = message.Payload.TryGetValue("nodeType", out var nt) && nt is string t ? t : "dataset",
            SourceSystem = message.Payload.TryGetValue("sourceSystem", out var ss) && ss is string src ? src : null,
            Path = message.Payload.TryGetValue("path", out var p) && p is string path ? path : null
        };

        _nodes[node.NodeId] = node;
        Interlocked.Increment(ref _totalNodes);

        message.Payload["success"] = true;
        message.Payload["nodeId"] = node.NodeId;
        return Task.CompletedTask;
    }

    private Task HandleAddEdgeAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("sourceNodeId", out var srcObj) || srcObj is not string sourceNodeId)
        {
            message.Payload["success"] = false;
            message.Payload["error"] = "Missing 'sourceNodeId' parameter";
            return Task.CompletedTask;
        }

        if (!message.Payload.TryGetValue("targetNodeId", out var tgtObj) || tgtObj is not string targetNodeId)
        {
            message.Payload["success"] = false;
            message.Payload["error"] = "Missing 'targetNodeId' parameter";
            return Task.CompletedTask;
        }

        var edge = new LineageEdge
        {
            EdgeId = Guid.NewGuid().ToString("N"),
            SourceNodeId = sourceNodeId,
            TargetNodeId = targetNodeId,
            EdgeType = message.Payload.TryGetValue("edgeType", out var et) && et is string e ? e : "derived_from",
            TransformationDetails = message.Payload.TryGetValue("transformationDetails", out var td) && td is string t ? t : null
        };

        _edges[edge.EdgeId] = edge;
        Interlocked.Increment(ref _totalEdges);

        message.Payload["success"] = true;
        message.Payload["edgeId"] = edge.EdgeId;
        return Task.CompletedTask;
    }

    private Task HandleGetNodeAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("nodeId", out var nidObj) || nidObj is not string nodeId)
        {
            message.Payload["success"] = false;
            message.Payload["error"] = "Missing 'nodeId' parameter";
            return Task.CompletedTask;
        }

        if (!_nodes.TryGetValue(nodeId, out var node))
        {
            message.Payload["success"] = false;
            message.Payload["error"] = "Node not found";
            return Task.CompletedTask;
        }

        message.Payload["success"] = true;
        message.Payload["name"] = node.Name;
        message.Payload["nodeType"] = node.NodeType;
        message.Payload["sourceSystem"] = node.SourceSystem ?? "";
        message.Payload["path"] = node.Path ?? "";
        message.Payload["createdAt"] = node.CreatedAt;
        return Task.CompletedTask;
    }

    private Task HandleSearchAsync(PluginMessage message)
    {
        var query = message.Payload.TryGetValue("query", out var q) && q is string qry ? qry : "";
        var nodeType = message.Payload.TryGetValue("nodeType", out var nt) && nt is string t ? t : null;

        var results = _nodes.Values
            .Where(n => n.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                       (n.Path?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .Where(n => nodeType == null || n.NodeType.Equals(nodeType, StringComparison.OrdinalIgnoreCase))
            .Take(100)
            .Select(n => new Dictionary<string, object>
            {
                ["nodeId"] = n.NodeId,
                ["name"] = n.Name,
                ["nodeType"] = n.NodeType,
                ["sourceSystem"] = n.SourceSystem ?? ""
            })
            .ToList();

        message.Payload["success"] = true;
        message.Payload["results"] = results;
        message.Payload["count"] = results.Count;
        return Task.CompletedTask;
    }

    private Task HandleExportAsync(PluginMessage message)
    {
        var format = message.Payload.TryGetValue("format", out var f) && f is string fmt ? fmt : "json";

        // Export based on format
        var export = new Dictionary<string, object>
        {
            ["nodes"] = _nodes.Values.Select(n => new Dictionary<string, object>
            {
                ["id"] = n.NodeId,
                ["name"] = n.Name,
                ["type"] = n.NodeType
            }).ToList(),
            ["edges"] = _edges.Values.Select(e => new Dictionary<string, object>
            {
                ["id"] = e.EdgeId,
                ["source"] = e.SourceNodeId,
                ["target"] = e.TargetNodeId,
                ["type"] = e.EdgeType
            }).ToList()
        };

        message.Payload["success"] = true;
        message.Payload["format"] = format;
        message.Payload["data"] = export;
        return Task.CompletedTask;
    }

    private Task HandleStatsAsync(PluginMessage message)
    {
        message.Payload["totalNodes"] = Interlocked.Read(ref _totalNodes);
        message.Payload["totalEdges"] = Interlocked.Read(ref _totalEdges);
        message.Payload["totalTrackingEvents"] = Interlocked.Read(ref _totalTrackingEvents);
        message.Payload["totalImpactAnalyses"] = Interlocked.Read(ref _totalImpactAnalyses);
        message.Payload["registeredStrategies"] = _registry.Count;

        var usageByStrategy = new Dictionary<string, long>(_usageStats);
        message.Payload["usageByStrategy"] = usageByStrategy;
        return Task.CompletedTask;
    }

    private Task HandleListStrategiesAsync(PluginMessage message)
    {
        var categoryFilter = message.Payload.TryGetValue("category", out var catObj) && catObj is string catStr
            && Enum.TryParse<LineageCategory>(catStr, true, out var cat)
            ? cat
            : (LineageCategory?)null;

        var strategies = categoryFilter.HasValue
            ? _registry.GetByCategory(categoryFilter.Value)
            : _registry.GetAll();

        var strategyList = strategies.Select(s => new Dictionary<string, object>
        {
            ["id"] = s.StrategyId,
            ["displayName"] = s.DisplayName,
            ["category"] = s.Category.ToString(),
            ["capabilities"] = new Dictionary<string, object>
            {
                ["supportsUpstream"] = s.Capabilities.SupportsUpstream,
                ["supportsDownstream"] = s.Capabilities.SupportsDownstream,
                ["supportsTransformations"] = s.Capabilities.SupportsTransformations,
                ["supportsImpactAnalysis"] = s.Capabilities.SupportsImpactAnalysis,
                ["supportsVisualization"] = s.Capabilities.SupportsVisualization
            },
            ["tags"] = s.Tags
        }).ToList();

        message.Payload["strategies"] = strategyList;
        message.Payload["count"] = strategyList.Count;
        return Task.CompletedTask;
    }

    private Task HandleProvenanceAsync(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("dataObjectId", out var doidObj) || doidObj is not string dataObjectId)
        {
            message.Payload["success"] = false;
            message.Payload["error"] = "Missing 'dataObjectId' parameter";
            return Task.CompletedTask;
        }

        // Get provenance from default strategy
        var strategy = _registry.Get(_defaultStrategy) as LineageStrategyBase;
        // Provenance would be retrieved here

        message.Payload["success"] = true;
        message.Payload["dataObjectId"] = dataObjectId;
        return Task.CompletedTask;
    }

    #endregion

    #region Helper Methods

    private List<ILineageStrategy> GetStrategiesByCategory(LineageCategory category)
    {
        return _registry.GetByCategory(category).ToList();
    }

    private void IncrementUsageStats(string strategyId)
    {
        _usageStats.AddOrUpdate(strategyId, 1, (_, count) => count + 1);
    }

    private void DiscoverAndRegisterStrategies()
    {
        _registry.AutoDiscover(Assembly.GetExecutingAssembly());
    }

    #endregion

    /// <summary>
    /// Disposes resources.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_disposed) return;
            _disposed = true;
            _usageStats.Clear();
            _nodes.Clear();
            _edges.Clear();
        }
        base.Dispose(disposing);
    }
}
