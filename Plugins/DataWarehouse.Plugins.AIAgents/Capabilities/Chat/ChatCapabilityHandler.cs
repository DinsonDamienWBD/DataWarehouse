// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using DataWarehouse.Plugins.AIAgents.Capabilities;

namespace DataWarehouse.Plugins.AIAgents.Capabilities.Chat;

// Type aliases for types defined in AIAgentPlugin.cs
using IAIProvider = DataWarehouse.Plugins.AIAgents.IExtendedAIProvider;
using ChatRequest = DataWarehouse.Plugins.AIAgents.ChatRequest;
using ChatMessage = DataWarehouse.Plugins.AIAgents.ChatMessage;
using FunctionCallRequest = DataWarehouse.Plugins.AIAgents.FunctionCallRequest;
using FunctionDefinition = DataWarehouse.Plugins.AIAgents.FunctionDefinition;
using UsageLimits = DataWarehouse.Plugins.AIAgents.UsageLimits;

/// <summary>
/// Handles conversational AI capabilities including single-turn, multi-turn, and agent-based chat.
/// Provides conversation history management, context window optimization, and streaming support.
/// </summary>
/// <remarks>
/// <para>
/// This handler implements the Chat capability domain with the following sub-capabilities:
/// </para>
/// <list type="bullet">
/// <item><description>SingleTurnChat - Stateless question-answering without conversation history</description></item>
/// <item><description>MultiTurnChat - Conversational dialogue with history tracking</description></item>
/// <item><description>AgentChat - Agentic conversations with tool/function calling</description></item>
/// <item><description>StreamingChat - Real-time streaming responses</description></item>
/// </list>
/// <para>
/// The handler manages conversation contexts, handles context window limits,
/// and supports both synchronous and streaming response modes.
/// </para>
/// </remarks>
public sealed class ChatCapabilityHandler : ICapabilityHandler, IDisposable
{
    /// <summary>
    /// Single-turn chat capability identifier.
    /// </summary>
    public const string SingleTurnChat = "SingleTurnChat";

    /// <summary>
    /// Multi-turn chat capability identifier.
    /// </summary>
    public const string MultiTurnChat = "MultiTurnChat";

    /// <summary>
    /// Agent chat (with function calling) capability identifier.
    /// </summary>
    public const string AgentChat = "AgentChat";

    /// <summary>
    /// Streaming chat capability identifier.
    /// </summary>
    public const string StreamingChat = "StreamingChat";

    private readonly Func<string, IAIProvider?> _providerResolver;
    private readonly ChatConfig _config;
    private readonly ConcurrentDictionary<string, ConversationContext> _conversations = new();
    private readonly Timer _cleanupTimer;
    private bool _disposed;

    /// <inheritdoc/>
    public string CapabilityDomain => "Chat";

    /// <inheritdoc/>
    public string DisplayName => "Conversational AI";

