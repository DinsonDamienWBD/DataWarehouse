// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.AIInterface.Channels;

#region ChatGPT Plugin Channel

/// <summary>
/// OpenAI ChatGPT Plugin integration channel.
/// Implements the ChatGPT Plugin specification for AI-to-AI communication.
/// </summary>
/// <remarks>
/// <para>
/// This channel handles:
/// <list type="bullet">
/// <item>Plugin manifest (ai-plugin.json)</item>
/// <item>OpenAPI schema for tool definitions</item>
/// <item>REST API endpoints for ChatGPT to invoke</item>
/// <item>OAuth and service authentication</item>
/// </list>
/// </para>
/// <para>
/// Routes all capability requests to the UltimateIntelligence plugin. This channel provides
/// the interface layer for ChatGPT to interact with DataWarehouse capabilities.
/// </para>
/// </remarks>
public sealed class ChatGPTPluginChannel : IntegrationChannelBase
{
    private readonly ChatGPTChannelConfig _config;

    /// <inheritdoc />
    public override string ChannelId => "chatgpt";

    /// <inheritdoc />
    public override string ChannelName => "ChatGPT Plugin";

    /// <inheritdoc />
    public override ChannelCategory Category => ChannelCategory.LLM;

    /// <inheritdoc />
    public override bool IsConfigured => !string.IsNullOrEmpty(_config.BaseUrl);

    /// <summary>
    /// Initializes a new instance of the <see cref="ChatGPTPluginChannel"/> class.
    /// </summary>
    /// <param name="config">ChatGPT configuration.</param>
    public ChatGPTPluginChannel(ChatGPTChannelConfig? config = null)
    {
        _config = config ?? new ChatGPTChannelConfig();
    }

    /// <inheritdoc />
    public override async Task<ChannelResponse> HandleRequestAsync(ChannelRequest request, CancellationToken ct = default)
    {
        return request.Type switch
        {
            "search" => await HandleSearchAsync(request, ct),
            "status" => await HandleStatusAsync(request, ct),
            "backup" => await HandleBackupAsync(request, ct),
            "explain" => await HandleExplainAsync(request, ct),
            "query" => await HandleQueryAsync(request, ct),
            "manifest" => ChannelResponse.Success(GetPluginManifest()),
            "openapi" => ChannelResponse.Success(GetOpenAPISchema()),
            _ => ChannelResponse.Error(404, "Endpoint not found")
        };
    }

    /// <inheritdoc />
    public override bool ValidateSignature(string body, string signature, IDictionary<string, string> headers)
    {
        if (_config.AuthType == "none") return true;

        if (_config.AuthType == "service_http")
        {
            if (!headers.TryGetValue("Authorization", out var authHeader))
                return false;

            if (!authHeader.StartsWith("Bearer "))
                return false;

            var token = authHeader.Substring(7);
            return token == _config.ServiceAuthToken;
        }

        // For OAuth, validate against OpenAI
        return true;
    }

    /// <inheritdoc />
    public override string GetWebhookEndpoint() => "/.well-known/ai-plugin.json";

    /// <inheritdoc />
    public override object GetConfiguration()
    {
        return new
        {
            manifest = GetPluginManifest(),
            openapi = GetOpenAPISchema()
        };
    }

    /// <summary>
    /// Gets the ai-plugin.json manifest.
    /// </summary>
    public object GetPluginManifest()
    {
        return new
        {
            schema_version = "v1",
            name_for_human = "DataWarehouse",
            name_for_model = "datawarehouse",
            description_for_human = "Manage and query your data warehouse. Search files, check storage status, manage backups, and get explanations.",
            description_for_model = @"Plugin for managing a data warehouse system. Use this plugin when users want to:
- Search for files or data in their storage
- Check storage status, usage, or quotas
- Create, list, or manage backups
- Get explanations about storage decisions (why files were archived, tiered, etc.)
- Understand trends in their data usage

Always prefer specific endpoints (search, status, backup) over the generic query endpoint when the intent is clear.",
            auth = new
            {
                type = _config.AuthType,
                authorization_type = _config.AuthType == "service_http" ? "bearer" : null,
                verification_tokens = _config.AuthType == "service_http"
                    ? new Dictionary<string, string> { ["openai"] = _config.VerificationToken ?? "" }
                    : null
            },
            api = new
            {
                type = "openapi",
                url = $"{_config.BaseUrl}/.well-known/openapi.yaml",
                is_user_authenticated = _config.AuthType == "oauth"
            },
            logo_url = _config.LogoUrl ?? $"{_config.BaseUrl}/logo.png",
            contact_email = _config.ContactEmail,
            legal_info_url = _config.LegalInfoUrl ?? $"{_config.BaseUrl}/legal"
        };
    }

