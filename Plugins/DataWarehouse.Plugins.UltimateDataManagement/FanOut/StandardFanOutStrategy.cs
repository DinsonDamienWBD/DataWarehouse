using System.Diagnostics;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateDataManagement.FanOut;

/// <summary>
/// Configuration for the standard fan out strategy.
/// Can be set at Instance, UserGroup, User, or Operation level.
/// </summary>
public sealed class StandardFanOutConfiguration
{
    /// <summary>Enable primary storage destination (always required).</summary>
    public bool EnablePrimaryStorage { get; set; } = true;

    /// <summary>Enable metadata/index storage destination.</summary>
    public bool EnableMetadataStorage { get; set; } = true;

    /// <summary>Enable full-text index destination.</summary>
    public bool EnableFullTextIndex { get; set; } = true;

    /// <summary>Enable vector store destination (requires T90 Intelligence).</summary>
    public bool EnableVectorIndex { get; set; } = true;

    /// <summary>Enable cache layer destination.</summary>
    public bool EnableCaching { get; set; } = true;

    /// <summary>Success criteria for the fan out operation.</summary>
    public FanOutSuccessCriteria SuccessCriteria { get; set; } = FanOutSuccessCriteria.PrimaryPlusOne;

    /// <summary>Timeout for non-required destinations.</summary>
    public TimeSpan NonRequiredTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Whether child levels can override this configuration.</summary>
    public bool AllowChildOverride { get; set; } = true;

    /// <summary>
    /// Merges this configuration with a child override, respecting AllowChildOverride.
    /// </summary>
    public StandardFanOutConfiguration MergeWith(StandardFanOutConfiguration? childOverride)
    {
        if (childOverride == null || !AllowChildOverride)
        {
            return this;
        }

        return new StandardFanOutConfiguration
        {
            EnablePrimaryStorage = true, // Always enabled
            EnableMetadataStorage = childOverride.EnableMetadataStorage,
            EnableFullTextIndex = childOverride.EnableFullTextIndex,
            EnableVectorIndex = childOverride.EnableVectorIndex,
            EnableCaching = childOverride.EnableCaching,
            SuccessCriteria = childOverride.SuccessCriteria,
            NonRequiredTimeout = childOverride.NonRequiredTimeout,
            AllowChildOverride = childOverride.AllowChildOverride
        };
    }
}

/// <summary>
/// Standard fan out strategy for regular (non-tamper-proof) instances.
/// Uses MESSAGE BUS to communicate with T97 UltimateStorage for all storage operations.
/// Fully configurable at any level of the 4-tier hierarchy (Instance → UserGroup → User → Operation).
/// </summary>
/// <remarks>
/// <b>CONFIGURABLE STRATEGY:</b> This strategy can be modified at runtime
/// at any hierarchy level where AllowChildOverride is true.
///
/// <b>Architecture:</b>
/// - All storage operations go through MESSAGE BUS to T97 UltimateStorage
/// - Does NOT own storage destinations directly
///
/// <b>Destinations (via T97):</b>
/// <list type="bullet">
///   <item>Primary Storage - Always required (storage.primary.write)</item>
///   <item>Metadata/Index Storage - Configurable (storage.index.write)</item>
///   <item>Full-Text Index - Configurable (storage.fulltext.write)</item>
///   <item>Vector Store - Configurable (intelligence.vector.write via T90)</item>
///   <item>Cache Layer - Configurable (cache.write)</item>
/// </list>
///
/// <b>Success Criteria:</b> Configurable (default: PrimaryPlusOne)
/// </remarks>
public sealed class StandardFanOutStrategy : FanOutStrategyBase
{
    private readonly StandardFanOutConfiguration _configuration;
    private readonly IMessageBus? _messageBus;
    private readonly HashSet<WriteDestinationType> _enabledDestinations;
    private readonly HashSet<WriteDestinationType> _requiredDestinations;

    /// <inheritdoc/>
    public override string StrategyId => "Standard";

    /// <inheritdoc/>
    public override string DisplayName => "Standard Fan Out";

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Configurable fan out strategy for standard instances. " +
        "All storage operations go through message bus to T97 UltimateStorage. " +
        "Configuration can be set at any hierarchy level.";

    /// <inheritdoc/>
    public override bool IsLocked => false;

    /// <inheritdoc/>
    public override IReadOnlySet<WriteDestinationType> EnabledDestinations => _enabledDestinations;

