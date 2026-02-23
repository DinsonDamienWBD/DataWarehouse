using System.Buffers.Binary;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.SDK.VirtualDiskEngine.Regions;

/// <summary>
/// A single compute module entry in the Compute Code Cache, storing a WASM
/// module directory record with SHA-256 hash-based content addressing.
/// </summary>
/// <remarks>
/// Serialized layout:
/// [ModuleHash:32][ModuleId:16][BlockOffset:8 LE][BlockCount:4 LE]
/// [ModuleSizeBytes:4 LE][AbiVersion:2 LE][RegisteredUtcTicks:8 LE]
/// [EntryPointNameLength:2 LE][EntryPointName:variable, max 128]
///
/// Fixed overhead: 76 bytes + variable EntryPointName.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 73: VDE regions -- Compute Code Cache entry")]
public readonly record struct ComputeModuleEntry
{
    /// <summary>Fixed portion of the serialized size (excluding variable fields): 76 bytes.</summary>
    public const int FixedSize = 76; // 32+16+8+4+4+2+8+2

    /// <summary>Required length of the SHA-256 module hash in bytes.</summary>
    public const int HashSize = 32;

    /// <summary>Maximum length of the entry point name in bytes.</summary>
    public const int MaxEntryPointNameLength = 128;

    /// <summary>32-byte SHA-256 hash of the WASM module content (the lookup key).</summary>
    public byte[] ModuleHash { get; init; }

    /// <summary>Unique module identifier.</summary>
    public Guid ModuleId { get; init; }

    /// <summary>Starting block where module data is stored in the VDE.</summary>
    public long BlockOffset { get; init; }

    /// <summary>Number of blocks the module occupies.</summary>
    public int BlockCount { get; init; }

    /// <summary>Exact byte size of the WASM binary.</summary>
    public int ModuleSizeBytes { get; init; }

    /// <summary>
    /// WASI ABI version.
    /// 0=preview1, 1=preview2, 2=stable.
    /// </summary>
    public ushort AbiVersion { get; init; }

    /// <summary>UTC ticks when the module was registered.</summary>
    public long RegisteredUtcTicks { get; init; }

    /// <summary>UTF-8 entry point function name (max 128 bytes, variable length).</summary>
    public byte[] EntryPointName { get; init; }

    /// <summary>
    /// Serialized size of this entry in bytes.
    /// </summary>
    public int SerializedSize => FixedSize + (EntryPointName?.Length ?? 0);

    /// <summary>Writes this entry to the buffer at the specified offset, returning bytes written.</summary>
    internal int WriteTo(Span<byte> buffer, int offset)
    {
        int start = offset;

        var hash = ModuleHash ?? new byte[HashSize];
        hash.AsSpan(0, Math.Min(hash.Length, HashSize)).CopyTo(buffer.Slice(offset, HashSize));
        offset += HashSize;

        ModuleId.TryWriteBytes(buffer.Slice(offset, 16));
        offset += 16;

        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), BlockOffset);
        offset += 8;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(offset), BlockCount);
        offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(offset), ModuleSizeBytes);
        offset += 4;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset), AbiVersion);
        offset += 2;
        BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset), RegisteredUtcTicks);
        offset += 8;

        var entryPoint = EntryPointName ?? Array.Empty<byte>();
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.Slice(offset), (ushort)entryPoint.Length);
        offset += 2;
        entryPoint.AsSpan().CopyTo(buffer.Slice(offset));
        offset += entryPoint.Length;

        return offset - start;
    }

    /// <summary>Reads an entry from the buffer at the specified offset.</summary>
    internal static ComputeModuleEntry ReadFrom(ReadOnlySpan<byte> buffer, int offset, out int bytesRead)
    {
        int start = offset;

        byte[] moduleHash = buffer.Slice(offset, HashSize).ToArray();
        offset += HashSize;

        var moduleId = new Guid(buffer.Slice(offset, 16));
        offset += 16;

        long blockOffset = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;
        int blockCount = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset));
        offset += 4;
        int moduleSizeBytes = BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(offset));
        offset += 4;
        ushort abiVersion = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset));
        offset += 2;
        long registeredUtcTicks = BinaryPrimitives.ReadInt64LittleEndian(buffer.Slice(offset));
        offset += 8;

        ushort entryPointLen = BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(offset));
        offset += 2;
        byte[] entryPointName = buffer.Slice(offset, entryPointLen).ToArray();
        offset += entryPointLen;

        bytesRead = offset - start;
        return new ComputeModuleEntry
        {
            ModuleHash = moduleHash,
            ModuleId = moduleId,
            BlockOffset = blockOffset,
            BlockCount = blockCount,
            ModuleSizeBytes = moduleSizeBytes,
            AbiVersion = abiVersion,
            RegisteredUtcTicks = registeredUtcTicks,
            EntryPointName = entryPointName
        };
    }
}

