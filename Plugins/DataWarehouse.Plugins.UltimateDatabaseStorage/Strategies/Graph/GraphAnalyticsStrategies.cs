using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.UltimateDatabaseStorage.Strategies.Graph;

/// <summary>
/// Graph analytics algorithm base class.
/// Provides common functionality for graph analysis algorithms.
/// </summary>
public abstract class GraphAnalyticsAlgorithmBase
{
    /// <summary>Gets the algorithm identifier.</summary>
    public abstract string AlgorithmId { get; }

    /// <summary>Gets the algorithm name.</summary>
    public abstract string Name { get; }

    /// <summary>Gets the algorithm category.</summary>
    public abstract GraphAnalyticsCategory Category { get; }

    /// <summary>Gets whether this algorithm requires iterative computation.</summary>
    public abstract bool IsIterative { get; }
}

/// <summary>
/// Graph analytics algorithm category.
/// </summary>
public enum GraphAnalyticsCategory
{
    /// <summary>Centrality measures (PageRank, betweenness, etc.)</summary>
    Centrality,
    /// <summary>Community detection algorithms.</summary>
    CommunityDetection,
    /// <summary>Path finding algorithms.</summary>
    PathFinding,
    /// <summary>Graph structure analysis.</summary>
    StructureAnalysis,
    /// <summary>Similarity and recommendation.</summary>
    Similarity,
    /// <summary>Link prediction.</summary>
    LinkPrediction
}

/// <summary>
/// PageRank algorithm implementation.
/// Computes the importance/influence of vertices in a graph.
/// </summary>
/// <remarks>
/// Features:
/// - Configurable damping factor
/// - Convergence detection
/// - Supports personalized PageRank
/// - Handles dangling nodes (no outgoing edges)
/// </remarks>
public sealed class PageRankAlgorithm : GraphAnalyticsAlgorithmBase
{
    /// <inheritdoc/>
    public override string AlgorithmId => "analytics.pagerank";

    /// <inheritdoc/>
    public override string Name => "PageRank";

    /// <inheritdoc/>
    public override GraphAnalyticsCategory Category => GraphAnalyticsCategory.Centrality;

    /// <inheritdoc/>
    public override bool IsIterative => true;

    /// <summary>Gets or sets the damping factor (typically 0.85).</summary>
    public double DampingFactor { get; set; } = 0.85;

    /// <summary>Gets or sets the convergence tolerance.</summary>
    public double Tolerance { get; set; } = 1e-6;

    /// <summary>Gets or sets the maximum number of iterations.</summary>
    public int MaxIterations { get; set; } = 100;

