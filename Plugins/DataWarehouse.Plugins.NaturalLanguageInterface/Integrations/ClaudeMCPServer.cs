// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Plugins.NaturalLanguageInterface.Engine;
using DataWarehouse.Plugins.NaturalLanguageInterface.Models;
using System.Text.Json;

namespace DataWarehouse.Plugins.NaturalLanguageInterface.Integrations;

/// <summary>
/// Anthropic Claude Model Context Protocol (MCP) server implementation.
/// Provides tools and resources for Claude to interact with DataWarehouse.
/// </summary>
public sealed class ClaudeMCPServer : IntegrationProviderBase
{
    private readonly QueryEngine _queryEngine;
    private readonly ConversationEngine _conversationEngine;
    private readonly ExplanationEngine _explanationEngine;
    private readonly StorytellingEngine _storytellingEngine;
    private readonly ClaudeMCPConfig _config;

    private int _requestId = 0;

    public override string PlatformId => "claude_mcp";
    public override string PlatformName => "Claude MCP";
    public override bool IsConfigured => !string.IsNullOrEmpty(_config.ServerName);

    public ClaudeMCPServer(
        QueryEngine queryEngine,
        ConversationEngine conversationEngine,
        ExplanationEngine explanationEngine,
        StorytellingEngine storytellingEngine,
        ClaudeMCPConfig? config = null)
    {
        _queryEngine = queryEngine;
        _conversationEngine = conversationEngine;
        _explanationEngine = explanationEngine;
        _storytellingEngine = storytellingEngine;
        _config = config ?? new ClaudeMCPConfig();
    }

    public override async Task<IntegrationResponse> HandleWebhookAsync(WebhookEvent webhookEvent, CancellationToken ct = default)
    {
        // MCP uses JSON-RPC 2.0 protocol
        var requestJson = JsonSerializer.Serialize(webhookEvent.Payload);
        var request = JsonSerializer.Deserialize<MCPRequest>(requestJson);

        if (request == null)
        {
            return SuccessResponse(CreateErrorResponse(null, -32600, "Invalid request"));
        }

        var result = request.Method switch
        {
            // Lifecycle methods
            "initialize" => HandleInitialize(request),
            "shutdown" => HandleShutdown(request),

            // Tool methods
            "tools/list" => HandleToolsList(request),
            "tools/call" => await HandleToolsCallAsync(request, ct),

            // Resource methods
            "resources/list" => HandleResourcesList(request),
            "resources/read" => await HandleResourcesReadAsync(request, ct),
            "resources/subscribe" => HandleResourcesSubscribe(request),
            "resources/unsubscribe" => HandleResourcesUnsubscribe(request),

            // Prompt methods
            "prompts/list" => HandlePromptsList(request),
            "prompts/get" => HandlePromptsGet(request),

            // Logging
            "logging/setLevel" => HandleSetLogLevel(request),

            _ => CreateErrorResponse(request.Id, -32601, $"Method not found: {request.Method}")
        };

        return SuccessResponse(result);
    }

    public override bool ValidateWebhookSignature(string body, string signature, IDictionary<string, string> headers)
    {
        // MCP typically runs over stdio or local transport
        // For HTTP transport, implement appropriate authentication
        return true;
    }

    public override object GetConfiguration()
    {
        return GetServerInfo();
    }

    /// <summary>
    /// Get MCP server information.
    /// </summary>
    public MCPServerInfo GetServerInfo()
    {
        return new MCPServerInfo
        {
            Name = _config.ServerName,
            Version = _config.ServerVersion,
            ProtocolVersion = "2024-11-05",
            Capabilities = new MCPCapabilities
            {
                Tools = new MCPToolCapabilities { ListChanged = true },
                Resources = new MCPResourceCapabilities { Subscribe = true, ListChanged = true },
                Prompts = new MCPPromptCapabilities { ListChanged = true },
                Logging = new { }
            }
        };
    }

