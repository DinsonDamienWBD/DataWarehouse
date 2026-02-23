using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Hosting;

/// <summary>
/// Deployment topology determines which components are deployed on a host.
/// Selected at install time via <c>dw install --topology</c>.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 84: Deployment topology (DPLY-01)")]
public enum DeploymentTopology
{
    /// <summary>Full DW kernel + local VDE engine co-located on the same host (default).</summary>
    DwPlusVde = 0,

    /// <summary>DW kernel only. Connects to remote VDE instances over the network for storage.</summary>
    DwOnly = 1,

    /// <summary>VDE engine only. Slim host without full DW kernel. Accepts remote DW connections.</summary>
    VdeOnly = 2,
}

/// <summary>
/// Describes a deployment topology's characteristics: what runs, what connects remotely.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 84: Deployment topology descriptor (DPLY-01)")]
public static class DeploymentTopologyDescriptor
{
    public static bool RequiresDwKernel(DeploymentTopology topology) =>
        topology is DeploymentTopology.DwPlusVde or DeploymentTopology.DwOnly;

    public static bool RequiresVdeEngine(DeploymentTopology topology) =>
        topology is DeploymentTopology.DwPlusVde or DeploymentTopology.VdeOnly;

    public static bool RequiresRemoteVdeConnection(DeploymentTopology topology) =>
        topology == DeploymentTopology.DwOnly;

    public static bool AcceptsRemoteDwConnections(DeploymentTopology topology) =>
        topology == DeploymentTopology.VdeOnly;

    public static string GetDescription(DeploymentTopology topology) => topology switch
    {
        DeploymentTopology.DwPlusVde => "Full DataWarehouse + VDE co-located (default)",
        DeploymentTopology.DwOnly => "DataWarehouse kernel only (connects to remote VDE)",
        DeploymentTopology.VdeOnly => "VDE engine only (accepts remote DW connections)",
        _ => throw new ArgumentOutOfRangeException(nameof(topology)),
    };
}
