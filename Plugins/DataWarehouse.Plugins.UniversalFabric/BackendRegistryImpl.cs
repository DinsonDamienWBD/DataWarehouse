using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Storage.Fabric;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

using IStorageStrategy = DataWarehouse.SDK.Contracts.Storage.IStorageStrategy;
using StorageTier = DataWarehouse.SDK.Contracts.Storage.StorageTier;

namespace DataWarehouse.Plugins.UniversalFabric;

/// <summary>
/// Thread-safe implementation of <see cref="IBackendRegistry"/> that maintains
/// a registry of storage backends indexed by ID, with support for lookup by
/// tag, tier, and capability requirements.
/// </summary>
public sealed class BackendRegistryImpl : IBackendRegistry
{
    private readonly ConcurrentDictionary<string, (BackendDescriptor Descriptor, IStorageStrategy Strategy)> _backends = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public event Action<BackendDescriptor, bool>? BackendChanged;

    /// <inheritdoc/>
    public void Register(BackendDescriptor descriptor, IStorageStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNullOrEmpty(descriptor.BackendId);

        var added = !_backends.ContainsKey(descriptor.BackendId);
        _backends[descriptor.BackendId] = (descriptor, strategy);

        BackendChanged?.Invoke(descriptor, true);
    }

    /// <inheritdoc/>
    public bool Unregister(string backendId)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(backendId);

        if (_backends.TryRemove(backendId, out var entry))
        {
            BackendChanged?.Invoke(entry.Descriptor, false);
            return true;
        }
        return false;
    }

    /// <inheritdoc/>
    public IReadOnlyList<BackendDescriptor> FindByTag(string tag)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(tag);

        return _backends.Values
            .Where(e => e.Descriptor.Tags.Contains(tag))
            .Select(e => e.Descriptor)
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc/>
    public IReadOnlyList<BackendDescriptor> FindByTier(StorageTier tier)
    {
        return _backends.Values
            .Where(e => e.Descriptor.Tier == tier)
            .Select(e => e.Descriptor)
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc/>
    public IReadOnlyList<BackendDescriptor> FindByCapabilities(StorageCapabilities required)
    {
        ArgumentNullException.ThrowIfNull(required);

        return _backends.Values
            .Where(e => MatchesCapabilities(e.Descriptor.Capabilities, required))
            .Select(e => e.Descriptor)
            .ToList()
            .AsReadOnly();
    }

    /// <inheritdoc/>
    public BackendDescriptor? GetById(string backendId)
    {
        if (string.IsNullOrEmpty(backendId)) return null;
        return _backends.TryGetValue(backendId, out var entry) ? entry.Descriptor : null;
    }

    /// <inheritdoc/>
    public IStorageStrategy? GetStrategy(string backendId)
    {
        if (string.IsNullOrEmpty(backendId)) return null;
        return _backends.TryGetValue(backendId, out var entry) ? entry.Strategy : null;
    }

    /// <inheritdoc/>
    public IReadOnlyList<BackendDescriptor> All =>
        _backends.Values
            .Select(e => e.Descriptor)
            .OrderBy(d => d.Priority)
            .ToList()
            .AsReadOnly();

    /// <summary>
    /// Gets the total number of registered backends.
    /// </summary>
    public int Count => _backends.Count;

    /// <summary>
    /// Checks if a backend with the given ID is registered.
    /// </summary>
    public bool Contains(string backendId)
    {
        if (string.IsNullOrEmpty(backendId)) return false;
        return _backends.ContainsKey(backendId);
    }

    /// <summary>
    /// Checks whether a backend's capabilities satisfy all required capability flags.
    /// A backend matches if every required capability is also present in the backend's capabilities.
    /// </summary>
    private static bool MatchesCapabilities(StorageCapabilities actual, StorageCapabilities required)
    {
        if (required.SupportsVersioning && !actual.SupportsVersioning) return false;
        if (required.SupportsMetadata && !actual.SupportsMetadata) return false;
        if (required.SupportsLocking && !actual.SupportsLocking) return false;
        if (required.SupportsTiering && !actual.SupportsTiering) return false;
        if (required.SupportsEncryption && !actual.SupportsEncryption) return false;
        if (required.SupportsCompression && !actual.SupportsCompression) return false;
        if (required.SupportsStreaming && !actual.SupportsStreaming) return false;
        if (required.SupportsMultipart && !actual.SupportsMultipart) return false;
        return true;
    }
}