    /// <summary>
    /// Computes PageRank scores for all vertices.
    /// </summary>
    /// <param name="adjacency">Adjacency list: vertex -> list of outgoing neighbor vertices.</param>
    /// <param name="personalizationVector">Optional personalization vector for personalized PageRank.</param>
    /// <returns>PageRank result with scores and metadata.</returns>
    public PageRankResult Compute(
        IDictionary<string, IList<string>> adjacency,
        IDictionary<string, double>? personalizationVector = null)
    {
        var vertices = adjacency.Keys.ToList();
        var n = vertices.Count;
        if (n == 0)
        {
            return new PageRankResult { Scores = new Dictionary<string, double>(), Iterations = 0, Converged = true };
        }

        var vertexIndex = vertices.Select((v, i) => (v, i)).ToDictionary(x => x.v, x => x.i);

        // Initialize scores
        var scores = new double[n];
        var newScores = new double[n];
        var initialScore = 1.0 / n;

        for (int i = 0; i < n; i++)
        {
            if (personalizationVector != null && personalizationVector.TryGetValue(vertices[i], out var pv))
            {
                scores[i] = pv;
            }
            else
            {
                scores[i] = initialScore;
            }
        }

        // Pre-compute out-degrees
        var outDegrees = new int[n];
        for (int i = 0; i < n; i++)
        {
            outDegrees[i] = adjacency[vertices[i]].Count;
        }

        // Find dangling nodes (no outgoing edges)
        var danglingNodes = vertices.Where(v => adjacency[v].Count == 0).Select(v => vertexIndex[v]).ToList();

        // Iterative computation
        int iteration;
        double diff = double.MaxValue;

        for (iteration = 0; iteration < MaxIterations && diff > Tolerance; iteration++)
        {
            // Calculate dangling node contribution
            double danglingSum = 0;
            foreach (var dn in danglingNodes)
            {
                danglingSum += scores[dn];
            }
            var danglingContribution = danglingSum / n;

            // Initialize new scores with teleportation probability
            var teleportProb = (1 - DampingFactor) / n;
            for (int i = 0; i < n; i++)
            {
                newScores[i] = teleportProb + DampingFactor * danglingContribution;
            }

            // Add contributions from incoming edges
            for (int i = 0; i < n; i++)
            {
                var vertex = vertices[i];
                var neighbors = adjacency[vertex];
                if (neighbors.Count == 0) continue;

                var contribution = DampingFactor * scores[i] / neighbors.Count;
                foreach (var neighbor in neighbors)
                {
                    if (vertexIndex.TryGetValue(neighbor, out var neighborIdx))
                    {
                        newScores[neighborIdx] += contribution;
                    }
                }
            }

            // Calculate convergence
            diff = 0;
            for (int i = 0; i < n; i++)
            {
                diff += Math.Abs(newScores[i] - scores[i]);
            }

            // Swap arrays
            (scores, newScores) = (newScores, scores);
        }

        // Build result
        var result = new Dictionary<string, double>();
        for (int i = 0; i < n; i++)
        {
            result[vertices[i]] = scores[i];
        }

        return new PageRankResult
        {
            Scores = result,
            Iterations = iteration,
            Converged = diff <= Tolerance,
            FinalDifference = diff
        };
    }
}

/// <summary>
/// PageRank computation result.
/// </summary>
public sealed class PageRankResult
{
    /// <summary>Gets the PageRank score for each vertex.</summary>
    public required Dictionary<string, double> Scores { get; init; }

    /// <summary>Gets the number of iterations performed.</summary>
    public int Iterations { get; init; }

    /// <summary>Gets whether the algorithm converged.</summary>
    public bool Converged { get; init; }

    /// <summary>Gets the final difference between iterations.</summary>
    public double FinalDifference { get; init; }

    /// <summary>Gets the top N vertices by PageRank score.</summary>
    public IEnumerable<(string Vertex, double Score)> GetTopN(int n)
    {
        return Scores.OrderByDescending(kv => kv.Value).Take(n).Select(kv => (kv.Key, kv.Value));
    }
}

/// <summary>
/// Betweenness centrality algorithm.
/// Measures how often a vertex lies on shortest paths between other vertices.
/// </summary>
public sealed class BetweennessCentralityAlgorithm : GraphAnalyticsAlgorithmBase
{
    /// <inheritdoc/>
    public override string AlgorithmId => "analytics.betweenness";

    /// <inheritdoc/>
    public override string Name => "Betweenness Centrality";

    /// <inheritdoc/>
    public override GraphAnalyticsCategory Category => GraphAnalyticsCategory.Centrality;

    /// <inheritdoc/>
    public override bool IsIterative => false;

    /// <summary>Gets or sets whether to normalize the scores.</summary>
    public bool Normalize { get; set; } = true;

