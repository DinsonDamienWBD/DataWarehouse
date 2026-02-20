using System.Collections.Concurrent;
using System.Diagnostics;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Memory.Regeneration;

/// <summary>
/// Multi-pass regeneration pipeline that orchestrates complex regeneration workflows.
/// Provides staged processing with fallback strategies and progressive refinement.
/// </summary>
public sealed class RegenerationPipeline
{
    private readonly RegenerationStrategyRegistry _registry;
    private readonly AccuracyVerifier _verifier;
    private readonly RegenerationMetrics _metrics;
    private readonly PipelineConfiguration _config;
    private readonly BoundedDictionary<string, PipelineExecution> _activeExecutions = new BoundedDictionary<string, PipelineExecution>(1000);

    /// <summary>
    /// Initializes a new regeneration pipeline with specified components.
    /// </summary>
    /// <param name="registry">Strategy registry for strategy selection.</param>
    /// <param name="verifier">Accuracy verifier for validation.</param>
    /// <param name="metrics">Metrics collector for tracking.</param>
    /// <param name="config">Pipeline configuration.</param>
    public RegenerationPipeline(
        RegenerationStrategyRegistry registry,
        AccuracyVerifier verifier,
        RegenerationMetrics metrics,
        PipelineConfiguration? config = null)
    {
        _registry = registry;
        _verifier = verifier;
        _metrics = metrics;
        _config = config ?? new PipelineConfiguration();
    }

    /// <summary>
    /// Creates a pipeline with default components.
    /// </summary>
    /// <returns>A new pipeline instance.</returns>
    public static RegenerationPipeline CreateDefault()
    {
        var registry = RegenerationStrategyRegistry.Instance;
        var metrics = new RegenerationMetrics();
        var verifier = new AccuracyVerifier(metrics);
        return new RegenerationPipeline(registry, verifier, metrics);
    }

