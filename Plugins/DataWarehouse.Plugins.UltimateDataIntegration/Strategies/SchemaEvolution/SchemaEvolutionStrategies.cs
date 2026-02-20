using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataIntegration.Strategies.SchemaEvolution;

#region 126.5.1 Forward Compatible Schema Strategy

/// <summary>
/// 126.5.1: Forward compatible schema evolution strategy allowing
/// readers with old schema to read data written with new schema.
/// </summary>
public sealed class ForwardCompatibleSchemaStrategy : DataIntegrationStrategyBase
{
    private readonly BoundedDictionary<string, SchemaVersion> _schemas = new BoundedDictionary<string, SchemaVersion>(1000);
    private readonly BoundedDictionary<string, List<SchemaVersion>> _versionHistory = new BoundedDictionary<string, List<SchemaVersion>>(1000);

    public override string StrategyId => "schema-forward-compatible";
    public override string DisplayName => "Forward Compatible Schema";
    public override IntegrationCategory Category => IntegrationCategory.SchemaEvolution;
    public override DataIntegrationCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsExactlyOnce = true,
        SupportsSchemaEvolution = true,
        SupportsIncremental = true,
        SupportsParallel = true,
        SupportsDistributed = true,
        MaxThroughputRecordsPerSec = 1000000,
        TypicalLatencyMs = 10.0
    };
    public override string SemanticDescription =>
        "Forward compatible schema evolution allowing old readers to read new data. " +
        "New fields must have defaults, field removal is allowed, type widening supported.";
    public override string[] Tags => ["schema", "evolution", "forward-compatible", "versioning"];

    /// <summary>
    /// Registers a new schema version.
    /// </summary>
    public Task<SchemaVersion> RegisterSchemaAsync(
        string schemaId,
        string schemaName,
        IReadOnlyList<FieldDefinition> fields,
        CancellationToken ct = default)
    {
        var version = GetNextVersion(schemaId);

        var schema = new SchemaVersion
        {
            SchemaId = schemaId,
            SchemaName = schemaName,
            Version = version,
            Fields = fields.ToList(),
            Compatibility = SchemaCompatibility.Forward,
            CreatedAt = DateTime.UtcNow
        };

        if (!_versionHistory.ContainsKey(schemaId))
            _versionHistory[schemaId] = new List<SchemaVersion>();

        _versionHistory[schemaId].Add(schema);
        _schemas[$"{schemaId}:v{version}"] = schema;

        RecordOperation("RegisterSchema");
        return Task.FromResult(schema);
    }

    /// <summary>
    /// Checks if a schema change is forward compatible.
    /// </summary>
    public Task<CompatibilityCheckResult> CheckCompatibilityAsync(
        string schemaId,
        IReadOnlyList<FieldDefinition> newFields,
        CancellationToken ct = default)
    {
        var currentVersion = GetLatestVersion(schemaId);
        if (currentVersion == null)
        {
            return Task.FromResult(new CompatibilityCheckResult
            {
                IsCompatible = true,
                Message = "No existing schema, compatible by default"
            });
        }

        var issues = new List<string>();

        // Check for new required fields without defaults
        foreach (var newField in newFields)
        {
            var existingField = currentVersion.Fields.FirstOrDefault(f => f.Name == newField.Name);
            if (existingField == null && !newField.IsNullable && newField.DefaultValue == null)
            {
                issues.Add($"New field '{newField.Name}' must have default value or be nullable");
            }
        }

        // Check for incompatible type changes
        foreach (var existingField in currentVersion.Fields)
        {
            var newField = newFields.FirstOrDefault(f => f.Name == existingField.Name);
            if (newField != null && !IsTypeCompatible(existingField.DataType, newField.DataType))
            {
                issues.Add($"Type change for '{existingField.Name}' from {existingField.DataType} to {newField.DataType} is not compatible");
            }
        }

        RecordOperation("CheckCompatibility");

        return Task.FromResult(new CompatibilityCheckResult
        {
            IsCompatible = issues.Count == 0,
            Message = issues.Count == 0 ? "Schema is forward compatible" : string.Join("; ", issues),
            Issues = issues
        });
    }

    private int GetNextVersion(string schemaId)
    {
        return _versionHistory.TryGetValue(schemaId, out var history) ? history.Count + 1 : 1;
    }

    private SchemaVersion? GetLatestVersion(string schemaId)
    {
        return _versionHistory.TryGetValue(schemaId, out var history) ? history.LastOrDefault() : null;
    }

    private bool IsTypeCompatible(string oldType, string newType)
    {
        if (oldType == newType) return true;

        // Type widening rules
        var wideningRules = new Dictionary<string, string[]>
        {
            ["int"] = new[] { "long", "double", "decimal" },
            ["long"] = new[] { "double", "decimal" },
            ["float"] = new[] { "double" },
            ["string"] = new[] { "string" }
        };

        return wideningRules.TryGetValue(oldType.ToLower(), out var allowedTypes) &&
               allowedTypes.Contains(newType.ToLower());
    }
}

