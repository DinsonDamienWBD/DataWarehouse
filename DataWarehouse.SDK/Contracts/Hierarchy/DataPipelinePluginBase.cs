using DataWarehouse.SDK.Contracts.IntelligenceAware;
using System;
using System.Collections.Generic;

namespace DataWarehouse.SDK.Contracts.Hierarchy;

/// <summary>
/// Abstract base class for plugins that data flows THROUGH (AD-01 DataPipeline branch).
/// Provides pipeline semantics: stage ordering, back-pressure signals, throughput tracking.
/// Examples: Encryption, Compression, Storage, Replication, Transit, Integrity.
/// </summary>
/// <remarks>
/// <para>
/// DataPipeline plugins are fundamentally different from Feature plugins:
/// </para>
/// <list type="bullet">
///   <item>Data enters, gets transformed/stored/moved, and exits</item>
///   <item>They participate in pipeline ordering (earlier stages run first)</item>
///   <item>They track throughput and can signal back-pressure</item>
///   <item>They may mutate data (encryption, compression) or persist it (storage)</item>
/// </list>
/// </remarks>
public abstract class DataPipelinePluginBase : IntelligenceAwarePluginBase
{
    /// <summary>
    /// Default execution order in the pipeline (lower = earlier).
    /// Can be overridden at runtime by the kernel.
    /// </summary>
    public virtual int DefaultPipelineOrder => 100;

    /// <summary>
    /// Whether this pipeline stage can be bypassed based on content analysis.
    /// </summary>
    public virtual bool AllowBypass => false;

    /// <summary>
    /// Stage dependencies -- other stages that must run before this one.
    /// Empty means no dependencies.
    /// </summary>
    public virtual IReadOnlyList<string> RequiredPrecedingStages => Array.Empty<string>();

    /// <summary>
    /// Stage conflicts -- stages that cannot run in the same pipeline.
    /// </summary>
    public virtual IReadOnlyList<string> IncompatibleStages => Array.Empty<string>();

    /// <summary>
    /// Whether this pipeline stage mutates data (true for encryption/compression)
    /// or persists/moves it (false for storage/replication).
    /// </summary>
    public virtual bool MutatesData => false;

    /// <summary>
    /// Gets pipeline-specific metadata for registration and AI-driven optimization.
    /// </summary>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["PipelineBranch"] = "DataPipeline";
        metadata["DefaultPipelineOrder"] = DefaultPipelineOrder;
        metadata["AllowBypass"] = AllowBypass;
        metadata["MutatesData"] = MutatesData;
        metadata["RequiredPrecedingStages"] = RequiredPrecedingStages;
        metadata["IncompatibleStages"] = IncompatibleStages;
        return metadata;
    }
}
