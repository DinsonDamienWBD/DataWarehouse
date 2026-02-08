using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.AI;

namespace DataWarehouse.Plugins.UltimateIntelligence.Modes;

#region Message Bus Topics

/// <summary>
/// Message bus topics for interaction mode operations.
/// Provides comprehensive topic definitions for all mode-related messaging.
/// </summary>
public static class InteractionModeTopics
{
    /// <summary>Base prefix for all interaction mode topics.</summary>
    public const string Prefix = "intelligence.modes";

    #region On-Demand Mode (K1)

    /// <summary>Start an interactive chat session.</summary>
    public const string ChatStart = $"{Prefix}.chat.start";

    /// <summary>Response for chat start.</summary>
    public const string ChatStartResponse = $"{Prefix}.chat.start.response";

    /// <summary>Send a chat message.</summary>
    public const string ChatMessage = $"{Prefix}.chat.message";

    /// <summary>Response for chat message (full response).</summary>
    public const string ChatMessageResponse = $"{Prefix}.chat.message.response";

    /// <summary>Stream chat response tokens.</summary>
    public const string ChatStream = $"{Prefix}.chat.stream";

    /// <summary>Individual streamed token.</summary>
    public const string ChatStreamToken = $"{Prefix}.chat.stream.token";

    /// <summary>Stream completed.</summary>
    public const string ChatStreamComplete = $"{Prefix}.chat.stream.complete";

    /// <summary>End chat session.</summary>
    public const string ChatEnd = $"{Prefix}.chat.end";

    /// <summary>Response for chat end.</summary>
    public const string ChatEndResponse = $"{Prefix}.chat.end.response";

    /// <summary>Get conversation history.</summary>
    public const string ChatHistory = $"{Prefix}.chat.history";

    /// <summary>Response for chat history.</summary>
    public const string ChatHistoryResponse = $"{Prefix}.chat.history.response";

    #endregion

    #region Background Mode (K2)

    /// <summary>Submit a background task.</summary>
    public const string BackgroundSubmit = $"{Prefix}.background.submit";

    /// <summary>Response for background submit.</summary>
    public const string BackgroundSubmitResponse = $"{Prefix}.background.submit.response";

    /// <summary>Get background task status.</summary>
    public const string BackgroundStatus = $"{Prefix}.background.status";

    /// <summary>Response for background status.</summary>
    public const string BackgroundStatusResponse = $"{Prefix}.background.status.response";

    /// <summary>Cancel a background task.</summary>
    public const string BackgroundCancel = $"{Prefix}.background.cancel";

    /// <summary>Response for background cancel.</summary>
    public const string BackgroundCancelResponse = $"{Prefix}.background.cancel.response";

    /// <summary>Background task completed event.</summary>
    public const string BackgroundCompleted = $"{Prefix}.background.completed";

    /// <summary>Background task failed event.</summary>
    public const string BackgroundFailed = $"{Prefix}.background.failed";

    /// <summary>Get queue status.</summary>
    public const string QueueStatus = $"{Prefix}.queue.status";

    /// <summary>Response for queue status.</summary>
    public const string QueueStatusResponse = $"{Prefix}.queue.status.response";

    #endregion

    #region Scheduled Mode (K3)

    /// <summary>Create a scheduled task.</summary>
    public const string ScheduleCreate = $"{Prefix}.schedule.create";

    /// <summary>Response for schedule create.</summary>
    public const string ScheduleCreateResponse = $"{Prefix}.schedule.create.response";

    /// <summary>Update a scheduled task.</summary>
    public const string ScheduleUpdate = $"{Prefix}.schedule.update";

    /// <summary>Response for schedule update.</summary>
    public const string ScheduleUpdateResponse = $"{Prefix}.schedule.update.response";

    /// <summary>Delete a scheduled task.</summary>
    public const string ScheduleDelete = $"{Prefix}.schedule.delete";

    /// <summary>Response for schedule delete.</summary>
    public const string ScheduleDeleteResponse = $"{Prefix}.schedule.delete.response";

    /// <summary>List scheduled tasks.</summary>
    public const string ScheduleList = $"{Prefix}.schedule.list";

    /// <summary>Response for schedule list.</summary>
    public const string ScheduleListResponse = $"{Prefix}.schedule.list.response";

    /// <summary>Scheduled task executed event.</summary>
    public const string ScheduleExecuted = $"{Prefix}.schedule.executed";

    /// <summary>Request scheduled report generation.</summary>
    public const string ReportGenerate = $"{Prefix}.report.generate";

    /// <summary>Response for report generation.</summary>
    public const string ReportGenerateResponse = $"{Prefix}.report.generate.response";

    #endregion

    #region Reactive Mode (K4)

    /// <summary>Register an event listener.</summary>
    public const string EventRegister = $"{Prefix}.event.register";

    /// <summary>Response for event register.</summary>
    public const string EventRegisterResponse = $"{Prefix}.event.register.response";

    /// <summary>Unregister an event listener.</summary>
    public const string EventUnregister = $"{Prefix}.event.unregister";

    /// <summary>Response for event unregister.</summary>
    public const string EventUnregisterResponse = $"{Prefix}.event.unregister.response";

    /// <summary>Create a trigger.</summary>
    public const string TriggerCreate = $"{Prefix}.trigger.create";

    /// <summary>Response for trigger create.</summary>
    public const string TriggerCreateResponse = $"{Prefix}.trigger.create.response";

    /// <summary>Delete a trigger.</summary>
    public const string TriggerDelete = $"{Prefix}.trigger.delete";

    /// <summary>Response for trigger delete.</summary>
    public const string TriggerDeleteResponse = $"{Prefix}.trigger.delete.response";

    /// <summary>Trigger fired event.</summary>
    public const string TriggerFired = $"{Prefix}.trigger.fired";

    /// <summary>Anomaly detected event.</summary>
    public const string AnomalyDetected = $"{Prefix}.anomaly.detected";

    /// <summary>Anomaly response executed event.</summary>
    public const string AnomalyResponded = $"{Prefix}.anomaly.responded";

    #endregion

    #region Mode Management

    /// <summary>Get current active modes.</summary>
    public const string GetActiveModes = $"{Prefix}.active";

    /// <summary>Response for active modes.</summary>
    public const string GetActiveModesResponse = $"{Prefix}.active.response";

    /// <summary>Mode statistics.</summary>
    public const string GetStats = $"{Prefix}.stats";

    /// <summary>Response for mode statistics.</summary>
    public const string GetStatsResponse = $"{Prefix}.stats.response";

    #endregion
}

#endregion

#region K1: On-Demand Mode

/// <summary>
/// Represents a chat message in a conversation.
/// </summary>
public sealed record ChatMessage
{
    /// <summary>Unique message identifier.</summary>
    public string MessageId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Role of the message sender (user, assistant, system).</summary>
    public required ChatRole Role { get; init; }

    /// <summary>Content of the message.</summary>
    public required string Content { get; init; }

    /// <summary>Timestamp when message was created.</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>Token count for this message.</summary>
    public int TokenCount { get; init; }

    /// <summary>Additional metadata.</summary>
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Role of a chat participant.
/// </summary>
public enum ChatRole
{
    /// <summary>System instructions.</summary>
    System,

    /// <summary>User input.</summary>
    User,

    /// <summary>AI assistant response.</summary>
    Assistant,

    /// <summary>Tool/function output.</summary>
    Tool
}

/// <summary>
/// Maintains conversation context across turns.
/// </summary>
public sealed class ConversationMemory
{
    private readonly List<ChatMessage> _messages = new();
    private readonly object _lock = new();
    private readonly int _maxMessages;
    private readonly int _maxTokens;
    private int _totalTokens;

    /// <summary>
    /// Initializes conversation memory.
    /// </summary>
    /// <param name="maxMessages">Maximum messages to retain.</param>
    /// <param name="maxTokens">Maximum total tokens.</param>
    public ConversationMemory(int maxMessages = 100, int maxTokens = 32000)
    {
        _maxMessages = maxMessages;
        _maxTokens = maxTokens;
    }

    /// <summary>Conversation identifier.</summary>
    public string ConversationId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>When conversation started.</summary>
    public DateTime StartedAt { get; } = DateTime.UtcNow;

    /// <summary>Last activity time.</summary>
    public DateTime LastActivityAt { get; private set; } = DateTime.UtcNow;

    /// <summary>Total messages in memory.</summary>
    public int MessageCount
    {
        get
        {
            lock (_lock) return _messages.Count;
        }
    }

    /// <summary>Total tokens in memory.</summary>
    public int TotalTokens
    {
        get
        {
            lock (_lock) return _totalTokens;
        }
    }

    /// <summary>
    /// Adds a message to memory.
    /// </summary>
    /// <param name="message">Message to add.</param>
    public void AddMessage(ChatMessage message)
    {
        lock (_lock)
        {
            _messages.Add(message);
            _totalTokens += message.TokenCount;
            LastActivityAt = DateTime.UtcNow;

            // Trim if exceeding limits
            TrimIfNeeded();
        }
    }