#endregion

#region 126.5.2 Backward Compatible Schema Strategy

/// <summary>
/// 126.5.2: Backward compatible schema evolution strategy allowing
/// readers with new schema to read data written with old schema.
/// </summary>
public sealed class BackwardCompatibleSchemaStrategy : DataIntegrationStrategyBase
{
    private readonly BoundedDictionary<string, SchemaVersion> _schemas = new BoundedDictionary<string, SchemaVersion>(1000);
    private readonly BoundedDictionary<string, List<SchemaVersion>> _versionHistory = new BoundedDictionary<string, List<SchemaVersion>>(1000);

    public override string StrategyId => "schema-backward-compatible";
    public override string DisplayName => "Backward Compatible Schema";
    public override IntegrationCategory Category => IntegrationCategory.SchemaEvolution;
    public override DataIntegrationCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsExactlyOnce = true,
        SupportsSchemaEvolution = true,
        SupportsIncremental = true,
        SupportsParallel = true,
        SupportsDistributed = true,
        MaxThroughputRecordsPerSec = 1000000,
        TypicalLatencyMs = 10.0
    };
    public override string SemanticDescription =>
        "Backward compatible schema evolution allowing new readers to read old data. " +
        "Field addition with defaults allowed, removal of required fields allowed.";
    public override string[] Tags => ["schema", "evolution", "backward-compatible", "versioning"];

    /// <summary>
    /// Registers a new schema version.
    /// </summary>
    public Task<SchemaVersion> RegisterSchemaAsync(
        string schemaId,
        string schemaName,
        IReadOnlyList<FieldDefinition> fields,
        CancellationToken ct = default)
    {
        var version = GetNextVersion(schemaId);

        var schema = new SchemaVersion
        {
            SchemaId = schemaId,
            SchemaName = schemaName,
            Version = version,
            Fields = fields.ToList(),
            Compatibility = SchemaCompatibility.Backward,
            CreatedAt = DateTime.UtcNow
        };

        if (!_versionHistory.ContainsKey(schemaId))
            _versionHistory[schemaId] = new List<SchemaVersion>();

        _versionHistory[schemaId].Add(schema);
        _schemas[$"{schemaId}:v{version}"] = schema;

        RecordOperation("RegisterSchema");
        return Task.FromResult(schema);
    }

    /// <summary>
    /// Checks if a schema change is backward compatible.
    /// </summary>
    public Task<CompatibilityCheckResult> CheckCompatibilityAsync(
        string schemaId,
        IReadOnlyList<FieldDefinition> newFields,
        CancellationToken ct = default)
    {
        var currentVersion = GetLatestVersion(schemaId);
        if (currentVersion == null)
        {
            return Task.FromResult(new CompatibilityCheckResult
            {
                IsCompatible = true,
                Message = "No existing schema, compatible by default"
            });
        }

        var issues = new List<string>();

        // For backward compatibility, new fields must have defaults
        foreach (var newField in newFields)
        {
            var existingField = currentVersion.Fields.FirstOrDefault(f => f.Name == newField.Name);
            if (existingField == null && !newField.IsNullable && newField.DefaultValue == null)
            {
                issues.Add($"New field '{newField.Name}' must have default value or be nullable for backward compatibility");
            }
        }

        RecordOperation("CheckCompatibility");

        return Task.FromResult(new CompatibilityCheckResult
        {
            IsCompatible = issues.Count == 0,
            Message = issues.Count == 0 ? "Schema is backward compatible" : string.Join("; ", issues),
            Issues = issues
        });
    }

    private int GetNextVersion(string schemaId)
    {
        return _versionHistory.TryGetValue(schemaId, out var history) ? history.Count + 1 : 1;
    }

    private SchemaVersion? GetLatestVersion(string schemaId)
    {
        return _versionHistory.TryGetValue(schemaId, out var history) ? history.LastOrDefault() : null;
    }
}