    /// <summary>
    /// Executes the regeneration pipeline for the given context.
    /// </summary>
    /// <param name="context">The encoded context to regenerate.</param>
    /// <param name="options">Regeneration options.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Pipeline execution result.</returns>
    public async Task<PipelineResult> ExecuteAsync(
        EncodedContext context,
        RegenerationOptions? options = null,
        CancellationToken ct = default)
    {
        var executionId = Guid.NewGuid().ToString("N")[..12];
        var execution = new PipelineExecution(executionId);
        _activeExecutions[executionId] = execution;

        var stopwatch = Stopwatch.StartNew();
        options ??= new RegenerationOptions();

        try
        {
            execution.Status = PipelineStatus.Running;
            execution.StartedAt = DateTime.UtcNow;

            // Phase 1: Strategy Selection
            var selectionResult = await SelectStrategiesAsync(context, options, ct);
            execution.AddPhase("StrategySelection", selectionResult.Strategies.Count, stopwatch.Elapsed);

            if (selectionResult.Strategies.Count == 0)
            {
                return CreateFailureResult(executionId, "No suitable strategies found", stopwatch.Elapsed);
            }

            // Phase 2: Initial Regeneration Attempts
            var regenerationResults = await ExecuteRegenerationPassesAsync(
                context, selectionResult.Strategies, options, execution, ct);

            // Phase 3: Verification and Refinement
            var verifiedResults = await VerifyAndRefineAsync(
                context, regenerationResults, options, execution, ct);

            // Phase 4: Result Selection
            var bestResult = SelectBestResult(verifiedResults);

            // Phase 5: Final Validation
            if (bestResult != null && _config.EnableFinalValidation)
            {
                bestResult = await PerformFinalValidationAsync(context, bestResult, execution, ct);
            }

            stopwatch.Stop();
            execution.Status = bestResult != null ? PipelineStatus.Completed : PipelineStatus.Failed;
            execution.CompletedAt = DateTime.UtcNow;

            // Record metrics
            if (bestResult != null)
            {
                _metrics.RecordSuccess(
                    bestResult.StrategyId,
                    bestResult.ActualAccuracy,
                    stopwatch.Elapsed);
            }
            else
            {
                _metrics.RecordFailure("Pipeline", "NoAcceptableResult", stopwatch.Elapsed);
            }

            return new PipelineResult
            {
                ExecutionId = executionId,
                Success = bestResult != null,
                Result = bestResult,
                AllResults = verifiedResults,
                TotalDuration = stopwatch.Elapsed,
                Phases = execution.Phases.ToList(),
                StrategiesAttempted = selectionResult.Strategies.Count,
                ErrorMessage = bestResult == null ? "No result met accuracy threshold" : null
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            execution.Status = PipelineStatus.Failed;
            execution.ErrorMessage = ex.Message;
            _metrics.RecordFailure("Pipeline", ex.GetType().Name, stopwatch.Elapsed);

            return CreateFailureResult(executionId, ex.Message, stopwatch.Elapsed);
        }
        finally
        {
            _activeExecutions.TryRemove(executionId, out _);
        }
    }

    /// <summary>
    /// Executes a multi-strategy ensemble regeneration for maximum accuracy.
    /// </summary>
    /// <param name="context">The encoded context to regenerate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Ensemble result with combined verification.</returns>
    public async Task<EnsembleResult> ExecuteEnsembleAsync(
        EncodedContext context,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var strategies = _registry.GetAllStrategies();
        var results = new ConcurrentBag<(IAdvancedRegenerationStrategy Strategy, RegenerationResult Result)>();

        // Run all strategies in parallel
        var tasks = strategies.Select(async strategy =>
        {
            try
            {
                var capability = await strategy.AssessCapabilityAsync(context, ct);
                if (capability.CanRegenerate && capability.ExpectedAccuracy >= 0.8)
                {
                    var result = await strategy.RegenerateAsync(context, new RegenerationOptions(), ct);
                    if (result.Success)
                    {
                        results.Add((strategy, result));
                    }
                }
            }
            catch
            {
                // Strategy failed, continue with others
            }
        });

        await Task.WhenAll(tasks);

        if (results.IsEmpty)
        {
            return new EnsembleResult
            {
                Success = false,
                ErrorMessage = "No strategies produced valid results"
            };
        }

        // Verify all results
        var verifiedResults = new List<VerifiedResult>();
        foreach (var (strategy, result) in results)
        {
            var verification = await _verifier.VerifyAsync(context, result, ct);
            verifiedResults.Add(new VerifiedResult
            {
                StrategyId = strategy.StrategyId,
                Result = result,
                Verification = verification
            });
        }

        // Calculate consensus
        var consensus = CalculateConsensus(verifiedResults);

        stopwatch.Stop();

        return new EnsembleResult
        {
            Success = true,
            BestResult = verifiedResults.OrderByDescending(r => r.Verification.OverallScore).First(),
            AllResults = verifiedResults,
            ConsensusScore = consensus.Score,
            ConsensusData = consensus.Data,
            TotalDuration = stopwatch.Elapsed,
            StrategiesSucceeded = verifiedResults.Count
        };
    }

    /// <summary>
    /// Executes incremental regeneration for large contexts.
    /// </summary>
    /// <param name="context">The encoded context to regenerate.</param>
    /// <param name="chunkSize">Size of chunks to process.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Incremental regeneration result.</returns>
    public async Task<IncrementalResult> ExecuteIncrementalAsync(
        EncodedContext context,
        int chunkSize = 4096,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var chunks = ChunkContext(context, chunkSize);
        var regeneratedChunks = new List<ChunkResult>();
        var failedChunks = new List<int>();

        for (int i = 0; i < chunks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var chunkContext = chunks[i];
                var result = await ExecuteAsync(chunkContext, null, ct);

                if (result.Success && result.Result != null)
                {
                    regeneratedChunks.Add(new ChunkResult
                    {
                        ChunkIndex = i,
                        Success = true,
                        Data = result.Result.RegeneratedContent,
                        Accuracy = result.Result.ActualAccuracy
                    });
                }
                else
                {
                    failedChunks.Add(i);
                    regeneratedChunks.Add(new ChunkResult
                    {
                        ChunkIndex = i,
                        Success = false,
                        ErrorMessage = result.ErrorMessage
                    });
                }
            }
            catch (Exception ex)
            {
                failedChunks.Add(i);
                regeneratedChunks.Add(new ChunkResult
                {
                    ChunkIndex = i,
                    Success = false,
                    ErrorMessage = ex.Message
                });
            }
        }

        stopwatch.Stop();

        // Combine successful chunks
        var combinedData = CombineChunks(regeneratedChunks);
        var overallAccuracy = regeneratedChunks
            .Where(c => c.Success)
            .Select(c => c.Accuracy)
            .DefaultIfEmpty(0)
            .Average();

        return new IncrementalResult
        {
            Success = failedChunks.Count == 0,
            TotalChunks = chunks.Count,
            SuccessfulChunks = chunks.Count - failedChunks.Count,
            FailedChunkIndices = failedChunks,
            CombinedData = combinedData,
            OverallAccuracy = overallAccuracy,
            ChunkResults = regeneratedChunks,
            TotalDuration = stopwatch.Elapsed
        };
    }

