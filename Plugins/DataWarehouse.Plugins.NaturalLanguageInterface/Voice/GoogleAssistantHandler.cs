// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Plugins.NaturalLanguageInterface.Engine;
using DataWarehouse.Plugins.NaturalLanguageInterface.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.NaturalLanguageInterface.Voice;

/// <summary>
/// Google Assistant / Actions on Google voice handler.
/// Supports conversational actions with rich responses.
/// </summary>
public sealed class GoogleAssistantHandler : IVoiceHandler
{
    private readonly QueryEngine _queryEngine;
    private readonly ConversationEngine _conversationEngine;
    private readonly GoogleAssistantConfig _config;

    public string PlatformId => "google_assistant";
    public string PlatformName => "Google Assistant";
    public bool IsConfigured => !string.IsNullOrEmpty(_config.ProjectId);

    public GoogleAssistantHandler(
        QueryEngine queryEngine,
        ConversationEngine conversationEngine,
        GoogleAssistantConfig? config = null)
    {
        _queryEngine = queryEngine;
        _conversationEngine = conversationEngine;
        _config = config ?? new GoogleAssistantConfig();
    }

    public async Task<VoiceResponse> HandleRequestAsync(VoiceRequest request, CancellationToken ct = default)
    {
        // Determine handler based on request type
        return request.IntentName switch
        {
            "actions.intent.MAIN" or null when request.Type == VoiceRequestType.Launch
                => HandleWelcome(request),

            "actions.intent.TEXT" => await HandleTextInputAsync(request, ct),

            "actions.intent.CANCEL" => HandleCancel(request),

            "actions.intent.NO_INPUT" => HandleNoInput(request),

            _ => await HandleCustomIntentAsync(request, ct)
        };
    }

    public bool ValidateSignature(string body, string signature, IDictionary<string, string> headers)
    {
        // Google uses OAuth/JWT for authentication
        // In production, validate the Authorization header JWT token
        // using Google's public keys

        if (!headers.TryGetValue("Authorization", out var authHeader))
            return _config.SkipAuthValidation;

        if (!authHeader.StartsWith("Bearer "))
            return false;

        var token = authHeader.Substring(7);

        // Validate JWT token
        // In production: verify signature, issuer, audience, expiration
        return !string.IsNullOrEmpty(token) || _config.SkipAuthValidation;
    }

    public object GetSkillConfiguration()
    {
        return new GoogleActionPackage
        {
            Manifest = new ActionManifest
            {
                DisplayName = "DataWarehouse",
                InvocationName = "data warehouse",
                Category = "business",
                Description = "Manage your data warehouse with voice commands. Search files, check storage status, and manage backups.",
                SmallLogoImage = _config.SmallLogoUrl,
                LargeLogoImage = _config.LargeLogoUrl,
                PrivacyUrl = _config.PrivacyUrl,
                TermsUrl = _config.TermsUrl
            },
            Actions = new List<ActionDefinition>
            {
                new()
                {
                    Name = "MAIN",
                    Intent = new IntentDefinition
                    {
                        Name = "actions.intent.MAIN",
                        Trigger = new TriggerDefinition { QueryPatterns = new[] { "talk to data warehouse" } }
                    },
                    Fulfillment = new FulfillmentDefinition { ConversationName = "datawarehouse" }
                },
                new()
                {
                    Name = "SEARCH_FILES",
                    Intent = new IntentDefinition
                    {
                        Name = "search_files",
                        Trigger = new TriggerDefinition
                        {
                            QueryPatterns = new[]
                            {
                                "find $SchemaOrg_Text:query files",
                                "search for $SchemaOrg_Text:query",
                                "find files from $SchemaOrg_Date:date"
                            }
                        },
                        Parameters = new List<ParameterDefinition>
                        {
                            new() { Name = "query", Type = "SchemaOrg_Text" },
                            new() { Name = "date", Type = "SchemaOrg_Date" }
                        }
                    },
                    Fulfillment = new FulfillmentDefinition { ConversationName = "datawarehouse" }
                },
                new()
                {
                    Name = "STORAGE_STATUS",
                    Intent = new IntentDefinition
                    {
                        Name = "storage_status",
                        Trigger = new TriggerDefinition
                        {
                            QueryPatterns = new[]
                            {
                                "check storage status",
                                "storage status",
                                "how much storage am I using"
                            }
                        }
                    },
                    Fulfillment = new FulfillmentDefinition { ConversationName = "datawarehouse" }
                },
                new()
                {
                    Name = "LIST_BACKUPS",
                    Intent = new IntentDefinition
                    {
                        Name = "list_backups",
                        Trigger = new TriggerDefinition
                        {
                            QueryPatterns = new[]
                            {
                                "list backups",
                                "show my backups",
                                "what backups do I have"
                            }
                        }
                    },
                    Fulfillment = new FulfillmentDefinition { ConversationName = "datawarehouse" }
                }
            },
            Conversations = new Dictionary<string, ConversationDefinition>
            {
                ["datawarehouse"] = new()
                {
                    Name = "datawarehouse",
                    Url = _config.WebhookUrl,
                    FulfillmentApiVersion = 2,
                    InDialogIntents = new[]
                    {
                        new DialogIntentDefinition { Name = "actions.intent.TEXT" },
                        new DialogIntentDefinition { Name = "actions.intent.CANCEL" },
                        new DialogIntentDefinition { Name = "actions.intent.NO_INPUT" }
                    }
                }
            }
        };
    }

