using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Tags
{
    /// <summary>
    /// In-memory implementation of <see cref="ITagSchemaRegistry"/> backed by <see cref="ConcurrentDictionary{TKey,TValue}"/>.
    /// Suitable for tests and single-node deployments. Plugins can provide distributed/persistent
    /// implementations via the same interface.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 55: In-memory tag schema registry")]
    public sealed class InMemoryTagSchemaRegistry : ITagSchemaRegistry
    {
        private readonly ConcurrentDictionary<string, TagSchema> _schemas = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<TagKey, string> _tagKeyIndex = new();

        /// <inheritdoc />
        public Task RegisterAsync(TagSchema schema, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(schema);
            ArgumentException.ThrowIfNullOrWhiteSpace(schema.SchemaId);

            if (!_schemas.TryAdd(schema.SchemaId, schema))
            {
                throw new InvalidOperationException(
                    $"A schema with ID '{schema.SchemaId}' is already registered.");
            }

            _tagKeyIndex[schema.TagKey] = schema.SchemaId;
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<TagSchema?> GetAsync(string schemaId, CancellationToken ct = default)
        {
            _schemas.TryGetValue(schemaId, out var schema);
            return Task.FromResult(schema);
        }

        /// <inheritdoc />
        public Task<TagSchema?> GetByTagKeyAsync(TagKey tagKey, CancellationToken ct = default)
        {
            if (_tagKeyIndex.TryGetValue(tagKey, out var schemaId) &&
                _schemas.TryGetValue(schemaId, out var schema))
            {
                return Task.FromResult<TagSchema?>(schema);
            }

            return Task.FromResult<TagSchema?>(null);
        }

        /// <inheritdoc />
        public async IAsyncEnumerable<TagSchema> ListAsync(
            string? namespaceFilter = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var schema in _schemas.Values)
            {
                ct.ThrowIfCancellationRequested();

                if (namespaceFilter is null ||
                    schema.TagKey.Namespace.StartsWith(namespaceFilter, StringComparison.OrdinalIgnoreCase))
                {
                    yield return schema;
                }
            }

            await Task.CompletedTask; // Satisfy async requirement
        }

        /// <inheritdoc />
        public Task<bool> AddVersionAsync(
            string schemaId,
            TagSchemaVersion version,
            TagSchemaEvolutionRule rule = TagSchemaEvolutionRule.Compatible,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(version);

            if (!_schemas.TryGetValue(schemaId, out var existing))
            {
                return Task.FromResult(false);
            }

            // Enforce evolution rules
            var current = existing.CurrentVersion;

            switch (rule)
            {
                case TagSchemaEvolutionRule.None:
                    // No evolution allowed
                    return Task.FromResult(false);

                case TagSchemaEvolutionRule.Compatible:
                    // New version must not change RequiredKind
                    if (version.RequiredKind != current.RequiredKind)
                    {
                        return Task.FromResult(false);
                    }
                    break;

                case TagSchemaEvolutionRule.Additive:
                    // Must not change RequiredKind, and constraints must only add (no removals/tightening)
                    if (version.RequiredKind != current.RequiredKind)
                    {
                        return Task.FromResult(false);
                    }

                    if (!IsAdditiveChange(current.Constraints, version.Constraints))
                    {
                        return Task.FromResult(false);
                    }
                    break;

                case TagSchemaEvolutionRule.Breaking:
                    // Breaking changes allowed -- no validation needed
                    break;
            }

            // Build new versions list
            var newVersions = new List<TagSchemaVersion>(existing.Versions) { version };

            var updated = existing with { Versions = newVersions };

            _schemas[schemaId] = updated;
            return Task.FromResult(true);
        }

        /// <inheritdoc />
        public Task<bool> DeleteAsync(string schemaId, CancellationToken ct = default)
        {
            if (!_schemas.TryRemove(schemaId, out var removed))
            {
                return Task.FromResult(false);
            }

            _tagKeyIndex.TryRemove(removed.TagKey, out _);
            return Task.FromResult(true);
        }

        /// <inheritdoc />
        public Task<int> CountAsync(CancellationToken ct = default)
        {
            return Task.FromResult(_schemas.Count);
        }

        /// <summary>
        /// Validates that the new constraints are additive relative to the old constraints.
        /// Additive means: no removals of existing constraints, no tightening of ranges.
        /// Only new optional constraints or widened ranges are permitted.
        /// </summary>
        private static bool IsAdditiveChange(TagConstraint old, TagConstraint @new)
        {
            // MinValue can only decrease (widen) or stay the same, not increase (tighten)
            if (old.MinValue.HasValue && @new.MinValue.HasValue && @new.MinValue > old.MinValue)
                return false;
            // Cannot remove a MinValue that existed (removal = widening, which is OK)

            // MaxValue can only increase (widen) or stay the same, not decrease (tighten)
            if (old.MaxValue.HasValue && @new.MaxValue.HasValue && @new.MaxValue < old.MaxValue)
                return false;

            // MinLength can only decrease or stay the same
            if (old.MinLength.HasValue && @new.MinLength.HasValue && @new.MinLength > old.MinLength)
                return false;

            // MaxLength can only increase or stay the same
            if (old.MaxLength.HasValue && @new.MaxLength.HasValue && @new.MaxLength < old.MaxLength)
                return false;

            // MaxItems can only increase or stay the same
            if (old.MaxItems.HasValue && @new.MaxItems.HasValue && @new.MaxItems < old.MaxItems)
                return false;

            // MaxDepth can only increase or stay the same
            if (old.MaxDepth.HasValue && @new.MaxDepth.HasValue && @new.MaxDepth < old.MaxDepth)
                return false;

            // AllowedValues: new set must be a superset of old set (can only add values)
            if (old.AllowedValues is not null && @new.AllowedValues is not null)
            {
                if (!old.AllowedValues.IsSubsetOf(@new.AllowedValues))
                    return false;
            }
            // Cannot add AllowedValues restriction where none existed
            if (old.AllowedValues is null && @new.AllowedValues is not null)
                return false;

            // AllowedItemKinds: new set must be a superset of old set
            if (old.AllowedItemKinds is not null && @new.AllowedItemKinds is not null)
            {
                if (!old.AllowedItemKinds.IsSubsetOf(@new.AllowedItemKinds))
                    return false;
            }
            // Cannot add AllowedItemKinds restriction where none existed
            if (old.AllowedItemKinds is null && @new.AllowedItemKinds is not null)
                return false;

            // Required cannot go from false to true (that's tightening)
            if (!old.Required && @new.Required)
                return false;

            // Pattern: cannot add a pattern where none existed (tightening), cannot change to more restrictive
            if (old.Pattern is null && @new.Pattern is not null)
                return false;
            if (old.Pattern is not null && @new.Pattern is not null && old.Pattern != @new.Pattern)
                return false; // Changing pattern is not reliably verifiable as additive

            return true;
        }
    }
}
