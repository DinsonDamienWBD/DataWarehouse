// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Plugins.NaturalLanguageInterface.Engine;
using DataWarehouse.Plugins.NaturalLanguageInterface.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.NaturalLanguageInterface.Integrations;

/// <summary>
/// OpenAI ChatGPT Plugin integration.
/// Implements the ChatGPT Plugin specification with ai-plugin.json and OpenAPI schema.
/// </summary>
public sealed class ChatGPTPluginIntegration : IntegrationProviderBase
{
    private readonly QueryEngine _queryEngine;
    private readonly ConversationEngine _conversationEngine;
    private readonly ChatGPTPluginConfig _config;

    public override string PlatformId => "chatgpt";
    public override string PlatformName => "ChatGPT Plugin";
    public override bool IsConfigured => !string.IsNullOrEmpty(_config.BaseUrl);

    public ChatGPTPluginIntegration(
        QueryEngine queryEngine,
        ConversationEngine conversationEngine,
        ChatGPTPluginConfig? config = null)
    {
        _queryEngine = queryEngine;
        _conversationEngine = conversationEngine;
        _config = config ?? new ChatGPTPluginConfig();
    }

    public override async Task<IntegrationResponse> HandleWebhookAsync(WebhookEvent webhookEvent, CancellationToken ct = default)
    {
        // ChatGPT plugins use REST API endpoints rather than webhooks
        // The webhook event here represents an API call from ChatGPT

        var endpoint = webhookEvent.Type;

        return endpoint switch
        {
            "search" => await HandleSearchAsync(webhookEvent, ct),
            "status" => await HandleStatusAsync(webhookEvent, ct),
            "backup" => await HandleBackupAsync(webhookEvent, ct),
            "explain" => await HandleExplainAsync(webhookEvent, ct),
            "query" => await HandleQueryAsync(webhookEvent, ct),
            _ => ErrorResponse(404, "Endpoint not found")
        };
    }

    public override bool ValidateWebhookSignature(string body, string signature, IDictionary<string, string> headers)
    {
        // ChatGPT uses OAuth or service_http authentication
        // Validate based on configured auth type

        if (_config.AuthType == "none")
        {
            return true;
        }

        if (_config.AuthType == "service_http")
        {
            // Validate bearer token
            if (!headers.TryGetValue("Authorization", out var authHeader))
                return false;

            if (!authHeader.StartsWith("Bearer "))
                return false;

            var token = authHeader.Substring(7);
            return token == _config.ServiceAuthToken;
        }

        // For OAuth, validate the token against OpenAI
        // This requires calling OpenAI's token validation endpoint
        return true;
    }

    public override string GetWebhookEndpoint() => "/.well-known/ai-plugin.json";

    public override object GetConfiguration()
    {
        return new
        {
            manifest = GetPluginManifest(),
            openapi = GetOpenAPISchema()
        };
    }

    /// <summary>
    /// Get the ai-plugin.json manifest.
    /// </summary>
    public ChatGPTPluginManifest GetPluginManifest()
    {
        return new ChatGPTPluginManifest
        {
            SchemaVersion = "v1",
            NameForHuman = "DataWarehouse",
            NameForModel = "datawarehouse",
            DescriptionForHuman = "Manage and query your data warehouse. Search files, check storage status, manage backups, and get explanations.",
            DescriptionForModel = @"Plugin for managing a data warehouse system. Use this plugin when users want to:
- Search for files or data in their storage
- Check storage status, usage, or quotas
- Create, list, or manage backups
- Get explanations about storage decisions (why files were archived, tiered, etc.)
- Understand trends in their data usage

Always prefer specific endpoints (search, status, backup) over the generic query endpoint when the intent is clear.",
            Auth = new ChatGPTPluginAuth
            {
                Type = _config.AuthType,
                AuthorizationType = _config.AuthType == "service_http" ? "bearer" : null,
                VerificationTokens = _config.AuthType == "service_http"
                    ? new Dictionary<string, string> { ["openai"] = _config.VerificationToken ?? "" }
                    : null
            },
            Api = new ChatGPTPluginApi
            {
                Type = "openapi",
                Url = $"{_config.BaseUrl}/.well-known/openapi.yaml",
                IsUserAuthenticated = _config.AuthType == "user_http" || _config.AuthType == "oauth"
            },
            LogoUrl = _config.LogoUrl ?? $"{_config.BaseUrl}/logo.png",
            ContactEmail = _config.ContactEmail,
            LegalInfoUrl = _config.LegalInfoUrl ?? $"{_config.BaseUrl}/legal"
        };
    }