    /// <inheritdoc/>
    public IReadOnlyList<string> SupportedCapabilities => new[]
    {
        SingleTurnChat,
        MultiTurnChat,
        AgentChat,
        StreamingChat
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="ChatCapabilityHandler"/> class.
    /// </summary>
    /// <param name="providerResolver">Function to resolve providers by name.</param>
    /// <param name="config">Optional configuration settings.</param>
    public ChatCapabilityHandler(
        Func<string, IAIProvider?> providerResolver,
        ChatConfig? config = null)
    {
        _providerResolver = providerResolver ?? throw new ArgumentNullException(nameof(providerResolver));
        _config = config ?? new ChatConfig();

        // Start cleanup timer for expired conversations
        _cleanupTimer = new Timer(
            CleanupExpiredConversations,
            null,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(5));
    }

    /// <inheritdoc/>
    public bool IsCapabilityEnabled(InstanceCapabilityConfig instanceConfig, string capability)
    {
        ArgumentNullException.ThrowIfNull(instanceConfig);

        // Check if explicitly disabled
        if (instanceConfig.DisabledCapabilities.Contains(capability))
            return false;

        // Check full domain.capability
        var fullCapability = $"{CapabilityDomain}.{capability}";
        if (instanceConfig.DisabledCapabilities.Contains(fullCapability))
            return false;

        // Check quota tier restrictions
        var limits = UsageLimits.GetDefaultLimits(instanceConfig.QuotaTier);

        // Streaming requires at least Basic tier
        if (capability == StreamingChat && !limits.StreamingEnabled)
            return false;

        // Agent chat (function calling) requires at least Basic tier
        if (capability == AgentChat && !limits.FunctionCallingEnabled)
            return false;

        return true;
    }

    /// <inheritdoc/>
    public string? GetProviderForCapability(InstanceCapabilityConfig instanceConfig, string capability)
    {
        ArgumentNullException.ThrowIfNull(instanceConfig);

        if (instanceConfig.CapabilityProviderMappings.TryGetValue(capability, out var provider))
            return provider;

        if (instanceConfig.CapabilityProviderMappings.TryGetValue(CapabilityDomain, out provider))
            return provider;

        // Default provider based on capability
        return capability switch
        {
            AgentChat => "openai", // Best function calling support
            _ => _config.DefaultProvider
        };
    }

    /// <summary>
    /// Executes a single-turn chat without conversation history.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <param name="request">The chat request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The chat result.</returns>
    public async Task<CapabilityResult<ChatResult>> ChatAsync(
        InstanceCapabilityConfig instanceConfig,
        SingleTurnChatRequest request,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsCapabilityEnabled(instanceConfig, SingleTurnChat))
            return CapabilityResult<ChatResult>.Disabled(SingleTurnChat);

        if (string.IsNullOrWhiteSpace(request.Message))
            return CapabilityResult<ChatResult>.Fail("Message cannot be empty.", "INVALID_INPUT");

        var providerName = GetProviderForCapability(instanceConfig, SingleTurnChat);
        if (string.IsNullOrEmpty(providerName))
            return CapabilityResult<ChatResult>.NoProvider(SingleTurnChat);

        var provider = _providerResolver(providerName);
        if (provider == null)
            return CapabilityResult<ChatResult>.Fail($"Provider '{providerName}' not available.", "PROVIDER_UNAVAILABLE");

        try
        {
            var sw = Stopwatch.StartNew();

            var messages = new List<ChatMessage>();
            if (!string.IsNullOrEmpty(request.SystemPrompt))
            {
                messages.Add(new ChatMessage { Role = "system", Content = request.SystemPrompt });
            }
            messages.Add(new ChatMessage { Role = "user", Content = request.Message });

            var chatRequest = new ChatRequest
            {
                Model = request.Model ?? provider.DefaultModel,
                Messages = messages,
                MaxTokens = request.MaxTokens ?? _config.DefaultMaxTokens,
                Temperature = request.Temperature ?? _config.DefaultTemperature
            };

            var response = await provider.ChatAsync(chatRequest, ct);
            sw.Stop();

            return CapabilityResult<ChatResult>.Ok(
                new ChatResult
                {
                    Response = response.Content,
                    FinishReason = response.FinishReason,
                    ConversationId = null // Single-turn has no conversation
                },
                providerName,
                response.Model,
                new TokenUsage
                {
                    InputTokens = response.InputTokens,
                    OutputTokens = response.OutputTokens
                },
                sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CapabilityResult<ChatResult>.Fail(ex.Message, "CHAT_ERROR");
        }
    }

    /// <summary>
    /// Executes a multi-turn chat with conversation history.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <param name="request">The multi-turn chat request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The chat result with conversation ID.</returns>
    public async Task<CapabilityResult<ChatResult>> ConversationChatAsync(
        InstanceCapabilityConfig instanceConfig,
        MultiTurnChatRequest request,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsCapabilityEnabled(instanceConfig, MultiTurnChat))
            return CapabilityResult<ChatResult>.Disabled(MultiTurnChat);

        if (string.IsNullOrWhiteSpace(request.Message))
            return CapabilityResult<ChatResult>.Fail("Message cannot be empty.", "INVALID_INPUT");

        var providerName = GetProviderForCapability(instanceConfig, MultiTurnChat);
        if (string.IsNullOrEmpty(providerName))
            return CapabilityResult<ChatResult>.NoProvider(MultiTurnChat);

        var provider = _providerResolver(providerName);
        if (provider == null)
            return CapabilityResult<ChatResult>.Fail($"Provider '{providerName}' not available.", "PROVIDER_UNAVAILABLE");

        try
        {
            var sw = Stopwatch.StartNew();

            // Get or create conversation context
            var context = GetOrCreateConversation(request.ConversationId, request.SystemPrompt);

            // Build messages with history
            var messages = BuildMessagesWithHistory(context, request.Message, request.MaxHistoryTurns);

            // Trim to fit context window
            var trimmedMessages = TrimToContextWindow(
                messages,
                request.MaxContextTokens ?? _config.DefaultMaxContextTokens,
                provider.DefaultModel);

            var chatRequest = new ChatRequest
            {
                Model = request.Model ?? provider.DefaultModel,
                Messages = trimmedMessages,
                MaxTokens = request.MaxTokens ?? _config.DefaultMaxTokens,
                Temperature = request.Temperature ?? _config.DefaultTemperature
            };

            var response = await provider.ChatAsync(chatRequest, ct);
            sw.Stop();

            // Update conversation history
            context.AddTurn(request.Message, response.Content);

            return CapabilityResult<ChatResult>.Ok(
                new ChatResult
                {
                    Response = response.Content,
                    FinishReason = response.FinishReason,
                    ConversationId = context.ConversationId,
                    TurnCount = context.TurnCount
                },
                providerName,
                response.Model,
                new TokenUsage
                {
                    InputTokens = response.InputTokens,
                    OutputTokens = response.OutputTokens
                },
                sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CapabilityResult<ChatResult>.Fail(ex.Message, "CONVERSATION_ERROR");
        }
    }

    /// <summary>
    /// Executes an agent chat with function/tool calling capability.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <param name="request">The agent chat request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The agent chat result.</returns>
    public async Task<CapabilityResult<AgentChatResult>> AgentChatAsync(
        InstanceCapabilityConfig instanceConfig,
        AgentChatRequest request,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsCapabilityEnabled(instanceConfig, AgentChat))
            return CapabilityResult<AgentChatResult>.Disabled(AgentChat);

        if (string.IsNullOrWhiteSpace(request.Message))
            return CapabilityResult<AgentChatResult>.Fail("Message cannot be empty.", "INVALID_INPUT");

        var providerName = GetProviderForCapability(instanceConfig, AgentChat);
        if (string.IsNullOrEmpty(providerName))
            return CapabilityResult<AgentChatResult>.NoProvider(AgentChat);

        var provider = _providerResolver(providerName);
        if (provider == null)
            return CapabilityResult<AgentChatResult>.Fail($"Provider '{providerName}' not available.", "PROVIDER_UNAVAILABLE");

        if (!provider.SupportsFunctionCalling)
            return CapabilityResult<AgentChatResult>.Fail($"Provider '{providerName}' does not support function calling.", "FUNCTION_CALLING_NOT_SUPPORTED");

        try
        {
            var sw = Stopwatch.StartNew();

            var messages = new List<ChatMessage>();
            if (!string.IsNullOrEmpty(request.SystemPrompt))
            {
                messages.Add(new ChatMessage { Role = "system", Content = request.SystemPrompt });
            }
            messages.Add(new ChatMessage { Role = "user", Content = request.Message });

            var functionCallRequest = new FunctionCallRequest
            {
                Model = request.Model ?? provider.DefaultModel,
                Messages = messages,
                Functions = request.Functions ?? new List<FunctionDefinition>(),
                FunctionCall = request.FunctionCallMode ?? "auto"
            };

            var response = await provider.FunctionCallAsync(functionCallRequest, ct);
            sw.Stop();

            return CapabilityResult<AgentChatResult>.Ok(
                new AgentChatResult
                {
                    Response = response.Content,
                    FunctionCall = response.FunctionName != null ? new FunctionCallInfo
                    {
                        Name = response.FunctionName,
                        Arguments = response.Arguments
                    } : null,
                    RequiresAction = response.FunctionName != null
                },
                providerName,
                request.Model ?? provider.DefaultModel,
                duration: sw.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CapabilityResult<AgentChatResult>.Fail(ex.Message, "AGENT_CHAT_ERROR");
        }
    }

    /// <summary>
    /// Streams a chat response in real-time.
    /// </summary>
    /// <param name="instanceConfig">The instance configuration.</param>
    /// <param name="request">The streaming chat request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Async enumerable of response chunks.</returns>
    public async IAsyncEnumerable<CapabilityResult<StreamChunk>> StreamChatAsync(
        InstanceCapabilityConfig instanceConfig,
        StreamingChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!IsCapabilityEnabled(instanceConfig, StreamingChat))
        {
            yield return CapabilityResult<StreamChunk>.Disabled(StreamingChat);
            yield break;
        }

        if (string.IsNullOrWhiteSpace(request.Message))
        {
            yield return CapabilityResult<StreamChunk>.Fail("Message cannot be empty.", "INVALID_INPUT");
            yield break;
        }

        var providerName = GetProviderForCapability(instanceConfig, StreamingChat);
        if (string.IsNullOrEmpty(providerName))
        {
            yield return CapabilityResult<StreamChunk>.NoProvider(StreamingChat);
            yield break;
        }

        var provider = _providerResolver(providerName);
        if (provider == null)
        {
            yield return CapabilityResult<StreamChunk>.Fail($"Provider '{providerName}' not available.", "PROVIDER_UNAVAILABLE");
            yield break;
        }

        if (!provider.SupportsStreaming)
        {
            yield return CapabilityResult<StreamChunk>.Fail($"Provider '{providerName}' does not support streaming.", "STREAMING_NOT_SUPPORTED");
            yield break;
        }

        var messages = new List<ChatMessage>();
        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            messages.Add(new ChatMessage { Role = "system", Content = request.SystemPrompt });
        }
        messages.Add(new ChatMessage { Role = "user", Content = request.Message });

