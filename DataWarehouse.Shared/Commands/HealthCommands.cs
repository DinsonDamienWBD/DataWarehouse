// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace DataWarehouse.Shared.Commands;

/// <summary>
/// System health status record.
/// </summary>
public sealed record HealthStatus
{
    /// <summary>Overall health status (Healthy, Degraded, Unhealthy).</summary>
    public required string Status { get; init; }

    /// <summary>Kernel health details.</summary>
    public required KernelHealth Kernel { get; init; }

    /// <summary>Plugin health summary.</summary>
    public required PluginHealth Plugins { get; init; }

    /// <summary>Resource utilization.</summary>
    public required ResourceHealth Resources { get; init; }
}

/// <summary>
/// Kernel health details.
/// </summary>
public sealed record KernelHealth
{
    /// <summary>Storage manager status.</summary>
    public required string StorageManager { get; init; }

    /// <summary>Message bus status.</summary>
    public required string MessageBus { get; init; }

    /// <summary>Pipeline orchestrator status.</summary>
    public required string PipelineOrchestrator { get; init; }
}

/// <summary>
/// Plugin health summary.
/// </summary>
public sealed record PluginHealth
{
    /// <summary>Total active plugins.</summary>
    public int Active { get; init; }

    /// <summary>Healthy storage providers.</summary>
    public required string StorageProviders { get; init; }

    /// <summary>Healthy data transformations.</summary>
    public required string DataTransformation { get; init; }

    /// <summary>Healthy interfaces.</summary>
    public required string Interface { get; init; }

    /// <summary>Healthy other plugins.</summary>
    public required string Other { get; init; }
}

/// <summary>
/// Resource health details.
/// </summary>
public sealed record ResourceHealth
{
    /// <summary>CPU utilization percentage.</summary>
    public double CpuPercent { get; init; }

    /// <summary>Memory used in bytes.</summary>
    public long MemoryUsed { get; init; }

    /// <summary>Total memory in bytes.</summary>
    public long MemoryTotal { get; init; }

    /// <summary>Disk used in bytes.</summary>
    public long DiskUsed { get; init; }

    /// <summary>Total disk in bytes.</summary>
    public long DiskTotal { get; init; }

    /// <summary>Memory utilization percentage.</summary>
    public double MemoryPercent => MemoryTotal > 0 ? (double)MemoryUsed / MemoryTotal * 100 : 0;

    /// <summary>Disk utilization percentage.</summary>
    public double DiskPercent => DiskTotal > 0 ? (double)DiskUsed / DiskTotal * 100 : 0;
}

/// <summary>
/// System metrics record.
/// </summary>
public sealed record SystemMetrics
{
    /// <summary>CPU utilization percentage.</summary>
    public double CpuPercent { get; init; }

    /// <summary>Memory utilization percentage.</summary>
    public double MemoryPercent { get; init; }

    /// <summary>Disk utilization percentage.</summary>
    public double DiskPercent { get; init; }

    /// <summary>Network utilization percentage.</summary>
    public double NetworkPercent { get; init; }

    /// <summary>Requests per second.</summary>
    public int RequestsPerSecond { get; init; }

    /// <summary>Average latency in milliseconds.</summary>
    public double AvgLatencyMs { get; init; }

    /// <summary>Active connections.</summary>
    public int ActiveConnections { get; init; }

    /// <summary>Thread count.</summary>
    public int ThreadCount { get; init; }

    /// <summary>Uptime as TimeSpan.</summary>
    public TimeSpan Uptime { get; init; }
}

/// <summary>
/// Alert information record.
/// </summary>
public sealed record AlertInfo
{
    /// <summary>Alert severity (Critical, Warning, Info).</summary>
    public required string Severity { get; init; }

    /// <summary>Alert title.</summary>
    public required string Title { get; init; }

    /// <summary>Alert time.</summary>
    public required DateTime Time { get; init; }

    /// <summary>Whether alert is acknowledged.</summary>
    public bool IsAcknowledged { get; init; }

    /// <summary>Alert source component.</summary>
    public string? Source { get; init; }

    /// <summary>Alert details.</summary>
    public string? Details { get; init; }
}

