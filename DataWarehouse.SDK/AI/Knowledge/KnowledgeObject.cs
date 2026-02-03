using System;
using System.Collections.Generic;

namespace DataWarehouse.SDK.AI.Knowledge
{
    // ==================================================================================
    // T99.C1: CORE KNOWLEDGE OBJECT TYPES
    // ==================================================================================

    /// <summary>
    /// Universal envelope for all AI interactions in the DataWarehouse ecosystem.
    /// Provides routing, versioning, provenance, and typed payload support for
    /// inter-plugin knowledge exchange.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <see cref="KnowledgeObject"/> serves as the fundamental communication unit
    /// between AI plugins, enabling semantic routing, temporal queries, and provenance
    /// tracking. Each knowledge object is uniquely identified, versioned, and enriched
    /// with metadata for discoverability and auditability.
    /// </para>
    /// <para>
    /// Knowledge objects support multiple interaction patterns:
    /// - Query/Response for information retrieval
    /// - Command/Event for action execution and notification
    /// - State snapshots for plugin state sharing
    /// - Capability registration for plugin discovery
    /// </para>
    /// </remarks>
    public sealed record KnowledgeObject
    {
        /// <summary>
        /// Unique identifier for this knowledge object.
        /// </summary>
        public required Guid ObjectId { get; init; }

        /// <summary>
        /// Type of this knowledge object (Query, Command, Event, etc.).
        /// </summary>
        public required KnowledgeObjectType ObjectType { get; init; }

        /// <summary>
        /// Version of this knowledge object for temporal tracking.
        /// </summary>
        public required int Version { get; init; }

        /// <summary>
        /// Timestamp when this knowledge object was created.
        /// </summary>
        public required DateTime CreatedAt { get; init; }

        /// <summary>
        /// Timestamp when this knowledge object was last updated.
        /// </summary>
        public DateTime UpdatedAt { get; init; }

        /// <summary>
        /// Plugin ID that originated this knowledge object (null for kernel-originated).
        /// </summary>
        public string? SourcePluginId { get; init; }

        /// <summary>
        /// Target plugin ID for routing (null for broadcast or general availability).
        /// </summary>
        public string? TargetPluginId { get; init; }

        /// <summary>
        /// Typed payload containing the actual knowledge data.
        /// </summary>
        public required KnowledgePayload Payload { get; init; }

        /// <summary>
        /// Extensible metadata dictionary for custom attributes and context.
        /// </summary>
        public Dictionary<string, object> Metadata { get; init; } = new();

        /// <summary>
        /// Provenance information tracking the origin and derivation of this knowledge.
        /// </summary>
        public KnowledgeProvenance? Provenance { get; init; }

        /// <summary>
        /// Temporal context for time-travel queries (null for current/latest).
        /// </summary>
        public TemporalContext? TemporalContext { get; init; }

        /// <summary>
        /// Creates a new knowledge object with automatic initialization.
        /// </summary>
        /// <param name="objectType">Type of knowledge object.</param>
        /// <param name="payload">Payload containing knowledge data.</param>
        /// <param name="sourcePluginId">Optional source plugin identifier.</param>
        /// <param name="targetPluginId">Optional target plugin identifier.</param>
        /// <returns>A new knowledge object instance.</returns>
        public static KnowledgeObject Create(
            KnowledgeObjectType objectType,
            KnowledgePayload payload,
            string? sourcePluginId = null,
            string? targetPluginId = null)
        {
            var now = DateTime.UtcNow;
            return new KnowledgeObject
            {
                ObjectId = Guid.NewGuid(),
                ObjectType = objectType,
                Version = 1,
                CreatedAt = now,
                UpdatedAt = now,
                SourcePluginId = sourcePluginId,
                TargetPluginId = targetPluginId,
                Payload = payload
            };
        }

