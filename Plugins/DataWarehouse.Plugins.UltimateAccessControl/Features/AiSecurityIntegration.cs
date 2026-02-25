using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DataWarehouse.Plugins.UltimateAccessControl.Features
{
    /// <summary>
    /// AI-powered security decisions using Intelligence plugin with rule-based fallback.
    /// Delegates AI decision-making to Intelligence plugin via message bus.
    /// </summary>
    /// <remarks>
    /// <b>DEPENDENCY:</b> Universal Intelligence plugin (T90) for AI-powered security decisions.
    /// <b>MESSAGE TOPIC:</b> intelligence.decide
    /// <b>FALLBACK:</b> Configurable rule engine for security decisions when Intelligence unavailable.
    /// </remarks>
    public sealed class AiSecurityIntegration
    {
        private readonly ILogger _logger;
        private readonly IMessageBus? _messageBus;
        private readonly RuleEngine _ruleEngine;

        /// <summary>
        /// Initializes a new instance of the <see cref="AiSecurityIntegration"/> class.
        /// </summary>
        /// <param name="messageBus">Message bus for Intelligence plugin communication (optional).</param>
        /// <param name="logger">Logger for diagnostic output (optional).</param>
        public AiSecurityIntegration(IMessageBus? messageBus = null, ILogger? logger = null)
        {
            _messageBus = messageBus;
            _logger = logger ?? NullLogger.Instance;
            _ruleEngine = new RuleEngine();
        }

        /// <summary>
        /// Makes AI-powered security decision using Intelligence plugin or rule-based fallback.
        /// </summary>
        /// <param name="context">Security context with relevant factors.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Security decision with recommendation and confidence.</returns>
        public async Task<SecurityDecision> MakeDecisionAsync(
            SecurityDecisionContext context,
            CancellationToken cancellationToken = default)
        {
            if (context == null)
                throw new ArgumentNullException(nameof(context));

            SecurityDecision decision;

            // Try AI-based decision via Intelligence plugin
            if (_messageBus != null)
            {
                try
                {
                    decision = await TryAiDecisionAsync(context, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "AI decision failed for context {ContextId}, using rule-based fallback", context.ContextId);
                    decision = await _ruleEngine.EvaluateAsync(context, cancellationToken);
                    decision.DecisionMethod = "Rule-Based-Fallback";
                }
            }
            else
            {
                // No message bus - use rule-based engine
                decision = await _ruleEngine.EvaluateAsync(context, cancellationToken);
                decision.DecisionMethod = "Rule-Based";
            }

            return decision;
        }

        /// <summary>
        /// Attempts AI-based security decision via Intelligence plugin.
        /// </summary>
        private async Task<SecurityDecision> TryAiDecisionAsync(
            SecurityDecisionContext context,
            CancellationToken cancellationToken)
        {
            if (_messageBus == null)
                throw new InvalidOperationException("Message bus is not available");

            // Build request for Intelligence plugin
            var request = new PluginMessage
            {
                Type = "decision.request",
                Source = "UltimateAccessControl",
                Payload = new Dictionary<string, object>
                {
                    ["DataType"] = "security-context",
                    ["DecisionType"] = "access-risk",
                    ["ContextId"] = context.ContextId,
                    ["UserId"] = context.UserId,
                    ["ResourceId"] = context.ResourceId,
                    ["Action"] = context.Action,
                    ["RiskFactors"] = new Dictionary<string, object>
                    {
                        ["AnomalyScore"] = context.AnomalyScore,
                        ["ThreatScore"] = context.ThreatScore,
                        ["ComplianceRisk"] = context.ComplianceRisk,
                        ["BehaviorDeviation"] = context.BehaviorDeviation,
                        ["DataSensitivity"] = context.DataSensitivity
                    },
                    ["ContextualData"] = new Dictionary<string, object>
                    {
                        ["UserRole"] = context.UserRole ?? "unknown",
                        ["Location"] = context.Location ?? "unknown",
                        ["DeviceTrust"] = context.DeviceTrustLevel,
                        ["TimeOfDay"] = context.Timestamp.Hour,
                        ["DayOfWeek"] = context.Timestamp.DayOfWeek.ToString()
                    }
                }
            };

            // Send request to Intelligence plugin
            var response = await _messageBus.SendAsync(
                "intelligence.decide",
                request,
                TimeSpan.FromSeconds(30),
                cancellationToken);

            if (!response.Success)
            {
                _logger.LogWarning("Intelligence plugin decision failed: {Error}", response.ErrorMessage);
                // Fall back to rule engine
                return await _ruleEngine.EvaluateAsync(context, cancellationToken);
            }

            // Parse response
            var payload = response.Payload as Dictionary<string, object>;
            if (payload == null)
            {
                _logger.LogWarning("Intelligence plugin returned invalid payload format");
                return await _ruleEngine.EvaluateAsync(context, cancellationToken);
            }

            var recommendation = payload.TryGetValue("Recommendation", out var recObj) && recObj is string r
                ? Enum.Parse<SecurityRecommendation>(r, true)
                : SecurityRecommendation.Review;

            var confidence = payload.TryGetValue("Confidence", out var confObj) && confObj is double c ? c : 0.0;
            var reasoning = payload.TryGetValue("Reasoning", out var reasonObj) && reasonObj is string reason ? reason : "AI analysis";
            var riskScore = payload.TryGetValue("RiskScore", out var riskObj) && riskObj is double risk ? risk : 0.0;

            return new SecurityDecision
            {
                ContextId = context.ContextId,
                Recommendation = recommendation,
                Confidence = confidence,
                RiskScore = riskScore,
                Reasoning = reasoning,
                DecisionMethod = "AI-Intelligence",
                DecidedAt = DateTime.UtcNow,
                RequiresMfa = recommendation == SecurityRecommendation.AllowWithMfa || riskScore >= 60,
                RequiresApproval = recommendation == SecurityRecommendation.Review || riskScore >= 80
            };
        }

        /// <summary>
        /// Registers custom security rule in rule engine.
        /// </summary>
        public void RegisterRule(SecurityRule rule)
        {
            _ruleEngine.RegisterRule(rule);
        }
    }

    #region Rule Engine

    /// <summary>
    /// Rule-based security decision engine (fallback when AI unavailable).
    /// </summary>
    public sealed class RuleEngine
    {
        private readonly List<SecurityRule> _rules = new();

        public void RegisterRule(SecurityRule rule)
        {
            _rules.Add(rule);
        }

        public Task<SecurityDecision> EvaluateAsync(SecurityDecisionContext context, CancellationToken cancellationToken)
        {
            // Default rules based on risk scores
            var riskScore = CalculateRiskScore(context);
            var recommendation = DetermineRecommendation(riskScore);

            // Apply custom rules
            foreach (var rule in _rules)
            {
                if (rule.Condition(context))
                {
                    recommendation = rule.Recommendation;
                    break;
                }
            }

            return Task.FromResult(new SecurityDecision
            {
                ContextId = context.ContextId,
                Recommendation = recommendation,
                Confidence = 0.7, // Rule-based has moderate confidence
                RiskScore = riskScore,
                Reasoning = GetReasoning(riskScore, recommendation),
                DecisionMethod = "Rule-Based",
                DecidedAt = DateTime.UtcNow,
                RequiresMfa = riskScore >= 60,
                RequiresApproval = riskScore >= 80
            });
        }

        private double CalculateRiskScore(SecurityDecisionContext context)
        {
            var score = 0.0;

            score += context.AnomalyScore * 0.3;
            score += context.ThreatScore * 0.3;
            score += context.ComplianceRisk * 0.2;
            score += context.BehaviorDeviation * 0.1;
            score += context.DataSensitivity * 0.1;

            return Math.Min(score, 100.0);
        }

        private SecurityRecommendation DetermineRecommendation(double riskScore)
        {
            return riskScore switch
            {
                >= 90 => SecurityRecommendation.Deny,
                >= 80 => SecurityRecommendation.Review,
                >= 60 => SecurityRecommendation.AllowWithMfa,
                >= 40 => SecurityRecommendation.AllowWithMonitoring,
                _ => SecurityRecommendation.Allow
            };
        }

        private string GetReasoning(double riskScore, SecurityRecommendation recommendation)
        {
            return recommendation switch
            {
                SecurityRecommendation.Deny => $"Critical risk (score: {riskScore:F1}) - access denied",
                SecurityRecommendation.Review => $"High risk (score: {riskScore:F1}) - manual review required",
                SecurityRecommendation.AllowWithMfa => $"Elevated risk (score: {riskScore:F1}) - MFA required",
                SecurityRecommendation.AllowWithMonitoring => $"Moderate risk (score: {riskScore:F1}) - allow with monitoring",
                _ => $"Low risk (score: {riskScore:F1}) - access allowed"
            };
        }
    }

    #endregion

    #region Supporting Types

    /// <summary>
    /// Security decision context.
    /// </summary>
    public sealed class SecurityDecisionContext
    {
        public required string ContextId { get; init; }
        public required string UserId { get; init; }
        public required string ResourceId { get; init; }
        public required string Action { get; init; }
        public required DateTime Timestamp { get; init; }
        public required double AnomalyScore { get; init; }
        public required double ThreatScore { get; init; }
        public required double ComplianceRisk { get; init; }
        public required double BehaviorDeviation { get; init; }
        public required double DataSensitivity { get; init; }
        public string? UserRole { get; init; }
        public string? Location { get; init; }
        public required double DeviceTrustLevel { get; init; }
    }

    /// <summary>
    /// Security decision result.
    /// </summary>
    public sealed class SecurityDecision
    {
        public required string ContextId { get; init; }
        public required SecurityRecommendation Recommendation { get; init; }
        public required double Confidence { get; init; }
        public required double RiskScore { get; init; }
        public required string Reasoning { get; init; }
        public required string DecisionMethod { get; set; }
        public required DateTime DecidedAt { get; init; }
        public required bool RequiresMfa { get; init; }
        public required bool RequiresApproval { get; init; }
    }

    /// <summary>
    /// Security recommendation.
    /// </summary>
    public enum SecurityRecommendation
    {
        Allow,
        AllowWithMonitoring,
        AllowWithMfa,
        Review,
        Deny
    }

    /// <summary>
    /// Custom security rule.
    /// </summary>
    public sealed class SecurityRule
    {
        public required string RuleId { get; init; }
        public required string Name { get; init; }
        public required Func<SecurityDecisionContext, bool> Condition { get; init; }
        public required SecurityRecommendation Recommendation { get; init; }
    }

    #endregion
}