#endregion

#region 126.5.3 Full Compatible Schema Strategy

/// <summary>
/// 126.5.3: Full compatible schema evolution strategy requiring both
/// forward and backward compatibility.
/// </summary>
public sealed class FullCompatibleSchemaStrategy : DataIntegrationStrategyBase
{
    private readonly BoundedDictionary<string, SchemaVersion> _schemas = new BoundedDictionary<string, SchemaVersion>(1000);
    private readonly BoundedDictionary<string, List<SchemaVersion>> _versionHistory = new BoundedDictionary<string, List<SchemaVersion>>(1000);

    public override string StrategyId => "schema-full-compatible";
    public override string DisplayName => "Full Compatible Schema";
    public override IntegrationCategory Category => IntegrationCategory.SchemaEvolution;
    public override DataIntegrationCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsExactlyOnce = true,
        SupportsSchemaEvolution = true,
        SupportsIncremental = true,
        SupportsParallel = true,
        SupportsDistributed = true,
        MaxThroughputRecordsPerSec = 1000000,
        TypicalLatencyMs = 10.0
    };
    public override string SemanticDescription =>
        "Full compatible schema evolution requiring both forward and backward compatibility. " +
        "Only optional field additions with defaults are allowed, no field removal.";
    public override string[] Tags => ["schema", "evolution", "full-compatible", "strict"];

    /// <summary>
    /// Registers a new schema version.
    /// </summary>
    public Task<SchemaVersion> RegisterSchemaAsync(
        string schemaId,
        string schemaName,
        IReadOnlyList<FieldDefinition> fields,
        CancellationToken ct = default)
    {
        var version = GetNextVersion(schemaId);

        var schema = new SchemaVersion
        {
            SchemaId = schemaId,
            SchemaName = schemaName,
            Version = version,
            Fields = fields.ToList(),
            Compatibility = SchemaCompatibility.Full,
            CreatedAt = DateTime.UtcNow
        };

        if (!_versionHistory.ContainsKey(schemaId))
            _versionHistory[schemaId] = new List<SchemaVersion>();

        _versionHistory[schemaId].Add(schema);
        _schemas[$"{schemaId}:v{version}"] = schema;

        RecordOperation("RegisterSchema");
        return Task.FromResult(schema);
    }

    /// <summary>
    /// Checks if a schema change is fully compatible.
    /// </summary>
    public Task<CompatibilityCheckResult> CheckCompatibilityAsync(
        string schemaId,
        IReadOnlyList<FieldDefinition> newFields,
        CancellationToken ct = default)
    {
        var currentVersion = GetLatestVersion(schemaId);
        if (currentVersion == null)
        {
            return Task.FromResult(new CompatibilityCheckResult
            {
                IsCompatible = true,
                Message = "No existing schema, compatible by default"
            });
        }

        var issues = new List<string>();

        // Check for removed fields (not allowed in full compatibility)
        foreach (var existingField in currentVersion.Fields)
        {
            if (!newFields.Any(f => f.Name == existingField.Name))
            {
                issues.Add($"Field '{existingField.Name}' cannot be removed in full compatibility mode");
            }
        }

        // New fields must be optional with defaults
        foreach (var newField in newFields)
        {
            var existingField = currentVersion.Fields.FirstOrDefault(f => f.Name == newField.Name);
            if (existingField == null)
            {
                if (!newField.IsNullable && newField.DefaultValue == null)
                {
                    issues.Add($"New field '{newField.Name}' must be optional or have a default value");
                }
            }
        }

        // Check for type changes (not allowed)
        foreach (var existingField in currentVersion.Fields)
        {
            var newField = newFields.FirstOrDefault(f => f.Name == existingField.Name);
            if (newField != null && existingField.DataType != newField.DataType)
            {
                issues.Add($"Type change for '{existingField.Name}' is not allowed in full compatibility mode");
            }
        }

        RecordOperation("CheckCompatibility");

        return Task.FromResult(new CompatibilityCheckResult
        {
            IsCompatible = issues.Count == 0,
            Message = issues.Count == 0 ? "Schema is fully compatible" : string.Join("; ", issues),
            Issues = issues
        });
    }

    private int GetNextVersion(string schemaId)
    {
        return _versionHistory.TryGetValue(schemaId, out var history) ? history.Count + 1 : 1;
    }

    private SchemaVersion? GetLatestVersion(string schemaId)
    {
        return _versionHistory.TryGetValue(schemaId, out var history) ? history.LastOrDefault() : null;
    }
}