        /// <summary>
        /// Creates a new version of this knowledge object with updated payload.
        /// Preserves ObjectId and increments version.
        /// </summary>
        /// <param name="newPayload">Updated payload.</param>
        /// <returns>A new knowledge object representing the next version.</returns>
        public KnowledgeObject WithNewVersion(KnowledgePayload newPayload)
        {
            return this with
            {
                Version = Version + 1,
                UpdatedAt = DateTime.UtcNow,
                Payload = newPayload
            };
        }
    }

    /// <summary>
    /// Specifies the type of knowledge object for semantic routing and processing.
    /// </summary>
    public enum KnowledgeObjectType
    {
        /// <summary>
        /// Plugin capability registration or announcement.
        /// </summary>
        Registration,

        /// <summary>
        /// Query or information request.
        /// </summary>
        Query,

        /// <summary>
        /// Command or action request.
        /// </summary>
        Command,

        /// <summary>
        /// Event notification or state change broadcast.
        /// </summary>
        Event,

        /// <summary>
        /// Response to a query or command.
        /// </summary>
        Response,

        /// <summary>
        /// Notification or alert message.
        /// </summary>
        Notification,

        /// <summary>
        /// Plugin state snapshot for sharing current state.
        /// </summary>
        State,

        /// <summary>
        /// Plugin capability descriptor for discovery.
        /// </summary>
        Capability
    }

    /// <summary>
    /// Request payload for knowledge queries and commands.
    /// </summary>
    /// <remarks>
    /// Encapsulates query parameters, execution context, and timeout constraints
    /// for knowledge requests between AI plugins.
    /// </remarks>
    public sealed record KnowledgeRequest
    {
        /// <summary>
        /// The query or command identifier (e.g., "semantic.search", "data.transform").
        /// </summary>
        public required string Query { get; init; }

        /// <summary>
        /// Parameters for the query or command.
        /// </summary>
        public Dictionary<string, object> Parameters { get; init; } = new();

        /// <summary>
        /// Execution context (user ID, security context, etc.).
        /// </summary>
        public Dictionary<string, object> Context { get; init; } = new();

        /// <summary>
        /// Maximum execution time for this request (null for no timeout).
        /// </summary>
        public TimeSpan? Timeout { get; init; }

        /// <summary>
        /// Temporal context for time-travel queries.
        /// </summary>
        public TemporalContext? TemporalContext { get; init; }

        /// <summary>
        /// Expected response content type (e.g., "application/json", "text/plain").
        /// </summary>
        public string? ExpectedContentType { get; init; }

        /// <summary>
        /// Creates a simple knowledge request with a query identifier.
        /// </summary>
        public static KnowledgeRequest Simple(string query, Dictionary<string, object>? parameters = null)
        {
            return new KnowledgeRequest
            {
                Query = query,
                Parameters = parameters ?? new Dictionary<string, object>()
            };
        }
    }

    /// <summary>
    /// Response payload for knowledge queries and commands.
    /// </summary>
    /// <remarks>
    /// Provides success/error status, response data, error details, and execution metrics.
    /// </remarks>
    public sealed record KnowledgeResponse
    {
        /// <summary>
        /// Whether the request succeeded.
        /// </summary>
        public required bool Success { get; init; }

        /// <summary>
        /// Response data (null on error).
        /// </summary>
        public object? Data { get; init; }

        /// <summary>
        /// Error message if not successful.
        /// </summary>
        public string? Error { get; init; }

        /// <summary>
        /// Error code for programmatic error handling.
        /// </summary>
        public string? ErrorCode { get; init; }

        /// <summary>
        /// Execution duration.
        /// </summary>
        public TimeSpan Duration { get; init; }

        /// <summary>
        /// Additional response metadata (e.g., total count, page info).
        /// </summary>
        public Dictionary<string, object> Metadata { get; init; } = new();

        /// <summary>
        /// Creates a successful response with data.
        /// </summary>
        public static KnowledgeResponse Ok(object? data = null, TimeSpan? duration = null)
        {
            return new KnowledgeResponse
            {
                Success = true,
                Data = data,
                Duration = duration ?? TimeSpan.Zero
            };
        }

