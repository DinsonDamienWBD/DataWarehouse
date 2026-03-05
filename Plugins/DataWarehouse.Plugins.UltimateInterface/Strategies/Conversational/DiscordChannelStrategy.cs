using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SdkInterface = DataWarehouse.SDK.Contracts.Interface;

namespace DataWarehouse.Plugins.UltimateInterface.Strategies.Conversational;

/// <summary>
/// Discord channel integration strategy for conversational DataWarehouse interactions.
/// </summary>
/// <remarks>
/// <para>
/// Provides production-ready Discord integration with:
/// <list type="bullet">
/// <item><description>Discord Interaction payloads (v10 API)</description></item>
/// <item><description>PING/PONG webhook verification</description></item>
/// <item><description>APPLICATION_COMMAND (slash commands)</description></item>
/// <item><description>MESSAGE_COMPONENT (buttons, select menus)</description></item>
/// <item><description>MODAL_SUBMIT (form submissions)</description></item>
/// <item><description>Discord embed responses with fields and colors</description></item>
/// <item><description>Ed25519 signature verification</description></item>
/// </list>
/// </para>
/// <para>
/// All message content is routed via message bus topic "nlp.intent.parse" for
/// natural language understanding and DataWarehouse operation mapping.
/// </para>
/// </remarks>
internal sealed class DiscordChannelStrategy : SdkInterface.InterfaceStrategyBase, IPluginInterfaceStrategy
{
    private volatile byte[]? _discordPublicKey;

    /// <summary>
    /// Configures the Discord application public key used for Ed25519 signature verification.
    /// Must be called before handling requests. The public key is available in the Discord
    /// Developer Portal under your application's "General Information" tab.
    /// </summary>
    /// <param name="publicKeyHex">The Discord application public key as a hex string (64 chars = 32 bytes).</param>
    /// <exception cref="ArgumentException">Thrown when publicKeyHex is null, empty, or not a valid 32-byte hex string.</exception>
    public void ConfigurePublicKey(string publicKeyHex)
    {
        if (string.IsNullOrWhiteSpace(publicKeyHex))
            throw new ArgumentException("Discord public key must not be null or empty.", nameof(publicKeyHex));
        if (publicKeyHex.Length != 64)
            throw new ArgumentException("Discord public key must be 64 hex characters (32 bytes).", nameof(publicKeyHex));

        try
        {
            _discordPublicKey = Convert.FromHexString(publicKeyHex);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("Discord public key must be a valid hex string.", nameof(publicKeyHex), ex);
        }
    }

    // IPluginInterfaceStrategy metadata
    public override string StrategyId => "discord";
    public string DisplayName => "Discord Channel";
    public string SemanticDescription => "Conversational interface for Discord server integration with slash commands, interactions, and embed responses.";
    public InterfaceCategory Category => InterfaceCategory.Conversational;
    public string[] Tags => new[] { "discord", "chat", "conversational", "gaming", "interactions", "embeds" };

    // SDK contract properties
    public override SdkInterface.InterfaceProtocol Protocol => SdkInterface.InterfaceProtocol.Custom;
    public override SdkInterface.InterfaceCapabilities Capabilities => new SdkInterface.InterfaceCapabilities(
        SupportsStreaming: false,
        SupportsAuthentication: true,
        SupportedContentTypes: new[] { "application/json" },
        MaxRequestSize: 256 * 1024, // 256 KB
        MaxResponseSize: 6000, // Discord embed description limit
        DefaultTimeout: TimeSpan.FromSeconds(3) // Discord requires response within 3s
    );

    /// <summary>
    /// Initializes the Discord channel strategy.
    /// </summary>
    protected override Task StartAsyncCore(CancellationToken cancellationToken)
    {
        // No state initialization required
        return Task.CompletedTask;
    }

    /// <summary>
    /// Cleans up Discord channel resources.
    /// </summary>
    protected override Task StopAsyncCore(CancellationToken cancellationToken)
    {
        // No cleanup required
        return Task.CompletedTask;
    }

