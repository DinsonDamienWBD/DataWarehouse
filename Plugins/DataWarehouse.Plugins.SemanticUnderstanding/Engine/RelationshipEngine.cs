using DataWarehouse.Plugins.SemanticUnderstanding.Models;
using DataWarehouse.SDK.AI;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.SemanticUnderstanding.Engine
{
    /// <summary>
    /// Engine for discovering relationships between documents.
    /// Uses embeddings, shared entities, and AI to find connections.
    /// </summary>
    public sealed class RelationshipEngine
    {
        private readonly IAIProvider? _aiProvider;
        private readonly IVectorStore? _vectorStore;
        private readonly IKnowledgeGraph? _knowledgeGraph;

        public RelationshipEngine(
            IAIProvider? aiProvider = null,
            IVectorStore? vectorStore = null,
            IKnowledgeGraph? knowledgeGraph = null)
        {
            _aiProvider = aiProvider;
            _vectorStore = vectorStore;
            _knowledgeGraph = knowledgeGraph;
        }

        /// <summary>
        /// Discovers relationships for a document against a corpus.
        /// </summary>
        public async Task<RelationshipDiscoveryResult> DiscoverRelationshipsAsync(
            DocumentInfo document,
            IEnumerable<DocumentInfo> corpus,
            RelationshipDiscoveryConfig? config = null,
            CancellationToken ct = default)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            config ??= new RelationshipDiscoveryConfig();

            var relationships = new ConcurrentBag<DocumentRelationship>();
            var corpusList = corpus.ToList();

            // Parallel relationship discovery
            await Parallel.ForEachAsync(
                corpusList,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                async (other, token) =>
                {
                    if (other.Id == document.Id)
                        return;

                    var docRelationships = await FindRelationshipsAsync(document, other, config, token);
                    foreach (var rel in docRelationships)
                    {
                        relationships.Add(rel);
                    }
                });

            // Sort by strength and limit
            var sortedRelationships = relationships
                .OrderByDescending(r => r.Strength)
                .Take(config.MaxRelationshipsPerDocument)
                .ToList();

            // Perform topic clustering if enabled
            var clusters = new List<TopicCluster>();
            if (config.EnableClustering && _aiProvider != null)
            {
                clusters = await ClusterDocumentsAsync(corpusList, config, ct);
            }

            // Add to knowledge graph if available
            if (_knowledgeGraph != null)
            {
                await AddToKnowledgeGraphAsync(document, sortedRelationships, ct);
            }

            sw.Stop();

            return new RelationshipDiscoveryResult
            {
                Relationships = sortedRelationships,
                Clusters = clusters,
                Duration = sw.Elapsed,
                DocumentsAnalyzed = corpusList.Count
            };
        }

        /// <summary>
        /// Finds relationships between two documents.
        /// </summary>
        private async Task<List<DocumentRelationship>> FindRelationshipsAsync(
            DocumentInfo doc1,
            DocumentInfo doc2,
            RelationshipDiscoveryConfig config,
            CancellationToken ct)
        {
            var relationships = new List<DocumentRelationship>();

            // Embedding similarity
            if (config.EnabledTypes.Contains(RelationshipType.SimilarContent) &&
                doc1.Embedding != null && doc2.Embedding != null)
            {
                var similarity = VectorMath.CosineSimilarity(doc1.Embedding, doc2.Embedding);
                if (similarity >= config.MinSimilarityThreshold)
                {
                    relationships.Add(new DocumentRelationship
                    {
                        SourceDocumentId = doc1.Id,
                        TargetDocumentId = doc2.Id,
                        Type = RelationshipType.SimilarContent,
                        Strength = similarity,
                        Confidence = 0.9f,
                        IsBidirectional = true,
                        Evidence = new List<RelationshipEvidence>
                        {
                            new()
                            {
                                Type = EvidenceType.CosineSimilarity,
                                Description = $"Cosine similarity: {similarity:F3}",
                                Strength = similarity
                            }
                        }
                    });
                }
            }

            // Shared entities
            if (config.EnabledTypes.Contains(RelationshipType.SharedEntities) &&
                doc1.Entities != null && doc2.Entities != null)
            {
                var sharedEntities = FindSharedEntities(doc1.Entities, doc2.Entities);
                if (sharedEntities.Count > 0)
                {
                    var strength = Math.Min(1.0f, sharedEntities.Count / 5.0f);
                    relationships.Add(new DocumentRelationship
                    {
                        SourceDocumentId = doc1.Id,
                        TargetDocumentId = doc2.Id,
                        Type = RelationshipType.SharedEntities,
                        Strength = strength,
                        Confidence = 0.85f,
                        IsBidirectional = true,
                        Evidence = new List<RelationshipEvidence>
                        {
                            new()
                            {
                                Type = EvidenceType.SharedEntities,
                                Description = $"Shared entities: {string.Join(", ", sharedEntities.Take(5))}",
                                Strength = strength,
                                Data = new Dictionary<string, object> { ["entities"] = sharedEntities }
                            }
                        }
                    });
                }
            }

            // Citation detection
            if (config.EnableCitationDetection &&
                config.EnabledTypes.Contains(RelationshipType.References))
            {
                var citationResult = DetectCitation(doc1.Text, doc2);
                if (citationResult.HasCitation)
                {
                    relationships.Add(new DocumentRelationship
                    {
                        SourceDocumentId = doc1.Id,
                        TargetDocumentId = doc2.Id,
                        Type = RelationshipType.References,
                        Strength = citationResult.Confidence,
                        Confidence = citationResult.Confidence,
                        IsBidirectional = false,
                        Evidence = new List<RelationshipEvidence>
                        {
                            new()
                            {
                                Type = EvidenceType.Citation,
                                Description = citationResult.Evidence,
                                Strength = citationResult.Confidence
                            }
                        }
                    });
                }
            }

            // Shared keywords/topics
            if (config.EnabledTypes.Contains(RelationshipType.SharedTopic) &&
                doc1.Keywords != null && doc2.Keywords != null)
            {
                var sharedKeywords = doc1.Keywords.Keys.Intersect(doc2.Keywords.Keys).ToList();
                if (sharedKeywords.Count >= 3)
                {
                    var strength = Math.Min(1.0f, sharedKeywords.Count / 10.0f);
                    relationships.Add(new DocumentRelationship
                    {
                        SourceDocumentId = doc1.Id,
                        TargetDocumentId = doc2.Id,
                        Type = RelationshipType.SharedTopic,
                        Strength = strength,
                        Confidence = 0.8f,
                        IsBidirectional = true,
                        Evidence = new List<RelationshipEvidence>
                        {
                            new()
                            {
                                Type = EvidenceType.SharedKeywords,
                                Description = $"Shared keywords: {string.Join(", ", sharedKeywords.Take(10))}",
                                Strength = strength
                            }
                        }
                    });
                }
            }

            // Temporal relationships
            if (doc1.Metadata.TryGetValue("created", out var created1) &&
                doc2.Metadata.TryGetValue("created", out var created2))
            {
                if (created1 is DateTime dt1 && created2 is DateTime dt2)
                {
                    // Check for version relationship
                    if (Math.Abs((dt1 - dt2).TotalDays) < 30 &&
                        doc1.Embedding != null && doc2.Embedding != null)
                    {
                        var similarity = VectorMath.CosineSimilarity(doc1.Embedding, doc2.Embedding);
                        if (similarity > 0.9f) // Very similar content
                        {
                            relationships.Add(new DocumentRelationship
                            {
                                SourceDocumentId = doc1.Id,
                                TargetDocumentId = doc2.Id,
                                Type = dt1 > dt2 ? RelationshipType.NewerVersion : RelationshipType.OlderVersion,
                                Strength = similarity,
                                Confidence = 0.7f,
                                IsBidirectional = false,
                                Evidence = new List<RelationshipEvidence>
                                {
                                    new()
                                    {
                                        Type = EvidenceType.TemporalProximity,
                                        Description = $"Similar content with temporal proximity ({Math.Abs((dt1 - dt2).TotalDays)} days apart)",
                                        Strength = similarity
                                    }
                                }
                            });
                        }
                    }
                }
            }

            return relationships;
        }

        /// <summary>
        /// Finds shared entities between two entity lists.
        /// </summary>
        private List<string> FindSharedEntities(
            List<ExtractedEntity> entities1,
            List<ExtractedEntity> entities2)
        {
            var normalized1 = entities1
                .Where(e => e.Type != EntityType.Unknown)
                .Select(e => (e.NormalizedText ?? e.Text).ToLowerInvariant())
                .ToHashSet();

            var normalized2 = entities2
                .Where(e => e.Type != EntityType.Unknown)
                .Select(e => (e.NormalizedText ?? e.Text).ToLowerInvariant())
                .ToHashSet();

            return normalized1.Intersect(normalized2).ToList();
        }

        /// <summary>
        /// Detects citations of one document in another.
        /// </summary>
        private (bool HasCitation, float Confidence, string Evidence) DetectCitation(
            string? text,
            DocumentInfo citedDoc)
        {
            if (string.IsNullOrEmpty(text))
                return (false, 0, string.Empty);

            // Check for title mention
            if (!string.IsNullOrEmpty(citedDoc.Title) &&
                text.Contains(citedDoc.Title, StringComparison.OrdinalIgnoreCase))
            {
                return (true, 0.8f, $"Title mentioned: \"{citedDoc.Title}\"");
            }

            // Check for ID/reference number
            if (text.Contains(citedDoc.Id))
            {
                return (true, 0.9f, $"Document ID referenced: {citedDoc.Id}");
            }

            // Check for author + year pattern (academic citations)
            if (citedDoc.Metadata.TryGetValue("author", out var author) &&
                citedDoc.Metadata.TryGetValue("year", out var year))
            {
                var citationPattern = $@"{author}\s*\(?\s*{year}\s*\)?";
                if (Regex.IsMatch(text, citationPattern, RegexOptions.IgnoreCase))
                {
                    return (true, 0.75f, $"Academic citation: {author} ({year})");
                }
            }

            // Check for hyperlink
            if (citedDoc.Metadata.TryGetValue("url", out var url) && url is string urlStr)
            {
                if (text.Contains(urlStr))
                {
                    return (true, 0.95f, $"Hyperlink reference: {urlStr}");
                }
            }

            return (false, 0, string.Empty);
        }

        /// <summary>
        /// Clusters documents by topic using K-means on embeddings.
        /// </summary>
        private async Task<List<TopicCluster>> ClusterDocumentsAsync(
            List<DocumentInfo> documents,
            RelationshipDiscoveryConfig config,
            CancellationToken ct)
        {
            var docsWithEmbeddings = documents.Where(d => d.Embedding != null).ToList();
            if (docsWithEmbeddings.Count < 2)
                return new List<TopicCluster>();

            var dimension = docsWithEmbeddings[0].Embedding!.Length;
            var targetClusters = config.TargetClusterCount > 0
                ? config.TargetClusterCount
                : Math.Max(2, (int)Math.Sqrt(docsWithEmbeddings.Count / 2));

            // Simple K-means clustering
            var clusters = KMeansClustering(docsWithEmbeddings, targetClusters, dimension, ct);

            // Generate topic labels
            var labeledClusters = new List<TopicCluster>();
            foreach (var cluster in clusters)
            {
                ct.ThrowIfCancellationRequested();

                var label = await GenerateClusterLabelAsync(cluster.DocumentIds, documents, ct);

                labeledClusters.Add(new TopicCluster
                {
                    ClusterId = cluster.ClusterId,
                    TopicLabel = label.Label,
                    KeyTerms = label.KeyTerms,
                    DocumentIds = cluster.DocumentIds,
                    CentroidVector = cluster.CentroidVector,
                    Coherence = CalculateCoherence(cluster.DocumentIds, docsWithEmbeddings)
                });
            }

            return labeledClusters;
        }

        /// <summary>
        /// Simple K-means clustering implementation.
        /// </summary>
        private List<(string ClusterId, List<string> DocumentIds, float[] CentroidVector)> KMeansClustering(
            List<DocumentInfo> documents,
            int k,
            int dimension,
            CancellationToken ct)
        {
            if (documents.Count <= k)
            {
                // Each document is its own cluster
                return documents.Select((d, i) => (
                    $"cluster_{i}",
                    new List<string> { d.Id },
                    d.Embedding!
                )).ToList();
            }

            // Initialize centroids with k random documents
            var random = new Random(42);
            var indices = Enumerable.Range(0, documents.Count).OrderBy(_ => random.Next()).Take(k).ToList();
            var centroids = indices.Select(i => (float[])documents[i].Embedding!.Clone()).ToList();

            var assignments = new int[documents.Count];
            var maxIterations = 20;

            for (int iter = 0; iter < maxIterations; iter++)
            {
                ct.ThrowIfCancellationRequested();

                var changed = false;

                // Assign documents to nearest centroid
                for (int i = 0; i < documents.Count; i++)
                {
                    var bestCluster = 0;
                    var bestDistance = float.MaxValue;

                    for (int c = 0; c < k; c++)
                    {
                        var distance = EuclideanDistance(documents[i].Embedding!, centroids[c]);
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            bestCluster = c;
                        }
                    }

                    if (assignments[i] != bestCluster)
                    {
                        assignments[i] = bestCluster;
                        changed = true;
                    }
                }

                if (!changed)
                    break;

                // Update centroids
                for (int c = 0; c < k; c++)
                {
                    var clusterDocs = documents
                        .Where((_, i) => assignments[i] == c)
                        .ToList();

                    if (clusterDocs.Count > 0)
                    {
                        centroids[c] = ComputeCentroid(clusterDocs.Select(d => d.Embedding!).ToList(), dimension);
                    }
                }
            }

            // Build cluster results
            var clusters = new List<(string, List<string>, float[])>();
            for (int c = 0; c < k; c++)
            {
                var docIds = documents
                    .Where((_, i) => assignments[i] == c)
                    .Select(d => d.Id)
                    .ToList();

                if (docIds.Count > 0)
                {
                    clusters.Add(($"cluster_{c}", docIds, centroids[c]));
                }
            }

            return clusters;
        }

        private float EuclideanDistance(float[] a, float[] b)
        {
            var sum = 0.0;
            for (int i = 0; i < a.Length; i++)
            {
                var diff = a[i] - b[i];
                sum += diff * diff;
            }
            return (float)Math.Sqrt(sum);
        }

        private float[] ComputeCentroid(List<float[]> vectors, int dimension)
        {
            var centroid = new float[dimension];
            foreach (var v in vectors)
            {
                for (int i = 0; i < dimension; i++)
                {
                    centroid[i] += v[i];
                }
            }

            for (int i = 0; i < dimension; i++)
            {
                centroid[i] /= vectors.Count;
            }

            return centroid;
        }

        private float CalculateCoherence(List<string> documentIds, List<DocumentInfo> allDocs)
        {
            if (documentIds.Count <= 1)
                return 1.0f;

            var clusterDocs = allDocs.Where(d => documentIds.Contains(d.Id)).ToList();
            if (clusterDocs.Count <= 1)
                return 1.0f;

            var totalSimilarity = 0.0f;
            var count = 0;

            for (int i = 0; i < clusterDocs.Count; i++)
            {
                for (int j = i + 1; j < clusterDocs.Count; j++)
                {
                    if (clusterDocs[i].Embedding != null && clusterDocs[j].Embedding != null)
                    {
                        totalSimilarity += VectorMath.CosineSimilarity(
                            clusterDocs[i].Embedding!,
                            clusterDocs[j].Embedding!);
                        count++;
                    }
                }
            }

            return count > 0 ? totalSimilarity / count : 0;
        }

        private async Task<(string Label, List<string> KeyTerms)> GenerateClusterLabelAsync(
            List<string> documentIds,
            List<DocumentInfo> allDocs,
            CancellationToken ct)
        {
            var clusterDocs = allDocs.Where(d => documentIds.Contains(d.Id)).ToList();

            // Collect all keywords
            var keywordCounts = new Dictionary<string, int>();
            foreach (var doc in clusterDocs)
            {
                if (doc.Keywords != null)
                {
                    foreach (var kw in doc.Keywords.Keys)
                    {
                        keywordCounts.TryGetValue(kw, out var count);
                        keywordCounts[kw] = count + 1;
                    }
                }
            }

            var topKeywords = keywordCounts
                .OrderByDescending(kv => kv.Value)
                .Take(10)
                .Select(kv => kv.Key)
                .ToList();

            // Use AI for label generation if available
            if (_aiProvider != null && _aiProvider.IsAvailable && topKeywords.Count > 0)
            {
                var request = new AIRequest
                {
                    Prompt = $"Generate a short topic label (3-5 words) for documents about: {string.Join(", ", topKeywords)}",
                    SystemMessage = "You are a topic labeling system. Generate concise, descriptive labels.",
                    MaxTokens = 20
                };

                var response = await _aiProvider.CompleteAsync(request, ct);
                if (response.Success)
                {
                    var label = response.Content.Trim().Trim('"');
                    return (label, topKeywords);
                }
            }

            // Fall back to top keywords as label
            var fallbackLabel = string.Join(", ", topKeywords.Take(3));
            return (fallbackLabel, topKeywords);
        }

        private async Task AddToKnowledgeGraphAsync(
            DocumentInfo document,
            List<DocumentRelationship> relationships,
            CancellationToken ct)
        {
            if (_knowledgeGraph == null)
                return;

            // Add document node using SDK's IKnowledgeGraph API
            var docProperties = new Dictionary<string, object>
            {
                ["documentId"] = document.Id,
                ["title"] = document.Title ?? ""
            };
            var docNode = await _knowledgeGraph.AddNodeAsync(
                document.Title ?? document.Id,
                docProperties,
                ct);

            // Add relationships as edges
            foreach (var rel in relationships)
            {
                var edgeProperties = new Dictionary<string, object>
                {
                    ["type"] = rel.Type.ToString(),
                    ["confidence"] = rel.Confidence,
                    ["strength"] = rel.Strength
                };

                await _knowledgeGraph.AddEdgeAsync(
                    docNode.Id,
                    $"doc:{rel.TargetDocumentId}",
                    rel.Type.ToString(),
                    edgeProperties,
                    ct);
            }
        }
    }

    /// <summary>
    /// Document information for relationship discovery.
    /// </summary>
    public sealed class DocumentInfo
    {
        public required string Id { get; init; }
        public string? Title { get; init; }
        public string? Text { get; init; }
        public float[]? Embedding { get; init; }
        public List<ExtractedEntity>? Entities { get; init; }
        public Dictionary<string, float>? Keywords { get; init; }
        public Dictionary<string, object> Metadata { get; init; } = new();
    }
}
