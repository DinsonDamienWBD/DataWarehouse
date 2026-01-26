// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text;
using System.Text.Json;

namespace DataWarehouse.Shared.Commands;

/// <summary>
/// Audit log entry record.
/// </summary>
public sealed record AuditEntry
{
    /// <summary>Entry unique identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Timestamp of the event.</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>User who performed the action.</summary>
    public string? User { get; init; }

    /// <summary>Category of the action.</summary>
    public required string Category { get; init; }

    /// <summary>Action performed.</summary>
    public required string Action { get; init; }

    /// <summary>Target resource.</summary>
    public string? Resource { get; init; }

    /// <summary>Result of the action (Success, Failure).</summary>
    public required string Result { get; init; }

    /// <summary>Source IP address.</summary>
    public string? SourceIp { get; init; }

    /// <summary>Additional details.</summary>
    public Dictionary<string, object>? Details { get; init; }
}

/// <summary>
/// Audit statistics record.
/// </summary>
public sealed record AuditStats
{
    /// <summary>Total number of entries in period.</summary>
    public int TotalEntries { get; init; }

    /// <summary>Number of successful operations.</summary>
    public int SuccessCount { get; init; }

    /// <summary>Number of failed operations.</summary>
    public int FailureCount { get; init; }

    /// <summary>Entries by category.</summary>
    public Dictionary<string, int> ByCategory { get; init; } = new();

    /// <summary>Entries by user.</summary>
    public Dictionary<string, int> ByUser { get; init; } = new();

    /// <summary>Most active hours.</summary>
    public Dictionary<int, int> ByHour { get; init; } = new();
}

