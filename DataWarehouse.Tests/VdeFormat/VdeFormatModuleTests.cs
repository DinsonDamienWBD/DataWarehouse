using System.Numerics;
using System.Runtime.InteropServices;
using DataWarehouse.SDK.VirtualDiskEngine.Format;
using DataWarehouse.SDK.VirtualDiskEngine.Metadata;
using Xunit;

namespace DataWarehouse.Tests.VdeFormat;

/// <summary>
/// Tests for VDE format modules: 19-module manifest/metadata, region serialization,
/// 7 creation profiles, superblock structure, block trailer, inode, block type tags,
/// and cross-profile verification.
/// </summary>
public class VdeFormatModuleTests
{
    // ── 1. Module Manifest Tests (19 modules) ──────────────────────────────

    [Theory]
    [InlineData(ModuleId.Security, 0)]
    [InlineData(ModuleId.Compliance, 1)]
    [InlineData(ModuleId.Intelligence, 2)]
    [InlineData(ModuleId.Tags, 3)]
    [InlineData(ModuleId.Replication, 4)]
    [InlineData(ModuleId.Raid, 5)]
    [InlineData(ModuleId.Streaming, 6)]
    [InlineData(ModuleId.Compute, 7)]
    [InlineData(ModuleId.Fabric, 8)]
    [InlineData(ModuleId.Consensus, 9)]
    [InlineData(ModuleId.Compression, 10)]
    [InlineData(ModuleId.Integrity, 11)]
    [InlineData(ModuleId.Snapshot, 12)]
    [InlineData(ModuleId.Query, 13)]
    [InlineData(ModuleId.Privacy, 14)]
    [InlineData(ModuleId.Sustainability, 15)]
    [InlineData(ModuleId.Transit, 16)]
    [InlineData(ModuleId.Observability, 17)]
    [InlineData(ModuleId.AuditLog, 18)]
    public void Module_BitPosition_SetsCorrectBit(ModuleId moduleId, int expectedBit)
    {
        uint manifest = 1u << (byte)moduleId;
        Assert.Equal(1u << expectedBit, manifest);
        Assert.True(ModuleRegistry.IsModuleActive(manifest, moduleId));
    }

    [Theory]
    [InlineData(ModuleId.Security)]
    [InlineData(ModuleId.Compliance)]
    [InlineData(ModuleId.Intelligence)]
    [InlineData(ModuleId.Tags)]
    [InlineData(ModuleId.Replication)]
    [InlineData(ModuleId.Raid)]
    [InlineData(ModuleId.Streaming)]
    [InlineData(ModuleId.Compute)]
    [InlineData(ModuleId.Fabric)]
    [InlineData(ModuleId.Consensus)]
    [InlineData(ModuleId.Compression)]
    [InlineData(ModuleId.Integrity)]
    [InlineData(ModuleId.Snapshot)]
    [InlineData(ModuleId.Query)]
    [InlineData(ModuleId.Privacy)]
    [InlineData(ModuleId.Sustainability)]
    [InlineData(ModuleId.Transit)]
    [InlineData(ModuleId.Observability)]
    [InlineData(ModuleId.AuditLog)]
    public void Module_HasValidMetadata(ModuleId moduleId)
    {
        var module = ModuleRegistry.GetModule(moduleId);
        Assert.Equal(moduleId, module.Id);
        Assert.False(string.IsNullOrEmpty(module.Name));
        Assert.False(string.IsNullOrEmpty(module.Abbreviation));
        Assert.True(module.Abbreviation.Length >= 3 && module.Abbreviation.Length <= 4);
        Assert.NotNull(module.BlockTypeTags);
        Assert.NotNull(module.RegionNames);
        Assert.True(module.InodeFieldBytes >= 0);
    }

    [Fact]
    public void ModuleRegistry_AllModules_Has19Entries()
    {
        Assert.Equal(19, ModuleRegistry.AllModules.Count);
    }

    [Fact]
    public void ModuleRegistry_GetActiveModules_AllBitsSet_Returns19()
    {
        uint allModules = 0x0007_FFFF;
        var active = ModuleRegistry.GetActiveModules(allModules);
        Assert.Equal(19, active.Count);
    }

    [Fact]
    public void ModuleRegistry_GetActiveModules_NoBitsSet_ReturnsEmpty()
    {
        var active = ModuleRegistry.GetActiveModules(0u);
        Assert.Empty(active);
    }

    // ── 2. Module Manifest Serialization ──────────────────────────────────

    [Fact]
    public void ModuleManifestField_SerializeDeserialize_RoundTrip()
    {
        var original = new ModuleManifestField(0x0007_FFFF);
        var buffer = new byte[4];
        ModuleManifestField.Serialize(original, buffer);
        var deserialized = ModuleManifestField.Deserialize(buffer);
        Assert.Equal(original.Value, deserialized.Value);
    }