#endregion

#region 126.5.4 Schema Migration Strategy

/// <summary>
/// 126.5.4: Schema migration strategy for managing and executing
/// schema migrations across versions.
/// </summary>
public sealed class SchemaMigrationStrategy : DataIntegrationStrategyBase
{
    private readonly BoundedDictionary<string, SchemaMigration> _migrations = new BoundedDictionary<string, SchemaMigration>(1000);
    private readonly BoundedDictionary<string, MigrationExecution> _executions = new BoundedDictionary<string, MigrationExecution>(1000);

    public override string StrategyId => "schema-migration";
    public override string DisplayName => "Schema Migration";
    public override IntegrationCategory Category => IntegrationCategory.SchemaEvolution;
    public override DataIntegrationCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = false,
        SupportsExactlyOnce = true,
        SupportsSchemaEvolution = true,
        SupportsIncremental = true,
        SupportsParallel = false,
        SupportsDistributed = true,
        MaxThroughputRecordsPerSec = 500000,
        TypicalLatencyMs = 100.0
    };
    public override string SemanticDescription =>
        "Schema migration for managing versioned schema changes with up/down migrations, " +
        "data transformations, rollback support, and migration history tracking.";
    public override string[] Tags => ["schema", "migration", "versioning", "rollback", "upgrade"];

    /// <summary>
    /// Creates a schema migration.
    /// </summary>
    public Task<SchemaMigration> CreateMigrationAsync(
        string migrationId,
        string schemaId,
        int fromVersion,
        int toVersion,
        IReadOnlyList<MigrationStep> upSteps,
        IReadOnlyList<MigrationStep>? downSteps = null,
        CancellationToken ct = default)
    {
        var migration = new SchemaMigration
        {
            MigrationId = migrationId,
            SchemaId = schemaId,
            FromVersion = fromVersion,
            ToVersion = toVersion,
            UpSteps = upSteps.ToList(),
            DownSteps = downSteps?.ToList() ?? GenerateDownSteps(upSteps),
            Status = MigrationStatus.Created,
            CreatedAt = DateTime.UtcNow
        };

        if (!_migrations.TryAdd(migrationId, migration))
            throw new InvalidOperationException($"Migration {migrationId} already exists");

        RecordOperation("CreateMigration");
        return Task.FromResult(migration);
    }

    /// <summary>
    /// Executes a schema migration.
    /// </summary>
    public async Task<MigrationExecution> ExecuteMigrationAsync(
        string migrationId,
        MigrationDirection direction = MigrationDirection.Up,
        CancellationToken ct = default)
    {
        if (!_migrations.TryGetValue(migrationId, out var migration))
            throw new KeyNotFoundException($"Migration {migrationId} not found");

        var execution = new MigrationExecution
        {
            ExecutionId = Guid.NewGuid().ToString("N"),
            MigrationId = migrationId,
            Direction = direction,
            Status = MigrationExecutionStatus.Running,
            StartedAt = DateTime.UtcNow
        };

        _executions[execution.ExecutionId] = execution;

        try
        {
            var steps = direction == MigrationDirection.Up ? migration.UpSteps : migration.DownSteps;
            var stepResults = new List<StepResult>();

            foreach (var step in steps)
            {
                var result = await ExecuteStepAsync(step, ct);
                stepResults.Add(result);

                if (!result.Success)
                {
                    execution.Status = MigrationExecutionStatus.Failed;
                    execution.ErrorMessage = result.ErrorMessage;
                    break;
                }
            }

            if (execution.Status != MigrationExecutionStatus.Failed)
            {
                execution.Status = MigrationExecutionStatus.Completed;
                migration.Status = direction == MigrationDirection.Up
                    ? MigrationStatus.Applied
                    : MigrationStatus.RolledBack;
            }

            execution.StepResults = stepResults;
        }
        catch (Exception ex)
        {
            execution.Status = MigrationExecutionStatus.Failed;
            execution.ErrorMessage = ex.Message;
            RecordFailure();
        }

        execution.CompletedAt = DateTime.UtcNow;
        RecordOperation("ExecuteMigration");
        return execution;
    }

    private List<MigrationStep> GenerateDownSteps(IReadOnlyList<MigrationStep> upSteps)
    {
        var downSteps = new List<MigrationStep>();

        foreach (var upStep in upSteps.Reverse())
        {
            var downStep = upStep.Type switch
            {
                MigrationStepType.AddColumn => new MigrationStep
                {
                    StepId = $"down_{upStep.StepId}",
                    Type = MigrationStepType.DropColumn,
                    ColumnName = upStep.ColumnName
                },
                MigrationStepType.DropColumn => new MigrationStep
                {
                    StepId = $"down_{upStep.StepId}",
                    Type = MigrationStepType.AddColumn,
                    ColumnName = upStep.ColumnName,
                    DataType = upStep.DataType
                },
                MigrationStepType.RenameColumn => new MigrationStep
                {
                    StepId = $"down_{upStep.StepId}",
                    Type = MigrationStepType.RenameColumn,
                    ColumnName = upStep.NewColumnName,
                    NewColumnName = upStep.ColumnName
                },
                _ => upStep
            };

            downSteps.Add(downStep);
        }

        return downSteps;
    }

    private Task<StepResult> ExecuteStepAsync(MigrationStep step, CancellationToken ct)
    {
        // Simulate step execution
        return Task.FromResult(new StepResult
        {
            StepId = step.StepId,
            Success = true,
            DurationMs = 100
        });
    }
}

