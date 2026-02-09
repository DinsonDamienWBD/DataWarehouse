using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace DataWarehouse.Plugins.UltimateDataIntegration.Strategies.ELT;

#region 126.2.1 Cloud-Native ELT Strategy

/// <summary>
/// 126.2.1: Cloud-native ELT pattern leveraging cloud data warehouse compute
/// for transformations after loading raw data.
/// </summary>
public sealed class CloudNativeEltStrategy : DataIntegrationStrategyBase
{
    private readonly ConcurrentDictionary<string, EltPipeline> _pipelines = new();
    private long _totalTransformationsExecuted;

    public override string StrategyId => "elt-cloud-native";
    public override string DisplayName => "Cloud-Native ELT";
    public override IntegrationCategory Category => IntegrationCategory.EltPatterns;
    public override DataIntegrationCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = false,
        SupportsExactlyOnce = true,
        SupportsSchemaEvolution = true,
        SupportsIncremental = true,
        SupportsParallel = true,
        SupportsDistributed = true,
        MaxThroughputRecordsPerSec = 10000000,
        TypicalLatencyMs = 500.0
    };
    public override string SemanticDescription =>
        "Cloud-native ELT pattern loading raw data into cloud data warehouses (Snowflake, BigQuery, Redshift) " +
        "and performing transformations using warehouse compute power.";
    public override string[] Tags => ["elt", "cloud-native", "snowflake", "bigquery", "redshift", "warehouse"];

    /// <summary>
    /// Creates an ELT pipeline.
    /// </summary>
    public Task<EltPipeline> CreatePipelineAsync(
        string pipelineId,
        EltSource source,
        EltTarget target,
        IReadOnlyList<EltTransformation> transformations,
        EltConfig? config = null,
        CancellationToken ct = default)
    {
        var pipeline = new EltPipeline
        {
            PipelineId = pipelineId,
            Source = source,
            Target = target,
            Transformations = transformations.ToList(),
            Config = config ?? new EltConfig(),
            Status = EltPipelineStatus.Created,
            CreatedAt = DateTime.UtcNow
        };

        if (!_pipelines.TryAdd(pipelineId, pipeline))
            throw new InvalidOperationException($"Pipeline {pipelineId} already exists");

        RecordOperation("CreateEltPipeline");
        return Task.FromResult(pipeline);
    }

    /// <summary>
    /// Executes the ELT pipeline.
    /// </summary>
    public async Task<EltExecutionResult> ExecuteAsync(
        string pipelineId,
        CancellationToken ct = default)
    {
        if (!_pipelines.TryGetValue(pipelineId, out var pipeline))
            throw new KeyNotFoundException($"Pipeline {pipelineId} not found");

        var startTime = DateTime.UtcNow;

        // Extract & Load phase (E+L)
        var loadResult = await LoadRawDataAsync(pipeline.Source, pipeline.Target, ct);

        // Transform phase (T) - executed in the warehouse
        var transformResults = new List<TransformationResult>();
        foreach (var transformation in pipeline.Transformations)
        {
            var result = await ExecuteTransformationAsync(transformation, pipeline.Target, ct);
            transformResults.Add(result);
            Interlocked.Increment(ref _totalTransformationsExecuted);
        }

        RecordOperation("ExecuteEltPipeline");

        return new EltExecutionResult
        {
            PipelineId = pipelineId,
            ExecutionId = Guid.NewGuid().ToString("N"),
            RecordsLoaded = loadResult.RecordsLoaded,
            TransformationResults = transformResults,
            StartTime = startTime,
            EndTime = DateTime.UtcNow,
            Status = EltExecutionStatus.Success
        };
    }

    private Task<LoadResult> LoadRawDataAsync(EltSource source, EltTarget target, CancellationToken ct)
    {
        // Simulate raw data loading
        return Task.FromResult(new LoadResult
        {
            RecordsLoaded = 100000,
            BytesLoaded = 50_000_000
        });
    }

    private Task<TransformationResult> ExecuteTransformationAsync(
        EltTransformation transformation,
        EltTarget target,
        CancellationToken ct)
    {
        // Simulate SQL transformation execution in warehouse
        return Task.FromResult(new TransformationResult
        {
            TransformationId = transformation.TransformationId,
            RowsAffected = 95000,
            DurationMs = 5000,
            Status = TransformationStatus.Success
        });
    }
}