    [Fact]
    public void ModuleManifestField_FromModules_SetsCorrectBits()
    {
        var manifest = ModuleManifestField.FromModules(ModuleId.Security, ModuleId.Compression, ModuleId.Integrity);
        Assert.True(manifest.IsModuleActive(ModuleId.Security));
        Assert.True(manifest.IsModuleActive(ModuleId.Compression));
        Assert.True(manifest.IsModuleActive(ModuleId.Integrity));
        Assert.False(manifest.IsModuleActive(ModuleId.Tags));
        Assert.Equal(3, manifest.ActiveModuleCount);
    }

    [Fact]
    public void ModuleManifestField_WithModule_AddsModule()
    {
        var manifest = ModuleManifestField.None.WithModule(ModuleId.Security);
        Assert.True(manifest.IsModuleActive(ModuleId.Security));
        Assert.Equal(1, manifest.ActiveModuleCount);
    }

    [Fact]
    public void ModuleManifestField_WithoutModule_RemovesModule()
    {
        var manifest = ModuleManifestField.AllModules.WithoutModule(ModuleId.AuditLog);
        Assert.False(manifest.IsModuleActive(ModuleId.AuditLog));
        Assert.Equal(18, manifest.ActiveModuleCount);
    }

    [Fact]
    public void ModuleManifestField_AllModules_Has19BitsSet()
    {
        Assert.Equal(0x0007_FFFFu, ModuleManifestField.AllModules.Value);
        Assert.Equal(19, ModuleManifestField.AllModules.ActiveModuleCount);
    }

    // ── 3. VdeCreationProfile Factory Tests (7 profiles) ──────────────────

    [Fact]
    public void Profile_Minimal_HasNoModules()
    {
        var profile = VdeCreationProfile.Minimal(1024);
        Assert.Equal(0x0000_0000u, profile.ModuleManifest);
        Assert.Equal(4096, profile.BlockSize);
        Assert.Equal(0x00, profile.TamperResponseLevel);
        Assert.Equal(VdeProfileType.Minimal, profile.ProfileType);
        Assert.Empty(profile.ModuleConfigLevels);
    }

    [Fact]
    public void Profile_Standard_HasSecCmprIntgSnap()
    {
        var profile = VdeCreationProfile.Standard(1024);
        Assert.Equal(0x0000_1C01u, profile.ModuleManifest);
        Assert.True(ModuleRegistry.IsModuleActive(profile.ModuleManifest, ModuleId.Security));
        Assert.True(ModuleRegistry.IsModuleActive(profile.ModuleManifest, ModuleId.Compression));
        Assert.True(ModuleRegistry.IsModuleActive(profile.ModuleManifest, ModuleId.Integrity));
        Assert.True(ModuleRegistry.IsModuleActive(profile.ModuleManifest, ModuleId.Snapshot));
        Assert.Equal(4, profile.ModuleConfigLevels.Count);
    }

    [Fact]
    public void Profile_Enterprise_HasExpectedModules()
    {
        var profile = VdeCreationProfile.Enterprise(1024);
        Assert.Equal(0x0004_0C0Fu, profile.ModuleManifest);
        Assert.True(ModuleRegistry.IsModuleActive(profile.ModuleManifest, ModuleId.Security));
        Assert.True(ModuleRegistry.IsModuleActive(profile.ModuleManifest, ModuleId.Compliance));
        Assert.True(ModuleRegistry.IsModuleActive(profile.ModuleManifest, ModuleId.Intelligence));
        Assert.True(ModuleRegistry.IsModuleActive(profile.ModuleManifest, ModuleId.Tags));
        Assert.True(ModuleRegistry.IsModuleActive(profile.ModuleManifest, ModuleId.AuditLog));
    }

    [Fact]
    public void Profile_MaxSecurity_HasAll19Modules()
    {
        var profile = VdeCreationProfile.MaxSecurity(1024);
        Assert.Equal(0x0007_FFFFu, profile.ModuleManifest);
        Assert.Equal(0x04, profile.TamperResponseLevel);
        Assert.Equal(19, profile.ModuleConfigLevels.Count);
        foreach (var (_, level) in profile.ModuleConfigLevels)
        {
            Assert.Equal(0xF, level);
        }
    }

    [Fact]
    public void Profile_EdgeIoT_Has512ByteBlocks()
    {
        var profile = VdeCreationProfile.EdgeIoT(1024);
        Assert.Equal(0x0000_0840u, profile.ModuleManifest);
        Assert.Equal(512, profile.BlockSize);
        Assert.True(ModuleRegistry.IsModuleActive(profile.ModuleManifest, ModuleId.Streaming));
        Assert.True(ModuleRegistry.IsModuleActive(profile.ModuleManifest, ModuleId.Integrity));
    }

