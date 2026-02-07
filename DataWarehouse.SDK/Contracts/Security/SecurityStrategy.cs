using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.AI;

namespace DataWarehouse.SDK.Contracts.Security
{
    /// <summary>
    /// Security domains for policy evaluation and control enforcement.
    /// Each domain represents a distinct security concern with specialized policies.
    /// </summary>
    public enum SecurityDomain
    {
        /// <summary>
        /// Access control policies (authorization, permissions, ACLs).
        /// Determines who can access what resources and under what conditions.
        /// </summary>
        AccessControl = 0,

        /// <summary>
        /// Identity and authentication policies (user verification, MFA).
        /// Validates user identity and authentication strength.
        /// </summary>
        Identity = 1,

        /// <summary>
        /// Threat detection and prevention (anomaly detection, intrusion prevention).
        /// Identifies and blocks suspicious activities and attack patterns.
        /// </summary>
        ThreatDetection = 2,

        /// <summary>
        /// Data integrity and validation (checksums, signatures, tamper detection).
        /// Ensures data has not been modified or corrupted.
        /// </summary>
        Integrity = 3,

        /// <summary>
        /// Audit and compliance logging (activity tracking, evidence collection).
        /// Records security-relevant events for compliance and forensics.
        /// </summary>
        Audit = 4,

        /// <summary>
        /// Privacy and data protection (PII handling, data masking, consent).
        /// Protects personally identifiable information and enforces privacy regulations.
        /// </summary>
        Privacy = 5
    }

    /// <summary>
    /// Security decision outcome with reasoning and evidence.
    /// Immutable record for audit trails and policy debugging.
    /// </summary>
    public sealed record SecurityDecision
    {
        /// <summary>
        /// Gets a value indicating whether the operation is allowed.
        /// True = permitted, False = denied.
        /// </summary>
        public required bool Allowed { get; init; }

        /// <summary>
        /// Gets the security domain that made this decision.
        /// </summary>
        public required SecurityDomain Domain { get; init; }

        /// <summary>
        /// Gets the human-readable reason for the decision.
        /// Should explain why access was granted or denied.
        /// </summary>
        public required string Reason { get; init; }

        /// <summary>
        /// Gets the policy rule or identifier that triggered this decision.
        /// Used for audit trails and policy debugging.
        /// </summary>
        public string? PolicyId { get; init; }

        /// <summary>
        /// Gets supporting evidence for this decision.
        /// Can include user attributes, resource metadata, environmental conditions.
        /// </summary>
        public IReadOnlyDictionary<string, object>? Evidence { get; init; }

        /// <summary>
        /// Gets required remediation actions if access is denied.
        /// Examples: "Enable MFA", "Request elevated privileges", "Contact administrator".
        /// </summary>
        public IReadOnlyList<string>? RequiredActions { get; init; }

        /// <summary>
        /// Gets the confidence score for this decision (0.0 to 1.0).
        /// Lower values indicate uncertainty and may require manual review.
        /// </summary>
        public double Confidence { get; init; } = 1.0;

        /// <summary>
        /// Gets the timestamp when this decision was made.
        /// </summary>
        public DateTime Timestamp { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Gets additional metadata about the decision context.
        /// </summary>
        public IReadOnlyDictionary<string, string>? Metadata { get; init; }

        /// <summary>
        /// Creates an allowed decision with reasoning.
        /// </summary>
        /// <param name="domain">The security domain.</param>
        /// <param name="reason">The reason for allowing access.</param>
        /// <param name="policyId">The policy that allowed access.</param>
        /// <param name="evidence">Supporting evidence.</param>
        /// <returns>An allowed security decision.</returns>
        public static SecurityDecision Allow(
            SecurityDomain domain,
            string reason,
            string? policyId = null,
            IReadOnlyDictionary<string, object>? evidence = null)
        {
            return new SecurityDecision
            {
                Allowed = true,
                Domain = domain,
                Reason = reason,
                PolicyId = policyId,
                Evidence = evidence
            };
        }

        /// <summary>
        /// Creates a denied decision with reasoning and required actions.
        /// </summary>
        /// <param name="domain">The security domain.</param>
        /// <param name="reason">The reason for denying access.</param>
        /// <param name="policyId">The policy that denied access.</param>
        /// <param name="requiredActions">Actions needed to gain access.</param>
        /// <param name="evidence">Supporting evidence.</param>
        /// <returns>A denied security decision.</returns>
        public static SecurityDecision Deny(
            SecurityDomain domain,
            string reason,
            string? policyId = null,
            IReadOnlyList<string>? requiredActions = null,
            IReadOnlyDictionary<string, object>? evidence = null)
        {
            return new SecurityDecision
            {
                Allowed = false,
                Domain = domain,
                Reason = reason,
                PolicyId = policyId,
                RequiredActions = requiredActions,
                Evidence = evidence
            };
        }
    }

