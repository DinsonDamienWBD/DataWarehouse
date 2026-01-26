using Spectre.Console;
using DataWarehouse.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.CLI.Commands;

/// <summary>
/// Install command - Installs and initializes a new DataWarehouse instance.
/// </summary>
public static class InstallCommand
{
    public static async Task ExecuteAsync(
        string path,
        string? dataPath,
        string? adminPassword,
        bool createService,
        bool autoStart)
    {
        await AnsiConsole.Status()
            .StartAsync("Installing DataWarehouse...", async ctx =>
            {
                try
                {
                    var host = new DataWarehouseHost(NullLoggerFactory.Instance);

                    var config = new InstallConfiguration
                    {
                        InstallPath = path,
                        DataPath = dataPath,
                        CreateDefaultAdmin = !string.IsNullOrEmpty(adminPassword),
                        AdminPassword = adminPassword,
                        CreateService = createService,
                        AutoStart = autoStart
                    };

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
