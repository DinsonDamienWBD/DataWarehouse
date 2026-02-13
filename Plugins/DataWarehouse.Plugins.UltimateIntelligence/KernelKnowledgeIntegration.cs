using System.Collections.Concurrent;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

using SdkKnowledgeQuery = DataWarehouse.SDK.Contracts.KnowledgeQuery;

namespace DataWarehouse.Plugins.UltimateIntelligence;

// ===================================================================================
// T90.P1.1: Kernel KnowledgeObject Integration
// Adds KnowledgeObject message handling to kernel for AI interactions
// ===================================================================================

/// <summary>
/// Kernel integration handler for KnowledgeObject message processing.
/// Routes knowledge requests through the message bus to appropriate handlers.
/// </summary>
public sealed class KernelKnowledgeObjectHandler : IDisposable
{
    private readonly IMessageBus _messageBus;
    private readonly KnowledgeAggregator _aggregator;
    private readonly List<IDisposable> _subscriptions = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<KnowledgeResponse>> _pendingRequests = new();
    private bool _disposed;

    /// <summary>
    /// Message topic for knowledge queries.
    /// </summary>
    public const string KnowledgeQueryTopic = "kernel.knowledge.query";

    /// <summary>
    /// Message topic for knowledge commands.
    /// </summary>
    public const string KnowledgeCommandTopic = "kernel.knowledge.command";

    /// <summary>
    /// Message topic for knowledge registration.
    /// </summary>
    public const string KnowledgeRegistrationTopic = "kernel.knowledge.register";

    /// <summary>
    /// Message topic for knowledge responses.
    /// </summary>
    public const string KnowledgeResponseTopic = "kernel.knowledge.response";

    /// <summary>
    /// Message topic for knowledge events.
    /// </summary>
    public const string KnowledgeEventTopic = "kernel.knowledge.event";

