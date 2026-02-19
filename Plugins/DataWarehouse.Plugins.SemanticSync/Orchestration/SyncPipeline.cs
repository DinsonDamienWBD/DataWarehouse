// <copyright file="SyncPipeline.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.SemanticSync;
using DataWarehouse.SDK.Utilities;
using ConflictResolutionResult = DataWarehouse.SDK.Contracts.SemanticSync.ConflictResolutionResult;
using ConflictResolution = DataWarehouse.SDK.Contracts.SemanticSync.ConflictResolution;
using DataWarehouse.Plugins.SemanticSync.Strategies.Classification;
using DataWarehouse.Plugins.SemanticSync.Strategies.ConflictResolution;
using DataWarehouse.Plugins.SemanticSync.Strategies.EdgeInference;
using DataWarehouse.Plugins.SemanticSync.Strategies.Fidelity;
using DataWarehouse.Plugins.SemanticSync.Strategies.Routing;

namespace DataWarehouse.Plugins.SemanticSync.Orchestration;

/// <summary>
/// Defines the sync processing pipeline that transforms raw data into an optimized sync payload
/// through five sequential stages: classify, decide fidelity, route, prepare payload, and return result.
/// Also provides a separate conflict resolution path for handling sync conflicts.
/// </summary>
/// <remarks>
/// <para>
/// Pipeline stages execute in strict order:
/// <list type="number">
///   <item><b>Classify:</b> Determine semantic importance via edge inference or hybrid classifier.</item>
///   <item><b>Decide Fidelity:</b> Choose sync fidelity based on classification and bandwidth budget.</item>
///   <item><b>Route:</b> Confirm/adjust fidelity decision and generate summary if needed.</item>
///   <item><b>Prepare Payload:</b> Build the actual bytes to send at the chosen fidelity level.</item>
///   <item><b>Return:</b> Package result with compression ratio, processing time, and status.</item>
/// </list>
/// </para>
/// </remarks>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic sync orchestration")]
internal sealed class SyncPipeline
{
    private readonly ISemanticClassifier _classifier;
    private readonly ISyncFidelityController _fidelityController;
    private readonly ISummaryRouter _router;
    private readonly ISemanticConflictResolver _conflictResolver;
    private readonly EdgeInferenceCoordinator _edgeInference;

    /// <summary>
    /// Initializes a new instance of the <see cref="SyncPipeline"/> class.
    /// </summary>
    /// <param name="classifier">The semantic classifier for data importance classification.</param>
    /// <param name="fidelityController">The fidelity controller for bandwidth-aware decisions.</param>
    /// <param name="router">The summary router for generating reduced-fidelity representations.</param>
    /// <param name="conflictResolver">The conflict resolver for handling sync conflicts.</param>
    /// <param name="edgeInference">The edge inference coordinator for local AI classification.</param>
    /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
    public SyncPipeline(
        ISemanticClassifier classifier,
        ISyncFidelityController fidelityController,
        ISummaryRouter router,
        ISemanticConflictResolver conflictResolver,
        EdgeInferenceCoordinator edgeInference)
    {
        _classifier = classifier ?? throw new ArgumentNullException(nameof(classifier));
        _fidelityController = fidelityController ?? throw new ArgumentNullException(nameof(fidelityController));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _conflictResolver = conflictResolver ?? throw new ArgumentNullException(nameof(conflictResolver));
        _edgeInference = edgeInference ?? throw new ArgumentNullException(nameof(edgeInference));
    }

    /// <summary>
    /// Executes the full sync pipeline for a data item, processing it through classify, fidelity,
    /// route, and prepare stages to produce an optimized sync payload.
    /// </summary>
    /// <param name="dataId">Unique identifier of the data item.</param>
    /// <param name="data">The raw data payload to process.</param>
    /// <param name="metadata">Optional metadata key-value pairs to assist classification.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="SyncPipelineResult"/> containing the optimized payload and processing metadata.</returns>
    public async Task<SyncPipelineResult> ExecuteAsync(
        string dataId,
        ReadOnlyMemory<byte> data,
        IDictionary<string, string>? metadata,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataId);
        ct.ThrowIfCancellationRequested();

        var stopwatch = Stopwatch.StartNew();
        long originalSizeBytes = data.Length;

