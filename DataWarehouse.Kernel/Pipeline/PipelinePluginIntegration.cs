using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Pipeline;
using DataWarehouse.SDK.Security;
using System.Collections.Concurrent;

namespace DataWarehouse.Kernel.Pipeline
{
    /// <summary>
    /// Provides integration between the pipeline orchestrator and Ultimate plugin suite.
    /// Maps pipeline policy stage settings to specific plugin strategies.
    /// </summary>
    public class PipelinePluginIntegration
    {
        private readonly ConcurrentDictionary<string, IDataTransformation> _resolvedStageCache = new();
        private readonly ConcurrentDictionary<string, IDataTerminal> _terminalCache = new();

        /// <summary>
        /// Resolves the encryption strategy from the pipeline policy.
        /// Maps PluginId + StrategyName to the actual IEncryptionStrategy.
        /// </summary>
        /// <param name="stagePolicy">The stage policy configuration.</param>
        /// <param name="context">The kernel context for plugin discovery.</param>
        /// <returns>The resolved data transformation plugin, or null if not found/enabled.</returns>
        public IDataTransformation? ResolveEncryptionStage(PipelineStagePolicy stagePolicy, IKernelContext? context)
        {
            if (stagePolicy.Enabled == false)
                return null;

            if (context == null)
                return null;

            // Try to get from cache first
            var cacheKey = $"encryption:{stagePolicy.PluginId}:{stagePolicy.StrategyName}";
            if (_resolvedStageCache.TryGetValue(cacheKey, out var cached))
                return cached;

            // Get the Ultimate Encryption plugin
            var plugins = context.GetPlugins<IDataTransformation>();
            var encryptionPlugin = plugins.FirstOrDefault(p =>
                p.SubCategory.Equals("Encryption", StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(stagePolicy.PluginId) || p.Id == stagePolicy.PluginId));

            if (encryptionPlugin == null)
            {
                context.LogWarning($"Encryption plugin not found for stage policy: {stagePolicy.PluginId ?? "default"}");
                return null;
            }

            // If a specific strategy is requested, we would need to configure it
            // For now, return the plugin itself which can be configured via args
            _resolvedStageCache[cacheKey] = encryptionPlugin;
            return encryptionPlugin;
        }

        /// <summary>
        /// Resolves the compression strategy from the pipeline policy.
        /// </summary>
        /// <param name="stagePolicy">The stage policy configuration.</param>
        /// <param name="context">The kernel context for plugin discovery.</param>
        /// <returns>The resolved data transformation plugin, or null if not found/enabled.</returns>
        public IDataTransformation? ResolveCompressionStage(PipelineStagePolicy stagePolicy, IKernelContext? context)
        {
            if (stagePolicy.Enabled == false)
                return null;

            if (context == null)
                return null;

            var cacheKey = $"compression:{stagePolicy.PluginId}:{stagePolicy.StrategyName}";
            if (_resolvedStageCache.TryGetValue(cacheKey, out var cached))
                return cached;

            var plugins = context.GetPlugins<IDataTransformation>();
            var compressionPlugin = plugins.FirstOrDefault(p =>
                p.SubCategory.Equals("Compression", StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(stagePolicy.PluginId) || p.Id == stagePolicy.PluginId));

            if (compressionPlugin == null)
            {
                context.LogWarning($"Compression plugin not found for stage policy: {stagePolicy.PluginId ?? "default"}");
                return null;
            }

            _resolvedStageCache[cacheKey] = compressionPlugin;
            return compressionPlugin;
        }

