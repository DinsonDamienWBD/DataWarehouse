using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.SDK.Contracts.Interface;

/// <summary>
/// Direction of WebSocket message flow.
/// </summary>
public enum WebSocketDirection
{
    /// <summary>Messages flow from server to client only.</summary>
    ServerToClient,

    /// <summary>Messages flow from client to server only.</summary>
    ClientToServer,

    /// <summary>Messages flow in both directions.</summary>
    Bidirectional
}

/// <summary>
/// Defines a WebSocket channel generated from plugin capabilities.
/// </summary>
public sealed record WebSocketChannelDefinition
{
    /// <summary>Channel path (e.g., "/ws/v1/storage/events").</summary>
    public required string Path { get; init; }

    /// <summary>Source plugin identifier.</summary>
    public required string PluginId { get; init; }

    /// <summary>Source capability name.</summary>
    public required string CapabilityName { get; init; }

    /// <summary>Human-readable description.</summary>
    public string? Description { get; init; }

    /// <summary>Message direction for this channel.</summary>
    public WebSocketDirection Direction { get; init; } = WebSocketDirection.Bidirectional;

    /// <summary>JSON schema for messages on this channel.</summary>
    public string? MessageSchema { get; init; }

    /// <summary>Expected message types on this channel.</summary>
    public IReadOnlyList<string> MessageTypes { get; init; } = Array.Empty<string>();

    /// <summary>Whether authentication is required to connect.</summary>
    public bool RequiresAuth { get; init; } = true;

    /// <summary>Maximum message size in bytes (null = unlimited).</summary>
    public long? MaxMessageSize { get; init; }
}

/// <summary>
/// The standard WebSocket message envelope used across all channels.
/// </summary>
public sealed record WebSocketMessageEnvelope
{
    /// <summary>Message type: "event", "command", or "response".</summary>
    public required string Type { get; init; }

    /// <summary>Channel path this message belongs to.</summary>
    public required string Channel { get; init; }

    /// <summary>Message payload (JSON object).</summary>
    public required JsonElement Payload { get; init; }

    /// <summary>Correlation ID for request/response matching.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Timestamp when the message was created.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Source plugin ID (set by server for events).</summary>
    public string? SourcePluginId { get; init; }
}

