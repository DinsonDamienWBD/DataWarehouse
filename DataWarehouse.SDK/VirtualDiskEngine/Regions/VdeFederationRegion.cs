using System.Buffers.Binary;
using System.Text;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Regions;

/// <summary>
/// Defines a geographic region for federation-aware VDE routing.
/// </summary>
/// <remarks>
/// Serialized layout (variable size):
/// [RegionId:2][NameLength:2][RegionName:variable max 32][Latitude:8][Longitude:8]
/// </remarks>
[SdkCompatibility("6.0.0")]
public readonly record struct GeoRegion
{
    /// <summary>Maximum length in bytes for the region name.</summary>
    public const int MaxNameLength = 32;

    /// <summary>Numeric ID for the geographic region.</summary>
    public ushort RegionId { get; init; }

    /// <summary>UTF-8 name (max 32 bytes, e.g., "us-east-1", "eu-west-1").</summary>
    public byte[] RegionName { get; init; }

    /// <summary>Region center latitude in degrees.</summary>
    public double Latitude { get; init; }

    /// <summary>Region center longitude in degrees.</summary>
    public double Longitude { get; init; }

    /// <summary>Total serialized size of this region entry.</summary>
    public int SerializedSize => 20 + (RegionName?.Length ?? 0);

    /// <summary>Writes this region to the buffer at the specified offset.</summary>
    /// <returns>Number of bytes written.</returns>
    internal int WriteTo(Span<byte> buffer, int offset)
    {
        int start = offset;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset), RegionId);
        offset += 2;

        int nameLen = RegionName?.Length ?? 0;
        if (nameLen > MaxNameLength)
            throw new InvalidOperationException($"Region name exceeds {MaxNameLength} bytes.");
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset), (ushort)nameLen);
        offset += 2;

        if (nameLen > 0)
        {
            RegionName.AsSpan(0, nameLen).CopyTo(buffer.Slice(offset, nameLen));
            offset += nameLen;
        }

        BinaryPrimitives.WriteDoubleLittleEndian(buffer.Slice(offset), Latitude);
        offset += 8;
        BinaryPrimitives.WriteDoubleLittleEndian(buffer.Slice(offset), Longitude);
        offset += 8;

        return offset - start;
    }

    /// <summary>Reads a region from the buffer at the specified offset.</summary>
    internal static GeoRegion ReadFrom(ReadOnlySpan<byte> buffer, int offset, out int bytesRead)
    {
        int start = offset;
        ushort regionId = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset));
        offset += 2;

        ushort nameLen = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset));
        offset += 2;
        if (nameLen > MaxNameLength || offset + nameLen > buffer.Length)
            throw new InvalidDataException($"GeoRegion nameLen {nameLen} exceeds maximum {MaxNameLength} or buffer bounds.");

        byte[] regionName = nameLen > 0
            ? buffer.Slice(offset, nameLen).ToArray()
            : Array.Empty<byte>();
        offset += nameLen;

        double latitude = BinaryPrimitives.ReadDoubleLittleEndian(buffer.Slice(offset));
        offset += 8;
        double longitude = BinaryPrimitives.ReadDoubleLittleEndian(buffer.Slice(offset));
        offset += 8;

        bytesRead = offset - start;

        return new GeoRegion
        {
            RegionId = regionId,
            RegionName = regionName,
            Latitude = latitude,
            Longitude = longitude
        };
    }

    /// <summary>Returns the region name as a UTF-8 string.</summary>
    public override string ToString()
    {
        string name = RegionName is { Length: > 0 }
            ? Encoding.UTF8.GetString(RegionName)
            : "<unnamed>";
        return $"GeoRegion({RegionId}: {name}, {Latitude:F4}, {Longitude:F4})";
    }
}

