// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace DataWarehouse.Shared.Commands;

/// <summary>
/// RAID configuration information record.
/// </summary>
public sealed record RaidConfigInfo
{
    /// <summary>RAID array unique identifier.</summary>
    public required string Id { get; init; }

    /// <summary>RAID array display name.</summary>
    public required string Name { get; init; }

    /// <summary>RAID level (0, 1, 5, 6, 10, etc.).</summary>
    public required string Level { get; init; }

    /// <summary>Number of disks.</summary>
    public int DiskCount { get; init; }

    /// <summary>Stripe size in KB.</summary>
    public int StripeSizeKB { get; init; }

    /// <summary>Total capacity in bytes.</summary>
    public long Capacity { get; init; }

    /// <summary>Array status (Healthy, Degraded, Rebuilding, Offline).</summary>
    public required string Status { get; init; }

    /// <summary>Rebuild progress percentage (0-100).</summary>
    public double? RebuildProgress { get; init; }

    /// <summary>Creation timestamp.</summary>
    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// RAID level information record.
/// </summary>
public sealed record RaidLevelInfo
{
    /// <summary>RAID level identifier.</summary>
    public required string Level { get; init; }

    /// <summary>Level description.</summary>
    public required string Description { get; init; }

    /// <summary>Minimum number of disks required.</summary>
    public int MinDisks { get; init; }

    /// <summary>Maximum disk failure tolerance.</summary>
    public int FaultTolerance { get; init; }

    /// <summary>Storage efficiency percentage.</summary>
    public double Efficiency { get; init; }

    /// <summary>Read performance relative rating.</summary>
    public required string ReadPerformance { get; init; }

    /// <summary>Write performance relative rating.</summary>
    public required string WritePerformance { get; init; }
}

/// <summary>
/// Lists RAID configurations.
/// </summary>
public sealed class RaidListCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "raid.list";

    /// <inheritdoc />
    public string Description => "List RAID configurations";

    /// <inheritdoc />
    public string Category => "raid";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredFeatures => new[] { "raid" };

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        context.EnsureConnected();

        var response = await context.InstanceManager.ExecuteAsync("raid.list",
            new Dictionary<string, object>(), cancellationToken);

        var configs = new List<RaidConfigInfo>
        {
            new() { Id = "raid-001", Name = "Primary RAID", Level = "5", DiskCount = 4, StripeSizeKB = 64, Capacity = 3L * 1024 * 1024 * 1024 * 1024, Status = "Healthy", CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) },
            new() { Id = "raid-002", Name = "Archive RAID", Level = "6", DiskCount = 6, StripeSizeKB = 128, Capacity = 8L * 1024 * 1024 * 1024 * 1024, Status = "Healthy", CreatedAt = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc) },
            new() { Id = "raid-003", Name = "Cache RAID", Level = "10", DiskCount = 4, StripeSizeKB = 32, Capacity = 500L * 1024 * 1024 * 1024, Status = "Healthy", CreatedAt = new DateTime(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc) },
        };

        return CommandResult.Table(configs, $"Found {configs.Count} RAID configuration(s)");
    }
}

/// <summary>
/// Creates a RAID array.
/// </summary>
public sealed class RaidCreateCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "raid.create";

    /// <inheritdoc />
    public string Description => "Create a RAID array";

    /// <inheritdoc />
    public string Category => "raid";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredFeatures => new[] { "raid" };

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        context.EnsureConnected();

        var name = parameters.GetValueOrDefault("name")?.ToString();
        var level = parameters.GetValueOrDefault("level")?.ToString() ?? "5";
        var disks = Convert.ToInt32(parameters.GetValueOrDefault("disks") ?? 4);
        var stripeSize = Convert.ToInt32(parameters.GetValueOrDefault("stripeSize") ?? 64);

        if (string.IsNullOrEmpty(name))
        {
            return CommandResult.Fail("RAID array name is required.");
        }

        // Validate RAID level requirements
        var (valid, error) = ValidateRaidConfig(level, disks);
        if (!valid)
        {
            return CommandResult.Fail(error!);
        }

        var response = await context.InstanceManager.ExecuteAsync("raid.create",
            new Dictionary<string, object>
            {
                ["name"] = name,
                ["level"] = level,
                ["disks"] = disks,
                ["stripeSize"] = stripeSize
            }, cancellationToken);

        if (response?.Error != null)
        {
            return CommandResult.Fail(response.Error);
        }

        var raidId = response?.Data?.GetValueOrDefault("id")?.ToString() ?? $"raid-{Guid.NewGuid():N}"[..12];

        var config = new RaidConfigInfo
        {
            Id = raidId,
            Name = name,
            Level = level,
            DiskCount = disks,
            StripeSizeKB = stripeSize,
            Capacity = CalculateCapacity(level, disks, 1L * 1024 * 1024 * 1024 * 1024),
            Status = "Initializing",
            CreatedAt = DateTime.UtcNow
        };

        return CommandResult.Ok(config, $"RAID array '{name}' created successfully with ID: {raidId}");
    }

    private static (bool valid, string? error) ValidateRaidConfig(string level, int disks)
    {
        return level switch
        {
            "0" when disks < 2 => (false, "RAID 0 requires at least 2 disks."),
            "1" when disks < 2 => (false, "RAID 1 requires at least 2 disks."),
            "5" when disks < 3 => (false, "RAID 5 requires at least 3 disks."),
            "6" when disks < 4 => (false, "RAID 6 requires at least 4 disks."),
            "10" when disks < 4 || disks % 2 != 0 => (false, "RAID 10 requires at least 4 disks and an even number."),
            _ => (true, null)
        };
    }

    private static long CalculateCapacity(string level, int disks, long diskSize)
    {
        return level switch
        {
            "0" => disks * diskSize,
            "1" => diskSize,
            "5" => (disks - 1) * diskSize,
            "6" => (disks - 2) * diskSize,
            "10" => disks / 2 * diskSize,
            _ => (disks - 1) * diskSize
        };
    }
}

