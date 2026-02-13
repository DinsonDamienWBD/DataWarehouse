namespace DataWarehouse.SDK.Hosting;

/// <summary>
/// Connection target for Connect mode.
/// Represents the endpoint and configuration for connecting to a DataWarehouse instance.
/// </summary>
public sealed class ConnectionTarget
{
    /// <summary>
    /// Type of connection (Local, Remote, InProcess, Cluster).
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
    /// Display name for this connection.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Extensible metadata dictionary for custom connection properties.
    /// </summary>
    public Dictionary<string, string> Metadata { get; set; } = new();

    /// <summary>
    /// Creates a local connection target.
    /// </summary>
    /// <param name="path">Path to the local DataWarehouse instance.</param>
    /// <returns>A <see cref="ConnectionTarget"/> configured for local connection.</returns>
    public static ConnectionTarget Local(string path) => new()
    {
        Type = ConnectionType.Local,
        LocalPath = path
    };

    /// <summary>
    /// Creates a remote connection target.
    /// </summary>
    /// <param name="host">Remote host address.</param>
    /// <param name="port">Remote port (default 8080).</param>
    /// <param name="useTls">Whether to use TLS/SSL (default true).</param>
    /// <returns>A <see cref="ConnectionTarget"/> configured for remote connection.</returns>
    public static ConnectionTarget Remote(string host, int port = 8080, bool useTls = true) => new()
    {
        Type = ConnectionType.Remote,
        Host = host,
        Port = port,
        UseTls = useTls
    };
}
