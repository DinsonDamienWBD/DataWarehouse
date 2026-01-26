using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.SchemaRegistry
{
    /// <summary>
    /// Manages schema versions and tracks changes over time.
    /// Provides version history, change detection, and schema evolution capabilities.
    /// </summary>
    public sealed class SchemaVersionManager
    {
        private readonly ConcurrentDictionary<string, List<SchemaVersion>> _versionHistory;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public SchemaVersionManager()
        {
            _versionHistory = new ConcurrentDictionary<string, List<SchemaVersion>>();
        }

        /// <summary>
        /// Add a new schema version to the history.
        /// </summary>
        public async Task<SchemaVersion> AddVersionAsync(SchemaDefinition schema, string? description = null)
        {
            await _lock.WaitAsync();
            try
            {
                var versions = _versionHistory.GetOrAdd(schema.SchemaId, _ => new List<SchemaVersion>());

                // Detect changes from previous version
                var changes = new List<SchemaChange>();
                if (versions.Count > 0)
                {
                    var previousVersion = versions[^1];
                    changes = DetectChanges(previousVersion.Schema, schema);
                }

                var version = new SchemaVersion
                {
                    SchemaId = schema.SchemaId,
                    Version = schema.Version,
                    Schema = schema,
                    Changes = changes,
                    Description = description,
                    CreatedAt = DateTime.UtcNow
                };

                versions.Add(version);
                return version;
            }
            finally
            {
                _lock.Release();
            }
        }

        /// <summary>
        /// Get all versions for a schema.
        /// </summary>
        public List<SchemaVersion> GetVersionHistory(string schemaId)
        {
            if (_versionHistory.TryGetValue(schemaId, out var versions))
            {
                return versions.ToList();
            }
            return new List<SchemaVersion>();
        }

        /// <summary>
        /// Get a specific version of a schema.
        /// </summary>
        public SchemaVersion? GetVersion(string schemaId, int version)
        {
            if (_versionHistory.TryGetValue(schemaId, out var versions))
            {
                return versions.FirstOrDefault(v => v.Version == version);
            }
            return null;
        }

        /// <summary>
        /// Get the latest version of a schema.
        /// </summary>
        public SchemaVersion? GetLatestVersion(string schemaId)
        {
            if (_versionHistory.TryGetValue(schemaId, out var versions) && versions.Count > 0)
            {
                return versions[^1];
            }
            return null;
        }

        /// <summary>
        /// Compare two schema versions and generate a diff.
        /// </summary>
        public SchemaDiff CompareVersions(string schemaId, int fromVersion, int toVersion)
        {
            var from = GetVersion(schemaId, fromVersion);
            var to = GetVersion(schemaId, toVersion);

            if (from == null || to == null)
            {
                throw new ArgumentException($"Version not found for schema {schemaId}");
            }

            var changes = DetectChanges(from.Schema, to.Schema);
            var diff = new SchemaDiff
            {
                SchemaId = schemaId,
                FromVersion = fromVersion,
                ToVersion = toVersion,
                Changes = changes,
                IsCompatible = !changes.Any(c => c.IsBreaking)
            };

            // Analyze compatibility
            AnalyzeCompatibility(diff);

            return diff;
        }

        /// <summary>
        /// Detect changes between two schema definitions.
        /// </summary>
        public List<SchemaChange> DetectChanges(SchemaDefinition oldSchema, SchemaDefinition newSchema)
        {
            var changes = new List<SchemaChange>();

            // Check table rename
            if (oldSchema.Name != newSchema.Name)
            {
                changes.Add(new SchemaChange
                {
                    ChangeType = SchemaChangeType.TableRenamed,
                    Element = "TableName",
                    OldValue = oldSchema.Name,
                    NewValue = newSchema.Name,
                    Description = $"Table renamed from {oldSchema.Name} to {newSchema.Name}",
                    IsBreaking = true
                });
            }

            // Detect column changes
            var oldColumns = oldSchema.Columns.ToDictionary(c => c.Name);
            var newColumns = newSchema.Columns.ToDictionary(c => c.Name);

            // Added columns
            foreach (var newCol in newColumns.Values.Where(c => !oldColumns.ContainsKey(c.Name)))
            {
                changes.Add(new SchemaChange
                {
                    ChangeType = SchemaChangeType.ColumnAdded,
                    Element = newCol.Name,
                    NewValue = $"{newCol.DataType}{(newCol.IsNullable ? " NULL" : " NOT NULL")}",
                    Description = $"Column '{newCol.Name}' added",
                    IsBreaking = !newCol.IsNullable && string.IsNullOrEmpty(newCol.DefaultValue)
                });
            }

            // Removed columns
            foreach (var oldCol in oldColumns.Values.Where(c => !newColumns.ContainsKey(c.Name)))
            {
                changes.Add(new SchemaChange
                {
                    ChangeType = SchemaChangeType.ColumnRemoved,
                    Element = oldCol.Name,
                    OldValue = oldCol.DataType,
                    Description = $"Column '{oldCol.Name}' removed",
                    IsBreaking = true
                });
            }

            // Modified columns
            foreach (var colName in oldColumns.Keys.Intersect(newColumns.Keys))
            {
                var oldCol = oldColumns[colName];
                var newCol = newColumns[colName];

                if (!AreColumnsEqual(oldCol, newCol))
                {
                    var isBreaking = IsColumnChangeBreaking(oldCol, newCol);
                    changes.Add(new SchemaChange
                    {
                        ChangeType = SchemaChangeType.ColumnModified,
                        Element = colName,
                        OldValue = SerializeColumn(oldCol),
                        NewValue = SerializeColumn(newCol),
                        Description = $"Column '{colName}' modified",
                        IsBreaking = isBreaking
                    });
                }
            }

            // Detect primary key changes
            DetectPrimaryKeyChanges(oldSchema, newSchema, changes);

            // Detect foreign key changes
            DetectForeignKeyChanges(oldSchema, newSchema, changes);

            // Detect index changes
            DetectIndexChanges(oldSchema, newSchema, changes);

            return changes;
        }

        /// <summary>
        /// Calculate schema checksum for change detection.
        /// </summary>
        public string CalculateChecksum(SchemaDefinition schema)
        {
            var json = JsonSerializer.Serialize(schema, new JsonSerializerOptions
            {
                WriteIndented = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(json));
            return Convert.ToBase64String(hash);
        }

        /// <summary>
        /// Check if schema has changed by comparing checksums.
        /// </summary>
        public bool HasSchemaChanged(SchemaDefinition oldSchema, SchemaDefinition newSchema)
        {
            var oldChecksum = CalculateChecksum(oldSchema);
            var newChecksum = CalculateChecksum(newSchema);
            return oldChecksum != newChecksum;
        }

        private void DetectPrimaryKeyChanges(SchemaDefinition oldSchema, SchemaDefinition newSchema, List<SchemaChange> changes)
        {
            var oldPks = oldSchema.PrimaryKeys.ToHashSet();
            var newPks = newSchema.PrimaryKeys.ToHashSet();

            var addedPks = newPks.Except(oldPks).ToList();
            var removedPks = oldPks.Except(newPks).ToList();

            foreach (var pk in addedPks)
            {
                changes.Add(new SchemaChange
                {
                    ChangeType = SchemaChangeType.PrimaryKeyAdded,
                    Element = pk,
                    Description = $"Primary key added on column '{pk}'",
                    IsBreaking = false
                });
            }

            foreach (var pk in removedPks)
            {
                changes.Add(new SchemaChange
                {
                    ChangeType = SchemaChangeType.PrimaryKeyRemoved,
                    Element = pk,
                    Description = $"Primary key removed from column '{pk}'",
                    IsBreaking = true
                });
            }
        }

        private void DetectForeignKeyChanges(SchemaDefinition oldSchema, SchemaDefinition newSchema, List<SchemaChange> changes)
        {
            var oldFks = oldSchema.ForeignKeys.ToDictionary(fk => fk.Name);
            var newFks = newSchema.ForeignKeys.ToDictionary(fk => fk.Name);

            foreach (var fk in newFks.Values.Where(f => !oldFks.ContainsKey(f.Name)))
            {
                changes.Add(new SchemaChange
                {
                    ChangeType = SchemaChangeType.ForeignKeyAdded,
                    Element = fk.Name,
                    NewValue = $"{string.Join(",", fk.Columns)} -> {fk.ReferencedTable}({string.Join(",", fk.ReferencedColumns)})",
                    Description = $"Foreign key '{fk.Name}' added",
                    IsBreaking = false
                });
            }

            foreach (var fk in oldFks.Values.Where(f => !newFks.ContainsKey(f.Name)))
            {
                changes.Add(new SchemaChange
                {
                    ChangeType = SchemaChangeType.ForeignKeyRemoved,
                    Element = fk.Name,
                    Description = $"Foreign key '{fk.Name}' removed",
                    IsBreaking = true
                });
            }
        }

        private void DetectIndexChanges(SchemaDefinition oldSchema, SchemaDefinition newSchema, List<SchemaChange> changes)
        {
            var oldIndexes = oldSchema.Indexes.ToDictionary(i => i.Name);
            var newIndexes = newSchema.Indexes.ToDictionary(i => i.Name);

            foreach (var idx in newIndexes.Values.Where(i => !oldIndexes.ContainsKey(i.Name)))
            {
                changes.Add(new SchemaChange
                {
                    ChangeType = SchemaChangeType.IndexAdded,
                    Element = idx.Name,
                    NewValue = $"{string.Join(",", idx.Columns)} ({(idx.IsUnique ? "UNIQUE" : "NON-UNIQUE")})",
                    Description = $"Index '{idx.Name}' added",
                    IsBreaking = false
                });
            }

            foreach (var idx in oldIndexes.Values.Where(i => !newIndexes.ContainsKey(i.Name)))
            {
                changes.Add(new SchemaChange
                {
                    ChangeType = SchemaChangeType.IndexRemoved,
                    Element = idx.Name,
                    Description = $"Index '{idx.Name}' removed",
                    IsBreaking = false
                });
            }
        }

        private bool AreColumnsEqual(ColumnDefinition col1, ColumnDefinition col2)
        {
            return col1.DataType == col2.DataType &&
                   col1.IsNullable == col2.IsNullable &&
                   col1.IsPrimaryKey == col2.IsPrimaryKey &&
                   col1.MaxLength == col2.MaxLength &&
                   col1.Precision == col2.Precision &&
                   col1.Scale == col2.Scale &&
                   col1.DefaultValue == col2.DefaultValue;
        }

        private bool IsColumnChangeBreaking(ColumnDefinition oldCol, ColumnDefinition newCol)
        {
            // Changing to NOT NULL is breaking if no default value
            if (!oldCol.IsNullable && newCol.IsNullable == false && oldCol.IsNullable != newCol.IsNullable)
                return string.IsNullOrEmpty(newCol.DefaultValue);

            // Changing data type is potentially breaking
            if (oldCol.DataType != newCol.DataType)
                return !IsDataTypeCompatible(oldCol.DataType, newCol.DataType);

            // Reducing size is breaking
            if (oldCol.MaxLength.HasValue && newCol.MaxLength.HasValue && newCol.MaxLength < oldCol.MaxLength)
                return true;

            if (oldCol.Precision.HasValue && newCol.Precision.HasValue && newCol.Precision < oldCol.Precision)
                return true;

            return false;
        }

        private bool IsDataTypeCompatible(string oldType, string newType)
        {
            // Simple compatibility check - can be extended
            var compatiblePairs = new Dictionary<string, HashSet<string>>
            {
                ["int"] = new() { "bigint", "decimal", "numeric" },
                ["smallint"] = new() { "int", "bigint", "decimal", "numeric" },
                ["varchar"] = new() { "nvarchar", "text" },
                ["char"] = new() { "varchar", "nvarchar" }
            };

            if (compatiblePairs.TryGetValue(oldType.ToLower(), out var compatible))
            {
                return compatible.Contains(newType.ToLower());
            }

            return false;
        }

        private string SerializeColumn(ColumnDefinition col)
        {
            return $"{col.DataType}{(col.MaxLength.HasValue ? $"({col.MaxLength})" : "")}" +
                   $"{(col.Precision.HasValue ? $"({col.Precision},{col.Scale})" : "")}" +
                   $" {(col.IsNullable ? "NULL" : "NOT NULL")}" +
                   $"{(col.DefaultValue != null ? $" DEFAULT {col.DefaultValue}" : "")}";
        }

        private void AnalyzeCompatibility(SchemaDiff diff)
        {
            foreach (var change in diff.Changes)
            {
                if (change.IsBreaking)
                {
                    diff.CompatibilityNotes.Add($"{change.ChangeType}: {change.Description} - BREAKING");
                    diff.IsCompatible = false;
                }
                else
                {
                    diff.CompatibilityNotes.Add($"{change.ChangeType}: {change.Description}");
                }
            }
        }
    }
}
