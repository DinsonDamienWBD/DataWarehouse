using Spectre.Console;
using DataWarehouse.Integration;
using DataWarehouse.SDK.Hardware;
using DataWarehouse.SDK.Hosting;
using DataWarehouse.SDK.Primitives.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.CLI.Commands;

/// <summary>
/// Install command - Installs and initializes a new DataWarehouse instance.
/// Integrates hardware probe to auto-select best-fit configuration preset.
/// Supports deployment topology selection (DW-only, VDE-only, DW+VDE).
/// </summary>
public static class InstallCommand
{
    public static async Task ExecuteAsync(
        string path,
        string? dataPath,
        string? adminPassword,
        bool createService,
        bool autoStart,
        string topology = "dw+vde",
        string? remoteVdeUrl = null,
        int vdeListenPort = 9443)
    {
        await AnsiConsole.Status()
            .StartAsync("Installing DataWarehouse...", async ctx =>
            {
                try
                {
                    // Parse topology
                    var deploymentTopology = topology.ToLowerInvariant() switch
                    {
                        "dw-only" => DeploymentTopology.DwOnly,
                        "vde-only" => DeploymentTopology.VdeOnly,
                        _ => DeploymentTopology.DwPlusVde,
                    };

                    var host = new DataWarehouseHost(NullLoggerFactory.Instance);

                    // Detect hardware and select best-fit configuration preset
                    ctx.Status("[cyan]Detecting hardware capabilities...[/]");
                    ctx.Spinner(Spinner.Known.Dots);

                    using var probe = HardwareProbeFactory.Create();
                    var (presetName, suggestedConfig) = await PresetSelector.SelectPresetAsync(probe);

                    AnsiConsole.MarkupLine($"  Hardware suggests [yellow]{presetName}[/] preset (CPU: {Environment.ProcessorCount} cores)");
                    AnsiConsole.MarkupLine($"  Topology: [yellow]{DeploymentTopologyDescriptor.GetDescription(deploymentTopology)}[/]");

                    // Validate topology-specific options
                    if (deploymentTopology == DeploymentTopology.DwOnly && string.IsNullOrEmpty(remoteVdeUrl))
                    {
                        AnsiConsole.MarkupLine("[yellow]  Warning: DW-only topology without --remote-vde; you'll need to configure remote VDE later.[/]");
                    }

                    // Store preset in install config
                    var config = new InstallConfiguration
                    {
                        InstallPath = path,
                        DataPath = dataPath,
                        CreateDefaultAdmin = !string.IsNullOrEmpty(adminPassword),
                        AdminPassword = adminPassword,
                        CreateService = createService,
                        AutoStart = autoStart,
                        Topology = deploymentTopology,
                        RemoteVdeUrl = remoteVdeUrl,
                        VdeListenPort = vdeListenPort,
                    };
                    config.InitialConfig["configPreset"] = presetName;
                    config.InitialConfig["deploymentTopology"] = deploymentTopology.ToString();

                    // For VDE-only topology, skip DW-specific configuration
                    if (deploymentTopology == DeploymentTopology.VdeOnly)
                    {
                        config.CreateDefaultAdmin = false;
                        config.InitialConfig["vdeListenPort"] = vdeListenPort;
                    }

                    // Save selected configuration to install path
                    var configFilePath = Path.Combine(path, "config", "datawarehouse-config.xml");
                    ConfigurationSerializer.SaveToFile(suggestedConfig, configFilePath);

                    var progress = new Progress<InstallProgress>(p =>
                    {
                        ctx.Status($"[cyan]{p.Message}[/]");
                        ctx.Spinner(Spinner.Known.Dots);
                    });

                    var result = await host.InstallAsync(config, progress);

                    if (result.Success)
                    {
                        AnsiConsole.MarkupLine($"[green]Installation successful![/]");
                        AnsiConsole.MarkupLine($"  Install Path: [cyan]{result.InstallPath}[/]");
                        AnsiConsole.MarkupLine($"  Instance ID: [cyan]{config.InitialConfig.GetValueOrDefault("instanceId", "N/A")}[/]");

                        if (createService)
                        {
                            AnsiConsole.MarkupLine($"  Service: [green]Registered[/]");
                        }

                        if (autoStart)
                        {
                            AnsiConsole.MarkupLine($"  Auto-start: [green]Enabled[/]");
                        }

                        // Register shell handlers for VDE-capable topologies
                        if (DeploymentTopologyDescriptor.RequiresVdeEngine(deploymentTopology))
                        {
                            ctx.Status("[cyan]Registering file extensions...[/]");
                            var shellResult = InstallShellRegistration.RegisterFileExtensions(
                                path,
                                new Progress<string>(msg => ctx.Status($"[cyan]{msg}[/]")));

                            if (shellResult.Success)
                            {
                                AnsiConsole.MarkupLine($"  File extensions: [green]{string.Join(", ", shellResult.RegisteredExtensions)}[/]");
                            }
                            else
                            {
                                AnsiConsole.MarkupLine($"  File extensions: [yellow]Registration failed: {shellResult.Error}[/]");
                            }
                        }
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[red]Installation failed: {result.Message}[/]");
                        if (result.Exception != null)
                        {
                            AnsiConsole.WriteException(result.Exception);
                        }
                    }

                    await host.DisposeAsync();
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Error during installation: {ex.Message}[/]");
                    AnsiConsole.WriteException(ex);
                }
            });
    }
}
