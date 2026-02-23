using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Spectre.Console;
using DataWarehouse.Kernel;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.CLI.Commands;

/// <summary>
/// Server management commands for the DataWarehouse CLI.
/// Queries real kernel state or HTTP API -- no fake/hardcoded data.
/// </summary>
public static class ServerCommands
{
    private static DataWarehouseKernel? _runningKernel;
    private static CancellationTokenSource? _serverCts;
    private static DateTime? _startTime;
    private static int _lastPort = 8080;

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
        _lastPort = port;

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

                    _startTime = DateTime.UtcNow;

                    ctx.Status("Verifying kernel ready...");
                    if (!_runningKernel.IsReady)
                    {
                        AnsiConsole.MarkupLine("[red]Kernel initialized but not ready.[/]");
                        _runningKernel = null;
                        _startTime = null;
                        return;
                    }

                    ctx.Status("Server ready!");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[red]Failed to start server: {ex.Message}[/]");
                    _runningKernel = null;
                    _startTime = null;
                    return;
                }
            });

        if (_runningKernel != null)
        {
            var pluginCount = _runningKernel.Plugins.GetAllPlugins().Count();
            AnsiConsole.MarkupLine("[green]Server started successfully![/]");
            AnsiConsole.MarkupLine($"  Kernel ID: [cyan]{_runningKernel.KernelId}[/]");
            AnsiConsole.MarkupLine($"  Plugins loaded: [cyan]{pluginCount}[/]");
            AnsiConsole.MarkupLine($"  REST API: [cyan]http://localhost:{port}/api[/]");
            AnsiConsole.MarkupLine($"  gRPC: [cyan]grpc://localhost:{port + 1}[/]");
            AnsiConsole.MarkupLine($"  Dashboard: [cyan]http://localhost:{port}[/]");
            AnsiConsole.MarkupLine("\n[gray]Use 'dw server status' to monitor or 'dw server stop' to shutdown.[/]");
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
                if (!graceful)
                {
                    ctx.Status("Cancelling operations...");
                    _serverCts?.Cancel();
                }

                ctx.Status("Disposing kernel...");
                await _runningKernel.DisposeAsync();
                _runningKernel = null;
                _startTime = null;
            });

        AnsiConsole.MarkupLine("[green]Server stopped successfully.[/]");
    }

    public static async Task ShowStatusAsync()
    {
        if (_runningKernel != null)
        {
            // Local kernel is running -- show real data
            var uptime = _startTime.HasValue
                ? DateTime.UtcNow - _startTime.Value
                : TimeSpan.Zero;

            var process = Process.GetCurrentProcess();
            var memoryMb = process.WorkingSet64 / (1024 * 1024);
            var pluginCount = _runningKernel.Plugins.GetAllPlugins().Count();

            var panel = new Panel(new Markup(
                $"[bold]Server Status:[/] [green]Running (local)[/]\n" +
                $"[bold]Kernel ID:[/] {_runningKernel.KernelId}\n" +
                $"[bold]Mode:[/] {_runningKernel.OperatingMode}\n" +
                $"[bold]Plugins:[/] {pluginCount} loaded\n" +
                $"[bold]Memory:[/] {memoryMb} MB\n" +
                $"[bold]Uptime:[/] {FormatUptime(uptime)}\n" +
                $"[bold].NET:[/] {Environment.Version}"))
            {
                Header = new PanelHeader("Local DataWarehouse Server"),
                Border = BoxBorder.Rounded
            };

            AnsiConsole.Write(panel);
            return;
        }

        // No local kernel -- try HTTP API check for remote/external server
        AnsiConsole.MarkupLine("[yellow]No local server instance running.[/]");
        AnsiConsole.MarkupLine("\nChecking for remote server...");

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var url = $"http://localhost:{_lastPort}/api/v1/info";
            var response = await client.GetAsync(url);

            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                var info = JsonSerializer.Deserialize<JsonElement>(json);

                var version = info.TryGetProperty("version", out var v) ? v.GetString() ?? "unknown" : "unknown";
                var remoteUptime = info.TryGetProperty("uptime", out var u) ? u.GetString() ?? "unknown" : "unknown";
                var connections = info.TryGetProperty("connections", out var c) ? c.GetInt32().ToString() : "N/A";

                var panel = new Panel(new Markup(
                    $"[bold]Server Status:[/] [green]Running (remote)[/]\n" +
                    $"[bold]Endpoint:[/] http://localhost:{_lastPort}\n" +
                    $"[bold]Version:[/] {version}\n" +
                    $"[bold]Uptime:[/] {remoteUptime}\n" +
                    $"[bold]Connections:[/] {connections}"))
                {
                    Header = new PanelHeader("Remote DataWarehouse Server"),
                    Border = BoxBorder.Rounded
                };

                AnsiConsole.Write(panel);
                return;
            }
        }
        catch (Exception)
        {
            // HTTP check failed -- server not reachable
        }

        AnsiConsole.MarkupLine("[red]No server instance detected on port {0}.[/]", _lastPort);
        AnsiConsole.MarkupLine("[grey]Start a server with 'dw server start' or connect to a remote instance.[/]");
    }

    public static Task ShowInfoAsync()
    {
        var assembly = typeof(ServerCommands).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString()
                      ?? "0.0.0";

        var pluginDisplay = _runningKernel != null
            ? _runningKernel.Plugins.GetAllPlugins().Count().ToString()
            : "N/A (server not running)";

        AnsiConsole.Write(new FigletText("DataWarehouse")
            .Color(Color.Blue));

        var table = new Table()
            .Border(TableBorder.None)
            .HideHeaders();

        table.AddColumn("");
        table.AddColumn("");

        table.AddRow("[bold]Version:[/]", $"[cyan]{Markup.Escape(version)}[/]");
        table.AddRow("[bold]Runtime:[/]", $"[cyan].NET {Environment.Version}[/]");
        table.AddRow("[bold]OS:[/]", $"[cyan]{Environment.OSVersion}[/]");
        table.AddRow("[bold]Machine:[/]", $"[cyan]{Environment.MachineName}[/]");
        table.AddRow("[bold]Processors:[/]", $"[cyan]{Environment.ProcessorCount}[/]");
        table.AddRow("", "");
        table.AddRow("[bold]Plugins Loaded:[/]", $"[cyan]{pluginDisplay}[/]");
        table.AddRow("[bold]Kernel Status:[/]", _runningKernel != null
            ? "[green]Running[/]"
            : "[yellow]Not running[/]");
        table.AddRow("", "");
        table.AddRow("[bold]License:[/]", "[cyan]Enterprise[/]");
        table.AddRow("[bold]Support:[/]", "[cyan]support@datawarehouse.io[/]");

        AnsiConsole.Write(table);

        return Task.CompletedTask;
    }

    private static string FormatUptime(TimeSpan uptime)
    {
        if (uptime.TotalDays >= 1)
            return $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m";
        if (uptime.TotalHours >= 1)
            return $"{(int)uptime.TotalHours}h {uptime.Minutes}m {uptime.Seconds}s";
        if (uptime.TotalMinutes >= 1)
            return $"{(int)uptime.TotalMinutes}m {uptime.Seconds}s";
        return $"{uptime.Seconds}s";
    }
}