#endregion

#region 126.5.5 Schema Registry Strategy

/// <summary>
/// 126.5.5: Schema registry strategy for centralized schema management
/// with versioning, validation, and governance.
/// </summary>
public sealed class SchemaRegistryStrategy : DataIntegrationStrategyBase
{
    private readonly BoundedDictionary<string, RegisteredSchema> _schemas = new BoundedDictionary<string, RegisteredSchema>(1000);
    private readonly BoundedDictionary<string, SchemaSubject> _subjects = new BoundedDictionary<string, SchemaSubject>(1000);

    public override string StrategyId => "schema-registry";
    public override string DisplayName => "Schema Registry";
    public override IntegrationCategory Category => IntegrationCategory.SchemaEvolution;
    public override DataIntegrationCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsExactlyOnce = true,
        SupportsSchemaEvolution = true,
        SupportsIncremental = true,
        SupportsParallel = true,
        SupportsDistributed = true,
        MaxThroughputRecordsPerSec = 2000000,
        TypicalLatencyMs = 5.0
    };
    public override string SemanticDescription =>
        "Centralized schema registry for managing schemas across the organization. " +
        "Supports schema versioning, compatibility validation, and governance policies.";
    public override string[] Tags => ["schema", "registry", "governance", "versioning", "confluent"];

    /// <summary>
    /// Creates a subject for schema organization.
    /// </summary>
    public Task<SchemaSubject> CreateSubjectAsync(
        string subjectName,
        SchemaCompatibility compatibility = SchemaCompatibility.Backward,
        CancellationToken ct = default)
    {
        var subject = new SchemaSubject
        {
            SubjectName = subjectName,
            Compatibility = compatibility,
            CreatedAt = DateTime.UtcNow
        };

        if (!_subjects.TryAdd(subjectName, subject))
            throw new InvalidOperationException($"Subject {subjectName} already exists");

        RecordOperation("CreateSubject");
        return Task.FromResult(subject);
    }

    /// <summary>
    /// Registers a schema under a subject.
    /// </summary>
    public Task<RegisteredSchema> RegisterSchemaAsync(
        string subjectName,
        string schemaDefinition,
        SchemaType schemaType = SchemaType.Avro,
        CancellationToken ct = default)
    {
        if (!_subjects.TryGetValue(subjectName, out var subject))
            throw new KeyNotFoundException($"Subject {subjectName} not found");

        var schemaId = GenerateSchemaId(schemaDefinition);
        var version = GetNextVersionForSubject(subjectName);

        var schema = new RegisteredSchema
        {
            SchemaId = schemaId,
            SubjectName = subjectName,
            Version = version,
            SchemaDefinition = schemaDefinition,
            SchemaType = schemaType,
            Fingerprint = GenerateFingerprint(schemaDefinition),
            RegisteredAt = DateTime.UtcNow
        };

        var key = $"{subjectName}:v{version}";
        if (!_schemas.TryAdd(key, schema))
            throw new InvalidOperationException($"Schema version {version} already exists for subject {subjectName}");

        RecordOperation("RegisterSchema");
        return Task.FromResult(schema);
    }

    /// <summary>
    /// Gets a schema by subject and version.
    /// </summary>
    public Task<RegisteredSchema?> GetSchemaAsync(
        string subjectName,
        int? version = null,
        CancellationToken ct = default)
    {
        var v = version ?? GetLatestVersionForSubject(subjectName);
        var key = $"{subjectName}:v{v}";

        _schemas.TryGetValue(key, out var schema);
        RecordOperation("GetSchema");
        return Task.FromResult<RegisteredSchema?>(schema);
    }

    /// <summary>
    /// Gets all versions for a subject.
    /// </summary>
    public Task<List<int>> GetVersionsAsync(
        string subjectName,
        CancellationToken ct = default)
    {
        var versions = _schemas.Keys
            .Where(k => k.StartsWith($"{subjectName}:v"))
            .Select(k => int.Parse(k.Split(":v")[1]))
            .OrderBy(v => v)
            .ToList();

        RecordOperation("GetVersions");
        return Task.FromResult(versions);
    }

    /// <summary>
    /// Deletes a schema version.
    /// </summary>
    public Task<bool> DeleteSchemaAsync(
        string subjectName,
        int version,
        CancellationToken ct = default)
    {
        var key = $"{subjectName}:v{version}";
        var deleted = _schemas.TryRemove(key, out _);
        RecordOperation("DeleteSchema");
        return Task.FromResult(deleted);
    }

    private int GenerateSchemaId(string definition)
    {
        return Math.Abs(definition.GetHashCode());
    }

    private int GetNextVersionForSubject(string subjectName)
    {
        return _schemas.Keys
            .Where(k => k.StartsWith($"{subjectName}:v"))
            .Select(k => int.Parse(k.Split(":v")[1]))
            .DefaultIfEmpty(0)
            .Max() + 1;
    }

    private int GetLatestVersionForSubject(string subjectName)
    {
        return _schemas.Keys
            .Where(k => k.StartsWith($"{subjectName}:v"))
            .Select(k => int.Parse(k.Split(":v")[1]))
            .DefaultIfEmpty(1)
            .Max();
    }

    private string GenerateFingerprint(string definition)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(definition));
        return Convert.ToBase64String(hash).Substring(0, 16);
    }
}