        /// <summary>
        /// Creates an error response.
        /// </summary>
        public static KnowledgeResponse Fail(string error, string? errorCode = null, TimeSpan? duration = null)
        {
            return new KnowledgeResponse
            {
                Success = false,
                Error = error,
                ErrorCode = errorCode,
                Duration = duration ?? TimeSpan.Zero
            };
        }
    }

    /// <summary>
    /// Typed payload container for knowledge data with content type metadata.
    /// </summary>
    /// <remarks>
    /// Supports arbitrary payload types with MIME-type annotation for proper
    /// serialization and deserialization by receiving plugins.
    /// </remarks>
    public sealed record KnowledgePayload
    {
        /// <summary>
        /// Content type of the payload (e.g., "application/json", "text/plain", "application/octet-stream").
        /// </summary>
        public required string ContentType { get; init; }

        /// <summary>
        /// The actual payload data.
        /// </summary>
        public required object Data { get; init; }

        /// <summary>
        /// Size of the payload in bytes (for metrics and filtering).
        /// </summary>
        public long? SizeBytes { get; init; }

        /// <summary>
        /// Encoding information for text payloads (e.g., "utf-8", "utf-16").
        /// </summary>
        public string? Encoding { get; init; }

        /// <summary>
        /// Creates a JSON payload.
        /// </summary>
        public static KnowledgePayload Json(object data, long? sizeBytes = null)
        {
            return new KnowledgePayload
            {
                ContentType = "application/json",
                Data = data,
                SizeBytes = sizeBytes,
                Encoding = "utf-8"
            };
        }

        /// <summary>
        /// Creates a plain text payload.
        /// </summary>
        public static KnowledgePayload Text(string text, long? sizeBytes = null)
        {
            return new KnowledgePayload
            {
                ContentType = "text/plain",
                Data = text,
                SizeBytes = sizeBytes ?? text.Length,
                Encoding = "utf-8"
            };
        }

        /// <summary>
        /// Creates a binary payload.
        /// </summary>
        public static KnowledgePayload Binary(byte[] data)
        {
            return new KnowledgePayload
            {
                ContentType = "application/octet-stream",
                Data = data,
                SizeBytes = data.Length
            };
        }
    }

    /// <summary>
    /// Plugin capability descriptor for knowledge-based discovery.
    /// </summary>
    /// <remarks>
    /// Describes what a plugin can do, enabling AI agents to discover and invoke
    /// capabilities dynamically based on semantic intent.
    /// </remarks>
    public sealed record KnowledgeCapability
    {
        /// <summary>
        /// Unique capability identifier (e.g., "semantic.search", "data.compress").
        /// </summary>
        public required string CapabilityId { get; init; }

        /// <summary>
        /// Human-readable description of what this capability does.
        /// </summary>
        public required string Description { get; init; }

        /// <summary>
        /// Category for semantic grouping.
        /// </summary>
        public string? Category { get; init; }

        /// <summary>
        /// Parameter schema (JSON Schema or custom format).
        /// </summary>
        public Dictionary<string, KnowledgeParameterDescriptor> Parameters { get; init; } = new();

        /// <summary>
        /// Expected return type description.
        /// </summary>
        public string? ReturnType { get; init; }

        /// <summary>
        /// Examples of how to invoke this capability.
        /// </summary>
        public List<KnowledgeCapabilityExample> Examples { get; init; } = new();

        /// <summary>
        /// Whether this capability requires explicit user approval.
        /// </summary>
        public bool RequiresApproval { get; init; }

        /// <summary>
        /// Estimated execution time range.
        /// </summary>
        public TimeSpan? TypicalDuration { get; init; }

        /// <summary>
        /// Tags for semantic search and filtering.
        /// </summary>
        public List<string> Tags { get; init; } = new();
    }

    /// <summary>
    /// Describes a parameter for a knowledge capability.
    /// </summary>
    public sealed record KnowledgeParameterDescriptor
    {
        /// <summary>
        /// Parameter name.
        /// </summary>
        public required string Name { get; init; }

        /// <summary>
        /// Parameter type (e.g., "string", "integer", "object").
        /// </summary>
        public required string Type { get; init; }

