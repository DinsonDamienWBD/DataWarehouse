// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.AI;

namespace DataWarehouse.Plugins.UltimateIntelligence.Capabilities;

#region Request/Response Types

/// <summary>
/// Unified chat request supporting multi-turn conversations, function calling, vision, and streaming.
/// </summary>
public sealed record ChatRequest
{
    /// <summary>The model to use for chat.</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>The messages in the conversation.</summary>
    public List<ChatMessage> Messages { get; init; } = new();

    /// <summary>Optional conversation ID for multi-turn chat.</summary>
    public string? ConversationId { get; init; }

    /// <summary>Maximum tokens to generate.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>Temperature for randomness (0.0-2.0).</summary>
    public double? Temperature { get; init; }

    /// <summary>Top-p nucleus sampling.</summary>
    public double? TopP { get; init; }

    /// <summary>Frequency penalty (-2.0 to 2.0).</summary>
    public double? FrequencyPenalty { get; init; }

    /// <summary>Presence penalty (-2.0 to 2.0).</summary>
    public double? PresencePenalty { get; init; }

    /// <summary>Stop sequences to halt generation.</summary>
    public string[]? StopSequences { get; init; }

    /// <summary>Whether to stream the response.</summary>
    public bool Stream { get; init; }

    /// <summary>Tool/function definitions for function calling.</summary>
    public List<ToolDefinition>? Tools { get; init; }

    /// <summary>Tool choice mode ("auto", "none", or specific tool name).</summary>
    public string? ToolChoice { get; init; }

    /// <summary>Response format ("text" or "json").</summary>
    public string? ResponseFormat { get; init; }

    /// <summary>Unique user identifier for abuse monitoring.</summary>
    public string? User { get; init; }

    /// <summary>Provider-specific metadata.</summary>
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Unified chat response with content, tool calls, and token usage.
/// </summary>
public sealed record ChatResponse
{
    /// <summary>The model that generated the response.</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>The generated content.</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>Reason the response finished.</summary>
    public string? FinishReason { get; init; }

    /// <summary>Tool calls requested by the model.</summary>
    public List<ToolCall>? ToolCalls { get; init; }

    /// <summary>Input tokens consumed.</summary>
    public int InputTokens { get; init; }

    /// <summary>Output tokens generated.</summary>
    public int OutputTokens { get; init; }

    /// <summary>Total tokens (input + output).</summary>
    public int TotalTokens => InputTokens + OutputTokens;

    /// <summary>Conversation ID for multi-turn tracking.</summary>
    public string? ConversationId { get; init; }

    /// <summary>Turn number in the conversation.</summary>
    public int? TurnNumber { get; init; }

    /// <summary>Estimated cost in USD.</summary>
    public double? CostUsd { get; init; }

    /// <summary>Provider-specific metadata.</summary>
    public Dictionary<string, object>? Metadata { get; init; }
}

/// <summary>
/// Text completion request (legacy/non-chat models).
/// </summary>
public sealed record CompletionRequest
{
    /// <summary>The model to use.</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>The prompt text.</summary>
    public string Prompt { get; init; } = string.Empty;

    /// <summary>Maximum tokens to generate.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>Temperature for randomness.</summary>
    public double? Temperature { get; init; }

    /// <summary>Top-p nucleus sampling.</summary>
    public double? TopP { get; init; }

    /// <summary>Stop sequences.</summary>
    public string[]? StopSequences { get; init; }

    /// <summary>Provider-specific options.</summary>
    public Dictionary<string, object>? Options { get; init; }
}

/// <summary>
/// Text completion response.
/// </summary>
public sealed record CompletionResponse
{
    /// <summary>The generated text.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>The model used.</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>Finish reason.</summary>
    public string? FinishReason { get; init; }

    /// <summary>Input tokens.</summary>
    public int InputTokens { get; init; }

    /// <summary>Output tokens.</summary>
    public int OutputTokens { get; init; }

    /// <summary>Total tokens.</summary>
    public int TotalTokens => InputTokens + OutputTokens;
}

/// <summary>
/// Embedding generation request.
/// </summary>
public sealed record EmbeddingRequest
{
    /// <summary>The embedding model to use.</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>Single text to embed.</summary>
    public string? Input { get; init; }

