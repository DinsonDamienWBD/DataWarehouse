using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateAccessControl
{
    /// <summary>
    /// Ultimate Access Control plugin providing comprehensive security strategies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implements multiple access control paradigms:
    /// - RBAC (Role-Based Access Control): Traditional role/permission model
    /// - ABAC (Attribute-Based Access Control): Fine-grained attribute-based policies
    /// - Zero Trust: Continuous verification with risk assessment
    /// </para>
    /// <para>
    /// Security features:
    /// - Canary/Honeypot: Detect unauthorized access with decoy objects
    /// - Steganography: Hide data within carrier files
    /// - Ephemeral Sharing: Time-limited, self-destructing shares
    /// - Watermarking: Forensic tracing for leak detection
    /// </para>
    /// </remarks>
    public sealed class UltimateAccessControlPlugin : FeaturePluginBase, IDisposable
    {
        private readonly ConcurrentDictionary<string, IAccessControlStrategy> _strategies = new();
        private IAccessControlStrategy? _defaultStrategy;
        private IMessageBus? _messageBus;
        private bool _initialized;
        private bool _disposed;

        /// <inheritdoc/>
        public override string Id => "com.datawarehouse.accesscontrol.ultimate";

        /// <inheritdoc/>
        public override string Name => "Ultimate Access Control";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <inheritdoc/>
        public override PluginCategory Category => PluginCategory.FeatureProvider;

        /// <summary>
        /// Gets all registered strategies.
        /// </summary>
        public IReadOnlyCollection<IAccessControlStrategy> GetStrategies() => _strategies.Values.ToList().AsReadOnly();

        /// <summary>
        /// Gets a strategy by ID.
        /// </summary>
        public IAccessControlStrategy? GetStrategy(string strategyId)
        {
            return _strategies.TryGetValue(strategyId, out var strategy) ? strategy : null;
        }

        /// <summary>
        /// Registers a strategy.
        /// </summary>
        public void RegisterStrategy(IAccessControlStrategy strategy)
        {
            _strategies[strategy.StrategyId] = strategy;
        }

        /// <summary>
        /// Sets the default strategy.
        /// </summary>
        public void SetDefaultStrategy(string strategyId)
        {
            if (_strategies.TryGetValue(strategyId, out var strategy))
            {
                _defaultStrategy = strategy;
            }
        }

        /// <summary>
        /// Evaluates access using the specified or default strategy.
        /// </summary>
        public async Task<AccessDecision> EvaluateAccessAsync(
            AccessContext context,
            string? strategyId = null,
            CancellationToken cancellationToken = default)
        {
            var strategy = !string.IsNullOrEmpty(strategyId)
                ? GetStrategy(strategyId) ?? throw new ArgumentException($"Strategy '{strategyId}' not found")
                : _defaultStrategy ?? throw new InvalidOperationException("No default strategy configured");

            return await strategy.EvaluateAccessAsync(context, cancellationToken);
        }

        /// <inheritdoc/>
        public override async Task StartAsync(CancellationToken ct)
        {
            if (_initialized)
                return;

            await DiscoverAndRegisterStrategiesAsync(ct);

            // Set default strategy
            if (_strategies.TryGetValue("rbac", out var rbac))
            {
                _defaultStrategy = rbac;
            }
            else if (_strategies.Any())
            {
                _defaultStrategy = _strategies.Values.First();
            }

            _initialized = true;
        }

        /// <inheritdoc/>
        public override Task StopAsync()
        {
            _initialized = false;
            return Task.CompletedTask;
        }

        private async Task DiscoverAndRegisterStrategiesAsync(CancellationToken ct)
        {
            var strategyType = typeof(IAccessControlStrategy);
            var assembly = Assembly.GetExecutingAssembly();

            var types = assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && strategyType.IsAssignableFrom(t));

            foreach (var type in types)
            {
                if (ct.IsCancellationRequested)
                    break;

                try
                {
                    if (Activator.CreateInstance(type) is IAccessControlStrategy strategy)
                    {
                        await strategy.InitializeAsync(new Dictionary<string, object>(), ct);
                        _strategies[strategy.StrategyId] = strategy;
                    }
                }
                catch
                {
                    // Skip strategies that fail to initialize
                }
            }
        }

        /// <inheritdoc/>
        protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities
        {
            get
            {
                var capabilities = new List<RegisteredCapability>
                {
                    new()
                    {
                        CapabilityId = "access-control.ultimate",
                        PluginId = Id,
                        PluginName = Name,
                        PluginVersion = Version,
                        DisplayName = "Ultimate Access Control",
                        Description = "Comprehensive access control with RBAC, ABAC, Zero Trust, and advanced security features",
                        Category = SDK.Contracts.CapabilityCategory.Security,
                        Tags = ["accesscontrol", "security", "rbac", "abac", "zerotrust"]
                    }
                };

                foreach (var (strategyId, strategy) in _strategies)
                {
                    var caps = strategy.Capabilities;
                    var tags = new List<string> { "accesscontrol", "security", strategyId };

                    if (caps.SupportsRealTimeDecisions)
                        tags.Add("realtime");
                    if (caps.SupportsTemporalAccess)
                        tags.Add("temporal");
                    if (caps.SupportsGeographicRestrictions)
                        tags.Add("geographic");

                    capabilities.Add(new RegisteredCapability
                    {
                        CapabilityId = $"access-control.{strategyId}",
                        PluginId = Id,
                        PluginName = Name,
                        PluginVersion = Version,
                        DisplayName = strategy.StrategyName,
                        Description = $"Access control strategy: {strategy.StrategyName}",
                        Category = SDK.Contracts.CapabilityCategory.Security,
                        Tags = tags.ToArray()
                    });
                }

                return capabilities.AsReadOnly();
            }
        }

        /// <inheritdoc/>
        protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
        {
            var strategyIds = _strategies.Keys.ToList();

            return new List<KnowledgeObject>
            {
                new()
                {
                    Id = $"{Id}:overview",
                    Topic = "access-control",
                    SourcePluginId = Id,
                    SourcePluginName = Name,
                    KnowledgeType = "capability",
                    Description = $"Ultimate Access Control Plugin provides {_strategies.Count} access control strategies: {string.Join(", ", strategyIds)}. " +
                                  "Includes RBAC (Role-Based Access Control), ABAC (Attribute-Based Access Control), Zero Trust verification, " +
                                  "Canary/Honeypot detection, Steganography data hiding, Ephemeral Sharing, and Forensic Watermarking.",
                    Tags = ["accesscontrol", "security", "rbac", "abac", "zerotrust", "canary", "steganography", "watermarking"],
                    Payload = new Dictionary<string, object>
                    {
                        ["strategyCount"] = _strategies.Count,
                        ["strategies"] = strategyIds,
                        ["defaultStrategy"] = _defaultStrategy?.StrategyId ?? "none"
                    }
                }
            };
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "AccessControl";
            metadata["SupportsAutoDiscovery"] = true;
            metadata["RegisteredStrategies"] = _strategies.Count;
            metadata["DefaultStrategy"] = _defaultStrategy?.StrategyId ?? "none";
            return metadata;
        }

        /// <inheritdoc/>
        public override async Task OnMessageAsync(PluginMessage message)
        {
            switch (message.Type)
            {
                case "accesscontrol.evaluate":
                    await HandleEvaluateAsync(message);
                    break;

                case "accesscontrol.list-strategies":
                    HandleListStrategies(message);
                    break;

                case "accesscontrol.set-default":
                    HandleSetDefault(message);
                    break;
            }

            await base.OnMessageAsync(message);
        }

        private async Task HandleEvaluateAsync(PluginMessage message)
        {
            if (!message.Payload.TryGetValue("Context", out var contextObj) ||
                contextObj is not AccessContext context)
            {
                message.Payload["Error"] = "Missing or invalid Context";
                return;
            }

            var strategyId = message.Payload.TryGetValue("StrategyId", out var sidObj) && sidObj is string sid
                ? sid
                : null;

            var decision = await EvaluateAccessAsync(context, strategyId);
            message.Payload["Decision"] = decision;
        }

        private void HandleListStrategies(PluginMessage message)
        {
            var strategies = _strategies.Values.Select(s => new Dictionary<string, object>
            {
                ["Id"] = s.StrategyId,
                ["Name"] = s.StrategyName,
                ["SupportsRealTime"] = s.Capabilities.SupportsRealTimeDecisions,
                ["SupportsAudit"] = s.Capabilities.SupportsAuditTrail,
                ["SupportsTemporal"] = s.Capabilities.SupportsTemporalAccess,
                ["SupportsGeo"] = s.Capabilities.SupportsGeographicRestrictions
            }).ToList();

            message.Payload["Strategies"] = strategies;
            message.Payload["Count"] = strategies.Count;
        }

        private void HandleSetDefault(PluginMessage message)
        {
            if (message.Payload.TryGetValue("StrategyId", out var sidObj) && sidObj is string strategyId)
            {
                SetDefaultStrategy(strategyId);
                message.Payload["Success"] = true;
                message.Payload["DefaultStrategy"] = _defaultStrategy?.StrategyId ?? "none";
            }
            else
            {
                message.Payload["Success"] = false;
                message.Payload["Error"] = "Missing StrategyId";
            }
        }

        /// <inheritdoc/>
        public override Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
        {
            if (request.Config != null &&
                request.Config.TryGetValue("MessageBus", out var mbObj) &&
                mbObj is IMessageBus messageBus)
            {
                _messageBus = messageBus;
                SetMessageBus(messageBus);
            }

            return base.OnHandshakeAsync(request);
        }

        /// <summary>
        /// Disposes resources.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _strategies.Clear();
        }
    }
}