    /// <summary>
    /// Gets all messages.
    /// </summary>
    /// <returns>Copy of all messages.</returns>
    public IReadOnlyList<ChatMessage> GetMessages()
    {
        lock (_lock) return _messages.ToList().AsReadOnly();
    }

    /// <summary>
    /// Gets recent messages.
    /// </summary>
    /// <param name="count">Number of messages.</param>
    /// <returns>Recent messages.</returns>
    public IReadOnlyList<ChatMessage> GetRecentMessages(int count)
    {
        lock (_lock) return _messages.TakeLast(count).ToList().AsReadOnly();
    }

    /// <summary>
    /// Builds context string for AI prompt.
    /// </summary>
    /// <param name="maxTokens">Maximum tokens to include.</param>
    /// <returns>Formatted conversation context.</returns>
    public string BuildContextString(int maxTokens = 8000)
    {
        lock (_lock)
        {
            var context = new System.Text.StringBuilder();
            var tokenCount = 0;

            // Include messages from newest to oldest until token limit
            foreach (var msg in _messages.AsEnumerable().Reverse())
            {
                if (tokenCount + msg.TokenCount > maxTokens)
                    break;

                context.Insert(0, $"{msg.Role}: {msg.Content}\n\n");
                tokenCount += msg.TokenCount;
            }

            return context.ToString();
        }
    }

    /// <summary>
    /// Clears all messages.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _messages.Clear();
            _totalTokens = 0;
        }
    }

    private void TrimIfNeeded()
    {
        // Trim by message count
        while (_messages.Count > _maxMessages)
        {
            var removed = _messages[0];
            _messages.RemoveAt(0);
            _totalTokens -= removed.TokenCount;
        }

        // Trim by token count
        while (_totalTokens > _maxTokens && _messages.Count > 1)
        {
            var removed = _messages[0];
            _messages.RemoveAt(0);
            _totalTokens -= removed.TokenCount;
        }
    }
}

/// <summary>
/// Provides streaming support for AI responses.
/// </summary>
public sealed class StreamingSupport
{
    private readonly IAIProvider _aiProvider;
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeStreams = new();

    /// <summary>
    /// Initializes streaming support.
    /// </summary>
    /// <param name="aiProvider">AI provider for streaming.</param>
    public StreamingSupport(IAIProvider aiProvider)
    {
        _aiProvider = aiProvider;
    }

    /// <summary>
    /// Streams a response token by token.
    /// </summary>
    /// <param name="prompt">Input prompt.</param>
    /// <param name="systemPrompt">Optional system prompt.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async enumerable of response chunks.</returns>
    public async IAsyncEnumerable<StreamChunk> StreamResponseAsync(
        string prompt,
        string? systemPrompt = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var streamId = Guid.NewGuid().ToString("N");
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _activeStreams[streamId] = cts;

        try
        {
            var fullPrompt = string.IsNullOrEmpty(systemPrompt)
                ? prompt
                : $"{systemPrompt}\n\nUser: {prompt}\n\nAssistant:";

            var request = new AIRequest
            {
                Prompt = fullPrompt,
                MaxTokens = 2000,
                Temperature = 0.7f
            };

            var tokenIndex = 0;
            await foreach (var chunk in _aiProvider.CompleteStreamingAsync(request, cts.Token))
            {
                yield return new StreamChunk
                {
                    StreamId = streamId,
                    Content = chunk.Content,
                    TokenIndex = tokenIndex++,
                    IsComplete = chunk.IsFinal,
                    FinishReason = chunk.FinishReason
                };

                if (chunk.IsFinal)
                    break;
            }
        }
        finally
        {
            _activeStreams.TryRemove(streamId, out _);
            cts.Dispose();
        }
    }

    /// <summary>
    /// Cancels an active stream.
    /// </summary>
    /// <param name="streamId">Stream identifier.</param>
    public void CancelStream(string streamId)
    {
        if (_activeStreams.TryRemove(streamId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    /// <summary>
    /// Gets count of active streams.
    /// </summary>
    public int ActiveStreamCount => _activeStreams.Count;
}

/// <summary>
/// Individual chunk in a streamed response.
/// </summary>
public sealed record StreamChunk
{
    /// <summary>Stream identifier.</summary>
    public required string StreamId { get; init; }

    /// <summary>Content of this chunk.</summary>
    public required string Content { get; init; }

    /// <summary>Token index in the stream.</summary>
    public int TokenIndex { get; init; }

    /// <summary>Whether this is the final chunk.</summary>
    public bool IsComplete { get; init; }

    /// <summary>Reason for completion (if complete).</summary>
    public string? FinishReason { get; init; }
}

/// <summary>
/// Handles interactive chat sessions.
/// </summary>
public sealed class ChatHandler : FeatureStrategyBase
{
    private readonly ConcurrentDictionary<string, ChatSession> _sessions = new();
    private StreamingSupport? _streamingSupport;

    /// <inheritdoc/>
    public override string StrategyId => "mode-chat-handler";

    /// <inheritdoc/>
    public override string StrategyName => "Interactive Chat Handler";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Chat Handler",
        Description = "Handles interactive chat sessions with conversation memory and streaming support",
        Capabilities = IntelligenceCapabilities.ChatCompletion | IntelligenceCapabilities.Streaming,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "MaxSessionDurationMinutes", Description = "Maximum session duration", Required = false, DefaultValue = "60" },
            new ConfigurationRequirement { Key = "MaxMessagesPerSession", Description = "Maximum messages per session", Required = false, DefaultValue = "100" },
            new ConfigurationRequirement { Key = "SystemPrompt", Description = "Default system prompt", Required = false, DefaultValue = "You are a helpful AI assistant." }
        },
        CostTier = 3,
        LatencyTier = 2,
        RequiresNetworkAccess = true,
        Tags = new[] { "chat", "interactive", "streaming", "conversation" }
    };

    /// <summary>
    /// Initializes the chat handler with an AI provider.
    /// </summary>
    /// <param name="provider">AI provider to use.</param>
    public void Initialize(IAIProvider provider)
    {
        SetAIProvider(provider);
        _streamingSupport = new StreamingSupport(provider);
    }

    /// <summary>
    /// Starts a new chat session.
    /// </summary>
    /// <param name="systemPrompt">Optional system prompt.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The new chat session.</returns>
    public async Task<ChatSession> StartSessionAsync(
        string? systemPrompt = null,
        CancellationToken ct = default)
    {
        var session = new ChatSession
        {
            SystemPrompt = systemPrompt ?? GetConfig("SystemPrompt") ?? "You are a helpful AI assistant."
        };

        _sessions[session.SessionId] = session;

        // Add system message
        session.Memory.AddMessage(new ChatMessage
        {
            Role = ChatRole.System,
            Content = session.SystemPrompt,
            TokenCount = EstimateTokens(session.SystemPrompt)
        });

        await Task.CompletedTask;
        return session;
    }

    /// <summary>
    /// Sends a message and gets a response.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="message">User message.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Assistant response.</returns>
    public async Task<ChatMessage> SendMessageAsync(
        string sessionId,
        string message,
        CancellationToken ct = default)
    {
        var session = GetSession(sessionId)
            ?? throw new InvalidOperationException($"Session {sessionId} not found");

        if (AIProvider == null)
            throw new InvalidOperationException("AI provider not configured");

        // Add user message
        session.Memory.AddMessage(new ChatMessage
        {
            Role = ChatRole.User,
            Content = message,
            TokenCount = EstimateTokens(message)
        });

        // Build context
        var context = session.Memory.BuildContextString();
        var prompt = $"{context}\n\nAssistant:";

        // Get AI response
        var response = await AIProvider.CompleteAsync(new AIRequest
        {
            Prompt = prompt,
            MaxTokens = 2000,
            Temperature = 0.7f
        }, ct);

        // Add assistant response
        var assistantMessage = new ChatMessage
        {
            Role = ChatRole.Assistant,
            Content = response.Content.Trim(),
            TokenCount = response.Usage?.CompletionTokens ?? EstimateTokens(response.Content)
        };

        session.Memory.AddMessage(assistantMessage);
        session.TotalTokensUsed += response.Usage?.TotalTokens ?? 0;

        return assistantMessage;
    }

    /// <summary>
    /// Streams a response token by token.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="message">User message.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async enumerable of response chunks.</returns>
    public async IAsyncEnumerable<StreamChunk> StreamMessageAsync(
        string sessionId,
        string message,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var session = GetSession(sessionId)
            ?? throw new InvalidOperationException($"Session {sessionId} not found");

        if (_streamingSupport == null)
            throw new InvalidOperationException("Streaming not initialized");

        // Add user message
        session.Memory.AddMessage(new ChatMessage
        {
            Role = ChatRole.User,
            Content = message,
            TokenCount = EstimateTokens(message)
        });

        // Build context
        var context = session.Memory.BuildContextString();
        var fullContent = new System.Text.StringBuilder();

        await foreach (var chunk in _streamingSupport.StreamResponseAsync(context, session.SystemPrompt, ct))
        {
            fullContent.Append(chunk.Content);
            yield return chunk;
        }

        // Add complete assistant response
        var content = fullContent.ToString();
        session.Memory.AddMessage(new ChatMessage
        {
            Role = ChatRole.Assistant,
            Content = content,
            TokenCount = EstimateTokens(content)
        });
    }