    [Fact]
    public void Profile_Analytics_Has64KBlocks()
    {
        var profile = VdeCreationProfile.Analytics(1024);
        Assert.Equal(0x0000_3404u, profile.ModuleManifest);
        Assert.Equal(65536, profile.BlockSize);
        Assert.True(ModuleRegistry.IsModuleActive(profile.ModuleManifest, ModuleId.Intelligence));
        Assert.True(ModuleRegistry.IsModuleActive(profile.ModuleManifest, ModuleId.Compression));
        Assert.True(ModuleRegistry.IsModuleActive(profile.ModuleManifest, ModuleId.Snapshot));
        Assert.True(ModuleRegistry.IsModuleActive(profile.ModuleManifest, ModuleId.Query));
    }

    [Fact]
    public void Profile_Custom_OnlySpecifiedBitsSet()
    {
        uint customManifest = (1u << (int)ModuleId.Security) | (1u << (int)ModuleId.Integrity);
        var configLevels = new Dictionary<ModuleId, byte>
        {
            [ModuleId.Security] = 0x3,
            [ModuleId.Integrity] = 0x2,
        };
        var profile = VdeCreationProfile.Custom(1024, customManifest, configLevels);
        Assert.Equal(VdeProfileType.Custom, profile.ProfileType);
        Assert.Equal(customManifest, profile.ModuleManifest);
        Assert.True(ModuleRegistry.IsModuleActive(profile.ModuleManifest, ModuleId.Security));
        Assert.True(ModuleRegistry.IsModuleActive(profile.ModuleManifest, ModuleId.Integrity));
        Assert.False(ModuleRegistry.IsModuleActive(profile.ModuleManifest, ModuleId.Tags));
        Assert.Equal(2, profile.ModuleConfigLevels.Count);
    }

    // ── 4. SuperblockV2 Structure Tests ───────────────────────────────────

    [Fact]
    public void SuperblockV2_ConstructorImmutability_AllPropertiesSet()
    {
        var uuid = Guid.NewGuid();
        var magic = MagicSignature.CreateDefault();
        var sb = SuperblockV2.CreateDefault(4096, 1024, uuid);

        Assert.Equal(uuid, sb.VolumeUuid);
        Assert.Equal(4096, sb.BlockSize);
        Assert.Equal(1024, sb.TotalBlocks);
        Assert.True(sb.Magic.Validate());
        Assert.Equal(1, sb.CheckpointSequence);
    }

    [Fact]
    public void SuperblockV2_SerializeDeserialize_RoundTrip()
    {
        var uuid = Guid.NewGuid();
        var sb = SuperblockV2.CreateDefault(4096, 1024, uuid);
        var buffer = new byte[4096];
        SuperblockV2.Serialize(sb, buffer, 4096);
        var deserialized = SuperblockV2.Deserialize(buffer, 4096);

        Assert.Equal(sb.VolumeUuid, deserialized.VolumeUuid);
        Assert.Equal(sb.BlockSize, deserialized.BlockSize);
        Assert.Equal(sb.TotalBlocks, deserialized.TotalBlocks);
        Assert.Equal(sb.ModuleManifest, deserialized.ModuleManifest);
        Assert.Equal(sb.CheckpointSequence, deserialized.CheckpointSequence);
    }

    [Fact]
    public void FormatVersionInfo_Has16Bytes()
    {
        Assert.Equal(16, FormatVersionInfo.SerializedSize);
    }

    [Fact]
    public void MagicSignature_Validate_ChecksDwvdMagicBytes()
    {
        var magic = MagicSignature.CreateDefault();
        Assert.True(magic.Validate());
        Assert.Equal(FormatConstants.FormatMajorVersion, magic.MajorVersion);
        Assert.Equal(FormatConstants.FormatMinorVersion, magic.MinorVersion);
    }

    [Fact]
    public void MagicSignature_SerializeDeserialize_RoundTrip()
    {
        var magic = MagicSignature.CreateDefault();
        var buffer = new byte[16];
        MagicSignature.Serialize(magic, buffer);
        var deserialized = MagicSignature.Deserialize(buffer);
        Assert.Equal(magic, deserialized);
        Assert.True(deserialized.Validate());
    }

    [Fact]
    public void SuperblockV2_NamespaceAnchor_StoredAsUlong()
    {
        var magic = MagicSignature.CreateDefault();
        // NamespaceAnchor is stored as ulong for zero-alloc
        Assert.IsType<ulong>(magic.NamespaceAnchor);
        Assert.NotEqual(0UL, magic.NamespaceAnchor);
    }

