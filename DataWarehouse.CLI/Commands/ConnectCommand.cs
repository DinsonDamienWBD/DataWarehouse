using Spectre.Console;
using DataWarehouse.Integration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.CLI.Commands;

/// <summary>
/// Connect command - Connects to an existing DataWarehouse instance.
/// </summary>
public static class ConnectCommand
{
    public static async Task ExecuteAsync(
        string? host,
        int port,
        string? localPath,
        string? authToken,
        bool useTls,
        string? profileName)
    {
        await AnsiConsole.Status()
            .StartAsync("Connecting to DataWarehouse instance...", async ctx =>
            {
                try
                {
                    var dwHost = new DataWarehouseHost(NullLoggerFactory.Instance);

                    ConnectionTarget target;

                    if (!string.IsNullOrEmpty(localPath))
                    {
                        target = ConnectionTarget.Local(localPath);
                        AnsiConsole.MarkupLine($"Connecting to local instance at: [cyan]{localPath}[/]");
                    }
                    else if (!string.IsNullOrEmpty(host))
                    {
                        target = ConnectionTarget.Remote(host, port, useTls);
                        target.AuthToken = authToken;
                        AnsiConsole.MarkupLine($"Connecting to remote instance at: [cyan]{(useTls ? "https" : "http")}://{host}:{port}[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine("[red]Error: Either --host or --local-path must be specified.[/]");
                        return;
                    }

                    var result = await dwHost.ConnectAsync(target);

                    if (result.Success)
                    {
                        AnsiConsole.MarkupLine($"[green]Connected successfully![/]");
                        AnsiConsole.MarkupLine($"  Instance ID: [cyan]{result.InstanceId}[/]");

                        if (result.Capabilities != null)
                        {
                            var table = new Table()
                                .Border(TableBorder.Rounded)
                                .AddColumn("Capability")
                                .AddColumn("Value");

                            table.AddRow("Version", result.Capabilities.Version);
                            table.AddRow("Plugins", result.Capabilities.AvailablePlugins.Count.ToString());
                            table.AddRow("Storage Backends", string.Join(", ", result.Capabilities.StorageBackends));
                            table.AddRow("Clustered", result.Capabilities.IsClustered ? "Yes" : "No");
                            table.AddRow("Node Count", result.Capabilities.NodeCount.ToString());
                            table.AddRow("AI Capabilities", result.Capabilities.HasAICapabilities ? "Yes" : "No");

                            AnsiConsole.Write(table);
                        }

                        // Save profile if specified
                        if (!string.IsNullOrEmpty(profileName))
                        {
                            await dwHost.SaveConfigurationAsync(profileName);
                            AnsiConsole.MarkupLine($"  Saved to profile: [cyan]{profileName}[/]");
                        }
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[red]Connection failed: {result.Message}[/]");
                        if (result.Exception != null)
                        {
                            AnsiConsole.WriteException(result.Exception);
                        }
                    }

                    await dwHost.DisposeAsync();
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Error connecting: {ex.Message}[/]");
                    AnsiConsole.WriteException(ex);
                }
            });
    }

    public static async Task ListProfilesAsync()
    {
        try
        {
            var dwHost = new DataWarehouseHost(NullLoggerFactory.Instance);
            var profiles = dwHost.ListProfiles();

            if (!profiles.Any())
            {
                AnsiConsole.MarkupLine("[yellow]No saved profiles found.[/]");
                return;
            }

            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Profile Name")
                .AddColumn("Last Saved");

            foreach (var profileName in profiles)
            {
                var profile = await dwHost.LoadProfileAsync(profileName);
                if (profile != null)
                {
                    table.AddRow(profile.Name, profile.SavedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
                }
            }

            AnsiConsole.Write(table);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error listing profiles: {ex.Message}[/]");
        }
    }
}
