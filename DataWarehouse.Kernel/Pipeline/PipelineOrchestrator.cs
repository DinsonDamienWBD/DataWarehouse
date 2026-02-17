using DataWarehouse.Kernel.Messaging;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Utilities;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

// Use SDK's MessageTopics
using static DataWarehouse.SDK.Contracts.MessageTopics;

namespace DataWarehouse.Kernel.Pipeline
{
    /// <summary>
    /// Default implementation of the pipeline orchestrator.
    ///
    /// Manages transformation chains with:
    /// - Runtime-configurable ordering
    /// - Default: Compress → Encrypt
    /// - User overrides supported
    /// - Stage validation and dependency checking
    /// - Non-blocking async execution
    /// - Distributed tracing support
    /// </summary>
    public sealed class DefaultPipelineOrchestrator(
        PluginRegistry registry,
        DefaultMessageBus messageBus,
        ILogger? logger = null,
        IDistributedTracing? tracing = null) : IPipelineOrchestrator
    {
        private readonly PluginRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        private readonly DefaultMessageBus _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
        private readonly ILogger? _logger = logger;
        private readonly IDistributedTracing? _tracing = tracing;
        private readonly ConcurrentDictionary<string, IDataTransformation> _stages = new();
        private readonly Lock _configLock = new();

        private PipelineConfiguration _currentConfig = PipelineConfiguration.CreateDefault();

        /// <summary>
        /// Get the current pipeline configuration.
        /// </summary>
        public PipelineConfiguration GetConfiguration()
        {
            lock (_configLock)
            {
                return _currentConfig;
            }
        }

        /// <summary>
        /// Set a new pipeline configuration.
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

            lock (_configLock)
            {
                _currentConfig = config;
            }

            _logger?.LogInformation("Pipeline configuration updated: {Name}", config.Name);

            // Publish config change event
            _ = _messageBus.PublishAsync(MessageTopics.ConfigChanged, new PluginMessage
            {
                Type = "pipeline.config.changed",
                Payload = new Dictionary<string, object>
                {
                    ["ConfigurationId"] = config.ConfigurationId,
                    ["Name"] = config.Name,
                    ["StageCount"] = config.WriteStages.Count
                }
            });
        }

        /// <summary>
        /// Reset to default configuration.
        /// </summary>
        public void ResetToDefaults()
        {
            SetConfiguration(PipelineConfiguration.CreateDefault());
            _logger?.LogInformation("Pipeline configuration reset to defaults");
        }

        /// <summary>
        /// Register a pipeline stage.
        /// </summary>
        public void RegisterStage(IDataTransformation stage)
        {
            ArgumentNullException.ThrowIfNull(stage);

            var plugin = stage as IPlugin;
            var stageId = plugin?.Id ?? stage.GetType().Name;

            _stages[stageId] = stage;
            _logger?.LogDebug("Pipeline stage registered: {StageId} ({SubCategory})",
                stageId, stage.SubCategory);
        }

        /// <summary>
        /// Unregister a pipeline stage.
        /// </summary>
        public void UnregisterStage(string stageId)
        {
            if (_stages.TryRemove(stageId, out _))
            {
                _logger?.LogDebug("Pipeline stage unregistered: {StageId}", stageId);
            }
        }