    /// <summary>
    /// Gets a chat session.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <returns>The session if found.</returns>
    public ChatSession? GetSession(string sessionId)
    {
        return _sessions.TryGetValue(sessionId, out var session) ? session : null;
    }

    /// <summary>
    /// Gets conversation history.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <returns>List of messages.</returns>
    public IReadOnlyList<ChatMessage> GetHistory(string sessionId)
    {
        var session = GetSession(sessionId);
        return session?.Memory.GetMessages() ?? Array.Empty<ChatMessage>();
    }

    /// <summary>
    /// Ends a chat session.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task EndSessionAsync(string sessionId, CancellationToken ct = default)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            session.EndedAt = DateTime.UtcNow;
            session.Memory.Clear();
        }

        await Task.CompletedTask;
    }

    private static int EstimateTokens(string text)
    {
        // Rough estimation: ~4 characters per token
        return (text?.Length ?? 0) / 4;
    }
}

/// <summary>
/// Represents an active chat session.
/// </summary>
public sealed class ChatSession
{
    /// <summary>Session identifier.</summary>
    public string SessionId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>System prompt for this session.</summary>
    public string SystemPrompt { get; init; } = "";

    /// <summary>Conversation memory.</summary>
    public ConversationMemory Memory { get; } = new();

    /// <summary>When session started.</summary>
    public DateTime StartedAt { get; } = DateTime.UtcNow;

    /// <summary>When session ended.</summary>
    public DateTime? EndedAt { get; set; }

    /// <summary>Total tokens used in session.</summary>
    public int TotalTokensUsed { get; set; }

    /// <summary>Session metadata.</summary>
    public Dictionary<string, object> Metadata { get; } = new();
}

#endregion

#region K2: Background Mode

/// <summary>
/// Priority levels for background tasks.
/// </summary>
public enum TaskPriority
{
    /// <summary>Lowest priority.</summary>
    Low = 0,

    /// <summary>Normal priority.</summary>
    Normal = 50,

    /// <summary>High priority.</summary>
    High = 75,

    /// <summary>Critical priority.</summary>
    Critical = 100
}

/// <summary>
/// Status of a background task.
/// </summary>
public enum BackgroundTaskStatus
{
    /// <summary>Task is queued.</summary>
    Queued,

    /// <summary>Task is running.</summary>
    Running,

    /// <summary>Task completed successfully.</summary>
    Completed,

    /// <summary>Task failed.</summary>
    Failed,

    /// <summary>Task was cancelled.</summary>
    Cancelled,

    /// <summary>Task timed out.</summary>
    TimedOut
}

/// <summary>
/// Represents a background task.
/// </summary>
public sealed class BackgroundTask
{
    /// <summary>Task identifier.</summary>
    public string TaskId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Task name/description.</summary>
    public required string Name { get; init; }

    /// <summary>Task priority.</summary>
    public TaskPriority Priority { get; init; } = TaskPriority.Normal;

    /// <summary>The work to execute.</summary>
    public required Func<CancellationToken, Task<object?>> Work { get; init; }

    /// <summary>Current status.</summary>
    public BackgroundTaskStatus Status { get; set; } = BackgroundTaskStatus.Queued;

    /// <summary>When task was queued.</summary>
    public DateTime QueuedAt { get; } = DateTime.UtcNow;

    /// <summary>When task started executing.</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>When task completed.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Task result.</summary>
    public object? Result { get; set; }

    /// <summary>Error message if failed.</summary>
    public string? Error { get; set; }

    /// <summary>Timeout in milliseconds.</summary>
    public int TimeoutMs { get; init; } = 300000; // 5 minutes

    /// <summary>Retry count.</summary>
    public int RetryCount { get; set; }

    /// <summary>Maximum retries.</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>Task metadata.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Background task queue with priorities.
/// </summary>
public sealed class TaskQueue
{
    private readonly PriorityQueue<BackgroundTask, int> _queue = new();
    private readonly ConcurrentDictionary<string, BackgroundTask> _allTasks = new();
    private readonly object _lock = new();

    /// <summary>Total tasks in queue.</summary>
    public int Count
    {
        get { lock (_lock) return _queue.Count; }
    }

    /// <summary>
    /// Enqueues a task.
    /// </summary>
    /// <param name="task">Task to enqueue.</param>
    public void Enqueue(BackgroundTask task)
    {
        lock (_lock)
        {
            // Higher priority = lower priority value for dequeue order
            _queue.Enqueue(task, 100 - (int)task.Priority);
            _allTasks[task.TaskId] = task;
        }
    }

    /// <summary>
    /// Dequeues the highest priority task.
    /// </summary>
    /// <returns>The task, or null if queue is empty.</returns>
    public BackgroundTask? Dequeue()
    {
        lock (_lock)
        {
            if (_queue.TryDequeue(out var task, out _))
            {
                return task;
            }
            return null;
        }
    }

    /// <summary>
    /// Gets a task by ID.
    /// </summary>
    /// <param name="taskId">Task identifier.</param>
    /// <returns>The task if found.</returns>
    public BackgroundTask? GetTask(string taskId)
    {
        return _allTasks.TryGetValue(taskId, out var task) ? task : null;
    }

    /// <summary>
    /// Gets all tasks with specified status.
    /// </summary>
    /// <param name="status">Status to filter by.</param>
    /// <returns>Matching tasks.</returns>
    public IReadOnlyList<BackgroundTask> GetTasksByStatus(BackgroundTaskStatus status)
    {
        return _allTasks.Values.Where(t => t.Status == status).ToList();
    }

    /// <summary>
    /// Removes a task.
    /// </summary>
    /// <param name="taskId">Task identifier.</param>
    public void Remove(string taskId)
    {
        _allTasks.TryRemove(taskId, out _);
    }

    /// <summary>
    /// Gets queue statistics.
    /// </summary>
    public QueueStatistics GetStatistics()
    {
        var tasks = _allTasks.Values.ToList();
        return new QueueStatistics
        {
            TotalTasks = tasks.Count,
            QueuedTasks = tasks.Count(t => t.Status == BackgroundTaskStatus.Queued),
            RunningTasks = tasks.Count(t => t.Status == BackgroundTaskStatus.Running),
            CompletedTasks = tasks.Count(t => t.Status == BackgroundTaskStatus.Completed),
            FailedTasks = tasks.Count(t => t.Status == BackgroundTaskStatus.Failed),
            CancelledTasks = tasks.Count(t => t.Status == BackgroundTaskStatus.Cancelled)
        };
    }
}

/// <summary>
/// Queue statistics.
/// </summary>
public sealed record QueueStatistics
{
    /// <summary>Total tasks tracked.</summary>
    public int TotalTasks { get; init; }

    /// <summary>Tasks waiting in queue.</summary>
    public int QueuedTasks { get; init; }

    /// <summary>Tasks currently running.</summary>
    public int RunningTasks { get; init; }

    /// <summary>Tasks completed successfully.</summary>
    public int CompletedTasks { get; init; }

    /// <summary>Tasks that failed.</summary>
    public int FailedTasks { get; init; }

    /// <summary>Tasks that were cancelled.</summary>
    public int CancelledTasks { get; init; }
}

/// <summary>
/// Policy for autonomous decision-making.
/// </summary>
public sealed record AutoDecisionPolicy
{
    /// <summary>Policy identifier.</summary>
    public string PolicyId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Policy name.</summary>
    public required string Name { get; init; }

    /// <summary>Allowed actions.</summary>
    public List<string> AllowedActions { get; init; } = new();

    /// <summary>Denied actions.</summary>
    public List<string> DeniedActions { get; init; } = new();

    /// <summary>Maximum impact level allowed.</summary>
    public float MaxImpactLevel { get; init; } = 0.5f;

    /// <summary>Whether to require confirmation for actions.</summary>
    public bool RequireConfirmation { get; init; } = true;

    /// <summary>Actions requiring confirmation.</summary>
    public List<string> ConfirmationRequired { get; init; } = new();
}

/// <summary>
/// Handles autonomous decisions within policy bounds.
/// </summary>
public sealed class AutoDecision
{
    private readonly ConcurrentDictionary<string, AutoDecisionPolicy> _policies = new();
    private readonly ConcurrentQueue<DecisionRecord> _decisionHistory = new();

    /// <summary>
    /// Registers a policy.
    /// </summary>
    /// <param name="policy">Policy to register.</param>
    public void RegisterPolicy(AutoDecisionPolicy policy)
    {
        _policies[policy.PolicyId] = policy;
    }

