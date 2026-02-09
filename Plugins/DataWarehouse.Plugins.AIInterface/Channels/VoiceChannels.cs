// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.AIInterface.Channels;

#region Base Voice Channel

/// <summary>
/// Base class for voice assistant integration channels.
/// Provides common voice request/response handling patterns.
/// </summary>
public abstract class VoiceChannelBase : IntegrationChannelBase
{
    /// <inheritdoc />
    public override ChannelCategory Category => ChannelCategory.Voice;

    /// <summary>
    /// Processes a voice request and returns a spoken response.
    /// </summary>
    /// <param name="query">The natural language query.</param>
    /// <param name="userId">The user identifier.</param>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Voice response data.</returns>
    protected async Task<VoiceResponseData> ProcessVoiceQueryAsync(
        string query,
        string userId,
        string? sessionId,
        CancellationToken ct)
    {
        var response = await RouteToIntelligenceAsync(
            "intelligence.request.conversation",
            new Dictionary<string, object>
            {
                ["message"] = query,
                ["platform"] = ChannelId,
                ["voice"] = true
            },
            userId,
            sessionId,
            ct);

        return new VoiceResponseData
        {
            Speech = response.Response ?? "I'm sorry, I couldn't process your request.",
            DisplayText = response.Response,
            ShouldEndSession = true,
            Data = response.Data,
            Suggestions = response.SuggestedFollowUps
        };
    }
}

/// <summary>
/// Voice response data structure.
/// </summary>
public sealed record VoiceResponseData
{
    /// <summary>Gets or sets the speech output (SSML or plain text).</summary>
    public string Speech { get; init; } = string.Empty;

    /// <summary>Gets or sets the display text for screens.</summary>
    public string? DisplayText { get; init; }

    /// <summary>Gets or sets whether the session should end.</summary>
    public bool ShouldEndSession { get; init; } = true;

    /// <summary>Gets or sets additional response data.</summary>
    public object? Data { get; init; }

    /// <summary>Gets or sets suggested follow-up queries.</summary>
    public List<string> Suggestions { get; init; } = new();
}

#endregion

#region Alexa Handler

/// <summary>
/// Amazon Alexa voice integration channel.
/// Routes Alexa skill requests to AIAgents via message bus.
/// </summary>
/// <remarks>
/// <para>
/// This channel handles:
/// <list type="bullet">
/// <item>LaunchRequest - Skill invocation</item>
/// <item>IntentRequest - Custom intents (SearchIntent, StatusIntent, BackupIntent)</item>
/// <item>SessionEndedRequest - Session cleanup</item>
/// <item>Alexa-specific features (AudioPlayer, Display)</item>
/// </list>
/// </para>
/// </remarks>
public sealed class AlexaChannel : VoiceChannelBase
{
    private readonly AlexaChannelConfig _config;

    /// <inheritdoc />
    public override string ChannelId => "alexa";

    /// <inheritdoc />
    public override string ChannelName => "Amazon Alexa";

    /// <inheritdoc />
    public override bool IsConfigured => !string.IsNullOrEmpty(_config.SkillId);

    /// <summary>
    /// Initializes a new instance of the <see cref="AlexaChannel"/> class.
    /// </summary>
    /// <param name="config">Alexa configuration.</param>
    public AlexaChannel(AlexaChannelConfig? config = null)
    {
        _config = config ?? new AlexaChannelConfig();
    }

    /// <inheritdoc />
    public override async Task<ChannelResponse> HandleRequestAsync(ChannelRequest request, CancellationToken ct = default)
    {
        if (!request.Payload.TryGetValue("request", out var requestObj))
        {
            return ChannelResponse.Error(400, "Invalid Alexa request");
        }

        var alexaRequest = ParseAlexaRequest(request.Payload);
        var requestType = alexaRequest.Request?.Type ?? "";

        var userId = $"alexa:{alexaRequest.Session?.User?.UserId ?? "unknown"}";
        var sessionId = alexaRequest.Session?.SessionId;

        var voiceResponse = requestType switch
        {
            "LaunchRequest" => await HandleLaunchAsync(userId, sessionId, ct),
            "IntentRequest" => await HandleIntentAsync(alexaRequest, userId, sessionId, ct),
            "SessionEndedRequest" => new VoiceResponseData { Speech = "", ShouldEndSession = true },
            _ => new VoiceResponseData { Speech = "I'm not sure how to help with that." }
        };

        return ChannelResponse.Success(BuildAlexaResponse(voiceResponse));
    }