    /// <summary>
    /// Computes betweenness centrality using Brandes' algorithm.
    /// </summary>
    /// <param name="adjacency">Adjacency list.</param>
    /// <returns>Betweenness centrality for each vertex.</returns>
    public Dictionary<string, double> Compute(IDictionary<string, IList<string>> adjacency)
    {
        var vertices = adjacency.Keys.ToList();
        var n = vertices.Count;
        var centrality = new Dictionary<string, double>();

        foreach (var v in vertices)
        {
            centrality[v] = 0.0;
        }

        foreach (var s in vertices)
        {
            // Single-source shortest paths from s
            var stack = new Stack<string>();
            var predecessors = new Dictionary<string, List<string>>();
            var sigma = new Dictionary<string, double>();
            var dist = new Dictionary<string, int>();
            var delta = new Dictionary<string, double>();

            foreach (var v in vertices)
            {
                predecessors[v] = new List<string>();
                sigma[v] = 0;
                dist[v] = -1;
                delta[v] = 0;
            }

            sigma[s] = 1;
            dist[s] = 0;

            var queue = new Queue<string>();
            queue.Enqueue(s);

            // BFS
            while (queue.Count > 0)
            {
                var v = queue.Dequeue();
                stack.Push(v);

                foreach (var w in adjacency[v])
                {
                    if (!adjacency.ContainsKey(w)) continue;

                    // First visit
                    if (dist[w] < 0)
                    {
                        queue.Enqueue(w);
                        dist[w] = dist[v] + 1;
                    }

                    // Shortest path via v
                    if (dist[w] == dist[v] + 1)
                    {
                        sigma[w] += sigma[v];
                        predecessors[w].Add(v);
                    }
                }
            }

            // Accumulation
            while (stack.Count > 0)
            {
                var w = stack.Pop();
                foreach (var v in predecessors[w])
                {
                    delta[v] += (sigma[v] / sigma[w]) * (1 + delta[w]);
                }

                if (w != s)
                {
                    centrality[w] += delta[w];
                }
            }
        }

        // Normalize if requested
        if (Normalize && n > 2)
        {
            var scale = 1.0 / ((n - 1) * (n - 2));
            foreach (var v in vertices)
            {
                centrality[v] *= scale;
            }
        }

        return centrality;
    }
}

/// <summary>
/// Closeness centrality algorithm.
/// Measures how close a vertex is to all other vertices.
/// </summary>
public sealed class ClosenessCentralityAlgorithm : GraphAnalyticsAlgorithmBase
{
    /// <inheritdoc/>
    public override string AlgorithmId => "analytics.closeness";

    /// <inheritdoc/>
    public override string Name => "Closeness Centrality";

    /// <inheritdoc/>
    public override GraphAnalyticsCategory Category => GraphAnalyticsCategory.Centrality;

    /// <inheritdoc/>
    public override bool IsIterative => false;

    /// <summary>Gets or sets whether to use harmonic mean (handles disconnected graphs).</summary>
    public bool UseHarmonic { get; set; } = true;

    /// <summary>
    /// Computes closeness centrality for all vertices.
    /// </summary>
    /// <param name="adjacency">Adjacency list.</param>
    /// <returns>Closeness centrality for each vertex.</returns>
    public Dictionary<string, double> Compute(IDictionary<string, IList<string>> adjacency)
    {
        var vertices = adjacency.Keys.ToList();
        var n = vertices.Count;
        var centrality = new Dictionary<string, double>();

        foreach (var source in vertices)
        {
            var distances = ComputeShortestPaths(source, adjacency);
            double totalDist = 0;
            int reachable = 0;

            foreach (var v in vertices)
            {
                if (v == source) continue;
                if (distances.TryGetValue(v, out var d) && d > 0)
                {
                    if (UseHarmonic)
                    {
                        totalDist += 1.0 / d;
                    }
                    else
                    {
                        totalDist += d;
                    }
                    reachable++;
                }
            }

            if (reachable > 0)
            {
                centrality[source] = UseHarmonic
                    ? totalDist / (n - 1)
                    : reachable / totalDist;
            }
            else
            {
                centrality[source] = 0;
            }
        }

        return centrality;
    }

    private static Dictionary<string, int> ComputeShortestPaths(string source, IDictionary<string, IList<string>> adjacency)
    {
        var distances = new Dictionary<string, int> { [source] = 0 };
        var queue = new Queue<string>();
        queue.Enqueue(source);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var currentDist = distances[current];

            foreach (var neighbor in adjacency[current])
            {
                if (!distances.ContainsKey(neighbor) && adjacency.ContainsKey(neighbor))
                {
                    distances[neighbor] = currentDist + 1;
                    queue.Enqueue(neighbor);
                }
            }
        }

