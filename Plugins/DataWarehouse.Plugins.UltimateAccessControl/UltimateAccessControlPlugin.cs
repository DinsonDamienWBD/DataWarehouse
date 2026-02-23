using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateAccessControl
{
    /// <summary>
    /// Ultimate Access Control plugin providing comprehensive security strategies.
    /// Intelligence-aware for UEBA (User and Entity Behavior Analytics) and anomaly detection.
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
    /// - Intelligence-aware behavior analysis
    /// </para>
    /// <para>
    /// <b>MIGRATION GUIDE (from individual security plugins):</b>
    /// </para>
    /// <para>
    /// This Ultimate Access Control plugin consolidates and replaces the following deprecated plugins:
    /// - DataWarehouse.Plugins.AccessControl (basic access control)
    /// - DataWarehouse.Plugins.IAM (identity and access management)
    /// - DataWarehouse.Plugins.Security (miscellaneous security features)
    /// </para>
    /// <para>
    /// <b>Migration Steps:</b>
    /// 1. Update plugin references to use UltimateAccessControl instead of individual security plugins
    /// 2. Review access control strategies - all previous strategies are included as consolidated strategies
    /// 3. Update configuration to use unified policy evaluation modes (FirstMatch, AllMustAllow, AnyMustAllow, Weighted)
    /// 4. Test access control policies with the unified policy engine (EvaluateWithPolicyEngineAsync)
    /// 5. Remove references to deprecated plugins after verification
    /// </para>
    /// <para>
    /// <b>Key Differences:</b>
    /// - Strategy Pattern: All access control modes are now strategies (RBAC, ABAC, ZeroTrust, etc.)
    /// - Unified Policy Engine: Multiple strategies can be evaluated with configurable modes
    /// - Intelligence Integration: Built-in UEBA and anomaly detection via Intelligence plugin
    /// - Advanced Features: Includes DLP, PAM, MFA orchestration, threat intelligence, and SIEM integration
    /// </para>
    /// <para>
    /// <b>Deprecation Timeline:</b>
    /// Individual security plugins will be marked obsolete in Phase 17 and removed in Phase 18.
    /// All new projects should use UltimateAccessControl exclusively.
    /// </para>
    /// </remarks>
    public sealed class UltimateAccessControlPlugin : SecurityPluginBase, IDisposable
    {
        private readonly BoundedDictionary<string, IAccessControlStrategy> _strategies = new BoundedDictionary<string, IAccessControlStrategy>(1000);
        private readonly BoundedDictionary<string, double> _strategyWeights = new BoundedDictionary<string, double>(1000);
        private readonly ConcurrentQueue<PolicyAccessDecision> _auditLog = new();
        private IAccessControlStrategy? _defaultStrategy;
        private PolicyEvaluationMode _evaluationMode = PolicyEvaluationMode.FirstMatch;
        private bool _initialized;
        private bool _disposed;

        /// <inheritdoc/>
        public override string Id => "com.datawarehouse.accesscontrol.ultimate";

        /// <inheritdoc/>
        public override string Name => "Ultimate Access Control";

        /// <inheritdoc/>
        public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override string SecurityDomain => "AccessControl";

        /// <inheritdoc/>
        public override PluginCategory Category => PluginCategory.SecurityProvider;

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
        /// Sets the policy evaluation mode.
        /// </summary>
        public void SetEvaluationMode(PolicyEvaluationMode mode)
        {
            _evaluationMode = mode;
        }

        /// <summary>
        /// Sets the weight for a strategy (used in Weighted evaluation mode).
        /// </summary>
        public void SetStrategyWeight(string strategyId, double weight)
        {
            _strategyWeights[strategyId] = weight;
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

        /// <summary>
        /// Evaluates access using unified security policy engine with multiple strategies.
        /// </summary>
        public async Task<PolicyAccessDecision> EvaluateWithPolicyEngineAsync(
            AccessContext context,
            IEnumerable<string>? strategyIds = null,
            PolicyEvaluationMode? mode = null,
            CancellationToken cancellationToken = default)
        {
            var evaluationMode = mode ?? _evaluationMode;
            var strategies = strategyIds != null
                ? strategyIds.Select(id => GetStrategy(id)).Where(s => s != null).Cast<IAccessControlStrategy>().ToList()
                : _strategies.Values.ToList();

            if (!strategies.Any())
            {
                throw new InvalidOperationException("No strategies available for evaluation");
            }

            var startTime = DateTime.UtcNow;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var strategyDecisions = new List<StrategyDecisionDetail>();

            // Evaluate all strategies
            foreach (var strategy in strategies)
            {
                try
                {
                    var decision = await strategy.EvaluateAccessAsync(context, cancellationToken);
                    strategyDecisions.Add(new StrategyDecisionDetail
                    {
                        StrategyId = strategy.StrategyId,
                        StrategyName = strategy.StrategyName,
                        Decision = decision,
                        Weight = _strategyWeights.TryGetValue(strategy.StrategyId, out var w) ? w : 1.0
                    });
                }
                catch (Exception ex)
                {
                    strategyDecisions.Add(new StrategyDecisionDetail
                    {
                        StrategyId = strategy.StrategyId,
                        StrategyName = strategy.StrategyName,
                        Decision = new AccessDecision
                        {
                            IsGranted = false,
                            Reason = $"Strategy evaluation failed: {ex.Message}"
                        },
                        Weight = 0,
                        Error = ex.Message
                    });
                }
            }

            sw.Stop();

            // Apply policy evaluation mode
            var finalDecision = ApplyPolicyEvaluationMode(strategyDecisions, evaluationMode);
            var policyDecision = new PolicyAccessDecision
            {
                IsGranted = finalDecision.IsGranted,
                Reason = finalDecision.Reason,
                DecisionId = Guid.NewGuid().ToString("N"),
                Timestamp = startTime,
                EvaluationTimeMs = sw.Elapsed.TotalMilliseconds,
                EvaluationMode = evaluationMode,
                StrategyDecisions = strategyDecisions.AsReadOnly(),
                Context = context
            };

            // Audit logging
            LogAccessDecision(policyDecision);

            return policyDecision;
        }

        private (bool IsGranted, string Reason) ApplyPolicyEvaluationMode(
            List<StrategyDecisionDetail> decisions,
            PolicyEvaluationMode mode)
        {
            return mode switch
            {
                PolicyEvaluationMode.AllMustAllow => EvaluateAllMustAllow(decisions),
                PolicyEvaluationMode.AnyMustAllow => EvaluateAnyMustAllow(decisions),
                PolicyEvaluationMode.FirstMatch => EvaluateFirstMatch(decisions),
                PolicyEvaluationMode.Weighted => EvaluateWeighted(decisions),
                _ => (false, "Unknown evaluation mode")
            };
        }

        private (bool IsGranted, string Reason) EvaluateAllMustAllow(List<StrategyDecisionDetail> decisions)
        {
            var deniedStrategies = decisions.Where(d => !d.Decision.IsGranted).ToList();
            if (deniedStrategies.Any())
            {
                var deniedNames = string.Join(", ", deniedStrategies.Select(d => d.StrategyName));
                return (false, $"Access denied by AllMustAllow mode - denied by: {deniedNames}");
            }

            return (true, "Access granted - all strategies allowed");
        }

        private (bool IsGranted, string Reason) EvaluateAnyMustAllow(List<StrategyDecisionDetail> decisions)
        {
            var grantedStrategy = decisions.FirstOrDefault(d => d.Decision.IsGranted);
            if (grantedStrategy != null)
            {
                return (true, $"Access granted by AnyMustAllow mode - allowed by: {grantedStrategy.StrategyName}");
            }

            return (false, "Access denied by AnyMustAllow mode - no strategy allowed");
        }

        private (bool IsGranted, string Reason) EvaluateFirstMatch(List<StrategyDecisionDetail> decisions)
        {
            var firstDecision = decisions.FirstOrDefault();
            if (firstDecision != null)
            {
                return (firstDecision.Decision.IsGranted,
                    $"First match: {firstDecision.StrategyName} - {firstDecision.Decision.Reason}");
            }

            return (false, "No strategy evaluated");
        }

        private (bool IsGranted, string Reason) EvaluateWeighted(List<StrategyDecisionDetail> decisions)
        {
            var totalWeight = decisions.Sum(d => d.Weight);
            var grantedWeight = decisions.Where(d => d.Decision.IsGranted).Sum(d => d.Weight);
            var grantedPercentage = totalWeight > 0 ? (grantedWeight / totalWeight) * 100 : 0;

            // Threshold: require >50% weighted approval
            var isGranted = grantedPercentage > 50;
            return (isGranted,
                $"Weighted decision: {grantedPercentage:F1}% approval ({grantedWeight}/{totalWeight}) - {(isGranted ? "granted" : "denied")}");
        }

        private void LogAccessDecision(PolicyAccessDecision decision)
        {
            _auditLog.Enqueue(decision);

            // Keep only last 1000 decisions in memory (best-effort trim)
            while (_auditLog.Count > 1000)
            {
                _auditLog.TryDequeue(out _);
            }
        }

        /// <summary>
        /// Gets recent access decisions for audit purposes.
        /// </summary>
        public IReadOnlyList<PolicyAccessDecision> GetAuditLog(int maxCount = 100)
        {
            return _auditLog.Reverse().Take(maxCount).Reverse().ToList().AsReadOnly();
        }

        /// <inheritdoc/>
        public override async Task StartAsync(CancellationToken ct)
        {
            // Subscribe to Intelligence topics first (from base class)
            await base.StartAsync(ct);

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

        /// <summary>
        /// Called when Intelligence becomes available - register access control capabilities.
        /// </summary>
        protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct)
        {
            await base.OnStartWithIntelligenceAsync(ct);

            // Register access control capabilities with Intelligence
            if (MessageBus != null)
            {
                var strategyIds = _strategies.Keys.ToList();

                await MessageBus.PublishAsync(IntelligenceTopics.QueryCapability, new PluginMessage
                {
                    Type = "capability.register",
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["pluginId"] = Id,
                        ["pluginName"] = Name,
                        ["pluginType"] = "accesscontrol",
                        ["capabilities"] = new Dictionary<string, object>
                        {
                            ["strategyCount"] = _strategies.Count,
                            ["strategies"] = strategyIds,
                            ["supportsBehaviorAnalysis"] = true,
                            ["supportsAnomalyDetection"] = true,
                            ["supportsRBAC"] = _strategies.ContainsKey("rbac"),
                            ["supportsABAC"] = _strategies.ContainsKey("abac"),
                            ["supportsZeroTrust"] = _strategies.ContainsKey("zerotrust")
                        },
                        ["semanticDescription"] = $"Ultimate Access Control with {_strategies.Count} strategies including RBAC, ABAC, Zero Trust, " +
                            "Canary detection, Steganography, Ephemeral Sharing, and Watermarking.",
                        ["tags"] = new[] { "accesscontrol", "security", "rbac", "abac", "zerotrust", "ueba" }
                    }
                }, ct);

                // Subscribe to behavior analysis requests
                SubscribeToBehaviorAnalysisRequests();
            }
        }

        /// <summary>
        /// Subscribes to behavior analysis requests from Intelligence.
        /// </summary>
        private void SubscribeToBehaviorAnalysisRequests()
        {
            if (MessageBus == null) return;

            MessageBus.Subscribe(IntelligenceTopics.RequestBehaviorAnalysis, async msg =>
            {
                if (msg.Payload.TryGetValue("userId", out var uidObj) && uidObj is string userId)
                {
                    var analysis = AnalyzeUserBehavior(userId, msg.Payload);

                    await MessageBus.PublishAsync(IntelligenceTopics.RequestBehaviorAnalysisResponse, new PluginMessage
                    {
                        Type = "behavior-analysis.response",
                        CorrelationId = msg.CorrelationId,
                        Source = Id,
                        Payload = new Dictionary<string, object>
                        {
                            ["success"] = true,
                            ["userId"] = userId,
                            ["riskScore"] = analysis.RiskScore,
                            ["isAnomaly"] = analysis.IsAnomaly,
                            ["anomalyTypes"] = analysis.AnomalyTypes,
                            ["recommendations"] = analysis.Recommendations
                        }
                    });
                }
            });
        }

        /// <summary>
        /// Analyzes user behavior for anomalies.
        /// </summary>
        private (double RiskScore, bool IsAnomaly, string[] AnomalyTypes, string[] Recommendations)
            AnalyzeUserBehavior(string userId, Dictionary<string, object> context)
        {
            var anomalyTypes = new List<string>();
            var recommendations = new List<string>();
            var riskScore = 0.0;

            // Check for unusual access time
            if (context.TryGetValue("accessHour", out var ahObj) && ahObj is int hour)
            {
                if (hour < 6 || hour > 22) // Outside business hours
                {
                    anomalyTypes.Add("unusual-access-time");
                    riskScore += 0.2;
                    recommendations.Add("Verify access during off-hours is authorized");
                }
            }

            // Check for unusual location
            if (context.TryGetValue("isNewLocation", out var nlObj) && nlObj is true)
            {
                anomalyTypes.Add("new-location");
                riskScore += 0.15;
                recommendations.Add("Confirm user is accessing from authorized location");
            }

            // Check for unusual volume
            if (context.TryGetValue("accessCount24h", out var acObj) && acObj is int accessCount && accessCount > 1000)
            {
                anomalyTypes.Add("high-volume-access");
                riskScore += 0.3;
                recommendations.Add("Investigate high volume of access requests");
            }

            // Check for sensitive resource access
            if (context.TryGetValue("accessingSensitive", out var asObj) && asObj is true)
            {
                riskScore += 0.15;
                recommendations.Add("Log sensitive resource access for audit");
            }

            var isAnomaly = riskScore >= 0.3 || anomalyTypes.Count >= 2;

            return (riskScore, isAnomaly, anomalyTypes.ToArray(), recommendations.ToArray());
        }

        /// <inheritdoc/>
        protected override Task OnStartCoreAsync(CancellationToken ct)
        {
            // Register typed message handler for access evaluation requests (KS3 pattern)
            RegisterHandler<AccessEvaluationRequest, AccessEvaluationResponse>(
                async (request, cancellationToken) =>
                {
                    var context = new AccessContext
                    {
                        ResourceId = request.ResourceId,
                        Action = request.Action,
                        SubjectId = request.SubjectId
                    };

                    var decision = await EvaluateAccessAsync(context, request.StrategyId);
                    return new AccessEvaluationResponse
                    {
                        Allowed = decision.IsGranted,
                        Reason = decision.Reason,
                        StrategyId = request.StrategyId ?? _defaultStrategy?.StrategyId ?? ""
                    };
                });

            return Task.CompletedTask;
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
                SetMessageBus(messageBus);
            }

            return base.OnHandshakeAsync(request);
        }

        /// <summary>
        /// Disposes resources.
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_disposed) return;
                _disposed = true;
                _strategies.Clear();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Typed request DTO for access evaluation via message bus (KS3).
    /// Topic: <c>DataWarehouse.Plugins.UltimateAccessControl.AccessEvaluationRequest</c>
    /// </summary>
    public sealed class AccessEvaluationRequest
    {
        /// <summary>Subject (user or service) requesting access.</summary>
        public string SubjectId { get; init; } = "";
        /// <summary>Resource being accessed.</summary>
        public string ResourceId { get; init; } = "";
        /// <summary>Action being performed.</summary>
        public string Action { get; init; } = "";
        /// <summary>Optional strategy to use for evaluation.</summary>
        public string? StrategyId { get; init; }
    }

    /// <summary>
    /// Typed response DTO for access evaluation via message bus (KS3).
    /// </summary>
    public sealed class AccessEvaluationResponse
    {
        /// <summary>Whether access is granted.</summary>
        public bool Allowed { get; init; }
        /// <summary>Reason for the decision.</summary>
        public string Reason { get; init; } = "";
        /// <summary>Strategy that made the decision.</summary>
        public string StrategyId { get; init; } = "";
    }
}
