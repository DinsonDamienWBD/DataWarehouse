using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Federation.Routing;
using DataWarehouse.SDK.Utilities;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Federation.Authorization;

/// <summary>
/// Decorator for <see cref="IStorageRouter"/> that performs pre-flight ACL checks before routing requests.
/// </summary>
/// <remarks>
/// <para>
/// PermissionAwareRouter implements the deny-early pattern: access control checks happen at the
/// routing layer before requests reach storage nodes. This prevents unauthorized requests from
/// consuming storage resources and provides a single enforcement point for ACL policies.
/// </para>
/// <para>
/// <strong>Architecture:</strong> PermissionAwareRouter wraps an inner <see cref="IStorageRouter"/>
/// using the decorator pattern. On each routing request:
/// </para>
/// <list type="number">
///   <item><description>Check if request has a user ID (skip ACL check if not, per configuration)</description></item>
///   <item><description>Try to retrieve cached permission from <see cref="IPermissionCache"/></description></item>
///   <item><description>On cache miss, call UltimateAccessControl via message bus topic "accesscontrol.check"</description></item>
///   <item><description>Cache the fresh ACL result</description></item>
///   <item><description>If permission denied, log to "logging.security.denied" and return 403 immediately</description></item>
///   <item><description>If permission granted, delegate to inner router</description></item>
/// </list>
/// <para>
/// <strong>Message Bus Integration:</strong> ACL checks use synchronous request-response pattern
/// via <see cref="IMessageBus.SendAsync"/>. Request payload: { userId, resource, operation, metadata }.
/// Expected response: { allowed: bool, reason: string }.
/// </para>
/// <para>
/// <strong>Fail-Secure Default:</strong> If the ACL check throws an exception (message bus timeout,
/// UltimateAccessControl unavailable, etc.), permission is DENIED. This prevents authorization
/// bypass via error injection.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Permission-aware routing with deny-early pattern (FOS-03)")]
public sealed class PermissionAwareRouter : IStorageRouter
{
    private readonly IStorageRouter _innerRouter;
    private readonly IMessageBus _messageBus;
    private readonly IPermissionCache _cache;
    private readonly PermissionRouterConfiguration _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="PermissionAwareRouter"/> class.
    /// </summary>
    /// <param name="innerRouter">The inner router to delegate to after ACL checks pass.</param>
    /// <param name="messageBus">The message bus for communicating with UltimateAccessControl.</param>
    /// <param name="cache">
    /// Optional permission cache. If null, creates a new <see cref="InMemoryPermissionCache"/> with defaults.
    /// </param>
    /// <param name="config">
    /// Optional configuration. If null, uses <see cref="PermissionRouterConfiguration"/> defaults.
    /// </param>
    public PermissionAwareRouter(
        IStorageRouter innerRouter,
        IMessageBus messageBus,
        IPermissionCache? cache = null,
        PermissionRouterConfiguration? config = null)
    {
        _innerRouter = innerRouter;
        _messageBus = messageBus;
        _cache = cache ?? new InMemoryPermissionCache();
        _config = config ?? new PermissionRouterConfiguration();
    }

    /// <inheritdoc/>
    public async Task<StorageResponse> RouteRequestAsync(StorageRequest request, CancellationToken ct = default)
    {
        // Skip permission check if no user ID
        if (string.IsNullOrEmpty(request.UserId))
        {
            if (_config.RequireUserId)
            {
                return new StorageResponse
                {
                    Success = false,
                    NodeId = "router",
                    ErrorMessage = "Permission check failed: UserId required but not provided"
                };
            }

            // No user ID and not required -- allow through
            return await _innerRouter.RouteRequestAsync(request, ct).ConfigureAwait(false);
        }

        // Pre-flight permission check
        var permissionCheck = await CheckPermissionAsync(request, ct).ConfigureAwait(false);

        if (!permissionCheck.Granted)
        {
            // Log denial
            await LogPermissionDenialAsync(request, permissionCheck, ct).ConfigureAwait(false);

            return new StorageResponse
            {
                Success = false,
                NodeId = "router",
                ErrorMessage = $"Permission denied: {permissionCheck.Reason}"
            };
        }

        // Permission granted -- route to inner router
        return await _innerRouter.RouteRequestAsync(request, ct).ConfigureAwait(false);
    }