        return distances;
    }
}

/// <summary>
/// Degree centrality algorithm.
/// Simple centrality based on vertex degree.
/// </summary>
public sealed class DegreeCentralityAlgorithm : GraphAnalyticsAlgorithmBase
{
    /// <inheritdoc/>
    public override string AlgorithmId => "analytics.degree";

    /// <inheritdoc/>
    public override string Name => "Degree Centrality";

    /// <inheritdoc/>
    public override GraphAnalyticsCategory Category => GraphAnalyticsCategory.Centrality;

    /// <inheritdoc/>
    public override bool IsIterative => false;

    /// <summary>Gets or sets the degree type to compute.</summary>
    public DegreeType DegreeType { get; set; } = DegreeType.Total;

    /// <summary>Gets or sets whether to normalize by (n-1).</summary>
    public bool Normalize { get; set; } = true;

    /// <summary>
    /// Computes degree centrality for all vertices.
    /// </summary>
    /// <param name="adjacency">Adjacency list (outgoing edges).</param>
    /// <param name="reverseAdjacency">Optional reverse adjacency (incoming edges).</param>
    /// <returns>Degree centrality for each vertex.</returns>
    public Dictionary<string, double> Compute(
        IDictionary<string, IList<string>> adjacency,
        IDictionary<string, IList<string>>? reverseAdjacency = null)
    {
        var vertices = adjacency.Keys.ToList();
        var n = vertices.Count;
        var normFactor = Normalize && n > 1 ? 1.0 / (n - 1) : 1.0;
        var centrality = new Dictionary<string, double>();

        foreach (var v in vertices)
        {
            double degree = DegreeType switch
            {
                DegreeType.Out => adjacency[v].Count,
                DegreeType.In => reverseAdjacency != null && reverseAdjacency.TryGetValue(v, out var inList) ? inList.Count : 0,
                _ => adjacency[v].Count + (reverseAdjacency != null && reverseAdjacency.TryGetValue(v, out var totalList) ? totalList.Count : 0)
            };

            centrality[v] = degree * normFactor;
        }

        return centrality;
    }
}

/// <summary>
/// Degree type for centrality computation.
/// </summary>
public enum DegreeType
{
    /// <summary>Total degree (in + out).</summary>
    Total,
    /// <summary>In-degree only.</summary>
    In,
    /// <summary>Out-degree only.</summary>
    Out
}

/// <summary>
/// Louvain community detection algorithm.
/// Fast modularity-based community detection.
/// </summary>
public sealed class LouvainCommunityDetection : GraphAnalyticsAlgorithmBase
{
    /// <inheritdoc/>
    public override string AlgorithmId => "analytics.louvain";

    /// <inheritdoc/>
    public override string Name => "Louvain Community Detection";

    /// <inheritdoc/>
    public override GraphAnalyticsCategory Category => GraphAnalyticsCategory.CommunityDetection;

    /// <inheritdoc/>
    public override bool IsIterative => true;

    /// <summary>Gets or sets the minimum modularity improvement threshold.</summary>
    public double MinModularityGain { get; set; } = 1e-7;

    /// <summary>Gets or sets the maximum number of passes.</summary>
    public int MaxPasses { get; set; } = 100;