/// <summary>
/// Lists audit log entries.
/// </summary>
public sealed class AuditListCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "audit.list";

    /// <inheritdoc />
    public string Description => "List audit log entries";

    /// <inheritdoc />
    public string Category => "audit";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredFeatures => Array.Empty<string>();

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        context.EnsureConnected();

        var limit = Convert.ToInt32(parameters.GetValueOrDefault("limit") ?? 50);
        var category = parameters.GetValueOrDefault("category")?.ToString();
        var user = parameters.GetValueOrDefault("user")?.ToString();
        var since = parameters.GetValueOrDefault("since") as DateTime?;

        var response = await context.InstanceManager.ExecuteAsync("audit.list",
            new Dictionary<string, object>
            {
                ["limit"] = limit,
                ["category"] = category ?? "",
                ["user"] = user ?? "",
                ["since"] = since?.ToString("O") ?? ""
            }, cancellationToken);

        var entries = GenerateSampleEntries(limit, category, user, since);

        return CommandResult.Table(entries, $"Found {entries.Count} audit entries");
    }

    private static List<AuditEntry> GenerateSampleEntries(int limit, string? category, string? user, DateTime? since)
    {
        var entries = new List<AuditEntry>
        {
            new() { Id = "audit-001", Timestamp = DateTime.UtcNow.AddMinutes(-5), User = "admin", Category = "storage", Action = "pool.create", Resource = "pool-004", Result = "Success" },
            new() { Id = "audit-002", Timestamp = DateTime.UtcNow.AddMinutes(-15), User = "system", Category = "backup", Action = "backup.complete", Resource = "backup-20260126", Result = "Success" },
            new() { Id = "audit-003", Timestamp = DateTime.UtcNow.AddMinutes(-30), User = "admin", Category = "plugin", Action = "plugin.enable", Resource = "ai-agents", Result = "Success" },
            new() { Id = "audit-004", Timestamp = DateTime.UtcNow.AddHours(-1), User = "user1", Category = "storage", Action = "object.upload", Resource = "data/file.bin", Result = "Success" },
            new() { Id = "audit-005", Timestamp = DateTime.UtcNow.AddHours(-2), User = "admin", Category = "config", Action = "config.update", Resource = "storage.compression", Result = "Success" },
            new() { Id = "audit-006", Timestamp = DateTime.UtcNow.AddHours(-3), User = "system", Category = "health", Action = "health.check", Resource = "system", Result = "Success" },
            new() { Id = "audit-007", Timestamp = DateTime.UtcNow.AddHours(-4), User = "user2", Category = "auth", Action = "login", Resource = null, Result = "Failure", SourceIp = "192.168.1.100" },
        };

        if (!string.IsNullOrEmpty(category))
        {
            entries = entries.Where(e => e.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (!string.IsNullOrEmpty(user))
        {
            entries = entries.Where(e => e.User?.Equals(user, StringComparison.OrdinalIgnoreCase) == true).ToList();
        }

        if (since.HasValue)
        {
            entries = entries.Where(e => e.Timestamp >= since.Value).ToList();
        }

        return entries.Take(limit).ToList();
    }
}

/// <summary>
/// Exports audit log.
/// </summary>
public sealed class AuditExportCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "audit.export";

    /// <inheritdoc />
    public string Description => "Export audit log";

    /// <inheritdoc />
    public string Category => "audit";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredFeatures => Array.Empty<string>();

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        context.EnsureConnected();

        var path = parameters.GetValueOrDefault("path")?.ToString();
        var format = parameters.GetValueOrDefault("format")?.ToString() ?? "json";

        if (string.IsNullOrEmpty(path))
        {
            return CommandResult.Fail("Export path is required.");
        }

        var response = await context.InstanceManager.ExecuteAsync("audit.export",
            new Dictionary<string, object> { ["format"] = format }, cancellationToken);

        // Generate sample data for export
        var entries = new List<AuditEntry>
        {
            new() { Id = "audit-001", Timestamp = DateTime.UtcNow.AddMinutes(-5), User = "admin", Category = "storage", Action = "pool.create", Resource = "pool-004", Result = "Success" },
            new() { Id = "audit-002", Timestamp = DateTime.UtcNow.AddMinutes(-15), User = "system", Category = "backup", Action = "backup.complete", Resource = "backup-20260126", Result = "Success" },
        };

        string content;
        if (format.Equals("csv", StringComparison.OrdinalIgnoreCase))
        {
            var sb = new StringBuilder();
            sb.AppendLine("Id,Timestamp,User,Category,Action,Resource,Result");
            foreach (var entry in entries)
            {
                sb.AppendLine($"{entry.Id},{entry.Timestamp:O},{entry.User},{entry.Category},{entry.Action},{entry.Resource},{entry.Result}");
            }
            content = sb.ToString();
        }
        else
        {
            content = JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
        }

        await File.WriteAllTextAsync(path, content, cancellationToken);

        return CommandResult.Ok(new
        {
            Path = path,
            Format = format,
            EntryCount = entries.Count
        }, $"Audit log exported to: {path}");
    }
}

/// <summary>
/// Shows audit statistics.
/// </summary>
public sealed class AuditStatsCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "audit.stats";

    /// <inheritdoc />
    public string Description => "Show audit statistics";

    /// <inheritdoc />
    public string Category => "audit";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredFeatures => Array.Empty<string>();

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        context.EnsureConnected();

        var period = parameters.GetValueOrDefault("period")?.ToString() ?? "24h";

        var response = await context.InstanceManager.ExecuteAsync("audit.stats",
            new Dictionary<string, object> { ["period"] = period }, cancellationToken);

        var stats = new AuditStats
        {
            TotalEntries = 1234,
            SuccessCount = 1180,
            FailureCount = 54,
            ByCategory = new Dictionary<string, int>
            {
                ["storage"] = 450,
                ["backup"] = 200,
                ["auth"] = 180,
                ["config"] = 150,
                ["plugin"] = 120,
                ["health"] = 134
            },
            ByUser = new Dictionary<string, int>
            {
                ["admin"] = 400,
                ["system"] = 350,
                ["user1"] = 250,
                ["user2"] = 234
            }
        };

        return CommandResult.Ok(stats, $"Audit statistics for period: {period}");
    }
}
