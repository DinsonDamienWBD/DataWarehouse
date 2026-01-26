namespace DataWarehouse.Integration;

/// <summary>
/// Operating modes for DataWarehouse host applications.
/// </summary>
public enum OperatingMode
{
    /// <summary>
    /// Install and initialize a new DataWarehouse instance.
    /// Creates necessary directories, configuration files, and initializes the kernel.
    /// </summary>
    Install,

    /// <summary>
    /// Connect to an existing DataWarehouse instance (local or remote).
    /// Allows configuration and management of instances without installation.
    /// </summary>
    Connect,

    /// <summary>
    /// Run a tiny embedded DataWarehouse instance for local/single-user scenarios.
    /// Self-contained, requires no external dependencies or installation.
    /// </summary>
    Embedded
}

/// <summary>
/// Connection target for Connect mode.
/// </summary>
public sealed class ConnectionTarget
{
    /// <summary>
    /// Type of connection (Local, Remote, Cluster).
    /// </summary>
    public ConnectionType Type { get; set; } = ConnectionType.Local;

    /// <summary>
    /// Host address for remote connections.
    /// </summary>
    public string Host { get; set; } = "localhost";

    /// <summary>
    /// Port for remote connections.
    /// </summary>
    public int Port { get; set; } = 8080;

    /// <summary>
    /// Path to local instance (for Local type).
    /// </summary>
    public string? LocalPath { get; set; }

    /// <summary>
    /// Authentication token for secure connections.
    /// </summary>
    public string? AuthToken { get; set; }

    /// <summary>
    /// Whether to use TLS/SSL for the connection.
    /// </summary>
    public bool UseTls { get; set; } = true;

    /// <summary>
    /// Connection timeout in seconds.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Creates a local connection target.
    /// </summary>
    public static ConnectionTarget Local(string path) => new()
    {
        Type = ConnectionType.Local,
        LocalPath = path
    };

    /// <summary>
    /// Creates a remote connection target.
    /// </summary>
    public static ConnectionTarget Remote(string host, int port = 8080, bool useTls = true) => new()
    {
        Type = ConnectionType.Remote,
        Host = host,
        Port = port,
        UseTls = useTls
    };
}

/// <summary>
/// Type of connection.
/// </summary>
public enum ConnectionType
{
    /// <summary>
    /// Connect to a local instance via IPC.
    /// </summary>
    Local,

    /// <summary>
    /// Connect to a remote instance via network.
    /// </summary>
    Remote,

    /// <summary>
    /// Connect to a cluster of instances.
    /// </summary>
    Cluster
}

/// <summary>
/// Installation configuration for Install mode.
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

/// <summary>
/// Embedded mode configuration.
/// </summary>
public sealed class EmbeddedConfiguration
{
    /// <summary>
    /// Whether to persist data between sessions.
    /// </summary>
    public bool PersistData { get; set; } = true;

    /// <summary>
    /// Path for persistent data (null for temp directory).
    /// </summary>
    public string? DataPath { get; set; }

    /// <summary>
    /// Maximum memory usage in MB (0 for unlimited).
    /// </summary>
    public int MaxMemoryMb { get; set; } = 256;

    /// <summary>
    /// Whether to expose HTTP interface.
    /// </summary>
    public bool ExposeHttp { get; set; } = false;

    /// <summary>
    /// HTTP port if ExposeHttp is true.
    /// </summary>
    public int HttpPort { get; set; } = 8080;

    /// <summary>
    /// Plugins to load in embedded mode.
    /// </summary>
    public List<string> Plugins { get; set; } = new();
}
