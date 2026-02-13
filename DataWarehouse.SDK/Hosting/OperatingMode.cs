namespace DataWarehouse.SDK.Hosting;

/// <summary>
/// Operating modes for DataWarehouse host applications.
/// This controls how the host application behaves (install, connect, embed, or run as service).
/// NOT to be confused with <see cref="DataWarehouse.SDK.Primitives.OperatingMode"/> which controls kernel resource scaling.
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
    Embedded,

    /// <summary>
    /// Run as a Windows service or Linux daemon.
    /// Long-running background process with system integration.
    /// </summary>
    Service
}
