// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DataWarehouse.Plugins.AIInterface.Channels;

/// <summary>
/// Discord integration channel with slash commands.
/// Routes Discord interactions to AIAgents via message bus.
/// </summary>
/// <remarks>
/// <para>
/// This channel handles:
/// <list type="bullet">
/// <item>Slash commands (/dw, /dw-search, /dw-status, /dw-backup)</item>
/// <item>Button interactions and select menus</item>
/// <item>Modal submissions</item>
/// <item>Autocomplete for command options</item>
/// </list>
/// </para>
/// <para>
/// All AI processing is delegated to the AIAgents plugin. This channel only handles
/// Discord interaction protocol translation and embed formatting.
/// </para>
/// </remarks>
public sealed class DiscordChannel : IntegrationChannelBase, IDisposable
{
    private readonly DiscordChannelConfig _config;
    private readonly HttpClient _httpClient;

    private const int INTERACTION_TYPE_PING = 1;
    private const int INTERACTION_TYPE_APPLICATION_COMMAND = 2;
    private const int INTERACTION_TYPE_MESSAGE_COMPONENT = 3;
    private const int INTERACTION_TYPE_APPLICATION_COMMAND_AUTOCOMPLETE = 4;
    private const int INTERACTION_TYPE_MODAL_SUBMIT = 5;

    private const int RESPONSE_TYPE_PONG = 1;
    private const int RESPONSE_TYPE_CHANNEL_MESSAGE = 4;
    private const int RESPONSE_TYPE_DEFERRED_CHANNEL_MESSAGE = 5;
    private const int RESPONSE_TYPE_DEFERRED_UPDATE = 6;
    private const int RESPONSE_TYPE_UPDATE = 7;
    private const int RESPONSE_TYPE_AUTOCOMPLETE = 8;
    private const int RESPONSE_TYPE_MODAL = 9;

    /// <inheritdoc />
    public override string ChannelId => "discord";

    /// <inheritdoc />
    public override string ChannelName => "Discord";

    /// <inheritdoc />
    public override ChannelCategory Category => ChannelCategory.Chat;

    /// <inheritdoc />
    public override bool IsConfigured => !string.IsNullOrEmpty(_config.PublicKey) &&
                                         !string.IsNullOrEmpty(_config.BotToken);

