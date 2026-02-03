using System;
using System.Collections.Generic;
using System.Linq;

namespace DataWarehouse.SDK.AI
{
    /// <summary>
    /// Represents a knowledge object for plugin capability registration and knowledge exchange.
    /// Part of Universal Intelligence system (T90) integration.
    /// </summary>
    /// <remarks>
    /// KnowledgeObject is the core data structure for communication between plugins and
    /// the Universal Intelligence system. Plugins use this to:
    /// 1. Register their capabilities during initialization
    /// 2. Query other plugins for knowledge
    /// 3. Respond to knowledge requests
    /// 4. Share semantic information for AI-driven operations
    /// </remarks>
    public record KnowledgeObject
    {
        /// <summary>
        /// Unique identifier for this knowledge object.
        /// </summary>
        public required string Id { get; init; }

        /// <summary>
        /// Knowledge topic (e.g., "plugin.capabilities", "data.schema", "operation.semantics").
        /// </summary>
        /// <remarks>
        /// Topic naming convention: category.subcategory.specifics
        /// Common topics:
        /// - "plugin.capabilities" - Plugin capability registration
        /// - "plugin.operations" - Available operations
        /// - "data.schema" - Data schema information
        /// - "security.requirements" - Security requirements
        /// - "performance.characteristics" - Performance metrics
        /// </remarks>
        public required string Topic { get; init; }

        /// <summary>
        /// Plugin ID that owns/created this knowledge object.
        /// </summary>
        public required string SourcePluginId { get; init; }

        /// <summary>
        /// Plugin name for human-readable context.
        /// </summary>
        public required string SourcePluginName { get; init; }

        /// <summary>
        /// Knowledge type (e.g., "capability", "schema", "metric", "semantic").
        /// </summary>
        public required string KnowledgeType { get; init; }

        /// <summary>
        /// Human-readable description of this knowledge.
        /// </summary>
        public string? Description { get; init; }

        /// <summary>
        /// The actual knowledge payload.
        /// Structure depends on KnowledgeType and Topic.
        /// </summary>
        /// <remarks>
        /// Common payload structures:
        /// - Capability: { "operations": [...], "constraints": {...}, "dependencies": [...] }
        /// - Schema: { "fields": [...], "relationships": [...], "indexes": [...] }
        /// - Metric: { "name": "...", "value": ..., "unit": "...", "timestamp": ... }
        /// - Semantic: { "concepts": [...], "relationships": [...], "embeddings": [...] }
        /// </remarks>
        public required Dictionary<string, object> Payload { get; init; }

        /// <summary>
        /// When this knowledge was created or last updated.
        /// </summary>
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// When this knowledge expires (optional).
        /// Null means knowledge does not expire.
        /// </summary>
        public DateTimeOffset? ExpiresAt { get; init; }

        /// <summary>
        /// Version of this knowledge object.
        /// Useful for tracking knowledge evolution over time.
        /// </summary>
        public string Version { get; init; } = "1.0.0";

        /// <summary>
        /// Confidence score (0.0 to 1.0) indicating reliability of this knowledge.
        /// 1.0 = absolute certainty, 0.0 = pure speculation.
        /// </summary>
        /// <remarks>
        /// Use this for AI-generated or inferred knowledge to indicate confidence level.
        /// Human-entered or system-verified knowledge typically has confidence = 1.0.
        /// </remarks>
        public double Confidence { get; init; } = 1.0;

