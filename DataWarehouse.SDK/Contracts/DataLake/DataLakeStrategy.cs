using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.DataLake;

/// <summary>
/// Defines the category of data lake strategy.
/// </summary>
public enum DataLakeCategory
{
    /// <summary>Data lake architecture strategies.</summary>
    Architecture,
    /// <summary>Schema-on-read strategies.</summary>
    Schema,
    /// <summary>Data cataloging strategies.</summary>
    Catalog,
    /// <summary>Data lineage tracking strategies.</summary>
    Lineage,
    /// <summary>Data lake zone management strategies.</summary>
    Zones,
    /// <summary>Data lake security strategies.</summary>
    Security,
    /// <summary>Data lake governance strategies.</summary>
    Governance,
    /// <summary>Lake-to-warehouse integration strategies.</summary>
    Integration
}

/// <summary>
/// Defines the data lake zone types following medallion architecture.
/// </summary>
public enum DataLakeZone
{
    /// <summary>Raw/Bronze zone - landing zone for raw data.</summary>
    Raw,
    /// <summary>Curated/Silver zone - cleansed and validated data.</summary>
    Curated,
    /// <summary>Consumption/Gold zone - business-ready aggregated data.</summary>
    Consumption,
    /// <summary>Sandbox zone - for experimentation and data science.</summary>
    Sandbox,
    /// <summary>Archive zone - for historical data with infrequent access.</summary>
    Archive
}

