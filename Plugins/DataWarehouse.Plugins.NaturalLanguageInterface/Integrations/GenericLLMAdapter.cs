// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Plugins.NaturalLanguageInterface.Engine;
using DataWarehouse.Plugins.NaturalLanguageInterface.Models;
using System.Text.Json;

namespace DataWarehouse.Plugins.NaturalLanguageInterface.Integrations;

/// <summary>
/// Generic adapter for LLM integrations (Copilot, Bard/Gemini, Llama, etc.).
/// Provides a unified interface for various AI assistant integrations.
/// </summary>
public sealed class GenericLLMAdapter : IntegrationProviderBase
{
    private readonly QueryEngine _queryEngine;
    private readonly ConversationEngine _conversationEngine;
    private readonly GenericLLMConfig _config;

    public override string PlatformId => _config.PlatformId;
    public override string PlatformName => _config.PlatformName;
    public override bool IsConfigured => !string.IsNullOrEmpty(_config.ApiEndpoint);

    public GenericLLMAdapter(
        QueryEngine queryEngine,
        ConversationEngine conversationEngine,
        GenericLLMConfig? config = null)
    {
        _queryEngine = queryEngine;
        _conversationEngine = conversationEngine;
        _config = config ?? new GenericLLMConfig();
    }

    public override async Task<IntegrationResponse> HandleWebhookAsync(WebhookEvent webhookEvent, CancellationToken ct = default)
    {
        // Generic handler that can be adapted for various LLM platforms
        var requestType = webhookEvent.Type;

        return requestType switch
        {
            "chat" or "message" => await HandleChatAsync(webhookEvent, ct),
            "completion" => await HandleCompletionAsync(webhookEvent, ct),
            "function_call" or "tool_use" => await HandleFunctionCallAsync(webhookEvent, ct),
            "stream" => await HandleStreamAsync(webhookEvent, ct),
            _ => await HandleGenericRequestAsync(webhookEvent, ct)
        };
    }

    public override bool ValidateWebhookSignature(string body, string signature, IDictionary<string, string> headers)
    {
        // Implement platform-specific validation based on config
        if (string.IsNullOrEmpty(_config.ApiSecret))
            return true;

        if (!headers.TryGetValue(_config.SignatureHeader, out var headerSig))
            return false;

        // Compute expected signature
        using var hmac = new System.Security.Cryptography.HMACSHA256(
            System.Text.Encoding.UTF8.GetBytes(_config.ApiSecret));
        var expectedSig = Convert.ToBase64String(
            hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(body)));

