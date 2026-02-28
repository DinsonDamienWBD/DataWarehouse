using System;

namespace DataWarehouse.SDK.Hosting;

/// <summary>
/// Installation configuration for Install mode.
/// Defines all options for installing and initializing a new DataWarehouse instance.
/// </summary>
public sealed class InstallConfiguration
{
    /// <summary>
    /// Target installation directory.
    /// </summary>
    public string InstallPath { get; set; } = "";

    /// <summary>
    /// Data storage directory (can be different from install path).
    /// </summary>
    public string? DataPath { get; set; }

    /// <summary>
    /// Plugins to include in the installation.
    /// </summary>
    public List<string> IncludedPlugins { get; set; } = new();

    /// <summary>
    /// Whether to create a Windows service (Windows only).
    /// </summary>
    public bool CreateService { get; set; }

    /// <summary>
    /// Whether to start automatically on system boot.
    /// </summary>
    public bool AutoStart { get; set; }

    /// <summary>
    /// Initial configuration values to apply.
    /// </summary>
    public Dictionary<string, object> InitialConfig { get; set; } = new();

    /// <summary>
    /// Whether to create default user/admin account.
    /// </summary>
    public bool CreateDefaultAdmin { get; set; } = true;

    /// <summary>
    /// Admin username (if CreateDefaultAdmin is true).
    /// </summary>
    public string AdminUsername { get; set; } = "admin";

    /// <summary>
    /// Admin password (if CreateDefaultAdmin is true).
    /// Must be set when CreateDefaultAdmin is true; a null or empty password would create a blank admin account.
    /// </summary>
    public string? AdminPassword { get; set; }

    /// <summary>
    /// Validates the configuration and throws <see cref="InvalidOperationException"/> if required fields are missing.
    /// Call before passing to the installation pipeline.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when AdminPassword is null or empty and CreateDefaultAdmin is true.</exception>
    public void Validate()
    {
        if (CreateDefaultAdmin && string.IsNullOrWhiteSpace(AdminPassword))
            throw new InvalidOperationException(
                "AdminPassword must be set when CreateDefaultAdmin is true. " +
                "A null or empty password would create a blank admin account, which is a security risk.");
    }

    /// <summary>
    /// Deployment topology: DW-only, VDE-only, or DW+VDE co-located (default).
    /// </summary>
    public DeploymentTopology Topology { get; set; } = DeploymentTopology.DwPlusVde;

    /// <summary>
    /// Remote VDE URL to connect to (used when Topology == DwOnly).
    /// </summary>
    public string? RemoteVdeUrl { get; set; }

    /// <summary>
    /// Port for VDE to accept remote DW connections (used when Topology == VdeOnly).
    /// </summary>
    public int VdeListenPort { get; set; } = 9443;
}
