// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Plugins.NaturalLanguageInterface.Engine;
using DataWarehouse.Plugins.NaturalLanguageInterface.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.NaturalLanguageInterface.Voice;

/// <summary>
/// Apple Siri / SiriKit voice handler.
/// Supports Siri Shortcuts and SiriKit Intents.
/// </summary>
public sealed class SiriHandler : IVoiceHandler
{
    private readonly QueryEngine _queryEngine;
    private readonly ConversationEngine _conversationEngine;
    private readonly SiriConfig _config;

    public string PlatformId => "siri";
    public string PlatformName => "Apple Siri";
    public bool IsConfigured => !string.IsNullOrEmpty(_config.AppBundleId);

    public SiriHandler(
        QueryEngine queryEngine,
        ConversationEngine conversationEngine,
        SiriConfig? config = null)
    {
        _queryEngine = queryEngine;
        _conversationEngine = conversationEngine;
        _config = config ?? new SiriConfig();
    }

    public async Task<VoiceResponse> HandleRequestAsync(VoiceRequest request, CancellationToken ct = default)
    {
        // Siri uses intent-based routing
        return request.IntentName switch
        {
            // Custom DataWarehouse intents
            "SearchFilesIntent" => await HandleSearchFilesAsync(request, ct),
            "StorageStatusIntent" => await HandleStorageStatusAsync(request, ct),
            "BackupIntent" => await HandleBackupAsync(request, ct),
            "StorageUsageIntent" => await HandleStorageUsageAsync(request, ct),

            // SiriKit built-in intents we might extend
            "INSearchForFilesIntent" => await HandleSearchFilesAsync(request, ct),
            "INGetCarLockStatusIntent" => CreateUnsupportedResponse(), // Example of unhandled intent

            // Siri Shortcuts
            "RunShortcut" => await HandleShortcutAsync(request, ct),

            // Generic text input (from Shortcuts app)
            "TextInput" or null => await HandleTextInputAsync(request, ct),

            _ => await HandleCustomIntentAsync(request, ct)
        };
    }

    public bool ValidateSignature(string body, string signature, IDictionary<string, string> headers)
    {
        // Siri requests come from your own app, so validation depends on your implementation
        // For server-side handling via App Clip or Siri Shortcuts:
        // - Validate the request came from your app using shared secrets or certificates
        // - Check the device token if using push notifications

        if (_config.SkipValidation) return true;

        // Validate using shared secret
        if (!string.IsNullOrEmpty(_config.SharedSecret) &&
            headers.TryGetValue("X-App-Signature", out var appSig))
        {
            // Compute expected signature: HMAC-SHA256(body, sharedSecret)
            using var hmac = new System.Security.Cryptography.HMACSHA256(
                System.Text.Encoding.UTF8.GetBytes(_config.SharedSecret));
            var expectedSig = Convert.ToBase64String(
                hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(body)));
            return appSig == expectedSig;
        }