    /// <summary>
    /// Evaluates if an action is allowed.
    /// </summary>
    /// <param name="action">Action to evaluate.</param>
    /// <param name="impactLevel">Estimated impact level.</param>
    /// <param name="policyId">Policy ID to use.</param>
    /// <returns>Decision result.</returns>
    public DecisionResult EvaluateAction(string action, float impactLevel, string? policyId = null)
    {
        var policy = policyId != null && _policies.TryGetValue(policyId, out var p)
            ? p
            : _policies.Values.FirstOrDefault();

        if (policy == null)
        {
            return new DecisionResult
            {
                Action = action,
                IsAllowed = false,
                Reason = "No policy configured"
            };
        }

        // Check denied list
        if (policy.DeniedActions.Any(d => action.Contains(d, StringComparison.OrdinalIgnoreCase)))
        {
            return RecordDecision(new DecisionResult
            {
                Action = action,
                IsAllowed = false,
                Reason = "Action is in denied list",
                PolicyId = policy.PolicyId
            });
        }

        // Check impact level
        if (impactLevel > policy.MaxImpactLevel)
        {
            return RecordDecision(new DecisionResult
            {
                Action = action,
                IsAllowed = false,
                Reason = $"Impact level {impactLevel} exceeds maximum {policy.MaxImpactLevel}",
                PolicyId = policy.PolicyId
            });
        }

        // Check allowed list (if not empty, action must be in it)
        if (policy.AllowedActions.Count > 0 &&
            !policy.AllowedActions.Any(a => action.Contains(a, StringComparison.OrdinalIgnoreCase)))
        {
            return RecordDecision(new DecisionResult
            {
                Action = action,
                IsAllowed = false,
                Reason = "Action not in allowed list",
                PolicyId = policy.PolicyId
            });
        }

        // Check if confirmation required
        var needsConfirmation = policy.RequireConfirmation ||
            policy.ConfirmationRequired.Any(c => action.Contains(c, StringComparison.OrdinalIgnoreCase));

        return RecordDecision(new DecisionResult
        {
            Action = action,
            IsAllowed = true,
            RequiresConfirmation = needsConfirmation,
            PolicyId = policy.PolicyId
        });
    }

    private DecisionResult RecordDecision(DecisionResult result)
    {
        _decisionHistory.Enqueue(new DecisionRecord
        {
            Result = result,
            Timestamp = DateTime.UtcNow
        });

        // Keep only last 1000 decisions
        while (_decisionHistory.Count > 1000)
            _decisionHistory.TryDequeue(out _);

        return result;
    }

    /// <summary>
    /// Gets recent decision history.
    /// </summary>
    /// <param name="count">Number of records.</param>
    public IReadOnlyList<DecisionRecord> GetHistory(int count = 100)
    {
        return _decisionHistory.TakeLast(count).ToList();
    }
}

/// <summary>
/// Result of a decision evaluation.
/// </summary>
public sealed record DecisionResult
{
    /// <summary>Action that was evaluated.</summary>
    public required string Action { get; init; }

    /// <summary>Whether action is allowed.</summary>
    public bool IsAllowed { get; init; }

    /// <summary>Reason for the decision.</summary>
    public string? Reason { get; init; }

    /// <summary>Whether confirmation is required.</summary>
    public bool RequiresConfirmation { get; init; }

    /// <summary>Policy that was used.</summary>
    public string? PolicyId { get; init; }
}

/// <summary>
/// Record of a decision.
/// </summary>
public sealed record DecisionRecord
{
    /// <summary>The decision result.</summary>
    public required DecisionResult Result { get; init; }

    /// <summary>When decision was made.</summary>
    public DateTime Timestamp { get; init; }
}

/// <summary>
/// Processes background tasks autonomously.
/// </summary>
public sealed class BackgroundProcessor : FeatureStrategyBase
{
    private readonly TaskQueue _taskQueue = new();
    private readonly AutoDecision _autoDecision = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runningTasks = new();
    private readonly SemaphoreSlim _concurrencySemaphore;
    private CancellationTokenSource? _processorCts;
    private Task? _processorTask;
    private bool _isRunning;

    /// <summary>
    /// Initializes the background processor.
    /// </summary>
    /// <param name="maxConcurrency">Maximum concurrent tasks.</param>
    public BackgroundProcessor(int maxConcurrency = 4)
    {
        _concurrencySemaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    /// <inheritdoc/>
    public override string StrategyId => "mode-background-processor";

    /// <inheritdoc/>
    public override string StrategyName => "Background Task Processor";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Background Processor",
        Description = "Autonomous background task processing with priority queue and policy-based decision making",
        Capabilities = IntelligenceCapabilities.TaskPlanning,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "MaxConcurrency", Description = "Maximum concurrent tasks", Required = false, DefaultValue = "4" },
            new ConfigurationRequirement { Key = "DefaultTimeoutMs", Description = "Default task timeout", Required = false, DefaultValue = "300000" },
            new ConfigurationRequirement { Key = "EnableAutoRetry", Description = "Enable automatic retry on failure", Required = false, DefaultValue = "true" }
        },
        CostTier = 2,
        LatencyTier = 3,
        RequiresNetworkAccess = false,
        SupportsOfflineMode = true,
        Tags = new[] { "background", "async", "queue", "autonomous" }
    };

    /// <summary>
    /// Starts the background processor.
    /// </summary>
    public void Start()
    {
        if (_isRunning) return;

        _processorCts = new CancellationTokenSource();
        _processorTask = ProcessQueueAsync(_processorCts.Token);
        _isRunning = true;
    }

    /// <summary>
    /// Stops the background processor.
    /// </summary>
    public async Task StopAsync()
    {
        if (!_isRunning) return;

        _processorCts?.Cancel();

        if (_processorTask != null)
        {
            try { await _processorTask; }
            catch (OperationCanceledException) { }
        }

        _isRunning = false;
    }

    /// <summary>
    /// Submits a task to the queue.
    /// </summary>
    /// <param name="task">Task to submit.</param>
    /// <returns>The queued task.</returns>
    public BackgroundTask SubmitTask(BackgroundTask task)
    {
        _taskQueue.Enqueue(task);
        return task;
    }

    /// <summary>
    /// Gets task status.
    /// </summary>
    /// <param name="taskId">Task identifier.</param>
    /// <returns>The task if found.</returns>
    public BackgroundTask? GetTaskStatus(string taskId)
    {
        return _taskQueue.GetTask(taskId);
    }

    /// <summary>
    /// Cancels a task.
    /// </summary>
    /// <param name="taskId">Task identifier.</param>
    /// <returns>True if cancelled.</returns>
    public bool CancelTask(string taskId)
    {
        var task = _taskQueue.GetTask(taskId);
        if (task == null) return false;

        if (_runningTasks.TryRemove(taskId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }

        task.Status = BackgroundTaskStatus.Cancelled;
        task.CompletedAt = DateTime.UtcNow;
        return true;
    }

    /// <summary>
    /// Gets queue statistics.
    /// </summary>
    public QueueStatistics GetQueueStatistics()
    {
        return _taskQueue.GetStatistics();
    }

    /// <summary>
    /// Registers an auto-decision policy.
    /// </summary>
    /// <param name="policy">Policy to register.</param>
    public void RegisterPolicy(AutoDecisionPolicy policy)
    {
        _autoDecision.RegisterPolicy(policy);
    }

    private async Task ProcessQueueAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _concurrencySemaphore.WaitAsync(ct);

                var task = _taskQueue.Dequeue();
                if (task == null)
                {
                    _concurrencySemaphore.Release();
                    await Task.Delay(100, ct);
                    continue;
                }

                _ = ExecuteTaskAsync(task, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Log and continue
            }
        }
    }

    private async Task ExecuteTaskAsync(BackgroundTask task, CancellationToken ct)
    {
        var taskCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        taskCts.CancelAfter(task.TimeoutMs);
        _runningTasks[task.TaskId] = taskCts;

        try
        {
            task.Status = BackgroundTaskStatus.Running;
            task.StartedAt = DateTime.UtcNow;

            task.Result = await task.Work(taskCts.Token);
            task.Status = BackgroundTaskStatus.Completed;
        }
        catch (OperationCanceledException)
        {
            task.Status = taskCts.IsCancellationRequested && !ct.IsCancellationRequested
                ? BackgroundTaskStatus.TimedOut
                : BackgroundTaskStatus.Cancelled;
        }
        catch (Exception ex)
        {
            task.Error = ex.Message;

            // Retry logic
            if (task.RetryCount < task.MaxRetries && bool.Parse(GetConfig("EnableAutoRetry") ?? "true"))
            {
                task.RetryCount++;
                task.Status = BackgroundTaskStatus.Queued;
                _taskQueue.Enqueue(task);
            }
            else
            {
                task.Status = BackgroundTaskStatus.Failed;
            }
        }
        finally
        {
            task.CompletedAt = DateTime.UtcNow;
            _runningTasks.TryRemove(task.TaskId, out _);
            taskCts.Dispose();
            _concurrencySemaphore.Release();
        }
    }
}

