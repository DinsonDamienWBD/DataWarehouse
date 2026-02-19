using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Generic;

using IStorageStrategy = DataWarehouse.SDK.Contracts.Storage.IStorageStrategy;
using StorageTier = DataWarehouse.SDK.Contracts.Storage.StorageTier;

namespace DataWarehouse.SDK.Storage.Fabric;

/// <summary>
/// Registry for discovering, registering, and managing storage backends within the fabric.
/// Provides lookup by backend ID, tags, tier, and capability requirements.
/// </summary>
/// <remarks>
/// <para>
/// Each backend is described by a <see cref="BackendDescriptor"/> and backed by an
/// <see cref="IStorageStrategy"/> implementation. The registry is the central catalog
/// that the fabric queries when resolving addresses and selecting placement targets.
/// </para>
/// <para>
/// Backends can be registered and unregistered at runtime, enabling dynamic topology changes
/// (e.g., adding a new cloud region, decommissioning a local drive). The <see cref="BackendChanged"/>
/// event notifies subscribers of registration changes.
/// </para>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 63: Universal Storage Fabric")]
public interface IBackendRegistry
{
    /// <summary>
    /// Registers a storage backend with its descriptor and strategy implementation.
    /// If a backend with the same ID is already registered, it is replaced.
    /// </summary>
    /// <param name="descriptor">The backend metadata describing capabilities, tier, tags, and identity.</param>
    /// <param name="strategy">The storage strategy implementation for this backend.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="descriptor"/> or <paramref name="strategy"/> is null.</exception>
    void Register(BackendDescriptor descriptor, IStorageStrategy strategy);

    /// <summary>
    /// Unregisters a backend by its unique identifier.
    /// </summary>
    /// <param name="backendId">The unique identifier of the backend to remove.</param>
    /// <returns>True if the backend was found and removed; false if not found.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="backendId"/> is null or empty.</exception>
    bool Unregister(string backendId);

    /// <summary>
    /// Finds all backends that have the specified tag.
    /// </summary>
    /// <param name="tag">The tag to search for (e.g., "cloud", "local", "archive", "encrypted").</param>
    /// <returns>A read-only list of matching backend descriptors, empty if none match.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="tag"/> is null or empty.</exception>
    IReadOnlyList<BackendDescriptor> FindByTag(string tag);

    /// <summary>
    /// Finds all backends operating on the specified storage tier.
    /// </summary>
    /// <param name="tier">The storage tier to filter by.</param>
    /// <returns>A read-only list of matching backend descriptors, empty if none match.</returns>
    IReadOnlyList<BackendDescriptor> FindByTier(StorageTier tier);

    /// <summary>
    /// Finds all backends whose capabilities satisfy the specified requirements.
    /// A backend matches if it supports all capabilities flagged as required.
    /// </summary>
    /// <param name="required">The minimum capability requirements to match against.</param>
    /// <returns>A read-only list of matching backend descriptors, empty if none match.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="required"/> is null.</exception>
    IReadOnlyList<BackendDescriptor> FindByCapabilities(StorageCapabilities required);

    /// <summary>
    /// Gets a specific backend descriptor by its unique identifier.
    /// </summary>
    /// <param name="backendId">The unique identifier of the backend.</param>
    /// <returns>The backend descriptor, or null if not found.</returns>
    BackendDescriptor? GetById(string backendId);

    /// <summary>
    /// Gets the <see cref="IStorageStrategy"/> implementation for a specific backend.
    /// </summary>
    /// <param name="backendId">The unique identifier of the backend.</param>
    /// <returns>The storage strategy, or null if the backend is not registered.</returns>
    IStorageStrategy? GetStrategy(string backendId);

    /// <summary>
    /// Gets all registered backend descriptors.
    /// </summary>
    IReadOnlyList<BackendDescriptor> All { get; }

    /// <summary>
    /// Raised when a backend is registered or unregistered.
    /// The boolean parameter is true for registration, false for unregistration.
    /// </summary>
    event Action<BackendDescriptor, bool>? BackendChanged;
}
