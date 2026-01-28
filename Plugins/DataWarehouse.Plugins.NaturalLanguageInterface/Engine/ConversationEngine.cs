// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Plugins.NaturalLanguageInterface.Models;
using DataWarehouse.SDK.AI;
using System.Collections.Concurrent;
using System.Text.Json;

namespace DataWarehouse.Plugins.NaturalLanguageInterface.Engine;

/// <summary>
/// Conversation engine for managing multi-turn dialogues.
/// Handles context management, follow-up queries, and clarification requests.
/// </summary>
public sealed class ConversationEngine : IDisposable
{
    private readonly ConcurrentDictionary<string, Conversation> _conversations = new();
    private readonly QueryEngine _queryEngine;
    private readonly IAIProviderRegistry? _aiRegistry;
    private readonly ConversationEngineConfig _config;
    private readonly Timer _cleanupTimer;

    public ConversationEngine(QueryEngine queryEngine, IAIProviderRegistry? aiRegistry = null, ConversationEngineConfig? config = null)
    {
        _queryEngine = queryEngine;
        _aiRegistry = aiRegistry;
        _config = config ?? new ConversationEngineConfig();

        // Cleanup expired conversations periodically
        _cleanupTimer = new Timer(
            CleanupExpiredConversations,
            null,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(5));
    }

    /// <summary>
    /// Start a new conversation.
    /// </summary>
    public Conversation StartConversation(string userId, string platform = "api", string? channelId = null, string? language = null)
    {
        var conversation = new Conversation
        {
            UserId = userId,
            Platform = platform,
            ChannelId = channelId,
            Language = language ?? "en",
            MaxHistoryLength = _config.MaxHistoryLength
        };

        // Enforce max conversations limit
        while (_conversations.Count >= _config.MaxConversations)
        {
            var oldest = _conversations.OrderBy(kv => kv.Value.LastActivityAt).FirstOrDefault();
            if (!string.IsNullOrEmpty(oldest.Key))
            {
                _conversations.TryRemove(oldest.Key, out _);
            }
            else
            {
                break;
            }
        }

        _conversations[conversation.Id] = conversation;
        return conversation;
    }

    /// <summary>
    /// Get an existing conversation.
    /// </summary>
    public Conversation? GetConversation(string conversationId)
    {
        return _conversations.TryGetValue(conversationId, out var conversation) ? conversation : null;
    }

    /// <summary>
    /// Process a user message in a conversation.
    /// </summary>
    public async Task<ConversationTurnResult> ProcessMessageAsync(
        string conversationId,
        string message,
        List<MessageAttachment>? attachments = null,
        CancellationToken ct = default)
    {
        if (!_conversations.TryGetValue(conversationId, out var conversation))
        {
            return new ConversationTurnResult
            {
                Success = false,
                Response = "Conversation not found. Please start a new conversation.",
                State = DialogueState.Error,
                Error = "Invalid conversation ID"
            };
        }

        // Add user message to history
        var userMessage = new ConversationMessage
        {
            Role = "user",
            Content = message,
            Attachments = attachments ?? new List<MessageAttachment>()
        };
        conversation.AddMessage(userMessage);

        try
        {
            // Parse the query with conversation context
            var intent = await _queryEngine.ParseQueryAsync(message, conversation.Context, ct);
            userMessage.Intent = intent;
            userMessage.Entities.AddRange(intent.Entities);

            // Handle different dialogue states
            var result = intent.Type switch
            {
                // Conversational intents
                IntentType.Greeting => HandleGreeting(conversation),
                IntentType.Goodbye => HandleGoodbye(conversation),
                IntentType.Thanks => HandleThanks(conversation),
                IntentType.Confirmation => await HandleConfirmationAsync(conversation, message, ct),
                IntentType.Cancellation => HandleCancellation(conversation),
                IntentType.Clarification => await HandleClarificationResponseAsync(conversation, message, ct),

                // Check if clarification is needed
                _ when intent.Confidence < _config.ClarificationThreshold =>
                    await RequestClarificationAsync(conversation, intent, ct),

                // Process the query
                _ => await ProcessQueryAsync(conversation, intent, ct)
            };

            // Add assistant response to history
            var assistantMessage = new ConversationMessage
            {
                Role = "assistant",
                Content = result.Response
            };
            conversation.AddMessage(assistantMessage);

            // Update context
            UpdateContext(conversation.Context, intent);

            return result;
        }
        catch (Exception ex)
        {
            var errorResult = new ConversationTurnResult
            {
                Success = false,
                Response = "I encountered an error processing your request. Please try again.",
                State = DialogueState.Error,
                Error = ex.Message
            };

            conversation.AddMessage(new ConversationMessage
            {
                Role = "assistant",
                Content = errorResult.Response
            });

            return errorResult;
        }
    }

