using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Tags
{
    /// <summary>
    /// Registry for managing tag schemas -- the governance layer that defines
    /// what tags can exist, their types, constraints, and evolution rules.
    /// Implementations back this with persistent storage (database, file, etc.).
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 55: Tag schema registry interface")]
    public interface ITagSchemaRegistry
    {
        /// <summary>
        /// Registers a new tag schema. Fails if a schema with the same <see cref="TagSchema.SchemaId"/> already exists.
        /// </summary>
        /// <param name="schema">The schema to register.</param>
        /// <param name="ct">Cancellation token.</param>
        Task RegisterAsync(TagSchema schema, CancellationToken ct = default);

        /// <summary>
        /// Retrieves a schema by its unique identifier.
        /// </summary>
        /// <param name="schemaId">The schema identifier (e.g., "compliance.data-classification").</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The schema if found; otherwise <c>null</c>.</returns>
        Task<TagSchema?> GetAsync(string schemaId, CancellationToken ct = default);

        /// <summary>
        /// Retrieves a schema by the tag key it governs.
        /// </summary>
        /// <param name="tagKey">The tag key to look up.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>The schema governing the tag key if found; otherwise <c>null</c>.</returns>
        Task<TagSchema?> GetByTagKeyAsync(TagKey tagKey, CancellationToken ct = default);

        /// <summary>
        /// Lists all registered schemas, optionally filtered by namespace prefix.
        /// </summary>
        /// <param name="namespaceFilter">Optional namespace prefix filter (e.g., "compliance" returns schemas whose tag key namespace starts with "compliance").</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>An async stream of matching schemas.</returns>
        IAsyncEnumerable<TagSchema> ListAsync(string? namespaceFilter = null, CancellationToken ct = default);

        /// <summary>
        /// Adds a new version to an existing schema, subject to the specified evolution rule.
        /// </summary>
        /// <param name="schemaId">The schema to evolve.</param>
        /// <param name="version">The new version to add.</param>
        /// <param name="rule">The evolution rule governing what changes are permitted. Defaults to <see cref="TagSchemaEvolutionRule.Compatible"/>.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns><c>true</c> if the version was added; <c>false</c> if the schema was not found or the evolution rule was violated.</returns>
        Task<bool> AddVersionAsync(
            string schemaId,
            TagSchemaVersion version,
            TagSchemaEvolutionRule rule = TagSchemaEvolutionRule.Compatible,
            CancellationToken ct = default);

        /// <summary>
        /// Deletes a schema by its identifier.
        /// </summary>
        /// <param name="schemaId">The schema to delete.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns><c>true</c> if the schema was deleted; <c>false</c> if it was not found.</returns>
        Task<bool> DeleteAsync(string schemaId, CancellationToken ct = default);

        /// <summary>
        /// Returns the total number of registered schemas.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        Task<int> CountAsync(CancellationToken ct = default);
    }
}
