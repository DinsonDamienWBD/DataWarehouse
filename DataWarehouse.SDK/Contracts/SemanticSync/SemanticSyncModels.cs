using System;
using System.Collections.Generic;

namespace DataWarehouse.SDK.Contracts.SemanticSync;

#region Enums

/// <summary>
/// Classifies the semantic importance of data for sync prioritization.
/// AI classifiers produce this as output to drive fidelity decisions.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic sync types")]
public enum SemanticImportance
{
    /// <summary>Data is critical and must be synced at full fidelity immediately.</summary>
    Critical,

    /// <summary>Data is high importance and should be synced at detailed fidelity promptly.</summary>
    High,

    /// <summary>Data is normal importance and can be synced at standard fidelity.</summary>
    Normal,

    /// <summary>Data is low importance and may be synced at reduced fidelity or deferred.</summary>
    Low,

    /// <summary>Data is negligible and can be synced as metadata-only or dropped entirely.</summary>
    Negligible
}

/// <summary>
/// Defines the fidelity level at which data is synchronized, ranging from
/// full raw payload to metadata-only summaries.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic sync types")]
public enum SyncFidelity
{
    /// <summary>Full raw data with no reduction.</summary>
    Full,

    /// <summary>Detailed representation preserving most structure and content.</summary>
    Detailed,

    /// <summary>Standard representation with moderate compression/summarization.</summary>
    Standard,

    /// <summary>Summarized representation with significant compression.</summary>
    Summarized,

    /// <summary>Metadata only -- schema, tags, and classification but no content payload.</summary>
    Metadata
}

/// <summary>
/// Reasons why a particular sync decision was made, enabling audit trails
/// and explainability for fidelity choices.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic sync types")]
public enum SyncDecisionReason
{
    /// <summary>Fidelity reduced because available bandwidth is insufficient for full sync.</summary>
    BandwidthConstrained,

    /// <summary>Fidelity elevated because the data was classified as high importance.</summary>
    HighImportance,

    /// <summary>Fidelity reduced because the data was classified as low importance.</summary>
    LowImportance,

    /// <summary>Fidelity overridden by an administrative or system policy.</summary>
    PolicyOverride,

    /// <summary>Fidelity explicitly set by a user request.</summary>
    UserExplicit,

    /// <summary>Fidelity determined by an edge-local AI inference result.</summary>
    EdgeInferenceResult,

    /// <summary>Fidelity adjusted as part of conflict resolution.</summary>
    ConflictResolution
}

/// <summary>
/// Classifies the type of semantic conflict between local and remote data versions.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic sync types")]
public enum ConflictType
{
    /// <summary>Both versions express the same meaning despite different representations.</summary>
    SemanticEquivalent,

    /// <summary>Versions have diverged in meaning and cannot be trivially merged.</summary>
    SemanticDivergent,

    /// <summary>Schema has evolved between versions (fields added, removed, or renamed).</summary>
    SchemaEvolution,

    /// <summary>Versions partially overlap in content with some unique changes on each side.</summary>
    PartialOverlap,

    /// <summary>Versions are fundamentally incompatible and cannot be automatically merged.</summary>
    Irreconcilable
}

/// <summary>
/// Strategies for resolving semantic conflicts between local and remote data.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic sync types")]
public enum ConflictResolution
{
    /// <summary>Merge by combining semantic fields from both versions.</summary>
    MergeSemanticFields,

    /// <summary>Prefer the version with higher fidelity data.</summary>
    PreferHigherFidelity,

    /// <summary>Prefer the version with newer semantic meaning (most recent classification).</summary>
    PreferNewerMeaning,

    /// <summary>Prefer the edge/local version.</summary>
    PreferEdge,

    /// <summary>Prefer the cloud/remote version.</summary>
    PreferCloud,

    /// <summary>Defer resolution to a human operator.</summary>
    DeferToUser,

    /// <summary>Automatically resolve using AI-driven semantic analysis.</summary>
    AutoResolve
}

#endregion

#region Records

/// <summary>
/// Result of AI-driven semantic classification of a data item.
/// Captures the importance level, confidence score, semantic tags, and domain hint
/// produced by the classifier.
/// </summary>
/// <param name="DataId">Unique identifier of the classified data item.</param>
/// <param name="Importance">The semantic importance level assigned by the classifier.</param>
/// <param name="Confidence">Confidence score for the classification (0.0 to 1.0).</param>
/// <param name="SemanticTags">Semantic tags describing the content (e.g., "financial", "medical", "logs").</param>
/// <param name="DomainHint">Optional domain hint for the classifier (e.g., "healthcare", "iot").</param>
/// <param name="ClassifiedAt">UTC timestamp when the classification was performed.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic sync types")]
public sealed record SemanticClassification(
    string DataId,
    SemanticImportance Importance,
    double Confidence,
    string[] SemanticTags,
    string DomainHint,
    DateTimeOffset ClassifiedAt);