/// <summary>
/// A VDE registered in the federation with geo-region, namespace, and status information.
/// </summary>
/// <remarks>
/// Serialized layout (variable size):
/// [VdeId:16][GeoRegionId:2][NamespacePathLength:2][NamespacePath:variable max 256]
/// [LastSeenUtcTicks:8][LatencyMs:2][ReplicaCount:1][Status:1]
/// </remarks>
[SdkCompatibility("6.0.0")]
public readonly record struct FederationEntry
{
    /// <summary>Maximum length in bytes for the namespace path.</summary>
    public const int MaxNamespacePathLength = 256;

    /// <summary>VDE status: Online and operational.</summary>
    public const byte StatusOnline = 0;

    /// <summary>VDE status: Degraded performance or partial availability.</summary>
    public const byte StatusDegraded = 1;

    /// <summary>VDE status: Offline and unreachable.</summary>
    public const byte StatusOffline = 2;

    /// <summary>VDE status: Migrating data to another location.</summary>
    public const byte StatusMigrating = 3;

    /// <summary>The VDE in the federation.</summary>
    public Guid VdeId { get; init; }

    /// <summary>Which geographic region this VDE is in.</summary>
    public ushort GeoRegionId { get; init; }

    /// <summary>UTF-8 dw:// namespace path this VDE serves (max 256 bytes).</summary>
    public byte[] NamespacePath { get; init; }

    /// <summary>Last heartbeat/contact from this VDE (UTC ticks).</summary>
    public long LastSeenUtcTicks { get; init; }

    /// <summary>Estimated latency to reach this VDE from the local region in milliseconds.</summary>
    public ushort LatencyMs { get; init; }

    /// <summary>Number of replicas of this VDE in this region.</summary>
    public byte ReplicaCount { get; init; }

    /// <summary>Status: 0=Online, 1=Degraded, 2=Offline, 3=Migrating.</summary>
    public byte Status { get; init; }

    /// <summary>Total serialized size of this entry.</summary>
    public int SerializedSize => 32 + (NamespacePath?.Length ?? 0);

    /// <summary>Writes this entry to the buffer at the specified offset.</summary>
    /// <returns>Number of bytes written.</returns>
    internal int WriteTo(Span<byte> buffer, int offset)
    {
        int start = offset;
        VdeId.TryWriteBytes(buffer.Slice(offset, 16));
        offset += 16;

        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset), GeoRegionId);
        offset += 2;

        int nsLen = NamespacePath?.Length ?? 0;
        if (nsLen > MaxNamespacePathLength)
            throw new InvalidOperationException($"Namespace path exceeds {MaxNamespacePathLength} bytes.");
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset), (ushort)nsLen);
        offset += 2;

        if (nsLen > 0)
        {
            NamespacePath.AsSpan(0, nsLen).CopyTo(buffer.Slice(offset, nsLen));
            offset += nsLen;
        }

        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), LastSeenUtcTicks);
        offset += 8;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset), LatencyMs);
        offset += 2;
        buffer[offset++] = ReplicaCount;
        buffer[offset++] = Status;

        return offset - start;
    }

    /// <summary>Reads an entry from the buffer at the specified offset.</summary>
    internal static FederationEntry ReadFrom(ReadOnlySpan<byte> buffer, int offset, out int bytesRead)
    {
        int start = offset;
        var vdeId = new Guid(buffer.Slice(offset, 16));
        offset += 16;

        ushort geoRegionId = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset));
        offset += 2;

        ushort nsLen = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset));
        offset += 2;

        byte[] namespacePath = nsLen > 0
            ? buffer.Slice(offset, nsLen).ToArray()
            : Array.Empty<byte>();
        offset += nsLen;

        long lastSeenUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;
        ushort latencyMs = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset));
        offset += 2;
        byte replicaCount = buffer[offset++];
        byte status = buffer[offset++];

        bytesRead = offset - start;

        return new FederationEntry
        {
            VdeId = vdeId,
            GeoRegionId = geoRegionId,
            NamespacePath = namespacePath,
            LastSeenUtcTicks = lastSeenUtcTicks,
            LatencyMs = latencyMs,
            ReplicaCount = replicaCount,
            Status = status
        };
    }
}