        var chatRequest = new ChatRequest
        {
            Model = request.Model ?? provider.DefaultModel,
            Messages = messages,
            MaxTokens = request.MaxTokens ?? _config.DefaultMaxTokens,
            Temperature = request.Temperature ?? _config.DefaultTemperature,
            Stream = true
        };

        var chunkIndex = 0;
        var fullContent = new StringBuilder();

        await foreach (var chunk in provider.StreamChatAsync(chatRequest, ct))
        {
            fullContent.Append(chunk);
            yield return CapabilityResult<StreamChunk>.Ok(
                new StreamChunk
                {
                    Content = chunk,
                    Index = chunkIndex++,
                    IsComplete = false
                },
                providerName,
                request.Model ?? provider.DefaultModel);
        }

        // Final chunk with complete flag
        yield return CapabilityResult<StreamChunk>.Ok(
            new StreamChunk
            {
                Content = "",
                Index = chunkIndex,
                IsComplete = true,
                FullContent = fullContent.ToString()
            },
            providerName,
            request.Model ?? provider.DefaultModel);
    }

    /// <summary>
    /// Creates a new conversation.
    /// </summary>
    /// <param name="systemPrompt">Optional system prompt for the conversation.</param>
    /// <returns>The conversation ID.</returns>
    public string CreateConversation(string? systemPrompt = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var context = new ConversationContext
        {
            ConversationId = Guid.NewGuid().ToString("N"),
            SystemPrompt = systemPrompt,
            CreatedAt = DateTime.UtcNow
        };

        _conversations[context.ConversationId] = context;
        return context.ConversationId;
    }

    /// <summary>
    /// Gets conversation information.
    /// </summary>
    /// <param name="conversationId">The conversation ID.</param>
    /// <returns>Conversation info, or null if not found.</returns>
    public ConversationInfo? GetConversation(string conversationId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_conversations.TryGetValue(conversationId, out var context))
            return null;

        return new ConversationInfo
        {
            ConversationId = context.ConversationId,
            TurnCount = context.TurnCount,
            CreatedAt = context.CreatedAt,
            LastActivityAt = context.LastActivityAt,
            SystemPrompt = context.SystemPrompt
        };
    }

    /// <summary>
    /// Clears conversation history while keeping the conversation active.
    /// </summary>
    /// <param name="conversationId">The conversation ID.</param>
    /// <returns>True if cleared, false if conversation not found.</returns>
    public bool ClearConversation(string conversationId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_conversations.TryGetValue(conversationId, out var context))
        {
            context.Clear();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Ends and removes a conversation.
    /// </summary>
    /// <param name="conversationId">The conversation ID.</param>
    /// <returns>True if removed, false if not found.</returns>
    public bool EndConversation(string conversationId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _conversations.TryRemove(conversationId, out _);
    }

    #region Private Methods

    private ConversationContext GetOrCreateConversation(string? conversationId, string? systemPrompt)
    {
        if (!string.IsNullOrEmpty(conversationId) && _conversations.TryGetValue(conversationId, out var existing))
        {
            existing.Touch();
            return existing;
        }

        var context = new ConversationContext
        {
            ConversationId = conversationId ?? Guid.NewGuid().ToString("N"),
            SystemPrompt = systemPrompt,
            CreatedAt = DateTime.UtcNow
        };

        _conversations[context.ConversationId] = context;
        return context;
    }

    private List<ChatMessage> BuildMessagesWithHistory(ConversationContext context, string newMessage, int? maxTurns)
    {
        var messages = new List<ChatMessage>();

        // Add system prompt
        if (!string.IsNullOrEmpty(context.SystemPrompt))
        {
            messages.Add(new ChatMessage { Role = "system", Content = context.SystemPrompt });
        }

        // Add conversation history
        var history = context.GetHistory(maxTurns ?? _config.DefaultMaxHistoryTurns);
        messages.AddRange(history);

        // Add new user message
        messages.Add(new ChatMessage { Role = "user", Content = newMessage });

        return messages;
    }

    private List<ChatMessage> TrimToContextWindow(List<ChatMessage> messages, int maxTokens, string model)
    {
        // Estimate tokens (rough: ~4 chars per token)
        var estimateTokens = (string text) => text.Length / 4;

        var totalTokens = messages.Sum(m => estimateTokens(m.Content));

        if (totalTokens <= maxTokens)
            return messages;

        // Keep system message and recent messages, trim older history
        var result = new List<ChatMessage>();
        var currentTokens = 0;

        // Always keep system message if present
        var systemMessage = messages.FirstOrDefault(m => m.Role == "system");
        if (systemMessage != null)
        {
            result.Add(systemMessage);
            currentTokens += estimateTokens(systemMessage.Content);
        }

        // Always keep the latest user message
        var latestUserMessage = messages.LastOrDefault(m => m.Role == "user");
        if (latestUserMessage != null)
        {
            currentTokens += estimateTokens(latestUserMessage.Content);
        }

        // Add messages from most recent, skipping system and latest user
        var historyMessages = messages
            .Where(m => m != systemMessage && m != latestUserMessage)
            .Reverse()
            .ToList();

        var includedHistory = new List<ChatMessage>();
        foreach (var msg in historyMessages)
        {
            var msgTokens = estimateTokens(msg.Content);
            if (currentTokens + msgTokens > maxTokens)
                break;

            includedHistory.Insert(0, msg);
            currentTokens += msgTokens;
        }

        result.AddRange(includedHistory);

        if (latestUserMessage != null)
        {
            result.Add(latestUserMessage);
        }

        return result;
    }

    private void CleanupExpiredConversations(object? state)
    {
        var now = DateTime.UtcNow;
        var expiredIds = _conversations
            .Where(kvp => now - kvp.Value.LastActivityAt > _config.ConversationTTL)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var id in expiredIds)
        {
            _conversations.TryRemove(id, out _);
        }

        // Also enforce max count
        while (_conversations.Count > _config.MaxConversations)
        {
            var oldest = _conversations
                .OrderBy(kvp => kvp.Value.LastActivityAt)
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
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cleanupTimer.Dispose();
        _conversations.Clear();
    }

    #endregion
}