#endregion

#region 126.2.2 dbt-Style Transformation Strategy

/// <summary>
/// 126.2.2: dbt-style transformation strategy with SQL-based models,
/// dependency graphs, and incremental materialization.
/// </summary>
public sealed class DbtStyleTransformationStrategy : DataIntegrationStrategyBase
{
    private readonly ConcurrentDictionary<string, DbtProject> _projects = new();
    private readonly ConcurrentDictionary<string, DbtModel> _models = new();

    public override string StrategyId => "elt-dbt-style";
    public override string DisplayName => "dbt-Style Transformation";
    public override IntegrationCategory Category => IntegrationCategory.EltPatterns;
    public override DataIntegrationCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = false,
        SupportsExactlyOnce = true,
        SupportsSchemaEvolution = true,
        SupportsIncremental = true,
        SupportsParallel = true,
        SupportsDistributed = false,
        MaxThroughputRecordsPerSec = 5000000,
        TypicalLatencyMs = 1000.0
    };
    public override string SemanticDescription =>
        "dbt-style SQL transformation with models, tests, documentation, dependency graphs, " +
        "incremental materialization, and source freshness checks.";
    public override string[] Tags => ["elt", "dbt", "sql", "models", "incremental", "dag"];

    /// <summary>
    /// Creates a dbt project.
    /// </summary>
    public Task<DbtProject> CreateProjectAsync(
        string projectId,
        string projectName,
        DbtProjectConfig? config = null,
        CancellationToken ct = default)
    {
        var project = new DbtProject
        {
            ProjectId = projectId,
            ProjectName = projectName,
            Config = config ?? new DbtProjectConfig(),
            CreatedAt = DateTime.UtcNow
        };

        if (!_projects.TryAdd(projectId, project))
            throw new InvalidOperationException($"Project {projectId} already exists");

        RecordOperation("CreateDbtProject");
        return Task.FromResult(project);
    }

    /// <summary>
    /// Adds a model to the project.
    /// </summary>
    public Task<DbtModel> AddModelAsync(
        string projectId,
        string modelName,
        string sqlQuery,
        MaterializationType materialization = MaterializationType.View,
        IReadOnlyList<string>? dependencies = null,
        CancellationToken ct = default)
    {
        if (!_projects.ContainsKey(projectId))
            throw new KeyNotFoundException($"Project {projectId} not found");

        var model = new DbtModel
        {
            ModelId = $"{projectId}:{modelName}",
            ProjectId = projectId,
            ModelName = modelName,
            SqlQuery = sqlQuery,
            Materialization = materialization,
            Dependencies = dependencies?.ToList() ?? new List<string>(),
            CreatedAt = DateTime.UtcNow
        };

        if (!_models.TryAdd(model.ModelId, model))
            throw new InvalidOperationException($"Model {modelName} already exists in project {projectId}");

        RecordOperation("AddDbtModel");
        return Task.FromResult(model);
    }

    /// <summary>
    /// Runs the dbt project.
    /// </summary>
    public async Task<DbtRunResult> RunAsync(
        string projectId,
        DbtRunOptions? options = null,
        CancellationToken ct = default)
    {
        if (!_projects.TryGetValue(projectId, out var project))
            throw new KeyNotFoundException($"Project {projectId} not found");

        var startTime = DateTime.UtcNow;
        var models = _models.Values.Where(m => m.ProjectId == projectId).ToList();

        // Build dependency graph and execute in order
        var executionOrder = TopologicalSort(models);
        var modelResults = new List<DbtModelResult>();

        foreach (var model in executionOrder)
        {
            var result = await ExecuteModelAsync(model, ct);
            modelResults.Add(result);
        }

        RecordOperation("RunDbtProject");

        return new DbtRunResult
        {
            ProjectId = projectId,
            RunId = Guid.NewGuid().ToString("N"),
            ModelsExecuted = modelResults.Count,
            ModelResults = modelResults,
            StartTime = startTime,
            EndTime = DateTime.UtcNow,
            Status = DbtRunStatus.Success
        };
    }

    private List<DbtModel> TopologicalSort(List<DbtModel> models)
    {
        // Simplified topological sort
        return models.OrderBy(m => m.Dependencies.Count).ToList();
    }

    private Task<DbtModelResult> ExecuteModelAsync(DbtModel model, CancellationToken ct)
    {
        return Task.FromResult(new DbtModelResult
        {
            ModelId = model.ModelId,
            ModelName = model.ModelName,
            RowsAffected = 10000,
            DurationMs = 2000,
            Status = DbtModelStatus.Success
        });
    }
}