    /// <inheritdoc/>
    public override IReadOnlySet<WriteDestinationType> RequiredDestinations => _requiredDestinations;

    /// <summary>
    /// Gets the current configuration.
    /// </summary>
    public StandardFanOutConfiguration Configuration => _configuration;

    /// <summary>
    /// Initializes a new StandardFanOutStrategy with default configuration.
    /// </summary>
    public StandardFanOutStrategy() : this(new StandardFanOutConfiguration(), null)
    {
    }

    /// <summary>
    /// Initializes a new StandardFanOutStrategy with the specified configuration.
    /// </summary>
    /// <param name="configuration">Strategy configuration.</param>
    /// <param name="messageBus">Message bus for T97 communication.</param>
    public StandardFanOutStrategy(StandardFanOutConfiguration configuration, IMessageBus? messageBus = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _configuration = configuration;
        _messageBus = messageBus;

        // Apply configuration settings
        SuccessCriteria = configuration.SuccessCriteria;
        NonRequiredTimeout = configuration.NonRequiredTimeout;
        AllowChildOverride = configuration.AllowChildOverride;

        // Build enabled destinations set
        _enabledDestinations = new HashSet<WriteDestinationType>
        {
            WriteDestinationType.PrimaryStorage  // Always enabled
        };

        if (configuration.EnableMetadataStorage)
            _enabledDestinations.Add(WriteDestinationType.MetadataStorage);

        if (configuration.EnableFullTextIndex)
            _enabledDestinations.Add(WriteDestinationType.TextIndex);

        if (configuration.EnableVectorIndex)
            _enabledDestinations.Add(WriteDestinationType.VectorStore);

        if (configuration.EnableCaching)
            _enabledDestinations.Add(WriteDestinationType.Cache);

        // Build required destinations based on success criteria
        _requiredDestinations = BuildRequiredDestinations(configuration.SuccessCriteria);
    }

    /// <summary>
    /// Sets the message bus for T97 communication.
    /// </summary>
    public StandardFanOutStrategy WithMessageBus(IMessageBus messageBus)
    {
        return new StandardFanOutStrategy(_configuration, messageBus);
    }

    /// <summary>
    /// Creates a new strategy with configuration merged from child override.
    /// </summary>
    /// <param name="childOverride">Child configuration to merge.</param>
    /// <returns>New strategy with merged configuration.</returns>
    public StandardFanOutStrategy WithOverride(StandardFanOutConfiguration? childOverride)
    {
        var mergedConfig = _configuration.MergeWith(childOverride);
        return new StandardFanOutStrategy(mergedConfig, _messageBus);
    }

    /// <inheritdoc/>
    public override async Task<FanOutStrategyResult> ExecuteAsync(
        string objectId,
        IndexableContent content,
        IReadOnlyDictionary<WriteDestinationType, IWriteDestination> destinations,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var results = new Dictionary<WriteDestinationType, WriteDestinationResult>();

        // Build list of write tasks based on enabled destinations
        var tasks = new List<Task<(WriteDestinationType type, WriteDestinationResult result)>>();

        // Primary Storage (always)
        if (_enabledDestinations.Contains(WriteDestinationType.PrimaryStorage))
        {
            tasks.Add(WriteToPrimaryStorageAsync(objectId, content, ct));
        }

        // Metadata/Index Storage
        if (_enabledDestinations.Contains(WriteDestinationType.MetadataStorage))
        {
            tasks.Add(WriteToIndexStorageAsync(objectId, content, ct));
        }

        // Full-Text Index
        if (_enabledDestinations.Contains(WriteDestinationType.TextIndex))
        {
            tasks.Add(WriteToFullTextIndexAsync(objectId, content, ct));
        }

        // Vector Store (via T90)
        if (_enabledDestinations.Contains(WriteDestinationType.VectorStore))
        {
            tasks.Add(WriteToVectorStoreAsync(objectId, content, ct));
        }

        // Cache
        if (_enabledDestinations.Contains(WriteDestinationType.Cache))
        {
            tasks.Add(WriteToCacheAsync(objectId, content, ct));
        }

        // Execute all writes in parallel
        var writeResults = await Task.WhenAll(tasks);
        foreach (var (type, result) in writeResults)
        {
            results[type] = result;
        }

        sw.Stop();

        // Evaluate success based on criteria
        var success = EvaluateSuccess(results);

        return new FanOutStrategyResult
        {
            Success = success,
            ErrorMessage = success ? null : GetFailureMessage(results),
            DestinationResults = results,
            Duration = sw.Elapsed
        };
    }