    /// <summary>Multiple texts to embed.</summary>
    public string[]? Inputs { get; init; }

    /// <summary>Embedding dimensions (if supported by model).</summary>
    public int? Dimensions { get; init; }

    /// <summary>Encoding format ("float" or "base64").</summary>
    public string? EncodingFormat { get; init; }

    /// <summary>User identifier.</summary>
    public string? User { get; init; }
}

/// <summary>
/// Embedding generation response.
/// </summary>
public sealed record EmbeddingResponse
{
    /// <summary>The embeddings (one per input text).</summary>
    public List<EmbeddingData> Data { get; init; } = new();

    /// <summary>The model used.</summary>
    public string Model { get; init; } = string.Empty;

    /// <summary>Total tokens consumed.</summary>
    public int TotalTokens { get; init; }
}

/// <summary>
/// Single embedding vector.
/// </summary>
public sealed record EmbeddingData
{
    /// <summary>Index in the original input array.</summary>
    public int Index { get; init; }

    /// <summary>The embedding vector.</summary>
    public float[] Embedding { get; init; } = Array.Empty<float>();

    /// <summary>Object type (always "embedding").</summary>
    public string Object { get; init; } = "embedding";
}

#endregion

#region Chat Message Types

/// <summary>
/// Represents a message in a chat conversation.
/// </summary>
public sealed record ChatMessage
{
    /// <summary>Role of the message sender ("system", "user", "assistant", "tool").</summary>
    public string Role { get; init; } = string.Empty;

    /// <summary>The message content.</summary>
    public string? Content { get; init; }

    /// <summary>Optional name of the participant.</summary>
    public string? Name { get; init; }

    /// <summary>Tool calls (for assistant messages requesting tool execution).</summary>
    public List<ToolCall>? ToolCalls { get; init; }

    /// <summary>Tool call ID (for tool role messages returning results).</summary>
    public string? ToolCallId { get; init; }

    /// <summary>Vision attachments (images, videos).</summary>
    public List<VisionAttachment>? Attachments { get; init; }
}

/// <summary>
/// Vision attachment for multimodal messages.
/// </summary>
public sealed record VisionAttachment
{
    /// <summary>Attachment type ("image_url", "image_base64").</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>Image URL (if Type is "image_url").</summary>
    public string? ImageUrl { get; init; }

    /// <summary>Base64-encoded image data (if Type is "image_base64").</summary>
    public string? ImageBase64 { get; init; }

    /// <summary>Image MIME type (e.g., "image/png").</summary>
    public string? MimeType { get; init; }

    /// <summary>Detail level for vision models ("low", "high", "auto").</summary>
    public string? Detail { get; init; }
}

#endregion

#region Tool/Function Calling Types

/// <summary>
/// Tool/function definition for function calling.
/// </summary>
public sealed record ToolDefinition
{
    /// <summary>Tool type (currently only "function").</summary>
    public string Type { get; init; } = "function";

    /// <summary>The function definition.</summary>
    public FunctionDefinition Function { get; init; } = new();
}

/// <summary>
/// Function definition.
/// </summary>
public sealed record FunctionDefinition
{
    /// <summary>Function name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Function description for the model.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>JSON Schema for function parameters.</summary>
    public JsonElement? Parameters { get; init; }

    /// <summary>Whether this function is strict (exact schema match required).</summary>
    public bool? Strict { get; init; }
}

/// <summary>
/// Tool call made by the model.
/// </summary>
public sealed record ToolCall
{
    /// <summary>Unique identifier for this tool call.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Tool type (currently only "function").</summary>
    public string Type { get; init; } = "function";

    /// <summary>The function call details.</summary>
    public FunctionCall Function { get; init; } = new();
}

/// <summary>
/// Function call details.
/// </summary>
public sealed record FunctionCall
{
    /// <summary>Function name to call.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Function arguments as JSON string.</summary>
    public string Arguments { get; init; } = string.Empty;

    /// <summary>Parsed arguments (convenience property).</summary>
    public Dictionary<string, object>? ParsedArguments
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Arguments)) return null;
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, object>>(Arguments);
            }
            catch
            {
                return null;
            }
        }
    }
}

#endregion

#region Message Bus Handlers