#region Configuration

/// <summary>
/// Configuration for the chat capability handler.
/// </summary>
public sealed class ChatConfig
{
    /// <summary>Default provider for chat.</summary>
    public string DefaultProvider { get; init; } = "anthropic";

    /// <summary>Default maximum tokens per response.</summary>
    public int DefaultMaxTokens { get; init; } = 4096;

    /// <summary>Default temperature for responses.</summary>
    public double DefaultTemperature { get; init; } = 0.7;

    /// <summary>Default maximum context window tokens.</summary>
    public int DefaultMaxContextTokens { get; init; } = 8000;

    /// <summary>Default maximum history turns to include.</summary>
    public int DefaultMaxHistoryTurns { get; init; } = 10;

    /// <summary>Time-to-live for inactive conversations.</summary>
    public TimeSpan ConversationTTL { get; init; } = TimeSpan.FromHours(24);

    /// <summary>Maximum number of concurrent conversations.</summary>
    public int MaxConversations { get; init; } = 10_000;
}

#endregion

#region Request Types

/// <summary>
/// Request for single-turn chat.
/// </summary>
public sealed class SingleTurnChatRequest
{
    /// <summary>The user message.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Optional system prompt.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Specific model to use.</summary>
    public string? Model { get; init; }

    /// <summary>Maximum tokens for response.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>Temperature for randomness.</summary>
    public double? Temperature { get; init; }
}