    /// <summary>
    /// Gets the status of an active pipeline execution.
    /// </summary>
    /// <param name="executionId">The execution ID.</param>
    /// <returns>Execution status, or null if not found.</returns>
    public PipelineExecution? GetExecutionStatus(string executionId)
    {
        return _activeExecutions.GetValueOrDefault(executionId);
    }

    private async Task<StrategySelectionResult> SelectStrategiesAsync(
        EncodedContext context,
        RegenerationOptions options,
        CancellationToken ct)
    {
        var strategies = new List<IAdvancedRegenerationStrategy>();
        var assessments = await _registry.AssessAllStrategiesAsync(context, ct);

        foreach (var (strategy, capability) in assessments)
        {
            if (capability.ExpectedAccuracy >= options.MinAccuracy * 0.9) // Allow slightly below threshold
            {
                strategies.Add(strategy);

                if (strategies.Count >= _config.MaxStrategiesPerExecution)
                    break;
            }
        }

        return new StrategySelectionResult
        {
            Strategies = strategies,
            DetectedFormat = assessments.FirstOrDefault().Capability.DetectedContentType ?? "unknown"
        };
    }

    private async Task<List<RegenerationResult>> ExecuteRegenerationPassesAsync(
        EncodedContext context,
        IReadOnlyList<IAdvancedRegenerationStrategy> strategies,
        RegenerationOptions options,
        PipelineExecution execution,
        CancellationToken ct)
    {
        var results = new List<RegenerationResult>();
        var passStopwatch = Stopwatch.StartNew();

        foreach (var strategy in strategies)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var result = await strategy.RegenerateAsync(context, options, ct);
                result = result with { StrategyId = strategy.StrategyId };
                results.Add(result);

                // Early exit if we found a perfect result
                if (result.Success && result.ActualAccuracy >= 0.9999999)
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                results.Add(new RegenerationResult
                {
                    Success = false,
                    StrategyId = strategy.StrategyId,
                    Warnings = new List<string> { ex.Message }
                });
            }
        }

        execution.AddPhase("RegenerationPasses", results.Count(r => r.Success), passStopwatch.Elapsed);
        return results;
    }

    private async Task<List<VerifiedResult>> VerifyAndRefineAsync(
        EncodedContext context,
        List<RegenerationResult> results,
        RegenerationOptions options,
        PipelineExecution execution,
        CancellationToken ct)
    {
        var verifiedResults = new List<VerifiedResult>();
        var verifyStopwatch = Stopwatch.StartNew();

        foreach (var result in results.Where(r => r.Success))
        {
            ct.ThrowIfCancellationRequested();

            var verification = await _verifier.VerifyAsync(context, result, ct);

            var verifiedResult = new VerifiedResult
            {
                StrategyId = result.StrategyId,
                Result = result,
                Verification = verification
            };

            // Attempt refinement if below threshold but close
            if (verification.OverallScore < options.MinAccuracy &&
                verification.OverallScore >= options.MinAccuracy * 0.95 &&
                _config.EnableRefinement)
            {
                var refined = await AttemptRefinementAsync(context, result, verification, ct);
                if (refined != null)
                {
                    verifiedResult = refined;
                }
            }

            verifiedResults.Add(verifiedResult);
        }

        execution.AddPhase("Verification", verifiedResults.Count, verifyStopwatch.Elapsed);
        return verifiedResults;
    }

    private async Task<VerifiedResult?> AttemptRefinementAsync(
        EncodedContext context,
        RegenerationResult result,
        VerificationResult verification,
        CancellationToken ct)
    {
        // Try to refine based on verification issues
        var strategy = _registry.GetStrategy(result.StrategyId);
        if (strategy == null) return null;

        // Create refined options based on issues
        var refinedOptions = new RegenerationOptions
        {
            EnableSemanticVerification = true,
            EnableStructuralVerification = true,
            MaxPasses = 3,
            MinAccuracy = 0.9999999
        };

        try
        {
            var refinedResult = await strategy.RegenerateAsync(context, refinedOptions, ct);
            if (refinedResult.Success && refinedResult.ActualAccuracy > result.ActualAccuracy)
            {
                var refinedVerification = await _verifier.VerifyAsync(context, refinedResult, ct);
                if (refinedVerification.OverallScore > verification.OverallScore)
                {
                    return new VerifiedResult
                    {
                        StrategyId = result.StrategyId,
                        Result = refinedResult,
                        Verification = refinedVerification,
                        WasRefined = true
                    };
                }
            }
        }
        catch
        {
            // Refinement failed, return original
        }

        return null;
    }

    private RegenerationResult? SelectBestResult(List<VerifiedResult> results)
    {
        var best = results
            .Where(r => r.Verification.OverallScore >= _config.MinimumAcceptableAccuracy)
            .OrderByDescending(r => r.Verification.OverallScore)
            .ThenByDescending(r => r.Result.ActualAccuracy)
            .FirstOrDefault();

        return best?.Result;
    }

    private async Task<RegenerationResult?> PerformFinalValidationAsync(
        EncodedContext context,
        RegenerationResult result,
        PipelineExecution execution,
        CancellationToken ct)
    {
        var validationStopwatch = Stopwatch.StartNew();

        var finalVerification = await _verifier.VerifyWithCrossCheckAsync(
            context, result, ct);

        execution.AddPhase("FinalValidation", 1, validationStopwatch.Elapsed);

        if (finalVerification.OverallScore >= _config.MinimumAcceptableAccuracy)
        {
            return result with { ActualAccuracy = finalVerification.OverallScore };
        }

        return null;
    }

    private (double Score, string Data) CalculateConsensus(List<VerifiedResult> results)
    {
        if (results.Count < 2)
        {
            var single = results.FirstOrDefault();
            return (single?.Verification.OverallScore ?? 0, single?.Result.RegeneratedContent ?? "");
        }

        // Find common elements across results
        var dataVotes = new Dictionary<string, int>();
        foreach (var result in results)
        {
            var data = result.Result.RegeneratedContent;
            dataVotes[data] = dataVotes.GetValueOrDefault(data, 0) + 1;
        }

        var consensusData = dataVotes
            .OrderByDescending(kv => kv.Value)
            .First();

        var consensusScore = (double)consensusData.Value / results.Count;

        return (consensusScore, consensusData.Key);
    }

    private List<EncodedContext> ChunkContext(EncodedContext context, int chunkSize)
    {
        var data = context.EncodedData;
        var chunks = new List<EncodedContext>();

        for (int i = 0; i < data.Length; i += chunkSize)
        {
            var length = Math.Min(chunkSize, data.Length - i);
            var chunkData = data.Substring(i, length);

            chunks.Add(new EncodedContext
            {
                EncodedData = chunkData,
                EncodingMethod = context.EncodingMethod,
                IsSemantic = context.IsSemantic,
                OriginalSize = length,
                EncodedSize = length
            });
        }

        return chunks;
    }

    private string CombineChunks(List<ChunkResult> chunks)
    {
        return string.Join("", chunks
            .OrderBy(c => c.ChunkIndex)
            .Where(c => c.Success)
            .Select(c => c.Data));
    }

    private PipelineResult CreateFailureResult(string executionId, string error, TimeSpan duration)
    {
        return new PipelineResult
        {
            ExecutionId = executionId,
            Success = false,
            ErrorMessage = error,
            TotalDuration = duration,
            AllResults = new List<VerifiedResult>()
        };
    }
}

