// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Plugins.AIInterface.Channels;
using DataWarehouse.Plugins.AIInterface.CLI;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using System.Collections.Concurrent;

namespace DataWarehouse.Plugins.AIInterface.GUI;

/// <summary>
/// Provides chat panel functionality for GUI integration with AI.
/// Enables conversational interaction with the data warehouse through a chat interface.
/// </summary>
/// <remarks>
/// <para>
/// The ChatPanelProvider integrates with any GUI framework to provide:
/// <list type="bullet">
/// <item>Real-time chat interface for natural language queries</item>
/// <item>Streaming responses for long-running operations</item>
/// <item>Rich message formatting with code, tables, and links</item>
/// <item>Quick action buttons for common operations</item>
/// <item>Conversation history with session persistence</item>
/// </list>
/// </para>
/// <para>
/// This provider is framework-agnostic and can be integrated with WPF, WinForms,
/// web frontends (Blazor, React), or any other UI technology.
/// </para>
/// </remarks>
public sealed class ChatPanelProvider : IDisposable
{
    private readonly IMessageBus? _messageBus;
    private readonly ConversationContext _conversationContext;
    private readonly ConcurrentDictionary<string, ChatSession> _sessions = new();
    private readonly ChatPanelConfig _config;
    private bool _intelligenceAvailable;
    private IntelligenceCapabilities _capabilities = IntelligenceCapabilities.None;
    private bool _disposed;

    /// <summary>
    /// Event raised when a new message is received.
    /// </summary>
    public event EventHandler<ChatMessageEventArgs>? MessageReceived;

    /// <summary>
    /// Event raised when Intelligence availability changes.
    /// </summary>
    public event EventHandler<IntelligenceStatusEventArgs>? IntelligenceStatusChanged;

    /// <summary>
    /// Event raised when streaming content is received.
    /// </summary>
    public event EventHandler<StreamingContentEventArgs>? StreamingContentReceived;

    /// <summary>
    /// Gets whether Intelligence is available.
    /// </summary>
    public bool IsIntelligenceAvailable => _intelligenceAvailable;

    /// <summary>
    /// Gets the current capabilities.
    /// </summary>
    public IntelligenceCapabilities Capabilities => _capabilities;

    /// <summary>
    /// Initializes a new instance of the <see cref="ChatPanelProvider"/> class.
    /// </summary>
    /// <param name="messageBus">Message bus for Intelligence communication.</param>
    /// <param name="config">Optional configuration.</param>
    public ChatPanelProvider(IMessageBus? messageBus, ChatPanelConfig? config = null)
    {
        _messageBus = messageBus;
        _config = config ?? new ChatPanelConfig();
        _conversationContext = new ConversationContext(_config.MaxHistorySize);
    }

    /// <summary>
    /// Initializes the chat panel and discovers Intelligence availability.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_messageBus == null)
        {
            _intelligenceAvailable = false;
            OnIntelligenceStatusChanged(false, IntelligenceCapabilities.None);
            return;
        }

