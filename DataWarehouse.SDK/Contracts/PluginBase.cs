using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts.Compression;
using DataWarehouse.SDK.Contracts.Encryption;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Primitives.Configuration;
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Utilities;
using System;
using System.Collections.Concurrent;
using System.Linq;
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
