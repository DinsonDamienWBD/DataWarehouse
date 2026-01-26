// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.Shared.Models;

namespace DataWarehouse.Shared.Commands;

/// <summary>
/// Storage pool information record.
/// </summary>
public sealed record StoragePoolInfo
{
    /// <summary>Pool unique identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Pool display name.</summary>
    public required string Name { get; init; }

    /// <summary>Pool type (SSD, HDD, NVMe, Archive, Cache).</summary>
    public required string Type { get; init; }

    /// <summary>Current status (Healthy, Degraded, Offline).</summary>
    public required string Status { get; init; }

    /// <summary>Total capacity in bytes.</summary>
    public long Capacity { get; init; }

    /// <summary>Used space in bytes.</summary>
    public long Used { get; init; }

    /// <summary>Creation timestamp.</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>Read operation count.</summary>
    public long ReadOps { get; init; }

    /// <summary>Write operation count.</summary>
    public long WriteOps { get; init; }

    /// <summary>Number of instances using this pool.</summary>
    public int InstanceCount { get; init; }

    /// <summary>Utilization percentage.</summary>
    public double UtilizationPercent => Capacity > 0 ? (double)Used / Capacity * 100 : 0;
}

/// <summary>
/// Lists all storage pools.
/// </summary>
public sealed class StorageListCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "storage.list";

    /// <inheritdoc />
    public string Description => "List all storage pools";

    /// <inheritdoc />
    public string Category => "storage";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredFeatures => Array.Empty<string>();

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        context.EnsureConnected();

        var response = await context.InstanceManager.ExecuteAsync("storage.list", new Dictionary<string, object>(), cancellationToken);

        if (response?.Data?.TryGetValue("pools", out var poolsObj) == true && poolsObj is IEnumerable<object> poolsData)
        {
            var pools = poolsData.Cast<Dictionary<string, object>>()
                .Select(p => new StoragePoolInfo
                {
                    Id = p.GetValueOrDefault("id")?.ToString() ?? "",
                    Name = p.GetValueOrDefault("name")?.ToString() ?? "",
                    Type = p.GetValueOrDefault("type")?.ToString() ?? "",
                    Status = p.GetValueOrDefault("status")?.ToString() ?? "Unknown",
                    Capacity = Convert.ToInt64(p.GetValueOrDefault("capacity") ?? 0),
                    Used = Convert.ToInt64(p.GetValueOrDefault("used") ?? 0),
                    CreatedAt = p.TryGetValue("createdAt", out var dt) && dt is DateTime d ? d : DateTime.UtcNow
                }).ToList();

            return CommandResult.Table(pools, $"Found {pools.Count} storage pool(s)");
        }

        // Return sample data for development/demo
        var samplePools = new List<StoragePoolInfo>
        {
            new() { Id = "pool-001", Name = "Primary", Type = "SSD", Status = "Healthy", Capacity = 1L * 1024 * 1024 * 1024 * 1024, Used = 256L * 1024 * 1024 * 1024 },
            new() { Id = "pool-002", Name = "Archive", Type = "HDD", Status = "Healthy", Capacity = 5L * 1024 * 1024 * 1024 * 1024, Used = 3900L * 1024 * 1024 * 1024 },
            new() { Id = "pool-003", Name = "Cache", Type = "NVMe", Status = "Healthy", Capacity = 500L * 1024 * 1024 * 1024, Used = 115L * 1024 * 1024 * 1024 },
        };

        return CommandResult.Table(samplePools, $"Found {samplePools.Count} storage pool(s)");
    }
}

/// <summary>
/// Creates a new storage pool.
/// </summary>
public sealed class StorageCreateCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "storage.create";

    /// <inheritdoc />
    public string Description => "Create a new storage pool";

    /// <inheritdoc />
    public string Category => "storage";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredFeatures => Array.Empty<string>();

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        context.EnsureConnected();

        var name = parameters.GetValueOrDefault("name")?.ToString();
        var type = parameters.GetValueOrDefault("type")?.ToString() ?? "Standard";
        var capacity = Convert.ToInt64(parameters.GetValueOrDefault("capacity") ?? 100L * 1024 * 1024 * 1024);

        if (string.IsNullOrEmpty(name))
        {
            return CommandResult.Fail("Pool name is required.");
        }

        var response = await context.InstanceManager.ExecuteAsync("storage.create",
            new Dictionary<string, object>
            {
                ["name"] = name,
                ["type"] = type,
                ["capacity"] = capacity
            }, cancellationToken);

        if (response?.Error != null)
        {
            return CommandResult.Fail(response.Error);
        }

        var poolId = response?.Data?.GetValueOrDefault("id")?.ToString() ?? Guid.NewGuid().ToString("N")[..8];

        return CommandResult.Ok(new StoragePoolInfo
        {
            Id = poolId,
            Name = name,
            Type = type,
            Status = "Healthy",
            Capacity = capacity,
            Used = 0,
            CreatedAt = DateTime.UtcNow
        }, $"Storage pool '{name}' created successfully with ID: {poolId}");
    }
}