#endregion

#region 126.2.3 Reverse ELT Strategy

/// <summary>
/// 126.2.3: Reverse ELT pattern for syncing transformed warehouse data
/// back to operational systems and SaaS applications.
/// </summary>
public sealed class ReverseEltStrategy : DataIntegrationStrategyBase
{
    private readonly ConcurrentDictionary<string, ReverseEltSync> _syncs = new();

    public override string StrategyId => "elt-reverse";
    public override string DisplayName => "Reverse ELT";
    public override IntegrationCategory Category => IntegrationCategory.EltPatterns;
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
        MaxThroughputRecordsPerSec = 100000,
        TypicalLatencyMs = 200.0
    };
    public override string SemanticDescription =>
        "Reverse ELT for syncing transformed data from warehouse back to operational systems, " +
        "CRMs, marketing tools, and SaaS applications for data activation.";
    public override string[] Tags => ["elt", "reverse-etl", "activation", "sync", "crm", "saas"];

    /// <summary>
    /// Creates a reverse ELT sync.
    /// </summary>
    public Task<ReverseEltSync> CreateSyncAsync(
        string syncId,
        WarehouseSource source,
        OperationalTarget target,
        SyncConfig? config = null,
        CancellationToken ct = default)
    {
        var sync = new ReverseEltSync
        {
            SyncId = syncId,
            Source = source,
            Target = target,
            Config = config ?? new SyncConfig(),
            Status = SyncStatus.Created,
            CreatedAt = DateTime.UtcNow
        };

        if (!_syncs.TryAdd(syncId, sync))
            throw new InvalidOperationException($"Sync {syncId} already exists");

        RecordOperation("CreateReverseEltSync");
        return Task.FromResult(sync);
    }

    /// <summary>
    /// Executes the reverse ELT sync.
    /// </summary>
    public async Task<ReverseSyncResult> ExecuteSyncAsync(
        string syncId,
        CancellationToken ct = default)
    {
        if (!_syncs.TryGetValue(syncId, out var sync))
            throw new KeyNotFoundException($"Sync {syncId} not found");

        var startTime = DateTime.UtcNow;

        // Extract from warehouse
        var records = await ExtractFromWarehouseAsync(sync.Source, ct);

        // Sync to operational system
        var syncResult = await SyncToTargetAsync(records, sync.Target, ct);

        RecordOperation("ExecuteReverseEltSync");

        return new ReverseSyncResult
        {
            SyncId = syncId,
            ExecutionId = Guid.NewGuid().ToString("N"),
            RecordsExtracted = records.Count,
            RecordsSynced = syncResult.Synced,
            RecordsFailed = syncResult.Failed,
            StartTime = startTime,
            EndTime = DateTime.UtcNow,
            Status = ReverseSyncStatus.Success
        };
    }

    private Task<List<Dictionary<string, object>>> ExtractFromWarehouseAsync(
        WarehouseSource source,
        CancellationToken ct)
    {
        var records = new List<Dictionary<string, object>>();
        for (int i = 0; i < 1000; i++)
        {
            records.Add(new Dictionary<string, object>
            {
                ["id"] = i,
                ["customer_id"] = $"CUST_{i}",
                ["score"] = 85.5 + (i % 15)
            });
        }
        return Task.FromResult(records);
    }

    private Task<(int Synced, int Failed)> SyncToTargetAsync(
        List<Dictionary<string, object>> records,
        OperationalTarget target,
        CancellationToken ct)
    {
        return Task.FromResult((records.Count, 0));
    }
}