#endregion

#region K3: Scheduled Mode

/// <summary>
/// Represents a scheduled task.
/// </summary>
public sealed class ScheduledTask
{
    /// <summary>Task identifier.</summary>
    public string TaskId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Task name.</summary>
    public required string Name { get; init; }

    /// <summary>Cron expression for scheduling.</summary>
    public required string CronExpression { get; init; }

    /// <summary>The work to execute.</summary>
    public required Func<CancellationToken, Task<object?>> Work { get; init; }

    /// <summary>Whether task is enabled.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>When task was created.</summary>
    public DateTime CreatedAt { get; } = DateTime.UtcNow;

    /// <summary>Next scheduled execution.</summary>
    public DateTime? NextRunAt { get; set; }

    /// <summary>Last execution time.</summary>
    public DateTime? LastRunAt { get; set; }

    /// <summary>Last execution result.</summary>
    public object? LastResult { get; set; }

    /// <summary>Last error if any.</summary>
    public string? LastError { get; set; }

    /// <summary>Total execution count.</summary>
    public int ExecutionCount { get; set; }

    /// <summary>Timezone for schedule.</summary>
    public string TimeZone { get; init; } = "UTC";

    /// <summary>Task metadata.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Manages scheduled tasks with cron expressions.
/// </summary>
public sealed class ScheduledTasks : FeatureStrategyBase
{
    private readonly ConcurrentDictionary<string, ScheduledTask> _tasks = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _runningTasks = new();
    private CancellationTokenSource? _schedulerCts;
    private Task? _schedulerTask;
    private bool _isRunning;

    /// <inheritdoc/>
    public override string StrategyId => "mode-scheduled-tasks";

    /// <inheritdoc/>
    public override string StrategyName => "Scheduled Task Manager";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Scheduled Tasks",
        Description = "Manages scheduled tasks using cron expressions for periodic execution",
        Capabilities = IntelligenceCapabilities.TaskPlanning,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "SchedulerIntervalMs", Description = "Scheduler check interval", Required = false, DefaultValue = "1000" },
            new ConfigurationRequirement { Key = "MaxConcurrentTasks", Description = "Maximum concurrent scheduled tasks", Required = false, DefaultValue = "10" }
        },
        CostTier = 1,
        LatencyTier = 1,
        RequiresNetworkAccess = false,
        SupportsOfflineMode = true,
        Tags = new[] { "scheduled", "cron", "periodic", "timer" }
    };

    /// <summary>
    /// Starts the scheduler.
    /// </summary>
    public void Start()
    {
        if (_isRunning) return;

        _schedulerCts = new CancellationTokenSource();
        _schedulerTask = RunSchedulerAsync(_schedulerCts.Token);
        _isRunning = true;
    }

    /// <summary>
    /// Stops the scheduler.
    /// </summary>
    public async Task StopAsync()
    {
        if (!_isRunning) return;

        _schedulerCts?.Cancel();

        if (_schedulerTask != null)
        {
            try { await _schedulerTask; }
            catch (OperationCanceledException) { }
        }

        _isRunning = false;
    }

    /// <summary>
    /// Creates a scheduled task.
    /// </summary>
    /// <param name="task">Task to schedule.</param>
    /// <returns>The scheduled task.</returns>
    public ScheduledTask CreateTask(ScheduledTask task)
    {
        task.NextRunAt = CalculateNextRun(task.CronExpression, DateTime.UtcNow);
        _tasks[task.TaskId] = task;
        return task;
    }

    /// <summary>
    /// Updates a scheduled task.
    /// </summary>
    /// <param name="taskId">Task identifier.</param>
    /// <param name="cronExpression">New cron expression.</param>
    /// <param name="isEnabled">Whether task is enabled.</param>
    public void UpdateTask(string taskId, string? cronExpression = null, bool? isEnabled = null)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
            throw new InvalidOperationException($"Task {taskId} not found");

        if (isEnabled.HasValue)
            task.IsEnabled = isEnabled.Value;

        if (cronExpression != null)
        {
            // Validate and update - would need actual cron parsing
            task.NextRunAt = CalculateNextRun(cronExpression, DateTime.UtcNow);
        }
    }

    /// <summary>
    /// Deletes a scheduled task.
    /// </summary>
    /// <param name="taskId">Task identifier.</param>
    public void DeleteTask(string taskId)
    {
        _tasks.TryRemove(taskId, out _);

        if (_runningTasks.TryRemove(taskId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }
    }

    /// <summary>
    /// Lists all scheduled tasks.
    /// </summary>
    public IReadOnlyList<ScheduledTask> ListTasks()
    {
        return _tasks.Values.ToList();
    }

    /// <summary>
    /// Gets a task by ID.
    /// </summary>
    /// <param name="taskId">Task identifier.</param>
    public ScheduledTask? GetTask(string taskId)
    {
        return _tasks.TryGetValue(taskId, out var task) ? task : null;
    }

    private async Task RunSchedulerAsync(CancellationToken ct)
    {
        var intervalMs = int.Parse(GetConfig("SchedulerIntervalMs") ?? "1000");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;

                foreach (var task in _tasks.Values.Where(t => t.IsEnabled))
                {
                    if (task.NextRunAt.HasValue && task.NextRunAt <= now && !_runningTasks.ContainsKey(task.TaskId))
                    {
                        _ = ExecuteScheduledTaskAsync(task, ct);
                    }
                }

                await Task.Delay(intervalMs, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Log and continue
            }
        }
    }

    private async Task ExecuteScheduledTaskAsync(ScheduledTask task, CancellationToken ct)
    {
        var taskCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runningTasks[task.TaskId] = taskCts;

        try
        {
            task.LastRunAt = DateTime.UtcNow;
            task.LastResult = await task.Work(taskCts.Token);
            task.LastError = null;
            task.ExecutionCount++;
        }
        catch (Exception ex)
        {
            task.LastError = ex.Message;
        }
        finally
        {
            task.NextRunAt = CalculateNextRun(task.CronExpression, DateTime.UtcNow);
            _runningTasks.TryRemove(task.TaskId, out _);
            taskCts.Dispose();
        }
    }

    /// <summary>
    /// Calculates next run time from cron expression.
    /// </summary>
    /// <param name="cronExpression">Cron expression.</param>
    /// <param name="from">Start time.</param>
    /// <returns>Next run time.</returns>
    public static DateTime? CalculateNextRun(string cronExpression, DateTime from)
    {
        // Simplified cron parsing - supports basic patterns
        // Full implementation would use a proper cron library
        var parts = cronExpression.Split(' ');
        if (parts.Length < 5) return null;

        try
        {
            var next = from.AddMinutes(1);

            // Handle common patterns
            if (cronExpression == "* * * * *")
                return next; // Every minute

            if (cronExpression.StartsWith("*/"))
            {
                var interval = int.Parse(parts[0].Substring(2));
                var minutesToAdd = interval - (next.Minute % interval);
                return next.AddMinutes(minutesToAdd);
            }

            if (cronExpression == "0 * * * *")
                return new DateTime(next.Year, next.Month, next.Day, next.Hour + 1, 0, 0, DateTimeKind.Utc);

            if (cronExpression == "0 0 * * *")
                return next.Date.AddDays(1);

            // Default: next hour
            return new DateTime(next.Year, next.Month, next.Day, next.Hour + 1, 0, 0, DateTimeKind.Utc);
        }
        catch
        {
            return from.AddHours(1);
        }
    }
}

/// <summary>
/// Type of scheduled report.
/// </summary>
public enum ReportFrequency
{
    /// <summary>Daily report.</summary>
    Daily,

    /// <summary>Weekly report.</summary>
    Weekly,

    /// <summary>Monthly report.</summary>
    Monthly,

    /// <summary>Custom schedule.</summary>
    Custom
}

/// <summary>
/// Generates scheduled reports.
/// </summary>
public sealed class ReportGenerator : FeatureStrategyBase
{
    private readonly ScheduledTasks _scheduler;
    private readonly ConcurrentDictionary<string, ReportDefinition> _reports = new();

    /// <summary>
    /// Initializes the report generator.
    /// </summary>
    /// <param name="scheduler">Scheduler to use.</param>
    public ReportGenerator(ScheduledTasks scheduler)
    {
        _scheduler = scheduler;
    }

    /// <inheritdoc/>
    public override string StrategyId => "mode-report-generator";

