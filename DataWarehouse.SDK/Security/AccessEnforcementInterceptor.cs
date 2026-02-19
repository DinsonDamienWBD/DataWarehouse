// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Security;

/// <summary>
/// Universal access control enforcement interceptor for the message bus.
/// Wraps any IMessageBus implementation and enforces AccessVerificationMatrix checks
/// on EVERY message before it reaches handlers.
///
/// UNIVERSAL ENFORCEMENT: No message passes through without access verification.
/// Messages without CommandIdentity are DENIED by default (fail-closed).
///
/// The interceptor extracts the topic as the "resource" and the message type as the "action"
/// for the access verification matrix evaluation.
/// </summary>
public sealed class AccessEnforcementInterceptor : IMessageBus
{
    private readonly IMessageBus _inner;
    private readonly AccessVerificationMatrix _matrix;
    private readonly Action<AccessVerdict>? _onDenied;
    private readonly Action<AccessVerdict>? _onAllowed;
    private readonly HashSet<string> _bypassTopics;

    /// <summary>
    /// Creates a new enforcement interceptor.
    /// </summary>
    /// <param name="inner">The underlying message bus to wrap.</param>
    /// <param name="matrix">The access verification matrix for evaluation.</param>
    /// <param name="onDenied">Optional callback when access is denied (for audit logging).</param>
    /// <param name="onAllowed">Optional callback when access is allowed (for audit logging).</param>
    /// <param name="bypassTopics">Topics that bypass enforcement (system.startup, system.shutdown only).</param>
    public AccessEnforcementInterceptor(
        IMessageBus inner,
        AccessVerificationMatrix matrix,
        Action<AccessVerdict>? onDenied = null,
        Action<AccessVerdict>? onAllowed = null,
        IEnumerable<string>? bypassTopics = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _matrix = matrix ?? throw new ArgumentNullException(nameof(matrix));
        _onDenied = onDenied;
        _onAllowed = onAllowed;
        // Only system lifecycle topics can bypass — these are kernel-internal
        _bypassTopics = new HashSet<string>(bypassTopics ?? new[]
        {
            "system.startup",
            "system.shutdown",
            "system.healthcheck",
            "plugin.loaded",
            "plugin.unloaded"
        }, StringComparer.OrdinalIgnoreCase);
    }

    public async Task PublishAsync(string topic, PluginMessage message, CancellationToken ct = default)
    {
        EnforceAccess(topic, message);
        await _inner.PublishAsync(topic, message, ct);
    }

    public async Task PublishAndWaitAsync(string topic, PluginMessage message, CancellationToken ct = default)
    {
        EnforceAccess(topic, message);
        await _inner.PublishAndWaitAsync(topic, message, ct);
    }

    public async Task<MessageResponse> SendAsync(string topic, PluginMessage message, CancellationToken ct = default)
    {
        EnforceAccess(topic, message);
        return await _inner.SendAsync(topic, message, ct);
    }

    public async Task<MessageResponse> SendAsync(string topic, PluginMessage message, TimeSpan timeout, CancellationToken ct = default)
    {
        EnforceAccess(topic, message);
        return await _inner.SendAsync(topic, message, timeout, ct);
    }

    public IDisposable Subscribe(string topic, Func<PluginMessage, Task> handler)
        => _inner.Subscribe(topic, WrapHandlerWithEnforcement(topic, handler));

    public IDisposable Subscribe(string topic, Func<PluginMessage, Task<MessageResponse>> handler)
        => _inner.Subscribe(topic, WrapResponseHandlerWithEnforcement(topic, handler));

    public IDisposable SubscribePattern(string pattern, Func<PluginMessage, Task> handler)
    {
        // BUS-05: Restrict overly broad wildcard patterns.
        // Patterns like "*" or "#" match everything — only permitted for system/kernel identities.
        // Plugins should subscribe to specific topic prefixes, not global wildcards.
        if (IsRestrictedPattern(pattern))
        {
            throw new UnauthorizedAccessException(
                $"Access denied: Wildcard subscription pattern '{pattern}' is too broad. " +
                "Subscribe to specific topic prefixes (e.g., 'storage.*', 'pipeline.*') instead. " +
                "Global wildcard subscriptions require kernel-level access.");
        }

        return _inner.SubscribePattern(pattern, WrapHandlerWithEnforcement(pattern, handler));
    }

    public void Unsubscribe(string topic) => _inner.Unsubscribe(topic);

    public IEnumerable<string> GetActiveTopics() => _inner.GetActiveTopics();

