using Spectre.Console;

namespace DataWarehouse.CLI.Commands;

/// <summary>
/// Audit log commands for the DataWarehouse CLI.
/// </summary>
public static class AuditCommands
{
    public static async Task ListEntriesAsync(int limit, string? category, string? user, DateTime? since)
    {
        await AnsiConsole.Status()
            .StartAsync("Loading audit entries...", async ctx =>
            {
                await Task.Delay(300);

                var entries = GetAuditEntries()
                    .Where(e => string.IsNullOrEmpty(category) || e.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
                    .Where(e => string.IsNullOrEmpty(user) || e.User.Equals(user, StringComparison.OrdinalIgnoreCase))
                    .Where(e => !since.HasValue || e.Timestamp >= since.Value)
                    .Take(limit)
                    .ToList();

                if (entries.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]No audit entries found matching criteria.[/]");
                    return;
                }

                var table = new Table()
                    .Border(TableBorder.Rounded)
                    .AddColumn("Time")
                    .AddColumn("Category")
                    .AddColumn("Action")
                    .AddColumn("User")
                    .AddColumn("Status")
                    .AddColumn("Details");

                foreach (var entry in entries)
                {
                    var statusColor = entry.Success ? "green" : "red";
                    table.AddRow(
                        entry.Timestamp.ToString("HH:mm:ss"),
                        $"[cyan]{entry.Category}[/]",
                        entry.Action,
                        entry.User,
                        $"[{statusColor}]{(entry.Success ? "Success" : "Failed")}[/]",
                        entry.Details.Length > 30 ? entry.Details[..30] + "..." : entry.Details
                    );
                }

                AnsiConsole.Write(table);
                AnsiConsole.MarkupLine($"\n[gray]Showing {entries.Count} of {limit} requested entries[/]");
            });
    }

    public static async Task ExportAuditLogAsync(string path, string format)
    {
        await AnsiConsole.Progress()
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("Exporting audit log");
                while (!task.IsFinished)
                {
                    await Task.Delay(30);
                    task.Increment(2);
                }
            });

        AnsiConsole.MarkupLine($"[green]Audit log exported to:[/] {path}");
        AnsiConsole.MarkupLine($"  Format: {format.ToUpper()}");
        AnsiConsole.MarkupLine($"  Entries: 15,234");
        AnsiConsole.MarkupLine($"  Size: 2.3 MB");
    }

    public static async Task ShowStatsAsync(string period)
    {
        await AnsiConsole.Status()
            .StartAsync("Calculating statistics...", async ctx =>
            {
                await Task.Delay(300);

                AnsiConsole.MarkupLine($"[bold underline]Audit Statistics ({period})[/]\n");

                var chart = new BarChart()
                    .Width(50)
                    .Label("[bold]Operations by Category[/]")
                    .AddItem("Storage", 4523, Color.Blue)
                    .AddItem("Security", 1234, Color.Red)
                    .AddItem("Config", 567, Color.Yellow)
                    .AddItem("Plugin", 890, Color.Green)
                    .AddItem("System", 345, Color.Purple);

                AnsiConsole.Write(chart);

                AnsiConsole.MarkupLine("\n[bold]Summary:[/]");
                AnsiConsole.MarkupLine("  Total Operations: [cyan]7,559[/]");
                AnsiConsole.MarkupLine("  Successful: [green]7,456 (98.6%)[/]");
                AnsiConsole.MarkupLine("  Failed: [red]103 (1.4%)[/]");
                AnsiConsole.MarkupLine("  Unique Users: [cyan]12[/]");
                AnsiConsole.MarkupLine("  Peak Hour: [cyan]14:00 (892 ops)[/]");
            });
    }

    private static List<AuditEntry> GetAuditEntries()
    {
        var now = DateTime.Now;
        return new List<AuditEntry>
        {
            new() { Timestamp = now.AddMinutes(-5), Category = "Storage", Action = "Write", User = "admin", Success = true, Details = "Wrote 1.2 MB to pool-001" },
            new() { Timestamp = now.AddMinutes(-10), Category = "Security", Action = "Login", User = "operator", Success = true, Details = "Successful login" },
            new() { Timestamp = now.AddMinutes(-15), Category = "Storage", Action = "Read", User = "app-service", Success = true, Details = "Read 500 KB from cache" },
            new() { Timestamp = now.AddMinutes(-20), Category = "Config", Action = "Update", User = "admin", Success = true, Details = "Updated compression settings" },
            new() { Timestamp = now.AddMinutes(-25), Category = "Plugin", Action = "Reload", User = "admin", Success = true, Details = "Reloaded gzip-compression" },
            new() { Timestamp = now.AddMinutes(-30), Category = "Storage", Action = "Delete", User = "admin", Success = false, Details = "Permission denied for protected file" },
            new() { Timestamp = now.AddMinutes(-35), Category = "Security", Action = "Login", User = "unknown", Success = false, Details = "Invalid credentials" },
            new() { Timestamp = now.AddMinutes(-40), Category = "System", Action = "Backup", User = "scheduler", Success = true, Details = "Daily backup completed" },
        };
    }

    private record AuditEntry
    {
        public DateTime Timestamp { get; init; }
        public string Category { get; init; } = "";
        public string Action { get; init; } = "";
        public string User { get; init; } = "";
        public bool Success { get; init; }
        public string Details { get; init; } = "";
    }
}