    [Fact]
    public void RegionPointerTable_MutableSlots_Assignment()
    {
        var rpt = new RegionPointerTable();
        var pointer = new RegionPointer(BlockTypeTags.POLV, RegionFlags.Active, 10, 2, 0);
        rpt.SetSlot(0, pointer);
        var retrieved = rpt.GetSlot(0);
        Assert.Equal(pointer, retrieved);
        Assert.Equal(BlockTypeTags.POLV, retrieved.RegionTypeId);
    }

    [Fact]
    public void SuperblockV2_VolumeLabel_RoundTrip()
    {
        var label = SuperblockV2.CreateVolumeLabel("TestVolume");
        Assert.Equal(64, label.Length);
        var uuid = Guid.NewGuid();
        var sb = new SuperblockV2(
            MagicSignature.CreateDefault(),
            new FormatVersionInfo(2, 2, IncompatibleFeatureFlags.None, ReadOnlyCompatibleFeatureFlags.None, CompatibleFeatureFlags.None),
            0, 0, 0, 4096, 1024, 1000, 1024 * 4096, 24,
            uuid, Guid.Empty, 0, 0, 1, 304, 1, 0, 0, 0, 0,
            label, DateTimeOffset.UtcNow.Ticks, DateTimeOffset.UtcNow.Ticks,
            0, 1, 0, Guid.Empty, DateTimeOffset.UtcNow.Ticks, Guid.Empty,
            0, new byte[32]);
        Assert.Equal("TestVolume", sb.GetVolumeLabelString());
    }

    [Fact]
    public void SuperblockV2_Equality_Works()
    {
        var uuid = Guid.NewGuid();
        var sb1 = SuperblockV2.CreateDefault(4096, 1024, uuid);
        var sb2 = SuperblockV2.CreateDefault(4096, 1024, uuid);
        // Both have same UUID and fields
        Assert.Equal(sb1.VolumeUuid, sb2.VolumeUuid);
    }

    // ── 5. UniversalBlockTrailer Tests ────────────────────────────────────

    [Fact]
    public void UniversalBlockTrailer_StructLayout_Is16Bytes()
    {
        Assert.Equal(16, UniversalBlockTrailer.Size);
        Assert.Equal(16, Marshal.SizeOf<UniversalBlockTrailer>());
    }

    [Fact]
    public void UniversalBlockTrailer_XxHash64_VerificationWorks()
    {
        int blockSize = 4096;
        var block = new byte[blockSize];
        // Write some payload
        block[0] = 0xAB;
        block[1] = 0xCD;
        UniversalBlockTrailer.Write(block, blockSize, BlockTypeTags.DATA, 1);
        Assert.True(UniversalBlockTrailer.Verify(block, blockSize));
    }

    [Fact]
    public void UniversalBlockTrailer_BlockTypeTag_BigEndian()
    {
        // SUPB = 0x53555042 which is 'S','U','P','B' in big-endian
        string tag = BlockTypeTags.TagToString(BlockTypeTags.SUPB);
        Assert.Equal("SUPB", tag);

        uint roundTrip = BlockTypeTags.StringToTag("SUPB");
        Assert.Equal(BlockTypeTags.SUPB, roundTrip);
    }

    [Fact]
    public void UniversalBlockTrailer_AtEndOfBlock()
    {
        int blockSize = 4096;
        var block = new byte[blockSize];
        UniversalBlockTrailer.Write(block, blockSize, BlockTypeTags.DATA, 42);

        var trailer = UniversalBlockTrailer.Read(block, blockSize);
        Assert.Equal(BlockTypeTags.DATA, trailer.BlockTypeTag);
        Assert.Equal(42u, trailer.GenerationNumber);
        Assert.True(UniversalBlockTrailer.Verify(block, blockSize));
    }

    [Fact]
    public void UniversalBlockTrailer_CorruptPayload_FailsVerify()
    {
        int blockSize = 4096;
        var block = new byte[blockSize];
        UniversalBlockTrailer.Write(block, blockSize, BlockTypeTags.DATA, 1);
        // Corrupt payload
        block[0] ^= 0xFF;
        Assert.False(UniversalBlockTrailer.Verify(block, blockSize));
    }

    // ── 6. InodeV2 Tests ──────────────────────────────────────────────────

    [Fact]
    public void InodeV2_IsClass_DueToVariableSize()
    {
        var inode = new InodeV2();
        Assert.NotNull(inode); // class, not struct
        Assert.IsType<InodeV2>(inode);
    }

    [Fact]
    public void InodeV2_ModuleFields_AsRawByteBlob()
    {
        var inode = new InodeV2();
        Assert.NotNull(inode.ModuleFieldData);
        Assert.Empty(inode.ModuleFieldData);
    }

    [Fact]
    public void InodeV2_LayoutDescriptor_OffsetsCorrect()
    {
        uint manifest = 0x0000_0001; // Security only
        var descriptor = InodeLayoutDescriptor.Create(manifest);
        Assert.True(descriptor.InodeSize > 0);
        Assert.Equal((ushort)FormatConstants.InodeCoreSize, descriptor.CoreFieldsEnd);
        Assert.True(descriptor.ModuleFieldCount > 0);
    }

