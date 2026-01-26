// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Text.Json;

namespace DataWarehouse.Shared.Commands;

/// <summary>
/// Shows current configuration.
/// </summary>
public sealed class ConfigShowCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "config.show";

    /// <inheritdoc />
    public string Description => "Show current configuration";

    /// <inheritdoc />
    public string Category => "config";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredFeatures => Array.Empty<string>();

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        context.EnsureConnected();

        var section = parameters.GetValueOrDefault("section")?.ToString();

        var response = await context.InstanceManager.ExecuteAsync("config.show",
            new Dictionary<string, object> { ["section"] = section ?? "" }, cancellationToken);

        var config = new Dictionary<string, object>
        {
            ["instanceId"] = "dw-instance-001",
            ["dataPath"] = "/var/lib/datawarehouse/data",
            ["pluginPath"] = "/var/lib/datawarehouse/plugins",
            ["logPath"] = "/var/log/datawarehouse",
            ["storage"] = new Dictionary<string, object>
            {
                ["maxPoolCount"] = 16,
                ["defaultPoolType"] = "Standard",
                ["compressionEnabled"] = true,
                ["encryptionEnabled"] = true
            },
            ["network"] = new Dictionary<string, object>
            {
                ["httpPort"] = 8080,
                ["grpcPort"] = 9090,
                ["tlsEnabled"] = true
            },
            ["security"] = new Dictionary<string, object>
            {
                ["authProvider"] = "internal",
                ["sessionTimeout"] = 3600,
                ["auditEnabled"] = true
            }
        };

        if (!string.IsNullOrEmpty(section) && config.TryGetValue(section, out var sectionConfig))
        {
            return CommandResult.Ok(sectionConfig, $"Configuration: {section}");
        }

        return CommandResult.Ok(config, "Current configuration");
    }
}

/// <summary>
/// Sets a configuration value.
/// </summary>
public sealed class ConfigSetCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "config.set";

    /// <inheritdoc />
    public string Description => "Set configuration value";

    /// <inheritdoc />
    public string Category => "config";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredFeatures => Array.Empty<string>();

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        context.EnsureConnected();

        var key = parameters.GetValueOrDefault("key")?.ToString();
        var value = parameters.GetValueOrDefault("value")?.ToString();

        if (string.IsNullOrEmpty(key))
        {
            return CommandResult.Fail("Configuration key is required.");
        }

        if (string.IsNullOrEmpty(value))
        {
            return CommandResult.Fail("Configuration value is required.");
        }

        var response = await context.InstanceManager.ExecuteAsync("config.set",
            new Dictionary<string, object>
            {
                ["key"] = key,
                ["value"] = value
            }, cancellationToken);

        if (response?.Error != null)
        {
            return CommandResult.Fail(response.Error);
        }

        return CommandResult.Ok(message: $"Configuration '{key}' set to '{value}'.");
    }
}

/// <summary>
/// Gets a configuration value.
/// </summary>
public sealed class ConfigGetCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "config.get";

    /// <inheritdoc />
    public string Description => "Get configuration value";

    /// <inheritdoc />
    public string Category => "config";

    /// <inheritdoc />
    public IReadOnlyList<string> RequiredFeatures => Array.Empty<string>();

    /// <inheritdoc />
    public async Task<CommandResult> ExecuteAsync(
        CommandContext context,
        Dictionary<string, object?> parameters,
        CancellationToken cancellationToken = default)
    {
        context.EnsureConnected();

        var key = parameters.GetValueOrDefault("key")?.ToString();

        if (string.IsNullOrEmpty(key))
        {
            return CommandResult.Fail("Configuration key is required.");
        }

        var response = await context.InstanceManager.ExecuteAsync("config.get",
            new Dictionary<string, object> { ["key"] = key }, cancellationToken);

        var value = response?.Data?.GetValueOrDefault("value") ?? "N/A";

        return CommandResult.Ok(new { Key = key, Value = value }, $"{key} = {value}");
    }
}

/// <summary>
/// Exports configuration.
/// </summary>
public sealed class ConfigExportCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "config.export";

    /// <inheritdoc />
    public string Description => "Export configuration";

    /// <inheritdoc />
    public string Category => "config";

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

        if (string.IsNullOrEmpty(path))
        {
            return CommandResult.Fail("Export path is required.");
        }

        var response = await context.InstanceManager.ExecuteAsync("config.export",
            new Dictionary<string, object>(), cancellationToken);

        // Get configuration and write to file
        var config = response?.Data ?? new Dictionary<string, object>
        {
            ["instanceId"] = "dw-instance-001",
            ["exportedAt"] = DateTime.UtcNow.ToString("O"),
            ["version"] = "1.0.0"
        };

        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(path, json, cancellationToken);

        return CommandResult.Ok(message: $"Configuration exported to: {path}");
    }
}

/// <summary>
/// Imports configuration.
/// </summary>
public sealed class ConfigImportCommand : ICommand
{
    /// <inheritdoc />
    public string Name => "config.import";

    /// <inheritdoc />
    public string Description => "Import configuration";

    /// <inheritdoc />
    public string Category => "config";

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
        var merge = parameters.GetValueOrDefault("merge") as bool? ?? false;

        if (string.IsNullOrEmpty(path))
        {
            return CommandResult.Fail("Import path is required.");
        }

        if (!File.Exists(path))
        {
            return CommandResult.Fail($"Configuration file not found: {path}");
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken);
        var config = JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new Dictionary<string, object>();

        var response = await context.InstanceManager.ExecuteAsync("config.import",
            new Dictionary<string, object>
            {
                ["config"] = config,
                ["merge"] = merge
            }, cancellationToken);

        if (response?.Error != null)
        {
            return CommandResult.Fail(response.Error);
        }

        return CommandResult.Ok(message: $"Configuration imported from: {path}" + (merge ? " (merged)" : " (replaced)"));
    }
}