    /// <summary>
    /// Context for security policy evaluation.
    /// Contains all information needed to make access control decisions.
    /// </summary>
    public sealed class SecurityContext
    {
        /// <summary>
        /// Gets or sets the user identifier requesting access.
        /// </summary>
        public required string UserId { get; init; }

        /// <summary>
        /// Gets or sets the tenant or organization identifier.
        /// Used for multi-tenant isolation and tenant-specific policies.
        /// </summary>
        public string? TenantId { get; init; }

        /// <summary>
        /// Gets or sets the resource being accessed.
        /// Format: "container/blob" or resource URI.
        /// </summary>
        public required string Resource { get; init; }

        /// <summary>
        /// Gets or sets the operation being performed.
        /// Examples: "read", "write", "delete", "list", "admin".
        /// </summary>
        public required string Operation { get; init; }

        /// <summary>
        /// Gets or sets user roles and group memberships.
        /// Used for role-based access control (RBAC).
        /// </summary>
        public IReadOnlyList<string>? Roles { get; init; }

        /// <summary>
        /// Gets or sets user attributes and claims.
        /// Can include department, clearance level, location, device trust, etc.
        /// </summary>
        public IReadOnlyDictionary<string, object>? UserAttributes { get; init; }

        /// <summary>
        /// Gets or sets resource attributes and metadata.
        /// Can include classification level, owner, retention policy, etc.
        /// </summary>
        public IReadOnlyDictionary<string, object>? ResourceAttributes { get; init; }

        /// <summary>
        /// Gets or sets environmental conditions.
        /// Can include IP address, time of day, device type, location, network trust.
        /// </summary>
        public IReadOnlyDictionary<string, object>? Environment { get; init; }

        /// <summary>
        /// Gets or sets the authentication strength level.
        /// Examples: "none", "password", "mfa", "certificate", "biometric".
        /// </summary>
        public string? AuthenticationStrength { get; init; }

        /// <summary>
        /// Gets or sets the request timestamp.
        /// </summary>
        public DateTime RequestTime { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Gets or sets the session identifier for this request.
        /// Used for correlating related security events.
        /// </summary>
        public string? SessionId { get; init; }

        /// <summary>
        /// Gets or sets additional context metadata.
        /// </summary>
        public IReadOnlyDictionary<string, string>? Metadata { get; init; }

        /// <summary>
        /// Creates a minimal security context for testing.
        /// </summary>
        public static SecurityContext CreateSimple(string userId, string resource, string operation)
        {
            return new SecurityContext
            {
                UserId = userId,
                Resource = resource,
                Operation = operation
            };
        }
    }

    /// <summary>
    /// Common interface for security strategy implementations.
    /// Security strategies evaluate policies and make access control decisions.
    /// </summary>
    public interface ISecurityStrategy
    {
        /// <summary>
        /// Gets the unique identifier for this security strategy.
        /// Example: "abac-policy", "rbac-policy", "zerotrust-policy".
        /// </summary>
        string StrategyId { get; }

        /// <summary>
        /// Gets the human-readable name of this security strategy.
        /// </summary>
        string StrategyName { get; }

