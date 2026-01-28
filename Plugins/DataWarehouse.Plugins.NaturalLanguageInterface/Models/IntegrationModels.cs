// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.NaturalLanguageInterface.Models;

/// <summary>
/// Configuration for AI provider routing per interaction type.
/// </summary>
public sealed class AIProviderRouting
{
    /// <summary>Provider for query parsing.</summary>
    public string QueryParsingProvider { get; init; } = "default";

    /// <summary>Provider for conversation handling.</summary>
    public string ConversationProvider { get; init; } = "default";

    /// <summary>Provider for explanations.</summary>
    public string ExplanationProvider { get; init; } = "default";

    /// <summary>Provider for storytelling.</summary>
    public string StorytellingProvider { get; init; } = "default";

    /// <summary>Provider for embeddings.</summary>
    public string EmbeddingProvider { get; init; } = "default";
}

/// <summary>
/// Integration webhook event.
/// </summary>
public sealed class WebhookEvent
{
    /// <summary>Event type.</summary>
    public string Type { get; init; } = string.Empty;

    /// <summary>Event timestamp.</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>Source platform.</summary>
    public string Platform { get; init; } = string.Empty;

    /// <summary>Event payload.</summary>
    public Dictionary<string, object> Payload { get; init; } = new();

    /// <summary>Request headers.</summary>
    public Dictionary<string, string> Headers { get; init; } = new();

    /// <summary>Signature for verification.</summary>
    public string? Signature { get; init; }
}

/// <summary>
/// Integration response.
/// </summary>
public sealed class IntegrationResponse
{
    /// <summary>HTTP status code.</summary>
    public int StatusCode { get; init; } = 200;

    /// <summary>Response body.</summary>
    public object? Body { get; init; }

    /// <summary>Response headers.</summary>
    public Dictionary<string, string> Headers { get; init; } = new();

    /// <summary>Content type.</summary>
    public string ContentType { get; init; } = "application/json";
}

#region Slack Models

/// <summary>
/// Slack slash command request.
/// </summary>
public sealed class SlackSlashCommand
{
    [JsonPropertyName("token")]
    public string Token { get; init; } = string.Empty;

    [JsonPropertyName("team_id")]
    public string TeamId { get; init; } = string.Empty;

    [JsonPropertyName("team_domain")]
    public string TeamDomain { get; init; } = string.Empty;

    [JsonPropertyName("channel_id")]
    public string ChannelId { get; init; } = string.Empty;

    [JsonPropertyName("channel_name")]
    public string ChannelName { get; init; } = string.Empty;

    [JsonPropertyName("user_id")]
    public string UserId { get; init; } = string.Empty;

    [JsonPropertyName("user_name")]
    public string UserName { get; init; } = string.Empty;

    [JsonPropertyName("command")]
    public string Command { get; init; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    [JsonPropertyName("response_url")]
    public string ResponseUrl { get; init; } = string.Empty;

    [JsonPropertyName("trigger_id")]
    public string TriggerId { get; init; } = string.Empty;
}

/// <summary>
/// Slack event callback.
/// </summary>
public sealed class SlackEventCallback
{
    [JsonPropertyName("token")]
    public string Token { get; init; } = string.Empty;

    [JsonPropertyName("team_id")]
    public string TeamId { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("event")]
    public SlackEvent? Event { get; init; }

    [JsonPropertyName("challenge")]
    public string? Challenge { get; init; }
}

/// <summary>
/// Slack event.
/// </summary>
public sealed class SlackEvent
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("user")]
    public string User { get; init; } = string.Empty;

    [JsonPropertyName("channel")]
    public string Channel { get; init; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    [JsonPropertyName("ts")]
    public string Timestamp { get; init; } = string.Empty;

    [JsonPropertyName("thread_ts")]
    public string? ThreadTimestamp { get; init; }

    [JsonPropertyName("bot_id")]
    public string? BotId { get; init; }
}

/// <summary>
/// Slack message response.
/// </summary>
public sealed class SlackMessageResponse
{
    [JsonPropertyName("response_type")]
    public string ResponseType { get; init; } = "in_channel"; // or "ephemeral"

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    [JsonPropertyName("blocks")]
    public List<SlackBlock>? Blocks { get; init; }

    [JsonPropertyName("attachments")]
    public List<SlackAttachment>? Attachments { get; init; }

