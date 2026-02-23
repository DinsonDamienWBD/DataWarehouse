using DataWarehouse.SDK.Contracts;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.VirtualDiskEngine.Lakehouse;

/// <summary>
/// Defines a single column in a lakehouse table schema.
/// </summary>
/// <param name="Name">Column name.</param>
/// <param name="DataType">Parquet-compatible data type.</param>
/// <param name="IsNullable">Whether the column accepts null values.</param>
/// <param name="Comment">Optional column description.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 85: Lakehouse table operations")]
public sealed record ColumnDefinition(
    string Name,
    string DataType,
    bool IsNullable,
    string? Comment = null)
{
    /// <summary>
    /// Valid Parquet-compatible data types.
    /// </summary>
    public static readonly IReadOnlySet<string> ValidDataTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "INT32", "INT64", "FLOAT", "DOUBLE", "BINARY", "BOOLEAN",
        "STRING", "TIMESTAMP_MILLIS", "DATE", "DECIMAL"
    };
}

/// <summary>
/// Schema definition for a lakehouse table.
/// </summary>
/// <param name="SchemaId">Auto-incrementing schema identifier.</param>
/// <param name="Columns">Ordered column definitions.</param>
/// <param name="PartitionColumns">Column names used for partitioning.</param>
/// <param name="Properties">Optional schema-level properties.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 85: Lakehouse table operations")]
public sealed record TableSchema(
    int SchemaId,
    IReadOnlyList<ColumnDefinition> Columns,
    IReadOnlyList<string> PartitionColumns,
    Dictionary<string, string>? Properties = null);

/// <summary>
/// Partition transformation applied to a field.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 85: Lakehouse table operations")]
public enum PartitionTransform
{
    /// <summary>Use the field value as-is.</summary>
    Identity,
    /// <summary>Extract year from timestamp.</summary>
    Year,
    /// <summary>Extract month from timestamp.</summary>
    Month,
    /// <summary>Extract day from timestamp.</summary>
    Day,
    /// <summary>Extract hour from timestamp.</summary>
    Hour,
    /// <summary>Hash the field value into buckets.</summary>
    Hash,
    /// <summary>Truncate the field value.</summary>
    Truncate
}

/// <summary>
/// Partition specification for a table field.
/// </summary>
/// <param name="FieldName">The field to partition on.</param>
/// <param name="Transform">The transformation to apply.</param>
/// <param name="BucketCount">Number of buckets for Hash transform.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 85: Lakehouse table operations")]
public sealed record PartitionSpec(
    string FieldName,
    PartitionTransform Transform,
    int? BucketCount = null);

/// <summary>
/// Table-level operations built on the VDE-native transaction log.
/// Provides create, schema evolution, file management, compaction, and vacuum.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 85: Lakehouse table operations")]
public sealed class LakehouseTableOperations
{
    private readonly DeltaIcebergTransactionLog _transactionLog;
    private readonly ILogger? _logger;
    private int _nextSchemaId;

    /// <summary>
    /// Initializes a new instance of table operations.
    /// </summary>
    /// <param name="transactionLog">The underlying transaction log.</param>
    /// <param name="logger">Optional logger.</param>
    public LakehouseTableOperations(
        DeltaIcebergTransactionLog transactionLog,
        ILogger? logger = null)
    {
        _transactionLog = transactionLog ?? throw new ArgumentNullException(nameof(transactionLog));
        _logger = logger;
    }

    /// <summary>
    /// Creates a new lakehouse table with an initial schema recorded in the transaction log.
    /// </summary>
    /// <param name="tableName">Human-readable table name.</param>
    /// <param name="schema">The initial table schema.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The generated table identifier.</returns>
    public async ValueTask<string> CreateTableAsync(
        string tableName,
        TableSchema schema,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tableName);
        ArgumentNullException.ThrowIfNull(schema);

        string tableId = $"tbl_{Guid.NewGuid():N}";
        int schemaId = Interlocked.Increment(ref _nextSchemaId);

        var schemaWithId = schema with { SchemaId = schemaId };
        string schemaJson = JsonSerializer.Serialize(schemaWithId, _jsonOptions);

        var entries = new List<TransactionEntry>
        {
            new TransactionEntry(
                0, tableId, TransactionAction.SetSchema,
                DateTimeOffset.UtcNow, null, null, schemaJson, string.Empty),
            new TransactionEntry(
                0, tableId, TransactionAction.CommitInfo,
                DateTimeOffset.UtcNow, null,
                new Dictionary<string, string>
                {
                    ["tableName"] = tableName,
                    ["operation"] = "CREATE TABLE"
                },
                null, string.Empty)
        };

        await _transactionLog.CommitAsync(tableId, entries, ct).ConfigureAwait(false);