    /// <inheritdoc/>
    public override string StrategyName => "Scheduled Report Generator";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Report Generator",
        Description = "Generates scheduled reports with customizable frequency and content",
        Capabilities = IntelligenceCapabilities.Summarization,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "DefaultFormat", Description = "Default report format", Required = false, DefaultValue = "json" },
            new ConfigurationRequirement { Key = "OutputPath", Description = "Report output path", Required = false, DefaultValue = "./reports" }
        },
        CostTier = 2,
        LatencyTier = 3,
        RequiresNetworkAccess = false,
        SupportsOfflineMode = true,
        Tags = new[] { "reports", "scheduled", "analytics" }
    };

    /// <summary>
    /// Creates a scheduled report.
    /// </summary>
    /// <param name="definition">Report definition.</param>
    /// <returns>The created report definition.</returns>
    public ReportDefinition CreateReport(ReportDefinition definition)
    {
        _reports[definition.ReportId] = definition;

        // Create scheduled task
        var cronExpression = definition.Frequency switch
        {
            ReportFrequency.Daily => "0 0 * * *",      // Midnight daily
            ReportFrequency.Weekly => "0 0 * * 0",     // Midnight Sunday
            ReportFrequency.Monthly => "0 0 1 * *",    // Midnight first of month
            ReportFrequency.Custom => definition.CustomCron ?? "0 0 * * *",
            _ => "0 0 * * *"
        };

        var task = new ScheduledTask
        {
            Name = $"Report: {definition.Name}",
            CronExpression = cronExpression,
            Work = async ct => await GenerateReportAsync(definition.ReportId, ct)
        };

        _scheduler.CreateTask(task);
        definition.ScheduledTaskId = task.TaskId;

        return definition;
    }

    /// <summary>
    /// Generates a report immediately.
    /// </summary>
    /// <param name="reportId">Report identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Generated report.</returns>
    public async Task<GeneratedReport> GenerateReportAsync(string reportId, CancellationToken ct = default)
    {
        if (!_reports.TryGetValue(reportId, out var definition))
            throw new InvalidOperationException($"Report {reportId} not found");

        var report = new GeneratedReport
        {
            ReportId = reportId,
            Title = definition.Name,
            GeneratedAt = DateTime.UtcNow,
            Sections = new List<ReportSection>()
        };

        // Generate sections based on data sources
        foreach (var source in definition.DataSources)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var data = await source.FetchData(ct);
                report.Sections.Add(new ReportSection
                {
                    Title = source.Name,
                    Content = FormatData(data, definition.Format)
                });
            }
            catch (Exception ex)
            {
                report.Sections.Add(new ReportSection
                {
                    Title = source.Name,
                    Content = $"Error fetching data: {ex.Message}"
                });
            }
        }

        // AI summary if provider available
        if (AIProvider != null && definition.IncludeAISummary)
        {
            try
            {
                var summary = await GenerateAISummaryAsync(report, ct);
                report.Summary = summary;
            }
            catch
            {
                report.Summary = "AI summary unavailable";
            }
        }

        definition.LastGenerated = report.GeneratedAt;
        definition.GenerationCount++;

        return report;
    }

    /// <summary>
    /// Lists all report definitions.
    /// </summary>
    public IReadOnlyList<ReportDefinition> ListReports()
    {
        return _reports.Values.ToList();
    }

    /// <summary>
    /// Deletes a report definition.
    /// </summary>
    /// <param name="reportId">Report identifier.</param>
    public void DeleteReport(string reportId)
    {
        if (_reports.TryRemove(reportId, out var definition) && definition.ScheduledTaskId != null)
        {
            _scheduler.DeleteTask(definition.ScheduledTaskId);
        }
    }

    private string FormatData(object? data, string format)
    {
        return format.ToLower() switch
        {
            "json" => JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }),
            "text" => data?.ToString() ?? "",
            _ => data?.ToString() ?? ""
        };
    }

    private async Task<string> GenerateAISummaryAsync(GeneratedReport report, CancellationToken ct)
    {
        var content = string.Join("\n\n", report.Sections.Select(s => $"## {s.Title}\n{s.Content}"));
        var prompt = $"Summarize this report in 2-3 sentences:\n\n{content}";

        var response = await AIProvider!.CompleteAsync(new AIRequest
        {
            Prompt = prompt,
            MaxTokens = 200
        }, ct);

        return response.Content.Trim();
    }
}

/// <summary>
/// Definition of a scheduled report.
/// </summary>
public sealed class ReportDefinition
{
    /// <summary>Report identifier.</summary>
    public string ReportId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Report name.</summary>
    public required string Name { get; init; }

    /// <summary>Report frequency.</summary>
    public ReportFrequency Frequency { get; init; } = ReportFrequency.Daily;

    /// <summary>Custom cron expression.</summary>
    public string? CustomCron { get; init; }

    /// <summary>Output format.</summary>
    public string Format { get; init; } = "json";

    /// <summary>Data sources for the report.</summary>
    public List<ReportDataSource> DataSources { get; init; } = new();

    /// <summary>Whether to include AI summary.</summary>
    public bool IncludeAISummary { get; init; } = true;

    /// <summary>Scheduled task ID.</summary>
    public string? ScheduledTaskId { get; set; }

    /// <summary>Last generation time.</summary>
    public DateTime? LastGenerated { get; set; }

    /// <summary>Total generations.</summary>
    public int GenerationCount { get; set; }
}

/// <summary>
/// Data source for a report.
/// </summary>
public sealed class ReportDataSource
{
    /// <summary>Source name.</summary>
    public required string Name { get; init; }

    /// <summary>Function to fetch data.</summary>
    public required Func<CancellationToken, Task<object?>> FetchData { get; init; }
}

/// <summary>
/// A generated report.
/// </summary>
public sealed class GeneratedReport
{
    /// <summary>Report definition ID.</summary>
    public required string ReportId { get; init; }

    /// <summary>Report title.</summary>
    public required string Title { get; init; }

    /// <summary>When report was generated.</summary>
    public DateTime GeneratedAt { get; init; }

    /// <summary>AI-generated summary.</summary>
    public string? Summary { get; set; }

    /// <summary>Report sections.</summary>
    public List<ReportSection> Sections { get; init; } = new();
}

/// <summary>
/// Section of a report.
/// </summary>
public sealed class ReportSection
{
    /// <summary>Section title.</summary>
    public required string Title { get; init; }

    /// <summary>Section content.</summary>
    public required string Content { get; init; }
}

#endregion

#region K4: Reactive Mode

/// <summary>
/// System event that can trigger reactions.
/// </summary>
public sealed record SystemEvent
{
    /// <summary>Event identifier.</summary>
    public string EventId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Event type.</summary>
    public required string EventType { get; init; }

    /// <summary>Event source.</summary>
    public required string Source { get; init; }

    /// <summary>Event timestamp.</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>Event payload.</summary>
    public Dictionary<string, object>? Payload { get; init; }

    /// <summary>Event severity.</summary>
    public EventSeverity Severity { get; init; } = EventSeverity.Info;
}

/// <summary>
/// Severity of an event.
/// </summary>
public enum EventSeverity
{
    /// <summary>Debug information.</summary>
    Debug,

    /// <summary>General information.</summary>
    Info,

    /// <summary>Warning.</summary>
    Warning,

    /// <summary>Error.</summary>
    Error,

    /// <summary>Critical error.</summary>
    Critical
}

/// <summary>
/// Listens to and distributes system events.
/// </summary>
public sealed class EventListener : FeatureStrategyBase
{
    private readonly ConcurrentDictionary<string, List<EventSubscription>> _subscriptions = new();
    private readonly ConcurrentQueue<SystemEvent> _eventHistory = new();
    private readonly int _maxHistorySize;

    /// <summary>
    /// Initializes the event listener.
    /// </summary>
    /// <param name="maxHistorySize">Maximum events to keep in history.</param>
    public EventListener(int maxHistorySize = 1000)
    {
        _maxHistorySize = maxHistorySize;
    }

    /// <inheritdoc/>
    public override string StrategyId => "mode-event-listener";