/// <summary>
/// Generates WebSocket channel definitions and TypeScript client SDK type definitions
/// from a <see cref="DynamicApiModel"/>.
/// </summary>
/// <remarks>
/// <para>Channel generation rules:</para>
/// <list type="bullet">
///   <item><description>Streaming capabilities (Pipeline, Replication, Transit, Transport) get server-to-client event channels</description></item>
///   <item><description>Command capabilities get bidirectional channels for send/receive</description></item>
///   <item><description>All channels use the standard message envelope: type, channel, payload, correlationId</description></item>
/// </list>
/// </remarks>
public sealed class WebSocketApiGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>
    /// Generates WebSocket channel definitions from the API model.
    /// </summary>
    /// <param name="model">The API model to generate from.</param>
    /// <returns>A list of channel definitions.</returns>
    public IReadOnlyList<WebSocketChannelDefinition> GenerateChannelDefinitions(DynamicApiModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var channels = new List<WebSocketChannelDefinition>();

        foreach (var endpoint in model.Endpoints.Where(e => e.IsStreaming))
        {
            channels.Add(CreateStreamingChannel(endpoint));
        }

        // Also create command channels for non-streaming endpoints that benefit from real-time interaction
        var pluginGroups = model.Endpoints
            .Where(e => !e.IsStreaming)
            .GroupBy(e => e.PluginId);

        foreach (var group in pluginGroups)
        {
            channels.Add(CreateCommandChannel(group.Key, group.ToList()));
        }

        // System-level channels
        channels.Add(new WebSocketChannelDefinition
        {
            Path = "/ws/v1/system/events",
            PluginId = "system",
            CapabilityName = "system.events",
            Description = "System-wide events: capability changes, plugin lifecycle, health updates",
            Direction = WebSocketDirection.ServerToClient,
            MessageTypes = new[] { "capability.registered", "capability.unregistered", "capability.availability", "plugin.loaded", "plugin.unloaded", "health.update" },
            MessageSchema = GenerateSystemEventSchema()
        });

        channels.Add(new WebSocketChannelDefinition
        {
            Path = "/ws/v1/system/commands",
            PluginId = "system",
            CapabilityName = "system.commands",
            Description = "System command channel: send commands, receive responses",
            Direction = WebSocketDirection.Bidirectional,
            MessageTypes = new[] { "command", "response", "error" },
            MessageSchema = GenerateCommandSchema()
        });

        return channels.AsReadOnly();
    }

    /// <summary>
    /// Generates TypeScript type definitions for WebSocket client SDK.
    /// </summary>
    /// <param name="channels">Channel definitions to generate types for.</param>
    /// <returns>TypeScript source code with type definitions.</returns>
    public string GenerateClientSdkTypescript(IReadOnlyList<WebSocketChannelDefinition> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);

        var sb = new StringBuilder(4096);

        WriteTypeScriptHeader(sb);
        WriteEnvelopeTypes(sb);
        WriteChannelTypes(sb, channels);
        WriteClientInterface(sb, channels);
        WriteChannelMap(sb, channels);

        return sb.ToString();
    }

    #region Channel Generation

    private static WebSocketChannelDefinition CreateStreamingChannel(ApiEndpoint endpoint)
    {
        var pluginSlug = SanitizeSlug(endpoint.PluginId);
        var capSlug = SanitizeSlug(endpoint.CapabilityName);

        return new WebSocketChannelDefinition
        {
            Path = $"/ws/v1/{pluginSlug}/{capSlug}",
            PluginId = endpoint.PluginId,
            CapabilityName = endpoint.CapabilityName,
            Description = $"Streaming channel for {endpoint.Description ?? endpoint.CapabilityName}",
            Direction = WebSocketDirection.ServerToClient,
            MessageTypes = new[] { "event", "status", "error" },
            MessageSchema = GenerateStreamEventSchema(endpoint),
            RequiresAuth = true,
            MaxMessageSize = 1024 * 1024 // 1 MB per message default
        };
    }

    private static WebSocketChannelDefinition CreateCommandChannel(string pluginId, List<ApiEndpoint> endpoints)
    {
        var pluginSlug = SanitizeSlug(pluginId);
        var messageTypes = new List<string> { "command", "response", "error" };

        return new WebSocketChannelDefinition
        {
            Path = $"/ws/v1/{pluginSlug}/commands",
            PluginId = pluginId,
            CapabilityName = $"{pluginId}.commands",
            Description = $"Command channel for {pluginId}: send commands, receive responses. " +
                $"Supports {endpoints.Count} operations.",
            Direction = WebSocketDirection.Bidirectional,
            MessageTypes = messageTypes,
            MessageSchema = GenerateCommandSchemaForEndpoints(endpoints),
            RequiresAuth = true,
            MaxMessageSize = 10 * 1024 * 1024 // 10 MB for command payloads
        };
    }

    #endregion

    #region Schema Generation

    private static string GenerateStreamEventSchema(ApiEndpoint endpoint)
    {
        var schema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["type"] = new { type = "string", @enum = new[] { "event", "status", "error" } },
                ["channel"] = new { type = "string", @const = endpoint.Path },
                ["payload"] = new { type = "object" },
                ["correlationId"] = new { type = "string" },
                ["timestamp"] = new { type = "string", format = "date-time" }
            },
            required = new[] { "type", "channel", "payload", "timestamp" }
        };

        return JsonSerializer.Serialize(schema, JsonOptions);
    }

    private static string GenerateCommandSchemaForEndpoints(List<ApiEndpoint> endpoints)
    {
        var commands = endpoints.Select(ep => new
        {
            command = ep.CapabilityName,
            method = ep.Method.ToString(),
            description = ep.Description ?? ep.CapabilityName
        });

        var schema = new
        {
            type = "object",
            description = "Command/response envelope",
            properties = new Dictionary<string, object>
            {
                ["type"] = new { type = "string", @enum = new[] { "command", "response", "error" } },
                ["channel"] = new { type = "string" },
                ["payload"] = new
                {
                    type = "object",
                    properties = new Dictionary<string, object>
                    {
                        ["command"] = new { type = "string" },
                        ["data"] = new { type = "object" }
                    }
                },
                ["correlationId"] = new { type = "string" }
            },
            required = new[] { "type", "channel", "payload" }
        };

        return JsonSerializer.Serialize(schema, JsonOptions);
    }

    private static string GenerateSystemEventSchema()
    {
        var schema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["type"] = new { type = "string", @enum = new[] { "capability.registered", "capability.unregistered", "capability.availability", "plugin.loaded", "plugin.unloaded", "health.update" } },
                ["channel"] = new { type = "string", @const = "/ws/v1/system/events" },
                ["payload"] = new { type = "object" },
                ["timestamp"] = new { type = "string", format = "date-time" }
            },
            required = new[] { "type", "channel", "payload", "timestamp" }
        };

        return JsonSerializer.Serialize(schema, JsonOptions);
    }

    private static string GenerateCommandSchema()
    {
        var schema = new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["type"] = new { type = "string", @enum = new[] { "command", "response", "error" } },
                ["channel"] = new { type = "string" },
                ["payload"] = new { type = "object" },
                ["correlationId"] = new { type = "string" }
            },
            required = new[] { "type", "channel", "payload" }
        };

        return JsonSerializer.Serialize(schema, JsonOptions);
    }

    #endregion

    #region TypeScript Generation

    private static void WriteTypeScriptHeader(StringBuilder sb)
    {
        sb.AppendLine("// Auto-generated DataWarehouse WebSocket Client SDK");
        sb.AppendLine("// DO NOT EDIT - regenerated when plugin capabilities change");
        sb.AppendLine();
        sb.AppendLine("/* eslint-disable @typescript-eslint/no-explicit-any */");
        sb.AppendLine();
    }

    private static void WriteEnvelopeTypes(StringBuilder sb)
    {
        sb.AppendLine("/** Message type discriminator */");
        sb.AppendLine("export type MessageType = 'event' | 'command' | 'response' | 'error';");
        sb.AppendLine();

        sb.AppendLine("/** Direction of message flow on a channel */");
        sb.AppendLine("export type ChannelDirection = 'server-to-client' | 'client-to-server' | 'bidirectional';");
        sb.AppendLine();

        sb.AppendLine("/** Standard message envelope for all WebSocket communications */");
        sb.AppendLine("export interface WebSocketMessage<T = any> {");
        sb.AppendLine("  type: MessageType;");
        sb.AppendLine("  channel: string;");
        sb.AppendLine("  payload: T;");
        sb.AppendLine("  correlationId?: string;");
        sb.AppendLine("  timestamp: string;");
        sb.AppendLine("  sourcePluginId?: string;");
        sb.AppendLine("}");
        sb.AppendLine();

        sb.AppendLine("/** Command message sent from client to server */");
        sb.AppendLine("export interface CommandMessage<T = any> extends WebSocketMessage<T> {");
        sb.AppendLine("  type: 'command';");
        sb.AppendLine("}");
        sb.AppendLine();

        sb.AppendLine("/** Event message sent from server to client */");
        sb.AppendLine("export interface EventMessage<T = any> extends WebSocketMessage<T> {");
        sb.AppendLine("  type: 'event';");
        sb.AppendLine("}");
        sb.AppendLine();

        sb.AppendLine("/** Response message sent from server in reply to a command */");
        sb.AppendLine("export interface ResponseMessage<T = any> extends WebSocketMessage<T> {");
        sb.AppendLine("  type: 'response';");
        sb.AppendLine("  correlationId: string;");
        sb.AppendLine("}");
        sb.AppendLine();

        sb.AppendLine("/** Error message sent from server */");
        sb.AppendLine("export interface ErrorMessage extends WebSocketMessage<{ code: string; message: string; details?: any }> {");
        sb.AppendLine("  type: 'error';");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void WriteChannelTypes(StringBuilder sb, IReadOnlyList<WebSocketChannelDefinition> channels)
    {
        sb.AppendLine("/** Channel definition metadata */");
        sb.AppendLine("export interface ChannelDefinition {");
        sb.AppendLine("  path: string;");
        sb.AppendLine("  pluginId: string;");
        sb.AppendLine("  capabilityName: string;");
        sb.AppendLine("  description: string;");
        sb.AppendLine("  direction: ChannelDirection;");
        sb.AppendLine("  messageTypes: MessageType[];");
        sb.AppendLine("  requiresAuth: boolean;");
        sb.AppendLine("  maxMessageSize?: number;");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void WriteClientInterface(StringBuilder sb, IReadOnlyList<WebSocketChannelDefinition> channels)
    {
        sb.AppendLine("/** DataWarehouse WebSocket client interface */");
        sb.AppendLine("export interface DataWarehouseWebSocketClient {");
        sb.AppendLine("  /** Connect to the WebSocket server */");
        sb.AppendLine("  connect(url: string, authToken?: string): Promise<void>;");
        sb.AppendLine();
        sb.AppendLine("  /** Disconnect from the server */");
        sb.AppendLine("  disconnect(): Promise<void>;");
        sb.AppendLine();
        sb.AppendLine("  /** Subscribe to events on a channel */");
        sb.AppendLine("  subscribe<T = any>(channel: string, handler: (message: EventMessage<T>) => void): () => void;");
        sb.AppendLine();
        sb.AppendLine("  /** Send a command and await the response */");
        sb.AppendLine("  sendCommand<TReq = any, TRes = any>(channel: string, command: string, data: TReq): Promise<ResponseMessage<TRes>>;");
        sb.AppendLine();
        sb.AppendLine("  /** Get all available channel definitions */");
        sb.AppendLine("  getChannels(): ChannelDefinition[];");
        sb.AppendLine();
        sb.AppendLine("  /** Check if connected */");
        sb.AppendLine("  readonly isConnected: boolean;");
        sb.AppendLine("}");
        sb.AppendLine();
    }

    private static void WriteChannelMap(StringBuilder sb, IReadOnlyList<WebSocketChannelDefinition> channels)
    {
        sb.AppendLine("/** All available WebSocket channels */");
        sb.AppendLine("export const CHANNELS: Record<string, ChannelDefinition> = {");

        foreach (var channel in channels)
        {
            var key = channel.Path.Replace("/", "_").TrimStart('_').Replace("-", "_");
            var direction = channel.Direction switch
            {
                WebSocketDirection.ServerToClient => "server-to-client",
                WebSocketDirection.ClientToServer => "client-to-server",
                _ => "bidirectional"
            };

            sb.AppendLine($"  '{key}': {{");
            sb.AppendLine($"    path: '{channel.Path}',");
            sb.AppendLine($"    pluginId: '{channel.PluginId}',");
            sb.AppendLine($"    capabilityName: '{channel.CapabilityName}',");
            sb.AppendLine($"    description: '{EscapeForTs(channel.Description ?? channel.CapabilityName)}',");
            sb.AppendLine($"    direction: '{direction}',");
            sb.AppendLine($"    messageTypes: [{string.Join(", ", channel.MessageTypes.Select(t => $"'{t}'"))}],");
            sb.AppendLine($"    requiresAuth: {(channel.RequiresAuth ? "true" : "false")},");
            if (channel.MaxMessageSize.HasValue)
            {
                sb.AppendLine($"    maxMessageSize: {channel.MaxMessageSize.Value},");
            }
            sb.AppendLine("  },");
        }

        sb.AppendLine("};");
        sb.AppendLine();
    }

    #endregion

    #region Helpers

    private static string SanitizeSlug(string input)
    {
        return input.Replace(".", "-").Replace(" ", "-").ToLowerInvariant();
    }

    private static string EscapeForTs(string input)
    {
        return input.Replace("'", "\\'").Replace("\n", " ").Replace("\r", "");
    }

    #endregion
}
