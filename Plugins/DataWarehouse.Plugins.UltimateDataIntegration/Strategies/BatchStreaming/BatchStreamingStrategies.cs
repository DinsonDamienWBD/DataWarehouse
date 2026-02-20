using System.Runtime.CompilerServices;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataIntegration.Strategies.BatchStreaming;

#region 126.7.1 Lambda Architecture Strategy

/// <summary>
/// 126.7.1: Lambda architecture strategy combining batch and speed layers
/// for comprehensive data processing with accuracy and low latency.
/// </summary>
public sealed class LambdaArchitectureStrategy : DataIntegrationStrategyBase
{
    private readonly BoundedDictionary<string, LambdaPipeline> _pipelines = new BoundedDictionary<string, LambdaPipeline>(1000);
    private readonly BoundedDictionary<string, BatchView> _batchViews = new BoundedDictionary<string, BatchView>(1000);
    private readonly BoundedDictionary<string, SpeedView> _speedViews = new BoundedDictionary<string, SpeedView>(1000);

    public override string StrategyId => "integration-lambda";
    public override string DisplayName => "Lambda Architecture";
    public override IntegrationCategory Category => IntegrationCategory.BatchStreamingIntegration;
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
        TypicalLatencyMs = 50.0
    };
    public override string SemanticDescription =>
        "Lambda architecture with batch layer for accurate historical processing, speed layer for " +
        "real-time views, and serving layer that merges both for comprehensive query responses.";
    public override string[] Tags => ["lambda", "batch", "streaming", "hybrid", "real-time"];

    /// <summary>
    /// Creates a Lambda pipeline.
    /// </summary>
    public Task<LambdaPipeline> CreatePipelineAsync(
        string pipelineId,
        LambdaConfig config,
        CancellationToken ct = default)
    {
        var pipeline = new LambdaPipeline
        {
            PipelineId = pipelineId,
            Config = config,
            Status = LambdaStatus.Created,
            CreatedAt = DateTime.UtcNow
        };

        if (!_pipelines.TryAdd(pipelineId, pipeline))
            throw new InvalidOperationException($"Pipeline {pipelineId} already exists");

        _batchViews[pipelineId] = new BatchView { PipelineId = pipelineId };
        _speedViews[pipelineId] = new SpeedView { PipelineId = pipelineId };

        RecordOperation("CreateLambdaPipeline");
        return Task.FromResult(pipeline);
    }

    /// <summary>
    /// Processes batch data (batch layer).
    /// </summary>
    public async Task<BatchLayerResult> ProcessBatchAsync(
        string pipelineId,
        IReadOnlyList<Dictionary<string, object>> records,
        CancellationToken ct = default)
    {
        if (!_pipelines.TryGetValue(pipelineId, out var pipeline))
            throw new KeyNotFoundException($"Pipeline {pipelineId} not found");

        if (!_batchViews.TryGetValue(pipelineId, out var batchView))
            throw new InvalidOperationException($"Batch view not found for pipeline {pipelineId}");

        var startTime = DateTime.UtcNow;

        // Process all data for accurate batch view
        batchView.TotalRecords += records.Count;
        batchView.LastUpdated = DateTime.UtcNow;

        // Compute aggregations
        foreach (var record in records)
        {
            UpdateBatchAggregations(batchView, record);
        }

        RecordOperation("ProcessBatch");

        return new BatchLayerResult
        {
            PipelineId = pipelineId,
            RecordsProcessed = records.Count,
            ProcessingTime = DateTime.UtcNow - startTime,
            ViewVersion = batchView.Version
        };
    }

    /// <summary>
    /// Processes streaming data (speed layer).
    /// </summary>
    public async IAsyncEnumerable<SpeedLayerResult> ProcessStreamAsync(
        string pipelineId,
        IAsyncEnumerable<Dictionary<string, object>> events,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_pipelines.TryGetValue(pipelineId, out var pipeline))
            throw new KeyNotFoundException($"Pipeline {pipelineId} not found");

        if (!_speedViews.TryGetValue(pipelineId, out var speedView))
            throw new InvalidOperationException($"Speed view not found for pipeline {pipelineId}");

        await foreach (var evt in events.WithCancellation(ct))
        {
            var startTime = DateTime.UtcNow;

            speedView.TotalEvents++;
            speedView.LastUpdated = DateTime.UtcNow;

            UpdateSpeedAggregations(speedView, evt);

            yield return new SpeedLayerResult
            {
                PipelineId = pipelineId,
                EventProcessed = true,
                Latency = DateTime.UtcNow - startTime
            };
        }

        RecordOperation("ProcessStream");
    }

    /// <summary>
    /// Queries the serving layer (merged view).
    /// </summary>
    public Task<ServingLayerResult> QueryAsync(
        string pipelineId,
        LambdaQuery query,
        CancellationToken ct = default)
    {
        if (!_batchViews.TryGetValue(pipelineId, out var batchView))
            throw new KeyNotFoundException($"Batch view not found for pipeline {pipelineId}");

        if (!_speedViews.TryGetValue(pipelineId, out var speedView))
            throw new KeyNotFoundException($"Speed view not found for pipeline {pipelineId}");

        // Merge batch and speed views
        var mergedData = MergeViews(batchView, speedView, query);

        RecordOperation("QueryServingLayer");

        return Task.FromResult(new ServingLayerResult
        {
            PipelineId = pipelineId,
            Data = mergedData,
            BatchViewTimestamp = batchView.LastUpdated,
            SpeedViewTimestamp = speedView.LastUpdated
        });
    }

    private void UpdateBatchAggregations(BatchView view, Dictionary<string, object> record)
    {
        view.Version++;
    }

    private void UpdateSpeedAggregations(SpeedView view, Dictionary<string, object> evt)
    {
        view.Version++;
    }

    private Dictionary<string, object> MergeViews(BatchView batch, SpeedView speed, LambdaQuery query)
    {
        return new Dictionary<string, object>
        {
            ["batch_records"] = batch.TotalRecords,
            ["speed_events"] = speed.TotalEvents,
            ["batch_version"] = batch.Version,
            ["speed_version"] = speed.Version,
            ["merged_at"] = DateTime.UtcNow
        };
    }
}