        try
        {
            // Stage 1: Classify - determine semantic importance
            SemanticClassification classification;
            if (_edgeInference.SupportsLocalInference)
            {
                try
                {
                    var edgeResult = await _edgeInference.InferAsync(dataId, data, metadata, ct)
                        .ConfigureAwait(false);
                    classification = edgeResult.Classification;
                }
                catch
                {
                    // Edge inference failed, fall back to classifier
                    classification = await _classifier.ClassifyAsync(data, metadata, ct)
                        .ConfigureAwait(false);
                }
            }
            else
            {
                classification = await _classifier.ClassifyAsync(data, metadata, ct)
                    .ConfigureAwait(false);
            }

            // Stage 2: Decide Fidelity - choose sync quality based on classification and budget
            var budget = await _fidelityController.GetCurrentBudgetAsync(ct).ConfigureAwait(false);
            var decision = await _fidelityController.DecideFidelityAsync(classification, budget, ct)
                .ConfigureAwait(false);

            // Check for deferral
            if (decision.DeferUntil.HasValue)
            {
                stopwatch.Stop();
                return new SyncPipelineResult(
                    DataId: dataId,
                    Status: SyncPipelineStatus.Deferred,
                    Classification: classification,
                    Decision: decision,
                    Payload: ReadOnlyMemory<byte>.Empty,
                    Summary: null,
                    OriginalSizeBytes: originalSizeBytes,
                    PayloadSizeBytes: 0,
                    CompressionRatio: 0.0,
                    ProcessingTime: stopwatch.Elapsed);
            }

            // Stage 3: Route - confirm/adjust fidelity and generate summary if needed
            var routeDecision = await _router.RouteAsync(dataId, classification, budget, ct)
                .ConfigureAwait(false);

            DataSummary? summary = null;
            ReadOnlyMemory<byte> payload;

            if (routeDecision.RequiresSummary)
            {
                summary = await _router.GenerateSummaryAsync(dataId, data, routeDecision.Fidelity, ct)
                    .ConfigureAwait(false);
            }

            // Stage 4: Prepare Payload - build the actual bytes to send
            if (routeDecision.Fidelity == SyncFidelity.Full)
            {
                // Full fidelity: send raw data as-is
                payload = data;
            }
            else if (summary is not null)
            {
                // Summary available: use summary text as payload
                payload = System.Text.Encoding.UTF8.GetBytes(summary.SummaryText);
            }
            else
            {
                // Reduced fidelity without summary: use downsampled raw data
                payload = data;
            }

            // Stage 5: Return result
            long payloadSizeBytes = payload.Length;
            double compressionRatio = originalSizeBytes > 0
                ? (double)payloadSizeBytes / originalSizeBytes
                : 1.0;

            stopwatch.Stop();

            return new SyncPipelineResult(
                DataId: dataId,
                Status: SyncPipelineStatus.Ready,
                Classification: classification,
                Decision: routeDecision,
                Payload: payload,
                Summary: summary,
                OriginalSizeBytes: originalSizeBytes,
                PayloadSizeBytes: payloadSizeBytes,
                CompressionRatio: compressionRatio,
                ProcessingTime: stopwatch.Elapsed);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            stopwatch.Stop();

            // Create a minimal failed result
            var fallbackClassification = new SemanticClassification(
                DataId: dataId,
                Importance: SemanticImportance.Normal,
                Confidence: 0.0,
                SemanticTags: Array.Empty<string>(),
                DomainHint: "unknown",
                ClassifiedAt: DateTimeOffset.UtcNow);

            var fallbackDecision = new SyncDecision(
                DataId: dataId,
                Fidelity: SyncFidelity.Full,
                Reason: SyncDecisionReason.PolicyOverride,
                RequiresSummary: false,
                EstimatedSizeBytes: originalSizeBytes,
                DeferUntil: null);

            return new SyncPipelineResult(
                DataId: dataId,
                Status: SyncPipelineStatus.Failed,
                Classification: fallbackClassification,
                Decision: fallbackDecision,
                Payload: ReadOnlyMemory<byte>.Empty,
                Summary: null,
                OriginalSizeBytes: originalSizeBytes,
                PayloadSizeBytes: 0,
                CompressionRatio: 0.0,
                ProcessingTime: stopwatch.Elapsed);
        }
    }

    /// <summary>
    /// Resolves a conflict between local and remote versions of a data item using semantic analysis.
    /// Called separately when sync detects a conflict during replication.
    /// </summary>
    /// <param name="dataId">Unique identifier of the conflicting data item.</param>
    /// <param name="localData">The local version of the data payload.</param>
    /// <param name="remoteData">The remote version of the data payload.</param>
    /// <param name="messageBus">Optional message bus for publishing pending conflicts.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="ConflictResolutionResult"/> with the resolved data and explanation.</returns>
    public async Task<ConflictResolutionResult> ResolveConflictAsync(
        string dataId,
        ReadOnlyMemory<byte> localData,
        ReadOnlyMemory<byte> remoteData,
        IMessageBus? messageBus,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataId);
        ct.ThrowIfCancellationRequested();

        // Step 1: Detect conflict
        var conflict = await _conflictResolver.DetectConflictAsync(dataId, localData, remoteData, ct)
            .ConfigureAwait(false);

        // Step 2: If no conflict detected, auto-resolve with local data
        if (conflict is null)
        {
            return new ConflictResolutionResult(
                DataId: dataId,
                Strategy: ConflictResolution.AutoResolve,
                ResolvedData: localData,
                SemanticSimilarity: 1.0,
                Explanation: "No semantic conflict detected; local and remote versions are compatible.");
        }

        // Step 3: Classify conflict type
        var conflictType = await _conflictResolver.ClassifyConflictAsync(conflict, ct)
            .ConfigureAwait(false);

        // Step 4: Resolve using appropriate strategy
        var result = await _conflictResolver.ResolveAsync(conflict, ct).ConfigureAwait(false);

        // Step 5: If deferred to user, publish to pending topic
        if (result.Strategy == ConflictResolution.DeferToUser && messageBus is not null)
        {
            await messageBus.PublishAsync("semantic-sync.conflict.pending", new PluginMessage
            {
                Type = "conflict-pending",
                Source = "semantic-sync",
                CorrelationId = Guid.NewGuid().ToString("N"),
                Payload = new Dictionary<string, object>
                {
                    ["data_id"] = dataId,
                    ["conflict_type"] = conflictType.ToString(),
                    ["similarity"] = result.SemanticSimilarity,
                    ["explanation"] = result.Explanation,
                    ["timestamp"] = DateTimeOffset.UtcNow
                }
            }, ct).ConfigureAwait(false);
        }

        return result;
    }
}