/// <summary>
/// Handles intelligence.chat message bus topic for unified chat interface.
/// Routes chat requests to appropriate provider strategies.
/// </summary>
public sealed class ChatCapabilityHandler : IDisposable
{
    private readonly Func<string, IAIProvider?> _providerResolver;
    private readonly ConversationManager _conversationManager;
    private readonly FunctionCallingHandler _functionHandler;
    private readonly VisionHandler _visionHandler;
    private readonly StreamingHandler _streamingHandler;
    private readonly ChatConfig _config;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChatCapabilityHandler"/> class.
    /// </summary>
    /// <param name="providerResolver">Function to resolve AI providers by name.</param>
    /// <param name="config">Optional chat configuration.</param>
    public ChatCapabilityHandler(
        Func<string, IAIProvider?> providerResolver,
        ChatConfig? config = null)
    {
        _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
        _config = config ?? new ChatConfig();
        _conversationManager = new ConversationManager(_config.ConversationTTL, _config.MaxConversations);
        _functionHandler = new FunctionCallingHandler();
        _visionHandler = new VisionHandler();
        _streamingHandler = new StreamingHandler();
    }

    /// <summary>
    /// Handles a chat request with automatic conversation management.
    /// </summary>
    /// <param name="request">The chat request.</param>
    /// <param name="providerName">Optional provider name (defaults to config default).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The chat response.</returns>
    public async Task<ChatResponse> HandleChatAsync(
        ChatRequest request,
        string? providerName = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        providerName ??= _config.DefaultProvider;
        var provider = _providerResolver(providerName);
        if (provider == null)
        {
            throw new InvalidOperationException($"Provider '{providerName}' not found.");
        }

        var sw = Stopwatch.StartNew();

        // Handle conversation history if ConversationId is provided
        List<ChatMessage> messages;
        if (!string.IsNullOrEmpty(request.ConversationId))
        {
            var conversation = _conversationManager.GetOrCreate(request.ConversationId);
            messages = conversation.BuildMessages(request.Messages, _config.MaxHistoryTokens);
        }
        else
        {
            messages = request.Messages;
        }

        // Build AI request
        var aiRequest = new AIRequest
        {
            Prompt = BuildPromptFromMessages(messages),
            SystemMessage = ExtractSystemMessage(messages),
            Model = request.Model,
            MaxTokens = request.MaxTokens ?? _config.DefaultMaxTokens,
            Temperature = (float)(request.Temperature ?? _config.DefaultTemperature)
        };

        // Execute request
        var aiResponse = await provider.CompleteAsync(aiRequest, ct);
        sw.Stop();

        if (!aiResponse.Success)
        {
            throw new InvalidOperationException($"AI request failed: {aiResponse.ErrorMessage}");
        }

        // Update conversation if tracking
        if (!string.IsNullOrEmpty(request.ConversationId))
        {
            var conversation = _conversationManager.GetOrCreate(request.ConversationId);
            conversation.AddUserMessage(messages.Last().Content ?? string.Empty);
            conversation.AddAssistantMessage(aiResponse.Content);
        }

        // Build response
        return new ChatResponse
        {
            Model = request.Model,
            Content = aiResponse.Content,
            FinishReason = aiResponse.FinishReason ?? "stop",
            InputTokens = aiResponse.Usage?.PromptTokens ?? 0,
            OutputTokens = aiResponse.Usage?.CompletionTokens ?? 0,
            ConversationId = request.ConversationId,
            TurnNumber = !string.IsNullOrEmpty(request.ConversationId)
                ? _conversationManager.GetOrCreate(request.ConversationId).TurnCount
                : null,
            Metadata = new Dictionary<string, object>
            {
                ["provider"] = providerName,
                ["durationMs"] = sw.ElapsedMilliseconds
            }
        };
    }

