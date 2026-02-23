using System.Buffers.Binary;
using DataWarehouse.SDK.VirtualDiskEngine.Format;
using Spectre.Console;

namespace DataWarehouse.CLI.Commands;

/// <summary>
/// VDE (Virtual Disk Engine) CLI commands for creating, inspecting, and managing
/// DWVD v2.0 composable virtual disk files.
/// </summary>
public static class VdeCommands
{
    /// <summary>
    /// The four available residency strategies per MRES-08.
    /// </summary>
    private static readonly string[] ResidencyStrategies =
    {
        "VdePrimary",
        "PluginOnly",
        "VdeFirstSync",
        "FallbackAndRepair",
    };

    /// <summary>
    /// Creates a new DWVD v2.0 file with the specified modules and options.
    /// </summary>
    /// <param name="outputPath">Output file path for the .dwvd file.</param>
    /// <param name="modules">Comma-separated module names (e.g., "security,tags,replication").</param>
    /// <param name="profile">Optional named profile (minimal, standard, enterprise, max-security, edge-iot, analytics).</param>
    /// <param name="blockSize">Block size in bytes (default 4096).</param>
    /// <param name="totalBlocks">Total number of blocks (default 1048576).</param>
    /// <param name="residencyStrategy">Optional residency strategy; prompts if null.</param>
    public static async Task<int> CreateAsync(
        string outputPath,
        string modules,
        string? profile,
        int blockSize,
        long totalBlocks,
        string? residencyStrategy)
    {
        // 1. Parse module names to ModuleId values
        var parsedModules = new List<ModuleId>();
        var moduleNames = modules.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var name in moduleNames)
        {
            if (Enum.TryParse<ModuleId>(name, ignoreCase: true, out var id))
            {
                parsedModules.Add(id);
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Unknown module name '[bold]{Markup.Escape(name)}[/]'.");
                AnsiConsole.MarkupLine("[yellow]Valid module names:[/]");
                var validNames = Enum.GetValues<ModuleId>();
                foreach (var valid in validNames)
                {
                    var mod = ModuleRegistry.GetModule(valid);
                    AnsiConsole.MarkupLine($"  [cyan]{valid}[/] ({mod.Abbreviation}) - {mod.Name}");
                }
                return 1;
            }
        }

