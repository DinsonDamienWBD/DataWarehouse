using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.Plugins.AppPlatform.Models;
using DataWarehouse.Plugins.AppPlatform.Services;
using System.Text.Json;

namespace DataWarehouse.Plugins.AppPlatform;

/// <summary>
/// Application Platform Services plugin for DataWarehouse.
/// Enables registered applications to consume DW services with per-app isolation,
/// service tokens for authentication, and integration with the access control system.
///
/// <para>
/// This plugin subscribes to all <c>platform.*</c> message bus topics and delegates
/// to <see cref="AppRegistrationService"/> for app lifecycle management and
/// <see cref="ServiceTokenService"/> for token operations.
/// </para>
///
/// <para>
/// App registration automatically provisions a corresponding tenant in UltimateAccessControl
/// via message bus, establishing per-app isolation without direct plugin references.
/// </para>
///
/// <para>
/// Message Commands:
/// <list type="bullet">
///   <item><c>platform.register</c>: Register a new application</item>
///   <item><c>platform.deregister</c>: Deregister an application</item>
///   <item><c>platform.get</c>: Get an application by ID</item>
///   <item><c>platform.list</c>: List all active applications</item>
///   <item><c>platform.update</c>: Update an application</item>
///   <item><c>platform.token.create</c>: Create a service token</item>
///   <item><c>platform.token.rotate</c>: Rotate a service token</item>
///   <item><c>platform.token.revoke</c>: Revoke a service token</item>
///   <item><c>platform.token.validate</c>: Validate a service token</item>
/// </list>
/// </para>
/// </summary>
public sealed class AppPlatformPlugin : IntelligenceAwarePluginBase, IDisposable
{
    /// <summary>
    /// Service for managing application registrations and tenant provisioning.
    /// </summary>
    private AppRegistrationService? _registrationService;

    /// <summary>
    /// Service for managing service token lifecycle (create, validate, rotate, revoke).
    /// </summary>
    private ServiceTokenService? _tokenService;

    /// <summary>
    /// List of message bus subscription handles for cleanup on disposal.
    /// </summary>
    private readonly List<IDisposable> _subscriptions = new();

    /// <summary>
    /// Whether this instance has been disposed.
    /// </summary>
    private bool _disposed;

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.platform.app";