/// <summary>
/// Status of a sync pipeline execution result.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic sync orchestration")]
internal enum SyncPipelineStatus
{
    /// <summary>Pipeline completed successfully and payload is ready to sync.</summary>
    Ready,

    /// <summary>Pipeline deferred the sync operation to a later time due to bandwidth constraints.</summary>
    Deferred,

    /// <summary>Pipeline failed to process the data item.</summary>
    Failed
}

/// <summary>
/// Result of executing the sync pipeline for a single data item. Contains the optimized
/// payload, classification, fidelity decision, compression metrics, and processing time.
/// </summary>
/// <param name="DataId">Unique identifier of the processed data item.</param>
/// <param name="Status">Pipeline execution status (Ready, Deferred, or Failed).</param>
/// <param name="Classification">The semantic classification produced during the classify stage.</param>
/// <param name="Decision">The fidelity decision that determined the payload representation.</param>
/// <param name="Payload">The optimized sync payload (may be raw, downsampled, or summarized).</param>
/// <param name="Summary">Optional summary if the data was routed through the summary path.</param>
/// <param name="OriginalSizeBytes">Size of the original raw data in bytes.</param>
/// <param name="PayloadSizeBytes">Size of the prepared sync payload in bytes.</param>
/// <param name="CompressionRatio">Ratio of payload size to original size (0.0 to 1.0).</param>
/// <param name="ProcessingTime">Total time spent processing the data through the pipeline.</param>
[SdkCompatibility("5.0.0", Notes = "Phase 60: Semantic sync orchestration")]
internal sealed record SyncPipelineResult(
    string DataId,
    SyncPipelineStatus Status,
    SemanticClassification Classification,
    SyncDecision Decision,
    ReadOnlyMemory<byte> Payload,
    DataSummary? Summary,
    long OriginalSizeBytes,
    long PayloadSizeBytes,
    double CompressionRatio,
    TimeSpan ProcessingTime);