    /// <summary>
    /// Creates a new kernel knowledge handler.
    /// </summary>
    /// <param name="messageBus">The message bus for routing.</param>
    /// <param name="aggregator">The knowledge aggregator.</param>
    public KernelKnowledgeObjectHandler(IMessageBus messageBus, KnowledgeAggregator aggregator)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));

        Initialize();
    }

    /// <summary>
    /// Initializes message subscriptions.
    /// </summary>
    private void Initialize()
    {
        // Subscribe to knowledge queries
        _subscriptions.Add(_messageBus.Subscribe(KnowledgeQueryTopic, HandleKnowledgeQueryAsync));

        // Subscribe to knowledge commands
        _subscriptions.Add(_messageBus.Subscribe(KnowledgeCommandTopic, HandleKnowledgeCommandAsync));

        // Subscribe to knowledge registrations
        _subscriptions.Add(_messageBus.Subscribe(KnowledgeRegistrationTopic, HandleKnowledgeRegistrationAsync));

        // Subscribe to aggregator events and broadcast changes
        _aggregator.KnowledgeChanged += OnKnowledgeChanged;
    }

    /// <summary>
    /// Handles incoming knowledge queries.
    /// </summary>
    private async Task HandleKnowledgeQueryAsync(PluginMessage message)
    {
        try
        {
            var query = ExtractQuery(message.Payload);
            if (query == null) return;

            // Query the aggregator
            var results = await QueryKnowledgeAsync(query);

            // Send response
            await SendResponseAsync(message.SourcePluginId, query.RequestId, results);
        }
        catch (Exception ex)
        {
            await SendErrorResponseAsync(
                message.SourcePluginId,
                message.Payload.TryGetValue("requestId", out var reqId) ? reqId?.ToString() ?? "" : "",
                ex.Message
            );
        }
    }

    /// <summary>
    /// Handles incoming knowledge commands.
    /// </summary>
    private async Task HandleKnowledgeCommandAsync(PluginMessage message)
    {
        try
        {
            var command = ExtractCommand(message.Payload);
            if (command == null) return;

            var result = command.CommandType switch
            {
                KnowledgeCommandType.Refresh => await ExecuteRefreshAsync(command),
                KnowledgeCommandType.Invalidate => await ExecuteInvalidateAsync(command),
                KnowledgeCommandType.Subscribe => await ExecuteSubscribeAsync(command, message.SourcePluginId),
                KnowledgeCommandType.Unsubscribe => await ExecuteUnsubscribeAsync(command, message.SourcePluginId),
                _ => false
            };

            await SendCommandResultAsync(message.SourcePluginId, command.CommandId, result);
        }
        catch (Exception ex)
        {
            await SendErrorResponseAsync(
                message.SourcePluginId,
                message.Payload.TryGetValue("commandId", out var cmdId) ? cmdId?.ToString() ?? "" : "",
                ex.Message
            );
        }
    }

    /// <summary>
    /// Handles knowledge registration from plugins.
    /// </summary>
    private async Task HandleKnowledgeRegistrationAsync(PluginMessage message)
    {
        try
        {
            if (!message.Payload.TryGetValue("knowledge", out var knowledgeObj))
                return;

            if (knowledgeObj is KnowledgeObject knowledge)
            {
                // Find or create source for this plugin
                var source = _aggregator.GetSource(message.SourcePluginId);
                if (source == null)
                {
                    // Create a dynamic source for the plugin
                    var dynamicSource = new DynamicKnowledgeSource(
                        message.SourcePluginId,
                        message.Payload.TryGetValue("sourceName", out var name) ? name?.ToString() ?? message.SourcePluginId : message.SourcePluginId
                    );
                    dynamicSource.AddKnowledge(knowledge);
                    await _aggregator.RegisterSourceAsync(dynamicSource);
                }
            }
        }
        catch
        {
            // Log error but don't fail the message
        }
    }

    /// <summary>
    /// Broadcasts knowledge change events.
    /// </summary>
    private void OnKnowledgeChanged(object? sender, KnowledgeChangedEventArgs e)
    {
        _ = _messageBus.PublishAsync(KnowledgeEventTopic, new PluginMessage
        {
            Type = KnowledgeEventTopic,
            SourcePluginId = "kernel",
            Source = "Kernel",
            Payload = new Dictionary<string, object>
            {
                ["eventType"] = e.ChangeType.ToString(),
                ["sourceId"] = e.SourceId,
                ["timestamp"] = e.Timestamp,
                ["affectedCount"] = e.AffectedKnowledge.Count + e.RemovedKnowledgeIds.Count
            }
        });
    }

    /// <summary>
    /// Queries knowledge from the aggregator.
    /// </summary>
    private async Task<IReadOnlyCollection<KnowledgeObject>> QueryKnowledgeAsync(KernelKnowledgeQueryRequest query)
    {
        var allKnowledge = await _aggregator.GetAllKnowledgeAsync();

        IEnumerable<KnowledgeObject> results = allKnowledge.AllKnowledge;

        // Apply topic filter
        if (!string.IsNullOrEmpty(query.TopicPattern))
        {
            if (query.TopicPattern.Contains('*'))
            {
                var pattern = "^" + query.TopicPattern.Replace("*", ".*") + "$";
                results = results.Where(k => System.Text.RegularExpressions.Regex.IsMatch(k.Topic, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase));
            }
            else
            {
                results = results.Where(k => k.Topic.Equals(query.TopicPattern, StringComparison.OrdinalIgnoreCase));
            }
        }

        // Apply source filter
        if (!string.IsNullOrEmpty(query.SourcePluginId))
        {
            results = results.Where(k => k.SourcePluginId.Equals(query.SourcePluginId, StringComparison.OrdinalIgnoreCase));
        }

        // Apply search text
        if (!string.IsNullOrEmpty(query.SearchText))
        {
            var searchLower = query.SearchText.ToLowerInvariant();
            results = results.Where(k =>
                (k.Description?.ToLowerInvariant().Contains(searchLower) ?? false) ||
                (k.Topic?.ToLowerInvariant().Contains(searchLower) ?? false) ||
                k.Tags.Any(t => t.ToLowerInvariant().Contains(searchLower))
            );
        }

        // Apply limit
        if (query.MaxResults > 0)
        {
            results = results.Take(query.MaxResults);
        }

        return results.ToArray();
    }

    private async Task<bool> ExecuteRefreshAsync(KnowledgeCommand command)
    {
        if (!string.IsNullOrEmpty(command.TargetSourceId))
        {
            _aggregator.InvalidateCache(command.TargetSourceId);
        }
        else
        {
            _aggregator.InvalidateAllCaches();
        }
        return true;
    }

    private Task<bool> ExecuteInvalidateAsync(KnowledgeCommand command)
    {
        if (!string.IsNullOrEmpty(command.TargetSourceId))
        {
            _aggregator.InvalidateCache(command.TargetSourceId);
        }
        return Task.FromResult(true);
    }

    private Task<bool> ExecuteSubscribeAsync(KnowledgeCommand command, string pluginId)
    {
        // Subscription is handled by message bus subscription
        return Task.FromResult(true);
    }

    private Task<bool> ExecuteUnsubscribeAsync(KnowledgeCommand command, string pluginId)
    {
        // Unsubscription is handled by message bus
        return Task.FromResult(true);
    }

    private Task SendResponseAsync(string targetPluginId, string requestId, IReadOnlyCollection<KnowledgeObject> results)
    {
        return _messageBus.PublishAsync(KnowledgeResponseTopic, new PluginMessage
        {
            Type = KnowledgeResponseTopic,
            SourcePluginId = "kernel",
            Source = "Kernel",
            Payload = new Dictionary<string, object>
            {
                ["targetPluginId"] = targetPluginId,
                ["requestId"] = requestId,
                ["success"] = true,
                ["resultCount"] = results.Count,
                ["results"] = results.ToArray()
            }
        });
    }

    private Task SendErrorResponseAsync(string targetPluginId, string requestId, string error)
    {
        return _messageBus.PublishAsync(KnowledgeResponseTopic, new PluginMessage
        {
            Type = KnowledgeResponseTopic,
            SourcePluginId = "kernel",
            Source = "Kernel",
            Payload = new Dictionary<string, object>
            {
                ["targetPluginId"] = targetPluginId,
                ["requestId"] = requestId,
                ["success"] = false,
                ["error"] = error
            }
        });
    }

    private Task SendCommandResultAsync(string targetPluginId, string commandId, bool success)
    {
        return _messageBus.PublishAsync(KnowledgeResponseTopic, new PluginMessage
        {
            Type = KnowledgeResponseTopic,
            SourcePluginId = "kernel",
            Source = "Kernel",
            Payload = new Dictionary<string, object>
            {
                ["targetPluginId"] = targetPluginId,
                ["commandId"] = commandId,
                ["success"] = success
            }
        });
    }

    private static KernelKnowledgeQueryRequest? ExtractQuery(Dictionary<string, object> payload)
    {
        if (!payload.TryGetValue("requestId", out var reqIdObj))
            return null;

        return new KernelKnowledgeQueryRequest
        {
            RequestId = reqIdObj?.ToString() ?? Guid.NewGuid().ToString(),
            TopicPattern = payload.TryGetValue("topicPattern", out var tp) ? tp?.ToString() : null,
            SourcePluginId = payload.TryGetValue("sourcePluginId", out var sp) ? sp?.ToString() : null,
            SearchText = payload.TryGetValue("searchText", out var st) ? st?.ToString() : null,
            MaxResults = payload.TryGetValue("maxResults", out var mr) && mr is int maxR ? maxR : 100
        };
    }

    private static KnowledgeCommand? ExtractCommand(Dictionary<string, object> payload)
    {
        if (!payload.TryGetValue("commandId", out var cmdIdObj))
            return null;

        var commandType = KnowledgeCommandType.Refresh;
        if (payload.TryGetValue("commandType", out var ctObj) && ctObj is string ctStr)
        {
            Enum.TryParse<KnowledgeCommandType>(ctStr, true, out commandType);
        }

        return new KnowledgeCommand
        {
            CommandId = cmdIdObj?.ToString() ?? Guid.NewGuid().ToString(),
            CommandType = commandType,
            TargetSourceId = payload.TryGetValue("targetSourceId", out var ts) ? ts?.ToString() : null
        };
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _aggregator.KnowledgeChanged -= OnKnowledgeChanged;

        foreach (var sub in _subscriptions)
        {
            try { sub.Dispose(); } catch { }
        }
        _subscriptions.Clear();
    }
}