/// <summary>
/// Request for multi-turn chat.
/// </summary>
public sealed class MultiTurnChatRequest
{
    /// <summary>The user message.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Conversation ID for continuing a conversation.</summary>
    public string? ConversationId { get; init; }

    /// <summary>System prompt (used when creating new conversation).</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Maximum history turns to include.</summary>
    public int? MaxHistoryTurns { get; init; }

    /// <summary>Maximum context tokens (for trimming).</summary>
    public int? MaxContextTokens { get; init; }

    /// <summary>Specific model to use.</summary>
    public string? Model { get; init; }

    /// <summary>Maximum tokens for response.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>Temperature for randomness.</summary>
    public double? Temperature { get; init; }
}

/// <summary>
/// Request for agent chat with function calling.
/// </summary>
public sealed class AgentChatRequest
{
    /// <summary>The user message.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Optional system prompt.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Available functions/tools.</summary>
    public List<FunctionDefinition>? Functions { get; init; }

    /// <summary>Function call mode ("auto", "none", or function name).</summary>
    public string? FunctionCallMode { get; init; }

    /// <summary>Specific model to use.</summary>
    public string? Model { get; init; }
}

/// <summary>
/// Request for streaming chat.
/// </summary>
public sealed class StreamingChatRequest
{
    /// <summary>The user message.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>Optional system prompt.</summary>
    public string? SystemPrompt { get; init; }

