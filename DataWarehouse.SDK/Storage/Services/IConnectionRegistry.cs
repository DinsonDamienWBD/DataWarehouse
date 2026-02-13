using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Storage.Services;

/// <summary>
/// Composable connection registry service (AD-03).
/// Manages multiple storage backend connections, health monitoring, and instance lifecycle.
/// Extracted from HybridStoragePluginBase to enable composition without inheritance.
/// </summary>
/// <typeparam name="TConfig">The configuration type for connections.</typeparam>
public interface IConnectionRegistry<TConfig> where TConfig : class
{
    /// <summary>Registers a new storage backend connection.</summary>
    /// <param name="name">A unique name for this connection.</param>
    /// <param name="config">The connection configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The generated connection ID.</returns>
    Task<string> RegisterAsync(string name, TConfig config, CancellationToken ct = default);

    /// <summary>Unregisters and disconnects a storage backend.</summary>
    /// <param name="connectionId">The connection ID to unregister.</param>
    /// <param name="ct">Cancellation token.</param>
    Task UnregisterAsync(string connectionId, CancellationToken ct = default);

    /// <summary>Gets the configuration for a registered connection.</summary>
    /// <param name="connectionId">The connection ID.</param>
    /// <returns>The configuration, or null if not found.</returns>
    TConfig? GetConfig(string connectionId);

    /// <summary>Lists all registered connection IDs.</summary>
    /// <returns>A read-only list of connection IDs.</returns>
    IReadOnlyList<string> GetRegisteredIds();

    /// <summary>Gets health status for a specific connection.</summary>
    /// <param name="connectionId">The connection ID to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The health status of the connection.</returns>
    Task<ConnectionHealth> GetHealthAsync(string connectionId, CancellationToken ct = default);

    /// <summary>Gets health status for all connections.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Health status keyed by connection ID.</returns>
    Task<IReadOnlyDictionary<string, ConnectionHealth>> GetAllHealthAsync(CancellationToken ct = default);
}

/// <summary>
/// Health status information for a storage connection.
/// </summary>
public record ConnectionHealth
{
    /// <summary>The connection ID.</summary>
    public string ConnectionId { get; init; } = string.Empty;

    /// <summary>Whether the connection is healthy and accepting operations.</summary>
    public bool IsHealthy { get; init; }

    /// <summary>The latency of the last health check.</summary>
    public TimeSpan Latency { get; init; }

    /// <summary>Error message if the connection is unhealthy.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>When the health was last checked.</summary>
    public DateTimeOffset LastChecked { get; init; }
}