    #region Private Methods

    private VoiceResponse HandleWelcome(VoiceRequest request)
    {
        var conversation = _conversationEngine.StartConversation(request.UserId, "google_assistant");

        return new VoiceResponse
        {
            Speech = "Welcome to DataWarehouse! I can help you search files, check storage status, " +
                     "manage backups, and more. What would you like to do?",
            DisplayText = "Welcome to DataWarehouse!",
            Reprompt = "You can say things like 'check my storage status' or 'find files from last week'.",
            ShouldEndSession = false,
            SessionAttributes = new Dictionary<string, object>
            {
                ["conversationId"] = conversation.Id
            },
            Card = new VoiceCard
            {
                Type = VoiceCardType.Standard,
                Title = "DataWarehouse Assistant",
                Content = "Try these commands:\n" +
                         "- Check storage status\n" +
                         "- Find files from last week\n" +
                         "- List my backups\n" +
                         "- How much space am I using?"
            },
            PlatformData = new Dictionary<string, object>
            {
                ["suggestions"] = new[]
                {
                    new { title = "Storage status" },
                    new { title = "Find files" },
                    new { title = "List backups" }
                }
            }
        };
    }

    private async Task<VoiceResponse> HandleTextInputAsync(VoiceRequest request, CancellationToken ct)
    {
        var query = request.RawTranscript ?? "";

        // Get or create conversation
        var conversationId = request.SessionAttributes.TryGetValue("conversationId", out var convId)
            ? convId?.ToString()
            : null;

        if (string.IsNullOrEmpty(conversationId))
        {
            var conversation = _conversationEngine.StartConversation(request.UserId, "google_assistant");
            conversationId = conversation.Id;
        }

        // Process the query
        var result = await _conversationEngine.ProcessMessageAsync(conversationId, query, null, ct);

        return CreateResponseFromResult(result, conversationId);
    }

    private async Task<VoiceResponse> HandleCustomIntentAsync(VoiceRequest request, CancellationToken ct)
    {
        // Build query from intent
        var query = request.IntentName switch
        {
            "search_files" => BuildSearchQuery(request.Slots),
            "storage_status" => "What is my storage status?",
            "list_backups" => "List my backups",
            "create_backup" => BuildBackupQuery(request.Slots),
            _ => request.RawTranscript ?? "help"
        };

        // Get or create conversation
        var conversationId = request.SessionAttributes.TryGetValue("conversationId", out var convId)
            ? convId?.ToString()
            : null;

        if (string.IsNullOrEmpty(conversationId))
        {
            var conversation = _conversationEngine.StartConversation(request.UserId, "google_assistant");
            conversationId = conversation.Id;
        }

        var result = await _conversationEngine.ProcessMessageAsync(conversationId, query, null, ct);

        return CreateResponseFromResult(result, conversationId);
    }

    private VoiceResponse HandleCancel(VoiceRequest request)
    {
        // End conversation
        if (request.SessionAttributes.TryGetValue("conversationId", out var convId) &&
            convId is string conversationId)
        {
            _conversationEngine.EndConversation(conversationId);
        }

        return new VoiceResponse
        {
            Speech = "Goodbye! Come back anytime you need help with your data.",
            ShouldEndSession = true
        };
    }

    private VoiceResponse HandleNoInput(VoiceRequest request)
    {
        // Get no-input count
        var noInputCount = request.SessionAttributes.TryGetValue("noInputCount", out var count)
            ? Convert.ToInt32(count) + 1
            : 1;

        if (noInputCount >= 3)
        {
            return new VoiceResponse
            {
                Speech = "I didn't hear anything. Let's try again later. Goodbye!",
                ShouldEndSession = true
            };
        }

        var prompts = new[]
        {
            "I didn't catch that. What would you like to do with your data warehouse?",
            "I'm still here. You can ask me about storage status, find files, or manage backups.",
            "Just say something like 'check my storage' or 'find recent files'."
        };

        return new VoiceResponse
        {
            Speech = prompts[Math.Min(noInputCount - 1, prompts.Length - 1)],
            ShouldEndSession = false,
            SessionAttributes = new Dictionary<string, object>(request.SessionAttributes)
            {
                ["noInputCount"] = noInputCount
            }
        };
    }