    /// <summary>
    /// Get the OpenAPI schema for the plugin.
    /// </summary>
    public object GetOpenAPISchema()
    {
        return new
        {
            openapi = "3.0.1",
            info = new
            {
                title = "DataWarehouse Plugin",
                description = "API for managing data warehouse operations",
                version = "1.0.0"
            },
            servers = new[]
            {
                new { url = _config.BaseUrl }
            },
            paths = new Dictionary<string, object>
            {
                ["/api/chatgpt/search"] = new
                {
                    post = new
                    {
                        operationId = "searchFiles",
                        summary = "Search for files in the data warehouse",
                        description = "Search files by name, content, type, date range, size, or metadata tags.",
                        requestBody = new
                        {
                            required = true,
                            content = new
                            {
                                application_json = new
                                {
                                    schema = new
                                    {
                                        type = "object",
                                        properties = new Dictionary<string, object>
                                        {
                                            ["query"] = new { type = "string", description = "Natural language search query" },
                                            ["fileType"] = new { type = "string", description = "Filter by file type (e.g., pdf, doc, image)" },
                                            ["dateRange"] = new
                                            {
                                                type = "object",
                                                properties = new
                                                {
                                                    start = new { type = "string", format = "date" },
                                                    end = new { type = "string", format = "date" }
                                                }
                                            },
                                            ["minSize"] = new { type = "integer", description = "Minimum file size in bytes" },
                                            ["maxSize"] = new { type = "integer", description = "Maximum file size in bytes" },
                                            ["tags"] = new { type = "array", items = new { type = "string" } },
                                            ["limit"] = new { type = "integer", @default = 10, maximum = 100 }
                                        },
                                        required = new[] { "query" }
                                    }
                                }
                            }
                        },
                        responses = GetStandardResponses("Search results")
                    }
                },
                ["/api/chatgpt/status"] = new
                {
                    get = new
                    {
                        operationId = "getStatus",
                        summary = "Get storage status and statistics",
                        description = "Returns current storage usage, quota information, and system health status.",
                        responses = GetStandardResponses("Storage status")
                    }
                },
                ["/api/chatgpt/backup"] = new
                {
                    post = new
                    {
                        operationId = "manageBackup",
                        summary = "Manage backups",
                        description = "Create, list, or verify backups.",
                        requestBody = new
                        {
                            required = true,
                            content = new
                            {
                                application_json = new
                                {
                                    schema = new
                                    {
                                        type = "object",
                                        properties = new Dictionary<string, object>
                                        {
                                            ["action"] = new
                                            {
                                                type = "string",
                                                @enum = new[] { "list", "create", "verify" },
                                                description = "Backup action to perform"
                                            },
                                            ["name"] = new { type = "string", description = "Backup name (for create)" },
                                            ["backupId"] = new { type = "string", description = "Backup ID (for verify)" }
                                        },
                                        required = new[] { "action" }
                                    }
                                }
                            }
                        },
                        responses = GetStandardResponses("Backup operation result")
                    }
                },
                ["/api/chatgpt/explain"] = new
                {
                    post = new
                    {
                        operationId = "explainDecision",
                        summary = "Explain a storage decision",
                        description = "Get an explanation for why a file was tiered, archived, or other storage decisions.",
                        requestBody = new
                        {
                            required = true,
                            content = new
                            {
                                application_json = new
                                {
                                    schema = new
                                    {
                                        type = "object",
                                        properties = new Dictionary<string, object>
                                        {
                                            ["fileId"] = new { type = "string", description = "File or item ID" },
                                            ["question"] = new { type = "string", description = "Specific question about the decision" }
                                        },
                                        required = new[] { "question" }
                                    }
                                }
                            }
                        },
                        responses = GetStandardResponses("Explanation")
                    }
                },
                ["/api/chatgpt/query"] = new
                {
                    post = new
                    {
                        operationId = "naturalLanguageQuery",
                        summary = "Process a natural language query",
                        description = "Fallback endpoint for any natural language query about the data warehouse.",
                        requestBody = new
                        {
                            required = true,
                            content = new
                            {
                                application_json = new
                                {
                                    schema = new
                                    {
                                        type = "object",
                                        properties = new Dictionary<string, object>
                                        {
                                            ["query"] = new { type = "string", description = "Natural language query" }
                                        },
                                        required = new[] { "query" }
                                    }
                                }
                            }
                        },
                        responses = GetStandardResponses("Query result")
                    }
                }
            }
        };
    }

    #region Private Methods

    private async Task<IntegrationResponse> HandleSearchAsync(WebhookEvent webhookEvent, CancellationToken ct)
    {
        var query = GetPayloadValue<string>(webhookEvent.Payload, "query") ?? "";
        var fileType = GetPayloadValue<string>(webhookEvent.Payload, "fileType");
        var limit = GetPayloadValue<int?>(webhookEvent.Payload, "limit") ?? 10;

        // Build search query
        var searchQuery = $"Find files {query}";
        if (!string.IsNullOrEmpty(fileType))
        {
            searchQuery += $" of type {fileType}";
        }

        var result = await ProcessQueryAsync(webhookEvent, searchQuery, ct);

        return SuccessResponse(new
        {
            success = result.Success,
            response = result.Response,
            results = result.Data,
            suggestions = result.SuggestedFollowUps
        });
    }