    /// <summary>
    /// Gets the OpenAPI schema for the plugin.
    /// </summary>
    public object GetOpenAPISchema()
    {
        return new
        {
            openapi = "3.0.1",
            info = new
            {
                title = "DataWarehouse AI Plugin",
                description = "AI-powered API for managing data warehouse operations",
                version = "1.0.0"
            },
            servers = new[] { new { url = _config.BaseUrl } },
            paths = new Dictionary<string, object>
            {
                ["/api/chatgpt/search"] = CreateEndpoint("searchFiles", "Search for files in the data warehouse", "post",
                    new { query = Prop("string", "Natural language search query", true), fileType = Prop("string", "File type filter") }),
                ["/api/chatgpt/status"] = CreateEndpoint("getStatus", "Get storage status and statistics", "get"),
                ["/api/chatgpt/backup"] = CreateEndpoint("manageBackup", "Manage backups", "post",
                    new { action = PropEnum("string", "Backup action", "list", "create", "verify"), name = Prop("string", "Backup name") }),
                ["/api/chatgpt/explain"] = CreateEndpoint("explainDecision", "Explain a storage decision", "post",
                    new { fileId = Prop("string", "File or item ID"), question = Prop("string", "Specific question", true) }),
                ["/api/chatgpt/query"] = CreateEndpoint("naturalLanguageQuery", "Process any natural language query", "post",
                    new { query = Prop("string", "Natural language query", true) })
            }
        };
    }

    #region Request Handlers

    private async Task<ChannelResponse> HandleSearchAsync(ChannelRequest request, CancellationToken ct)
    {
        var query = GetPayloadValue<string>(request.Payload, "query") ?? "";
        var fileType = GetPayloadValue<string>(request.Payload, "fileType");

        var searchQuery = $"Find files {query}";
        if (!string.IsNullOrEmpty(fileType)) searchQuery += $" of type {fileType}";

        var userId = ExtractUserId(request.Headers);
        var result = await RouteToIntelligenceAsync("intelligence.request.conversation", new Dictionary<string, object>
        {
            ["message"] = searchQuery,
            ["platform"] = "chatgpt"
        }, userId, null, ct);

        return ChannelResponse.Success(new
        {
            success = result.Success,
            response = result.Response,
            results = result.Data,
            suggestions = result.SuggestedFollowUps
        });
    }

    private async Task<ChannelResponse> HandleStatusAsync(ChannelRequest request, CancellationToken ct)
    {
        var userId = ExtractUserId(request.Headers);
        var result = await RouteToIntelligenceAsync("intelligence.request.conversation", new Dictionary<string, object>
        {
            ["message"] = "What is my storage status?",
            ["platform"] = "chatgpt"
        }, userId, null, ct);

        return ChannelResponse.Success(new { success = result.Success, status = result.Response, data = result.Data });
    }

    private async Task<ChannelResponse> HandleBackupAsync(ChannelRequest request, CancellationToken ct)
    {
        var action = GetPayloadValue<string>(request.Payload, "action") ?? "list";
        var name = GetPayloadValue<string>(request.Payload, "name");

        var query = action switch
        {
            "create" => string.IsNullOrEmpty(name) ? "Create a backup" : $"Create a backup called {name}",
            "verify" => "Verify my latest backup",
            _ => "List my backups"
        };

        var userId = ExtractUserId(request.Headers);
        var result = await RouteToIntelligenceAsync("intelligence.request.conversation", new Dictionary<string, object>
        {
            ["message"] = query,
            ["platform"] = "chatgpt"
        }, userId, null, ct);

        return ChannelResponse.Success(new { success = result.Success, response = result.Response, backups = result.Data });
    }

