using System.Collections.Concurrent;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.Plugins.AppPlatform.Models;

namespace DataWarehouse.Plugins.AppPlatform.Strategies;

/// <summary>
/// Strategy that manages per-application access control policies and binds them into
/// UltimateAccessControl via the message bus. Each registered application can define
/// its own RBAC, ABAC, MAC, DAC, or PBAC policy rules that are enforced centrally.
/// </summary>
/// <remarks>
/// <para>
/// Policy binding sends the full policy configuration to UltimateAccessControl via the
/// <c>accesscontrol.policy.bind</c> message bus topic. Unbinding removes the policy via
/// <c>accesscontrol.policy.unbind</c>. All communication is through the message bus;
/// no direct plugin references are used.
/// </para>
/// <para>
/// Access evaluation is delegated to UltimateAccessControl via the <c>accesscontrol.evaluate</c>
/// topic, which returns an allow/deny decision based on the bound per-app policy.
/// </para>
/// </remarks>
internal sealed class AppAccessPolicyStrategy
{
    /// <summary>
    /// Thread-safe dictionary storing per-app access policies keyed by AppId.
    /// </summary>
    private readonly ConcurrentDictionary<string, AppAccessPolicy> _policies = new();

    /// <summary>
    /// Message bus for communicating policy operations to UltimateAccessControl.
    /// </summary>
    private readonly IMessageBus _messageBus;

    /// <summary>
    /// Plugin identifier used as the source in outgoing messages.
    /// </summary>
    private readonly string _pluginId;

    /// <summary>
    /// Initializes a new instance of the <see cref="AppAccessPolicyStrategy"/> class.
    /// </summary>
    /// <param name="messageBus">The message bus for inter-plugin communication.</param>
    /// <param name="pluginId">The plugin identifier used as message source.</param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="messageBus"/> or <paramref name="pluginId"/> is <c>null</c>.
    /// </exception>
    public AppAccessPolicyStrategy(IMessageBus messageBus, string pluginId)
    {
        _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        _pluginId = pluginId ?? throw new ArgumentNullException(nameof(pluginId));
    }

    /// <summary>
    /// Binds a per-app access control policy by storing it locally and sending the binding
    /// to UltimateAccessControl via the <c>accesscontrol.policy.bind</c> message bus topic.
    /// </summary>
    /// <param name="policy">The access control policy to bind.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="MessageResponse"/> from UltimateAccessControl confirming the binding.</returns>
    public async Task<MessageResponse> BindPolicyAsync(AppAccessPolicy policy, CancellationToken ct = default)
    {
        _policies[policy.AppId] = policy;

        var response = await _messageBus.SendAsync("accesscontrol.policy.bind", new PluginMessage
        {
            Type = "accesscontrol.policy.bind",
            SourcePluginId = _pluginId,
            Payload = new Dictionary<string, object>
            {
                ["AppId"] = policy.AppId,
                ["PolicyId"] = policy.PolicyId,
                ["Model"] = policy.Model.ToString(),
                ["Roles"] = policy.Roles,
                ["Attributes"] = policy.Attributes,
                ["EnforceTenantIsolation"] = policy.EnforceTenantIsolation,
                ["AllowCrossTenantAccess"] = policy.AllowCrossTenantAccess,
                ["AllowedCrossTenantApps"] = policy.AllowedCrossTenantApps
            }
        }, ct);

        return response;
    }

    /// <summary>
    /// Unbinds a per-app access control policy by removing it locally and sending the unbind
    /// notification to UltimateAccessControl via the <c>accesscontrol.policy.unbind</c> topic.
    /// </summary>
    /// <param name="appId">The application identifier whose policy should be unbound.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="MessageResponse"/> from UltimateAccessControl confirming the unbinding.</returns>
    public async Task<MessageResponse> UnbindPolicyAsync(string appId, CancellationToken ct = default)
    {
        _policies.TryRemove(appId, out _);

        var response = await _messageBus.SendAsync("accesscontrol.policy.unbind", new PluginMessage
        {
            Type = "accesscontrol.policy.unbind",
            SourcePluginId = _pluginId,
            Payload = new Dictionary<string, object>
            {
                ["AppId"] = appId
            }
        }, ct);

        return response;
    }

    /// <summary>
    /// Retrieves the current access control policy for the specified application.
    /// </summary>
    /// <param name="appId">The application identifier to look up.</param>
    /// <returns>The <see cref="AppAccessPolicy"/> if found; <c>null</c> otherwise.</returns>
    public Task<AppAccessPolicy?> GetPolicyAsync(string appId)
    {
        _policies.TryGetValue(appId, out var policy);
        return Task.FromResult(policy);
    }

    /// <summary>
    /// Updates an existing per-app access control policy by replacing it locally
    /// and rebinding the updated policy via the message bus.
    /// </summary>
    /// <param name="updatedPolicy">The updated access control policy.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="MessageResponse"/> from UltimateAccessControl confirming the rebinding.</returns>
    public async Task<MessageResponse> UpdatePolicyAsync(AppAccessPolicy updatedPolicy, CancellationToken ct = default)
    {
        var withTimestamp = updatedPolicy with { UpdatedAt = DateTime.UtcNow };
        _policies[withTimestamp.AppId] = withTimestamp;

        return await BindPolicyAsync(withTimestamp, ct);
    }

    /// <summary>
    /// Evaluates an access control decision for the specified application, user, resource, and action.
    /// Sends the evaluation request to UltimateAccessControl via the <c>accesscontrol.evaluate</c> topic
    /// and returns the allow/deny decision.
    /// </summary>
    /// <param name="appId">The application identifier (used as TenantId for per-app isolation).</param>
    /// <param name="userId">The user requesting access.</param>
    /// <param name="resource">The resource being accessed.</param>
    /// <param name="action">The action being performed on the resource.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The <see cref="MessageResponse"/> with the allow/deny decision from UltimateAccessControl.</returns>
    public async Task<MessageResponse> EvaluateAccessAsync(
        string appId,
        string userId,
        string resource,
        string action,
        CancellationToken ct = default)
    {
        var response = await _messageBus.SendAsync("accesscontrol.evaluate", new PluginMessage
        {
            Type = "accesscontrol.evaluate",
            SourcePluginId = _pluginId,
            Payload = new Dictionary<string, object>
            {
                ["TenantId"] = appId,
                ["UserId"] = userId,
                ["Resource"] = resource,
                ["Action"] = action
            }
        }, ct);

        return response;
    }
}
