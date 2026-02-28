using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Storage.Services;

/// <summary>
/// Default in-memory connection registry implementation (AD-03).
/// Manages registration, unregistration, and health tracking for storage backend connections.
/// Extracted from HybridStoragePluginBase logic.
/// </summary>
/// <typeparam name="TConfig">The configuration type for connections.</typeparam>
public sealed class DefaultConnectionRegistry<TConfig> : IConnectionRegistry<TConfig> where TConfig : class
{
    private readonly BoundedDictionary<string, RegistrationEntry> _connections = new BoundedDictionary<string, RegistrationEntry>(1000);
    private readonly BoundedDictionary<string, ConnectionHealth> _healthCache = new BoundedDictionary<string, ConnectionHealth>(1000);
    private readonly Func<string, TConfig, CancellationToken, Task<bool>>? _healthChecker;

    /// <summary>
    /// Creates a new DefaultConnectionRegistry.
    /// </summary>
    /// <param name="healthChecker">
    /// Optional health check function that takes (connectionId, config, ct) and returns true if healthy.
    /// If not provided, all connections report as healthy.
    /// </param>
    public DefaultConnectionRegistry(Func<string, TConfig, CancellationToken, Task<bool>>? healthChecker = null)
    {
        _healthChecker = healthChecker;
    }

    /// <inheritdoc/>
    public Task<string> RegisterAsync(string name, TConfig config, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(config);

        var connectionId = $"{name}-{Guid.NewGuid():N}"[..Math.Min(name.Length + 33, 64)];

        _connections[connectionId] = new RegistrationEntry
        {
            Name = name,
            Config = config,
            RegisteredAt = DateTimeOffset.UtcNow
        };

        _healthCache[connectionId] = new ConnectionHealth
        {
            ConnectionId = connectionId,
            IsHealthy = true,
            Latency = TimeSpan.Zero,
            LastChecked = DateTimeOffset.UtcNow
        };

        return Task.FromResult(connectionId);
    }

    /// <inheritdoc/>
    public Task UnregisterAsync(string connectionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        _connections.TryRemove(connectionId, out _);
        _healthCache.TryRemove(connectionId, out _);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public TConfig? GetConfig(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        return _connections.TryGetValue(connectionId, out var entry) ? entry.Config : null;
    }

    /// <inheritdoc/>
    public IReadOnlyList<string> GetRegisteredIds()
    {
        // Single allocation: Array.AsReadOnly wraps without an extra copy
        return Array.AsReadOnly(_connections.Keys.ToArray());
    }

    /// <inheritdoc/>
    public async Task<ConnectionHealth> GetHealthAsync(string connectionId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);

        if (!_connections.TryGetValue(connectionId, out var entry))
        {
            return new ConnectionHealth
            {
                ConnectionId = connectionId,
                IsHealthy = false,
                ErrorMessage = "Connection not registered",
                LastChecked = DateTimeOffset.UtcNow
            };
        }

        if (_healthChecker != null)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var isHealthy = await _healthChecker(connectionId, entry.Config, ct);
                sw.Stop();

                var health = new ConnectionHealth
                {
                    ConnectionId = connectionId,
                    IsHealthy = isHealthy,
                    Latency = sw.Elapsed,
                    ErrorMessage = isHealthy ? null : "Health check returned false",
                    LastChecked = DateTimeOffset.UtcNow
                };

                _healthCache[connectionId] = health;
                return health;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sw.Stop();
                var health = new ConnectionHealth
                {
                    ConnectionId = connectionId,
                    IsHealthy = false,
                    Latency = sw.Elapsed,
                    ErrorMessage = ex.Message,
                    LastChecked = DateTimeOffset.UtcNow
                };

                _healthCache[connectionId] = health;
                return health;
            }
        }

        // No health checker: return cached or default healthy
        if (_healthCache.TryGetValue(connectionId, out var cached))
            return cached;

        return new ConnectionHealth
        {
            ConnectionId = connectionId,
            IsHealthy = true,
            Latency = TimeSpan.Zero,
            LastChecked = DateTimeOffset.UtcNow
        };
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, ConnectionHealth>> GetAllHealthAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var results = new Dictionary<string, ConnectionHealth>();

        foreach (var connectionId in _connections.Keys)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                results[connectionId] = await GetHealthAsync(connectionId, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                results[connectionId] = new ConnectionHealth
                {
                    ConnectionId = connectionId,
                    IsHealthy = false,
                    ErrorMessage = ex.Message,
                    LastChecked = DateTimeOffset.UtcNow
                };
            }
        }

        return results;
    }

    private record RegistrationEntry
    {
        public string Name { get; init; } = string.Empty;
        public required TConfig Config { get; init; }
        public DateTimeOffset RegisteredAt { get; init; }
    }
}
