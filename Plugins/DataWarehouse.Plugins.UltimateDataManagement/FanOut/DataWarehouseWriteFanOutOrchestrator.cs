using System.Diagnostics;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.Plugins.UltimateDataManagement.Strategies.Caching;
using DataWarehouse.Plugins.UltimateDataManagement.Strategies.Indexing;

namespace DataWarehouse.Plugins.UltimateDataManagement.FanOut;

/// <summary>
/// Deployment mode for the fan-out orchestrator instance.
/// </summary>
public enum FanOutDeploymentMode
{
    /// <summary>Standard mode with configurable strategies at any hierarchy level.</summary>
    Standard,

    /// <summary>TamperProof mode with locked strategy for high-integrity instances.</summary>
    TamperProof,

    /// <summary>Custom mode with user-defined strategy combinations.</summary>
    Custom
}

/// <summary>
/// DataWarehouse implementation of Write Fan-Out Orchestrator.
/// Multi-instance, strategy-based plugin supporting TamperProof, Standard, and Custom modes.
/// </summary>
/// <remarks>
/// <b>Multi-Instance Design:</b>
/// Same plugin codebase supports different deployment modes:
/// <list type="bullet">
///   <item><b>TamperProof:</b> Strategy locked at instance deployment, cannot be changed</item>
///   <item><b>Standard:</b> Strategy configurable at any 4-tier hierarchy level</item>
///   <item><b>Custom:</b> Fully user-defined destination combinations</item>
/// </list>
///
/// <b>Features:</b>
/// - Strategy-based write coordination
/// - Parallel writes to all enabled destinations
/// - Required vs optional destination handling
/// - Configurable timeouts and success criteria
/// - Content processing pipeline integration
/// - Comprehensive write statistics
/// - Soft dependency integration via message bus
/// </remarks>
public sealed class DataWarehouseWriteFanOutOrchestrator : WriteFanOutOrchestratorPluginBase
{
    private readonly BoundedDictionary<string, long> _writeStats = new BoundedDictionary<string, long>(1000);
    private readonly BoundedDictionary<string, IFanOutStrategy> _strategies = new BoundedDictionary<string, IFanOutStrategy>(1000);
    private readonly List<IContentProcessor> _contentProcessors = new();
    private readonly object _processorLock = new();
    private IMessageBus? _messageBus;
    private IFanOutStrategy _activeStrategy;
    private FanOutDeploymentMode _deploymentMode;
    private bool _initialized;
    private bool _isLocked;

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.fanout.orchestrator";

    /// <inheritdoc/>
    public override string Name => "DataWarehouse Fan-Out Orchestrator";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <summary>
    /// Gets the current deployment mode.
    /// </summary>
    public FanOutDeploymentMode DeploymentMode => _deploymentMode;

    /// <summary>
    /// Gets whether the strategy configuration is locked.
    /// </summary>
    public bool IsLocked => _isLocked;

    /// <summary>
    /// Gets the active fan-out strategy.
    /// </summary>
    public IFanOutStrategy ActiveStrategy => _activeStrategy;

    /// <summary>
    /// Semantic description for AI discovery.
    /// </summary>
    public string SemanticDescription =>
        "Multi-instance fan-out orchestrator supporting TamperProof, Standard, and Custom modes. " +
        "Coordinates parallel writes to primary storage, metadata, full-text index, vector store, " +
        "blockchain anchor, WORM storage, and cache layers with configurable success criteria.";

    /// <summary>
    /// Initializes the orchestrator with default Standard mode.
    /// </summary>
    public DataWarehouseWriteFanOutOrchestrator()
    {
        _activeStrategy = new StandardFanOutStrategy();
        _deploymentMode = FanOutDeploymentMode.Standard;
        _isLocked = false;

        // Register built-in strategies
        _strategies["TamperProof"] = new TamperProofFanOutStrategy();
        _strategies["Standard"] = _activeStrategy;
    }

    /// <summary>
    /// Sets the message bus for inter-plugin communication.
    /// </summary>
    public new void SetMessageBus(IMessageBus? messageBus)
    {
        _messageBus = messageBus;
    }