    [JsonPropertyName("thread_ts")]
    public string? ThreadTimestamp { get; init; }

    [JsonPropertyName("replace_original")]
    public bool ReplaceOriginal { get; init; }
}

/// <summary>
/// Slack Block Kit block.
/// </summary>
public sealed class SlackBlock
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("text")]
    public SlackTextObject? Text { get; init; }

    [JsonPropertyName("elements")]
    public List<object>? Elements { get; init; }

    [JsonPropertyName("block_id")]
    public string? BlockId { get; init; }

    [JsonPropertyName("accessory")]
    public object? Accessory { get; init; }
}

/// <summary>
/// Slack text object.
/// </summary>
public sealed class SlackTextObject
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "mrkdwn"; // or "plain_text"

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    [JsonPropertyName("emoji")]
    public bool? Emoji { get; init; }
}

/// <summary>
/// Slack attachment (legacy but still useful).
/// </summary>
public sealed class SlackAttachment
{
    [JsonPropertyName("color")]
    public string? Color { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("fields")]
    public List<SlackField>? Fields { get; init; }

    [JsonPropertyName("footer")]
    public string? Footer { get; init; }

    [JsonPropertyName("ts")]
    public long? Timestamp { get; init; }
}

/// <summary>
/// Slack attachment field.
/// </summary>
public sealed class SlackField
{
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;

    [JsonPropertyName("short")]
    public bool Short { get; init; }
}

#endregion

#region Teams Models

/// <summary>
/// Microsoft Teams activity.
/// </summary>
public sealed class TeamsActivity
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; init; }

    [JsonPropertyName("serviceUrl")]
    public string ServiceUrl { get; init; } = string.Empty;

    [JsonPropertyName("channelId")]
    public string ChannelId { get; init; } = string.Empty;

    [JsonPropertyName("from")]
    public TeamsChannelAccount? From { get; init; }

    [JsonPropertyName("conversation")]
    public TeamsConversationAccount? Conversation { get; init; }

    [JsonPropertyName("recipient")]
    public TeamsChannelAccount? Recipient { get; init; }

    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    [JsonPropertyName("value")]
    public Dictionary<string, object>? Value { get; init; }

    [JsonPropertyName("attachments")]
    public List<TeamsAttachment>? Attachments { get; init; }
}

/// <summary>
/// Teams channel account.
/// </summary>
public sealed class TeamsChannelAccount
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("aadObjectId")]
    public string? AadObjectId { get; init; }
}

/// <summary>
/// Teams conversation account.
/// </summary>
public sealed class TeamsConversationAccount
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("conversationType")]
    public string ConversationType { get; init; } = string.Empty;

    [JsonPropertyName("tenantId")]
    public string? TenantId { get; init; }

    [JsonPropertyName("isGroup")]
    public bool IsGroup { get; init; }
}

/// <summary>
/// Teams attachment.
/// </summary>
public sealed class TeamsAttachment
{
    [JsonPropertyName("contentType")]
    public string ContentType { get; init; } = string.Empty;

    [JsonPropertyName("content")]
    public object? Content { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

/// <summary>
/// Teams Adaptive Card.
/// </summary>
public sealed class AdaptiveCard
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "AdaptiveCard";

    [JsonPropertyName("$schema")]
    public string Schema { get; init; } = "http://adaptivecards.io/schemas/adaptive-card.json";

    [JsonPropertyName("version")]
    public string Version { get; init; } = "1.4";

    [JsonPropertyName("body")]
    public List<object> Body { get; init; } = new();

    [JsonPropertyName("actions")]
    public List<object>? Actions { get; init; }
}

#endregion

#region Discord Models

/// <summary>
/// Discord interaction.
/// </summary>
public sealed class DiscordInteraction
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("application_id")]
    public string ApplicationId { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public int Type { get; init; }

    [JsonPropertyName("data")]
    public DiscordInteractionData? Data { get; init; }

    [JsonPropertyName("guild_id")]
    public string? GuildId { get; init; }

    [JsonPropertyName("channel_id")]
    public string? ChannelId { get; init; }

    [JsonPropertyName("member")]
    public DiscordMember? Member { get; init; }

    [JsonPropertyName("user")]
    public DiscordUser? User { get; init; }

    [JsonPropertyName("token")]
    public string Token { get; init; } = string.Empty;
}

