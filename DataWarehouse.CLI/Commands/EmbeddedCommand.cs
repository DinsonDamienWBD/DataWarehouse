using Spectre.Console;
using DataWarehouse.Integration;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.CLI.Commands;

/// <summary>
/// Embedded command - Runs a lightweight embedded DataWarehouse instance.
/// </summary>
public static class EmbeddedCommand
{
    public static async Task<int> ExecuteAsync(
        string? dataPath,
        int maxMemoryMb,
        bool exposeHttp,
        int httpPort,
        string[]? plugins)
    {
        try
        {
            // Create logger factory for embedded mode
            using var loggerFactory = LoggerFactory.Create(builder =>
            {
                builder.AddConsole();
                builder.SetMinimumLevel(LogLevel.Information);
            });

            var host = new DataWarehouseHost(loggerFactory);

            var config = new EmbeddedConfiguration
            {
                DataPath = dataPath,
                PersistData = !string.IsNullOrEmpty(dataPath),
                MaxMemoryMb = maxMemoryMb,
                ExposeHttp = exposeHttp,
                HttpPort = httpPort,
                Plugins = plugins?.ToList() ?? new List<string>()
            };

            AnsiConsole.MarkupLine("[bold green]Starting embedded DataWarehouse instance...[/]");
            AnsiConsole.MarkupLine($"  Data Path: [cyan]{dataPath ?? "(memory only)"}[/]");
            AnsiConsole.MarkupLine($"  Max Memory: [cyan]{maxMemoryMb} MB[/]");
            AnsiConsole.MarkupLine($"  HTTP: [cyan]{(exposeHttp ? $"Enabled on port {httpPort}" : "Disabled")}[/]");

            if (plugins != null && plugins.Length > 0)
            {
                AnsiConsole.MarkupLine($"  Plugins: [cyan]{string.Join(", ", plugins)}[/]");
            }

            AnsiConsole.MarkupLine("\n[yellow]Press Ctrl+C to stop the instance.[/]\n");

            // Setup cancellation
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
                AnsiConsole.MarkupLine("\n[yellow]Shutdown requested...[/]");
            };

            // Run embedded instance
            var exitCode = await host.RunEmbeddedAsync(config, cts.Token);

            if (exitCode == 0)
            {
                AnsiConsole.MarkupLine("[green]Embedded instance stopped successfully.[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"[red]Embedded instance stopped with exit code: {exitCode}[/]");
            }

            await host.DisposeAsync();
            return exitCode;
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error running embedded instance: {ex.Message}[/]");
            AnsiConsole.WriteException(ex);
            return 1;
        }
    }
}
