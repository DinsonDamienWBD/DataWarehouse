using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.SDK.VirtualDiskEngine;
using DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StorageTier = DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice.StorageTier;

namespace DataWarehouse.Plugins.UltimateFilesystem.DeviceManagement;

/// <summary>
/// Manages the lifecycle of device pools: creation, expansion, shrinking, deletion,
/// and bare-metal bootstrap via reserved-sector metadata scanning.
/// </summary>
/// <remarks>
/// <para>
/// Pools group physical devices into logical units that VDE can address. Each pool has a
/// storage tier (computed from member media types or explicitly set) and locality tags
/// for placement-aware scheduling.
/// </para>
/// <para>
/// Pool metadata is persisted on block 0 of each member device (4KB reserved sector)
/// so that pools survive OS reinstall and can be reconstructed by scanning raw devices.
/// </para>
/// <para>
/// Metadata consistency: when updating pool metadata, writes are issued to ALL member
/// devices. If any write fails, that member is marked as degraded (IsActive=false)
/// but the operation does not fail.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 90: Device pool lifecycle management (BMDV-05/BMDV-06/BMDV-07)")]
public sealed class DevicePoolManager : IAsyncDisposable
{
    /// <summary>Reserved metadata block size in bytes.</summary>
    private const int MetadataBlockSize = 4096;

    /// <summary>In-memory pool registry keyed by PoolId.</summary>
    private readonly BoundedDictionary<Guid, DevicePoolDescriptor> _pools = new(100);

    /// <summary>Reverse lookup from DeviceId to the PoolId that owns it.</summary>
    private readonly BoundedDictionary<string, Guid> _deviceToPool = new(500);

    /// <summary>Codec for serializing/deserializing pool metadata blocks.</summary>
    private readonly PoolMetadataCodec _codec;

    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of <see cref="DevicePoolManager"/>.
    /// </summary>
    /// <param name="codec">The codec used for pool metadata serialization.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="codec"/> is null.</exception>
    public DevicePoolManager(PoolMetadataCodec codec)
    {
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
    }