/// <summary>
/// Discord interaction data.
/// </summary>
public sealed class DiscordInteractionData
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public int Type { get; init; }

    [JsonPropertyName("options")]
    public List<DiscordCommandOption>? Options { get; init; }

    [JsonPropertyName("custom_id")]
    public string? CustomId { get; init; }

    [JsonPropertyName("values")]
    public List<string>? Values { get; init; }
}

/// <summary>
/// Discord command option.
/// </summary>
public sealed class DiscordCommandOption
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public int Type { get; init; }

    [JsonPropertyName("value")]
    public object? Value { get; init; }

    [JsonPropertyName("options")]
    public List<DiscordCommandOption>? Options { get; init; }
}

/// <summary>
/// Discord member.
/// </summary>
public sealed class DiscordMember
{
    [JsonPropertyName("user")]
    public DiscordUser? User { get; init; }

    [JsonPropertyName("nick")]
    public string? Nick { get; init; }

    [JsonPropertyName("roles")]
    public List<string>? Roles { get; init; }

    [JsonPropertyName("permissions")]
    public string? Permissions { get; init; }
}

/// <summary>
/// Discord user.
/// </summary>
public sealed class DiscordUser
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; init; } = string.Empty;

    [JsonPropertyName("discriminator")]
    public string Discriminator { get; init; } = string.Empty;

    [JsonPropertyName("avatar")]
    public string? Avatar { get; init; }
}

/// <summary>
/// Discord interaction response.
/// </summary>
public sealed class DiscordInteractionResponse
{
    [JsonPropertyName("type")]
    public int Type { get; init; } = 4; // CHANNEL_MESSAGE_WITH_SOURCE

    [JsonPropertyName("data")]
    public DiscordInteractionResponseData? Data { get; init; }
}

/// <summary>
/// Discord interaction response data.
/// </summary>
public sealed class DiscordInteractionResponseData
{
    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("embeds")]
    public List<DiscordEmbed>? Embeds { get; init; }

    [JsonPropertyName("flags")]
    public int? Flags { get; init; } // 64 for ephemeral

    [JsonPropertyName("components")]
    public List<object>? Components { get; init; }
}

/// <summary>
/// Discord embed.
/// </summary>
public sealed class DiscordEmbed
{
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("color")]
    public int? Color { get; init; }

    [JsonPropertyName("fields")]
    public List<DiscordEmbedField>? Fields { get; init; }

    [JsonPropertyName("footer")]
    public DiscordEmbedFooter? Footer { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTime? Timestamp { get; init; }
}

/// <summary>
/// Discord embed field.
/// </summary>
public sealed class DiscordEmbedField
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("value")]
    public string Value { get; init; } = string.Empty;

    [JsonPropertyName("inline")]
    public bool Inline { get; init; }
}

/// <summary>
/// Discord embed footer.
/// </summary>
public sealed class DiscordEmbedFooter
{
    [JsonPropertyName("text")]
    public string Text { get; init; } = string.Empty;

    [JsonPropertyName("icon_url")]
    public string? IconUrl { get; init; }
}

#endregion

#region ChatGPT Plugin Models

/// <summary>
/// OpenAI ChatGPT plugin manifest (ai-plugin.json).
/// </summary>
public sealed class ChatGPTPluginManifest
{
    [JsonPropertyName("schema_version")]
    public string SchemaVersion { get; init; } = "v1";

    [JsonPropertyName("name_for_human")]
    public string NameForHuman { get; init; } = string.Empty;

    [JsonPropertyName("name_for_model")]
    public string NameForModel { get; init; } = string.Empty;

    [JsonPropertyName("description_for_human")]
    public string DescriptionForHuman { get; init; } = string.Empty;

    [JsonPropertyName("description_for_model")]
    public string DescriptionForModel { get; init; } = string.Empty;

    [JsonPropertyName("auth")]
    public ChatGPTPluginAuth Auth { get; init; } = new();

    [JsonPropertyName("api")]
    public ChatGPTPluginApi Api { get; init; } = new();

    [JsonPropertyName("logo_url")]
    public string LogoUrl { get; init; } = string.Empty;

    [JsonPropertyName("contact_email")]
    public string ContactEmail { get; init; } = string.Empty;

    [JsonPropertyName("legal_info_url")]
    public string LegalInfoUrl { get; init; } = string.Empty;
}

