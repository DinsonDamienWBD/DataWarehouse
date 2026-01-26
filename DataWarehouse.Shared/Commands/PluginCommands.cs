// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

namespace DataWarehouse.Shared.Commands;

/// <summary>
/// Plugin information record.
/// </summary>
public sealed record PluginInfo
{
    /// <summary>Plugin unique identifier.</summary>
    public required string Id { get; init; }

    /// <summary>Plugin display name.</summary>
    public required string Name { get; init; }

    /// <summary>Plugin category (Storage, DataTransformation, Interface, etc.).</summary>
    public required string Category { get; init; }

    /// <summary>Plugin version.</summary>
    public required string Version { get; init; }

    /// <summary>Whether plugin is enabled.</summary>
    public bool IsEnabled { get; init; }

    /// <summary>Whether plugin is healthy.</summary>
    public bool IsHealthy { get; init; }

    /// <summary>Plugin description.</summary>
    public string? Description { get; init; }

    /// <summary>Plugin author.</summary>
    public string? Author { get; init; }

    /// <summary>When plugin was loaded.</summary>
    public DateTime? LoadedAt { get; init; }

    /// <summary>Plugin dependencies.</summary>
    public IReadOnlyList<string> Dependencies { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Lists all plugins.
/// </summary>
public sealed class PluginListCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "plugin.list";

    /// <inheritdoc />
    public string Description => "List all plugins";

    /// <inheritdoc />
    public string Category => "plugin";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredFeatures => Array.Empty<string>();

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        context.EnsureConnected();

        var categoryFilter = parameters.GetValueOrDefault("category")?.ToString();

        var response = await context.InstanceManager.ExecuteAsync("plugin.list",
            new Dictionary<string, object>(), cancellationToken);

        var plugins = GetSamplePlugins();

        if (!string.IsNullOrEmpty(categoryFilter))
        {
            plugins = plugins.Where(p => p.Category.Equals(categoryFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return CommandResult.Table(plugins, $"Found {plugins.Count} plugin(s)");
    }

    private static List<PluginInfo> GetSamplePlugins() => new()
    {
        new() { Id = "local-storage", Name = "LocalStoragePlugin", Category = "Storage", Version = "1.0.0", IsEnabled = true, IsHealthy = true, Description = "File system storage provider" },
        new() { Id = "s3-storage", Name = "S3StoragePlugin", Category = "Storage", Version = "1.0.0", IsEnabled = true, IsHealthy = true, Description = "Amazon S3 compatible storage" },
        new() { Id = "azure-blob", Name = "AzureBlobStoragePlugin", Category = "Storage", Version = "1.0.0", IsEnabled = true, IsHealthy = true, Description = "Azure Blob storage provider" },
        new() { Id = "gzip-compression", Name = "GZipCompressionPlugin", Category = "DataTransformation", Version = "1.0.0", IsEnabled = true, IsHealthy = true, Description = "GZip compression pipeline" },
        new() { Id = "aes-encryption", Name = "AesEncryptionPlugin", Category = "DataTransformation", Version = "1.0.0", IsEnabled = true, IsHealthy = true, Description = "AES-256-GCM encryption" },
        new() { Id = "raft-consensus", Name = "RaftConsensusPlugin", Category = "Orchestration", Version = "1.0.0", IsEnabled = true, IsHealthy = true, Description = "Raft distributed consensus" },
        new() { Id = "rest-interface", Name = "RestInterfacePlugin", Category = "Interface", Version = "1.0.0", IsEnabled = true, IsHealthy = true, Description = "RESTful API interface" },
        new() { Id = "grpc-interface", Name = "GrpcInterfacePlugin", Category = "Interface", Version = "1.0.0", IsEnabled = true, IsHealthy = true, Description = "gRPC interface" },
        new() { Id = "ai-agents", Name = "AIAgentPlugin", Category = "AI", Version = "1.0.0", IsEnabled = true, IsHealthy = true, Description = "AI provider integration" },
        new() { Id = "access-control", Name = "AdvancedAclPlugin", Category = "Security", Version = "1.0.0", IsEnabled = true, IsHealthy = true, Description = "RBAC and ACL management" },
        new() { Id = "opentelemetry", Name = "OpenTelemetryPlugin", Category = "Metrics", Version = "1.0.0", IsEnabled = true, IsHealthy = true, Description = "OpenTelemetry observability" },
        new() { Id = "governance", Name = "GovernancePlugin", Category = "Governance", Version = "1.0.0", IsEnabled = true, IsHealthy = true, Description = "Data governance and compliance" },
    };
}

/// <summary>
/// Shows plugin details.
/// </summary>
public sealed class PluginInfoCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "plugin.info";

    /// <inheritdoc />
    public string Description => "Show plugin details";

    /// <inheritdoc />
    public string Category => "plugin";

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
            return CommandResult.Fail("Plugin ID is required.");
        }

        var response = await context.InstanceManager.ExecuteAsync("plugin.info",
            new Dictionary<string, object> { ["id"] = id }, cancellationToken);

        var plugin = new PluginInfo
        {
            Id = id,
            Name = $"{id}Plugin",
            Category = "Storage",
            Version = "1.0.0",
            IsEnabled = true,
            IsHealthy = true,
            Description = "Sample plugin description",
            Author = "DataWarehouse Team",
            LoadedAt = new DateTime(2026, 1, 19, 8, 0, 0, DateTimeKind.Utc),
            Dependencies = Array.Empty<string>()
        };

        return CommandResult.Ok(plugin, $"Plugin: {plugin.Name}");
    }
}

/// <summary>
/// Enables a plugin.
/// </summary>
public sealed class PluginEnableCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "plugin.enable";

    /// <inheritdoc />
    public string Description => "Enable a plugin";

    /// <inheritdoc />
    public string Category => "plugin";

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
            return CommandResult.Fail("Plugin ID is required.");
        }

        var response = await context.InstanceManager.ExecuteAsync("plugin.enable",
            new Dictionary<string, object> { ["id"] = id }, cancellationToken);

        if (response?.Error != null)
        {
            return CommandResult.Fail(response.Error);
        }

        return CommandResult.Ok(message: $"Plugin '{id}' enabled successfully.");
    }
}

/// <summary>
/// Disables a plugin.
/// </summary>
public sealed class PluginDisableCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "plugin.disable";

    /// <inheritdoc />
    public string Description => "Disable a plugin";

    /// <inheritdoc />
    public string Category => "plugin";

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
            return CommandResult.Fail("Plugin ID is required.");
        }

        var response = await context.InstanceManager.ExecuteAsync("plugin.disable",
            new Dictionary<string, object> { ["id"] = id }, cancellationToken);

        if (response?.Error != null)
        {
            return CommandResult.Fail(response.Error);
        }

        return CommandResult.Ok(message: $"Plugin '{id}' disabled successfully.");
    }
}

/// <summary>
/// Reloads a plugin.
/// </summary>
public sealed class PluginReloadCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "plugin.reload";

    /// <inheritdoc />
    public string Description => "Reload a plugin";

    /// <inheritdoc />
    public string Category => "plugin";

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
            return CommandResult.Fail("Plugin ID is required.");
        }

        var response = await context.InstanceManager.ExecuteAsync("plugin.reload",
            new Dictionary<string, object> { ["id"] = id }, cancellationToken);

        if (response?.Error != null)
        {
            return CommandResult.Fail(response.Error);
        }

        return CommandResult.Ok(message: $"Plugin '{id}' reloaded successfully.");
    }
}