    /// <inheritdoc/>
    public override string Name => "Application Platform Services";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    /// <summary>
    /// Initializes the plugin when Intelligence (T90) is available.
    /// Calls the common service initialization routine.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    protected override Task OnStartWithIntelligenceAsync(CancellationToken ct)
    {
        InitializeServices();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Initializes the plugin when Intelligence (T90) is not available.
    /// Calls the common service initialization routine.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    protected override Task OnStartWithoutIntelligenceAsync(CancellationToken ct)
    {
        InitializeServices();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates service instances and subscribes to all platform message bus topics.
    /// </summary>
    private void InitializeServices()
    {
        _registrationService = new AppRegistrationService(MessageBus!, Id);
        _tokenService = new ServiceTokenService();
        SubscribeToPlatformTopics();
    }

    /// <summary>
    /// Subscribes to all platform message bus topics using the response-capable overload.
    /// Each subscription is stored for cleanup on disposal.
    /// </summary>
    private void SubscribeToPlatformTopics()
    {
        if (MessageBus is null) return;

        // App lifecycle topics
        _subscriptions.Add(MessageBus.Subscribe(PlatformTopics.AppRegister, HandleAppRegisterAsync));
        _subscriptions.Add(MessageBus.Subscribe(PlatformTopics.AppDeregister, HandleAppDeregisterAsync));
        _subscriptions.Add(MessageBus.Subscribe(PlatformTopics.AppGet, HandleAppGetAsync));
        _subscriptions.Add(MessageBus.Subscribe(PlatformTopics.AppList, HandleAppListAsync));
        _subscriptions.Add(MessageBus.Subscribe(PlatformTopics.AppUpdate, HandleAppUpdateAsync));

        // Token management topics
        _subscriptions.Add(MessageBus.Subscribe(PlatformTopics.TokenCreate, HandleTokenCreateAsync));
        _subscriptions.Add(MessageBus.Subscribe(PlatformTopics.TokenRotate, HandleTokenRotateAsync));
        _subscriptions.Add(MessageBus.Subscribe(PlatformTopics.TokenRevoke, HandleTokenRevokeAsync));
        _subscriptions.Add(MessageBus.Subscribe(PlatformTopics.TokenValidate, HandleTokenValidateAsync));
    }

    // ========================================
    // App Lifecycle Handlers
    // ========================================

    /// <summary>
    /// Handles application registration requests.
    /// Extracts AppName, OwnerUserId, CallbackUrls, and optional ServiceConfig from the message payload.
    /// </summary>
    /// <param name="message">The incoming message containing registration details.</param>
    /// <returns>A <see cref="MessageResponse"/> with the created <see cref="AppRegistration"/>.</returns>
    private async Task<MessageResponse> HandleAppRegisterAsync(PluginMessage message)
    {
        try
        {
            var appName = GetPayloadString(message, "AppName");
            var ownerUserId = GetPayloadString(message, "OwnerUserId");
            var callbackUrls = GetPayloadStringArray(message, "CallbackUrls");

            if (appName is null || ownerUserId is null || callbackUrls is null)
                return MessageResponse.Error("Missing required fields: AppName, OwnerUserId, CallbackUrls");

            AppServiceConfig? config = null;
            if (message.Payload.TryGetValue("ServiceConfig", out var configObj) && configObj is AppServiceConfig svcConfig)
            {
                config = svcConfig;
            }

            var registration = await _registrationService!.RegisterAppAsync(appName, ownerUserId, callbackUrls, config);
            return MessageResponse.Ok(registration);
        }
        catch (Exception ex)
        {
            return MessageResponse.Error($"Registration failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles application deregistration requests.
    /// Extracts AppId from the message payload and marks the app as deleted.
    /// </summary>
    /// <param name="message">The incoming message containing the AppId.</param>
    /// <returns>A <see cref="MessageResponse"/> indicating success or failure.</returns>
    private async Task<MessageResponse> HandleAppDeregisterAsync(PluginMessage message)
    {
        try
        {
            var appId = GetPayloadString(message, "AppId");
            if (appId is null)
                return MessageResponse.Error("Missing required field: AppId");

            var success = await _registrationService!.DeregisterAppAsync(appId);
            return success
                ? MessageResponse.Ok(new { Deregistered = true, AppId = appId })
                : MessageResponse.Error($"Application not found: {appId}");
        }
        catch (Exception ex)
        {
            return MessageResponse.Error($"Deregistration failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles application lookup requests by AppId.
    /// </summary>
    /// <param name="message">The incoming message containing the AppId.</param>
    /// <returns>A <see cref="MessageResponse"/> with the <see cref="AppRegistration"/> or an error.</returns>
    private async Task<MessageResponse> HandleAppGetAsync(PluginMessage message)
    {
        try
        {
            var appId = GetPayloadString(message, "AppId");
            if (appId is null)
                return MessageResponse.Error("Missing required field: AppId");

            var app = await _registrationService!.GetAppAsync(appId);
            return app is not null
                ? MessageResponse.Ok(app)
                : MessageResponse.Error($"Application not found: {appId}");
        }
        catch (Exception ex)
        {
            return MessageResponse.Error($"Get app failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles listing all active application registrations.
    /// </summary>
    /// <param name="message">The incoming message (payload unused).</param>
    /// <returns>A <see cref="MessageResponse"/> with an array of active <see cref="AppRegistration"/> entries.</returns>
    private async Task<MessageResponse> HandleAppListAsync(PluginMessage message)
    {
        try
        {
            var apps = await _registrationService!.ListAppsAsync();
            return MessageResponse.Ok(apps);
        }
        catch (Exception ex)
        {
            return MessageResponse.Error($"List apps failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles application update requests.
    /// Extracts AppId and optional DisplayName, Description, ServiceConfig from the payload.
    /// </summary>
    /// <param name="message">The incoming message containing update details.</param>
    /// <returns>A <see cref="MessageResponse"/> with the updated <see cref="AppRegistration"/> or an error.</returns>
    private async Task<MessageResponse> HandleAppUpdateAsync(PluginMessage message)
    {
        try
        {
            var appId = GetPayloadString(message, "AppId");
            if (appId is null)
                return MessageResponse.Error("Missing required field: AppId");

            var displayName = GetPayloadString(message, "DisplayName");
            var description = GetPayloadString(message, "Description");

            AppServiceConfig? config = null;
            if (message.Payload.TryGetValue("ServiceConfig", out var configObj) && configObj is AppServiceConfig svcConfig)
            {
                config = svcConfig;
            }

            var updated = await _registrationService!.UpdateAppAsync(appId, displayName, description, config);
            return updated is not null
                ? MessageResponse.Ok(updated)
                : MessageResponse.Error($"Application not found: {appId}");
        }
        catch (Exception ex)
        {
            return MessageResponse.Error($"Update app failed: {ex.Message}");
        }
    }

    // ========================================
    // Token Management Handlers
    // ========================================

    /// <summary>
    /// Handles service token creation requests.
    /// Extracts AppId, Scopes, and ValidityMinutes from the payload.
    /// Returns the raw key (exactly once) and the token metadata.
    /// </summary>
    /// <param name="message">The incoming message containing token creation details.</param>
    /// <returns>A <see cref="MessageResponse"/> with RawKey and Token.</returns>
    private async Task<MessageResponse> HandleTokenCreateAsync(PluginMessage message)
    {
        try
        {
            var appId = GetPayloadString(message, "AppId");
            var scopes = GetPayloadStringArray(message, "Scopes");

            if (appId is null || scopes is null)
                return MessageResponse.Error("Missing required fields: AppId, Scopes");

            var validityMinutes = 60; // Default 1 hour
            if (message.Payload.TryGetValue("ValidityMinutes", out var validityObj))
            {
                validityMinutes = validityObj switch
                {
                    int i => i,
                    long l => (int)l,
                    double d => (int)d,
                    string s when int.TryParse(s, out var parsed) => parsed,
                    _ => validityMinutes
                };
            }

            var (rawKey, token) = await _tokenService!.CreateTokenAsync(
                appId,
                scopes,
                TimeSpan.FromMinutes(validityMinutes));

            return MessageResponse.Ok(new { RawKey = rawKey, Token = token });
        }
        catch (Exception ex)
        {
            return MessageResponse.Error($"Token creation failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles service token rotation requests.
    /// Revokes the old token and creates a new one with the same or updated scopes.
    /// </summary>
    /// <param name="message">The incoming message containing rotation details.</param>
    /// <returns>A <see cref="MessageResponse"/> with the new RawKey and Token, or an error.</returns>
    private async Task<MessageResponse> HandleTokenRotateAsync(PluginMessage message)
    {
        try
        {
            var appId = GetPayloadString(message, "AppId");
            var oldRawKey = GetPayloadString(message, "OldRawKey");
            var scopes = GetPayloadStringArray(message, "Scopes");

            if (appId is null || oldRawKey is null || scopes is null)
                return MessageResponse.Error("Missing required fields: AppId, OldRawKey, Scopes");

            var validityMinutes = 60;
            if (message.Payload.TryGetValue("ValidityMinutes", out var validityObj))
            {
                validityMinutes = validityObj switch
                {
                    int i => i,
                    long l => (int)l,
                    double d => (int)d,
                    string s when int.TryParse(s, out var parsed) => parsed,
                    _ => validityMinutes
                };
            }

            var result = await _tokenService!.RotateTokenAsync(
                appId,
                oldRawKey,
                scopes,
                TimeSpan.FromMinutes(validityMinutes));

            if (result is null)
                return MessageResponse.Error("Token rotation failed: old token is invalid");

            var (rawKey, token) = result.Value;
            return MessageResponse.Ok(new { RawKey = rawKey, Token = token });
        }
        catch (Exception ex)
        {
            return MessageResponse.Error($"Token rotation failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles service token revocation requests.
    /// Extracts TokenId and Reason from the payload.
    /// </summary>
    /// <param name="message">The incoming message containing revocation details.</param>
    /// <returns>A <see cref="MessageResponse"/> indicating success or failure.</returns>
    private async Task<MessageResponse> HandleTokenRevokeAsync(PluginMessage message)
    {
        try
        {
            var tokenId = GetPayloadString(message, "TokenId");
            var reason = GetPayloadString(message, "Reason") ?? "Revoked by request";

            if (tokenId is null)
                return MessageResponse.Error("Missing required field: TokenId");

            var success = await _tokenService!.RevokeTokenAsync(tokenId, reason);
            return success
                ? MessageResponse.Ok(new { Revoked = true, TokenId = tokenId })
                : MessageResponse.Error($"Token not found: {tokenId}");
        }
        catch (Exception ex)
        {
            return MessageResponse.Error($"Token revocation failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles service token validation requests.
    /// Extracts RawKey from the payload and returns the validation result.
    /// </summary>
    /// <param name="message">The incoming message containing the raw key to validate.</param>
    /// <returns>A <see cref="MessageResponse"/> with the <see cref="TokenValidationResult"/>.</returns>
    private async Task<MessageResponse> HandleTokenValidateAsync(PluginMessage message)
    {
        try
        {
            var rawKey = GetPayloadString(message, "RawKey");
            if (rawKey is null)
                return MessageResponse.Error("Missing required field: RawKey");

            var result = await _tokenService!.ValidateTokenAsync(rawKey);
            return MessageResponse.Ok(result);
        }
        catch (Exception ex)
        {
            return MessageResponse.Error($"Token validation failed: {ex.Message}");
        }
    }

    // ========================================
    // Lifecycle Management
    // ========================================

    /// <summary>
    /// Stops the plugin by disposing all message bus subscriptions and cleaning up resources.
    /// </summary>
    /// <returns>A task representing the asynchronous stop operation.</returns>
    public override async Task StopAsync()
    {
        DisposeSubscriptions();
        await base.StopAsync();
    }

    /// <summary>
    /// Disposes the plugin and all held resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeSubscriptions();
    }

    /// <summary>
    /// Disposes all message bus subscription handles.
    /// </summary>
    private void DisposeSubscriptions()
    {
        foreach (var subscription in _subscriptions)
        {
            try { subscription.Dispose(); } catch { }
        }
        _subscriptions.Clear();
    }

    // ========================================
    // Payload Extraction Helpers
    // ========================================

    /// <summary>
    /// Extracts a string value from the message payload by key.
    /// Handles both direct string values and JsonElement deserialization.
    /// </summary>
    /// <param name="message">The message to extract from.</param>
    /// <param name="key">The payload key.</param>
    /// <returns>The string value, or <c>null</c> if not found or not a string.</returns>
    private static string? GetPayloadString(PluginMessage message, string key)
    {
        if (!message.Payload.TryGetValue(key, out var value))
            return null;

        return value switch
        {
            string s => s,
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            _ => value.ToString()
        };
    }

    /// <summary>
    /// Extracts a string array from the message payload by key.
    /// Handles direct string arrays, object arrays, and JsonElement deserialization.
    /// </summary>
    /// <param name="message">The message to extract from.</param>
    /// <param name="key">The payload key.</param>
    /// <returns>The string array, or <c>null</c> if not found or not convertible.</returns>
    private static string[]? GetPayloadStringArray(PluginMessage message, string key)
    {
        if (!message.Payload.TryGetValue(key, out var value))
            return null;

        return value switch
        {
            string[] arr => arr,
            object[] objs => objs.Select(o => o?.ToString() ?? string.Empty).ToArray(),
            JsonElement je when je.ValueKind == JsonValueKind.Array =>
                je.EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray(),
            _ => null
        };
    }
}
