using System;
using System.Collections.Generic;

namespace DataWarehouse.SDK.Contracts.IntelligenceAware
{
    /// <summary>Result of intent parsing from natural language input.</summary>
    public sealed class IntentParseResult
    {
        public string? Intent { get; init; }
        public double Confidence { get; init; }
        public Dictionary<string, object> Entities { get; init; } = new();
        public IntentAlternative[] AlternativeIntents { get; init; } = Array.Empty<IntentAlternative>();
    }

    /// <summary>Alternative intent suggestion.</summary>
    public sealed class IntentAlternative
    {
        public string Intent { get; init; } = string.Empty;
        public double Confidence { get; init; }
    }

    /// <summary>Conversation response.</summary>
    public sealed class ConversationResponse
    {
        public string Response { get; init; } = string.Empty;
        public string? Intent { get; init; }
        public Dictionary<string, object>? Entities { get; init; }
        public string[]? SuggestedActions { get; init; }
        public double Confidence { get; init; }
    }

    /// <summary>Conversation message for history.</summary>
    public sealed class ConversationMessage
    {
        public string Role { get; init; } = "user";
        public string Content { get; init; } = string.Empty;
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    }

    /// <summary>Language detection result.</summary>
    public sealed class LanguageDetectionResult
    {
        public string LanguageCode { get; init; } = "unknown";
        public string LanguageName { get; init; } = "Unknown";
        public double Confidence { get; init; }
        public LanguageAlternative[] Alternatives { get; init; } = Array.Empty<LanguageAlternative>();
    }

    /// <summary>Alternative language suggestion.</summary>
    public sealed class LanguageAlternative
    {
        public string LanguageCode { get; init; } = string.Empty;
        public double Confidence { get; init; }
    }
}