    /// <summary>
    /// Continue a conversation with follow-up context.
    /// </summary>
    public async Task<ConversationTurnResult> ContinueConversationAsync(
        string conversationId,
        string followUpMessage,
        CancellationToken ct = default)
    {
        if (!_conversations.TryGetValue(conversationId, out var conversation))
        {
            return new ConversationTurnResult
            {
                Success = false,
                Response = "Conversation not found.",
                State = DialogueState.Error
            };
        }

        // Process as a follow-up with context
        return await ProcessMessageAsync(conversationId, followUpMessage, null, ct);
    }

    /// <summary>
    /// Provide clarification to a previous ambiguous query.
    /// </summary>
    public async Task<ConversationTurnResult> ProvideClarificationAsync(
        string conversationId,
        string clarificationValue,
        CancellationToken ct = default)
    {
        if (!_conversations.TryGetValue(conversationId, out var conversation))
        {
            return new ConversationTurnResult
            {
                Success = false,
                Response = "Conversation not found.",
                State = DialogueState.Error
            };
        }

        // Get the pending clarification from context
        if (!conversation.Context.Variables.TryGetValue("pendingClarification", out var pending))
        {
            return new ConversationTurnResult
            {
                Success = false,
                Response = "No pending clarification found.",
                State = DialogueState.Error
            };
        }

        // Remove pending clarification
        conversation.Context.Variables.Remove("pendingClarification");

        // Reconstruct query with clarification
        if (pending is QueryIntent originalIntent)
        {
            var newParams = new Dictionary<string, object>(originalIntent.Parameters)
            {
                ["clarification"] = clarificationValue
            };

            var clarifiedIntent = new QueryIntent
            {
                Type = originalIntent.Type,
                SubType = originalIntent.SubType,
                Confidence = 0.9, // Boost confidence after clarification
                Entities = originalIntent.Entities,
                Parameters = newParams,
                OriginalQuery = originalIntent.OriginalQuery,
                NormalizedQuery = originalIntent.NormalizedQuery,
                Language = originalIntent.Language,
                Keywords = originalIntent.Keywords,
                SuggestedCommand = originalIntent.SuggestedCommand,
                IsFollowUp = originalIntent.IsFollowUp
            };

            return await ProcessQueryAsync(conversation, clarifiedIntent, ct);
        }

        // Treat clarification as new input
        return await ProcessMessageAsync(conversationId, clarificationValue, null, ct);
    }

    /// <summary>
    /// Clear conversation history.
    /// </summary>
    public void ClearConversation(string conversationId)
    {
        if (_conversations.TryGetValue(conversationId, out var conversation))
        {
            conversation.Messages.Clear();
            conversation.Context.Variables.Clear();
            conversation.Context.FileTypes.Clear();
            conversation.Context.Paths.Clear();
            conversation.Context.Tags.Clear();
            conversation.Context.LastResultIds.Clear();
            conversation.Context.TimeRange = null;
            conversation.Context.SizeRange = null;
            conversation.Context.CurrentTopic = null;
        }
    }

    /// <summary>
    /// End a conversation.
    /// </summary>
    public void EndConversation(string conversationId)
    {
        _conversations.TryRemove(conversationId, out _);
    }

    /// <summary>
    /// Get conversation history.
    /// </summary>
    public IEnumerable<ConversationMessage> GetHistory(string conversationId, int? limit = null)
    {
        if (!_conversations.TryGetValue(conversationId, out var conversation))
        {
            return Enumerable.Empty<ConversationMessage>();
        }

        return limit.HasValue
            ? conversation.Messages.TakeLast(limit.Value)
            : conversation.Messages;
    }

    /// <summary>
    /// Get active conversations for a user.
    /// </summary>
    public IEnumerable<Conversation> GetUserConversations(string userId)
    {
        return _conversations.Values
            .Where(c => c.UserId == userId && c.IsActive)
            .OrderByDescending(c => c.LastActivityAt);
    }

    #region Private Methods

    private ConversationTurnResult HandleGreeting(Conversation conversation)
    {
        var greetings = new[]
        {
            $"Hello! I'm your DataWarehouse assistant. How can I help you today?",
            $"Hi there! I can help you search files, check storage status, manage backups, and more. What would you like to do?",
            $"Welcome! Ask me anything about your data warehouse."
        };

        return new ConversationTurnResult
        {
            Success = true,
            Response = greetings[Random.Shared.Next(greetings.Length)],
            State = DialogueState.Initial,
            SuggestedFollowUps = new List<string>
            {
                "Show me my storage status",
                "Find files modified today",
                "List recent backups",
                "What can you help me with?"
            }
        };
    }

