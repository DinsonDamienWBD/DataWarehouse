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
using DataWarehouse.SDK.Security;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateKeyManagement
{
    public class UltimateKeyManagementPlugin : FeaturePluginBase, IKeyStoreRegistry, IDisposable
    {
        private readonly ConcurrentDictionary<string, IKeyStore> _keyStores = new();
        private readonly ConcurrentDictionary<string, IEnvelopeKeyStore> _envelopeKeyStores = new();
        private readonly ConcurrentDictionary<string, IKeyStoreStrategy> _strategies = new();
        private UltimateKeyManagementConfig _config = new();
        private KeyRotationScheduler? _rotationScheduler;
        private IMessageBus? _messageBus;
        private bool _initialized;
        private bool _disposed;

        public override string Id => "com.datawarehouse.keymanagement.ultimate";
        public override string Name => "Ultimate Key Management";
        public override string Version => "1.0.0";
        public override PluginCategory Category => PluginCategory.FeatureProvider;

        protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities
        {
            get
            {
                var capabilities = new List<RegisteredCapability>
                {
                    new()
                    {
                        CapabilityId = "key-management",
                        PluginId = Id,
                        PluginName = Name,
                        PluginVersion = Version,
                        DisplayName = "Ultimate Key Management",
                        Description = "Comprehensive key management system with multiple storage strategies, envelope encryption, key rotation, and HSM support",
                        Category = SDK.Contracts.CapabilityCategory.Security,
                        Tags = ["keymanagement", "security", "encryption", "rotation", "hsm", "envelope"]
                    }
                };

                foreach (var (id, strategy) in _strategies)
                {
                    var caps = strategy.Capabilities;
                    var tags = new List<string> { "keystore", "security" };

                    if (caps.SupportsEnvelope)
                        tags.Add("envelope");
                    if (caps.SupportsRotation)
                        tags.Add("rotation");
                    if (caps.SupportsHsm)
                        tags.Add("hsm");

                    capabilities.Add(new RegisteredCapability
                    {
                        CapabilityId = $"keystore-{id.Replace(".", "-").Replace(" ", "-").ToLowerInvariant()}",
                        PluginId = Id,
                        PluginName = Name,
                        PluginVersion = Version,
                        DisplayName = $"Key Store: {id}",
                        Description = $"Key storage strategy supporting: " +
                                    $"Envelope={caps.SupportsEnvelope}, " +
                                    $"Rotation={caps.SupportsRotation}, " +
                                    $"HSM={caps.SupportsHsm}",
                        Category = SDK.Contracts.CapabilityCategory.Security,
                        Tags = tags.ToArray()
                    });
                }

                return capabilities.AsReadOnly();
            }
        }

        public override async Task StartAsync(CancellationToken ct)
        {
            if (_initialized)
                return;

            _config = new UltimateKeyManagementConfig();

            if (_config.AutoDiscoverStrategies)
            {
                await DiscoverAndRegisterStrategiesAsync(ct);
            }

            if (_config.EnableKeyRotation)
            {
                _rotationScheduler = new KeyRotationScheduler(_config, _messageBus);

                foreach (var (strategyId, strategy) in _strategies)
                {
                    var policy = GetRotationPolicyForStrategy(strategyId);
                    _rotationScheduler.RegisterStrategy(strategyId, strategy, policy);
                }

                _rotationScheduler.Start();
            }

            _initialized = true;

            await PublishEventAsync("keymanagement.started", new Dictionary<string, object>
            {
                ["strategiesRegistered"] = _strategies.Count,
                ["rotationEnabled"] = _config.EnableKeyRotation
            });
        }

        public override async Task StopAsync()
        {
            if (!_initialized)
                return;

            if (_rotationScheduler != null)
            {
                await _rotationScheduler.StopAsync();
                _rotationScheduler.Dispose();
                _rotationScheduler = null;
            }

            _initialized = false;

            await PublishEventAsync("keymanagement.stopped", new Dictionary<string, object>
            {
                ["timestamp"] = DateTime.UtcNow
            });
        }

        private async Task DiscoverAndRegisterStrategiesAsync(CancellationToken ct)
        {
            var assemblies = GetAssembliesForDiscovery();
            var strategyType = typeof(IKeyStoreStrategy);

            foreach (var assembly in assemblies)
            {
                if (ct.IsCancellationRequested)
                    break;

                try
                {
                    var types = assembly.GetTypes()
                        .Where(t => t.IsClass && !t.IsAbstract && strategyType.IsAssignableFrom(t));

                    foreach (var type in types)
                    {
                        try
                        {
                            await RegisterStrategyTypeAsync(type, ct);
                        }
                        catch (Exception ex)
                        {
                            await PublishEventAsync("keymanagement.strategy.registration.failed", new Dictionary<string, object>
                            {
                                ["strategyType"] = type.FullName ?? type.Name,
                                ["error"] = ex.Message
                            });
                        }
                    }
                }
                catch
                {
                    continue;
                }
            }
        }

        private IEnumerable<Assembly> GetAssembliesForDiscovery()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();

            if (_config.DiscoveryAssemblyPatterns.Count == 0 && _config.DiscoveryExcludePatterns.Count == 0)
            {
                return assemblies;
            }

            var filtered = assemblies.AsEnumerable();

            if (_config.DiscoveryAssemblyPatterns.Count > 0)
            {
                filtered = filtered.Where(a =>
                {
                    var name = a.GetName().Name ?? "";
                    return _config.DiscoveryAssemblyPatterns.Any(pattern => MatchesPattern(name, pattern));
                });
            }

            if (_config.DiscoveryExcludePatterns.Count > 0)
            {
                filtered = filtered.Where(a =>
                {
                    var name = a.GetName().Name ?? "";
                    return !_config.DiscoveryExcludePatterns.Any(pattern => MatchesPattern(name, pattern));
                });
            }

            return filtered;
        }

        private bool MatchesPattern(string name, string pattern)
        {
            var regexPattern = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";

            return System.Text.RegularExpressions.Regex.IsMatch(name, regexPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private async Task RegisterStrategyTypeAsync(Type strategyType, CancellationToken ct)
        {
            var instance = Activator.CreateInstance(strategyType);
            if (instance is not IKeyStoreStrategy strategy)
                return;

            var strategyId = strategyType.FullName ?? strategyType.Name;

            var configuration = GetConfigurationForStrategy(strategyId);

            await strategy.InitializeAsync(configuration, ct);

            _strategies[strategyId] = strategy;

            Register(strategyId, strategy);

            if (strategy is IEnvelopeKeyStore envelopeKeyStore)
            {
                RegisterEnvelope(strategyId, envelopeKeyStore);
            }

            await PublishEventAsync("keymanagement.strategy.registered", new Dictionary<string, object>
            {
                ["strategyId"] = strategyId,
                ["strategyType"] = strategyType.Name,
                ["supportsEnvelope"] = strategy.Capabilities.SupportsEnvelope,
                ["supportsRotation"] = strategy.Capabilities.SupportsRotation,
                ["supportsHsm"] = strategy.Capabilities.SupportsHsm
            });
        }

        private Dictionary<string, object> GetConfigurationForStrategy(string strategyId)
        {
            if (_config.StrategyConfigurations.TryGetValue(strategyId, out var config))
            {
                var configCopy = new Dictionary<string, object>(config);
                if (_messageBus != null)
                {
                    configCopy["MessageBus"] = _messageBus;
                }
                return configCopy;
            }

            var defaultConfig = new Dictionary<string, object>();
            if (_messageBus != null)
            {
                defaultConfig["MessageBus"] = _messageBus;
            }
            return defaultConfig;
        }

        private KeyRotationPolicy GetRotationPolicyForStrategy(string strategyId)
        {
            if (_config.StrategyRotationPolicies.TryGetValue(strategyId, out var policy))
            {
                return policy;
            }

            return _config.DefaultRotationPolicy;
        }

        public void Register(string pluginId, IKeyStore keyStore)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
            ArgumentNullException.ThrowIfNull(keyStore);

            _keyStores[pluginId] = keyStore;

            if (keyStore is IEnvelopeKeyStore envelopeKeyStore)
            {
                _envelopeKeyStores[pluginId] = envelopeKeyStore;
            }
        }

        public void RegisterEnvelope(string pluginId, IEnvelopeKeyStore envelopeKeyStore)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
            ArgumentNullException.ThrowIfNull(envelopeKeyStore);

            _envelopeKeyStores[pluginId] = envelopeKeyStore;
            _keyStores[pluginId] = envelopeKeyStore;
        }

        public IKeyStore? GetKeyStore(string? pluginId)
        {
            if (string.IsNullOrEmpty(pluginId))
                return null;

            return _keyStores.TryGetValue(pluginId, out var keyStore) ? keyStore : null;
        }

        public IEnvelopeKeyStore? GetEnvelopeKeyStore(string? pluginId)
        {
            if (string.IsNullOrEmpty(pluginId))
                return null;

            return _envelopeKeyStores.TryGetValue(pluginId, out var keyStore) ? keyStore : null;
        }

        public IReadOnlyList<string> GetRegisteredKeyStoreIds()
        {
            return _keyStores.Keys.ToList().AsReadOnly();
        }

        public IReadOnlyList<string> GetRegisteredEnvelopeKeyStoreIds()
        {
            return _envelopeKeyStores.Keys.ToList().AsReadOnly();
        }

        public override async Task OnMessageAsync(PluginMessage message)
        {
            if (message.Type == "keymanagement.configure")
            {
                await HandleConfigurationMessageAsync(message);
            }
            else if (message.Type == "keymanagement.register.strategy")
            {
                await HandleStrategyRegistrationMessageAsync(message);
            }
            else if (message.Type == "keymanagement.rotate.now")
            {
                await HandleImmediateRotationMessageAsync(message);
            }

            await base.OnMessageAsync(message);
        }

        private Task HandleConfigurationMessageAsync(PluginMessage message)
        {
            return Task.CompletedTask;
        }

        private async Task HandleStrategyRegistrationMessageAsync(PluginMessage message)
        {
            if (message.Payload.TryGetValue("Strategy", out var strategyObj) && strategyObj is IKeyStoreStrategy strategy)
            {
                var strategyId = message.Payload.TryGetValue("StrategyId", out var idObj) && idObj is string id
                    ? id
                    : strategy.GetType().FullName ?? strategy.GetType().Name;

                var configuration = message.Payload.TryGetValue("Configuration", out var configObj) && configObj is Dictionary<string, object> config
                    ? config
                    : new Dictionary<string, object>();

                await strategy.InitializeAsync(configuration);

                _strategies[strategyId] = strategy;
                Register(strategyId, strategy);

                if (strategy is IEnvelopeKeyStore envelopeKeyStore)
                {
                    RegisterEnvelope(strategyId, envelopeKeyStore);
                }

                if (_rotationScheduler != null)
                {
                    var policy = GetRotationPolicyForStrategy(strategyId);
                    _rotationScheduler.RegisterStrategy(strategyId, strategy, policy);
                }
            }
        }

        private Task HandleImmediateRotationMessageAsync(PluginMessage message)
        {
            return Task.CompletedTask;
        }

        private async Task PublishEventAsync(string eventType, Dictionary<string, object> payload)
        {
            if (_messageBus == null || !_config.PublishKeyEvents)
                return;

            try
            {
                var message = new PluginMessage
                {
                    Type = eventType,
                    Payload = payload
                };

                await _messageBus.PublishAsync(eventType, message);
            }
            catch
            {
            }
        }

        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "KeyManagement";
            metadata["SupportsAutoDiscovery"] = true;
            metadata["SupportsKeyRotation"] = true;
            metadata["SupportsStrategyRegistry"] = true;
            metadata["RegisteredStrategies"] = _strategies.Count;
            metadata["RegisteredKeyStores"] = _keyStores.Count;
            metadata["RegisteredEnvelopeKeyStores"] = _envelopeKeyStores.Count;
            return metadata;
        }

        protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
        {
            var strategyIds = _strategies.Keys.ToList();
            var envelopeCount = _strategies.Values.Count(s => s.Capabilities.SupportsEnvelope);
            var rotationCount = _strategies.Values.Count(s => s.Capabilities.SupportsRotation);
            var hsmCount = _strategies.Values.Count(s => s.Capabilities.SupportsHsm);

            return new List<KnowledgeObject>
            {
                new()
                {
                    Id = $"{Id}:overview",
                    Topic = "key-management",
                    SourcePluginId = Id,
                    SourcePluginName = Name,
                    KnowledgeType = "capability",
                    Description = $"Ultimate Key Management Plugin provides comprehensive key storage and management with {_strategies.Count} registered strategies. " +
                              $"Supports envelope encryption ({envelopeCount} strategies), automatic key rotation ({rotationCount} strategies), " +
                              $"and Hardware Security Module integration ({hsmCount} strategies). " +
                              $"Auto-discovery: {_config.AutoDiscoverStrategies}, Rotation enabled: {_config.EnableKeyRotation}. " +
                              $"Registered strategies: {string.Join(", ", strategyIds)}",
                    Tags = ["keymanagement", "security", "encryption", "rotation", "hsm", "envelope", "keystore"],
                    Payload = new Dictionary<string, object>
                    {
                        ["strategyCount"] = _strategies.Count,
                        ["envelopeCount"] = envelopeCount,
                        ["rotationCount"] = rotationCount,
                        ["hsmCount"] = hsmCount,
                        ["autoDiscoveryEnabled"] = _config.AutoDiscoverStrategies,
                        ["rotationEnabled"] = _config.EnableKeyRotation,
                        ["strategies"] = strategyIds
                    }
                }
            };
        }

        public override Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
        {
            if (request.Context != null)
            {
            }

            if (request.Config != null && request.Config.TryGetValue("MessageBus", out var messageBusObj) && messageBusObj is IMessageBus messageBus)
            {
                _messageBus = messageBus;
                SetMessageBus(messageBus);
            }

            return base.OnHandshakeAsync(request);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;

            StopAsync().GetAwaiter().GetResult();

            foreach (var strategy in _strategies.Values)
            {
                if (strategy is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }

            _strategies.Clear();
            _keyStores.Clear();
            _envelopeKeyStores.Clear();

            GC.SuppressFinalize(this);
        }
    }
}