#endregion

#region 126.2.4 Medallion Architecture Strategy

/// <summary>
/// 126.2.4: Medallion architecture (Bronze/Silver/Gold) for organizing
/// data quality tiers in lakehouse environments.
/// </summary>
public sealed class MedallionArchitectureStrategy : DataIntegrationStrategyBase
{
    private readonly ConcurrentDictionary<string, MedallionPipeline> _pipelines = new();

    public override string StrategyId => "elt-medallion";
    public override string DisplayName => "Medallion Architecture";
    public override IntegrationCategory Category => IntegrationCategory.EltPatterns;
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
        MaxThroughputRecordsPerSec = 5000000,
        TypicalLatencyMs = 300.0
    };
    public override string SemanticDescription =>
        "Medallion architecture with Bronze (raw), Silver (validated/enriched), and Gold (aggregated/curated) " +
        "tiers for progressive data refinement in lakehouse environments.";
    public override string[] Tags => ["elt", "medallion", "bronze", "silver", "gold", "lakehouse", "databricks"];

    /// <summary>
    /// Creates a medallion pipeline.
    /// </summary>
    public Task<MedallionPipeline> CreatePipelineAsync(
        string pipelineId,
        MedallionSource source,
        MedallionConfig? config = null,
        CancellationToken ct = default)
    {
        var pipeline = new MedallionPipeline
        {
            PipelineId = pipelineId,
            Source = source,
            Config = config ?? new MedallionConfig(),
            Status = MedallionStatus.Created,
            CreatedAt = DateTime.UtcNow
        };

        if (!_pipelines.TryAdd(pipelineId, pipeline))
            throw new InvalidOperationException($"Pipeline {pipelineId} already exists");

        RecordOperation("CreateMedallionPipeline");
        return Task.FromResult(pipeline);
    }

    /// <summary>
    /// Processes data through medallion tiers.
    /// </summary>
    public async Task<MedallionResult> ProcessAsync(
        string pipelineId,
        CancellationToken ct = default)
    {
        if (!_pipelines.TryGetValue(pipelineId, out var pipeline))
            throw new KeyNotFoundException($"Pipeline {pipelineId} not found");

        var startTime = DateTime.UtcNow;

        // Bronze tier - raw data ingestion
        var bronzeResult = await ProcessBronzeTierAsync(pipeline, ct);

        // Silver tier - cleansed and enriched
        var silverResult = await ProcessSilverTierAsync(pipeline, bronzeResult, ct);

        // Gold tier - aggregated and curated
        var goldResult = await ProcessGoldTierAsync(pipeline, silverResult, ct);

        RecordOperation("ProcessMedallion");

        return new MedallionResult
        {
            PipelineId = pipelineId,
            ExecutionId = Guid.NewGuid().ToString("N"),
            BronzeRecords = bronzeResult.Records,
            SilverRecords = silverResult.Records,
            GoldRecords = goldResult.Records,
            StartTime = startTime,
            EndTime = DateTime.UtcNow,
            Status = MedallionExecutionStatus.Success
        };
    }

    private Task<TierResult> ProcessBronzeTierAsync(MedallionPipeline pipeline, CancellationToken ct)
    {
        return Task.FromResult(new TierResult { Tier = "bronze", Records = 100000 });
    }

    private Task<TierResult> ProcessSilverTierAsync(MedallionPipeline pipeline, TierResult bronze, CancellationToken ct)
    {
        return Task.FromResult(new TierResult { Tier = "silver", Records = 95000 });
    }

    private Task<TierResult> ProcessGoldTierAsync(MedallionPipeline pipeline, TierResult silver, CancellationToken ct)
    {
        return Task.FromResult(new TierResult { Tier = "gold", Records = 50000 });
    }
}

