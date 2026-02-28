using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateAccessControl.Features
{
    /// <summary>
    /// Data Loss Prevention (DLP) engine with content inspection, pattern matching, and policy enforcement.
    /// </summary>
    public sealed class DlpEngine
    {
        private readonly BoundedDictionary<string, DlpPolicy> _policies = new BoundedDictionary<string, DlpPolicy>(1000);
        private readonly BoundedDictionary<string, Regex> _regexCache = new BoundedDictionary<string, Regex>(1000);
        private readonly ConcurrentQueue<DlpViolation> _violations = new();
        private readonly int _maxViolationHistorySize = 1000;

        /// <summary>
        /// Registers a DLP policy.
        /// </summary>
        public void RegisterPolicy(DlpPolicy policy)
        {
            if (policy == null)
                throw new ArgumentNullException(nameof(policy));

            if (string.IsNullOrWhiteSpace(policy.PolicyId))
                throw new ArgumentException("Policy ID cannot be null or empty");

            _policies[policy.PolicyId] = policy;

            // Pre-compile regex patterns for performance
            foreach (var rule in policy.Rules.Where(r => r.MatchType == DlpMatchType.Regex))
            {
                try
                {
                    _regexCache[rule.Pattern] = new Regex(rule.Pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
                }
                catch (ArgumentException ex)
                {
                    throw new InvalidOperationException($"Invalid regex pattern in rule '{rule.RuleId}': {rule.Pattern}", ex);
                }
            }
        }

        /// <summary>
        /// Scans content for DLP policy violations.
        /// </summary>
        public async Task<DlpScanResult> ScanContentAsync(
            string content,
            DlpScanContext context,
            CancellationToken cancellationToken = default)
        {
            if (content == null)
                throw new ArgumentNullException(nameof(content));

            if (context == null)
                throw new ArgumentNullException(nameof(context));

            // Guard against unbounded inputs causing blocking regex on huge payloads (finding #1116)
            const int MaxDlpScanBytes = 100 * 1024 * 1024; // 100 MB
            if (content.Length > MaxDlpScanBytes)
                throw new ArgumentException($"Content length {content.Length} bytes exceeds DLP scan limit of {MaxDlpScanBytes} bytes. Split input before scanning.", nameof(content));

            var violations = new List<DlpRuleViolation>();
            var applicablePolicies = _policies.Values
                .Where(p => p.IsEnabled && (p.Scope == DlpScope.All || p.Scope == context.Scope))
                .ToList();

            foreach (var policy in applicablePolicies)
            {
                foreach (var rule in policy.Rules.Where(r => r.IsEnabled))
                {
                    var matches = await DetectMatchesAsync(content, rule, cancellationToken);

                    foreach (var match in matches)
                    {
                        violations.Add(new DlpRuleViolation
                        {
                            PolicyId = policy.PolicyId,
                            PolicyName = policy.Name,
                            RuleId = rule.RuleId,
                            RuleName = rule.Name,
                            Severity = rule.Severity,
                            Action = rule.Action,
                            MatchedText = match.MatchedText,
                            Position = match.Position,
                            Classification = rule.DataClassification,
                            Description = rule.Description
                        });
                    }
                }
            }

            // Record violations
            if (violations.Any())
            {
                var violation = new DlpViolation
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Timestamp = DateTime.UtcNow,
                    UserId = context.UserId,
                    ResourceId = context.ResourceId,
                    Action = context.Action,
                    Scope = context.Scope,
                    Violations = violations
                };

                _violations.Enqueue(violation);

                // Prune old violations
                while (_violations.Count > _maxViolationHistorySize)
                {
                    _violations.TryDequeue(out _);
                }
            }

            // Determine overall action
            var blockers = violations.Where(v => v.Action == DlpAction.Block).ToList();
            var alerts = violations.Where(v => v.Action == DlpAction.Alert).ToList();
            var highestSeverity = violations.Any() ? violations.Max(v => v.Severity) : DlpSeverity.None;

            return new DlpScanResult
            {
                IsClean = !violations.Any(),
                RecommendedAction = blockers.Any() ? DlpAction.Block :
                                    alerts.Any() ? DlpAction.Alert : DlpAction.Allow,
                Violations = violations,
                HighestSeverity = highestSeverity,
                ScannedAt = DateTime.UtcNow,
                Summary = violations.Any()
                    ? $"Found {violations.Count} violations ({blockers.Count} blocking, {alerts.Count} alerting)"
                    : "No policy violations detected"
            };
        }

        private Task<List<DlpMatch>> DetectMatchesAsync(string content, DlpRule rule, CancellationToken cancellationToken)
        {
            var matches = new List<DlpMatch>();

            switch (rule.MatchType)
            {
                case DlpMatchType.Regex:
                    if (_regexCache.TryGetValue(rule.Pattern, out var regex))
                    {
                        var regexMatches = regex.Matches(content);
                        foreach (Match match in regexMatches)
                        {
                            matches.Add(new DlpMatch
                            {
                                MatchedText = MaskSensitiveData(match.Value),
                                Position = match.Index
                            });
                        }
                    }
                    break;

                case DlpMatchType.Keyword:
                    var keywords = rule.Pattern.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var keyword in keywords)
                    {
                        var trimmed = keyword.Trim();
                        var index = content.IndexOf(trimmed, StringComparison.OrdinalIgnoreCase);
                        while (index >= 0)
                        {
                            matches.Add(new DlpMatch
                            {
                                MatchedText = MaskSensitiveData(trimmed),
                                Position = index
                            });
                            index = content.IndexOf(trimmed, index + trimmed.Length, StringComparison.OrdinalIgnoreCase);
                        }
                    }
                    break;

                case DlpMatchType.ContentHash:
                    // Hash-based detection for known sensitive documents
                    var contentHash = ComputeSimpleHash(content);
                    if (rule.Pattern.Contains(contentHash))
                    {
                        matches.Add(new DlpMatch
                        {
                            MatchedText = "[Content Hash Match]",
                            Position = 0
                        });
                    }
                    break;
            }

            return Task.FromResult(matches);
        }

        private string MaskSensitiveData(string text)
        {
            if (text.Length <= 4)
                return new string('*', text.Length);

            return text.Substring(0, 2) + new string('*', text.Length - 4) + text.Substring(text.Length - 2);
        }

        private string ComputeSimpleHash(string content)
        {
            // Use SHA-256 for reliable, collision-resistant content fingerprinting
            var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
            return Convert.ToHexString(hashBytes);
        }

        /// <summary>
        /// Gets all registered policies.
        /// </summary>
        public IReadOnlyCollection<DlpPolicy> GetPolicies()
        {
            return _policies.Values.ToList().AsReadOnly();
        }

        /// <summary>
        /// Gets recent violations.
        /// </summary>
        public IReadOnlyCollection<DlpViolation> GetRecentViolations(int maxCount = 100)
        {
            return _violations.Take(maxCount).ToList().AsReadOnly();
        }

        /// <summary>
        /// Removes a policy.
        /// </summary>
        public bool RemovePolicy(string policyId)
        {
            return _policies.TryRemove(policyId, out _);
        }
    }

    #region Supporting Types

    /// <summary>
    /// DLP policy definition.
    /// </summary>
    public sealed class DlpPolicy
    {
        public required string PolicyId { get; init; }
        public required string Name { get; init; }
        public required string Description { get; init; }
        public required bool IsEnabled { get; init; }
        public required DlpScope Scope { get; init; }
        public required List<DlpRule> Rules { get; init; }
    }

    /// <summary>
    /// DLP rule.
    /// </summary>
    public sealed class DlpRule
    {
        public required string RuleId { get; init; }
        public required string Name { get; init; }
        public required string Description { get; init; }
        public required bool IsEnabled { get; init; }
        public required DlpMatchType MatchType { get; init; }
        public required string Pattern { get; init; }
        public required DlpSeverity Severity { get; init; }
        public required DlpAction Action { get; init; }
        public required string DataClassification { get; init; }
    }

    /// <summary>
    /// DLP scope.
    /// </summary>
    public enum DlpScope
    {
        All,
        Upload,
        Download,
        Share,
        Email,
        Print
    }

    /// <summary>
    /// DLP match type.
    /// </summary>
    public enum DlpMatchType
    {
        Regex,
        Keyword,
        ContentHash
    }

    /// <summary>
    /// DLP severity.
    /// </summary>
    public enum DlpSeverity
    {
        None = 0,
        Low = 1,
        Medium = 2,
        High = 3,
        Critical = 4
    }

    /// <summary>
    /// DLP action.
    /// </summary>
    public enum DlpAction
    {
        Allow,
        Alert,
        Block,
        Encrypt,
        Redact
    }

    /// <summary>
    /// DLP scan context.
    /// </summary>
    public sealed class DlpScanContext
    {
        public required string UserId { get; init; }
        public required string ResourceId { get; init; }
        public required string Action { get; init; }
        public required DlpScope Scope { get; init; }
    }

    /// <summary>
    /// DLP match.
    /// </summary>
    public sealed class DlpMatch
    {
        public required string MatchedText { get; init; }
        public required int Position { get; init; }
    }

    /// <summary>
    /// DLP rule violation.
    /// </summary>
    public sealed class DlpRuleViolation
    {
        public required string PolicyId { get; init; }
        public required string PolicyName { get; init; }
        public required string RuleId { get; init; }
        public required string RuleName { get; init; }
        public required DlpSeverity Severity { get; init; }
        public required DlpAction Action { get; init; }
        public required string MatchedText { get; init; }
        public required int Position { get; init; }
        public required string Classification { get; init; }
        public string? Description { get; init; }
    }

    /// <summary>
    /// DLP scan result.
    /// </summary>
    public sealed class DlpScanResult
    {
        public required bool IsClean { get; init; }
        public required DlpAction RecommendedAction { get; init; }
        public required List<DlpRuleViolation> Violations { get; init; }
        public required DlpSeverity HighestSeverity { get; init; }
        public required DateTime ScannedAt { get; init; }
        public required string Summary { get; init; }
    }

    /// <summary>
    /// DLP violation record.
    /// </summary>
    public sealed class DlpViolation
    {
        public required string Id { get; init; }
        public required DateTime Timestamp { get; init; }
        public required string UserId { get; init; }
        public required string ResourceId { get; init; }
        public required string Action { get; init; }
        public required DlpScope Scope { get; init; }
        public required List<DlpRuleViolation> Violations { get; init; }
    }

    #endregion
}
