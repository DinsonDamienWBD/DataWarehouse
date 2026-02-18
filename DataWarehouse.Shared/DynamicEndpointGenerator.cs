// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Collections.Concurrent;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.Shared;

/// <summary>
/// Endpoint descriptor representing a dynamically-generated API endpoint from capability registry.
/// </summary>
public sealed record EndpointDescriptor
{
    /// <summary>Unique endpoint identifier (e.g., "encryption.aes-256-gcm").</summary>
    public required string EndpointId { get; init; }

    /// <summary>HTTP path for the endpoint (e.g., "/api/v1/encryption/aes-256-gcm").</summary>
    public required string Path { get; init; }

    /// <summary>HTTP method (GET, POST, PUT, DELETE, etc.).</summary>
    public required string HttpMethod { get; init; }

    /// <summary>Human-readable display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Description of what this endpoint does.</summary>
    public string? Description { get; init; }

    /// <summary>Capability category.</summary>
    public CapabilityCategory Category { get; init; }

    /// <summary>Plugin ID that provides this endpoint.</summary>
    public required string PluginId { get; init; }

    /// <summary>Plugin name for display.</summary>
    public required string PluginName { get; init; }

    /// <summary>JSON schema for request parameters.</summary>
    public string? ParameterSchema { get; init; }

    /// <summary>Tags for discovery.</summary>
    public string[] Tags { get; init; } = Array.Empty<string>();

    /// <summary>Whether this endpoint is currently available (plugin loaded and healthy).</summary>
    public bool IsAvailable { get; init; } = true;

    /// <summary>When this endpoint was registered.</summary>
    public DateTimeOffset RegisteredAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Additional metadata.</summary>
    public IReadOnlyDictionary<string, object> Metadata { get; init; } = new Dictionary<string, object>();
}

/// <summary>
/// Event fired when endpoints change.
/// </summary>
public sealed class EndpointChangeEvent
{
    /// <summary>Type of change (added, removed, updated).</summary>
    public required string ChangeType { get; init; }

    /// <summary>Endpoints affected by this change.</summary>
    public required IReadOnlyList<EndpointDescriptor> Endpoints { get; init; }

    /// <summary>When the change occurred.</summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Generates API endpoints dynamically from the capability register.
/// Subscribes to capability.changed events to add/remove endpoints at runtime.
/// ALL interfaces (REST, gRPC, CLI, GUI) consume this as their single source of truth.
/// </summary>
public sealed class DynamicEndpointGenerator : IDisposable
{
    private readonly ConcurrentDictionary<string, EndpointDescriptor> _endpoints = new(StringComparer.OrdinalIgnoreCase);
    private readonly IPluginCapabilityRegistry? _capabilityRegistry;
    private readonly IDisposable? _registeredSubscription;
    private readonly IDisposable? _unregisteredSubscription;
    private readonly IDisposable? _availabilitySubscription;
    private bool _disposed;

    /// <summary>
    /// Event fired when endpoints change (for live-updating interfaces).
    /// </summary>
    public event Action<EndpointChangeEvent>? OnEndpointChanged;

    /// <summary>
    /// Creates a new DynamicEndpointGenerator.
    /// </summary>
    /// <param name="capabilityRegistry">Optional capability registry to subscribe to.</param>
    public DynamicEndpointGenerator(IPluginCapabilityRegistry? capabilityRegistry = null)
    {
        _capabilityRegistry = capabilityRegistry;

        if (_capabilityRegistry != null)
        {
            // Subscribe to capability events for live updates
            _registeredSubscription = _capabilityRegistry.OnCapabilityRegistered(HandleCapabilityRegistered);
            _unregisteredSubscription = _capabilityRegistry.OnCapabilityUnregistered(HandleCapabilityUnregistered);
            _availabilitySubscription = _capabilityRegistry.OnAvailabilityChanged(HandleAvailabilityChanged);

            // Initial load from registry
            RefreshFromCapabilities(_capabilityRegistry.GetAll());
        }
    }

    /// <summary>
    /// Rebuild endpoints from current capability register state.
    /// Called on startup and on capability.changed events.
    /// </summary>
    public void RefreshFromCapabilities(IEnumerable<RegisteredCapability> capabilities)
    {
        var newEndpoints = new List<EndpointDescriptor>();

        foreach (var capability in capabilities)
        {
            var endpoint = CapabilityToEndpoint(capability);
            if (_endpoints.TryAdd(endpoint.EndpointId, endpoint))
            {
                newEndpoints.Add(endpoint);
            }
            else
            {
                // Update existing endpoint (might have changed availability)
                _endpoints[endpoint.EndpointId] = endpoint;
            }
        }

        if (newEndpoints.Count > 0)
        {
            OnEndpointChanged?.Invoke(new EndpointChangeEvent
            {
                ChangeType = "added",
                Endpoints = newEndpoints
            });
        }
    }

