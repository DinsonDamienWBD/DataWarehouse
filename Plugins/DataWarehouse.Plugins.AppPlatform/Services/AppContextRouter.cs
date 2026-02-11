using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.Plugins.AppPlatform.Models;

namespace DataWarehouse.Plugins.AppPlatform.Services;

/// <summary>
/// Request router that validates service tokens, resolves application context,
/// and forwards enriched messages to downstream service plugins.
/// </summary>
/// <remarks>
/// <para>
/// Every incoming service request passes through the <see cref="AppContextRouter"/>.
/// The router performs three validation steps before forwarding:
/// <list type="number">
///   <item>Token validation: verifies the raw key maps to a valid, non-revoked, non-expired token.</item>
///   <item>Scope check: verifies the token has the scope required for the target service.</item>
///   <item>App status check: verifies the owning application is active.</item>
/// </list>
/// </para>
/// <para>
/// After validation, the router creates an <see cref="AppRequestContext"/> and enriches
/// the forwarded message with <c>AppId</c>, <c>AppContext</c>, and <c>ServiceTokenId</c>
/// in the payload so that downstream plugins can enforce per-app policies.
/// </para>
/// </remarks>
internal sealed class AppContextRouter
{
    /// <summary>
    /// Message bus for forwarding enriched messages to downstream service plugins.
    /// </summary>
    private readonly IMessageBus _messageBus;

    /// <summary>
    /// Plugin identifier used as the source in forwarded messages.
    /// </summary>
    private readonly string _pluginId;

    /// <summary>
    /// Service for validating service tokens.
    /// </summary>
    private readonly ServiceTokenService _tokenService;

    /// <summary>
    /// Service for retrieving application registrations and verifying app status.
    /// </summary>
    private readonly AppRegistrationService _registrationService;