        return headerSig == expectedSig || headerSig == $"sha256={expectedSig}";
    }

    public override object GetConfiguration()
    {
        return new
        {
            platform = new
            {
                id = _config.PlatformId,
                name = _config.PlatformName,
                version = "1.0.0"
            },
            endpoints = new
            {
                chat = $"{_config.ApiEndpoint}/chat",
                completion = $"{_config.ApiEndpoint}/completion",
                function = $"{_config.ApiEndpoint}/function"
            },
            capabilities = new
            {
                chat = true,
                completion = true,
                function_calling = true,
                streaming = _config.SupportsStreaming,
                context_window = _config.MaxContextTokens
            },
            authentication = new
            {
                type = _config.AuthType,
                header = _config.AuthHeader
            },
            functions = GetFunctionDefinitions()
        };
    }

    /// <summary>
    /// Get function/tool definitions for the LLM.
    /// </summary>
    public List<object> GetFunctionDefinitions()
    {
        return new List<object>
        {
            CreateFunctionDefinition(
                "search_files",
                "Search for files in the data warehouse",
                new Dictionary<string, object>
                {
                    ["query"] = new { type = "string", description = "Search query" },
                    ["file_type"] = new { type = "string", description = "File type filter", optional = true },
                    ["limit"] = new { type = "integer", description = "Max results", optional = true }
                }),

            CreateFunctionDefinition(
                "get_storage_status",
                "Get current storage status and statistics",
                new Dictionary<string, object>()),

            CreateFunctionDefinition(
                "manage_backup",
                "Create, list, or verify backups",
                new Dictionary<string, object>
                {
                    ["action"] = new { type = "string", @enum = new[] { "list", "create", "verify" } },
                    ["name"] = new { type = "string", description = "Backup name", optional = true }
                }),

            CreateFunctionDefinition(
                "explain_decision",
                "Explain why a storage decision was made",
                new Dictionary<string, object>
                {
                    ["item_id"] = new { type = "string", description = "ID of the item" },
                    ["question"] = new { type = "string", description = "Specific question" }
                }),

            CreateFunctionDefinition(
                "generate_report",
                "Generate a report about storage or activity",
                new Dictionary<string, object>
                {
                    ["report_type"] = new { type = "string", @enum = new[] { "storage", "backup", "activity" } },
                    ["time_range"] = new { type = "string", description = "Time range", optional = true }
                }),

            CreateFunctionDefinition(
                "natural_query",
                "Process any natural language query",
                new Dictionary<string, object>
                {
                    ["query"] = new { type = "string", description = "Natural language query" }
                })
        };
    }

    #region Private Methods

    private async Task<IntegrationResponse> HandleChatAsync(WebhookEvent webhookEvent, CancellationToken ct)
    {
        var message = GetPayloadValue<string>(webhookEvent.Payload, "message")
            ?? GetPayloadValue<string>(webhookEvent.Payload, "content")
            ?? GetPayloadValue<string>(webhookEvent.Payload, "text")
            ?? "";

        var conversationId = GetPayloadValue<string>(webhookEvent.Payload, "conversation_id")
            ?? GetPayloadValue<string>(webhookEvent.Payload, "session_id");

        var userId = GetPayloadValue<string>(webhookEvent.Payload, "user_id")
            ?? $"{_config.PlatformId}:anonymous";

        // Get or create conversation
        Conversation conversation;
        if (!string.IsNullOrEmpty(conversationId))
        {
            conversation = _conversationEngine.GetConversation(conversationId)
                ?? _conversationEngine.StartConversation(userId, _config.PlatformId);
        }
        else
        {
            conversation = _conversationEngine.StartConversation(userId, _config.PlatformId);
        }

        var result = await _conversationEngine.ProcessMessageAsync(conversation.Id, message, null, ct);

        return SuccessResponse(FormatResponse(result, conversation.Id));
    }

    private async Task<IntegrationResponse> HandleCompletionAsync(WebhookEvent webhookEvent, CancellationToken ct)
    {
        var prompt = GetPayloadValue<string>(webhookEvent.Payload, "prompt") ?? "";

        // Parse the prompt and process
        var intent = await _queryEngine.ParseQueryAsync(prompt, null, ct);
        var translation = _queryEngine.TranslateToCommand(intent);

        return SuccessResponse(new
        {
            completion = translation.Explanation,
            command = translation.Command,
            parameters = translation.Parameters,
            confidence = translation.Confidence
        });
    }

    private async Task<IntegrationResponse> HandleFunctionCallAsync(WebhookEvent webhookEvent, CancellationToken ct)
    {
        var functionName = GetPayloadValue<string>(webhookEvent.Payload, "function")
            ?? GetPayloadValue<string>(webhookEvent.Payload, "name")
            ?? GetPayloadValue<string>(webhookEvent.Payload, "tool")
            ?? "";

        var arguments = GetPayloadValue<Dictionary<string, object>>(webhookEvent.Payload, "arguments")
            ?? GetPayloadValue<Dictionary<string, object>>(webhookEvent.Payload, "parameters")
            ?? new Dictionary<string, object>();

        // Build query from function call
        var query = functionName switch
        {
            "search_files" => BuildSearchQuery(arguments),
            "get_storage_status" => "What is my storage status?",
            "manage_backup" => BuildBackupQuery(arguments),
            "explain_decision" => BuildExplainQuery(arguments),
            "generate_report" => BuildReportQuery(arguments),
            "natural_query" => GetArg<string>(arguments, "query") ?? "help",
            _ => $"Execute {functionName}"
        };

        var conversation = _conversationEngine.StartConversation($"{_config.PlatformId}:function", _config.PlatformId);
        var result = await _conversationEngine.ProcessMessageAsync(conversation.Id, query, null, ct);

        return SuccessResponse(new
        {
            function = functionName,
            result = new
            {
                success = result.Success,
                response = result.Response,
                data = result.Data
            }
        });
    }

    private async Task<IntegrationResponse> HandleStreamAsync(WebhookEvent webhookEvent, CancellationToken ct)
    {
        // For streaming, return a response that indicates streaming should be used
        // Actual streaming would be handled at the transport layer

        var message = GetPayloadValue<string>(webhookEvent.Payload, "message") ?? "";

        var conversation = _conversationEngine.StartConversation($"{_config.PlatformId}:stream", _config.PlatformId);
        var result = await _conversationEngine.ProcessMessageAsync(conversation.Id, message, null, ct);

        // Return chunked response format
        return SuccessResponse(new
        {
            stream = true,
            chunks = SplitIntoChunks(result.Response, 100),
            metadata = new
            {
                conversation_id = conversation.Id,
                complete = true
            }
        });
    }

    private async Task<IntegrationResponse> HandleGenericRequestAsync(WebhookEvent webhookEvent, CancellationToken ct)
    {
        // Try to extract query from various possible payload formats
        var query = GetPayloadValue<string>(webhookEvent.Payload, "query")
            ?? GetPayloadValue<string>(webhookEvent.Payload, "message")
            ?? GetPayloadValue<string>(webhookEvent.Payload, "input")
            ?? GetPayloadValue<string>(webhookEvent.Payload, "prompt")
            ?? "help";

        var conversation = _conversationEngine.StartConversation($"{_config.PlatformId}:generic", _config.PlatformId);
        var result = await _conversationEngine.ProcessMessageAsync(conversation.Id, query, null, ct);

        return SuccessResponse(FormatResponse(result, conversation.Id));
    }

    private object FormatResponse(ConversationTurnResult result, string conversationId)
    {
        // Format response based on configured response format
        return _config.ResponseFormat switch
        {
            "openai" => new
            {
                id = $"chatcmpl-{Guid.NewGuid():N}",
                @object = "chat.completion",
                created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        message = new { role = "assistant", content = result.Response },
                        finish_reason = result.State == DialogueState.Completed ? "stop" : "length"
                    }
                },
                usage = new { prompt_tokens = 0, completion_tokens = 0, total_tokens = 0 }
            },

            "anthropic" => new
            {
                id = $"msg_{Guid.NewGuid():N}",
                type = "message",
                role = "assistant",
                content = new[]
                {
                    new { type = "text", text = result.Response }
                },
                stop_reason = result.State == DialogueState.Completed ? "end_turn" : null
            },

            _ => new
            {
                conversation_id = conversationId,
                response = result.Response,
                data = result.Data,
                state = result.State.ToString(),
                suggestions = result.SuggestedFollowUps
            }
        };
    }

    private string BuildSearchQuery(Dictionary<string, object> args)
    {
        var query = GetArg<string>(args, "query") ?? "";
        var fileType = GetArg<string>(args, "file_type");

        var parts = new List<string> { "Find files" };
        if (!string.IsNullOrEmpty(query)) parts.Add($"matching {query}");
        if (!string.IsNullOrEmpty(fileType)) parts.Add($"of type {fileType}");

        return string.Join(" ", parts);
    }

    private string BuildBackupQuery(Dictionary<string, object> args)
    {
        var action = GetArg<string>(args, "action") ?? "list";
        var name = GetArg<string>(args, "name");

        return action switch
        {
            "create" => string.IsNullOrEmpty(name) ? "Create a backup" : $"Create a backup called {name}",
            "verify" => "Verify my latest backup",
            _ => "List my backups"
        };
    }

    private string BuildExplainQuery(Dictionary<string, object> args)
    {
        var itemId = GetArg<string>(args, "item_id");
        var question = GetArg<string>(args, "question") ?? "what happened";

        return string.IsNullOrEmpty(itemId)
            ? $"Explain {question}"
            : $"Explain why {itemId} {question}";
    }

    private string BuildReportQuery(Dictionary<string, object> args)
    {
        var reportType = GetArg<string>(args, "report_type") ?? "storage";
        var timeRange = GetArg<string>(args, "time_range") ?? "this month";

        return $"Generate a {reportType} report for {timeRange}";
    }

    private object CreateFunctionDefinition(string name, string description, Dictionary<string, object> parameters)
    {
        // Format based on target platform
        return _config.FunctionFormat switch
        {
            "openai" => new
            {
                type = "function",
                function = new
                {
                    name = name,
                    description = description,
                    parameters = new
                    {
                        type = "object",
                        properties = parameters,
                        required = parameters
                            .Where(p => !((p.Value as dynamic)?.optional ?? false))
                            .Select(p => p.Key)
                            .ToArray()
                    }
                }
            },

            "anthropic" => new
            {
                name = name,
                description = description,
                input_schema = new
                {
                    type = "object",
                    properties = parameters,
                    required = parameters
                        .Where(p => !((p.Value as dynamic)?.optional ?? false))
                        .Select(p => p.Key)
                        .ToArray()
                }
            },

            _ => new
            {
                name = name,
                description = description,
                parameters = parameters
            }
        };
    }

    private List<string> SplitIntoChunks(string text, int chunkSize)
    {
        var chunks = new List<string>();
        for (int i = 0; i < text.Length; i += chunkSize)
        {
            chunks.Add(text.Substring(i, Math.Min(chunkSize, text.Length - i)));
        }
        return chunks;
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

    private T? GetArg<T>(Dictionary<string, object> args, string key)
    {
        if (!args.TryGetValue(key, out var value))
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

        try
        {
            return (T)Convert.ChangeType(value, typeof(T));
        }
        catch
        {
            return default;
        }
    }

    #endregion
}