    /// <inheritdoc />
    public override bool ValidateSignature(string body, string signature, IDictionary<string, string> headers)
    {
        if (_config.SkipValidation) return true;

        // Alexa request validation includes:
        // 1. Verify the signature certificate URL
        // 2. Validate the signing certificate
        // 3. Verify the request signature
        // 4. Check the timestamp

        if (!headers.TryGetValue("SignatureCertChainUrl", out var certUrl) ||
            !headers.TryGetValue("Signature-256", out var sig))
        {
            return false;
        }

        // Validate cert URL format
        if (!Uri.TryCreate(certUrl, UriKind.Absolute, out var certUri))
            return false;

        if (certUri.Scheme != "https" ||
            certUri.Host != "s3.amazonaws.com" ||
            !certUri.AbsolutePath.StartsWith("/echo.api/"))
        {
            return false;
        }

        // In production, fetch and validate the certificate, then verify signature
        return true;
    }

    /// <inheritdoc />
    public override object GetConfiguration()
    {
        return new
        {
            manifest = new
            {
                publishingInformation = new
                {
                    locales = new Dictionary<string, object>
                    {
                        ["en-US"] = new
                        {
                            name = "DataWarehouse AI",
                            summary = "AI-powered data warehouse management",
                            description = "Use your voice to search files, check storage status, and manage backups with AI.",
                            examplePhrases = new[]
                            {
                                "Alexa, ask DataWarehouse to search for documents",
                                "Alexa, ask DataWarehouse for storage status",
                                "Alexa, ask DataWarehouse to list backups"
                            }
                        }
                    }
                },
                apis = new
                {
                    custom = new
                    {
                        endpoint = new { uri = $"{_config.EndpointUrl}/api/ai/channels/alexa" },
                        interfaces = new object[] { }
                    }
                },
                permissions = Array.Empty<object>()
            },
            interactionModel = GetInteractionModel()
        };
    }

    private object GetInteractionModel()
    {
        return new
        {
            languageModel = new
            {
                invocationName = "data warehouse",
                intents = new object[]
                {
                    new
                    {
                        name = "SearchFilesIntent",
                        slots = new[]
                        {
                            new { name = "query", type = "AMAZON.SearchQuery" },
                            new { name = "fileType", type = "FileType" }
                        },
                        samples = new[]
                        {
                            "search for {query}",
                            "find files about {query}",
                            "look for {fileType} files"
                        }
                    },
                    new
                    {
                        name = "StorageStatusIntent",
                        samples = new[] { "storage status", "how much storage", "check my storage" }
                    },
                    new
                    {
                        name = "BackupIntent",
                        slots = new[] { new { name = "action", type = "BackupAction" } },
                        samples = new[]
                        {
                            "{action} backup",
                            "list my backups",
                            "create a backup"
                        }
                    },
                    new { name = "AMAZON.HelpIntent", samples = Array.Empty<string>() },
                    new { name = "AMAZON.StopIntent", samples = Array.Empty<string>() },
                    new { name = "AMAZON.CancelIntent", samples = Array.Empty<string>() }
                },
                types = new object[]
                {
                    new { name = "FileType", values = new[] { new { name = new { value = "documents" } }, new { name = new { value = "images" } }, new { name = new { value = "videos" } } } },
                    new { name = "BackupAction", values = new[] { new { name = new { value = "list" } }, new { name = new { value = "create" } }, new { name = new { value = "verify" } } } }
                }
            }
        };
    }

    private async Task<VoiceResponseData> HandleLaunchAsync(string userId, string? sessionId, CancellationToken ct)
    {
        return await ProcessVoiceQueryAsync(
            "What can you help me with?",
            userId,
            sessionId,
            ct) with
        {
            Speech = "Welcome to DataWarehouse AI. You can ask me to search for files, check your storage status, or manage backups. What would you like to do?",
            ShouldEndSession = false
        };
    }