    /// <summary>
    /// Get all currently available endpoints.
    /// </summary>
    public IReadOnlyList<EndpointDescriptor> GetEndpoints()
    {
        return _endpoints.Values.Where(e => e.IsAvailable).ToList();
    }

    /// <summary>
    /// Get endpoints by category.
    /// </summary>
    public IReadOnlyList<EndpointDescriptor> GetEndpointsByCategory(CapabilityCategory category)
    {
        return _endpoints.Values.Where(e => e.Category == category && e.IsAvailable).ToList();
    }

    /// <summary>
    /// Get endpoints by plugin ID.
    /// </summary>
    public IReadOnlyList<EndpointDescriptor> GetEndpointsByPlugin(string pluginId)
    {
        return _endpoints.Values.Where(e => e.PluginId == pluginId && e.IsAvailable).ToList();
    }

    /// <summary>
    /// Check if a specific endpoint exists and is available.
    /// </summary>
    public bool IsEndpointAvailable(string endpointId)
    {
        return _endpoints.TryGetValue(endpointId, out var endpoint) && endpoint.IsAvailable;
    }

    /// <summary>
    /// Get a specific endpoint by ID.
    /// </summary>
    public EndpointDescriptor? GetEndpoint(string endpointId)
    {
        return _endpoints.TryGetValue(endpointId, out var endpoint) ? endpoint : null;
    }

    #region Event Handlers

    private void HandleCapabilityRegistered(RegisteredCapability capability)
    {
        var endpoint = CapabilityToEndpoint(capability);
        _endpoints[endpoint.EndpointId] = endpoint;

        OnEndpointChanged?.Invoke(new EndpointChangeEvent
        {
            ChangeType = "added",
            Endpoints = new[] { endpoint }
        });
    }

    private void HandleCapabilityUnregistered(string capabilityId)
    {
        if (_endpoints.TryRemove(capabilityId, out var endpoint))
        {
            OnEndpointChanged?.Invoke(new EndpointChangeEvent
            {
                ChangeType = "removed",
                Endpoints = new[] { endpoint }
            });
        }
    }

    private void HandleAvailabilityChanged(string capabilityId, bool isAvailable)
    {
        if (_endpoints.TryGetValue(capabilityId, out var endpoint))
        {
            var updated = endpoint with { IsAvailable = isAvailable };
            _endpoints[capabilityId] = updated;

            OnEndpointChanged?.Invoke(new EndpointChangeEvent
            {
                ChangeType = "updated",
                Endpoints = new[] { updated }
            });
        }
    }

    #endregion

    #region Capability to Endpoint Mapping

    /// <summary>
    /// Converts a RegisteredCapability to an EndpointDescriptor.
    /// </summary>
    private static EndpointDescriptor CapabilityToEndpoint(RegisteredCapability capability)
    {
        // Determine HTTP method based on capability name conventions
        var httpMethod = InferHttpMethod(capability.CapabilityId);

        // Generate path from capability ID
        var path = GeneratePathFromCapabilityId(capability.CapabilityId, capability.Category);

        return new EndpointDescriptor
        {
            EndpointId = capability.CapabilityId,
            Path = path,
            HttpMethod = httpMethod,
            DisplayName = capability.DisplayName,
            Description = capability.Description,
            Category = capability.Category,
            PluginId = capability.PluginId,
            PluginName = capability.PluginName,
            ParameterSchema = capability.ParameterSchema,
            Tags = capability.Tags,
            IsAvailable = capability.IsAvailable,
            RegisteredAt = capability.RegisteredAt,
            Metadata = capability.Metadata
        };
    }

    private static string InferHttpMethod(string capabilityId)
    {
        var lower = capabilityId.ToLowerInvariant();

        // Check for CRUD patterns
        if (lower.Contains("create") || lower.Contains("add") || lower.Contains("write") || lower.Contains("insert"))
            return "POST";
        if (lower.Contains("update") || lower.Contains("modify") || lower.Contains("edit") || lower.Contains("patch"))
            return "PUT";
        if (lower.Contains("delete") || lower.Contains("remove") || lower.Contains("destroy"))
            return "DELETE";
        if (lower.Contains("list") || lower.Contains("query") || lower.Contains("search") || lower.Contains("get") || lower.Contains("read"))
            return "GET";

        // Default to POST for actions
        return "POST";
    }

    private static string GeneratePathFromCapabilityId(string capabilityId, CapabilityCategory category)
    {
        // Convert capabilityId to path: "encryption.aes-256-gcm" -> "/api/v1/encryption/aes-256-gcm"
        var parts = capabilityId.Split('.', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
        {
            return $"/api/v1/{category.ToString().ToLowerInvariant()}/unknown";
        }

        var pathParts = parts.Select(p => p.ToLowerInvariant().Replace("_", "-"));
        return $"/api/v1/{string.Join("/", pathParts)}";
    }

    #endregion

    /// <summary>
    /// Disposes resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _registeredSubscription?.Dispose();
        _unregisteredSubscription?.Dispose();
        _availabilitySubscription?.Dispose();
        _endpoints.Clear();
    }
}