/// <summary>
/// Describes what fidelity level to use when syncing a data item, the reason for
/// the decision, and optional deferral timing.
/// </summary>
/// <param name="DataId">Unique identifier of the data item this decision applies to.</param>
/// <param name="Fidelity">The fidelity level at which the data should be synced.</param>
/// <param name="Reason">The reason this fidelity level was chosen.</param>
/// <param name="RequiresSummary">Whether a summary representation must be generated before sync.</param>
/// <param name="EstimatedSizeBytes">Estimated size of the sync payload in bytes at the chosen fidelity.</param>
/// <param name="DeferUntil">Optional time to defer synchronization until (e.g., off-peak window).</param>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic sync types")]
public sealed record SyncDecision(
    string DataId,
    SyncFidelity Fidelity,
    SyncDecisionReason Reason,
    bool RequiresSummary,
    long EstimatedSizeBytes,
    TimeSpan? DeferUntil);

/// <summary>
/// An AI-generated summary representation of a data item, used when syncing at
/// reduced fidelity levels. Includes the summary text, embedding vector,
/// source fidelity, and compression ratio.
/// </summary>
/// <param name="DataId">Unique identifier of the summarized data item.</param>
/// <param name="SummaryText">Human-readable summary of the data content.</param>
/// <param name="SummaryEmbedding">Vector embedding of the summary for semantic similarity comparisons.</param>
/// <param name="SourceFidelity">The fidelity level of the source data that was summarized.</param>
/// <param name="CompressionRatio">Ratio of summary size to original size (0.0 to 1.0).</param>
/// <param name="GeneratedAt">UTC timestamp when the summary was generated.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic sync types")]
public sealed record DataSummary(
    string DataId,
    string SummaryText,
    byte[] SummaryEmbedding,
    SyncFidelity SourceFidelity,
    double CompressionRatio,
    DateTimeOffset GeneratedAt);

/// <summary>
/// Represents a conflict between local and remote versions of data, enriched
/// with semantic classification context from both sides.
/// </summary>
/// <param name="DataId">Unique identifier of the conflicting data item.</param>
/// <param name="Type">The type of semantic conflict detected.</param>
/// <param name="LocalData">Raw byte payload of the local version.</param>
/// <param name="RemoteData">Raw byte payload of the remote version.</param>
/// <param name="LocalClassification">Semantic classification of the local version.</param>
/// <param name="RemoteClassification">Semantic classification of the remote version.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic sync types")]
public sealed record SemanticConflict(
    string DataId,
    ConflictType Type,
    ReadOnlyMemory<byte> LocalData,
    ReadOnlyMemory<byte> RemoteData,
    SemanticClassification LocalClassification,
    SemanticClassification RemoteClassification);

/// <summary>
/// Result of resolving a semantic conflict, including the chosen strategy,
/// merged data, semantic similarity score, and human-readable explanation.
/// </summary>
/// <param name="DataId">Unique identifier of the resolved data item.</param>
/// <param name="Strategy">The conflict resolution strategy that was applied.</param>
/// <param name="ResolvedData">The resolved/merged data payload.</param>
/// <param name="SemanticSimilarity">Semantic similarity between the two conflicting versions (0.0 to 1.0).</param>
/// <param name="Explanation">Human-readable explanation of how the conflict was resolved.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic sync types")]
public sealed record ConflictResolutionResult(
    string DataId,
    ConflictResolution Strategy,
    ReadOnlyMemory<byte> ResolvedData,
    double SemanticSimilarity,
    string Explanation);

/// <summary>
/// Snapshot of the current bandwidth budget state, including available bandwidth,
/// pending sync count, utilization percentage, and distribution of items across fidelity levels.
/// </summary>
/// <param name="AvailableBandwidthBps">Currently available bandwidth in bytes per second.</param>
/// <param name="PendingSyncCount">Number of data items awaiting synchronization.</param>
/// <param name="BudgetUtilizationPercent">Percentage of bandwidth budget currently consumed (0.0 to 100.0).</param>
/// <param name="FidelityDistribution">Count of pending items at each fidelity level.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic sync types")]
public sealed record FidelityBudget(
    long AvailableBandwidthBps,
    int PendingSyncCount,
    double BudgetUtilizationPercent,
    Dictionary<SyncFidelity, int> FidelityDistribution);

/// <summary>
/// Output of an edge-local AI inference that produced both a classification
/// and a sync decision, with latency and model provenance metadata.
/// </summary>
/// <param name="ModelId">Identifier of the AI model used for inference.</param>
/// <param name="Classification">The semantic classification produced by the model.</param>
/// <param name="Decision">The sync decision derived from the classification.</param>
/// <param name="InferenceLatencyMs">Time taken for the inference in milliseconds.</param>
/// <param name="UsedLocalModel">Whether the inference used a locally-deployed model (true) or a remote/cloud model (false).</param>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic sync types")]
public sealed record EdgeInferenceResult(
    string ModelId,
    SemanticClassification Classification,
    SyncDecision Decision,
    double InferenceLatencyMs,
    bool UsedLocalModel);

#endregion