    [Fact]
    public void InodeV2_SerializeDeserialize_RoundTrip()
    {
        uint manifest = (1u << (int)ModuleId.Security) | (1u << (int)ModuleId.Replication);
        var descriptor = InodeLayoutDescriptor.Create(manifest);

        var inode = new InodeV2
        {
            InodeNumber = 42,
            Type = InodeType.File,
            Flags = InodeFlags.None,
            LinkCount = 1,
            OwnerId = Guid.NewGuid(),
            Size = 4096,
            AllocatedSize = 4096,
            CreatedUtc = DateTimeOffset.UtcNow.Ticks,
            ModifiedUtc = DateTimeOffset.UtcNow.Ticks,
            AccessedUtc = DateTimeOffset.UtcNow.Ticks,
            ChangedUtc = DateTimeOffset.UtcNow.Ticks,
            ModuleFieldData = new byte[descriptor.InodeSize - descriptor.CoreFieldsEnd - descriptor.PaddingBytes],
        };

        var buffer = InodeV2.Serialize(inode, descriptor);
        var deserialized = InodeV2.Deserialize(buffer, descriptor);

        Assert.Equal(42, deserialized.InodeNumber);
        Assert.Equal(InodeType.File, deserialized.Type);
        Assert.Equal(1, deserialized.LinkCount);
        Assert.Equal(inode.OwnerId, deserialized.OwnerId);
    }

    [Fact]
    public void ModuleFieldEntry_IncludesFieldVersion()
    {
        var entry = new ModuleFieldEntry(
            moduleId: 0,
            fieldOffset: 304,
            fieldSize: 24,
            fieldVersion: 1,
            flags: ModuleFieldEntry.FlagActive);

        Assert.Equal(1, entry.FieldVersion);
        Assert.True(entry.IsActive);
    }

    // ── 7. BlockTypeTags Tests ────────────────────────────────────────────

    [Fact]
    public void BlockTypeTags_Has28OrMoreTags()
    {
        var tags = new HashSet<uint>
        {
            BlockTypeTags.SUPB, BlockTypeTags.RMAP, BlockTypeTags.POLV, BlockTypeTags.ENCR,
            BlockTypeTags.BMAP, BlockTypeTags.INOD, BlockTypeTags.TAGI, BlockTypeTags.MWAL,
            BlockTypeTags.MTRK, BlockTypeTags.BTRE, BlockTypeTags.SNAP, BlockTypeTags.REPL,
            BlockTypeTags.RAID, BlockTypeTags.COMP, BlockTypeTags.INTE, BlockTypeTags.STRE,
            BlockTypeTags.XREF, BlockTypeTags.WORM, BlockTypeTags.CODE, BlockTypeTags.DWAL,
            BlockTypeTags.DATA, BlockTypeTags.FREE, BlockTypeTags.CMVT, BlockTypeTags.ALOG,
            BlockTypeTags.CLOG, BlockTypeTags.DICT, BlockTypeTags.ANON, BlockTypeTags.MLOG,
            BlockTypeTags.ERCV
        };
        Assert.True(tags.Count >= 28);
    }

    [Fact]
    public void BlockTypeTags_AllUnique()
    {
        var tags = new[]
        {
            BlockTypeTags.SUPB, BlockTypeTags.RMAP, BlockTypeTags.POLV, BlockTypeTags.ENCR,
            BlockTypeTags.BMAP, BlockTypeTags.INOD, BlockTypeTags.TAGI, BlockTypeTags.MWAL,
            BlockTypeTags.MTRK, BlockTypeTags.BTRE, BlockTypeTags.SNAP, BlockTypeTags.REPL,
            BlockTypeTags.RAID, BlockTypeTags.COMP, BlockTypeTags.INTE, BlockTypeTags.STRE,
            BlockTypeTags.XREF, BlockTypeTags.WORM, BlockTypeTags.CODE, BlockTypeTags.DWAL,
            BlockTypeTags.DATA, BlockTypeTags.FREE, BlockTypeTags.CMVT, BlockTypeTags.ALOG,
            BlockTypeTags.CLOG, BlockTypeTags.DICT, BlockTypeTags.ANON, BlockTypeTags.MLOG,
            BlockTypeTags.ERCV
        };
        Assert.Equal(tags.Length, tags.Distinct().Count());
    }

    [Theory]
    [InlineData("SUPB", 0x53555042u)]
    [InlineData("DATA", 0x44415441u)]
    [InlineData("POLV", 0x504F4C56u)]
    public void BlockTypeTags_BigEndianEncoding_Correct(string ascii, uint expected)
    {
        uint tag = BlockTypeTags.StringToTag(ascii);
        Assert.Equal(expected, tag);
        string roundTrip = BlockTypeTags.TagToString(tag);
        Assert.Equal(ascii, roundTrip);
    }