        /// <summary>
        /// Gets the security domains handled by this strategy.
        /// </summary>
        IReadOnlyList<SecurityDomain> SupportedDomains { get; }

        /// <summary>
        /// Evaluates security policies for the given context.
        /// </summary>
        /// <param name="context">The security context to evaluate.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A security decision with reasoning and evidence.</returns>
        /// <exception cref="ArgumentNullException">If context is null.</exception>
        /// <exception cref="SecurityException">If evaluation fails.</exception>
        Task<SecurityDecision> EvaluateAsync(
            SecurityContext context,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Evaluates policies for a specific security domain.
        /// </summary>
        /// <param name="context">The security context to evaluate.</param>
        /// <param name="domain">The specific domain to evaluate.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A security decision for the specified domain.</returns>
        /// <exception cref="ArgumentNullException">If context is null.</exception>
        /// <exception cref="ArgumentException">If domain is not supported.</exception>
        /// <exception cref="SecurityException">If evaluation fails.</exception>
        Task<SecurityDecision> EvaluateDomainAsync(
            SecurityContext context,
            SecurityDomain domain,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Validates that a security context has all required fields.
        /// </summary>
        /// <param name="context">The context to validate.</param>
        /// <returns>True if valid, false otherwise.</returns>
        bool ValidateContext(SecurityContext context);

        /// <summary>
        /// Gets statistics about security evaluations performed.
        /// </summary>
        /// <returns>Security evaluation statistics.</returns>
        SecurityStatistics GetStatistics();

        /// <summary>
        /// Resets statistics counters.
        /// </summary>
        void ResetStatistics();
    }

    /// <summary>
    /// Statistics tracking for security policy evaluations.
    /// Used for monitoring, auditing, and anomaly detection.
    /// </summary>
    public sealed class SecurityStatistics
    {
        /// <summary>
        /// Gets or sets the total number of policy evaluations.
        /// </summary>
        public long TotalEvaluations { get; init; }

        /// <summary>
        /// Gets or sets the number of allowed decisions.
        /// </summary>
        public long AllowedCount { get; init; }

        /// <summary>
        /// Gets or sets the number of denied decisions.
        /// </summary>
        public long DeniedCount { get; init; }

        /// <summary>
        /// Gets or sets evaluation counts by security domain.
        /// </summary>
        public IReadOnlyDictionary<SecurityDomain, long>? EvaluationsByDomain { get; init; }

        /// <summary>
        /// Gets or sets denied counts by security domain.
        /// </summary>
        public IReadOnlyDictionary<SecurityDomain, long>? DenialsByDomain { get; init; }

        /// <summary>
        /// Gets or sets the number of evaluation errors.
        /// </summary>
        public long ErrorCount { get; init; }

        /// <summary>
        /// Gets or sets the average evaluation time in milliseconds.
        /// </summary>
        public double AverageEvaluationTimeMs { get; init; }

        /// <summary>
        /// Gets or sets the timestamp when statistics tracking started.
        /// </summary>
        public DateTime StartTime { get; init; }

        /// <summary>
        /// Gets or sets the timestamp of the last statistic update.
        /// </summary>
        public DateTime LastUpdateTime { get; init; }

        /// <summary>
        /// Gets the denial rate (denied / total evaluations).
        /// </summary>
        public double DenialRate => TotalEvaluations > 0 ? (double)DeniedCount / TotalEvaluations : 0;

        /// <summary>
        /// Gets the error rate (errors / total evaluations).
        /// </summary>
        public double ErrorRate => TotalEvaluations > 0 ? (double)ErrorCount / TotalEvaluations : 0;

        /// <summary>
        /// Creates an empty statistics object.
        /// </summary>
        public static SecurityStatistics Empty => new()
        {
            StartTime = DateTime.UtcNow,
            LastUpdateTime = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Exception thrown when security policy evaluation fails.
    /// </summary>
    public sealed class SecurityException : Exception
    {
        /// <summary>
        /// Gets the security domain where the failure occurred.
        /// </summary>
        public SecurityDomain? Domain { get; }

        /// <summary>
        /// Initializes a new instance of the <see cref="SecurityException"/> class.
        /// </summary>
        /// <param name="message">The error message.</param>
        public SecurityException(string message) : base(message) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="SecurityException"/> class.
        /// </summary>
        /// <param name="message">The error message.</param>
        /// <param name="innerException">The inner exception.</param>
        public SecurityException(string message, Exception innerException) : base(message, innerException) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="SecurityException"/> class.
        /// </summary>
        /// <param name="domain">The security domain where the failure occurred.</param>
        /// <param name="message">The error message.</param>
        public SecurityException(SecurityDomain domain, string message) : base(message)
        {
            Domain = domain;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="SecurityException"/> class.
        /// </summary>
        /// <param name="domain">The security domain where the failure occurred.</param>
        /// <param name="message">The error message.</param>
        /// <param name="innerException">The inner exception.</param>
        public SecurityException(SecurityDomain domain, string message, Exception innerException)
            : base(message, innerException)
        {
            Domain = domain;
        }
    }

    /// <summary>
    /// Abstract base class for security strategy implementations.
    /// Provides common functionality for policy evaluation, audit logging, and statistics tracking.
    /// Thread-safe for concurrent operations.
    /// </summary>
    public abstract class SecurityStrategyBase : ISecurityStrategy
    {
        private long _totalEvaluations;
        private long _allowedCount;
        private long _deniedCount;
        private long _errorCount;
        private double _totalEvaluationTimeMs;
        private readonly DateTime _startTime;
        private DateTime _lastUpdateTime;
        private readonly object _statsLock = new();
        private readonly Dictionary<SecurityDomain, long> _evaluationsByDomain = new();
        private readonly Dictionary<SecurityDomain, long> _denialsByDomain = new();

        /// <summary>
        /// Initializes a new instance of the <see cref="SecurityStrategyBase"/> class.
        /// </summary>
        protected SecurityStrategyBase()
        {
            _startTime = DateTime.UtcNow;
            _lastUpdateTime = DateTime.UtcNow;

            // Initialize domain counters
            foreach (SecurityDomain domain in Enum.GetValues(typeof(SecurityDomain)))
            {
                _evaluationsByDomain[domain] = 0;
                _denialsByDomain[domain] = 0;
            }
        }

        #region Intelligence Integration

        /// <summary>
        /// Gets the message bus for Intelligence communication.
        /// </summary>
        protected IMessageBus? MessageBus { get; private set; }

        /// <summary>
        /// Configures Intelligence integration for this security strategy.
        /// </summary>
        /// <param name="messageBus">Optional message bus for Intelligence communication.</param>
        public virtual void ConfigureIntelligence(IMessageBus? messageBus)
        {
            MessageBus = messageBus;
        }

        /// <summary>
        /// Gets a value indicating whether Intelligence integration is available.
        /// </summary>
        protected bool IsIntelligenceAvailable => MessageBus != null;

        /// <summary>
        /// Gets static knowledge about this security strategy for Intelligence registration.
        /// </summary>
        /// <returns>A KnowledgeObject describing this strategy's capabilities.</returns>
        public virtual KnowledgeObject GetStrategyKnowledge()
        {
            return new KnowledgeObject
            {
                Id = $"security.{StrategyId}",
                Topic = "security.strategy",
                SourcePluginId = "sdk.security",
                SourcePluginName = StrategyName,
                KnowledgeType = "capability",
                Description = $"{StrategyName} security strategy for policy evaluation and access control",
                Payload = new Dictionary<string, object>
                {
                    ["strategyId"] = StrategyId,
                    ["supportedDomains"] = SupportedDomains.Select(d => d.ToString()).ToArray()
                },
                Tags = new[] { "security", "policy", "accesscontrol", "strategy" }
            };
        }

        /// <summary>
        /// Gets the registered capability for this security strategy.
        /// </summary>
        /// <returns>A RegisteredCapability describing this strategy.</returns>
        public virtual RegisteredCapability GetStrategyCapability()
        {
            return new RegisteredCapability
            {
                CapabilityId = $"security.{StrategyId}",
                DisplayName = StrategyName,
                Description = $"{StrategyName} security strategy",
                Category = CapabilityCategory.Security,
                SubCategory = "PolicyEvaluation",
                PluginId = "sdk.security",
                PluginName = StrategyName,
                PluginVersion = "1.0.0",
                Tags = new[] { "security", "authentication", "authorization" },
                SemanticDescription = $"Use {StrategyName} for security policy evaluation and access control decisions"
            };
        }

        /// <summary>
        /// Requests security optimization suggestions from Intelligence.
        /// </summary>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>Optimization suggestions if available, null otherwise.</returns>
        protected async Task<object?> RequestSecurityOptimizationAsync(CancellationToken ct = default)
        {
            if (!IsIntelligenceAvailable) return null;

            // Send request to Intelligence for security optimization
            await Task.CompletedTask;
            return null;
        }

        #endregion

        /// <inheritdoc/>
        public abstract string StrategyId { get; }

        /// <inheritdoc/>
        public abstract string StrategyName { get; }

        /// <inheritdoc/>
        public abstract IReadOnlyList<SecurityDomain> SupportedDomains { get; }

        /// <inheritdoc/>
        public async Task<SecurityDecision> EvaluateAsync(
            SecurityContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (!ValidateContext(context))
            {
                IncrementErrorCount();
                throw new ArgumentException("Invalid security context", nameof(context));
            }

            var startTime = DateTime.UtcNow;
            try
            {
                var decision = await EvaluateCoreAsync(context, cancellationToken).ConfigureAwait(false);

                // Update statistics
                var evaluationTime = (DateTime.UtcNow - startTime).TotalMilliseconds;
                UpdateStatistics(decision, evaluationTime);

                // Audit log
                await LogSecurityDecisionAsync(context, decision, cancellationToken).ConfigureAwait(false);

                return decision;
            }
            catch (Exception ex)
            {
                IncrementErrorCount();
                throw new SecurityException("Security evaluation failed", ex);
            }
        }

        /// <inheritdoc/>
        public async Task<SecurityDecision> EvaluateDomainAsync(
            SecurityContext context,
            SecurityDomain domain,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (!SupportedDomains.Contains(domain))
            {
                throw new ArgumentException(
                    $"Domain {domain} is not supported by strategy {StrategyId}",
                    nameof(domain));
            }

            if (!ValidateContext(context))
            {
                IncrementErrorCount();
                throw new ArgumentException("Invalid security context", nameof(context));
            }

            var startTime = DateTime.UtcNow;
            try
            {
                var decision = await EvaluateDomainCoreAsync(context, domain, cancellationToken).ConfigureAwait(false);

                // Update statistics
                var evaluationTime = (DateTime.UtcNow - startTime).TotalMilliseconds;
                UpdateStatistics(decision, evaluationTime);

                // Audit log
                await LogSecurityDecisionAsync(context, decision, cancellationToken).ConfigureAwait(false);

                return decision;
            }
            catch (Exception ex)
            {
                IncrementErrorCount();
                throw new SecurityException(domain, "Domain evaluation failed", ex);
            }
        }

        /// <inheritdoc/>
        public virtual bool ValidateContext(SecurityContext context)
        {
            if (context == null)
                return false;

            if (string.IsNullOrWhiteSpace(context.UserId))
                return false;

            if (string.IsNullOrWhiteSpace(context.Resource))
                return false;

            if (string.IsNullOrWhiteSpace(context.Operation))
                return false;

            return true;
        }

        /// <inheritdoc/>
        public SecurityStatistics GetStatistics()
        {
            lock (_statsLock)
            {
                return new SecurityStatistics
                {
                    TotalEvaluations = Interlocked.Read(ref _totalEvaluations),
                    AllowedCount = Interlocked.Read(ref _allowedCount),
                    DeniedCount = Interlocked.Read(ref _deniedCount),
                    ErrorCount = Interlocked.Read(ref _errorCount),
                    AverageEvaluationTimeMs = _totalEvaluations > 0 ? _totalEvaluationTimeMs / _totalEvaluations : 0,
                    EvaluationsByDomain = new Dictionary<SecurityDomain, long>(_evaluationsByDomain),
                    DenialsByDomain = new Dictionary<SecurityDomain, long>(_denialsByDomain),
                    StartTime = _startTime,
                    LastUpdateTime = _lastUpdateTime
                };
            }
        }

        /// <inheritdoc/>
        public void ResetStatistics()
        {
            lock (_statsLock)
            {
                Interlocked.Exchange(ref _totalEvaluations, 0);
                Interlocked.Exchange(ref _allowedCount, 0);
                Interlocked.Exchange(ref _deniedCount, 0);
                Interlocked.Exchange(ref _errorCount, 0);
                _totalEvaluationTimeMs = 0;
                _lastUpdateTime = DateTime.UtcNow;

                foreach (var domain in _evaluationsByDomain.Keys.ToList())
                {
                    _evaluationsByDomain[domain] = 0;
                }

                foreach (var domain in _denialsByDomain.Keys.ToList())
                {
                    _denialsByDomain[domain] = 0;
                }
            }
        }

        /// <summary>
        /// Core evaluation implementation to be provided by derived classes.
        /// Evaluates all supported security domains.
        /// </summary>
        /// <param name="context">The security context.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A security decision.</returns>
        protected abstract Task<SecurityDecision> EvaluateCoreAsync(
            SecurityContext context,
            CancellationToken cancellationToken);

        /// <summary>
        /// Core domain-specific evaluation implementation.
        /// Default implementation calls EvaluateCoreAsync and validates domain.
        /// Override for optimized single-domain evaluation.
        /// </summary>
        /// <param name="context">The security context.</param>
        /// <param name="domain">The domain to evaluate.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A security decision for the specified domain.</returns>
        protected virtual async Task<SecurityDecision> EvaluateDomainCoreAsync(
            SecurityContext context,
            SecurityDomain domain,
            CancellationToken cancellationToken)
        {
            var decision = await EvaluateCoreAsync(context, cancellationToken).ConfigureAwait(false);

            // Validate that the decision is for the requested domain
            if (decision.Domain != domain)
            {
                return SecurityDecision.Deny(
                    domain,
                    $"Domain mismatch: expected {domain}, got {decision.Domain}",
                    StrategyId);
            }

            return decision;
        }

        /// <summary>
        /// Logs a security decision for audit trails.
        /// Override to implement custom audit logging (database, SIEM, etc.).
        /// </summary>
        /// <param name="context">The security context.</param>
        /// <param name="decision">The security decision made.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        protected virtual Task LogSecurityDecisionAsync(
            SecurityContext context,
            SecurityDecision decision,
            CancellationToken cancellationToken)
        {
            // Default implementation: no-op
            // Derived classes should override to write to audit log
            return Task.CompletedTask;
        }

        /// <summary>
        /// Updates statistics atomically.
        /// </summary>
        private void UpdateStatistics(SecurityDecision decision, double evaluationTimeMs)
        {
            lock (_statsLock)
            {
                Interlocked.Increment(ref _totalEvaluations);
                _totalEvaluationTimeMs += evaluationTimeMs;
                _lastUpdateTime = DateTime.UtcNow;

                if (decision.Allowed)
                {
                    Interlocked.Increment(ref _allowedCount);
                }
                else
                {
                    Interlocked.Increment(ref _deniedCount);
                    _denialsByDomain[decision.Domain]++;
                }

                _evaluationsByDomain[decision.Domain]++;
            }
        }

        /// <summary>
        /// Increments error counter atomically.
        /// </summary>
        private void IncrementErrorCount()
        {
            Interlocked.Increment(ref _errorCount);
        }
    }
}
