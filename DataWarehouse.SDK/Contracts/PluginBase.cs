using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts.Compression;
using DataWarehouse.SDK.Contracts.Encryption;
using DataWarehouse.SDK.Contracts.Persistence;
using DataWarehouse.SDK.Contracts.Policy;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Primitives.Configuration;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts.Hierarchy;

namespace DataWarehouse.SDK.Contracts
{
    /// <summary>
    /// Abstract base class for all plugins. Provides default implementations
    /// of common plugin functionality. Plugins should inherit from this instead
    /// of implementing IPlugin directly.
    /// AI-native: Includes built-in support for AI-driven operations.
    /// </summary>
    public abstract class PluginBase : IPlugin, IDisposable, IAsyncDisposable
    {
        /// <summary>
        /// Knowledge cache for performance optimization.
        /// Maps knowledge topic to cached KnowledgeObject.
        /// Initialized lazily in InitializeAsync after the StateStore is available
        /// so that cache entries can be auto-persisted.
        /// </summary>
        private BoundedDictionary<string, KnowledgeObject>? _knowledgeCache;

        /// <summary>
        /// Capability registry reference for capability registration.
        /// Set during initialization.
        /// </summary>
        protected IPluginCapabilityRegistry? CapabilityRegistry { get; private set; }

        /// <summary>
        /// Knowledge lake reference (injected by kernel).
        /// </summary>
        protected IKnowledgeLake? KnowledgeLake { get; private set; }

        /// <summary>
        /// List of registered capability IDs for cleanup.
        /// </summary>
        private readonly List<string> _registeredCapabilityIds = new();

        /// <summary>
        /// Track registered knowledge IDs for cleanup.
        /// </summary>
        private readonly List<string> _registeredKnowledgeIds = new();

        /// <summary>
        /// Knowledge subscription handles for cleanup.
        /// </summary>
        private readonly List<IDisposable> _knowledgeSubscriptions = new();

        /// <summary>
        /// Whether knowledge has been registered.
        /// </summary>
        private bool _knowledgeRegistered;

        /// <summary>
        /// Whether this plugin has been disposed.
        /// </summary>
        private bool _disposed;

        /// <summary>
        /// Lock for knowledge registration.
        /// </summary>
        private readonly object _knowledgeRegistrationLock = new();

        /// <summary>
        /// Message bus reference for knowledge communication.
        /// Set during initialization via InitializeAsync.
        /// </summary>
        protected IMessageBus? MessageBus { get; private set; }

        /// <summary>
        /// State store for persisting plugin state. Initialized during InitializeAsync.
        /// Plugins get this for free — no additional code needed for basic state persistence.
        /// Override <see cref="CreateCustomStateStore"/> to supply a custom backend.
        /// </summary>
        protected IPluginStateStore? StateStore { get; private set; }

        /// <summary>
        /// Policy context providing access to the v6.0 Policy Engine.
        /// Automatically available to all plugins. Null-safe — check <see cref="PolicyContext.IsAvailable"/>
        /// before using. Set during kernel initialization; plugins do not set this themselves.
        /// </summary>
        protected PolicyContext PolicyContext { get; private set; } = PolicyContext.Empty;

        /// <summary>
        /// Called by the kernel to inject the policy context into this plugin.
        /// Plugins should NOT call this directly.
        /// </summary>
        /// <param name="context">The policy context to inject, or null for empty context.</param>
        public void SetPolicyContext(PolicyContext context)
        {
            PolicyContext = context ?? PolicyContext.Empty;
        }

        /// <summary>
        /// Tracked bounded collections for automatic disposal when this plugin is disposed.
        /// </summary>
        private readonly List<IDisposable> _trackedCollections = new();

        /// <summary>
        /// Maximum number of entries in the knowledge cache.
        /// Override in derived classes to customize. Default: 10,000.
        /// Set to 0 for unlimited (not recommended).
        /// </summary>
        protected virtual int MaxKnowledgeCacheSize => 10_000;

        /// <summary>
        /// Unique Plugin ID - must be set by derived classes.
        /// Use a stable identifier like "com.company.plugin.name" for consistency.
        /// </summary>
        public abstract string Id { get; }

        /// <summary>
        /// Gets the category of the plugin.
        /// </summary>
        public abstract PluginCategory Category { get; }

        /// <summary>
        /// Human-readable Name - must be set by derived classes.
        /// </summary>
        public abstract string Name { get; }

        /// <summary>
        /// Semantic Version - default implementation returns "1.0.0".
        /// Supports full semantic versioning (1.0.0-beta, 2.0.0-rc.1+build.123).
        /// </summary>
        public virtual string Version => "1.0.0";

        /// <summary>
        /// Parses a semantic version string into a Version object.
        /// Handles formats like: 1.0.0, v1.0.0, 1.0.0-beta, 1.0.0-rc.1+build.123
        /// </summary>
        protected static Version ParseSemanticVersion(string versionString)
        {
            if (string.IsNullOrWhiteSpace(versionString))
            {
                return new Version(1, 0, 0);
            }

            var version = versionString.Trim();

            // Strip leading 'v' or 'V'
            if (version.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                version = version.Substring(1);
            }

            // Strip prerelease and build metadata (everything after - or +)
            var dashIndex = version.IndexOf('-');
            var plusIndex = version.IndexOf('+');
            var stripIndex = -1;

            if (dashIndex >= 0 && plusIndex >= 0)
            {
                stripIndex = Math.Min(dashIndex, plusIndex);
            }
            else if (dashIndex >= 0)
            {
                stripIndex = dashIndex;
            }
            else if (plusIndex >= 0)
            {
                stripIndex = plusIndex;
            }

            if (stripIndex >= 0)
            {
                version = version.Substring(0, stripIndex);
            }

            // Try to parse as a valid version
            if (System.Version.TryParse(version, out var parsed))
            {
                return parsed;
            }

            // If parsing failed, try to extract numeric parts
            var parts = version.Split('.', StringSplitOptions.RemoveEmptyEntries);
            var numericParts = new List<int>();

            foreach (var part in parts)
            {
                // Extract leading digits from each part
                var numericChars = new string(part.TakeWhile(char.IsDigit).ToArray());
                if (int.TryParse(numericChars, out var num))
                {
                    numericParts.Add(num);
                }
                else
                {
                    numericParts.Add(0);
                }
            }

            // Ensure we have at least major.minor
            while (numericParts.Count < 2)
            {
                numericParts.Add(0);
            }

            return numericParts.Count switch
            {
                2 => new Version(numericParts[0], numericParts[1]),
                3 => new Version(numericParts[0], numericParts[1], numericParts[2]),
                _ => new Version(numericParts[0], numericParts[1], numericParts[2], numericParts[3])
            };
        }