        /// <summary>
        /// Parameter description.
        /// </summary>
        public string? Description { get; init; }

        /// <summary>
        /// Whether this parameter is required.
        /// </summary>
        public bool Required { get; init; }

        /// <summary>
        /// Default value if not provided.
        /// </summary>
        public object? DefaultValue { get; init; }

        /// <summary>
        /// Valid values or range constraints.
        /// </summary>
        public object? Constraints { get; init; }
    }

    /// <summary>
    /// Example invocation of a knowledge capability.
    /// </summary>
    public sealed record KnowledgeCapabilityExample
    {
        /// <summary>
        /// Natural language description of this example.
        /// </summary>
        public required string Description { get; init; }

        /// <summary>
        /// Example parameters.
        /// </summary>
        public Dictionary<string, object> Parameters { get; init; } = new();

        /// <summary>
        /// Expected result or response.
        /// </summary>
        public object? ExpectedResult { get; init; }
    }

    /// <summary>
    /// Plugin state snapshot for knowledge sharing.
    /// </summary>
    /// <remarks>
    /// Captures the current state of a plugin at a specific point in time,
    /// enabling state synchronization, debugging, and AI-driven state analysis.
    /// </remarks>
    public sealed record KnowledgeState
    {
        /// <summary>
        /// Unique state identifier.
        /// </summary>
        public required Guid StateId { get; init; }

        /// <summary>
        /// Plugin identifier this state belongs to.
        /// </summary>
        public required string PluginId { get; init; }

        /// <summary>
        /// Timestamp when this state snapshot was captured.
        /// </summary>
        public required DateTime Timestamp { get; init; }

        /// <summary>
        /// State data (plugin-specific structure).
        /// </summary>
        public required Dictionary<string, object> Data { get; init; }

        /// <summary>
        /// State version for change tracking.
        /// </summary>
        public int Version { get; init; }

        /// <summary>
        /// Previous state ID for linking state history.
        /// </summary>
        public Guid? PreviousStateId { get; init; }

        /// <summary>
        /// Tags for categorizing this state snapshot.
        /// </summary>
        public List<string> Tags { get; init; } = new();

        /// <summary>
        /// Creates a new state snapshot.
        /// </summary>
        public static KnowledgeState Capture(string pluginId, Dictionary<string, object> data, int version = 1)
        {
            return new KnowledgeState
            {
                StateId = Guid.NewGuid(),
                PluginId = pluginId,
                Timestamp = DateTime.UtcNow,
                Data = data,
                Version = version
            };
        }
    }

    // ==================================================================================
    // T99.C2: TEMPORAL KNOWLEDGE
    // ==================================================================================

    /// <summary>
    /// Context for time-travel queries over knowledge objects.
    /// </summary>
    /// <remarks>
    /// Enables querying knowledge as it existed at specific points in time,
    /// supporting temporal analysis, debugging, and compliance scenarios.
    /// </remarks>
    public sealed record TemporalContext
    {
        /// <summary>
        /// Query type for temporal selection.
        /// </summary>
        public required TemporalQueryType QueryType { get; init; }

        /// <summary>
        /// Point-in-time timestamp for AsOf queries.
        /// </summary>
        public DateTime? AsOfTime { get; init; }

        /// <summary>
        /// Start of time range for Between and Timeline queries.
        /// </summary>
        public DateTime? FromTime { get; init; }

        /// <summary>
        /// End of time range for Between and Timeline queries.
        /// </summary>
        public DateTime? ToTime { get; init; }

        /// <summary>
        /// Maximum number of versions to retrieve (for Timeline queries).
        /// </summary>
        public int? MaxVersions { get; init; }

        /// <summary>
        /// Whether to include deleted or invalidated knowledge objects.
        /// </summary>
        public bool IncludeDeleted { get; init; }

        /// <summary>
        /// Creates an "as of" temporal context for point-in-time queries.
        /// </summary>
        public static TemporalContext AsOf(DateTime timestamp)
        {
            return new TemporalContext
            {
                QueryType = TemporalQueryType.AsOf,
                AsOfTime = timestamp
            };
        }