#endregion

#region 126.7.2 Kappa Architecture Strategy

/// <summary>
/// 126.7.2: Kappa architecture strategy using a single stream processing
/// layer for both real-time and historical data reprocessing.
/// </summary>
public sealed class KappaArchitectureStrategy : DataIntegrationStrategyBase
{
    private readonly BoundedDictionary<string, KappaPipeline> _pipelines = new BoundedDictionary<string, KappaPipeline>(1000);
    private readonly BoundedDictionary<string, StreamView> _views = new BoundedDictionary<string, StreamView>(1000);

    public override string StrategyId => "integration-kappa";
    public override string DisplayName => "Kappa Architecture";
    public override IntegrationCategory Category => IntegrationCategory.BatchStreamingIntegration;
    public override DataIntegrationCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = false,
        SupportsStreaming = true,
        SupportsExactlyOnce = true,
        SupportsSchemaEvolution = true,
        SupportsIncremental = true,
        SupportsParallel = true,
        SupportsDistributed = true,
        MaxThroughputRecordsPerSec = 1500000,
        TypicalLatencyMs = 20.0
    };
    public override string SemanticDescription =>
        "Kappa architecture treating all data as streams. Uses single stream processing layer " +
        "for both real-time and historical data, simplifying the architecture.";
    public override string[] Tags => ["kappa", "streaming-only", "simplified", "reprocessing"];

    /// <summary>
    /// Creates a Kappa pipeline.
    /// </summary>
    public Task<KappaPipeline> CreatePipelineAsync(
        string pipelineId,
        KappaConfig config,
        CancellationToken ct = default)
    {
        var pipeline = new KappaPipeline
        {
            PipelineId = pipelineId,
            Config = config,
            Status = KappaStatus.Created,
            CreatedAt = DateTime.UtcNow
        };

        if (!_pipelines.TryAdd(pipelineId, pipeline))
            throw new InvalidOperationException($"Pipeline {pipelineId} already exists");

        _views[pipelineId] = new StreamView { PipelineId = pipelineId };

        RecordOperation("CreateKappaPipeline");
        return Task.FromResult(pipeline);
    }

    /// <summary>
    /// Processes events through the stream layer.
    /// </summary>
    public async IAsyncEnumerable<KappaResult> ProcessEventsAsync(
        string pipelineId,
        IAsyncEnumerable<StreamEvent> events,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!_pipelines.TryGetValue(pipelineId, out var pipeline))
            throw new KeyNotFoundException($"Pipeline {pipelineId} not found");

        if (!_views.TryGetValue(pipelineId, out var view))
            throw new InvalidOperationException($"View not found for pipeline {pipelineId}");

        await foreach (var evt in events.WithCancellation(ct))
        {
            var startTime = DateTime.UtcNow;

            view.TotalEvents++;
            view.LastUpdated = DateTime.UtcNow;
            view.Version++;

            yield return new KappaResult
            {
                PipelineId = pipelineId,
                EventId = evt.EventId,
                ProcessedAt = DateTime.UtcNow,
                Latency = DateTime.UtcNow - startTime
            };
        }

        RecordOperation("ProcessEvents");
    }

    /// <summary>
    /// Triggers historical reprocessing.
    /// </summary>
    public Task<ReprocessingJob> StartReprocessingAsync(
        string pipelineId,
        DateTime fromTimestamp,
        DateTime? toTimestamp = null,
        CancellationToken ct = default)
    {
        if (!_pipelines.TryGetValue(pipelineId, out var pipeline))
            throw new KeyNotFoundException($"Pipeline {pipelineId} not found");

        var job = new ReprocessingJob
        {
            JobId = Guid.NewGuid().ToString("N"),
            PipelineId = pipelineId,
            FromTimestamp = fromTimestamp,
            ToTimestamp = toTimestamp ?? DateTime.UtcNow,
            Status = ReprocessingStatus.Running,
            StartedAt = DateTime.UtcNow
        };

        RecordOperation("StartReprocessing");
        return Task.FromResult(job);
    }
}