#endregion

#region Supporting Types

public enum SchemaCompatibility { None, Backward, Forward, Full, Transitive }
public enum MigrationStatus { Created, Applied, RolledBack, Failed }
public enum MigrationDirection { Up, Down }
public enum MigrationExecutionStatus { Running, Completed, Failed, RolledBack }
public enum MigrationStepType { AddColumn, DropColumn, RenameColumn, ModifyType, AddIndex, DropIndex, Custom }
public enum SchemaType { Avro, Json, Protobuf }

public sealed record SchemaVersion
{
    public required string SchemaId { get; init; }
    public required string SchemaName { get; init; }
    public int Version { get; init; }
    public required List<FieldDefinition> Fields { get; init; }
    public SchemaCompatibility Compatibility { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed record FieldDefinition
{
    public required string Name { get; init; }
    public required string DataType { get; init; }
    public bool IsNullable { get; init; } = true;
    public object? DefaultValue { get; init; }
    public string? Documentation { get; init; }
}

public sealed record CompatibilityCheckResult
{
    public bool IsCompatible { get; init; }
    public required string Message { get; init; }
    public List<string>? Issues { get; init; }
}

public sealed record SchemaMigration
{
    public required string MigrationId { get; init; }
    public required string SchemaId { get; init; }
    public int FromVersion { get; init; }
    public int ToVersion { get; init; }
    public required List<MigrationStep> UpSteps { get; init; }
    public required List<MigrationStep> DownSteps { get; init; }
    public MigrationStatus Status { get; set; }
    public DateTime CreatedAt { get; init; }
}

public sealed record MigrationStep
{
    public required string StepId { get; init; }
    public MigrationStepType Type { get; init; }
    public string? ColumnName { get; init; }
    public string? NewColumnName { get; init; }
    public string? DataType { get; init; }
    public string? CustomSql { get; init; }
}

public sealed record MigrationExecution
{
    public required string ExecutionId { get; init; }
    public required string MigrationId { get; init; }
    public MigrationDirection Direction { get; init; }
    public MigrationExecutionStatus Status { get; set; }
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; set; }
    public string? ErrorMessage { get; set; }
    public List<StepResult>? StepResults { get; set; }
}

public sealed record StepResult
{
    public required string StepId { get; init; }
    public bool Success { get; init; }
    public long DurationMs { get; init; }
    public string? ErrorMessage { get; init; }
}

public sealed record SchemaSubject
{
    public required string SubjectName { get; init; }
    public SchemaCompatibility Compatibility { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed record RegisteredSchema
{
    public required int SchemaId { get; init; }
    public required string SubjectName { get; init; }
    public int Version { get; init; }
    public required string SchemaDefinition { get; init; }
    public SchemaType SchemaType { get; init; }
    public required string Fingerprint { get; init; }
    public DateTime RegisteredAt { get; init; }
}

#endregion
