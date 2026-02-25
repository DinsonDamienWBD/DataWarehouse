using DataWarehouse.SDK.Contracts.IntelligenceAware;
using System.Collections.Generic;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base class for plugins that provide SERVICES (AD-01 Feature branch).
/// Provides service lifecycle semantics: start, stop, health reporting.
/// Examples: Security, Interface, DataManagement, Compute, Observability, Streaming.
/// </summary>
/// <remarks>
/// <para>
/// Feature plugins are fundamentally different from DataPipeline plugins:
/// </para>
/// <list type="bullet">
///   <item>They provide services/capabilities to the system (not data transformation)</item>
///   <item>They observe, enforce, or serve data (not mutate or persist it)</item>
///   <item>They have their own lifecycle (start/stop) independent of pipeline ordering</item>
///   <item>They may expose external interfaces (REST, gRPC) or enforce policies (compliance)</item>
/// </list>
/// <para>
/// FeaturePluginBase inherits from IntelligenceAwarePluginBase (AD-01).
/// All service-oriented plugins inherit from this or a domain-specific
/// Hierarchy base that extends it (e.g., SecurityPluginBase, ComputePluginBase).
/// </para>
/// </remarks>
public abstract class FeaturePluginBase : IntelligenceAwarePluginBase
{
    /// <summary>
    /// Whether this feature supports hot-reload (reconfiguration without restart).
    /// </summary>
    public virtual bool SupportsHotReload => false;

    /// <summary>
    /// Feature category for grouping in capability registry.
    /// Override to specify (e.g., "Security", "Interface", "Compute").
    /// </summary>
    public virtual string FeatureCategory => "Generic";

    /// <summary>
    /// Gets feature-specific metadata for registration and AI-driven discovery.
    /// </summary>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["PipelineBranch"] = "Feature";
        metadata["FeatureCategory"] = FeatureCategory;
        metadata["SupportsHotReload"] = SupportsHotReload;
        metadata["RequiresLifecycleManagement"] = true;
        return metadata;
    }
}