        try
        {
            var response = await _messageBus.RequestAsync(
                IntelligenceTopics.Discover,
                new Dictionary<string, object>
                {
                    ["requestorId"] = "gui.chatpanel",
                    ["requestorName"] = "ChatPanelProvider",
                    ["timestamp"] = DateTimeOffset.UtcNow
                },
                ct);

            if (response != null &&
                response.TryGetValue("available", out var available) && available is true)
            {
                _intelligenceAvailable = true;

                if (response.TryGetValue("capabilities", out var caps))
                {
                    if (caps is IntelligenceCapabilities ic)
                        _capabilities = ic;
                    else if (caps is long longVal)
                        _capabilities = (IntelligenceCapabilities)longVal;
                }

                OnIntelligenceStatusChanged(true, _capabilities);
            }
            else
            {
                _intelligenceAvailable = false;
                OnIntelligenceStatusChanged(false, IntelligenceCapabilities.None);
            }
        }
        catch
        {
            _intelligenceAvailable = false;
            OnIntelligenceStatusChanged(false, IntelligenceCapabilities.None);
        }
    }

    /// <summary>
    /// Sends a message and gets a response.
    /// </summary>
    /// <param name="message">The user message.</param>
    /// <param name="sessionId">Optional session ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The chat response.</returns>
    public async Task<ChatMessage> SendMessageAsync(
        string message,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        sessionId ??= Guid.NewGuid().ToString("N");
        var session = GetOrCreateSession(sessionId);

        // Create user message
        var userMessage = new ChatMessage
        {
            Id = Guid.NewGuid().ToString("N"),
            SessionId = sessionId,
            Role = ChatRole.User,
            Content = message,
            Timestamp = DateTime.UtcNow
        };

        session.Messages.Add(userMessage);
        _conversationContext.AddUserTurn(sessionId, message);

        OnMessageReceived(userMessage);

        // If Intelligence is not available, return helpful fallback
        if (!_intelligenceAvailable || _messageBus == null)
        {
            var fallbackMessage = new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                SessionId = sessionId,
                Role = ChatRole.Assistant,
                Content = "I'm currently operating in offline mode. Intelligence services are not available. " +
                         "You can still use traditional commands like:\n\n" +
                         "- `upload <file>` - Upload a file\n" +
                         "- `search <query>` - Search for files\n" +
                         "- `list` - List files\n" +
                         "- `help` - Show available commands",
                Timestamp = DateTime.UtcNow,
                Formatting = new MessageFormatting { IsMarkdown = true }
            };

            session.Messages.Add(fallbackMessage);
            _conversationContext.AddAssistantTurn(sessionId, fallbackMessage.Content);
            OnMessageReceived(fallbackMessage);

            return fallbackMessage;
        }

        try
        {
            // Build conversation payload
            var history = _conversationContext.GetHistory(sessionId);
            var conversationPayload = BuildConversationPayload(history);

            // Send to Intelligence
            var response = await _messageBus.RequestAsync(
                IntelligenceTopics.RequestConversation,
                new Dictionary<string, object>
                {
                    ["message"] = message,
                    ["conversation"] = conversationPayload,
                    ["sessionId"] = sessionId,
                    ["context"] = session.Context,
                    ["capabilities"] = new[] { "search", "upload", "download", "encrypt", "metadata", "audit" }
                },
                ct);

            // Parse response
            var assistantMessage = ParseIntelligenceResponse(sessionId, response);
            session.Messages.Add(assistantMessage);
            _conversationContext.AddAssistantTurn(sessionId, assistantMessage.Content);
            OnMessageReceived(assistantMessage);

            return assistantMessage;
        }
        catch (Exception ex)
        {
            var errorMessage = new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                SessionId = sessionId,
                Role = ChatRole.Assistant,
                Content = $"Sorry, I encountered an error processing your request: {ex.Message}",
                Timestamp = DateTime.UtcNow,
                IsError = true
            };

            session.Messages.Add(errorMessage);
            _conversationContext.AddAssistantTurn(sessionId, errorMessage.Content);
            OnMessageReceived(errorMessage);

            return errorMessage;
        }
    }

    /// <summary>
    /// Sends a message with streaming response support.
    /// </summary>
    /// <param name="message">The user message.</param>
    /// <param name="sessionId">Optional session ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The streaming response.</returns>
    public async IAsyncEnumerable<StreamingChunk> SendMessageStreamingAsync(
        string message,
        string? sessionId = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        sessionId ??= Guid.NewGuid().ToString("N");
        var session = GetOrCreateSession(sessionId);

        // Add user message
        var userMessage = new ChatMessage
        {
            Id = Guid.NewGuid().ToString("N"),
            SessionId = sessionId,
            Role = ChatRole.User,
            Content = message,
            Timestamp = DateTime.UtcNow
        };

        session.Messages.Add(userMessage);
        _conversationContext.AddUserTurn(sessionId, message);
        OnMessageReceived(userMessage);

        if (!_intelligenceAvailable || _messageBus == null)
        {
            yield return new StreamingChunk
            {
                Content = "Intelligence services are not available. Please try again later.",
                IsComplete = true
            };
            yield break;
        }

        // For streaming, we'll simulate chunked response
        // In a real implementation, this would connect to a streaming endpoint
        var fullResponse = await SendMessageAsync(message, sessionId, ct);

        // Simulate streaming by chunking the response
        var content = fullResponse.Content;
        var chunkSize = 20;

        for (var i = 0; i < content.Length; i += chunkSize)
        {
            if (ct.IsCancellationRequested) yield break;

            var chunk = content.Substring(i, Math.Min(chunkSize, content.Length - i));
            yield return new StreamingChunk
            {
                Content = chunk,
                IsComplete = i + chunkSize >= content.Length
            };

            OnStreamingContentReceived(sessionId, chunk, i + chunkSize >= content.Length);

            await Task.Delay(10, ct); // Small delay to simulate streaming
        }
    }

    /// <summary>
    /// Gets the message history for a session.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <returns>The message history.</returns>
    public IReadOnlyList<ChatMessage> GetHistory(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            return session.Messages.ToList().AsReadOnly();
        }
        return Array.Empty<ChatMessage>();
    }

    /// <summary>
    /// Clears the chat history for a session.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    public void ClearHistory(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.Messages.Clear();
            session.Context.Clear();
        }
        _conversationContext.ClearHistory(sessionId);
    }

    /// <summary>
    /// Gets quick action suggestions based on context.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <returns>List of suggested quick actions.</returns>
    public IReadOnlyList<QuickAction> GetQuickActions(string sessionId)
    {
        var actions = new List<QuickAction>
        {
            new QuickAction
            {
                Id = "search",
                Label = "Search files",
                Icon = "search",
                Prompt = "Search for "
            },
            new QuickAction
            {
                Id = "upload",
                Label = "Upload file",
                Icon = "upload",
                Prompt = "Upload file from "
            },
            new QuickAction
            {
                Id = "list",
                Label = "List files",
                Icon = "list",
                Prompt = "Show me all files"
            }
        };

        if (_intelligenceAvailable)
        {
            actions.AddRange(new[]
            {
                new QuickAction
                {
                    Id = "organize",
                    Label = "Organize files",
                    Icon = "folder",
                    Prompt = "Help me organize my files"
                },
                new QuickAction
                {
                    Id = "audit",
                    Label = "Run audit",
                    Icon = "shield",
                    Prompt = "Run a compliance audit"
                }
            });
        }

        // Add context-specific actions
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            if (session.Context.ContainsKey("lastSearchResults"))
            {
                actions.Insert(0, new QuickAction
                {
                    Id = "encrypt_results",
                    Label = "Encrypt results",
                    Icon = "lock",
                    Prompt = "Encrypt the search results"
                });
            }
        }

        return actions.AsReadOnly();
    }

    /// <summary>
    /// Sets context for a session.
    /// </summary>
    /// <param name="sessionId">The session ID.</param>
    /// <param name="key">The context key.</param>
    /// <param name="value">The context value.</param>
    public void SetContext(string sessionId, string key, object value)
    {
        var session = GetOrCreateSession(sessionId);
        session.Context[key] = value;
    }

    /// <summary>
    /// Gets the chat panel configuration UI model.
    /// </summary>
    /// <returns>The panel configuration.</returns>
    public ChatPanelUIModel GetUIModel()
    {
        return new ChatPanelUIModel
        {
            IsIntelligenceAvailable = _intelligenceAvailable,
            Capabilities = _capabilities,
            PlaceholderText = _intelligenceAvailable
                ? "Ask me anything about your data..."
                : "Type a command (e.g., 'list files', 'search documents')",
            WelcomeMessage = _intelligenceAvailable
                ? "Hello! I'm your AI assistant for the data warehouse. Ask me to find files, analyze data, run compliance checks, or anything else you need."
                : "Welcome! Intelligence services are currently offline. You can still use basic commands.",
            SupportedFeatures = GetSupportedFeatures()
        };
    }

    private ChatSession GetOrCreateSession(string sessionId)
    {
        return _sessions.GetOrAdd(sessionId, id => new ChatSession
        {
            SessionId = id,
            CreatedAt = DateTime.UtcNow
        });
    }

    private Dictionary<string, object> BuildConversationPayload(IReadOnlyList<ConversationTurn> history)
    {
        return new Dictionary<string, object>
        {
            ["messages"] = history.Select(t => new Dictionary<string, object>
            {
                ["role"] = t.Role,
                ["content"] = t.Content,
                ["timestamp"] = t.Timestamp
            }).ToList(),
            ["turnCount"] = history.Count
        };
    }

    private ChatMessage ParseIntelligenceResponse(string sessionId, Dictionary<string, object>? response)
    {
        if (response == null)
        {
            return new ChatMessage
            {
                Id = Guid.NewGuid().ToString("N"),
                SessionId = sessionId,
                Role = ChatRole.Assistant,
                Content = "I didn't receive a response. Please try again.",
                Timestamp = DateTime.UtcNow,
                IsError = true
            };
        }

        // Extract content
        var content = response.TryGetValue("response", out var respObj) ? respObj?.ToString()
                    : response.TryGetValue("content", out var contObj) ? contObj?.ToString()
                    : response.TryGetValue("message", out var msgObj) ? msgObj?.ToString()
                    : "I processed your request.";

        // Build message
        var message = new ChatMessage
        {
            Id = Guid.NewGuid().ToString("N"),
            SessionId = sessionId,
            Role = ChatRole.Assistant,
            Content = content ?? string.Empty,
            Timestamp = DateTime.UtcNow
        };

        // Extract formatting hints
        if (response.TryGetValue("formatting", out var formatting) && formatting is Dictionary<string, object> fmt)
        {
            message.Formatting = new MessageFormatting
            {
                IsMarkdown = fmt.TryGetValue("markdown", out var md) && md is true,
                IsCode = fmt.TryGetValue("code", out var code) && code is true,
                Language = fmt.TryGetValue("language", out var lang) ? lang?.ToString() : null
            };
        }

        // Extract quick actions
        if (response.TryGetValue("actions", out var actions) && actions is IEnumerable<object> actionList)
        {
            message.Actions = actionList
                .OfType<Dictionary<string, object>>()
                .Select(a => new ChatAction
                {
                    Label = a.TryGetValue("label", out var l) ? l?.ToString() ?? string.Empty : string.Empty,
                    Command = a.TryGetValue("command", out var c) ? c?.ToString() ?? string.Empty : string.Empty,
                    Type = a.TryGetValue("type", out var t) ? t?.ToString() ?? "button" : "button"
                })
                .ToList();
        }

        // Extract data
        if (response.TryGetValue("data", out var data))
        {
            message.Data = data;
        }

        return message;
    }

    private string[] GetSupportedFeatures()
    {
        var features = new List<string> { "search", "upload", "download", "list", "metadata" };

        if (_intelligenceAvailable)
        {
            if ((_capabilities & IntelligenceCapabilities.SemanticSearch) != 0)
                features.Add("semantic_search");
            if ((_capabilities & IntelligenceCapabilities.Summarization) != 0)
                features.Add("summarization");
            if ((_capabilities & IntelligenceCapabilities.Classification) != 0)
                features.Add("classification");
            if ((_capabilities & IntelligenceCapabilities.PIIDetection) != 0)
                features.Add("pii_detection");
            if ((_capabilities & IntelligenceCapabilities.TaskPlanning) != 0)
                features.Add("agent_mode");
        }

        return features.ToArray();
    }

    private void OnMessageReceived(ChatMessage message)
    {
        MessageReceived?.Invoke(this, new ChatMessageEventArgs { Message = message });
    }

    private void OnIntelligenceStatusChanged(bool available, IntelligenceCapabilities capabilities)
    {
        IntelligenceStatusChanged?.Invoke(this, new IntelligenceStatusEventArgs
        {
            IsAvailable = available,
            Capabilities = capabilities
        });
    }

    private void OnStreamingContentReceived(string sessionId, string content, bool isComplete)
    {
        StreamingContentReceived?.Invoke(this, new StreamingContentEventArgs
        {
            SessionId = sessionId,
            Content = content,
            IsComplete = isComplete
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _conversationContext.Dispose();
        _sessions.Clear();
    }
}

/// <summary>
/// Chat message.
/// </summary>
public sealed class ChatMessage
{
    /// <summary>Gets or sets the message ID.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets or sets the session ID.</summary>
    public string SessionId { get; init; } = string.Empty;

    /// <summary>Gets or sets the role (user, assistant, system).</summary>
    public ChatRole Role { get; init; }

    /// <summary>Gets or sets the message content.</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>Gets or sets the timestamp.</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>Gets or sets whether this is an error message.</summary>
    public bool IsError { get; init; }

    /// <summary>Gets or sets formatting information.</summary>
    public MessageFormatting? Formatting { get; set; }

    /// <summary>Gets or sets available actions.</summary>
    public List<ChatAction>? Actions { get; set; }

    /// <summary>Gets or sets associated data.</summary>
    public object? Data { get; set; }
}

/// <summary>
/// Chat role.
/// </summary>
public enum ChatRole
{
    /// <summary>User message.</summary>
    User,

    /// <summary>Assistant message.</summary>
    Assistant,

    /// <summary>System message.</summary>
    System
}

/// <summary>
/// Message formatting hints.
/// </summary>
public sealed class MessageFormatting
{
    /// <summary>Gets or sets whether content is markdown.</summary>
    public bool IsMarkdown { get; init; }

    /// <summary>Gets or sets whether content is code.</summary>
    public bool IsCode { get; init; }

    /// <summary>Gets or sets the code language.</summary>
    public string? Language { get; init; }
}

/// <summary>
/// Chat action button.
/// </summary>
public sealed class ChatAction
{
    /// <summary>Gets or sets the label.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Gets or sets the command to execute.</summary>
    public string Command { get; init; } = string.Empty;

    /// <summary>Gets or sets the action type.</summary>
    public string Type { get; init; } = "button";
}

/// <summary>
/// Quick action suggestion.
/// </summary>
public sealed class QuickAction
{
    /// <summary>Gets or sets the action ID.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets or sets the label.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Gets or sets the icon name.</summary>
    public string Icon { get; init; } = string.Empty;

    /// <summary>Gets or sets the prompt to send.</summary>
    public string Prompt { get; init; } = string.Empty;
}

/// <summary>
/// Streaming response chunk.
/// </summary>
public sealed class StreamingChunk
{
    /// <summary>Gets or sets the content.</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>Gets or sets whether this is the last chunk.</summary>
    public bool IsComplete { get; init; }
}

/// <summary>
/// Chat panel UI model.
/// </summary>
public sealed class ChatPanelUIModel
{
    /// <summary>Gets or sets whether Intelligence is available.</summary>
    public bool IsIntelligenceAvailable { get; init; }

    /// <summary>Gets or sets the capabilities.</summary>
    public IntelligenceCapabilities Capabilities { get; init; }

    /// <summary>Gets or sets the placeholder text.</summary>
    public string PlaceholderText { get; init; } = string.Empty;

    /// <summary>Gets or sets the welcome message.</summary>
    public string WelcomeMessage { get; init; } = string.Empty;

    /// <summary>Gets or sets supported features.</summary>
    public string[] SupportedFeatures { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Chat session state.
/// </summary>
internal sealed class ChatSession
{
    public string SessionId { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
    public List<ChatMessage> Messages { get; } = new();
    public Dictionary<string, object> Context { get; } = new();
}

/// <summary>
/// Chat panel configuration.
/// </summary>
public sealed class ChatPanelConfig
{
    /// <summary>Maximum history size.</summary>
    public int MaxHistorySize { get; init; } = 100;

    /// <summary>Enable streaming responses.</summary>
    public bool EnableStreaming { get; init; } = true;

    /// <summary>Enable quick actions.</summary>
    public bool EnableQuickActions { get; init; } = true;
}

/// <summary>
/// Event args for message received.
/// </summary>
public sealed class ChatMessageEventArgs : EventArgs
{
    /// <summary>Gets or sets the message.</summary>
    public required ChatMessage Message { get; init; }
}

/// <summary>
/// Event args for Intelligence status change.
/// </summary>
public sealed class IntelligenceStatusEventArgs : EventArgs
{
    /// <summary>Gets or sets whether Intelligence is available.</summary>
    public bool IsAvailable { get; init; }

    /// <summary>Gets or sets the capabilities.</summary>
    public IntelligenceCapabilities Capabilities { get; init; }
}

/// <summary>
/// Event args for streaming content.
/// </summary>
public sealed class StreamingContentEventArgs : EventArgs
{
    /// <summary>Gets or sets the session ID.</summary>
    public string SessionId { get; init; } = string.Empty;

    /// <summary>Gets or sets the content.</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>Gets or sets whether this is the last chunk.</summary>
    public bool IsComplete { get; init; }
}