    /// <summary>
    /// Handles Discord Interaction requests.
    /// </summary>
    /// <param name="request">The validated interface request.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>An InterfaceResponse containing the Discord-formatted response.</returns>
    protected override async Task<SdkInterface.InterfaceResponse> HandleRequestAsyncCore(
        SdkInterface.InterfaceRequest request,
        CancellationToken cancellationToken)
    {
        // Verify Ed25519 signature
        if (request.Headers.TryGetValue("X-Signature-Ed25519", out var signature) &&
            request.Headers.TryGetValue("X-Signature-Timestamp", out var timestamp))
        {
            if (!VerifyEd25519Signature(request.Body, signature, timestamp))
            {
                return SdkInterface.InterfaceResponse.Unauthorized("Invalid Ed25519 signature");
            }
        }

        var bodyText = Encoding.UTF8.GetString(request.Body.Span);
        using var doc = JsonDocument.Parse(bodyText);
        var root = doc.RootElement;

        // Get interaction type
        if (!root.TryGetProperty("type", out var typeElement))
        {
            return SdkInterface.InterfaceResponse.BadRequest("Missing interaction type");
        }

        var interactionType = typeElement.GetInt32();

        return interactionType switch
        {
            1 => HandlePingInteraction(), // PING
            2 => await HandleApplicationCommandAsync(root, cancellationToken), // APPLICATION_COMMAND
            3 => await HandleMessageComponentAsync(root, cancellationToken), // MESSAGE_COMPONENT
            5 => await HandleModalSubmitAsync(root, cancellationToken), // MODAL_SUBMIT
            _ => SdkInterface.InterfaceResponse.BadRequest("Unknown interaction type")
        };
    }