    /// <summary>
    /// Write to Primary Data Storage via T97 message bus.
    /// </summary>
    private async Task<(WriteDestinationType type, WriteDestinationResult result)> WriteToPrimaryStorageAsync(
        string objectId,
        IndexableContent content,
        CancellationToken ct)
    {
        return await WriteViaMessageBusAsync(
            WriteDestinationType.PrimaryStorage,
            "storage.primary.write",
            objectId,
            new Dictionary<string, object>
            {
                ["objectId"] = objectId,
                ["filename"] = content.Filename ?? objectId,
                ["contentType"] = content.ContentType ?? "application/octet-stream",
                ["size"] = content.Size ?? 0
            },
            isRequired: true,
            ct);
    }

    /// <summary>
    /// Write to Index/Metadata Storage via T97 message bus.
    /// </summary>
    private async Task<(WriteDestinationType type, WriteDestinationResult result)> WriteToIndexStorageAsync(
        string objectId,
        IndexableContent content,
        CancellationToken ct)
    {
        var payload = new Dictionary<string, object>
        {
            ["objectId"] = objectId,
            ["filename"] = content.Filename ?? "",
            ["contentType"] = content.ContentType ?? "",
            ["size"] = content.Size ?? 0
        };

        if (content.Metadata != null)
        {
            payload["metadata"] = content.Metadata;
        }

        return await WriteViaMessageBusAsync(
            WriteDestinationType.MetadataStorage,
            "storage.index.write",
            objectId,
            payload,
            isRequired: _requiredDestinations.Contains(WriteDestinationType.MetadataStorage),
            ct);
    }

    /// <summary>
    /// Write to Full-Text Index via T97 message bus.
    /// </summary>
    private async Task<(WriteDestinationType type, WriteDestinationResult result)> WriteToFullTextIndexAsync(
        string objectId,
        IndexableContent content,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(content.TextContent))
        {
            return (WriteDestinationType.TextIndex, new WriteDestinationResult
            {
                Success = true,
                ErrorMessage = null,
                Duration = TimeSpan.Zero
            });
        }

