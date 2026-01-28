namespace DataWarehouse.Plugins.SemanticUnderstanding.Models
{
    /// <summary>
    /// Result of entity extraction from content.
    /// </summary>
    public sealed class EntityExtractionResult
    {
        /// <summary>All extracted entities.</summary>
        public List<ExtractedEntity> Entities { get; init; } = new();

        /// <summary>Total count of entities found.</summary>
        public int TotalCount => Entities.Count;

        /// <summary>Count by entity type.</summary>
        public Dictionary<EntityType, int> CountByType =>
            Entities.GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.Count());

        /// <summary>Processing duration.</summary>
        public TimeSpan Duration { get; init; }

        /// <summary>Whether AI was used for extraction.</summary>
        public bool UsedAI { get; init; }

        /// <summary>Document language if detected.</summary>
        public string? DetectedLanguage { get; init; }
    }

    /// <summary>
    /// An extracted entity from text.
    /// </summary>
    public sealed class ExtractedEntity
    {
        /// <summary>The entity text as found in the document.</summary>
        public required string Text { get; init; }

        /// <summary>Normalized/canonical form of the entity.</summary>
        public string? NormalizedText { get; init; }

        /// <summary>Entity type classification.</summary>
        public EntityType Type { get; init; }

        /// <summary>Sub-type for more specific classification.</summary>
        public string? SubType { get; init; }

        /// <summary>Confidence score (0-1).</summary>
        public float Confidence { get; init; }

        /// <summary>Character position in source text.</summary>
        public int StartPosition { get; init; }

        /// <summary>Length of entity text in source.</summary>
        public int Length { get; init; }

        /// <summary>Surrounding context text.</summary>
        public string? Context { get; init; }

        /// <summary>External knowledge graph ID if linked.</summary>
        public string? KnowledgeGraphId { get; init; }

        /// <summary>External knowledge graph URL.</summary>
        public string? KnowledgeGraphUrl { get; init; }

        /// <summary>Additional metadata about the entity.</summary>
        public Dictionary<string, object> Metadata { get; init; } = new();

        /// <summary>Entity embeddings for similarity matching.</summary>
        public float[]? Embedding { get; init; }
    }

    /// <summary>
    /// Entity types for Named Entity Recognition.
    /// </summary>
    public enum EntityType
    {
        /// <summary>Unknown entity type.</summary>
        Unknown = 0,

        // People and organizations
        /// <summary>Person name.</summary>
        Person,
        /// <summary>Organization or company name.</summary>
        Organization,
        /// <summary>Product or brand name.</summary>
        Product,

        // Locations
        /// <summary>Geographic location.</summary>
        Location,
        /// <summary>Country name.</summary>
        Country,
        /// <summary>City name.</summary>
        City,
        /// <summary>Address.</summary>
        Address,

        // Time
        /// <summary>Date reference.</summary>
        Date,
        /// <summary>Time reference.</summary>
        Time,
        /// <summary>Duration or time span.</summary>
        Duration,

        // Numeric
        /// <summary>Monetary value.</summary>
        Money,
        /// <summary>Percentage value.</summary>
        Percentage,
        /// <summary>Quantity or number.</summary>
        Quantity,
        /// <summary>Phone number.</summary>
        PhoneNumber,

        // Digital
        /// <summary>Email address.</summary>
        Email,
        /// <summary>URL or web address.</summary>
        Url,
        /// <summary>IP address.</summary>
        IpAddress,
        /// <summary>File path.</summary>
        FilePath,

        // Documents and identifiers
        /// <summary>Document reference number.</summary>
        DocumentId,
        /// <summary>Invoice or order number.</summary>
        InvoiceNumber,
        /// <summary>Account number.</summary>
        AccountNumber,

        // Technical
        /// <summary>Code identifier (variable, function, class).</summary>
        CodeIdentifier,
        /// <summary>Technical term or acronym.</summary>
        TechnicalTerm,
        /// <summary>Version number.</summary>
        Version,

        // Events and concepts
        /// <summary>Event name.</summary>
        Event,
        /// <summary>Abstract concept or topic.</summary>
        Concept,
        /// <summary>Skill or competency.</summary>
        Skill,

        // Legal
        /// <summary>Legal citation or reference.</summary>
        LegalCitation,
        /// <summary>Regulation or law name.</summary>
        Regulation,

        // Custom
        /// <summary>Custom entity type defined by user.</summary>
        Custom
    }

    /// <summary>
    /// Custom entity type definition.
    /// </summary>
    public sealed class CustomEntityType
    {
        /// <summary>Unique identifier for this entity type.</summary>
        public required string Id { get; init; }

        /// <summary>Human-readable name.</summary>
        public required string Name { get; init; }

        /// <summary>Description of what this entity type represents.</summary>
        public string? Description { get; init; }

        /// <summary>Regex patterns for matching this entity type.</summary>
        public List<string> Patterns { get; init; } = new();

        /// <summary>Keywords that indicate this entity type.</summary>
        public List<string> Keywords { get; init; } = new();

        /// <summary>Example entities for training.</summary>
        public List<string> Examples { get; init; } = new();

        /// <summary>Parent entity type for inheritance.</summary>
        public EntityType? ParentType { get; init; }

        /// <summary>Whether to apply normalization rules.</summary>
        public bool EnableNormalization { get; init; } = true;
    }

    /// <summary>
    /// Entity linking configuration.
    /// </summary>
    public sealed class EntityLinkingConfig
    {
        /// <summary>Whether to link entities to knowledge graphs.</summary>
        public bool EnableLinking { get; init; } = true;

        /// <summary>Knowledge graph sources to use.</summary>
        public List<KnowledgeGraphSource> Sources { get; init; } = new();

        /// <summary>Minimum confidence for linking.</summary>
        public float MinLinkingConfidence { get; init; } = 0.7f;

        /// <summary>Maximum linking candidates to consider.</summary>
        public int MaxCandidates { get; init; } = 5;
    }

    /// <summary>
    /// Knowledge graph source for entity linking.
    /// </summary>
    public enum KnowledgeGraphSource
    {
        /// <summary>Internal knowledge graph.</summary>
        Internal,
        /// <summary>Wikidata knowledge base.</summary>
        Wikidata,
        /// <summary>DBpedia knowledge base.</summary>
        DBpedia,
        /// <summary>Google Knowledge Graph.</summary>
        GoogleKG,
        /// <summary>Custom knowledge base.</summary>
        Custom
    }

    /// <summary>
    /// Entity co-occurrence information.
    /// </summary>
    public sealed class EntityCoOccurrence
    {
        /// <summary>First entity.</summary>
        public required ExtractedEntity Entity1 { get; init; }

        /// <summary>Second entity.</summary>
        public required ExtractedEntity Entity2 { get; init; }

        /// <summary>Number of times these entities appear together.</summary>
        public int Count { get; init; }

        /// <summary>Average proximity in text (characters).</summary>
        public float AverageProximity { get; init; }

        /// <summary>Relationship strength score.</summary>
        public float RelationshipStrength { get; init; }

        /// <summary>Inferred relationship type if available.</summary>
        public string? InferredRelationship { get; init; }
    }
}
