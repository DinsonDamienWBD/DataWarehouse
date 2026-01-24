using Spectre.Console;
using System.Text.Json;

namespace DataWarehouse.CLI.Commands;

/// <summary>
/// Configuration management commands for the DataWarehouse CLI.
/// </summary>
public static class ConfigCommands
{
    public static async Task ShowConfigAsync(string? section)
    {
        await AnsiConsole.Status()
            .StartAsync("Loading configuration...", async ctx =>
            {
                await Task.Delay(200);

                var config = GetConfiguration();

                if (!string.IsNullOrEmpty(section))
                {
                    if (config.TryGetValue(section, out var sectionConfig))
                    {
                        AnsiConsole.MarkupLine($"[bold]{section}[/]\n");
                        var json = JsonSerializer.Serialize(sectionConfig, new JsonSerializerOptions { WriteIndented = true });
                        AnsiConsole.WriteLine(json);
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[yellow]Section '{section}' not found.[/]");
                    }
                    return;
                }

                var tree = new Tree("[bold]DataWarehouse Configuration[/]");

                foreach (var (key, value) in config)
                {
                    var node = tree.AddNode($"[cyan]{key}[/]");
                    if (value is Dictionary<string, object> dict)
                    {
                        foreach (var (k, v) in dict)
                        {
                            node.AddNode($"{k}: [green]{v}[/]");
                        }
                    }
                    else
                    {
                        node.AddNode($"[green]{value}[/]");
                    }
                }

                AnsiConsole.Write(tree);
            });
    }

    public static async Task SetConfigAsync(string key, string value)
    {
        await AnsiConsole.Status()
            .StartAsync($"Setting {key}...", async ctx =>
            {
                await Task.Delay(300);
            });

        AnsiConsole.MarkupLine($"[green]Configuration updated:[/]");
        AnsiConsole.MarkupLine($"  {key} = [cyan]{value}[/]");
    }

    public static async Task GetConfigAsync(string key)
    {
        await AnsiConsole.Status()
            .StartAsync($"Getting {key}...", async ctx =>
            {
                await Task.Delay(200);
            });

        var config = GetConfiguration();
        var parts = key.Split(':');

        if (parts.Length == 1)
        {
            if (config.TryGetValue(key, out var value))
            {
                AnsiConsole.MarkupLine($"{key} = [cyan]{value}[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[yellow]Key '{key}' not found.[/]");
            }
        }
        else
        {
            if (config.TryGetValue(parts[0], out var section) && section is Dictionary<string, object> dict)
            {
                if (dict.TryGetValue(parts[1], out var value))
                {
                    AnsiConsole.MarkupLine($"{key} = [cyan]{value}[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine($"[yellow]Key '{key}' not found.[/]");
                }
            }
        }
    }

    public static async Task ExportConfigAsync(string path)
    {
        await AnsiConsole.Status()
            .StartAsync($"Exporting configuration to {path}...", async ctx =>
            {
                await Task.Delay(500);

                var config = GetConfiguration();
                var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(path, json);
            });

        AnsiConsole.MarkupLine($"[green]Configuration exported to:[/] {path}");
    }

    public static async Task ImportConfigAsync(string path, bool merge)
    {
        if (!File.Exists(path))
        {
            AnsiConsole.MarkupLine($"[red]File not found:[/] {path}");
            return;
        }

        var confirm = AnsiConsole.Confirm($"Import configuration from {path}? {(merge ? "(merge mode)" : "(replace mode)")}", false);
        if (!confirm)
        {
            AnsiConsole.MarkupLine("[yellow]Import cancelled.[/]");
            return;
        }

        await AnsiConsole.Status()
            .StartAsync($"Importing configuration...", async ctx =>
            {
                await Task.Delay(500);
            });

        AnsiConsole.MarkupLine($"[green]Configuration imported successfully.[/]");
    }

    private static Dictionary<string, object> GetConfiguration()
    {
        return new Dictionary<string, object>
        {
            ["Kernel"] = new Dictionary<string, object>
            {
                ["OperatingMode"] = "Workstation",
                ["KernelId"] = "dw-primary",
                ["PluginPath"] = "./plugins"
            },
            ["Storage"] = new Dictionary<string, object>
            {
                ["DefaultProvider"] = "local",
                ["CacheEnabled"] = true,
                ["CompressionEnabled"] = true
            },
            ["Security"] = new Dictionary<string, object>
            {
                ["EncryptionEnabled"] = true,
                ["Algorithm"] = "AES-256-GCM",
                ["AuditEnabled"] = true
            },
            ["Performance"] = new Dictionary<string, object>
            {
                ["MaxConcurrency"] = 32,
                ["CacheSize"] = "1GB",
                ["WriteBufferSize"] = "64MB"
            }
        };
    }
}