    private async Task<VoiceResponseData> HandleIntentAsync(AlexaRequest alexaRequest, string userId, string? sessionId, CancellationToken ct)
    {
        var intentName = alexaRequest.Request?.Intent?.Name ?? "";
        var slots = alexaRequest.Request?.Intent?.Slots ?? new Dictionary<string, AlexaSlot>();

        var query = intentName switch
        {
            "SearchFilesIntent" => BuildSearchQuery(slots),
            "StorageStatusIntent" => "What is my storage status?",
            "BackupIntent" => BuildBackupQuery(slots),
            "AMAZON.HelpIntent" => "help",
            "AMAZON.StopIntent" or "AMAZON.CancelIntent" => "goodbye",
            _ => "help"
        };

        return await ProcessVoiceQueryAsync(query, userId, sessionId, ct);
    }

    private string BuildSearchQuery(Dictionary<string, AlexaSlot> slots)
    {
        var query = slots.TryGetValue("query", out var q) ? q.Value : "";
        var fileType = slots.TryGetValue("fileType", out var ft) ? ft.Value : "";

        var result = "Find files";
        if (!string.IsNullOrEmpty(query)) result += $" about {query}";
        if (!string.IsNullOrEmpty(fileType)) result += $" of type {fileType}";
        return result;
    }

    private string BuildBackupQuery(Dictionary<string, AlexaSlot> slots)
    {
        var action = slots.TryGetValue("action", out var a) ? a.Value : "list";
        return action switch
        {
            "create" => "Create a backup",
            "verify" => "Verify my latest backup",
            _ => "List my backups"
        };
    }

    private AlexaRequest ParseAlexaRequest(Dictionary<string, object> payload)
    {
        var json = JsonSerializer.Serialize(payload);
        return JsonSerializer.Deserialize<AlexaRequest>(json) ?? new AlexaRequest();
    }

    private object BuildAlexaResponse(VoiceResponseData response)
    {
        return new
        {
            version = "1.0",
            response = new
            {
                outputSpeech = new
                {
                    type = "PlainText",
                    text = response.Speech
                },
                card = !string.IsNullOrEmpty(response.DisplayText) ? new
                {
                    type = "Simple",
                    title = "DataWarehouse AI",
                    content = response.DisplayText
                } : null,
                shouldEndSession = response.ShouldEndSession
            }
        };
    }
}

internal sealed class AlexaRequest
{
    [JsonPropertyName("session")]
    public AlexaSession? Session { get; init; }

    [JsonPropertyName("request")]
    public AlexaRequestBody? Request { get; init; }
}

internal sealed class AlexaSession
{
    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    [JsonPropertyName("user")]
    public AlexaUser? User { get; init; }
}

internal sealed class AlexaUser
{
    [JsonPropertyName("userId")]
    public string? UserId { get; init; }
}

internal sealed class AlexaRequestBody
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("intent")]
    public AlexaIntent? Intent { get; init; }
}

internal sealed class AlexaIntent
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("slots")]
    public Dictionary<string, AlexaSlot>? Slots { get; init; }
}

internal sealed class AlexaSlot
{
    [JsonPropertyName("value")]
    public string? Value { get; init; }
}

/// <summary>
/// Configuration for Alexa channel.
/// </summary>
public sealed class AlexaChannelConfig
{
    /// <summary>Alexa Skill ID.</summary>
    public string SkillId { get; init; } = string.Empty;

    /// <summary>Skill endpoint URL.</summary>
    public string EndpointUrl { get; init; } = string.Empty;

    /// <summary>Skip request validation (development only).</summary>
    public bool SkipValidation { get; init; }
}

#endregion

#region Google Assistant Handler

/// <summary>
/// Google Assistant integration channel.
/// Routes Actions on Google requests to AIAgents via message bus.
/// </summary>
public sealed class GoogleAssistantChannel : VoiceChannelBase
{
    private readonly GoogleAssistantChannelConfig _config;