    /// <summary>
    /// Detects communities using the Louvain algorithm.
    /// </summary>
    /// <param name="edges">List of edges as (source, target, weight) tuples.</param>
    /// <returns>Community detection result.</returns>
    public CommunityDetectionResult Compute(IList<(string Source, string Target, double Weight)> edges)
    {
        // Build adjacency with weights
        var adjacency = new Dictionary<string, Dictionary<string, double>>();
        var vertices = new HashSet<string>();

        foreach (var (source, target, weight) in edges)
        {
            vertices.Add(source);
            vertices.Add(target);

            if (!adjacency.ContainsKey(source))
                adjacency[source] = new Dictionary<string, double>();
            if (!adjacency.ContainsKey(target))
                adjacency[target] = new Dictionary<string, double>();

            adjacency[source][target] = (adjacency[source].GetValueOrDefault(target) + weight);
            adjacency[target][source] = (adjacency[target].GetValueOrDefault(source) + weight);
        }

        var vertexList = vertices.ToList();
        var n = vertexList.Count;

        // Initialize each vertex in its own community
        var community = new Dictionary<string, int>();
        for (int i = 0; i < n; i++)
        {
            community[vertexList[i]] = i;
        }

        // Compute total edge weight
        double m = edges.Sum(e => e.Weight);
        if (m == 0) m = edges.Count;

        // Compute weighted degree for each vertex
        var k = new Dictionary<string, double>();
        foreach (var v in vertexList)
        {
            k[v] = adjacency[v].Values.Sum();
        }

        int pass;
        bool improved = true;

        for (pass = 0; pass < MaxPasses && improved; pass++)
        {
            improved = false;

            // First phase: local moving
            var moved = true;
            while (moved)
            {
                moved = false;

                foreach (var v in vertexList)
                {
                    var currentCommunity = community[v];

                    // Find best community to move to
                    var neighborCommunities = new Dictionary<int, double>();
                    foreach (var (neighbor, weight) in adjacency[v])
                    {
                        var c = community[neighbor];
                        neighborCommunities[c] = neighborCommunities.GetValueOrDefault(c) + weight;
                    }

                    int bestCommunity = currentCommunity;
                    double bestGain = 0;

                    foreach (var (c, eIn) in neighborCommunities)
                    {
                        if (c == currentCommunity) continue;

                        // Calculate modularity gain
                        var gain = CalculateModularityGain(v, c, currentCommunity, community, k, adjacency, m);
                        if (gain > bestGain)
                        {
                            bestGain = gain;
                            bestCommunity = c;
                        }
                    }

                    if (bestGain > MinModularityGain)
                    {
                        community[v] = bestCommunity;
                        moved = true;
                        improved = true;
                    }
                }
            }

            // Renumber communities
            var communityMapping = new Dictionary<int, int>();
            int nextId = 0;
            foreach (var v in vertexList)
            {
                var c = community[v];
                if (!communityMapping.ContainsKey(c))
                {
                    communityMapping[c] = nextId++;
                }
                community[v] = communityMapping[c];
            }
        }

        // Build result
        var modularity = CalculateModularity(community, adjacency, k, m);

        return new CommunityDetectionResult
        {
            Communities = new Dictionary<string, int>(community),
            NumCommunities = community.Values.Distinct().Count(),
            Modularity = modularity,
            Iterations = pass
        };
    }

    private static double CalculateModularityGain(
        string vertex,
        int targetCommunity,
        int sourceCommunity,
        Dictionary<string, int> community,
        Dictionary<string, double> k,
        Dictionary<string, Dictionary<string, double>> adjacency,
        double m)
    {
        // Sum of weights to vertices in target community
        double sumIn = 0;
        double sumTot = 0;

        foreach (var (v, c) in community)
        {
            if (c == targetCommunity)
            {
                sumTot += k[v];
                if (adjacency[vertex].TryGetValue(v, out var w))
                {
                    sumIn += w;
                }
            }
        }

        var ki = k[vertex];
        return (sumIn - sumTot * ki / m) / m;
    }

    private static double CalculateModularity(
        Dictionary<string, int> community,
        Dictionary<string, Dictionary<string, double>> adjacency,
        Dictionary<string, double> k,
        double m)
    {
        double q = 0;

        foreach (var v1 in community.Keys)
        {
            foreach (var v2 in community.Keys)
            {
                if (community[v1] != community[v2]) continue;

                var aij = adjacency[v1].GetValueOrDefault(v2);
                q += aij - k[v1] * k[v2] / (2 * m);
            }
        }

        return q / (2 * m);
    }
}

/// <summary>
/// Community detection result.
/// </summary>
public sealed class CommunityDetectionResult
{
    /// <summary>Gets community assignment for each vertex.</summary>
    public required Dictionary<string, int> Communities { get; init; }