    private async Task<PermissionCheckResult> CheckPermissionAsync(StorageRequest request, CancellationToken ct)
    {
        var resourceKey = request.Address.ToKey();
        var operation = request.Operation.ToString().ToLowerInvariant();

        // Try cache first
        if (_cache.TryGet(request.UserId!, resourceKey, operation, out var cachedResult))
        {
            return cachedResult;
        }

        // Cache miss -- call UltimateAccessControl via message bus
        var checkMessage = new PluginMessage
        {
            MessageId = Guid.NewGuid().ToString(),
            Timestamp = DateTime.UtcNow,
            Payload = new Dictionary<string, object>
            {
                ["userId"] = request.UserId!,
                ["resource"] = resourceKey,
                ["operation"] = operation,
                ["metadata"] = request.Metadata ?? new Dictionary<string, string>()
            }
        };

        try
        {
            var response = await _messageBus.SendAsync(
                "accesscontrol.check",
                checkMessage,
                ct
            ).ConfigureAwait(false);

            bool granted;
            string reason;

            if (response.Success && response.Payload != null)
            {
                // Parse payload (could be dynamic object, dictionary, or JSON element)
                granted = TryExtractBool(response.Payload, "allowed", out var allowedValue) && allowedValue;
                reason = TryExtractString(response.Payload, "reason", out var reasonValue)
                    ? reasonValue
                    : (granted ? "Access granted" : "Access denied");
            }
            else
            {
                // Response indicates failure -- deny by default (fail-secure)
                granted = false;
                reason = response.ErrorMessage ?? "Permission check failed: no response from access control";
            }

            // Cache the result
            _cache.Set(request.UserId!, resourceKey, operation, granted, _config.CacheTtl);

            return new PermissionCheckResult
            {
                Granted = granted,
                Reason = reason,
                FromCache = false
            };
        }
        catch (Exception ex)
        {
            // On error, default to deny (fail-secure)
            return PermissionCheckResult.Denied($"Permission check failed: {ex.Message}");
        }
    }

    private async Task LogPermissionDenialAsync(StorageRequest request, PermissionCheckResult check, CancellationToken ct)
    {
        var logMessage = new PluginMessage
        {
            MessageId = Guid.NewGuid().ToString(),
            Timestamp = DateTime.UtcNow,
            Payload = new Dictionary<string, object>
            {
                ["level"] = "Warning",
                ["category"] = "Federation.Authorization",
                ["message"] = "Permission denied at router",
                ["userId"] = request.UserId ?? string.Empty,
                ["resource"] = request.Address.ToKey(),
                ["operation"] = request.Operation.ToString(),
                ["reason"] = check.Reason,
                ["fromCache"] = check.FromCache,
                ["timestampUtc"] = DateTime.UtcNow
            }
        };

        try
        {
            await _messageBus.PublishAsync("logging.security.denied", logMessage, ct).ConfigureAwait(false);
        }
        catch
        {
            // Logging failure should not block routing
        }
    }

    /// <summary>
    /// Gets cache statistics for observability.
    /// </summary>
    /// <returns>Current permission cache statistics including hit rate.</returns>
    public PermissionCacheStatistics GetCacheStatistics() => _cache.GetStatistics();

    // Helper methods to extract values from dynamic payloads
    private static bool TryExtractBool(object payload, string key, out bool value)
    {
        value = false;
        try
        {
            if (payload is IDictionary<string, object> dict && dict.TryGetValue(key, out var objValue))
            {
                if (objValue is bool boolValue)
                {
                    value = boolValue;
                    return true;
                }
                if (objValue is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.True)
                {
                    value = true;
                    return true;
                }
                if (objValue is JsonElement jsonElement2 && jsonElement2.ValueKind == JsonValueKind.False)
                {
                    value = false;
                    return true;
                }
            }

            // Try reflection for anonymous types
            var propInfo = payload.GetType().GetProperty(key);
            if (propInfo != null && propInfo.PropertyType == typeof(bool))
            {
                value = (bool)propInfo.GetValue(payload)!;
                return true;
            }
        }
        catch
        {
            // Ignore extraction errors
        }
        return false;
    }

    private static bool TryExtractString(object payload, string key, out string value)
    {
        value = string.Empty;
        try
        {
            if (payload is IDictionary<string, object> dict && dict.TryGetValue(key, out var objValue))
            {
                if (objValue is string strValue)
                {
                    value = strValue;
                    return true;
                }
                if (objValue is JsonElement jsonElement && jsonElement.ValueKind == JsonValueKind.String)
                {
                    value = jsonElement.GetString() ?? string.Empty;
                    return true;
                }
            }

            // Try reflection for anonymous types
            var propInfo = payload.GetType().GetProperty(key);
            if (propInfo != null && propInfo.PropertyType == typeof(string))
            {
                value = (string?)propInfo.GetValue(payload) ?? string.Empty;
                return true;
            }
        }
        catch
        {
            // Ignore extraction errors
        }
        return false;
    }
}

/// <summary>
/// Configuration for <see cref="PermissionAwareRouter"/>.
/// </summary>
/// <remarks>
/// Controls whether user IDs are required for all requests and how long ACL results are cached.
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 34: Permission router configuration (FOS-03)")]
public sealed record PermissionRouterConfiguration
{
    /// <summary>
    /// Gets whether all storage requests must include a user ID.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Default: false. When false, requests without a user ID bypass ACL checks and are routed
    /// to the inner router directly. When true, requests without a user ID are rejected with
    /// "Permission denied: UserId required".
    /// </para>
    /// <para>
    /// Set this to true in environments where all access must be authenticated and attributed.
    /// </para>
    /// </remarks>
    public bool RequireUserId { get; init; } = false;

    /// <summary>
    /// Gets the time-to-live for cached permission check results.
    /// </summary>
    /// <remarks>
    /// Default: 5 minutes. Shorter TTLs improve security (faster propagation of ACL changes)
    /// at the cost of more message bus round-trips. Longer TTLs improve performance but delay
    /// ACL updates.
    /// </remarks>
    public TimeSpan CacheTtl { get; init; } = TimeSpan.FromMinutes(5);
}
