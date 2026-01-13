using DataWarehouse.Kernel.Messaging;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
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
    /// </summary>
    public sealed class DefaultPipelineOrchestrator : IPipelineOrchestrator
    {
        private readonly PluginRegistry _registry;
        private readonly DefaultMessageBus _messageBus;
        private readonly ILogger? _logger;
        private readonly ConcurrentDictionary<string, IDataTransformation> _stages = new();
        private readonly object _configLock = new();

        private PipelineConfiguration _currentConfig;

        public DefaultPipelineOrchestrator(
            PluginRegistry registry,
            DefaultMessageBus messageBus,
            ILogger? logger = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _messageBus = messageBus ?? throw new ArgumentNullException(nameof(messageBus));
            _logger = logger;
            _currentConfig = PipelineConfiguration.CreateDefault();
        }

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
            return _stages.Select(kvp =>
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
        /// </summary>
        public async Task<Stream> ExecuteWritePipelineAsync(
            Stream input,
            PipelineContext context,
            CancellationToken ct = default)
        {
            var config = GetConfiguration();
            var orderedStages = config.WriteStages
                .Where(s => s.Enabled)
                .OrderBy(s => s.Order)
                .ToList();

            _logger?.LogDebug("Executing write pipeline with {Count} stages", orderedStages.Count);

            // Publish pipeline start event
            await _messageBus.PublishAsync(MessageTopics.PipelineExecute, new PluginMessage
            {
                Type = "pipeline.write.start",
                Payload = new Dictionary<string, object>
                {
                    ["StageCount"] = orderedStages.Count,
                    ["OriginalSize"] = context.OriginalSize ?? -1
                }
            }, ct);

            var currentStream = input;
            var kernelContext = context.KernelContext ?? CreateDefaultKernelContext();

            try
            {
                foreach (var stageConfig in orderedStages)
                {
                    ct.ThrowIfCancellationRequested();

                    var stage = FindStage(stageConfig);
                    if (stage == null)
                    {
                        _logger?.LogWarning("Stage not found: {StageType}", stageConfig.StageType);
                        continue;
                    }

                    _logger?.LogDebug("Executing stage: {StageType}", stageConfig.StageType);

                    currentStream = stage.OnWrite(currentStream, kernelContext, stageConfig.Parameters);
                    context.ExecutedStages.Add(stageConfig.StageType);
                }

                // Publish pipeline complete event
                await _messageBus.PublishAsync(MessageTopics.PipelineCompleted, new PluginMessage
                {
                    Type = "pipeline.write.complete",
                    Payload = new Dictionary<string, object>
                    {
                        ["ExecutedStages"] = context.ExecutedStages.ToArray()
                    }
                }, ct);

                return currentStream;
            }
            catch (Exception ex)
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

                throw;
            }
        }

        /// <summary>
        /// Execute the read pipeline (storage → user data).
        /// Applies transformations in reverse order.
        /// </summary>
        public async Task<Stream> ExecuteReadPipelineAsync(
            Stream input,
            PipelineContext context,
            CancellationToken ct = default)
        {
            var config = GetConfiguration();
            var orderedStages = config.WriteStages
                .Where(s => s.Enabled)
                .OrderByDescending(s => s.Order) // Reverse order for read
                .ToList();

            _logger?.LogDebug("Executing read pipeline with {Count} stages (reversed)", orderedStages.Count);

            await _messageBus.PublishAsync(MessageTopics.PipelineExecute, new PluginMessage
            {
                Type = "pipeline.read.start",
                Payload = new Dictionary<string, object>
                {
                    ["StageCount"] = orderedStages.Count
                }
            }, ct);

            var currentStream = input;
            var kernelContext = context.KernelContext ?? CreateDefaultKernelContext();

            try
            {
                foreach (var stageConfig in orderedStages)
                {
                    ct.ThrowIfCancellationRequested();

                    var stage = FindStage(stageConfig);
                    if (stage == null)
                    {
                        _logger?.LogWarning("Stage not found: {StageType}", stageConfig.StageType);
                        continue;
                    }

                    _logger?.LogDebug("Executing reverse stage: {StageType}", stageConfig.StageType);

                    currentStream = stage.OnRead(currentStream, kernelContext, stageConfig.Parameters);
                    context.ExecutedStages.Add(stageConfig.StageType);
                }

                await _messageBus.PublishAsync(MessageTopics.PipelineCompleted, new PluginMessage
                {
                    Type = "pipeline.read.complete",
                    Payload = new Dictionary<string, object>
                    {
                        ["ExecutedStages"] = context.ExecutedStages.ToArray()
                    }
                }, ct);

                return currentStream;
            }
            catch (Exception ex)
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

        private IKernelContext CreateDefaultKernelContext()
        {
            return new DefaultKernelContext(_registry);
        }

        /// <summary>
        /// Default kernel context implementation for pipeline operations.
        /// </summary>
        private sealed class DefaultKernelContext : IKernelContext
        {
            private readonly PluginRegistry _registry;

            public DefaultKernelContext(PluginRegistry registry)
            {
                _registry = registry;
            }

            public OperatingMode Mode => _registry.OperatingMode;
            public string RootPath => Environment.CurrentDirectory;

            public void LogInfo(string message) { /* Default: no-op */ }
            public void LogError(string message, Exception? ex = null) { /* Default: no-op */ }
            public void LogWarning(string message) { /* Default: no-op */ }
            public void LogDebug(string message) { /* Default: no-op */ }

            public T? GetPlugin<T>() where T : class, IPlugin => _registry.GetPlugin<T>();
            public IEnumerable<T> GetPlugins<T>() where T : class, IPlugin => _registry.GetPlugins<T>();
        }
    }
}