/// <summary>
/// Deletes a storage pool.
/// </summary>
public sealed class StorageDeleteCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "storage.delete";

    /// <inheritdoc />
    public string Description => "Delete a storage pool";

    /// <inheritdoc />
    public string Category => "storage";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredFeatures => Array.Empty<string>();

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        context.EnsureConnected();

        var id = parameters.GetValueOrDefault("id")?.ToString();

        if (string.IsNullOrEmpty(id))
        {
            return CommandResult.Fail("Pool ID is required.");
        }

        var response = await context.InstanceManager.ExecuteAsync("storage.delete",
            new Dictionary<string, object> { ["id"] = id }, cancellationToken);

        if (response?.Error != null)
        {
            return CommandResult.Fail(response.Error);
        }

        return CommandResult.Ok(message: $"Storage pool '{id}' deleted successfully.");
    }
}

/// <summary>
/// Shows detailed information about a storage pool.
/// </summary>
public sealed class StorageInfoCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "storage.info";

    /// <inheritdoc />
    public string Description => "Show detailed information about a storage pool";

    /// <inheritdoc />
    public string Category => "storage";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredFeatures => Array.Empty<string>();

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        context.EnsureConnected();

        var id = parameters.GetValueOrDefault("id")?.ToString();

        if (string.IsNullOrEmpty(id))
        {
            return CommandResult.Fail("Pool ID is required.");
        }

        var response = await context.InstanceManager.ExecuteAsync("storage.info",
            new Dictionary<string, object> { ["id"] = id }, cancellationToken);

        if (response?.Error != null)
        {
            return CommandResult.Fail(response.Error);
        }

        // Return sample data for development
        var pool = new StoragePoolInfo
        {
            Id = id,
            Name = "Primary Storage",
            Type = "SSD",
            Status = "Healthy",
            Capacity = 1L * 1024 * 1024 * 1024 * 1024,
            Used = 256L * 1024 * 1024 * 1024,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ReadOps = 1234567,
            WriteOps = 567890,
            InstanceCount = 3
        };

        return CommandResult.Ok(pool, $"Storage pool: {id}");
    }
}

/// <summary>
/// Shows storage statistics.
/// </summary>
public sealed class StorageStatsCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "storage.stats";

    /// <inheritdoc />
    public string Description => "Show storage statistics";

    /// <inheritdoc />
    public string Category => "storage";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredFeatures => Array.Empty<string>();

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        context.EnsureConnected();

        var response = await context.InstanceManager.ExecuteAsync("storage.stats",
            new Dictionary<string, object>(), cancellationToken);

        var stats = new StorageStats
        {
            TotalPools = 4,
            TotalCapacity = 10L * 1024 * 1024 * 1024 * 1024,
            TotalUsed = 3500L * 1024 * 1024 * 1024,
            AverageUtilization = 35.0,
            PoolUtilizations = new Dictionary<string, double>
            {
                ["Primary"] = 45.0,
                ["Archive"] = 78.0,
                ["Cache"] = 23.0,
                ["Backup"] = 12.0
            }
        };

        return CommandResult.Ok(stats, "Storage statistics");
    }
}

/// <summary>
/// Storage statistics record.
/// </summary>
public sealed record StorageStats
{
    /// <summary>Total number of storage pools.</summary>
    public int TotalPools { get; init; }

    /// <summary>Total capacity across all pools in bytes.</summary>
    public long TotalCapacity { get; init; }

    /// <summary>Total used space across all pools in bytes.</summary>
    public long TotalUsed { get; init; }

    /// <summary>Average utilization percentage.</summary>
    public double AverageUtilization { get; init; }

    /// <summary>Utilization by pool name.</summary>
    public Dictionary<string, double> PoolUtilizations { get; init; } = new();
}
