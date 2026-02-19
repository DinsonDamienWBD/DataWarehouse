using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using DataWarehouse.SDK.Moonshots;

namespace DataWarehouse.Plugins.UltimateDataGovernance.Moonshots;

/// <summary>
/// Provides the standard moonshot pipeline definitions used during data ingest
/// and lifecycle processing. The <see cref="IngestToLifecycle"/> pipeline defines
/// the canonical 10-stage order from data consciousness scoring through universal
/// fabric namespace registration.
/// </summary>
public static class DefaultPipelineDefinition
{
    /// <summary>
    /// The standard ingest-to-lifecycle pipeline executing all 10 moonshots in order:
    /// <list type="number">
    ///   <item>DataConsciousness -- score the data first (value/liability determines everything else)</item>
    ///   <item>UniversalTags -- attach tags based on consciousness score and content analysis</item>
    ///   <item>CompliancePassports -- issue passport using tags for regulation matching</item>
    ///   <item>SovereigntyMesh -- check sovereignty zone constraints using passport</item>
    ///   <item>ZeroGravityStorage -- compute placement using tags, passport, sovereignty</item>
    ///   <item>CryptoTimeLocks -- apply time-lock policy based on compliance requirements</item>
    ///   <item>SemanticSync -- configure sync fidelity based on consciousness score</item>
    ///   <item>ChaosVaccination -- register object in chaos vaccination scope</item>
    ///   <item>CarbonAwareLifecycle -- assign lifecycle tier based on consciousness and carbon budget</item>
    ///   <item>UniversalFabric -- register in dw:// namespace for universal addressing</item>
    /// </list>
    /// All 10 moonshots are enabled by default.
    /// </summary>
    public static MoonshotPipelineDefinition IngestToLifecycle { get; } = BuildIngestToLifecyclePipeline();

    private static MoonshotPipelineDefinition BuildIngestToLifecyclePipeline()
    {
        var stageOrder = new List<MoonshotId>
        {
            MoonshotId.DataConsciousness,
            MoonshotId.UniversalTags,
            MoonshotId.CompliancePassports,
            MoonshotId.SovereigntyMesh,
            MoonshotId.ZeroGravityStorage,
            MoonshotId.CryptoTimeLocks,
            MoonshotId.SemanticSync,
            MoonshotId.ChaosVaccination,
            MoonshotId.CarbonAwareLifecycle,
            MoonshotId.UniversalFabric
        };

        var featureFlags = new Dictionary<MoonshotId, MoonshotFeatureFlag>();
        foreach (var id in stageOrder)
        {
            featureFlags[id] = new MoonshotFeatureFlag(id, Enabled: true);
        }

        return new MoonshotPipelineDefinition(
            Name: "IngestToLifecycle",
            StageOrder: stageOrder.AsReadOnly(),
            FeatureFlags: new ReadOnlyDictionary<MoonshotId, MoonshotFeatureFlag>(featureFlags));
    }
}