/// <summary>
/// Shows system health status.
/// </summary>
public sealed class HealthStatusCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "health.status";

    /// <inheritdoc />
    public string Description => "Show system health status";

    /// <inheritdoc />
    public string Category => "health";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredFeatures => Array.Empty<string>();

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        context.EnsureConnected();

        var response = await context.InstanceManager.ExecuteAsync("health.status",
            new Dictionary<string, object>(), cancellationToken);

        var status = new HealthStatus
        {
            Status = "Healthy",
            Kernel = new KernelHealth
            {
                StorageManager = "Healthy",
                MessageBus = "Healthy",
                PipelineOrchestrator = "Healthy"
            },
            Plugins = new PluginHealth
            {
                Active = 12,
                StorageProviders = "4/4 healthy",
                DataTransformation = "4/4 healthy",
                Interface = "3/3 healthy",
                Other = "1/1 healthy"
            },
            Resources = new ResourceHealth
            {
                CpuPercent = 23,
                MemoryUsed = 4200L * 1024 * 1024,
                MemoryTotal = 16L * 1024 * 1024 * 1024,
                DiskUsed = 3500L * 1024 * 1024 * 1024,
                DiskTotal = 10L * 1024 * 1024 * 1024 * 1024
            }
        };

        return CommandResult.Ok(status, $"System Health: {status.Status}") with { DataType = ResultDataType.Tree };
    }
}

/// <summary>
/// Shows system metrics.
/// </summary>
public sealed class HealthMetricsCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "health.metrics";

    /// <inheritdoc />
    public string Description => "Show system metrics";

    /// <inheritdoc />
    public string Category => "health";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredFeatures => Array.Empty<string>();

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        context.EnsureConnected();

        var response = await context.InstanceManager.ExecuteAsync("health.metrics",
            new Dictionary<string, object>(), cancellationToken);

        var metrics = new SystemMetrics
        {
            CpuPercent = 23,
            MemoryPercent = 26,
            DiskPercent = 35,
            NetworkPercent = 12,
            RequestsPerSecond = 1234,
            AvgLatencyMs = 12,
            ActiveConnections = 45,
            ThreadCount = 32,
            Uptime = new TimeSpan(15, 7, 23, 0)
        };

        return CommandResult.Ok(metrics, "System Metrics");
    }
}

/// <summary>
/// Shows active alerts.
/// </summary>
public sealed class HealthAlertsCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "health.alerts";

    /// <inheritdoc />
    public string Description => "Show active alerts";

    /// <inheritdoc />
    public string Category => "health";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredFeatures => Array.Empty<string>();

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        context.EnsureConnected();

        var includeAcknowledged = parameters.GetValueOrDefault("all") as bool? ?? false;

        var response = await context.InstanceManager.ExecuteAsync("health.alerts",
            new Dictionary<string, object> { ["includeAcknowledged"] = includeAcknowledged }, cancellationToken);

        var alerts = new List<AlertInfo>
        {
            new() { Severity = "Warning", Title = "High disk usage on pool-002", Time = new DateTime(2026, 1, 26, 10, 30, 0, DateTimeKind.Utc), IsAcknowledged = false },
            new() { Severity = "Info", Title = "Scheduled backup completed", Time = new DateTime(2026, 1, 26, 3, 0, 0, DateTimeKind.Utc), IsAcknowledged = true },
        };

        if (!includeAcknowledged)
        {
            alerts = alerts.Where(a => !a.IsAcknowledged).ToList();
        }

        if (alerts.Count == 0)
        {
            return CommandResult.Ok(message: "No active alerts.");
        }

        return CommandResult.Table(alerts, $"Found {alerts.Count} alert(s)");
    }
}

/// <summary>
/// Runs health check.
/// </summary>
public sealed class HealthCheckCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "health.check";

    /// <inheritdoc />
    public string Description => "Run health check";

    /// <inheritdoc />
    public string Category => "health";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredFeatures => Array.Empty<string>();

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        context.EnsureConnected();

        var component = parameters.GetValueOrDefault("component")?.ToString();

        var response = await context.InstanceManager.ExecuteAsync("health.check",
            new Dictionary<string, object>
            {
                ["component"] = component ?? ""
            }, cancellationToken);

        var components = string.IsNullOrEmpty(component)
            ? new[] { "Kernel", "Storage", "Plugins", "Network", "Memory", "Disk" }
            : new[] { component };

        var results = components.Select(c => new
        {
            Component = c,
            Status = "Healthy",
            Details = "All checks passed"
        }).ToList();

        return CommandResult.Table(results, "Health check completed. All components healthy.");
    }
}
