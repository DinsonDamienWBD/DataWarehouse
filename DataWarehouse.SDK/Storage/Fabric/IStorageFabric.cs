using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Storage;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using IStorageStrategy = DataWarehouse.SDK.Contracts.Storage.IStorageStrategy;

namespace DataWarehouse.SDK.Storage.Fabric;

/// <summary>
/// Defines a unified storage facade that routes operations across multiple storage backends.
/// Extends <see cref="IObjectStorageCore"/> with dw:// address routing, backend selection,
/// cross-backend copy/move, and fabric-wide health reporting.
/// </summary>
/// <remarks>
/// <para>
/// IStorageFabric is the single entry point for all storage operations in the DataWarehouse.
/// It resolves <see cref="StorageAddress"/> instances to the correct backend
/// <see cref="IStorageStrategy"/> and delegates operations accordingly.
/// </para>
/// <para>
/// Placement hints allow callers to express preferences (tier, region, tags, encryption)
/// without coupling to specific backends. The fabric selects the best backend matching
/// the hints, falling back to priority-based selection.
/// </para>
/// <para>
/// Cross-backend operations (copy, move) are handled transparently -- the fabric reads
/// from the source backend and writes to the destination backend, handling stream lifecycle.
/// </para>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 63: Universal Storage Fabric")]
public interface IStorageFabric : IObjectStorageCore
{
    /// <summary>
    /// Stores data at the specified <see cref="StorageAddress"/> with optional placement hints.
    /// The fabric resolves the address to the appropriate backend and executes the store operation.
    /// </summary>
    /// <param name="address">The target storage address (dw:// or any supported scheme).</param>
    /// <param name="data">The data stream to store.</param>
    /// <param name="hints">Optional placement hints for backend selection (tier, region, tags).</param>
    /// <param name="metadata">Optional key-value metadata to associate with the stored object.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Metadata of the stored object including key, size, and backend information.</returns>
    /// <exception cref="BackendNotFoundException">No backend found that can handle the address.</exception>
    /// <exception cref="PlacementFailedException">No backend matches the specified placement hints.</exception>
    /// <exception cref="BackendUnavailableException">The resolved backend is unhealthy or unreachable.</exception>
    Task<StorageObjectMetadata> StoreAsync(
        StorageAddress address,
        Stream data,
        StoragePlacementHints? hints,
        IDictionary<string, string>? metadata = null,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves data from the specified <see cref="StorageAddress"/> with automatic backend resolution.
    /// </summary>
    /// <param name="address">The source storage address to retrieve from.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A readable stream containing the object data. Caller is responsible for disposal.</returns>
    /// <exception cref="BackendNotFoundException">No backend found that can handle the address.</exception>
    /// <exception cref="BackendUnavailableException">The resolved backend is unhealthy or unreachable.</exception>
    new Task<Stream> RetrieveAsync(StorageAddress address, CancellationToken ct = default);

    /// <summary>
    /// Copies an object from one backend to another. Both source and destination addresses
    /// are resolved independently, enabling cross-backend data movement.
    /// </summary>
    /// <param name="source">The source storage address to copy from.</param>
    /// <param name="destination">The destination storage address to copy to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Metadata of the copied object at the destination.</returns>
    /// <exception cref="BackendNotFoundException">Source or destination backend not found.</exception>
    /// <exception cref="MigrationFailedException">The cross-backend copy operation failed.</exception>
    Task<StorageObjectMetadata> CopyAsync(
        StorageAddress source,
        StorageAddress destination,
        CancellationToken ct = default);

    /// <summary>
    /// Moves an object from one backend to another (copy + delete source).
    /// Both addresses are resolved independently. The source object is deleted only after
    /// successful copy to the destination.
    /// </summary>
    /// <param name="source">The source storage address to move from.</param>
    /// <param name="destination">The destination storage address to move to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Metadata of the moved object at the destination.</returns>
    /// <exception cref="BackendNotFoundException">Source or destination backend not found.</exception>
    /// <exception cref="MigrationFailedException">The cross-backend move operation failed.</exception>
    Task<StorageObjectMetadata> MoveAsync(
        StorageAddress source,
        StorageAddress destination,
        CancellationToken ct = default);

    /// <summary>
    /// Resolves a <see cref="StorageAddress"/> to the <see cref="IStorageStrategy"/> that handles it.
    /// Returns null if no backend is registered for the address.
    /// </summary>
    /// <param name="address">The storage address to resolve.</param>
    /// <returns>The storage strategy for the address, or null if no backend matches.</returns>
    IStorageStrategy? ResolveBackend(StorageAddress address);

    /// <summary>
    /// Gets the backend registry for discovering, registering, and managing storage backends.
    /// </summary>
    IBackendRegistry Registry { get; }

    /// <summary>
    /// Gets a fabric-wide health report aggregating health status from all registered backends.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="FabricHealthReport"/> with per-backend and aggregate health status.</returns>
    Task<FabricHealthReport> GetFabricHealthAsync(CancellationToken ct = default);
}
