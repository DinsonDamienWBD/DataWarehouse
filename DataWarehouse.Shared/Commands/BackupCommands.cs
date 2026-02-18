// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace DataWarehouse.Shared.Commands;

/// <summary>
/// Backup information record.
/// </summary>
public sealed record BackupInfo
{
    /// <summary>Backup unique identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Backup display name.</summary>
    public required string Name { get; init; }

    /// <summary>Backup type (Full, Incremental, Differential).</summary>
    public required string Type { get; init; }

    /// <summary>Backup size in bytes.</summary>
    public long Size { get; init; }

    /// <summary>Creation timestamp.</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>Backup status (Completed, InProgress, Failed, Verified).</summary>
    public required string Status { get; init; }

    /// <summary>Backup destination path.</summary>
    public string? Destination { get; init; }

    /// <summary>Whether backup is compressed.</summary>
    public bool IsCompressed { get; init; }

    /// <summary>Whether backup is encrypted.</summary>
    public bool IsEncrypted { get; init; }

    /// <summary>Verification checksum.</summary>
    public string? Checksum { get; init; }

    /// <summary>Number of files in backup.</summary>
    public int FileCount { get; init; }

    /// <summary>Backup duration in seconds.</summary>
    public double DurationSeconds { get; init; }
}

/// <summary>
/// Creates a backup.
/// </summary>
public sealed class BackupCreateCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "backup.create";

    /// <inheritdoc />
    public string Description => "Create a backup";

    /// <inheritdoc />
    public string Category => "backup";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredFeatures => new[] { "backup" };

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        context.EnsureConnected();

        var name = parameters.GetValueOrDefault("name")?.ToString();
        var destination = parameters.GetValueOrDefault("destination")?.ToString();
        var incremental = parameters.GetValueOrDefault("incremental") as bool? ?? false;
        var compress = parameters.GetValueOrDefault("compress") as bool? ?? true;
        var encrypt = parameters.GetValueOrDefault("encrypt") as bool? ?? false;

        if (string.IsNullOrEmpty(name))
        {
            return CommandResult.Fail("Backup name is required.");
        }

        var backupId = $"backup-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
        destination ??= $"./backups/{backupId}";

        var response = await context.InstanceManager.ExecuteAsync("backup.create",
            new Dictionary<string, object>
            {
                ["name"] = name,
                ["destination"] = destination,
                ["incremental"] = incremental,
                ["compress"] = compress,
                ["encrypt"] = encrypt
            }, cancellationToken);

        if (response?.Error != null)
        {
            return CommandResult.Fail(response.Error);
        }

        // TODO: Parse actual response data when DataProtection plugin implements backup.create
        var backup = new BackupInfo
        {
            Id = backupId,
            Name = name,
            Type = incremental ? "Incremental" : "Full",
            Status = "Completed",
            Size = 0, // Will be populated by actual backup operation
            CreatedAt = DateTime.UtcNow,
            Destination = destination,
            IsCompressed = compress,
            IsEncrypted = encrypt,
            FileCount = 0,
            DurationSeconds = 0.0
        };

        return CommandResult.Ok(backup, $"Backup '{name}' initiated with ID: {backupId}");
    }
}

/// <summary>
/// Lists all backups.
/// </summary>
public sealed class BackupListCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "backup.list";

    /// <inheritdoc />
    public string Description => "List all backups";

    /// <inheritdoc />
    public string Category => "backup";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredFeatures => new[] { "backup" };

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        context.EnsureConnected();

        var response = await context.InstanceManager.ExecuteAsync("backup.list",
            new Dictionary<string, object>(), cancellationToken);

        // TODO: Parse actual backup list when DataProtection plugin implements backup.list
        var backups = new List<BackupInfo>();

        return CommandResult.Table(backups, "Backup listing not yet implemented - DataProtection plugin integration pending");
    }
}

/// <summary>
/// Restores from a backup.
/// </summary>
public sealed class BackupRestoreCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "backup.restore";

    /// <inheritdoc />
    public string Description => "Restore from a backup";

    /// <inheritdoc />
    public string Category => "backup";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredFeatures => new[] { "backup" };

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        context.EnsureConnected();

        var id = parameters.GetValueOrDefault("id")?.ToString();
        var target = parameters.GetValueOrDefault("target")?.ToString();
        var verify = parameters.GetValueOrDefault("verify") as bool? ?? true;

        if (string.IsNullOrEmpty(id))
        {
            return CommandResult.Fail("Backup ID is required.");
        }

        var response = await context.InstanceManager.ExecuteAsync("backup.restore",
            new Dictionary<string, object>
            {
                ["id"] = id,
                ["target"] = target ?? "",
                ["verify"] = verify
            }, cancellationToken);

        if (response?.Error != null)
        {
            return CommandResult.Fail(response.Error);
        }

        // TODO: Parse actual restore results when DataProtection plugin implements backup.restore
        return CommandResult.Ok(new
        {
            BackupId = id,
            Target = target ?? "default",
            Verified = verify,
            FilesRestored = 0,
            DurationSeconds = 0.0
        }, $"Backup '{id}' restore initiated.");
    }
}

/// <summary>
/// Verifies a backup.
/// </summary>
public sealed class BackupVerifyCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "backup.verify";

    /// <inheritdoc />
    public string Description => "Verify backup integrity";

    /// <inheritdoc />
    public string Category => "backup";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredFeatures => new[] { "backup" };

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
            return CommandResult.Fail("Backup ID is required.");
        }

        var response = await context.InstanceManager.ExecuteAsync("backup.verify",
            new Dictionary<string, object> { ["id"] = id }, cancellationToken);

        if (response?.Error != null)
        {
            return CommandResult.Fail(response.Error);
        }

        // TODO: Parse actual verification results when DataProtection plugin implements backup.verify
        return CommandResult.Ok(new
        {
            BackupId = id,
            Status = "Unknown",
            FilesChecked = 0,
            Checksum = "",
            Integrity = 0.0
        }, $"Backup '{id}' verification initiated.");
    }
}

/// <summary>
/// Deletes a backup.
/// </summary>
public sealed class BackupDeleteCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "backup.delete";

    /// <inheritdoc />
    public string Description => "Delete a backup";

    /// <inheritdoc />
    public string Category => "backup";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredFeatures => new[] { "backup" };

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
            return CommandResult.Fail("Backup ID is required.");
        }

        var response = await context.InstanceManager.ExecuteAsync("backup.delete",
            new Dictionary<string, object> { ["id"] = id }, cancellationToken);

        if (response?.Error != null)
        {
            return CommandResult.Fail(response.Error);
        }

        return CommandResult.Ok(message: $"Backup '{id}' deleted successfully.");
    }
}