    /// <summary>
    /// Handles a text completion request (non-chat models).
    /// </summary>
    /// <param name="request">The completion request.</param>
    /// <param name="providerName">Optional provider name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The completion response.</returns>
    public async Task<CompletionResponse> HandleCompleteAsync(
        CompletionRequest request,
        string? providerName = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        providerName ??= _config.DefaultProvider;
        var provider = _providerResolver(providerName);
        if (provider == null)
        {
            throw new InvalidOperationException($"Provider '{providerName}' not found.");
        }

        var aiRequest = new AIRequest
        {
            Prompt = request.Prompt,
            Model = request.Model,
            MaxTokens = request.MaxTokens ?? _config.DefaultMaxTokens,
            Temperature = (float)(request.Temperature ?? _config.DefaultTemperature)
        };

        var aiResponse = await provider.CompleteAsync(aiRequest, ct);

        if (!aiResponse.Success)
        {
            throw new InvalidOperationException($"Completion request failed: {aiResponse.ErrorMessage}");
        }

        return new CompletionResponse
        {
            Text = aiResponse.Content,
            Model = request.Model,
            FinishReason = aiResponse.FinishReason ?? "stop",
            InputTokens = aiResponse.Usage?.PromptTokens ?? 0,
            OutputTokens = aiResponse.Usage?.CompletionTokens ?? 0
        };
    }

    /// <summary>
    /// Handles an embedding generation request.
    /// </summary>
    /// <param name="request">The embedding request.</param>
    /// <param name="providerName">Optional provider name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The embedding response.</returns>
    public async Task<EmbeddingResponse> HandleEmbedAsync(
        EmbeddingRequest request,
        string? providerName = null,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        providerName ??= _config.DefaultEmbeddingProvider;
        var provider = _providerResolver(providerName);
        if (provider == null)
        {
            throw new InvalidOperationException($"Provider '{providerName}' not found.");
        }

        // Get inputs
        var inputs = request.Inputs ?? (request.Input != null ? new[] { request.Input } : Array.Empty<string>());
        if (inputs.Length == 0)
        {
            throw new ArgumentException("Either Input or Inputs must be provided.");
        }

        // Generate embeddings using batch API
        var embeddings = await provider.GetEmbeddingsBatchAsync(inputs, ct);

        // Convert to response format
        var data = new List<EmbeddingData>();
        for (int i = 0; i < embeddings.Length; i++)
        {
            data.Add(new EmbeddingData
            {
                Index = i,
                Embedding = embeddings[i]
            });
        }

        // Estimate token count (rough: ~1 token per 4 characters)
        var totalTokens = inputs.Sum(s => s.Length / 4);

        return new EmbeddingResponse
        {
            Data = data,
            Model = request.Model,
            TotalTokens = totalTokens
        };
    }

    /// <summary>
    /// Handles a streaming chat request.
    /// </summary>
    /// <param name="request">The chat request.</param>
    /// <param name="providerName">Optional provider name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async enumerable of response chunks.</returns>
    public async IAsyncEnumerable<string> HandleStreamAsync(
        ChatRequest request,
        string? providerName = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        providerName ??= _config.DefaultProvider;
        var provider = _providerResolver(providerName);
        if (provider == null)
        {
            throw new InvalidOperationException($"Provider '{providerName}' not found.");
        }

        // Build messages
        List<ChatMessage> messages;
        if (!string.IsNullOrEmpty(request.ConversationId))
        {
            var conversation = _conversationManager.GetOrCreate(request.ConversationId);
            messages = conversation.BuildMessages(request.Messages, _config.MaxHistoryTokens);
        }
        else
        {
            messages = request.Messages;
        }

        var prompt = BuildPromptFromMessages(messages);
        var systemMessage = ExtractSystemMessage(messages);

        // Stream response
        var fullContent = new StringBuilder();
        await foreach (var chunk in _streamingHandler.StreamAsync(provider, prompt, systemMessage, request.Model, ct))
        {
            fullContent.Append(chunk);
            yield return chunk;
        }

        // Update conversation after streaming completes
        if (!string.IsNullOrEmpty(request.ConversationId))
        {
            var conversation = _conversationManager.GetOrCreate(request.ConversationId);
            conversation.AddUserMessage(messages.Last().Content ?? string.Empty);
            conversation.AddAssistantMessage(fullContent.ToString());
        }
    }

    private string BuildPromptFromMessages(List<ChatMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var msg in messages.Where(m => m.Role != "system"))
        {
            var prefix = msg.Role == "user" ? "User" : "Assistant";
            sb.AppendLine($"{prefix}: {msg.Content}");
        }
        return sb.ToString().TrimEnd();
    }

    private string? ExtractSystemMessage(List<ChatMessage> messages)
    {
        return messages.FirstOrDefault(m => m.Role == "system")?.Content;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _conversationManager.Dispose();
    }
}

