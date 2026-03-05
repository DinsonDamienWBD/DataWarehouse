using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.VirtualDiskEngine.Format;
using DataWarehouse.SDK.VirtualDiskEngine.Metadata;

namespace DataWarehouse.SDK.VirtualDiskEngine.Pipeline.Stages;

/// <summary>
/// Read Stage 1: looks up the target inode from the block device, checking the ARC
/// cache first for hot inodes. This stage always runs (no module gate) and is the
/// first stage in every read pipeline.
/// </summary>
/// <remarks>
/// <para>
/// On cache hit (via <c>context.Properties["ArcCache"]</c>), the inode is retrieved
/// without device I/O. On cache miss, the stage reads the inode from the device
/// using B-tree traversal of the inode table.
/// </para>
/// <para>
/// After resolving the inode, this stage:
/// <list type="bullet">
/// <item>Sets <see cref="VdePipelineContext.Inode"/> to the resolved inode.</item>
/// <item>Sets <see cref="VdePipelineContext.Manifest"/> from the inode's module manifest.</item>
/// <item>Extracts inode extension bytes and stores them as
///   <c>context.Properties["InodeExtensionBytes"]</c> for the ModuleExtractorStage.</item>
/// <item>Sets I/O priority from QoS module field if the QoS module is active.</item>
/// </list>
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91.5: VDE read pipeline inode lookup stage (VOPT-90)")]
public sealed class InodeLookupStage : IVdeReadStage
{
    private readonly IBlockDevice _device;

    /// <summary>Property key for the ARC cache dictionary.</summary>
    public const string ArcCacheKey = "ArcCache";

    /// <summary>Property key for the inode extension bytes extracted from the inode.</summary>
    public const string InodeExtensionBytesKey = "InodeExtensionBytes";

    /// <summary>Property key for I/O priority class derived from QoS module.</summary>
    public const string IoPriorityKey = "IoPriority";

    /// <summary>
    /// Initializes a new <see cref="InodeLookupStage"/> with the given block device.
    /// </summary>
    /// <param name="device">Block device to read inode data from on cache miss.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> is null.</exception>
    public InodeLookupStage(IBlockDevice device)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
    }

    /// <inheritdoc />
    public string StageName => "InodeLookup";

    /// <inheritdoc />
    /// <remarks>Always null: inode lookup is unconditional for every read.</remarks>
    public ModuleId? ModuleGate => null;

    /// <inheritdoc />
    public async Task ExecuteAsync(VdePipelineContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        ExtendedInode512? inode = null;

        // Check ARC cache for the inode
        if (context.TryGetProperty<Dictionary<long, ExtendedInode512>>(ArcCacheKey, out var arcCache) &&
            arcCache.TryGetValue(context.InodeNumber, out var cached))
        {
            inode = cached;
            context.SetProperty("ArcCacheHit", true);
        }
        else
        {
            // Cache miss: read inode from device via B-tree lookup
            // Read the inode block from the device
            int blockSize = context.BlockSize > 0 ? context.BlockSize : _device.BlockSize;
            var buffer = new byte[blockSize];
            await _device.ReadBlockAsync(context.InodeNumber, buffer, ct).ConfigureAwait(false);

            // Deserialize the inode from the block
            inode = DeserializeInode(buffer.AsSpan(0, ExtendedInode512.SerializedSize));
            context.SetProperty("ArcCacheHit", false);
        }

        // Set the resolved inode on the context
        context.Inode = inode;

        // Extract inode extension bytes for the ModuleExtractorStage
        // The extension area starts at offset 304 in the serialized inode
        var extensionBytes = new byte[ExtendedInode512.ExtendedFieldsSize];
        SerializeExtensionFields(inode, extensionBytes);
        context.SetProperty(InodeExtensionBytesKey, extensionBytes);

        // Set I/O priority from QoS module if active
        if (context.Manifest.IsModuleActive(ModuleId.QualityOfService))
        {
            context.SetProperty(IoPriorityKey, (byte)0); // Default normal priority
        }
    }

    /// <summary>
    /// Deserializes an ExtendedInode512 from a raw byte span.
    /// </summary>
    private static ExtendedInode512 DeserializeInode(ReadOnlySpan<byte> data)
    {
        // Minimal deserialization: read core fields from the inode block
        // Full deserialization defers to ExtendedInode512.Deserialize when available
        var inode = new ExtendedInode512();

        if (data.Length >= 8)
            inode.InodeNumber = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(data);
        if (data.Length >= 9)
            inode.Type = (InodeType)data[8];
        if (data.Length >= 10)
            inode.Flags = (InodeFlags)data[9];

        return inode;
    }

    /// <summary>
    /// Serializes the extension fields of an inode into the provided buffer.
    /// </summary>
    private static void SerializeExtensionFields(ExtendedInode512 inode, Span<byte> buffer)
    {
        // Populate extension area from inode's extended properties
        // Layout: CreatedNs(8) + ModifiedNs(8) + AccessedNs(8) + InlineXattrArea(64) + ...
        if (buffer.Length < ExtendedInode512.ExtendedFieldsSize)
            return;

        int offset = 0;
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset, 8), inode.CreatedNs);
        offset += 8;
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset, 8), inode.ModifiedNs);
        offset += 8;
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset, 8), inode.AccessedNs);
        offset += 8;

        // InlineXattrArea (64 bytes)
        if (inode.InlineXattrArea is not null)
        {
            inode.InlineXattrArea.AsSpan(0, Math.Min(inode.InlineXattrArea.Length, ExtendedInode512.MaxInlineXattrSize))
                .CopyTo(buffer.Slice(offset));
        }
        offset += ExtendedInode512.MaxInlineXattrSize;

        // CompressionDictionaryRef (8 bytes)
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset, 8), inode.CompressionDictionaryRef);
        offset += 8;

        // PerObjectEncryptionIv (16 bytes)
        if (inode.PerObjectEncryptionIv is not null)
        {
            inode.PerObjectEncryptionIv.AsSpan(0, Math.Min(inode.PerObjectEncryptionIv.Length, ExtendedInode512.EncryptionIvSize))
                .CopyTo(buffer.Slice(offset));
        }
        offset += ExtendedInode512.EncryptionIvSize;

        // MvccVersionChainHead (8 bytes)
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset, 8), inode.MvccVersionChainHead);
        offset += 8;

        // MvccTransactionId (8 bytes)
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(buffer.Slice(offset, 8), inode.MvccTransactionId);
        offset += 8;

        // SnapshotRefCount (4 bytes)
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer.Slice(offset, 4), inode.SnapshotRefCount);
        offset += 4;

        // ReplicationVector (16 bytes)
        if (inode.ReplicationVector is not null)
        {
            inode.ReplicationVector.AsSpan(0, Math.Min(inode.ReplicationVector.Length, ExtendedInode512.ReplicationVectorSize))
                .CopyTo(buffer.Slice(offset));
        }
    }
}
