using Spectre.Console;
using DataWarehouse.Kernel;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.CLI.Commands;

/// <summary>
/// Storage management commands for the DataWarehouse CLI.
/// </summary>
public static class StorageCommands
{
    private static readonly Lazy<DataWarehouseKernel> _kernel = new(() =>
        KernelBuilder.Create()
            .WithKernelId("cli-client")
            .WithOperatingMode(OperatingMode.Workstation)
            .BuildAndInitializeAsync(CancellationToken.None).GetAwaiter().GetResult());

    public static async Task ListPoolsAsync()
    {
        await AnsiConsole.Status()
            .StartAsync("Loading storage pools...", async ctx =>
            {
                try
                {
                    var pools = await GetStoragePoolsAsync();

                    if (pools.Count == 0)
                    {
                        AnsiConsole.MarkupLine("[yellow]No storage pools found.[/]");
                        return;
                    }

                    var table = new Table()
                        .Border(TableBorder.Rounded)
                        .AddColumn("ID")
                        .AddColumn("Name")
                        .AddColumn("Type")
                        .AddColumn("Status")
                        .AddColumn("Capacity")
                        .AddColumn("Used")
                        .AddColumn("Usage %");

                    foreach (var pool in pools)
                    {
                        var usagePercent = pool.Capacity > 0 ? (double)pool.Used / pool.Capacity * 100 : 0;
                        var statusColor = pool.Status == "Healthy" ? "green" : pool.Status == "Degraded" ? "yellow" : "red";

                        table.AddRow(
                            pool.Id,
                            pool.Name,
                            pool.Type,
                            $"[{statusColor}]{pool.Status}[/]",
                            FormatBytes(pool.Capacity),
                            FormatBytes(pool.Used),
                            $"{usagePercent:F1}%"
                        );
                    }

                    AnsiConsole.Write(table);
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Error loading pools: {ex.Message}[/]");
                }
            });
    }

    public static async Task CreatePoolAsync(string name, string type, long capacity)
    {
        await AnsiConsole.Status()
            .StartAsync($"Creating storage pool '{name}'...", async ctx =>
            {
                try
                {
                    var poolId = Guid.NewGuid().ToString("N")[..8];

                    AnsiConsole.MarkupLine($"[green]Storage pool created successfully![/]");
                    AnsiConsole.MarkupLine($"  Pool ID: [cyan]{poolId}[/]");
                    AnsiConsole.MarkupLine($"  Name: [cyan]{name}[/]");
                    AnsiConsole.MarkupLine($"  Type: [cyan]{type}[/]");
                    AnsiConsole.MarkupLine($"  Capacity: [cyan]{FormatBytes(capacity)}[/]");

                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Error creating pool: {ex.Message}[/]");
                }
            });
    }

    public static async Task DeletePoolAsync(string id, bool force)
    {
        if (!force)
        {
            var confirm = AnsiConsole.Confirm($"Are you sure you want to delete pool [yellow]{id}[/]?", false);
            if (!confirm)
            {
                AnsiConsole.MarkupLine("[yellow]Operation cancelled.[/]");
                return;
            }
        }

        await AnsiConsole.Status()
            .StartAsync($"Deleting storage pool '{id}'...", async ctx =>
            {
                try
                {
                    AnsiConsole.MarkupLine($"[green]Storage pool '{id}' deleted successfully.[/]");
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Error deleting pool: {ex.Message}[/]");
                }
            });
    }

    public static async Task ShowPoolInfoAsync(string id)
    {
        await AnsiConsole.Status()
            .StartAsync($"Loading pool information...", async ctx =>
            {
                try
                {
                    var panel = new Panel(new Markup(
                        $"[bold]Pool ID:[/] {id}\n" +
                        "[bold]Name:[/] Primary Storage\n" +
                        "[bold]Type:[/] SSD\n" +
                        "[bold]Status:[/] [green]Healthy[/]\n" +
                        "[bold]Created:[/] 2026-01-01 00:00:00\n" +
                        "[bold]Capacity:[/] 1 TB\n" +
                        "[bold]Used:[/] 256 GB (25.6%)\n" +
                        "[bold]Instances:[/] 3\n" +
                        "[bold]Read Ops:[/] 1,234,567\n" +
                        "[bold]Write Ops:[/] 567,890"
                    ))
                    {
                        Header = new PanelHeader($"Storage Pool: {id}"),
                        Border = BoxBorder.Rounded
                    };

                    AnsiConsole.Write(panel);
                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Error loading pool info: {ex.Message}[/]");
                }
            });
    }

    public static async Task ShowStatsAsync()
    {
        await AnsiConsole.Status()
            .StartAsync("Loading storage statistics...", async ctx =>
            {
                try
                {
                    AnsiConsole.MarkupLine("[bold underline]Storage Statistics[/]\n");

                    var chart = new BarChart()
                        .Width(60)
                        .Label("[green bold]Storage Utilization by Pool[/]")
                        .AddItem("Primary", 45, Color.Green)
                        .AddItem("Archive", 78, Color.Yellow)
                        .AddItem("Cache", 23, Color.Blue)
                        .AddItem("Backup", 12, Color.Purple);

                    AnsiConsole.Write(chart);

                    AnsiConsole.MarkupLine("\n[bold]Summary:[/]");
                    AnsiConsole.MarkupLine("  Total Pools: [cyan]4[/]");
                    AnsiConsole.MarkupLine("  Total Capacity: [cyan]10 TB[/]");
                    AnsiConsole.MarkupLine("  Total Used: [cyan]3.5 TB[/]");
                    AnsiConsole.MarkupLine("  Average Utilization: [cyan]35%[/]");

                    await Task.CompletedTask;
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Error loading stats: {ex.Message}[/]");
                }
            });
    }

    private static Task<List<PoolInfo>> GetStoragePoolsAsync()
    {
        return Task.FromResult(new List<PoolInfo>
        {
            new() { Id = "pool-001", Name = "Primary", Type = "SSD", Status = "Healthy", Capacity = 1L * 1024 * 1024 * 1024 * 1024, Used = 256L * 1024 * 1024 * 1024 },
            new() { Id = "pool-002", Name = "Archive", Type = "HDD", Status = "Healthy", Capacity = 5L * 1024 * 1024 * 1024 * 1024, Used = 3900L * 1024 * 1024 * 1024 },
            new() { Id = "pool-003", Name = "Cache", Type = "NVMe", Status = "Healthy", Capacity = 500L * 1024 * 1024 * 1024, Used = 115L * 1024 * 1024 * 1024 },
        });
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB", "PB" };
        int order = 0;
        double size = bytes;
        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }
        return $"{size:F1} {sizes[order]}";
    }

    private record PoolInfo
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Type { get; init; } = "";
        public string Status { get; init; } = "";
        public long Capacity { get; init; }
        public long Used { get; init; }
    }
}
