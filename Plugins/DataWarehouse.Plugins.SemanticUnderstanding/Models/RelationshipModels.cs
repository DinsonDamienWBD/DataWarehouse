namespace DataWarehouse.Plugins.SemanticUnderstanding.Models
{
    /// <summary>
    /// Result of relationship discovery between documents.
    /// </summary>
    public sealed class RelationshipDiscoveryResult
    {
        /// <summary>Discovered relationships.</summary>
        public List<DocumentRelationship> Relationships { get; init; } = new();

        /// <summary>Total relationships found.</summary>
        public int TotalCount => Relationships.Count;

        /// <summary>Topic clusters identified.</summary>
        public List<TopicCluster> Clusters { get; init; } = new();

        /// <summary>Processing duration.</summary>
        public TimeSpan Duration { get; init; }

        /// <summary>Documents analyzed.</summary>
        public int DocumentsAnalyzed { get; init; }
    }

    /// <summary>
    /// Relationship between two documents.
    /// </summary>
    public sealed class DocumentRelationship
    {
        /// <summary>Source document ID.</summary>
        public required string SourceDocumentId { get; init; }

        /// <summary>Target document ID.</summary>
        public required string TargetDocumentId { get; init; }

        /// <summary>Type of relationship.</summary>
        public RelationshipType Type { get; init; }

        /// <summary>Relationship strength (0-1).</summary>
        public float Strength { get; init; }

        /// <summary>Confidence in this relationship (0-1).</summary>
        public float Confidence { get; init; }

        /// <summary>Evidence supporting this relationship.</summary>
        public List<RelationshipEvidence> Evidence { get; init; } = new();

        /// <summary>Whether this relationship is bidirectional.</summary>
        public bool IsBidirectional { get; init; }

        /// <summary>Additional metadata.</summary>
        public Dictionary<string, object> Metadata { get; init; } = new();
    }

    /// <summary>
    /// Types of document relationships.
    /// </summary>
    public enum RelationshipType
    {
        /// <summary>Unknown relationship.</summary>
        Unknown = 0,

        // Content-based
        /// <summary>Documents are semantically similar.</summary>
        SimilarContent,
        /// <summary>Documents share a topic or theme.</summary>
        SharedTopic,
        /// <summary>One document is a duplicate of another.</summary>
        Duplicate,
        /// <summary>One document is near-duplicate.</summary>
        NearDuplicate,

        // Reference-based
        /// <summary>One document references/cites another.</summary>
        References,
        /// <summary>One document is referenced by another.</summary>
        ReferencedBy,
        /// <summary>Documents are related via hyperlinks.</summary>
        Hyperlink,

        // Structural
        /// <summary>Documents share common entities.</summary>
        SharedEntities,
        /// <summary>Documents share author/creator.</summary>
        SharedAuthor,
        /// <summary>Documents are in same category.</summary>
        SameCategory,

        // Temporal
        /// <summary>One document is a newer version.</summary>
        NewerVersion,
        /// <summary>One document is an older version.</summary>
        OlderVersion,
        /// <summary>Documents are part of a series.</summary>
        Series,

        // Organizational
        /// <summary>Documents are part of same project.</summary>
        SameProject,
        /// <summary>One document responds to another.</summary>
        ResponseTo,
        /// <summary>One document is derived from another.</summary>
        DerivedFrom,

        // Custom
        /// <summary>User-defined relationship type.</summary>
        Custom
    }

    /// <summary>
    /// Evidence supporting a relationship.
    /// </summary>
    public sealed class RelationshipEvidence
    {
        /// <summary>Type of evidence.</summary>
        public EvidenceType Type { get; init; }

        /// <summary>Description of the evidence.</summary>
        public required string Description { get; init; }

        /// <summary>Strength of this evidence (0-1).</summary>
        public float Strength { get; init; }

        /// <summary>Specific data supporting the evidence.</summary>
        public Dictionary<string, object> Data { get; init; } = new();
    }

    /// <summary>
    /// Types of relationship evidence.
    /// </summary>
    public enum EvidenceType
    {
        /// <summary>Cosine similarity score.</summary>
        CosineSimilarity,
        /// <summary>Shared keywords.</summary>
        SharedKeywords,
        /// <summary>Shared entities.</summary>
        SharedEntities,
        /// <summary>Text overlap.</summary>
        TextOverlap,
        /// <summary>Explicit citation.</summary>
        Citation,
        /// <summary>Hyperlink reference.</summary>
        Hyperlink,
        /// <summary>Metadata match.</summary>
        MetadataMatch,
        /// <summary>Temporal proximity.</summary>
        TemporalProximity,
        /// <summary>AI inference.</summary>
        AIInference
    }

    /// <summary>
    /// A cluster of documents sharing a topic.
    /// </summary>
    public sealed class TopicCluster
    {
        /// <summary>Unique cluster identifier.</summary>
        public required string ClusterId { get; init; }

        /// <summary>Topic label/description.</summary>
        public required string TopicLabel { get; init; }

        /// <summary>Key terms defining this cluster.</summary>
        public List<string> KeyTerms { get; init; } = new();

        /// <summary>Document IDs in this cluster.</summary>
        public List<string> DocumentIds { get; init; } = new();

        /// <summary>Centroid vector for this cluster.</summary>
        public float[]? CentroidVector { get; init; }

        /// <summary>Average coherence score within cluster.</summary>
        public float Coherence { get; init; }

        /// <summary>Size of the cluster.</summary>
        public int Size => DocumentIds.Count;

        /// <summary>Parent cluster ID for hierarchical clustering.</summary>
        public string? ParentClusterId { get; init; }

        /// <summary>Child cluster IDs.</summary>
        public List<string> ChildClusterIds { get; init; } = new();
    }

    /// <summary>
    /// Configuration for relationship discovery.
    /// </summary>
    public sealed class RelationshipDiscoveryConfig
    {
        /// <summary>Minimum similarity threshold for relationships.</summary>
        public float MinSimilarityThreshold { get; init; } = 0.6f;

        /// <summary>Maximum relationships per document.</summary>
        public int MaxRelationshipsPerDocument { get; init; } = 20;

        /// <summary>Enable topic clustering.</summary>
        public bool EnableClustering { get; init; } = true;

        /// <summary>Target number of clusters (0 = auto-detect).</summary>
        public int TargetClusterCount { get; init; } = 0;

        /// <summary>Enable citation/reference detection.</summary>
        public bool EnableCitationDetection { get; init; } = true;

        /// <summary>Relationship types to discover.</summary>
        public List<RelationshipType> EnabledTypes { get; init; } = new()
        {
            RelationshipType.SimilarContent,
            RelationshipType.SharedTopic,
            RelationshipType.SharedEntities,
            RelationshipType.References
        };
    }
}