        /// <summary>
        /// Tags for categorization and discovery.
        /// </summary>
        public string[] Tags { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Related knowledge object IDs.
        /// </summary>
        /// <remarks>
        /// Use this to create knowledge graphs linking related concepts.
        /// Example: A schema knowledge object might reference its parent plugin's capability object.
        /// </remarks>
        public string[] RelatedKnowledgeIds { get; init; } = Array.Empty<string>();

        /// <summary>
        /// Additional metadata for extensibility.
        /// </summary>
        public Dictionary<string, object>? Metadata { get; init; }

        /// <summary>
        /// Creates a capability knowledge object for plugin registration.
        /// </summary>
        /// <param name="pluginId">Plugin identifier.</param>
        /// <param name="pluginName">Plugin name.</param>
        /// <param name="operations">Available operations.</param>
        /// <param name="constraints">Operation constraints.</param>
        /// <param name="dependencies">Plugin dependencies.</param>
        /// <returns>Knowledge object describing plugin capabilities.</returns>
        public static KnowledgeObject CreateCapabilityKnowledge(
            string pluginId,
            string pluginName,
            string[] operations,
            Dictionary<string, object>? constraints = null,
            string[]? dependencies = null)
        {
            return new KnowledgeObject
            {
                Id = $"{pluginId}.capability.{Guid.NewGuid():N}",
                Topic = "plugin.capabilities",
                SourcePluginId = pluginId,
                SourcePluginName = pluginName,
                KnowledgeType = "capability",
                Description = $"Capabilities for {pluginName}",
                Payload = new Dictionary<string, object>
                {
                    ["operations"] = operations,
                    ["constraints"] = constraints ?? new Dictionary<string, object>(),
                    ["dependencies"] = dependencies ?? Array.Empty<string>()
                },
                Tags = new[] { "capability", "plugin", pluginId }
            };
        }

        /// <summary>
        /// Creates a schema knowledge object for data structure description.
        /// </summary>
        /// <param name="pluginId">Plugin identifier.</param>
        /// <param name="pluginName">Plugin name.</param>
        /// <param name="schemaName">Schema name.</param>
        /// <param name="fields">Field definitions.</param>
        /// <returns>Knowledge object describing data schema.</returns>
        public static KnowledgeObject CreateSchemaKnowledge(
            string pluginId,
            string pluginName,
            string schemaName,
            Dictionary<string, object>[] fields)
        {
            return new KnowledgeObject
            {
                Id = $"{pluginId}.schema.{schemaName}.{Guid.NewGuid():N}",
                Topic = "data.schema",
                SourcePluginId = pluginId,
                SourcePluginName = pluginName,
                KnowledgeType = "schema",
                Description = $"Schema definition for {schemaName}",
                Payload = new Dictionary<string, object>
                {
                    ["schemaName"] = schemaName,
                    ["fields"] = fields
                },
                Tags = new[] { "schema", "data", schemaName }
            };
        }

        /// <summary>
        /// Creates a metric knowledge object for performance or operational metrics.
        /// </summary>
        /// <param name="pluginId">Plugin identifier.</param>
        /// <param name="pluginName">Plugin name.</param>
        /// <param name="metricName">Metric name.</param>
        /// <param name="value">Metric value.</param>
        /// <param name="unit">Unit of measurement.</param>
        /// <returns>Knowledge object containing metric data.</returns>
        public static KnowledgeObject CreateMetricKnowledge(
            string pluginId,
            string pluginName,
            string metricName,
            object value,
            string unit)
        {
            return new KnowledgeObject
            {
                Id = $"{pluginId}.metric.{metricName}.{Guid.NewGuid():N}",
                Topic = "performance.characteristics",
                SourcePluginId = pluginId,
                SourcePluginName = pluginName,
                KnowledgeType = "metric",
                Description = $"Metric: {metricName}",
                Payload = new Dictionary<string, object>
                {
                    ["name"] = metricName,
                    ["value"] = value,
                    ["unit"] = unit,
                    ["timestamp"] = DateTimeOffset.UtcNow
                },
                Tags = new[] { "metric", "performance", metricName }
            };
        }

        /// <summary>
        /// Creates a semantic knowledge object for AI-driven operations.
        /// </summary>
        /// <param name="pluginId">Plugin identifier.</param>
        /// <param name="pluginName">Plugin name.</param>
        /// <param name="semanticDescription">Semantic description.</param>
        /// <param name="concepts">Related concepts.</param>
        /// <param name="embeddings">Vector embeddings (optional).</param>
        /// <returns>Knowledge object with semantic information.</returns>
        public static KnowledgeObject CreateSemanticKnowledge(
            string pluginId,
            string pluginName,
            string semanticDescription,
            string[] concepts,
            float[]? embeddings = null)
        {
            var payload = new Dictionary<string, object>
            {
                ["description"] = semanticDescription,
                ["concepts"] = concepts
            };

            if (embeddings != null)
            {
                payload["embeddings"] = embeddings;
            }

            return new KnowledgeObject
            {
                Id = $"{pluginId}.semantic.{Guid.NewGuid():N}",
                Topic = "operation.semantics",
                SourcePluginId = pluginId,
                SourcePluginName = pluginName,
                KnowledgeType = "semantic",
                Description = semanticDescription,
                Payload = payload,
                Tags = new[] { "semantic", "ai" }.Concat(concepts).ToArray()
            };
        }
    }

    /// <summary>
    /// Knowledge request object for querying knowledge from plugins.
    /// </summary>
    public record KnowledgeRequest
    {
        /// <summary>
        /// Unique request identifier.
        /// </summary>
        public required string RequestId { get; init; }

        /// <summary>
        /// Plugin ID making the request.
        /// </summary>
        public required string RequestorPluginId { get; init; }

        /// <summary>
        /// Knowledge topic being requested.
        /// </summary>
        public required string Topic { get; init; }

        /// <summary>
        /// Optional query parameters to filter results.
        /// </summary>
        public Dictionary<string, object>? QueryParameters { get; init; }

        /// <summary>
        /// When the request was made.
        /// </summary>
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// Request timeout.
        /// </summary>
        public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Knowledge response object for query results.
    /// </summary>
    public record KnowledgeResponse
    {
        /// <summary>
        /// Request ID this response corresponds to.
        /// </summary>
        public required string RequestId { get; init; }

        /// <summary>
        /// Whether the request was successful.
        /// </summary>
        public required bool Success { get; init; }

        /// <summary>
        /// Matching knowledge objects.
        /// </summary>
        public KnowledgeObject[] Results { get; init; } = Array.Empty<KnowledgeObject>();

        /// <summary>
        /// Error message if not successful.
        /// </summary>
        public string? ErrorMessage { get; init; }

        /// <summary>
        /// When the response was generated.
        /// </summary>
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// Additional metadata.
        /// </summary>
        public Dictionary<string, object>? Metadata { get; init; }
    }
}