    private async Task<ChannelResponse> HandleExplainAsync(ChannelRequest request, CancellationToken ct)
    {
        var question = GetPayloadValue<string>(request.Payload, "question") ?? "";
        var fileId = GetPayloadValue<string>(request.Payload, "fileId");

        var query = string.IsNullOrEmpty(fileId) ? $"Explain {question}" : $"Explain why {fileId} {question}";

        var userId = ExtractUserId(request.Headers);
        var result = await RouteToIntelligenceAsync("intelligence.request.conversation", new Dictionary<string, object>
        {
            ["message"] = query,
            ["platform"] = "chatgpt"
        }, userId, null, ct);

        return ChannelResponse.Success(new { success = result.Success, explanation = result.Response, reasoning = result.Data });
    }

    private async Task<ChannelResponse> HandleQueryAsync(ChannelRequest request, CancellationToken ct)
    {
        var query = GetPayloadValue<string>(request.Payload, "query") ?? "help";

        var userId = ExtractUserId(request.Headers);
        var result = await RouteToIntelligenceAsync("intelligence.request.conversation", new Dictionary<string, object>
        {
            ["message"] = query,
            ["platform"] = "chatgpt"
        }, userId, null, ct);

        return ChannelResponse.Success(new
        {
            success = result.Success,
            response = result.Response,
            data = result.Data,
            suggestions = result.SuggestedFollowUps
        });
    }

    #endregion

    #region Helpers

    private string ExtractUserId(Dictionary<string, string> headers)
    {
        return headers.TryGetValue("X-OpenAI-User", out var user) ? $"chatgpt:{user}" : "chatgpt:anonymous";
    }

    private T? GetPayloadValue<T>(Dictionary<string, object> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value)) return default;
        if (value is T typed) return typed;
        if (value is JsonElement element) try { return element.Deserialize<T>(); } catch { return default; }
        return default;
    }

    private object CreateEndpoint(string operationId, string description, string method, object? properties = null)
    {
        var endpoint = new Dictionary<string, object>
        {
            [method] = new
            {
                operationId,
                summary = description,
                responses = new { _200 = new { description = "Successful response" } }
            }
        };

        if (properties != null)
        {
            ((dynamic)endpoint[method]).requestBody = new
            {
                required = true,
                content = new { application_json = new { schema = new { type = "object", properties } } }
            };
        }

        return endpoint;
    }

    private object Prop(string type, string description, bool required = false) =>
        new { type, description, required };

    private object PropEnum(string type, string description, params string[] values) =>
        new { type, description, @enum = values };

    #endregion
}

/// <summary>
/// Configuration for ChatGPT Plugin channel.
/// </summary>
public sealed class ChatGPTChannelConfig
{
    /// <summary>Base URL for the plugin API.</summary>
    public string BaseUrl { get; init; } = string.Empty;

    /// <summary>Authentication type (none, service_http, oauth).</summary>
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
}

#endregion

#region Claude MCP Channel

/// <summary>
/// Anthropic Claude Model Context Protocol (MCP) integration channel.
/// Implements the MCP specification for Claude-to-system communication.
/// </summary>
/// <remarks>
/// <para>
/// This channel handles:
/// <list type="bullet">
/// <item>MCP JSON-RPC 2.0 protocol</item>
/// <item>Tool definitions and invocations</item>
/// <item>Resource management</item>
/// <item>Prompt templates</item>
/// </list>
/// </para>
/// </remarks>
public sealed class ClaudeMCPChannel : IntegrationChannelBase
{
    private readonly ClaudeMCPChannelConfig _config;

    /// <inheritdoc />
    public override string ChannelId => "claude_mcp";