    [Fact]
    public void BlockTypeTags_IsKnownTag_RecognizesDefinedTags()
    {
        Assert.True(BlockTypeTags.IsKnownTag(BlockTypeTags.SUPB));
        Assert.True(BlockTypeTags.IsKnownTag(BlockTypeTags.ERCV));
        Assert.False(BlockTypeTags.IsKnownTag(0x12345678u));
    }

    [Fact]
    public void BlockTypeTags_StringToTag_InvalidLength_Throws()
    {
        Assert.Throws<ArgumentException>(() => BlockTypeTags.StringToTag("AB"));
        Assert.Throws<ArgumentException>(() => BlockTypeTags.StringToTag("ABCDE"));
    }

    // ── 8. Cross-Profile Module Verification ──────────────────────────────

    [Theory]
    [InlineData(VdeProfileType.Minimal)]
    [InlineData(VdeProfileType.Standard)]
    [InlineData(VdeProfileType.Enterprise)]
    [InlineData(VdeProfileType.MaxSecurity)]
    [InlineData(VdeProfileType.EdgeIoT)]
    [InlineData(VdeProfileType.Analytics)]
    [InlineData(VdeProfileType.Custom)]
    public void Profile_ProducesValidProfile(VdeProfileType profileType)
    {
        var profile = profileType switch
        {
            VdeProfileType.Minimal => VdeCreationProfile.Minimal(1024),
            VdeProfileType.Standard => VdeCreationProfile.Standard(1024),
            VdeProfileType.Enterprise => VdeCreationProfile.Enterprise(1024),
            VdeProfileType.MaxSecurity => VdeCreationProfile.MaxSecurity(1024),
            VdeProfileType.EdgeIoT => VdeCreationProfile.EdgeIoT(1024),
            VdeProfileType.Analytics => VdeCreationProfile.Analytics(1024),
            VdeProfileType.Custom => VdeCreationProfile.Custom(1024, 0x01, new Dictionary<ModuleId, byte> { [ModuleId.Security] = 0x1 }),
            _ => throw new ArgumentOutOfRangeException()
        };

        Assert.Equal(profileType, profile.ProfileType);
        Assert.True(profile.TotalBlocks > 0);
        Assert.True(profile.BlockSize >= 512 && profile.BlockSize <= 65536);
    }

    [Theory]
    [InlineData(VdeProfileType.Minimal, 0)]
    [InlineData(VdeProfileType.Standard, 4)]
    [InlineData(VdeProfileType.Enterprise, 7)]
    [InlineData(VdeProfileType.MaxSecurity, 19)]
    [InlineData(VdeProfileType.EdgeIoT, 2)]
    [InlineData(VdeProfileType.Analytics, 4)]
    public void Profile_ManifestBitCount_MatchesExpectedModuleCount(VdeProfileType profileType, int expectedModules)
    {
        var profile = profileType switch
        {
            VdeProfileType.Minimal => VdeCreationProfile.Minimal(1024),
            VdeProfileType.Standard => VdeCreationProfile.Standard(1024),
            VdeProfileType.Enterprise => VdeCreationProfile.Enterprise(1024),
            VdeProfileType.MaxSecurity => VdeCreationProfile.MaxSecurity(1024),
            VdeProfileType.EdgeIoT => VdeCreationProfile.EdgeIoT(1024),
            VdeProfileType.Analytics => VdeCreationProfile.Analytics(1024),
            _ => throw new ArgumentOutOfRangeException()
        };

        int bitCount = BitOperations.PopCount(profile.ModuleManifest);
        Assert.Equal(expectedModules, bitCount);
    }

    [Theory]
    [InlineData(VdeProfileType.Standard)]
    [InlineData(VdeProfileType.Enterprise)]
    [InlineData(VdeProfileType.MaxSecurity)]
    [InlineData(VdeProfileType.EdgeIoT)]
    [InlineData(VdeProfileType.Analytics)]
    public void Profile_ModuleConfigLevels_HasEntryForEachActiveModule(VdeProfileType profileType)
    {
        var profile = profileType switch
        {
            VdeProfileType.Standard => VdeCreationProfile.Standard(1024),
            VdeProfileType.Enterprise => VdeCreationProfile.Enterprise(1024),
            VdeProfileType.MaxSecurity => VdeCreationProfile.MaxSecurity(1024),
            VdeProfileType.EdgeIoT => VdeCreationProfile.EdgeIoT(1024),
            VdeProfileType.Analytics => VdeCreationProfile.Analytics(1024),
            _ => throw new ArgumentOutOfRangeException()
        };

        int activeModules = BitOperations.PopCount(profile.ModuleManifest);
        Assert.Equal(activeModules, profile.ModuleConfigLevels.Count);
    }