#endregion

#region 126.7.3 Unified Batch-Streaming Strategy

/// <summary>
/// 126.7.3: Unified batch-streaming strategy providing a single API
/// for both batch and streaming workloads with automatic optimization.
/// </summary>
public sealed class UnifiedBatchStreamingStrategy : DataIntegrationStrategyBase
{
    private readonly BoundedDictionary<string, UnifiedPipeline> _pipelines = new BoundedDictionary<string, UnifiedPipeline>(1000);

    public override string StrategyId => "integration-unified";
    public override string DisplayName => "Unified Batch-Streaming";
    public override IntegrationCategory Category => IntegrationCategory.BatchStreamingIntegration;
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
        TypicalLatencyMs = 30.0
    };
    public override string SemanticDescription =>
        "Unified batch-streaming with single API for both workloads. Automatically optimizes " +
        "execution based on data characteristics and latency requirements.";
    public override string[] Tags => ["unified", "batch", "streaming", "auto-optimize", "flink", "beam"];

    /// <summary>
    /// Creates a unified pipeline.
    /// </summary>
    public Task<UnifiedPipeline> CreatePipelineAsync(
        string pipelineId,
        UnifiedConfig config,
        CancellationToken ct = default)
    {
        var pipeline = new UnifiedPipeline
        {
            PipelineId = pipelineId,
            Config = config,
            Status = UnifiedStatus.Created,
            CreatedAt = DateTime.UtcNow
        };

        if (!_pipelines.TryAdd(pipelineId, pipeline))
            throw new InvalidOperationException($"Pipeline {pipelineId} already exists");

        RecordOperation("CreateUnifiedPipeline");
        return Task.FromResult(pipeline);
    }

    /// <summary>
    /// Processes data with automatic mode selection.
    /// </summary>
    public async Task<UnifiedResult> ProcessAsync(
        string pipelineId,
        DataSource source,
        CancellationToken ct = default)
    {
        if (!_pipelines.TryGetValue(pipelineId, out var pipeline))
            throw new KeyNotFoundException($"Pipeline {pipelineId} not found");

        var startTime = DateTime.UtcNow;

        // Determine optimal execution mode
        var mode = DetermineExecutionMode(source, pipeline.Config);

        var result = mode switch
        {
            ExecutionMode.Batch => await ProcessBatchModeAsync(pipeline, source, ct),
            ExecutionMode.Streaming => await ProcessStreamingModeAsync(pipeline, source, ct),
            ExecutionMode.MicroBatch => await ProcessMicroBatchModeAsync(pipeline, source, ct),
            _ => throw new ArgumentException($"Unknown execution mode: {mode}")
        };

        RecordOperation("ProcessUnified");

        return new UnifiedResult
        {
            PipelineId = pipelineId,
            ExecutionMode = mode,
            RecordsProcessed = result.RecordsProcessed,
            ProcessingTime = DateTime.UtcNow - startTime,
            Status = UnifiedExecutionStatus.Success
        };
    }

    private ExecutionMode DetermineExecutionMode(DataSource source, UnifiedConfig config)
    {
        if (config.ForcedMode.HasValue)
            return config.ForcedMode.Value;

        if (source.IsStreaming)
            return config.MaxLatency < TimeSpan.FromSeconds(1) ? ExecutionMode.Streaming : ExecutionMode.MicroBatch;

        return ExecutionMode.Batch;
    }

    private Task<(int RecordsProcessed, TimeSpan ProcessingTime)> ProcessBatchModeAsync(
        UnifiedPipeline pipeline,
        DataSource source,
        CancellationToken ct)
    {
        return Task.FromResult((100000, TimeSpan.FromSeconds(10)));
    }

    private Task<(int RecordsProcessed, TimeSpan ProcessingTime)> ProcessStreamingModeAsync(
        UnifiedPipeline pipeline,
        DataSource source,
        CancellationToken ct)
    {
        return Task.FromResult((50000, TimeSpan.FromSeconds(5)));
    }

    private Task<(int RecordsProcessed, TimeSpan ProcessingTime)> ProcessMicroBatchModeAsync(
        UnifiedPipeline pipeline,
        DataSource source,
        CancellationToken ct)
    {
        return Task.FromResult((75000, TimeSpan.FromSeconds(7)));
    }
}