    private async Task<IntegrationResponse> HandleStatusAsync(WebhookEvent webhookEvent, CancellationToken ct)
    {
        var result = await ProcessQueryAsync(webhookEvent, "What is my storage status?", ct);

        return SuccessResponse(new
        {
            success = result.Success,
            status = result.Response,
            data = result.Data
        });
    }

    private async Task<IntegrationResponse> HandleBackupAsync(WebhookEvent webhookEvent, CancellationToken ct)
    {
        var action = GetPayloadValue<string>(webhookEvent.Payload, "action") ?? "list";
        var name = GetPayloadValue<string>(webhookEvent.Payload, "name");

        var query = action switch
        {
            "create" => string.IsNullOrEmpty(name) ? "Create a backup" : $"Create a backup called {name}",
            "verify" => "Verify my latest backup",
            _ => "List my backups"
        };

        var result = await ProcessQueryAsync(webhookEvent, query, ct);

        return SuccessResponse(new
        {
            success = result.Success,
            response = result.Response,
            backups = result.Data
        });
    }

    private async Task<IntegrationResponse> HandleExplainAsync(WebhookEvent webhookEvent, CancellationToken ct)
    {
        var question = GetPayloadValue<string>(webhookEvent.Payload, "question") ?? "";
        var fileId = GetPayloadValue<string>(webhookEvent.Payload, "fileId");

        var query = string.IsNullOrEmpty(fileId)
            ? $"Explain {question}"
            : $"Explain why {fileId} {question}";

        var result = await ProcessQueryAsync(webhookEvent, query, ct);

        return SuccessResponse(new
        {
            success = result.Success,
            explanation = result.Response,
            reasoning = result.Data
        });
    }

    private async Task<IntegrationResponse> HandleQueryAsync(WebhookEvent webhookEvent, CancellationToken ct)
    {
        var query = GetPayloadValue<string>(webhookEvent.Payload, "query") ?? "help";

        var result = await ProcessQueryAsync(webhookEvent, query, ct);

        return SuccessResponse(new
        {
            success = result.Success,
            response = result.Response,
            data = result.Data,
            suggestions = result.SuggestedFollowUps
        });
    }

    private async Task<ConversationTurnResult> ProcessQueryAsync(WebhookEvent webhookEvent, string query, CancellationToken ct)
    {
        // Get user ID from auth context
        var userId = webhookEvent.Headers.TryGetValue("X-OpenAI-User", out var user)
            ? $"chatgpt:{user}"
            : "chatgpt:anonymous";

        // Create or get conversation
        var conversation = _conversationEngine.StartConversation(userId, "chatgpt");

        // Process the query
        return await _conversationEngine.ProcessMessageAsync(conversation.Id, query, null, ct);
    }

    private T? GetPayloadValue<T>(Dictionary<string, object> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value))
            return default;

        if (value is T typed)
            return typed;

        if (value is JsonElement element)
        {
            try
            {
                return element.Deserialize<T>();
            }
            catch
            {
                return default;
            }
        }

        return default;
    }

    private object GetStandardResponses(string description)
    {
        return new Dictionary<string, object>
        {
            ["200"] = new
            {
                description,
                content = new
                {
                    application_json = new
                    {
                        schema = new
                        {
                            type = "object",
                            properties = new
                            {
                                success = new { type = "boolean" },
                                response = new { type = "string" },
                                data = new { type = "object" },
                                suggestions = new { type = "array", items = new { type = "string" } }
                            }
                        }
                    }
                }
            },
            ["400"] = new { description = "Bad request" },
            ["401"] = new { description = "Unauthorized" },
            ["500"] = new { description = "Internal server error" }
        };
    }

    #endregion
}

/// <summary>
/// Configuration for ChatGPT Plugin.
/// </summary>
public sealed class ChatGPTPluginConfig
{
    /// <summary>Whether ChatGPT plugin is enabled.</summary>
    public bool Enabled { get; init; }

    /// <summary>Base URL for the plugin API.</summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>Authentication type (none, service_http, user_http, oauth).</summary>
    public string AuthType { get; init; } = "service_http";

    /// <summary>Service authentication token.</summary>
    public string? ServiceAuthToken { get; init; }

    /// <summary>OpenAI verification token.</summary>
    public string? VerificationToken { get; init; }

    /// <summary>Logo URL.</summary>
    public string? LogoUrl { get; init; }

    /// <summary>Contact email.</summary>
    public string ContactEmail { get; init; } = string.Empty;

    /// <summary>Legal info URL.</summary>
    public string? LegalInfoUrl { get; init; }

    /// <summary>OAuth client ID (if using OAuth).</summary>
    public string? OAuthClientId { get; init; }

    /// <summary>OAuth authorization URL.</summary>
    public string? OAuthAuthorizationUrl { get; init; }

    /// <summary>OAuth token URL.</summary>
    public string? OAuthTokenUrl { get; init; }
}