    /// <summary>
    /// Get available tools.
    /// </summary>
    public List<MCPTool> GetTools()
    {
        return new List<MCPTool>
        {
            new()
            {
                Name = "search_files",
                Description = "Search for files in the data warehouse using natural language queries",
                InputSchema = new MCPInputSchema
                {
                    Type = "object",
                    Properties = new Dictionary<string, MCPPropertySchema>
                    {
                        ["query"] = new() { Type = "string", Description = "Natural language search query" },
                        ["file_type"] = new() { Type = "string", Description = "Filter by file type (optional)" },
                        ["max_results"] = new() { Type = "number", Description = "Maximum results to return (default: 20)" }
                    },
                    Required = new List<string> { "query" }
                }
            },
            new()
            {
                Name = "get_storage_status",
                Description = "Get current storage status including usage, quotas, and health",
                InputSchema = new MCPInputSchema
                {
                    Type = "object",
                    Properties = new Dictionary<string, MCPPropertySchema>
                    {
                        ["include_details"] = new() { Type = "boolean", Description = "Include detailed breakdown" }
                    }
                }
            },
            new()
            {
                Name = "list_backups",
                Description = "List available backups with their status and metadata",
                InputSchema = new MCPInputSchema
                {
                    Type = "object",
                    Properties = new Dictionary<string, MCPPropertySchema>
                    {
                        ["limit"] = new() { Type = "number", Description = "Maximum backups to list" },
                        ["status"] = new() { Type = "string", Description = "Filter by status", Enum = new List<string> { "all", "completed", "failed" } }
                    }
                }
            },
            new()
            {
                Name = "create_backup",
                Description = "Create a new backup of the data warehouse",
                InputSchema = new MCPInputSchema
                {
                    Type = "object",
                    Properties = new Dictionary<string, MCPPropertySchema>
                    {
                        ["name"] = new() { Type = "string", Description = "Name for the backup" },
                        ["incremental"] = new() { Type = "boolean", Description = "Create incremental backup" }
                    }
                }
            },
            new()
            {
                Name = "explain_decision",
                Description = "Get an explanation for a storage decision (tiering, archiving, etc.)",
                InputSchema = new MCPInputSchema
                {
                    Type = "object",
                    Properties = new Dictionary<string, MCPPropertySchema>
                    {
                        ["file_id"] = new() { Type = "string", Description = "File or item ID" },
                        ["decision_type"] = new() { Type = "string", Description = "Type of decision to explain", Enum = new List<string> { "tiering", "archiving", "deletion", "replication" } }
                    },
                    Required = new List<string> { "file_id" }
                }
            },
            new()
            {
                Name = "generate_report",
                Description = "Generate a narrative report about storage usage or activity",
                InputSchema = new MCPInputSchema
                {
                    Type = "object",
                    Properties = new Dictionary<string, MCPPropertySchema>
                    {
                        ["report_type"] = new() { Type = "string", Description = "Type of report", Enum = new List<string> { "storage", "backup", "activity", "trends" } },
                        ["time_range"] = new() { Type = "string", Description = "Time range for the report (e.g., 'last week', 'this month')" }
                    },
                    Required = new List<string> { "report_type" }
                }
            },
            new()
            {
                Name = "natural_query",
                Description = "Process any natural language query about the data warehouse",
                InputSchema = new MCPInputSchema
                {
                    Type = "object",
                    Properties = new Dictionary<string, MCPPropertySchema>
                    {
                        ["query"] = new() { Type = "string", Description = "Natural language query" }
                    },
                    Required = new List<string> { "query" }
                }
            }
        };
    }

    /// <summary>
    /// Get available resources.
    /// </summary>
    public List<MCPResource> GetResources()
    {
        return new List<MCPResource>
        {
            new()
            {
                Uri = "datawarehouse://status",
                Name = "Storage Status",
                Description = "Current storage status and health information",
                MimeType = "application/json"
            },
            new()
            {
                Uri = "datawarehouse://backups",
                Name = "Backup List",
                Description = "List of available backups",
                MimeType = "application/json"
            },
            new()
            {
                Uri = "datawarehouse://usage",
                Name = "Usage Statistics",
                Description = "Storage usage statistics and trends",
                MimeType = "application/json"
            },
            new()
            {
                Uri = "datawarehouse://alerts",
                Name = "Active Alerts",
                Description = "Current system alerts and notifications",
                MimeType = "application/json"
            }
        };
    }