/// <summary>
/// Configuration for the regeneration pipeline.
/// </summary>
public sealed record PipelineConfiguration
{
    /// <summary>Maximum strategies to attempt per execution.</summary>
    public int MaxStrategiesPerExecution { get; init; } = 5;

    /// <summary>Minimum acceptable accuracy for results.</summary>
    public double MinimumAcceptableAccuracy { get; init; } = 0.9999999;

    /// <summary>Enable refinement passes for close-miss results.</summary>
    public bool EnableRefinement { get; init; } = true;

    /// <summary>Enable final validation step.</summary>
    public bool EnableFinalValidation { get; init; } = true;

    /// <summary>Timeout for individual strategy execution.</summary>
    public TimeSpan StrategyTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Enable parallel strategy execution.</summary>
    public bool EnableParallelExecution { get; init; } = false;
}

/// <summary>
/// Result from pipeline execution.
/// </summary>
public sealed record PipelineResult
{
    /// <summary>Unique execution identifier.</summary>
    public string ExecutionId { get; init; } = "";

    /// <summary>Whether the pipeline succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>The best regeneration result.</summary>
    public RegenerationResult? Result { get; init; }

    /// <summary>All verified results from the pipeline.</summary>
    public List<VerifiedResult> AllResults { get; init; } = new();

    /// <summary>Total pipeline duration.</summary>
    public TimeSpan TotalDuration { get; init; }

    /// <summary>Phases executed in the pipeline.</summary>
    public List<PipelinePhase> Phases { get; init; } = new();

    /// <summary>Number of strategies attempted.</summary>
    public int StrategiesAttempted { get; init; }

    /// <summary>Error message if failed.</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Result from ensemble execution.
/// </summary>
public sealed record EnsembleResult
{
    /// <summary>Whether the ensemble succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>The best result from the ensemble.</summary>
    public VerifiedResult? BestResult { get; init; }

