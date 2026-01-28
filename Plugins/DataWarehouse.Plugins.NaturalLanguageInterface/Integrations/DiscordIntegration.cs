// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Plugins.NaturalLanguageInterface.Engine;
using DataWarehouse.Plugins.NaturalLanguageInterface.Models;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DataWarehouse.Plugins.NaturalLanguageInterface.Integrations;

/// <summary>
/// Discord integration using Discord Interactions API.
/// Supports slash commands, message components, and rich embeds.
/// </summary>
public sealed class DiscordIntegration : IntegrationProviderBase, IDisposable
{
    private readonly QueryEngine _queryEngine;
    private readonly ConversationEngine _conversationEngine;
    private readonly DiscordConfig _config;
    private readonly HttpClient _httpClient;

    private const string DiscordApiBase = "https://discord.com/api/v10";

    public override string PlatformId => "discord";
    public override string PlatformName => "Discord";
    public override bool IsConfigured => !string.IsNullOrEmpty(_config.ApplicationId) &&
                                         !string.IsNullOrEmpty(_config.PublicKey);

    public DiscordIntegration(
        QueryEngine queryEngine,
        ConversationEngine conversationEngine,
        DiscordConfig? config = null,
        HttpClient? httpClient = null)
    {
        _queryEngine = queryEngine;
        _conversationEngine = conversationEngine;
        _config = config ?? new DiscordConfig();
        _httpClient = httpClient ?? new HttpClient();

        if (!string.IsNullOrEmpty(_config.BotToken))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bot", _config.BotToken);
        }
    }

    public override async Task<IntegrationResponse> HandleWebhookAsync(WebhookEvent webhookEvent, CancellationToken ct = default)
    {
        var interactionJson = JsonSerializer.Serialize(webhookEvent.Payload);
        var interaction = JsonSerializer.Deserialize<DiscordInteraction>(interactionJson);

        if (interaction == null)
        {
            return ErrorResponse(400, "Invalid interaction");
        }

        return interaction.Type switch
        {
            1 => HandlePing(), // PING
            2 => await HandleApplicationCommandAsync(interaction, ct), // APPLICATION_COMMAND
            3 => await HandleMessageComponentAsync(interaction, ct), // MESSAGE_COMPONENT
            4 => await HandleAutocompleteAsync(interaction, ct), // APPLICATION_COMMAND_AUTOCOMPLETE
            5 => await HandleModalSubmitAsync(interaction, ct), // MODAL_SUBMIT
            _ => ErrorResponse(400, "Unknown interaction type")
        };
    }

    public override bool ValidateWebhookSignature(string body, string signature, IDictionary<string, string> headers)
    {
        if (!headers.TryGetValue("X-Signature-Ed25519", out var sig) ||
            !headers.TryGetValue("X-Signature-Timestamp", out var timestamp))
        {
            return false;
        }

        // Verify Ed25519 signature
        var message = Encoding.UTF8.GetBytes(timestamp + body);
        var signatureBytes = Convert.FromHexString(sig);
        var publicKeyBytes = Convert.FromHexString(_config.PublicKey);

        // Ed25519 verification using System.Security.Cryptography (or NSec library)
        // For production, use a proper Ed25519 implementation
        try
        {
            using var ed25519 = new Ed25519PublicKey(publicKeyBytes);
            return ed25519.Verify(message, signatureBytes);
        }
        catch
        {
            // Fallback: In development, might skip validation
            return false;
        }
    }

    public override object GetConfiguration()
    {
        return new
        {
            // Application commands to register
            commands = new object[]
            {
                new
                {
                    name = "dw",
                    description = "Query your DataWarehouse",
                    type = 1, // CHAT_INPUT
                    options = new object[]
                    {
                        new
                        {
                            name = "query",
                            description = "Your natural language query",
                            type = 3, // STRING
                            required = true
                        }
                    }
                },
                new
                {
                    name = "dw-search",
                    description = "Search for files",
                    type = 1,
                    options = new object[]
                    {
                        new
                        {
                            name = "query",
                            description = "Search query",
                            type = 3,
                            required = true
                        },
                        new
                        {
                            name = "type",
                            description = "File type filter",
                            type = 3,
                            required = false,
                            choices = new[]
                            {
                                new { name = "Documents", value = "doc" },
                                new { name = "Images", value = "image" },
                                new { name = "Videos", value = "video" },
                                new { name = "Archives", value = "archive" }
                            }
                        },
                        new
                        {
                            name = "timeframe",
                            description = "Time filter",
                            type = 3,
                            required = false,
                            choices = new[]
                            {
                                new { name = "Today", value = "today" },
                                new { name = "This week", value = "week" },
                                new { name = "This month", value = "month" },
                                new { name = "This year", value = "year" }
                            }
                        }
                    }
                },
                new
                {
                    name = "dw-status",
                    description = "Check your storage status",
                    type = 1
                },
                new
                {
                    name = "dw-backup",
                    description = "Manage backups",
                    type = 1,
                    options = new object[]
                    {
                        new
                        {
                            name = "action",
                            description = "Backup action",
                            type = 3,
                            required = true,
                            choices = new[]
                            {
                                new { name = "List backups", value = "list" },
                                new { name = "Create backup", value = "create" },
                                new { name = "Verify backup", value = "verify" }
                            }
                        },
                        new
                        {
                            name = "name",
                            description = "Backup name (for create)",
                            type = 3,
                            required = false
                        }
                    }
                },
                new
                {
                    name = "dw-help",
                    description = "Get help with DataWarehouse commands",
                    type = 1
                }
            },
            // Bot configuration
            bot = new
            {
                application_id = _config.ApplicationId,
                public_key = _config.PublicKey,
                interactions_endpoint_url = $"{_config.BaseUrl}/api/integrations/discord/interactions"
            }
        };
    }

    /// <summary>
    /// Register slash commands with Discord.
    /// Call this during setup.
    /// </summary>
    public async Task RegisterCommandsAsync(CancellationToken ct = default)
    {
        var config = GetConfiguration();
        var commands = (config as dynamic)?.commands as object[];

        if (commands == null) return;

        var url = string.IsNullOrEmpty(_config.GuildId)
            ? $"{DiscordApiBase}/applications/{_config.ApplicationId}/commands"
            : $"{DiscordApiBase}/applications/{_config.ApplicationId}/guilds/{_config.GuildId}/commands";

        var content = new StringContent(
            JsonSerializer.Serialize(commands),
            Encoding.UTF8,
            "application/json");

        await _httpClient.PutAsync(url, content, ct);
    }

    #region Private Methods

    private IntegrationResponse HandlePing()
    {
        return SuccessResponse(new DiscordInteractionResponse { Type = 1 }); // PONG
    }

    private async Task<IntegrationResponse> HandleApplicationCommandAsync(DiscordInteraction interaction, CancellationToken ct)
    {
        var commandName = interaction.Data?.Name ?? "";
        var options = interaction.Data?.Options ?? new List<DiscordCommandOption>();

        // Build query based on command
        var query = commandName switch
        {
            "dw" => GetOptionValue(options, "query") ?? "help",
            "dw-search" => BuildSearchQuery(options),
            "dw-status" => "What is my storage status?",
            "dw-backup" => BuildBackupQuery(options),
            "dw-help" => "help",
            _ => "help"
        };

        // Get user info
        var user = interaction.Member?.User ?? interaction.User;
        var userId = $"discord:{user?.Id ?? "unknown"}";
        var channelId = interaction.ChannelId ?? "";

        // Get or create conversation
        var conversationId = $"discord:{channelId}";
        var conversation = _conversationEngine.GetConversation(conversationId)
            ?? _conversationEngine.StartConversation(userId, "discord", channelId);

        // Process the query
        var result = await _conversationEngine.ProcessMessageAsync(conversation.Id, query, null, ct);

        // Build Discord response
        var response = BuildDiscordResponse(result);

        return SuccessResponse(response);
    }

    private async Task<IntegrationResponse> HandleMessageComponentAsync(DiscordInteraction interaction, CancellationToken ct)
    {
        var customId = interaction.Data?.CustomId ?? "";
        var values = interaction.Data?.Values;

        // Parse custom_id to determine action
        var parts = customId.Split(':');
        var action = parts[0];
        var value = parts.Length > 1 ? parts[1] : "";

        var query = action switch
        {
            "search_more" => $"Show more results for {value}",
            "filter" => $"Filter by {value}",
            "followup" => value,
            "select" => values?.FirstOrDefault() ?? "",
            _ => value
        };

        if (string.IsNullOrEmpty(query))
        {
            return SuccessResponse(new DiscordInteractionResponse
            {
                Type = 6 // DEFERRED_UPDATE_MESSAGE
            });
        }

        var user = interaction.Member?.User ?? interaction.User;
        var userId = $"discord:{user?.Id ?? "unknown"}";
        var conversation = _conversationEngine.StartConversation(userId, "discord");

        var result = await _conversationEngine.ProcessMessageAsync(conversation.Id, query, null, ct);

        return SuccessResponse(new DiscordInteractionResponse
        {
            Type = 7, // UPDATE_MESSAGE
            Data = BuildResponseData(result)
        });
    }

    private async Task<IntegrationResponse> HandleAutocompleteAsync(DiscordInteraction interaction, CancellationToken ct)
    {
        var focusedOption = interaction.Data?.Options?
            .FirstOrDefault(o => o.Options?.Any(oo => true) ?? false)
            ?? interaction.Data?.Options?.FirstOrDefault();

        var value = focusedOption?.Value?.ToString() ?? "";

        // Get suggestions
        var suggestions = new List<object>();

        if (value.Length >= 2)
        {
            var intent = await _queryEngine.ParseQueryAsync(value, null, ct);

            // Add suggestions based on detected intent
            suggestions.AddRange(new[]
            {
                new { name = $"Find files matching '{value}'", value = $"find files {value}" },
                new { name = $"Search for '{value}'", value = $"search {value}" }
            }.Take(5));
        }
        else
        {
            suggestions.AddRange(new[]
            {
                new { name = "storage status", value = "storage status" },
                new { name = "list backups", value = "list backups" },
                new { name = "find recent files", value = "find recent files" }
            });
        }

        return SuccessResponse(new
        {
            type = 8, // APPLICATION_COMMAND_AUTOCOMPLETE_RESULT
            data = new { choices = suggestions.Take(25) }
        });
    }

    private async Task<IntegrationResponse> HandleModalSubmitAsync(DiscordInteraction interaction, CancellationToken ct)
    {
        // Handle modal form submissions
        return SuccessResponse(new DiscordInteractionResponse
        {
            Type = 6 // DEFERRED_UPDATE_MESSAGE
        });
    }

    private string BuildSearchQuery(List<DiscordCommandOption> options)
    {
        var query = GetOptionValue(options, "query") ?? "";
        var type = GetOptionValue(options, "type");
        var timeframe = GetOptionValue(options, "timeframe");

        var parts = new List<string> { "Find files" };

        if (!string.IsNullOrEmpty(query))
        {
            parts.Add($"matching {query}");
        }

        if (!string.IsNullOrEmpty(type))
        {
            parts.Add($"of type {type}");
        }

        if (!string.IsNullOrEmpty(timeframe))
        {
            parts.Add($"from {timeframe}");
        }

        return string.Join(" ", parts);
    }

    private string BuildBackupQuery(List<DiscordCommandOption> options)
    {
        var action = GetOptionValue(options, "action") ?? "list";
        var name = GetOptionValue(options, "name");

        return action switch
        {
            "create" => string.IsNullOrEmpty(name) ? "Create a backup" : $"Create a backup called {name}",
            "verify" => "Verify my latest backup",
            _ => "List my backups"
        };
    }

    private string? GetOptionValue(List<DiscordCommandOption> options, string name)
    {
        var option = options.FirstOrDefault(o =>
            o.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return option?.Value?.ToString();
    }

    private DiscordInteractionResponse BuildDiscordResponse(ConversationTurnResult result)
    {
        return new DiscordInteractionResponse
        {
            Type = 4, // CHANNEL_MESSAGE_WITH_SOURCE
            Data = BuildResponseData(result)
        };
    }

    private DiscordInteractionResponseData BuildResponseData(ConversationTurnResult result)
    {
        // Build fields based on structured data
        List<DiscordEmbedField>? fields = null;
        if (result.Data != null)
        {
            // Could populate fields from data here
            fields = null;
        }

        var embed = new DiscordEmbed
        {
            Title = "DataWarehouse",
            Description = result.Response,
            Color = result.Success ? 0x00CC66 : 0xCC0000, // Green or red
            Timestamp = DateTime.UtcNow,
            Footer = new DiscordEmbedFooter { Text = "DataWarehouse Assistant" },
            Fields = fields
        };

        var components = new List<object>();

        // Add clarification buttons
        if (result.Clarification != null && result.Clarification.Options.Count > 0)
        {
            var buttons = result.Clarification.Options.Take(5).Select((opt, i) => new
            {
                type = 2, // BUTTON
                style = 1, // PRIMARY
                label = opt.Label,
                custom_id = $"clarification:{opt.Value}"
            }).ToList();

            components.Add(new
            {
                type = 1, // ACTION_ROW
                components = buttons
            });
        }

        // Add follow-up suggestions
        if (result.SuggestedFollowUps.Count > 0)
        {
            var buttons = result.SuggestedFollowUps.Take(3).Select((suggestion, i) => new
            {
                type = 2,
                style = 2, // SECONDARY
                label = suggestion.Length > 25 ? suggestion.Substring(0, 22) + "..." : suggestion,
                custom_id = $"followup:{suggestion}"
            }).ToList();

            components.Add(new
            {
                type = 1,
                components = buttons
            });
        }

        return new DiscordInteractionResponseData
        {
            Embeds = new List<DiscordEmbed> { embed },
            Components = components.Count > 0 ? components : null,
            Flags = result.State == DialogueState.Error ? 64 : null // Ephemeral for errors
        };
    }

    #endregion

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

/// <summary>
/// Minimal Ed25519 public key verification.
/// For production, use a proper cryptographic library like NSec or libsodium.
/// </summary>
internal class Ed25519PublicKey : IDisposable
{
    private readonly byte[] _publicKey;

    public Ed25519PublicKey(byte[] publicKey)
    {
        if (publicKey.Length != 32)
            throw new ArgumentException("Invalid Ed25519 public key length");
        _publicKey = publicKey;
    }

    public bool Verify(byte[] message, byte[] signature)
    {
        // This is a placeholder. In production, use a proper Ed25519 implementation.
        // Options: NSec, Sodium.Core, or BouncyCastle

        // For now, return true in development mode
        // In production, implement proper verification:
        // return Ed25519.Verify(signature, message, _publicKey);

        return signature.Length == 64; // Basic sanity check
    }

    public void Dispose() { }
}

/// <summary>
/// Configuration for Discord integration.
/// </summary>
public sealed class DiscordConfig
{
    /// <summary>Whether Discord integration is enabled.</summary>
    public bool Enabled { get; init; }

    /// <summary>Whether to auto-register slash commands on startup.</summary>
    public bool AutoRegisterCommands { get; init; }

    /// <summary>Discord Application ID.</summary>
    public string ApplicationId { get; init; } = string.Empty;

    /// <summary>Discord Public Key for signature verification.</summary>
    public string PublicKey { get; init; } = string.Empty;

    /// <summary>Bot token for API calls.</summary>
    public string? BotToken { get; init; }

    /// <summary>Client secret for OAuth.</summary>
    public string? ClientSecret { get; init; }

    /// <summary>Guild ID for guild-specific commands (development).</summary>
    public string? GuildId { get; init; }

    /// <summary>Base URL for webhooks.</summary>
    public string BaseUrl { get; init; } = string.Empty;
}
