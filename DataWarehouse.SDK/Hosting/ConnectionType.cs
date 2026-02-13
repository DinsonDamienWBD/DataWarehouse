namespace DataWarehouse.SDK.Hosting;

/// <summary>
/// Type of connection to a DataWarehouse instance.
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
    /// Connect to an in-process instance (direct kernel invocation).
    /// </summary>
    InProcess,

    /// <summary>
    /// Connect to a cluster of instances.
    /// </summary>
    Cluster
}