/// <summary>
/// Compute Code Cache Region: stores <see cref="ComputeModuleEntry"/> records
/// for WASM modules with O(1) hash-based retrieval by SHA-256 content hash.
/// Serialized using <see cref="BlockTypeTags.CODE"/> type tag.
/// </summary>
/// <remarks>
/// Serialization layout:
///   Block 0 (header): [ModuleCount:4 LE][Reserved:12][ComputeModuleEntry entries...]
///     Entries are packed sequentially and overflow to block 1+ if needed.
///   Each block ends with [UniversalBlockTrailer].
///
/// Modules are content-addressed: the SHA-256 hash of the WASM binary uniquely
/// identifies each module. Duplicate hashes are rejected on registration.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 73: VDE regions -- Compute Code Cache (VREG-12)")]
public sealed class ComputeCodeCacheRegion
{
    /// <summary>Size of the header fields at the start of block 0: ModuleCount(4) + Reserved(12) = 16 bytes.</summary>
    private const int HeaderFieldsSize = 16;

    private readonly List<ComputeModuleEntry> _modules = new();
    private readonly Dictionary<string, int> _indexByHash = new(StringComparer.Ordinal);

    /// <summary>Monotonic generation number for torn-write detection.</summary>
    public uint Generation { get; set; }

    /// <summary>Number of modules stored in this cache.</summary>
    public int ModuleCount => _modules.Count;

    /// <summary>
    /// Converts a SHA-256 hash byte array to its uppercase hex string representation.
    /// </summary>
    private static string HashToHex(byte[] hash) => Convert.ToHexString(hash);

    /// <summary>
    /// Registers a WASM module in the code cache.
    /// </summary>
    /// <param name="entry">The module entry to register.</param>
    /// <exception cref="ArgumentException">
    /// Thrown if the module hash is not 32 bytes, entry point name exceeds 128 bytes,
    /// or a module with the same hash is already registered.
    /// </exception>
    public void RegisterModule(ComputeModuleEntry entry)
    {
        if (entry.ModuleHash is null || entry.ModuleHash.Length != ComputeModuleEntry.HashSize)
            throw new ArgumentException(
                $"ModuleHash must be exactly {ComputeModuleEntry.HashSize} bytes.",
                nameof(entry));

        if (entry.EntryPointName is not null && entry.EntryPointName.Length > ComputeModuleEntry.MaxEntryPointNameLength)
            throw new ArgumentException(
                $"EntryPointName length {entry.EntryPointName.Length} exceeds maximum {ComputeModuleEntry.MaxEntryPointNameLength}.",
                nameof(entry));

        string hexKey = HashToHex(entry.ModuleHash);
        if (_indexByHash.ContainsKey(hexKey))
            throw new ArgumentException(
                $"A module with hash {hexKey} is already registered.",
                nameof(entry));

        _indexByHash[hexKey] = _modules.Count;
        _modules.Add(entry);
    }

    /// <summary>
    /// Retrieves a module by its SHA-256 content hash in O(1) time.
    /// </summary>
    /// <param name="moduleHash">The 32-byte SHA-256 hash to look up.</param>
    /// <returns>The matching module entry, or null if not found.</returns>
    public ComputeModuleEntry? GetByHash(byte[] moduleHash)
    {
        if (moduleHash is null || moduleHash.Length != ComputeModuleEntry.HashSize)
            return null;

        string hexKey = HashToHex(moduleHash);
        if (_indexByHash.TryGetValue(hexKey, out int index))
            return _modules[index];

        return null;
    }

    /// <summary>
    /// Retrieves a module by its unique module identifier (linear scan, secondary lookup).
    /// </summary>
    /// <param name="moduleId">The module GUID to look up.</param>
    /// <returns>The matching module entry, or null if not found.</returns>
    public ComputeModuleEntry? GetByModuleId(Guid moduleId)
    {
        for (int i = 0; i < _modules.Count; i++)
        {
            if (_modules[i].ModuleId == moduleId)
                return _modules[i];
        }
        return null;
    }

    /// <summary>
    /// Removes a module by its SHA-256 content hash using swap-with-last for O(1) removal.
    /// </summary>
    /// <param name="moduleHash">The 32-byte SHA-256 hash of the module to remove.</param>
    /// <returns>True if the module was found and removed; false otherwise.</returns>
    public bool RemoveModule(byte[] moduleHash)
    {
        if (moduleHash is null || moduleHash.Length != ComputeModuleEntry.HashSize)
            return false;

        string hexKey = HashToHex(moduleHash);
        if (!_indexByHash.TryGetValue(hexKey, out int index))
            return false;

        int lastIndex = _modules.Count - 1;
        if (index < lastIndex)
        {
            // Swap with last element to maintain contiguous storage
            var lastEntry = _modules[lastIndex];
            _modules[index] = lastEntry;
            _indexByHash[HashToHex(lastEntry.ModuleHash)] = index;
        }

        _modules.RemoveAt(lastIndex);
        _indexByHash.Remove(hexKey);
        return true;
    }