        return await WriteViaMessageBusAsync(
            WriteDestinationType.TextIndex,
            "storage.fulltext.write",
            objectId,
            new Dictionary<string, object>
            {
                ["objectId"] = objectId,
                ["textContent"] = content.TextContent,
                ["filename"] = content.Filename ?? "",
                ["contentType"] = content.ContentType ?? ""
            },
            isRequired: _requiredDestinations.Contains(WriteDestinationType.TextIndex),
            ct);
    }

    /// <summary>
    /// Write to Vector Store via T90 Intelligence message bus.
    /// </summary>
    private async Task<(WriteDestinationType type, WriteDestinationResult result)> WriteToVectorStoreAsync(
        string objectId,
        IndexableContent content,
        CancellationToken ct)
    {
        // Vector store requires either embeddings or text content
        if (content.Embeddings == null && string.IsNullOrEmpty(content.TextContent))
        {
            return (WriteDestinationType.VectorStore, new WriteDestinationResult
            {
                Success = true,
                ErrorMessage = null,
                Duration = TimeSpan.Zero
            });
        }

        var payload = new Dictionary<string, object>
        {
            ["objectId"] = objectId,
            ["filename"] = content.Filename ?? ""
        };

        if (content.Embeddings != null)
        {
            payload["embeddings"] = content.Embeddings;
        }
        else if (!string.IsNullOrEmpty(content.TextContent))
        {
            payload["textContent"] = content.TextContent;
            payload["generateEmbeddings"] = true;
        }

        return await WriteViaMessageBusAsync(
            WriteDestinationType.VectorStore,
            "intelligence.vector.write",
            objectId,
            payload,
            isRequired: _requiredDestinations.Contains(WriteDestinationType.VectorStore),
            ct);
    }

    /// <summary>
    /// Write to Cache via message bus.
    /// </summary>
    private async Task<(WriteDestinationType type, WriteDestinationResult result)> WriteToCacheAsync(
        string objectId,
        IndexableContent content,
        CancellationToken ct)
    {
        return await WriteViaMessageBusAsync(
            WriteDestinationType.Cache,
            "cache.write",
            objectId,
            new Dictionary<string, object>
            {
                ["objectId"] = objectId,
                ["filename"] = content.Filename ?? "",
                ["contentType"] = content.ContentType ?? "",
                ["size"] = content.Size ?? 0,
                ["summary"] = content.Summary ?? "",
                ["ttlSeconds"] = (int)NonRequiredTimeout.TotalSeconds * 30 // 30x timeout as TTL
            },
            isRequired: _requiredDestinations.Contains(WriteDestinationType.Cache),
            ct);
    }

    /// <summary>
    /// Generic method to write via message bus with timeout handling.
    /// </summary>
    private async Task<(WriteDestinationType type, WriteDestinationResult result)> WriteViaMessageBusAsync(
        WriteDestinationType destinationType,
        string topic,
        string objectId,
        Dictionary<string, object> payload,
        bool isRequired,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        if (_messageBus == null)
        {
            sw.Stop();
            // If message bus not available, return success for optional, failure for required
            return (destinationType, new WriteDestinationResult
            {
                Success = !isRequired,
                ErrorMessage = isRequired ? "Message bus not available" : null,
                Duration = sw.Elapsed
            });
        }

        var timeout = isRequired ? TimeSpan.FromSeconds(60) : NonRequiredTimeout;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            var response = await _messageBus.SendAsync(
                topic,
                new PluginMessage
                {
                    Type = topic,
                    Source = "StandardFanOutStrategy",
                    Payload = payload
                },
                timeout,
                cts.Token);

            sw.Stop();

            var success = response?.Success == true;

            // For optional destinations, timeout or failure is acceptable
            if (!isRequired && !success)
            {
                return (destinationType, new WriteDestinationResult
                {
                    Success = true, // Optional failures don't fail the operation
                    ErrorMessage = $"Optional write to {destinationType} failed (non-blocking)",
                    Duration = sw.Elapsed
                });
            }

            return (destinationType, new WriteDestinationResult
            {
                Success = success,
                ErrorMessage = success ? null : $"{destinationType} write failed",
                Duration = sw.Elapsed
            });
        }
        catch (OperationCanceledException) when (!isRequired)
        {
            sw.Stop();
            return (destinationType, new WriteDestinationResult
            {
                Success = true, // Optional timeout doesn't fail the operation
                ErrorMessage = $"Optional write to {destinationType} timed out (non-blocking)",
                Duration = sw.Elapsed
            });
        }
        catch (Exception ex)
        {
            sw.Stop();

            if (!isRequired)
            {
                return (destinationType, new WriteDestinationResult
                {
                    Success = true, // Optional failures don't fail the operation
                    ErrorMessage = $"Optional write to {destinationType} error: {ex.Message} (non-blocking)",
                    Duration = sw.Elapsed
                });
            }

            return (destinationType, new WriteDestinationResult
            {
                Success = false,
                ErrorMessage = ex.Message,
                Duration = sw.Elapsed
            });
        }
    }

    private static HashSet<WriteDestinationType> BuildRequiredDestinations(FanOutSuccessCriteria criteria)
    {
        return criteria switch
        {
            FanOutSuccessCriteria.AllRequired => new HashSet<WriteDestinationType>
            {
                WriteDestinationType.PrimaryStorage,
                WriteDestinationType.MetadataStorage,
                WriteDestinationType.TextIndex,
                WriteDestinationType.VectorStore,
                WriteDestinationType.Cache
            },
            FanOutSuccessCriteria.PrimaryPlusOne or
            FanOutSuccessCriteria.PrimaryOnly => new HashSet<WriteDestinationType>
            {
                WriteDestinationType.PrimaryStorage
            },
            FanOutSuccessCriteria.Majority => new HashSet<WriteDestinationType>
            {
                WriteDestinationType.PrimaryStorage,
                WriteDestinationType.MetadataStorage
            },
            FanOutSuccessCriteria.Any => new HashSet<WriteDestinationType>(),
            _ => new HashSet<WriteDestinationType> { WriteDestinationType.PrimaryStorage }
        };
    }
}