    /// <inheritdoc />
    public override string ChannelName => "Claude MCP";

    /// <inheritdoc />
    public override ChannelCategory Category => ChannelCategory.LLM;

    /// <inheritdoc />
    public override bool IsConfigured => !string.IsNullOrEmpty(_config.ServerName);

    /// <summary>
    /// Initializes a new instance of the <see cref="ClaudeMCPChannel"/> class.
    /// </summary>
    /// <param name="config">MCP configuration.</param>
    public ClaudeMCPChannel(ClaudeMCPChannelConfig? config = null)
    {
        _config = config ?? new ClaudeMCPChannelConfig();
    }

    /// <inheritdoc />
    public override async Task<ChannelResponse> HandleRequestAsync(ChannelRequest request, CancellationToken ct = default)
    {
        var mcpRequest = ParseMCPRequest(request.Payload);

        var result = mcpRequest.Method switch
        {
            "initialize" => HandleInitialize(mcpRequest),
            "shutdown" => CreateSuccessResponse(mcpRequest.Id, new { }),
            "tools/list" => CreateSuccessResponse(mcpRequest.Id, new { tools = GetTools() }),
            "tools/call" => await HandleToolsCallAsync(mcpRequest, ct),
            "resources/list" => CreateSuccessResponse(mcpRequest.Id, new { resources = GetResources() }),
            "resources/read" => await HandleResourcesReadAsync(mcpRequest, ct),
            "resources/subscribe" => CreateSuccessResponse(mcpRequest.Id, new { }),
            "resources/unsubscribe" => CreateSuccessResponse(mcpRequest.Id, new { }),
            "prompts/list" => CreateSuccessResponse(mcpRequest.Id, new { prompts = GetPrompts() }),
            "prompts/get" => HandlePromptsGet(mcpRequest),
            "logging/setLevel" => CreateSuccessResponse(mcpRequest.Id, new { }),
            _ => CreateErrorResponse(mcpRequest.Id, -32601, $"Method not found: {mcpRequest.Method}")
        };

        return ChannelResponse.Success(result);
    }

    /// <inheritdoc />
    public override bool ValidateSignature(string body, string signature, IDictionary<string, string> headers)
    {
        // MCP typically runs over stdio or local transport
        return true;
    }

    /// <inheritdoc />
    public override object GetConfiguration() => GetServerInfo();

    /// <summary>
    /// Gets MCP server information.
    /// </summary>
    public object GetServerInfo()
    {
        return new
        {
            name = _config.ServerName,
            version = _config.ServerVersion,
            protocolVersion = "2024-11-05",
            capabilities = new
            {
                tools = new { listChanged = true },
                resources = new { subscribe = true, listChanged = true },
                prompts = new { listChanged = true },
                logging = new { }
            }
        };
    }

    /// <summary>
    /// Gets available MCP tools.
    /// </summary>
    public object[] GetTools()
    {
        return new object[]
        {
            CreateTool("search_files", "Search for files using natural language", new
            {
                query = ToolProp("string", "Natural language search query"),
                file_type = ToolProp("string", "Filter by file type"),
                max_results = ToolProp("number", "Maximum results to return")
            }, new[] { "query" }),
            CreateTool("get_storage_status", "Get current storage status", new
            {
                include_details = ToolProp("boolean", "Include detailed breakdown")
            }),
            CreateTool("list_backups", "List available backups", new
            {
                limit = ToolProp("number", "Maximum backups to list"),
                status = ToolPropEnum("string", "Filter by status", "all", "completed", "failed")
            }),
            CreateTool("create_backup", "Create a new backup", new
            {
                name = ToolProp("string", "Name for the backup"),
                incremental = ToolProp("boolean", "Create incremental backup")
            }),
            CreateTool("explain_decision", "Explain a storage decision", new
            {
                file_id = ToolProp("string", "File or item ID"),
                decision_type = ToolPropEnum("string", "Type of decision", "tiering", "archiving", "deletion", "replication")
            }, new[] { "file_id" }),
            CreateTool("generate_report", "Generate a narrative report", new
            {
                report_type = ToolPropEnum("string", "Type of report", "storage", "backup", "activity", "trends"),
                time_range = ToolProp("string", "Time range for the report")
            }, new[] { "report_type" }),
            CreateTool("natural_query", "Process any natural language query", new
            {
                query = ToolProp("string", "Natural language query")
            }, new[] { "query" })
        };
    }