        /// <summary>
        /// Creates a time range temporal context.
        /// </summary>
        public static TemporalContext Between(DateTime from, DateTime to)
        {
            return new TemporalContext
            {
                QueryType = TemporalQueryType.Between,
                FromTime = from,
                ToTime = to
            };
        }

        /// <summary>
        /// Creates a timeline temporal context for retrieving version history.
        /// </summary>
        public static TemporalContext Timeline(int? maxVersions = null)
        {
            return new TemporalContext
            {
                QueryType = TemporalQueryType.Timeline,
                MaxVersions = maxVersions
            };
        }

        /// <summary>
        /// Creates a "latest" temporal context (default, current state).
        /// </summary>
        public static TemporalContext Latest()
        {
            return new TemporalContext
            {
                QueryType = TemporalQueryType.Latest
            };
        }
    }

    /// <summary>
    /// Type of temporal query to execute.
    /// </summary>
    public enum TemporalQueryType
    {
        /// <summary>
        /// Retrieve knowledge as it existed at a specific point in time.
        /// </summary>
        AsOf,

        /// <summary>
        /// Retrieve all knowledge changes within a time range.
        /// </summary>
        Between,

        /// <summary>
        /// Retrieve complete version timeline/history.
        /// </summary>
        Timeline,

        /// <summary>
        /// Retrieve the latest/current state (default).
        /// </summary>
        Latest
    }

    /// <summary>
    /// Timeline of knowledge object versions for temporal analysis.
    /// </summary>
    /// <remarks>
    /// Maintains a chronological sequence of knowledge object versions,
    /// enabling time-travel queries and historical analysis.
    /// </remarks>
    public sealed class KnowledgeTimeline
    {
        /// <summary>
        /// The base knowledge object identifier.
        /// </summary>
        public required Guid ObjectId { get; init; }

        /// <summary>
        /// All versions of this knowledge object in chronological order.
        /// </summary>
        public List<KnowledgeTimelineEntry> Entries { get; init; } = new();

        /// <summary>
        /// When this timeline was created.
        /// </summary>
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// When this timeline was last updated.
        /// </summary>
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Adds a new version to the timeline.
        /// </summary>
        public void AddVersion(KnowledgeObject knowledgeObject)
        {
            Entries.Add(new KnowledgeTimelineEntry
            {
                Version = knowledgeObject.Version,
                KnowledgeObject = knowledgeObject,
                Timestamp = knowledgeObject.UpdatedAt
            });
            UpdatedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// Retrieves the knowledge object as it existed at a specific time.
        /// </summary>
        public KnowledgeObject? GetAsOf(DateTime timestamp)
        {
            return Entries
                .Where(e => e.Timestamp <= timestamp)
                .OrderByDescending(e => e.Timestamp)
                .FirstOrDefault()?.KnowledgeObject;
        }

        /// <summary>
        /// Retrieves all versions within a time range.
        /// </summary>
        public List<KnowledgeObject> GetBetween(DateTime from, DateTime to)
        {
            return Entries
                .Where(e => e.Timestamp >= from && e.Timestamp <= to)
                .OrderBy(e => e.Timestamp)
                .Select(e => e.KnowledgeObject)
                .ToList();
        }

        /// <summary>
        /// Gets the latest version.
        /// </summary>
        public KnowledgeObject? GetLatest()
        {
            return Entries
                .OrderByDescending(e => e.Version)
                .FirstOrDefault()?.KnowledgeObject;
        }
    }

    /// <summary>
    /// Single entry in a knowledge timeline.
    /// </summary>
    public sealed record KnowledgeTimelineEntry
    {
        /// <summary>
        /// Version number of this entry.
        /// </summary>
        public required int Version { get; init; }

        /// <summary>
        /// The knowledge object at this version.
        /// </summary>
        public required KnowledgeObject KnowledgeObject { get; init; }

        /// <summary>
        /// Timestamp of this version.
        /// </summary>
        public required DateTime Timestamp { get; init; }