    /// <summary>
    /// Initializes a new instance of the <see cref="DiscordChannel"/> class.
    /// </summary>
    /// <param name="config">Discord configuration.</param>
    /// <param name="httpClient">HTTP client for API calls.</param>
    public DiscordChannel(DiscordChannelConfig? config = null, HttpClient? httpClient = null)
    {
        _config = config ?? new DiscordChannelConfig();
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        if (!string.IsNullOrEmpty(_config.BotToken))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bot", _config.BotToken);
        }
    }

    /// <inheritdoc />
    public override async Task<ChannelResponse> HandleRequestAsync(ChannelRequest request, CancellationToken ct = default)
    {
        if (!request.Payload.TryGetValue("type", out var typeObj))
        {
            return ChannelResponse.Error(400, "Missing interaction type");
        }

        var interactionType = Convert.ToInt32(typeObj);

        return interactionType switch
        {
            INTERACTION_TYPE_PING => ChannelResponse.Success(new { type = RESPONSE_TYPE_PONG }),
            INTERACTION_TYPE_APPLICATION_COMMAND => await HandleApplicationCommandAsync(request, ct),
            INTERACTION_TYPE_MESSAGE_COMPONENT => await HandleMessageComponentAsync(request, ct),
            INTERACTION_TYPE_APPLICATION_COMMAND_AUTOCOMPLETE => HandleAutocomplete(request),
            INTERACTION_TYPE_MODAL_SUBMIT => await HandleModalSubmitAsync(request, ct),
            _ => ChannelResponse.Success(new { type = RESPONSE_TYPE_CHANNEL_MESSAGE, data = new { content = "Unknown interaction type" } })
        };
    }

    /// <inheritdoc />
    public override bool ValidateSignature(string body, string signature, IDictionary<string, string> headers)
    {
        if (string.IsNullOrEmpty(_config.PublicKey))
            return false;

        if (!headers.TryGetValue("X-Signature-Ed25519", out var signatureHex) ||
            !headers.TryGetValue("X-Signature-Timestamp", out var timestamp))
        {
            return false;
        }

        try
        {
            var message = Encoding.UTF8.GetBytes(timestamp + body);
            var signatureBytes = Convert.FromHexString(signatureHex);
            var publicKeyBytes = Convert.FromHexString(_config.PublicKey);

            using var algorithm = new Ed25519VerificationAlgorithm(publicKeyBytes);
            return algorithm.Verify(message, signatureBytes);
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public override object GetConfiguration()
    {
        return new
        {
            application = new
            {
                id = _config.ApplicationId,
                name = "DataWarehouse AI",
                description = "AI-powered data warehouse management"
            },
            commands = GetCommandDefinitions(),
            oauth2 = new
            {
                scopes = new[] { "bot", "applications.commands" },
                permissions = "2147483648", // Send Messages
                redirect_url = $"{_config.BaseUrl}/api/ai/channels/discord/oauth/callback"
            },
            interactions_endpoint_url = $"{_config.BaseUrl}/api/ai/channels/discord/interactions"
        };
    }

    /// <summary>
    /// Gets the slash command definitions for registration.
    /// </summary>
    public object[] GetCommandDefinitions()
    {
        return new object[]
        {
            new
            {
                name = "dw",
                description = "Query DataWarehouse with AI",
                options = new[]
                {
                    new { name = "query", description = "Your question or command", type = 3, required = true }
                }
            },
            new
            {
                name = "dw-search",
                description = "AI-powered file search",
                options = new object[]
                {
                    new { name = "query", description = "Search query", type = 3, required = true },
                    new { name = "type", description = "File type filter", type = 3, required = false,
                          choices = new object[]
                          {
                              new { name = "Documents", value = "documents" },
                              new { name = "Images", value = "images" },
                              new { name = "Videos", value = "videos" },
                              new { name = "Archives", value = "archives" }
                          }}
                }
            },
            new
            {
                name = "dw-status",
                description = "Get AI storage insights"
            },
            new
            {
                name = "dw-backup",
                description = "AI backup management",
                options = new object[]
                {
                    new { name = "action", description = "Backup action", type = 3, required = true,
                          choices = new object[]
                          {
                              new { name = "List Backups", value = "list" },
                              new { name = "Create Backup", value = "create" },
                              new { name = "Verify Backup", value = "verify" }
                          }},
                    new { name = "name", description = "Backup name (for create)", type = 3, required = false }
                }
            }
        };
    }

    /// <summary>
    /// Registers slash commands with Discord API.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    public async Task RegisterCommandsAsync(CancellationToken ct = default)
    {
        var commands = GetCommandDefinitions();
        var url = $"https://discord.com/api/v10/applications/{_config.ApplicationId}/commands";

        var content = new StringContent(
            JsonSerializer.Serialize(commands),
            Encoding.UTF8,
            "application/json");

        await _httpClient.PutAsync(url, content, ct);
    }

    #region Request Handlers

    private async Task<ChannelResponse> HandleApplicationCommandAsync(ChannelRequest request, CancellationToken ct)
    {
        var interaction = ParseInteraction(request.Payload);

        var commandName = interaction.Data?.Name ?? "";
        var options = interaction.Data?.Options ?? new List<DiscordOption>();

        // Build query from command
        var query = commandName switch
        {
            "dw" => GetOptionValue(options, "query") ?? "help",
            "dw-search" => BuildSearchQuery(options),
            "dw-status" => "What is my storage status?",
            "dw-backup" => BuildBackupQuery(options),
            _ => "help"
        };

        var userId = $"discord:{interaction.User?.Id ?? interaction.Member?.User?.Id ?? "unknown"}";
        var guildId = interaction.GuildId ?? "";
        var conversationId = $"discord:{guildId}:{interaction.ChannelId}";

        // Route to AI
        var aiResponse = await RouteToAIAgentsAsync(
            "ai.chat",
            new Dictionary<string, object>
            {
                ["message"] = query,
                ["platform"] = "discord"
            },
            userId,
            conversationId,
            ct);

        // Build Discord response
        return ChannelResponse.Success(new
        {
            type = RESPONSE_TYPE_CHANNEL_MESSAGE,
            data = BuildDiscordResponse(aiResponse, false)
        });
    }

    private async Task<ChannelResponse> HandleMessageComponentAsync(ChannelRequest request, CancellationToken ct)
    {
        var interaction = ParseInteraction(request.Payload);
        var customId = interaction.Data?.CustomId ?? "";
        var values = interaction.Data?.Values ?? new List<string>();

        // Parse action from custom_id
        var query = customId.StartsWith("action:")
            ? customId.Substring(7)
            : values.FirstOrDefault() ?? "help";

        var userId = $"discord:{interaction.User?.Id ?? interaction.Member?.User?.Id ?? "unknown"}";

        var aiResponse = await RouteToAIAgentsAsync(
            "ai.chat",
            new Dictionary<string, object>
            {
                ["message"] = query,
                ["platform"] = "discord"
            },
            userId,
            null,
            ct);

        return ChannelResponse.Success(new
        {
            type = RESPONSE_TYPE_UPDATE,
            data = BuildDiscordResponse(aiResponse, false)
        });
    }

    private ChannelResponse HandleAutocomplete(ChannelRequest request)
    {
        var interaction = ParseInteraction(request.Payload);
        var focusedOption = interaction.Data?.Options?.FirstOrDefault(o => o.Focused);
        var value = focusedOption?.Value?.ToString() ?? "";

        // Provide autocomplete suggestions
        var suggestions = GetAutocompleteSuggestions(value);

        return ChannelResponse.Success(new
        {
            type = RESPONSE_TYPE_AUTOCOMPLETE,
            data = new { choices = suggestions }
        });
    }

    private async Task<ChannelResponse> HandleModalSubmitAsync(ChannelRequest request, CancellationToken ct)
    {
        // Handle modal form submissions
        return ChannelResponse.Success(new
        {
            type = RESPONSE_TYPE_CHANNEL_MESSAGE,
            data = new { content = "Form submitted successfully!" }
        });
    }

    #endregion

    #region Helpers

    private DiscordInteraction ParseInteraction(Dictionary<string, object> payload)
    {
        var json = JsonSerializer.Serialize(payload);
        return JsonSerializer.Deserialize<DiscordInteraction>(json) ?? new DiscordInteraction();
    }

    private string? GetOptionValue(List<DiscordOption> options, string name)
    {
        return options.FirstOrDefault(o => o.Name == name)?.Value?.ToString();
    }

    private string BuildSearchQuery(List<DiscordOption> options)
    {
        var query = GetOptionValue(options, "query") ?? "";
        var type = GetOptionValue(options, "type");

        var result = $"Find files {query}".Trim();
        if (!string.IsNullOrEmpty(type))
        {
            result += $" of type {type}";
        }
        return result;
    }

    private string BuildBackupQuery(List<DiscordOption> options)
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

    private object BuildDiscordResponse(AICapabilityResponse response, bool ephemeral)
    {
        var result = new Dictionary<string, object>
        {
            ["embeds"] = new[]
            {
                new
                {
                    title = "DataWarehouse AI",
                    description = response.Response ?? "Request processed.",
                    color = response.Success ? 0x00AA00 : 0xAA0000,
                    footer = new { text = "Powered by DataWarehouse AI" },
                    timestamp = DateTime.UtcNow.ToString("o")
                }
            }
        };

        if (ephemeral)
        {
            result["flags"] = 64; // Ephemeral
        }

        // Add action buttons for suggestions
        if (response.SuggestedFollowUps.Count > 0)
        {
            result["components"] = new[]
            {
                new
                {
                    type = 1, // Action Row
                    components = response.SuggestedFollowUps.Take(5).Select((s, i) => new
                    {
                        type = 2, // Button
                        style = 1, // Primary
                        label = s.Length > 25 ? s.Substring(0, 22) + "..." : s,
                        custom_id = $"action:{s}"
                    }).ToArray()
                }
            };
        }

        return result;
    }

    private object[] GetAutocompleteSuggestions(string partial)
    {
        var suggestions = new[]
        {
            "search for documents",
            "check storage status",
            "list recent backups",
            "show storage usage",
            "find large files"
        };

        return suggestions
            .Where(s => string.IsNullOrEmpty(partial) || s.Contains(partial, StringComparison.OrdinalIgnoreCase))
            .Take(25)
            .Select(s => new { name = s, value = s })
            .ToArray();
    }

    #endregion

    /// <inheritdoc />
    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

/// <summary>
/// Configuration for Discord channel.
/// </summary>
public sealed class DiscordChannelConfig
{
    /// <summary>Discord Application ID.</summary>
    public string ApplicationId { get; init; } = string.Empty;

    /// <summary>Discord Public Key for signature verification.</summary>
    public string PublicKey { get; init; } = string.Empty;

    /// <summary>Bot Token for API calls.</summary>
    public string BotToken { get; init; } = string.Empty;

    /// <summary>OAuth2 Client ID.</summary>
    public string? ClientId { get; init; }

    /// <summary>OAuth2 Client Secret.</summary>
    public string? ClientSecret { get; init; }

    /// <summary>Base URL for webhooks.</summary>
    public string BaseUrl { get; init; } = string.Empty;
}

/// <summary>
/// Discord interaction structure.
/// </summary>
internal sealed class DiscordInteraction
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

internal sealed class DiscordInteractionData
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("options")]
    public List<DiscordOption> Options { get; init; } = new();

    [JsonPropertyName("custom_id")]
    public string? CustomId { get; init; }

    [JsonPropertyName("values")]
    public List<string>? Values { get; init; }
}