        return !string.IsNullOrEmpty(signature);
    }

    public object GetSkillConfiguration()
    {
        return new SiriIntentsConfiguration
        {
            // App configuration
            AppBundleId = _config.AppBundleId,
            IntentsExtensionBundleId = $"{_config.AppBundleId}.Intents",

            // Intent definitions
            IntentDefinitions = new List<SiriIntentDefinition>
            {
                new()
                {
                    ClassName = "SearchFilesIntent",
                    Title = "Search Files",
                    Description = "Search for files in DataWarehouse",
                    Category = "search",
                    SuggestedInvocationPhrase = "Find my files",
                    Parameters = new List<SiriIntentParameter>
                    {
                        new()
                        {
                            Name = "query",
                            DisplayName = "Search Query",
                            Type = "String",
                            IsRequired = false
                        },
                        new()
                        {
                            Name = "fileType",
                            DisplayName = "File Type",
                            Type = "String",
                            IsRequired = false
                        },
                        new()
                        {
                            Name = "dateRange",
                            DisplayName = "Date Range",
                            Type = "DateInterval",
                            IsRequired = false
                        }
                    }
                },
                new()
                {
                    ClassName = "StorageStatusIntent",
                    Title = "Check Storage Status",
                    Description = "Check the status of your DataWarehouse storage",
                    Category = "generic",
                    SuggestedInvocationPhrase = "Check my storage status"
                },
                new()
                {
                    ClassName = "BackupIntent",
                    Title = "Manage Backups",
                    Description = "Create or list DataWarehouse backups",
                    Category = "generic",
                    SuggestedInvocationPhrase = "List my backups",
                    Parameters = new List<SiriIntentParameter>
                    {
                        new()
                        {
                            Name = "action",
                            DisplayName = "Action",
                            Type = "BackupAction",
                            IsRequired = true,
                            PossibleValues = new[] { "create", "list", "verify" }
                        },
                        new()
                        {
                            Name = "backupName",
                            DisplayName = "Backup Name",
                            Type = "String",
                            IsRequired = false
                        }
                    }
                },
                new()
                {
                    ClassName = "StorageUsageIntent",
                    Title = "Check Storage Usage",
                    Description = "See how much storage you're using",
                    Category = "generic",
                    SuggestedInvocationPhrase = "How much storage am I using"
                }
            },

            // Siri Shortcuts
            ShortcutDefinitions = new List<SiriShortcutDefinition>
            {
                new()
                {
                    ActivityType = $"{_config.AppBundleId}.searchFiles",
                    Title = "Search DataWarehouse Files",
                    SuggestedInvocationPhrase = "Find my files",
                    IsEligibleForSearch = true,
                    IsEligibleForPrediction = true
                },
                new()
                {
                    ActivityType = $"{_config.AppBundleId}.storageStatus",
                    Title = "Check Storage Status",
                    SuggestedInvocationPhrase = "Check my storage",
                    IsEligibleForSearch = true,
                    IsEligibleForPrediction = true
                },
                new()
                {
                    ActivityType = $"{_config.AppBundleId}.createBackup",
                    Title = "Create Backup",
                    SuggestedInvocationPhrase = "Back up my data",
                    IsEligibleForSearch = true,
                    IsEligibleForPrediction = true
                },
                new()
                {
                    ActivityType = $"{_config.AppBundleId}.listBackups",
                    Title = "List Backups",
                    SuggestedInvocationPhrase = "Show my backups",
                    IsEligibleForSearch = true,
                    IsEligibleForPrediction = true
                }
            },

            // Webhook configuration
            WebhookUrl = _config.WebhookUrl
        };
    }

    #region Private Methods

    private async Task<VoiceResponse> HandleSearchFilesAsync(VoiceRequest request, CancellationToken ct)
    {
        var query = "Find files";

        if (request.Slots.TryGetValue("query", out var searchQuery) && !string.IsNullOrEmpty(searchQuery))
        {
            query += $" matching {searchQuery}";
        }

        if (request.Slots.TryGetValue("fileType", out var fileType) && !string.IsNullOrEmpty(fileType))
        {
            query += $" of type {fileType}";
        }

        if (request.Slots.TryGetValue("dateRange", out var dateRange) && !string.IsNullOrEmpty(dateRange))
        {
            query += $" from {dateRange}";
        }

        return await ProcessQueryAsync(request, query, ct);
    }

    private async Task<VoiceResponse> HandleStorageStatusAsync(VoiceRequest request, CancellationToken ct)
    {
        return await ProcessQueryAsync(request, "What is my storage status?", ct);
    }

    private async Task<VoiceResponse> HandleBackupAsync(VoiceRequest request, CancellationToken ct)
    {
        var action = request.Slots.TryGetValue("action", out var a) ? a : "list";

        var query = action.ToLower() switch
        {
            "create" => request.Slots.TryGetValue("backupName", out var name)
                ? $"Create a backup called {name}"
                : "Create a new backup",
            "verify" => "Verify my latest backup",
            "list" or _ => "List my backups"
        };

        return await ProcessQueryAsync(request, query, ct);
    }

    private async Task<VoiceResponse> HandleStorageUsageAsync(VoiceRequest request, CancellationToken ct)
    {
        return await ProcessQueryAsync(request, "How much storage am I using?", ct);
    }

    private async Task<VoiceResponse> HandleShortcutAsync(VoiceRequest request, CancellationToken ct)
    {
        // Handle Siri Shortcut invocation
        var activityType = request.Slots.TryGetValue("activityType", out var at) ? at : "";

        return activityType switch
        {
            var t when t.EndsWith("searchFiles") => await HandleSearchFilesAsync(request, ct),
            var t when t.EndsWith("storageStatus") => await HandleStorageStatusAsync(request, ct),
            var t when t.EndsWith("createBackup") => await HandleBackupAsync(
                new VoiceRequest
                {
                    Type = request.Type,
                    IntentName = request.IntentName,
                    Slots = new Dictionary<string, string> { ["action"] = "create" },
                    SessionId = request.SessionId,
                    UserId = request.UserId,
                    Locale = request.Locale,
                    RawTranscript = request.RawTranscript,
                    IsNewSession = request.IsNewSession,
                    Capabilities = request.Capabilities
                }, ct),
            var t when t.EndsWith("listBackups") => await HandleBackupAsync(request, ct),
            _ => await HandleTextInputAsync(request, ct)
        };
    }

    private async Task<VoiceResponse> HandleTextInputAsync(VoiceRequest request, CancellationToken ct)
    {
        var query = request.RawTranscript ?? request.Slots.GetValueOrDefault("input", "help");
        return await ProcessQueryAsync(request, query, ct);
    }

    private async Task<VoiceResponse> HandleCustomIntentAsync(VoiceRequest request, CancellationToken ct)
    {
        // Try to process the intent name as a query
        var query = request.IntentName?.Replace("Intent", "").Replace("_", " ") ?? "help";
        return await ProcessQueryAsync(request, query, ct);
    }

    private async Task<VoiceResponse> ProcessQueryAsync(VoiceRequest request, string query, CancellationToken ct)
    {
        // Get or create conversation
        var conversationId = request.SessionAttributes.TryGetValue("conversationId", out var convId)
            ? convId?.ToString()
            : null;

        if (string.IsNullOrEmpty(conversationId))
        {
            var conversation = _conversationEngine.StartConversation(request.UserId, "siri");
            conversationId = conversation.Id;
        }

        // Process through conversation engine
        var result = await _conversationEngine.ProcessMessageAsync(conversationId, query, null, ct);

        // Build Siri-appropriate response
        return new VoiceResponse
        {
            Speech = result.Response,
            DisplayText = result.Response,
            ShouldEndSession = result.State == DialogueState.Completed ||
                               result.State == DialogueState.PresentingResults,
            SessionAttributes = new Dictionary<string, object>
            {
                ["conversationId"] = conversationId
            },
            // Siri-specific response data
            PlatformData = new Dictionary<string, object>
            {
                ["intentResponse"] = new
                {
                    code = result.Success ? "success" : "failure",
                    userActivity = new
                    {
                        activityType = $"{_config.AppBundleId}.response",
                        title = "DataWarehouse Response",
                        userInfo = new Dictionary<string, object>
                        {
                            ["query"] = query,
                            ["response"] = result.Response
                        }
                    }
                }
            }
        };
    }

    private VoiceResponse CreateUnsupportedResponse()
    {
        return new VoiceResponse
        {
            Speech = "I can't help with that. Try asking about storage status, finding files, or managing backups.",
            DisplayText = "Unsupported request",
            ShouldEndSession = true,
            PlatformData = new Dictionary<string, object>
            {
                ["intentResponse"] = new { code = "failure" }
            }
        };
    }

    #endregion
}