    private ConversationTurnResult HandleGoodbye(Conversation conversation)
    {
        conversation.IsActive = false;

        return new ConversationTurnResult
        {
            Success = true,
            Response = "Goodbye! Feel free to start a new conversation anytime.",
            State = DialogueState.Completed
        };
    }

    private ConversationTurnResult HandleThanks(Conversation conversation)
    {
        var responses = new[]
        {
            "You're welcome! Is there anything else I can help you with?",
            "Happy to help! Let me know if you need anything else.",
            "Glad I could assist! What else would you like to do?"
        };

        return new ConversationTurnResult
        {
            Success = true,
            Response = responses[Random.Shared.Next(responses.Length)],
            State = DialogueState.FollowUp
        };
    }

    private async Task<ConversationTurnResult> HandleConfirmationAsync(Conversation conversation, string message, CancellationToken ct)
    {
        // Check if there's a pending action to confirm
        if (conversation.Context.Variables.TryGetValue("pendingAction", out var pending))
        {
            var isPositive = IsPositiveConfirmation(message);

            if (isPositive)
            {
                // Execute the pending action
                conversation.Context.Variables.Remove("pendingAction");

                if (pending is QueryTranslation translation)
                {
                    return new ConversationTurnResult
                    {
                        Success = true,
                        Response = $"Confirmed. Executing: {translation.Explanation}",
                        State = DialogueState.ProcessingQuery,
                        Data = translation
                    };
                }
            }
            else
            {
                // Cancel the pending action
                conversation.Context.Variables.Remove("pendingAction");
                return new ConversationTurnResult
                {
                    Success = true,
                    Response = "Action cancelled. What would you like to do instead?",
                    State = DialogueState.FollowUp
                };
            }
        }

        // No pending action, treat as ambiguous
        return new ConversationTurnResult
        {
            Success = true,
            Response = "I'm not sure what you're confirming. Could you please clarify?",
            State = DialogueState.WaitingForClarification
        };
    }

    private ConversationTurnResult HandleCancellation(Conversation conversation)
    {
        // Clear any pending actions
        conversation.Context.Variables.Remove("pendingAction");
        conversation.Context.Variables.Remove("pendingClarification");

        return new ConversationTurnResult
        {
            Success = true,
            Response = "Cancelled. What would you like to do instead?",
            State = DialogueState.Initial
        };
    }

    private async Task<ConversationTurnResult> HandleClarificationResponseAsync(Conversation conversation, string message, CancellationToken ct)
    {
        // This is called when user provides clarification
        return await ProvideClarificationAsync(conversation.Id, message, ct);
    }

    private async Task<ConversationTurnResult> RequestClarificationAsync(Conversation conversation, QueryIntent intent, CancellationToken ct)
    {
        // Store the original intent for when clarification is provided
        conversation.Context.Variables["pendingClarification"] = intent;

        var clarification = GenerateClarification(intent);

        return new ConversationTurnResult
        {
            Success = true,
            Response = clarification.Question,
            Clarification = clarification,
            State = DialogueState.WaitingForClarification
        };
    }

    private async Task<ConversationTurnResult> ProcessQueryAsync(Conversation conversation, QueryIntent intent, CancellationToken ct)
    {
        // Translate intent to command
        var translation = _queryEngine.TranslateToCommand(intent, conversation.Context);

        // Check if confirmation is needed for destructive actions
        if (RequiresConfirmation(intent, translation))
        {
            conversation.Context.Variables["pendingAction"] = translation;

            return new ConversationTurnResult
            {
                Success = true,
                Response = $"Are you sure you want to {translation.Explanation}? This action may be destructive.",
                State = DialogueState.WaitingForClarification,
                Clarification = new ClarificationRequest
                {
                    Type = ClarificationType.ConfirmAction,
                    Question = "Please confirm this action.",
                    Options = new List<ClarificationOption>
                    {
                        new() { Label = "Yes, proceed", Value = "yes" },
                        new() { Label = "No, cancel", Value = "no" }
                    }
                }
            };
        }

        // Generate response based on intent
        var response = await GenerateResponseAsync(conversation, intent, translation, ct);

        return new ConversationTurnResult
        {
            Success = translation.Success,
            Response = response,
            Data = new
            {
                Command = translation.Command,
                Parameters = translation.Parameters,
                Filter = translation.FilterExpression
            },
            State = DialogueState.PresentingResults,
            SuggestedFollowUps = GenerateSuggestedFollowUps(intent, translation)
        };
    }