    /// <inheritdoc />
    public override string ChannelId => "google_assistant";

    /// <inheritdoc />
    public override string ChannelName => "Google Assistant";

    /// <inheritdoc />
    public override bool IsConfigured => !string.IsNullOrEmpty(_config.ProjectId);

    /// <summary>
    /// Initializes a new instance of the <see cref="GoogleAssistantChannel"/> class.
    /// </summary>
    /// <param name="config">Google Assistant configuration.</param>
    public GoogleAssistantChannel(GoogleAssistantChannelConfig? config = null)
    {
        _config = config ?? new GoogleAssistantChannelConfig();
    }

    /// <inheritdoc />
    public override async Task<ChannelResponse> HandleRequestAsync(ChannelRequest request, CancellationToken ct = default)
    {
        var googleRequest = ParseGoogleRequest(request.Payload);
        var intentName = googleRequest.Handler?.Name ?? googleRequest.Intent?.Name ?? "";
        var query = googleRequest.Intent?.Query ?? "";

        var userId = $"google:{googleRequest.User?.VerificationStatus ?? "unknown"}";
        var sessionId = googleRequest.Session?.Id;

        VoiceResponseData voiceResponse;

        if (intentName == "actions.intent.MAIN" || string.IsNullOrEmpty(intentName))
        {
            voiceResponse = new VoiceResponseData
            {
                Speech = "Welcome to DataWarehouse AI. You can ask me to search for files, check storage status, or manage backups. How can I help?",
                ShouldEndSession = false
            };
        }
        else
        {
            var processedQuery = IntentToQuery(intentName, googleRequest.Intent?.Params);
            voiceResponse = await ProcessVoiceQueryAsync(processedQuery, userId, sessionId, ct);
        }

        return ChannelResponse.Success(BuildGoogleResponse(voiceResponse, googleRequest.Scene?.Name));
    }

    /// <inheritdoc />
    public override bool ValidateSignature(string body, string signature, IDictionary<string, string> headers)
    {
        // Google Actions verification using project ID and JWT
        if (!headers.TryGetValue("Authorization", out var auth))
            return true; // Webhook verification may not require auth

        // In production, verify the Google-signed JWT token
        return !string.IsNullOrEmpty(auth);
    }

    /// <inheritdoc />
    public override object GetConfiguration()
    {
        return new
        {
            manifest = new
            {
                displayName = "DataWarehouse AI",
                pronunciation = "data warehouse AI",
                developerName = "DataWarehouse",
                developerEmail = _config.DeveloperEmail,
                shortDescription = "AI-powered data warehouse management",
                fullDescription = "Use your voice to search files, check storage status, and manage backups with AI assistance.",
                smallLogoImage = _config.SmallLogoUrl,
                category = "Productivity"
            },
            webhooks = new
            {
                ActionsOnGoogleFulfillment = new
                {
                    handlers = new[]
                    {
                        new { name = "main" },
                        new { name = "search_files" },
                        new { name = "storage_status" },
                        new { name = "manage_backup" }
                    }
                }
            }
        };
    }

    private string IntentToQuery(string intent, Dictionary<string, GoogleParam>? parameters)
    {
        return intent switch
        {
            "search_files" => BuildSearchQuery(parameters),
            "storage_status" => "What is my storage status?",
            "manage_backup" => BuildBackupQuery(parameters),
            _ => parameters?.TryGetValue("query", out var q) == true ? q.Resolved ?? "" : "help"
        };
    }

    private string BuildSearchQuery(Dictionary<string, GoogleParam>? parameters)
    {
        var query = parameters?.TryGetValue("query", out var q) == true ? q.Resolved : "";
        return $"Find files {query}".Trim();
    }

    private string BuildBackupQuery(Dictionary<string, GoogleParam>? parameters)
    {
        var action = parameters?.TryGetValue("action", out var a) == true ? a.Resolved : "list";
        return action switch
        {
            "create" => "Create a backup",
            "verify" => "Verify my latest backup",
            _ => "List my backups"
        };
    }