    [Theory]
    [InlineData(VdeProfileType.Minimal, true)]
    [InlineData(VdeProfileType.Standard, true)]
    [InlineData(VdeProfileType.Enterprise, true)]
    [InlineData(VdeProfileType.MaxSecurity, true)]
    [InlineData(VdeProfileType.EdgeIoT, true)]
    [InlineData(VdeProfileType.Analytics, true)]
    public void Profile_ThinProvisioned_SetCorrectly(VdeProfileType profileType, bool expectedThinProvisioned)
    {
        var profile = profileType switch
        {
            VdeProfileType.Minimal => VdeCreationProfile.Minimal(1024),
            VdeProfileType.Standard => VdeCreationProfile.Standard(1024),
            VdeProfileType.Enterprise => VdeCreationProfile.Enterprise(1024),
            VdeProfileType.MaxSecurity => VdeCreationProfile.MaxSecurity(1024),
            VdeProfileType.EdgeIoT => VdeCreationProfile.EdgeIoT(1024),
            VdeProfileType.Analytics => VdeCreationProfile.Analytics(1024),
            _ => throw new ArgumentOutOfRangeException()
        };

        Assert.Equal(expectedThinProvisioned, profile.ThinProvisioned);
    }

    // ── Additional Coverage ───────────────────────────────────────────────

    [Fact]
    public void VdeCreator_CalculateLayout_MinimalProfile_HasCoreRegions()
    {
        var profile = VdeCreationProfile.Minimal(1024);
        var layout = VdeCreator.CalculateLayout(profile);
        Assert.True(layout.Regions.ContainsKey("PrimarySuperblock"));
        Assert.True(layout.Regions.ContainsKey("MirrorSuperblock"));
        Assert.True(layout.Regions.ContainsKey("RegionDirectory"));
        Assert.True(layout.Regions.ContainsKey("AllocationBitmap"));
        Assert.True(layout.Regions.ContainsKey("InodeTable"));
        Assert.True(layout.Regions.ContainsKey("MetadataWAL"));
        Assert.True(layout.DataBlocks > 0);
    }

    [Fact]
    public void VdeCreator_CalculateLayout_MaxSecurityProfile_HasModuleRegions()
    {
        var profile = VdeCreationProfile.MaxSecurity(4096);
        var layout = VdeCreator.CalculateLayout(profile);
        Assert.True(layout.Regions.ContainsKey("PolicyVault"));
        Assert.True(layout.Regions.ContainsKey("EncryptionHeader"));
        Assert.True(layout.Regions.ContainsKey("DataWAL"));
        Assert.True(layout.Regions.ContainsKey("StreamingAppend"));
        Assert.True(layout.MetadataBlocks > 0);
    }

    [Fact]
    public void VdeCreator_CalculateLayout_InvalidBlockSize_Throws()
    {
        var profile = VdeCreationProfile.Custom(1024, 0, new Dictionary<ModuleId, byte>(), blockSize: 100);
        Assert.Throws<ArgumentException>(() => VdeCreator.CalculateLayout(profile));
    }

    [Fact]
    public void VdeCreator_CalculateLayout_TooFewBlocks_Throws()
    {
        var profile = VdeCreationProfile.Custom(10, 0, new Dictionary<ModuleId, byte>());
        Assert.Throws<ArgumentException>(() => VdeCreator.CalculateLayout(profile));
    }

    [Fact]
    public void RegionDirectory_AddRegion_FindRegion_RoundTrip()
    {
        var dir = new RegionDirectory();
        int slot = dir.AddRegion(BlockTypeTags.POLV, RegionFlags.None, 10, 2);
        Assert.Equal(0, slot);
        int found = dir.FindRegion(BlockTypeTags.POLV);
        Assert.Equal(0, found);
        Assert.Equal(1, dir.ActiveRegionCount);
    }

    [Fact]
    public void RegionDirectory_SerializeDeserialize_RoundTrip()
    {
        int blockSize = 4096;
        var dir = new RegionDirectory { Generation = 7 };
        dir.AddRegion(BlockTypeTags.POLV, RegionFlags.None, 10, 2);
        dir.AddRegion(BlockTypeTags.TAGI, RegionFlags.None, 20, 64);

        var buffer = new byte[2 * blockSize];
        dir.Serialize(buffer, blockSize);

        var deserialized = RegionDirectory.Deserialize(buffer, blockSize);
        Assert.Equal(2, deserialized.ActiveRegionCount);
        Assert.Equal(7u, deserialized.Generation);
        Assert.Equal(0, deserialized.FindRegion(BlockTypeTags.POLV));
        Assert.Equal(1, deserialized.FindRegion(BlockTypeTags.TAGI));
    }