        /// <summary>
        /// Get all registered stages.
        /// </summary>
        public IEnumerable<PipelineStageInfo> GetRegisteredStages()
        {
            return [.. _stages.Select(kvp =>
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
                    RequiredPrecedingStages = pipelinePlugin?.RequiredPrecedingStages?.ToArray() ?? [],
                    IncompatibleStages = pipelinePlugin?.IncompatibleStages?.ToArray() ?? [],
                    Description = plugin?.Name ?? "Pipeline stage"
                };
            })];
        }

        /// <summary>
        /// Validate a pipeline configuration.
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
                if (!_stages.ContainsKey(stageConfig.PluginId!))
                {
                    warnings.Add($"Stage plugin not found: {stageConfig.PluginId}");
                    suggestions.Add($"Available plugins for {stageConfig.StageType}: " +
                        string.Join(", ", GetStagesByCategory(stageConfig.StageType)));
                }
            }

            // Check stage dependencies
            var orderedStages = config.WriteStages.OrderBy(s => s.Order).ToList();
            var executedStages = new HashSet<string>();

            foreach (var stageConfig in orderedStages)
            {
                var stageInfo = GetRegisteredStages()
                    .FirstOrDefault(s => s.PluginId == stageConfig.PluginId ||
                                         s.SubCategory.Equals(stageConfig.StageType, StringComparison.OrdinalIgnoreCase));

                if (stageInfo != null)
                {
                    // Check required preceding stages
                    foreach (var required in stageInfo.RequiredPrecedingStages)
                    {
                        if (!executedStages.Contains(required))
                        {
                            errors.Add($"Stage {stageConfig.StageType} requires {required} to run first");
                        }
                    }

                    // Check incompatible stages
                    foreach (var incompatible in stageInfo.IncompatibleStages)
                    {
                        if (executedStages.Contains(incompatible))
                        {
                            errors.Add($"Stage {stageConfig.StageType} is incompatible with {incompatible}");
                        }
                    }

                    executedStages.Add(stageInfo.SubCategory);
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

        /// <summary>
        /// Execute the write pipeline (user data → storage).
        /// Properly manages stream lifecycle and positions between stages.
        /// </summary>
        public async Task<Stream> ExecuteWritePipelineAsync(
            Stream input,
            PipelineContext context,
            CancellationToken ct = default)
        {
            // Start distributed trace span
            using var traceScope = _tracing?.StartSpan("pipeline.write");

            var config = GetConfiguration();
            var orderedStages = config.WriteStages
                .Where(s => s.Enabled)
                .OrderBy(s => s.Order)
                .ToList();

            // Get security context from PipelineContext or use anonymous fallback
            var securityContext = context.SecurityContext ?? AnonymousSecurityContext.Instance;

            // Add trace context
            traceScope?.SetTag("stage_count", orderedStages.Count);
            traceScope?.SetTag("user_id", securityContext.UserId);
            traceScope?.SetTag("original_size", context.OriginalSize ?? -1);

            _logger?.LogDebug("Executing write pipeline with {Count} stages (User: {UserId}, TraceId: {TraceId})",
                orderedStages.Count, securityContext.UserId, _tracing?.Current?.CorrelationId ?? "none");

            // Publish pipeline start event with security audit and correlation ID
            await _messageBus.PublishAsync(MessageTopics.PipelineExecute, new PluginMessage
            {
                Type = "pipeline.write.start",
                CorrelationId = _tracing?.Current?.CorrelationId,
                Payload = new Dictionary<string, object>
                {
                    ["StageCount"] = orderedStages.Count,
                    ["OriginalSize"] = context.OriginalSize ?? -1,
                    ["UserId"] = securityContext.UserId,
                    ["TenantId"] = securityContext.TenantId ?? "none",
                    ["CorrelationId"] = _tracing?.Current?.CorrelationId ?? "none"
                }
            }, ct);

            var currentStream = input;
            var kernelContext = context.KernelContext ?? CreateDefaultKernelContext();

            // Track intermediate streams for proper disposal
            var intermediateStreams = new List<Stream>();
            var missingStages = new List<string>();

            try
            {
                foreach (var stageConfig in orderedStages)
                {
                    ct.ThrowIfCancellationRequested();

                    var stage = FindStage(stageConfig);
                    if (stage == null)
                    {
                        _logger?.LogWarning("Stage not found: {StageType} (PluginId: {PluginId})",
                            stageConfig.StageType, stageConfig.PluginId ?? "auto");
                        missingStages.Add(stageConfig.StageType);

                        // Check if this is a critical stage that cannot be skipped
                        if (stageConfig.StageType.Equals("Encryption", StringComparison.OrdinalIgnoreCase) ||
                            stageConfig.StageType.Equals("Compress", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger?.LogError("Critical pipeline stage missing: {StageType}. Data may be stored without proper transformation.",
                                stageConfig.StageType);
                        }
                        continue;
                    }

                    _logger?.LogDebug("Executing stage: {StageType}", stageConfig.StageType);

                    // Ensure stream is at beginning before passing to stage
                    if (currentStream.CanSeek)
                    {
                        currentStream.Position = 0;
                    }

                    var previousStream = currentStream;
                    currentStream = stage.OnWrite(currentStream, kernelContext, stageConfig.Parameters);

                    // Track intermediate streams for cleanup (but not the original input)
                    if (previousStream != input && !intermediateStreams.Contains(previousStream))
                    {
                        intermediateStreams.Add(previousStream);
                    }

                    context.ExecutedStages.Add(stageConfig.StageType);
                }

                // Ensure final stream position is at beginning for caller
                if (currentStream.CanSeek)
                {
                    currentStream.Position = 0;
                }

                // Publish pipeline complete event
                await _messageBus.PublishAsync(MessageTopics.PipelineCompleted, new PluginMessage
                {
                    Type = "pipeline.write.complete",
                    Payload = new Dictionary<string, object>
                    {
                        ["ExecutedStages"] = context.ExecutedStages.ToArray(),
                        ["MissingStages"] = missingStages.ToArray()
                    }
                }, ct);

                // Store intermediate streams in context for later cleanup by caller
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

                await _messageBus.PublishAsync(MessageTopics.PipelineError, new PluginMessage
                {
                    Type = "pipeline.write.error",
                    Payload = new Dictionary<string, object>
                    {
                        ["Error"] = ex.Message,
                        ["ExecutedStages"] = context.ExecutedStages.ToArray(),
                        ["MissingStages"] = missingStages.ToArray()
                    }
                }, ct);

                throw;
            }
        }

        /// <summary>
        /// Execute the read pipeline (storage → user data).
        /// Applies transformations in reverse order.
        /// Properly manages stream lifecycle and positions between stages.
        /// </summary>
        public async Task<Stream> ExecuteReadPipelineAsync(
            Stream input,
            PipelineContext context,
            CancellationToken ct = default)
        {
            // Start distributed trace span
            using var traceScope = _tracing?.StartSpan("pipeline.read");

            var config = GetConfiguration();
            var orderedStages = config.WriteStages
                .Where(s => s.Enabled)
                .OrderByDescending(s => s.Order) // Reverse order for read
                .ToList();

            // Get security context from PipelineContext or use anonymous fallback
            var securityContext = context.SecurityContext ?? AnonymousSecurityContext.Instance;

            // Add trace context
            traceScope?.SetTag("stage_count", orderedStages.Count);
            traceScope?.SetTag("user_id", securityContext.UserId);

            _logger?.LogDebug("Executing read pipeline with {Count} stages (reversed, User: {UserId}, TraceId: {TraceId})",
                orderedStages.Count, securityContext.UserId, _tracing?.Current?.CorrelationId ?? "none");

            await _messageBus.PublishAsync(MessageTopics.PipelineExecute, new PluginMessage
            {
                Type = "pipeline.read.start",
                CorrelationId = _tracing?.Current?.CorrelationId,
                Payload = new Dictionary<string, object>
                {
                    ["StageCount"] = orderedStages.Count,
                    ["UserId"] = securityContext.UserId,
                    ["TenantId"] = securityContext.TenantId ?? "none",
                    ["CorrelationId"] = _tracing?.Current?.CorrelationId ?? "none"
                }
            }, ct);

            var currentStream = input;
            var kernelContext = context.KernelContext ?? CreateDefaultKernelContext();

            // Track intermediate streams for proper disposal
            var intermediateStreams = new List<Stream>();
            var missingStages = new List<string>();

            try
            {
                foreach (var stageConfig in orderedStages)
                {
                    ct.ThrowIfCancellationRequested();

                    var stage = FindStage(stageConfig);
                    if (stage == null)
                    {
                        _logger?.LogWarning("Stage not found: {StageType} (PluginId: {PluginId})",
                            stageConfig.StageType, stageConfig.PluginId ?? "auto");
                        missingStages.Add(stageConfig.StageType);
                        continue;
                    }

                    _logger?.LogDebug("Executing reverse stage: {StageType}", stageConfig.StageType);

                    // Ensure stream is at beginning before passing to stage
                    if (currentStream.CanSeek)
                    {
                        currentStream.Position = 0;
                    }

                    var previousStream = currentStream;
                    currentStream = stage.OnRead(currentStream, kernelContext, stageConfig.Parameters);

                    // Track intermediate streams for cleanup (but not the original input)
                    if (previousStream != input && !intermediateStreams.Contains(previousStream))
                    {
                        intermediateStreams.Add(previousStream);
                    }

                    context.ExecutedStages.Add(stageConfig.StageType);
                }

                // Ensure final stream position is at beginning for caller
                if (currentStream.CanSeek)
                {
                    currentStream.Position = 0;
                }

                await _messageBus.PublishAsync(MessageTopics.PipelineCompleted, new PluginMessage
                {
                    Type = "pipeline.read.complete",
                    Payload = new Dictionary<string, object>
                    {
                        ["ExecutedStages"] = context.ExecutedStages.ToArray(),
                        ["MissingStages"] = missingStages.ToArray()
                    }
                }, ct);

                // Store intermediate streams in context for later cleanup by caller
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

                await _messageBus.PublishAsync(MessageTopics.PipelineError, new PluginMessage
                {
                    Type = "pipeline.read.error",
                    Payload = new Dictionary<string, object>
                    {
                        ["Error"] = ex.Message,
                        ["ExecutedStages"] = context.ExecutedStages.ToArray(),
                        ["MissingStages"] = missingStages.ToArray()
                    }
                }, ct);

                throw;
            }
        }

        private IDataTransformation? FindStage(PipelineStageConfig config)
        {
            // First try by specific plugin ID
            if (!string.IsNullOrEmpty(config.PluginId))
            {
                if (_stages.TryGetValue(config.PluginId, out var stage))
                {
                    return stage;
                }
            }

            // Otherwise find by category/subcategory
            var byCategory = _stages.Values
                .Where(s => s.SubCategory.Equals(config.StageType, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.QualityLevel)
                .FirstOrDefault();

            return byCategory;
        }

        private IEnumerable<string> GetStagesByCategory(string category)
        {
            return _stages
                .Where(kvp => kvp.Value.SubCategory.Equals(category, StringComparison.OrdinalIgnoreCase))
                .Select(kvp => kvp.Key);
        }

        private DefaultKernelContext CreateDefaultKernelContext()
        {
            return new DefaultKernelContext(_registry);
        }

        /// <summary>
        /// Default kernel context implementation for pipeline operations.
        /// </summary>
        private sealed class DefaultKernelContext(PluginRegistry registry) : IKernelContext
        {
            private readonly PluginRegistry _registry = registry;
            private readonly IKernelStorageService _storage = new NullKernelStorageService();

            public OperatingMode Mode => _registry.OperatingMode;
            public string RootPath => Environment.CurrentDirectory;
            public IKernelStorageService Storage => _storage;

            public void LogInfo(string message) { /* Default: no-op */ }
            public void LogError(string message, Exception? ex = null) { /* Default: no-op */ }
            public void LogWarning(string message) { /* Default: no-op */ }
            public void LogDebug(string message) { /* Default: no-op */ }

            public T? GetPlugin<T>() where T : class, IPlugin => _registry.GetPlugin<T>();
            public IEnumerable<T> GetPlugins<T>() where T : class, IPlugin => _registry.GetPlugins<T>();
        }

        /// <summary>
        /// Anonymous security context for pipeline operations when no user context is available.
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
        /// Null implementation of IKernelStorageService for contexts where storage is not available.
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
}