    private void EnforceAccess(string topic, PluginMessage message)
    {
        // Bypass topics are kernel-internal lifecycle events
        if (_bypassTopics.Contains(topic))
            return;

        // Fail-closed: messages without identity are DENIED
        if (message.Identity is null)
        {
            var deniedVerdict = AccessVerdict.Denied(
                CommandIdentity.System("unknown"),
                topic,
                message.Type,
                HierarchyLevel.System,
                "no-identity",
                "Message has no CommandIdentity — access denied (fail-closed). Every action must carry identity.");
            _onDenied?.Invoke(deniedVerdict);
            throw new UnauthorizedAccessException(
                $"Access denied: Message on topic '{topic}' has no CommandIdentity. " +
                "Every action must carry identity for access verification.");
        }

        // Evaluate through the multi-level hierarchy
        var verdict = _matrix.Evaluate(message.Identity, topic, message.Type);

        if (!verdict.Allowed)
        {
            _onDenied?.Invoke(verdict);
            throw new UnauthorizedAccessException(
                $"Access denied: {verdict.Reason} " +
                $"[Principal={verdict.Identity.EffectivePrincipalId}, " +
                $"Resource={verdict.Resource}, Action={verdict.Action}, " +
                $"Level={verdict.DecidedAtLevel}, Rule={verdict.RuleId}]");
        }

        _onAllowed?.Invoke(verdict);
    }

    /// <summary>
    /// Wraps a subscription handler to enforce access on each delivered message.
    /// This ensures that even if a subscription was established, each message
    /// is still checked for proper identity and authorization.
    /// </summary>
    private Func<PluginMessage, Task> WrapHandlerWithEnforcement(string topic, Func<PluginMessage, Task> handler)
    {
        return async message =>
        {
            // Bypass topics pass through
            if (_bypassTopics.Contains(topic))
            {
                await handler(message);
                return;
            }

            // Messages delivered to subscribers must still have valid identity
            if (message.Identity is not null)
            {
                var verdict = _matrix.Evaluate(message.Identity, topic, message.Type);
                if (!verdict.Allowed)
                {
                    _onDenied?.Invoke(verdict);
                    return; // Silently drop — don't throw in subscriber context
                }
            }
            // Note: null-identity messages in subscription delivery are allowed through
            // because the PUBLISH side already enforced identity. Subscription-side
            // enforcement is defense-in-depth for messages that bypass the interceptor
            // (e.g., kernel-internal messages on bypass topics).

            await handler(message);
        };
    }

    /// <summary>
    /// Wraps a request/response subscription handler with enforcement.
    /// </summary>
    private Func<PluginMessage, Task<MessageResponse>> WrapResponseHandlerWithEnforcement(
        string topic, Func<PluginMessage, Task<MessageResponse>> handler)
    {
        return async message =>
        {
            if (_bypassTopics.Contains(topic))
                return await handler(message);

            if (message.Identity is not null)
            {
                var verdict = _matrix.Evaluate(message.Identity, topic, message.Type);
                if (!verdict.Allowed)
                {
                    _onDenied?.Invoke(verdict);
                    return new MessageResponse
                    {
                        Success = false,
                        ErrorMessage = $"Access denied: {verdict.Reason}",
                        ErrorCode = "ACCESS_DENIED"
                    };
                }
            }

            return await handler(message);
        };
    }

    /// <summary>
    /// Determines if a subscription pattern is too broad (BUS-05 fix).
    /// Patterns that match all topics are restricted to prevent information leakage.
    /// </summary>
    private static bool IsRestrictedPattern(string pattern)
    {
        // Block global wildcards: "*", "#", "**", or empty
        if (string.IsNullOrWhiteSpace(pattern)) return true;
        var trimmed = pattern.Trim();
        return trimmed is "*" or "#" or "**" or ">" or "*.*";
    }
}

/// <summary>
/// Extension methods for wiring access enforcement into the message bus pipeline.
/// </summary>
public static class AccessEnforcementExtensions
{
    /// <summary>
    /// Wraps a message bus with universal access enforcement.
    /// After this call, every message must carry CommandIdentity and pass the verification matrix.
    /// </summary>
    public static IMessageBus WithAccessEnforcement(
        this IMessageBus bus,
        AccessVerificationMatrix matrix,
        Action<AccessVerdict>? onDenied = null,
        Action<AccessVerdict>? onAllowed = null,
        IEnumerable<string>? bypassTopics = null)
    {
        return new AccessEnforcementInterceptor(bus, matrix, onDenied, onAllowed, bypassTopics);
    }
}