    /// <summary>All results from participating strategies.</summary>
    public List<VerifiedResult> AllResults { get; init; } = new();

    /// <summary>Consensus score across strategies.</summary>
    public double ConsensusScore { get; init; }

    /// <summary>Consensus data (most agreed upon).</summary>
    public string ConsensusData { get; init; } = "";

    /// <summary>Total execution duration.</summary>
    public TimeSpan TotalDuration { get; init; }

    /// <summary>Number of strategies that succeeded.</summary>
    public int StrategiesSucceeded { get; init; }

    /// <summary>Error message if failed.</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Result from incremental regeneration.
/// </summary>
public sealed record IncrementalResult
{
    /// <summary>Whether all chunks succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Total number of chunks.</summary>
    public int TotalChunks { get; init; }

    /// <summary>Number of successful chunks.</summary>
    public int SuccessfulChunks { get; init; }

    /// <summary>Indices of failed chunks.</summary>
    public List<int> FailedChunkIndices { get; init; } = new();

    /// <summary>Combined regenerated data.</summary>
    public string CombinedData { get; init; } = "";

    /// <summary>Overall accuracy across chunks.</summary>
    public double OverallAccuracy { get; init; }

    /// <summary>Individual chunk results.</summary>
    public List<ChunkResult> ChunkResults { get; init; } = new();

    /// <summary>Total execution duration.</summary>
    public TimeSpan TotalDuration { get; init; }
}

/// <summary>
/// Result for a single chunk.
/// </summary>
public sealed record ChunkResult
{
    /// <summary>Chunk index.</summary>
    public int ChunkIndex { get; init; }

    /// <summary>Whether the chunk succeeded.</summary>
    public bool Success { get; init; }

    /// <summary>Regenerated data for this chunk.</summary>
    public string Data { get; init; } = "";

    /// <summary>Accuracy for this chunk.</summary>
    public double Accuracy { get; init; }

    /// <summary>Error message if failed.</summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// A verified regeneration result.
/// </summary>
public sealed record VerifiedResult
{
    /// <summary>Strategy that produced the result.</summary>
    public string StrategyId { get; init; } = "";

    /// <summary>The regeneration result.</summary>
    public RegenerationResult Result { get; init; } = new();

    /// <summary>Verification details.</summary>
    public VerificationResult Verification { get; init; } = new();

    /// <summary>Whether refinement was applied.</summary>
    public bool WasRefined { get; init; }
}

/// <summary>
/// Strategy selection result.
/// </summary>
internal sealed record StrategySelectionResult
{
    public List<IAdvancedRegenerationStrategy> Strategies { get; init; } = new();
    public string DetectedFormat { get; init; } = "";
}

/// <summary>
/// Pipeline execution tracking.
/// </summary>
public sealed class PipelineExecution
{
    /// <summary>Execution ID.</summary>
    public string ExecutionId { get; }

    /// <summary>Current status.</summary>
    public PipelineStatus Status { get; set; } = PipelineStatus.Pending;

    /// <summary>Start time.</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>Completion time.</summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>Error message if failed.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Execution phases.</summary>
    public List<PipelinePhase> Phases { get; } = new();

    /// <summary>
    /// Initializes a new execution tracker.
    /// </summary>
    /// <param name="executionId">The execution ID.</param>
    public PipelineExecution(string executionId)
    {
        ExecutionId = executionId;
    }

    /// <summary>
    /// Adds a phase to the execution.
    /// </summary>
    public void AddPhase(string name, int itemsProcessed, TimeSpan duration)
    {
        Phases.Add(new PipelinePhase
        {
            Name = name,
            ItemsProcessed = itemsProcessed,
            Duration = duration,
            CompletedAt = DateTime.UtcNow
        });
    }
}

/// <summary>
/// A phase in pipeline execution.
/// </summary>
public sealed record PipelinePhase
{
    /// <summary>Phase name.</summary>
    public string Name { get; init; } = "";

    /// <summary>Items processed in this phase.</summary>
    public int ItemsProcessed { get; init; }

    /// <summary>Phase duration.</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Completion time.</summary>
    public DateTime CompletedAt { get; init; }
}

/// <summary>
/// Pipeline execution status.
/// </summary>
public enum PipelineStatus
{
    /// <summary>Pending execution.</summary>
    Pending,

    /// <summary>Currently running.</summary>
    Running,

    /// <summary>Completed successfully.</summary>
    Completed,

    /// <summary>Failed with error.</summary>
    Failed,

    /// <summary>Cancelled by user.</summary>
    Cancelled
}