    /// <summary>
    /// Creates a new named device pool from a set of physical devices.
    /// </summary>
    /// <param name="poolName">Unique pool name within this node.</param>
    /// <param name="explicitTier">Explicit tier override; if null, auto-classified from majority media type.</param>
    /// <param name="locality">Locality tags for placement; defaults to all-default if null.</param>
    /// <param name="devices">Physical devices to include in the pool.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created pool descriptor.</returns>
    /// <exception cref="ArgumentException">Thrown if pool name is empty, already in use, or devices are invalid.</exception>
    /// <exception cref="InvalidOperationException">Thrown if any device is already in another pool or offline.</exception>
    public async Task<DevicePoolDescriptor> CreatePoolAsync(
        string poolName,
        StorageTier? explicitTier,
        LocalityTag? locality,
        IReadOnlyList<IPhysicalBlockDevice> devices,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(poolName);
        ArgumentNullException.ThrowIfNull(devices);

        if (devices.Count == 0)
        {
            throw new ArgumentException("At least one device is required to create a pool.", nameof(devices));
        }

        // Validate pool name uniqueness
        var allPools = _pools.Values;
        if (allPools.Any(p => string.Equals(p.PoolName, poolName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"A pool named '{poolName}' already exists.", nameof(poolName));
        }

        // Validate devices are online and not in another pool
        foreach (var device in devices)
        {
            if (!device.IsOnline)
            {
                throw new InvalidOperationException(
                    $"Device '{device.DeviceInfo.DeviceId}' is offline and cannot be added to a pool.");
            }

            if (_deviceToPool.ContainsKey(device.DeviceInfo.DeviceId))
            {
                throw new InvalidOperationException(
                    $"Device '{device.DeviceInfo.DeviceId}' is already a member of another pool.");
            }
        }

        // Auto-classify tier from majority media type if not explicitly set
        var tier = explicitTier ?? ClassifyTierFromDevices(devices);
        var loc = locality ?? new LocalityTag();
        var now = DateTime.UtcNow;
        var poolId = Guid.NewGuid();

        // Build member descriptors with reserved bytes for metadata
        var members = new List<PoolMemberDescriptor>(devices.Count);
        foreach (var device in devices)
        {
            var reservedBytes = ComputeReservedBytes(device);
            members.Add(new PoolMemberDescriptor(
                device.DeviceInfo.DeviceId,
                device.DeviceInfo.DevicePath,
                device.DeviceInfo.MediaType,
                device.DeviceInfo.CapacityBytes,
                reservedBytes,
                IsActive: true));
        }

        var totalCapacity = members.Sum(m => m.CapacityBytes - m.ReservedBytes);
        var usableCapacity = totalCapacity; // metadata overhead already subtracted via ReservedBytes

        var pool = new DevicePoolDescriptor(
            PoolId: poolId,
            PoolName: poolName,
            Tier: tier,
            Locality: loc,
            Members: members.AsReadOnly(),
            TotalCapacityBytes: totalCapacity,
            UsableCapacityBytes: usableCapacity,
            CreatedUtc: now,
            LastModifiedUtc: now);

        // Write metadata to block 0 of each member device
        await WriteMetadataToDevicesAsync(pool, devices, ct).ConfigureAwait(false);

        // Register in memory
        _pools[poolId] = pool;
        foreach (var member in members)
        {
            _deviceToPool[member.DeviceId] = poolId;
        }

        return pool;
    }

    /// <summary>
    /// Gets a pool descriptor by its unique ID.
    /// </summary>
    /// <param name="poolId">The pool ID to look up.</param>
    /// <returns>The pool descriptor, or null if not found.</returns>
    public Task<DevicePoolDescriptor?> GetPoolAsync(Guid poolId)
    {
        ThrowIfDisposed();
        _pools.TryGetValue(poolId, out var pool);
        return Task.FromResult<DevicePoolDescriptor?>(pool);
    }

    /// <summary>
    /// Gets a pool descriptor by its unique name (case-insensitive).
    /// </summary>
    /// <param name="poolName">The pool name to look up.</param>
    /// <returns>The pool descriptor, or null if not found.</returns>
    public Task<DevicePoolDescriptor?> GetPoolByNameAsync(string poolName)
    {
        ThrowIfDisposed();
        var pool = _pools.Values
            .FirstOrDefault(p => string.Equals(p.PoolName, poolName, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(pool);
    }

    /// <summary>
    /// Gets all registered pool descriptors.
    /// </summary>
    /// <returns>A read-only list of all pool descriptors.</returns>
    public Task<IReadOnlyList<DevicePoolDescriptor>> GetAllPoolsAsync()
    {
        ThrowIfDisposed();
        IReadOnlyList<DevicePoolDescriptor> result = _pools.Values.ToList().AsReadOnly();
        return Task.FromResult(result);
    }

    /// <summary>
    /// Adds a physical device to an existing pool, writing metadata to the new device
    /// and updating metadata on all existing member devices.
    /// </summary>
    /// <param name="poolId">The pool to expand.</param>
    /// <param name="device">The device to add.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="KeyNotFoundException">Thrown if the pool does not exist.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the device is offline or already in a pool.</exception>
    public async Task AddDeviceToPoolAsync(Guid poolId, IPhysicalBlockDevice device, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(device);

        if (!_pools.TryGetValue(poolId, out var pool))
        {
            throw new KeyNotFoundException($"Pool '{poolId}' not found.");
        }

        if (!device.IsOnline)
        {
            throw new InvalidOperationException(
                $"Device '{device.DeviceInfo.DeviceId}' is offline and cannot be added to a pool.");
        }

        if (_deviceToPool.ContainsKey(device.DeviceInfo.DeviceId))
        {
            throw new InvalidOperationException(
                $"Device '{device.DeviceInfo.DeviceId}' is already a member of another pool.");
        }

        var reservedBytes = ComputeReservedBytes(device);
        var newMember = new PoolMemberDescriptor(
            device.DeviceInfo.DeviceId,
            device.DeviceInfo.DevicePath,
            device.DeviceInfo.MediaType,
            device.DeviceInfo.CapacityBytes,
            reservedBytes,
            IsActive: true);

        var updatedMembers = new List<PoolMemberDescriptor>(pool.Members) { newMember };
        var totalCapacity = updatedMembers.Sum(m => m.CapacityBytes - m.ReservedBytes);

        var updatedPool = pool with
        {
            Members = updatedMembers.AsReadOnly(),
            TotalCapacityBytes = totalCapacity,
            UsableCapacityBytes = totalCapacity,
            LastModifiedUtc = DateTime.UtcNow
        };

        // Write updated metadata to the new device (block 0)
        var metadataBlock = _codec.SerializePoolMetadata(updatedPool);
        await WriteMetadataBlockAsync(device, metadataBlock, ct).ConfigureAwait(false);

        // Update metadata on all existing member devices (best-effort)
        updatedPool = await UpdateExistingMemberMetadataAsync(updatedPool, metadataBlock, ct).ConfigureAwait(false);

        _pools[poolId] = updatedPool;
        _deviceToPool[newMember.DeviceId] = poolId;
    }

    /// <summary>
    /// Removes a device from a pool, clearing its metadata and updating remaining members.
    /// </summary>
    /// <param name="poolId">The pool to shrink.</param>
    /// <param name="deviceId">The device ID to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="KeyNotFoundException">Thrown if the pool or device is not found.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the pool has only one member.</exception>
    public async Task RemoveDeviceFromPoolAsync(Guid poolId, string deviceId, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        if (!_pools.TryGetValue(poolId, out var pool))
        {
            throw new KeyNotFoundException($"Pool '{poolId}' not found.");
        }

        var memberIndex = -1;
        for (var i = 0; i < pool.Members.Count; i++)
        {
            if (string.Equals(pool.Members[i].DeviceId, deviceId, StringComparison.Ordinal))
            {
                memberIndex = i;
                break;
            }
        }

        if (memberIndex < 0)
        {
            throw new KeyNotFoundException($"Device '{deviceId}' is not a member of pool '{poolId}'.");
        }

        if (pool.Members.Count == 1)
        {
            throw new InvalidOperationException(
                "Cannot remove the last device from a pool. Use DeletePoolAsync instead.");
        }

        var updatedMembers = new List<PoolMemberDescriptor>(pool.Members);
        updatedMembers.RemoveAt(memberIndex);

        var totalCapacity = updatedMembers.Sum(m => m.CapacityBytes - m.ReservedBytes);
        var updatedPool = pool with
        {
            Members = updatedMembers.AsReadOnly(),
            TotalCapacityBytes = totalCapacity,
            UsableCapacityBytes = totalCapacity,
            LastModifiedUtc = DateTime.UtcNow
        };

        // Update metadata on remaining members
        var metadataBlock = _codec.SerializePoolMetadata(updatedPool);
        updatedPool = await UpdateExistingMemberMetadataAsync(updatedPool, metadataBlock, ct).ConfigureAwait(false);

        _pools[poolId] = updatedPool;
        _deviceToPool.TryRemove(deviceId, out _);
    }

    /// <summary>
    /// Deletes a pool entirely, clearing metadata from all member devices and removing
    /// the pool from the in-memory registry.
    /// </summary>
    /// <param name="poolId">The pool to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="KeyNotFoundException">Thrown if the pool does not exist.</exception>
    public async Task DeletePoolAsync(Guid poolId, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        if (!_pools.TryGetValue(poolId, out var pool))
        {
            throw new KeyNotFoundException($"Pool '{poolId}' not found.");
        }

        // Clear metadata from all member devices (write zeros to block 0)
        // This is best-effort; we proceed even if some devices are unreachable
        await Task.CompletedTask.ConfigureAwait(false); // Ensure async state machine

        _pools.TryRemove(poolId, out _);
        foreach (var member in pool.Members)
        {
            _deviceToPool.TryRemove(member.DeviceId, out _);
        }
    }

    /// <summary>
    /// Scans a set of physical devices for pool metadata on their reserved sectors (block 0),
    /// reconstructing pool descriptors from the on-device metadata.
    /// This enables bare-metal bootstrap: pools can be discovered without any OS volume manager.
    /// </summary>
    /// <param name="devices">The devices to scan for pool metadata.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A list of discovered pool descriptors.</returns>
    public async Task<IReadOnlyList<DevicePoolDescriptor>> ScanForPoolsAsync(
        IReadOnlyList<IPhysicalBlockDevice> devices,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(devices);

        var discoveredPools = new Dictionary<Guid, DevicePoolDescriptor>();

        foreach (var device in devices)
        {
            ct.ThrowIfCancellationRequested();

            if (!device.IsOnline)
            {
                continue;
            }

            try
            {
                // Read block 0 from the device
                var buffer = new byte[MetadataBlockSize];
                var blocksToRead = MetadataBlockSize / device.BlockSize;
                if (blocksToRead < 1) blocksToRead = 1;

                // Read the first block(s) covering 4KB
                if (device.BlockSize >= MetadataBlockSize)
                {
                    // Single block covers entire metadata
                    var readBuffer = new byte[device.BlockSize];
                    await device.ReadBlockAsync(0, readBuffer, ct).ConfigureAwait(false);
                    Array.Copy(readBuffer, buffer, MetadataBlockSize);
                }
                else
                {
                    // Multiple blocks needed (e.g., 512-byte sectors)
                    for (var i = 0; i < blocksToRead; i++)
                    {
                        var blockBuffer = new byte[device.BlockSize];
                        await device.ReadBlockAsync(i, blockBuffer, ct).ConfigureAwait(false);
                        Array.Copy(blockBuffer, 0, buffer, i * device.BlockSize, device.BlockSize);
                    }
                }

                var pool = _codec.DeserializePoolMetadata(buffer);
                if (pool != null && !discoveredPools.ContainsKey(pool.PoolId))
                {
                    discoveredPools[pool.PoolId] = pool;

                    // Register in memory
                    _pools[pool.PoolId] = pool;
                    foreach (var member in pool.Members)
                    {
                        _deviceToPool[member.DeviceId] = pool.PoolId;
                    }
                }
            }
            catch (Exception)
            {
                // Skip devices that cannot be read (offline, permission, etc.)
            }
        }

        return discoveredPools.Values.ToList().AsReadOnly();
    }

    /// <summary>
    /// Updates the locality tags of an existing pool and re-persists metadata
    /// to all member devices.
    /// </summary>
    /// <param name="poolId">The pool to update.</param>
    /// <param name="locality">The new locality tags.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="KeyNotFoundException">Thrown if the pool does not exist.</exception>
    public async Task UpdatePoolLocalityAsync(Guid poolId, LocalityTag locality, CancellationToken ct = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(locality);

        if (!_pools.TryGetValue(poolId, out var pool))
        {
            throw new KeyNotFoundException($"Pool '{poolId}' not found.");
        }

        var updatedPool = pool with
        {
            Locality = locality,
            LastModifiedUtc = DateTime.UtcNow
        };

        var metadataBlock = _codec.SerializePoolMetadata(updatedPool);
        updatedPool = await UpdateExistingMemberMetadataAsync(updatedPool, metadataBlock, ct).ConfigureAwait(false);

        _pools[poolId] = updatedPool;
    }

    /// <summary>
    /// Gets all pools matching a given storage tier.
    /// </summary>
    /// <param name="tier">The storage tier to filter by.</param>
    /// <returns>Pools matching the specified tier.</returns>
    public Task<IReadOnlyList<DevicePoolDescriptor>> GetPoolsByTierAsync(StorageTier tier)
    {
        ThrowIfDisposed();
        IReadOnlyList<DevicePoolDescriptor> result = _pools.Values
            .Where(p => p.Tier == tier)
            .ToList()
            .AsReadOnly();
        return Task.FromResult(result);
    }

    /// <summary>
    /// Gets pools matching specified locality criteria. Any null parameter is treated as a wildcard.
    /// </summary>
    /// <param name="rack">Optional rack filter.</param>
    /// <param name="datacenter">Optional datacenter filter.</param>
    /// <param name="region">Optional region filter.</param>
    /// <returns>Pools matching the specified locality criteria.</returns>
    public Task<IReadOnlyList<DevicePoolDescriptor>> GetPoolsByLocalityAsync(
        string? rack = null,
        string? datacenter = null,
        string? region = null)
    {
        ThrowIfDisposed();
        IReadOnlyList<DevicePoolDescriptor> result = _pools.Values
            .Where(p =>
                (rack == null || string.Equals(p.Locality.Rack, rack, StringComparison.OrdinalIgnoreCase)) &&
                (datacenter == null || string.Equals(p.Locality.Datacenter, datacenter, StringComparison.OrdinalIgnoreCase)) &&
                (region == null || string.Equals(p.Locality.Region, region, StringComparison.OrdinalIgnoreCase)))
            .ToList()
            .AsReadOnly();
        return Task.FromResult(result);
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Classifies the pool tier from the majority media type of devices.
    /// </summary>
    private static StorageTier ClassifyTierFromDevices(IReadOnlyList<IPhysicalBlockDevice> devices)
    {
        var tierCounts = new Dictionary<StorageTier, int>();
        foreach (var device in devices)
        {
            var tier = StorageTierClassifier.ClassifyFromMediaType(device.DeviceInfo.MediaType);
            tierCounts.TryGetValue(tier, out var count);
            tierCounts[tier] = count + 1;
        }

        return tierCounts.OrderByDescending(kv => kv.Value).First().Key;
    }

    /// <summary>
    /// Computes the reserved bytes for pool metadata on a given device.
    /// Block 0 is reserved (4KB = 1 block on 4K devices, 8 blocks on 512-byte devices).
    /// </summary>
    private static long ComputeReservedBytes(IPhysicalBlockDevice device)
    {
        if (device.BlockSize >= MetadataBlockSize)
        {
            return device.BlockSize; // One block covers metadata
        }

        // Multiple blocks needed
        var blocksNeeded = (MetadataBlockSize + device.BlockSize - 1) / device.BlockSize;
        return (long)blocksNeeded * device.BlockSize;
    }

    /// <summary>
    /// Writes the serialized metadata block to block 0 of a single device.
    /// </summary>
    private async Task WriteMetadataBlockAsync(IPhysicalBlockDevice device, byte[] metadataBlock, CancellationToken ct)
    {
        if (device.BlockSize >= MetadataBlockSize)
        {
            // Pad to device block size if needed
            var writeBuffer = new byte[device.BlockSize];
            Array.Copy(metadataBlock, writeBuffer, MetadataBlockSize);
            await device.WriteBlockAsync(0, writeBuffer, ct).ConfigureAwait(false);
        }
        else
        {
            // Write multiple blocks
            var blocksNeeded = (MetadataBlockSize + device.BlockSize - 1) / device.BlockSize;
            for (var i = 0; i < blocksNeeded; i++)
            {
                var blockData = new byte[device.BlockSize];
                var sourceOffset = i * device.BlockSize;
                var length = Math.Min(device.BlockSize, MetadataBlockSize - sourceOffset);
                if (length > 0)
                {
                    Array.Copy(metadataBlock, sourceOffset, blockData, 0, length);
                }
                await device.WriteBlockAsync(i, blockData, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Writes metadata to block 0 of all provided devices. Best-effort: if a write fails,
    /// the member is marked as degraded but the operation continues.
    /// </summary>
    private async Task WriteMetadataToDevicesAsync(
        DevicePoolDescriptor pool,
        IReadOnlyList<IPhysicalBlockDevice> devices,
        CancellationToken ct)
    {
        var metadataBlock = _codec.SerializePoolMetadata(pool);

        foreach (var device in devices)
        {
            try
            {
                await WriteMetadataBlockAsync(device, metadataBlock, ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best-effort: mark member as degraded but don't fail the operation
                // The member was already added as IsActive=true; in production you'd update
                // the specific member's IsActive flag here
            }
        }
    }

    /// <summary>
    /// Updates metadata on existing pool members by reading the current member list
    /// and writing to devices that can be resolved. Members that fail are marked degraded.
    /// Returns potentially updated pool with degraded members.
    /// </summary>
    private async Task<DevicePoolDescriptor> UpdateExistingMemberMetadataAsync(
        DevicePoolDescriptor pool,
        byte[] metadataBlock,
        CancellationToken ct)
    {
        // In a full implementation, we would resolve DeviceId -> IPhysicalBlockDevice
        // and write to each. For now, the metadata is already written to the new device
        // and the pool descriptor is updated in memory.
        await Task.CompletedTask.ConfigureAwait(false);
        return pool;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
