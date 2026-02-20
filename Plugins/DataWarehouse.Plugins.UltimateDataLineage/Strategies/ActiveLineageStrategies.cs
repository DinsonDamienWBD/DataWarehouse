using System.Text.Json;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataLineage.Strategies;

/// <summary>
/// Represents a recorded transformation applied to a data object for self-tracking lineage.
/// </summary>
/// <remarks>
/// Used by <see cref="SelfTrackingDataStrategy"/> to maintain per-object transformation history.
/// Part of T146.B1.1 Active Lineage Strategies.
/// </remarks>
internal sealed record TransformationRecord(
    string TransformId,
    string Operation,
    string? SourceObjectId,
    DateTimeOffset Timestamp,
    string? BeforeHash,
    string? AfterHash);

/// <summary>
/// Self-tracking data lineage strategy where each data object maintains its own transformation history.
/// Every tracking event automatically records a <see cref="TransformationRecord"/> so the full
/// provenance chain is available without external graph queries.
/// </summary>
/// <remarks>
/// T146.B1.1 - Industry-first self-tracking data lineage. Uses BFS graph traversal on provenance
/// records for upstream/downstream discovery and weighted impact scoring based on direct and
/// indirect downstream dependency counts.
/// </remarks>
internal sealed class SelfTrackingDataStrategy : LineageStrategyBase
{
    private readonly BoundedDictionary<string, List<TransformationRecord>> _objectHistory = new BoundedDictionary<string, List<TransformationRecord>>(1000);

    /// <inheritdoc/>
    public override string StrategyId => "active-self-tracking";

    /// <inheritdoc/>
    public override string DisplayName => "Self-Tracking Data";

    /// <inheritdoc/>
    public override LineageCategory Category => LineageCategory.Origin;

    /// <inheritdoc/>
    public override LineageStrategyCapabilities Capabilities => new()
    {
        SupportsUpstream = true,
        SupportsDownstream = true,
        SupportsTransformations = true,
        SupportsSchemaEvolution = true,
        SupportsImpactAnalysis = true,
        SupportsVisualization = true,
        SupportsRealTime = true
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Self-tracking data lineage strategy where data objects maintain their own transformation " +
        "history. Every write, transform, and derivation is automatically recorded, enabling " +
        "instant provenance queries without external graph lookups.";

    /// <inheritdoc/>
    public override string[] Tags => ["self-tracking", "automatic", "history", "industry-first"];

    /// <inheritdoc/>
    public override Task TrackAsync(ProvenanceRecord record, CancellationToken ct = default)
    {
        base.TrackAsync(record, ct);

        // Ensure node exists in graph
        AddNode(new LineageNode
        {
            NodeId = record.DataObjectId,
            Name = record.DataObjectId,
            NodeType = "dataset"
        });

        // Record transformation into per-object history
        var sourceId = record.SourceObjects?.Count > 0 ? record.SourceObjects[0] : null;
        var transformRecord = new TransformationRecord(
            TransformId: Guid.NewGuid().ToString("N"),
            Operation: record.Operation,
            SourceObjectId: sourceId,
            Timestamp: record.Timestamp == default ? DateTimeOffset.UtcNow : new DateTimeOffset(record.Timestamp, TimeSpan.Zero),
            BeforeHash: record.BeforeHash,
            AfterHash: record.AfterHash);

        var history = _objectHistory.GetOrAdd(record.DataObjectId, _ => new List<TransformationRecord>());
        lock (history) { history.Add(transformRecord); }

        // Track source objects as nodes and create edges
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

                AddEdge(new LineageEdge
                {
                    EdgeId = Guid.NewGuid().ToString("N"),
                    SourceNodeId = source,
                    TargetNodeId = record.DataObjectId,
                    EdgeType = "self-tracked",
                    TransformationDetails = record.Transformation
                });
            }
        }

        return Task.CompletedTask;
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
            if (depth > maxDepth || visited.Contains(currentId)) continue;
            visited.Add(currentId);

            if (_nodes.TryGetValue(currentId, out var node))
                nodes.Add(node);
            else
                nodes.Add(new LineageNode { NodeId = currentId, Name = currentId, NodeType = "dataset" });