        /// <summary>
        /// Default handshake implementation. Override to provide custom initialization.
        /// </summary>
        public virtual async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
        {
            // Auto-register capabilities and knowledge
            await RegisterWithSystemAsync();

            return new HandshakeResponse
            {
                PluginId = Id,
                Name = Name,
                Version = ParseSemanticVersion(Version),
                Category = Category,
                Success = true,
                ReadyState = PluginReadyState.Ready,
                Capabilities = GetCapabilities(),
                Dependencies = GetDependencies(),
                Metadata = GetMetadata()
            };
        }

        /// <summary>
        /// Default message handler. Override to handle custom messages.
        /// </summary>
        public virtual Task OnMessageAsync(PluginMessage message)
        {
            // Default: log and ignore
            return Task.CompletedTask;
        }

        #region Explicit Lifecycle Methods (HIER-02)

        /// <summary>
        /// Initializes the plugin with the provided cancellation token.
        /// Default implementation calls <see cref="OnHandshakeAsync"/> with a default request.
        /// Override to add custom initialization logic. Always call base.InitializeAsync(ct).
        /// </summary>
        /// <param name="ct">Cancellation token for the initialization operation.</param>
        /// <returns>A task representing the initialization operation.</returns>
        public virtual async Task InitializeAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();

            // Initialize state persistence infrastructure (MessageBus must be injected first
            // via InjectKernelServices before InitializeAsync is called by the kernel).
            StateStore = CreateCustomStateStore();

            // Initialize knowledge cache with a bounded dictionary now that StateStore is available.
            // This gives LRU eviction and optional auto-persistence for free.
            var cache = new BoundedDictionary<string, KnowledgeObject>(1000);
            _trackedCollections.Add(cache);
            _knowledgeCache = cache;

            // Trigger the existing handshake flow which registers capabilities and knowledge
            await OnHandshakeAsync(new HandshakeRequest
            {
                Timestamp = DateTime.UtcNow
            });
        }