    /// <summary>Gets the number of communities found.</summary>
    public int NumCommunities { get; init; }

    /// <summary>Gets the modularity score.</summary>
    public double Modularity { get; init; }

    /// <summary>Gets the number of iterations.</summary>
    public int Iterations { get; init; }

    /// <summary>Gets vertices in a specific community.</summary>
    public IEnumerable<string> GetCommunityMembers(int communityId)
    {
        return Communities.Where(kv => kv.Value == communityId).Select(kv => kv.Key);
    }

    /// <summary>Gets the size of each community.</summary>
    public Dictionary<int, int> GetCommunitySizes()
    {
        return Communities.Values.GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());
    }
}

/// <summary>
/// Label propagation community detection.
/// Fast, simple community detection using label spreading.
/// </summary>
public sealed class LabelPropagationCommunityDetection : GraphAnalyticsAlgorithmBase
{
    /// <inheritdoc/>
    public override string AlgorithmId => "analytics.label-propagation";

    /// <inheritdoc/>
    public override string Name => "Label Propagation Community Detection";

    /// <inheritdoc/>
    public override GraphAnalyticsCategory Category => GraphAnalyticsCategory.CommunityDetection;

    /// <inheritdoc/>
    public override bool IsIterative => true;

    /// <summary>Gets or sets the maximum number of iterations.</summary>
    public int MaxIterations { get; set; } = 100;

    /// <summary>
    /// Detects communities using label propagation.
    /// </summary>
    /// <param name="adjacency">Adjacency list.</param>
    /// <returns>Community detection result.</returns>
    public CommunityDetectionResult Compute(IDictionary<string, IList<string>> adjacency)
    {
        var vertices = adjacency.Keys.ToList();
        var n = vertices.Count;
        var random = new Random();

        // Initialize each vertex with unique label
        var labels = new Dictionary<string, int>();
        for (int i = 0; i < n; i++)
        {
            labels[vertices[i]] = i;
        }

        int iteration;
        bool changed = true;

        for (iteration = 0; iteration < MaxIterations && changed; iteration++)
        {
            changed = false;

            // Shuffle vertices
            var shuffled = vertices.OrderBy(_ => random.Next()).ToList();

            foreach (var v in shuffled)
            {
                if (adjacency[v].Count == 0) continue;

                // Count neighbor labels
                var labelCounts = new Dictionary<int, int>();
                foreach (var neighbor in adjacency[v])
                {
                    if (labels.TryGetValue(neighbor, out var label))
                    {
                        labelCounts[label] = labelCounts.GetValueOrDefault(label) + 1;
                    }
                }

                if (labelCounts.Count == 0) continue;

                // Find most frequent label
                var maxCount = labelCounts.Values.Max();
                var maxLabels = labelCounts.Where(kv => kv.Value == maxCount).Select(kv => kv.Key).ToList();
                var newLabel = maxLabels[random.Next(maxLabels.Count)];

                if (newLabel != labels[v])
                {
                    labels[v] = newLabel;
                    changed = true;
                }
            }
        }

        // Renumber communities
        var labelMapping = new Dictionary<int, int>();
        int nextId = 0;
        var communities = new Dictionary<string, int>();

        foreach (var v in vertices)
        {
            var label = labels[v];
            if (!labelMapping.ContainsKey(label))
            {
                labelMapping[label] = nextId++;
            }
            communities[v] = labelMapping[label];
        }

        return new CommunityDetectionResult
        {
            Communities = communities,
            NumCommunities = nextId,
            Modularity = 0, // Would need to compute
            Iterations = iteration
        };
    }
}

/// <summary>
/// Triangle counting algorithm.
/// Counts the number of triangles each vertex participates in.
/// </summary>
public sealed class TriangleCountingAlgorithm : GraphAnalyticsAlgorithmBase
{
    /// <inheritdoc/>
    public override string AlgorithmId => "analytics.triangles";

    /// <inheritdoc/>
    public override string Name => "Triangle Counting";