/// <summary>
/// Kernel knowledge query request.
/// </summary>
public sealed class KernelKnowledgeQueryRequest
{
    /// <summary>Unique request identifier for response correlation.</summary>
    public string RequestId { get; init; } = "";

    /// <summary>Topic pattern to filter (supports * wildcard).</summary>
    public string? TopicPattern { get; init; }

    /// <summary>Source plugin ID to filter.</summary>
    public string? SourcePluginId { get; init; }

    /// <summary>Full-text search term.</summary>
    public string? SearchText { get; init; }

    /// <summary>Maximum results to return.</summary>
    public int MaxResults { get; init; } = 100;
}

/// <summary>
/// Knowledge command types.
/// </summary>
public enum KnowledgeCommandType
{
    /// <summary>Refresh knowledge cache.</summary>
    Refresh,

    /// <summary>Invalidate specific knowledge.</summary>
    Invalidate,

    /// <summary>Subscribe to knowledge changes.</summary>
    Subscribe,

    /// <summary>Unsubscribe from knowledge changes.</summary>
    Unsubscribe
}

/// <summary>
/// Knowledge command.
/// </summary>
public sealed class KnowledgeCommand
{
    /// <summary>Unique command identifier.</summary>
    public string CommandId { get; init; } = "";

    /// <summary>Command type.</summary>
    public KnowledgeCommandType CommandType { get; init; }