    #region Private Methods

    private MCPResponse HandleInitialize(MCPRequest request)
    {
        return CreateSuccessResponse(request.Id, new
        {
            protocolVersion = "2024-11-05",
            capabilities = GetServerInfo().Capabilities,
            serverInfo = new
            {
                name = _config.ServerName,
                version = _config.ServerVersion
            }
        });
    }

    private MCPResponse HandleShutdown(MCPRequest request)
    {
        return CreateSuccessResponse(request.Id, new { });
    }

    private MCPResponse HandleToolsList(MCPRequest request)
    {
        return CreateSuccessResponse(request.Id, new
        {
            tools = GetTools()
        });
    }

    private async Task<MCPResponse> HandleToolsCallAsync(MCPRequest request, CancellationToken ct)
    {
        var toolName = GetParam<string>(request.Params, "name");
        var arguments = GetParam<Dictionary<string, object>>(request.Params, "arguments")
            ?? new Dictionary<string, object>();

        if (string.IsNullOrEmpty(toolName))
        {
            return CreateErrorResponse(request.Id, -32602, "Tool name required");
        }

        try
        {
            var result = toolName switch
            {
                "search_files" => await ExecuteSearchFilesAsync(arguments, ct),
                "get_storage_status" => await ExecuteGetStorageStatusAsync(arguments, ct),
                "list_backups" => await ExecuteListBackupsAsync(arguments, ct),
                "create_backup" => await ExecuteCreateBackupAsync(arguments, ct),
                "explain_decision" => await ExecuteExplainDecisionAsync(arguments, ct),
                "generate_report" => await ExecuteGenerateReportAsync(arguments, ct),
                "natural_query" => await ExecuteNaturalQueryAsync(arguments, ct),
                _ => throw new ArgumentException($"Unknown tool: {toolName}")
            };

            return CreateSuccessResponse(request.Id, result);
        }
        catch (Exception ex)
        {
            return CreateSuccessResponse(request.Id, new MCPToolResult
            {
                IsError = true,
                Content = new List<MCPContent>
                {
                    new() { Type = "text", Text = $"Error: {ex.Message}" }
                }
            });
        }
    }

    private MCPResponse HandleResourcesList(MCPRequest request)
    {
        return CreateSuccessResponse(request.Id, new
        {
            resources = GetResources()
        });
    }

    private async Task<MCPResponse> HandleResourcesReadAsync(MCPRequest request, CancellationToken ct)
    {
        var uri = GetParam<string>(request.Params, "uri");

        if (string.IsNullOrEmpty(uri))
        {
            return CreateErrorResponse(request.Id, -32602, "Resource URI required");
        }

        // Process the resource URI
        var query = uri switch
        {
            "datawarehouse://status" => "What is my storage status?",
            "datawarehouse://backups" => "List my backups",
            "datawarehouse://usage" => "Show my storage usage",
            "datawarehouse://alerts" => "Show active alerts",
            _ => $"Get information about {uri}"
        };

        var conversation = _conversationEngine.StartConversation("mcp:resource", "mcp");
        var result = await _conversationEngine.ProcessMessageAsync(conversation.Id, query, null, ct);

        return CreateSuccessResponse(request.Id, new
        {
            contents = new[]
            {
                new
                {
                    uri = uri,
                    mimeType = "application/json",
                    text = JsonSerializer.Serialize(new
                    {
                        response = result.Response,
                        data = result.Data
                    })
                }
            }
        });
    }

    private MCPResponse HandleResourcesSubscribe(MCPRequest request)
    {
        // Implement resource subscription for real-time updates
        return CreateSuccessResponse(request.Id, new { });
    }

    private MCPResponse HandleResourcesUnsubscribe(MCPRequest request)
    {
        return CreateSuccessResponse(request.Id, new { });
    }

