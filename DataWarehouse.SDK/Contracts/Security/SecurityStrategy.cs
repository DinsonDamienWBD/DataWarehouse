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
        Privacy = 5,

        /// <summary>
        /// Data protection policies (encryption at rest, in transit, key management).
        /// Ensures data confidentiality through cryptographic controls.
        /// </summary>
        DataProtection = 6,

        /// <summary>
        /// Network security policies (firewall rules, segmentation, TLS enforcement).
        /// Controls network-level access and communication security.
        /// </summary>
        Network = 7,

        /// <summary>
        /// Regulatory compliance policies (GDPR, HIPAA, SOC2, FedRAMP).
        /// Evaluates operations against regulatory framework requirements.
        /// </summary>
        Compliance = 8,

        /// <summary>
        /// Integrity verification policies (hash verification, digital signatures, chain of custody).
        /// Provides cryptographic proof that data has not been tampered with.
        /// </summary>
        IntegrityVerification = 9,

        /// <summary>
        /// Zero trust security policies (never trust, always verify, least privilege).
        /// Enforces continuous verification regardless of network location or prior authentication.
        /// </summary>
        ZeroTrust = 10
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
    public abstract class SecurityStrategyBase : StrategyBase, ISecurityStrategy
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

        /// <inheritdoc/>
        public override abstract string StrategyId { get; }

        /// <inheritdoc/>
        public abstract string StrategyName { get; }

        /// <summary>
        /// Gets the human-readable name. Delegates to StrategyName for backward compatibility.
        /// </summary>
        public override string Name => StrategyName;

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
            catch (OperationCanceledException)
            {
                // Preserve standard cancellation semantics — do not wrap in SecurityException
                throw;
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
                    TotalEvaluations = _totalEvaluations,
                    AllowedCount = _allowedCount,
                    DeniedCount = _deniedCount,
                    ErrorCount = _errorCount,
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
                _totalEvaluations = 0;
                _allowedCount = 0;
                _deniedCount = 0;
                _errorCount = 0;
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

            // If the core evaluator returned a decision for the requested domain, use it directly.
            // Otherwise, the core evaluator may not support per-domain evaluation — return
            // the decision as-is with its domain adjusted, since the core already evaluated the context.
            if (decision.Domain != domain)
            {
                // Preserve the core evaluator's allow/deny verdict but fix the domain.
                // This avoids spurious denials when EvaluateCoreAsync only returns its default domain.
                return decision.Allowed
                    ? SecurityDecision.Allow(domain, decision.Reason, decision.PolicyId)
                    : SecurityDecision.Deny(domain, decision.Reason, decision.PolicyId);
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
                _totalEvaluations++;
                _totalEvaluationTimeMs += evaluationTimeMs;
                _lastUpdateTime = DateTime.UtcNow;

                if (decision.Allowed)
                {
                    _allowedCount++;
                }
                else
                {
                    _deniedCount++;
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
            lock (_statsLock)
            {
                _errorCount++;
            }
        }
    }

    #region T95.A5 — ZeroTrust Policy Framework

    /// <summary>
    /// Zero trust security principles that guide policy evaluation.
    /// Based on NIST SP 800-207 Zero Trust Architecture.
    /// </summary>
    public enum ZeroTrustPrinciple
    {
        /// <summary>
        /// Never trust any request implicitly; always verify identity, device, and context.
        /// </summary>
        NeverTrustAlwaysVerify = 0,

        /// <summary>
        /// Grant only the minimum permissions needed for the requested operation.
        /// </summary>
        LeastPrivilege = 1,

        /// <summary>
        /// Assume breach has already occurred; limit blast radius through segmentation.
        /// </summary>
        AssumeBreachLimitBlastRadius = 2,

        /// <summary>
        /// Isolate workloads and resources into fine-grained security zones.
        /// </summary>
        MicroSegmentation = 3,

        /// <summary>
        /// Continuously validate trust throughout the session, not just at login.
        /// </summary>
        ContinuousValidation = 4,

        /// <summary>
        /// Base access decisions on multiple signals (identity, device, location, behavior).
        /// </summary>
        ContextAwareAccess = 5,

        /// <summary>
        /// Encrypt all data in transit and at rest regardless of network location.
        /// </summary>
        EncryptEverything = 6
    }

    /// <summary>
    /// A zero trust policy rule that defines conditions and actions for access evaluation.
    /// Policies are composable and evaluated in priority order.
    /// </summary>
    public sealed record ZeroTrustRule
    {
        /// <summary>
        /// Gets the unique identifier for this rule.
        /// </summary>
        public required string RuleId { get; init; }

        /// <summary>
        /// Gets the human-readable name of this rule.
        /// </summary>
        public required string Name { get; init; }

        /// <summary>
        /// Gets the zero trust principle this rule enforces.
        /// </summary>
        public required ZeroTrustPrinciple Principle { get; init; }

        /// <summary>
        /// Gets the priority of this rule (lower = higher priority, evaluated first).
        /// </summary>
        public int Priority { get; init; }

        /// <summary>
        /// Gets the conditions that must be met for this rule to apply.
        /// Keys are condition names; values are expected values or patterns.
        /// </summary>
        public IReadOnlyDictionary<string, string>? Conditions { get; init; }

        /// <summary>
        /// Gets a value indicating whether this rule is enabled.
        /// </summary>
        public bool Enabled { get; init; } = true;

        /// <summary>
        /// Gets the description of what this rule enforces.
        /// </summary>
        public string? Description { get; init; }
    }

    /// <summary>
    /// A zero trust policy composed of ordered rules evaluated against security contexts.
    /// Policies support composability, versioning, and audit logging.
    /// </summary>
    public sealed record ZeroTrustPolicy
    {
        /// <summary>
        /// Gets the unique identifier for this policy.
        /// </summary>
        public required string PolicyId { get; init; }

        /// <summary>
        /// Gets the human-readable name of this policy.
        /// </summary>
        public required string Name { get; init; }

        /// <summary>
        /// Gets the version of this policy for change tracking.
        /// </summary>
        public int Version { get; init; } = 1;

        /// <summary>
        /// Gets the ordered list of rules in this policy (evaluated by priority).
        /// </summary>
        public required IReadOnlyList<ZeroTrustRule> Rules { get; init; }

        /// <summary>
        /// Gets the default action when no rule matches.
        /// True = allow, False = deny (secure default: deny).
        /// </summary>
        public bool DefaultAllow { get; init; } = false;

        /// <summary>
        /// Gets the zero trust principles enforced by this policy.
        /// </summary>
        public IReadOnlyList<ZeroTrustPrinciple>? EnforcedPrinciples { get; init; }

        /// <summary>
        /// Gets the minimum authentication strength required by this policy.
        /// Examples: "mfa", "certificate", "biometric".
        /// </summary>
        public string? MinimumAuthenticationStrength { get; init; }

        /// <summary>
        /// Gets the maximum session duration before re-authentication is required.
        /// Supports continuous validation principle.
        /// </summary>
        public TimeSpan? MaxSessionDuration { get; init; }

        /// <summary>
        /// Gets a value indicating whether device trust assessment is required.
        /// </summary>
        public bool RequireDeviceTrust { get; init; }

        /// <summary>
        /// Gets the timestamp when this policy was created.
        /// </summary>
        public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Gets the timestamp when this policy was last modified.
        /// </summary>
        public DateTime? LastModifiedAt { get; init; }

        /// <summary>
        /// Gets additional metadata about this policy.
        /// </summary>
        public IReadOnlyDictionary<string, string>? Metadata { get; init; }
    }

    /// <summary>
    /// Result of evaluating a zero trust policy against a security context.
    /// Includes detailed per-principle and per-rule evaluation results for auditing.
    /// </summary>
    public sealed record ZeroTrustEvaluation
    {
        /// <summary>
        /// Gets a value indicating whether the request passed zero trust evaluation.
        /// </summary>
        public required bool Passed { get; init; }

        /// <summary>
        /// Gets the policy that was evaluated.
        /// </summary>
        public required string PolicyId { get; init; }

        /// <summary>
        /// Gets the overall trust score (0.0 = no trust, 1.0 = full trust).
        /// </summary>
        public required double TrustScore { get; init; }

        /// <summary>
        /// Gets per-principle evaluation results.
        /// </summary>
        public IReadOnlyDictionary<ZeroTrustPrinciple, PrincipleEvaluation>? PrincipleResults { get; init; }

        /// <summary>
        /// Gets the list of rule IDs that were triggered (matched conditions).
        /// </summary>
        public IReadOnlyList<string>? TriggeredRules { get; init; }

        /// <summary>
        /// Gets the list of rule IDs that were violated.
        /// </summary>
        public IReadOnlyList<string>? ViolatedRules { get; init; }

        /// <summary>
        /// Gets required remediation actions to improve trust score.
        /// </summary>
        public IReadOnlyList<string>? RemediationActions { get; init; }

        /// <summary>
        /// Gets the timestamp when this evaluation was performed.
        /// </summary>
        public DateTime EvaluatedAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Gets the duration of the evaluation.
        /// </summary>
        public TimeSpan EvaluationDuration { get; init; }
    }

    /// <summary>
    /// Evaluation result for a single zero trust principle.
    /// </summary>
    public sealed record PrincipleEvaluation
    {
        /// <summary>
        /// Gets the principle that was evaluated.
        /// </summary>
        public required ZeroTrustPrinciple Principle { get; init; }

        /// <summary>
        /// Gets a value indicating whether this principle was satisfied.
        /// </summary>
        public required bool Satisfied { get; init; }

        /// <summary>
        /// Gets the trust score contribution from this principle (0.0 to 1.0).
        /// </summary>
        public double Score { get; init; }

        /// <summary>
        /// Gets the reason for the evaluation result.
        /// </summary>
        public string? Reason { get; init; }

        /// <summary>
        /// Gets evidence supporting this evaluation.
        /// </summary>
        public IReadOnlyDictionary<string, object>? Evidence { get; init; }
    }

    #endregion

    #region T95.A6 — Threat Detection Abstractions

    /// <summary>
    /// Types of security threats that can be detected by strategy-level detectors.
    /// </summary>
    public enum SecurityThreatType
    {
        /// <summary>Unknown or unclassified threat.</summary>
        Unknown = 0,

        /// <summary>Unauthorized access attempt.</summary>
        UnauthorizedAccess = 1,

        /// <summary>Brute force password or token attack.</summary>
        BruteForce = 2,

        /// <summary>Credential stuffing or stolen credential usage.</summary>
        CredentialAbuse = 3,

        /// <summary>Privilege escalation attempt.</summary>
        PrivilegeEscalation = 4,

        /// <summary>Data exfiltration or unusual download patterns.</summary>
        DataExfiltration = 5,

        /// <summary>Insider threat or anomalous employee behavior.</summary>
        InsiderThreat = 6,

        /// <summary>Injection attack (SQL, command, LDAP, etc.).</summary>
        InjectionAttack = 7,

        /// <summary>Denial of service or resource exhaustion.</summary>
        DenialOfService = 8,

        /// <summary>Malware or ransomware activity.</summary>
        Malware = 9,

        /// <summary>Man-in-the-middle or session hijacking.</summary>
        SessionHijacking = 10,

        /// <summary>Replay attack using captured tokens or requests.</summary>
        ReplayAttack = 11,

        /// <summary>Anomalous behavior that does not match known patterns.</summary>
        AnomalousBehavior = 12,

        /// <summary>Policy violation detected.</summary>
        PolicyViolation = 13,

        /// <summary>Data tampering or integrity violation.</summary>
        DataTampering = 14,

        /// <summary>Cryptographic attack (key compromise, cipher downgrade).</summary>
        CryptographicAttack = 15
    }

    /// <summary>
    /// Severity levels for security threats detected at the strategy level.
    /// Aligned with CVSS severity ratings.
    /// </summary>
    public enum SecurityThreatSeverity
    {
        /// <summary>Informational — no immediate risk.</summary>
        Info = 0,

        /// <summary>Low severity — minimal risk, routine monitoring.</summary>
        Low = 1,

        /// <summary>Medium severity — moderate risk, investigate within normal workflow.</summary>
        Medium = 2,

        /// <summary>High severity — significant risk, investigate promptly.</summary>
        High = 3,

        /// <summary>Critical severity — immediate risk, requires urgent response.</summary>
        Critical = 4
    }

    /// <summary>
    /// A threat indicator detected during security evaluation.
    /// Immutable record for inclusion in threat reports and audit trails.
    /// </summary>
    public sealed record ThreatIndicator
    {
        /// <summary>
        /// Gets the type of threat detected.
        /// </summary>
        public required SecurityThreatType Type { get; init; }

        /// <summary>
        /// Gets the severity of the detected threat.
        /// </summary>
        public required SecurityThreatSeverity Severity { get; init; }

        /// <summary>
        /// Gets the confidence score for this detection (0.0 to 1.0).
        /// Higher values indicate greater certainty.
        /// </summary>
        public required double Confidence { get; init; }

        /// <summary>
        /// Gets a human-readable description of the threat indicator.
        /// </summary>
        public required string Description { get; init; }

        /// <summary>
        /// Gets the source of this indicator (e.g., "behavioral-analysis", "signature-match", "anomaly-detection").
        /// </summary>
        public string? Source { get; init; }

        /// <summary>
        /// Gets the affected resource identifier.
        /// </summary>
        public string? AffectedResource { get; init; }

        /// <summary>
        /// Gets the user or principal associated with the threat.
        /// </summary>
        public string? AssociatedPrincipal { get; init; }

        /// <summary>
        /// Gets the timestamp when this indicator was detected.
        /// </summary>
        public DateTime DetectedAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Gets additional evidence supporting this indicator.
        /// </summary>
        public IReadOnlyDictionary<string, object>? Evidence { get; init; }

        /// <summary>
        /// Gets recommended actions to mitigate this threat.
        /// </summary>
        public IReadOnlyList<string>? RecommendedActions { get; init; }
    }

    /// <summary>
    /// Result of a threat detection evaluation.
    /// Contains all detected indicators and an aggregate risk assessment.
    /// </summary>
    public sealed record ThreatDetectionResult
    {
        /// <summary>
        /// Gets a value indicating whether any threats were detected.
        /// </summary>
        public required bool ThreatsDetected { get; init; }

        /// <summary>
        /// Gets the highest severity among all detected threats.
        /// </summary>
        public SecurityThreatSeverity HighestSeverity { get; init; } = SecurityThreatSeverity.Info;

        /// <summary>
        /// Gets the overall risk score (0.0 = no risk, 1.0 = maximum risk).
        /// Calculated from individual indicator severities and confidences.
        /// </summary>
        public double RiskScore { get; init; }

        /// <summary>
        /// Gets the list of detected threat indicators.
        /// </summary>
        public IReadOnlyList<ThreatIndicator> Indicators { get; init; } = Array.Empty<ThreatIndicator>();

        /// <summary>
        /// Gets the timestamp when detection was performed.
        /// </summary>
        public DateTime DetectedAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Gets the duration of the detection analysis.
        /// </summary>
        public TimeSpan AnalysisDuration { get; init; }

        /// <summary>
        /// Creates a result indicating no threats were detected.
        /// </summary>
        /// <returns>A clean threat detection result.</returns>
        public static ThreatDetectionResult Clean => new()
        {
            ThreatsDetected = false,
            HighestSeverity = SecurityThreatSeverity.Info,
            RiskScore = 0.0
        };
    }

    /// <summary>
    /// Interface for strategy-level threat detection within security evaluation.
    /// Implementations analyze security contexts for indicators of compromise or attack.
    /// </summary>
    public interface IThreatDetector
    {
        /// <summary>
        /// Gets the unique identifier for this threat detector.
        /// </summary>
        string DetectorId { get; }

        /// <summary>
        /// Gets the display name of this threat detector.
        /// </summary>
        string DetectorName { get; }

        /// <summary>
        /// Gets the threat types this detector can identify.
        /// </summary>
        IReadOnlyList<SecurityThreatType> DetectableThreats { get; }

        /// <summary>
        /// Detects threats in the given security context.
        /// </summary>
        /// <param name="context">The security context to analyze.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A result containing any detected threat indicators.</returns>
        /// <exception cref="ArgumentNullException">If context is null.</exception>
        Task<ThreatDetectionResult> DetectAsync(
            SecurityContext context,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Reports a confirmed threat for learning and pattern updates.
        /// </summary>
        /// <param name="indicator">The confirmed threat indicator.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task ReportConfirmedThreatAsync(
            ThreatIndicator indicator,
            CancellationToken cancellationToken = default);
    }

    #endregion

    #region T95.A7 — Integrity Verification Framework

    /// <summary>
    /// Types of integrity violations that can be detected.
    /// </summary>
    public enum IntegrityViolationType
    {
        /// <summary>Unknown violation type.</summary>
        Unknown = 0,

        /// <summary>Hash mismatch — computed hash differs from expected hash.</summary>
        HashMismatch = 1,

        /// <summary>Digital signature is invalid or cannot be verified.</summary>
        SignatureInvalid = 2,

        /// <summary>Digital signature has expired.</summary>
        SignatureExpired = 3,

        /// <summary>Data was modified after being sealed or committed.</summary>
        TamperDetected = 4,

        /// <summary>Chain of custody record is incomplete or inconsistent.</summary>
        ChainOfCustodyBroken = 5,

        /// <summary>Metadata does not match data contents.</summary>
        MetadataMismatch = 6,

        /// <summary>Version sequence is inconsistent or has gaps.</summary>
        VersionSequenceViolation = 7,

        /// <summary>Timestamp ordering is violated.</summary>
        TimestampViolation = 8,

        /// <summary>Size or checksum of stored data has changed unexpectedly.</summary>
        StorageIntegrityViolation = 9
    }

    /// <summary>
    /// A detected integrity violation with details about what failed and where.
    /// Immutable record for inclusion in verification reports and audit trails.
    /// </summary>
    public sealed record IntegrityViolation
    {
        /// <summary>
        /// Gets the type of violation detected.
        /// </summary>
        public required IntegrityViolationType Type { get; init; }

        /// <summary>
        /// Gets a human-readable description of the violation.
        /// </summary>
        public required string Description { get; init; }

        /// <summary>
        /// Gets the resource or data element affected by this violation.
        /// </summary>
        public required string AffectedResource { get; init; }

        /// <summary>
        /// Gets the expected value (hash, signature, version, etc.).
        /// </summary>
        public string? ExpectedValue { get; init; }

        /// <summary>
        /// Gets the actual value found during verification.
        /// </summary>
        public string? ActualValue { get; init; }

        /// <summary>
        /// Gets the severity of this violation.
        /// </summary>
        public SecurityThreatSeverity Severity { get; init; } = SecurityThreatSeverity.High;

        /// <summary>
        /// Gets the timestamp when this violation was detected.
        /// </summary>
        public DateTime DetectedAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Gets additional evidence supporting this violation finding.
        /// </summary>
        public IReadOnlyDictionary<string, object>? Evidence { get; init; }
    }

    /// <summary>
    /// Result of an integrity verification operation.
    /// Contains the verification outcome, any violations, and cryptographic proof.
    /// </summary>
    public sealed record IntegrityVerificationResult
    {
        /// <summary>
        /// Gets a value indicating whether the data passed integrity verification.
        /// </summary>
        public required bool IsValid { get; init; }

        /// <summary>
        /// Gets the computed hash of the verified data.
        /// </summary>
        public string? ComputedHash { get; init; }

        /// <summary>
        /// Gets the hash algorithm used for verification.
        /// Examples: "SHA-256", "SHA-384", "SHA-512", "BLAKE3".
        /// </summary>
        public string? HashAlgorithm { get; init; }

        /// <summary>
        /// Gets the digital signature verification status, if applicable.
        /// </summary>
        public bool? SignatureValid { get; init; }

        /// <summary>
        /// Gets the list of integrity violations found.
        /// Empty list when IsValid is true.
        /// </summary>
        public IReadOnlyList<IntegrityViolation> Violations { get; init; } = Array.Empty<IntegrityViolation>();

        /// <summary>
        /// Gets the timestamp when verification was performed.
        /// </summary>
        public DateTime VerifiedAt { get; init; } = DateTime.UtcNow;

        /// <summary>
        /// Gets the duration of the verification operation.
        /// </summary>
        public TimeSpan VerificationDuration { get; init; }

        /// <summary>
        /// Gets the chain of custody records associated with this verification.
        /// </summary>
        public IReadOnlyList<CustodyRecord>? ChainOfCustody { get; init; }

        /// <summary>
        /// Gets additional metadata about the verification.
        /// </summary>
        public IReadOnlyDictionary<string, string>? Metadata { get; init; }

        /// <summary>
        /// Creates a valid result with the computed hash.
        /// </summary>
        /// <param name="hash">The computed hash value.</param>
        /// <param name="algorithm">The hash algorithm used.</param>
        /// <returns>A valid integrity verification result.</returns>
        public static IntegrityVerificationResult Valid(string hash, string algorithm)
        {
            return new IntegrityVerificationResult
            {
                IsValid = true,
                ComputedHash = hash,
                HashAlgorithm = algorithm
            };
        }

        /// <summary>
        /// Creates an invalid result with violation details.
        /// </summary>
        /// <param name="violations">The list of violations found.</param>
        /// <returns>An invalid integrity verification result.</returns>
        public static IntegrityVerificationResult Invalid(IReadOnlyList<IntegrityViolation> violations)
        {
            return new IntegrityVerificationResult
            {
                IsValid = false,
                Violations = violations
            };
        }
    }

    /// <summary>
    /// A record in the chain of custody for data integrity tracking.
    /// Documents who accessed or modified data and when.
    /// </summary>
    public sealed record CustodyRecord
    {
        /// <summary>
        /// Gets the principal who performed the action.
        /// </summary>
        public required string Principal { get; init; }

        /// <summary>
        /// Gets the action performed (e.g., "created", "modified", "accessed", "transferred").
        /// </summary>
        public required string Action { get; init; }

        /// <summary>
        /// Gets the timestamp of the action.
        /// </summary>
        public required DateTime Timestamp { get; init; }

        /// <summary>
        /// Gets the hash of the data at this point in the chain.
        /// </summary>
        public string? DataHash { get; init; }

        /// <summary>
        /// Gets the location or system where the action occurred.
        /// </summary>
        public string? Location { get; init; }

        /// <summary>
        /// Gets additional notes or evidence for this custody record.
        /// </summary>
        public string? Notes { get; init; }
    }

    /// <summary>
    /// Interface for strategy-level integrity verification within the security framework.
    /// Implementations verify data integrity through hashing, signatures, and chain of custody.
    /// </summary>
    public interface IIntegrityVerifier
    {
        /// <summary>
        /// Gets the unique identifier for this verifier.
        /// </summary>
        string VerifierId { get; }

        /// <summary>
        /// Gets the display name of this verifier.
        /// </summary>
        string VerifierName { get; }

        /// <summary>
        /// Gets the hash algorithms supported by this verifier.
        /// </summary>
        IReadOnlyList<string> SupportedAlgorithms { get; }

        /// <summary>
        /// Verifies the integrity of the specified data.
        /// </summary>
        /// <param name="data">The data to verify.</param>
        /// <param name="expectedHash">The expected hash value for comparison.</param>
        /// <param name="algorithm">The hash algorithm to use (e.g., "SHA-256").</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The integrity verification result.</returns>
        /// <exception cref="ArgumentNullException">If data is null.</exception>
        /// <exception cref="ArgumentException">If algorithm is not supported.</exception>
        Task<IntegrityVerificationResult> VerifyAsync(
            ReadOnlyMemory<byte> data,
            string? expectedHash = null,
            string algorithm = "SHA-256",
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Computes and stores a hash for future integrity verification.
        /// </summary>
        /// <param name="resourceId">The resource identifier.</param>
        /// <param name="data">The data to hash.</param>
        /// <param name="algorithm">The hash algorithm to use.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The computed hash value.</returns>
        Task<string> ComputeAndStoreHashAsync(
            string resourceId,
            ReadOnlyMemory<byte> data,
            string algorithm = "SHA-256",
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Verifies the chain of custody for a resource.
        /// </summary>
        /// <param name="resourceId">The resource to verify.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The integrity verification result including chain of custody analysis.</returns>
        Task<IntegrityVerificationResult> VerifyChainOfCustodyAsync(
            string resourceId,
            CancellationToken cancellationToken = default);
    }

    #endregion
}