    private async Task<string> GenerateResponseAsync(Conversation conversation, QueryIntent intent, QueryTranslation translation, CancellationToken ct)
    {
        // Try AI-generated response if available
        if (_aiRegistry != null && _config.UseAIForResponses)
        {
            var provider = _aiRegistry.GetProvider(_config.ProviderRouting.ConversationProvider)
                ?? _aiRegistry.GetDefaultProvider();

            if (provider != null && provider.IsAvailable)
            {
                try
                {
                    var aiResponse = await GenerateAIResponseAsync(provider, conversation, intent, translation, ct);
                    if (!string.IsNullOrEmpty(aiResponse))
                    {
                        return aiResponse;
                    }
                }
                catch
                {
                    // Fall through to template-based response
                }
            }
        }

        // Template-based response
        return GenerateTemplateResponse(intent, translation);
    }

    private async Task<string?> GenerateAIResponseAsync(
        IAIProvider provider,
        Conversation conversation,
        QueryIntent intent,
        QueryTranslation translation,
        CancellationToken ct)
    {
        var systemPrompt = """
            You are a helpful data warehouse assistant. Generate a natural, conversational response
            explaining what you're doing based on the user's query. Keep responses concise but informative.
            Do not use markdown formatting. Speak directly to the user.
            """;

        var contextSummary = conversation.Messages.Count > 1
            ? $"Previous context: {string.Join(" | ", conversation.GetRecentMessages(3).Select(m => $"{m.Role}: {m.Content.Substring(0, Math.Min(50, m.Content.Length))}..."))}"
            : "";

        var prompt = $"""
            User query: "{intent.OriginalQuery}"
            Detected intent: {intent.Type}
            Command to execute: {translation.Command}
            Parameters: {JsonSerializer.Serialize(translation.Parameters)}
            {contextSummary}

            Generate a helpful response explaining what will be done.
            """;

        var request = new AIRequest
        {
            SystemMessage = systemPrompt,
            Prompt = prompt,
            Temperature = 0.7f,
            MaxTokens = 200
        };

        var response = await provider.CompleteAsync(request, ct);
        return response.Success ? response.Content : null;
    }

    private string GenerateTemplateResponse(QueryIntent intent, QueryTranslation translation)
    {
        return intent.Type switch
        {
            IntentType.Search or IntentType.FindFiles =>
                $"Searching for files matching your criteria. {translation.Explanation}",

            IntentType.FindByDate =>
                $"Looking for files within the specified time range. {translation.Explanation}",

            IntentType.FindBySize =>
                $"Filtering files by size. {translation.Explanation}",

            IntentType.GetStatus =>
                "Checking system status...",

            IntentType.GetStats =>
                "Retrieving storage statistics...",

            IntentType.GetUsage =>
                "Calculating storage usage...",

            IntentType.Backup =>
                $"Initiating backup operation. {translation.Explanation}",

            IntentType.Restore =>
                $"Preparing to restore data. {translation.Explanation}",

            IntentType.Help =>
                GenerateHelpResponse(),

            _ => translation.Explanation
        };
    }

    private string GenerateHelpResponse()
    {
        return """
            I can help you with:
            - Searching files: "Find all PDFs from last week"
            - Checking status: "How much storage am I using?"
            - Managing backups: "Create a backup called daily-backup"
            - Understanding data: "Why was this file archived?"
            - Getting reports: "Summarize my storage usage this month"

            Just ask in natural language and I'll do my best to help!
            """;
    }

