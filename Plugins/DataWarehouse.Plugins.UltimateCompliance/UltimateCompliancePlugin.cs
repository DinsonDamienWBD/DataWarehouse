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

namespace DataWarehouse.Plugins.UltimateCompliance
{
    /// <summary>
    /// Ultimate Compliance plugin providing comprehensive compliance checking and enforcement.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Supported compliance frameworks:
    /// - GDPR: EU data protection regulation
    /// - HIPAA: US healthcare data protection
    /// - SOX: US financial reporting requirements
    /// - Data Sovereignty: Geographic restrictions and data residency
    /// </para>
    /// <para>
    /// Features:
    /// - Multi-framework compliance checking
    /// - Violation detection and remediation recommendations
    /// - Audit trail generation
    /// - Policy-based configuration
    /// - Real-time compliance verification
    /// </para>
    /// </remarks>
    public sealed class UltimateCompliancePlugin : FeaturePluginBase, IDisposable
    {
        private readonly ConcurrentDictionary<string, IComplianceStrategy> _strategies = new();
        private IMessageBus? _messageBus;
        private bool _initialized;
        private bool _disposed;

        /// <inheritdoc/>
        public override string Id => "com.datawarehouse.compliance.ultimate";

        /// <inheritdoc/>
        public override string Name => "Ultimate Compliance";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

        /// <inheritdoc/>
        public override PluginCategory Category => PluginCategory.FeatureProvider;

        /// <summary>
        /// Gets all registered compliance strategies.
        /// </summary>
        public IReadOnlyCollection<IComplianceStrategy> GetStrategies() => _strategies.Values.ToList().AsReadOnly();

        /// <summary>
        /// Gets a compliance strategy by ID.
        /// </summary>
        public IComplianceStrategy? GetStrategy(string strategyId)
        {
            return _strategies.TryGetValue(strategyId, out var strategy) ? strategy : null;
        }

        /// <summary>
        /// Registers a compliance strategy.
        /// </summary>
        public void RegisterStrategy(IComplianceStrategy strategy)
        {
            _strategies[strategy.StrategyId] = strategy;
        }

        /// <summary>
        /// Checks compliance using a specific strategy.
        /// </summary>
        public async Task<ComplianceResult> CheckComplianceAsync(
            ComplianceContext context,
            string strategyId,
            CancellationToken cancellationToken = default)
        {
            var strategy = GetStrategy(strategyId)
                ?? throw new ArgumentException($"Compliance strategy '{strategyId}' not found");

            return await strategy.CheckComplianceAsync(context, cancellationToken);
        }

        /// <summary>
        /// Checks compliance against all applicable strategies.
        /// </summary>
        public async Task<ComplianceReport> CheckAllComplianceAsync(
            ComplianceContext context,
            CancellationToken cancellationToken = default)
        {
            var results = new List<ComplianceResult>();

            foreach (var strategy in _strategies.Values)
            {
                try
                {
                    var result = await strategy.CheckComplianceAsync(context, cancellationToken);
                    results.Add(result);
                }
                catch (Exception ex)
                {
                    results.Add(new ComplianceResult
                    {
                        IsCompliant = false,
                        Framework = strategy.Framework,
                        Status = ComplianceStatus.RequiresReview,
                        Violations = new[]
                        {
                            new ComplianceViolation
                            {
                                Code = "CHECK-ERROR",
                                Description = $"Error checking {strategy.Framework} compliance: {ex.Message}",
                                Severity = ViolationSeverity.Medium
                            }
                        }
                    });
                }
            }

            return new ComplianceReport
            {
                Results = results,
                OverallCompliant = results.All(r => r.IsCompliant),
                OverallStatus = DetermineOverallStatus(results),
                TotalViolations = results.Sum(r => r.Violations.Count),
                CheckedAt = DateTime.UtcNow
            };
        }

        /// <inheritdoc/>
        public override async Task StartAsync(CancellationToken ct)
        {
            if (_initialized)
                return;

            await DiscoverAndRegisterStrategiesAsync(ct);
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
            var strategyType = typeof(IComplianceStrategy);
            var assembly = Assembly.GetExecutingAssembly();

            var types = assembly.GetTypes()
                .Where(t => t.IsClass && !t.IsAbstract && strategyType.IsAssignableFrom(t));

            foreach (var type in types)
            {
                if (ct.IsCancellationRequested)
                    break;

                try
                {
                    if (Activator.CreateInstance(type) is IComplianceStrategy strategy)
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

        private ComplianceStatus DetermineOverallStatus(List<ComplianceResult> results)
        {
            if (!results.Any())
                return ComplianceStatus.NotApplicable;

            if (results.All(r => r.Status == ComplianceStatus.Compliant))
                return ComplianceStatus.Compliant;

            if (results.Any(r => r.Status == ComplianceStatus.NonCompliant))
                return ComplianceStatus.NonCompliant;

            if (results.Any(r => r.Status == ComplianceStatus.PartiallyCompliant))
                return ComplianceStatus.PartiallyCompliant;

            return ComplianceStatus.RequiresReview;
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
                        CapabilityId = "compliance.ultimate",
                        PluginId = Id,
                        PluginName = Name,
                        PluginVersion = Version,
                        DisplayName = "Ultimate Compliance",
                        Description = "Comprehensive compliance checking for GDPR, HIPAA, SOX, and data sovereignty requirements",
                        Category = SDK.Contracts.CapabilityCategory.Governance,
                        Tags = ["compliance", "gdpr", "hipaa", "sox", "geofencing", "sovereignty"]
                    }
                };

                foreach (var (strategyId, strategy) in _strategies)
                {
                    capabilities.Add(new RegisteredCapability
                    {
                        CapabilityId = $"compliance.{strategyId}",
                        PluginId = Id,
                        PluginName = Name,
                        PluginVersion = Version,
                        DisplayName = strategy.StrategyName,
                        Description = $"Compliance checking for {strategy.Framework}",
                        Category = SDK.Contracts.CapabilityCategory.Governance,
                        Tags = ["compliance", strategy.Framework.ToLowerInvariant(), strategyId]
                    });
                }

                return capabilities.AsReadOnly();
            }
        }