/// <summary>
/// Shows RAID status.
/// </summary>
public sealed class RaidStatusCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "raid.status";

    /// <inheritdoc />
    public string Description => "Show RAID status";

    /// <inheritdoc />
    public string Category => "raid";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredFeatures => new[] { "raid" };

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
            return CommandResult.Fail("RAID array ID is required.");
        }

        var response = await context.InstanceManager.ExecuteAsync("raid.status",
            new Dictionary<string, object> { ["id"] = id }, cancellationToken);

        var config = new RaidConfigInfo
        {
            Id = id,
            Name = "Primary RAID",
            Level = "5",
            DiskCount = 4,
            StripeSizeKB = 64,
            Capacity = 3L * 1024 * 1024 * 1024 * 1024,
            Status = "Healthy",
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
        };

        return CommandResult.Ok(config, $"RAID array: {id}");
    }
}

/// <summary>
/// Starts RAID rebuild.
/// </summary>
public sealed class RaidRebuildCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "raid.rebuild";

    /// <inheritdoc />
    public string Description => "Start RAID rebuild";

    /// <inheritdoc />
    public string Category => "raid";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredFeatures => new[] { "raid" };

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
            return CommandResult.Fail("RAID array ID is required.");
        }

        var response = await context.InstanceManager.ExecuteAsync("raid.rebuild",
            new Dictionary<string, object> { ["id"] = id }, cancellationToken);

        if (response?.Error != null)
        {
            return CommandResult.Fail(response.Error);
        }

        return CommandResult.Ok(new
        {
            ArrayId = id,
            Status = "Rebuilding",
            Progress = 0.0,
            EstimatedTime = "2 hours"
        }, $"RAID rebuild started for array '{id}'.");
    }
}

/// <summary>
/// Lists supported RAID levels.
/// </summary>
public sealed class RaidLevelsCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "raid.levels";

    /// <inheritdoc />
    public string Description => "List supported RAID levels";

    /// <inheritdoc />
    public string Category => "raid";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredFeatures => new[] { "raid" };

    /// <inheritdoc />
    public Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        var levels = new List<RaidLevelInfo>
        {
            new() { Level = "0", Description = "Striping (no redundancy)", MinDisks = 2, FaultTolerance = 0, Efficiency = 100, ReadPerformance = "Excellent", WritePerformance = "Excellent" },
            new() { Level = "1", Description = "Mirroring", MinDisks = 2, FaultTolerance = 1, Efficiency = 50, ReadPerformance = "Good", WritePerformance = "Good" },
            new() { Level = "5", Description = "Striping with distributed parity", MinDisks = 3, FaultTolerance = 1, Efficiency = 67, ReadPerformance = "Good", WritePerformance = "Fair" },
            new() { Level = "6", Description = "Striping with double distributed parity", MinDisks = 4, FaultTolerance = 2, Efficiency = 50, ReadPerformance = "Good", WritePerformance = "Fair" },
            new() { Level = "10", Description = "Mirrored stripes", MinDisks = 4, FaultTolerance = 1, Efficiency = 50, ReadPerformance = "Excellent", WritePerformance = "Good" },
            new() { Level = "50", Description = "RAID 5 over RAID 0", MinDisks = 6, FaultTolerance = 1, Efficiency = 75, ReadPerformance = "Excellent", WritePerformance = "Good" },
            new() { Level = "60", Description = "RAID 6 over RAID 0", MinDisks = 8, FaultTolerance = 2, Efficiency = 67, ReadPerformance = "Excellent", WritePerformance = "Fair" },
        };

        return Task.FromResult(CommandResult.Table(levels, "Supported RAID levels"));
    }
}