/// <summary>
/// Cross-region VDE federation with geo-aware dw:// namespace resolution.
/// Manages VDE registrations across geographic regions and resolves namespace
/// queries with latency-based routing preferences (local region first, then
/// lowest latency, then online status).
/// </summary>
/// <remarks>
/// Serialization layout (two-section):
///   Header: [GeoRegionCount:4 LE][FederationEntryCount:4 LE][LocalRegionId:2 LE][Reserved:6]
///   Section 1: [GeoRegion entries...] (variable size)
///   Section 2: [FederationEntry entries...] (variable size)
///   Each block ends with <see cref="UniversalBlockTrailer"/>.
///   Uses <see cref="BlockTypeTags.XREF"/> (differentiated from CrossVdeReferenceRegion
///   by RegionDirectory slot assignment).
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 73: VDE regions -- VDE Federation (VADV-03)")]
public sealed class VdeFederationRegion
{
    /// <summary>Header size: GeoRegionCount(4) + FederationEntryCount(4) + LocalRegionId(2) + Reserved(6) = 16 bytes.</summary>
    private const int HeaderFieldsSize = 16;

    /// <summary>Earth's mean radius in kilometers for Haversine calculation.</summary>
    private const double EarthRadiusKm = 6371.0;

    private readonly List<GeoRegion> _geoRegions = new();
    private readonly List<FederationEntry> _entries = new();
    private readonly Dictionary<Guid, int> _indexByVdeId = new();

    /// <summary>Monotonic generation number for torn-write detection.</summary>
    public uint Generation { get; set; }

    /// <summary>The geographic region of the local VDE.</summary>
    public ushort LocalRegionId { get; set; }

    /// <summary>Number of geographic regions defined.</summary>
    public int GeoRegionCount => _geoRegions.Count;

    /// <summary>Number of VDE federation entries.</summary>
    public int FederationEntryCount => _entries.Count;

    // ── Geo Region Management ───────────────────────────────────────────

    /// <summary>
    /// Adds a geographic region definition.
    /// </summary>
    /// <param name="region">The geographic region to add.</param>
    public void AddGeoRegion(GeoRegion region)
    {
        _geoRegions.Add(region);
    }

    /// <summary>
    /// Looks up a geographic region by ID.
    /// </summary>
    /// <param name="regionId">The region ID to find.</param>
    /// <returns>The matching region, or null if not found.</returns>
    public GeoRegion? GetGeoRegion(ushort regionId)
    {
        for (int i = 0; i < _geoRegions.Count; i++)
        {
            if (_geoRegions[i].RegionId == regionId)
                return _geoRegions[i];
        }
        return null;
    }

    /// <summary>
    /// Returns all geographic regions.
    /// </summary>
    public IReadOnlyList<GeoRegion> GetAllGeoRegions() => _geoRegions;

    // ── Federation Management ───────────────────────────────────────────

    /// <summary>
    /// Registers or updates a VDE in the federation.
    /// If a VDE with the same ID already exists, it is replaced.
    /// </summary>
    /// <param name="entry">The federation entry to register.</param>
    public void RegisterVde(FederationEntry entry)
    {
        if (_indexByVdeId.TryGetValue(entry.VdeId, out int existingIndex))
        {
            _entries[existingIndex] = entry;
        }
        else
        {
            _indexByVdeId[entry.VdeId] = _entries.Count;
            _entries.Add(entry);
        }
    }

    /// <summary>
    /// Removes a VDE from the federation.
    /// </summary>
    /// <param name="vdeId">The VDE ID to remove.</param>
    /// <returns>True if the VDE was found and removed; false otherwise.</returns>
    public bool UnregisterVde(Guid vdeId)
    {
        if (!_indexByVdeId.TryGetValue(vdeId, out int index))
            return false;

        int lastIndex = _entries.Count - 1;
        if (index < lastIndex)
        {
            var lastEntry = _entries[lastIndex];
            _entries[index] = lastEntry;
            _indexByVdeId[lastEntry.VdeId] = index;
        }

        _entries.RemoveAt(lastIndex);
        _indexByVdeId.Remove(vdeId);
        return true;
    }

    /// <summary>
    /// Looks up a VDE by its ID.
    /// </summary>
    /// <param name="vdeId">The VDE ID to find.</param>
    /// <returns>The matching entry, or null if not found.</returns>
    public FederationEntry? GetVde(Guid vdeId)
    {
        if (_indexByVdeId.TryGetValue(vdeId, out int index))
            return _entries[index];
        return null;
    }

    // ── Namespace Resolution (Geo-Aware) ─────────────────────────────────