/// <summary>
/// Configuration for generic LLM adapter.
/// </summary>
public sealed class GenericLLMConfig
{
    /// <summary>Platform identifier.</summary>
    public string PlatformId { get; init; } = "generic_llm";

    /// <summary>Platform display name.</summary>
    public string PlatformName { get; init; } = "Generic LLM";

    /// <summary>API endpoint.</summary>
    public string ApiEndpoint { get; init; } = string.Empty;

    /// <summary>API key or secret.</summary>
    public string? ApiSecret { get; init; }

    /// <summary>Authentication type (bearer, api_key, basic).</summary>
    public string AuthType { get; init; } = "bearer";

    /// <summary>Authentication header name.</summary>
    public string AuthHeader { get; init; } = "Authorization";

    /// <summary>Signature header for webhook validation.</summary>
    public string SignatureHeader { get; init; } = "X-Signature";

    /// <summary>Whether the platform supports streaming.</summary>
    public bool SupportsStreaming { get; init; } = true;

    /// <summary>Maximum context window tokens.</summary>
    public int MaxContextTokens { get; init; } = 128000;

    /// <summary>Response format (openai, anthropic, generic).</summary>
    public string ResponseFormat { get; init; } = "generic";

    /// <summary>Function definition format (openai, anthropic, generic).</summary>
    public string FunctionFormat { get; init; } = "generic";
}
