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
    /// </summary>
    public string? AdminPassword { get; set; }
}