        /// <summary>
        /// Activates the plugin for distributed coordination.
        /// Called after all plugins have completed InitializeAsync, when the cluster
        /// membership is resolved and cross-node capabilities are discovered.
        /// <para>
        /// This is Phase 3 of the 3-phase plugin initialization:
        /// <list type="number">
        ///   <item><description>Construction: Zero dependencies (constructor)</description></item>
        ///   <item><description>InitializeAsync: MessageBus available, local setup</description></item>
        ///   <item><description>ActivateAsync: Distributed coordination available, cross-node discovery</description></item>
        /// </list>
        /// </para>
        /// <para>
        /// Default implementation is a no-op. Override in plugins that need cluster-level
        /// coordination (e.g., distributed caching, cross-node replication, federated queries).
        /// Plugins that do not override this method work exactly as before.
        /// </para>
        /// </summary>
        /// <param name="ct">Cancellation token for the activation operation.</param>
        /// <returns>A task representing the activation operation.</returns>
        public virtual Task ActivateAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Executes the plugin's main processing logic.
        /// Default implementation is a no-op. Override to implement plugin-specific processing.
        /// For FeaturePluginBase derivatives, this maps to StartAsync.
        /// For DataPipelinePluginBase derivatives, this sets up the pipeline stage.
        /// </summary>
        /// <param name="ct">Cancellation token for the execution operation.</param>
        /// <returns>A task representing the execution operation.</returns>
        public virtual Task ExecuteAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        /// <summary>
        /// Performs a health check for this plugin.
        /// Called by the kernel's HealthCheckAggregator to determine plugin health.
        /// <para>
        /// Default implementation returns <see cref="HealthStatus.Healthy"/>.
        /// Override in plugins that need to report degraded or unhealthy status
        /// based on internal state (e.g., connection failures, resource exhaustion).
        /// </para>
        /// </summary>
        /// <param name="ct">Cancellation token for the health check operation.</param>
        /// <returns>A <see cref="HealthCheckResult"/> indicating plugin health.</returns>
        public virtual Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(HealthCheckResult.Healthy($"{Name} is healthy"));
        }

        /// <summary>
        /// Shuts down the plugin gracefully, releasing resources in preparation for disposal.
        /// Default implementation unregisters from system registries. Always call base.ShutdownAsync(ct).
        /// </summary>
        /// <param name="ct">Cancellation token for the shutdown operation.</param>
        /// <returns>A task representing the shutdown operation.</returns>
        public virtual async Task ShutdownAsync(CancellationToken ct = default)
        {
            try
            {
                await UnregisterFromSystemAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Best effort during shutdown
            }
        }

        #endregion

        /// <summary>
        /// Capabilities provided by this plugin. Override to declare your capabilities.
        /// These are automatically registered with the central Capability Registry on handshake.
        /// For strategy-based plugins, this is auto-generated from the strategy registry.
        /// </summary>
        protected virtual IReadOnlyList<RegisteredCapability> DeclaredCapabilities => Array.Empty<RegisteredCapability>();

        /// <summary>
        /// Override to provide plugin capabilities for AI agents.
        /// </summary>
        protected virtual List<PluginCapabilityDescriptor> GetCapabilities()
        {
            return new List<PluginCapabilityDescriptor>();
        }

        /// <summary>
        /// Override to declare plugin dependencies.
        /// </summary>
        protected virtual List<PluginDependency> GetDependencies()
        {
            return new List<PluginDependency>();
        }

        /// <summary>
        /// Override to provide additional metadata for AI agents.
        /// </summary>
        protected virtual Dictionary<string, object> GetMetadata()
        {
            return new Dictionary<string, object>
            {
                ["Description"] = $"{Name} plugin for DataWarehouse",
                ["AIFriendly"] = true,
                ["SupportsStreaming"] = false
            };
        }

        #region Knowledge and Capability Integration

        /// <summary>
        /// Sets the capability registry reference for capability registration.
        /// Called by the kernel during plugin initialization.
        /// </summary>
        /// <param name="registry">Capability registry instance.</param>
        public virtual void SetCapabilityRegistry(IPluginCapabilityRegistry? registry)
        {
            CapabilityRegistry = registry;
        }

        /// <summary>
        /// Gets the registration knowledge object for this plugin.
        /// Override to provide plugin-specific knowledge for registration with Universal Intelligence.
        /// </summary>
        /// <returns>Knowledge object describing plugin capabilities, or null if not applicable.</returns>
        /// <remarks>
        /// This method is called during plugin initialization to register the plugin's knowledge
        /// with the Universal Intelligence system (T90). The default implementation builds
        /// knowledge from GetCapabilities() and GetCapabilityRegistrations().
        /// Plugins can override to provide additional custom knowledge.
        /// </remarks>
        public virtual KnowledgeObject? GetRegistrationKnowledge()
        {
            // Build knowledge from capabilities
            var capabilities = GetCapabilities();
            var capabilityRegistrations = GetCapabilityRegistrations();

            if (capabilities.Count == 0 && capabilityRegistrations.Count == 0)
            {
                return null;
            }

            var operations = capabilities.Select(c => c.Name ?? c.CapabilityId).ToArray();
            var constraints = new Dictionary<string, object>
            {
                ["category"] = Category.ToString(),
                ["version"] = Version,
                ["capabilityCount"] = capabilities.Count + capabilityRegistrations.Count
            };

            // Add strategy information if this is a strategy-based plugin
            var strategyInfo = GetStrategyKnowledge();
            if (strategyInfo != null)
            {
                constraints["strategies"] = strategyInfo;
            }

            // Add configuration state
            var configState = GetConfigurationState();
            if (configState.Count > 0)
            {
                constraints["configuration"] = configState;
            }

            return KnowledgeObject.CreateCapabilityKnowledge(
                Id,
                Name,
                operations,
                constraints,
                GetDependencies()?.Select(d => d.RequiredInterface).ToArray()
            );
        }

        /// <summary>
        /// Gets strategy-specific knowledge for strategy-based plugins.
        /// When <see cref="_strategyRegistry"/> is initialized and has registered strategies,
        /// returns a dictionary describing all available strategies automatically.
        /// Override in strategy-based plugins to supplement or replace this default.
        /// </summary>
        /// <returns>Dictionary with strategy information, or null if not a strategy-based plugin.</returns>
        protected virtual Dictionary<string, object>? GetStrategyKnowledge()
        {
            // Auto-populate from registry when strategies have been registered
            if (_strategyRegistry is not null && _strategyRegistry.Count > 0)
            {
                var strategies = _strategyRegistry.GetAll()
                    .Select(s => new Dictionary<string, object>
                    {
                        ["strategyId"] = s.StrategyId,
                        ["name"] = s.Name,
                        ["description"] = s.Description
                    })
                    .ToList<object>();

                var info = new Dictionary<string, object>
                {
                    ["strategies"] = strategies,
                    ["count"] = _strategyRegistry.Count
                };

                if (_strategyRegistry.DefaultStrategyId is not null)
                    info["defaultStrategyId"] = _strategyRegistry.DefaultStrategyId;

                return info;
            }

            // Default: no strategy knowledge (registry not used or empty)
            return null;
        }

        /// <summary>
        /// Gets current configuration state for knowledge reporting.
        /// Override to expose configuration that should be queryable.
        /// </summary>
        /// <returns>Dictionary with configuration key-value pairs.</returns>
        protected virtual Dictionary<string, object> GetConfigurationState()
        {
            // Default: empty configuration
            // Override to expose config like "fipsMode: true", "defaultAlgorithm: aes-256-gcm"
            return new Dictionary<string, object>();
        }

        /// <summary>
        /// Gets detailed capability registrations for the central registry.
        /// Override to provide rich capability metadata beyond basic GetCapabilities().
        /// </summary>
        /// <returns>List of capabilities to register.</returns>
        protected virtual List<RegisteredCapability> GetCapabilityRegistrations()
        {
            // Default: convert basic capabilities to registered capabilities
            var basicCapabilities = GetCapabilities();
            var category = MapPluginCategoryToCapabilityCategory(Category);

            return basicCapabilities.Select(c => new RegisteredCapability
            {
                CapabilityId = $"{Id}.{c.Name ?? c.CapabilityId}",
                DisplayName = c.DisplayName ?? c.Name ?? c.CapabilityId,
                Description = c.Description,
                Category = category,
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                ParameterSchema = c.ParameterSchemaJson,
                Tags = GetCapabilityTags(c),
                Metadata = c.Parameters ?? new Dictionary<string, object>()
            }).ToList();
        }

        /// <summary>
        /// Maps PluginCategory to CapabilityCategory.
        /// </summary>
        private static CapabilityCategory MapPluginCategoryToCapabilityCategory(PluginCategory pluginCategory)
        {
            return pluginCategory switch
            {
                PluginCategory.StorageProvider => CapabilityCategory.Storage,
                PluginCategory.SecurityProvider => CapabilityCategory.Security,
                PluginCategory.DataTransformationProvider => CapabilityCategory.Pipeline,
                PluginCategory.MetadataIndexingProvider => CapabilityCategory.Metadata,
                PluginCategory.AIProvider => CapabilityCategory.AI,
                PluginCategory.GovernanceProvider => CapabilityCategory.Governance,
                _ => CapabilityCategory.Custom
            };
        }

        /// <summary>
        /// Gets tags for a capability descriptor.
        /// Override to provide custom tagging logic.
        /// </summary>
        protected virtual string[] GetCapabilityTags(PluginCapabilityDescriptor capability)
        {
            var tags = new List<string> { Category.ToString().ToLowerInvariant() };

            if (capability.RequiresApproval)
                tags.Add("requires-approval");

            return tags.ToArray();
        }

        /// <summary>
        /// Handles incoming knowledge requests from the Universal Intelligence system.
        /// Override to respond to knowledge queries about plugin capabilities.
        /// </summary>
        /// <param name="request">Knowledge request object.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Knowledge response with matching knowledge objects.</returns>
        /// <remarks>
        /// This method is called when the Universal Intelligence system queries plugin knowledge.
        /// The default implementation handles standard query types.
        /// Plugins should override to provide dynamic knowledge responses.
        /// </remarks>
        public virtual Task<KnowledgeResponse> HandleKnowledgeQueryAsync(KnowledgeRequest request, CancellationToken ct = default)
        {
            var results = new List<KnowledgeObject>();

            // Handle standard query topics
            var topic = request.Topic.ToLowerInvariant();

            if (topic == "plugin.capabilities" || topic == "capabilities")
            {
                var knowledge = GetRegistrationKnowledge();
                if (knowledge != null)
                {
                    results.Add(knowledge);
                }
            }
            else if (topic == "plugin.strategies" || topic == "strategies")
            {
                var strategyKnowledge = BuildStrategyListKnowledge(request.QueryParameters);
                if (strategyKnowledge != null)
                {
                    results.Add(strategyKnowledge);
                }
            }
            else if (topic == "plugin.configuration" || topic == "configuration")
            {
                var configKnowledge = BuildConfigurationKnowledge();
                results.Add(configKnowledge);
            }
            else if (topic == "plugin.statistics" || topic == "statistics")
            {
                var statsKnowledge = BuildStatisticsKnowledge();
                if (statsKnowledge != null)
                {
                    results.Add(statsKnowledge);
                }
            }

            // Allow derived classes to add custom knowledge
            var customResults = HandleCustomKnowledgeQuery(topic, request.QueryParameters, ct);
            results.AddRange(customResults);

            return Task.FromResult(new KnowledgeResponse
            {
                RequestId = request.RequestId,
                Success = true,
                Results = results.ToArray()
            });
        }

        /// <summary>
        /// Handles custom knowledge queries not covered by standard topics.
        /// Override to support plugin-specific query types.
        /// </summary>
        /// <param name="topic">Query topic.</param>
        /// <param name="parameters">Query parameters.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>List of matching knowledge objects.</returns>
        protected virtual IEnumerable<KnowledgeObject> HandleCustomKnowledgeQuery(
            string topic,
            Dictionary<string, object>? parameters,
            CancellationToken ct)
        {
            return Enumerable.Empty<KnowledgeObject>();
        }

        /// <summary>
        /// Builds knowledge object listing available strategies.
        /// Override in strategy-based plugins.
        /// </summary>
        protected virtual KnowledgeObject? BuildStrategyListKnowledge(Dictionary<string, object>? filters)
        {
            var strategyInfo = GetStrategyKnowledge();
            if (strategyInfo == null)
            {
                return null;
            }

            return new KnowledgeObject
            {
                Id = $"{Id}.strategies.{Guid.NewGuid():N}",
                Topic = "plugin.strategies",
                SourcePluginId = Id,
                SourcePluginName = Name,
                KnowledgeType = "capability",
                Description = $"Available strategies for {Name}",
                Payload = strategyInfo,
                Tags = new[] { "strategies", Category.ToString().ToLowerInvariant() }
            };
        }

        /// <summary>
        /// Builds knowledge object with current configuration.
        /// </summary>
        protected virtual KnowledgeObject BuildConfigurationKnowledge()
        {
            return new KnowledgeObject
            {
                Id = $"{Id}.configuration.{Guid.NewGuid():N}",
                Topic = "plugin.configuration",
                SourcePluginId = Id,
                SourcePluginName = Name,
                KnowledgeType = "metric",
                Description = $"Current configuration for {Name}",
                Payload = GetConfigurationState(),
                Tags = new[] { "configuration", Category.ToString().ToLowerInvariant() }
            };
        }

        /// <summary>
        /// Builds knowledge object with plugin statistics.
        /// Override to provide plugin-specific statistics.
        /// </summary>
        protected virtual KnowledgeObject? BuildStatisticsKnowledge()
        {
            // Default: no statistics
            // Override in plugins that track usage statistics
            return null;
        }

        /// <summary>
        /// Registers all plugin knowledge and capabilities.
        /// Called automatically during InitializeAsync.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        protected virtual async Task RegisterAllKnowledgeAsync(CancellationToken ct = default)
        {
            lock (_knowledgeRegistrationLock)
            {
                if (_knowledgeRegistered)
                    return;
                _knowledgeRegistered = true;
            }

            // Register capabilities with central registry
            await RegisterCapabilitiesAsync(ct);

            // Register knowledge with Universal Intelligence
            await RegisterKnowledgeAsync(ct);

            // Subscribe to knowledge queries
            SubscribeToKnowledgeRequests(ct);
        }

        /// <summary>
        /// Registers capabilities with the central capability registry.
        /// IDEMPOTENT: Safe to call multiple times. Only registers capabilities that aren't already registered.
        /// </summary>
        protected virtual async Task RegisterCapabilitiesAsync(CancellationToken ct = default)
        {
            if (CapabilityRegistry == null)
            {
                return;
            }

            try
            {
                var capabilities = GetCapabilityRegistrations();
                foreach (var capability in capabilities)
                {
                    // Skip if already registered by this plugin (idempotent protection)
                    if (_registeredCapabilityIds.Contains(capability.CapabilityId))
                    {
                        continue;
                    }

                    // RegisterAsync in PluginCapabilityRegistry uses TryAdd which is idempotent
                    // Returns true if added, false if already exists
                    if (await CapabilityRegistry.RegisterAsync(capability, ct))
                    {
                        _registeredCapabilityIds.Add(capability.CapabilityId);
                    }
                }
            }
            catch (Exception)
            {
                // Graceful degradation: capability registration failed
                // Plugin can still function without registry
            }
        }

        /// <summary>
        /// Auto-registers plugin knowledge with Universal Intelligence during initialization.
        /// Called automatically by RegisterAllKnowledgeAsync.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Task representing the async operation.</returns>
        protected virtual async Task RegisterKnowledgeAsync(CancellationToken ct = default)
        {
            if (MessageBus == null)
            {
                return;
            }

            var knowledge = GetRegistrationKnowledge();
            if (knowledge == null)
            {
                return;
            }

            try
            {
                var message = new PluginMessage
                {
                    Type = "intelligence.knowledge.register",
                    Payload = new Dictionary<string, object>
                    {
                        ["Knowledge"] = knowledge,
                        ["PluginId"] = Id,
                        ["PluginName"] = Name,
                        ["Timestamp"] = DateTimeOffset.UtcNow
                    }
                };

                await MessageBus.PublishAsync("intelligence.knowledge.register", message, ct);

                // Cache the registered knowledge (null-safe: cache available after InitializeAsync)
                if (_knowledgeCache != null)
                    _knowledgeCache[knowledge.Topic] = knowledge;
            }
            catch (Exception)
            {
                // Graceful degradation: Universal Intelligence not available
            }
        }

        /// <summary>
        /// Subscribes to knowledge request messages from Universal Intelligence.
        /// Called automatically by RegisterAllKnowledgeAsync.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        protected virtual void SubscribeToKnowledgeRequests(CancellationToken ct = default)
        {
            if (MessageBus == null)
            {
                return;
            }

            try
            {
                // Subscribe to direct knowledge requests
                var directSub = MessageBus.Subscribe($"intelligence.knowledge.request.{Id}", async (message) =>
                {
                    if (message.Payload.TryGetValue("Request", out var reqObj) && reqObj is KnowledgeRequest request)
                    {
                        var response = await HandleKnowledgeQueryAsync(request, ct);

                        // Publish response
                        var responseMessage = new PluginMessage
                        {
                            Type = "intelligence.knowledge.response",
                            CorrelationId = message.CorrelationId,
                            Payload = new Dictionary<string, object>
                            {
                                ["Response"] = response,
                                ["PluginId"] = Id
                            }
                        };

                        await MessageBus.PublishAsync($"intelligence.knowledge.response.{message.CorrelationId}", responseMessage, ct);
                    }
                });
                _knowledgeSubscriptions.Add(directSub);

                // Subscribe to broadcast knowledge queries
                var broadcastSub = MessageBus.Subscribe("intelligence.knowledge.query.broadcast", async (message) =>
                {
                    if (message.Payload.TryGetValue("Request", out var reqObj) && reqObj is KnowledgeRequest request)
                    {
                        var response = await HandleKnowledgeQueryAsync(request, ct);

                        if (response.Results.Length > 0)
                        {
                            var responseMessage = new PluginMessage
                            {
                                Type = "intelligence.knowledge.response",
                                CorrelationId = message.CorrelationId,
                                Payload = new Dictionary<string, object>
                                {
                                    ["Response"] = response,
                                    ["PluginId"] = Id
                                }
                            };

                            await MessageBus.PublishAsync($"intelligence.knowledge.response.{message.CorrelationId}", responseMessage, ct);
                        }
                    }
                });
                _knowledgeSubscriptions.Add(broadcastSub);
            }
            catch (Exception)
            {
                // Graceful degradation: Unable to subscribe
            }
        }

        /// <summary>
        /// Sets the message bus reference for knowledge communication.
        /// Should be called during plugin initialization.
        /// </summary>
        /// <param name="messageBus">Message bus instance.</param>
        public virtual void SetMessageBus(IMessageBus? messageBus)
        {
            MessageBus = messageBus;
        }

        /// <summary>
        /// Gets the unified system-wide configuration for this plugin instance.
        /// Injected by the kernel during plugin registration.
        /// All plugins receive the same global configuration object.
        /// Named SystemConfiguration to avoid collision with domain-specific Configuration properties
        /// (e.g., TamperProofConfiguration in TamperProofProviderPluginBase).
        /// </summary>
        protected DataWarehouseConfiguration SystemConfiguration { get; private set; } = ConfigurationPresets.CreateStandard();

        /// <summary>
        /// Injects the unified system configuration into this plugin.
        /// Called by the kernel during plugin registration.
        /// </summary>
        public void InjectConfiguration(DataWarehouseConfiguration config)
        {
            SystemConfiguration = config ?? throw new ArgumentNullException(nameof(config));
        }

        /// <summary>
        /// Injects kernel services. Called by kernel during plugin initialization.
        /// SEALED to prevent plugins from overriding this method and intercepting kernel
        /// service references via reflection (ISO-01, CVSS 9.4 mitigation).
        /// Use <see cref="OnKernelServicesInjected"/> for post-injection hooks.
        /// </summary>
        public void InjectKernelServices(
            IMessageBus? messageBus,
            IPluginCapabilityRegistry? capabilityRegistry,
            IKnowledgeLake? knowledgeLake)
        {
            MessageBus = messageBus;
            CapabilityRegistry = capabilityRegistry;
            KnowledgeLake = knowledgeLake;

            // Allow subclasses to react to service injection (e.g., subscribe to bus topics)
            OnKernelServicesInjected();
        }

        /// <summary>
        /// Called after kernel services have been injected. Override to perform post-injection
        /// setup such as subscribing to message bus topics or registering capabilities.
        /// This replaces overriding InjectKernelServices which is now sealed for security (ISO-01).
        /// </summary>
        protected virtual void OnKernelServicesInjected()
        {
            // Default: no-op. Subclasses can override for post-injection initialization.
        }

        /// <summary>
        /// Gets static knowledge to register at load time.
        /// Override to provide plugin-specific knowledge.
        /// </summary>
        protected virtual IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
        {
            // Default: Generate basic capability knowledge
            var knowledge = new List<KnowledgeObject>();

            // Add plugin capability summary
            var capabilities = DeclaredCapabilities;
            if (capabilities.Count > 0)
            {
                knowledge.Add(KnowledgeObject.CreateCapabilityKnowledge(
                    Id,
                    Name,
                    capabilities.Select(c => c.CapabilityId).ToArray(),
                    new Dictionary<string, object>
                    {
                        ["category"] = Category.ToString(),
                        ["version"] = Version,
                        ["capabilityCount"] = capabilities.Count
                    }
                ));
            }

            return knowledge;
        }

        /// <summary>
        /// Handles dynamic knowledge queries at runtime for the new knowledge lake system.
        /// Override to respond to specific query topics.
        /// </summary>
        protected virtual Task<IReadOnlyList<KnowledgeObject>> HandleDynamicKnowledgeQueryAsync(
            KnowledgeRequest request,
            CancellationToken ct = default)
        {
            // Default: No dynamic knowledge
            return Task.FromResult<IReadOnlyList<KnowledgeObject>>(Array.Empty<KnowledgeObject>());
        }

        /// <summary>
        /// Registers capabilities and knowledge with the system.
        /// Called automatically during handshake.
        /// </summary>
        protected virtual async Task RegisterWithSystemAsync(CancellationToken ct = default)
        {
            // Register capabilities
            await RegisterCapabilitiesAsync(ct);

            // Register static knowledge
            await RegisterStaticKnowledgeAsync(ct);

            // Subscribe to knowledge queries
            SubscribeToKnowledgeQueries(ct);
        }

        /// <summary>
        /// Registers static knowledge with the knowledge lake.
        /// </summary>
        private async Task RegisterStaticKnowledgeAsync(CancellationToken ct)
        {
            if (KnowledgeLake == null) return;

            try
            {
                var knowledge = GetStaticKnowledge();
                foreach (var k in knowledge)
                {
                    await KnowledgeLake.StoreAsync(k, isStatic: true, ct: ct);
                    _registeredKnowledgeIds.Add(k.Id);
                }
            }
            catch
            {
                // Graceful degradation
            }
        }

        /// <summary>
        /// Subscribes to knowledge query messages.
        /// </summary>
        private void SubscribeToKnowledgeQueries(CancellationToken ct)
        {
            if (MessageBus == null) return;

            try
            {
                // Subscribe to direct queries
                var sub = MessageBus.Subscribe($"knowledge.query.{Id}", async (message) =>
                {
                    if (message.Payload.TryGetValue("Request", out var reqObj) && reqObj is KnowledgeRequest request)
                    {
                        var results = await HandleDynamicKnowledgeQueryAsync(request, ct);

                        if (results.Count > 0 && MessageBus != null)
                        {
                            var response = new PluginMessage
                            {
                                Type = "knowledge.response",
                                CorrelationId = message.CorrelationId,
                                Payload = new Dictionary<string, object>
                                {
                                    ["Results"] = results,
                                    ["PluginId"] = Id
                                }
                            };
                            await MessageBus.PublishAsync($"knowledge.response.{message.CorrelationId}", response, ct);
                        }
                    }
                });
                _knowledgeSubscriptions.Add(sub);
            }
            catch
            {
                // Graceful degradation
            }
        }

        /// <summary>
        /// Unregisters from the system. Called during shutdown.
        /// </summary>
        protected virtual async Task UnregisterFromSystemAsync(CancellationToken ct = default)
        {
            // Dispose subscriptions
            foreach (var sub in _knowledgeSubscriptions)
            {
                try { sub.Dispose(); } catch { /* Best-effort cleanup */ }
            }
            _knowledgeSubscriptions.Clear();

            // Unregister capabilities
            if (CapabilityRegistry != null)
            {
                await CapabilityRegistry.UnregisterPluginAsync(Id, ct);
            }
            _registeredCapabilityIds.Clear();

            // Remove knowledge
            if (KnowledgeLake != null)
            {
                await KnowledgeLake.RemoveByPluginAsync(Id, ct);
            }
            _registeredKnowledgeIds.Clear();
        }

        /// <summary>
        /// Gets cached knowledge object by topic.
        /// </summary>
        /// <param name="topic">Knowledge topic.</param>
        /// <returns>Cached knowledge object, or null if not found.</returns>
        protected KnowledgeObject? GetCachedKnowledge(string topic)
        {
            if (_knowledgeCache == null) return null;
            _knowledgeCache.TryGetValue(topic, out var knowledge);
            return knowledge;
        }

        /// <summary>
        /// Caches a knowledge object for later retrieval.
        /// </summary>
        /// <param name="topic">Knowledge topic.</param>
        /// <param name="knowledge">Knowledge object to cache.</param>
        protected void CacheKnowledge(string topic, KnowledgeObject knowledge)
        {
            // BoundedDictionary handles LRU eviction automatically — no manual eviction needed.
            // Guard for pre-InitializeAsync calls (cache is null until InitializeAsync completes).
            if (_knowledgeCache != null)
                _knowledgeCache[topic] = knowledge;
        }

        /// <summary>
        /// Clears all cached knowledge objects.
        /// </summary>
        protected void ClearKnowledgeCache()
        {
            _knowledgeCache?.Clear();
        }

        /// <summary>
        /// Cleans up knowledge and capability registrations.
        /// Called during plugin shutdown.
        /// </summary>
        protected virtual async Task UnregisterKnowledgeAsync(CancellationToken ct = default)
        {
            // Dispose knowledge subscriptions
            foreach (var sub in _knowledgeSubscriptions)
            {
                sub.Dispose();
            }
            _knowledgeSubscriptions.Clear();

            // Unregister capabilities
            if (CapabilityRegistry != null)
            {
                await CapabilityRegistry.UnregisterPluginAsync(Id, ct);
            }
            _registeredCapabilityIds.Clear();

            // Clear knowledge cache
            ClearKnowledgeCache();
            _knowledgeRegistered = false;
        }

        /// <summary>
        /// Converts basic capability descriptors to registered capabilities.
        /// Used when DeclaredCapabilities is not overridden.
        /// </summary>
        protected RegisteredCapability ToRegisteredCapability(PluginCapabilityDescriptor descriptor)
        {
            return new RegisteredCapability
            {
                CapabilityId = $"{Id}.{descriptor.Name ?? descriptor.CapabilityId}",
                DisplayName = descriptor.DisplayName ?? descriptor.Name ?? descriptor.CapabilityId,
                Description = descriptor.Description,
                Category = MapToCapabilityCategory(Category),
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                ParameterSchema = descriptor.ParameterSchemaJson,
                Tags = GetDefaultTags(),
                Metadata = descriptor.Parameters ?? new Dictionary<string, object>()
            };
        }

        private CapabilityCategory MapToCapabilityCategory(PluginCategory category)
        {
            return category switch
            {
                PluginCategory.StorageProvider => CapabilityCategory.Storage,
                PluginCategory.SecurityProvider => CapabilityCategory.Security,
                PluginCategory.DataTransformationProvider => CapabilityCategory.Pipeline,
                PluginCategory.MetadataIndexingProvider => CapabilityCategory.Metadata,
                PluginCategory.AIProvider => CapabilityCategory.AI,
                PluginCategory.GovernanceProvider => CapabilityCategory.Governance,
                _ => CapabilityCategory.Custom
            };
        }

        protected virtual string[] GetDefaultTags()
        {
            return new[] { Category.ToString().ToLowerInvariant(), "plugin" };
        }

        #endregion

        #region State Persistence

        /// <summary>
        /// Override to provide a custom state store implementation.
        /// The default creates a <see cref="DefaultPluginStateStore"/> that routes all operations
        /// through the message bus to whatever storage backend is currently configured.
        /// Return <c>null</c> to disable automatic state persistence.
        /// </summary>
        /// <returns>An <see cref="IPluginStateStore"/> instance, or <c>null</c> to disable persistence.</returns>
        protected virtual IPluginStateStore? CreateCustomStateStore()
            => MessageBus != null ? new DefaultPluginStateStore(MessageBus) : null;

        /// <summary>
        /// Called before state is persisted via <see cref="SaveStateAsync"/>.
        /// Override to perform validation, encryption, or transformation of data before storage.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        protected virtual Task OnBeforeStatePersistAsync(CancellationToken ct = default)
            => Task.CompletedTask;

        /// <summary>
        /// Called after state is successfully persisted via <see cref="SaveStateAsync"/>.
        /// Override to emit metrics, logs, or notifications after storage completes.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        protected virtual Task OnAfterStatePersistAsync(CancellationToken ct = default)
            => Task.CompletedTask;

        /// <summary>
        /// Saves arbitrary binary state for this plugin using the state store.
        /// Calls <see cref="OnBeforeStatePersistAsync"/> before and <see cref="OnAfterStatePersistAsync"/> after.
        /// No-op when no state store is configured.
        /// </summary>
        /// <param name="key">State key scoped to this plugin's namespace.</param>
        /// <param name="data">Raw binary payload to persist.</param>
        /// <param name="ct">Cancellation token.</param>
        protected async Task SaveStateAsync(string key, byte[] data, CancellationToken ct = default)
        {
            if (StateStore == null) return;
            await OnBeforeStatePersistAsync(ct).ConfigureAwait(false);
            await StateStore.SaveAsync(Id, key, data, ct).ConfigureAwait(false);
            await OnAfterStatePersistAsync(ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Loads previously saved state for this plugin from the state store.
        /// Returns <c>null</c> when no state store is configured or the key does not exist.
        /// </summary>
        /// <param name="key">State key scoped to this plugin's namespace.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Stored bytes, or <c>null</c> if not found or persistence is disabled.</returns>
        protected async Task<byte[]?> LoadStateAsync(string key, CancellationToken ct = default)
            => StateStore != null ? await StateStore.LoadAsync(Id, key, ct).ConfigureAwait(false) : null;

        /// <summary>
        /// Deletes a previously saved state entry for this plugin.
        /// No-op when no state store is configured or the key does not exist.
        /// </summary>
        /// <param name="key">State key scoped to this plugin's namespace.</param>
        /// <param name="ct">Cancellation token.</param>
        protected async Task DeleteStateAsync(string key, CancellationToken ct = default)
        {
            if (StateStore != null)
                await StateStore.DeleteAsync(Id, key, ct).ConfigureAwait(false);
        }

        /// <summary>
        /// Lists all state keys previously stored for this plugin.
        /// Returns an empty list when no state store is configured.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        protected async Task<IReadOnlyList<string>> ListStateKeysAsync(CancellationToken ct = default)
            => StateStore != null
                ? await StateStore.ListKeysAsync(Id, ct).ConfigureAwait(false)
                : Array.Empty<string>();

        // -------------------------------------------------------------------------
        // Bounded collection factory methods
        // -------------------------------------------------------------------------

        /// <summary>
        /// Creates a bounded dictionary that is automatically tracked by this plugin's lifecycle.
        /// The dictionary uses LRU eviction when capacity is exceeded and optionally auto-persists
        /// changes through the plugin's <see cref="StateStore"/>.
        /// </summary>
        /// <typeparam name="TKey">The type of keys. Must be non-null.</typeparam>
        /// <typeparam name="TValue">The type of values.</typeparam>
        /// <param name="collectionName">
        /// Logical name used as the state key for auto-persistence (e.g., "pending-writes").
        /// </param>
        /// <param name="maxCapacity">Maximum number of entries before LRU eviction triggers.</param>
        /// <returns>A new <see cref="BoundedDictionary{TKey,TValue}"/> instance.</returns>
        protected BoundedDictionary<TKey, TValue> CreateBoundedDictionary<TKey, TValue>(
            string collectionName, int maxCapacity) where TKey : notnull
        {
            var dict = new BoundedDictionary<TKey, TValue>(1000);
            _trackedCollections.Add(dict);
            return dict;
        }

        /// <summary>
        /// Creates a bounded list that is automatically tracked by this plugin's lifecycle.
        /// When capacity is exceeded the oldest entry is removed.
        /// Optionally auto-persists through the plugin's <see cref="StateStore"/>.
        /// </summary>
        /// <typeparam name="T">The type of elements.</typeparam>
        /// <param name="collectionName">Logical name used as the state key for auto-persistence.</param>
        /// <param name="maxCapacity">Maximum number of elements.</param>
        /// <returns>A new <see cref="BoundedList{T}"/> instance.</returns>
        protected BoundedList<T> CreateBoundedList<T>(string collectionName, int maxCapacity)
        {
            var list = new BoundedList<T>(maxCapacity, StateStore, Id, $"collection.{collectionName}");
            _trackedCollections.Add(list);
            return list;
        }

        /// <summary>
        /// Creates a bounded queue that is automatically tracked by this plugin's lifecycle.
        /// When capacity is exceeded the oldest entry is dequeued.
        /// Optionally auto-persists through the plugin's <see cref="StateStore"/>.
        /// </summary>
        /// <typeparam name="T">The type of elements.</typeparam>
        /// <param name="collectionName">Logical name used as the state key for auto-persistence.</param>
        /// <param name="maxCapacity">Maximum number of elements.</param>
        /// <returns>A new <see cref="BoundedQueue{T}"/> instance.</returns>
        protected BoundedQueue<T> CreateBoundedQueue<T>(string collectionName, int maxCapacity)
        {
            var queue = new BoundedQueue<T>(maxCapacity, StateStore, Id, $"collection.{collectionName}");
            _trackedCollections.Add(queue);
            return queue;
        }

        #endregion

        #region Strategy Registry and Dispatch

        /// <summary>
        /// Lazy-initialized backing store for the strategy registry.
        /// Null until first accessed via <see cref="StrategyRegistry"/>.
        /// Non-strategy plugins incur zero overhead.
        /// </summary>
        private StrategyRegistry<IStrategy>? _strategyRegistry;

        /// <summary>Lock protecting lazy initialization of <see cref="_strategyRegistry"/>.</summary>
        private readonly object _strategyRegistryLock = new();

        /// <summary>
        /// Gets the strategy registry for this plugin. Lazily initialized on first access.
        /// Non-strategy plugins that never call this property pay no overhead.
        /// </summary>
        protected StrategyRegistry<IStrategy> StrategyRegistry
        {
            get
            {
                if (_strategyRegistry is not null) return _strategyRegistry;
                lock (_strategyRegistryLock)
                {
                    _strategyRegistry ??= new StrategyRegistry<IStrategy>(s => s.StrategyId);
                }
                return _strategyRegistry;
            }
        }

        /// <summary>
        /// Optional ACL provider for strategy access control.
        /// When null, all strategies are accessible (allow-all default).
        /// Inject an <see cref="IStrategyAclProvider"/> to restrict strategy access by principal.
        /// </summary>
        protected IStrategyAclProvider? StrategyAclProvider { get; set; }

        /// <summary>
        /// Registers a strategy with this plugin's registry.
        /// If the plugin is already initialized, calls <see cref="IStrategy.InitializeAsync"/> on the strategy.
        /// </summary>
        /// <param name="strategy">The strategy to register.</param>
        protected void RegisterStrategy(IStrategy strategy)
        {
            ArgumentNullException.ThrowIfNull(strategy);
            StrategyRegistry.Register(strategy);
        }

        /// <summary>
        /// Resolves a strategy by ID, optionally enforcing ACL via <see cref="StrategyAclProvider"/>.
        /// Returns null if the strategy is not found (caller decides how to handle absence).
        /// </summary>
        /// <typeparam name="TStrategy">Concrete strategy type. Must implement <see cref="IStrategy"/>.</typeparam>
        /// <param name="strategyId">The strategy ID to resolve.</param>
        /// <param name="identity">
        /// Optional command identity. When provided and <see cref="StrategyAclProvider"/> is set,
        /// ACL is checked before returning the strategy.
        /// </param>
        /// <returns>The resolved strategy cast to <typeparamref name="TStrategy"/>, or null if not found.</returns>
        /// <exception cref="UnauthorizedAccessException">
        /// Thrown when the identity is denied access by <see cref="StrategyAclProvider"/>.
        /// </exception>
        protected TStrategy? ResolveStrategy<TStrategy>(string strategyId, CommandIdentity? identity = null)
            where TStrategy : class, IStrategy
        {
            var strategy = StrategyRegistry.Get(strategyId);
            if (strategy is null) return null;

            if (identity is not null && StrategyAclProvider is not null)
            {
                if (!StrategyAclProvider.IsStrategyAllowed(strategyId, identity))
                {
                    throw new UnauthorizedAccessException(
                        $"Principal '{identity.EffectivePrincipalId}' is not allowed to use strategy '{strategyId}'.");
                }
            }

            return strategy as TStrategy;
        }

        /// <summary>
        /// Resolves a strategy by ID (or falls back to the default), throwing if none is found.
        /// Falls back to <see cref="GetDefaultStrategyId"/> then the registry's DefaultStrategyId
        /// when <paramref name="strategyId"/> is null or empty.
        /// </summary>
        /// <typeparam name="TStrategy">Concrete strategy type. Must implement <see cref="IStrategy"/>.</typeparam>
        /// <param name="strategyId">The strategy ID, or null/empty to use the default.</param>
        /// <param name="identity">Optional command identity for ACL enforcement.</param>
        /// <returns>The resolved strategy cast to <typeparamref name="TStrategy"/>.</returns>
        /// <exception cref="InvalidOperationException">Thrown when no strategy is found.</exception>
        /// <exception cref="UnauthorizedAccessException">
        /// Thrown when the identity is denied access by <see cref="StrategyAclProvider"/>.
        /// </exception>
        protected TStrategy ResolveStrategyOrDefault<TStrategy>(string? strategyId, CommandIdentity? identity = null)
            where TStrategy : class, IStrategy
        {
            var effectiveId = string.IsNullOrWhiteSpace(strategyId)
                ? (GetDefaultStrategyId() ?? StrategyRegistry.DefaultStrategyId)
                : strategyId;

            if (string.IsNullOrWhiteSpace(effectiveId))
                throw new InvalidOperationException(
                    $"No strategy ID provided and no default strategy is configured for plugin '{Id}'.");

            var resolved = ResolveStrategy<TStrategy>(effectiveId, identity);
            if (resolved is null)
                throw new InvalidOperationException(
                    $"Strategy '{effectiveId}' is not registered in plugin '{Id}'.");

            return resolved;
        }

        /// <summary>
        /// Returns the domain-specific default strategy ID for this plugin.
        /// Override in domain base classes to provide domain-specific defaults
        /// (e.g., EncryptionPluginBase might return "aes-256-gcm").
        /// Returns null by default, deferring to the registry's DefaultStrategyId.
        /// </summary>
        protected virtual string? GetDefaultStrategyId() => null;

        /// <summary>
        /// Resolves a strategy and executes an async operation against it.
        /// This is the primary dispatch pattern for strategy-based plugins.
        /// </summary>
        /// <typeparam name="TStrategy">Concrete strategy type.</typeparam>
        /// <typeparam name="TResult">Return type of the operation.</typeparam>
        /// <param name="strategyId">Strategy ID, or null to use the default.</param>
        /// <param name="identity">Optional command identity for ACL enforcement.</param>
        /// <param name="operation">Async operation to execute against the resolved strategy.</param>
        /// <param name="ct">Cancellation token passed to the operation.</param>
        /// <returns>The result of the operation.</returns>
        protected async Task<TResult> ExecuteWithStrategyAsync<TStrategy, TResult>(
            string? strategyId,
            CommandIdentity? identity,
            Func<TStrategy, Task<TResult>> operation,
            CancellationToken ct = default)
            where TStrategy : class, IStrategy
        {
            ArgumentNullException.ThrowIfNull(operation);
            ct.ThrowIfCancellationRequested();

            var strategy = ResolveStrategyOrDefault<TStrategy>(strategyId, identity);
            return await operation(strategy).ConfigureAwait(false);
        }

        /// <summary>
        /// Scans the given assemblies for non-abstract concrete types implementing
        /// <see cref="IStrategy"/>, instantiates them, and registers each one.
        /// Types that cannot be instantiated are silently skipped.
        /// </summary>
        /// <param name="assemblies">Assemblies to scan.</param>
        /// <returns>Number of strategies successfully discovered and registered.</returns>
        protected int DiscoverStrategiesFromAssembly(params Assembly[] assemblies)
        {
            return StrategyRegistry.DiscoverFromAssembly(assemblies);
        }

        #endregion

        #region IDisposable / IAsyncDisposable

        /// <summary>
        /// Gets whether this plugin has been disposed.
        /// </summary>
        protected bool IsDisposed => _disposed;

        /// <summary>
        /// Throws <see cref="ObjectDisposedException"/> if this plugin has been disposed.
        /// </summary>
        protected void ThrowIfDisposed()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
        }

        /// <summary>
        /// Releases managed and unmanaged resources.
        /// Override in derived classes to add cleanup logic. Always call base.Dispose(disposing).
        /// </summary>
        /// <param name="disposing">true if called from Dispose(); false if called from DisposeAsync().</param>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                // Dispose knowledge subscriptions
                foreach (var sub in _knowledgeSubscriptions)
                {
                    try { sub.Dispose(); } catch { /* Swallow during cleanup */ }
                }
                _knowledgeSubscriptions.Clear();

                // Dispose all registered strategies (sync path -- prefer DisposeAsync for async shutdown)
                if (_strategyRegistry is not null)
                {
                    foreach (var strategy in _strategyRegistry.GetAll())
                    {
                        try { strategy.Dispose(); } catch { /* Swallow during cleanup */ }
                    }
                }

                // Dispose all tracked bounded collections (includes _knowledgeCache)
                foreach (var collection in _trackedCollections)
                {
                    try { collection.Dispose(); } catch { /* Swallow during cleanup */ }
                }
                _trackedCollections.Clear();

                // Clear registration tracking
                _registeredCapabilityIds.Clear();
                _registeredKnowledgeIds.Clear();
            }

            _disposed = true;
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Performs async cleanup. Override in derived classes for async disposal.
        /// Always call base.DisposeAsyncCore() at the end of your override.
        /// </summary>
        protected virtual async ValueTask DisposeAsyncCore()
        {
            // Async cleanup: shutdown and dispose all registered strategies
            if (_strategyRegistry is not null)
            {
                foreach (var strategy in _strategyRegistry.GetAll())
                {
                    try { await strategy.ShutdownAsync().ConfigureAwait(false); } catch { /* Best-effort */ }
                    try { await strategy.DisposeAsync().ConfigureAwait(false); } catch { /* Best-effort */ }
                }
            }

            // Async cleanup: unregister from system if message bus is available
            if (MessageBus != null)
            {
                try
                {
                    await UnregisterFromSystemAsync();
                }
                catch
                {
                    // Swallow during cleanup -- best effort
                }
            }
        }

        /// <inheritdoc/>
        public async ValueTask DisposeAsync()
        {
            await DisposeAsyncCore();
            Dispose(false);
            GC.SuppressFinalize(this);
        }

        #endregion
    }

    // Legacy base classes (DataTransformationPluginBase, SecurityProviderPluginBase, InterfacePluginBase,
    // PipelinePluginBase, EncryptionPluginBase, CompressionPluginBase, ReplicationPluginBase,
    // AccessControlPluginBase) have been deleted. Consumers migrated to v3.0 hierarchy.
    //
    // Extracted to standalone files (consumers not yet migrated):
    //   - LegacyStoragePluginBases.cs: StorageProviderPluginBase, ListableStoragePluginBase,
    //     TieredStoragePluginBase, CacheableStoragePluginBase, IndexableStoragePluginBase
    //   - LegacyConsensusPluginBase.cs: ConsensusPluginBase, ClusterState
    //   - LegacyContainerManagerPluginBase.cs: ContainerManagerPluginBase
}
