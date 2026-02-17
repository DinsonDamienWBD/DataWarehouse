using DataWarehouse.SDK.Contracts.Hierarchy;

namespace DataWarehouse.SDK.Contracts
{
    /// <summary>
    /// Compatibility shim forwarding to <see cref="Hierarchy.InterfacePluginBase"/>.
    /// All new code should extend <see cref="Hierarchy.InterfacePluginBase"/> directly.
    /// </summary>
    public abstract class InterfacePluginBase : Hierarchy.InterfacePluginBase
    {
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "Interface";
            metadata["SupportsDiscovery"] = true;
            return metadata;
        }
    }
}
