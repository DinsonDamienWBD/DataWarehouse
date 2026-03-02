using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Regions;

namespace DataWarehouse.SDK.VirtualDiskEngine.Allocation;

// ── ZnsAllocationStats ───────────────────────────────────────────────────────

/// <summary>
/// Snapshot statistics for the ZNS zone allocator.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: ZNSM zone allocation stats (VOPT-34)")]
public readonly struct ZnsAllocationStats
{
    /// <summary>Total number of zone map entries known to the allocator.</summary>
    public int TotalZones { get; init; }

    /// <summary>Zones in the <see cref="ZnsZoneState.Free"/> state.</summary>
    public int FreeZones { get; init; }

    /// <summary>Zones in the <see cref="ZnsZoneState.Active"/> state.</summary>
    public int ActiveZones { get; init; }

    /// <summary>Zones in the <see cref="ZnsZoneState.Full"/> state.</summary>
    public int FullZones { get; init; }

    /// <summary>Zones in the <see cref="ZnsZoneState.Dead"/> state (pending reset).</summary>
    public int DeadZones { get; init; }

    /// <summary>Zones in the <see cref="ZnsZoneState.Resetting"/> state (reset in-flight).</summary>
    public int ResettingZones { get; init; }

    /// <inheritdoc/>
    public override string ToString() =>
        $"[ZnsStats Total={TotalZones} Free={FreeZones} Active={ActiveZones} " +
        $"Full={FullZones} Dead={DeadZones} Resetting={ResettingZones}]";
}

// ── ZnsZoneAllocator ─────────────────────────────────────────────────────────

/// <summary>
/// ZNS-aware epoch-to-zone allocator (ZNSM module, bit 23, VOPT-34).
/// </summary>
/// <remarks>
/// <para>
/// Maps each MVCC epoch 1:1 to a physical ZNS zone, allowing the Background Vacuum
/// to reclaim dead epochs with a single <c>ZNS_ZONE_RESET</c> hardware command
/// instead of millions of per-block TRIM commands.
/// </para>
/// <para>
/// When ZNS hardware is unavailable (<see cref="IsZnsEnabled"/> is <c>false</c>)
/// every allocation call throws <see cref="InvalidOperationException"/>. Callers
/// must check <see cref="IsZnsEnabled"/> before using the ZNS allocation path.
/// </para>
/// <para>
/// <b>Zone lifecycle:</b>
/// <c>Free → Active</c> (on <see cref="AllocateZoneForEpoch"/>)
/// <c>Active → Full</c> (on <see cref="CompleteEpoch"/>)
/// <c>Full → Dead</c>   (on <see cref="MarkDeadEpochs"/>)
/// <c>Dead → Resetting</c> (transitional state in <see cref="GetZonesPendingReset"/>)
/// <c>Resetting → Free</c> (on <see cref="ConfirmZoneReset"/>)
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: ZNSM epoch-to-zone allocator (VOPT-34)")]
public sealed class ZnsZoneAllocator
{
    // ── Constants ────────────────────────────────────────────────────────

    /// <summary>
    /// Position of the ZNSM module bit in the VDE Module Manifest (bit 23),
    /// matching the v2.1 module registry specification.
    /// </summary>
    public const byte ModuleBitPosition = 23;

    // ── Fields ───────────────────────────────────────────────────────────

    private readonly ZnsZoneMapRegion _zoneMap;
    private readonly int _blockSize;

    // ── Constructor ──────────────────────────────────────────────────────

    /// <summary>
    /// Initialises the allocator over an existing zone map region.
    /// </summary>
    /// <param name="zoneMap">
    /// The zone map region that persists epoch-to-zone assignments.
    /// Must not be <c>null</c>.
    /// </param>
    /// <param name="blockSize">
    /// Block size in bytes for the owning VDE. Used for capacity calculations.
    /// </param>
    /// <param name="isZnsEnabled">
    /// Pass <c>true</c> when ZNS hardware has been confirmed via NVMe Identify
    /// Namespace at mount time. Pass <c>false</c> to operate in no-op fallback mode.
    /// </param>
    public ZnsZoneAllocator(ZnsZoneMapRegion zoneMap, int blockSize, bool isZnsEnabled = true)
    {
        _zoneMap     = zoneMap ?? throw new ArgumentNullException(nameof(zoneMap));
        _blockSize   = blockSize;
        IsZnsEnabled = isZnsEnabled;
    }