        /// <summary>
        /// Optional description of changes in this version.
        /// </summary>
        public string? ChangeDescription { get; init; }

        /// <summary>
        /// Who/what created this version (user ID, plugin ID, etc.).
        /// </summary>
        public string? CreatedBy { get; init; }
    }

    // ==================================================================================
    // T99.C3: KNOWLEDGE PROVENANCE
    // ==================================================================================

    /// <summary>
    /// Provenance information tracking the origin and derivation of knowledge.
    /// </summary>
    /// <remarks>
    /// Provides full lineage tracking for knowledge objects, including source attribution,
    /// derivation chains, and trust scoring for AI-driven decision making.
    /// </remarks>
    public sealed record KnowledgeProvenance
    {
        /// <summary>
        /// Original source of this knowledge (plugin ID, external system, user, etc.).
        /// </summary>
        public required string Source { get; init; }

        /// <summary>
        /// Source type (e.g., "plugin", "user", "external-api", "inference").
        /// </summary>
        public string? SourceType { get; init; }

        /// <summary>
        /// Timestamp when this knowledge was originally created.
        /// </summary>
        public DateTime SourceTimestamp { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Derivation chain showing how this knowledge was derived from other knowledge.
        /// </summary>
        public ProvenanceChain? DerivationChain { get; init; }

        /// <summary>
        /// Trust score for this knowledge (0.0 to 1.0).
        /// </summary>
        public TrustScore? Trust { get; init; }

        /// <summary>
        /// Verification status (e.g., "verified", "unverified", "disputed").
        /// </summary>
        public string? VerificationStatus { get; init; }

        /// <summary>
        /// Who/what verified this knowledge.
        /// </summary>
        public string? VerifiedBy { get; init; }

        /// <summary>
        /// When this knowledge was verified.
        /// </summary>
        public DateTime? VerifiedAt { get; init; }

        /// <summary>
        /// Additional provenance metadata.
        /// </summary>
        public Dictionary<string, object> Metadata { get; init; } = new();

        /// <summary>
        /// Creates provenance for knowledge from a specific plugin.
        /// </summary>
        public static KnowledgeProvenance FromPlugin(string pluginId, TrustScore? trust = null)
        {
            return new KnowledgeProvenance
            {
                Source = pluginId,
                SourceType = "plugin",
                Trust = trust
            };
        }

        /// <summary>
        /// Creates provenance for inferred knowledge.
        /// </summary>
        public static KnowledgeProvenance FromInference(string inferenceEngineId, ProvenanceChain derivationChain, TrustScore? trust = null)
        {
            return new KnowledgeProvenance
            {
                Source = inferenceEngineId,
                SourceType = "inference",
                DerivationChain = derivationChain,
                Trust = trust
            };
        }
    }

    /// <summary>
    /// Tracks the derivation chain of knowledge showing how it was derived from other knowledge.
    /// </summary>
    /// <remarks>
    /// Enables full lineage tracking and impact analysis for knowledge objects.
    /// </remarks>
    public sealed class ProvenanceChain
    {
        /// <summary>
        /// Source knowledge objects that contributed to the derived knowledge.
        /// </summary>
        public List<Guid> SourceObjectIds { get; init; } = new();

        /// <summary>
        /// Derivation method or algorithm used (e.g., "inference-rule-42", "ml-model-v2").
        /// </summary>
        public string? DerivationMethod { get; init; }

        /// <summary>
        /// Parameters used in the derivation process.
        /// </summary>
        public Dictionary<string, object> Parameters { get; init; } = new();

        /// <summary>
        /// Confidence score for the derivation (0.0 to 1.0).
        /// </summary>
        public double? Confidence { get; init; }

        /// <summary>
        /// When the derivation occurred.
        /// </summary>
        public DateTime DerivedAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Parent provenance chains (for multi-level derivations).
        /// </summary>
        public List<ProvenanceChain> ParentChains { get; init; } = new();