        /// <inheritdoc/>
        protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
        {
            var strategyIds = _strategies.Keys.ToList();
            var frameworks = _strategies.Values.Select(s => s.Framework).Distinct().ToList();

            return new List<KnowledgeObject>
            {
                new()
                {
                    Id = $"{Id}:overview",
                    Topic = "compliance",
                    SourcePluginId = Id,
                    SourcePluginName = Name,
                    KnowledgeType = "capability",
                    Description = $"Ultimate Compliance Plugin provides {_strategies.Count} compliance strategies covering: {string.Join(", ", frameworks)}. " +
                                  "Includes GDPR (EU data protection), HIPAA (US healthcare), SOX (US financial reporting), " +
                                  "and Geofencing (data sovereignty and localization requirements).",
                    Tags = ["compliance", "gdpr", "hipaa", "sox", "geofencing", "data-sovereignty"],
                    Payload = new Dictionary<string, object>
                    {
                        ["strategyCount"] = _strategies.Count,
                        ["strategies"] = strategyIds,
                        ["frameworks"] = frameworks
                    }
                }
            };
        }

        /// <inheritdoc/>
        protected override Dictionary<string, object> GetMetadata()
        {
            var metadata = base.GetMetadata();
            metadata["FeatureType"] = "Compliance";
            metadata["SupportsAutoDiscovery"] = true;
            metadata["RegisteredStrategies"] = _strategies.Count;
            metadata["Frameworks"] = _strategies.Values.Select(s => s.Framework).Distinct().ToList();
            return metadata;
        }

        /// <inheritdoc/>
        public override async Task OnMessageAsync(PluginMessage message)
        {
            switch (message.Type)
            {
                case "compliance.check":
                    await HandleCheckAsync(message);
                    break;

                case "compliance.check-all":
                    await HandleCheckAllAsync(message);
                    break;

                case "compliance.list-strategies":
                    HandleListStrategies(message);
                    break;
            }

            await base.OnMessageAsync(message);
        }

        private async Task HandleCheckAsync(PluginMessage message)
        {
            if (!message.Payload.TryGetValue("Context", out var contextObj) ||
                contextObj is not ComplianceContext context)
            {
                message.Payload["Error"] = "Missing or invalid Context";
                return;
            }

            if (!message.Payload.TryGetValue("StrategyId", out var sidObj) ||
                sidObj is not string strategyId)
            {
                message.Payload["Error"] = "Missing StrategyId";
                return;
            }

            var result = await CheckComplianceAsync(context, strategyId);
            message.Payload["Result"] = result;
        }

        private async Task HandleCheckAllAsync(PluginMessage message)
        {
            if (!message.Payload.TryGetValue("Context", out var contextObj) ||
                contextObj is not ComplianceContext context)
            {
                message.Payload["Error"] = "Missing or invalid Context";
                return;
            }

            var report = await CheckAllComplianceAsync(context);
            message.Payload["Report"] = report;
        }

        private void HandleListStrategies(PluginMessage message)
        {
            var strategies = _strategies.Values.Select(s => new Dictionary<string, object>
            {
                ["Id"] = s.StrategyId,
                ["Name"] = s.StrategyName,
                ["Framework"] = s.Framework
            }).ToList();

            message.Payload["Strategies"] = strategies;
            message.Payload["Count"] = strategies.Count;
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

    /// <summary>
    /// Report containing results from multiple compliance checks.
    /// </summary>
    public record ComplianceReport
    {
        /// <summary>
        /// Results from each compliance strategy.
        /// </summary>
        public IReadOnlyList<ComplianceResult> Results { get; init; } = Array.Empty<ComplianceResult>();

        /// <summary>
        /// Whether all checks passed.
        /// </summary>
        public bool OverallCompliant { get; init; }

        /// <summary>
        /// Overall compliance status.
        /// </summary>
        public ComplianceStatus OverallStatus { get; init; }

        /// <summary>
        /// Total number of violations across all checks.
        /// </summary>
        public int TotalViolations { get; init; }

        /// <summary>
        /// Timestamp when checks were performed.
        /// </summary>
        public DateTime CheckedAt { get; init; }
    }
}