    /// <inheritdoc/>
    public override GraphAnalyticsCategory Category => GraphAnalyticsCategory.StructureAnalysis;

    /// <inheritdoc/>
    public override bool IsIterative => false;

    /// <summary>
    /// Counts triangles for each vertex.
    /// </summary>
    /// <param name="adjacency">Adjacency list (undirected graph assumed).</param>
    /// <returns>Triangle count result.</returns>
    public TriangleCountResult Compute(IDictionary<string, IList<string>> adjacency)
    {
        var vertices = adjacency.Keys.ToList();
        var triangleCounts = new Dictionary<string, int>();
        long totalTriangles = 0;

        foreach (var v in vertices)
        {
            triangleCounts[v] = 0;
        }

        // Build neighbor sets for O(1) lookup
        var neighborSets = new Dictionary<string, HashSet<string>>();
        foreach (var v in vertices)
        {
            neighborSets[v] = new HashSet<string>(adjacency[v]);
        }

        // For each vertex, check pairs of neighbors
        foreach (var v in vertices)
        {
            var neighbors = adjacency[v];
            for (int i = 0; i < neighbors.Count; i++)
            {
                var u = neighbors[i];
                if (string.Compare(u, v, StringComparison.Ordinal) <= 0) continue;

                for (int j = i + 1; j < neighbors.Count; j++)
                {
                    var w = neighbors[j];
                    if (string.Compare(w, v, StringComparison.Ordinal) <= 0) continue;

                    // Check if u and w are connected
                    if (neighborSets[u].Contains(w))
                    {
                        triangleCounts[v]++;
                        triangleCounts[u]++;
                        triangleCounts[w]++;
                        totalTriangles++;
                    }
                }
            }
        }

        return new TriangleCountResult
        {
            VertexTriangleCounts = triangleCounts,
            TotalTriangles = totalTriangles
        };
    }
}

/// <summary>
/// Triangle count result.
/// </summary>
public sealed class TriangleCountResult
{
    /// <summary>Gets triangle count for each vertex.</summary>
    public required Dictionary<string, int> VertexTriangleCounts { get; init; }

    /// <summary>Gets total number of triangles in the graph.</summary>
    public long TotalTriangles { get; init; }

    /// <summary>Computes the clustering coefficient for each vertex.</summary>
    public Dictionary<string, double> ComputeClusteringCoefficients(IDictionary<string, IList<string>> adjacency)
    {
        var coefficients = new Dictionary<string, double>();

        foreach (var (v, triangles) in VertexTriangleCounts)
        {
            var degree = adjacency[v].Count;
            if (degree < 2)
            {
                coefficients[v] = 0;
            }
            else
            {
                var possibleTriangles = degree * (degree - 1) / 2;
                coefficients[v] = (double)triangles / possibleTriangles;
            }
        }

        return coefficients;
    }
}

/// <summary>
/// Connected components algorithm.
/// Finds all connected components in an undirected graph.
/// </summary>
public sealed class ConnectedComponentsAlgorithm : GraphAnalyticsAlgorithmBase
{
    /// <inheritdoc/>
    public override string AlgorithmId => "analytics.connected-components";

    /// <inheritdoc/>
    public override string Name => "Connected Components";

    /// <inheritdoc/>
    public override GraphAnalyticsCategory Category => GraphAnalyticsCategory.StructureAnalysis;

    /// <inheritdoc/>
    public override bool IsIterative => false;

    /// <summary>
    /// Finds connected components using Union-Find.
    /// </summary>
    /// <param name="adjacency">Adjacency list.</param>
    /// <returns>Component assignments.</returns>
    public ConnectedComponentsResult Compute(IDictionary<string, IList<string>> adjacency)
    {
        var vertices = adjacency.Keys.ToList();
        var parent = new Dictionary<string, string>();
        var rank = new Dictionary<string, int>();

        // Initialize
        foreach (var v in vertices)
        {
            parent[v] = v;
            rank[v] = 0;
        }

        // Union all edges
        foreach (var v in vertices)
        {
            foreach (var u in adjacency[v])
            {
                if (parent.ContainsKey(u))
                {
                    Union(v, u, parent, rank);
                }
            }
        }

        // Build component mapping
        var componentMapping = new Dictionary<string, int>();
        var components = new Dictionary<string, int>();
        int nextComponent = 0;

        foreach (var v in vertices)
        {
            var root = Find(v, parent);
            if (!componentMapping.ContainsKey(root))
            {
                componentMapping[root] = nextComponent++;
            }
            components[v] = componentMapping[root];
        }

        return new ConnectedComponentsResult
        {
            Components = components,
            NumComponents = nextComponent
        };
    }