    private MCPResponse HandlePromptsList(MCPRequest request)
    {
        var prompts = new List<object>
        {
            new
            {
                name = "storage_analysis",
                description = "Analyze storage usage and provide recommendations",
                arguments = new object[]
                {
                    new { name = "focus_area", description = "Area to focus on (usage, costs, optimization)", required = false }
                }
            },
            new
            {
                name = "backup_strategy",
                description = "Review and recommend backup strategies",
                arguments = new object[] { }
            },
            new
            {
                name = "troubleshoot",
                description = "Help troubleshoot an issue with the data warehouse",
                arguments = new object[]
                {
                    new { name = "issue", description = "Description of the issue", required = true }
                }
            }
        };

        return CreateSuccessResponse(request.Id, new { prompts });
    }

    private MCPResponse HandlePromptsGet(MCPRequest request)
    {
        var promptName = GetParam<string>(request.Params, "name");
        var promptArgs = GetParam<Dictionary<string, string>>(request.Params, "arguments")
            ?? new Dictionary<string, string>();

        var messages = promptName switch
        {
            "storage_analysis" => new[]
            {
                new
                {
                    role = "user",
                    content = new
                    {
                        type = "text",
                        text = $"Please analyze my data warehouse storage{(promptArgs.TryGetValue("focus_area", out var focus) ? $" with focus on {focus}" : "")}. Use the available tools to gather information and provide insights and recommendations."
                    }
                }
            },
            "backup_strategy" => new[]
            {
                new
                {
                    role = "user",
                    content = new
                    {
                        type = "text",
                        text = "Review my current backup setup and suggest improvements. List existing backups, analyze their patterns, and recommend an optimal backup strategy."
                    }
                }
            },
            "troubleshoot" => new[]
            {
                new
                {
                    role = "user",
                    content = new
                    {
                        type = "text",
                        text = $"Help me troubleshoot this issue with my data warehouse: {promptArgs.GetValueOrDefault("issue", "unknown issue")}. Use available tools to diagnose and suggest solutions."
                    }
                }
            },
            _ => Array.Empty<object>()
        };

        return CreateSuccessResponse(request.Id, new
        {
            description = $"Prompt: {promptName}",
            messages = messages
        });
    }

    private MCPResponse HandleSetLogLevel(MCPRequest request)
    {
        var level = GetParam<string>(request.Params, "level") ?? "info";
        // Set log level
        return CreateSuccessResponse(request.Id, new { });
    }

    private async Task<MCPToolResult> ExecuteSearchFilesAsync(Dictionary<string, object> args, CancellationToken ct)
    {
        var query = GetArg<string>(args, "query") ?? "";
        var fileType = GetArg<string>(args, "file_type");
        var maxResults = GetArg<int?>(args, "max_results") ?? 20;

        var searchQuery = $"Find files {query}";
        if (!string.IsNullOrEmpty(fileType))
        {
            searchQuery += $" of type {fileType}";
        }

        var conversation = _conversationEngine.StartConversation("mcp:tool", "mcp");
        var result = await _conversationEngine.ProcessMessageAsync(conversation.Id, searchQuery, null, ct);

        return new MCPToolResult
        {
            Content = new List<MCPContent>
            {
                new() { Type = "text", Text = result.Response }
            }
        };
    }

    private async Task<MCPToolResult> ExecuteGetStorageStatusAsync(Dictionary<string, object> args, CancellationToken ct)
    {
        var includeDetails = GetArg<bool?>(args, "include_details") ?? false;

        var query = includeDetails
            ? "What is my storage status with detailed breakdown?"
            : "What is my storage status?";

        var conversation = _conversationEngine.StartConversation("mcp:tool", "mcp");
        var result = await _conversationEngine.ProcessMessageAsync(conversation.Id, query, null, ct);

        return new MCPToolResult
        {
            Content = new List<MCPContent>
            {
                new() { Type = "text", Text = result.Response }
            }
        };
    }

    private async Task<MCPToolResult> ExecuteListBackupsAsync(Dictionary<string, object> args, CancellationToken ct)
    {
        var conversation = _conversationEngine.StartConversation("mcp:tool", "mcp");
        var result = await _conversationEngine.ProcessMessageAsync(conversation.Id, "List my backups", null, ct);

        return new MCPToolResult
        {
            Content = new List<MCPContent>
            {
                new() { Type = "text", Text = result.Response }
            }
        };
    }