#region Siri Configuration Models

/// <summary>
/// Complete Siri/SiriKit configuration for an app.
/// </summary>
public class SiriIntentsConfiguration
{
    [JsonPropertyName("appBundleId")]
    public string AppBundleId { get; init; } = string.Empty;

    [JsonPropertyName("intentsExtensionBundleId")]
    public string IntentsExtensionBundleId { get; init; } = string.Empty;

    [JsonPropertyName("intentDefinitions")]
    public List<SiriIntentDefinition> IntentDefinitions { get; init; } = new();

    [JsonPropertyName("shortcutDefinitions")]
    public List<SiriShortcutDefinition> ShortcutDefinitions { get; init; } = new();

    [JsonPropertyName("webhookUrl")]
    public string? WebhookUrl { get; init; }
}

/// <summary>
/// SiriKit Intent definition.
/// </summary>
public class SiriIntentDefinition
{
    [JsonPropertyName("className")]
    public string ClassName { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; init; } = "generic";

    [JsonPropertyName("suggestedInvocationPhrase")]
    public string? SuggestedInvocationPhrase { get; init; }

    [JsonPropertyName("parameters")]
    public List<SiriIntentParameter>? Parameters { get; init; }
}

/// <summary>
/// SiriKit Intent parameter.
/// </summary>
public class SiriIntentParameter
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = "String";

    [JsonPropertyName("isRequired")]
    public bool IsRequired { get; init; }

    [JsonPropertyName("possibleValues")]
    public string[]? PossibleValues { get; init; }
}

/// <summary>
/// Siri Shortcut definition.
/// </summary>
public class SiriShortcutDefinition
{
    [JsonPropertyName("activityType")]
    public string ActivityType { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("suggestedInvocationPhrase")]
    public string? SuggestedInvocationPhrase { get; init; }

    [JsonPropertyName("isEligibleForSearch")]
    public bool IsEligibleForSearch { get; init; }

    [JsonPropertyName("isEligibleForPrediction")]
    public bool IsEligibleForPrediction { get; init; }
}

#endregion

/// <summary>
/// Configuration for Siri handler.
/// </summary>
public sealed class SiriConfig
{
    /// <summary>Whether Siri integration is enabled.</summary>
    public bool Enabled { get; init; }

    /// <summary>App Bundle ID.</summary>
    public string AppBundleId { get; init; } = string.Empty;

    /// <summary>Webhook URL for server-side handling.</summary>
    public string WebhookUrl { get; init; } = string.Empty;

    /// <summary>Shared secret for request validation.</summary>
    public string? SharedSecret { get; init; }

    /// <summary>Skip request validation (development only).</summary>
    public bool SkipValidation { get; init; }
}
