using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Honeypot
{
    /// <summary>
    /// Honeypot canary strategy that creates decoy objects to detect unauthorized access.
    /// When a canary is accessed, it triggers alerts for security monitoring.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Canary objects are strategically placed throughout the data warehouse to:
    /// - Detect insider threats accessing data they shouldn't
    /// - Identify compromised credentials being used for data exfiltration
    /// - Track lateral movement by attackers
    /// - Provide early warning of data breaches
    /// </para>
    /// <para>
    /// The strategy supports different canary types:
    /// - File canaries: Fake documents with tempting names
    /// - Credential canaries: Fake API keys and passwords
    /// - Database canaries: Fake tables with attractive data
    /// - Token canaries: Trackable tokens embedded in data
    /// </para>
    /// </remarks>
    public sealed class CanaryStrategy : AccessControlStrategyBase
    {
        private readonly ConcurrentDictionary<string, CanaryObject> _canaries = new();
        private readonly ConcurrentQueue<CanaryAlert> _alertQueue = new();
        private Func<CanaryAlert, Task>? _alertHandler;
        private int _maxAlertsInQueue = 10000;

        /// <inheritdoc/>
        public override string StrategyId => "canary";

        /// <inheritdoc/>
        public override string StrategyName => "Honeypot Canary";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = false,
            SupportsTemporalAccess = true,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 10000
        };

        /// <summary>
        /// Gets the list of triggered alerts.
        /// </summary>
        public IReadOnlyCollection<CanaryAlert> GetRecentAlerts(int count = 100)
        {
            return _alertQueue.Take(count).ToList();
        }

        /// <summary>
        /// Registers an alert handler for real-time notifications.
        /// </summary>
        public void SetAlertHandler(Func<CanaryAlert, Task> handler)
        {
            _alertHandler = handler;
        }

        /// <summary>
        /// Creates a new canary object.
        /// </summary>
        public CanaryObject CreateCanary(string resourceId, CanaryType type, string? description = null)
        {
            var canary = new CanaryObject
            {
                Id = Guid.NewGuid().ToString("N"),
                ResourceId = resourceId,
                Type = type,
                Description = description ?? $"Canary for {resourceId}",
                CreatedAt = DateTime.UtcNow,
                Token = GenerateCanaryToken(),
                IsActive = true
            };

            _canaries[resourceId] = canary;
            return canary;
        }

        /// <summary>
        /// Checks if a resource is a canary.
        /// </summary>
        public bool IsCanary(string resourceId)
        {
            return _canaries.ContainsKey(resourceId);
        }

        /// <summary>
        /// Deactivates a canary.
        /// </summary>
        public void DeactivateCanary(string resourceId)
        {
            if (_canaries.TryGetValue(resourceId, out var canary))
            {
                canary.IsActive = false;
            }
        }

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("MaxAlertsInQueue", out var maxAlerts) && maxAlerts is int max)
            {
                _maxAlertsInQueue = max;
            }

            // Initialize canaries from configuration
            if (configuration.TryGetValue("Canaries", out var canariesObj) &&
                canariesObj is IEnumerable<Dictionary<string, object>> canaryConfigs)
            {
                foreach (var config in canaryConfigs)
                {
                    var resourceId = config["ResourceId"]?.ToString() ?? "";
                    var typeStr = config.TryGetValue("Type", out var t) ? t?.ToString() : "File";
                    var type = Enum.TryParse<CanaryType>(typeStr, out var ct) ? ct : CanaryType.File;
                    var description = config.TryGetValue("Description", out var d) ? d?.ToString() : null;

                    CreateCanary(resourceId, type, description);
                }
            }

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <inheritdoc/>
        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            // Check if the accessed resource is a canary
            if (_canaries.TryGetValue(context.ResourceId, out var canary) && canary.IsActive)
            {
                // Canary accessed! Generate alert
                var alert = new CanaryAlert
                {
                    Id = Guid.NewGuid().ToString("N"),
                    CanaryId = canary.Id,
                    ResourceId = context.ResourceId,
                    CanaryType = canary.Type,
                    AccessedBy = context.SubjectId,
                    AccessedAt = DateTime.UtcNow,
                    Action = context.Action,
                    ClientIpAddress = context.ClientIpAddress,
                    Location = context.Location,
                    Severity = DetermineSeverity(context, canary),
                    AdditionalContext = new Dictionary<string, object>
                    {
                        ["Roles"] = context.Roles,
                        ["SubjectAttributes"] = context.SubjectAttributes
                    }
                };

                // Update canary statistics
                canary.AccessCount++;
                canary.LastAccessedAt = DateTime.UtcNow;
                canary.LastAccessedBy = context.SubjectId;

                // Queue the alert
                while (_alertQueue.Count >= _maxAlertsInQueue)
                {
                    _alertQueue.TryDequeue(out _);
                }
                _alertQueue.Enqueue(alert);

                // Notify alert handler if registered
                if (_alertHandler != null)
                {
                    try
                    {
                        await _alertHandler(alert);
                    }
                    catch
                    {
                        // Don't fail access evaluation due to alert handler failure
                    }
                }

                // Determine response based on configuration
                var blockAccess = Configuration.TryGetValue("BlockCanaryAccess", out var block) && block is true;

                if (blockAccess)
                {
                    return new AccessDecision
                    {
                        IsGranted = false,
                        Reason = "Access to restricted resource denied",
                        ApplicablePolicies = new[] { "CanaryProtection" },
                        Metadata = new Dictionary<string, object>
                        {
                            ["AlertId"] = alert.Id,
                            ["Severity"] = alert.Severity.ToString()
                        }
                    };
                }

                // Allow access but flag it (silent monitoring)
                return new AccessDecision
                {
                    IsGranted = true,
                    Reason = "Access granted (monitored)",
                    ApplicablePolicies = new[] { "CanaryMonitoring" },
                    Metadata = new Dictionary<string, object>
                    {
                        ["AlertId"] = alert.Id,
                        ["IsCanary"] = true,
                        ["Severity"] = alert.Severity.ToString()
                    }
                };
            }

            // Not a canary - allow access
            return new AccessDecision
            {
                IsGranted = true,
                Reason = "Resource is not a canary - access allowed",
                ApplicablePolicies = Array.Empty<string>()
            };
        }

        private AlertSeverity DetermineSeverity(AccessContext context, CanaryObject canary)
        {
            // High severity conditions
            if (canary.Type == CanaryType.Credential)
                return AlertSeverity.Critical;

            if (context.Action.Equals("delete", StringComparison.OrdinalIgnoreCase) ||
                context.Action.Equals("modify", StringComparison.OrdinalIgnoreCase))
                return AlertSeverity.High;

            // Check for suspicious patterns
            if (canary.AccessCount > 5)
                return AlertSeverity.High;

            // Check time-based suspicion (off-hours access)
            var hour = context.RequestTime.Hour;
            if (hour < 6 || hour > 22)
                return AlertSeverity.Medium;

            return AlertSeverity.Low;
        }

        private static string GenerateCanaryToken()
        {
            var bytes = RandomNumberGenerator.GetBytes(32);
            return Convert.ToBase64String(bytes);
        }
    }

    /// <summary>
    /// Types of canary objects.
    /// </summary>
    public enum CanaryType
    {
        /// <summary>Fake file with tempting name (e.g., "passwords.xlsx")</summary>
        File,

        /// <summary>Fake credential (e.g., API key, password)</summary>
        Credential,

        /// <summary>Fake database table with attractive data</summary>
        Database,

        /// <summary>Trackable token embedded in data</summary>
        Token,

        /// <summary>Fake network share or endpoint</summary>
        Network,

        /// <summary>Fake user account</summary>
        Account
    }

    /// <summary>
    /// Represents a canary object.
    /// </summary>
    public sealed class CanaryObject
    {
        public required string Id { get; init; }
        public required string ResourceId { get; init; }
        public required CanaryType Type { get; init; }
        public required string Description { get; init; }
        public required DateTime CreatedAt { get; init; }
        public required string Token { get; init; }
        public bool IsActive { get; set; }
        public int AccessCount { get; set; }
        public DateTime? LastAccessedAt { get; set; }
        public string? LastAccessedBy { get; set; }
    }

    /// <summary>
    /// Alert severity levels.
    /// </summary>
    public enum AlertSeverity
    {
        Low,
        Medium,
        High,
        Critical
    }

    /// <summary>
    /// Represents an alert triggered by canary access.
    /// </summary>
    public sealed record CanaryAlert
    {
        public required string Id { get; init; }
        public required string CanaryId { get; init; }
        public required string ResourceId { get; init; }
        public required CanaryType CanaryType { get; init; }
        public required string AccessedBy { get; init; }
        public required DateTime AccessedAt { get; init; }
        public required string Action { get; init; }
        public string? ClientIpAddress { get; init; }
        public GeoLocation? Location { get; init; }
        public required AlertSeverity Severity { get; init; }
        public IReadOnlyDictionary<string, object> AdditionalContext { get; init; } = new Dictionary<string, object>();
    }
}
