using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DataWarehouse.Plugins.SchemaRegistry
{
    /// <summary>
    /// Handles persistent storage and retrieval of schema definitions.
    /// Provides indexing and querying capabilities.
    /// </summary>
    public sealed class SchemaStorage
    {
        private readonly ConcurrentDictionary<string, SchemaDefinition> _schemas;
        private readonly ConcurrentDictionary<string, HashSet<string>> _sourceIndex;
        private readonly ConcurrentDictionary<string, HashSet<string>> _columnIndex;
        private readonly string _storagePath;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public SchemaStorage(string? storagePath = null)
        {
            _schemas = new ConcurrentDictionary<string, SchemaDefinition>();
            _sourceIndex = new ConcurrentDictionary<string, HashSet<string>>();
            _columnIndex = new ConcurrentDictionary<string, HashSet<string>>();

            _storagePath = storagePath ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DataWarehouse", "schemas");

            Directory.CreateDirectory(_storagePath);
        }

        /// <summary>
        /// Save a schema definition.
        /// </summary>
        public async Task SaveSchemaAsync(SchemaDefinition schema)
        {
            await _lock.WaitAsync();
            try
            {
                // Update in-memory store
                _schemas[schema.SchemaId] = schema;

                // Update indexes
                UpdateIndexes(schema);

                // Persist to disk
                await PersistSchemaAsync(schema);
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Get a schema by ID.
        /// </summary>
        public SchemaDefinition? GetSchema(string schemaId)
        {
            return _schemas.TryGetValue(schemaId, out var schema) ? schema : null;
        }

        /// <summary>
        /// Get all schemas.
        /// </summary>
        public List<SchemaDefinition> GetAllSchemas()
        {
            return _schemas.Values.ToList();
        }

        /// <summary>
        /// Delete a schema.
        /// </summary>
        public async Task<bool> DeleteSchemaAsync(string schemaId)
        {
            await _lock.WaitAsync();
            try
            {
                if (_schemas.TryRemove(schemaId, out var schema))
                {
                    // Remove from indexes
                    RemoveFromIndexes(schema);

                    // Delete from disk
                    var filePath = GetSchemaFilePath(schemaId);
                    if (File.Exists(filePath))
                    {
                        File.Delete(filePath);
                    }

                    return true;
                }

                return false;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Search schemas by criteria.
        /// </summary>
        public List<SchemaDefinition> SearchSchemas(SchemaSearchCriteria criteria)
        {
            var results = _schemas.Values.AsEnumerable();

            // Filter by name pattern
            if (!string.IsNullOrEmpty(criteria.NamePattern))
            {
                var pattern = "^" + Regex.Escape(criteria.NamePattern)
                    .Replace("\\*", ".*")
                    .Replace("\\?", ".") + "$";
                var regex = new Regex(pattern, RegexOptions.IgnoreCase);
                results = results.Where(s => regex.IsMatch(s.Name));
            }

            // Filter by source
            if (!string.IsNullOrEmpty(criteria.Source))
            {
                results = results.Where(s => s.Source.Equals(criteria.Source, StringComparison.OrdinalIgnoreCase));
            }

            // Filter by database
            if (!string.IsNullOrEmpty(criteria.Database))
            {
                results = results.Where(s => s.Database?.Equals(criteria.Database, StringComparison.OrdinalIgnoreCase) == true);
            }

            // Filter by column name
            if (!string.IsNullOrEmpty(criteria.ColumnName))
            {
                results = results.Where(s => s.Columns.Any(c =>
                    c.Name.Equals(criteria.ColumnName, StringComparison.OrdinalIgnoreCase)));
            }

            // Filter by column type
            if (!string.IsNullOrEmpty(criteria.ColumnType))
            {
                results = results.Where(s => s.Columns.Any(c =>
                    c.DataType.Equals(criteria.ColumnType, StringComparison.OrdinalIgnoreCase)));
            }

            // Filter by metadata
            if (criteria.Metadata != null && criteria.Metadata.Count > 0)
            {
                foreach (var (key, value) in criteria.Metadata)
                {
                    results = results.Where(s =>
                        s.Metadata.TryGetValue(key, out var metaValue) &&
                        metaValue.Equals(value, StringComparison.OrdinalIgnoreCase));
                }
            }

            return results.Take(criteria.Limit).ToList();
        }

        /// <summary>
        /// Get schemas by source.
        /// </summary>
        public List<SchemaDefinition> GetSchemasBySource(string source)
        {
            if (_sourceIndex.TryGetValue(source, out var schemaIds))
            {
                return schemaIds
                    .Select(id => _schemas.TryGetValue(id, out var schema) ? schema : null)
                    .Where(s => s != null)
                    .Cast<SchemaDefinition>()
                    .ToList();
            }

            return new List<SchemaDefinition>();
        }

        /// <summary>
        /// Find schemas containing a specific column.
        /// </summary>
        public List<SchemaDefinition> FindSchemasByColumn(string columnName)
        {
            var key = columnName.ToLowerInvariant();
            if (_columnIndex.TryGetValue(key, out var schemaIds))
            {
                return schemaIds
                    .Select(id => _schemas.TryGetValue(id, out var schema) ? schema : null)
                    .Where(s => s != null)
                    .Cast<SchemaDefinition>()
                    .ToList();
            }

            return new List<SchemaDefinition>();
        }

        /// <summary>
        /// Get statistics about stored schemas.
        /// </summary>
        public SchemaRegistryStats GetStatistics()
        {
            var schemas = _schemas.Values.ToList();

            var schemasBySource = schemas
                .GroupBy(s => s.Source)
                .ToDictionary(g => g.Key, g => g.Count());

            var recentlyUpdated = schemas
                .OrderByDescending(s => s.UpdatedAt)
                .Take(10)
                .Select(s => s.SchemaId)
                .ToList();

            var averageColumns = schemas.Any()
                ? schemas.Average(s => s.Columns.Count)
                : 0;

            return new SchemaRegistryStats
            {
                TotalSchemas = schemas.Count,
                SchemasBySource = schemasBySource,
                RecentlyUpdated = recentlyUpdated,
                AverageColumnCount = averageColumns,
                StorageSizeBytes = CalculateStorageSize()
            };
        }

        /// <summary>
        /// Load all schemas from disk.
        /// </summary>
        public async Task LoadAllSchemasAsync()
        {
            await _lock.WaitAsync();
            try
            {
                if (!Directory.Exists(_storagePath))
                    return;

                var files = Directory.GetFiles(_storagePath, "*.json");
                foreach (var file in files)
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(file);
                        var schema = JsonSerializer.Deserialize<SchemaDefinition>(json);

                        if (schema != null)
                        {
                            _schemas[schema.SchemaId] = schema;
                            UpdateIndexes(schema);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log error but continue loading other schemas
                        Console.WriteLine($"Error loading schema from {file}: {ex.Message}");
                    }
                }
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Export all schemas to JSON.
        /// </summary>
        public async Task<string> ExportSchemasAsync()
        {
            var schemas = _schemas.Values.ToList();
            var json = JsonSerializer.Serialize(schemas, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            return json;
        }

        /// <summary>
        /// Import schemas from JSON.
        /// </summary>
        public async Task<int> ImportSchemasAsync(string json)
        {
            var schemas = JsonSerializer.Deserialize<List<SchemaDefinition>>(json);
            if (schemas == null)
                return 0;

            var count = 0;
            foreach (var schema in schemas)
            {
                await SaveSchemaAsync(schema);
                count++;
            }

            return count;
        }

        private void UpdateIndexes(SchemaDefinition schema)
        {
            // Source index
            var sourceSchemas = _sourceIndex.GetOrAdd(schema.Source, _ => new HashSet<string>());
            sourceSchemas.Add(schema.SchemaId);

            // Column index
            foreach (var column in schema.Columns)
            {
                var key = column.Name.ToLowerInvariant();
                var columnSchemas = _columnIndex.GetOrAdd(key, _ => new HashSet<string>());
                columnSchemas.Add(schema.SchemaId);
            }
        }

        private void RemoveFromIndexes(SchemaDefinition schema)
        {
            // Source index
            if (_sourceIndex.TryGetValue(schema.Source, out var sourceSchemas))
            {
                sourceSchemas.Remove(schema.SchemaId);
                if (sourceSchemas.Count == 0)
                {
                    _sourceIndex.TryRemove(schema.Source, out _);
                }
            }

            // Column index
            foreach (var column in schema.Columns)
            {
                var key = column.Name.ToLowerInvariant();
                if (_columnIndex.TryGetValue(key, out var columnSchemas))
                {
                    columnSchemas.Remove(schema.SchemaId);
                    if (columnSchemas.Count == 0)
                    {
                        _columnIndex.TryRemove(key, out _);
                    }
                }
            }
        }

        private async Task PersistSchemaAsync(SchemaDefinition schema)
        {
            var filePath = GetSchemaFilePath(schema.SchemaId);
            var json = JsonSerializer.Serialize(schema, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            await File.WriteAllTextAsync(filePath, json);
        }

        private string GetSchemaFilePath(string schemaId)
        {
            // Sanitize schema ID for filename
            var sanitized = string.Join("_", schemaId.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(_storagePath, $"{sanitized}.json");
        }

        private long CalculateStorageSize()
        {
            if (!Directory.Exists(_storagePath))
                return 0;

            var files = Directory.GetFiles(_storagePath, "*.json");
            return files.Sum(f => new FileInfo(f).Length);
        }
    }
}