/// <summary>
/// Represents the capabilities of a data lake strategy.
/// </summary>
public sealed record DataLakeCapabilities
{
    /// <summary>Whether the strategy supports async operations.</summary>
    public required bool SupportsAsync { get; init; }
    /// <summary>Whether the strategy supports batch operations.</summary>
    public required bool SupportsBatch { get; init; }
    /// <summary>Whether the strategy supports streaming ingestion.</summary>
    public required bool SupportsStreaming { get; init; }
    /// <summary>Whether the strategy supports ACID transactions.</summary>
    public required bool SupportsAcid { get; init; }
    /// <summary>Whether the strategy supports schema evolution.</summary>
    public required bool SupportsSchemaEvolution { get; init; }
    /// <summary>Whether the strategy supports time travel queries.</summary>
    public required bool SupportsTimeTravel { get; init; }
    /// <summary>Maximum data size in bytes (0 = unlimited).</summary>
    public long MaxDataSize { get; init; }
    /// <summary>Supported file formats.</summary>
    public string[] SupportedFormats { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Represents data lake statistics.
/// </summary>
public sealed class DataLakeStatistics
{
    /// <summary>Total files in the data lake.</summary>
    public long TotalFiles { get; set; }
    /// <summary>Total data size in bytes.</summary>
    public long TotalSizeBytes { get; set; }
    /// <summary>Total tables/datasets.</summary>
    public long TotalTables { get; set; }
    /// <summary>Total read operations.</summary>
    public long TotalReads { get; set; }
    /// <summary>Total write operations.</summary>
    public long TotalWrites { get; set; }
    /// <summary>Total queries executed.</summary>
    public long TotalQueries { get; set; }
    /// <summary>Total lineage records.</summary>
    public long TotalLineageRecords { get; set; }
    /// <summary>Total catalog entries.</summary>
    public long TotalCatalogEntries { get; set; }
    /// <summary>Data by zone.</summary>
    public Dictionary<DataLakeZone, long> DataByZone { get; set; } = new();
}

/// <summary>
/// Interface for data lake strategies.
/// </summary>
public interface IDataLakeStrategy
{
    /// <summary>Unique identifier for this strategy.</summary>
    string StrategyId { get; }
    /// <summary>Human-readable display name.</summary>
    string DisplayName { get; }
    /// <summary>Category of this strategy.</summary>
    DataLakeCategory Category { get; }
    /// <summary>Capabilities of this strategy.</summary>
    DataLakeCapabilities Capabilities { get; }
    /// <summary>Semantic description for AI discovery.</summary>
    string SemanticDescription { get; }
    /// <summary>Tags for categorization and discovery.</summary>
    string[] Tags { get; }
    /// <summary>Gets statistics for this strategy.</summary>
    DataLakeStatistics GetStatistics();
    /// <summary>Resets the statistics.</summary>
    void ResetStatistics();
    /// <summary>Initializes the strategy.</summary>
    Task InitializeAsync(CancellationToken ct = default);
    /// <summary>Disposes of the strategy resources.</summary>
    Task DisposeAsync();
}

/// <summary>
/// Abstract base class for data lake strategies.
/// </summary>
public abstract class DataLakeStrategyBase : StrategyBase, IDataLakeStrategy
{
    private readonly DataLakeStatistics _statistics = new();
    private readonly object _statsLock = new();

    /// <inheritdoc/>
    public override abstract string StrategyId { get; }
    /// <inheritdoc/>
    public abstract string DisplayName { get; }
    /// <summary>Bridges DisplayName to the StrategyBase.Name contract.</summary>
    public override string Name => DisplayName;
    /// <inheritdoc/>
    public abstract DataLakeCategory Category { get; }
    /// <inheritdoc/>
    public abstract DataLakeCapabilities Capabilities { get; }
    /// <inheritdoc/>
    public abstract string SemanticDescription { get; }
    /// <inheritdoc/>
    public abstract string[] Tags { get; }

    /// <inheritdoc/>
    public DataLakeStatistics GetStatistics()
    {
        lock (_statsLock)
        {
            return new DataLakeStatistics
            {
                TotalFiles = _statistics.TotalFiles,
                TotalSizeBytes = _statistics.TotalSizeBytes,
                TotalTables = _statistics.TotalTables,
                TotalReads = _statistics.TotalReads,
                TotalWrites = _statistics.TotalWrites,
                TotalQueries = _statistics.TotalQueries,
                TotalLineageRecords = _statistics.TotalLineageRecords,
                TotalCatalogEntries = _statistics.TotalCatalogEntries,
                DataByZone = new Dictionary<DataLakeZone, long>(_statistics.DataByZone)
            };
        }
    }

    /// <inheritdoc/>
    public void ResetStatistics()
    {
        lock (_statsLock)
        {
            _statistics.TotalFiles = 0;
            _statistics.TotalSizeBytes = 0;
            _statistics.TotalTables = 0;
            _statistics.TotalReads = 0;
            _statistics.TotalWrites = 0;
            _statistics.TotalQueries = 0;
            _statistics.TotalLineageRecords = 0;
            _statistics.TotalCatalogEntries = 0;
            _statistics.DataByZone.Clear();
        }
    }

    /// <summary>Core initialization logic delegated from StrategyBase lifecycle.</summary>
    protected override async Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        await InitializeCoreAsync(cancellationToken);
    }

    /// <inheritdoc/>
    async Task IDataLakeStrategy.InitializeAsync(CancellationToken ct)
    {
        await InitializeAsync(ct);
    }

    /// <inheritdoc/>
    async Task IDataLakeStrategy.DisposeAsync()
    {
        if (!IsInitialized) return;
        await DisposeCoreAsync();
        await ShutdownAsync();
    }

    /// <summary>Core initialization logic.</summary>
    protected virtual Task InitializeCoreAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Core disposal logic.</summary>
    protected virtual Task DisposeCoreAsync() => Task.CompletedTask;

    /// <summary>Records a read operation.</summary>
    protected void RecordRead(long bytesRead = 0)
    {
        lock (_statsLock)
        {
            _statistics.TotalReads++;
            _statistics.TotalSizeBytes += bytesRead;
        }
    }

    /// <summary>Records a write operation.</summary>
    protected void RecordWrite(long bytesWritten = 0, DataLakeZone zone = DataLakeZone.Raw)
    {
        lock (_statsLock)
        {
            _statistics.TotalWrites++;
            _statistics.TotalSizeBytes += bytesWritten;
            _statistics.DataByZone.TryGetValue(zone, out var current);
            _statistics.DataByZone[zone] = current + bytesWritten;
        }
    }

    /// <summary>Records a query operation.</summary>
    protected void RecordQuery()
    {
        lock (_statsLock) { _statistics.TotalQueries++; }
    }

    /// <summary>Records a lineage entry.</summary>
    protected void RecordLineage()
    {
        lock (_statsLock) { _statistics.TotalLineageRecords++; }
    }

    /// <summary>Records a catalog entry.</summary>
    protected void RecordCatalogEntry()
    {
        lock (_statsLock) { _statistics.TotalCatalogEntries++; }
    }

    /// <summary>Records file count.</summary>
    protected void RecordFiles(int count)
    {
        lock (_statsLock) { _statistics.TotalFiles += count; }
    }

    /// <summary>Records table count.</summary>
    protected void RecordTables(int count)
    {
        lock (_statsLock) { _statistics.TotalTables += count; }
    }

}

/// <summary>
/// Represents a data catalog entry.
/// </summary>
public sealed record DataCatalogEntry
{
    /// <summary>Unique identifier.</summary>
    public required string Id { get; init; }
    /// <summary>Name of the dataset/table.</summary>
    public required string Name { get; init; }
    /// <summary>Description.</summary>
    public string? Description { get; init; }
    /// <summary>Physical location.</summary>
    public required string Location { get; init; }
    /// <summary>Data format (parquet, delta, iceberg, etc.).</summary>
    public required string Format { get; init; }
    /// <summary>Schema definition.</summary>
    public DataLakeSchema? Schema { get; init; }
    /// <summary>Data lake zone.</summary>
    public DataLakeZone Zone { get; init; }
    /// <summary>Owner/creator.</summary>
    public string? Owner { get; init; }
    /// <summary>Creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; init; }
    /// <summary>Last modified timestamp.</summary>
    public DateTimeOffset ModifiedAt { get; init; }
    /// <summary>Row count estimate.</summary>
    public long? RowCount { get; init; }
    /// <summary>Size in bytes.</summary>
    public long? SizeBytes { get; init; }
    /// <summary>Tags for classification.</summary>
    public string[] Tags { get; init; } = Array.Empty<string>();
    /// <summary>Custom properties.</summary>
    public Dictionary<string, string> Properties { get; init; } = new();
}

/// <summary>
/// Represents a data lake schema.
/// </summary>
public sealed record DataLakeSchema
{
    /// <summary>Schema version.</summary>
    public int Version { get; init; } = 1;
    /// <summary>Schema columns.</summary>
    public DataLakeColumn[] Columns { get; init; } = Array.Empty<DataLakeColumn>();
    /// <summary>Partition columns.</summary>
    public string[] PartitionColumns { get; init; } = Array.Empty<string>();
    /// <summary>Primary key columns.</summary>
    public string[] PrimaryKey { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Represents a data lake column.
/// </summary>
public sealed record DataLakeColumn
{
    /// <summary>Column name.</summary>
    public required string Name { get; init; }
    /// <summary>Data type.</summary>
    public required string DataType { get; init; }
    /// <summary>Whether the column is nullable.</summary>
    public bool Nullable { get; init; } = true;
    /// <summary>Column description.</summary>
    public string? Description { get; init; }
    /// <summary>Default value.</summary>
    public string? DefaultValue { get; init; }
    /// <summary>Column metadata.</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();
}

/// <summary>
/// Represents a data lineage record.
/// </summary>
public sealed record DataLineageRecord
{
    /// <summary>Unique identifier.</summary>
    public required string Id { get; init; }
    /// <summary>Source dataset ID.</summary>
    public required string SourceId { get; init; }
    /// <summary>Target dataset ID.</summary>
    public required string TargetId { get; init; }
    /// <summary>Transformation type.</summary>
    public required string TransformationType { get; init; }
    /// <summary>Transformation description.</summary>
    public string? TransformationDescription { get; init; }
    /// <summary>Column-level lineage mappings.</summary>
    public ColumnLineageMapping[] ColumnMappings { get; init; } = Array.Empty<ColumnLineageMapping>();
    /// <summary>Job/pipeline that created this lineage.</summary>
    public string? JobId { get; init; }
    /// <summary>Timestamp of the transformation.</summary>
    public DateTimeOffset Timestamp { get; init; }
    /// <summary>User who initiated the transformation.</summary>
    public string? InitiatedBy { get; init; }
}

/// <summary>
/// Represents column-level lineage mapping.
/// </summary>
public sealed record ColumnLineageMapping
{
    /// <summary>Source column name.</summary>
    public required string SourceColumn { get; init; }
    /// <summary>Target column name.</summary>
    public required string TargetColumn { get; init; }
    /// <summary>Transformation expression.</summary>
    public string? Expression { get; init; }
}

/// <summary>
/// Represents a data quality rule.
/// </summary>
public sealed record DataQualityRule
{
    /// <summary>Rule identifier.</summary>
    public required string RuleId { get; init; }
    /// <summary>Rule name.</summary>
    public required string Name { get; init; }
    /// <summary>Rule type (not_null, unique, range, regex, custom).</summary>
    public required string RuleType { get; init; }
    /// <summary>Column(s) the rule applies to.</summary>
    public string[] Columns { get; init; } = Array.Empty<string>();
    /// <summary>Rule expression or condition.</summary>
    public string? Expression { get; init; }
    /// <summary>Expected threshold (0-1).</summary>
    public double Threshold { get; init; } = 1.0;
    /// <summary>Whether rule failure blocks data promotion.</summary>
    public bool IsBlocking { get; init; } = true;
}

/// <summary>
/// Represents the result of a data quality check.
/// </summary>
public sealed record DataQualityResult
{
    /// <summary>Dataset ID that was checked.</summary>
    public required string DatasetId { get; init; }
    /// <summary>Overall pass/fail status.</summary>
    public required bool Passed { get; init; }
    /// <summary>Overall quality score (0-1).</summary>
    public double QualityScore { get; init; }
    /// <summary>Individual rule results.</summary>
    public RuleResult[] RuleResults { get; init; } = Array.Empty<RuleResult>();
    /// <summary>Check timestamp.</summary>
    public DateTimeOffset Timestamp { get; init; }
    /// <summary>Total rows checked.</summary>
    public long TotalRows { get; init; }
    /// <summary>Failed rows count.</summary>
    public long FailedRows { get; init; }
}

/// <summary>
/// Represents the result of a single rule check.
/// </summary>
public sealed record RuleResult
{
    /// <summary>Rule identifier.</summary>
    public required string RuleId { get; init; }
    /// <summary>Whether the rule passed.</summary>
    public required bool Passed { get; init; }
    /// <summary>Actual value/score.</summary>
    public double ActualValue { get; init; }
    /// <summary>Expected threshold.</summary>
    public double ExpectedThreshold { get; init; }
    /// <summary>Number of violations.</summary>
    public long ViolationCount { get; init; }
    /// <summary>Sample of violating records.</summary>
    public string[] ViolationSamples { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Represents a data lake access policy.
/// </summary>
public sealed record DataLakeAccessPolicy
{
    /// <summary>Policy identifier.</summary>
    public required string PolicyId { get; init; }
    /// <summary>Policy name.</summary>
    public required string Name { get; init; }
    /// <summary>Principal (user, group, role).</summary>
    public required string Principal { get; init; }
    /// <summary>Principal type.</summary>
    public required PrincipalType PrincipalType { get; init; }
    /// <summary>Resource pattern (dataset, path, tag-based).</summary>
    public required string ResourcePattern { get; init; }
    /// <summary>Allowed actions.</summary>
    public DataLakeAction[] AllowedActions { get; init; } = Array.Empty<DataLakeAction>();
    /// <summary>Denied actions.</summary>
    public DataLakeAction[] DeniedActions { get; init; } = Array.Empty<DataLakeAction>();
    /// <summary>Row-level filter expression.</summary>
    public string? RowFilter { get; init; }
    /// <summary>Column masking rules.</summary>
    public ColumnMask[] ColumnMasks { get; init; } = Array.Empty<ColumnMask>();
    /// <summary>Policy priority (higher = more precedence).</summary>
    public int Priority { get; init; }
    /// <summary>Whether policy is enabled.</summary>
    public bool IsEnabled { get; init; } = true;
}

/// <summary>
/// Types of principals.
/// </summary>
public enum PrincipalType { User, Group, Role, ServiceAccount }

/// <summary>
/// Data lake actions.
/// </summary>
public enum DataLakeAction
{
    Read, Write, Delete, Execute, Manage, CreateTable, DropTable, AlterTable,
    CreateSchema, DropSchema, Grant, Revoke, Export, Import
}

/// <summary>
/// Represents a column mask for data protection.
/// </summary>
public sealed record ColumnMask
{
    /// <summary>Column name.</summary>
    public required string ColumnName { get; init; }
    /// <summary>Mask type.</summary>
    public required MaskType MaskType { get; init; }
    /// <summary>Custom mask function.</summary>
    public string? CustomFunction { get; init; }
}

/// <summary>
/// Types of data masking.
/// </summary>
public enum MaskType
{
    FullMask, PartialMask, Hash, Tokenize, Redact, Custom, None
}