    /// <inheritdoc/>
    public override string StrategyName => "System Event Listener";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Event Listener",
        Description = "Listens to and distributes system events for reactive processing",
        Capabilities = IntelligenceCapabilities.AnomalyDetection,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "MaxHistorySize", Description = "Maximum events in history", Required = false, DefaultValue = "1000" },
            new ConfigurationRequirement { Key = "EnableFiltering", Description = "Enable event filtering", Required = false, DefaultValue = "true" }
        },
        CostTier = 1,
        LatencyTier = 1,
        RequiresNetworkAccess = false,
        SupportsOfflineMode = true,
        Tags = new[] { "events", "reactive", "listener", "pubsub" }
    };

    /// <summary>
    /// Subscribes to an event type.
    /// </summary>
    /// <param name="eventType">Event type pattern (supports wildcards).</param>
    /// <param name="handler">Handler to call when event occurs.</param>
    /// <returns>Subscription ID.</returns>
    public string Subscribe(string eventType, Func<SystemEvent, Task> handler)
    {
        var subscription = new EventSubscription
        {
            EventTypePattern = eventType,
            Handler = handler
        };

        _subscriptions.AddOrUpdate(
            eventType,
            _ => new List<EventSubscription> { subscription },
            (_, existing) =>
            {
                existing.Add(subscription);
                return existing;
            });

        return subscription.SubscriptionId;
    }

    /// <summary>
    /// Unsubscribes from events.
    /// </summary>
    /// <param name="subscriptionId">Subscription identifier.</param>
    public void Unsubscribe(string subscriptionId)
    {
        foreach (var kvp in _subscriptions)
        {
            kvp.Value.RemoveAll(s => s.SubscriptionId == subscriptionId);
        }
    }

    /// <summary>
    /// Publishes an event.
    /// </summary>
    /// <param name="evt">Event to publish.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task PublishAsync(SystemEvent evt, CancellationToken ct = default)
    {
        // Store in history
        _eventHistory.Enqueue(evt);
        while (_eventHistory.Count > _maxHistorySize)
            _eventHistory.TryDequeue(out _);

        // Find matching subscriptions
        var tasks = new List<Task>();

        foreach (var kvp in _subscriptions)
        {
            if (MatchesPattern(evt.EventType, kvp.Key))
            {
                foreach (var subscription in kvp.Value)
                {
                    tasks.Add(SafeInvokeHandler(subscription.Handler, evt));
                }
            }
        }

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Gets recent events.
    /// </summary>
    /// <param name="count">Number of events.</param>
    /// <param name="eventType">Optional type filter.</param>
    public IReadOnlyList<SystemEvent> GetRecentEvents(int count = 100, string? eventType = null)
    {
        var events = _eventHistory.AsEnumerable();

        if (eventType != null)
            events = events.Where(e => MatchesPattern(e.EventType, eventType));

        return events.TakeLast(count).ToList();
    }

    private static bool MatchesPattern(string eventType, string pattern)
    {
        if (pattern == "*") return true;
        if (pattern.EndsWith("*"))
            return eventType.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        return eventType.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task SafeInvokeHandler(Func<SystemEvent, Task> handler, SystemEvent evt)
    {
        try
        {
            await handler(evt);
        }
        catch
        {
            // Log error but don't propagate
        }
    }
}

/// <summary>
/// Event subscription.
/// </summary>
public sealed class EventSubscription
{
    /// <summary>Subscription identifier.</summary>
    public string SubscriptionId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Event type pattern.</summary>
    public required string EventTypePattern { get; init; }

    /// <summary>Handler function.</summary>
    public required Func<SystemEvent, Task> Handler { get; init; }

    /// <summary>When subscription was created.</summary>
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
}

/// <summary>
/// Defines a trigger condition and action.
/// </summary>
public sealed class TriggerDefinition
{
    /// <summary>Trigger identifier.</summary>
    public string TriggerId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Trigger name.</summary>
    public required string Name { get; init; }

    /// <summary>Condition to evaluate.</summary>
    public required TriggerCondition Condition { get; init; }

    /// <summary>Action to execute when triggered.</summary>
    public required Func<SystemEvent, CancellationToken, Task> Action { get; init; }

    /// <summary>Whether trigger is enabled.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Cooldown period in seconds.</summary>
    public int CooldownSeconds { get; init; } = 60;

    /// <summary>Last triggered time.</summary>
    public DateTime? LastTriggeredAt { get; set; }

    /// <summary>Total trigger count.</summary>
    public int TriggerCount { get; set; }
}

/// <summary>
/// Condition for a trigger.
/// </summary>
public sealed class TriggerCondition
{
    /// <summary>Event type to match.</summary>
    public required string EventType { get; init; }

    /// <summary>Minimum severity.</summary>
    public EventSeverity? MinSeverity { get; init; }

    /// <summary>Payload conditions (field -> expected value).</summary>
    public Dictionary<string, object>? PayloadConditions { get; init; }

    /// <summary>Custom condition predicate.</summary>
    public Func<SystemEvent, bool>? CustomPredicate { get; init; }

    /// <summary>
    /// Evaluates if an event matches this condition.
    /// </summary>
    public bool Matches(SystemEvent evt)
    {
        // Check event type
        if (!MatchesPattern(evt.EventType, EventType))
            return false;

        // Check severity
        if (MinSeverity.HasValue && evt.Severity < MinSeverity.Value)
            return false;

        // Check payload conditions
        if (PayloadConditions != null && evt.Payload != null)
        {
            foreach (var kvp in PayloadConditions)
            {
                if (!evt.Payload.TryGetValue(kvp.Key, out var value) || !Equals(value, kvp.Value))
                    return false;
            }
        }

        // Check custom predicate
        if (CustomPredicate != null && !CustomPredicate(evt))
            return false;

        return true;
    }

    private static bool MatchesPattern(string eventType, string pattern)
    {
        if (pattern == "*") return true;
        if (pattern.EndsWith("*"))
            return eventType.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        return eventType.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Manages trigger definitions and execution.
/// </summary>
public sealed class TriggerEngine : FeatureStrategyBase
{
    private readonly EventListener _eventListener;
    private readonly ConcurrentDictionary<string, TriggerDefinition> _triggers = new();

    /// <summary>
    /// Initializes the trigger engine.
    /// </summary>
    /// <param name="eventListener">Event listener to subscribe to.</param>
    public TriggerEngine(EventListener eventListener)
    {
        _eventListener = eventListener;

        // Subscribe to all events
        _eventListener.Subscribe("*", ProcessEventAsync);
    }

    /// <inheritdoc/>
    public override string StrategyId => "mode-trigger-engine";

    /// <inheritdoc/>
    public override string StrategyName => "Trigger Engine";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Trigger Engine",
        Description = "Evaluates conditions and executes actions in response to events",
        Capabilities = IntelligenceCapabilities.AnomalyDetection,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "MaxConcurrentActions", Description = "Maximum concurrent trigger actions", Required = false, DefaultValue = "5" },
            new ConfigurationRequirement { Key = "DefaultCooldownSeconds", Description = "Default trigger cooldown", Required = false, DefaultValue = "60" }
        },
        CostTier = 1,
        LatencyTier = 1,
        RequiresNetworkAccess = false,
        SupportsOfflineMode = true,
        Tags = new[] { "triggers", "reactive", "automation", "conditions" }
    };

    /// <summary>
    /// Creates a trigger.
    /// </summary>
    /// <param name="trigger">Trigger definition.</param>
    /// <returns>The created trigger.</returns>
    public TriggerDefinition CreateTrigger(TriggerDefinition trigger)
    {
        _triggers[trigger.TriggerId] = trigger;
        return trigger;
    }

    /// <summary>
    /// Deletes a trigger.
    /// </summary>
    /// <param name="triggerId">Trigger identifier.</param>
    public void DeleteTrigger(string triggerId)
    {
        _triggers.TryRemove(triggerId, out _);
    }

    /// <summary>
    /// Lists all triggers.
    /// </summary>
    public IReadOnlyList<TriggerDefinition> ListTriggers()
    {
        return _triggers.Values.ToList();
    }

    /// <summary>
    /// Enables or disables a trigger.
    /// </summary>
    /// <param name="triggerId">Trigger identifier.</param>
    /// <param name="enabled">Whether to enable.</param>
    public void SetTriggerEnabled(string triggerId, bool enabled)
    {
        if (_triggers.TryGetValue(triggerId, out var trigger))
            trigger.IsEnabled = enabled;
    }

    private async Task ProcessEventAsync(SystemEvent evt)
    {
        var now = DateTime.UtcNow;
        var tasks = new List<Task>();

        foreach (var trigger in _triggers.Values.Where(t => t.IsEnabled))
        {
            // Check cooldown
            if (trigger.LastTriggeredAt.HasValue &&
                (now - trigger.LastTriggeredAt.Value).TotalSeconds < trigger.CooldownSeconds)
                continue;

            // Check condition
            if (trigger.Condition.Matches(evt))
            {
                trigger.LastTriggeredAt = now;
                trigger.TriggerCount++;
                tasks.Add(ExecuteTriggerAction(trigger, evt));
            }
        }

        await Task.WhenAll(tasks);
    }

    private async Task ExecuteTriggerAction(TriggerDefinition trigger, SystemEvent evt)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            await trigger.Action(evt, cts.Token);
        }
        catch
        {
            // Log error but don't propagate
        }
    }
}

/// <summary>
/// Automatically responds to detected anomalies.
/// </summary>
public sealed class AnomalyResponder : FeatureStrategyBase
{
    private readonly EventListener _eventListener;
    private readonly TriggerEngine _triggerEngine;
    private readonly ConcurrentDictionary<string, AnomalyResponse> _responses = new();

    /// <summary>
    /// Initializes the anomaly responder.
    /// </summary>
    /// <param name="eventListener">Event listener.</param>
    /// <param name="triggerEngine">Trigger engine.</param>
    public AnomalyResponder(EventListener eventListener, TriggerEngine triggerEngine)
    {
        _eventListener = eventListener;
        _triggerEngine = triggerEngine;

        // Subscribe to anomaly events
        _eventListener.Subscribe("anomaly.*", HandleAnomalyAsync);
    }