    private async Task<MCPToolResult> ExecuteCreateBackupAsync(Dictionary<string, object> args, CancellationToken ct)
    {
        var name = GetArg<string>(args, "name");
        var incremental = GetArg<bool?>(args, "incremental") ?? false;

        var query = string.IsNullOrEmpty(name)
            ? $"Create a{(incremental ? "n incremental" : "")} backup"
            : $"Create a{(incremental ? "n incremental" : "")} backup called {name}";

        var conversation = _conversationEngine.StartConversation("mcp:tool", "mcp");
        var result = await _conversationEngine.ProcessMessageAsync(conversation.Id, query, null, ct);

        return new MCPToolResult
        {
            Content = new List<MCPContent>
            {
                new() { Type = "text", Text = result.Response }
            }
        };
    }

    private async Task<MCPToolResult> ExecuteExplainDecisionAsync(Dictionary<string, object> args, CancellationToken ct)
    {
        var fileId = GetArg<string>(args, "file_id") ?? "";
        var decisionType = GetArg<string>(args, "decision_type") ?? "tiering";

        var query = $"Explain why {fileId} was {decisionType switch {
            "tiering" => "tiered",
            "archiving" => "archived",
            "deletion" => "deleted",
            "replication" => "replicated",
            _ => decisionType
        }}";

        var conversation = _conversationEngine.StartConversation("mcp:tool", "mcp");
        var result = await _conversationEngine.ProcessMessageAsync(conversation.Id, query, null, ct);

        return new MCPToolResult
        {
            Content = new List<MCPContent>
            {
                new() { Type = "text", Text = result.Response }
            }
        };
    }

    private async Task<MCPToolResult> ExecuteGenerateReportAsync(Dictionary<string, object> args, CancellationToken ct)
    {
        var reportType = GetArg<string>(args, "report_type") ?? "storage";
        var timeRange = GetArg<string>(args, "time_range") ?? "this month";

        var query = $"Generate a {reportType} report for {timeRange}";

        var conversation = _conversationEngine.StartConversation("mcp:tool", "mcp");
        var result = await _conversationEngine.ProcessMessageAsync(conversation.Id, query, null, ct);

        return new MCPToolResult
        {
            Content = new List<MCPContent>
            {
                new() { Type = "text", Text = result.Response }
            }
        };
    }

    private async Task<MCPToolResult> ExecuteNaturalQueryAsync(Dictionary<string, object> args, CancellationToken ct)
    {
        var query = GetArg<string>(args, "query") ?? "help";

        var conversation = _conversationEngine.StartConversation("mcp:tool", "mcp");
        var result = await _conversationEngine.ProcessMessageAsync(conversation.Id, query, null, ct);

        return new MCPToolResult
        {
            Content = new List<MCPContent>
            {
                new() { Type = "text", Text = result.Response }
            }
        };
    }

    private MCPResponse CreateSuccessResponse(object requestId, object result)
    {
        return new MCPResponse
        {
            JsonRpc = "2.0",
            Id = requestId,
            Result = result
        };
    }

    private MCPResponse CreateErrorResponse(object? requestId, int code, string message)
    {
        return new MCPResponse
        {
            JsonRpc = "2.0",
            Id = requestId ?? 0,
            Error = new MCPError
            {
                Code = code,
                Message = message
            }
        };
    }

    private T? GetParam<T>(Dictionary<string, object>? parameters, string key)
    {
        if (parameters == null || !parameters.TryGetValue(key, out var value))
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
/// Configuration for Claude MCP server.
/// </summary>
public sealed class ClaudeMCPConfig
{
    /// <summary>Whether Claude MCP is enabled.</summary>
    public bool Enabled { get; init; }

    /// <summary>Server name.</summary>
    public string ServerName { get; init; } = "DataWarehouse MCP Server";

    /// <summary>Server version.</summary>
    public string ServerVersion { get; init; } = "1.0.0";

    /// <summary>Transport type (stdio, http).</summary>
    public string Transport { get; init; } = "stdio";

    /// <summary>HTTP port if using HTTP transport.</summary>
    public int? HttpPort { get; init; }
}