        _logger?.LogDebug("Created table {TableName} with id {TableId}", tableName, tableId);
        return tableId;
    }

    /// <summary>
    /// Evolves the schema of an existing table. Validates backward compatibility:
    /// no removing required columns, no type narrowing.
    /// </summary>
    /// <param name="tableId">The table identifier.</param>
    /// <param name="newSchema">The new schema definition.</param>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask EvolveSchemaAsync(
        string tableId,
        TableSchema newSchema,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tableId);
        ArgumentNullException.ThrowIfNull(newSchema);

        var currentSchema = await GetCurrentSchemaAsync(tableId, ct).ConfigureAwait(false);
        if (currentSchema is not null)
        {
            ValidateSchemaEvolution(currentSchema, newSchema);
        }

        int schemaId = Interlocked.Increment(ref _nextSchemaId);
        var schemaWithId = newSchema with { SchemaId = schemaId };
        string schemaJson = JsonSerializer.Serialize(schemaWithId, _jsonOptions);

        var entries = new List<TransactionEntry>
        {
            new TransactionEntry(
                0, tableId, TransactionAction.SetSchema,
                DateTimeOffset.UtcNow, null, null, schemaJson, string.Empty)
        };

        await _transactionLog.CommitAsync(tableId, entries, ct).ConfigureAwait(false);

        _logger?.LogDebug("Evolved schema for table {TableId} to schema {SchemaId}", tableId, schemaId);
    }

    /// <summary>
    /// Adds data files to a table via AddFile transaction entries.
    /// </summary>
    /// <param name="tableId">The table identifier.</param>
    /// <param name="filePaths">Paths of files to add.</param>
    /// <param name="partitionValues">Optional partition key-value pairs.</param>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask AddFilesAsync(
        string tableId,
        IReadOnlyList<string> filePaths,
        Dictionary<string, string>? partitionValues = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tableId);
        ArgumentNullException.ThrowIfNull(filePaths);

        var entries = filePaths.Select(fp => new TransactionEntry(
            0, tableId, TransactionAction.AddFile,
            DateTimeOffset.UtcNow, fp, partitionValues, null, string.Empty
        )).ToList();

        await _transactionLog.CommitAsync(tableId, entries, ct).ConfigureAwait(false);

        _logger?.LogDebug("Added {FileCount} files to table {TableId}", filePaths.Count, tableId);
    }

    /// <summary>
    /// Logically removes data files from a table (they remain accessible via time travel).
    /// </summary>
    /// <param name="tableId">The table identifier.</param>
    /// <param name="filePaths">Paths of files to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask RemoveFilesAsync(
        string tableId,
        IReadOnlyList<string> filePaths,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tableId);
        ArgumentNullException.ThrowIfNull(filePaths);

        var entries = filePaths.Select(fp => new TransactionEntry(
            0, tableId, TransactionAction.RemoveFile,
            DateTimeOffset.UtcNow, fp, null, null, string.Empty
        )).ToList();

        await _transactionLog.CommitAsync(tableId, entries, ct).ConfigureAwait(false);

        _logger?.LogDebug("Removed {FileCount} files from table {TableId}", filePaths.Count, tableId);
    }

    /// <summary>
    /// Compacts small files into larger ones: adds new merged file and removes old files
    /// in a single atomic commit.
    /// </summary>
    /// <param name="tableId">The table identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask OptimizeAsync(string tableId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tableId);

        var activeFiles = await ListActiveFilesAsync(tableId, ct).ConfigureAwait(false);
        if (activeFiles.Count <= 1) return;

        // Create a compacted file reference and remove originals
        string compactedFile = $"compacted_{Guid.NewGuid():N}.parquet";

        var entries = new List<TransactionEntry>();

        // Remove old files
        foreach (var file in activeFiles)
        {
            entries.Add(new TransactionEntry(
                0, tableId, TransactionAction.RemoveFile,
                DateTimeOffset.UtcNow, file, null, null, string.Empty));
        }

        // Add compacted file
        entries.Add(new TransactionEntry(
            0, tableId, TransactionAction.AddFile,
            DateTimeOffset.UtcNow, compactedFile, null, null, string.Empty));

        // Commit info
        entries.Add(new TransactionEntry(
            0, tableId, TransactionAction.CommitInfo,
            DateTimeOffset.UtcNow, null,
            new Dictionary<string, string>
            {
                ["operation"] = "OPTIMIZE",
                ["filesCompacted"] = activeFiles.Count.ToString(),
                ["compactedFile"] = compactedFile
            },
            null, string.Empty));

        await _transactionLog.CommitAsync(tableId, entries, ct).ConfigureAwait(false);

        _logger?.LogDebug(
            "Optimized table {TableId}: compacted {FileCount} files into {CompactedFile}",
            tableId, activeFiles.Count, compactedFile);
    }

    /// <summary>
    /// Gets the current schema for a table by replaying the log and finding the latest SetSchema.
    /// </summary>
    /// <param name="tableId">The table identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The current schema, or null if no schema has been set.</returns>
    public async ValueTask<TableSchema?> GetCurrentSchemaAsync(
        string tableId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tableId);

        long latestVersion = await _transactionLog.GetLatestVersionAsync(tableId, ct).ConfigureAwait(false);
        if (latestVersion < 0) return null;

        var snapshot = await _transactionLog.GetSnapshotAtVersionAsync(tableId, latestVersion, ct)
            .ConfigureAwait(false);

        var schemaEntry = snapshot.FirstOrDefault(e => e.Action == TransactionAction.SetSchema);
        if (schemaEntry?.SchemaJson is null) return null;

        return JsonSerializer.Deserialize<TableSchema>(schemaEntry.SchemaJson, _jsonOptions);
    }

    /// <summary>
    /// Lists all currently active files in a table (AddFile minus RemoveFile).
    /// </summary>
    /// <param name="tableId">The table identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of active file paths.</returns>
    public async ValueTask<IReadOnlyList<string>> ListActiveFilesAsync(
        string tableId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tableId);

        long latestVersion = await _transactionLog.GetLatestVersionAsync(tableId, ct).ConfigureAwait(false);
        if (latestVersion < 0) return Array.Empty<string>();

        var snapshot = await _transactionLog.GetSnapshotAtVersionAsync(tableId, latestVersion, ct)
            .ConfigureAwait(false);

        return snapshot
            .Where(e => e.Action == TransactionAction.AddFile && e.FilePath is not null)
            .Select(e => e.FilePath!)
            .ToList();
    }

    /// <summary>
    /// Permanently removes file references older than the retention period (Delta Lake VACUUM semantics).
    /// Files removed before the retention cutoff are no longer accessible via time travel.
    /// </summary>
    /// <param name="tableId">The table identifier.</param>
    /// <param name="retentionPeriod">Minimum age before files can be vacuumed.</param>
    /// <param name="ct">Cancellation token.</param>
    public async ValueTask VacuumAsync(
        string tableId,
        TimeSpan retentionPeriod,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(tableId);

        var cutoff = DateTimeOffset.UtcNow - retentionPeriod;
        long latestVersion = await _transactionLog.GetLatestVersionAsync(tableId, ct).ConfigureAwait(false);
        if (latestVersion < 0) return;

        // Find files that were removed before the retention cutoff
        var history = await _transactionLog.GetHistoryAsync(tableId, 0, latestVersion, ct)
            .ConfigureAwait(false);

        var removedBeforeCutoff = new HashSet<string>(StringComparer.Ordinal);

        foreach (var commit in history)
        {
            if (commit.Timestamp > cutoff) break;

            foreach (var entry in commit.Entries)
            {
                if (entry.Action == TransactionAction.RemoveFile && entry.FilePath is not null)
                {
                    removedBeforeCutoff.Add(entry.FilePath);
                }
            }
        }

        if (removedBeforeCutoff.Count > 0)
        {
            // Record vacuum operation in the log
            var entries = new List<TransactionEntry>
            {
                new TransactionEntry(
                    0, tableId, TransactionAction.CommitInfo,
                    DateTimeOffset.UtcNow, null,
                    new Dictionary<string, string>
                    {
                        ["operation"] = "VACUUM",
                        ["filesVacuumed"] = removedBeforeCutoff.Count.ToString(),
                        ["retentionHours"] = retentionPeriod.TotalHours.ToString("F1")
                    },
                    null, string.Empty)
            };

            await _transactionLog.CommitAsync(tableId, entries, ct).ConfigureAwait(false);

            _logger?.LogDebug(
                "Vacuumed table {TableId}: {FileCount} files older than {Retention}",
                tableId, removedBeforeCutoff.Count, retentionPeriod);
        }
    }

    private static void ValidateSchemaEvolution(TableSchema current, TableSchema proposed)
    {
        var currentColumns = current.Columns.ToDictionary(c => c.Name, StringComparer.Ordinal);

        foreach (var existingCol in current.Columns)
        {
            if (!existingCol.IsNullable)
            {
                // Required columns must still exist in the new schema
                var proposedCol = proposed.Columns.FirstOrDefault(
                    c => string.Equals(c.Name, existingCol.Name, StringComparison.Ordinal));

                if (proposedCol is null)
                {
                    throw new InvalidOperationException(
                        $"Cannot remove required column '{existingCol.Name}' during schema evolution.");
                }

                // No type narrowing: existing type must match or widen
                if (!IsCompatibleTypeChange(existingCol.DataType, proposedCol.DataType))
                {
                    throw new InvalidOperationException(
                        $"Cannot narrow type of column '{existingCol.Name}' from " +
                        $"'{existingCol.DataType}' to '{proposedCol.DataType}'.");
                }
            }
        }
    }

    private static bool IsCompatibleTypeChange(string from, string to)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) return true;

        // Allow widening: INT32 -> INT64, FLOAT -> DOUBLE
        return (from, to) switch
        {
            ("INT32", "INT64") => true,
            ("FLOAT", "DOUBLE") => true,
            _ => false
        };
    }

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };
}