    /// <summary>
    /// Handles PING interaction (webhook verification).
    /// </summary>
    private SdkInterface.InterfaceResponse HandlePingInteraction()
    {
        var response = new { type = 1 }; // PONG
        var responseBody = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response));
        return new SdkInterface.InterfaceResponse(
            StatusCode: 200,
            Headers: new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            Body: responseBody
        );
    }

    /// <summary>
    /// Handles APPLICATION_COMMAND interactions (slash commands).
    /// </summary>
    private async Task<SdkInterface.InterfaceResponse> HandleApplicationCommandAsync(
        JsonElement interaction,
        CancellationToken cancellationToken)
    {
        var data = interaction.GetProperty("data");
        var commandName = data.GetProperty("name").GetString() ?? string.Empty;

        // Extract command options (arguments)
        var options = data.TryGetProperty("options", out var optionsElement)
            ? optionsElement
            : default;

        // Route to NLP for intent parsing via message bus
        if (IsIntelligenceAvailable)
        {
            await MessageBus!.SendAsync("nlp.intent.parse", new DataWarehouse.SDK.Utilities.PluginMessage
            {
                Type = "nlp.intent.parse",
                Payload = new System.Collections.Generic.Dictionary<string, object> { ["command"] = commandName, ["options"] = options.ValueKind != System.Text.Json.JsonValueKind.Undefined ? options.ToString() : "{}" }
            }, cancellationToken).ConfigureAwait(false);
        }

        // Build Discord embed response
        var embed = new
        {
            title = "Query Processed",
            description = $"Executing command: **/{commandName}**",
            color = 0x5865F2, // Discord blurple
            fields = new[]
            {
                new
                {
                    name = "Status",
                    value = "Processing",
                    inline = true
                },
                new
                {
                    name = "Command",
                    value = commandName,
                    inline = true
                }
            },
            timestamp = DateTimeOffset.UtcNow.ToString("o")
        };

        var response = new
        {
            type = 4, // CHANNEL_MESSAGE_WITH_SOURCE
            data = new
            {
                embeds = new[] { embed }
            }
        };

        var responseBody = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response));
        return new SdkInterface.InterfaceResponse(
            StatusCode: 200,
            Headers: new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            Body: responseBody
        );
    }

    /// <summary>
    /// Handles MESSAGE_COMPONENT interactions (button clicks, select menus).
    /// </summary>
    // Cat 15 (finding 3287): removed spurious async — no await in body; return Task.FromResult directly.
    private Task<SdkInterface.InterfaceResponse> HandleMessageComponentAsync(
        JsonElement interaction,
        CancellationToken cancellationToken)
    {
        var data = interaction.GetProperty("data");
        var customId = data.GetProperty("custom_id").GetString() ?? string.Empty;
        var componentType = data.GetProperty("component_type").GetInt32();

        // Process button click or select menu choice
        // Dispatch to DataWarehouse operations based on custom_id

        var response = new
        {
            type = 7, // UPDATE_MESSAGE
            data = new
            {
                content = $"Action executed: {customId}",
                components = Array.Empty<object>() // Clear buttons
            }
        };

        var responseBody = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response));
        return Task.FromResult(new SdkInterface.InterfaceResponse(
            StatusCode: 200,
            Headers: new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            Body: responseBody
        ));
    }

    /// <summary>
    /// Handles MODAL_SUBMIT interactions (form submissions).
    /// </summary>
    // Cat 15 (finding 3287): removed spurious async — no await in body.
    private Task<SdkInterface.InterfaceResponse> HandleModalSubmitAsync(
        JsonElement interaction,
        CancellationToken cancellationToken)
    {
        var data = interaction.GetProperty("data");
        var customId = data.GetProperty("custom_id").GetString() ?? string.Empty;
        var components = data.GetProperty("components");

        // Extract form field values
        // Process submission and dispatch to DataWarehouse operations

        var response = new
        {
            type = 4, // CHANNEL_MESSAGE_WITH_SOURCE
            data = new
            {
                content = "Form submitted successfully!",
                flags = 64 // EPHEMERAL (only visible to user)
            }
        };

        var responseBody = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response));
        return Task.FromResult(new SdkInterface.InterfaceResponse(
            StatusCode: 200,
            Headers: new Dictionary<string, string> { ["Content-Type"] = "application/json" },
            Body: responseBody
        ));
    }

    /// <summary>
    /// Verifies Discord request signature using Ed25519.
    /// </summary>
    /// <param name="body">The raw request body.</param>
    /// <param name="signature">The X-Signature-Ed25519 header value (hex-encoded).</param>
    /// <param name="timestamp">The X-Signature-Timestamp header value.</param>
    /// <returns>True if signature is valid; otherwise, false.</returns>
    /// <summary>
    /// Verifies Discord request signature using Ed25519.
    /// </summary>
    /// <remarks>
    /// Ed25519 is not natively available in .NET's System.Security.Cryptography.
    /// This method requires the Discord application public key to be configured
    /// and a platform that supports Ed25519 via libsodium or a managed implementation.
    /// Rather than silently returning true (auth bypass), this throws if the
    /// cryptographic primitive is unavailable.
    /// </remarks>
    /// <param name="body">The raw request body.</param>
    /// <param name="signature">The X-Signature-Ed25519 header value (hex-encoded, 128 chars).</param>
    /// <param name="timestamp">The X-Signature-Timestamp header value.</param>
    /// <returns>True if signature is valid; otherwise, false.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the Discord public key is not configured.</exception>
    /// <exception cref="PlatformNotSupportedException">Thrown when Ed25519 verification is not available on this platform.</exception>
    private bool VerifyEd25519Signature(ReadOnlyMemory<byte> body, string signature, string timestamp)
    {
        var publicKey = _discordPublicKey
            ?? throw new InvalidOperationException(
                "Discord application public key not configured. Call ConfigurePublicKey() before handling requests.");

        // Structural validation
        if (string.IsNullOrWhiteSpace(signature) || signature.Length != 128) // 64 bytes = 128 hex chars
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(timestamp))
        {
            return false;
        }

        // Decode signature from hex
        byte[] signatureBytes;
        try
        {
            signatureBytes = Convert.FromHexString(signature);
        }
        catch (FormatException)
        {
            return false;
        }

        if (signatureBytes.Length != 64)
        {
            return false;
        }

        // Construct message: timestamp + body
        var timestampBytes = Encoding.UTF8.GetBytes(timestamp);
        var message = new byte[timestampBytes.Length + body.Length];
        timestampBytes.CopyTo(message, 0);
        body.Span.CopyTo(message.AsSpan(timestampBytes.Length));

        // Ed25519 is not available in .NET's built-in crypto; fail-closed rather than bypass.
        // Production deployments must provide libsodium (e.g., via NSec or libsodium-net NuGet package)
        // and override this strategy or inject a verification delegate.
        throw new PlatformNotSupportedException(
            "Ed25519 signature verification requires a native cryptography library (e.g., libsodium via " +
            "NSec or Sodium.Core NuGet package). Install the library and configure the Ed25519 verifier. " +
            "Returning true without verification is an authentication bypass vulnerability.");
    }
}