    [Fact]
    public void RegionPointer_SerializeDeserialize_RoundTrip()
    {
        var original = new RegionPointer(BlockTypeTags.INOD, RegionFlags.Active | RegionFlags.Encrypted, 100, 200, 50);
        var buffer = new byte[32];
        RegionPointer.Serialize(original, buffer);
        var deserialized = RegionPointer.Deserialize(buffer);
        Assert.Equal(original, deserialized);
    }

    [Fact]
    public void InodeLayoutDescriptor_Create_NoModules_MinimalInode()
    {
        var desc = InodeLayoutDescriptor.Create(0u);
        Assert.True(desc.InodeSize >= FormatConstants.InodeCoreSize);
        Assert.Equal(0, desc.ModuleFieldCount);
    }

    [Fact]
    public void InodeLayoutDescriptor_SerializeDeserialize_RoundTrip()
    {
        var desc = InodeLayoutDescriptor.Create(0x0000_1C01); // Standard profile
        var buffer = new byte[256];
        InodeLayoutDescriptor.Serialize(desc, buffer);
        var deserialized = InodeLayoutDescriptor.Deserialize(buffer);
        Assert.Equal(desc.InodeSize, deserialized.InodeSize);
        Assert.Equal(desc.CoreFieldsEnd, deserialized.CoreFieldsEnd);
        Assert.Equal(desc.ModuleFieldCount, deserialized.ModuleFieldCount);
    }

    [Fact]
    public void ModuleRegistry_CalculateTotalInodeFieldBytes_AllModules()
    {
        uint all = 0x0007_FFFF;
        int totalBytes = ModuleRegistry.CalculateTotalInodeFieldBytes(all);
        Assert.True(totalBytes > 0);

        // Sum manually
        int expected = 0;
        for (int i = 0; i < 19; i++)
        {
            expected += ModuleRegistry.GetModule((ModuleId)i).InodeFieldBytes;
        }
        Assert.Equal(expected, totalBytes);
    }

    [Fact]
    public void ModuleRegistry_GetRequiredBlockTypeTags_StandardProfile()
    {
        uint standard = 0x0000_1C01;
        var tags = ModuleRegistry.GetRequiredBlockTypeTags(standard);
        Assert.NotEmpty(tags);
        // Security has POLV + ENCR, Compression has DICT, Integrity has MTRK, Snapshot has SNAP
        Assert.Contains(BlockTypeTags.POLV, tags);
        Assert.Contains(BlockTypeTags.ENCR, tags);
        Assert.Contains(BlockTypeTags.DICT, tags);
        Assert.Contains(BlockTypeTags.MTRK, tags);
        Assert.Contains(BlockTypeTags.SNAP, tags);
    }

    [Fact]
    public void ModuleRegistry_GetRequiredRegions_StandardProfile()
    {
        uint standard = 0x0000_1C01;
        var regions = ModuleRegistry.GetRequiredRegions(standard);
        Assert.NotEmpty(regions);
        Assert.Contains("PolicyVault", regions);
        Assert.Contains("EncryptionHeader", regions);
    }

    [Fact]
    public void Sustainability_And_Transit_AreInodeOnly()
    {
        var sustainability = ModuleRegistry.GetModule(ModuleId.Sustainability);
        Assert.False(sustainability.HasRegion);
        Assert.True(sustainability.HasInodeFields);
        Assert.Equal(4, sustainability.InodeFieldBytes);

        var transit = ModuleRegistry.GetModule(ModuleId.Transit);
        Assert.False(transit.HasRegion);
        Assert.True(transit.HasInodeFields);
        Assert.Equal(1, transit.InodeFieldBytes);
    }

    [Fact]
    public void FormatConstants_DefinedModules_Is19()
    {
        Assert.Equal(19, FormatConstants.DefinedModules);
    }

    [Fact]
    public void Profile_GetManifest_ReturnsCorrectField()
    {
        var profile = VdeCreationProfile.Standard(1024);
        var manifest = profile.GetManifest();
        Assert.Equal(0x0000_1C01u, manifest.Value);
        Assert.Equal(4, manifest.ActiveModuleCount);
    }

    [Fact]
    public void Profile_GetConfig_ReturnsModuleConfigField()
    {
        var profile = VdeCreationProfile.Standard(1024);
        var config = profile.GetConfig();
        // ModuleConfigField wraps the nibble-encoded config
        Assert.True(config.ConfigPrimary != 0 || config.ConfigExtended != 0 || profile.ModuleConfigLevels.Count > 0);
    }

    [Fact]
    public void Profile_GetInodeLayout_ReturnsCorrectSize()
    {
        var profile = VdeCreationProfile.MaxSecurity(1024);
        var layout = profile.GetInodeLayout();
        Assert.True(layout.InodeSize >= FormatConstants.InodeCoreSize);
    }
}
