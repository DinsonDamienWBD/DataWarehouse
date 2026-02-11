using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.Plugins.AppPlatform.Models;
using DataWarehouse.Plugins.AppPlatform.Services;
using DataWarehouse.Plugins.AppPlatform.Strategies;
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
///   <item><c>platform.service.storage</c>: Route request to storage service</item>
///   <item><c>platform.service.accesscontrol</c>: Route request to access control service</item>
///   <item><c>platform.service.intelligence</c>: Route request to intelligence service</item>
///   <item><c>platform.service.observability</c>: Route request to observability service</item>
///   <item><c>platform.service.replication</c>: Route request to replication service</item>
///   <item><c>platform.service.compliance</c>: Route request to compliance service</item>
///   <item><c>platform.policy.bind</c>: Bind a per-app access control policy</item>
///   <item><c>platform.policy.unbind</c>: Unbind a per-app access control policy</item>
///   <item><c>platform.policy.get</c>: Get a per-app access control policy</item>
///   <item><c>platform.policy.evaluate</c>: Evaluate access against a per-app policy</item>
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
    /// Strategy for managing per-app access control policies and binding them to UltimateAccessControl.
    /// </summary>
    private AppAccessPolicyStrategy? _accessPolicyStrategy;

    /// <summary>
    /// Router that validates tokens, checks scopes, verifies app status, and forwards enriched
    /// messages to downstream service plugins.
    /// </summary>
    private AppContextRouter? _contextRouter;

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
        _accessPolicyStrategy = new AppAccessPolicyStrategy(MessageBus!, Id);
        _contextRouter = new AppContextRouter(MessageBus!, Id, _tokenService, _registrationService);
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

        // Service routing topics -- each routes to a downstream service via AppContextRouter
        _subscriptions.Add(MessageBus.Subscribe(PlatformTopics.ServiceStorage, HandleServiceStorageAsync));
        _subscriptions.Add(MessageBus.Subscribe(PlatformTopics.ServiceAccessControl, HandleServiceAccessControlAsync));
        _subscriptions.Add(MessageBus.Subscribe(PlatformTopics.ServiceIntelligence, HandleServiceIntelligenceAsync));
        _subscriptions.Add(MessageBus.Subscribe(PlatformTopics.ServiceObservability, HandleServiceObservabilityAsync));
        _subscriptions.Add(MessageBus.Subscribe(PlatformTopics.ServiceReplication, HandleServiceReplicationAsync));
        _subscriptions.Add(MessageBus.Subscribe(PlatformTopics.ServiceCompliance, HandleServiceComplianceAsync));

        // Policy management topics
        _subscriptions.Add(MessageBus.Subscribe(PlatformTopics.PolicyBind, HandlePolicyBindAsync));
        _subscriptions.Add(MessageBus.Subscribe(PlatformTopics.PolicyUnbind, HandlePolicyUnbindAsync));
        _subscriptions.Add(MessageBus.Subscribe(PlatformTopics.PolicyGet, HandlePolicyGetAsync));
        _subscriptions.Add(MessageBus.Subscribe(PlatformTopics.PolicyEvaluate, HandlePolicyEvaluateAsync));
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
    // Service Routing Handlers
    // ========================================

    /// <summary>
    /// Handles storage service requests by routing through <see cref="AppContextRouter"/>.
    /// Validates token, checks "storage" scope, verifies app status, and forwards enriched message.
    /// </summary>
    /// <param name="message">The incoming message with <c>RawKey</c> in the payload.</param>
    /// <returns>The downstream response or an authentication/authorization error.</returns>
    private async Task<MessageResponse> HandleServiceStorageAsync(PluginMessage message)
    {
        try
        {
            return await _contextRouter!.RouteStorageRequestAsync(message);
        }
        catch (Exception ex)
        {
            return MessageResponse.Error($"Storage routing failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles access control service requests by routing through <see cref="AppContextRouter"/>.
    /// Validates token, checks "accesscontrol" scope, verifies app status, and forwards enriched message.
    /// </summary>
    /// <param name="message">The incoming message with <c>RawKey</c> in the payload.</param>
    /// <returns>The downstream response or an authentication/authorization error.</returns>
    private async Task<MessageResponse> HandleServiceAccessControlAsync(PluginMessage message)
    {
        try
        {
            return await _contextRouter!.RouteAccessControlRequestAsync(message);
        }
        catch (Exception ex)
        {
            return MessageResponse.Error($"Access control routing failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles intelligence (AI) service requests by routing through <see cref="AppContextRouter"/>.
    /// Validates token, checks "intelligence" scope, verifies app status, and forwards enriched message.
    /// </summary>
    /// <param name="message">The incoming message with <c>RawKey</c> in the payload.</param>
    /// <returns>The downstream response or an authentication/authorization error.</returns>
    private async Task<MessageResponse> HandleServiceIntelligenceAsync(PluginMessage message)
    {
        try
        {
            return await _contextRouter!.RouteIntelligenceRequestAsync(message);
        }
        catch (Exception ex)
        {
            return MessageResponse.Error($"Intelligence routing failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles observability service requests by routing through <see cref="AppContextRouter"/>.
    /// Validates token, checks "observability" scope, verifies app status, and forwards enriched message.
    /// </summary>
    /// <param name="message">The incoming message with <c>RawKey</c> in the payload.</param>
    /// <returns>The downstream response or an authentication/authorization error.</returns>
    private async Task<MessageResponse> HandleServiceObservabilityAsync(PluginMessage message)
    {
        try
        {
            return await _contextRouter!.RouteObservabilityRequestAsync(message);
        }
        catch (Exception ex)
        {
            return MessageResponse.Error($"Observability routing failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles replication service requests by routing through <see cref="AppContextRouter"/>.
    /// Validates token, checks "replication" scope, verifies app status, and forwards enriched message.
    /// </summary>
    /// <param name="message">The incoming message with <c>RawKey</c> in the payload.</param>
    /// <returns>The downstream response or an authentication/authorization error.</returns>
    private async Task<MessageResponse> HandleServiceReplicationAsync(PluginMessage message)
    {
        try
        {
            return await _contextRouter!.RouteReplicationRequestAsync(message);
        }
        catch (Exception ex)
        {
            return MessageResponse.Error($"Replication routing failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles compliance service requests by routing through <see cref="AppContextRouter"/>.
    /// Validates token, checks "compliance" scope, verifies app status, and forwards enriched message.
    /// </summary>
    /// <param name="message">The incoming message with <c>RawKey</c> in the payload.</param>
    /// <returns>The downstream response or an authentication/authorization error.</returns>
    private async Task<MessageResponse> HandleServiceComplianceAsync(PluginMessage message)
    {
        try
        {
            return await _contextRouter!.RouteComplianceRequestAsync(message);
        }
        catch (Exception ex)
        {
            return MessageResponse.Error($"Compliance routing failed: {ex.Message}");
        }
    }

    // ========================================
    // Policy Management Handlers
    // ========================================

    /// <summary>
    /// Handles per-app access control policy binding requests.
    /// Extracts AppId, Model, Roles, Attributes, and tenant isolation settings from the payload.
    /// </summary>
    /// <param name="message">The incoming message containing policy details.</param>
    /// <returns>A <see cref="MessageResponse"/> confirming the binding or an error.</returns>
    private async Task<MessageResponse> HandlePolicyBindAsync(PluginMessage message)
    {
        try
        {
            var appId = GetPayloadString(message, "AppId");
            if (appId is null)
                return MessageResponse.Error("Missing required field: AppId");

            var modelStr = GetPayloadString(message, "Model") ?? "RBAC";
            if (!Enum.TryParse<AccessControlModel>(modelStr, true, out var model))
                model = AccessControlModel.RBAC;

            var roles = ExtractRoles(message);
            var attributes = ExtractAttributes(message);

            var enforceTenantIsolation = true;
            if (message.Payload.TryGetValue("EnforceTenantIsolation", out var etiObj))
            {
                enforceTenantIsolation = etiObj switch
                {
                    bool b => b,
                    string s when bool.TryParse(s, out var parsed) => parsed,
                    _ => true
                };
            }

            var allowCrossTenant = false;
            if (message.Payload.TryGetValue("AllowCrossTenantAccess", out var actObj))
            {
                allowCrossTenant = actObj switch
                {
                    bool b => b,
                    string s when bool.TryParse(s, out var parsed) => parsed,
                    _ => false
                };
            }

            var allowedCrossTenantApps = GetPayloadStringArray(message, "AllowedCrossTenantApps") ?? [];

            var policy = new AppAccessPolicy
            {
                AppId = appId,
                PolicyId = Guid.NewGuid().ToString("N"),
                Model = model,
                Roles = roles,
                Attributes = attributes,
                EnforceTenantIsolation = enforceTenantIsolation,
                AllowCrossTenantAccess = allowCrossTenant,
                AllowedCrossTenantApps = allowedCrossTenantApps,
                CreatedAt = DateTime.UtcNow
            };

            var response = await _accessPolicyStrategy!.BindPolicyAsync(policy);
            return MessageResponse.Ok(new { Policy = policy, UpstreamResponse = response });
        }
        catch (Exception ex)
        {
            return MessageResponse.Error($"Policy binding failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles per-app access control policy unbinding requests.
    /// Removes the policy for the specified AppId.
    /// </summary>
    /// <param name="message">The incoming message containing the AppId.</param>
    /// <returns>A <see cref="MessageResponse"/> confirming the unbinding or an error.</returns>
    private async Task<MessageResponse> HandlePolicyUnbindAsync(PluginMessage message)
    {
        try
        {
            var appId = GetPayloadString(message, "AppId");
            if (appId is null)
                return MessageResponse.Error("Missing required field: AppId");

            var response = await _accessPolicyStrategy!.UnbindPolicyAsync(appId);
            return MessageResponse.Ok(new { Unbound = true, AppId = appId, UpstreamResponse = response });
        }
        catch (Exception ex)
        {
            return MessageResponse.Error($"Policy unbinding failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles per-app access control policy retrieval requests.
    /// Returns the current policy for the specified AppId.
    /// </summary>
    /// <param name="message">The incoming message containing the AppId.</param>
    /// <returns>A <see cref="MessageResponse"/> with the <see cref="AppAccessPolicy"/> or an error.</returns>
    private async Task<MessageResponse> HandlePolicyGetAsync(PluginMessage message)
    {
        try
        {
            var appId = GetPayloadString(message, "AppId");
            if (appId is null)
                return MessageResponse.Error("Missing required field: AppId");

            var policy = await _accessPolicyStrategy!.GetPolicyAsync(appId);
            return policy is not null
                ? MessageResponse.Ok(policy)
                : MessageResponse.Error($"No policy found for application: {appId}");
        }
        catch (Exception ex)
        {
            return MessageResponse.Error($"Policy retrieval failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles access control evaluation requests against a per-app policy.
    /// Extracts AppId, UserId, Resource, and Action from the payload and delegates
    /// to <see cref="AppAccessPolicyStrategy.EvaluateAccessAsync"/>.
    /// </summary>
    /// <param name="message">The incoming message containing evaluation details.</param>
    /// <returns>A <see cref="MessageResponse"/> with the allow/deny decision.</returns>
    private async Task<MessageResponse> HandlePolicyEvaluateAsync(PluginMessage message)
    {
        try
        {
            var appId = GetPayloadString(message, "AppId");
            var userId = GetPayloadString(message, "UserId");
            var resource = GetPayloadString(message, "Resource");
            var action = GetPayloadString(message, "Action");

            if (appId is null || userId is null || resource is null || action is null)
                return MessageResponse.Error("Missing required fields: AppId, UserId, Resource, Action");

            return await _accessPolicyStrategy!.EvaluateAccessAsync(appId, userId, resource, action);
        }
        catch (Exception ex)
        {
            return MessageResponse.Error($"Policy evaluation failed: {ex.Message}");
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

    /// <summary>
    /// Extracts an array of <see cref="AppRole"/> from the message payload.
    /// Handles direct <see cref="AppRole"/>[] values and JsonElement deserialization.
    /// </summary>
    /// <param name="message">The message to extract from.</param>
    /// <returns>An array of <see cref="AppRole"/>, or an empty array if not found.</returns>
    private static AppRole[] ExtractRoles(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("Roles", out var value))
            return [];

        if (value is AppRole[] roles)
            return roles;

        if (value is JsonElement je && je.ValueKind == JsonValueKind.Array)
        {
            var result = new List<AppRole>();
            foreach (var item in je.EnumerateArray())
            {
                var roleName = item.TryGetProperty("RoleName", out var rn) ? rn.GetString() : null;
                if (roleName is null) continue;

                var permissions = Array.Empty<string>();
                if (item.TryGetProperty("Permissions", out var perms) && perms.ValueKind == JsonValueKind.Array)
                {
                    permissions = perms.EnumerateArray().Select(p => p.GetString() ?? string.Empty).ToArray();
                }

                var description = item.TryGetProperty("Description", out var desc) ? desc.GetString() : null;

                result.Add(new AppRole
                {
                    RoleName = roleName,
                    Permissions = permissions,
                    Description = description
                });
            }
            return result.ToArray();
        }

        return [];
    }

    /// <summary>
    /// Extracts an array of <see cref="AppAttribute"/> from the message payload.
    /// Handles direct <see cref="AppAttribute"/>[] values and JsonElement deserialization.
    /// </summary>
    /// <param name="message">The message to extract from.</param>
    /// <returns>An array of <see cref="AppAttribute"/>, or an empty array if not found.</returns>
    private static AppAttribute[] ExtractAttributes(PluginMessage message)
    {
        if (!message.Payload.TryGetValue("Attributes", out var value))
            return [];

        if (value is AppAttribute[] attributes)
            return attributes;

        if (value is JsonElement je && je.ValueKind == JsonValueKind.Array)
        {
            var result = new List<AppAttribute>();
            foreach (var item in je.EnumerateArray())
            {
                var attrName = item.TryGetProperty("AttributeName", out var an) ? an.GetString() : null;
                var attrValue = item.TryGetProperty("AttributeValue", out var av) ? av.GetString() : null;
                if (attrName is null || attrValue is null) continue;

                var op = AttributeOperator.Equals;
                if (item.TryGetProperty("Operator", out var opProp) &&
                    opProp.GetString() is string opStr &&
                    Enum.TryParse<AttributeOperator>(opStr, true, out var parsedOp))
                {
                    op = parsedOp;
                }

                result.Add(new AppAttribute
                {
                    AttributeName = attrName,
                    AttributeValue = attrValue,
                    Operator = op
                });
            }
            return result.ToArray();
        }

        return [];
    }
}