    private ClarificationRequest GenerateClarification(QueryIntent intent)
    {
        if (intent.Type == IntentType.Unknown)
        {
            return new ClarificationRequest
            {
                Type = ClarificationType.AmbiguousIntent,
                Question = "I'm not sure what you'd like to do. Could you choose one of these options or rephrase your request?",
                Options = new List<ClarificationOption>
                {
                    new() { Label = "Search for files", Value = "search", Description = "Find files by name, type, or content" },
                    new() { Label = "Check storage status", Value = "status", Description = "View system health and usage" },
                    new() { Label = "Manage backups", Value = "backup", Description = "Create, restore, or list backups" },
                    new() { Label = "Get help", Value = "help", Description = "See what I can do" }
                }
            };
        }

        // Intent-specific clarifications
        return intent.Type switch
        {
            IntentType.FindFiles when intent.Keywords.Count == 0 => new ClarificationRequest
            {
                Type = ClarificationType.MissingParameter,
                Question = "What kind of files are you looking for? You can specify by name, type, date, or other criteria.",
                Options = new List<ClarificationOption>
                {
                    new() { Label = "All files", Value = "all" },
                    new() { Label = "Recent files", Value = "recent", Description = "Modified in the last week" },
                    new() { Label = "Large files", Value = "large", Description = "Larger than 100MB" }
                }
            },

            IntentType.Delete => new ClarificationRequest
            {
                Type = ClarificationType.ConfirmAction,
                Question = "Which file or backup would you like to delete?",
                Reason = "Delete operations require specific targets"
            },

            _ => new ClarificationRequest
            {
                Type = ClarificationType.AmbiguousIntent,
                Question = "Could you be more specific about what you're looking for?",
                Options = new List<ClarificationOption>()
            }
        };
    }

    private List<string> GenerateSuggestedFollowUps(QueryIntent intent, QueryTranslation translation)
    {
        var suggestions = new List<string>();

        switch (intent.Type)
        {
            case IntentType.Search or IntentType.FindFiles:
                suggestions.Add("Show only the largest files");
                suggestions.Add("Filter by file type");
                suggestions.Add("Show files from a specific date range");
                break;

            case IntentType.GetStats:
                suggestions.Add("Compare to last month");
                suggestions.Add("Show by storage pool");
                suggestions.Add("Show by file type");
                break;

            case IntentType.Backup:
                suggestions.Add("List all backups");
                suggestions.Add("Schedule automatic backups");
                suggestions.Add("Verify the backup");
                break;

            case IntentType.GetStatus:
                suggestions.Add("Show detailed metrics");
                suggestions.Add("Check for alerts");
                suggestions.Add("View recent activity");
                break;
        }

        return suggestions.Take(3).ToList();
    }

    private bool RequiresConfirmation(QueryIntent intent, QueryTranslation translation)
    {
        // Destructive actions require confirmation
        return intent.Type == IntentType.Delete ||
               (intent.Type == IntentType.Restore && !translation.Parameters.ContainsKey("confirmed"));
    }

    private bool IsPositiveConfirmation(string message)
    {
        var positive = new[] { "yes", "yeah", "yep", "sure", "ok", "okay", "confirm", "proceed", "do it", "go ahead" };
        return positive.Any(p => message.ToLower().Contains(p));
    }

    private void UpdateContext(ConversationContext context, QueryIntent intent)
    {
        // Update context with extracted entities
        foreach (var entity in intent.Entities)
        {
            switch (entity.Type)
            {
                case EntityType.RelativeTime or EntityType.DateTime:
                    context.TimeRange = new TimeRange
                    {
                        RelativeDescription = entity.Value
                    };
                    break;

                case EntityType.FileType or EntityType.FileExtension:
                    context.FileTypes.Clear();
                    context.FileTypes.Add(entity.NormalizedValue);
                    break;

                case EntityType.FilePath:
                    context.Paths.Clear();
                    context.Paths.Add(entity.Value);
                    break;

                case EntityType.Tag:
                    context.Tags.Add(entity.Value);
                    break;
            }
        }

        // Update current topic
        context.CurrentTopic = intent.Type.ToString();
    }

    private void CleanupExpiredConversations(object? state)
    {
        var expiry = DateTime.UtcNow.AddMinutes(-_config.ConversationTimeoutMinutes);
        var expired = _conversations
            .Where(kv => kv.Value.LastActivityAt < expiry)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var id in expired)
        {
            _conversations.TryRemove(id, out _);
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
        _conversations.Clear();
    }

    #endregion
}

/// <summary>
/// Configuration for the conversation engine.
/// </summary>
public sealed class ConversationEngineConfig
{
    /// <summary>Maximum number of conversations to keep active.</summary>
    public int MaxConversations { get; init; } = 10000;

    /// <summary>Maximum messages per conversation history.</summary>
    public int MaxHistoryLength { get; init; } = 50;

    /// <summary>Conversation timeout in minutes.</summary>
    public int ConversationTimeoutMinutes { get; init; } = 60;

    /// <summary>Confidence threshold for requesting clarification.</summary>
    public double ClarificationThreshold { get; init; } = 0.4;

    /// <summary>Whether to use AI for response generation.</summary>
    public bool UseAIForResponses { get; init; } = true;

    /// <summary>AI provider routing configuration.</summary>
    public AIProviderRouting ProviderRouting { get; init; } = new();
}
