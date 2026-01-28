// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Plugins.NaturalLanguageInterface.Engine;
using DataWarehouse.Plugins.NaturalLanguageInterface.Models;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.NaturalLanguageInterface.Voice;

/// <summary>
/// Amazon Alexa voice handler implementation.
/// Handles Alexa Skills Kit requests and responses.
/// </summary>
public sealed class AlexaHandler : IVoiceHandler
{
    private readonly QueryEngine _queryEngine;
    private readonly ConversationEngine _conversationEngine;
    private readonly AlexaConfig _config;

    public string PlatformId => "alexa";
    public string PlatformName => "Amazon Alexa";
    public bool IsConfigured => !string.IsNullOrEmpty(_config.SkillId);

    public AlexaHandler(
        QueryEngine queryEngine,
        ConversationEngine conversationEngine,
        AlexaConfig? config = null)
    {
        _queryEngine = queryEngine;
        _conversationEngine = conversationEngine;
        _config = config ?? new AlexaConfig();
    }

    public async Task<VoiceResponse> HandleRequestAsync(VoiceRequest request, CancellationToken ct = default)
    {
        return request.Type switch
        {
            VoiceRequestType.Launch => HandleLaunchRequest(request),
            VoiceRequestType.Intent => await HandleIntentRequestAsync(request, ct),
            VoiceRequestType.SessionEnded => HandleSessionEnded(request),
            VoiceRequestType.CanFulfillIntent => HandleCanFulfillIntent(request),
            _ => CreateErrorResponse("I'm sorry, I didn't understand that request.")
        };
    }