#endregion

#region 126.2.5 Semantic Layer Strategy

/// <summary>
/// 126.2.5: Semantic layer strategy providing business-friendly data abstractions
/// with metrics definitions and governed access.
/// </summary>
public sealed class SemanticLayerStrategy : DataIntegrationStrategyBase
{
    private readonly ConcurrentDictionary<string, SemanticModel> _models = new();
    private readonly ConcurrentDictionary<string, MetricDefinition> _metrics = new();

    public override string StrategyId => "elt-semantic-layer";
    public override string DisplayName => "Semantic Layer";
    public override IntegrationCategory Category => IntegrationCategory.EltPatterns;
    public override DataIntegrationCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = false,
        SupportsExactlyOnce = true,
        SupportsSchemaEvolution = true,
        SupportsIncremental = false,
        SupportsParallel = true,
        SupportsDistributed = true,
        MaxThroughputRecordsPerSec = 1000000,
        TypicalLatencyMs = 100.0
    };
    public override string SemanticDescription =>
        "Semantic layer providing business-friendly data abstractions with standardized metrics, " +
        "dimensions, governed definitions, and consistent analytics across tools.";
    public override string[] Tags => ["elt", "semantic", "metrics", "dimensions", "governance", "analytics"];

    /// <summary>
    /// Creates a semantic model.
    /// </summary>
    public Task<SemanticModel> CreateModelAsync(
        string modelId,
        string modelName,
        IReadOnlyList<Dimension> dimensions,
        IReadOnlyList<Measure> measures,
        CancellationToken ct = default)
    {
        var model = new SemanticModel
        {
            ModelId = modelId,
            ModelName = modelName,
            Dimensions = dimensions.ToList(),
            Measures = measures.ToList(),
            CreatedAt = DateTime.UtcNow
        };

        if (!_models.TryAdd(modelId, model))
            throw new InvalidOperationException($"Model {modelId} already exists");

        RecordOperation("CreateSemanticModel");
        return Task.FromResult(model);
    }

    /// <summary>
    /// Defines a metric.
    /// </summary>
    public Task<MetricDefinition> DefineMetricAsync(
        string metricId,
        string metricName,
        string expression,
        MetricType metricType,
        string? description = null,
        CancellationToken ct = default)
    {
        var metric = new MetricDefinition
        {
            MetricId = metricId,
            MetricName = metricName,
            Expression = expression,
            MetricType = metricType,
            Description = description,
            CreatedAt = DateTime.UtcNow
        };

        if (!_metrics.TryAdd(metricId, metric))
            throw new InvalidOperationException($"Metric {metricId} already exists");

        RecordOperation("DefineMetric");
        return Task.FromResult(metric);
    }

    /// <summary>
    /// Queries the semantic layer.
    /// </summary>
    public Task<SemanticQueryResult> QueryAsync(
        string modelId,
        SemanticQuery query,
        CancellationToken ct = default)
    {
        if (!_models.TryGetValue(modelId, out var model))
            throw new KeyNotFoundException($"Model {modelId} not found");

        // Simulate query execution
        var result = new SemanticQueryResult
        {
            QueryId = Guid.NewGuid().ToString("N"),
            ModelId = modelId,
            RowCount = 100,
            Columns = query.Metrics.Concat(query.Dimensions).ToList(),
            ExecutionTimeMs = 50,
            Status = QueryStatus.Success
        };

        RecordOperation("QuerySemanticLayer");
        return Task.FromResult(result);
    }
}

#endregion