    /// <summary>Specific model to use.</summary>
    public string? Model { get; init; }

    /// <summary>Maximum tokens for response.</summary>
    public int? MaxTokens { get; init; }

    /// <summary>Temperature for randomness.</summary>
    public double? Temperature { get; init; }
}

#endregion

#region Result Types

/// <summary>
/// Result of a chat operation.
/// </summary>
public sealed class ChatResult
{
    /// <summary>The assistant's response.</summary>
    public string Response { get; init; } = string.Empty;

    /// <summary>Reason the response finished.</summary>
    public string? FinishReason { get; init; }

    /// <summary>Conversation ID for multi-turn chats.</summary>
    public string? ConversationId { get; init; }

    /// <summary>Number of turns in the conversation.</summary>
    public int TurnCount { get; init; }
}

/// <summary>
/// Result of an agent chat operation.
/// </summary>
public sealed class AgentChatResult
{
    /// <summary>The assistant's text response (may be null if function called).</summary>
    public string? Response { get; init; }

    /// <summary>Function call made by the agent.</summary>
    public FunctionCallInfo? FunctionCall { get; init; }

    /// <summary>Whether the client needs to take action (execute function).</summary>
    public bool RequiresAction { get; init; }
}

/// <summary>
/// Information about a function call.
/// </summary>
public sealed class FunctionCallInfo
{
    /// <summary>Name of the function to call.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Arguments for the function.</summary>
    public Dictionary<string, object>? Arguments { get; init; }
}