    /// <summary>
    /// Gets available MCP resources.
    /// </summary>
    public object[] GetResources()
    {
        return new object[]
        {
            new { uri = "datawarehouse://status", name = "Storage Status", description = "Current storage status and health", mimeType = "application/json" },
            new { uri = "datawarehouse://backups", name = "Backup List", description = "List of available backups", mimeType = "application/json" },
            new { uri = "datawarehouse://usage", name = "Usage Statistics", description = "Storage usage statistics and trends", mimeType = "application/json" },
            new { uri = "datawarehouse://alerts", name = "Active Alerts", description = "Current system alerts", mimeType = "application/json" }
        };
    }

    /// <summary>
    /// Gets available MCP prompts.
    /// </summary>
    public object[] GetPrompts()
    {
        return new object[]
        {
            new { name = "storage_analysis", description = "Analyze storage usage and provide recommendations", arguments = new[] { new { name = "focus_area", description = "Area to focus on", required = false } } },
            new { name = "backup_strategy", description = "Review and recommend backup strategies" },
            new { name = "troubleshoot", description = "Help troubleshoot an issue", arguments = new[] { new { name = "issue", description = "Description of the issue", required = true } } }
        };
    }

    #region Request Handlers

    private object HandleInitialize(MCPRequest request)
    {
        return CreateSuccessResponse(request.Id, new
        {
            protocolVersion = "2024-11-05",
            capabilities = ((dynamic)GetServerInfo()).capabilities,
            serverInfo = new { name = _config.ServerName, version = _config.ServerVersion }
        });
    }

    private async Task<object> HandleToolsCallAsync(MCPRequest request, CancellationToken ct)
    {
        var toolName = GetParam<string>(request.Params, "name") ?? "";
        var arguments = GetParam<Dictionary<string, object>>(request.Params, "arguments") ?? new();

        var query = ToolToQuery(toolName, arguments);
        var result = await RouteToIntelligenceAsync("intelligence.request.conversation", new Dictionary<string, object>
        {
            ["message"] = query,
            ["platform"] = "claude_mcp"
        }, "mcp:tool", null, ct);

        return CreateSuccessResponse(request.Id, new
        {
            content = new[] { new { type = "text", text = result.Response ?? "Request processed." } },
            isError = !result.Success
        });
    }

    private async Task<object> HandleResourcesReadAsync(MCPRequest request, CancellationToken ct)
    {
        var uri = GetParam<string>(request.Params, "uri") ?? "";
        var query = uri switch
        {
            "datawarehouse://status" => "What is my storage status?",
            "datawarehouse://backups" => "List my backups",
            "datawarehouse://usage" => "Show my storage usage",
            "datawarehouse://alerts" => "Show active alerts",
            _ => $"Get information about {uri}"
        };

        var result = await RouteToIntelligenceAsync("intelligence.request.conversation", new Dictionary<string, object>
        {
            ["message"] = query,
            ["platform"] = "claude_mcp"
        }, "mcp:resource", null, ct);

        return CreateSuccessResponse(request.Id, new
        {
            contents = new[]
            {
                new
                {
                    uri,
                    mimeType = "application/json",
                    text = JsonSerializer.Serialize(new { response = result.Response, data = result.Data })
                }
            }
        });
    }

    private object HandlePromptsGet(MCPRequest request)
    {
        var promptName = GetParam<string>(request.Params, "name") ?? "";
        var promptArgs = GetParam<Dictionary<string, string>>(request.Params, "arguments") ?? new();