#endregion

#region Conversation Manager

/// <summary>
/// Manages conversation state with automatic cleanup and history trimming.
/// Supports up to 10,000 concurrent conversations with 24-hour TTL.
/// </summary>
public sealed class ConversationManager : IDisposable
{
    private readonly ConcurrentDictionary<string, Conversation> _conversations = new();
    private readonly Timer _cleanupTimer;
    private readonly TimeSpan _ttl;
    private readonly int _maxConversations;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConversationManager"/> class.
    /// </summary>
    /// <param name="ttl">Time-to-live for inactive conversations.</param>
    /// <param name="maxConversations">Maximum number of conversations to track.</param>
    public ConversationManager(TimeSpan ttl, int maxConversations)
    {
        _ttl = ttl;
        _maxConversations = maxConversations;

        // Periodic cleanup every 5 minutes
        _cleanupTimer = new Timer(
            CleanupExpired,
            null,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// Gets or creates a conversation by ID.
    /// </summary>
    /// <param name="conversationId">The conversation identifier.</param>
    /// <returns>The conversation instance.</returns>
    public Conversation GetOrCreate(string conversationId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);

        var conversation = _conversations.GetOrAdd(conversationId, id => new Conversation(id));
        conversation.Touch();

        // Enforce max conversations
        while (_conversations.Count > _maxConversations)
        {
            var oldest = _conversations
                .OrderBy(kvp => kvp.Value.LastAccessedAt)
                .FirstOrDefault();

            if (!string.IsNullOrEmpty(oldest.Key))
            {
                _conversations.TryRemove(oldest.Key, out _);
            }
            else
            {
                break;
            }
        }

        return conversation;
    }

    /// <summary>
    /// Removes a conversation.
    /// </summary>
    /// <param name="conversationId">The conversation identifier.</param>
    /// <returns>True if removed, false if not found.</returns>
    public bool Remove(string conversationId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _conversations.TryRemove(conversationId, out _);
    }

    /// <summary>
    /// Clears all conversations.
    /// </summary>
    public void Clear()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _conversations.Clear();
    }

    /// <summary>
    /// Gets the number of active conversations.
    /// </summary>
    public int Count => _conversations.Count;

    private void CleanupExpired(object? state)
    {
        if (_disposed) return;

        var now = DateTime.UtcNow;
        var expired = _conversations
            .Where(kvp => now - kvp.Value.LastAccessedAt > _ttl)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var id in expired)
        {
            _conversations.TryRemove(id, out _);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cleanupTimer.Dispose();
        _conversations.Clear();
    }
}

/// <summary>
/// Represents a conversation with message history.
/// </summary>
public sealed class Conversation
{
    private readonly List<ChatMessage> _messages = new();
    private readonly object _lock = new();

    /// <summary>
    /// Gets the conversation identifier.
    /// </summary>
    public string Id { get; }

    /// <summary>
    /// Gets the conversation creation time.
    /// </summary>
    public DateTime CreatedAt { get; }

    /// <summary>
    /// Gets or sets the last accessed time.
    /// </summary>
    public DateTime LastAccessedAt { get; private set; }