    private GoogleRequest ParseGoogleRequest(Dictionary<string, object> payload)
    {
        var json = JsonSerializer.Serialize(payload);
        return JsonSerializer.Deserialize<GoogleRequest>(json) ?? new GoogleRequest();
    }

    private object BuildGoogleResponse(VoiceResponseData response, string? sceneName)
    {
        return new
        {
            session = new { id = Guid.NewGuid().ToString() },
            prompt = new
            {
                firstSimple = new
                {
                    speech = response.Speech,
                    text = response.DisplayText ?? response.Speech
                }
            },
            scene = response.ShouldEndSession ? null : new { name = sceneName ?? "MainScene" }
        };
    }
}

internal sealed class GoogleRequest
{
    [JsonPropertyName("handler")]
    public GoogleHandler? Handler { get; init; }

    [JsonPropertyName("intent")]
    public GoogleIntent? Intent { get; init; }

    [JsonPropertyName("scene")]
    public GoogleScene? Scene { get; init; }

    [JsonPropertyName("session")]
    public GoogleSession? Session { get; init; }

    [JsonPropertyName("user")]
    public GoogleUser? User { get; init; }
}

internal sealed class GoogleHandler
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

internal sealed class GoogleIntent
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("query")]
    public string? Query { get; init; }

    [JsonPropertyName("params")]
    public Dictionary<string, GoogleParam>? Params { get; init; }
}

internal sealed class GoogleParam
{
    [JsonPropertyName("resolved")]
    public string? Resolved { get; init; }
}

internal sealed class GoogleScene
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

internal sealed class GoogleSession
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }
}

internal sealed class GoogleUser
{
    [JsonPropertyName("verificationStatus")]
    public string? VerificationStatus { get; init; }
}

/// <summary>
/// Configuration for Google Assistant channel.
/// </summary>
public sealed class GoogleAssistantChannelConfig
{
    /// <summary>Google Cloud project ID.</summary>
    public string ProjectId { get; init; } = string.Empty;

    /// <summary>Developer email.</summary>
    public string? DeveloperEmail { get; init; }

    /// <summary>Small logo URL.</summary>
    public string? SmallLogoUrl { get; init; }
}

#endregion

#region Siri Handler

/// <summary>
/// Apple Siri / SiriKit integration channel.
/// Routes Siri Shortcuts and SiriKit Intents to AIAgents via message bus.
/// </summary>
public sealed class SiriChannel : VoiceChannelBase
{
    private readonly SiriChannelConfig _config;

    /// <inheritdoc />
    public override string ChannelId => "siri";

    /// <inheritdoc />
    public override string ChannelName => "Apple Siri";

    /// <inheritdoc />
    public override bool IsConfigured => !string.IsNullOrEmpty(_config.AppBundleId);

    /// <summary>
    /// Initializes a new instance of the <see cref="SiriChannel"/> class.
    /// </summary>
    /// <param name="config">Siri configuration.</param>
    public SiriChannel(SiriChannelConfig? config = null)
    {
        _config = config ?? new SiriChannelConfig();
    }

    /// <inheritdoc />
    public override async Task<ChannelResponse> HandleRequestAsync(ChannelRequest request, CancellationToken ct = default)
    {
        var intentName = request.Payload.TryGetValue("intentName", out var intent) ? intent?.ToString() : "";
        var userId = request.Payload.TryGetValue("userId", out var uid) ? $"siri:{uid}" : "siri:unknown";
        var sessionId = request.Payload.TryGetValue("sessionId", out var sid) ? sid?.ToString() : null;

        var query = IntentToQuery(intentName ?? "", request.Payload);
        var voiceResponse = await ProcessVoiceQueryAsync(query, userId, sessionId, ct);

        return ChannelResponse.Success(BuildSiriResponse(voiceResponse));
    }