    /// <summary>
    /// Returns all registered modules.
    /// </summary>
    public IReadOnlyList<ComputeModuleEntry> GetAllModules() => _modules.AsReadOnly();

    /// <summary>
    /// Computes the number of blocks required to serialize this cache.
    /// </summary>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <returns>Number of blocks required.</returns>
    public int RequiredBlocks(int blockSize)
    {
        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);
        if (_modules.Count == 0)
            return 1;

        int totalModuleBytes = 0;
        for (int i = 0; i < _modules.Count; i++)
            totalModuleBytes += _modules[i].SerializedSize;

        int availableInBlock0 = payloadSize - HeaderFieldsSize;
        if (totalModuleBytes <= availableInBlock0)
            return 1;

        int overflowBytes = totalModuleBytes - availableInBlock0;
        int overflowBlocks = (overflowBytes + payloadSize - 1) / payloadSize;
        return 1 + overflowBlocks;
    }

    /// <summary>
    /// Serializes the compute code cache into blocks with UniversalBlockTrailer on each block.
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

        // Block 0: Header + module entries
        var block0 = buffer.Slice(0, blockSize);
        BinaryPrimitives.WriteInt32LittleEndian(block0, _modules.Count);
        // bytes 4..15 reserved (already zeroed)

        int offset = HeaderFieldsSize;
        int currentBlock = 0;
        int moduleIndex = 0;

        while (moduleIndex < _modules.Count)
        {
            var block = buffer.Slice(currentBlock * blockSize, blockSize);
            int blockPayloadEnd = payloadSize;

            if (currentBlock > 0)
                offset = 0;

            while (moduleIndex < _modules.Count)
            {
                int entrySize = _modules[moduleIndex].SerializedSize;
                if (offset + entrySize > blockPayloadEnd)
                    break;

                _modules[moduleIndex].WriteTo(block, offset);
                offset += entrySize;
                moduleIndex++;
            }

            UniversalBlockTrailer.Write(block, blockSize, BlockTypeTags.CODE, Generation);

            if (moduleIndex < _modules.Count)
            {
                currentBlock++;
                offset = 0;
            }
        }

        // Write trailer for block 0 if no modules
        if (_modules.Count == 0)
            UniversalBlockTrailer.Write(block0, blockSize, BlockTypeTags.CODE, Generation);

        // Write trailers for any remaining blocks
        for (int blk = currentBlock + 1; blk < requiredBlocks; blk++)
        {
            var block = buffer.Slice(blk * blockSize, blockSize);
            UniversalBlockTrailer.Write(block, blockSize, BlockTypeTags.CODE, Generation);
        }
    }

    /// <summary>
    /// Deserializes a compute code cache region from blocks, verifying block trailers.
    /// </summary>
    /// <param name="buffer">Source buffer containing the region blocks.</param>
    /// <param name="blockSize">Block size in bytes.</param>
    /// <param name="blockCount">Number of blocks in the buffer for this region.</param>
    /// <returns>A populated <see cref="ComputeCodeCacheRegion"/>.</returns>
    /// <exception cref="InvalidDataException">Thrown if block trailers fail verification.</exception>
    public static ComputeCodeCacheRegion Deserialize(ReadOnlySpan<byte> buffer, int blockSize, int blockCount)
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
                throw new InvalidDataException($"Compute Code Cache block {blk} trailer verification failed.");
        }

        var block0 = buffer.Slice(0, blockSize);
        var trailer = UniversalBlockTrailer.Read(block0, blockSize);

        int moduleCount = BinaryPrimitives.ReadInt32LittleEndian(block0);

        var region = new ComputeCodeCacheRegion
        {
            Generation = trailer.GenerationNumber
        };

        int payloadSize = UniversalBlockTrailer.PayloadSize(blockSize);
        int offset = HeaderFieldsSize;
        int currentBlock = 0;
        int modulesRead = 0;

        while (modulesRead < moduleCount)
        {
            var block = buffer.Slice(currentBlock * blockSize, blockSize);
            int blockPayloadEnd = payloadSize;

            while (modulesRead < moduleCount && offset < blockPayloadEnd)
            {
                // Check if there's enough room for at least the fixed portion
                if (offset + ComputeModuleEntry.FixedSize > blockPayloadEnd)
                    break;

                var entry = ComputeModuleEntry.ReadFrom(block, offset, out int bytesRead);
                region._indexByHash[HashToHex(entry.ModuleHash)] = region._modules.Count;
                region._modules.Add(entry);
                offset += bytesRead;
                modulesRead++;
            }

            currentBlock++;
            offset = 0;
        }

        return region;
    }
}