    /// <summary>
    /// Gets the number of turns (user-assistant pairs).
    /// </summary>
    public int TurnCount
    {
        get
        {
            lock (_lock)
            {
                return _messages.Count(m => m.Role == "user");
            }
        }
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Conversation"/> class.
    /// </summary>
    /// <param name="id">The conversation identifier.</param>
    public Conversation(string id)
    {
        Id = id;
        CreatedAt = DateTime.UtcNow;
        LastAccessedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Adds a user message to the conversation.
    /// </summary>
    /// <param name="content">The message content.</param>
    public void AddUserMessage(string content)
    {
        lock (_lock)
        {
            _messages.Add(new ChatMessage { Role = "user", Content = content });
            Touch();
        }
    }

    /// <summary>
    /// Adds an assistant message to the conversation.
    /// </summary>
    /// <param name="content">The message content.</param>
    public void AddAssistantMessage(string content)
    {
        lock (_lock)
        {
            _messages.Add(new ChatMessage { Role = "assistant", Content = content });
            Touch();
        }
    }

    /// <summary>
    /// Builds the full message list including history.
    /// Trims to fit within token budget using a simple heuristic.
    /// </summary>
    /// <param name="newMessages">New messages to append.</param>
    /// <param name="maxTokens">Maximum tokens for history (rough estimate: 4 chars = 1 token).</param>
    /// <returns>Combined message list with history.</returns>
    public List<ChatMessage> BuildMessages(List<ChatMessage> newMessages, int maxTokens)
    {
        lock (_lock)
        {
            var combined = new List<ChatMessage>(_messages);
            combined.AddRange(newMessages);

            // Simple token estimation: 4 characters ≈ 1 token
            var estimatedTokens = combined.Sum(m => (m.Content?.Length ?? 0) / 4);

            if (estimatedTokens <= maxTokens)
            {
                return combined;
            }

            // Trim older messages (keep system and most recent)
            var systemMessages = combined.Where(m => m.Role == "system").ToList();
            var otherMessages = combined.Where(m => m.Role != "system").ToList();

            var result = new List<ChatMessage>(systemMessages);
            var currentTokens = systemMessages.Sum(m => (m.Content?.Length ?? 0) / 4);

            // Add messages from most recent until we hit limit
            for (int i = otherMessages.Count - 1; i >= 0; i--)
            {
                var msgTokens = (otherMessages[i].Content?.Length ?? 0) / 4;
                if (currentTokens + msgTokens > maxTokens)
                {
                    break;
                }
                result.Insert(systemMessages.Count, otherMessages[i]);
                currentTokens += msgTokens;
            }

            return result;
        }
    }

    /// <summary>
    /// Clears all messages from the conversation.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _messages.Clear();
            Touch();
        }
    }

    /// <summary>
    /// Updates the last accessed timestamp.
    /// </summary>
    public void Touch()
    {
        LastAccessedAt = DateTime.UtcNow;
    }
}

#endregion

#region Function Calling Handler

/// <summary>
/// Handles tool/function calling execution and result formatting.
/// </summary>
public sealed class FunctionCallingHandler
{
    /// <summary>
    /// Parses tool definitions into provider-specific format.
    /// </summary>
    /// <param name="tools">The tool definitions.</param>
    /// <returns>Provider-formatted tool definitions.</returns>
    public object ParseToolDefinitions(List<ToolDefinition> tools)
    {
        // Convert to OpenAI-compatible format
        return tools.Select(t => new
        {
            type = t.Type,
            function = new
            {
                name = t.Function.Name,
                description = t.Function.Description,
                parameters = t.Function.Parameters
            }
        }).ToList();
    }

    /// <summary>
    /// Executes a tool call and returns the result.
    /// </summary>
    /// <param name="toolCall">The tool call to execute.</param>
    /// <param name="availableFunctions">Dictionary of available functions.</param>
    /// <returns>The tool execution result.</returns>
    public async Task<string> ExecuteToolCallAsync(
        ToolCall toolCall,
        Dictionary<string, Func<Dictionary<string, object>, Task<string>>> availableFunctions)
    {
        if (!availableFunctions.TryGetValue(toolCall.Function.Name, out var func))
        {
            return $"Error: Function '{toolCall.Function.Name}' not found.";
        }

        var args = toolCall.Function.ParsedArguments ?? new Dictionary<string, object>();

        try
        {
            return await func(args);
        }
        catch (Exception ex)
        {
            return $"Error executing {toolCall.Function.Name}: {ex.Message}";
        }
    }

    /// <summary>
    /// Formats a tool result message for the conversation.
    /// </summary>
    /// <param name="toolCallId">The tool call identifier.</param>
    /// <param name="result">The execution result.</param>
    /// <returns>A formatted tool result message.</returns>
    public ChatMessage FormatToolResult(string toolCallId, string result)
    {
        return new ChatMessage
        {
            Role = "tool",
            Content = result,
            ToolCallId = toolCallId
        };
    }
}

#endregion

#region Vision Handler

/// <summary>
/// Handles multimodal vision requests with image encoding and processing.
/// </summary>
public sealed class VisionHandler
{
    /// <summary>
    /// Encodes an image file to base64.
    /// </summary>
    /// <param name="imagePath">Path to the image file.</param>
    /// <returns>Base64-encoded image data.</returns>
    public async Task<string> EncodeImageToBase64Async(string imagePath)
    {
        var bytes = await File.ReadAllBytesAsync(imagePath);
        return Convert.ToBase64String(bytes);
    }

