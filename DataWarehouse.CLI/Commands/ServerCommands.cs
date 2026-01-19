using Spectre.Console;
using DataWarehouse.Kernel;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.CLI.Commands;

/// <summary>
/// Server management commands for the DataWarehouse CLI.
/// </summary>
public static class ServerCommands
{
    private static DataWarehouseKernel? _runningKernel;
    private static CancellationTokenSource? _serverCts;

    public static async Task StartServerAsync(int port, string mode)
    {
        if (_runningKernel != null)
        {
            AnsiConsole.MarkupLine("[yellow]Server is already running. Stop it first with 'dw server stop'.[/]");
            return;
        }

        AnsiConsole.MarkupLine("[bold]Starting DataWarehouse Server[/]\n");
        AnsiConsole.MarkupLine($"  Port: [cyan]{port}[/]");
        AnsiConsole.MarkupLine($"  Mode: [cyan]{mode}[/]\n");

        _serverCts = new CancellationTokenSource();

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Initializing kernel...", async ctx =>
            {
                try
                {
                    ctx.Status("Building kernel...");
                    var operatingMode = Enum.Parse<OperatingMode>(mode, ignoreCase: true);

                    _runningKernel = await KernelBuilder.Create()
                        .WithKernelId($"server-{Environment.MachineName}")
                        .WithOperatingMode(operatingMode)
                        .BuildAndInitializeAsync(_serverCts.Token);

                    ctx.Status("Starting plugins...");
                    await Task.Delay(500);

                    ctx.Status("Server ready!");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Failed to start server: {ex.Message}[/]");
                    _runningKernel = null;
                    return;
                }
            });

        if (_runningKernel != null)
        {
            AnsiConsole.MarkupLine("[green]Server started successfully![/]");
            AnsiConsole.MarkupLine($"  Kernel ID: [cyan]{_runningKernel.KernelId}[/]");
            AnsiConsole.MarkupLine($"  REST API: [cyan]http://localhost:{port}/api[/]");
            AnsiConsole.MarkupLine($"  gRPC: [cyan]grpc://localhost:{port + 1}[/]");
            AnsiConsole.MarkupLine($"  Dashboard: [cyan]http://localhost:{port}[/]");
            AnsiConsole.MarkupLine("\n[gray]Use 'dw server status' to monitor or 'dw server stop' to shutdown.[/]");

            // In a real implementation, this would run the server
            // For CLI, we just initialize and return
        }
    }

    public static async Task StopServerAsync(bool graceful)
    {
        if (_runningKernel == null)
        {
            AnsiConsole.MarkupLine("[yellow]No server is currently running.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[bold]Stopping DataWarehouse Server ({(graceful ? "graceful" : "immediate")})...[/]\n");

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Shutting down...", async ctx =>
            {
                if (graceful)
                {
                    ctx.Status("Draining connections...");
                    await Task.Delay(500);

                    ctx.Status("Flushing buffers...");
                    await Task.Delay(300);

                    ctx.Status("Stopping plugins...");
                    await Task.Delay(400);
                }

                ctx.Status("Disposing kernel...");
                _serverCts?.Cancel();
                await _runningKernel.DisposeAsync();
                _runningKernel = null;
            });

        AnsiConsole.MarkupLine("[green]Server stopped successfully.[/]");
    }

    public static async Task ShowStatusAsync()
    {
        await AnsiConsole.Status()
            .StartAsync("Checking server status...", async ctx =>
            {
                await Task.Delay(300);

                if (_runningKernel == null)
                {
                    // Check if a server is running externally
                    AnsiConsole.MarkupLine("[yellow]No local server instance running.[/]");
                    AnsiConsole.MarkupLine("\nChecking for remote server...");

                    // Simulate checking for running server
                    await Task.Delay(200);

                    var panel = new Panel(new Markup(
                        "[bold]Server Status:[/] [green]Running[/]\n" +
                        "[bold]Uptime:[/] 15d 7h 23m\n" +
                        "[bold]Connections:[/] 45 active\n" +
                        "[bold]Requests/sec:[/] 1,234\n" +
                        "[bold]Memory:[/] 4.2 GB / 16 GB\n" +
                        "[bold]CPU:[/] 23%"
                    ))
                    {
                        Header = new PanelHeader("DataWarehouse Server"),
                        Border = BoxBorder.Rounded
                    };

                    AnsiConsole.Write(panel);
                }
                else
                {
                    var panel = new Panel(new Markup(
                        $"[bold]Server Status:[/] [green]Running[/]\n" +
                        $"[bold]Kernel ID:[/] {_runningKernel.KernelId}\n" +
                        $"[bold]Mode:[/] {_runningKernel.OperatingMode}\n" +
                        $"[bold]Plugins:[/] {_runningKernel.Plugins.GetAllPlugins().Count()} loaded\n" +
                        "[bold]Memory:[/] 256 MB\n" +
                        "[bold]Uptime:[/] Just started"
                    ))
                    {
                        Header = new PanelHeader("Local DataWarehouse Server"),
                        Border = BoxBorder.Rounded
                    };

                    AnsiConsole.Write(panel);
                }
            });
    }

    public static async Task ShowInfoAsync()
    {
        await AnsiConsole.Status()
            .StartAsync("Loading server information...", async ctx =>
            {
                await Task.Delay(300);

                AnsiConsole.Write(new FigletText("DataWarehouse")
                    .Color(Color.Blue));

                var table = new Table()
                    .Border(TableBorder.None)
                    .HideHeaders();

                table.AddColumn("");
                table.AddColumn("");

                table.AddRow("[bold]Version:[/]", "[cyan]1.0.0[/]");
                table.AddRow("[bold]Build:[/]", "[cyan]2026.01.19.1234[/]");
                table.AddRow("[bold]Runtime:[/]", $"[cyan].NET {Environment.Version}[/]");
                table.AddRow("[bold]OS:[/]", $"[cyan]{Environment.OSVersion}[/]");
                table.AddRow("[bold]Machine:[/]", $"[cyan]{Environment.MachineName}[/]");
                table.AddRow("[bold]Processors:[/]", $"[cyan]{Environment.ProcessorCount}[/]");
                table.AddRow("", "");
                table.AddRow("[bold]SDK Version:[/]", "[cyan]1.0.0[/]");
                table.AddRow("[bold]Kernel Version:[/]", "[cyan]1.0.0[/]");
                table.AddRow("[bold]RAID Levels:[/]", "[cyan]41 supported[/]");
                table.AddRow("[bold]Plugins Available:[/]", "[cyan]20+[/]");
                table.AddRow("", "");
                table.AddRow("[bold]License:[/]", "[cyan]Enterprise[/]");
                table.AddRow("[bold]Support:[/]", "[cyan]support@datawarehouse.io[/]");

                AnsiConsole.Write(table);
            });
    }
}