    private string BuildSearchQuery(Dictionary<string, string> slots)
    {
        var parts = new List<string> { "Find files" };

        if (slots.TryGetValue("query", out var query) && !string.IsNullOrEmpty(query))
        {
            parts.Add($"matching {query}");
        }

        if (slots.TryGetValue("date", out var date) && !string.IsNullOrEmpty(date))
        {
            parts.Add($"from {date}");
        }

        return string.Join(" ", parts);
    }

    private string BuildBackupQuery(Dictionary<string, string> slots)
    {
        if (slots.TryGetValue("name", out var name) && !string.IsNullOrEmpty(name))
        {
            return $"Create a backup called {name}";
        }
        return "Create a new backup";
    }

    private VoiceResponse CreateResponseFromResult(ConversationTurnResult result, string conversationId)
    {
        var suggestions = result.SuggestedFollowUps.Count > 0
            ? result.SuggestedFollowUps.Take(8).Select(s => new { title = s.Length > 25 ? s.Substring(0, 22) + "..." : s }).ToArray()
            : null;

        return new VoiceResponse
        {
            Speech = result.Response,
            DisplayText = result.Response,
            Reprompt = result.State == DialogueState.WaitingForClarification
                ? result.Clarification?.Question
                : null,
            ShouldEndSession = result.State == DialogueState.Completed,
            SessionAttributes = new Dictionary<string, object>
            {
                ["conversationId"] = conversationId
            },
            Card = result.Data != null ? new VoiceCard
            {
                Type = VoiceCardType.Standard,
                Title = "DataWarehouse",
                Content = result.Response
            } : null,
            PlatformData = new Dictionary<string, object>
            {
                ["suggestions"] = suggestions ?? Array.Empty<object>()
            }
        };
    }

    #endregion
}

#region Google Action Configuration Models

public class GoogleActionPackage
{
    [JsonPropertyName("manifest")]
    public ActionManifest Manifest { get; init; } = new();

    [JsonPropertyName("actions")]
    public List<ActionDefinition> Actions { get; init; } = new();

    [JsonPropertyName("conversations")]
    public Dictionary<string, ConversationDefinition> Conversations { get; init; } = new();
}

public class ActionManifest
{
    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("invocationName")]
    public string InvocationName { get; init; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("smallLogoImage")]
    public string? SmallLogoImage { get; init; }

    [JsonPropertyName("largeBannerImage")]
    public string? LargeLogoImage { get; init; }

    [JsonPropertyName("privacyUrl")]
    public string? PrivacyUrl { get; init; }

    [JsonPropertyName("termsOfServiceUrl")]
    public string? TermsUrl { get; init; }
}

public class ActionDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("intent")]
    public IntentDefinition Intent { get; init; } = new();

    [JsonPropertyName("fulfillment")]
    public FulfillmentDefinition Fulfillment { get; init; } = new();
}

public class IntentDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("trigger")]
    public TriggerDefinition? Trigger { get; init; }

    [JsonPropertyName("parameters")]
    public List<ParameterDefinition>? Parameters { get; init; }
}

public class TriggerDefinition
{
    [JsonPropertyName("queryPatterns")]
    public string[] QueryPatterns { get; init; } = Array.Empty<string>();
}

public class ParameterDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;
}

public class FulfillmentDefinition
{
    [JsonPropertyName("conversationName")]
    public string ConversationName { get; init; } = string.Empty;
}

public class ConversationDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("fulfillmentApiVersion")]
    public int FulfillmentApiVersion { get; init; } = 2;

    [JsonPropertyName("inDialogIntents")]
    public DialogIntentDefinition[] InDialogIntents { get; init; } = Array.Empty<DialogIntentDefinition>();
}

public class DialogIntentDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;
}

#endregion

/// <summary>
/// Configuration for Google Assistant handler.
/// </summary>
public sealed class GoogleAssistantConfig
{
    /// <summary>Whether Google Assistant integration is enabled.</summary>
    public bool Enabled { get; init; }

    /// <summary>Google Cloud Project ID.</summary>
    public string ProjectId { get; init; } = string.Empty;

    /// <summary>Webhook URL.</summary>
    public string WebhookUrl { get; init; } = string.Empty;

    /// <summary>Skip authentication validation (development only).</summary>
    public bool SkipAuthValidation { get; init; }

    /// <summary>Small logo URL.</summary>
    public string? SmallLogoUrl { get; init; }

    /// <summary>Large logo URL.</summary>
    public string? LargeLogoUrl { get; init; }

    /// <summary>Privacy policy URL.</summary>
    public string? PrivacyUrl { get; init; }

    /// <summary>Terms of service URL.</summary>
    public string? TermsUrl { get; init; }
}