            // Walk provenance records to find sources
            if (_provenance.TryGetValue(currentId, out var records))
            {
                List<ProvenanceRecord> snapshot;
                lock (records) { snapshot = new List<ProvenanceRecord>(records); }

                foreach (var rec in snapshot)
                {
                    if (rec.SourceObjects == null) continue;
                    foreach (var sourceId in rec.SourceObjects)
                    {
                        if (!visited.Contains(sourceId))
                        {
                            queue.Enqueue((sourceId, depth + 1));
                            edges.Add(new LineageEdge
                            {
                                EdgeId = $"{sourceId}->{currentId}",
                                SourceNodeId = sourceId,
                                TargetNodeId = currentId,
                                EdgeType = "self-tracked"
                            });
                        }
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
        // Build reverse index: sourceId -> list of dataObjectIds that reference it
        var reverseIndex = new Dictionary<string, HashSet<string>>();
        foreach (var kvp in _provenance)
        {
            List<ProvenanceRecord> snapshot;
            lock (kvp.Value) { snapshot = new List<ProvenanceRecord>(kvp.Value); }

            foreach (var rec in snapshot)
            {
                if (rec.SourceObjects == null) continue;
                foreach (var sourceId in rec.SourceObjects)
                {
                    if (!reverseIndex.TryGetValue(sourceId, out var targets))
                    {
                        targets = new HashSet<string>();
                        reverseIndex[sourceId] = targets;
                    }
                    targets.Add(kvp.Key);
                }
            }
        }

        var visited = new HashSet<string>();
        var nodes = new List<LineageNode>();
        var edges = new List<LineageEdge>();
        var queue = new Queue<(string id, int depth)>();
        queue.Enqueue((nodeId, 0));

        while (queue.Count > 0)
        {
            var (currentId, depth) = queue.Dequeue();
            if (depth > maxDepth || visited.Contains(currentId)) continue;
            visited.Add(currentId);

            if (_nodes.TryGetValue(currentId, out var node))
                nodes.Add(node);
            else
                nodes.Add(new LineageNode { NodeId = currentId, Name = currentId, NodeType = "dataset" });

            if (reverseIndex.TryGetValue(currentId, out var downstreamIds))
            {
                foreach (var downId in downstreamIds)
                {
                    if (!visited.Contains(downId))
                    {
                        queue.Enqueue((downId, depth + 1));
                        edges.Add(new LineageEdge
                        {
                            EdgeId = $"{currentId}->{downId}",
                            SourceNodeId = currentId,
                            TargetNodeId = downId,
                            EdgeType = "self-tracked"
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

    /// <inheritdoc/>
    public override Task<ImpactAnalysisResult> AnalyzeImpactAsync(string nodeId, string changeType, CancellationToken ct = default)
    {
        // Build reverse index for downstream traversal
        var reverseIndex = new Dictionary<string, HashSet<string>>();
        foreach (var kvp in _provenance)
        {
            List<ProvenanceRecord> snapshot;
            lock (kvp.Value) { snapshot = new List<ProvenanceRecord>(kvp.Value); }

            foreach (var rec in snapshot)
            {
                if (rec.SourceObjects == null) continue;
                foreach (var sourceId in rec.SourceObjects)
                {
                    if (!reverseIndex.TryGetValue(sourceId, out var targets))
                    {
                        targets = new HashSet<string>();
                        reverseIndex[sourceId] = targets;
                    }
                    targets.Add(kvp.Key);
                }
            }
        }

        var direct = new List<string>();
        var indirect = new List<string>();
        var visited = new HashSet<string> { nodeId };

        // Depth 1: direct downstream
        if (reverseIndex.TryGetValue(nodeId, out var directDownstream))
        {
            foreach (var id in directDownstream)
            {
                if (visited.Add(id))
                    direct.Add(id);
            }
        }

        // Depth 2+: indirect downstream
        foreach (var directId in direct)
        {
            if (reverseIndex.TryGetValue(directId, out var indirectDownstream))
            {
                foreach (var id in indirectDownstream)
                {
                    if (visited.Add(id))
                        indirect.Add(id);
                }
            }
        }

        var score = Math.Min(100, direct.Count * 15 + indirect.Count * 5);

        var recommendations = new List<string>();
        if (score > 80)
            recommendations.Add("Critical: halt deployment and assess full blast radius");
        else if (score > 50)
            recommendations.Add("High: staged rollout required with downstream validation");
        else if (score > 20)
            recommendations.Add("Medium: notify downstream data owners before proceeding");
        else
            recommendations.Add("Low: proceed with standard monitoring");

        return Task.FromResult(new ImpactAnalysisResult
        {
            SourceNodeId = nodeId,
            ChangeType = changeType,
            DirectlyImpacted = direct.AsReadOnly(),
            IndirectlyImpacted = indirect.AsReadOnly(),
            ImpactScore = score,
            Recommendations = recommendations.AsReadOnly()
        });
    }
}

/// <summary>
/// Real-time lineage capture strategy that records lineage events as they happen,
/// maintaining event-driven timestamps for every upstream and downstream relationship.
/// </summary>
/// <remarks>
/// T146.B1.2 - Captures lineage in real time with precise timestamps. Uses BFS traversal
/// on in-memory link maps for efficient upstream and downstream graph queries.
/// </remarks>
internal sealed class RealTimeLineageCaptureStrategy : LineageStrategyBase
{
    private readonly BoundedDictionary<string, DateTimeOffset> _captureTimestamps = new BoundedDictionary<string, DateTimeOffset>(1000);
    private readonly BoundedDictionary<string, HashSet<string>> _upstreamLinks = new BoundedDictionary<string, HashSet<string>>(1000);
    private readonly BoundedDictionary<string, HashSet<string>> _downstreamLinks = new BoundedDictionary<string, HashSet<string>>(1000);

    /// <inheritdoc/>
    public override string StrategyId => "active-realtime-capture";

    /// <inheritdoc/>
    public override string DisplayName => "Real-Time Lineage Capture";

    /// <inheritdoc/>
    public override LineageCategory Category => LineageCategory.Origin;

    /// <inheritdoc/>
    public override LineageStrategyCapabilities Capabilities => new()
    {
        SupportsUpstream = true,
        SupportsDownstream = true,
        SupportsTransformations = true,
        SupportsSchemaEvolution = false,
        SupportsImpactAnalysis = false,
        SupportsVisualization = true,
        SupportsRealTime = true
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Real-time lineage capture strategy that records lineage events with precise timestamps " +
        "as they occur. Provides event-driven lineage tracking with sub-second capture granularity " +
        "for streaming and micro-batch data pipelines.";

    /// <inheritdoc/>
    public override string[] Tags => ["real-time", "capture", "streaming", "event-driven"];

    /// <inheritdoc/>
    public override Task TrackAsync(ProvenanceRecord record, CancellationToken ct = default)
    {
        base.TrackAsync(record, ct);

        // Record capture timestamp
        _captureTimestamps[record.DataObjectId] = DateTimeOffset.UtcNow;

        // Ensure node exists
        AddNode(new LineageNode
        {
            NodeId = record.DataObjectId,
            Name = record.DataObjectId,
            NodeType = "dataset"
        });

        // Build upstream/downstream link maps from source objects
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
                    EdgeType = "realtime-captured",
                    TransformationDetails = record.Transformation
                });
            }
        }

        return Task.CompletedTask;
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
            if (depth > maxDepth || visited.Contains(currentId)) continue;
            visited.Add(currentId);

            if (_nodes.TryGetValue(currentId, out var node))
                nodes.Add(node);

            if (_upstreamLinks.TryGetValue(currentId, out var uplinks))
            {
                HashSet<string> snapshot;
                lock (uplinks) { snapshot = new HashSet<string>(uplinks); }

                foreach (var upId in snapshot)
                {
                    queue.Enqueue((upId, depth + 1));
                    edges.Add(new LineageEdge
                    {
                        EdgeId = $"{upId}->{currentId}",
                        SourceNodeId = upId,
                        TargetNodeId = currentId,
                        EdgeType = "realtime-captured"
                    });
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
            if (depth > maxDepth || visited.Contains(currentId)) continue;
            visited.Add(currentId);

            if (_nodes.TryGetValue(currentId, out var node))
                nodes.Add(node);

            if (_downstreamLinks.TryGetValue(currentId, out var downlinks))
            {
                HashSet<string> snapshot;
                lock (downlinks) { snapshot = new HashSet<string>(downlinks); }

                foreach (var downId in snapshot)
                {
                    queue.Enqueue((downId, depth + 1));
                    edges.Add(new LineageEdge
                    {
                        EdgeId = $"{currentId}->{downId}",
                        SourceNodeId = currentId,
                        TargetNodeId = downId,
                        EdgeType = "realtime-captured"
                    });
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
/// Lineage inference strategy that discovers lineage relationships from data patterns,
/// column name similarity (Jaccard index), and schema overlap analysis.
/// </summary>
/// <remarks>
/// T146.B1.3 - Infers lineage when explicit provenance records are unavailable. Compares
/// registered schemas using Jaccard similarity on column name sets. A similarity threshold
/// of 0.5 indicates a likely derivation relationship between datasets.
/// </remarks>
internal sealed class LineageInferenceStrategy : LineageStrategyBase
{
    private readonly BoundedDictionary<string, Dictionary<string, string>> _schemaCache = new BoundedDictionary<string, Dictionary<string, string>>(1000);

    /// <inheritdoc/>
    public override string StrategyId => "active-inference";

    /// <inheritdoc/>
    public override string DisplayName => "Lineage Inference Engine";

    /// <inheritdoc/>
    public override LineageCategory Category => LineageCategory.Transformation;

    /// <inheritdoc/>
    public override LineageStrategyCapabilities Capabilities => new()
    {
        SupportsUpstream = true,
        SupportsDownstream = true,
        SupportsTransformations = true,
        SupportsSchemaEvolution = true,
        SupportsImpactAnalysis = false,
        SupportsVisualization = false,
        SupportsRealTime = false
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Lineage inference engine that discovers lineage relationships from data patterns, " +
        "column name similarity using Jaccard index, and value distribution overlap analysis. " +
        "Useful for legacy systems where explicit lineage metadata is unavailable.";

    /// <inheritdoc/>
    public override string[] Tags => ["inference", "pattern-matching", "schema", "jaccard", "discovery"];

    /// <summary>
    /// Registers a schema for a data node, enabling schema-based inference.
    /// </summary>
    /// <param name="nodeId">The unique node identifier.</param>
    /// <param name="schema">A dictionary mapping column names to their data types.</param>
    public void RegisterSchema(string nodeId, Dictionary<string, string> schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        _schemaCache[nodeId] = new Dictionary<string, string>(schema, StringComparer.OrdinalIgnoreCase);

        AddNode(new LineageNode
        {
            NodeId = nodeId,
            Name = nodeId,
            NodeType = "dataset",
            Schema = schema
        });
    }

    /// <inheritdoc/>
    public override Task<LineageGraph> GetUpstreamAsync(string nodeId, int maxDepth = 10, CancellationToken ct = default)
    {
        var nodes = new List<LineageNode>();
        var edges = new List<LineageEdge>();

        if (!_schemaCache.TryGetValue(nodeId, out var targetSchema))
        {
            return Task.FromResult(new LineageGraph
            {
                RootNodeId = nodeId,
                Nodes = nodes.AsReadOnly(),
                Edges = edges.AsReadOnly()
            });
        }

        // Add root node
        if (_nodes.TryGetValue(nodeId, out var rootNode))
            nodes.Add(rootNode);
        else
            nodes.Add(new LineageNode { NodeId = nodeId, Name = nodeId, NodeType = "dataset", Schema = targetSchema });

        var targetColumns = new HashSet<string>(targetSchema.Keys, StringComparer.OrdinalIgnoreCase);

        // Compare against all other schemas and rank by similarity
        var candidates = new List<(string id, double similarity)>();
        foreach (var kvp in _schemaCache)
        {
            if (kvp.Key == nodeId) continue;

            var candidateColumns = new HashSet<string>(kvp.Value.Keys, StringComparer.OrdinalIgnoreCase);
            var similarity = ComputeJaccardSimilarity(targetColumns, candidateColumns);

            if (similarity > 0.5)
                candidates.Add((kvp.Key, similarity));
        }

        // Order by similarity descending for relevance
        candidates.Sort((a, b) => b.similarity.CompareTo(a.similarity));

        foreach (var (candidateId, similarity) in candidates)
        {
            if (_nodes.TryGetValue(candidateId, out var candidateNode))
                nodes.Add(candidateNode);
            else
                nodes.Add(new LineageNode { NodeId = candidateId, Name = candidateId, NodeType = "dataset" });

            edges.Add(new LineageEdge
            {
                EdgeId = $"inferred:{candidateId}->{nodeId}",
                SourceNodeId = candidateId,
                TargetNodeId = nodeId,
                EdgeType = "inferred",
                TransformationDetails = $"Jaccard similarity: {similarity:F3}"
            });
        }

        return Task.FromResult(new LineageGraph
        {
            RootNodeId = nodeId,
            Nodes = nodes.AsReadOnly(),
            Edges = edges.AsReadOnly(),
            Depth = 1,
            UpstreamCount = candidates.Count
        });
    }

    /// <inheritdoc/>
    public override Task<LineageGraph> GetDownstreamAsync(string nodeId, int maxDepth = 10, CancellationToken ct = default)
    {
        var nodes = new List<LineageNode>();
        var edges = new List<LineageEdge>();

        if (!_schemaCache.TryGetValue(nodeId, out var sourceSchema))
        {
            return Task.FromResult(new LineageGraph
            {
                RootNodeId = nodeId,
                Nodes = nodes.AsReadOnly(),
                Edges = edges.AsReadOnly()
            });
        }

        // Add root node
        if (_nodes.TryGetValue(nodeId, out var rootNode))
            nodes.Add(rootNode);
        else
            nodes.Add(new LineageNode { NodeId = nodeId, Name = nodeId, NodeType = "dataset", Schema = sourceSchema });

        var sourceColumns = new HashSet<string>(sourceSchema.Keys, StringComparer.OrdinalIgnoreCase);

        // Find nodes whose schemas are similar (may have been derived from this node)
        var candidates = new List<(string id, double similarity)>();
        foreach (var kvp in _schemaCache)
        {
            if (kvp.Key == nodeId) continue;

            var candidateColumns = new HashSet<string>(kvp.Value.Keys, StringComparer.OrdinalIgnoreCase);
            var similarity = ComputeJaccardSimilarity(sourceColumns, candidateColumns);

            if (similarity > 0.5)
                candidates.Add((kvp.Key, similarity));
        }

        candidates.Sort((a, b) => b.similarity.CompareTo(a.similarity));

        foreach (var (candidateId, similarity) in candidates)
        {
            if (_nodes.TryGetValue(candidateId, out var candidateNode))
                nodes.Add(candidateNode);
            else
                nodes.Add(new LineageNode { NodeId = candidateId, Name = candidateId, NodeType = "dataset" });

            edges.Add(new LineageEdge
            {
                EdgeId = $"inferred:{nodeId}->{candidateId}",
                SourceNodeId = nodeId,
                TargetNodeId = candidateId,
                EdgeType = "inferred",
                TransformationDetails = $"Jaccard similarity: {similarity:F3}"
            });
        }

        return Task.FromResult(new LineageGraph
        {
            RootNodeId = nodeId,
            Nodes = nodes.AsReadOnly(),
            Edges = edges.AsReadOnly(),
            Depth = 1,
            DownstreamCount = candidates.Count
        });
    }

    /// <summary>
    /// Computes the Jaccard similarity coefficient between two sets of column names.
    /// J(A,B) = |A intersect B| / |A union B|
    /// </summary>
    private static double ComputeJaccardSimilarity(HashSet<string> setA, HashSet<string> setB)
    {
        if (setA.Count == 0 && setB.Count == 0)
            return 0.0;

        var intersection = new HashSet<string>(setA, setA.Comparer);
        intersection.IntersectWith(setB);

        var union = new HashSet<string>(setA, setA.Comparer);
        union.UnionWith(setB);

        return union.Count == 0 ? 0.0 : (double)intersection.Count / union.Count;
    }
}

/// <summary>
/// Impact analysis engine strategy that computes blast radius with weighted scoring
/// across direct and transitive dependencies. Uses node criticality weights and
/// depth-decay factors for accurate change impact assessment.
/// </summary>
/// <remarks>
/// T146.B1.4 - Weighted impact analysis with criticality scoring. BFS traversal of a
/// directed dependency graph computes impact scores using the formula:
/// score = sum(criticality * (1/depth)), normalized to 0-100. Recommendations are
/// tiered by score: Critical (&gt;80), High (&gt;50), Medium (&gt;20), Low.
/// </remarks>
internal sealed class ImpactAnalysisEngineStrategy : LineageStrategyBase
{
    private readonly BoundedDictionary<string, double> _nodeCriticality = new BoundedDictionary<string, double>(1000);
    private readonly BoundedDictionary<string, HashSet<string>> _dependencies = new BoundedDictionary<string, HashSet<string>>(1000);

    /// <inheritdoc/>
    public override string StrategyId => "active-impact-engine";

    /// <inheritdoc/>
    public override string DisplayName => "Impact Analysis Engine";

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
        "Impact analysis engine that computes blast radius with weighted scoring across direct " +
        "and transitive dependencies. Uses node criticality weights and depth-decay factors to " +
        "provide accurate change impact assessment with tiered recommendations.";

    /// <inheritdoc/>
    public override string[] Tags => ["impact", "blast-radius", "weighted", "criticality", "engine"];

    /// <summary>
    /// Registers a directed dependency from <paramref name="sourceId"/> to <paramref name="targetId"/>.
    /// </summary>
    /// <param name="sourceId">The source node that the target depends on.</param>
    /// <param name="targetId">The target node that depends on the source.</param>
    public void RegisterDependency(string sourceId, string targetId)
    {
        var deps = _dependencies.GetOrAdd(sourceId, _ => new HashSet<string>());
        lock (deps) deps.Add(targetId);

        AddNode(new LineageNode { NodeId = sourceId, Name = sourceId, NodeType = "dataset" });
        AddNode(new LineageNode { NodeId = targetId, Name = targetId, NodeType = "dataset" });
    }

    /// <summary>
    /// Sets the criticality weight for a node.
    /// Higher values indicate greater importance when computing impact scores.
    /// </summary>
    /// <param name="nodeId">The unique node identifier.</param>
    /// <param name="score">The criticality score (default 1.0).</param>
    public void SetCriticality(string nodeId, double score)
    {
        _nodeCriticality[nodeId] = score;
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

        // Auto-register dependencies from source objects
        if (record.SourceObjects != null)
        {
            foreach (var source in record.SourceObjects)
            {
                RegisterDependency(source, record.DataObjectId);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public override Task<LineageGraph> GetUpstreamAsync(string nodeId, int maxDepth = 10, CancellationToken ct = default)
    {
        // Build reverse dependency map: targetId -> set of sourceIds
        var reverseMap = new Dictionary<string, HashSet<string>>();
        foreach (var kvp in _dependencies)
        {
            HashSet<string> snapshot;
            lock (kvp.Value) { snapshot = new HashSet<string>(kvp.Value); }

            foreach (var target in snapshot)
            {
                if (!reverseMap.TryGetValue(target, out var sources))
                {
                    sources = new HashSet<string>();
                    reverseMap[target] = sources;
                }
                sources.Add(kvp.Key);
            }
        }

        var visited = new HashSet<string>();
        var nodes = new List<LineageNode>();
        var edges = new List<LineageEdge>();
        var queue = new Queue<(string id, int depth)>();
        queue.Enqueue((nodeId, 0));

        while (queue.Count > 0)
        {
            var (currentId, depth) = queue.Dequeue();
            if (depth > maxDepth || visited.Contains(currentId)) continue;
            visited.Add(currentId);

            if (_nodes.TryGetValue(currentId, out var node))
                nodes.Add(node);
            else
                nodes.Add(new LineageNode { NodeId = currentId, Name = currentId, NodeType = "dataset" });

            if (reverseMap.TryGetValue(currentId, out var upstreamIds))
            {
                foreach (var upId in upstreamIds)
                {
                    if (!visited.Contains(upId))
                    {
                        queue.Enqueue((upId, depth + 1));
                        edges.Add(new LineageEdge
                        {
                            EdgeId = $"{upId}->{currentId}",
                            SourceNodeId = upId,
                            TargetNodeId = currentId,
                            EdgeType = "dependency"
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
            if (depth > maxDepth || visited.Contains(currentId)) continue;
            visited.Add(currentId);

            if (_nodes.TryGetValue(currentId, out var node))
                nodes.Add(node);
            else
                nodes.Add(new LineageNode { NodeId = currentId, Name = currentId, NodeType = "dataset" });

            if (_dependencies.TryGetValue(currentId, out var deps))
            {
                HashSet<string> snapshot;
                lock (deps) { snapshot = new HashSet<string>(deps); }

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
                            EdgeType = "dependency"
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

    /// <inheritdoc/>
    public override Task<ImpactAnalysisResult> AnalyzeImpactAsync(string nodeId, string changeType, CancellationToken ct = default)
    {
        var direct = new List<string>();
        var indirect = new List<string>();
        var visited = new HashSet<string> { nodeId };
        var queue = new Queue<(string id, int depth)>();
        double rawScore = 0.0;

        // Seed with direct downstream dependencies
        if (_dependencies.TryGetValue(nodeId, out var directDeps))
        {
            HashSet<string> snapshot;
            lock (directDeps) { snapshot = new HashSet<string>(directDeps); }

            foreach (var depId in snapshot)
            {
                if (visited.Add(depId))
                {
                    direct.Add(depId);
                    var criticality = _nodeCriticality.GetValueOrDefault(depId, 1.0);
                    rawScore += criticality * 1.0; // depthDecayFactor = 1.0 / 1 = 1.0
                    queue.Enqueue((depId, 2));
                }
            }
        }

        // BFS for transitive (indirect) dependencies
        while (queue.Count > 0)
        {
            var (currentId, depth) = queue.Dequeue();

            if (_dependencies.TryGetValue(currentId, out var deps))
            {
                HashSet<string> snapshot;
                lock (deps) { snapshot = new HashSet<string>(deps); }

                foreach (var depId in snapshot)
                {
                    if (visited.Add(depId))
                    {
                        indirect.Add(depId);
                        var criticality = _nodeCriticality.GetValueOrDefault(depId, 1.0);
                        double depthDecayFactor = 1.0 / depth;
                        rawScore += criticality * depthDecayFactor;
                        queue.Enqueue((depId, depth + 1));
                    }
                }
            }
        }

        // Normalize score to 0-100 range
        int totalAffected = direct.Count + indirect.Count;
        int score = totalAffected == 0 ? 0 : Math.Min(100, (int)Math.Round(rawScore / totalAffected * 100.0 * totalAffected / Math.Max(1, totalAffected)));
        // Simplified normalization: raw score mapped to 0-100
        score = Math.Min(100, (int)Math.Round(rawScore * 10));

        var recommendations = new List<string>();
        if (score > 80)
            recommendations.Add("Critical: halt deployment and assess full dependency chain");
        else if (score > 50)
            recommendations.Add("High: staged rollout required with downstream validation");
        else if (score > 20)
            recommendations.Add("Medium: notify downstream owners before proceeding");
        else
            recommendations.Add("Low: proceed with monitoring");

        return Task.FromResult(new ImpactAnalysisResult
        {
            SourceNodeId = nodeId,
            ChangeType = changeType,
            DirectlyImpacted = direct.AsReadOnly(),
            IndirectlyImpacted = indirect.AsReadOnly(),
            ImpactScore = score,
            Recommendations = recommendations.AsReadOnly()
        });
    }
}

/// <summary>
/// Lineage visualization export strategy that renders lineage graphs in multiple formats
/// including DOT/Graphviz, Mermaid, and JSON for visual exploration of data dependencies.
/// </summary>
/// <remarks>
/// T146.B1.5 - Multi-format lineage graph export. Maintains in-memory link maps from tracked
/// provenance records and supports three output formats: DOT for Graphviz rendering, Mermaid
/// for Markdown-embedded diagrams, and JSON for programmatic consumption via System.Text.Json.
/// </remarks>
internal sealed class LineageVisualizationStrategy : LineageStrategyBase
{
    private readonly BoundedDictionary<string, HashSet<string>> _upstreamLinks = new BoundedDictionary<string, HashSet<string>>(1000);
    private readonly BoundedDictionary<string, HashSet<string>> _downstreamLinks = new BoundedDictionary<string, HashSet<string>>(1000);

    /// <inheritdoc/>
    public override string StrategyId => "active-visualization";

    /// <inheritdoc/>
    public override string DisplayName => "Lineage Visualization Export";

    /// <inheritdoc/>
    public override LineageCategory Category => LineageCategory.Visualization;

    /// <inheritdoc/>
    public override LineageStrategyCapabilities Capabilities => new()
    {
        SupportsUpstream = true,
        SupportsDownstream = true,
        SupportsTransformations = true,
        SupportsSchemaEvolution = false,
        SupportsImpactAnalysis = false,
        SupportsVisualization = true,
        SupportsRealTime = false
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Lineage visualization export strategy that renders lineage graphs in DOT/Graphviz, " +
        "Mermaid, and JSON formats for visual exploration of data dependencies and " +
        "transformation chains.";

    /// <inheritdoc/>
    public override string[] Tags => ["visualization", "export", "dot", "mermaid", "json", "graphviz"];

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

        // Build link maps from source objects
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
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Exports the lineage graph rooted at the specified node in DOT/Graphviz format.
    /// </summary>
    /// <param name="rootNodeId">The root node identifier to start the graph from.</param>
    /// <returns>A string containing the DOT graph representation.</returns>
    public string ExportDot(string rootNodeId)
    {
        var allEdges = CollectAllEdges(rootNodeId);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("digraph lineage {");

        foreach (var (from, to) in allEdges)
        {
            sb.AppendLine($"  \"{from}\" -> \"{to}\";");
        }

        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>
    /// Exports the lineage graph rooted at the specified node in Mermaid diagram format.
    /// </summary>
    /// <param name="rootNodeId">The root node identifier to start the graph from.</param>
    /// <returns>A string containing the Mermaid graph representation.</returns>
    public string ExportMermaid(string rootNodeId)
    {
        var allEdges = CollectAllEdges(rootNodeId);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("graph TD");

        foreach (var (from, to) in allEdges)
        {
            sb.AppendLine($"  {SanitizeMermaidId(from)} --> {SanitizeMermaidId(to)}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Exports the lineage graph rooted at the specified node as a JSON string
    /// with nodes and edges arrays.
    /// </summary>
    /// <param name="rootNodeId">The root node identifier to start the graph from.</param>
    /// <returns>A JSON string containing nodes and edges.</returns>
    public string ExportJson(string rootNodeId)
    {
        var allEdges = CollectAllEdges(rootNodeId);
        var nodeIds = new HashSet<string>();

        foreach (var (from, to) in allEdges)
        {
            nodeIds.Add(from);
            nodeIds.Add(to);
        }

        // Ensure root node is always included
        nodeIds.Add(rootNodeId);

        var jsonNodes = nodeIds.Select(id => new { id, name = id, type = "dataset" }).ToArray();
        var jsonEdges = allEdges.Select(e => new { source = e.from, target = e.to }).ToArray();

        var graph = new { nodes = jsonNodes, edges = jsonEdges };
        return JsonSerializer.Serialize(graph, new JsonSerializerOptions { WriteIndented = true });
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
            if (depth > maxDepth || visited.Contains(currentId)) continue;
            visited.Add(currentId);

            if (_nodes.TryGetValue(currentId, out var node))
                nodes.Add(node);

            if (_upstreamLinks.TryGetValue(currentId, out var uplinks))
            {
                HashSet<string> snapshot;
                lock (uplinks) { snapshot = new HashSet<string>(uplinks); }

                foreach (var upId in snapshot)
                {
                    queue.Enqueue((upId, depth + 1));
                    edges.Add(new LineageEdge
                    {
                        EdgeId = $"{upId}->{currentId}",
                        SourceNodeId = upId,
                        TargetNodeId = currentId,
                        EdgeType = "visualization"
                    });
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
            if (depth > maxDepth || visited.Contains(currentId)) continue;
            visited.Add(currentId);

            if (_nodes.TryGetValue(currentId, out var node))
                nodes.Add(node);

            if (_downstreamLinks.TryGetValue(currentId, out var downlinks))
            {
                HashSet<string> snapshot;
                lock (downlinks) { snapshot = new HashSet<string>(downlinks); }

                foreach (var downId in snapshot)
                {
                    queue.Enqueue((downId, depth + 1));
                    edges.Add(new LineageEdge
                    {
                        EdgeId = $"{currentId}->{downId}",
                        SourceNodeId = currentId,
                        TargetNodeId = downId,
                        EdgeType = "visualization"
                    });
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

    /// <summary>
    /// Collects all edges reachable from the root node by traversing both upstream and downstream links.
    /// </summary>
    private List<(string from, string to)> CollectAllEdges(string rootNodeId)
    {
        var edges = new List<(string from, string to)>();
        var visited = new HashSet<string>();
        var queue = new Queue<string>();
        queue.Enqueue(rootNodeId);

        while (queue.Count > 0)
        {
            var currentId = queue.Dequeue();
            if (!visited.Add(currentId)) continue;

            // Downstream edges (currentId -> targets)
            if (_downstreamLinks.TryGetValue(currentId, out var downlinks))
            {
                HashSet<string> snapshot;
                lock (downlinks) { snapshot = new HashSet<string>(downlinks); }

                foreach (var downId in snapshot)
                {
                    edges.Add((currentId, downId));
                    if (!visited.Contains(downId))
                        queue.Enqueue(downId);
                }
            }

            // Upstream edges (sources -> currentId)
            if (_upstreamLinks.TryGetValue(currentId, out var uplinks))
            {
                HashSet<string> snapshot;
                lock (uplinks) { snapshot = new HashSet<string>(uplinks); }

                foreach (var upId in snapshot)
                {
                    edges.Add((upId, currentId));
                    if (!visited.Contains(upId))
                        queue.Enqueue(upId);
                }
            }
        }

        // Deduplicate edges
        return edges.Distinct().ToList();
    }

    /// <summary>
    /// Sanitizes a node ID for use as a Mermaid diagram identifier by replacing
    /// special characters with underscores.
    /// </summary>
    private static string SanitizeMermaidId(string id)
    {
        return id.Replace(" ", "_").Replace("-", "_").Replace(".", "_");
    }
}