    public bool ValidateSignature(string body, string signature, IDictionary<string, string> headers)
    {
        // In production, implement full Alexa signature validation:
        // 1. Verify the URL in the SignatureCertChainUrl header
        // 2. Download and validate the signing certificate
        // 3. Verify the signature using the certificate's public key
        // 4. Check the timestamp is within tolerance

        if (!headers.TryGetValue("SignatureCertChainUrl", out var certUrl) ||
            !headers.TryGetValue("Signature-256", out var sig256))
        {
            return false;
        }

        // Validate cert URL
        if (!Uri.TryCreate(certUrl, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != "https" ||
            !uri.Host.Equals("s3.amazonaws.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith("/echo.api/"))
        {
            return false;
        }

        // For full validation, download cert and verify signature
        // This is a simplified check for development
        return _config.SkipSignatureValidation || !string.IsNullOrEmpty(sig256);
    }

    public object GetSkillConfiguration()
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
                            name = "DataWarehouse Assistant",
                            summary = "Manage your data warehouse with voice commands",
                            description = "Use voice commands to search files, check storage status, manage backups, and more.",
                            examplePhrases = new[]
                            {
                                "Alexa, ask DataWarehouse for my storage status",
                                "Alexa, ask DataWarehouse to find files from last week",
                                "Alexa, ask DataWarehouse how much space I'm using"
                            },
                            keywords = new[] { "data", "storage", "backup", "files", "warehouse" }
                        }
                    },
                    isAvailableWorldwide = true,
                    category = "BUSINESS_AND_FINANCE"
                },
                apis = new
                {
                    custom = new
                    {
                        endpoint = new
                        {
                            uri = _config.WebhookUrl
                        },
                        interfaces = Array.Empty<object>()
                    }
                },
                permissions = Array.Empty<object>()
            },
            interactionModel = GetInteractionModel()
        };
    }

    #region Private Methods

    private VoiceResponse HandleLaunchRequest(VoiceRequest request)
    {
        return new VoiceResponse
        {
            Speech = "<speak>Welcome to DataWarehouse! You can ask me to check your storage status, " +
                     "find files, manage backups, and more. What would you like to do?</speak>",
            DisplayText = "Welcome to DataWarehouse!",
            Reprompt = "You can say things like 'check my storage status' or 'find files from last week'.",
            ShouldEndSession = false,
            Card = new VoiceCard
            {
                Type = VoiceCardType.Simple,
                Title = "DataWarehouse Assistant",
                Content = "Commands:\n- Check storage status\n- Find files\n- List backups\n- Storage usage"
            },
            SessionAttributes = new Dictionary<string, object>
            {
                ["conversationId"] = _conversationEngine.StartConversation(request.UserId, "alexa").Id
            }
        };
    }

    private async Task<VoiceResponse> HandleIntentRequestAsync(VoiceRequest request, CancellationToken ct)
    {
        // Handle built-in intents
        switch (request.IntentName)
        {
            case "AMAZON.HelpIntent":
                return CreateHelpResponse();

            case "AMAZON.StopIntent":
            case "AMAZON.CancelIntent":
                return CreateGoodbyeResponse();

            case "AMAZON.FallbackIntent":
                return CreateFallbackResponse();
        }

        // Get or create conversation
        var conversationId = request.SessionAttributes.TryGetValue("conversationId", out var convId)
            ? convId?.ToString()
            : null;

        if (string.IsNullOrEmpty(conversationId))
        {
            var conversation = _conversationEngine.StartConversation(request.UserId, "alexa");
            conversationId = conversation.Id;
        }

        // Build query from intent and slots
        var query = BuildQueryFromIntent(request);

        // Process through conversation engine
        var result = await _conversationEngine.ProcessMessageAsync(conversationId, query, null, ct);

        return new VoiceResponse
        {
            Speech = FormatSpeech(result.Response),
            DisplayText = result.Response,
            Reprompt = result.State == DialogueState.WaitingForClarification
                ? result.Clarification?.Question
                : null,
            ShouldEndSession = result.State == DialogueState.Completed,
            SessionAttributes = new Dictionary<string, object>
            {
                ["conversationId"] = conversationId
            },
            Card = CreateCardFromResult(result)
        };
    }

    private VoiceResponse HandleSessionEnded(VoiceRequest request)
    {
        // Clean up session
        if (request.SessionAttributes.TryGetValue("conversationId", out var convId) &&
            convId is string conversationId)
        {
            _conversationEngine.EndConversation(conversationId);
        }

        return new VoiceResponse
        {
            ShouldEndSession = true
        };
    }

    private VoiceResponse HandleCanFulfillIntent(VoiceRequest request)
    {
        // Determine if we can handle this intent
        var canFulfill = request.IntentName switch
        {
            "SearchFilesIntent" => "YES",
            "StorageStatusIntent" => "YES",
            "BackupIntent" => "YES",
            "StorageUsageIntent" => "YES",
            _ => "MAYBE"
        };

        return new VoiceResponse
        {
            PlatformData = new Dictionary<string, object>
            {
                ["canFulfillIntent"] = new
                {
                    canFulfill = canFulfill,
                    slots = request.Slots.ToDictionary(
                        kv => kv.Key,
                        kv => new { canUnderstand = "YES", canFulfill = "YES" })
                }
            }
        };
    }

    private string BuildQueryFromIntent(VoiceRequest request)
    {
        return request.IntentName switch
        {
            "SearchFilesIntent" => BuildSearchQuery(request.Slots),
            "StorageStatusIntent" => "What is my storage status?",
            "BackupIntent" => BuildBackupQuery(request.Slots),
            "StorageUsageIntent" => "How much storage am I using?",
            "ListBackupsIntent" => "List my recent backups",
            "ExplainIntent" => BuildExplainQuery(request.Slots),
            _ => request.RawTranscript ?? "help"
        };
    }

    private string BuildSearchQuery(Dictionary<string, string> slots)
    {
        var parts = new List<string> { "Find files" };

        if (slots.TryGetValue("FileType", out var fileType) && !string.IsNullOrEmpty(fileType))
        {
            parts.Add($"of type {fileType}");
        }

        if (slots.TryGetValue("TimeRange", out var timeRange) && !string.IsNullOrEmpty(timeRange))
        {
            parts.Add($"from {timeRange}");
        }

        if (slots.TryGetValue("SearchQuery", out var query) && !string.IsNullOrEmpty(query))
        {
            parts.Add($"containing {query}");
        }

        return string.Join(" ", parts);
    }

    private string BuildBackupQuery(Dictionary<string, string> slots)
    {
        if (slots.TryGetValue("BackupAction", out var action))
        {
            return action.ToLower() switch
            {
                "create" => slots.TryGetValue("BackupName", out var name)
                    ? $"Create a backup called {name}"
                    : "Create a new backup",
                "restore" => "Restore from backup",
                "list" => "List my backups",
                "verify" => "Verify my latest backup",
                _ => "Show backup status"
            };
        }
        return "Show backup status";
    }

    private string BuildExplainQuery(Dictionary<string, string> slots)
    {
        if (slots.TryGetValue("Subject", out var subject))
        {
            return $"Explain why {subject}";
        }
        return "What would you like me to explain?";
    }

    private string FormatSpeech(string text)
    {
        // Wrap in SSML if not already
        if (!text.StartsWith("<speak>"))
        {
            // Escape special characters
            text = text.Replace("&", "&amp;")
                       .Replace("<", "&lt;")
                       .Replace(">", "&gt;");
            text = $"<speak>{text}</speak>";
        }
        return text;
    }

    private VoiceCard? CreateCardFromResult(ConversationTurnResult result)
    {
        if (result.Data == null)
            return null;

        return new VoiceCard
        {
            Type = VoiceCardType.Standard,
            Title = "DataWarehouse",
            Content = result.Response
        };
    }

    private VoiceResponse CreateHelpResponse()
    {
        return new VoiceResponse
        {
            Speech = "<speak>You can ask me to check your storage status, find files by type or date, " +
                     "list or create backups, and explain storage decisions. " +
                     "For example, say 'find PDF files from last week' or 'how much space am I using'. " +
                     "What would you like to do?</speak>",
            Reprompt = "Try saying 'check my storage status' or 'find my recent files'.",
            ShouldEndSession = false,
            Card = new VoiceCard
            {
                Type = VoiceCardType.Simple,
                Title = "DataWarehouse Help",
                Content = "Available commands:\n" +
                         "- Check storage status\n" +
                         "- Find [file type] files from [time period]\n" +
                         "- List my backups\n" +
                         "- Create a backup\n" +
                         "- How much storage am I using?\n" +
                         "- Explain why [file] was archived"
            }
        };
    }

    private VoiceResponse CreateGoodbyeResponse()
    {
        return new VoiceResponse
        {
            Speech = "<speak>Goodbye! Come back anytime you need help with your data.</speak>",
            ShouldEndSession = true
        };
    }

    private VoiceResponse CreateFallbackResponse()
    {
        return new VoiceResponse
        {
            Speech = "<speak>I'm sorry, I didn't understand that. " +
                     "You can ask me about storage status, finding files, or managing backups. " +
                     "What would you like to do?</speak>",
            Reprompt = "Try saying 'help' for a list of things I can do.",
            ShouldEndSession = false
        };
    }

    private VoiceResponse CreateErrorResponse(string message)
    {
        return new VoiceResponse
        {
            Speech = $"<speak>{message}</speak>",
            ShouldEndSession = false
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
                        samples = new[]
                        {
                            "find files",
                            "find {FileType} files",
                            "find files from {TimeRange}",
                            "find {FileType} files from {TimeRange}",
                            "search for {SearchQuery}",
                            "look for {SearchQuery}",
                            "find files containing {SearchQuery}"
                        },
                        slots = new object[]
                        {
                            new { name = "FileType", type = "AMAZON.SearchQuery" },
                            new { name = "TimeRange", type = "AMAZON.DATE" },
                            new { name = "SearchQuery", type = "AMAZON.SearchQuery" }
                        }
                    },
                    new
                    {
                        name = "StorageStatusIntent",
                        samples = new[]
                        {
                            "storage status",
                            "check storage status",
                            "what is my storage status",
                            "show storage status",
                            "system status"
                        },
                        slots = Array.Empty<object>()
                    },
                    new
                    {
                        name = "StorageUsageIntent",
                        samples = new[]
                        {
                            "how much storage",
                            "how much space am I using",
                            "storage usage",
                            "check my usage",
                            "show usage"
                        },
                        slots = Array.Empty<object>()
                    },
                    new
                    {
                        name = "BackupIntent",
                        samples = new[]
                        {
                            "{BackupAction} backup",
                            "{BackupAction} a backup",
                            "{BackupAction} backup called {BackupName}",
                            "backup status"
                        },
                        slots = new object[]
                        {
                            new { name = "BackupAction", type = "BackupActionType" },
                            new { name = "BackupName", type = "AMAZON.SearchQuery" }
                        }
                    },
                    new
                    {
                        name = "ListBackupsIntent",
                        samples = new[]
                        {
                            "list backups",
                            "show my backups",
                            "what backups do I have",
                            "recent backups"
                        },
                        slots = Array.Empty<object>()
                    },
                    new
                    {
                        name = "ExplainIntent",
                        samples = new[]
                        {
                            "explain {Subject}",
                            "why {Subject}",
                            "why was {Subject}",
                            "tell me why {Subject}"
                        },
                        slots = new object[]
                        {
                            new { name = "Subject", type = "AMAZON.SearchQuery" }
                        }
                    },
                    new { name = "AMAZON.HelpIntent", samples = Array.Empty<string>() },
                    new { name = "AMAZON.StopIntent", samples = Array.Empty<string>() },
                    new { name = "AMAZON.CancelIntent", samples = Array.Empty<string>() },
                    new { name = "AMAZON.FallbackIntent", samples = Array.Empty<string>() }
                },
                types = new object[]
                {
                    new
                    {
                        name = "BackupActionType",
                        values = new object[]
                        {
                            new { name = new { value = "create", synonyms = new[] { "make", "start", "run" } } },
                            new { name = new { value = "restore", synonyms = new[] { "recover", "rollback" } } },
                            new { name = new { value = "list", synonyms = new[] { "show", "display" } } },
                            new { name = new { value = "verify", synonyms = new[] { "check", "validate" } } }
                        }
                    }
                }
            }
        };
    }

    #endregion
}

/// <summary>
/// Configuration for Alexa handler.
/// </summary>
public sealed class AlexaConfig
{
    /// <summary>Whether Alexa integration is enabled.</summary>
    public bool Enabled { get; init; }

    /// <summary>Alexa Skill ID.</summary>
    public string SkillId { get; init; } = string.Empty;

    /// <summary>Webhook URL for skill endpoint.</summary>
    public string WebhookUrl { get; init; } = string.Empty;

    /// <summary>Skip signature validation (development only).</summary>
    public bool SkipSignatureValidation { get; init; }
}
