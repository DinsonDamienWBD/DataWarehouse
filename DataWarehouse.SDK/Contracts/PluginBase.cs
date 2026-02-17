using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts.Compression;
using DataWarehouse.SDK.Contracts.Encryption;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Utilities;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;

using HierarchyInterfacePluginBase = DataWarehouse.SDK.Contracts.Hierarchy.InterfacePluginBase;
using HierarchyReplicationPluginBase = DataWarehouse.SDK.Contracts.Hierarchy.ReplicationPluginBase;
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
        /// </summary>
        private readonly ConcurrentDictionary<string, KnowledgeObject> _knowledgeCache = new();

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
        /// Override in strategy-based plugins to expose available strategies.
        /// </summary>
        /// <returns>Dictionary with strategy information, or null if not a strategy-based plugin.</returns>
        protected virtual Dictionary<string, object>? GetStrategyKnowledge()
        {
            // Default: no strategy knowledge
            // Override in strategy-based plugins (encryption, compression, etc.)
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

                // Cache the registered knowledge
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
        /// Injects kernel services. Called by kernel during plugin initialization.
        /// </summary>
        public virtual void InjectKernelServices(
            IMessageBus? messageBus,
            IPluginCapabilityRegistry? capabilityRegistry,
            IKnowledgeLake? knowledgeLake)
        {
            MessageBus = messageBus;
            CapabilityRegistry = capabilityRegistry;
            KnowledgeLake = knowledgeLake;
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
                try { sub.Dispose(); } catch { }
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
            // Enforce bounded cache size
            if (MaxKnowledgeCacheSize > 0 && !_knowledgeCache.ContainsKey(topic) && _knowledgeCache.Count >= MaxKnowledgeCacheSize)
            {
                // Evict oldest entry (first key in dictionary -- approximate LRU)
                var firstKey = _knowledgeCache.Keys.FirstOrDefault();
                if (firstKey != null)
                {
                    _knowledgeCache.TryRemove(firstKey, out _);
                }
            }
            _knowledgeCache[topic] = knowledge;
        }

        /// <summary>
        /// Clears all cached knowledge objects.
        /// </summary>
        protected void ClearKnowledgeCache()
        {
            _knowledgeCache.Clear();
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

                // Clear knowledge cache
                _knowledgeCache.Clear();

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

    /// <summary>
    /// Abstract base class for data transformation plugins (Crypto, Compression, etc.).
    /// Provides default implementations for common transformation operations.
    /// AI-native: Supports intelligent transformation based on context.
    /// </summary>
    public abstract class DataTransformationPluginBase : PluginBase, IDataTransformation
    {
        /// <summary>
        /// Category is always DataTransformationProvider for transformation plugins.
        /// </summary>
        public override PluginCategory Category => PluginCategory.DataTransformationProvider;

        /// <summary>
        /// Sub-category for more specific classification (e.g., "Encryption", "Compression").
        /// Must be implemented by derived classes.
        /// </summary>
        public abstract string SubCategory { get; }

        /// <summary>
        /// Quality level (1-100) for sorting and selection.
        /// Default is 50 (balanced). Override for specific quality levels.
        /// </summary>
        public virtual int QualityLevel => 50;

        /// <summary>
        /// Transform data during write operations (e.g., encrypt, compress).
        /// LEGACY: Use OnWriteAsync instead. This sync wrapper causes threadpool starvation under load.
        /// </summary>
        [Obsolete("Use OnWriteAsync instead. Sync-over-async causes threadpool starvation under load.")]
        public virtual Stream OnWrite(Stream input, IKernelContext context, Dictionary<string, object> args)
        {
            // Legacy sync bridge — callers should migrate to OnWriteAsync
            return OnWriteAsync(input, context, args).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Transform data during read operations (e.g., decrypt, decompress).
        /// LEGACY: Use OnReadAsync instead. This sync wrapper causes threadpool starvation under load.
        /// </summary>
        [Obsolete("Use OnReadAsync instead. Sync-over-async causes threadpool starvation under load.")]
        public virtual Stream OnRead(Stream stored, IKernelContext context, Dictionary<string, object> args)
        {
            // Legacy sync bridge — callers should migrate to OnReadAsync
            return OnReadAsync(stored, context, args).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Async version of OnWrite. Transform data during write operations (e.g., encrypt, compress).
        /// RECOMMENDED: Override this method instead of OnWrite for proper async support.
        /// This is the preferred approach for plugins that need to call async APIs (e.g., IKeyStore).
        /// </summary>
        /// <param name="input">The input stream to transform.</param>
        /// <param name="context">The kernel context for logging and plugin access.</param>
        /// <param name="args">Operation-specific arguments.</param>
        /// <returns>A task that completes with the transformed stream.</returns>
        protected virtual Task<Stream> OnWriteAsync(Stream input, IKernelContext context, Dictionary<string, object> args)
        {
            // Default implementation calls sync version
            // Derived classes override this for true async support
            return Task.FromResult(OnWrite(input, context, args));
        }

        /// <summary>
        /// Async version of OnRead. Transform data during read operations (e.g., decrypt, decompress).
        /// RECOMMENDED: Override this method instead of OnRead for proper async support.
        /// This is the preferred approach for plugins that need to call async APIs (e.g., IKeyStore).
        /// </summary>
        /// <param name="stored">The stored stream to transform.</param>
        /// <param name="context">The kernel context for logging and plugin access.</param>
        /// <param name="args">Operation-specific arguments.</param>
        /// <returns>A task that completes with the transformed stream.</returns>
        protected virtual Task<Stream> OnReadAsync(Stream stored, IKernelContext context, Dictionary<string, object> args)
        {
            // Default implementation calls sync version
            // Derived classes override this for true async support
            return Task.FromResult(OnRead(stored, context, args));
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["SubCategory"] = SubCategory;
            metadata["QualityLevel"] = QualityLevel;
            metadata["SupportsStreaming"] = true;
            metadata["TransformationType"] = "Bidirectional";
            metadata["SupportsAsyncTransform"] = true;
            return metadata;
        }
    }

    /// <summary>
    /// Abstract base class for storage provider plugins (Local, S3, IPFS, etc.).
    /// Provides default implementations for common storage operations.
    /// AI-native: Supports intelligent storage decisions based on content analysis.
    /// </summary>
    public abstract class StorageProviderPluginBase : DataPipelinePluginBase, IStorageProvider
    {
        /// <summary>
        /// Category is always StorageProvider for storage plugins.
        /// </summary>
        public override PluginCategory Category => PluginCategory.StorageProvider;

        /// <summary>
        /// URI scheme for this storage provider (e.g., "file", "s3", "ipfs").
        /// Must be implemented by derived classes.
        /// </summary>
        public abstract string Scheme { get; }

        /// <summary>
        /// Save data to storage. Must be implemented by derived classes.
        /// </summary>
        public abstract Task SaveAsync(Uri uri, Stream data);

        /// <summary>
        /// Retrieve data from storage. Must be implemented by derived classes.
        /// </summary>
        public abstract Task<Stream> LoadAsync(Uri uri);

        /// <summary>
        /// Delete data from storage. Must be implemented by derived classes.
        /// </summary>
        public abstract Task DeleteAsync(Uri uri);

        /// <summary>
        /// Check if data exists. Default implementation is optimized for efficiency.
        /// Override for provider-specific optimizations (e.g., HEAD request for HTTP).
        /// </summary>
        public virtual Task<bool> ExistsAsync(Uri uri)
        {
            // Default implementation - derived classes should override with
            // provider-specific efficient checks (e.g., S3 HeadObject, file system File.Exists)
            // This default uses try/load but is marked as inefficient for documentation
            return ExistsAsyncWithLoad(uri);
        }

        /// <summary>
        /// Fallback existence check using LoadAsync. Inefficient but works for any provider.
        /// Prefer overriding ExistsAsync with provider-specific implementation.
        /// </summary>
        protected async Task<bool> ExistsAsyncWithLoad(Uri uri)
        {
            try
            {
                // Use a limited read to avoid loading the entire file
                using var stream = await LoadAsync(uri);
                if (stream == null) return false;

                // Just check if we can read the first byte
                if (stream.CanRead)
                {
                    // Try to read 1 byte - if the file exists, this should succeed
                    var buffer = new byte[1];
                    var bytesRead = await stream.ReadAsync(buffer, 0, 1);
                    return true; // File exists (even if empty)
                }
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                // File exists but we can't access it
                return true;
            }
            catch
            {
                return false;
            }
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["StorageType"] = Scheme;
            metadata["SupportsStreaming"] = true;
            metadata["SupportsConcurrency"] = false;
            return metadata;
        }
    }
    /// <summary>
    /// Abstract base class for security provider plugins (ACL, Encryption, etc.).
    /// Provides default implementations for common security operations.
    /// AI-native: Supports intelligent access control based on context.
    /// </summary>
    public abstract class SecurityProviderPluginBase : SecurityPluginBase
    {
        /// <inheritdoc/>
        public override string SecurityDomain => "SecurityProvider";

        /// <summary>
        /// Category is always SecurityProvider for security plugins.
        /// </summary>
        public override PluginCategory Category => PluginCategory.SecurityProvider;

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["SecurityType"] = "Generic";
            metadata["SupportsEncryption"] = false;
            metadata["SupportsACL"] = false;
            metadata["SupportsAuthentication"] = false;
            return metadata;
        }
    }
    /// <summary>
    /// Abstract base class for interface plugins (REST, SQL, gRPC, WebSocket).
    /// Provides default implementations for external interface operations.
    /// AI-native: Supports AI-driven interface discovery and usage.
    /// </summary>
    [Obsolete("Use DataWarehouse.SDK.Contracts.Hierarchy.InterfacePluginBase instead. This legacy base exists for backward compatibility.")]
    public abstract class InterfacePluginBase : HierarchyInterfacePluginBase
    {
        // Protocol, Port, BasePath are inherited from HierarchyInterfacePluginBase.
        // Concrete subclasses override Protocol directly.

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "Interface";
            metadata["SupportsDiscovery"] = true;
            return metadata;
        }
    }

    /// <summary>
    /// Abstract base class for pipeline stage plugins (compression, encryption stages).
    /// Designed for runtime-configurable transformation pipelines.
    /// AI-native: Supports AI-driven pipeline optimization and ordering.
    /// </summary>
    public abstract class PipelinePluginBase : DataTransformationPluginBase
    {
        /// <summary>
        /// Default execution order (lower = earlier in pipeline).
        /// Can be overridden at runtime by the kernel.
        /// </summary>
        public virtual int DefaultOrder => 100;

        /// <summary>
        /// Whether this stage can be bypassed based on content analysis.
        /// </summary>
        public virtual bool AllowBypass => false;

        /// <summary>
        /// Stage dependencies - other stages that must run before this one.
        /// Empty means no dependencies.
        /// </summary>
        public virtual string[] RequiredPrecedingStages => Array.Empty<string>();

        /// <summary>
        /// Stage conflicts - stages that cannot run in the same pipeline.
        /// </summary>
        public virtual string[] IncompatibleStages => Array.Empty<string>();

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["PipelineStage"] = true;
            metadata["DefaultOrder"] = DefaultOrder;
            metadata["AllowBypass"] = AllowBypass;
            metadata["RequiredPrecedingStages"] = RequiredPrecedingStages;
            metadata["IncompatibleStages"] = IncompatibleStages;
            metadata["RuntimeOrderingSupported"] = true;
            return metadata;
        }
    }

    /// <summary>
    /// Abstract base class for listable storage providers that support file enumeration.
    /// Extends StorageProviderPluginBase with listing capabilities.
    /// </summary>
    public abstract class ListableStoragePluginBase : StorageProviderPluginBase, IListableStorage
    {
        /// <summary>
        /// List all files in storage matching a prefix.
        /// Must be implemented by derived classes.
        /// </summary>
        public abstract IAsyncEnumerable<StorageListItem> ListFilesAsync(string prefix = "", CancellationToken ct = default);

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["SupportsListing"] = true;
            return metadata;
        }
    }

    /// <summary>
    /// Abstract base class for tiered storage providers that support data tiering.
    /// Extends ListableStoragePluginBase with tier management capabilities.
    /// </summary>
    public abstract class TieredStoragePluginBase : ListableStoragePluginBase, ITieredStorage
    {
        /// <summary>
        /// Move a blob to the specified storage tier.
        /// Must be implemented by derived classes.
        /// </summary>
        public abstract Task<string> MoveToTierAsync(Manifest manifest, StorageTier targetTier);

        /// <summary>
        /// Get the current tier of a blob. Override for custom tier detection.
        /// </summary>
        public virtual Task<StorageTier> GetCurrentTierAsync(Uri uri)
        {
            return Task.FromResult(StorageTier.Hot);
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["SupportsTiering"] = true;
            metadata["SupportedTiers"] = new[] { "Hot", "Cool", "Cold", "Archive" };
            return metadata;
        }
    }

    #region Hybrid Storage Plugin Base Classes

    /// <summary>
    /// Abstract base class for cacheable storage providers.
    /// Adds TTL-based caching, expiration, and cache statistics to storage plugins.
    /// Plugins extending this class automatically gain caching capabilities.
    /// </summary>
    public abstract class CacheableStoragePluginBase : ListableStoragePluginBase, ICacheableStorage
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CacheEntryMetadata> _cacheMetadata = new();
        private readonly Timer? _cleanupTimer;
        private readonly CacheOptions _cacheOptions;

        /// <summary>
        /// Default cache options. Override to customize.
        /// </summary>
        protected virtual CacheOptions DefaultCacheOptions => new()
        {
            DefaultTtl = TimeSpan.FromHours(1),
            MaxEntries = 10000,
            EvictionPolicy = CacheEvictionPolicy.LRU,
            EnableStatistics = true
        };

        protected CacheableStoragePluginBase()
        {
            _cacheOptions = DefaultCacheOptions;
            if (_cacheOptions.CleanupInterval > TimeSpan.Zero)
            {
                _cleanupTimer = new Timer(
                    async _ => await CleanupExpiredAsync(),
                    null,
                    _cacheOptions.CleanupInterval,
                    _cacheOptions.CleanupInterval);
            }
        }

        /// <summary>
        /// Save data with a specific TTL.
        /// </summary>
        public virtual async Task SaveWithTtlAsync(Uri uri, Stream data, TimeSpan ttl, CancellationToken ct = default)
        {
            await SaveAsync(uri, data);
            var key = uri.ToString();
            _cacheMetadata[key] = new CacheEntryMetadata
            {
                Key = key,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.Add(ttl),
                LastAccessedAt = DateTime.UtcNow,
                Size = data.Length
            };
        }

        /// <summary>
        /// Get the remaining TTL for an entry.
        /// </summary>
        public virtual Task<TimeSpan?> GetTtlAsync(Uri uri, CancellationToken ct = default)
        {
            var key = uri.ToString();
            if (_cacheMetadata.TryGetValue(key, out var metadata) && metadata.ExpiresAt.HasValue)
            {
                var remaining = metadata.ExpiresAt.Value - DateTime.UtcNow;
                return Task.FromResult<TimeSpan?>(remaining > TimeSpan.Zero ? remaining : null);
            }
            return Task.FromResult<TimeSpan?>(null);
        }

        /// <summary>
        /// Set or update TTL for an existing entry.
        /// </summary>
        public virtual Task<bool> SetTtlAsync(Uri uri, TimeSpan ttl, CancellationToken ct = default)
        {
            var key = uri.ToString();
            if (_cacheMetadata.TryGetValue(key, out var metadata))
            {
                metadata.ExpiresAt = DateTime.UtcNow.Add(ttl);
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        /// <summary>
        /// Invalidate all entries matching a pattern.
        /// </summary>
        public virtual async Task<int> InvalidatePatternAsync(string pattern, CancellationToken ct = default)
        {
            var regex = new System.Text.RegularExpressions.Regex(
                "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$");

            var keysToRemove = _cacheMetadata.Keys.Where(k => regex.IsMatch(k)).ToList();
            var count = 0;

            foreach (var key in keysToRemove)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    await DeleteAsync(new Uri(key));
                    _cacheMetadata.TryRemove(key, out _);
                    count++;
                }
                catch { /* Ignore errors during invalidation */ }
            }

            return count;
        }

        /// <summary>
        /// Get cache statistics.
        /// </summary>
        public virtual Task<CacheStatistics> GetCacheStatisticsAsync(CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;
            var entries = _cacheMetadata.Values.ToList();

            return Task.FromResult(new CacheStatistics
            {
                TotalEntries = entries.Count,
                TotalSizeBytes = entries.Sum(e => e.Size),
                ExpiredEntries = entries.Count(e => e.ExpiresAt.HasValue && e.ExpiresAt < now),
                HitCount = entries.Sum(e => e.HitCount),
                MissCount = 0, // Would need separate tracking
                OldestEntry = entries.MinBy(e => e.CreatedAt)?.CreatedAt,
                NewestEntry = entries.MaxBy(e => e.CreatedAt)?.CreatedAt
            });
        }

        /// <summary>
        /// Remove all expired entries.
        /// </summary>
        public virtual async Task<int> CleanupExpiredAsync(CancellationToken ct = default)
        {
            var now = DateTime.UtcNow;
            var expiredKeys = _cacheMetadata
                .Where(kv => kv.Value.ExpiresAt.HasValue && kv.Value.ExpiresAt < now)
                .Select(kv => kv.Key)
                .ToList();

            var count = 0;
            foreach (var key in expiredKeys)
            {
                if (ct.IsCancellationRequested) break;
                if (_cacheMetadata.TryRemove(key, out _))
                {
                    try { await DeleteAsync(new Uri(key)); } catch { }
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Touch an entry to update its last access time and reset sliding expiration.
        /// </summary>
        public virtual Task<bool> TouchAsync(Uri uri, CancellationToken ct = default)
        {
            var key = uri.ToString();
            if (_cacheMetadata.TryGetValue(key, out var metadata))
            {
                metadata.LastAccessedAt = DateTime.UtcNow;
                metadata.HitCount++;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        /// <summary>
        /// Remove TTL from an item, making it permanent.
        /// </summary>
        public virtual Task<bool> RemoveTtlAsync(Uri uri, CancellationToken ct = default)
        {
            var key = uri.ToString();
            if (_cacheMetadata.TryGetValue(key, out var metadata))
            {
                metadata.ExpiresAt = null;
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        /// <summary>
        /// Invalidate all cached items with a specific tag.
        /// </summary>
        public virtual async Task<int> InvalidateByTagAsync(string tag, CancellationToken ct = default)
        {
            var keysWithTag = _cacheMetadata
                .Where(kv => kv.Value.Tags?.Contains(tag) == true)
                .Select(kv => kv.Key)
                .ToList();

            var count = 0;
            foreach (var key in keysWithTag)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    await DeleteAsync(new Uri(key));
                    _cacheMetadata.TryRemove(key, out _);
                    count++;
                }
                catch { /* Ignore errors during invalidation */ }
            }

            return count;
        }

        /// <summary>
        /// Load with cache hit tracking. Derived classes must implement actual loading.
        /// Implementations should call TouchAsync(uri) to track cache access before loading.
        /// </summary>
        public abstract override Task<Stream> LoadAsync(Uri uri);

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["SupportsCaching"] = true;
            metadata["SupportsTTL"] = true;
            metadata["SupportsExpiration"] = true;
            metadata["CacheEvictionPolicy"] = _cacheOptions.EvictionPolicy.ToString();
            return metadata;
        }

        /// <summary>
        /// Dispose cleanup timer.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _cleanupTimer?.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Abstract base class for indexable storage providers.
    /// Combines storage with full-text and metadata indexing capabilities.
    /// Plugins extending this class automatically gain indexing support.
    /// </summary>
    public abstract class IndexableStoragePluginBase : CacheableStoragePluginBase, IIndexableStorage
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<string, object>> _indexStore = new();
        private long _indexedCount;
        private DateTime? _lastIndexRebuild;

        /// <summary>
        /// Maximum number of entries in the index store.
        /// Override in derived classes to customize. Default: 100,000.
        /// Set to 0 for unlimited (not recommended).
        /// </summary>
        protected virtual int MaxIndexStoreSize => 100_000;

        /// <summary>
        /// Index a document with its metadata.
        /// </summary>
        public virtual Task IndexDocumentAsync(string id, Dictionary<string, object> metadata, CancellationToken ct = default)
        {
            // Enforce bounded index
            if (MaxIndexStoreSize > 0 && !_indexStore.ContainsKey(id) && _indexStore.Count >= MaxIndexStoreSize)
            {
                var firstKey = _indexStore.Keys.FirstOrDefault();
                if (firstKey != null)
                {
                    _indexStore.TryRemove(firstKey, out _);
                }
            }

            metadata["_indexed_at"] = DateTime.UtcNow;
            _indexStore[id] = metadata;
            Interlocked.Increment(ref _indexedCount);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Remove a document from the index.
        /// </summary>
        public virtual Task<bool> RemoveFromIndexAsync(string id, CancellationToken ct = default)
        {
            var removed = _indexStore.TryRemove(id, out _);
            if (removed) Interlocked.Decrement(ref _indexedCount);
            return Task.FromResult(removed);
        }

        /// <summary>
        /// Search the index with a text query.
        /// </summary>
        public virtual Task<string[]> SearchIndexAsync(string query, int limit = 100, CancellationToken ct = default)
        {
            var queryLower = query.ToLowerInvariant();
            var results = _indexStore
                .Where(kv => kv.Value.Values.Any(v =>
                    v?.ToString()?.Contains(queryLower, StringComparison.OrdinalIgnoreCase) == true))
                .Take(limit)
                .Select(kv => kv.Key)
                .ToArray();

            return Task.FromResult(results);
        }

        /// <summary>
        /// Query by specific metadata criteria.
        /// </summary>
        public virtual Task<string[]> QueryByMetadataAsync(Dictionary<string, object> criteria, CancellationToken ct = default)
        {
            var results = _indexStore
                .Where(kv => criteria.All(c =>
                    kv.Value.TryGetValue(c.Key, out var v) &&
                    Equals(v?.ToString(), c.Value?.ToString())))
                .Select(kv => kv.Key)
                .ToArray();

            return Task.FromResult(results);
        }

        /// <summary>
        /// Get index statistics.
        /// </summary>
        public virtual Task<IndexStatistics> GetIndexStatisticsAsync(CancellationToken ct = default)
        {
            return Task.FromResult(new IndexStatistics
            {
                DocumentCount = _indexStore.Count,
                TermCount = _indexStore.Values
                    .SelectMany(v => v.Keys)
                    .Distinct()
                    .Count(),
                LastUpdated = _lastIndexRebuild ?? DateTime.UtcNow,
                IndexSizeBytes = _indexStore.Sum(kv =>
                    kv.Key.Length + kv.Value.Sum(v => (v.Key?.Length ?? 0) + (v.Value?.ToString()?.Length ?? 0))),
                IndexType = "InMemory"
            });
        }

        /// <summary>
        /// Optimize the index for better query performance.
        /// </summary>
        public virtual Task OptimizeIndexAsync(CancellationToken ct = default)
        {
            // Default implementation: no-op for in-memory index
            // Derived classes should override for actual optimization (compact, defrag, merge segments)
            return Task.CompletedTask;
        }

        /// <summary>
        /// Rebuild the entire index.
        /// </summary>
        public virtual async Task<int> RebuildIndexAsync(CancellationToken ct = default)
        {
            _indexStore.Clear();
            _indexedCount = 0;
            var count = 0;

            await foreach (var item in ListFilesAsync("", ct))
            {
                if (ct.IsCancellationRequested) break;

                // Index with basic metadata
                var id = item.Uri.ToString();
                await IndexDocumentAsync(id, new Dictionary<string, object>
                {
                    ["uri"] = id,
                    ["size"] = item.Size,
                    ["path"] = item.Uri.AbsolutePath
                }, ct);
                count++;
            }

            _lastIndexRebuild = DateTime.UtcNow;
            return count;
        }

        #region IMetadataIndex Implementation

        /// <summary>
        /// Index a manifest.
        /// </summary>
        public virtual async Task IndexManifestAsync(Manifest manifest)
        {
            var metadata = new Dictionary<string, object>
            {
                ["id"] = manifest.Id,
                ["hash"] = manifest.ContentHash,
                ["size"] = manifest.OriginalSize,
                ["created"] = manifest.CreatedAt,
                ["contentType"] = manifest.ContentType ?? ""
            };

            if (manifest.Metadata != null)
            {
                foreach (var kv in manifest.Metadata)
                {
                    metadata[kv.Key] = kv.Value;
                }
            }

            await IndexDocumentAsync(manifest.Id, metadata);
        }

        /// <summary>
        /// Search manifests with optional vector.
        /// </summary>
        public virtual Task<string[]> SearchAsync(string query, float[]? vector, int limit)
        {
            // Vector search would require specialized implementation
            return SearchIndexAsync(query, limit);
        }

        /// <summary>
        /// Enumerate all manifests in the index.
        /// </summary>
        public virtual async IAsyncEnumerable<Manifest> EnumerateAllAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var kv in _indexStore)
            {
                if (ct.IsCancellationRequested) yield break;

                yield return new Manifest
                {
                    Id = kv.Key,
                    ContentHash = kv.Value.TryGetValue("hash", out var h) ? h?.ToString() ?? "" : "",
                    OriginalSize = kv.Value.TryGetValue("size", out var s) && s is long size ? size : 0,
                    CreatedAt = kv.Value.TryGetValue("created", out var c) && c is DateTime dt
                        ? new DateTimeOffset(dt).ToUnixTimeSeconds()
                        : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Metadata = kv.Value.Where(x => !x.Key.StartsWith("_"))
                        .ToDictionary(x => x.Key, x => x.Value?.ToString() ?? "")
                };

                await Task.Yield();
            }
        }

        /// <summary>
        /// Update last access time for a manifest.
        /// </summary>
        public virtual Task UpdateLastAccessAsync(string id, long timestamp)
        {
            if (_indexStore.TryGetValue(id, out var metadata))
            {
                metadata["_last_access"] = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).UtcDateTime;
            }
            return Task.CompletedTask;
        }

        /// <summary>
        /// Get a manifest by ID.
        /// </summary>
        public virtual Task<Manifest?> GetManifestAsync(string id)
        {
            if (_indexStore.TryGetValue(id, out var metadata))
            {
                return Task.FromResult<Manifest?>(new Manifest
                {
                    Id = id,
                    ContentHash = metadata.TryGetValue("hash", out var h) ? h?.ToString() ?? "" : "",
                    OriginalSize = metadata.TryGetValue("size", out var s) && s is long size ? size : 0,
                    CreatedAt = metadata.TryGetValue("created", out var c) && c is DateTime dt
                        ? new DateTimeOffset(dt).ToUnixTimeSeconds()
                        : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Metadata = metadata.Where(x => !x.Key.StartsWith("_"))
                        .ToDictionary(x => x.Key, x => x.Value?.ToString() ?? "")
                });
            }
            return Task.FromResult<Manifest?>(null);
        }

        /// <summary>
        /// Execute a text query.
        /// </summary>
        public virtual Task<string[]> ExecuteQueryAsync(string query, int limit)
        {
            return SearchIndexAsync(query, limit);
        }

        /// <summary>
        /// Execute a composite query.
        /// </summary>
        public virtual Task<string[]> ExecuteQueryAsync(CompositeQuery query, int limit = 50)
        {
            return ExecuteQueryAsync(query.ToString() ?? "", limit);
        }

        #endregion

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["SupportsIndexing"] = true;
            metadata["SupportsFullTextSearch"] = true;
            metadata["SupportsMetadataQuery"] = true;
            metadata["IndexedDocuments"] = _indexedCount;
            return metadata;
        }
    }

    #endregion

    /// <summary>
    /// Abstract base class for consensus engine plugins (Raft, Paxos, etc.).
    /// Provides default implementations for distributed consensus operations.
    /// AI-native: Supports intelligent leader election and quorum decisions.
    /// </summary>
    public abstract class ConsensusPluginBase : InfrastructurePluginBase, IConsensusEngine
    {
        /// <inheritdoc/>
        public override string InfrastructureDomain => "Consensus";

        /// <summary>
        /// Category is always OrchestrationProvider for consensus plugins.
        /// </summary>
        public override PluginCategory Category => PluginCategory.OrchestrationProvider;

        /// <summary>
        /// Whether this node is currently the leader.
        /// </summary>
        public abstract bool IsLeader { get; }

        /// <summary>
        /// Propose a state change to the cluster. Returns when quorum is reached.
        /// Must be implemented by derived classes.
        /// </summary>
        public abstract Task<bool> ProposeAsync(Proposal proposal);

        /// <summary>
        /// Subscribe to committed entries from other nodes.
        /// Must be implemented by derived classes.
        /// </summary>
        public abstract void OnCommit(Action<Proposal> handler);

        /// <summary>
        /// Get current cluster state. Override for custom state reporting.
        /// </summary>
        public virtual Task<ClusterState> GetClusterStateAsync()
        {
            return Task.FromResult(new ClusterState { IsHealthy = true });
        }

        #region Multi-Raft Extensions (Phase 41.1-06)

        /// <summary>
        /// Result of a consensus proposal operation.
        /// </summary>
        /// <param name="Success">Whether the proposal was committed by quorum.</param>
        /// <param name="LeaderId">Current leader node ID (null if no leader).</param>
        /// <param name="LogIndex">Log index of the committed entry.</param>
        /// <param name="Error">Error message if proposal failed.</param>
        public record ConsensusResult(bool Success, string? LeaderId, long LogIndex, string? Error);

        /// <summary>
        /// Current state of the consensus engine.
        /// </summary>
        /// <param name="State">State description (e.g., "follower", "leader", "multi-raft").</param>
        /// <param name="LeaderId">Current leader node ID.</param>
        /// <param name="CommitIndex">Highest committed log index.</param>
        /// <param name="LastApplied">Highest log index applied to state machine.</param>
        public record ConsensusState(string State, string? LeaderId, long CommitIndex, long LastApplied);

        /// <summary>
        /// Cluster health summary across all consensus groups.
        /// </summary>
        /// <param name="TotalNodes">Total nodes across all groups.</param>
        /// <param name="HealthyNodes">Number of healthy (reachable) nodes.</param>
        /// <param name="NodeStates">Per-node state descriptions.</param>
        public record ClusterHealthInfo(int TotalNodes, int HealthyNodes, Dictionary<string, string> NodeStates);

        /// <summary>
        /// Propose raw data to the consensus cluster. Routes to appropriate group in Multi-Raft.
        /// Default implementation wraps data in a <see cref="Proposal"/> and delegates to
        /// <see cref="ProposeAsync(Proposal)"/>.
        /// </summary>
        /// <param name="data">Binary data to propose.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Result including success, leader, log index, and error info.</returns>
        public virtual async Task<ConsensusResult> ProposeAsync(byte[] data, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var proposal = new Proposal { Payload = data };
            var success = await ProposeAsync(proposal).ConfigureAwait(false);
            return new ConsensusResult(success, null, 0, success ? null : "Proposal not committed by quorum");
        }

        /// <summary>
        /// Checks if the current node is a leader (async version with cancellation support).
        /// Default implementation returns <see cref="IsLeader"/>.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if this node is leader in at least one consensus group.</returns>
        public virtual Task<bool> IsLeaderAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(IsLeader);
        }

        /// <summary>
        /// Gets the current consensus state. Override for detailed state reporting.
        /// Default implementation builds state from <see cref="GetClusterStateAsync"/>.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Current consensus state including leader, commit index, and last applied.</returns>
        public virtual async Task<ConsensusState> GetStateAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var cluster = await GetClusterStateAsync().ConfigureAwait(false);
            return new ConsensusState(
                IsLeader ? "leader" : "follower",
                cluster.LeaderId,
                cluster.Term,
                cluster.Term);
        }

        /// <summary>
        /// Gets cluster health information aggregated across all consensus groups.
        /// Override for detailed per-group health reporting.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Aggregated health information.</returns>
        public virtual Task<ClusterHealthInfo> GetClusterHealthAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new ClusterHealthInfo(0, 0, new Dictionary<string, string>()));
        }

        #endregion

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "Consensus";
            metadata["ConsensusAlgorithm"] = "Generic";
            metadata["SupportsLeaderElection"] = true;
            return metadata;
        }
    }

    /// <summary>
    /// Cluster state information.
    /// </summary>
    public class ClusterState
    {
        /// <summary>Whether the cluster is healthy.</summary>
        public bool IsHealthy { get; init; }
        /// <summary>Current leader node ID.</summary>
        public string? LeaderId { get; init; }
        /// <summary>Number of nodes in the cluster.</summary>
        public int NodeCount { get; init; }
        /// <summary>Current term/epoch.</summary>
        public long Term { get; init; }
    }
    /// <summary>
    /// Abstract base class for replication service plugins.
    /// Manages data redundancy, backups, and restoration.
    /// AI-native: Supports intelligent replica selection and auto-healing.
    /// </summary>
    [Obsolete("Use DataWarehouse.SDK.Contracts.Hierarchy.ReplicationPluginBase instead. This legacy base exists for backward compatibility.")]
    public abstract class ReplicationPluginBase : HierarchyReplicationPluginBase, IReplicationService
    {
        /// <summary>
        /// Category is always FederationProvider for replication plugins.
        /// </summary>
        public override PluginCategory Category => PluginCategory.FederationProvider;

        /// <inheritdoc/>
        public override Task<Dictionary<string, object>> ReplicateAsync(string key, string[] targetNodes, CancellationToken ct = default)
            => Task.FromResult(new Dictionary<string, object> { ["status"] = "delegated" });

        /// <inheritdoc/>
        public override Task<Dictionary<string, object>> GetSyncStatusAsync(string key, CancellationToken ct = default)
            => Task.FromResult(new Dictionary<string, object> { ["status"] = "unknown" });

        /// <summary>
        /// Restores a corrupted blob using a healthy replica.
        /// Must be implemented by derived classes.
        /// </summary>
        public abstract Task<bool> RestoreAsync(string blobId, string? replicaId);

        /// <summary>
        /// Get all available replicas for a blob. Override for custom logic.
        /// </summary>
        public virtual Task<string[]> GetAvailableReplicasAsync(string blobId)
        {
            return Task.FromResult(Array.Empty<string>());
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "Replication";
            metadata["SupportsAutoHeal"] = true;
            return metadata;
        }
    }
    /// <summary>
    /// Abstract base class for access control plugins.
    /// Extends SecurityProviderPluginBase with ACL management.
    /// AI-native: Supports intelligent permission suggestions.
    /// </summary>
    public abstract class AccessControlPluginBase : SecurityProviderPluginBase, IAccessControl
    {
        public abstract void SetPermissions(string resource, string subject, Permission allow, Permission deny);
        public abstract bool HasAccess(string resource, string subject, Permission requested);
        public abstract void CreateScope(string resource, string owner);

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["SecurityType"] = "AccessControl";
            metadata["SupportsGranularACL"] = true;
            return metadata;
        }
    }
    #region Encryption Plugin Base

    /// <summary>
    /// Abstract base class for all encryption plugins with composable key management.
    /// Supports per-user, per-operation configuration for maximum flexibility.
    /// All encryption plugins MUST extend this class.
    /// </summary>
    public abstract class EncryptionPluginBase : PipelinePluginBase
    {
        #region Configuration

        /// <summary>
        /// Default key store (fallback when no user preference or explicit override).
        /// </summary>
        protected IKeyStore? DefaultKeyStore;

        /// <summary>
        /// Default key management mode.
        /// </summary>
        protected KeyManagementMode DefaultKeyManagementMode = KeyManagementMode.Direct;

        /// <summary>
        /// Default envelope key store for Envelope mode.
        /// </summary>
        protected IEnvelopeKeyStore? DefaultEnvelopeKeyStore;

        /// <summary>
        /// Default KEK key ID for Envelope mode.
        /// </summary>
        protected string? DefaultKekKeyId;

        /// <summary>
        /// Per-user configuration provider (optional, for multi-tenant).
        /// </summary>
        protected IKeyManagementConfigProvider? ConfigProvider;

        /// <summary>
        /// Key store registry for resolving plugin IDs.
        /// </summary>
        protected IKeyStoreRegistry? KeyStoreRegistry;

        #endregion

        #region Statistics

        /// <summary>
        /// Lock for thread-safe statistics updates.
        /// </summary>
        protected readonly object StatsLock = new();

        /// <summary>
        /// Total encryption operations performed.
        /// </summary>
        protected long EncryptionCount;

        /// <summary>
        /// Total decryption operations performed.
        /// </summary>
        protected long DecryptionCount;

        /// <summary>
        /// Total bytes encrypted.
        /// </summary>
        protected long TotalBytesEncrypted;

        /// <summary>
        /// Total bytes decrypted.
        /// </summary>
        protected long TotalBytesDecrypted;

        /// <summary>
        /// Key access audit log (tracks when each key was last used).
        /// </summary>
        protected readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> KeyAccessLog = new();

        #endregion

        #region Abstract Configuration Properties

        /// <summary>
        /// Key size in bytes for this encryption algorithm.
        /// E.g., 32 for AES-256.
        /// </summary>
        protected abstract int KeySizeBytes { get; }

        /// <summary>
        /// IV/nonce size in bytes for this encryption algorithm.
        /// E.g., 12 for AES-GCM.
        /// </summary>
        protected abstract int IvSizeBytes { get; }

        /// <summary>
        /// Authentication tag size in bytes (for AEAD algorithms).
        /// E.g., 16 for AES-GCM.
        /// </summary>
        protected abstract int TagSizeBytes { get; }

        /// <summary>
        /// The encryption algorithm identifier.
        /// E.g., "AES-256-GCM", "ChaCha20-Poly1305".
        /// </summary>
        protected abstract string AlgorithmId { get; }

        /// <summary>
        /// Maximum number of entries in the key access log.
        /// Override in derived classes to customize. Default: 10,000.
        /// </summary>
        protected virtual int MaxKeyAccessLogSize => 10_000;

        /// <summary>
        /// Records a key access event in the bounded key access log.
        /// </summary>
        protected void RecordKeyAccess(string keyId)
        {
            if (KeyAccessLog.Count >= MaxKeyAccessLogSize && !KeyAccessLog.ContainsKey(keyId))
            {
                // Evict oldest entry
                var oldest = KeyAccessLog.OrderBy(kvp => kvp.Value).FirstOrDefault();
                if (oldest.Key != null)
                {
                    KeyAccessLog.TryRemove(oldest.Key, out _);
                }
            }
            KeyAccessLog[keyId] = DateTime.UtcNow;
        }

        #endregion

        #region Configuration Resolution

        /// <summary>
        /// Resolves key management configuration for this operation.
        /// Priority: 1. Explicit args, 2. User preferences, 3. Plugin defaults
        /// </summary>
        protected virtual async Task<ResolvedKeyManagementConfig> ResolveConfigAsync(
            Dictionary<string, object> args,
            ISecurityContext context)
        {
            // 1. Check for explicit overrides in args
            if (TryGetConfigFromArgs(args, out var argsConfig))
                return argsConfig;

            // 2. Check for user preferences via ConfigProvider
            if (ConfigProvider != null)
            {
                var userConfig = await ConfigProvider.GetConfigAsync(context);
                if (userConfig != null)
                    return ResolveFromUserConfig(userConfig);
            }

            // 3. Fall back to plugin defaults
            return new ResolvedKeyManagementConfig
            {
                Mode = DefaultKeyManagementMode,
                KeyStore = DefaultKeyStore,
                EnvelopeKeyStore = DefaultEnvelopeKeyStore,
                KekKeyId = DefaultKekKeyId
            };
        }

        /// <summary>
        /// Attempts to extract configuration from operation arguments.
        /// </summary>
        protected virtual bool TryGetConfigFromArgs(Dictionary<string, object> args, out ResolvedKeyManagementConfig config)
        {
            config = default!;

            // Check if explicit mode is specified
            if (!args.TryGetValue("keyManagementMode", out var modeObj))
                return false;

            var mode = modeObj switch
            {
                KeyManagementMode m => m,
                string s when Enum.TryParse<KeyManagementMode>(s, true, out var parsed) => parsed,
                _ => KeyManagementMode.Direct
            };

            // Resolve key store from args
            IKeyStore? keyStore = null;
            IEnvelopeKeyStore? envelopeKeyStore = null;
            string? keyStorePluginId = null;
            string? envelopeKeyStorePluginId = null;
            string? kekKeyId = null;
            string? keyId = null;

            if (args.TryGetValue("keyStore", out var ksObj) && ksObj is IKeyStore ks)
                keyStore = ks;
            else if (args.TryGetValue("keyStorePluginId", out var kspObj) && kspObj is string ksp)
            {
                keyStorePluginId = ksp;
                keyStore = KeyStoreRegistry?.GetKeyStore(ksp);
            }

            if (args.TryGetValue("envelopeKeyStore", out var eksObj) && eksObj is IEnvelopeKeyStore eks)
                envelopeKeyStore = eks;
            else if (args.TryGetValue("envelopeKeyStorePluginId", out var ekspObj) && ekspObj is string eksp)
            {
                envelopeKeyStorePluginId = eksp;
                envelopeKeyStore = KeyStoreRegistry?.GetEnvelopeKeyStore(eksp);
            }

            if (args.TryGetValue("kekKeyId", out var kekObj) && kekObj is string kek)
                kekKeyId = kek;

            if (args.TryGetValue("keyId", out var kidObj) && kidObj is string kid)
                keyId = kid;

            config = new ResolvedKeyManagementConfig
            {
                Mode = mode,
                KeyStore = keyStore ?? DefaultKeyStore,
                KeyId = keyId,
                EnvelopeKeyStore = envelopeKeyStore ?? DefaultEnvelopeKeyStore,
                KekKeyId = kekKeyId ?? DefaultKekKeyId,
                KeyStorePluginId = keyStorePluginId,
                EnvelopeKeyStorePluginId = envelopeKeyStorePluginId
            };

            return true;
        }

        /// <summary>
        /// Resolves configuration from user preferences.
        /// </summary>
        protected virtual ResolvedKeyManagementConfig ResolveFromUserConfig(KeyManagementConfig userConfig)
        {
            return new ResolvedKeyManagementConfig
            {
                Mode = userConfig.Mode,
                KeyStore = userConfig.KeyStore ?? KeyStoreRegistry?.GetKeyStore(userConfig.KeyStorePluginId),
                KeyId = userConfig.KeyId,
                EnvelopeKeyStore = userConfig.EnvelopeKeyStore ?? KeyStoreRegistry?.GetEnvelopeKeyStore(userConfig.EnvelopeKeyStorePluginId),
                KekKeyId = userConfig.KekKeyId,
                KeyStorePluginId = userConfig.KeyStorePluginId,
                EnvelopeKeyStorePluginId = userConfig.EnvelopeKeyStorePluginId
            };
        }

        #endregion

        #region Key Management

        /// <summary>
        /// Gets a key for encryption based on resolved configuration.
        /// For Direct mode: retrieves key from key store.
        /// For Envelope mode: generates random DEK, wraps with KEK.
        /// </summary>
        protected virtual async Task<(byte[] key, string keyId, EnvelopeHeader? envelope)> GetKeyForEncryptionAsync(
            ResolvedKeyManagementConfig config,
            ISecurityContext context)
        {
            if (config.Mode == KeyManagementMode.Envelope)
            {
                return await GetEnvelopeKeyForEncryptionAsync(config, context);
            }
            else
            {
                return await GetDirectKeyForEncryptionAsync(config, context);
            }
        }

        /// <summary>
        /// Gets a key for Direct mode encryption.
        /// </summary>
        protected virtual async Task<(byte[] key, string keyId, EnvelopeHeader? envelope)> GetDirectKeyForEncryptionAsync(
            ResolvedKeyManagementConfig config,
            ISecurityContext context)
        {
            if (config.KeyStore == null)
                throw new InvalidOperationException("No key store configured for Direct mode encryption.");

            var keyId = config.KeyId ?? await config.KeyStore.GetCurrentKeyIdAsync();
            var key = await config.KeyStore.GetKeyAsync(keyId, context);

            // Log key access
            RecordKeyAccess(keyId);

            return (key, keyId, null);
        }

        /// <summary>
        /// Gets a key for Envelope mode encryption.
        /// Generates a random DEK and wraps it with the KEK.
        /// </summary>
        protected virtual async Task<(byte[] key, string keyId, EnvelopeHeader? envelope)> GetEnvelopeKeyForEncryptionAsync(
            ResolvedKeyManagementConfig config,
            ISecurityContext context)
        {
            if (config.EnvelopeKeyStore == null)
                throw new InvalidOperationException("No envelope key store configured for Envelope mode encryption.");
            if (string.IsNullOrEmpty(config.KekKeyId))
                throw new InvalidOperationException("No KEK key ID configured for Envelope mode encryption.");

            // Generate random DEK
            var dek = new byte[KeySizeBytes];
            System.Security.Cryptography.RandomNumberGenerator.Fill(dek);

            // Wrap DEK with KEK
            var wrappedDek = await config.EnvelopeKeyStore.WrapKeyAsync(config.KekKeyId, dek, context);

            // Generate IV
            var iv = GenerateIv();

            // Create envelope header
            var envelope = new EnvelopeHeader
            {
                KekId = config.KekKeyId,
                KeyStorePluginId = config.EnvelopeKeyStorePluginId ?? "",
                WrappedDek = wrappedDek,
                Iv = iv,
                EncryptionAlgorithm = AlgorithmId,
                EncryptionPluginId = Id,
                EncryptedAtTicks = DateTime.UtcNow.Ticks,
                EncryptedBy = context.UserId
            };

            // Log key access
            RecordKeyAccess($"envelope:{config.KekKeyId}");

            return (dek, $"envelope:{Guid.NewGuid():N}", envelope);
        }

        /// <summary>
        /// Gets a key for decryption based on stored metadata.
        /// </summary>
        protected virtual async Task<byte[]> GetKeyForDecryptionAsync(
            EnvelopeHeader? envelope,
            string? keyId,
            ResolvedKeyManagementConfig config,
            ISecurityContext context)
        {
            if (envelope != null)
            {
                // Envelope mode: unwrap DEK
                var envelopeKeyStore = config.EnvelopeKeyStore
                    ?? KeyStoreRegistry?.GetEnvelopeKeyStore(envelope.KeyStorePluginId)
                    ?? throw new InvalidOperationException($"Cannot find envelope key store '{envelope.KeyStorePluginId}' to unwrap DEK.");

                var dek = await envelopeKeyStore.UnwrapKeyAsync(envelope.KekId, envelope.WrappedDek, context);

                RecordKeyAccess($"envelope:{envelope.KekId}");

                return dek;
            }
            else
            {
                // Direct mode: get key from store
                if (string.IsNullOrEmpty(keyId))
                    throw new InvalidOperationException("Key ID required for Direct mode decryption.");

                var keyStore = config.KeyStore
                    ?? DefaultKeyStore
                    ?? throw new InvalidOperationException("No key store configured for Direct mode decryption.");

                var key = await keyStore.GetKeyAsync(keyId, context);

                RecordKeyAccess(keyId);

                return key;
            }
        }

        #endregion

        #region Security Context Resolution

        /// <summary>
        /// Gets the security context from operation arguments.
        /// </summary>
        protected virtual ISecurityContext GetSecurityContext(Dictionary<string, object> args, IKernelContext context)
        {
            // Check for explicit security context in args
            if (args.TryGetValue("securityContext", out var ctxObj) && ctxObj is ISecurityContext secCtx)
                return secCtx;

            // Try to get from kernel context
            // Kernel security context resolution: define kernel context -> security context mapping when available.

            // Fallback to system context
            return new DefaultSecurityContext();
        }

        /// <summary>
        /// Default security context for when none is provided.
        /// </summary>
        protected class DefaultSecurityContext : ISecurityContext
        {
            public string UserId => "system";
            public string? TenantId => null;
            public IEnumerable<string> Roles => new[] { "system" };
            public bool IsSystemAdmin => true;
        }

        #endregion

        #region OnWrite/OnRead Implementation

        /// <summary>
        /// Encrypts data during write operations.
        /// Resolves config per-operation, supports both Direct and Envelope modes.
        /// </summary>
        protected override async Task<Stream> OnWriteAsync(Stream input, IKernelContext context, Dictionary<string, object> args)
        {
            var securityContext = GetSecurityContext(args, context);
            var config = await ResolveConfigAsync(args, securityContext);

            var (key, keyId, envelope) = await GetKeyForEncryptionAsync(config, securityContext);

            try
            {
                // Get IV (from envelope or generate new)
                var iv = envelope?.Iv ?? GenerateIv();

                // Create encryption metadata for storage
                var metadata = new EncryptionMetadata
                {
                    EncryptionPluginId = Id,
                    KeyMode = config.Mode,
                    KeyId = config.Mode == KeyManagementMode.Direct ? keyId : null,
                    WrappedDek = envelope?.WrappedDek,
                    KekId = envelope?.KekId,
                    KeyStorePluginId = config.Mode == KeyManagementMode.Direct
                        ? config.KeyStorePluginId
                        : config.EnvelopeKeyStorePluginId,
                    AlgorithmParams = new Dictionary<string, object>
                    {
                        ["iv"] = Convert.ToBase64String(iv),
                        ["algorithm"] = AlgorithmId
                    },
                    EncryptedAt = DateTime.UtcNow,
                    EncryptedBy = securityContext.UserId
                };

                // Store metadata in args for downstream consumers (tamper-proof manifest, etc.)
                args["encryptionMetadata"] = metadata;

                // Perform encryption
                var result = await EncryptCoreAsync(input, key, iv, context);

                // Update statistics
                UpdateEncryptionStats(input.Length);

                return result;
            }
            finally
            {
                // Clear key from memory
                CryptographicOperations.ZeroMemory(key);
            }
        }

        /// <summary>
        /// Decrypts data during read operations.
        /// Uses stored metadata to determine correct decryption approach.
        /// </summary>
        protected override async Task<Stream> OnReadAsync(Stream stored, IKernelContext context, Dictionary<string, object> args)
        {
            var securityContext = GetSecurityContext(args, context);

            // Try to get encryption metadata from args (from manifest or header)
            EnvelopeHeader? envelope = null;
            string? keyId = null;
            byte[]? iv = null;

            if (args.TryGetValue("encryptionMetadata", out var metaObj) && metaObj is EncryptionMetadata metadata)
            {
                // Use metadata from manifest
                envelope = metadata.KeyMode == KeyManagementMode.Envelope
                    ? new EnvelopeHeader
                    {
                        KekId = metadata.KekId ?? "",
                        KeyStorePluginId = metadata.KeyStorePluginId ?? "",
                        WrappedDek = metadata.WrappedDek ?? Array.Empty<byte>()
                    }
                    : null;
                keyId = metadata.KeyId;

                if (metadata.AlgorithmParams.TryGetValue("iv", out var ivObj) && ivObj is string ivStr)
                {
                    iv = Convert.FromBase64String(ivStr);
                }
            }
            else
            {
                // Check for envelope header in stream
                if (await EnvelopeHeader.IsEnvelopeEncryptedAsync(stored))
                {
                    // Read and parse envelope header (pooled buffer to reduce GC pressure on hot path)
                    var headerBuffer = ArrayPool<byte>.Shared.Rent(4096);
                    try
                    {
                        var bytesRead = await stored.ReadAsync(headerBuffer, 0, 4096);
                        stored.Position = 0;

                        if (EnvelopeHeader.TryDeserialize(headerBuffer, out envelope, out var headerLength))
                        {
                            // Skip header for decryption
                            stored.Position = headerLength;
                            iv = envelope!.Iv;
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(headerBuffer, clearArray: true);
                    }
                }
                else if (args.TryGetValue("keyId", out var kidObj) && kidObj is string kid)
                {
                    keyId = kid;
                }
            }

            // Resolve config (may be overridden by args for testing/migration)
            var config = await ResolveConfigAsync(args, securityContext);

            // Get decryption key
            var key = await GetKeyForDecryptionAsync(envelope, keyId, config, securityContext);

            try
            {
                // Perform decryption
                var (result, _) = await DecryptCoreAsync(stored, key, iv, context);

                // Update statistics
                UpdateDecryptionStats(stored.Length);

                return result;
            }
            finally
            {
                // Clear key from memory
                CryptographicOperations.ZeroMemory(key);
            }
        }

        #endregion

        #region Abstract Methods (Algorithm-Specific)

        /// <summary>
        /// Performs the core encryption operation.
        /// Must be implemented by derived classes with specific algorithms.
        /// </summary>
        /// <param name="input">The plaintext input stream.</param>
        /// <param name="key">The encryption key.</param>
        /// <param name="iv">The initialization vector.</param>
        /// <param name="context">The kernel context.</param>
        /// <returns>The encrypted stream.</returns>
        protected abstract Task<Stream> EncryptCoreAsync(Stream input, byte[] key, byte[] iv, IKernelContext context);

        /// <summary>
        /// Performs the core decryption operation.
        /// Must be implemented by derived classes with specific algorithms.
        /// </summary>
        /// <param name="input">The ciphertext input stream.</param>
        /// <param name="key">The decryption key.</param>
        /// <param name="iv">The initialization vector (null if embedded in ciphertext).</param>
        /// <param name="context">The kernel context.</param>
        /// <returns>The decrypted stream and authentication tag (if applicable).</returns>
        protected abstract Task<(Stream data, byte[]? tag)> DecryptCoreAsync(Stream input, byte[] key, byte[]? iv, IKernelContext context);

        /// <summary>
        /// Generates a random IV/nonce.
        /// Override if algorithm requires specific IV generation.
        /// </summary>
        protected virtual byte[] GenerateIv()
        {
            var iv = new byte[IvSizeBytes];
            System.Security.Cryptography.RandomNumberGenerator.Fill(iv);
            return iv;
        }

        #endregion

        #region Statistics

        /// <summary>
        /// Updates encryption statistics.
        /// </summary>
        protected virtual void UpdateEncryptionStats(long bytesProcessed)
        {
            lock (StatsLock)
            {
                EncryptionCount++;
                TotalBytesEncrypted += bytesProcessed;
            }
        }

        /// <summary>
        /// Updates decryption statistics.
        /// </summary>
        protected virtual void UpdateDecryptionStats(long bytesProcessed)
        {
            lock (StatsLock)
            {
                DecryptionCount++;
                TotalBytesDecrypted += bytesProcessed;
            }
        }

        /// <summary>
        /// Gets encryption statistics.
        /// </summary>
        public virtual EncryptionStatistics GetStatistics()
        {
            lock (StatsLock)
            {
                return new EncryptionStatistics
                {
                    EncryptionCount = EncryptionCount,
                    DecryptionCount = DecryptionCount,
                    TotalBytesEncrypted = TotalBytesEncrypted,
                    TotalBytesDecrypted = TotalBytesDecrypted,
                    UniqueKeysUsed = KeyAccessLog.Count,
                    LastKeyAccess = KeyAccessLog.Values.DefaultIfEmpty().Max()
                };
            }
        }

        #endregion

        #region Configuration Methods

        /// <summary>
        /// Configures the default key store for Direct mode.
        /// </summary>
        public virtual void SetDefaultKeyStore(IKeyStore keyStore)
        {
            DefaultKeyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
        }

        /// <summary>
        /// Configures the default envelope key store for Envelope mode.
        /// </summary>
        public virtual void SetDefaultEnvelopeKeyStore(IEnvelopeKeyStore envelopeKeyStore, string kekKeyId)
        {
            DefaultEnvelopeKeyStore = envelopeKeyStore ?? throw new ArgumentNullException(nameof(envelopeKeyStore));
            DefaultKekKeyId = kekKeyId ?? throw new ArgumentNullException(nameof(kekKeyId));
        }

        /// <summary>
        /// Sets the default key management mode.
        /// </summary>
        public virtual void SetDefaultMode(KeyManagementMode mode)
        {
            DefaultKeyManagementMode = mode;
        }

        /// <summary>
        /// Sets the per-user configuration provider.
        /// </summary>
        public virtual void SetConfigProvider(IKeyManagementConfigProvider provider)
        {
            ConfigProvider = provider;
        }

        /// <summary>
        /// Sets the key store registry.
        /// </summary>
        public virtual void SetKeyStoreRegistry(IKeyStoreRegistry registry)
        {
            KeyStoreRegistry = registry;
        }

        #endregion

        #region Strategy Registry Support

        /// <summary>
        /// Strategy registry for this encryption plugin.
        /// Override to provide access to the strategy registry.
        /// </summary>
        protected virtual IEncryptionStrategyRegistry? StrategyRegistry => null;

        /// <inheritdoc/>
        protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities
        {
            get
            {
                var capabilities = new List<RegisteredCapability>();

                // Add base encryption capability
                capabilities.Add(new RegisteredCapability
                {
                    CapabilityId = $"{Id}.encrypt",
                    DisplayName = $"{Name} - Encrypt",
                    Description = "Encrypt data",
                    Category = CapabilityCategory.Encryption,
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = GetEncryptionTags()
                });

                capabilities.Add(new RegisteredCapability
                {
                    CapabilityId = $"{Id}.decrypt",
                    DisplayName = $"{Name} - Decrypt",
                    Description = "Decrypt data",
                    Category = CapabilityCategory.Encryption,
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = GetEncryptionTags()
                });

                // Auto-generate capabilities from strategy registry
                var registry = StrategyRegistry;
                if (registry != null)
                {
                    foreach (var strategy in registry.GetAllStrategies())
                    {
                        var tags = new List<string> { "encryption", "strategy" };
                        tags.Add(strategy.CipherInfo.SecurityLevel.ToString().ToLowerInvariant());
                        if (strategy.CipherInfo.Capabilities.IsAuthenticated) tags.Add("aead");
                        if (strategy.CipherInfo.Capabilities.IsStreamable) tags.Add("streaming");
                        if (strategy.CipherInfo.Capabilities.IsHardwareAcceleratable) tags.Add("hardware-accelerated");

                        capabilities.Add(new RegisteredCapability
                        {
                            CapabilityId = $"{Id}.strategy.{strategy.StrategyId}",
                            DisplayName = strategy.StrategyName,
                            Description = $"{strategy.CipherInfo.AlgorithmName} ({strategy.CipherInfo.KeySizeBits}-bit)",
                            Category = CapabilityCategory.Encryption,
                            SubCategory = strategy.CipherInfo.SecurityLevel.ToString(),
                            PluginId = Id,
                            PluginName = Name,
                            PluginVersion = Version,
                            Tags = tags.ToArray(),
                            Priority = (int)strategy.CipherInfo.SecurityLevel * 10,
                            Metadata = new Dictionary<string, object>
                            {
                                ["algorithm"] = strategy.CipherInfo.AlgorithmName,
                                ["keySizeBits"] = strategy.CipherInfo.KeySizeBits,
                                ["isAuthenticated"] = strategy.CipherInfo.Capabilities.IsAuthenticated,
                                ["securityLevel"] = strategy.CipherInfo.SecurityLevel.ToString()
                            },
                            SemanticDescription = $"Encrypt using {strategy.CipherInfo.AlgorithmName} with {strategy.CipherInfo.KeySizeBits}-bit key"
                        });
                    }
                }

                return capabilities;
            }
        }

        protected virtual string[] GetEncryptionTags() => new[] { "encryption", "security", "crypto" };

        /// <inheritdoc/>
        protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
        {
            var knowledge = new List<KnowledgeObject>(base.GetStaticKnowledge());

            var registry = StrategyRegistry;
            if (registry != null)
            {
                var strategies = registry.GetAllStrategies();
                knowledge.Add(new KnowledgeObject
                {
                    Id = $"{Id}.strategies",
                    Topic = "encryption.strategies",
                    SourcePluginId = Id,
                    SourcePluginName = Name,
                    KnowledgeType = "capability",
                    Description = $"{strategies.Count} encryption strategies available",
                    Payload = new Dictionary<string, object>
                    {
                        ["count"] = strategies.Count,
                        ["algorithms"] = strategies.Select(s => s.CipherInfo.AlgorithmName).Distinct().ToArray(),
                        ["aeadCount"] = strategies.Count(s => s.CipherInfo.Capabilities.IsAuthenticated),
                        ["hardwareAccelerated"] = strategies.Count(s => s.CipherInfo.Capabilities.IsHardwareAcceleratable)
                    },
                    Tags = new[] { "encryption", "strategies", "summary" }
                });
            }

            return knowledge;
        }

        #endregion

        #region Metadata

        public override string SubCategory => "Encryption";

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["EncryptionAlgorithm"] = AlgorithmId;
            metadata["KeySizeBytes"] = KeySizeBytes;
            metadata["IvSizeBytes"] = IvSizeBytes;
            metadata["TagSizeBytes"] = TagSizeBytes;
            metadata["SupportsEnvelopeMode"] = true;
            metadata["SupportsDirectMode"] = true;
            metadata["SupportsPerUserConfig"] = true;
            return metadata;
        }

        #endregion

        #region IDisposable

        private bool _disposed;

        protected override void Dispose(bool disposing)
        {
            if (_disposed) return;

            if (disposing)
            {
                KeyAccessLog.Clear();
            }

            _disposed = true;
            base.Dispose(disposing);
        }

        #endregion
    }

    /// <summary>
    /// Statistics for encryption plugin operations.
    /// </summary>
    public record EncryptionStatistics
    {
        public long EncryptionCount { get; init; }
        public long DecryptionCount { get; init; }
        public long TotalBytesEncrypted { get; init; }
        public long TotalBytesDecrypted { get; init; }
        public int UniqueKeysUsed { get; init; }
        public DateTime LastKeyAccess { get; init; }
    }

    #endregion

    #region Compression Plugin Base

    /// <summary>
    /// Abstract base class for all compression plugins with configurable algorithm selection.
    /// Supports per-user, per-operation configuration for maximum flexibility.
    /// All compression pipeline plugins MUST extend this class.
    /// </summary>
    public abstract class CompressionPluginBase : PipelinePluginBase
    {
        /// <summary>SubCategory is always "Compression".</summary>
        public override string SubCategory => "Compression";

        /// <summary>Default pipeline order (runs before encryption).</summary>
        public override int DefaultOrder => 50;

        /// <summary>Whether bypass is allowed based on content analysis.</summary>
        public override bool AllowBypass => true;

        // --- Compression-specific abstract properties ---

        /// <summary>The compression algorithm identifier (e.g., "Zstd", "LZ4", "Brotli").</summary>
        protected abstract string AlgorithmId { get; }

        /// <summary>Whether this algorithm supports streaming compression.</summary>
        protected abstract bool SupportsStreaming { get; }

        /// <summary>Whether this algorithm supports parallel compression.</summary>
        protected virtual bool SupportsParallel => false;

        /// <summary>Typical compression ratio (0.0-1.0, lower = better compression).</summary>
        protected virtual double TypicalRatio => 0.5;

        // --- Statistics ---
        protected readonly object StatsLock = new();
        protected long CompressionCount;
        protected long DecompressionCount;
        protected long TotalBytesCompressed;
        protected long TotalBytesDecompressed;

        // --- Entropy-based bypass ---

        /// <summary>Maximum entropy (0-8) to attempt compression. Above this, bypass.</summary>
        protected virtual double MaxEntropy => 7.8;

        /// <summary>
        /// Calculates Shannon entropy of data sample.
        /// Used to decide whether compression is worthwhile.
        /// </summary>
        protected static double CalculateEntropy(ReadOnlySpan<byte> data)
        {
            if (data.Length == 0) return 0;
            Span<int> freq = stackalloc int[256];
            freq.Clear();
            foreach (var b in data) freq[b]++;
            double entropy = 0.0, len = data.Length;
            for (int i = 0; i < 256; i++)
            {
                if (freq[i] == 0) continue;
                double p = freq[i] / len;
                entropy -= p * Math.Log2(p);
            }
            return entropy;
        }

        #region Strategy Registry Support

        /// <summary>
        /// Gets all compression strategies registered with this plugin.
        /// Override to provide access to compression strategies.
        /// </summary>
        protected virtual IEnumerable<Compression.ICompressionStrategy>? GetAllStrategies() => null;

        /// <inheritdoc/>
        protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities
        {
            get
            {
                var capabilities = new List<RegisteredCapability>();

                capabilities.Add(new RegisteredCapability
                {
                    CapabilityId = $"{Id}.compress",
                    DisplayName = $"{Name} - Compress",
                    Description = "Compress data",
                    Category = CapabilityCategory.Compression,
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = new[] { "compression", "data-transformation" }
                });

                capabilities.Add(new RegisteredCapability
                {
                    CapabilityId = $"{Id}.decompress",
                    DisplayName = $"{Name} - Decompress",
                    Description = "Decompress data",
                    Category = CapabilityCategory.Compression,
                    PluginId = Id,
                    PluginName = Name,
                    PluginVersion = Version,
                    Tags = new[] { "compression", "data-transformation" }
                });

                var strategies = GetAllStrategies();
                if (strategies != null)
                {
                    foreach (var strategy in strategies)
                    {
                        var chars = strategy.Characteristics;
                        var tags = new List<string> { "compression", "strategy", chars.AlgorithmName.ToLowerInvariant() };
                        if (chars.SupportsStreaming) tags.Add("streaming");
                        if (chars.SupportsParallelCompression) tags.Add("parallel");

                        capabilities.Add(new RegisteredCapability
                        {
                            CapabilityId = $"{Id}.strategy.{chars.AlgorithmName.ToLowerInvariant()}",
                            DisplayName = $"{chars.AlgorithmName} - {strategy.Level}",
                            Description = $"{chars.AlgorithmName} compression at {strategy.Level} level",
                            Category = CapabilityCategory.Compression,
                            SubCategory = chars.AlgorithmName,
                            PluginId = Id,
                            PluginName = Name,
                            PluginVersion = Version,
                            Tags = tags.ToArray(),
                            Priority = chars.SupportsStreaming ? 60 : 50,
                            Metadata = new Dictionary<string, object>
                            {
                                ["algorithm"] = chars.AlgorithmName,
                                ["supportsStreaming"] = chars.SupportsStreaming,
                                ["supportsParallelCompression"] = chars.SupportsParallelCompression,
                                ["compressionRatio"] = chars.TypicalCompressionRatio,
                                ["compressionSpeed"] = chars.CompressionSpeed,
                                ["decompressionSpeed"] = chars.DecompressionSpeed
                            },
                            SemanticDescription = $"Compress using {chars.AlgorithmName} algorithm"
                        });
                    }
                }

                return capabilities;
            }
        }

        /// <inheritdoc/>
        protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
        {
            var knowledge = new List<KnowledgeObject>(base.GetStaticKnowledge());

            var strategies = GetAllStrategies()?.ToList();
            if (strategies != null && strategies.Any())
            {
                knowledge.Add(new KnowledgeObject
                {
                    Id = $"{Id}.strategies",
                    Topic = "compression.strategies",
                    SourcePluginId = Id,
                    SourcePluginName = Name,
                    KnowledgeType = "capability",
                    Description = $"{strategies.Count} compression strategies available",
                    Payload = new Dictionary<string, object>
                    {
                        ["count"] = strategies.Count,
                        ["algorithms"] = strategies.Select(s => s.Characteristics.AlgorithmName).Distinct().ToArray(),
                        ["streamingCount"] = strategies.Count(s => s.Characteristics.SupportsStreaming),
                        ["parallelCount"] = strategies.Count(s => s.Characteristics.SupportsParallelCompression)
                    },
                    Tags = new[] { "compression", "strategies", "summary" }
                });
            }

            return knowledge;
        }

        #endregion

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["AlgorithmId"] = AlgorithmId;
            metadata["SupportsStreaming"] = SupportsStreaming;
            metadata["SupportsParallel"] = SupportsParallel;
            metadata["TypicalRatio"] = TypicalRatio;
            metadata["MaxEntropy"] = MaxEntropy;
            metadata["CompressionCount"] = CompressionCount;
            metadata["DecompressionCount"] = DecompressionCount;
            return metadata;
        }
    }

    #endregion

    /// <summary>
    /// Abstract base class for container/partition manager plugins.
    /// Provides storage-agnostic partition management.
    /// AI-native: Supports intelligent quota management and access suggestions.
    /// </summary>
    public abstract class ContainerManagerPluginBase : ComputePluginBase, IContainerManager
    {
        /// <inheritdoc/>
        public override string RuntimeType => "Container";

        /// <inheritdoc/>
        public override Task<Dictionary<string, object>> ExecuteWorkloadAsync(Dictionary<string, object> workload, CancellationToken ct = default)
            => Task.FromResult(new Dictionary<string, object> { ["status"] = "delegated-to-container-manager" });

        /// <summary>
        /// Category is always OrchestrationProvider for container managers.
        /// </summary>
        public override PluginCategory Category => PluginCategory.OrchestrationProvider;

        public abstract Task<ContainerInfo> CreateContainerAsync(
            ISecurityContext context,
            string containerId,
            ContainerOptions? options = null,
            CancellationToken ct = default);

        public abstract Task<ContainerInfo?> GetContainerAsync(
            ISecurityContext context,
            string containerId,
            CancellationToken ct = default);

        public abstract IAsyncEnumerable<ContainerInfo> ListContainersAsync(
            ISecurityContext context,
            CancellationToken ct = default);

        public abstract Task DeleteContainerAsync(
            ISecurityContext context,
            string containerId,
            CancellationToken ct = default);

        public abstract Task GrantAccessAsync(
            ISecurityContext ownerContext,
            string containerId,
            string targetUserId,
            ContainerAccessLevel level,
            CancellationToken ct = default);

        public abstract Task RevokeAccessAsync(
            ISecurityContext ownerContext,
            string containerId,
            string targetUserId,
            CancellationToken ct = default);

        public abstract Task<ContainerAccessLevel> GetAccessLevelAsync(
            ISecurityContext context,
            string containerId,
            string? userId = null,
            CancellationToken ct = default);

        public abstract IAsyncEnumerable<ContainerAccessEntry> ListAccessAsync(
            ISecurityContext context,
            string containerId,
            CancellationToken ct = default);

        public virtual Task<ContainerQuota> GetQuotaAsync(
            ISecurityContext context,
            string containerId,
            CancellationToken ct = default)
        {
            return Task.FromResult(new ContainerQuota());
        }

        public virtual Task SetQuotaAsync(
            ISecurityContext adminContext,
            string containerId,
            ContainerQuota quota,
            CancellationToken ct = default)
        {
            return Task.CompletedTask;
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "ContainerManager";
            metadata["SupportsQuotas"] = true;
            metadata["SupportsAccessControl"] = true;
            metadata["StorageAgnostic"] = true;
            return metadata;
        }
    }
}