        /// <summary>
        /// Gets the full depth of the provenance chain.
        /// </summary>
        public int GetDepth()
        {
            if (ParentChains.Count == 0)
                return 1;
            return 1 + ParentChains.Max(p => p.GetDepth());
        }

        /// <summary>
        /// Gets all source object IDs recursively.
        /// </summary>
        public HashSet<Guid> GetAllSourceIds()
        {
            var allIds = new HashSet<Guid>(SourceObjectIds);
            foreach (var parent in ParentChains)
            {
                foreach (var id in parent.GetAllSourceIds())
                {
                    allIds.Add(id);
                }
            }
            return allIds;
        }
    }

    /// <summary>
    /// Trust score calculation for knowledge objects.
    /// </summary>
    /// <remarks>
    /// Provides a multi-factor trust assessment for AI-driven decision making
    /// and knowledge filtering.
    /// </remarks>
    public sealed record TrustScore
    {
        /// <summary>
        /// Overall trust score (0.0 to 1.0, where 1.0 is highest trust).
        /// </summary>
        public required double Score { get; init; }

        /// <summary>
        /// Individual factors contributing to the trust score.
        /// </summary>
        public Dictionary<string, double> Factors { get; init; } = new();

        /// <summary>
        /// When this trust score was calculated.
        /// </summary>
        public DateTime CalculatedAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Algorithm or model used to calculate the trust score.
        /// </summary>
        public string? Algorithm { get; init; }

        /// <summary>
        /// Confidence in the trust score calculation (0.0 to 1.0).
        /// </summary>
        public double? Confidence { get; init; }

        /// <summary>
        /// Creates a trust score from individual factors.
        /// </summary>
        public static TrustScore FromFactors(Dictionary<string, double> factors, string? algorithm = null)
        {
            var score = factors.Values.Average();
            return new TrustScore
            {
                Score = Math.Clamp(score, 0.0, 1.0),
                Factors = factors,
                Algorithm = algorithm
            };
        }

        /// <summary>
        /// Creates a simple trust score.
        /// </summary>
        public static TrustScore Simple(double score)
        {
            return new TrustScore
            {
                Score = Math.Clamp(score, 0.0, 1.0)
            };
        }
    }

    // ==================================================================================
    // T99.C4: INFERENCE & SIMULATION
    // ==================================================================================

    /// <summary>
    /// Defines an inference rule for deriving new knowledge from existing knowledge.
    /// </summary>
    /// <remarks>
    /// Inference rules enable AI plugins to automatically derive new knowledge
    /// based on patterns, logical rules, or machine learning models.
    /// </remarks>
    public sealed record InferenceRule
    {
        /// <summary>
        /// Unique identifier for this inference rule.
        /// </summary>
        public required Guid RuleId { get; init; }

        /// <summary>
        /// Human-readable name for this rule.
        /// </summary>
        public required string Name { get; init; }

        /// <summary>
        /// Description of what this rule infers.
        /// </summary>
        public string? Description { get; init; }

        /// <summary>
        /// Condition that must be met for this rule to fire (predicate or query).
        /// </summary>
        public required string Condition { get; init; }

        /// <summary>
        /// Action to take when condition is met (e.g., create new knowledge object).
        /// </summary>
        public required string Action { get; init; }

        /// <summary>
        /// Priority for rule execution (higher values execute first).
        /// </summary>
        public int Priority { get; init; }

        /// <summary>
        /// Whether this rule is currently enabled.
        /// </summary>
        public bool Enabled { get; init; } = true;

        /// <summary>
        /// Confidence threshold for this rule to fire (0.0 to 1.0).
        /// </summary>
        public double? ConfidenceThreshold { get; init; }

        /// <summary>
        /// Tags for categorizing and filtering rules.
        /// </summary>
        public List<string> Tags { get; init; } = new();

        /// <summary>
        /// Metadata for rule configuration and tracking.
        /// </summary>
        public Dictionary<string, object> Metadata { get; init; } = new();

        /// <summary>
        /// When this rule was created.
        /// </summary>
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// When this rule was last modified.
        /// </summary>
        public DateTime UpdatedAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Who created this rule (plugin ID, user ID, etc.).
        /// </summary>
        public string? CreatedBy { get; init; }
    }