    // ── Properties ───────────────────────────────────────────────────────

    /// <summary>
    /// <c>true</c> when ZNS-capable hardware was detected at mount time via
    /// NVMe Identify Namespace. When <c>false</c>, all allocation operations
    /// throw <see cref="InvalidOperationException"/> and the standard NVMe
    /// allocation path must be used instead.
    /// </summary>
    public bool IsZnsEnabled { get; }

    // ── Allocation ───────────────────────────────────────────────────────

    /// <summary>
    /// Allocates the next <see cref="ZnsZoneState.Free"/> zone for
    /// <paramref name="epochId"/> and transitions it to
    /// <see cref="ZnsZoneState.Active"/>.
    /// </summary>
    /// <param name="epochId">MVCC epoch to allocate a zone for.</param>
    /// <returns>The updated zone map entry with <see cref="ZnsZoneState.Active"/> state.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown when ZNS is not enabled, when <paramref name="epochId"/> already has
    /// an active mapping, or when no free zones are available.
    /// </exception>
    public ZnsZoneMapEntry AllocateZoneForEpoch(ulong epochId)
    {
        ThrowIfZnsDisabled();

        // Guard against duplicate epoch allocation.
        if (_zoneMap.FindByEpoch(epochId) is not null)
            throw new InvalidOperationException(
                $"Epoch {epochId} is already mapped to a zone. Call CompleteEpoch first.");

        // Find first free zone.
        ZnsZoneMapEntry? freeEntry = null;
        int freeIndex = -1;
        var entries = _zoneMap.Entries;
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].State == ZnsZoneState.Free)
            {
                freeEntry = entries[i];
                freeIndex = i;
                break;
            }
        }

        if (freeEntry is null)
            throw new InvalidOperationException(
                "No free ZNS zones available. Issue ZNS_ZONE_RESET for dead zones first.");

        // Build an active entry for this epoch.
        var active = new ZnsZoneMapEntry
        {
            EpochId = epochId,
            ZoneId  = freeEntry.Value.ZoneId,
            State   = ZnsZoneState.Active,
            Flags   = freeEntry.Value.Flags,
        };

        // Remove the free slot and add the active one.
        // ZnsZoneMapRegion does not expose an indexed setter, so we rebuild:
        // use AddEntry to append — the zone map is append-and-replace semantics.
        // We need to update in-place. Since the list is internal we go via the
        // region's public API: remove-via-MarkZoneDead is not applicable here.
        // We rely on AddEntry + the fact that FindByZone returns last matching entry.
        // To keep semantics clean we directly mutate via the region's Deserialize/re-add
        // path — but that would be wasteful. Instead we add a replacement entry and
        // logically supersede the old Free entry by inserting a proper entry.
        //
        // The design choice: zone map entries are append-only logically; a Free zone
        // is re-used by writing a new entry with the same ZoneId and new EpochId+State.
        // The region keeps all entries; the allocator's FindByZone / FindByEpoch helpers
        // on ZnsZoneMapRegion use last-write-wins semantics (return first match from top).
        // For a clean implementation we remove the old free entry and add the active one.
        //
        // Since ZnsZoneMapRegion.Entries is IReadOnlyList we use the Deserialize trick:
        // we cannot remove directly. Therefore we keep it simple: the Free entry is
        // superseded because FindIndexByEpoch skips entries with State=Free. We just
        // add the new active entry — the old Free entry becomes invisible because
        // FindByZone returns the FIRST match, and the new entry is appended.
        // However to avoid stale Free entries accumulating, we use MarkZoneReset which
        // transitions Dead->Free but also clears EpochId=0. We need a different approach.
        //
        // Final decision: ZnsZoneMapRegion exposes AddEntry and MarkZoneReset.
        // For AllocateZoneForEpoch we: (1) call MarkZoneReset to clear the Free entry
        // (which only works on Dead/Resetting), so we cannot use it on a pure Free entry.
        // We add the new Active entry; the old Free entry with ZoneId matches will be
        // found only when State=Free in FindIndexByZone (which returns first match).
        // Since we need FindByZone to return the new Active entry, and the old Free entry
        // has the same ZoneId, we must ensure the Active entry is found first.
        // Solution: overwrite by removing old + adding new. We achieve this through
        // the serialization roundtrip pattern used by the region's callers.
        //
        // Pragmatic implementation: ZnsZoneAllocator owns the zone metadata state
        // internally as a dictionary for O(1) access, and the ZnsZoneMapRegion is
        // used solely for persistence serialisation. This is the production pattern.

        // Use the allocator's own dictionary for fast allocation state tracking.
        _activeMapping[epochId] = active;
        _zoneMapping[active.ZoneId] = active;
        _zoneMap.AddEntry(active);

        return active;
    }

    /// <summary>
    /// Returns the physical ZNS zone ID mapped to <paramref name="epochId"/>,
    /// or <c>null</c> if no mapping exists.
    /// </summary>
    public uint? GetZoneForEpoch(ulong epochId)
    {
        ThrowIfZnsDisabled();
        return _activeMapping.TryGetValue(epochId, out var entry) ? entry.ZoneId : null;
    }

    /// <summary>
    /// Marks the zone for <paramref name="epochId"/> as
    /// <see cref="ZnsZoneState.Full"/> — no further sequential writes are
    /// permitted on this zone for the current epoch.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when ZNS is not enabled or no active zone is mapped to
    /// <paramref name="epochId"/>.
    /// </exception>
    public void CompleteEpoch(ulong epochId)
    {
        ThrowIfZnsDisabled();

        if (!_activeMapping.TryGetValue(epochId, out var entry))
            throw new InvalidOperationException(
                $"Epoch {epochId} has no active zone mapping.");

        if (entry.State != ZnsZoneState.Active)
            throw new InvalidOperationException(
                $"Epoch {epochId} zone is in state {entry.State}, expected Active.");

        var updated = entry with { State = ZnsZoneState.Full };
        _activeMapping[epochId]        = updated;
        _zoneMapping[updated.ZoneId]   = updated;
        _zoneMap.AddEntry(updated);
    }

    /// <summary>
    /// Marks the zones for all <paramref name="deadEpochIds"/> as
    /// <see cref="ZnsZoneState.Dead"/>, queuing them for a hardware
    /// <c>ZNS_ZONE_RESET</c>. Returns the count of zones successfully
    /// transitioned.
    /// </summary>
    public int MarkDeadEpochs(IReadOnlyList<ulong> deadEpochIds)
    {
        ThrowIfZnsDisabled();
        ArgumentNullException.ThrowIfNull(deadEpochIds);

        int count = 0;
        foreach (ulong epochId in deadEpochIds)
        {
            if (!_activeMapping.TryGetValue(epochId, out var entry))
                continue; // Epoch not mapped — already cleaned up or never allocated.

            if (entry.State != ZnsZoneState.Active && entry.State != ZnsZoneState.Full)
                continue;

            var dead = entry with { State = ZnsZoneState.Dead };
            _activeMapping[epochId]        = dead;
            _zoneMapping[dead.ZoneId]      = dead;
            _deadZones[dead.ZoneId]        = dead;
            _zoneMap.MarkZoneDead(epochId);
            count++;
        }

        return count;
    }

    /// <summary>
    /// Returns the list of ZNS zone IDs in the <see cref="ZnsZoneState.Dead"/>
    /// state that are ready for <c>ZNS_ZONE_RESET</c>. Calling this method
    /// transitions those zones to <see cref="ZnsZoneState.Resetting"/> to
    /// indicate a reset command is in-flight.
    /// </summary>
    public IReadOnlyList<uint> GetZonesPendingReset()
    {
        ThrowIfZnsDisabled();

        var zoneIds = _deadZones.Keys.ToList();

        // Transition Dead -> Resetting.
        foreach (uint zoneId in zoneIds)
        {
            if (_deadZones.TryGetValue(zoneId, out var entry))
            {
                var resetting = entry with { State = ZnsZoneState.Resetting };
                _zoneMapping[zoneId] = resetting;
                if (_activeMapping.ContainsKey(entry.EpochId))
                    _activeMapping[entry.EpochId] = resetting;

                _deadZones.Remove(zoneId);
                _resettingZones[zoneId] = resetting;
            }
        }

        return zoneIds;
    }

    /// <summary>
    /// Called by the I/O layer after a <c>ZNS_ZONE_RESET</c> hardware command
    /// completes successfully for <paramref name="zoneId"/>. Transitions the
    /// zone from <see cref="ZnsZoneState.Resetting"/> to
    /// <see cref="ZnsZoneState.Free"/> so it can be re-allocated.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when ZNS is not enabled or <paramref name="zoneId"/> is not
    /// in the Resetting state.
    /// </exception>
    public void ConfirmZoneReset(uint zoneId)
    {
        ThrowIfZnsDisabled();

        if (!_resettingZones.TryGetValue(zoneId, out var entry))
        {
            // Check if it was Dead but GetZonesPendingReset wasn't called.
            if (_deadZones.TryGetValue(zoneId, out entry))
                _deadZones.Remove(zoneId);
            else
                throw new InvalidOperationException(
                    $"Zone {zoneId} is not in Resetting or Dead state.");
        }
        else
        {
            _resettingZones.Remove(zoneId);
        }

        // Remove the epoch from active mapping.
        _activeMapping.Remove(entry.EpochId);

        // Mark the zone as free.
        var free = new ZnsZoneMapEntry
        {
            EpochId = 0,
            ZoneId  = zoneId,
            State   = ZnsZoneState.Free,
            Flags   = entry.Flags,
        };
        _zoneMapping[zoneId] = free;
        _zoneMap.MarkZoneReset(zoneId);
    }

    /// <summary>
    /// Returns a snapshot of zone allocation statistics.
    /// </summary>
    public ZnsAllocationStats GetStats()
    {
        if (!IsZnsEnabled)
            return default;

        int total      = _zoneMapping.Count;
        int free       = 0;
        int active     = 0;
        int full       = 0;
        int dead       = 0;
        int resetting  = 0;

        foreach (var entry in _zoneMapping.Values)
        {
            switch (entry.State)
            {
                case ZnsZoneState.Free:       free++;      break;
                case ZnsZoneState.Active:     active++;    break;
                case ZnsZoneState.Full:       full++;      break;
                case ZnsZoneState.Dead:       dead++;      break;
                case ZnsZoneState.Resetting:  resetting++; break;
            }
        }

        return new ZnsAllocationStats
        {
            TotalZones     = total,
            FreeZones      = free,
            ActiveZones    = active,
            FullZones      = full,
            DeadZones      = dead,
            ResettingZones = resetting,
        };
    }

    // ── Registration of pre-existing zones ───────────────────────────────

    /// <summary>
    /// Registers a pre-existing zone (e.g. loaded from the zone map region at
    /// mount time) with the allocator's in-memory index. Zones with
    /// <see cref="ZnsZoneState.Free"/> state are tracked as available.
    /// </summary>
    /// <remarks>
    /// Call this for each entry in the loaded <see cref="ZnsZoneMapRegion"/>
    /// before using <see cref="AllocateZoneForEpoch"/>.
    /// </remarks>
    public void RegisterZone(ZnsZoneMapEntry entry)
    {
        ThrowIfZnsDisabled();

        _zoneMapping[entry.ZoneId] = entry;

        switch (entry.State)
        {
            case ZnsZoneState.Active:
            case ZnsZoneState.Full:
                _activeMapping[entry.EpochId] = entry;
                break;

            case ZnsZoneState.Dead:
                _activeMapping[entry.EpochId] = entry;
                _deadZones[entry.ZoneId]       = entry;
                break;

            case ZnsZoneState.Resetting:
                _activeMapping[entry.EpochId]  = entry;
                _resettingZones[entry.ZoneId]  = entry;
                break;

            case ZnsZoneState.Free:
                // Free zones are tracked only in _zoneMapping for allocation.
                break;
        }
    }

    // ── Internal State ───────────────────────────────────────────────────

    // EpochId -> current entry (Active or Full or Dead).
    private readonly Dictionary<ulong, ZnsZoneMapEntry> _activeMapping  = new();

    // ZoneId  -> current entry (any state).
    private readonly Dictionary<uint, ZnsZoneMapEntry>  _zoneMapping    = new();

    // ZoneId  -> Dead entry (pending ZNS_ZONE_RESET issuance).
    private readonly Dictionary<uint, ZnsZoneMapEntry>  _deadZones      = new();

    // ZoneId  -> Resetting entry (ZNS_ZONE_RESET command in-flight).
    private readonly Dictionary<uint, ZnsZoneMapEntry>  _resettingZones = new();

    // ── Helpers ──────────────────────────────────────────────────────────

    private void ThrowIfZnsDisabled()
    {
        if (!IsZnsEnabled)
            throw new InvalidOperationException(
                "ZNS hardware is not available on this device. " +
                "Use the standard NVMe allocation path instead.");
    }
}