    private static string Find(string v, Dictionary<string, string> parent)
    {
        if (parent[v] != v)
        {
            parent[v] = Find(parent[v], parent); // Path compression
        }
        return parent[v];
    }

    private static void Union(string u, string v, Dictionary<string, string> parent, Dictionary<string, int> rank)
    {
        var rootU = Find(u, parent);
        var rootV = Find(v, parent);

        if (rootU == rootV) return;

        // Union by rank
        if (rank[rootU] < rank[rootV])
        {
            parent[rootU] = rootV;
        }
        else if (rank[rootU] > rank[rootV])
        {
            parent[rootV] = rootU;
        }
        else
        {
            parent[rootV] = rootU;
            rank[rootU]++;
        }
    }
}

/// <summary>
/// Connected components result.
/// </summary>
public sealed class ConnectedComponentsResult
{
    /// <summary>Gets component assignment for each vertex.</summary>
    public required Dictionary<string, int> Components { get; init; }

    /// <summary>Gets the number of components.</summary>
    public int NumComponents { get; init; }

    /// <summary>Gets vertices in a specific component.</summary>
    public IEnumerable<string> GetComponentMembers(int componentId)
    {
        return Components.Where(kv => kv.Value == componentId).Select(kv => kv.Key);
    }

    /// <summary>Gets the largest component.</summary>
    public int GetLargestComponent()
    {
        return Components.Values.GroupBy(c => c).OrderByDescending(g => g.Count()).First().Key;
    }
}

/// <summary>
/// Registry for graph analytics algorithms.
/// </summary>
public sealed class GraphAnalyticsRegistry
{
    private readonly ConcurrentDictionary<string, GraphAnalyticsAlgorithmBase> _algorithms = new();

    /// <summary>
    /// Registers an algorithm.
    /// </summary>
    public void Register(GraphAnalyticsAlgorithmBase algorithm)
    {
        _algorithms[algorithm.AlgorithmId] = algorithm;
    }

    /// <summary>
    /// Gets an algorithm by ID.
    /// </summary>
    public GraphAnalyticsAlgorithmBase? Get(string algorithmId)
    {
        return _algorithms.TryGetValue(algorithmId, out var algo) ? algo : null;
    }

    /// <summary>
    /// Gets all registered algorithms.
    /// </summary>
    public IEnumerable<GraphAnalyticsAlgorithmBase> GetAll()
    {
        return _algorithms.Values;
    }

    /// <summary>
    /// Gets algorithms by category.
    /// </summary>
    public IEnumerable<GraphAnalyticsAlgorithmBase> GetByCategory(GraphAnalyticsCategory category)
    {
        return _algorithms.Values.Where(a => a.Category == category);
    }

    /// <summary>
    /// Creates a registry with all built-in algorithms.
    /// </summary>
    public static GraphAnalyticsRegistry CreateDefault()
    {
        var registry = new GraphAnalyticsRegistry();
        registry.Register(new PageRankAlgorithm());
        registry.Register(new BetweennessCentralityAlgorithm());
        registry.Register(new ClosenessCentralityAlgorithm());
        registry.Register(new DegreeCentralityAlgorithm());
        registry.Register(new LouvainCommunityDetection());
        registry.Register(new LabelPropagationCommunityDetection());
        registry.Register(new TriangleCountingAlgorithm());
        registry.Register(new ConnectedComponentsAlgorithm());
        return registry;
    }
}
