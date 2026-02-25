using DataWarehouse.Kernel.Messaging;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Pipeline;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Utilities;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.Kernel.Pipeline;

/// <summary>
/// Enhanced pipeline orchestrator that implements T126 Phase B requirements:
/// - B1: Accepts PipelinePolicy from IPipelineConfigProvider to resolve per-operation config
/// - B3: Universal enforcement — ALL operations route through pipeline, even if no stages are enabled
/// - B4: After each stage executes, record PipelineStageSnapshot to PipelineContext
/// - B5: On read, use per-blob PipelineStageSnapshot[] from manifest to reconstruct reverse pipeline
/// - B6: Compare blob's PolicyVersion with current policy, trigger lazy migration if stale
///
/// This orchestrator uses hierarchical policies (Instance → UserGroup → User → Operation)
/// instead of a single global configuration.
/// </summary>
public sealed class EnhancedPipelineOrchestrator : IPipelineOrchestrator
{
    private readonly IPipelineConfigProvider _configProvider;
    private readonly IPipelineMigrationEngine? _migrationEngine;
    private readonly DefaultMessageBus? _messageBus;
    private readonly ILogger? _logger;
    private readonly PipelinePluginIntegration? _pluginIntegration;
    private readonly IPipelineTransactionFactory? _transactionFactory;
    private readonly BoundedDictionary<string, IDataTransformation> _registeredStages = new BoundedDictionary<string, IDataTransformation>(1000);

    /// <summary>
    /// Creates a new enhanced pipeline orchestrator.
    /// </summary>
    /// <param name="configProvider">Policy configuration provider for resolving effective policies.</param>
    /// <param name="migrationEngine">Optional migration engine for handling policy version changes.</param>
    /// <param name="messageBus">Optional message bus for publishing pipeline events.</param>
    /// <param name="logger">Optional logger for diagnostic output.</param>
    /// <param name="pluginIntegration">Optional plugin integration for terminal resolution.</param>
    /// <param name="transactionFactory">Optional transaction factory for rollback support.</param>
    public EnhancedPipelineOrchestrator(
        IPipelineConfigProvider configProvider,
        IPipelineMigrationEngine? migrationEngine = null,
        DefaultMessageBus? messageBus = null,
        ILogger? logger = null,
        PipelinePluginIntegration? pluginIntegration = null,
        IPipelineTransactionFactory? transactionFactory = null)
    {
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _migrationEngine = migrationEngine;
        _messageBus = messageBus;
        _logger = logger;
        _pluginIntegration = pluginIntegration;
        _transactionFactory = transactionFactory;
    }

    /// <summary>
    /// Gets the current pipeline configuration.
    /// Note: For policy-based orchestrator, this returns the Instance-level default policy.
    /// </summary>
    public PipelineConfiguration GetConfiguration()
    {
        // For backward compatibility, return the Instance-level policy as a PipelineConfiguration
        var instancePolicy = _configProvider.GetPolicyAsync(PolicyLevel.Instance, "default").Result;

        if (instancePolicy == null)
        {
            return PipelineConfiguration.CreateDefault();
        }

        return ConvertPolicyToConfiguration(instancePolicy);
    }

