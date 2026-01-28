// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.NaturalLanguageInterface.Models;

/// <summary>
/// Represents a conversation session with context management.
/// </summary>
public sealed class Conversation
{
    /// <summary>Unique conversation identifier.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>User identifier.</summary>
    public string UserId { get; init; } = string.Empty;

    /// <summary>Platform source (slack, teams, discord, api, etc.).</summary>
    public string Platform { get; init; } = "api";

    /// <summary>Channel/thread identifier within the platform.</summary>
    public string? ChannelId { get; init; }

    /// <summary>Conversation messages history.</summary>
    public List<ConversationMessage> Messages { get; } = new();

    /// <summary>Extracted context from conversation.</summary>
    public ConversationContext Context { get; } = new();

    /// <summary>When the conversation started.</summary>
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Last activity timestamp.</summary>
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;

    /// <summary>Conversation language (ISO 639-1 code).</summary>
    public string Language { get; set; } = "en";

    /// <summary>Whether the conversation is active.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Maximum messages to retain in history.</summary>
    public int MaxHistoryLength { get; init; } = 50;

    /// <summary>Add a message to the conversation.</summary>
    public void AddMessage(ConversationMessage message)
    {
        Messages.Add(message);
        LastActivityAt = DateTime.UtcNow;

        // Trim history if exceeded
        while (Messages.Count > MaxHistoryLength)
        {
            Messages.RemoveAt(0);
        }
    }

    /// <summary>Get recent messages for context.</summary>
    public IEnumerable<ConversationMessage> GetRecentMessages(int count = 10)
    {
        return Messages.TakeLast(count);
    }
}

/// <summary>
/// A single message in a conversation.
/// </summary>
public sealed class ConversationMessage
{
    /// <summary>Message identifier.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Role of the sender (user, assistant, system).</summary>
    public string Role { get; init; } = "user";

    /// <summary>Message content.</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>When the message was sent.</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>Extracted intent from this message (if user message).</summary>
    public QueryIntent? Intent { get; set; }

    /// <summary>Entities extracted from this message.</summary>
    public List<ExtractedEntity> Entities { get; init; } = new();

    /// <summary>Attachments (images, files).</summary>
    public List<MessageAttachment> Attachments { get; init; } = new();

    /// <summary>Platform-specific metadata.</summary>
    public Dictionary<string, object> Metadata { get; init; } = new();
}

/// <summary>
/// Extracted context from conversation for follow-up queries.
/// </summary>
public sealed class ConversationContext
{
    /// <summary>Last mentioned time range.</summary>
    public TimeRange? TimeRange { get; set; }

    /// <summary>Last mentioned file types.</summary>
    public List<string> FileTypes { get; } = new();

    /// <summary>Last mentioned paths/locations.</summary>
    public List<string> Paths { get; } = new();

    /// <summary>Last mentioned tags.</summary>
    public List<string> Tags { get; } = new();

    /// <summary>Last mentioned size constraints.</summary>
    public SizeRange? SizeRange { get; set; }

    /// <summary>Last query results for reference.</summary>
    public List<string> LastResultIds { get; } = new();

    /// <summary>Current topic being discussed.</summary>
    public string? CurrentTopic { get; set; }

    /// <summary>Custom context variables.</summary>
    public Dictionary<string, object> Variables { get; } = new();

    /// <summary>Merge new context into existing.</summary>
    public void Merge(ConversationContext other)
    {
        if (other.TimeRange != null) TimeRange = other.TimeRange;
        if (other.FileTypes.Count > 0) { FileTypes.Clear(); FileTypes.AddRange(other.FileTypes); }
        if (other.Paths.Count > 0) { Paths.Clear(); Paths.AddRange(other.Paths); }
        if (other.Tags.Count > 0) { Tags.Clear(); Tags.AddRange(other.Tags); }
        if (other.SizeRange != null) SizeRange = other.SizeRange;
        if (other.LastResultIds.Count > 0) { LastResultIds.Clear(); LastResultIds.AddRange(other.LastResultIds); }
        if (other.CurrentTopic != null) CurrentTopic = other.CurrentTopic;
        foreach (var kv in other.Variables) Variables[kv.Key] = kv.Value;
    }
}

/// <summary>
/// Time range extracted from natural language.
/// </summary>
public sealed record TimeRange
{
    public DateTime? Start { get; init; }
    public DateTime? End { get; init; }
    public string? RelativeDescription { get; init; } // "last week", "yesterday"
}

/// <summary>
/// Size range extracted from natural language.
/// </summary>
public sealed record SizeRange
{
    public long? MinBytes { get; init; }
    public long? MaxBytes { get; init; }
    public string? Description { get; init; } // "larger than 1GB"
}

/// <summary>
/// Message attachment.
/// </summary>
public sealed class MessageAttachment
{
    public string Id { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty; // image, file, audio
    public string? Url { get; init; }
    public string? Base64Content { get; init; }
    public string? FileName { get; init; }
    public string? MimeType { get; init; }
    public long? SizeBytes { get; init; }
}

/// <summary>
/// Clarification request when query is ambiguous.
/// </summary>
public sealed class ClarificationRequest
{
    /// <summary>Type of clarification needed.</summary>
    public ClarificationType Type { get; init; }

    /// <summary>Question to ask the user.</summary>
    public string Question { get; init; } = string.Empty;

    /// <summary>Suggested options for the user.</summary>
    public List<ClarificationOption> Options { get; init; } = new();

    /// <summary>Context for why clarification is needed.</summary>
    public string? Reason { get; init; }
}

/// <summary>
/// Type of clarification needed.
/// </summary>
public enum ClarificationType
{
    AmbiguousIntent,
    MissingParameter,
    MultipleMatches,
    ConfirmAction,
    SpecifyScope,
    SpecifyTimeRange
}

/// <summary>
/// Option for clarification response.
/// </summary>
public sealed class ClarificationOption
{
    public string Label { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public string? Description { get; init; }
}

/// <summary>
/// Multi-turn dialogue state.
/// </summary>
public enum DialogueState
{
    Initial,
    WaitingForClarification,
    ProcessingQuery,
    PresentingResults,
    FollowUp,
    Completed,
    Error
}

/// <summary>
/// Conversation turn result.
/// </summary>
public sealed class ConversationTurnResult
{
    /// <summary>Whether the turn was successful.</summary>
    public bool Success { get; init; }

    /// <summary>Response text to show the user.</summary>
    public string Response { get; init; } = string.Empty;

    /// <summary>Structured data results (if any).</summary>
    public object? Data { get; init; }

    /// <summary>Clarification needed (if any).</summary>
    public ClarificationRequest? Clarification { get; init; }

    /// <summary>Suggested follow-up queries.</summary>
    public List<string> SuggestedFollowUps { get; init; } = new();

    /// <summary>Current dialogue state.</summary>
    public DialogueState State { get; init; }

    /// <summary>Error message if failed.</summary>
    public string? Error { get; init; }

    /// <summary>Platform-specific response formatting.</summary>
    public Dictionary<string, object> PlatformData { get; init; } = new();
}