    /// <summary>
    /// Context for simulation queries (what-if scenarios).
    /// </summary>
    /// <remarks>
    /// Enables AI plugins to simulate outcomes and explore hypothetical scenarios
    /// by projecting knowledge forward or backward in time.
    /// </remarks>
    public sealed record SimulationContext
    {
        /// <summary>
        /// Unique identifier for this simulation.
        /// </summary>
        public Guid SimulationId { get; init; } = Guid.NewGuid();

        /// <summary>
        /// Simulation scenario description.
        /// </summary>
        public string? Description { get; init; }

        /// <summary>
        /// Base timestamp for the simulation (what-if at this point in time).
        /// </summary>
        public DateTime BaseTimestamp { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Hypothetical parameters to apply to the simulation.
        /// </summary>
        public Dictionary<string, object> Parameters { get; init; } = new();

        /// <summary>
        /// Constraints for the simulation (e.g., max iterations, time limit).
        /// </summary>
        public Dictionary<string, object> Constraints { get; init; } = new();

        /// <summary>
        /// Inference rules to apply during simulation.
        /// </summary>
        public List<Guid> InferenceRuleIds { get; init; } = new();

        /// <summary>
        /// How far forward/backward to simulate (null for unlimited).
        /// </summary>
        public TimeSpan? SimulationDuration { get; init; }

        /// <summary>
        /// Random seed for reproducible simulations.
        /// </summary>
        public int? RandomSeed { get; init; }

        /// <summary>
        /// Confidence threshold for simulation results.
        /// </summary>
        public double? ConfidenceThreshold { get; init; }
    }

    /// <summary>
    /// Result of a simulation query.
    /// </summary>
    /// <remarks>
    /// Captures the projected outcomes of a simulation including derived knowledge,
    /// confidence scores, and execution metrics.
    /// </remarks>
    public sealed record SimulationResult
    {
        /// <summary>
        /// The simulation context that produced this result.
        /// </summary>
        public required SimulationContext Context { get; init; }

        /// <summary>
        /// Whether the simulation completed successfully.
        /// </summary>
        public required bool Success { get; init; }

        /// <summary>
        /// Projected knowledge objects resulting from the simulation.
        /// </summary>
        public List<KnowledgeObject> ProjectedKnowledge { get; init; } = new();

        /// <summary>
        /// Overall confidence in the simulation results (0.0 to 1.0).
        /// </summary>
        public double? Confidence { get; init; }

        /// <summary>
        /// How long the simulation took to execute.
        /// </summary>
        public TimeSpan ExecutionDuration { get; init; }

        /// <summary>
        /// Number of simulation iterations performed.
        /// </summary>
        public int IterationCount { get; init; }

        /// <summary>
        /// Error message if simulation failed.
        /// </summary>
        public string? Error { get; init; }

        /// <summary>
        /// Metrics and statistics from the simulation.
        /// </summary>
        public Dictionary<string, object> Metrics { get; init; } = new();

        /// <summary>
        /// Warnings or caveats about the simulation results.
        /// </summary>
        public List<string> Warnings { get; init; } = new();

        /// <summary>
        /// Creates a successful simulation result.
        /// </summary>
        public static SimulationResult Ok(
            SimulationContext context,
            List<KnowledgeObject> projectedKnowledge,
            double? confidence = null,
            TimeSpan? duration = null)
        {
            return new SimulationResult
            {
                Context = context,
                Success = true,
                ProjectedKnowledge = projectedKnowledge,
                Confidence = confidence,
                ExecutionDuration = duration ?? TimeSpan.Zero
            };
        }

        /// <summary>
        /// Creates a failed simulation result.
        /// </summary>
        public static SimulationResult Fail(SimulationContext context, string error, TimeSpan? duration = null)
        {
            return new SimulationResult
            {
                Context = context,
                Success = false,
                Error = error,
                ExecutionDuration = duration ?? TimeSpan.Zero
            };
        }
    }
}