    /// <summary>
    /// Sets a new pipeline configuration.
    /// Note: For policy-based orchestrator, this updates the Instance-level policy.
    /// </summary>
    public void SetConfiguration(PipelineConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var validation = ValidateConfiguration(config);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                $"Invalid pipeline configuration: {string.Join(", ", validation.Errors)}");
        }

        // Convert to policy and update Instance level
        var policy = ConvertConfigurationToPolicy(config);
        // SetConfiguration() is a synchronous interface. Task.Run avoids deadlocks on
        // synchronization-context-bound threads.
        Task.Run(() => _configProvider.SetPolicyAsync(policy)).ConfigureAwait(false).GetAwaiter().GetResult();

        _logger?.LogInformation("Instance-level pipeline policy updated: {Name}", config.Name);
    }

    /// <summary>
    /// Resets to default pipeline configuration.
    /// </summary>
    public void ResetToDefaults()
    {
        SetConfiguration(PipelineConfiguration.CreateDefault());
        _logger?.LogInformation("Pipeline configuration reset to defaults");
    }

    /// <summary>
    /// Executes the transit pipeline for data being sent over the network.
    /// Transit pipeline order: TransitCompress → TransitEncrypt
    /// </summary>
    /// <param name="input">Input stream to be transit-processed.</param>
    /// <param name="context">Pipeline context for configuration resolution.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Processed stream ready for network transmission.</returns>
    public async Task<Stream> ExecuteTransitWritePipelineAsync(
        Stream input,
        PipelineContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        ct.ThrowIfCancellationRequested();

        var securityContext = context.SecurityContext ?? AnonymousSecurityContext.Instance;

        // Resolve effective policy for transit stages
        var effectivePolicy = await _configProvider.ResolveEffectivePolicyAsync(
            userId: securityContext.UserId,
            groupId: securityContext.TenantId,
            operationId: context.Parameters.TryGetValue("OperationId", out var opId) ? opId?.ToString() : null,
            ct: ct);

        // Filter for transit-specific stages
        var transitStages = effectivePolicy.Stages
            .Where(s => (s.Enabled ?? true) &&
                       (s.StageType.Equals("TransitCompression", StringComparison.OrdinalIgnoreCase) ||
                        s.StageType.Equals("TransitEncryption", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(s => s.Order ?? 100)
            .ToList();

        _logger?.LogDebug("Executing transit write pipeline with {Count} stages", transitStages.Count);

        var currentStream = input;
        var intermediateStreams = new List<Stream>();

        try
        {
            foreach (var stagePolicy in transitStages)
            {
                ct.ThrowIfCancellationRequested();

                var stage = FindStage(stagePolicy);
                if (stage == null)
                {
                    _logger?.LogWarning("Transit stage plugin not found: {StageType}", stagePolicy.StageType);
                    continue;
                }

                if (currentStream.CanSeek)
                    currentStream.Position = 0;

                var previousStream = currentStream;
                var kernelContext = context.KernelContext ?? CreateDefaultKernelContext();

                currentStream = stage.OnWrite(currentStream, kernelContext, stagePolicy.Parameters ?? new Dictionary<string, object>());

                if (previousStream != input && !intermediateStreams.Contains(previousStream))
                    intermediateStreams.Add(previousStream);
            }

            if (currentStream.CanSeek)
                currentStream.Position = 0;

            context.IntermediateStreams = intermediateStreams;
            return currentStream;
        }
        catch (Exception ex)
        {
            foreach (var stream in intermediateStreams)
            {
                try { stream.Dispose(); } catch { /* Ignore disposal errors */ }
            }

            _logger?.LogError(ex, "Transit write pipeline execution failed");
            throw;
        }
    }

    /// <summary>
    /// Executes the reverse transit pipeline for data received from the network.
    /// Transit pipeline order: TransitDecrypt → TransitDecompress
    /// </summary>
    /// <param name="input">Input stream received from network.</param>
    /// <param name="context">Pipeline context for configuration resolution.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Deprocessed stream ready for storage or use.</returns>
    public async Task<Stream> ExecuteTransitReadPipelineAsync(
        Stream input,
        PipelineContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        ct.ThrowIfCancellationRequested();

        var securityContext = context.SecurityContext ?? AnonymousSecurityContext.Instance;

        // Resolve effective policy for transit stages
        var effectivePolicy = await _configProvider.ResolveEffectivePolicyAsync(
            userId: securityContext.UserId,
            groupId: securityContext.TenantId,
            operationId: context.Parameters.TryGetValue("OperationId", out var opId) ? opId?.ToString() : null,
            ct: ct);

        // Filter for transit-specific stages and reverse order
        var transitStages = effectivePolicy.Stages
            .Where(s => (s.Enabled ?? true) &&
                       (s.StageType.Equals("TransitCompression", StringComparison.OrdinalIgnoreCase) ||
                        s.StageType.Equals("TransitEncryption", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(s => s.Order ?? 100)
            .ToList();

        _logger?.LogDebug("Executing transit read pipeline with {Count} stages (reversed)", transitStages.Count);

        var currentStream = input;
        var intermediateStreams = new List<Stream>();

        try
        {
            foreach (var stagePolicy in transitStages)
            {
                ct.ThrowIfCancellationRequested();

                var stage = FindStage(stagePolicy);
                if (stage == null)
                {
                    _logger?.LogWarning("Transit stage plugin not found: {StageType}", stagePolicy.StageType);
                    continue;
                }

                if (currentStream.CanSeek)
                    currentStream.Position = 0;

                var previousStream = currentStream;
                var kernelContext = context.KernelContext ?? CreateDefaultKernelContext();

                currentStream = stage.OnRead(currentStream, kernelContext, stagePolicy.Parameters ?? new Dictionary<string, object>());

                if (previousStream != input && !intermediateStreams.Contains(previousStream))
                    intermediateStreams.Add(previousStream);
            }

            if (currentStream.CanSeek)
                currentStream.Position = 0;

            context.IntermediateStreams = intermediateStreams;
            return currentStream;
        }
        catch (Exception ex)
        {
            foreach (var stream in intermediateStreams)
            {
                try { stream.Dispose(); } catch { /* Ignore disposal errors */ }
            }

            _logger?.LogError(ex, "Transit read pipeline execution failed");
            throw;
        }
    }

    /// <summary>
    /// Executes the write pipeline (user data → storage).
    /// B3: Universal enforcement — ALL operations route through pipeline.
    /// B4: Records PipelineStageSnapshot after each stage execution.
    /// </summary>
    public async Task<Stream> ExecuteWritePipelineAsync(
        Stream input,
        PipelineContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        ct.ThrowIfCancellationRequested();

        var securityContext = context.SecurityContext ?? AnonymousSecurityContext.Instance;

        // B1: Resolve effective policy for this operation
        var effectivePolicy = await _configProvider.ResolveEffectivePolicyAsync(
            userId: securityContext.UserId,
            groupId: securityContext.TenantId,
            operationId: context.Parameters.TryGetValue("OperationId", out var opId) ? opId?.ToString() : null,
            ct: ct);

        _logger?.LogDebug("Resolved effective pipeline policy: {PolicyName} (Version: {Version})",
            effectivePolicy.Name, effectivePolicy.Version);

        // B3: Universal enforcement — build ordered stage list (may be empty)
        var orderedStages = BuildOrderedStages(effectivePolicy);

        _logger?.LogInformation(
            "Executing write pipeline with {Count} stages (User: {UserId}, Policy: {PolicyId}, Version: {Version})",
            orderedStages.Count, securityContext.UserId, effectivePolicy.PolicyId, effectivePolicy.Version);

        // Publish pipeline start event
        if (_messageBus != null)
        {
            await _messageBus.PublishAsync(MessageTopics.PipelineExecute, new PluginMessage
            {
                Type = "pipeline.write.start",
                Payload = new Dictionary<string, object>
                {
                    ["StageCount"] = orderedStages.Count,
                    ["UserId"] = securityContext.UserId,
                    ["PolicyId"] = effectivePolicy.PolicyId,
                    ["PolicyVersion"] = effectivePolicy.Version
                }
            }, ct);
        }

        var currentStream = input;
        var intermediateStreams = new List<Stream>();
        var executedSnapshots = new List<PipelineStageSnapshot>();

        try
        {
            // B4: Execute each stage and record snapshot
            foreach (var stagePolicy in orderedStages)
            {
                ct.ThrowIfCancellationRequested();

                // Find registered stage
                var stage = FindStage(stagePolicy);
                if (stage == null)
                {
                    _logger?.LogWarning("Stage plugin not found: {StageType} (PluginId: {PluginId})",
                        stagePolicy.StageType, stagePolicy.PluginId ?? "auto");
                    continue;
                }

                _logger?.LogDebug("Executing stage: {StageType} ({PluginId})",
                    stagePolicy.StageType, stagePolicy.PluginId ?? "auto");

                // Ensure stream is at beginning
                if (currentStream.CanSeek)
                {
                    currentStream.Position = 0;
                }

                var previousStream = currentStream;
                var kernelContext = context.KernelContext ?? CreateDefaultKernelContext();

                // Execute stage transformation
                currentStream = stage.OnWrite(
                    currentStream,
                    kernelContext,
                    stagePolicy.Parameters ?? new Dictionary<string, object>());

                // Track intermediate streams
                if (previousStream != input && !intermediateStreams.Contains(previousStream))
                {
                    intermediateStreams.Add(previousStream);
                }

                // B4: Record PipelineStageSnapshot
                var snapshot = new PipelineStageSnapshot
                {
                    StageType = stagePolicy.StageType,
                    PluginId = stagePolicy.PluginId ?? GetPluginId(stage),
                    StrategyName = stagePolicy.StrategyName ?? GetStrategyName(stage),
                    Order = stagePolicy.Order ?? 100,
                    Parameters = stagePolicy.Parameters ?? new Dictionary<string, object>(),
                    ExecutedAt = DateTimeOffset.UtcNow,
                    PluginVersion = GetPluginVersion(stage)
                };

                executedSnapshots.Add(snapshot);
                context.ExecutedStages.Add(stagePolicy.StageType);
            }

            // Ensure final stream position is at beginning
            if (currentStream.CanSeek)
            {
                currentStream.Position = 0;
            }

            // B4: Store snapshots in PipelineContext and Manifest
            if (context.Manifest != null)
            {
                context.Manifest.Pipeline.ExecutedStages = executedSnapshots;
                context.Manifest.Pipeline.PolicyId = effectivePolicy.PolicyId;
                context.Manifest.Pipeline.PolicyVersion = effectivePolicy.Version;
                context.Manifest.Pipeline.WrittenAt = DateTimeOffset.UtcNow;
            }

            // Publish completion event
            if (_messageBus != null)
            {
                await _messageBus.PublishAsync(MessageTopics.PipelineCompleted, new PluginMessage
                {
                    Type = "pipeline.write.complete",
                    Payload = new Dictionary<string, object>
                    {
                        ["ExecutedStages"] = context.ExecutedStages.ToArray(),
                        ["PolicyId"] = effectivePolicy.PolicyId,
                        ["PolicyVersion"] = effectivePolicy.Version
                    }
                }, ct);
            }

            // Store intermediate streams in context for caller cleanup
            context.IntermediateStreams = intermediateStreams;

            return currentStream;
        }
        catch (Exception ex)
        {
            // Dispose intermediate streams on error
            foreach (var stream in intermediateStreams)
            {
                try { stream.Dispose(); } catch { /* Ignore disposal errors */ }
            }

            _logger?.LogError(ex, "Pipeline write execution failed");

            if (_messageBus != null)
            {
                await _messageBus.PublishAsync(MessageTopics.PipelineError, new PluginMessage
                {
                    Type = "pipeline.write.error",
                    Payload = new Dictionary<string, object>
                    {
                        ["Error"] = ex.Message,
                        ["ExecutedStages"] = context.ExecutedStages.ToArray()
                    }
                }, ct);
            }

            throw;
        }
    }

    /// <summary>
    /// Executes the read pipeline (storage → user data).
    /// B5: Uses per-blob PipelineStageSnapshot[] from manifest to reconstruct exact reverse pipeline.
    /// B6: Compares blob's PolicyVersion with current policy, triggers lazy migration if stale.
    /// </summary>
    public async Task<Stream> ExecuteReadPipelineAsync(
        Stream input,
        PipelineContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        ct.ThrowIfCancellationRequested();

        var securityContext = context.SecurityContext ?? AnonymousSecurityContext.Instance;

        // B5: Read executed stages from manifest
        List<PipelineStageSnapshot>? executedStages = null;
        long blobPolicyVersion = 0;
        string? blobPolicyId = null;

        if (context.Manifest?.Pipeline != null)
        {
            executedStages = context.Manifest.Pipeline.ExecutedStages;
            blobPolicyVersion = context.Manifest.Pipeline.PolicyVersion;
            blobPolicyId = context.Manifest.Pipeline.PolicyId;
        }

        // B6: Check if blob policy is stale
        if (!string.IsNullOrEmpty(blobPolicyId))
        {
            var currentPolicy = await _configProvider.ResolveEffectivePolicyAsync(
                userId: securityContext.UserId,
                groupId: securityContext.TenantId,
                ct: ct);

            if (blobPolicyVersion < currentPolicy.Version)
            {
                _logger?.LogInformation(
                    "Blob policy is stale (Version: {BlobVersion}, Current: {CurrentVersion}). " +
                    "Migration behavior: {Behavior}",
                    blobPolicyVersion, currentPolicy.Version, currentPolicy.MigrationBehavior);

                // B6: Trigger lazy migration if configured
                if (currentPolicy.MigrationBehavior == MigrationBehavior.MigrateOnNextAccess && _migrationEngine != null)
                {
                    _logger?.LogInformation("Triggering lazy migration on blob access");

                    try
                    {
                        var migratedStream = await _migrationEngine.MigrateOnAccessAsync(
                            input,
                            executedStages?.ToArray() ?? Array.Empty<PipelineStageSnapshot>(),
                            currentPolicy,
                            ct);

                        return migratedStream;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Lazy migration failed, falling back to normal read pipeline");
                        // Fall through to normal read pipeline
                    }
                }
            }
        }

        // B5: Use ExecutedStages from manifest if available, otherwise fall back to current policy
        List<PipelineStageSnapshot> stagesToReverse;

        if (executedStages != null && executedStages.Count > 0)
        {
            _logger?.LogDebug("Using {Count} executed stages from manifest for reverse pipeline", executedStages.Count);
            stagesToReverse = executedStages;
        }
        else
        {
            // Backward compatibility: if no ExecutedStages, use current policy
            _logger?.LogDebug("No executed stages in manifest, falling back to current policy");

            var currentPolicy = await _configProvider.ResolveEffectivePolicyAsync(
                userId: securityContext.UserId,
                groupId: securityContext.TenantId,
                ct: ct);

            var orderedStages = BuildOrderedStages(currentPolicy);

            // Convert to snapshots (best effort)
            stagesToReverse = orderedStages.Select(sp => new PipelineStageSnapshot
            {
                StageType = sp.StageType,
                PluginId = sp.PluginId ?? "auto",
                StrategyName = sp.StrategyName ?? "default",
                Order = sp.Order ?? 100,
                Parameters = sp.Parameters ?? new Dictionary<string, object>(),
                ExecutedAt = DateTimeOffset.UtcNow
            }).ToList();
        }

        // Reverse the stages for read pipeline
        var reversedStages = stagesToReverse.OrderByDescending(s => s.Order).ToList();

        _logger?.LogInformation("Executing read pipeline with {Count} stages (reversed)",
            reversedStages.Count);

        // Publish pipeline start event
        if (_messageBus != null)
        {
            await _messageBus.PublishAsync(MessageTopics.PipelineExecute, new PluginMessage
            {
                Type = "pipeline.read.start",
                Payload = new Dictionary<string, object>
                {
                    ["StageCount"] = reversedStages.Count,
                    ["UserId"] = securityContext.UserId,
                    ["BlobPolicyVersion"] = blobPolicyVersion
                }
            }, ct);
        }

        var currentStream = input;
        var intermediateStreams = new List<Stream>();

        try
        {
            foreach (var snapshot in reversedStages)
            {
                ct.ThrowIfCancellationRequested();

                // Find stage by snapshot
                var stage = FindStageBySnapshot(snapshot);
                if (stage == null)
                {
                    _logger?.LogWarning("Stage plugin not found for reverse operation: {StageType} ({PluginId})",
                        snapshot.StageType, snapshot.PluginId);
                    continue;
                }

                _logger?.LogDebug("Executing reverse stage: {StageType} ({PluginId})",
                    snapshot.StageType, snapshot.PluginId);

                // Ensure stream is at beginning
                if (currentStream.CanSeek)
                {
                    currentStream.Position = 0;
                }

                var previousStream = currentStream;
                var kernelContext = context.KernelContext ?? CreateDefaultKernelContext();

                // Execute reverse transformation
                currentStream = stage.OnRead(currentStream, kernelContext, snapshot.Parameters);

                // Track intermediate streams
                if (previousStream != input && !intermediateStreams.Contains(previousStream))
                {
                    intermediateStreams.Add(previousStream);
                }

                context.ExecutedStages.Add(snapshot.StageType);
            }

            // Ensure final stream position is at beginning
            if (currentStream.CanSeek)
            {
                currentStream.Position = 0;
            }

            // Publish completion event
            if (_messageBus != null)
            {
                await _messageBus.PublishAsync(MessageTopics.PipelineCompleted, new PluginMessage
                {
                    Type = "pipeline.read.complete",
                    Payload = new Dictionary<string, object>
                    {
                        ["ExecutedStages"] = context.ExecutedStages.ToArray()
                    }
                }, ct);
            }

            // Store intermediate streams in context for caller cleanup
            context.IntermediateStreams = intermediateStreams;

            return currentStream;
        }
        catch (Exception ex)
        {
            // Dispose intermediate streams on error
            foreach (var stream in intermediateStreams)
            {
                try { stream.Dispose(); } catch { /* Ignore disposal errors */ }
            }

            _logger?.LogError(ex, "Pipeline read execution failed");

            if (_messageBus != null)
            {
                await _messageBus.PublishAsync(MessageTopics.PipelineError, new PluginMessage
                {
                    Type = "pipeline.read.error",
                    Payload = new Dictionary<string, object>
                    {
                        ["Error"] = ex.Message,
                        ["ExecutedStages"] = context.ExecutedStages.ToArray()
                    }
                }, ct);
            }

            throw;
        }
    }

    /// <summary>
    /// Registers a pipeline stage plugin.
    /// </summary>
    public void RegisterStage(IDataTransformation stage)
    {
        ArgumentNullException.ThrowIfNull(stage);

        var plugin = stage as IPlugin;
        var stageId = plugin?.Id ?? stage.GetType().Name;

        _registeredStages[stageId] = stage;
        _logger?.LogDebug("Pipeline stage registered: {StageId} ({SubCategory})",
            stageId, stage.SubCategory);
    }

    /// <summary>
    /// Unregisters a pipeline stage.
    /// </summary>
    public void UnregisterStage(string stageId)
    {
        if (_registeredStages.TryRemove(stageId, out _))
        {
            _logger?.LogDebug("Pipeline stage unregistered: {StageId}", stageId);
        }
    }

    /// <summary>
    /// Gets all registered pipeline stages.
    /// </summary>
    public IEnumerable<PipelineStageInfo> GetRegisteredStages()
    {
        return _registeredStages.Select(kvp =>
        {
            var plugin = kvp.Value as IPlugin;
            var pipelinePlugin = kvp.Value as DataWarehouse.SDK.Contracts.Hierarchy.DataPipelinePluginBase;

            return new PipelineStageInfo
            {
                PluginId = kvp.Key,
                Name = plugin?.Name ?? kvp.Key,
                SubCategory = kvp.Value.SubCategory,
                QualityLevel = kvp.Value.QualityLevel,
                DefaultOrder = pipelinePlugin?.DefaultPipelineOrder ?? 100,
                AllowBypass = pipelinePlugin?.AllowBypass ?? false,
                RequiredPrecedingStages = pipelinePlugin?.RequiredPrecedingStages?.ToArray() ?? Array.Empty<string>(),
                IncompatibleStages = pipelinePlugin?.IncompatibleStages?.ToArray() ?? Array.Empty<string>(),
                Description = plugin?.Name ?? "Pipeline stage"
            };
        }).ToList();
    }

    /// <summary>
    /// Executes the full write pipeline including terminal stages (storage).
    /// This is the unified entry point that handles: Transform → Compress → Encrypt → Store
    /// Uses transactions with automatic rollback on critical failures.
    /// </summary>
    public async Task<PipelineStorageResult> ExecuteWritePipelineWithStorageAsync(
        Stream input,
        PipelineContext context,
        CancellationToken ct = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Create transaction for unified rollback
        var blobId = context.Parameters.TryGetValue("BlobId", out var blobIdValue) ? blobIdValue?.ToString() : null;
        var transaction = _transactionFactory?.Create(
            transactionId: context.Parameters.TryGetValue("TransactionId", out var txId) ? txId?.ToString() : null,
            blobId: blobId,
            kernelContext: context.KernelContext);

        try
        {
            // Phase 1: Execute transformation stages (compress, encrypt) with transaction tracking
            var transformedStream = await ExecuteWritePipelineWithTransactionAsync(input, context, transaction, ct);

            // Phase 2: Execute terminal stages (storage fan-out) with transaction tracking
            var effectivePolicy = await ResolveEffectivePolicyAsync(context, ct);
            var terminalResults = await ExecuteTerminalsWithTransactionAsync(
                transformedStream, effectivePolicy, context, transaction, ct);

            // Check if any critical terminal failed
            var criticalFailure = terminalResults.FirstOrDefault(r =>
                !r.Success && IsCriticalTerminal(r.TerminalType));

            if (criticalFailure != null)
            {
                // Critical terminal failed - rollback everything
                if (transaction != null)
                {
                    transaction.MarkFailed(criticalFailure.Exception);
                    var rollbackResult = await transaction.RollbackAsync(ct);

                    _logger?.LogWarning(
                        "Pipeline transaction rolled back due to critical terminal failure: {TerminalType}. Rollback success: {Success}",
                        criticalFailure.TerminalType, rollbackResult.Success);
                }

                throw new PipelineTransactionException(
                    $"Critical terminal '{criticalFailure.TerminalType}' failed: {criticalFailure.ErrorMessage}",
                    criticalFailure.Exception,
                    terminalResults);
            }

            // All critical operations succeeded - commit transaction
            if (transaction != null)
            {
                await transaction.CommitAsync(ct);
            }

            stopwatch.Stop();

            return new PipelineStorageResult
            {
                TerminalResults = terminalResults,
                TotalDuration = stopwatch.Elapsed,
                Manifest = context.Manifest,
                TransactionId = transaction?.TransactionId
            };
        }
        catch (PipelineTransactionException)
        {
            throw; // Already handled
        }
        catch (Exception ex)
        {
            // Unexpected failure - rollback
            if (transaction != null)
            {
                transaction.MarkFailed(ex);
                await transaction.RollbackAsync(ct);
            }
            throw;
        }
        finally
        {
            if (transaction != null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// Executes write pipeline stages with transaction tracking for rollback support.
    /// </summary>
    private async Task<Stream> ExecuteWritePipelineWithTransactionAsync(
        Stream input,
        PipelineContext context,
        IPipelineTransaction? transaction,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        ct.ThrowIfCancellationRequested();

        var securityContext = context.SecurityContext ?? AnonymousSecurityContext.Instance;
        var effectivePolicy = await ResolveEffectivePolicyAsync(context, ct);

        // Get ordered stages from policy (exclude terminal stages)
        var orderedStages = effectivePolicy.Stages
            .Where(s => s.Enabled ?? true)
            .Where(s => !IsTerminalStageType(s.StageType))
            .OrderBy(s => s.Order ?? 100)
            .ToList();

        _logger?.LogInformation(
            "Executing write pipeline with {Count} stages (Transaction: {TxId})",
            orderedStages.Count, transaction?.TransactionId ?? "none");

        var currentStream = input;
        var intermediateStreams = new List<Stream>();

        try
        {
            int order = 0;
            foreach (var stagePolicy in orderedStages)
            {
                ct.ThrowIfCancellationRequested();

                var stage = FindStage(stagePolicy);
                if (stage == null)
                {
                    _logger?.LogWarning("Stage {StageType} not found, skipping", stagePolicy.StageType);
                    continue;
                }

                var stageParams = stagePolicy.Parameters ?? new Dictionary<string, object>();
                var previousStream = currentStream;

                // Execute the stage
                currentStream = stage.OnWrite(currentStream, context.KernelContext!, stageParams);

                // Track intermediate stream for cleanup
                if (previousStream != input && previousStream != currentStream)
                {
                    intermediateStreams.Add(previousStream);
                }

                // Record to transaction for potential rollback
                if (transaction != null)
                {
                    var supportsRollback = stage is IRollbackable rollbackable && rollbackable.SupportsRollback;

                    transaction.RecordStageExecution(new ExecutedStageInfo
                    {
                        StageType = stagePolicy.StageType,
                        PluginId = stagePolicy.PluginId ?? stage.GetType().Name,
                        StrategyName = stagePolicy.StrategyName ?? "default",
                        StageInstance = stage,
                        SupportsRollback = supportsRollback,
                        Parameters = new Dictionary<string, object>(stageParams),
                        Order = order++,
                        ExecutedAt = DateTimeOffset.UtcNow
                    });
                }

                // Record snapshot for manifest
                var snapshot = new PipelineStageSnapshot
                {
                    StageType = stagePolicy.StageType,
                    PluginId = stagePolicy.PluginId ?? GetPluginId(stage),
                    StrategyName = stagePolicy.StrategyName ?? GetStrategyName(stage),
                    Order = stagePolicy.Order ?? 100,
                    Parameters = stageParams,
                    ExecutedAt = DateTimeOffset.UtcNow,
                    PluginVersion = GetPluginVersion(stage)
                };

                context.ExecutedStages.Add(stagePolicy.StageType);

                if (context.Manifest?.Pipeline != null)
                {
                    context.Manifest.Pipeline.ExecutedStages ??= new List<PipelineStageSnapshot>();
                    context.Manifest.Pipeline.ExecutedStages.Add(snapshot);
                }
            }

            // Store intermediate streams in context for caller cleanup
            context.IntermediateStreams = intermediateStreams;

            return currentStream;
        }
        catch
        {
            // Cleanup intermediate streams on failure
            foreach (var stream in intermediateStreams)
            {
                try { stream.Dispose(); } catch { /* Best-effort cleanup */ }
            }
            throw;
        }
    }

    /// <summary>
    /// Executes terminal stages with transaction tracking for rollback support.
    /// </summary>
    private async Task<List<TerminalResult>> ExecuteTerminalsWithTransactionAsync(
        Stream input,
        PipelinePolicy policy,
        PipelineContext context,
        IPipelineTransaction? transaction,
        CancellationToken ct)
    {
        var terminals = policy.Terminals ?? new List<TerminalStagePolicy>();

        if (terminals.Count == 0)
        {
            terminals = new List<TerminalStagePolicy>
            {
                new() { TerminalType = "primary", Enabled = true, FailureIsCritical = true }
            };
        }

        var enabledTerminals = terminals.Where(t => t.Enabled ?? true).ToList();
        if (enabledTerminals.Count == 0)
            return new List<TerminalResult>();

        // Buffer input for fan-out
        byte[] data;
        using (var buffer = new MemoryStream(65536))
        {
            await input.CopyToAsync(buffer, ct);
            data = buffer.ToArray();
        }

        var results = new List<TerminalResult>();

        // Execute parallel terminals
        var parallelTerminals = enabledTerminals
            .Where(t => (t.ExecutionMode ?? TerminalExecutionMode.Parallel) == TerminalExecutionMode.Parallel)
            .ToList();

        if (parallelTerminals.Count > 0)
        {
            var parallelTasks = parallelTerminals.Select(tp =>
                ExecuteSingleTerminalWithTransactionAsync(data, tp, context, transaction, ct));
            var parallelResults = await Task.WhenAll(parallelTasks);
            results.AddRange(parallelResults);
        }

        // Execute sequential terminals
        var sequentialTerminals = enabledTerminals
            .Where(t => (t.ExecutionMode ?? TerminalExecutionMode.Parallel) == TerminalExecutionMode.Sequential)
            .OrderBy(t => t.Priority ?? 100)
            .ToList();

        foreach (var tp in sequentialTerminals)
        {
            var result = await ExecuteSingleTerminalWithTransactionAsync(data, tp, context, transaction, ct);
            results.Add(result);

            if (!result.Success && (tp.FailureIsCritical ?? true))
                break;
        }

        // Execute after-parallel terminals
        var afterParallelTerminals = enabledTerminals
            .Where(t => (t.ExecutionMode ?? TerminalExecutionMode.Parallel) == TerminalExecutionMode.AfterParallel)
            .OrderBy(t => t.Priority ?? 100)
            .ToList();

        foreach (var tp in afterParallelTerminals)
        {
            var result = await ExecuteSingleTerminalWithTransactionAsync(data, tp, context, transaction, ct);
            results.Add(result);

            if (!result.Success && (tp.FailureIsCritical ?? true))
                break;
        }

        return results;
    }

    /// <summary>
    /// Executes a single terminal with transaction tracking.
    /// </summary>
    private async Task<TerminalResult> ExecuteSingleTerminalWithTransactionAsync(
        byte[] data,
        TerminalStagePolicy terminalPolicy,
        PipelineContext context,
        IPipelineTransaction? transaction,
        CancellationToken ct)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var terminal = await ResolveTerminalAsync(terminalPolicy, context, ct);
            if (terminal == null)
            {
                return new TerminalResult
                {
                    TerminalId = terminalPolicy.PluginId ?? "unknown",
                    TerminalType = terminalPolicy.TerminalType,
                    Success = false,
                    ErrorMessage = $"Could not resolve terminal for type '{terminalPolicy.TerminalType}'",
                    Duration = stopwatch.Elapsed
                };
            }

            var terminalContext = BuildTerminalContext(context, terminalPolicy, data.Length);

            using var stream = new MemoryStream(data);
            await terminal.WriteAsync(stream, terminalContext, ct);

            stopwatch.Stop();

            // Record to transaction for potential rollback
            if (transaction != null)
            {
                transaction.RecordTerminalExecution(new ExecutedTerminalInfo
                {
                    TerminalType = terminalPolicy.TerminalType,
                    TerminalId = terminal.TerminalId,
                    TerminalInstance = terminal,
                    StoragePath = terminalContext.StoragePath,
                    BlobId = terminalContext.BlobId,
                    SupportsRollback = true,
                    ExecutedAt = DateTimeOffset.UtcNow
                });
            }

            return new TerminalResult
            {
                TerminalId = terminal.TerminalId,
                TerminalType = terminalPolicy.TerminalType,
                Success = true,
                Duration = stopwatch.Elapsed,
                BytesWritten = data.Length,
                StoragePath = terminalContext.StoragePath
            };
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return new TerminalResult
            {
                TerminalId = terminalPolicy.PluginId ?? "unknown",
                TerminalType = terminalPolicy.TerminalType,
                Success = false,
                ErrorMessage = ex.Message,
                Exception = ex,
                Duration = stopwatch.Elapsed
            };
        }
    }

    /// <summary>
    /// Determines if a terminal type is critical (must succeed for pipeline to succeed).
    /// </summary>
    private static bool IsCriticalTerminal(string terminalType) =>
        terminalType == "primary" || terminalType == "metadata";

    /// <summary>
    /// Determines if a stage type is a terminal stage (storage).
    /// </summary>
    private static bool IsTerminalStageType(string stageType) =>
        stageType.Equals("Storage", StringComparison.OrdinalIgnoreCase) ||
        stageType.Equals("Terminal", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Executes the full read pipeline from storage through reverse transformation.
    /// </summary>
    public async Task<Stream> ExecuteReadPipelineWithStorageAsync(
        TerminalContext terminalContext,
        PipelineContext pipelineContext,
        CancellationToken ct = default)
    {
        // Phase 1: Read from terminal (storage)
        var terminal = await ResolveReadTerminalAsync(pipelineContext, ct);
        if (terminal == null)
            throw new InvalidOperationException("No terminal available for read operation");

        var storedStream = await terminal.ReadAsync(terminalContext, ct);

        // Phase 2: Execute reverse transformation (decrypt, decompress)
        return await ExecuteReadPipelineAsync(storedStream, pipelineContext, ct);
    }

    /// <summary>
    /// Validates a proposed pipeline configuration.
    /// Returns errors if stages are incompatible or missing dependencies.
    /// </summary>
    public PipelineValidationResult ValidateConfiguration(PipelineConfiguration config)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var suggestions = new List<string>();

        if (config.WriteStages.Count == 0)
        {
            warnings.Add("Pipeline has no stages configured - data will pass through unchanged");
        }

        // Check for duplicate stages
        var stageTypes = config.WriteStages.Select(s => s.StageType).ToList();
        var duplicates = stageTypes.GroupBy(s => s).Where(g => g.Count() > 1).Select(g => g.Key);
        foreach (var dup in duplicates)
        {
            warnings.Add($"Duplicate stage type: {dup}");
        }

        // Check if referenced stages exist
        foreach (var stageConfig in config.WriteStages.Where(s => s.PluginId != null))
        {
            if (!_registeredStages.ContainsKey(stageConfig.PluginId!))
            {
                warnings.Add($"Stage plugin not found: {stageConfig.PluginId}");
            }
        }

        return new PipelineValidationResult
        {
            IsValid = errors.Count == 0,
            Errors = errors,
            Warnings = warnings,
            Suggestions = suggestions
        };
    }

    // Private helper methods

    /// <summary>
    /// Builds an ordered list of stages from a pipeline policy.
    /// Uses StageOrder if specified, otherwise orders by Order field.
    /// </summary>
    private List<PipelineStagePolicy> BuildOrderedStages(PipelinePolicy policy)
    {
        var enabledStages = policy.Stages.Where(s => s.Enabled ?? true).ToList();

        if (policy.StageOrder != null && policy.StageOrder.Count > 0)
        {
            // Use explicit stage ordering
            var orderedStages = new List<PipelineStagePolicy>();

            foreach (var stageType in policy.StageOrder)
            {
                var stage = enabledStages.FirstOrDefault(s =>
                    s.StageType.Equals(stageType, StringComparison.OrdinalIgnoreCase));

                if (stage != null)
                {
                    orderedStages.Add(stage);
                }
            }

            return orderedStages;
        }
        else
        {
            // Order by Order field
            return enabledStages.OrderBy(s => s.Order ?? 100).ToList();
        }
    }

    /// <summary>
    /// Finds a registered stage that matches the stage policy.
    /// </summary>
    private IDataTransformation? FindStage(PipelineStagePolicy stagePolicy)
    {
        // Try by explicit plugin ID first
        if (!string.IsNullOrEmpty(stagePolicy.PluginId))
        {
            if (_registeredStages.TryGetValue(stagePolicy.PluginId, out var stage))
            {
                return stage;
            }
        }

        // Fall back to finding by SubCategory
        return _registeredStages.Values
            .FirstOrDefault(s => s.SubCategory.Equals(stagePolicy.StageType, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Finds a registered stage that matches the pipeline snapshot.
    /// </summary>
    private IDataTransformation? FindStageBySnapshot(PipelineStageSnapshot snapshot)
    {
        // Try by plugin ID first
        if (!string.IsNullOrEmpty(snapshot.PluginId) && snapshot.PluginId != "auto")
        {
            if (_registeredStages.TryGetValue(snapshot.PluginId, out var stage))
            {
                return stage;
            }
        }

        // Fall back to finding by SubCategory
        return _registeredStages.Values
            .FirstOrDefault(s => s.SubCategory.Equals(snapshot.StageType, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Converts a PipelinePolicy to a PipelineConfiguration for backward compatibility.
    /// </summary>
    private PipelineConfiguration ConvertPolicyToConfiguration(PipelinePolicy policy)
    {
        return new PipelineConfiguration
        {
            ConfigurationId = policy.PolicyId,
            Name = policy.Name,
            WriteStages = policy.Stages.Select(s => new PipelineStageConfig
            {
                StageType = s.StageType,
                PluginId = s.PluginId,
                Order = s.Order ?? 100,
                Enabled = s.Enabled ?? true,
                Parameters = s.Parameters ?? new Dictionary<string, object>()
            }).ToList(),
            IsDefault = policy.Level == PolicyLevel.Instance,
            Metadata = new Dictionary<string, object>
            {
                ["PolicyVersion"] = policy.Version,
                ["PolicyLevel"] = policy.Level.ToString()
            }
        };
    }

    /// <summary>
    /// Converts a PipelineConfiguration to a PipelinePolicy for backward compatibility.
    /// </summary>
    private PipelinePolicy ConvertConfigurationToPolicy(PipelineConfiguration config)
    {
        return new PipelinePolicy
        {
            PolicyId = config.ConfigurationId,
            Name = config.Name,
            Level = PolicyLevel.Instance,
            ScopeId = "default",
            Stages = config.WriteStages.Select(s => new PipelineStagePolicy
            {
                StageType = s.StageType,
                Enabled = s.Enabled,
                PluginId = s.PluginId,
                Order = s.Order,
                Parameters = s.Parameters,
                AllowChildOverride = true
            }).ToList(),
            StageOrder = config.WriteStages.OrderBy(s => s.Order).Select(s => s.StageType).ToList(),
            Version = 1,
            UpdatedAt = DateTimeOffset.UtcNow,
            UpdatedBy = "System",
            MigrationBehavior = MigrationBehavior.KeepExisting,
            IsImmutable = false,
            Description = config.Name
        };
    }

    private IKernelContext CreateDefaultKernelContext()
    {
        return new NullKernelContext();
    }

    private string GetPluginId(IDataTransformation stage)
    {
        return (stage as IPlugin)?.Id ?? stage.GetType().Name;
    }

    private string GetStrategyName(IDataTransformation stage)
    {
        return (stage as IPlugin)?.Name ?? stage.SubCategory;
    }

    private string? GetPluginVersion(IDataTransformation stage)
    {
        var plugin = stage as IPlugin;
        return plugin?.GetType().Assembly.GetName().Version?.ToString();
    }

    /// <summary>
    /// Anonymous security context for operations without explicit user context.
    /// </summary>
    private sealed class AnonymousSecurityContext : ISecurityContext
    {
        public static readonly AnonymousSecurityContext Instance = new();
        public string UserId => "anonymous";
        public string? TenantId => null;
        public IEnumerable<string> Roles => Array.Empty<string>();
        public bool IsSystemAdmin => false;
    }

    /// <summary>
    /// Null kernel context for pipeline operations when no context is available.
    /// </summary>
    private sealed class NullKernelContext : IKernelContext
    {
        public OperatingMode Mode => OperatingMode.Workstation;
        public string RootPath => Environment.CurrentDirectory;
        public IKernelStorageService Storage => new NullKernelStorageService();

        public void LogInfo(string message) { }
        public void LogError(string message, Exception? ex = null) { }
        public void LogWarning(string message) { }
        public void LogDebug(string message) { }

        public T? GetPlugin<T>() where T : class, IPlugin => null;
        public IEnumerable<T> GetPlugins<T>() where T : class, IPlugin => Array.Empty<T>();
    }

    /// <summary>
    /// Null storage service for contexts where storage is not available.
    /// </summary>
    private sealed class NullKernelStorageService : IKernelStorageService
    {
        public Task SaveAsync(string path, Stream data, IDictionary<string, string>? metadata = null, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task SaveAsync(string path, byte[] data, IDictionary<string, string>? metadata = null, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<Stream?> LoadAsync(string path, CancellationToken ct = default)
            => Task.FromResult<Stream?>(null);
        public Task<byte[]?> LoadBytesAsync(string path, CancellationToken ct = default)
            => Task.FromResult<byte[]?>(null);
        public Task<IDictionary<string, string>?> GetMetadataAsync(string path, CancellationToken ct = default)
            => Task.FromResult<IDictionary<string, string>?>(null);
        public Task<bool> DeleteAsync(string path, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<bool> ExistsAsync(string path, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<IReadOnlyList<StorageItemInfo>> ListAsync(string prefix, int limit = 100, int offset = 0, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<StorageItemInfo>>(Array.Empty<StorageItemInfo>());
    }

    // Terminal execution methods

    private async Task<List<TerminalResult>> ExecuteTerminalsAsync(
        Stream input,
        PipelinePolicy policy,
        PipelineContext context,
        CancellationToken ct)
    {
        var terminals = policy.Terminals ?? new List<TerminalStagePolicy>();

        // If no terminals configured, use default primary terminal
        if (terminals.Count == 0)
        {
            terminals = new List<TerminalStagePolicy>
            {
                new() { TerminalType = "primary", Enabled = true, FailureIsCritical = true }
            };
        }

        var enabledTerminals = terminals.Where(t => t.Enabled ?? true).ToList();
        if (enabledTerminals.Count == 0)
            return new List<TerminalResult>();

        // Buffer the input for fan-out (each terminal needs a copy)
        byte[] data;
        using (var buffer = new MemoryStream(65536))
        {
            await input.CopyToAsync(buffer, ct);
            data = buffer.ToArray();
        }

        var results = new List<TerminalResult>();

        // Group by execution mode
        var parallelTerminals = enabledTerminals
            .Where(t => (t.ExecutionMode ?? TerminalExecutionMode.Parallel) == TerminalExecutionMode.Parallel)
            .OrderBy(t => t.Priority ?? 100)
            .ToList();

        var sequentialTerminals = enabledTerminals
            .Where(t => (t.ExecutionMode ?? TerminalExecutionMode.Parallel) == TerminalExecutionMode.Sequential)
            .OrderBy(t => t.Priority ?? 100)
            .ToList();

        var afterParallelTerminals = enabledTerminals
            .Where(t => (t.ExecutionMode ?? TerminalExecutionMode.Parallel) == TerminalExecutionMode.AfterParallel)
            .OrderBy(t => t.Priority ?? 100)
            .ToList();

        // Execute parallel terminals concurrently
        if (parallelTerminals.Count > 0)
        {
            var parallelTasks = parallelTerminals.Select(tp =>
                ExecuteSingleTerminalAsync(data, tp, context, ct));
            var parallelResults = await Task.WhenAll(parallelTasks);
            results.AddRange(parallelResults);

            // Check if any critical parallel terminal failed
            var criticalFailure = results.FirstOrDefault(r => !r.Success &&
                (parallelTerminals.First(t => t.TerminalType == r.TerminalType).FailureIsCritical ?? true));
            if (criticalFailure != null)
            {
                // Don't continue if critical terminal failed
                return results;
            }
        }

        // Execute sequential terminals in order
        foreach (var tp in sequentialTerminals)
        {
            var result = await ExecuteSingleTerminalAsync(data, tp, context, ct);
            results.Add(result);

            if (!result.Success && (tp.FailureIsCritical ?? true))
            {
                // Stop on critical failure
                break;
            }
        }

        // Execute after-parallel terminals
        foreach (var tp in afterParallelTerminals)
        {
            var result = await ExecuteSingleTerminalAsync(data, tp, context, ct);
            results.Add(result);

            if (!result.Success && (tp.FailureIsCritical ?? true))
                break;
        }

        return results;
    }

    private async Task<TerminalResult> ExecuteSingleTerminalAsync(
        byte[] data,
        TerminalStagePolicy terminalPolicy,
        PipelineContext context,
        CancellationToken ct)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var terminal = await ResolveTerminalAsync(terminalPolicy, context, ct);
            if (terminal == null)
            {
                return new TerminalResult
                {
                    TerminalId = terminalPolicy.PluginId ?? "unknown",
                    TerminalType = terminalPolicy.TerminalType,
                    Success = false,
                    ErrorMessage = $"Could not resolve terminal for type '{terminalPolicy.TerminalType}'",
                    Duration = stopwatch.Elapsed
                };
            }

            var terminalContext = BuildTerminalContext(context, terminalPolicy, data.Length);

            using var stream = new MemoryStream(data);

            // Apply timeout if specified
            using var timeoutCts = terminalPolicy.Timeout.HasValue
                ? new CancellationTokenSource(terminalPolicy.Timeout.Value)
                : null;
            using var linkedCts = timeoutCts != null
                ? CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token)
                : null;
            var effectiveCt = linkedCts?.Token ?? ct;

            await terminal.WriteAsync(stream, terminalContext, effectiveCt);

            stopwatch.Stop();

            // Publish success event
            await PublishTerminalEventAsync("pipeline.terminal.write.complete", terminalPolicy.TerminalType,
                terminal.TerminalId, data.Length, stopwatch.Elapsed, null);

            return new TerminalResult
            {
                TerminalId = terminal.TerminalId,
                TerminalType = terminalPolicy.TerminalType,
                Success = true,
                Duration = stopwatch.Elapsed,
                BytesWritten = data.Length,
                StoragePath = terminalContext.StoragePath
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Re-throw if external cancellation
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            await PublishTerminalEventAsync("pipeline.terminal.write.error", terminalPolicy.TerminalType,
                terminalPolicy.PluginId ?? "unknown", data.Length, stopwatch.Elapsed, ex.Message);

            return new TerminalResult
            {
                TerminalId = terminalPolicy.PluginId ?? "unknown",
                TerminalType = terminalPolicy.TerminalType,
                Success = false,
                ErrorMessage = ex.Message,
                Exception = ex,
                Duration = stopwatch.Elapsed
            };
        }
    }

    private async Task<IDataTerminal?> ResolveTerminalAsync(
        TerminalStagePolicy policy,
        PipelineContext context,
        CancellationToken ct)
    {
        // Try to resolve from plugin integration
        if (_pluginIntegration != null)
        {
            return await _pluginIntegration.ResolveTerminalAsync(policy, context.KernelContext, ct);
        }

        // Fallback: try to get from kernel context
        if (context.KernelContext != null && !string.IsNullOrEmpty(policy.PluginId))
        {
            var plugin = context.KernelContext.GetPlugin<IDataTerminal>();
            if (plugin != null && plugin.TerminalId == policy.TerminalType)
                return plugin;
        }

        return null;
    }

    private async Task<IDataTerminal?> ResolveReadTerminalAsync(
        PipelineContext context,
        CancellationToken ct)
    {
        var policy = await ResolveEffectivePolicyAsync(context, ct);
        var terminals = policy.Terminals ?? new List<TerminalStagePolicy>();

        // Prefer primary terminal for reads, then fall back by priority
        var primaryPolicy = terminals.FirstOrDefault(t => t.TerminalType == "primary" && (t.Enabled ?? true))
            ?? terminals.Where(t => t.Enabled ?? true).OrderBy(t => t.Priority ?? 100).FirstOrDefault();

        if (primaryPolicy == null)
        {
            // Default to primary terminal type
            primaryPolicy = new TerminalStagePolicy { TerminalType = "primary" };
        }

        return await ResolveTerminalAsync(primaryPolicy, context, ct);
    }

    private async Task<PipelinePolicy> ResolveEffectivePolicyAsync(
        PipelineContext context,
        CancellationToken ct)
    {
        var securityContext = context.SecurityContext ?? AnonymousSecurityContext.Instance;

        return await _configProvider.ResolveEffectivePolicyAsync(
            userId: securityContext.UserId,
            groupId: securityContext.TenantId,
            operationId: context.Parameters.TryGetValue("OperationId", out var opId) ? opId?.ToString() : null,
            ct: ct);
    }

    private TerminalContext BuildTerminalContext(
        PipelineContext pipelineContext,
        TerminalStagePolicy terminalPolicy,
        long contentLength)
    {
        var parameters = new Dictionary<string, object>(terminalPolicy.Parameters ?? new());

        // Add strategy name if specified
        if (!string.IsNullOrEmpty(terminalPolicy.StrategyName))
        {
            parameters["strategyId"] = terminalPolicy.StrategyName;
        }

        // Extract BlobId and StoragePath from parameters or generate defaults
        var blobId = pipelineContext.Parameters.TryGetValue("BlobId", out var bid) && bid is string bStr
            ? bStr
            : Guid.NewGuid().ToString("N");

        var storagePath = pipelineContext.Parameters.TryGetValue("StoragePath", out var sp) && sp is string spStr
            ? spStr
            : $"data/{DateTime.UtcNow:yyyy/MM/dd}/{Guid.NewGuid():N}";

        return new TerminalContext
        {
            BlobId = blobId,
            StoragePath = storagePath,
            Parameters = parameters,
            KernelContext = pipelineContext.KernelContext,
            Manifest = pipelineContext.Manifest,
            ContentLength = contentLength
        };
    }

    private async Task PublishTerminalEventAsync(
        string eventType,
        string terminalType,
        string terminalId,
        long bytes,
        TimeSpan duration,
        string? error)
    {
        if (_messageBus == null)
            return;

        try
        {
            var payload = new Dictionary<string, object>
            {
                ["terminalType"] = terminalType,
                ["terminalId"] = terminalId,
                ["bytesWritten"] = bytes,
                ["durationMs"] = duration.TotalMilliseconds,
                ["timestamp"] = DateTime.UtcNow
            };

            if (error != null)
                payload["error"] = error;

            await _messageBus.PublishAsync(eventType, new PluginMessage
            {
                Type = eventType,
                Payload = payload
            });
        }
        catch
        {
            // Ignore event publishing errors
        }
    }
}

/// <summary>
/// Exception thrown when a pipeline transaction fails due to critical terminal failure.
/// Contains all terminal results for diagnostic purposes.
/// </summary>
public class PipelineTransactionException : Exception
{
    /// <summary>
    /// Results from all terminals (both successful and failed).
    /// </summary>
    public IReadOnlyList<TerminalResult> TerminalResults { get; }

    /// <summary>
    /// Creates a new pipeline transaction exception.
    /// </summary>
    public PipelineTransactionException(string message, Exception? inner, List<TerminalResult> results)
        : base(message, inner)
    {
        TerminalResults = results.AsReadOnly();
    }
}
