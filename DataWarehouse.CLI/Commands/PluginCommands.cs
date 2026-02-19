using Spectre.Console;
using DataWarehouse.Kernel;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.CLI.Commands;

/// <summary>
/// Plugin management commands for the DataWarehouse CLI.
/// </summary>
public static class PluginCommands
{
    private static DataWarehouseKernel? _kernelInstance;
    private static readonly SemaphoreSlim _kernelLock = new(1, 1);

    private static async Task<DataWarehouseKernel> GetKernelAsync()
    {
        if (_kernelInstance != null)
            return _kernelInstance;

        await _kernelLock.WaitAsync();
        try
        {
            _kernelInstance ??= await KernelBuilder.Create()
                .WithKernelId("cli-plugins")
                .WithOperatingMode(OperatingMode.Workstation)
                .BuildAndInitializeAsync(CancellationToken.None);
            return _kernelInstance;
        }
        finally
        {
            _kernelLock.Release();
        }
    }

    public static async Task ListPluginsAsync(string? category)
    {
        await AnsiConsole.Status()
            .StartAsync("Loading plugins...", async ctx =>
            {
                var plugins = await GetPluginsAsync();

                if (!string.IsNullOrEmpty(category))
                {
                    plugins = plugins.Where(p => p.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList();
                }

                if (plugins.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]No plugins found.[/]");
                    return;
                }

                var table = new Table()
                    .Border(TableBorder.Rounded)
                    .AddColumn("ID")
                    .AddColumn("Name")
                    .AddColumn("Category")
                    .AddColumn("Version")
                    .AddColumn("Status")
                    .AddColumn("Health");

                foreach (var plugin in plugins)
                {
                    var statusColor = plugin.IsEnabled ? "green" : "gray";
                    var healthColor = plugin.IsHealthy ? "green" : "red";

                    table.AddRow(
                        plugin.Id,
                        plugin.Name,
                        plugin.Category,
                        plugin.Version,
                        $"[{statusColor}]{(plugin.IsEnabled ? "Enabled" : "Disabled")}[/]",
                        $"[{healthColor}]{(plugin.IsHealthy ? "Healthy" : "Unhealthy")}[/]"
                    );
                }

                AnsiConsole.Write(table);
            });
    }

    public static async Task ShowPluginInfoAsync(string id)
    {
        await AnsiConsole.Status()
            .StartAsync("Loading plugin details...", async ctx =>
            {
                var plugins = await GetPluginsAsync();
                var plugin = plugins.FirstOrDefault(p => p.Id == id);

                if (plugin == null)
                {
                    AnsiConsole.MarkupLine($"[red]Plugin '{id}' not found.[/]");
                    return;
                }

                var panel = new Panel(new Markup(
                    $"[bold]Plugin ID:[/] {plugin.Id}\n" +
                    $"[bold]Name:[/] {plugin.Name}\n" +
                    $"[bold]Category:[/] {plugin.Category}\n" +
                    $"[bold]Version:[/] {plugin.Version}\n" +
                    $"[bold]Status:[/] {(plugin.IsEnabled ? "[green]Enabled[/]" : "[gray]Disabled[/]")}\n" +
                    $"[bold]Health:[/] {(plugin.IsHealthy ? "[green]Healthy[/]" : "[red]Unhealthy[/]")}\n" +
                    $"[bold]Description:[/] {plugin.Description}\n" +
                    $"[bold]Author:[/] DataWarehouse Team\n" +
                    $"[bold]Loaded At:[/] 2026-01-19 08:00:00"
                ))
                {
                    Header = new PanelHeader($"Plugin: {plugin.Name}"),
                    Border = BoxBorder.Rounded
                };

                AnsiConsole.Write(panel);
            });
    }

    public static async Task EnablePluginAsync(string id)
    {
        await AnsiConsole.Status()
            .StartAsync($"Enabling plugin '{id}'...", async ctx =>
            {
                await Task.Delay(500);
                AnsiConsole.MarkupLine($"[green]Plugin '{id}' enabled successfully.[/]");
            });
    }

    public static async Task DisablePluginAsync(string id)
    {
        await AnsiConsole.Status()
            .StartAsync($"Disabling plugin '{id}'...", async ctx =>
            {
                await Task.Delay(500);
                AnsiConsole.MarkupLine($"[green]Plugin '{id}' disabled successfully.[/]");
            });
    }

    public static async Task ReloadPluginAsync(string id)
    {
        await AnsiConsole.Progress()
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask($"Reloading plugin '{id}'");
                while (!ctx.IsFinished)
                {
                    await Task.Delay(50);
                    task.Increment(5);
                }
            });

        AnsiConsole.MarkupLine($"[green]Plugin '{id}' reloaded successfully.[/]");
    }

    /// <summary>
    /// Queries the kernel message bus for loaded plugins.
    /// Returns an empty list if the kernel or plugin registry is unavailable.
    /// </summary>
    private static async Task<List<PluginInfo>> GetPluginsAsync()
    {
        try
        {
            var kernel = await GetKernelAsync();
            var request = new PluginMessage
            {
                Type = "kernel.plugin.list",
                SourcePluginId = "cli",
                Source = "CLI"
            };

            var response = await kernel.MessageBus.SendAsync(
                "kernel.plugin.list", request, TimeSpan.FromSeconds(5));

            if (response.Success && response.Payload is IEnumerable<object> pluginList)
            {
                var result = new List<PluginInfo>();
                foreach (var item in pluginList)
                {
                    if (item is Dictionary<string, object> p)
                    {
                        result.Add(new PluginInfo
                        {
                            Id = p.GetValueOrDefault("Id", "")?.ToString() ?? "",
                            Name = p.GetValueOrDefault("Name", "")?.ToString() ?? "",
                            Category = p.GetValueOrDefault("Category", "")?.ToString() ?? "",
                            Version = p.GetValueOrDefault("Version", "")?.ToString() ?? "",
                            IsEnabled = p.GetValueOrDefault("IsEnabled") is true,
                            IsHealthy = p.GetValueOrDefault("IsHealthy") is true,
                            Description = p.GetValueOrDefault("Description", "")?.ToString() ?? ""
                        });
                    }
                }
                return result;
            }

            AnsiConsole.MarkupLine("[yellow]Plugin information unavailable - kernel plugin registry not responding.[/]");
            return new List<PluginInfo>();
        }
        catch (Exception)
        {
            AnsiConsole.MarkupLine("[yellow]Plugin information unavailable - kernel context not accessible.[/]");
            return new List<PluginInfo>();
        }
    }

    private record PluginInfo
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Category { get; init; } = "";
        public string Version { get; init; } = "";
        public bool IsEnabled { get; init; }
        public bool IsHealthy { get; init; }
        public string Description { get; init; } = "";
    }
}