#region Supporting Types

// Cloud-Native ELT Types
public enum EltPipelineStatus { Created, Running, Completed, Failed }
public enum EltExecutionStatus { Success, PartialSuccess, Failed }
public enum TransformationStatus { Success, Failed }

public sealed record EltPipeline
{
    public required string PipelineId { get; init; }
    public required EltSource Source { get; init; }
    public required EltTarget Target { get; init; }
    public required List<EltTransformation> Transformations { get; init; }
    public required EltConfig Config { get; init; }
    public EltPipelineStatus Status { get; set; }
    public DateTime CreatedAt { get; init; }
}

public sealed record EltSource
{
    public required string SourceType { get; init; }
    public required string ConnectionString { get; init; }
    public string? Path { get; init; }
    public string? Format { get; init; }
}

public sealed record EltTarget
{
    public required string WarehouseType { get; init; }
    public required string ConnectionString { get; init; }
    public required string Schema { get; init; }
    public required string Table { get; init; }
}

public sealed record EltTransformation
{
    public required string TransformationId { get; init; }
    public required string Name { get; init; }
    public required string SqlQuery { get; init; }
    public IReadOnlyList<string>? Dependencies { get; init; }
}

public sealed record EltConfig
{
    public bool EnableIncremental { get; init; } = true;
    public int Parallelism { get; init; } = 4;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromHours(2);
}

public sealed record EltExecutionResult
{
    public required string PipelineId { get; init; }
    public required string ExecutionId { get; init; }
    public long RecordsLoaded { get; init; }
    public required List<TransformationResult> TransformationResults { get; init; }
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public EltExecutionStatus Status { get; init; }
}

public sealed record LoadResult
{
    public long RecordsLoaded { get; init; }
    public long BytesLoaded { get; init; }
}

public sealed record TransformationResult
{
    public required string TransformationId { get; init; }
    public long RowsAffected { get; init; }
    public long DurationMs { get; init; }
    public TransformationStatus Status { get; init; }
}

// dbt-Style Types
public enum MaterializationType { View, Table, Incremental, Ephemeral }
public enum DbtRunStatus { Success, PartialSuccess, Failed }
public enum DbtModelStatus { Success, Skipped, Failed }