    /// <summary>
    /// Finds all VDEs serving a dw:// namespace path (prefix match).
    /// Results ordered by: (1) local region first, (2) lowest latency, (3) online status preferred.
    /// </summary>
    /// <param name="namespacePath">The dw:// namespace path to resolve (UTF-8 bytes).</param>
    /// <returns>Matching VDEs ordered by preference.</returns>
    public IReadOnlyList<FederationEntry> ResolveNamespace(byte[] namespacePath)
    {
        if (namespacePath is null)
            throw new ArgumentNullException(nameof(namespacePath));

        var matches = new List<FederationEntry>();

        for (int i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            if (entry.NamespacePath is null) continue;

            // Prefix match: the entry's namespace path is a prefix of the query,
            // or the query is a prefix of the entry's namespace path
            if (IsPrefixMatch(entry.NamespacePath, namespacePath))
                matches.Add(entry);
        }

        // Sort: local region first, then latency, then status
        ushort localId = LocalRegionId;
        matches.Sort((a, b) =>
        {
            // Local region first
            bool aLocal = a.GeoRegionId == localId;
            bool bLocal = b.GeoRegionId == localId;
            if (aLocal != bLocal) return aLocal ? -1 : 1;

            // Lowest latency
            int latencyCompare = a.LatencyMs.CompareTo(b.LatencyMs);
            if (latencyCompare != 0) return latencyCompare;

            // Online preferred (lower status value = better)
            return a.Status.CompareTo(b.Status);
        });

        return matches;
    }

    /// <summary>
    /// Returns the single best VDE for the namespace (first result from ResolveNamespace).
    /// </summary>
    /// <param name="namespacePath">The dw:// namespace path to resolve (UTF-8 bytes).</param>
    /// <returns>The preferred VDE, or null if no match found.</returns>
    public FederationEntry? ResolveNamespacePreferred(byte[] namespacePath)
    {
        var results = ResolveNamespace(namespacePath);
        return results.Count > 0 ? results[0] : null;
    }