/// <summary>
/// A chunk of a streaming response.
/// </summary>
public sealed class StreamChunk
{
    /// <summary>Content of this chunk.</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>Index of this chunk (0-based).</summary>
    public int Index { get; init; }

    /// <summary>Whether this is the final chunk.</summary>
    public bool IsComplete { get; init; }

    /// <summary>Full content (only set on final chunk).</summary>
    public string? FullContent { get; init; }
}

/// <summary>
/// Information about a conversation.
/// </summary>
public sealed class ConversationInfo
{
    /// <summary>Conversation identifier.</summary>
    public string ConversationId { get; init; } = string.Empty;

    /// <summary>Number of conversation turns.</summary>
    public int TurnCount { get; init; }

    /// <summary>When the conversation was created.</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>When the conversation was last active.</summary>
    public DateTime LastActivityAt { get; init; }

    /// <summary>System prompt for the conversation.</summary>
    public string? SystemPrompt { get; init; }
}

#endregion

#region Internal Types

/// <summary>
/// Internal conversation context with history tracking.
/// </summary>
internal sealed class ConversationContext
{
    public string ConversationId { get; init; } = string.Empty;
    public string? SystemPrompt { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime LastActivityAt { get; private set; } = DateTime.UtcNow;

    private readonly List<ChatMessage> _history = new();
    private readonly object _lock = new();

    public int TurnCount
    {
        get
        {
            lock (_lock)
            {
                return _history.Count(m => m.Role == "user");
            }
        }
    }

    public void Touch()
    {
        LastActivityAt = DateTime.UtcNow;
    }

    public void AddTurn(string userMessage, string assistantResponse)
    {
        lock (_lock)
        {
            _history.Add(new ChatMessage { Role = "user", Content = userMessage });
            _history.Add(new ChatMessage { Role = "assistant", Content = assistantResponse });
            LastActivityAt = DateTime.UtcNow;
        }
    }

    public List<ChatMessage> GetHistory(int maxTurns)
    {
        lock (_lock)
        {
            // Each turn is 2 messages (user + assistant)
            var maxMessages = maxTurns * 2;
            if (_history.Count <= maxMessages)
            {
                return _history.ToList();
            }

            return _history.Skip(_history.Count - maxMessages).ToList();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _history.Clear();
            LastActivityAt = DateTime.UtcNow;
        }
    }
}

#endregion