    /// <summary>
    /// Configures the orchestrator for TamperProof mode.
    /// This LOCKS the configuration and cannot be undone.
    /// </summary>
    /// <returns>True if successfully configured, false if already locked.</returns>
    public bool ConfigureAsTamperProof()
    {
        if (_isLocked)
        {
            return false;
        }

        _activeStrategy = _strategies["TamperProof"];
        _deploymentMode = FanOutDeploymentMode.TamperProof;
        _isLocked = true;

        return true;
    }

    /// <summary>
    /// Configures the orchestrator for Standard mode with the specified configuration.
    /// </summary>
    /// <param name="configuration">Standard strategy configuration.</param>
    /// <returns>True if successfully configured, false if locked.</returns>
    public bool ConfigureAsStandard(StandardFanOutConfiguration? configuration = null)
    {
        if (_isLocked)
        {
            return false;
        }

        _activeStrategy = configuration != null
            ? new StandardFanOutStrategy(configuration)
            : new StandardFanOutStrategy();

        _strategies["Standard"] = _activeStrategy;
        _deploymentMode = FanOutDeploymentMode.Standard;

        return true;
    }

    /// <summary>
    /// Configures the orchestrator with a custom strategy.
    /// </summary>
    /// <param name="strategy">Custom strategy to use.</param>
    /// <returns>True if successfully configured, false if locked.</returns>
    public bool ConfigureWithCustomStrategy(IFanOutStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);

        if (_isLocked)
        {
            return false;
        }

        _strategies[strategy.StrategyId] = strategy;
        _activeStrategy = strategy;
        _deploymentMode = FanOutDeploymentMode.Custom;

        if (strategy.IsLocked)
        {
            _isLocked = true;
        }