        var messages = promptName switch
        {
            "storage_analysis" => new[] { new { role = "user", content = new { type = "text", text = $"Please analyze my data warehouse storage{(promptArgs.TryGetValue("focus_area", out var focus) ? $" with focus on {focus}" : "")}. Use the available tools to gather information and provide insights." } } },
            "backup_strategy" => new[] { new { role = "user", content = new { type = "text", text = "Review my current backup setup and suggest improvements." } } },
            "troubleshoot" => new[] { new { role = "user", content = new { type = "text", text = $"Help me troubleshoot this issue: {promptArgs.GetValueOrDefault("issue", "unknown issue")}" } } },
            _ => Array.Empty<object>()
        };

        return CreateSuccessResponse(request.Id, new { description = $"Prompt: {promptName}", messages });
    }

    #endregion

    #region Helpers

    private MCPRequest ParseMCPRequest(Dictionary<string, object> payload)
    {
        var json = JsonSerializer.Serialize(payload);
        return JsonSerializer.Deserialize<MCPRequest>(json) ?? new MCPRequest();
    }

    private string ToolToQuery(string toolName, Dictionary<string, object> args)
    {
        return toolName switch
        {
            "search_files" => $"Find files {GetArg<string>(args, "query")} {(GetArg<string>(args, "file_type") is string ft ? $"of type {ft}" : "")}".Trim(),
            "get_storage_status" => GetArg<bool?>(args, "include_details") == true ? "What is my storage status with detailed breakdown?" : "What is my storage status?",
            "list_backups" => "List my backups",
            "create_backup" => GetArg<string>(args, "name") is string n ? $"Create a backup called {n}" : "Create a backup",
            "explain_decision" => $"Explain why {GetArg<string>(args, "file_id")} was {GetArg<string>(args, "decision_type")}",
            "generate_report" => $"Generate a {GetArg<string>(args, "report_type")} report for {GetArg<string>(args, "time_range") ?? "this month"}",
            "natural_query" => GetArg<string>(args, "query") ?? "help",
            _ => "help"
        };
    }

    private object CreateSuccessResponse(object id, object result) => new { jsonrpc = "2.0", id, result };
    private object CreateErrorResponse(object id, int code, string message) => new { jsonrpc = "2.0", id, error = new { code, message } };

    private T? GetParam<T>(Dictionary<string, object>? parameters, string key)
    {
        if (parameters == null || !parameters.TryGetValue(key, out var value)) return default;
        if (value is T typed) return typed;
        if (value is JsonElement element) try { return element.Deserialize<T>(); } catch { return default; }
        return default;
    }

    private T? GetArg<T>(Dictionary<string, object> args, string key)
    {
        if (!args.TryGetValue(key, out var value)) return default;
        if (value is T typed) return typed;
        if (value is JsonElement el) try { return el.Deserialize<T>(); } catch { return default; }
        try { return (T)Convert.ChangeType(value, typeof(T)); } catch { return default; }
    }

    private object CreateTool(string name, string description, object properties, string[]? required = null) => new
    {
        name,
        description,
        inputSchema = new { type = "object", properties, required = required ?? Array.Empty<string>() }
    };

    private object ToolProp(string type, string description) => new { type, description };
    private object ToolPropEnum(string type, string description, params string[] values) => new { type, description, @enum = values };

    #endregion
}

internal sealed class MCPRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";

    [JsonPropertyName("id")]
    public object Id { get; init; } = 0;

    [JsonPropertyName("method")]
    public string Method { get; init; } = string.Empty;

    [JsonPropertyName("params")]
    public Dictionary<string, object>? Params { get; init; }
}

/// <summary>
/// Configuration for Claude MCP channel.
/// </summary>
public sealed class ClaudeMCPChannelConfig
{
    /// <summary>Server name.</summary>
    public string ServerName { get; init; } = "DataWarehouse MCP Server";

    /// <summary>Server version.</summary>
    public string ServerVersion { get; init; } = "1.0.0";

    /// <summary>Transport type (stdio, http).</summary>
    public string Transport { get; init; } = "stdio";

    /// <summary>HTTP port if using HTTP transport.</summary>
    public int? HttpPort { get; init; }
}

#endregion

#region Generic Webhook Channel