    /// <summary>Target source ID for the command.</summary>
    public string? TargetSourceId { get; init; }
}

/// <summary>
/// Knowledge response.
/// </summary>
public sealed class KnowledgeResponse
{
    /// <summary>Request/command ID for correlation.</summary>
    public string CorrelationId { get; init; } = "";

    /// <summary>Whether the request succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Error message if failed.</summary>
    public string? Error { get; init; }

    /// <summary>Knowledge results.</summary>
    public IReadOnlyCollection<KnowledgeObject> Results { get; init; } = Array.Empty<KnowledgeObject>();
}

/// <summary>
/// Dynamic knowledge source that can be updated at runtime.
/// </summary>
public sealed class DynamicKnowledgeSource : IKnowledgeSource
{
    private readonly List<KnowledgeObject> _knowledge = new();
    private readonly List<KnowledgeCapability> _capabilities = new();
    private readonly object _lock = new();

    /// <summary>
    /// Creates a new dynamic knowledge source.
    /// </summary>
    public DynamicKnowledgeSource(string sourceId, string sourceName)
    {
        SourceId = sourceId;
        SourceName = sourceName;
    }

    /// <inheritdoc/>
    public string SourceId { get; }

    /// <inheritdoc/>
    public string SourceName { get; }

    /// <inheritdoc/>
    public IReadOnlyCollection<KnowledgeCapability> Capabilities
    {
        get
        {
            lock (_lock)
            {
                return _capabilities.ToArray();
            }
        }
    }

    /// <inheritdoc/>
    public bool IsAvailable => true;

    /// <summary>
    /// Adds knowledge to this source.
    /// </summary>
    public void AddKnowledge(KnowledgeObject knowledge)
    {
        lock (_lock)
        {
            _knowledge.RemoveAll(k => k.Id == knowledge.Id);
            _knowledge.Add(knowledge);
        }
    }

    /// <summary>
    /// Removes knowledge from this source.
    /// </summary>
    public void RemoveKnowledge(string knowledgeId)
    {
        lock (_lock)
        {
            _knowledge.RemoveAll(k => k.Id == knowledgeId);
        }
    }

    /// <summary>
    /// Adds a capability to this source.
    /// </summary>
    public void AddCapability(KnowledgeCapability capability)
    {
        lock (_lock)
        {
            if (!_capabilities.Any(c => c.CapabilityId == capability.CapabilityId))
            {
                _capabilities.Add(capability);
            }
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyCollection<KnowledgeObject>> GetStaticKnowledgeAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyCollection<KnowledgeObject>>(_knowledge.ToArray());
        }
    }

