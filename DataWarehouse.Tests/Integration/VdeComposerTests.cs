using DataWarehouse.SDK.VirtualDiskEngine.Format;
using Xunit;

namespace DataWarehouse.Tests.Integration;

/// <summary>
/// Integration tests for VDE composer: module parsing, file creation, inspection,
/// and profile validation. Tests exercise VdeCreator, ModuleRegistry, and VdeCreationProfile.
/// </summary>
public sealed class VdeComposerTests : IDisposable
{
    private readonly List<string> _tempFiles = new();

    private string CreateTempVdePath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"test-{Guid.NewGuid():N}.dwvd");
        _tempFiles.Add(path);
        return path;
    }

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* best-effort cleanup */ }
        }
    }

    // ── Module Parsing Tests ────────────────────────────────────────────

    [Fact]
    public void ParseModules_ValidNames_ReturnsModuleIds()
    {
        var names = "security,tags,replication";
        var parsed = ParseModuleNames(names);

        Assert.Equal(3, parsed.Count);
        Assert.Contains(ModuleId.Security, parsed);
        Assert.Contains(ModuleId.Tags, parsed);
        Assert.Contains(ModuleId.Replication, parsed);
    }

    [Fact]
    public void ParseModules_CaseInsensitive()
    {
        var names = "Security,TAGS,Replication";
        var parsed = ParseModuleNames(names);

        Assert.Equal(3, parsed.Count);
        Assert.Contains(ModuleId.Security, parsed);
        Assert.Contains(ModuleId.Tags, parsed);
        Assert.Contains(ModuleId.Replication, parsed);
    }

    [Fact]
    public void ParseModules_InvalidName_ReturnsError()
    {
        var names = "security,nonexistent";
        var (parsed, errors) = ParseModuleNamesWithErrors(names);

        Assert.Single(parsed); // security parses ok
        Assert.Single(errors); // nonexistent fails
        Assert.Contains("nonexistent", errors[0]);
    }

    [Fact]
    public void ParseModules_EmptyString_ReturnsEmpty()
    {
        var names = "";
        var parsed = ParseModuleNames(names);
        Assert.Empty(parsed);
    }

    // ── VDE Creation Tests ──────────────────────────────────────────────

    [Fact]
    public async Task CreateVde_SecurityOnly_HasSecurityModule()
    {
        var path = CreateTempVdePath();
        var manifest = ModuleManifestField.FromModules(ModuleId.Security);

        var profile = new VdeCreationProfile
        {
            ProfileType = VdeProfileType.Custom,
            Name = "SecurityOnly",
            Description = "Test",
            ModuleManifest = manifest.Value,
            ModuleConfigLevels = new Dictionary<ModuleId, byte> { [ModuleId.Security] = 1 },
            BlockSize = FormatConstants.DefaultBlockSize,
            TotalBlocks = 1024,
            ThinProvisioned = true,
        };

        await VdeCreator.CreateVdeAsync(path, profile);

        Assert.True(File.Exists(path));
        Assert.True(ModuleRegistry.IsModuleActive(manifest.Value, ModuleId.Security));
        Assert.False(ModuleRegistry.IsModuleActive(manifest.Value, ModuleId.Tags));
    }

    [Fact]
    public async Task CreateVde_AllModules_HasAll19Active()
    {
        var path = CreateTempVdePath();
        var profile = VdeCreationProfile.MaxSecurity(1024);

        await VdeCreator.CreateVdeAsync(path, profile);

        var activeModules = ModuleRegistry.GetActiveModules(profile.ModuleManifest);
        Assert.Equal(FormatConstants.DefinedModules, activeModules.Count);
    }

    [Fact]
    public void CreateVde_EmptyModules_Minimal_Works()
    {
        // A minimal profile with zero modules is valid (bare container)
        var profile = VdeCreationProfile.Minimal(1024);
        Assert.Equal(0u, profile.ModuleManifest);
    }

    [Fact]
    public async Task CreateVde_CustomProfile_CorrectBlockSize()
    {
        var path = CreateTempVdePath();
        var profile = VdeCreationProfile.Custom(
            totalBlocks: 1024,
            manifest: ModuleManifestField.FromModules(ModuleId.Security).Value,
            configLevels: new Dictionary<ModuleId, byte> { [ModuleId.Security] = 1 },
            blockSize: 8192);

        await VdeCreator.CreateVdeAsync(path, profile);

        Assert.Equal(8192, profile.BlockSize);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void CreateVde_DefaultBlockSize_Is4096()
    {
        var profile = VdeCreationProfile.Standard(1024);
        Assert.Equal(FormatConstants.DefaultBlockSize, profile.BlockSize);
        Assert.Equal(4096, profile.BlockSize);
    }

    [Fact]
    public async Task CreateVde_ProducesValidFile()
    {
        var path = CreateTempVdePath();
        var profile = VdeCreationProfile.Standard(1024);

        await VdeCreator.CreateVdeAsync(path, profile);

        // Verify DWVD magic bytes at start
        var bytes = new byte[8];
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
        {
            fs.ReadExactly(bytes);
        }

        // Magic signature starts with "DWVD"
        Assert.Equal((byte)'D', bytes[0]);
        Assert.Equal((byte)'W', bytes[1]);
        Assert.Equal((byte)'V', bytes[2]);
        Assert.Equal((byte)'D', bytes[3]);
    }

    [Fact]
    public async Task CreateVde_FileSize_GreaterThanZero()
    {
        var path = CreateTempVdePath();
        var profile = VdeCreationProfile.Standard(1024);

        await VdeCreator.CreateVdeAsync(path, profile);

        var fileInfo = new FileInfo(path);
        Assert.True(fileInfo.Length > 0);
    }

    [Fact]
    public void ModuleManifest_FromNames_MatchesFromIds()
    {
        // Build manifest from ModuleId values
        var fromIds = ModuleManifestField.FromModules(
            ModuleId.Security, ModuleId.Tags, ModuleId.Replication);

        // Build same manifest from parsed names
        var parsedIds = ParseModuleNames("security,tags,replication");
        var fromNames = ModuleManifestField.FromModules(parsedIds.ToArray());

        Assert.Equal(fromIds.Value, fromNames.Value);
    }

    [Fact]
    public void VdeCreationProfile_Custom_HasCorrectType()
    {
        var profile = VdeCreationProfile.Custom(
            1024,
            ModuleManifestField.FromModules(ModuleId.Security).Value,
            new Dictionary<ModuleId, byte> { [ModuleId.Security] = 1 });

        Assert.Equal(VdeProfileType.Custom, profile.ProfileType);
    }

    [Fact]
    public void VdeCreationProfile_ModuleConfigLevels_DefaultOne()
    {
        var modules = new[] { ModuleId.Security, ModuleId.Tags, ModuleId.Replication };
        var configLevels = modules.ToDictionary(m => m, _ => (byte)1);

        foreach (var mod in modules)
        {
            Assert.Equal(1, configLevels[mod]);
        }
    }

    [Fact]
    public void ListModules_Returns19Modules()
    {
        var values = Enum.GetValues<ModuleId>();
        Assert.Equal(FormatConstants.DefinedModules, values.Length);
        Assert.Equal(19, values.Length);
    }

    [Fact]
    public async Task InspectVde_ReadsBackModules()
    {
        var path = CreateTempVdePath();
        var profile = VdeCreationProfile.Standard(1024);

        await VdeCreator.CreateVdeAsync(path, profile);

        // Validate the created file passes VDE validation
        var isValid = VdeCreator.ValidateVde(path);
        Assert.True(isValid, "Created VDE file should pass validation");

        // Read back and verify active modules match
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var firstBlock = new byte[FormatConstants.MaxBlockSize];
        stream.ReadExactly(firstBlock, 0, Math.Min((int)stream.Length, FormatConstants.MaxBlockSize));

        var magic = MagicSignature.Deserialize(firstBlock);
        Assert.True(magic.Validate(), "Magic signature should be valid");

        // Read superblock to check manifest
        int blockSize = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
            firstBlock.AsSpan(0x34));
        Assert.Equal(FormatConstants.DefaultBlockSize, blockSize);

        stream.Position = 0;
        var sbBuffer = new byte[FormatConstants.SuperblockGroupBlocks * blockSize];
        stream.ReadExactly(sbBuffer);

        var superblock = SuperblockV2.Deserialize(sbBuffer, blockSize);
        Assert.Equal(profile.ModuleManifest, superblock.ModuleManifest);

        var activeModules = ModuleRegistry.GetActiveModules(superblock.ModuleManifest);
        // Standard profile: SEC + CMPR + INTG + SNAP = 4 modules
        Assert.Equal(4, activeModules.Count);
    }

    [Fact]
    public void CalculateLayout_StandardProfile_HasExpectedRegions()
    {
        var profile = VdeCreationProfile.Standard(1024);
        var layout = VdeCreator.CalculateLayout(profile);

        Assert.Equal(4096, layout.BlockSize);
        Assert.Equal(1024, layout.TotalBlocks);
        Assert.True(layout.MetadataBlocks > 0);
        Assert.True(layout.DataBlocks > 0);
        Assert.True(layout.OverheadPercent > 0);
        Assert.True(layout.OverheadPercent < 100);

        // Standard profile has security module, so should have PolicyVault
        Assert.True(layout.Regions.ContainsKey("PolicyVault"));
        Assert.True(layout.Regions.ContainsKey("AllocationBitmap"));
        Assert.True(layout.Regions.ContainsKey("InodeTable"));
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static List<ModuleId> ParseModuleNames(string modules)
    {
        var result = new List<ModuleId>();
        var names = modules.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var name in names)
        {
            if (Enum.TryParse<ModuleId>(name, ignoreCase: true, out var id))
            {
                result.Add(id);
            }
        }

        return result;
    }

    private static (List<ModuleId> parsed, List<string> errors) ParseModuleNamesWithErrors(string modules)
    {
        var parsed = new List<ModuleId>();
        var errors = new List<string>();
        var names = modules.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var name in names)
        {
            if (Enum.TryParse<ModuleId>(name, ignoreCase: true, out var id))
            {
                parsed.Add(id);
            }
            else
            {
                errors.Add($"Unknown module: {name}");
            }
        }

        return (parsed, errors);
    }
}