/// <summary>
/// ChatGPT plugin authentication config.
/// </summary>
public sealed class ChatGPTPluginAuth
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "service_http"; // none, service_http, user_http, oauth

    [JsonPropertyName("authorization_type")]
    public string? AuthorizationType { get; init; } = "bearer";

    [JsonPropertyName("verification_tokens")]
    public Dictionary<string, string>? VerificationTokens { get; init; }
}

/// <summary>
/// ChatGPT plugin API config.
/// </summary>
public sealed class ChatGPTPluginApi
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "openapi";

    [JsonPropertyName("url")]
    public string Url { get; init; } = string.Empty;

    [JsonPropertyName("is_user_authenticated")]
    public bool IsUserAuthenticated { get; init; }
}

#endregion

#region Claude MCP Models

/// <summary>
/// MCP (Model Context Protocol) server info.
/// </summary>
public sealed class MCPServerInfo
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("protocol_version")]
    public string ProtocolVersion { get; init; } = "2024-11-05";

    [JsonPropertyName("capabilities")]
    public MCPCapabilities Capabilities { get; init; } = new();
}

/// <summary>
/// MCP server capabilities.
/// </summary>
public sealed class MCPCapabilities
{
    [JsonPropertyName("tools")]
    public MCPToolCapabilities? Tools { get; init; }

    [JsonPropertyName("resources")]
    public MCPResourceCapabilities? Resources { get; init; }

    [JsonPropertyName("prompts")]
    public MCPPromptCapabilities? Prompts { get; init; }

    [JsonPropertyName("logging")]
    public object? Logging { get; init; }
}

/// <summary>
/// MCP tool capabilities.
/// </summary>
public sealed class MCPToolCapabilities
{
    [JsonPropertyName("list_changed")]
    public bool? ListChanged { get; init; }
}

/// <summary>
/// MCP resource capabilities.
/// </summary>
public sealed class MCPResourceCapabilities
{
    [JsonPropertyName("subscribe")]
    public bool? Subscribe { get; init; }

    [JsonPropertyName("list_changed")]
    public bool? ListChanged { get; init; }
}

/// <summary>
/// MCP prompt capabilities.
/// </summary>
public sealed class MCPPromptCapabilities
{
    [JsonPropertyName("list_changed")]
    public bool? ListChanged { get; init; }
}

/// <summary>
/// MCP tool definition.
/// </summary>
public sealed class MCPTool
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("inputSchema")]
    public MCPInputSchema InputSchema { get; init; } = new();
}

/// <summary>
/// MCP input schema (JSON Schema).
/// </summary>
public sealed class MCPInputSchema
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "object";

    [JsonPropertyName("properties")]
    public Dictionary<string, MCPPropertySchema>? Properties { get; init; }

    [JsonPropertyName("required")]
    public List<string>? Required { get; init; }
}

/// <summary>
/// MCP property schema.
/// </summary>
public sealed class MCPPropertySchema
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "string";

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("enum")]
    public List<string>? Enum { get; init; }
}

/// <summary>
/// MCP resource definition.
/// </summary>
public sealed class MCPResource
{
    [JsonPropertyName("uri")]
    public string Uri { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("mimeType")]
    public string? MimeType { get; init; }
}

/// <summary>
/// MCP JSON-RPC request.
/// </summary>
public sealed class MCPRequest
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
/// MCP JSON-RPC response.
/// </summary>
public sealed class MCPResponse
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";

    [JsonPropertyName("id")]
    public object Id { get; init; } = 0;

    [JsonPropertyName("result")]
    public object? Result { get; init; }

    [JsonPropertyName("error")]
    public MCPError? Error { get; init; }
}

/// <summary>
/// MCP error.
/// </summary>
public sealed class MCPError
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("data")]
    public object? Data { get; init; }
}

/// <summary>
/// MCP tool call result.
/// </summary>
public sealed class MCPToolResult
{
    [JsonPropertyName("content")]
    public List<MCPContent> Content { get; init; } = new();

    [JsonPropertyName("isError")]
    public bool? IsError { get; init; }
}

/// <summary>
/// MCP content item.
/// </summary>
public sealed class MCPContent
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "text";

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("data")]
    public string? Data { get; init; }

    [JsonPropertyName("mimeType")]
    public string? MimeType { get; init; }
}

#endregion