/// <summary>
/// Custom fan out strategy allowing fully user-defined destination combinations.
/// Uses MESSAGE BUS for all storage operations.
/// </summary>
/// <remarks>
/// Use this strategy when neither TamperProof nor Standard meets requirements.
/// The user specifies exactly which destinations to use and their requirements.
/// All storage operations go through message bus to T97 UltimateStorage.
/// </remarks>
public sealed class CustomFanOutStrategy : FanOutStrategyBase
{
    private readonly string _strategyId;
    private readonly string _displayName;
    private readonly HashSet<WriteDestinationType> _enabledDestinations;
    private readonly HashSet<WriteDestinationType> _requiredDestinations;
    private readonly Dictionary<WriteDestinationType, string> _topicMappings;
    private readonly IMessageBus? _messageBus;

    /// <inheritdoc/>
    public override string StrategyId => _strategyId;

    /// <inheritdoc/>
    public override string DisplayName => _displayName;

    /// <inheritdoc/>
    public override string SemanticDescription =>
        $"Custom fan out strategy '{_displayName}' with user-defined destination configuration. " +
        "All storage operations go through message bus.";

    /// <inheritdoc/>
    public override bool IsLocked { get; }

    /// <inheritdoc/>
    public override IReadOnlySet<WriteDestinationType> EnabledDestinations => _enabledDestinations;

    /// <inheritdoc/>
    public override IReadOnlySet<WriteDestinationType> RequiredDestinations => _requiredDestinations;

    /// <summary>
    /// Initializes a new CustomFanOutStrategy.
    /// </summary>
    public CustomFanOutStrategy(
        string strategyId,
        string displayName,
        IEnumerable<WriteDestinationType> enabledDestinations,
        IEnumerable<WriteDestinationType> requiredDestinations,
        Dictionary<WriteDestinationType, string>? topicMappings = null,
        FanOutSuccessCriteria successCriteria = FanOutSuccessCriteria.AllRequired,
        bool isLocked = false,
        IMessageBus? messageBus = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(strategyId);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        _strategyId = strategyId;
        _displayName = displayName;
        _enabledDestinations = new HashSet<WriteDestinationType>(enabledDestinations);
        _requiredDestinations = new HashSet<WriteDestinationType>(requiredDestinations);
        _topicMappings = topicMappings ?? GetDefaultTopicMappings();
        _messageBus = messageBus;
        SuccessCriteria = successCriteria;
        IsLocked = isLocked;
    }

    /// <inheritdoc/>
    public override async Task<FanOutStrategyResult> ExecuteAsync(
        string objectId,
        IndexableContent content,
        IReadOnlyDictionary<WriteDestinationType, IWriteDestination> destinations,
        CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var results = new Dictionary<WriteDestinationType, WriteDestinationResult>();

        if (_messageBus == null)
        {
            sw.Stop();
            return new FanOutStrategyResult
            {
                Success = false,
                ErrorMessage = "Message bus not available for custom strategy",
                DestinationResults = results,
                Duration = sw.Elapsed
            };
        }

        // Execute writes for all enabled destinations
        var tasks = _enabledDestinations
            .Where(d => _topicMappings.ContainsKey(d))
            .Select(async type =>
            {
                var isRequired = _requiredDestinations.Contains(type);
                var topic = _topicMappings[type];
                var timeout = isRequired ? TimeSpan.FromSeconds(60) : NonRequiredTimeout;

                var taskSw = Stopwatch.StartNew();
                try
                {
                    var response = await _messageBus.SendAsync(
                        topic,
                        new PluginMessage
                        {
                            Type = topic,
                            Source = _strategyId,
                            Payload = BuildPayload(type, objectId, content)
                        },
                        timeout,
                        ct);

                    taskSw.Stop();
                    var success = response?.Success == true;

                    return (type, new WriteDestinationResult
                    {
                        Success = success || !isRequired,
                        ErrorMessage = success ? null : $"{type} write failed",
                        Duration = taskSw.Elapsed
                    });
                }
                catch (Exception ex)
                {
                    taskSw.Stop();
                    return (type, new WriteDestinationResult
                    {
                        Success = !isRequired,
                        ErrorMessage = ex.Message,
                        Duration = taskSw.Elapsed
                    });
                }
            });

        var writeResults = await Task.WhenAll(tasks);
        foreach (var (type, result) in writeResults)
        {
            results[type] = result;
        }

        sw.Stop();

        var success = EvaluateSuccess(results);

        return new FanOutStrategyResult
        {
            Success = success,
            ErrorMessage = success ? null : GetFailureMessage(results),
            DestinationResults = results,
            Duration = sw.Elapsed
        };
    }