        /// <summary>
        /// Resolves the storage backend from the pipeline policy.
        /// </summary>
        /// <param name="stagePolicy">The stage policy configuration.</param>
        /// <param name="context">The kernel context for plugin discovery.</param>
        /// <returns>The resolved data transformation plugin, or null if not found/enabled.</returns>
        public IDataTransformation? ResolveStorageStage(PipelineStagePolicy stagePolicy, IKernelContext? context)
        {
            if (stagePolicy.Enabled == false)
                return null;

            if (context == null)
                return null;

            var cacheKey = $"storage:{stagePolicy.PluginId}:{stagePolicy.StrategyName}";
            if (_resolvedStageCache.TryGetValue(cacheKey, out var cached))
                return cached;

            var plugins = context.GetPlugins<IDataTransformation>();
            var storagePlugin = plugins.FirstOrDefault(p =>
                p.SubCategory.Equals("Storage", StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(stagePolicy.PluginId) || p.Id == stagePolicy.PluginId));

            if (storagePlugin == null)
            {
                context.LogWarning($"Storage plugin not found for stage policy: {stagePolicy.PluginId ?? "default"}");
                return null;
            }

            _resolvedStageCache[cacheKey] = storagePlugin;
            return storagePlugin;
        }

        /// <summary>
        /// Resolves the RAID/erasure coding strategy from the pipeline policy.
        /// </summary>
        /// <param name="stagePolicy">The stage policy configuration.</param>
        /// <param name="context">The kernel context for plugin discovery.</param>
        /// <returns>The resolved data transformation plugin, or null if not found/enabled.</returns>
        public IDataTransformation? ResolveRaidStage(PipelineStagePolicy stagePolicy, IKernelContext? context)
        {
            if (stagePolicy.Enabled == false)
                return null;

            if (context == null)
                return null;

            var cacheKey = $"raid:{stagePolicy.PluginId}:{stagePolicy.StrategyName}";
            if (_resolvedStageCache.TryGetValue(cacheKey, out var cached))
                return cached;

            var plugins = context.GetPlugins<IDataTransformation>();
            var raidPlugin = plugins.FirstOrDefault(p =>
                (p.SubCategory.Equals("RAID", StringComparison.OrdinalIgnoreCase) ||
                 p.SubCategory.Equals("ErasureCoding", StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrEmpty(stagePolicy.PluginId) || p.Id == stagePolicy.PluginId));

            if (raidPlugin == null)
            {
                context.LogWarning($"RAID plugin not found for stage policy: {stagePolicy.PluginId ?? "default"}");
                return null;
            }

            _resolvedStageCache[cacheKey] = raidPlugin;
            return raidPlugin;
        }

        /// <summary>
        /// Resolves the key management strategy from the pipeline policy.
        /// Per-user/per-group key selection.
        /// </summary>
        /// <param name="stagePolicy">The stage policy configuration.</param>
        /// <param name="context">The kernel context for plugin discovery.</param>
        /// <returns>The resolved data transformation plugin, or null if not found/enabled.</returns>
        public IDataTransformation? ResolveKeyManagementStage(PipelineStagePolicy stagePolicy, IKernelContext? context)
        {
            if (stagePolicy.Enabled == false)
                return null;

            if (context == null)
                return null;

            var cacheKey = $"keymanagement:{stagePolicy.PluginId}:{stagePolicy.StrategyName}";
            if (_resolvedStageCache.TryGetValue(cacheKey, out var cached))
                return cached;

            var plugins = context.GetPlugins<IDataTransformation>();
            var keyManagementPlugin = plugins.FirstOrDefault(p =>
                p.SubCategory.Equals("KeyManagement", StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(stagePolicy.PluginId) || p.Id == stagePolicy.PluginId));

            if (keyManagementPlugin == null)
            {
                context.LogWarning($"Key Management plugin not found for stage policy: {stagePolicy.PluginId ?? "default"}");
                return null;
            }

            _resolvedStageCache[cacheKey] = keyManagementPlugin;
            return keyManagementPlugin;
        }

        /// <summary>
        /// E8: Transit Encryption integration.
        /// Resolves the transit encryption strategy from the pipeline policy.
        /// Looks up TransitEncryptionPluginBase-derived plugins matching the strategy name.
        /// </summary>
        /// <param name="stagePolicy">The stage policy configuration.</param>
        /// <param name="context">The kernel context for plugin discovery.</param>
        /// <returns>The resolved data transformation plugin, or null if not found/enabled.</returns>
        public IDataTransformation? ResolveTransitEncryptionStage(PipelineStagePolicy stagePolicy, IKernelContext? context)
        {
            if (stagePolicy.Enabled == false)
                return null;

            if (context == null)
                return null;

            var cacheKey = $"transitencryption:{stagePolicy.PluginId}:{stagePolicy.StrategyName}";
            if (_resolvedStageCache.TryGetValue(cacheKey, out var cached))
                return cached;

            var plugins = context.GetPlugins<IDataTransformation>();
            var transitEncryptionPlugin = plugins.FirstOrDefault(p =>
                p.SubCategory.Equals("TransitEncryption", StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(stagePolicy.PluginId) || p.Id == stagePolicy.PluginId));

            if (transitEncryptionPlugin == null)
            {
                context.LogWarning($"Transit Encryption plugin not found for stage policy: {stagePolicy.PluginId ?? "default"}");
                return null;
            }

            _resolvedStageCache[cacheKey] = transitEncryptionPlugin;
            return transitEncryptionPlugin;
        }

        /// <summary>
        /// E9: Transit Compression integration.
        /// Resolves the transit compression strategy from the pipeline policy.
        /// Looks up transit compression strategies from UltimateCompression plugin.
        /// </summary>
        /// <param name="stagePolicy">The stage policy configuration.</param>
        /// <param name="context">The kernel context for plugin discovery.</param>
        /// <returns>The resolved data transformation plugin, or null if not found/enabled.</returns>
        public IDataTransformation? ResolveTransitCompressionStage(PipelineStagePolicy stagePolicy, IKernelContext? context)
        {
            if (stagePolicy.Enabled == false)
                return null;

            if (context == null)
                return null;

            var cacheKey = $"transitcompression:{stagePolicy.PluginId}:{stagePolicy.StrategyName}";
            if (_resolvedStageCache.TryGetValue(cacheKey, out var cached))
                return cached;

            var plugins = context.GetPlugins<IDataTransformation>();
            var transitCompressionPlugin = plugins.FirstOrDefault(p =>
                p.SubCategory.Equals("TransitCompression", StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrEmpty(stagePolicy.PluginId) || p.Id == stagePolicy.PluginId));

            if (transitCompressionPlugin == null)
            {
                context.LogWarning($"Transit Compression plugin not found for stage policy: {stagePolicy.PluginId ?? "default"}");
                return null;
            }

            _resolvedStageCache[cacheKey] = transitCompressionPlugin;
            return transitCompressionPlugin;
        }

        /// <summary>
        /// Validates access permissions before pipeline execution.
        /// Enforce ACL checks before pipeline execution.
        /// </summary>
        /// <param name="securityContext">The security context containing user/role information.</param>
        /// <param name="operation">The operation being performed (read, write, delete, etc.).</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if access is granted, false otherwise.</returns>
        public async Task<bool> ValidateAccessAsync(ISecurityContext? securityContext, string operation, CancellationToken ct = default)
        {
            if (securityContext == null)
            {
                // No security context means deny by default
                return false;
            }

            // System admins always have access
            if (securityContext.IsSystemAdmin)
                return true;

            // TODO: Integrate with UltimateAccessControl plugin when available
            // For now, allow all authenticated users
            if (!string.IsNullOrEmpty(securityContext.UserId))
                return true;

            return false;
        }

        /// <summary>
        /// Gets mandatory pipeline stages required by compliance rules.
        /// Compliance rules can mandate minimum pipeline stages.
        /// </summary>
        /// <param name="securityContext">The security context containing user/tenant information.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>List of mandatory pipeline stage policies.</returns>
        public async Task<List<PipelineStagePolicy>> GetMandatoryStagesAsync(ISecurityContext? securityContext, CancellationToken ct = default)
        {
            var mandatoryStages = new List<PipelineStagePolicy>();

            // TODO: Integrate with UltimateCompliance plugin when available
            // For now, return empty list (no mandatory stages)

            // Example of what this might look like:
            // if (compliancePlugin.RequiresEncryption(securityContext.TenantId))
            // {
            //     mandatoryStages.Add(new PipelineStagePolicy
            //     {
            //         StageType = "Encryption",
            //         Enabled = true,
            //         AllowChildOverride = false
            //     });
            // }

            return await Task.FromResult(mandatoryStages);
        }

        /// <summary>
        /// Resolves a pipeline stage from its policy configuration.
        /// Dispatches to the appropriate plugin integration method based on StageType.
        /// </summary>
        /// <param name="stagePolicy">The stage policy configuration.</param>
        /// <param name="context">The kernel context for plugin discovery.</param>
        /// <returns>The resolved data transformation plugin, or null if not found/enabled.</returns>
        public IDataTransformation? ResolveStage(PipelineStagePolicy stagePolicy, IKernelContext? context)
        {
            return stagePolicy.StageType.ToLowerInvariant() switch
            {
                "encryption" => ResolveEncryptionStage(stagePolicy, context),
                "compression" => ResolveCompressionStage(stagePolicy, context),
                "storage" => ResolveStorageStage(stagePolicy, context),
                "raid" or "erasurecoding" => ResolveRaidStage(stagePolicy, context),
                "keymanagement" => ResolveKeyManagementStage(stagePolicy, context),
                "transitencryption" => ResolveTransitEncryptionStage(stagePolicy, context),
                "transitcompression" => ResolveTransitCompressionStage(stagePolicy, context),
                _ => null // Unknown stage type, skip
            };
        }

        /// <summary>
        /// Clears the resolved stage cache. Useful when plugins are reloaded.
        /// </summary>
        public void ClearCache()
        {
            _resolvedStageCache.Clear();
            _terminalCache.Clear();
        }

        /// <summary>
        /// Resolves a terminal plugin based on the terminal policy.
        /// </summary>
        public async Task<IDataTerminal?> ResolveTerminalAsync(
            TerminalStagePolicy policy,
            IKernelContext? kernelContext,
            CancellationToken ct = default)
        {
            // Try to resolve by terminal type from cached terminals
            if (_terminalCache.TryGetValue(policy.TerminalType, out var cachedTerminal))
                return cachedTerminal;

            if (kernelContext == null)
                return null;

            // Try to resolve from available IDataTerminal plugins
            var terminals = kernelContext.GetPlugins<IDataTerminal>();

            // First, try to match by explicit plugin ID
            if (!string.IsNullOrEmpty(policy.PluginId))
            {
                var terminal = terminals.FirstOrDefault(t =>
                    (t as IPlugin)?.Id == policy.PluginId);
                if (terminal != null)
                {
                    _terminalCache[policy.TerminalType] = terminal;
                    return terminal;
                }
            }

            // Try to match by terminal type
            var matchedTerminal = terminals.FirstOrDefault(t =>
                t.TerminalId == policy.TerminalType ||
                (string.IsNullOrEmpty(policy.TerminalType) && t.TerminalId == "primary"));

            if (matchedTerminal != null)
            {
                _terminalCache[policy.TerminalType] = matchedTerminal;
                return matchedTerminal;
            }

            // Fallback: use the first available terminal for "primary" type
            if (policy.TerminalType == "primary" || string.IsNullOrEmpty(policy.TerminalType))
            {
                var firstTerminal = terminals.FirstOrDefault();
                if (firstTerminal != null)
                {
                    _terminalCache[policy.TerminalType] = firstTerminal;
                    return firstTerminal;
                }
            }

            return await Task.FromResult<IDataTerminal?>(null);
        }

        /// <summary>
        /// Registers a terminal explicitly for a terminal type.
        /// </summary>
        public void RegisterTerminal(string terminalType, IDataTerminal terminal)
        {
            _terminalCache[terminalType] = terminal;
        }

        /// <summary>
        /// Gets all registered terminals.
        /// </summary>
        public IReadOnlyDictionary<string, IDataTerminal> GetRegisteredTerminals()
        {
            return new Dictionary<string, IDataTerminal>(_terminalCache);
        }
    }
}
