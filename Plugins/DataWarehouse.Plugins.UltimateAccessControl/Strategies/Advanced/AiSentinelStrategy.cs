using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Advanced
{
    /// <summary>
    /// AI-powered security sentinel that orchestrates threat detection and automated response.
    /// Delegates ML analysis to Intelligence plugin (T90) via message bus with rule-based fallback.
    /// </summary>
    /// <remarks>
    /// <b>DEPENDENCY:</b> Universal Intelligence plugin (T90) for ML-based threat scoring.
    /// <b>MESSAGE TOPIC:</b> intelligence.analyze
    /// <b>FALLBACK:</b> Configurable threshold-based threat scoring when Intelligence unavailable.
    /// </remarks>
    public sealed class AiSentinelStrategy : AccessControlStrategyBase
    {
        private readonly ILogger _logger;
        private readonly IMessageBus? _messageBus;

        public AiSentinelStrategy(ILogger? logger = null, IMessageBus? messageBus = null)
        {
            _logger = logger ?? NullLogger.Instance;
            _messageBus = messageBus;
        }

        public override string StrategyId => "ai-sentinel";
        public override string StrategyName => "AI Sentinel Strategy";

        public override AccessControlCapabilities Capabilities => new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = false,
            SupportsTemporalAccess = true,
            SupportsGeographicRestrictions = true,
            MaxConcurrentEvaluations = 500
        };

        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            double threatScore = 0.0;
            string analysisSource = "rule-based-fallback";

            // Step 1: Try Intelligence plugin via message bus
            if (_messageBus != null)
            {
                try
                {
                    var message = new PluginMessage
                    {
                        Type = "intelligence.analyze",
                        Payload = new Dictionary<string, object>
                        {
                            ["data_type"] = "security-event",
                            ["subject_id"] = context.SubjectId,
                            ["resource_id"] = context.ResourceId,
                            ["action"] = context.Action,
                            ["client_ip"] = context.ClientIpAddress ?? "",
                            ["timestamp"] = context.RequestTime,
                            ["analysis_type"] = "threat-scoring"
                        }
                    };

                    var response = await _messageBus.SendAsync("intelligence.analyze", message, TimeSpan.FromSeconds(10), cancellationToken);

                    if (response.Success && response.Payload is Dictionary<string, object> payload)
                    {
                        if (payload.TryGetValue("threat_score", out var scoreObj) && scoreObj is double score)
                        {
                            threatScore = score;
                            analysisSource = "ai-intelligence-plugin";
                            _logger.LogInformation("AI Sentinel received threat score {ThreatScore} from Intelligence plugin", threatScore);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Intelligence plugin unavailable for AI Sentinel, using rule-based fallback");
                }
            }

            // Step 2: Fallback to rule-based scoring
            if (analysisSource == "rule-based-fallback")
            {
                threatScore = CalculateRuleBasedThreatScore(context);
                _logger.LogInformation("AI Sentinel using rule-based threat score: {ThreatScore}", threatScore);
            }

            // Determine access
            var threatThreshold = Configuration.TryGetValue("threat_threshold", out var threshold) && threshold is double t ? t : 0.5;
            var isGranted = threatScore < threatThreshold;

            return new AccessDecision
            {
                IsGranted = isGranted,
                Reason = isGranted
                    ? $"AI Sentinel approved access (threat score: {threatScore:F2})"
                    : $"AI Sentinel blocked access (threat score: {threatScore:F2} exceeds threshold {threatThreshold:F2})",
                ApplicablePolicies = new[] { "ai-sentinel-policy" },
                Metadata = new Dictionary<string, object>
                {
                    ["threat_score"] = threatScore,
                    ["threat_threshold"] = threatThreshold,
                    ["analysis_source"] = analysisSource
                }
            };
        }

        private double CalculateRuleBasedThreatScore(AccessContext context)
        {
            var score = 0.0;

            // Rule 1: Off-hours access
            if (context.RequestTime.Hour < 6 || context.RequestTime.Hour > 22)
                score += 0.1;

            // Rule 2: Sensitive resource
            if (context.ResourceId.Contains("admin", StringComparison.OrdinalIgnoreCase))
                score += 0.15;

            // Rule 3: Privilege escalation
            if (context.Action.Contains("elevate", StringComparison.OrdinalIgnoreCase))
                score += 0.3;

            return Math.Min(score, 1.0);
        }
    }
}