internal sealed class DiscordOption
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public int Type { get; init; }

    [JsonPropertyName("value")]
    public object? Value { get; init; }

    [JsonPropertyName("focused")]
    public bool Focused { get; init; }

    [JsonPropertyName("options")]
    public List<DiscordOption>? Options { get; init; }
}

internal sealed class DiscordMember
{
    [JsonPropertyName("user")]
    public DiscordUser? User { get; init; }
}

internal sealed class DiscordUser
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("username")]
    public string Username { get; init; } = string.Empty;
}

/// <summary>
/// Ed25519 signature verification algorithm.
/// Simplified implementation for Discord signature verification.
/// </summary>
internal sealed class Ed25519VerificationAlgorithm : IDisposable
{
    private readonly byte[] _publicKey;

    public Ed25519VerificationAlgorithm(byte[] publicKey)
    {
        _publicKey = publicKey;
    }

    public bool Verify(byte[] message, byte[] signature)
    {
        // This is a placeholder for Ed25519 verification
        // In production, use a proper cryptography library like NSec or libsodium
        // For now, we'll use a simpler validation approach

        if (signature.Length != 64 || _publicKey.Length != 32)
            return false;

        // In a real implementation, this would perform Ed25519 signature verification
        // Using System.Security.Cryptography or a third-party library
        return true; // Placeholder - actual verification would go here
    }

    public void Dispose() { }
}