    /// <summary>
    /// Initializes a new instance of the <see cref="AppContextRouter"/> class.
    /// </summary>
    /// <param name="messageBus">The message bus for inter-plugin communication.</param>
    /// <param name="pluginId">The plugin identifier used as message source.</param>
    /// <param name="tokenService">The service for token validation.</param>
    /// <param name="registrationService">The service for app registration lookups.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when any parameter is <c>null</c>.
    /// </exception>
    public AppContextRouter(
        IMessageBus messageBus,
        string pluginId,
        ServiceTokenService tokenService,
        AppRegistrationService registrationService)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _pluginId = pluginId ?? throw new ArgumentNullException(nameof(pluginId));
        _tokenService = tokenService ?? throw new ArgumentNullException(nameof(tokenService));
        _registrationService = registrationService ?? throw new ArgumentNullException(nameof(registrationService));
    }

    /// <summary>
    /// Routes a service request by validating the token, checking scope and app status,
    /// enriching the message with <see cref="AppRequestContext"/>, and forwarding to the
    /// target service topic.
    /// </summary>
    /// <param name="message">The incoming service request message. Must contain <c>RawKey</c> in the payload.</param>
    /// <param name="targetServiceTopic">The message bus topic of the target downstream service.</param>
    /// <param name="requiredScope">The scope required for the target service (e.g., "storage", "intelligence").</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="MessageResponse"/> from the downstream service, or an error response
    /// if authentication, scope check, or app status validation fails.
    /// </returns>
    public async Task<MessageResponse> RouteServiceRequestAsync(
        PluginMessage message,
        string targetServiceTopic,
        string requiredScope,
        CancellationToken ct = default)
    {
        // Step 1: Extract and validate the service token
        if (!message.Payload.TryGetValue("RawKey", out var rawKeyObj) || rawKeyObj is not string rawKey)
        {
            return MessageResponse.Error("Missing required field: RawKey", "AUTH_FAILED");
        }

        var validation = await _tokenService.ValidateTokenAsync(rawKey);
        if (!validation.IsValid)
        {
            return MessageResponse.Error(
                $"Invalid or expired service token: {validation.FailureReason}",
                "AUTH_FAILED");
        }

        // Step 2: Check scope authorization
        if (!validation.AllowedScopes.Contains(requiredScope, StringComparer.OrdinalIgnoreCase))
        {
            return MessageResponse.Error(
                $"Token does not have scope for this service: {requiredScope}",
                "SCOPE_DENIED");
        }

        // Step 3: Verify app exists and is active
        var app = await _registrationService.GetAppAsync(validation.AppId!);
        if (app is null || app.Status != AppStatus.Active)
        {
            return MessageResponse.Error(
                "Application is not active",
                "APP_INACTIVE");
        }

        // Step 4: Create AppRequestContext
        var context = new AppRequestContext
        {
            AppId = validation.AppId!,
            TokenId = validation.TokenId!,
            AllowedScopes = validation.AllowedScopes,
            RequestTimestamp = DateTime.UtcNow
        };

        // Step 5: Enrich and forward the message
        var enrichedPayload = new Dictionary<string, object>(message.Payload)
        {
            ["AppId"] = validation.AppId!,
            ["AppContext"] = context.ToDictionary(),
            ["ServiceTokenId"] = validation.TokenId!
        };

        var enrichedMessage = new PluginMessage
        {
            Type = message.Type,
            SourcePluginId = _pluginId,
            Payload = enrichedPayload,
            CorrelationId = message.CorrelationId
        };

        // Step 6: Forward to downstream service and return its response
        return await _messageBus.SendAsync(targetServiceTopic, enrichedMessage, ct);
    }

    /// <summary>
    /// Routes a request to the storage service after validating the "storage" scope.
    /// </summary>
    /// <param name="message">The incoming message with <c>RawKey</c> in the payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The downstream response or an authentication/authorization error.</returns>
    public Task<MessageResponse> RouteStorageRequestAsync(PluginMessage message, CancellationToken ct = default)
        => RouteServiceRequestAsync(message, "storage.request", "storage", ct);

    /// <summary>
    /// Routes a request to the access control service after validating the "accesscontrol" scope.
    /// </summary>
    /// <param name="message">The incoming message with <c>RawKey</c> in the payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The downstream response or an authentication/authorization error.</returns>
    public Task<MessageResponse> RouteAccessControlRequestAsync(PluginMessage message, CancellationToken ct = default)
        => RouteServiceRequestAsync(message, "accesscontrol.request", "accesscontrol", ct);

    /// <summary>
    /// Routes a request to the intelligence (AI) service after validating the "intelligence" scope.
    /// </summary>
    /// <param name="message">The incoming message with <c>RawKey</c> in the payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The downstream response or an authentication/authorization error.</returns>
    public Task<MessageResponse> RouteIntelligenceRequestAsync(PluginMessage message, CancellationToken ct = default)
        => RouteServiceRequestAsync(message, "intelligence.request", "intelligence", ct);

    /// <summary>
    /// Routes a request to the observability service after validating the "observability" scope.
    /// </summary>
    /// <param name="message">The incoming message with <c>RawKey</c> in the payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The downstream response or an authentication/authorization error.</returns>
    public Task<MessageResponse> RouteObservabilityRequestAsync(PluginMessage message, CancellationToken ct = default)
        => RouteServiceRequestAsync(message, "observability.request", "observability", ct);

    /// <summary>
    /// Routes a request to the replication service after validating the "replication" scope.
    /// </summary>
    /// <param name="message">The incoming message with <c>RawKey</c> in the payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The downstream response or an authentication/authorization error.</returns>
    public Task<MessageResponse> RouteReplicationRequestAsync(PluginMessage message, CancellationToken ct = default)
        => RouteServiceRequestAsync(message, "replication.request", "replication", ct);

    /// <summary>
    /// Routes a request to the compliance service after validating the "compliance" scope.
    /// </summary>
    /// <param name="message">The incoming message with <c>RawKey</c> in the payload.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The downstream response or an authentication/authorization error.</returns>
    public Task<MessageResponse> RouteComplianceRequestAsync(PluginMessage message, CancellationToken ct = default)
        => RouteServiceRequestAsync(message, "compliance.request", "compliance", ct);
}