/// <summary>
/// Generic webhook integration channel for any LLM platform.
/// Provides a flexible interface that can be adapted for various AI assistants.
/// </summary>
public sealed class GenericWebhookChannel : IntegrationChannelBase
{
    private readonly GenericWebhookChannelConfig _config;

    /// <inheritdoc />
    public override string ChannelId => _config.ChannelId;

    /// <inheritdoc />
    public override string ChannelName => _config.ChannelName;

    /// <inheritdoc />
    public override ChannelCategory Category => ChannelCategory.Webhook;

    /// <inheritdoc />
    public override bool IsConfigured => !string.IsNullOrEmpty(_config.ApiEndpoint);

    /// <summary>
    /// Initializes a new instance of the <see cref="GenericWebhookChannel"/> class.
    /// </summary>
    /// <param name="config">Webhook configuration.</param>
    public GenericWebhookChannel(GenericWebhookChannelConfig? config = null)
    {
        _config = config ?? new GenericWebhookChannelConfig();
    }

    /// <inheritdoc />
    public override async Task<ChannelResponse> HandleRequestAsync(ChannelRequest request, CancellationToken ct = default)
    {
        return request.Type switch
        {
            "chat" or "message" => await HandleChatAsync(request, ct),
            "completion" => await HandleCompletionAsync(request, ct),
            "function_call" or "tool_use" => await HandleFunctionCallAsync(request, ct),
            _ => await HandleGenericAsync(request, ct)
        };
    }