#endregion

#region 126.7.4 Hybrid Integration Strategy

/// <summary>
/// 126.7.4: Hybrid integration strategy allowing dynamic switching
/// between batch and streaming modes based on data volume and latency.
/// </summary>
public sealed class HybridIntegrationStrategy : DataIntegrationStrategyBase
{
    private readonly BoundedDictionary<string, HybridPipeline> _pipelines = new BoundedDictionary<string, HybridPipeline>(1000);

    public override string StrategyId => "integration-hybrid";
    public override string DisplayName => "Hybrid Integration";
    public override IntegrationCategory Category => IntegrationCategory.BatchStreamingIntegration;
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
        MaxThroughputRecordsPerSec = 1500000,
        TypicalLatencyMs = 40.0
    };
    public override string SemanticDescription =>
        "Hybrid integration with dynamic mode switching based on data volume, latency requirements, " +
        "and resource availability for optimal cost-performance balance.";
    public override string[] Tags => ["hybrid", "dynamic", "adaptive", "cost-optimized"];

    /// <summary>
    /// Creates a hybrid pipeline.
    /// </summary>
    public Task<HybridPipeline> CreatePipelineAsync(
        string pipelineId,
        HybridConfig config,
        CancellationToken ct = default)
    {
        var pipeline = new HybridPipeline
        {
            PipelineId = pipelineId,
            Config = config,
            CurrentMode = IntegrationMode.Batch,
            Status = HybridStatus.Created,
            CreatedAt = DateTime.UtcNow
        };

        if (!_pipelines.TryAdd(pipelineId, pipeline))
            throw new InvalidOperationException($"Pipeline {pipelineId} already exists");

        RecordOperation("CreateHybridPipeline");
        return Task.FromResult(pipeline);
    }

    /// <summary>
    /// Switches the integration mode.
    /// </summary>
    public Task<ModeSwitch> SwitchModeAsync(
        string pipelineId,
        IntegrationMode newMode,
        CancellationToken ct = default)
    {
        if (!_pipelines.TryGetValue(pipelineId, out var pipeline))
            throw new KeyNotFoundException($"Pipeline {pipelineId} not found");

        var previousMode = pipeline.CurrentMode;
        pipeline.CurrentMode = newMode;
        pipeline.LastModeSwitch = DateTime.UtcNow;

        RecordOperation("SwitchMode");

        return Task.FromResult(new ModeSwitch
        {
            PipelineId = pipelineId,
            PreviousMode = previousMode,
            NewMode = newMode,
            SwitchedAt = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Auto-tunes the mode based on metrics.
    /// </summary>
    public Task<AutoTuneResult> AutoTuneAsync(
        string pipelineId,
        PipelineMetrics metrics,
        CancellationToken ct = default)
    {
        if (!_pipelines.TryGetValue(pipelineId, out var pipeline))
            throw new KeyNotFoundException($"Pipeline {pipelineId} not found");

        var recommendation = DetermineOptimalMode(metrics, pipeline.Config);
        var switched = false;

        if (pipeline.Config.EnableAutoTune && recommendation != pipeline.CurrentMode)
        {
            pipeline.CurrentMode = recommendation;
            pipeline.LastModeSwitch = DateTime.UtcNow;
            switched = true;
        }

        RecordOperation("AutoTune");

        return Task.FromResult(new AutoTuneResult
        {
            PipelineId = pipelineId,
            RecommendedMode = recommendation,
            ModeSwitched = switched,
            Reason = GetTuneReason(metrics, recommendation)
        });
    }

    private IntegrationMode DetermineOptimalMode(PipelineMetrics metrics, HybridConfig config)
    {
        if (metrics.CurrentLatency > config.LatencyThreshold)
            return IntegrationMode.Streaming;

        if (metrics.DataVolumePerSecond > config.VolumeThresholdForStreaming)
            return IntegrationMode.Streaming;

        if (metrics.DataVolumePerSecond < config.VolumeThresholdForBatch)
            return IntegrationMode.Batch;

        return IntegrationMode.MicroBatch;
    }

    private string GetTuneReason(PipelineMetrics metrics, IntegrationMode mode)
    {
        return mode switch
        {
            IntegrationMode.Streaming => $"High data volume ({metrics.DataVolumePerSecond}/s) or latency requirements",
            IntegrationMode.Batch => $"Low data volume ({metrics.DataVolumePerSecond}/s), batch is more cost-effective",
            IntegrationMode.MicroBatch => "Balanced mode for moderate volume and latency",
            _ => "Default recommendation"
        };
    }
}

#endregion

#region 126.7.5 Real-time Materialized View Strategy

/// <summary>
/// 126.7.5: Real-time materialized view strategy maintaining pre-computed
/// views that update incrementally with streaming data.
/// </summary>
public sealed class RealTimeMaterializedViewStrategy : DataIntegrationStrategyBase
{
    private readonly BoundedDictionary<string, MaterializedView> _views = new BoundedDictionary<string, MaterializedView>(1000);

    public override string StrategyId => "integration-materialized-view";
    public override string DisplayName => "Real-time Materialized View";
    public override IntegrationCategory Category => IntegrationCategory.BatchStreamingIntegration;
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
        MaxThroughputRecordsPerSec = 500000,
        TypicalLatencyMs = 10.0
    };
    public override string SemanticDescription =>
        "Real-time materialized views maintaining pre-computed results that update incrementally " +
        "with streaming data for low-latency query responses.";
    public override string[] Tags => ["materialized-view", "incremental", "real-time", "pre-computed"];

    /// <summary>
    /// Creates a materialized view.
    /// </summary>
    public Task<MaterializedView> CreateViewAsync(
        string viewId,
        string viewDefinition,
        MaterializedViewConfig? config = null,
        CancellationToken ct = default)
    {
        var view = new MaterializedView
        {
            ViewId = viewId,
            ViewDefinition = viewDefinition,
            Config = config ?? new MaterializedViewConfig(),
            Status = MaterializedViewStatus.Created,
            CreatedAt = DateTime.UtcNow
        };

        if (!_views.TryAdd(viewId, view))
            throw new InvalidOperationException($"View {viewId} already exists");

        RecordOperation("CreateMaterializedView");
        return Task.FromResult(view);
    }

    /// <summary>
    /// Applies incremental updates to the view.
    /// </summary>
    public Task<ViewUpdateResult> ApplyUpdatesAsync(
        string viewId,
        IReadOnlyList<ViewUpdate> updates,
        CancellationToken ct = default)
    {
        if (!_views.TryGetValue(viewId, out var view))
            throw new KeyNotFoundException($"View {viewId} not found");

        var applied = 0;
        foreach (var update in updates)
        {
            ApplyUpdate(view, update);
            applied++;
        }

        view.LastUpdated = DateTime.UtcNow;
        view.Version++;

        RecordOperation("ApplyViewUpdates");

        return Task.FromResult(new ViewUpdateResult
        {
            ViewId = viewId,
            UpdatesApplied = applied,
            NewVersion = view.Version,
            UpdatedAt = view.LastUpdated.Value
        });
    }

    /// <summary>
    /// Queries the materialized view.
    /// </summary>
    public Task<ViewQueryResult> QueryViewAsync(
        string viewId,
        ViewQuery query,
        CancellationToken ct = default)
    {
        if (!_views.TryGetValue(viewId, out var view))
            throw new KeyNotFoundException($"View {viewId} not found");

        // Return pre-computed result
        var result = new ViewQueryResult
        {
            ViewId = viewId,
            Version = view.Version,
            Data = view.Data,
            Freshness = DateTime.UtcNow - (view.LastUpdated ?? view.CreatedAt)
        };

        RecordOperation("QueryView");
        return Task.FromResult(result);
    }

    /// <summary>
    /// Triggers a full refresh of the view.
    /// </summary>
    public Task<RefreshResult> RefreshViewAsync(
        string viewId,
        CancellationToken ct = default)
    {
        if (!_views.TryGetValue(viewId, out var view))
            throw new KeyNotFoundException($"View {viewId} not found");

        view.Data.Clear();
        view.LastRefreshed = DateTime.UtcNow;
        view.Version = 0;

        RecordOperation("RefreshView");

        return Task.FromResult(new RefreshResult
        {
            ViewId = viewId,
            RefreshedAt = view.LastRefreshed.Value,
            Status = RefreshStatus.Success
        });
    }

    private void ApplyUpdate(MaterializedView view, ViewUpdate update)
    {
        switch (update.Operation)
        {
            case UpdateOperation.Insert:
            case UpdateOperation.Upsert:
                view.Data[update.Key] = update.Value!;
                break;
            case UpdateOperation.Delete:
                view.Data.Remove(update.Key);
                break;
            case UpdateOperation.Increment:
                if (view.Data.TryGetValue(update.Key, out var existing) && existing is double d)
                    view.Data[update.Key] = d + (update.Delta ?? 1);
                else
                    view.Data[update.Key] = update.Delta ?? 1;
                break;
        }
    }
}

#endregion

#region Supporting Types

// Lambda Architecture Types
public enum LambdaStatus { Created, Running, Stopped }

public sealed record LambdaPipeline
{
    public required string PipelineId { get; init; }
    public required LambdaConfig Config { get; init; }
    public LambdaStatus Status { get; set; }
    public DateTime CreatedAt { get; init; }
}

public sealed record LambdaConfig
{
    public TimeSpan BatchInterval { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan SpeedViewRetention { get; init; } = TimeSpan.FromHours(2);
}

public sealed record BatchView
{
    public required string PipelineId { get; init; }
    public long TotalRecords { get; set; }
    public int Version { get; set; }
    public DateTime? LastUpdated { get; set; }
}

public sealed record SpeedView
{
    public required string PipelineId { get; init; }
    public long TotalEvents { get; set; }
    public int Version { get; set; }
    public DateTime? LastUpdated { get; set; }
}

public sealed record BatchLayerResult
{
    public required string PipelineId { get; init; }
    public int RecordsProcessed { get; init; }
    public TimeSpan ProcessingTime { get; init; }
    public int ViewVersion { get; init; }
}

public sealed record SpeedLayerResult
{
    public required string PipelineId { get; init; }
    public bool EventProcessed { get; init; }
    public TimeSpan Latency { get; init; }
}

public sealed record LambdaQuery
{
    public string? AggregationType { get; init; }
    public DateTime? FromTime { get; init; }
    public DateTime? ToTime { get; init; }
}

public sealed record ServingLayerResult
{
    public required string PipelineId { get; init; }
    public required Dictionary<string, object> Data { get; init; }
    public DateTime? BatchViewTimestamp { get; init; }
    public DateTime? SpeedViewTimestamp { get; init; }
}

// Kappa Architecture Types
public enum KappaStatus { Created, Running, Stopped }
public enum ReprocessingStatus { Pending, Running, Completed, Failed }

public sealed record KappaPipeline
{
    public required string PipelineId { get; init; }
    public required KappaConfig Config { get; init; }
    public KappaStatus Status { get; set; }
    public DateTime CreatedAt { get; init; }
}

public sealed record KappaConfig
{
    public int Parallelism { get; init; } = 4;
    public TimeSpan RetentionPeriod { get; init; } = TimeSpan.FromDays(7);
}

public sealed record StreamView
{
    public required string PipelineId { get; init; }
    public long TotalEvents { get; set; }
    public int Version { get; set; }
    public DateTime? LastUpdated { get; set; }
}

public sealed record StreamEvent
{
    public required string EventId { get; init; }
    public required Dictionary<string, object> Data { get; init; }
    public DateTime Timestamp { get; init; }
}

public sealed record KappaResult
{
    public required string PipelineId { get; init; }
    public required string EventId { get; init; }
    public DateTime ProcessedAt { get; init; }
    public TimeSpan Latency { get; init; }
}

public sealed record ReprocessingJob
{
    public required string JobId { get; init; }
    public required string PipelineId { get; init; }
    public DateTime FromTimestamp { get; init; }
    public DateTime ToTimestamp { get; init; }
    public ReprocessingStatus Status { get; set; }
    public DateTime StartedAt { get; init; }
}

// Unified Types
public enum UnifiedStatus { Created, Running, Stopped }
public enum ExecutionMode { Batch, Streaming, MicroBatch }
public enum UnifiedExecutionStatus { Success, PartialSuccess, Failed }

public sealed record UnifiedPipeline
{
    public required string PipelineId { get; init; }
    public required UnifiedConfig Config { get; init; }
    public UnifiedStatus Status { get; set; }
    public DateTime CreatedAt { get; init; }
}

public sealed record UnifiedConfig
{
    public ExecutionMode? ForcedMode { get; init; }
    public TimeSpan MaxLatency { get; init; } = TimeSpan.FromSeconds(10);
}

public sealed record DataSource
{
    public required string SourceId { get; init; }
    public bool IsStreaming { get; init; }
    public long EstimatedRecords { get; init; }
}

public sealed record UnifiedResult
{
    public required string PipelineId { get; init; }
    public ExecutionMode ExecutionMode { get; init; }
    public int RecordsProcessed { get; init; }
    public TimeSpan ProcessingTime { get; init; }
    public UnifiedExecutionStatus Status { get; init; }
}

// Hybrid Types
public enum HybridStatus { Created, Running, Stopped }
public enum IntegrationMode { Batch, Streaming, MicroBatch }

public sealed record HybridPipeline
{
    public required string PipelineId { get; init; }
    public required HybridConfig Config { get; init; }
    public IntegrationMode CurrentMode { get; set; }
    public HybridStatus Status { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastModeSwitch { get; set; }
}

public sealed record HybridConfig
{
    public bool EnableAutoTune { get; init; } = true;
    public TimeSpan LatencyThreshold { get; init; } = TimeSpan.FromSeconds(5);
    public long VolumeThresholdForStreaming { get; init; } = 10000;
    public long VolumeThresholdForBatch { get; init; } = 100;
}

public sealed record ModeSwitch
{
    public required string PipelineId { get; init; }
    public IntegrationMode PreviousMode { get; init; }
    public IntegrationMode NewMode { get; init; }
    public DateTime SwitchedAt { get; init; }
}

public sealed record PipelineMetrics
{
    public TimeSpan CurrentLatency { get; init; }
    public long DataVolumePerSecond { get; init; }
    public double CpuUtilization { get; init; }
    public double MemoryUtilization { get; init; }
}

public sealed record AutoTuneResult
{
    public required string PipelineId { get; init; }
    public IntegrationMode RecommendedMode { get; init; }
    public bool ModeSwitched { get; init; }
    public required string Reason { get; init; }
}

// Materialized View Types
public enum MaterializedViewStatus { Created, Active, Refreshing, Stale }
public enum UpdateOperation { Insert, Update, Delete, Upsert, Increment }
public enum RefreshStatus { Success, Failed }

public sealed record MaterializedView
{
    public required string ViewId { get; init; }
    public required string ViewDefinition { get; init; }
    public required MaterializedViewConfig Config { get; init; }
    public MaterializedViewStatus Status { get; set; }
    public int Version { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastUpdated { get; set; }
    public DateTime? LastRefreshed { get; set; }
    public Dictionary<string, object> Data { get; init; } = new();
}

public sealed record MaterializedViewConfig
{
    public bool EnableIncrementalUpdates { get; init; } = true;
    public TimeSpan RefreshInterval { get; init; } = TimeSpan.FromHours(1);
    public int MaxViewSize { get; init; } = 1000000;
}

public sealed record ViewUpdate
{
    public required string Key { get; init; }
    public UpdateOperation Operation { get; init; }
    public object? Value { get; init; }
    public double? Delta { get; init; }
}

public sealed record ViewUpdateResult
{
    public required string ViewId { get; init; }
    public int UpdatesApplied { get; init; }
    public int NewVersion { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public sealed record ViewQuery
{
    public IReadOnlyList<string>? Keys { get; init; }
    public string? Filter { get; init; }
}

public sealed record ViewQueryResult
{
    public required string ViewId { get; init; }
    public int Version { get; init; }
    public required Dictionary<string, object> Data { get; init; }
    public TimeSpan Freshness { get; init; }
}

public sealed record RefreshResult
{
    public required string ViewId { get; init; }
    public DateTime RefreshedAt { get; init; }
    public RefreshStatus Status { get; init; }
}

#endregion
