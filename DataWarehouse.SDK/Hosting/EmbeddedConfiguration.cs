namespace DataWarehouse.SDK.Hosting;

/// <summary>
/// Embedded mode configuration.
/// Defines options for running a lightweight, self-contained DataWarehouse instance.
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