    /// <inheritdoc />
    public override bool ValidateSignature(string body, string signature, IDictionary<string, string> headers)
    {
        if (string.IsNullOrEmpty(_config.ApiSecret)) return true;
        if (!headers.TryGetValue(_config.SignatureHeader, out var headerSig)) return false;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_config.ApiSecret));
        var expected = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(body)));
        return headerSig == expected || headerSig == $"sha256={expected}";
    }

    /// <inheritdoc />
    public override object GetConfiguration()
    {
        return new
        {
            platform = new { id = _config.ChannelId, name = _config.ChannelName, version = "1.0.0" },
            endpoints = new
            {
                chat = $"{_config.ApiEndpoint}/chat",
                completion = $"{_config.ApiEndpoint}/completion",
                function = $"{_config.ApiEndpoint}/function"
            },
            capabilities = new { chat = true, completion = true, function_calling = true, streaming = _config.SupportsStreaming },
            functions = GetFunctionDefinitions()
        };
    }

    /// <summary>
    /// Gets function definitions for the LLM.
    /// </summary>
    public object[] GetFunctionDefinitions()
    {
        return new object[]
        {
            new { name = "search_files", description = "Search for files", parameters = new { query = new { type = "string" } } },
            new { name = "get_storage_status", description = "Get storage status" },
            new { name = "manage_backup", description = "Manage backups", parameters = new { action = new { type = "string" } } },
            new { name = "natural_query", description = "Process any query", parameters = new { query = new { type = "string" } } }
        };
    }

    private async Task<ChannelResponse> HandleChatAsync(ChannelRequest request, CancellationToken ct)
    {
        var message = ExtractMessage(request.Payload);
        var userId = ExtractValue<string>(request.Payload, "user_id") ?? $"{_config.ChannelId}:anonymous";
        var conversationId = ExtractValue<string>(request.Payload, "conversation_id");

        var result = await RouteToIntelligenceAsync("intelligence.request.conversation", new Dictionary<string, object>
        {
            ["message"] = message,
            ["platform"] = _config.ChannelId
        }, userId, conversationId, ct);

        return ChannelResponse.Success(FormatResponse(result, conversationId));
    }

    private async Task<ChannelResponse> HandleCompletionAsync(ChannelRequest request, CancellationToken ct)
    {
        var prompt = ExtractValue<string>(request.Payload, "prompt") ?? "";
        var result = await RouteToIntelligenceAsync("intelligence.request.completion", new Dictionary<string, object>
        {
            ["prompt"] = prompt,
            ["platform"] = _config.ChannelId
        }, null, null, ct);

        return ChannelResponse.Success(new { completion = result.Response });
    }

    private async Task<ChannelResponse> HandleFunctionCallAsync(ChannelRequest request, CancellationToken ct)
    {
        var functionName = ExtractValue<string>(request.Payload, "function")
            ?? ExtractValue<string>(request.Payload, "name")
            ?? ExtractValue<string>(request.Payload, "tool") ?? "";
        var arguments = ExtractValue<Dictionary<string, object>>(request.Payload, "arguments") ?? new();

        var query = functionName switch
        {
            "search_files" => $"Find files {arguments.GetValueOrDefault("query")}",
            "get_storage_status" => "What is my storage status?",
            "manage_backup" => $"{arguments.GetValueOrDefault("action", "list")} backup",
            "natural_query" => arguments.GetValueOrDefault("query")?.ToString() ?? "help",
            _ => $"Execute {functionName}"
        };

        var result = await RouteToIntelligenceAsync("intelligence.request.conversation", new Dictionary<string, object>
        {
            ["message"] = query,
            ["platform"] = _config.ChannelId
        }, null, null, ct);

        return ChannelResponse.Success(new { function = functionName, result = new { success = result.Success, response = result.Response, data = result.Data } });
    }

    private async Task<ChannelResponse> HandleGenericAsync(ChannelRequest request, CancellationToken ct)
    {
        var query = ExtractMessage(request.Payload);
        var result = await RouteToIntelligenceAsync("intelligence.request.conversation", new Dictionary<string, object>
        {
            ["message"] = query,
            ["platform"] = _config.ChannelId
        }, null, null, ct);

        return ChannelResponse.Success(FormatResponse(result, null));
    }

    private string ExtractMessage(Dictionary<string, object> payload)
    {
        return ExtractValue<string>(payload, "message")
            ?? ExtractValue<string>(payload, "content")
            ?? ExtractValue<string>(payload, "text")
            ?? ExtractValue<string>(payload, "query")
            ?? ExtractValue<string>(payload, "prompt")
            ?? "help";
    }

    private T? ExtractValue<T>(Dictionary<string, object> payload, string key)
    {
        if (!payload.TryGetValue(key, out var value)) return default;
        if (value is T typed) return typed;
        if (value is JsonElement el) try { return el.Deserialize<T>(); } catch { return default; }
        return default;
    }

    private object FormatResponse(AICapabilityResponse result, string? conversationId)
    {
        return _config.ResponseFormat switch
        {
            "openai" => new
            {
                id = $"chatcmpl-{Guid.NewGuid():N}",
                @object = "chat.completion",
                created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                choices = new[] { new { index = 0, message = new { role = "assistant", content = result.Response }, finish_reason = "stop" } }
            },
            "anthropic" => new
            {
                id = $"msg_{Guid.NewGuid():N}",
                type = "message",
                role = "assistant",
                content = new[] { new { type = "text", text = result.Response } },
                stop_reason = "end_turn"
            },
            _ => new { conversation_id = conversationId, response = result.Response, data = result.Data, suggestions = result.SuggestedFollowUps }
        };
    }
}

/// <summary>
/// Configuration for generic webhook channel.
/// </summary>
public sealed class GenericWebhookChannelConfig
{
    /// <summary>Channel identifier.</summary>
    public string ChannelId { get; init; } = "generic_webhook";

    /// <summary>Channel display name.</summary>
    public string ChannelName { get; init; } = "Generic Webhook";

    /// <summary>API endpoint.</summary>
    public string ApiEndpoint { get; init; } = string.Empty;

    /// <summary>API secret for signature validation.</summary>
    public string? ApiSecret { get; init; }

    /// <summary>Signature header name.</summary>
    public string SignatureHeader { get; init; } = "X-Signature";

    /// <summary>Whether streaming is supported.</summary>
    public bool SupportsStreaming { get; init; } = true;

    /// <summary>Response format (openai, anthropic, generic).</summary>
    public string ResponseFormat { get; init; } = "generic";
}

#endregion