    /// <inheritdoc/>
    public Task<IReadOnlyCollection<KnowledgeObject>> GetDynamicKnowledgeAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyCollection<KnowledgeObject>>(Array.Empty<KnowledgeObject>());
    }

    /// <inheritdoc/>
    public Task<IReadOnlyCollection<KnowledgeObject>> QueryAsync(SDK.Contracts.KnowledgeQuery query, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var results = _knowledge.AsEnumerable();

            if (!string.IsNullOrEmpty(query.TopicPattern))
            {
                results = results.Where(k => k.Topic.Contains(query.TopicPattern, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrEmpty(query.SearchText))
            {
                var searchLower = query.SearchText.ToLowerInvariant();
                results = results.Where(k =>
                    (k.Description?.ToLowerInvariant().Contains(searchLower) ?? false) ||
                    k.Tags.Any(t => t.ToLowerInvariant().Contains(searchLower))
                );
            }

            return Task.FromResult<IReadOnlyCollection<KnowledgeObject>>(results.ToArray());
        }
    }
}

/// <summary>
/// Extension methods for kernel knowledge integration.
/// </summary>
public static class KernelKnowledgeExtensions
{
    /// <summary>
    /// Creates and initializes the kernel knowledge handler.
    /// </summary>
    public static KernelKnowledgeObjectHandler CreateKnowledgeHandler(
        this IMessageBus messageBus,
        KnowledgeAggregator? aggregator = null)
    {
        return new KernelKnowledgeObjectHandler(
            messageBus,
            aggregator ?? new KnowledgeAggregator()
        );
    }

    /// <summary>
    /// Sends a knowledge query through the message bus.
    /// </summary>
    public static async Task<IReadOnlyCollection<KnowledgeObject>> QueryKnowledgeAsync(
        this IMessageBus messageBus,
        string? topicPattern = null,
        string? searchText = null,
        int maxResults = 100,
        CancellationToken ct = default)
    {
        var requestId = Guid.NewGuid().ToString();
        var tcs = new TaskCompletionSource<IReadOnlyCollection<KnowledgeObject>>();

        using var subscription = messageBus.Subscribe(KernelKnowledgeObjectHandler.KnowledgeResponseTopic, message =>
        {
            if (message.Payload.TryGetValue("requestId", out var rId) && rId?.ToString() == requestId)
            {
                if (message.Payload.TryGetValue("success", out var success) && success is bool s && s)
                {
                    if (message.Payload.TryGetValue("results", out var results) && results is KnowledgeObject[] arr)
                    {
                        tcs.TrySetResult(arr);
                    }
                    else
                    {
                        tcs.TrySetResult(Array.Empty<KnowledgeObject>());
                    }
                }
                else
                {
                    var error = message.Payload.TryGetValue("error", out var e) ? e?.ToString() : "Unknown error";
                    tcs.TrySetException(new InvalidOperationException(error));
                }
            }
            return Task.CompletedTask;
        });

        await messageBus.PublishAsync(KernelKnowledgeObjectHandler.KnowledgeQueryTopic, new PluginMessage
        {
            Type = KernelKnowledgeObjectHandler.KnowledgeQueryTopic,
            SourcePluginId = "client",
            Source = "Client",
            Payload = new Dictionary<string, object>
            {
                ["requestId"] = requestId,
                ["topicPattern"] = topicPattern ?? "",
                ["searchText"] = searchText ?? "",
                ["maxResults"] = maxResults
            }
        });

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            return await tcs.Task.WaitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<KnowledgeObject>();
        }
    }

    /// <summary>
    /// Registers knowledge through the message bus.
    /// </summary>
    public static async Task RegisterKnowledgeAsync(
        this IMessageBus messageBus,
        string sourcePluginId,
        string sourceName,
        KnowledgeObject knowledge,
        CancellationToken ct = default)
    {
        await messageBus.PublishAsync(KernelKnowledgeObjectHandler.KnowledgeRegistrationTopic, new PluginMessage
        {
            Type = KernelKnowledgeObjectHandler.KnowledgeRegistrationTopic,
            SourcePluginId = sourcePluginId,
            Source = sourceName,
            Payload = new Dictionary<string, object>
            {
                ["knowledge"] = knowledge,
                ["sourceName"] = sourceName
            }
        });
    }
}