        if (parsedModules.Count == 0)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] No modules specified. Use --modules with comma-separated module names.");
            return 1;
        }

        // 2. Build module manifest
        var manifest = ModuleManifestField.FromModules(parsedModules.ToArray());

        // 3. Build creation profile
        VdeCreationProfile creationProfile;

        if (!string.IsNullOrEmpty(profile))
        {
            creationProfile = ResolveNamedProfile(profile, totalBlocks);
            if (creationProfile.Name == null!)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] Unknown profile '[bold]{Markup.Escape(profile)}[/]'.");
                AnsiConsole.MarkupLine("[yellow]Valid profiles:[/] minimal, standard, enterprise, max-security, edge-iot, analytics");
                return 1;
            }
        }
        else
        {
            creationProfile = new VdeCreationProfile
            {
                ProfileType = VdeProfileType.Custom,
                Name = "Custom",
                Description = $"Custom VDE with modules: {modules}",
                ModuleManifest = manifest.Value,
                ModuleConfigLevels = parsedModules.ToDictionary(m => m, _ => (byte)1),
                BlockSize = blockSize,
                TotalBlocks = totalBlocks,
                ThinProvisioned = true,
            };
        }

        // 4. Residency strategy prompt (MRES-08)
        var selectedResidency = residencyStrategy;
        if (string.IsNullOrEmpty(selectedResidency))
        {
            // Show module residency table
            var residencyTable = new Table()
                .Title("[bold]Module Residency Configuration[/]")
                .Border(TableBorder.Rounded);
            residencyTable.AddColumn("Module");
            residencyTable.AddColumn("Abbreviation");
            residencyTable.AddColumn("Default Residency");

            foreach (var mod in parsedModules)
            {
                var modInfo = ModuleRegistry.GetModule(mod);
                residencyTable.AddRow(modInfo.Name, modInfo.Abbreviation, "VdePrimary");
            }

            AnsiConsole.Write(residencyTable);
            AnsiConsole.WriteLine();

            // Check if we can prompt interactively
            if (!AnsiConsole.Profile.Capabilities.Interactive)
            {
                selectedResidency = "VdePrimary";
                AnsiConsole.MarkupLine($"[grey]Non-interactive mode: using default residency strategy '[cyan]{selectedResidency}[/]'[/]");
            }
            else
            {
                selectedResidency = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[bold]Select metadata residency strategy (MRES-08):[/]")
                        .AddChoices(ResidencyStrategies));
            }
        }

        AnsiConsole.MarkupLine($"[grey]Residency strategy:[/] [cyan]{selectedResidency}[/]");

        // 5. Create VDE file
        try
        {
            AnsiConsole.Status()
                .Start("Creating VDE file...", ctx =>
                {
                    ctx.Spinner(Spinner.Known.Dots);
                });

            var createdPath = await VdeCreator.CreateVdeAsync(outputPath, creationProfile);

            // 6. Display summary
            var fileInfo = new FileInfo(createdPath);
            var activeModules = ModuleRegistry.GetActiveModules(creationProfile.ModuleManifest);

            var summaryPanel = new Panel(
                new Rows(
                    new Markup($"[bold]Output:[/]         {Markup.Escape(createdPath)}"),
                    new Markup($"[bold]File Size:[/]      {FormatSize(fileInfo.Length)}"),
                    new Markup($"[bold]Block Size:[/]     {creationProfile.BlockSize} bytes"),
                    new Markup($"[bold]Total Blocks:[/]   {creationProfile.TotalBlocks:N0}"),
                    new Markup($"[bold]Profile:[/]        {creationProfile.Name}"),
                    new Markup($"[bold]Residency:[/]      {selectedResidency}"),
                    new Markup($"[bold]Active Modules:[/] {activeModules.Count}"))
                )
                .Header("[green]VDE Created Successfully[/]")
                .Border(BoxBorder.Rounded);

            AnsiConsole.Write(summaryPanel);

            // Module details table
            var moduleTable = new Table()
                .Title("[bold]Active Modules[/]")
                .Border(TableBorder.Rounded);
            moduleTable.AddColumn("ID");
            moduleTable.AddColumn("Name");
            moduleTable.AddColumn("Abbreviation");
            moduleTable.AddColumn("Regions");
            moduleTable.AddColumn("Inode Bytes");

            foreach (var mod in activeModules)
            {
                moduleTable.AddRow(
                    ((byte)mod.Id).ToString(),
                    mod.Name,
                    mod.Abbreviation,
                    mod.HasRegion ? string.Join(", ", mod.RegionNames) : "(none)",
                    mod.InodeFieldBytes.ToString());
            }

            AnsiConsole.Write(moduleTable);

            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error creating VDE:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    /// <summary>
    /// Lists all 19 available VDE modules with their metadata.
    /// </summary>
    public static Task<int> ListModulesAsync()
    {
        var table = new Table()
            .Title("[bold]VDE v2.0 Composable Modules[/]")
            .Border(TableBorder.Rounded);

        table.AddColumn("Bit");
        table.AddColumn("ModuleId");
        table.AddColumn("Abbreviation");
        table.AddColumn("Name");
        table.AddColumn("Regions");
        table.AddColumn("Inode Bytes");

        foreach (var module in ModuleRegistry.AllModules)
        {
            table.AddRow(
                ((byte)module.Id).ToString(),
                module.Id.ToString(),
                module.Abbreviation,
                module.Name,
                module.HasRegion ? string.Join(", ", module.RegionNames) : "(none)",
                module.InodeFieldBytes.ToString());
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine($"\n[grey]Total modules: {ModuleRegistry.AllModules.Count}[/]");
        AnsiConsole.MarkupLine("[grey]Use --modules with comma-separated names for dw vde create[/]");

        return Task.FromResult(0);
    }

    /// <summary>
    /// Inspects an existing DWVD file and displays its metadata.
    /// </summary>
    /// <param name="path">Path to the .dwvd file to inspect.</param>
    public static Task<int> InspectAsync(string path)
    {
        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: [bold]{Markup.Escape(path)}[/]");
            return Task.FromResult(1);
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

            if (stream.Length < FormatConstants.MinBlockSize * 10)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] File is too small to be a valid DWVD file.");
                return Task.FromResult(1);
            }

            // Read first block to get magic and block size
            var firstBlock = new byte[FormatConstants.MaxBlockSize];
            int bytesRead = stream.Read(firstBlock, 0, Math.Min((int)stream.Length, FormatConstants.MaxBlockSize));
            if (bytesRead < FormatConstants.MinBlockSize)
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Could not read superblock.");
                return Task.FromResult(1);
            }

            // Validate magic
            var magic = MagicSignature.Deserialize(firstBlock);
            if (!magic.Validate())
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Invalid DWVD magic signature. Not a valid VDE file.");
                return Task.FromResult(1);
            }

            // Read block size from superblock at offset 0x34
            int blockSize = BinaryPrimitives.ReadInt32LittleEndian(firstBlock.AsSpan(0x34));

            // Read superblock
            stream.Position = 0;
            var sbBuffer = new byte[FormatConstants.SuperblockGroupBlocks * blockSize];
            stream.ReadExactly(sbBuffer);

            var superblock = SuperblockV2.Deserialize(sbBuffer, blockSize);

            // Get active modules
            var activeModules = ModuleRegistry.GetActiveModules(superblock.ModuleManifest);
            var manifestField = new ModuleManifestField(superblock.ModuleManifest);
            var fileInfo = new FileInfo(path);

            // Display information
            var panel = new Panel(
                new Rows(
                    new Markup($"[bold]File:[/]            {Markup.Escape(path)}"),
                    new Markup($"[bold]File Size:[/]       {FormatSize(fileInfo.Length)}"),
                    new Markup($"[bold]Format Version:[/]  {superblock.VersionInfo.MinReaderVersion}.{superblock.VersionInfo.MinWriterVersion}"),
                    new Markup($"[bold]Block Size:[/]      {superblock.BlockSize} bytes"),
                    new Markup($"[bold]Total Blocks:[/]    {superblock.TotalBlocks:N0}"),
                    new Markup($"[bold]Free Blocks:[/]     {superblock.FreeBlocks:N0}"),
                    new Markup($"[bold]Logical Size:[/]    {FormatSize(superblock.ExpectedFileSize)}"),
                    new Markup($"[bold]Volume UUID:[/]     {superblock.VolumeUuid}"),
                    new Markup($"[bold]Inode Size:[/]      {superblock.InodeSize} bytes"),
                    new Markup($"[bold]Manifest:[/]        0x{superblock.ModuleManifest:X8} ({manifestField.ActiveModuleCount} modules)"),
                    new Markup($"[bold]Checkpoint Seq:[/]  {superblock.CheckpointSequence}"))
                )
                .Header("[cyan]DWVD v2.0 File Inspection[/]")
                .Border(BoxBorder.Rounded);

            AnsiConsole.Write(panel);

            // Active modules table
            if (activeModules.Count > 0)
            {
                var moduleTable = new Table()
                    .Title("[bold]Active Modules[/]")
                    .Border(TableBorder.Rounded);
                moduleTable.AddColumn("Bit");
                moduleTable.AddColumn("Name");
                moduleTable.AddColumn("Abbreviation");
                moduleTable.AddColumn("Regions");

                foreach (var mod in activeModules)
                {
                    moduleTable.AddRow(
                        ((byte)mod.Id).ToString(),
                        mod.Name,
                        mod.Abbreviation,
                        mod.HasRegion ? string.Join(", ", mod.RegionNames) : "(none)");
                }

                AnsiConsole.Write(moduleTable);
            }
            else
            {
                AnsiConsole.MarkupLine("[grey]No modules active (bare container).[/]");
            }

            // Validation result
            var isValid = VdeCreator.ValidateVde(path);
            AnsiConsole.MarkupLine(isValid
                ? "\n[green]Validation: PASSED[/] (magic, trailers, region directory intact)"
                : "\n[red]Validation: FAILED[/] (metadata integrity check failed)");

            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error inspecting VDE:[/] {Markup.Escape(ex.Message)}");
            return Task.FromResult(1);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static VdeCreationProfile ResolveNamedProfile(string profileName, long totalBlocks) => profileName.ToLowerInvariant() switch
    {
        "minimal" => VdeCreationProfile.Minimal(totalBlocks),
        "standard" => VdeCreationProfile.Standard(totalBlocks),
        "enterprise" => VdeCreationProfile.Enterprise(totalBlocks),
        "max-security" or "maxsecurity" => VdeCreationProfile.MaxSecurity(totalBlocks),
        "edge-iot" or "edgeiot" => VdeCreationProfile.EdgeIoT(totalBlocks),
        "analytics" => VdeCreationProfile.Analytics(totalBlocks),
        _ => default,
    };

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024L => $"{bytes} B",
        < 1024L * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        < 1024L * 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):F1} GB",
        _ => $"{bytes / (1024.0 * 1024 * 1024 * 1024):F2} TB",
    };
}