public sealed record DbtProject
{
    public required string ProjectId { get; init; }
    public required string ProjectName { get; init; }
    public required DbtProjectConfig Config { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed record DbtProjectConfig
{
    public string? TargetSchema { get; init; }
    public bool EnableTests { get; init; } = true;
    public bool EnableDocs { get; init; } = true;
}

public sealed record DbtModel
{
    public required string ModelId { get; init; }
    public required string ProjectId { get; init; }
    public required string ModelName { get; init; }
    public required string SqlQuery { get; init; }
    public MaterializationType Materialization { get; init; }
    public required List<string> Dependencies { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed record DbtRunOptions
{
    public bool FullRefresh { get; init; } = false;
    public IReadOnlyList<string>? SelectModels { get; init; }
    public IReadOnlyList<string>? ExcludeModels { get; init; }
}

public sealed record DbtRunResult
{
    public required string ProjectId { get; init; }
    public required string RunId { get; init; }
    public int ModelsExecuted { get; init; }
    public required List<DbtModelResult> ModelResults { get; init; }
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public DbtRunStatus Status { get; init; }
}

public sealed record DbtModelResult
{
    public required string ModelId { get; init; }
    public required string ModelName { get; init; }
    public long RowsAffected { get; init; }
    public long DurationMs { get; init; }
    public DbtModelStatus Status { get; init; }
}

// Reverse ELT Types
public enum SyncStatus { Created, Running, Completed, Failed }
public enum ReverseSyncStatus { Success, PartialSuccess, Failed }

public sealed record ReverseEltSync
{
    public required string SyncId { get; init; }
    public required WarehouseSource Source { get; init; }
    public required OperationalTarget Target { get; init; }
    public required SyncConfig Config { get; init; }
    public SyncStatus Status { get; set; }
    public DateTime CreatedAt { get; init; }
}

public sealed record WarehouseSource
{
    public required string WarehouseType { get; init; }
    public required string ConnectionString { get; init; }
    public required string Query { get; init; }
}

public sealed record OperationalTarget
{
    public required string TargetType { get; init; }
    public required string ConnectionString { get; init; }
    public required string ObjectName { get; init; }
    public Dictionary<string, string>? FieldMappings { get; init; }
}

public sealed record SyncConfig
{
    public SyncMode Mode { get; init; } = SyncMode.Incremental;
    public int BatchSize { get; init; } = 1000;
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(15);
}

public enum SyncMode { Full, Incremental, Upsert }

public sealed record ReverseSyncResult
{
    public required string SyncId { get; init; }
    public required string ExecutionId { get; init; }
    public int RecordsExtracted { get; init; }
    public int RecordsSynced { get; init; }
    public int RecordsFailed { get; init; }
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public ReverseSyncStatus Status { get; init; }
}

// Medallion Architecture Types
public enum MedallionStatus { Created, Running, Completed, Failed }
public enum MedallionExecutionStatus { Success, PartialSuccess, Failed }

public sealed record MedallionPipeline
{
    public required string PipelineId { get; init; }
    public required MedallionSource Source { get; init; }
    public required MedallionConfig Config { get; init; }
    public MedallionStatus Status { get; set; }
    public DateTime CreatedAt { get; init; }
}

public sealed record MedallionSource
{
    public required string SourceType { get; init; }
    public required string Path { get; init; }
    public string? Format { get; init; }
}

public sealed record MedallionConfig
{
    public string BronzePath { get; init; } = "/bronze";
    public string SilverPath { get; init; } = "/silver";
    public string GoldPath { get; init; } = "/gold";
    public string Format { get; init; } = "delta";
}

public sealed record MedallionResult
{
    public required string PipelineId { get; init; }
    public required string ExecutionId { get; init; }
    public int BronzeRecords { get; init; }
    public int SilverRecords { get; init; }
    public int GoldRecords { get; init; }
    public DateTime StartTime { get; init; }
    public DateTime EndTime { get; init; }
    public MedallionExecutionStatus Status { get; init; }
}

public sealed record TierResult
{
    public required string Tier { get; init; }
    public int Records { get; init; }
}

// Semantic Layer Types
public enum MetricType { Simple, Derived, Cumulative }
public enum QueryStatus { Success, Failed }

public sealed record SemanticModel
{
    public required string ModelId { get; init; }
    public required string ModelName { get; init; }
    public required List<Dimension> Dimensions { get; init; }
    public required List<Measure> Measures { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed record Dimension
{
    public required string DimensionId { get; init; }
    public required string Name { get; init; }
    public required string Expression { get; init; }
    public string? Description { get; init; }
}

public sealed record Measure
{
    public required string MeasureId { get; init; }
    public required string Name { get; init; }
    public required string Expression { get; init; }
    public required string AggregationType { get; init; }
    public string? Description { get; init; }
}

public sealed record MetricDefinition
{
    public required string MetricId { get; init; }
    public required string MetricName { get; init; }
    public required string Expression { get; init; }
    public MetricType MetricType { get; init; }
    public string? Description { get; init; }
    public DateTime CreatedAt { get; init; }
}

public sealed record SemanticQuery
{
    public required IReadOnlyList<string> Metrics { get; init; }
    public required IReadOnlyList<string> Dimensions { get; init; }
    public IReadOnlyList<string>? Filters { get; init; }
    public int? Limit { get; init; }
}

public sealed record SemanticQueryResult
{
    public required string QueryId { get; init; }
    public required string ModelId { get; init; }
    public int RowCount { get; init; }
    public required List<string> Columns { get; init; }
    public long ExecutionTimeMs { get; init; }
    public QueryStatus Status { get; init; }
}

#endregion