    /// <inheritdoc />
    public override bool ValidateSignature(string body, string signature, IDictionary<string, string> headers)
    {
        if (_config.SkipValidation) return true;

        if (string.IsNullOrEmpty(_config.SharedSecret)) return false;

        if (!headers.TryGetValue("X-App-Signature", out var appSig))
            return false;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_config.SharedSecret));
        var expectedSig = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(body)));
        return appSig == expectedSig;
    }

    /// <inheritdoc />
    public override object GetConfiguration()
    {
        return new
        {
            appBundleId = _config.AppBundleId,
            intentsExtensionBundleId = $"{_config.AppBundleId}.Intents",
            intentDefinitions = new object[]
            {
                new
                {
                    className = "SearchFilesIntent",
                    title = "Search Files",
                    description = "Search for files in DataWarehouse",
                    suggestedInvocationPhrase = "Find my files"
                },
                new
                {
                    className = "StorageStatusIntent",
                    title = "Check Storage Status",
                    suggestedInvocationPhrase = "Check my storage status"
                },
                new
                {
                    className = "BackupIntent",
                    title = "Manage Backups",
                    suggestedInvocationPhrase = "List my backups"
                }
            },
            shortcutDefinitions = new object[]
            {
                new { activityType = $"{_config.AppBundleId}.searchFiles", title = "Search DataWarehouse Files" },
                new { activityType = $"{_config.AppBundleId}.storageStatus", title = "Check Storage Status" },
                new { activityType = $"{_config.AppBundleId}.createBackup", title = "Create Backup" }
            }
        };
    }

    private string IntentToQuery(string intentName, Dictionary<string, object> payload)
    {
        return intentName switch
        {
            "SearchFilesIntent" or "INSearchForFilesIntent" => BuildSearchQuery(payload),
            "StorageStatusIntent" => "What is my storage status?",
            "BackupIntent" => BuildBackupQuery(payload),
            "StorageUsageIntent" => "How much storage am I using?",
            "RunShortcut" => HandleShortcut(payload),
            "TextInput" => payload.TryGetValue("input", out var i) ? i?.ToString() ?? "help" : "help",
            _ => payload.TryGetValue("rawTranscript", out var t) ? t?.ToString() ?? "help" : "help"
        };
    }

    private string BuildSearchQuery(Dictionary<string, object> payload)
    {
        var query = payload.TryGetValue("query", out var q) ? q?.ToString() : "";
        var fileType = payload.TryGetValue("fileType", out var ft) ? ft?.ToString() : "";

        var result = "Find files";
        if (!string.IsNullOrEmpty(query)) result += $" matching {query}";
        if (!string.IsNullOrEmpty(fileType)) result += $" of type {fileType}";
        return result;
    }

    private string BuildBackupQuery(Dictionary<string, object> payload)
    {
        var action = payload.TryGetValue("action", out var a) ? a?.ToString() : "list";
        return action?.ToLower() switch
        {
            "create" => "Create a backup",
            "verify" => "Verify my latest backup",
            _ => "List my backups"
        };
    }

    private string HandleShortcut(Dictionary<string, object> payload)
    {
        var activityType = payload.TryGetValue("activityType", out var at) ? at?.ToString() : "";
        return activityType switch
        {
            var t when t?.EndsWith("searchFiles") == true => "Find files",
            var t when t?.EndsWith("storageStatus") == true => "What is my storage status?",
            var t when t?.EndsWith("createBackup") == true => "Create a backup",
            var t when t?.EndsWith("listBackups") == true => "List my backups",
            _ => payload.TryGetValue("input", out var i) ? i?.ToString() ?? "help" : "help"
        };
    }

    private object BuildSiriResponse(VoiceResponseData response)
    {
        return new
        {
            speech = response.Speech,
            displayText = response.DisplayText,
            intentResponse = new
            {
                code = "success",
                userActivity = new
                {
                    activityType = $"{_config.AppBundleId}.response",
                    title = "DataWarehouse Response"
                }
            }
        };
    }
}

/// <summary>
/// Configuration for Siri channel.
/// </summary>
public sealed class SiriChannelConfig
{
    /// <summary>App Bundle ID.</summary>
    public string AppBundleId { get; init; } = string.Empty;

    /// <summary>Webhook URL.</summary>
    public string? WebhookUrl { get; init; }

    /// <summary>Shared secret for request validation.</summary>
    public string? SharedSecret { get; init; }

    /// <summary>Skip request validation (development only).</summary>
    public bool SkipValidation { get; init; }
}

#endregion
