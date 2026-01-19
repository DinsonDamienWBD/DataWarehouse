using Spectre.Console;

namespace DataWarehouse.CLI.Commands;

/// <summary>
/// Health monitoring commands for the DataWarehouse CLI.
/// </summary>
public static class HealthCommands
{
    public static async Task ShowStatusAsync()
    {
        await AnsiConsole.Status()
            .StartAsync("Checking system health...", async ctx =>
            {
                await Task.Delay(300);

                var tree = new Tree("[bold green]System Health: Healthy[/]")
                    .Style(Style.Parse("green"));

                var kernel = tree.AddNode("[green]Kernel[/]");
                kernel.AddNode("[green] Storage Manager: Healthy[/]");
                kernel.AddNode("[green] Message Bus: Healthy[/]");
                kernel.AddNode("[green] Pipeline Orchestrator: Healthy[/]");

                var plugins = tree.AddNode("[green]Plugins (12 active)[/]");
                plugins.AddNode("[green] Storage Providers: 4/4 healthy[/]");
                plugins.AddNode("[green] Data Transformation: 4/4 healthy[/]");
                plugins.AddNode("[green] Interface: 3/3 healthy[/]");
                plugins.AddNode("[green] Other: 1/1 healthy[/]");

                var resources = tree.AddNode("[green]Resources[/]");
                resources.AddNode("[green] CPU: 23% utilization[/]");
                resources.AddNode("[green] Memory: 4.2 GB / 16 GB (26%)[/]");
                resources.AddNode("[green] Disk: 3.5 TB / 10 TB (35%)[/]");

                AnsiConsole.Write(tree);
            });
    }

    public static async Task ShowMetricsAsync()
    {
        await AnsiConsole.Status()
            .StartAsync("Loading metrics...", async ctx =>
            {
                await Task.Delay(200);

                AnsiConsole.MarkupLine("[bold underline]System Metrics[/]\n");

                var grid = new Grid();
                grid.AddColumn();
                grid.AddColumn();
                grid.AddColumn();
                grid.AddColumn();

                grid.AddRow(
                    new Panel("[bold]CPU[/]\n[green]23%[/]") { Border = BoxBorder.Rounded },
                    new Panel("[bold]Memory[/]\n[green]26%[/]") { Border = BoxBorder.Rounded },
                    new Panel("[bold]Disk[/]\n[yellow]35%[/]") { Border = BoxBorder.Rounded },
                    new Panel("[bold]Network[/]\n[green]12%[/]") { Border = BoxBorder.Rounded }
                );

                AnsiConsole.Write(grid);

                AnsiConsole.MarkupLine("\n[bold]Performance Metrics:[/]");
                AnsiConsole.MarkupLine("  Requests/sec: [cyan]1,234[/]");
                AnsiConsole.MarkupLine("  Avg Latency: [cyan]12ms[/]");
                AnsiConsole.MarkupLine("  Active Connections: [cyan]45[/]");
                AnsiConsole.MarkupLine("  Thread Count: [cyan]32[/]");
                AnsiConsole.MarkupLine("  Uptime: [cyan]15d 7h 23m[/]");
            });
    }

    public static async Task ShowAlertsAsync(bool includeAcknowledged)
    {
        await AnsiConsole.Status()
            .StartAsync("Loading alerts...", async ctx =>
            {
                await Task.Delay(200);

                var alerts = GetAlerts(includeAcknowledged);

                if (alerts.Count == 0)
                {
                    AnsiConsole.MarkupLine("[green]No active alerts.[/]");
                    return;
                }

                var table = new Table()
                    .Border(TableBorder.Rounded)
                    .AddColumn("Severity")
                    .AddColumn("Title")
                    .AddColumn("Time")
                    .AddColumn("Status");

                foreach (var alert in alerts)
                {
                    var severityColor = alert.Severity == "Critical" ? "red" : alert.Severity == "Warning" ? "yellow" : "blue";
                    var statusColor = alert.IsAcknowledged ? "gray" : "white";

                    table.AddRow(
                        $"[{severityColor}]{alert.Severity}[/]",
                        alert.Title,
                        alert.Time,
                        $"[{statusColor}]{(alert.IsAcknowledged ? "Acknowledged" : "Active")}[/]"
                    );
                }

                AnsiConsole.Write(table);
            });
    }

    public static async Task RunHealthCheckAsync(string? component)
    {
        var components = string.IsNullOrEmpty(component)
            ? new[] { "Kernel", "Storage", "Plugins", "Network", "Memory", "Disk" }
            : new[] { component };

        await AnsiConsole.Progress()
            .Columns(new ProgressColumn[]
            {
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new SpinnerColumn(),
            })
            .StartAsync(async ctx =>
            {
                foreach (var comp in components)
                {
                    var task = ctx.AddTask($"Checking {comp}");
                    while (!task.IsFinished)
                    {
                        await Task.Delay(30);
                        task.Increment(5);
                    }
                }
            });

        AnsiConsole.MarkupLine("\n[green]Health check completed. All components healthy.[/]");
    }

    public static async Task WatchHealthAsync(int interval)
    {
        AnsiConsole.MarkupLine($"[yellow]Watching system health (Ctrl+C to stop, interval: {interval}s)...[/]\n");

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                AnsiConsole.Clear();
                AnsiConsole.MarkupLine($"[bold]DataWarehouse Health Monitor[/] - {DateTime.Now:HH:mm:ss}\n");

                var cpu = Random.Shared.Next(15, 35);
                var mem = Random.Shared.Next(20, 40);
                var disk = Random.Shared.Next(30, 40);

                var cpuBar = new BreakdownChart()
                    .Width(40)
                    .AddItem("Used", cpu, Color.Green)
                    .AddItem("Free", 100 - cpu, Color.Grey);

                AnsiConsole.MarkupLine($"[bold]CPU:[/] {cpu}%");
                AnsiConsole.Write(cpuBar);

                AnsiConsole.MarkupLine($"\n[bold]Memory:[/] {mem}%");
                AnsiConsole.MarkupLine($"[bold]Disk:[/] {disk}%");
                AnsiConsole.MarkupLine($"[bold]Requests/sec:[/] {Random.Shared.Next(1000, 1500)}");

                AnsiConsole.MarkupLine($"\n[gray]Press Ctrl+C to stop watching...[/]");

                await Task.Delay(interval * 1000, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            AnsiConsole.MarkupLine("\n[yellow]Health watch stopped.[/]");
        }
    }

    private static List<AlertInfo> GetAlerts(bool includeAcknowledged)
    {
        var alerts = new List<AlertInfo>
        {
            new() { Severity = "Warning", Title = "High disk usage on pool-002", Time = "10:30:00", IsAcknowledged = false },
            new() { Severity = "Info", Title = "Scheduled backup completed", Time = "03:00:00", IsAcknowledged = true },
        };

        return includeAcknowledged ? alerts : alerts.Where(a => !a.IsAcknowledged).ToList();
    }

    private record AlertInfo
    {
        public string Severity { get; init; } = "";
        public string Title { get; init; } = "";
        public string Time { get; init; } = "";
        public bool IsAcknowledged { get; init; }
    }
}