    /// <summary>
    /// Determines the MIME type from file extension.
    /// </summary>
    /// <param name="filePath">The file path.</param>
    /// <returns>MIME type string.</returns>
    public string GetMimeType(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            _ => "application/octet-stream"
        };
    }

    /// <summary>
    /// Creates a vision attachment from a file path.
    /// </summary>
    /// <param name="imagePath">Path to the image.</param>
    /// <returns>Vision attachment ready for chat message.</returns>
    public async Task<VisionAttachment> CreateAttachmentFromFileAsync(string imagePath)
    {
        var base64 = await EncodeImageToBase64Async(imagePath);
        var mimeType = GetMimeType(imagePath);

        return new VisionAttachment
        {
            Type = "image_base64",
            ImageBase64 = base64,
            MimeType = mimeType,
            Detail = "auto"
        };
    }

    /// <summary>
    /// Creates a vision attachment from a URL.
    /// </summary>
    /// <param name="imageUrl">The image URL.</param>
    /// <param name="detail">Detail level ("low", "high", "auto").</param>
    /// <returns>Vision attachment ready for chat message.</returns>
    public VisionAttachment CreateAttachmentFromUrl(string imageUrl, string detail = "auto")
    {
        return new VisionAttachment
        {
            Type = "image_url",
            ImageUrl = imageUrl,
            Detail = detail
        };
    }
}

#endregion

#region Streaming Handler

/// <summary>
/// Handles streaming responses with Server-Sent Events (SSE) formatting.
/// </summary>
public sealed class StreamingHandler
{
    /// <summary>
    /// Streams a chat response chunk by chunk.
    /// </summary>
    /// <param name="provider">The AI provider.</param>
    /// <param name="prompt">The prompt text.</param>
    /// <param name="systemMessage">Optional system message.</param>
    /// <param name="model">The model to use.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async enumerable of response chunks.</returns>
    public async IAsyncEnumerable<string> StreamAsync(
        IAIProvider provider,
        string prompt,
        string? systemMessage,
        string model,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request = new AIRequest
        {
            Prompt = prompt,
            SystemMessage = systemMessage,
            Model = model,
            MaxTokens = 4096,
            Temperature = 0.7f
        };

        // SDK interface doesn't support streaming, so we simulate it
        // In a real implementation, you'd use provider-specific streaming APIs
        var response = await provider.CompleteAsync(request, ct);

        if (!response.Success)
        {
            yield return $"Error: {response.ErrorMessage}";
            yield break;
        }

        // Simulate streaming by chunking the response
        const int chunkSize = 20; // characters per chunk
        var content = response.Content;

        for (int i = 0; i < content.Length; i += chunkSize)
        {
            var chunk = content.Substring(i, Math.Min(chunkSize, content.Length - i));
            yield return chunk;
            await Task.Delay(10, ct); // Simulate network delay
        }
    }

    /// <summary>
    /// Formats a chunk as a Server-Sent Event.
    /// </summary>
    /// <param name="chunk">The chunk content.</param>
    /// <param name="eventType">The event type.</param>
    /// <returns>SSE-formatted string.</returns>
    public string FormatSSE(string chunk, string eventType = "message")
    {
        return $"event: {eventType}\ndata: {chunk}\n\n";
    }
}

#endregion

#region Configuration

/// <summary>
/// Configuration for chat capabilities.
/// </summary>
public sealed class ChatConfig
{
    /// <summary>Default AI provider for chat.</summary>
    public string DefaultProvider { get; init; } = "openai";

    /// <summary>Default provider for embeddings.</summary>
    public string DefaultEmbeddingProvider { get; init; } = "openai";

    /// <summary>Default maximum tokens per response.</summary>
    public int DefaultMaxTokens { get; init; } = 4096;

    /// <summary>Default temperature.</summary>
    public double DefaultTemperature { get; init; } = 0.7;

    /// <summary>Maximum tokens for conversation history.</summary>
    public int MaxHistoryTokens { get; init; } = 8000;

    /// <summary>Conversation time-to-live.</summary>
    public TimeSpan ConversationTTL { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Maximum concurrent conversations.</summary>
    public int MaxConversations { get; init; } = 10_000;
}

#endregion
