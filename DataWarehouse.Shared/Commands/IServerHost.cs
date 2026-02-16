// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.SDK.Hosting;

namespace DataWarehouse.Shared.Commands;

/// <summary>
/// Abstraction for server host operations used by CLI commands.
/// Implemented by DataWarehouseHost in the Launcher project.
/// This interface avoids circular dependencies: Shared -> SDK (IServerHost defined here),
/// Launcher -> Shared (DataWarehouseHost implements IServerHost).
/// </summary>
public interface IServerHost
{
    /// <summary>
    /// Gets whether the server is currently running.
    /// </summary>
    bool IsRunning { get; }

    /// <summary>
    /// Gets the current server status information.
    /// </summary>
    ServerHostStatus? Status { get; }

    /// <summary>
    /// Starts the server with the given embedded configuration.
    /// </summary>
    /// <param name="config">Embedded configuration for server startup.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task that completes when the server has started.</returns>
    Task StartAsync(EmbeddedConfiguration config, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the running server gracefully.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task that completes when the server has stopped.</returns>
    Task StopAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Installs DataWarehouse to the specified path.
    /// </summary>
    /// <param name="config">Installation configuration.</param>
    /// <param name="progress">Optional progress reporter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Installation result.</returns>
    Task<ServerInstallResult> InstallAsync(
        InstallConfiguration config,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Status information for a running server host.
/// </summary>
public sealed record ServerHostStatus
{
    /// <summary>The port the server is listening on.</summary>
    public int Port { get; init; }

    /// <summary>The operating mode (Embedded, Workstation, Server, etc.).</summary>
    public required string Mode { get; init; }

    /// <summary>The process ID.</summary>
    public int ProcessId { get; init; }

    /// <summary>When the server was started.</summary>
    public DateTime StartTime { get; init; }

    /// <summary>The instance ID.</summary>
    public string? InstanceId { get; init; }

    /// <summary>The data path being used.</summary>
    public string? DataPath { get; init; }

    /// <summary>Number of loaded plugins.</summary>
    public int LoadedPlugins { get; init; }

    /// <summary>Whether data is being persisted.</summary>
    public bool PersistData { get; init; }
}

/// <summary>
/// Result of a server installation operation.
/// </summary>
public sealed record ServerInstallResult
{
    /// <summary>Whether the installation succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>The installation path.</summary>
    public string? InstallPath { get; init; }

    /// <summary>Result message.</summary>
    public string? Message { get; init; }

    /// <summary>Admin credentials if created.</summary>
    public string? AdminUsername { get; init; }

    /// <summary>Generated admin password if auto-generated.</summary>
    public string? GeneratedPassword { get; init; }
}

/// <summary>
/// Static registry for the current IServerHost instance.
/// Set at application startup by the Launcher/host application.
/// </summary>
public static class ServerHostRegistry
{
    private static IServerHost? _current;

    /// <summary>
    /// Gets or sets the current IServerHost instance.
    /// </summary>
    public static IServerHost? Current
    {
        get => _current;
        set => _current = value;
    }
}