    private static Dictionary<WriteDestinationType, string> GetDefaultTopicMappings()
    {
        return new Dictionary<WriteDestinationType, string>
        {
            [WriteDestinationType.PrimaryStorage] = "storage.primary.write",
            [WriteDestinationType.MetadataStorage] = "storage.index.write",
            [WriteDestinationType.TextIndex] = "storage.fulltext.write",
            [WriteDestinationType.VectorStore] = "intelligence.vector.write",
            [WriteDestinationType.Cache] = "cache.write",
            [WriteDestinationType.DocumentStore] = "storage.document.write"
        };
    }

    private static Dictionary<string, object> BuildPayload(
        WriteDestinationType type,
        string objectId,
        IndexableContent content)
    {
        var payload = new Dictionary<string, object>
        {
            ["objectId"] = objectId,
            ["filename"] = content.Filename ?? objectId,
            ["contentType"] = content.ContentType ?? "application/octet-stream",
            ["size"] = content.Size ?? 0
        };

        if (type == WriteDestinationType.TextIndex && !string.IsNullOrEmpty(content.TextContent))
        {
            payload["textContent"] = content.TextContent;
        }

        if (type == WriteDestinationType.VectorStore)
        {
            if (content.Embeddings != null)
            {
                payload["embeddings"] = content.Embeddings;
            }
            else if (!string.IsNullOrEmpty(content.TextContent))
            {
                payload["textContent"] = content.TextContent;
                payload["generateEmbeddings"] = true;
            }
        }

        if (type == WriteDestinationType.MetadataStorage && content.Metadata != null)
        {
            payload["metadata"] = content.Metadata;
        }

        return payload;
    }

    /// <summary>
    /// Builder for creating custom fan out strategies.
    /// </summary>
    public sealed class Builder
    {
        private string _strategyId = "Custom";
        private string _displayName = "Custom Strategy";
        private readonly HashSet<WriteDestinationType> _enabled = new() { WriteDestinationType.PrimaryStorage };
        private readonly HashSet<WriteDestinationType> _required = new() { WriteDestinationType.PrimaryStorage };
        private readonly Dictionary<WriteDestinationType, string> _topics = new();
        private FanOutSuccessCriteria _criteria = FanOutSuccessCriteria.AllRequired;
        private TimeSpan _timeout = TimeSpan.FromSeconds(30);
        private bool _isLocked = false;
        private bool _allowChildOverride = true;
        private IMessageBus? _messageBus;

        /// <summary>Sets the strategy ID.</summary>
        public Builder WithId(string id) { _strategyId = id; return this; }

        /// <summary>Sets the display name.</summary>
        public Builder WithName(string name) { _displayName = name; return this; }

        /// <summary>Sets the message bus.</summary>
        public Builder WithMessageBus(IMessageBus bus) { _messageBus = bus; return this; }

        /// <summary>Adds an enabled destination with optional custom topic.</summary>
        public Builder AddDestination(WriteDestinationType type, bool required = false, string? customTopic = null)
        {
            _enabled.Add(type);
            if (required) _required.Add(type);
            if (customTopic != null) _topics[type] = customTopic;
            return this;
        }

        /// <summary>Sets the success criteria.</summary>
        public Builder WithSuccessCriteria(FanOutSuccessCriteria criteria) { _criteria = criteria; return this; }

        /// <summary>Sets the timeout for non-required destinations.</summary>
        public Builder WithTimeout(TimeSpan timeout) { _timeout = timeout; return this; }

        /// <summary>Locks the strategy configuration.</summary>
        public Builder Lock() { _isLocked = true; _allowChildOverride = false; return this; }

        /// <summary>Allows child override.</summary>
        public Builder AllowOverride() { _allowChildOverride = true; return this; }

        /// <summary>Builds the custom strategy.</summary>
        public CustomFanOutStrategy Build()
        {
            var strategy = new CustomFanOutStrategy(
                _strategyId,
                _displayName,
                _enabled,
                _required,
                _topics.Count > 0 ? _topics : null,
                _criteria,
                _isLocked,
                _messageBus);
            strategy.NonRequiredTimeout = _timeout;
            strategy.AllowChildOverride = _allowChildOverride;
            return strategy;
        }
    }
}