    /// <inheritdoc/>
    public override string StrategyId => "mode-anomaly-responder";

    /// <inheritdoc/>
    public override string StrategyName => "Anomaly Auto-Responder";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Anomaly Responder",
        Description = "Automatically responds to detected anomalies with configurable actions",
        Capabilities = IntelligenceCapabilities.AnomalyDetection,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "AutoResponseEnabled", Description = "Enable automatic responses", Required = false, DefaultValue = "true" },
            new ConfigurationRequirement { Key = "MaxResponsesPerMinute", Description = "Rate limit for responses", Required = false, DefaultValue = "10" },
            new ConfigurationRequirement { Key = "MinSeverityForResponse", Description = "Minimum severity to trigger response", Required = false, DefaultValue = "Warning" }
        },
        CostTier = 2,
        LatencyTier = 1,
        RequiresNetworkAccess = false,
        SupportsOfflineMode = true,
        Tags = new[] { "anomaly", "auto-response", "reactive", "security" }
    };

    /// <summary>
    /// Registers an anomaly response.
    /// </summary>
    /// <param name="response">Response to register.</param>
    public AnomalyResponse RegisterResponse(AnomalyResponse response)
    {
        _responses[response.ResponseId] = response;

        // Create trigger for this response
        var trigger = new TriggerDefinition
        {
            Name = $"Anomaly Response: {response.Name}",
            Condition = new TriggerCondition
            {
                EventType = response.AnomalyType,
                MinSeverity = response.MinSeverity
            },
            Action = async (evt, ct) => await ExecuteResponseAsync(response, evt, ct),
            CooldownSeconds = response.CooldownSeconds
        };

        _triggerEngine.CreateTrigger(trigger);
        response.TriggerId = trigger.TriggerId;

        return response;
    }

    /// <summary>
    /// Unregisters an anomaly response.
    /// </summary>
    /// <param name="responseId">Response identifier.</param>
    public void UnregisterResponse(string responseId)
    {
        if (_responses.TryRemove(responseId, out var response) && response.TriggerId != null)
        {
            _triggerEngine.DeleteTrigger(response.TriggerId);
        }
    }

    /// <summary>
    /// Lists all registered responses.
    /// </summary>
    public IReadOnlyList<AnomalyResponse> ListResponses()
    {
        return _responses.Values.ToList();
    }

    private async Task HandleAnomalyAsync(SystemEvent evt)
    {
        if (!bool.Parse(GetConfig("AutoResponseEnabled") ?? "true"))
            return;

        var minSeverity = Enum.Parse<EventSeverity>(GetConfig("MinSeverityForResponse") ?? "Warning");
        if (evt.Severity < minSeverity)
            return;

        // Find matching responses
        foreach (var response in _responses.Values.Where(r => r.IsEnabled))
        {
            if (MatchesAnomalyType(evt.EventType, response.AnomalyType))
            {
                await ExecuteResponseAsync(response, evt, CancellationToken.None);
            }
        }
    }

    private async Task ExecuteResponseAsync(AnomalyResponse response, SystemEvent evt, CancellationToken ct)
    {
        try
        {
            response.LastTriggeredAt = DateTime.UtcNow;
            response.TriggerCount++;

            foreach (var action in response.Actions)
            {
                ct.ThrowIfCancellationRequested();
                await action(evt, ct);
            }
        }
        catch
        {
            // Log error but don't propagate
        }
    }

    private static bool MatchesAnomalyType(string eventType, string pattern)
    {
        if (pattern == "*") return true;
        if (pattern.EndsWith("*"))
            return eventType.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        return eventType.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Definition of an automatic anomaly response.
/// </summary>
public sealed class AnomalyResponse
{
    /// <summary>Response identifier.</summary>
    public string ResponseId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Response name.</summary>
    public required string Name { get; init; }

    /// <summary>Anomaly type pattern to match.</summary>
    public required string AnomalyType { get; init; }

    /// <summary>Minimum severity to trigger.</summary>
    public EventSeverity MinSeverity { get; init; } = EventSeverity.Warning;

    /// <summary>Actions to execute.</summary>
    public List<Func<SystemEvent, CancellationToken, Task>> Actions { get; init; } = new();

    /// <summary>Whether response is enabled.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Cooldown in seconds.</summary>
    public int CooldownSeconds { get; init; } = 300;

    /// <summary>Associated trigger ID.</summary>
    public string? TriggerId { get; set; }

    /// <summary>Last triggered time.</summary>
    public DateTime? LastTriggeredAt { get; set; }

    /// <summary>Total trigger count.</summary>
    public int TriggerCount { get; set; }
}

#endregion

#region Mode Coordinator

/// <summary>
/// Coordinates all interaction modes.
/// </summary>
public sealed class InteractionModeCoordinator : FeatureStrategyBase
{
    /// <summary>Chat handler for on-demand mode.</summary>
    public ChatHandler ChatHandler { get; }

    /// <summary>Background processor for background mode.</summary>
    public BackgroundProcessor BackgroundProcessor { get; }

    /// <summary>Scheduled tasks for scheduled mode.</summary>
    public ScheduledTasks ScheduledTasks { get; }

    /// <summary>Report generator for scheduled reports.</summary>
    public ReportGenerator ReportGenerator { get; }

    /// <summary>Event listener for reactive mode.</summary>
    public EventListener EventListener { get; }

    /// <summary>Trigger engine for reactive mode.</summary>
    public TriggerEngine TriggerEngine { get; }

    /// <summary>Anomaly responder for reactive mode.</summary>
    public AnomalyResponder AnomalyResponder { get; }

    /// <summary>
    /// Initializes all interaction modes.
    /// </summary>
    public InteractionModeCoordinator()
    {
        ChatHandler = new ChatHandler();
        BackgroundProcessor = new BackgroundProcessor();
        ScheduledTasks = new ScheduledTasks();
        ReportGenerator = new ReportGenerator(ScheduledTasks);
        EventListener = new EventListener();
        TriggerEngine = new TriggerEngine(EventListener);
        AnomalyResponder = new AnomalyResponder(EventListener, TriggerEngine);
    }

    /// <inheritdoc/>
    public override string StrategyId => "mode-coordinator";

    /// <inheritdoc/>
    public override string StrategyName => "Interaction Mode Coordinator";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Mode Coordinator",
        Description = "Coordinates all interaction modes: On-Demand, Background, Scheduled, and Reactive",
        Capabilities = IntelligenceCapabilities.ChatCompletion | IntelligenceCapabilities.TaskPlanning | IntelligenceCapabilities.AnomalyDetection,
        ConfigurationRequirements = Array.Empty<ConfigurationRequirement>(),
        CostTier = 2,
        LatencyTier = 2,
        RequiresNetworkAccess = true,
        Tags = new[] { "coordinator", "modes", "interaction" }
    };

    /// <summary>
    /// Initializes all modes with the AI provider.
    /// </summary>
    /// <param name="aiProvider">AI provider to use.</param>
    public void Initialize(IAIProvider aiProvider)
    {
        SetAIProvider(aiProvider);
        ChatHandler.Initialize(aiProvider);
        ReportGenerator.SetAIProvider(aiProvider);
    }

    /// <summary>
    /// Starts all background modes.
    /// </summary>
    public void StartBackgroundModes()
    {
        BackgroundProcessor.Start();
        ScheduledTasks.Start();
    }

    /// <summary>
    /// Stops all background modes.
    /// </summary>
    public async Task StopBackgroundModesAsync()
    {
        await BackgroundProcessor.StopAsync();
        await ScheduledTasks.StopAsync();
    }

    /// <summary>
    /// Gets overall mode statistics.
    /// </summary>
    public new ModeStatistics GetStatistics()
    {
        return new ModeStatistics
        {
            ActiveChatSessions = 0, // Would track in ChatHandler
            BackgroundQueueStats = BackgroundProcessor.GetQueueStatistics(),
            ScheduledTaskCount = ScheduledTasks.ListTasks().Count,
            ActiveTriggerCount = TriggerEngine.ListTriggers().Count(t => t.IsEnabled),
            AnomalyResponseCount = AnomalyResponder.ListResponses().Count
        };
    }
}

/// <summary>
/// Overall mode statistics.
/// </summary>
public sealed record ModeStatistics
{
    /// <summary>Active chat sessions.</summary>
    public int ActiveChatSessions { get; init; }

    /// <summary>Background queue statistics.</summary>
    public QueueStatistics? BackgroundQueueStats { get; init; }

    /// <summary>Scheduled task count.</summary>
    public int ScheduledTaskCount { get; init; }

    /// <summary>Active trigger count.</summary>
    public int ActiveTriggerCount { get; init; }

    /// <summary>Anomaly response count.</summary>
    public int AnomalyResponseCount { get; init; }
}

#endregion