        return true;
    }

    /// <summary>
    /// Applies a configuration override at the specified hierarchy level.
    /// Only works in Standard mode when AllowChildOverride is true.
    /// </summary>
    /// <param name="configuration">Override configuration.</param>
    /// <returns>True if override applied, false otherwise.</returns>
    public bool ApplyConfigurationOverride(StandardFanOutConfiguration configuration)
    {
        if (_isLocked || _deploymentMode != FanOutDeploymentMode.Standard)
        {
            return false;
        }

        if (_activeStrategy is StandardFanOutStrategy standardStrategy && standardStrategy.AllowChildOverride)
        {
            _activeStrategy = standardStrategy.WithOverride(configuration);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Registers a custom strategy that can be selected later.
    /// </summary>
    /// <param name="strategy">Strategy to register.</param>
    public void RegisterStrategy(IFanOutStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(strategy);
        _strategies[strategy.StrategyId] = strategy;
    }

    /// <summary>
    /// Gets all registered strategies.
    /// </summary>
    public IReadOnlyDictionary<string, IFanOutStrategy> GetStrategies()
    {
        return new Dictionary<string, IFanOutStrategy>(_strategies);
    }

    /// <summary>
    /// Registers a content processor for the write pipeline.
    /// </summary>
    public void RegisterContentProcessor(IContentProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(processor);
        lock (_processorLock)
        {
            _contentProcessors.Add(processor);
        }
    }

    /// <inheritdoc/>
    public override async Task StartAsync(CancellationToken ct)
    {
        if (_initialized) return;

        // Auto-register default destinations if none registered
        if (GetDestinations().Count == 0)
        {
            await RegisterDefaultDestinationsAsync();
        }

        // Validate active strategy has required destinations BEFORE calling base.StartAsync
        // to avoid leaving partially-started state on validation failure.
        var validation = _activeStrategy.ValidateDestinations(
            GetDestinations().Select(d => d.DestinationType));

        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                $"Strategy '{_activeStrategy.StrategyId}' validation failed: " +
                string.Join("; ", validation.Errors));
        }

        await base.StartAsync(ct);

        _initialized = true;
    }

    /// <inheritdoc/>
    public override Task StopAsync()
    {
        _writeStats.Clear();
        _initialized = false;
        return base.StopAsync();
    }

    /// <inheritdoc/>
    protected override async Task<Dictionary<ContentProcessingType, ContentProcessingResult>> ProcessContentAsync(
        Stream data,
        Manifest manifest,
        FanOutWriteOptions options,
        CancellationToken ct)
    {
        var results = new Dictionary<ContentProcessingType, ContentProcessingResult>();

        // Read data for processing
        byte[] dataBytes;
        if (data.CanSeek)
        {
            data.Position = 0;
        }

        using (var ms = new MemoryStream(65536))
        {
            await data.CopyToAsync(ms, ct);
            dataBytes = ms.ToArray();
        }

        // Take a snapshot of processors to avoid holding the lock during async work.
        List<IContentProcessor> processorSnapshot;
        lock (_processorLock)
        {
            processorSnapshot = new List<IContentProcessor>(_contentProcessors);
        }

        // Process through each processor
        foreach (var processingType in options.ProcessingTypes)
        {
            ct.ThrowIfCancellationRequested();

            var processor = processorSnapshot.FirstOrDefault(p => p.ProcessingType == processingType);
            if (processor != null)
            {
                using var processingStream = new MemoryStream(dataBytes);
                var result = await processor.ProcessAsync(
                    processingStream,
                    manifest.ContentType ?? "application/octet-stream",
                    manifest.Metadata?.ToDictionary(kvp => kvp.Key, kvp => (object)kvp.Value),
                    ct);
                results[processingType] = result;
            }
            else if (_messageBus != null)
            {
                // Try via message bus
                var result = await RequestContentProcessingAsync(processingType, dataBytes, manifest, ct);
                if (result != null)
                {
                    results[processingType] = result;
                }
            }
        }

        return results;
    }

    /// <inheritdoc/>
    protected override IndexableContent CreateIndexableContent(
        string objectId,
        Manifest manifest,
        Dictionary<ContentProcessingType, ContentProcessingResult> processingResults)
    {
        var content = base.CreateIndexableContent(objectId, manifest, processingResults);

        // Enrich with additional processing results
        var metadata = new Dictionary<string, object>(content.Metadata ?? new());

        // Add classification if available
        if (processingResults.TryGetValue(ContentProcessingType.Classification, out var classResult) &&
            classResult.Classification != null)
        {
            metadata["_classification"] = classResult.Classification.Category;
            metadata["_classificationConfidence"] = classResult.Classification.Confidence;
            if (classResult.Classification.Tags != null)
            {
                metadata["_classificationTags"] = classResult.Classification.Tags;
            }
        }

        // Add entities if available
        if (processingResults.TryGetValue(ContentProcessingType.EntityExtraction, out var entityResult) &&
            entityResult.Entities != null)
        {
            metadata["_entities"] = entityResult.Entities
                .GroupBy(e => e.Type)
                .ToDictionary(g => g.Key, g => g.Select(e => e.Value).ToArray());
        }

        return new IndexableContent
        {
            ObjectId = content.ObjectId,
            Filename = content.Filename,
            ContentType = content.ContentType,
            Size = content.Size,
            TextContent = content.TextContent,
            Embeddings = content.Embeddings,
            Summary = content.Summary,
            Metadata = metadata
        };
    }

    /// <summary>
    /// Gets write statistics by destination type.
    /// </summary>
    public IReadOnlyDictionary<string, long> GetWriteStats()
    {
        return new Dictionary<string, long>(_writeStats);
    }

    private async Task RegisterDefaultDestinationsAsync()
    {
        // Register primary storage destination (via message bus)
        RegisterDestination(new PrimaryStorageDestination(_messageBus));

        // Register metadata storage destination (via message bus)
        RegisterDestination(new MetadataStorageDestination(_messageBus));

        // Register text index destination
        var fullTextIndex = new FullTextIndexStrategy();
        await fullTextIndex.InitializeAsync();
        RegisterDestination(new TextIndexDestination(fullTextIndex));

        // Register vector store destination
        var semanticIndex = new SemanticIndexStrategy();
        semanticIndex.SetMessageBus(_messageBus);
        await semanticIndex.InitializeAsync();
        RegisterDestination(new VectorStoreDestination(semanticIndex, _messageBus));

        // Register cache destination
        var cache = new InMemoryCacheStrategy();
        await cache.InitializeAsync();
        RegisterDestination(new CacheDestination(cache));

        // Register TamperProof-specific destinations
        if (_deploymentMode == FanOutDeploymentMode.TamperProof)
        {
            RegisterTamperProofDestinations();
        }

        // Register audit log destination (optional for all modes)
        RegisterDestination(new AuditLogDestination(_messageBus));
    }

    private void RegisterTamperProofDestinations()
    {
        // Blockchain anchor for tamper evidence
        RegisterDestination(new BlockchainAnchorDestination(_messageBus));

        // WORM storage for immutable finalization
        RegisterDestination(new WormStorageDestination(_messageBus));
    }

    /// <summary>
    /// Executes a fan-out write using the active strategy.
    /// </summary>
    /// <param name="objectId">Object identifier.</param>
    /// <param name="content">Content to write.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Strategy execution result.</returns>
    public async Task<FanOutStrategyResult> ExecuteStrategyWriteAsync(
        string objectId,
        IndexableContent content,
        CancellationToken ct = default)
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("Orchestrator not initialized. Call StartAsync first.");
        }

        // Build destination map
        var destinations = GetDestinations()
            .ToDictionary(d => d.DestinationType, d => d);

        // Execute via active strategy
        var result = await _activeStrategy.ExecuteAsync(objectId, content, destinations, ct);

        // Update statistics
        foreach (var (type, destResult) in result.DestinationResults)
        {
            var key = $"{type}:{(destResult.Success ? "success" : "failure")}";
            _writeStats.AddOrUpdate(key, 1, (_, count) => count + 1);
        }

        return result;
    }

    private async Task<ContentProcessingResult?> RequestContentProcessingAsync(
        ContentProcessingType processingType,
        byte[] data,
        Manifest manifest,
        CancellationToken ct)
    {
        if (_messageBus == null)
            return null;

        var messageType = processingType switch
        {
            ContentProcessingType.TextExtraction => "content.extract.text",
            ContentProcessingType.EmbeddingGeneration => "ai.embedding.generate",
            ContentProcessingType.Summarization => "ai.summarize",
            ContentProcessingType.MetadataExtraction => "content.extract.metadata",
            ContentProcessingType.Classification => "ai.classify",
            ContentProcessingType.EntityExtraction => "ai.extract.entities",
            _ => null
        };

        if (messageType == null)
            return null;

        try
        {
            var msgResponse = await _messageBus.SendAsync(
                messageType,
                new PluginMessage
                {
                    Type = messageType,
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["data"] = Convert.ToBase64String(data),
                        ["contentType"] = manifest.ContentType ?? "application/octet-stream",
                        ["filename"] = manifest.Name ?? ""
                    }
                },
                TimeSpan.FromSeconds(30),
                ct);

            if (msgResponse?.Success == true && msgResponse.Payload is Dictionary<string, object> payload)
            {
                return new ContentProcessingResult
                {
                    Success = true,
                    ProcessingType = processingType,
                    ExtractedText = payload.TryGetValue("ExtractedText", out var txt) ? txt as string : null,
                    Embeddings = payload.TryGetValue("Embeddings", out var emb) ? emb as float[] : null,
                    Summary = payload.TryGetValue("Summary", out var sum) ? sum as string : null,
                    Metadata = payload.TryGetValue("Metadata", out var meta) ? meta as Dictionary<string, object> : null,
                    Classification = payload.TryGetValue("Classification", out var cls) ? cls as ContentClassification : null,
                    Entities = payload.TryGetValue("Entities", out var ent) ? ent as IReadOnlyList<ExtractedEntity> : null
                };
            }
        }
        catch (Exception ex)
        {
            // Content processing service unavailable; non-fatal.
            System.Diagnostics.Debug.WriteLine($"[FanOut] Content processing via message bus failed for {processingType}: {ex.Message}");
        }

        return null;
    }

    private sealed class ContentProcessingResponse
    {
        public string? ExtractedText { get; init; }
        public float[]? Embeddings { get; init; }
        public string? Summary { get; init; }
        public Dictionary<string, object>? Metadata { get; init; }
        public ContentClassification? Classification { get; init; }
        public IReadOnlyList<ExtractedEntity>? Entities { get; init; }
    }

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["SemanticDescription"] = SemanticDescription;
        metadata["ContentProcessorCount"] = _contentProcessors.Count;
        metadata["WriteStats"] = GetWriteStats();
        return metadata;
    }
}
