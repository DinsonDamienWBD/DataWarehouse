using System.Collections.Concurrent;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.Plugins.AppPlatform.Models;

namespace DataWarehouse.Plugins.AppPlatform.Services;

/// <summary>
/// Manages application registration lifecycle within the DataWarehouse platform.
/// Provides thread-safe CRUD operations for app registrations and provisions
/// corresponding tenants in the access control system via message bus.
/// </summary>
internal sealed class AppRegistrationService
{
    /// <summary>
    /// Thread-safe dictionary storing all application registrations keyed by AppId.
    /// </summary>
    private readonly ConcurrentDictionary<string, AppRegistration> _registrations = new();

    /// <summary>
    /// Message bus for cross-plugin communication, used to provision tenants in UltimateAccessControl.
    /// </summary>
    private readonly IMessageBus _messageBus;

    /// <summary>
    /// Plugin identifier used as the source in outgoing messages.
    /// </summary>
    private readonly string _pluginId;

    /// <summary>
    /// Initializes a new instance of the <see cref="AppRegistrationService"/> class.
    /// </summary>
    /// <param name="messageBus">The message bus for inter-plugin communication.</param>
    /// <param name="pluginId">The plugin identifier used as message source.</param>
    public AppRegistrationService(IMessageBus messageBus, string pluginId)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _pluginId = pluginId ?? throw new ArgumentNullException(nameof(pluginId));
    }

    /// <summary>
    /// Registers a new application on the platform and provisions a corresponding tenant
    /// in the UltimateAccessControl system via message bus.
    /// </summary>
    /// <param name="appName">The short name of the application.</param>
    /// <param name="ownerUserId">The identifier of the user registering the application.</param>
    /// <param name="callbackUrls">URLs for platform-to-app notifications.</param>
    /// <param name="config">Optional service configuration; defaults are applied if null.</param>
    /// <returns>The newly created <see cref="AppRegistration"/>.</returns>
    public async Task<AppRegistration> RegisterAppAsync(
        string appName,
        string ownerUserId,
        string[] callbackUrls,
        AppServiceConfig? config = null)
    {
        var appId = Guid.NewGuid().ToString("N");
        var serviceConfig = config ?? new AppServiceConfig();

        var registration = new AppRegistration
        {
            AppId = appId,
            AppName = appName,
            OwnerUserId = ownerUserId,
            CallbackUrls = callbackUrls,
            Status = AppStatus.Active,
            CreatedAt = DateTime.UtcNow,
            ServiceConfig = serviceConfig
        };

        _registrations[appId] = registration;

        // Provision a corresponding tenant in UltimateAccessControl via message bus
        await _messageBus.SendAsync("accesscontrol.tenant.register", new PluginMessage
        {
            Type = "accesscontrol.tenant.register",
            SourcePluginId = _pluginId,
            Payload = new Dictionary<string, object>
            {
                ["TenantId"] = appId,
                ["TenantName"] = appName,
                ["MaxUsers"] = serviceConfig.MaxUsers,
                ["MaxResources"] = serviceConfig.MaxResources,
                ["MaxStorageBytes"] = serviceConfig.MaxStorageBytes,
                ["IsolationLevel"] = "Strict"
            }
        });

        return registration;
    }

    /// <summary>
    /// Deregisters an application by marking it as deleted and notifying the access control system
    /// to deprovision the corresponding tenant.
    /// </summary>
    /// <param name="appId">The identifier of the application to deregister.</param>
    /// <returns><c>true</c> if the application was found and deregistered; <c>false</c> otherwise.</returns>
    public async Task<bool> DeregisterAppAsync(string appId)
    {
        if (!_registrations.TryGetValue(appId, out var existing))
            return false;

        var updated = existing with
        {
            Status = AppStatus.Deleted,
            UpdatedAt = DateTime.UtcNow
        };

        _registrations[appId] = updated;

        // Notify UltimateAccessControl to deprovision the tenant
        await _messageBus.SendAsync("accesscontrol.tenant.deregister", new PluginMessage
        {
            Type = "accesscontrol.tenant.deregister",
            SourcePluginId = _pluginId,
            Payload = new Dictionary<string, object>
            {
                ["TenantId"] = appId
            }
        });

        return true;
    }

    /// <summary>
    /// Retrieves an application registration by its identifier.
    /// </summary>
    /// <param name="appId">The identifier of the application to retrieve.</param>
    /// <returns>The <see cref="AppRegistration"/> if found; <c>null</c> otherwise.</returns>
    public Task<AppRegistration?> GetAppAsync(string appId)
    {
        _registrations.TryGetValue(appId, out var registration);
        return Task.FromResult(registration);
    }

    /// <summary>
    /// Lists all application registrations that have an active status.
    /// </summary>
    /// <returns>An array of all active <see cref="AppRegistration"/> entries.</returns>
    public Task<AppRegistration[]> ListAppsAsync()
    {
        var active = _registrations.Values
            .Where(r => r.Status == AppStatus.Active)
            .ToArray();

        return Task.FromResult(active);
    }

    /// <summary>
    /// Updates mutable fields of an existing application registration.
    /// </summary>
    /// <param name="appId">The identifier of the application to update.</param>
    /// <param name="displayName">Optional new display name.</param>
    /// <param name="description">Optional new description.</param>
    /// <param name="config">Optional new service configuration.</param>
    /// <returns>The updated <see cref="AppRegistration"/> if found; <c>null</c> otherwise.</returns>
    public Task<AppRegistration?> UpdateAppAsync(
        string appId,
        string? displayName = null,
        string? description = null,
        AppServiceConfig? config = null)
    {
        if (!_registrations.TryGetValue(appId, out var existing))
            return Task.FromResult<AppRegistration?>(null);

        var updated = existing with
        {
            DisplayName = displayName ?? existing.DisplayName,
            Description = description ?? existing.Description,
            ServiceConfig = config ?? existing.ServiceConfig,
            UpdatedAt = DateTime.UtcNow
        };

        _registrations[appId] = updated;
        return Task.FromResult<AppRegistration?>(updated);
    }

    /// <summary>
    /// Suspends an application, preventing it from consuming platform services.
    /// </summary>
    /// <param name="appId">The identifier of the application to suspend.</param>
    /// <param name="reason">The reason for suspension.</param>
    /// <returns>The suspended <see cref="AppRegistration"/> if found; <c>null</c> otherwise.</returns>
    public Task<AppRegistration?> SuspendAppAsync(string appId, string reason)
    {
        if (!_registrations.TryGetValue(appId, out var existing))
            return Task.FromResult<AppRegistration?>(null);

        var updated = existing with
        {
            Status = AppStatus.Suspended,
            SuspendedAt = DateTime.UtcNow,
            SuspensionReason = reason,
            UpdatedAt = DateTime.UtcNow
        };

        _registrations[appId] = updated;
        return Task.FromResult<AppRegistration?>(updated);
    }

    /// <summary>
    /// Reactivates a previously suspended application, clearing suspension fields.
    /// </summary>
    /// <param name="appId">The identifier of the application to reactivate.</param>
    /// <returns>The reactivated <see cref="AppRegistration"/> if found; <c>null</c> otherwise.</returns>
    public Task<AppRegistration?> ReactivateAppAsync(string appId)
    {
        if (!_registrations.TryGetValue(appId, out var existing))
            return Task.FromResult<AppRegistration?>(null);

        var updated = existing with
        {
            Status = AppStatus.Active,
            SuspendedAt = null,
            SuspensionReason = null,
            UpdatedAt = DateTime.UtcNow
        };

        _registrations[appId] = updated;
        return Task.FromResult<AppRegistration?>(updated);
    }
}