    /// <summary>
    /// Returns all VDEs in a geographic region.
    /// </summary>
    /// <param name="geoRegionId">The geographic region ID to filter by.</param>
    /// <returns>All VDEs in this region.</returns>
    public IReadOnlyList<FederationEntry> GetVdesByRegion(ushort geoRegionId)
    {
        var results = new List<FederationEntry>();
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].GeoRegionId == geoRegionId)
                results.Add(_entries[i]);
        }
        return results;
    }

    /// <summary>
    /// Returns all VDEs with Status == Online.
    /// </summary>
    public IReadOnlyList<FederationEntry> GetOnlineVdes()
    {
        var results = new List<FederationEntry>();
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].Status == FederationEntry.StatusOnline)
                results.Add(_entries[i]);
        }
        return results;
    }

    // ── Geo-Routing Helpers ─────────────────────────────────────────────

    /// <summary>
    /// Calculates the Haversine distance between two geographic regions in kilometers.
    /// </summary>
    /// <param name="regionId1">First region ID.</param>
    /// <param name="regionId2">Second region ID.</param>
    /// <returns>Distance in kilometers, or <see cref="double.NaN"/> if either region is unknown.</returns>
    public double CalculateDistance(ushort regionId1, ushort regionId2)
    {
        var r1 = GetGeoRegion(regionId1);
        var r2 = GetGeoRegion(regionId2);
        if (r1 is null || r2 is null)
            return double.NaN;

        return HaversineDistance(r1.Value.Latitude, r1.Value.Longitude,
                                r2.Value.Latitude, r2.Value.Longitude);
    }

    /// <summary>
    /// Returns the nearest N regions to the specified region by Haversine distance.
    /// </summary>
    /// <param name="fromRegionId">The source region ID.</param>
    /// <param name="count">Maximum number of regions to return.</param>
    /// <returns>Nearest regions ordered by distance (excluding the source region).</returns>
    public IReadOnlyList<GeoRegion> GetNearestRegions(ushort fromRegionId, int count)
    {
        var source = GetGeoRegion(fromRegionId);
        if (source is null)
            return Array.Empty<GeoRegion>();

        var distances = new List<(GeoRegion Region, double Distance)>();
        for (int i = 0; i < _geoRegions.Count; i++)
        {
            var r = _geoRegions[i];
            if (r.RegionId == fromRegionId) continue;

            double dist = HaversineDistance(
                source.Value.Latitude, source.Value.Longitude,
                r.Latitude, r.Longitude);
            distances.Add((r, dist));
        }

        distances.Sort((a, b) => a.Distance.CompareTo(b.Distance));

        int resultCount = Math.Min(count, distances.Count);
        var results = new List<GeoRegion>(resultCount);
        for (int i = 0; i < resultCount; i++)
            results.Add(distances[i].Region);

        return results;
    }

    /// <summary>
    /// Computes the Haversine great-circle distance between two lat/lon coordinates.
    /// </summary>
    /// <param name="lat1">Latitude of point 1 in degrees.</param>
    /// <param name="lon1">Longitude of point 1 in degrees.</param>
    /// <param name="lat2">Latitude of point 2 in degrees.</param>
    /// <param name="lon2">Longitude of point 2 in degrees.</param>
    /// <returns>Distance in kilometers.</returns>
    private static double HaversineDistance(double lat1, double lon1, double lat2, double lon2)
    {
        double dLat = DegreesToRadians(lat2 - lat1);
        double dLon = DegreesToRadians(lon2 - lon1);

        double lat1Rad = DegreesToRadians(lat1);
        double lat2Rad = DegreesToRadians(lat2);

        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(lat1Rad) * Math.Cos(lat2Rad)
                 * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);

        double c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

        return EarthRadiusKm * c;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

    /// <summary>
    /// Checks if one byte array is a prefix of the other (bidirectional prefix match).
    /// </summary>
    private static bool IsPrefixMatch(byte[] a, byte[] b)
    {
        int minLen = Math.Min(a.Length, b.Length);
        for (int i = 0; i < minLen; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    // ── Serialization ───────────────────────────────────────────────────

    /// <summary>
    /// Computes the number of blocks required to serialize this federation region.
    /// </summary>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <returns>Number of blocks required (minimum 1).</returns>
    public int RequiredBlocks(int blockSize)
    {
        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);
        int totalDataBytes = HeaderFieldsSize;

        for (int i = 0; i < _geoRegions.Count; i++)
            totalDataBytes += _geoRegions[i].SerializedSize;
        for (int i = 0; i < _entries.Count; i++)
            totalDataBytes += _entries[i].SerializedSize;

        if (totalDataBytes <= payloadSize)
            return 1;

        return (totalDataBytes + payloadSize - 1) / payloadSize;
    }

    /// <summary>
    /// Serializes the federation region into blocks with UniversalBlockTrailer on each block.
    /// </summary>
    /// <param name="buffer">Target buffer (must be at least RequiredBlocks * blockSize bytes).</param>
    /// <param name="blockSize">Block size in bytes.</param>
    public void Serialize(Span<byte> buffer, int blockSize)
    {
        int requiredBlocks = RequiredBlocks(blockSize);
        int totalSize = blockSize * requiredBlocks;
        if (buffer.Length < totalSize)
            throw new ArgumentException($"Buffer must be at least {totalSize} bytes.", nameof(buffer));
        if (blockSize < FormatConstants.MinBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"Block size must be at least {FormatConstants.MinBlockSize} bytes.");

        buffer.Slice(0, totalSize).Clear();

        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);

        // Write header in block 0
        var block0 = buffer.Slice(0, blockSize);
        BinaryPrimitives.WriteInt32LittleEndian(block0, _geoRegions.Count);
        BinaryPrimitives.WriteInt32LittleEndian(block0.Slice(4), _entries.Count);
        BinaryPrimitives.WriteUInt16LittleEndian(block0.Slice(8), LocalRegionId);
        // bytes 10..15 reserved (already zeroed)

        // Sequential writer across blocks
        int currentBlock = 0;
        int offset = HeaderFieldsSize;

        // Write GeoRegion entries
        for (int i = 0; i < _geoRegions.Count; i++)
        {
            int entrySize = _geoRegions[i].SerializedSize;
            if (offset + entrySize > payloadSize && offset > 0)
            {
                // Finalize current block and move to next
                UniversalBlockTrailer.Write(
                    buffer.Slice(currentBlock * blockSize, blockSize),
                    blockSize, BlockTypeTags.XREF, Generation);
                currentBlock++;
                offset = 0;
            }

            _geoRegions[i].WriteTo(buffer.Slice(currentBlock * blockSize, blockSize), offset);
            offset += entrySize;
        }

        // Write FederationEntry entries
        for (int i = 0; i < _entries.Count; i++)
        {
            int entrySize = _entries[i].SerializedSize;
            if (offset + entrySize > payloadSize && offset > 0)
            {
                UniversalBlockTrailer.Write(
                    buffer.Slice(currentBlock * blockSize, blockSize),
                    blockSize, BlockTypeTags.XREF, Generation);
                currentBlock++;
                offset = 0;
            }

            _entries[i].WriteTo(buffer.Slice(currentBlock * blockSize, blockSize), offset);
            offset += entrySize;
        }

        // Write trailers for all blocks
        for (int blk = currentBlock; blk < requiredBlocks; blk++)
        {
            UniversalBlockTrailer.Write(
                buffer.Slice(blk * blockSize, blockSize),
                blockSize, BlockTypeTags.XREF, Generation);
        }
    }

    /// <summary>
    /// Deserializes a federation region from blocks, verifying block trailers
    /// and rebuilding the VDE index.
    /// </summary>
    /// <param name="buffer">Source buffer containing the region blocks.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="blockCount">Number of blocks in the buffer for this region.</param>
    /// <returns>A populated <see cref="VdeFederationRegion"/>.</returns>
    /// <exception cref="InvalidDataException">Thrown if block trailers fail verification.</exception>
    public static VdeFederationRegion Deserialize(ReadOnlySpan<byte> buffer, int blockSize, int blockCount)
    {
        int totalSize = blockSize * blockCount;
        if (buffer.Length < totalSize)
            throw new ArgumentException($"Buffer must be at least {totalSize} bytes.", nameof(buffer));
        if (blockSize < FormatConstants.MinBlockSize)
            throw new ArgumentOutOfRangeException(nameof(blockSize),
                $"Block size must be at least {FormatConstants.MinBlockSize} bytes.");
        if (blockCount < 1)
            throw new ArgumentOutOfRangeException(nameof(blockCount), "At least one block is required.");

        // Verify all block trailers
        for (int blk = 0; blk < blockCount; blk++)
        {
            var block = buffer.Slice(blk * blockSize, blockSize);
            if (!UniversalBlockTrailer.Verify(block, blockSize))
                throw new InvalidDataException($"VDE Federation block {blk} trailer verification failed.");
        }

        var block0 = buffer.Slice(0, blockSize);
        var trailer = UniversalBlockTrailer.Read(block0, blockSize);

        int geoRegionCount = BinaryPrimitives.ReadInt32LittleEndian(block0);
        int entryCount = BinaryPrimitives.ReadInt32LittleEndian(block0.Slice(4));
        ushort localRegionId = BinaryPrimitives.ReadUInt16LittleEndian(block0.Slice(8));

        var region = new VdeFederationRegion
        {
            Generation = trailer.GenerationNumber,
            LocalRegionId = localRegionId
        };

        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);

        // Build a contiguous span of all payload bytes for sequential reading
        // Since entries are variable-size, we read sequentially across blocks
        int currentBlock = 0;
        int offset = HeaderFieldsSize;

        // Read GeoRegion entries
        for (int i = 0; i < geoRegionCount; i++)
        {
            // Check if we need to advance to next block
            // Minimum GeoRegion size is 20 bytes (no name)
            if (offset >= payloadSize)
            {
                currentBlock++;
                offset = 0;
            }

            var blockSpan = buffer.Slice(currentBlock * blockSize, blockSize);
            var geoRegion = GeoRegion.ReadFrom(blockSpan, offset, out int bytesRead);
            region._geoRegions.Add(geoRegion);
            offset += bytesRead;
        }

        // Read FederationEntry entries
        for (int i = 0; i < entryCount; i++)
        {
            // Minimum FederationEntry size is 32 bytes (no namespace)
            if (offset >= payloadSize)
            {
                currentBlock++;
                offset = 0;
            }

            var blockSpan = buffer.Slice(currentBlock * blockSize, blockSize);
            var entry = FederationEntry.ReadFrom(blockSpan, offset, out int bytesRead);
            region._indexByVdeId[entry.VdeId] = region._entries.Count;
            region._entries.Add(entry);
            offset += bytesRead;
        }

        return region;
    }
}
