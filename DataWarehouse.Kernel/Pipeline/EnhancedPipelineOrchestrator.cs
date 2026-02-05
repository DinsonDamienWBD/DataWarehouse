using DataWarehouse.Kernel.Messaging;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Pipeline;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Utilities;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

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
    private readonly ConcurrentDictionary<string, IDataTransformation> _registeredStages = new();

    /// <summary>
    /// Creates a new enhanced pipeline orchestrator.
    /// </summary>
    /// <param name="configProvider">Policy configuration provider for resolving effective policies.</param>
    /// <param name="migrationEngine">Optional migration engine for handling policy version changes.</param>
    /// <param name="messageBus">Optional message bus for publishing pipeline events.</param>
    /// <param name="logger">Optional logger for diagnostic output.</param>
    public EnhancedPipelineOrchestrator(
        IPipelineConfigProvider configProvider,
        IPipelineMigrationEngine? migrationEngine = null,
        DefaultMessageBus? messageBus = null,
        ILogger? logger = null)
    {
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _migrationEngine = migrationEngine;
        _messageBus = messageBus;
        _logger = logger;
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
        _configProvider.SetPolicyAsync(policy).Wait();

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
            var pipelinePlugin = kvp.Value as PipelinePluginBase;

            return new PipelineStageInfo
            {
                PluginId = kvp.Key,
                Name = plugin?.Name ?? kvp.Key,
                SubCategory = kvp.Value.SubCategory,
                QualityLevel = kvp.Value.QualityLevel,
                DefaultOrder = pipelinePlugin?.DefaultOrder ?? 100,
                AllowBypass = pipelinePlugin?.AllowBypass ?? false,
                RequiredPrecedingStages = pipelinePlugin?.RequiredPrecedingStages ?? Array.Empty<string>(),
                IncompatibleStages = pipelinePlugin?.IncompatibleStages ?? Array.Empty<string>(),
                Description = plugin?.Name ?? "Pipeline stage"
            };
        }).ToList();
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
}
