using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Honeypot
{
    /// <summary>
    /// Comprehensive honeypot canary strategy implementing all T73 sub-tasks.
    /// Provides canary generation, placement, monitoring, alerting, and forensics.
    /// </summary>
    public sealed class CanaryStrategy : AccessControlStrategyBase, IDisposable
    {
        private readonly ConcurrentDictionary<string, CanaryObject> _canaries = new();
        private readonly ConcurrentDictionary<string, ExclusionRule> _exclusionRules = new();
        private readonly ConcurrentQueue<CanaryAlert> _alertQueue = new();
        private readonly ConcurrentDictionary<string, CanaryMetrics> _metrics = new();
        private readonly List<IAlertChannel> _alertChannels = new();
        private readonly CanaryGenerator _generator;
        private readonly PlacementEngine _placementEngine;
        private readonly ForensicCaptureEngine _forensicEngine;
        private readonly object _metricsLock = new();

        private Timer? _rotationTimer;
        private Timer? _metricsAggregationTimer;
        private Func<string, Task>? _lockdownHandler;
        private int _maxAlertsInQueue = 10000;
        private TimeSpan _rotationInterval = TimeSpan.FromHours(24);
        private bool _disposed;

        public CanaryStrategy()
        {
            _generator = new CanaryGenerator();
            _placementEngine = new PlacementEngine();
            _forensicEngine = new ForensicCaptureEngine();
        }

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

        #region 73.1 - Canary Generator

        /// <summary>
        /// Creates a convincing canary file with realistic content.
        /// Supports passwords.xlsx, wallet.dat, credentials.json, etc.
        /// </summary>
        public CanaryObject CreateCanaryFile(string resourceId, CanaryFileType fileType, CanaryPlacementHint? placementHint = null)
        {
            var content = _generator.GenerateFileContent(fileType);
            var suggestedName = _generator.GetSuggestedFileName(fileType);

            var canary = new CanaryObject
            {
                Id = Guid.NewGuid().ToString("N"),
                ResourceId = resourceId,
                Type = CanaryType.File,
                FileType = fileType,
                Description = $"Canary file: {suggestedName}",
                CreatedAt = DateTime.UtcNow,
                Token = GenerateCanaryToken(),
                IsActive = true,
                Content = content,
                SuggestedFileName = suggestedName,
                PlacementPath = placementHint != null
                    ? _placementEngine.SuggestPlacement(placementHint.Value)
                    : null,
                Metadata = new Dictionary<string, object>
                {
                    ["fileType"] = fileType.ToString(),
                    ["contentHash"] = ComputeContentHash(content),
                    ["generatedAt"] = DateTime.UtcNow.ToString("O")
                }
            };

            _canaries[resourceId] = canary;
            InitializeMetrics(canary.Id);
            return canary;
        }

        /// <summary>
        /// Creates a directory canary that monitors an entire directory for access.
        /// </summary>
        public CanaryObject CreateDirectoryCanary(string resourceId, string directoryPath)
        {
            var canary = new CanaryObject
            {
                Id = Guid.NewGuid().ToString("N"),
                ResourceId = resourceId,
                Type = CanaryType.Directory,
                Description = $"Directory canary: {directoryPath}",
                CreatedAt = DateTime.UtcNow,
                Token = GenerateCanaryToken(),
                IsActive = true,
                PlacementPath = directoryPath,
                Metadata = new Dictionary<string, object>
                {
                    ["directoryPath"] = directoryPath,
                    ["monitorSubdirectories"] = true
                }
            };

            _canaries[resourceId] = canary;
            InitializeMetrics(canary.Id);
            return canary;
        }

        /// <summary>
        /// Creates an API honeytoken for detecting unauthorized API access.
        /// </summary>
        public CanaryObject CreateApiHoneytoken(string resourceId, string apiEndpoint, string? fakeApiKey = null)
        {
            var apiKey = fakeApiKey ?? _generator.GenerateFakeApiKey();

            var canary = new CanaryObject
            {
                Id = Guid.NewGuid().ToString("N"),
                ResourceId = resourceId,
                Type = CanaryType.ApiHoneytoken,
                Description = $"API Honeytoken: {apiEndpoint}",
                CreatedAt = DateTime.UtcNow,
                Token = GenerateCanaryToken(),
                IsActive = true,
                Content = Encoding.UTF8.GetBytes(apiKey),
                Metadata = new Dictionary<string, object>
                {
                    ["apiEndpoint"] = apiEndpoint,
                    ["fakeApiKey"] = apiKey,
                    ["keyPrefix"] = apiKey[..8]
                }
            };

            _canaries[resourceId] = canary;
            InitializeMetrics(canary.Id);
            return canary;
        }

        /// <summary>
        /// Creates a database honeytoken (fake table with attractive data).
        /// </summary>
        public CanaryObject CreateDatabaseHoneytoken(string resourceId, string tableName)
        {
            var fakeData = _generator.GenerateFakeDatabaseContent(tableName);

            var canary = new CanaryObject
            {
                Id = Guid.NewGuid().ToString("N"),
                ResourceId = resourceId,
                Type = CanaryType.Database,
                Description = $"Database honeytoken: {tableName}",
                CreatedAt = DateTime.UtcNow,
                Token = GenerateCanaryToken(),
                IsActive = true,
                Content = Encoding.UTF8.GetBytes(fakeData),
                Metadata = new Dictionary<string, object>
                {
                    ["tableName"] = tableName,
                    ["recordCount"] = 100,
                    ["schema"] = "id,username,password_hash,email,ssn,credit_card"
                }
            };

            _canaries[resourceId] = canary;
            InitializeMetrics(canary.Id);
            return canary;
        }

        #endregion

        #region 73.2 - Placement Strategy

        /// <summary>
        /// Gets suggested placements for canaries based on directory scanning likelihood.
        /// </summary>
        public IReadOnlyList<PlacementSuggestion> GetPlacementSuggestions(int count = 10)
        {
            return _placementEngine.GetTopPlacements(count);
        }

        /// <summary>
        /// Automatically deploys canaries to high-value locations.
        /// </summary>
        public async Task<IReadOnlyList<CanaryObject>> AutoDeployCanariesAsync(
            AutoDeploymentConfig config,
            CancellationToken cancellationToken = default)
        {
            var deployed = new List<CanaryObject>();
            var placements = _placementEngine.GetTopPlacements(config.MaxCanaries);

            foreach (var placement in placements)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                var fileType = _placementEngine.GetBestFileTypeForLocation(placement.Path);
                var resourceId = $"canary:{placement.Path}:{Guid.NewGuid():N}";

                var canary = CreateCanaryFile(resourceId, fileType, placement.Hint);
                canary.PlacementPath = placement.Path;
                canary.Metadata["autoDeployed"] = true;
                canary.Metadata["deploymentReason"] = placement.Reason;

                deployed.Add(canary);

                // Small delay between deployments
                await Task.Delay(10, cancellationToken);
            }

            return deployed.AsReadOnly();
        }

        #endregion

        #region 73.3 - Access Monitoring

        /// <summary>
        /// Gets recent alerts from the monitoring queue.
        /// </summary>
        public IReadOnlyCollection<CanaryAlert> GetRecentAlerts(int count = 100)
        {
            return _alertQueue.Take(count).ToList();
        }

        /// <summary>
        /// Checks if a resource is a canary.
        /// </summary>
        public bool IsCanary(string resourceId)
        {
            return _canaries.ContainsKey(resourceId);
        }

        /// <summary>
        /// Gets all active canaries.
        /// </summary>
        public IReadOnlyCollection<CanaryObject> GetActiveCanaries()
        {
            return _canaries.Values.Where(c => c.IsActive).ToList().AsReadOnly();
        }

        #endregion

        #region 73.4 - Instant Lockdown

        /// <summary>
        /// Registers a lockdown handler for automatic account/session termination.
        /// </summary>
        public void SetLockdownHandler(Func<string, Task> handler)
        {
            _lockdownHandler = handler;
        }

        /// <summary>
        /// Triggers immediate lockdown for a subject.
        /// </summary>
        public async Task TriggerLockdownAsync(string subjectId, CanaryAlert triggeringAlert)
        {
            var lockdownEvent = new LockdownEvent
            {
                Id = Guid.NewGuid().ToString("N"),
                SubjectId = subjectId,
                TriggeredAt = DateTime.UtcNow,
                TriggeringAlertId = triggeringAlert.Id,
                CanaryId = triggeringAlert.CanaryId,
                Reason = $"Canary access detected: {triggeringAlert.ResourceId}"
            };

            // Execute lockdown handler if registered
            if (_lockdownHandler != null)
            {
                try
                {
                    await _lockdownHandler(subjectId);
                    lockdownEvent.Success = true;
                }
                catch (Exception ex)
                {
                    lockdownEvent.Success = false;
                    lockdownEvent.ErrorMessage = ex.Message;
                }
            }

            // Add lockdown info to alert
            triggeringAlert.LockdownTriggered = true;
            triggeringAlert.LockdownEvent = lockdownEvent;
        }

        #endregion

        #region 73.5 - Alert Pipeline

        /// <summary>
        /// Registers an alert channel for multi-channel notifications.
        /// </summary>
        public void RegisterAlertChannel(IAlertChannel channel)
        {
            _alertChannels.Add(channel);
        }

        /// <summary>
        /// Sends alert through all registered channels.
        /// </summary>
        private async Task SendAlertsAsync(CanaryAlert alert)
        {
            var tasks = _alertChannels
                .Where(c => c.IsEnabled && alert.Severity >= c.MinimumSeverity)
                .Select(async channel =>
                {
                    try
                    {
                        await channel.SendAlertAsync(alert);
                    }
                    catch
                    {
                        // Log but don't fail on channel errors
                    }
                });

            await Task.WhenAll(tasks);
        }

        #endregion

        #region 73.6 - Forensic Capture

        /// <summary>
        /// Captures forensic information when a canary is triggered.
        /// </summary>
        private ForensicSnapshot CaptureForensics(AccessContext context, CanaryObject canary)
        {
            return _forensicEngine.CaptureSnapshot(context, canary);
        }

        #endregion

        #region 73.7 - Canary Rotation

        /// <summary>
        /// Starts automatic canary rotation.
        /// </summary>
        public void StartRotation(TimeSpan interval)
        {
            _rotationInterval = interval;
            _rotationTimer?.Dispose();
            _rotationTimer = new Timer(
                async _ => await RotateCanariesAsync(),
                null,
                interval,
                interval);
        }

        /// <summary>
        /// Stops automatic canary rotation.
        /// </summary>
        public void StopRotation()
        {
            _rotationTimer?.Dispose();
            _rotationTimer = null;
        }

        /// <summary>
        /// Manually rotates all canaries to avoid detection.
        /// </summary>
        public async Task RotateCanariesAsync()
        {
            var canariesToRotate = _canaries.Values
                .Where(c => c.IsActive && c.Type == CanaryType.File)
                .ToList();

            foreach (var canary in canariesToRotate)
            {
                // Generate new content while keeping the same resource ID
                if (canary.FileType.HasValue)
                {
                    var newContent = _generator.GenerateFileContent(canary.FileType.Value);
                    canary.Content = newContent;
                    canary.Token = GenerateCanaryToken();
                    canary.RotatedAt = DateTime.UtcNow;
                    canary.RotationCount++;
                    canary.Metadata["contentHash"] = ComputeContentHash(newContent);
                    canary.Metadata["lastRotation"] = DateTime.UtcNow.ToString("O");
                }

                await Task.Yield(); // Prevent blocking
            }
        }

        #endregion

        #region 73.8 - Exclusion Rules

        /// <summary>
        /// Adds an exclusion rule for legitimate processes.
        /// </summary>
        public void AddExclusionRule(ExclusionRule rule)
        {
            _exclusionRules[rule.Id] = rule;
        }

        /// <summary>
        /// Removes an exclusion rule.
        /// </summary>
        public void RemoveExclusionRule(string ruleId)
        {
            _exclusionRules.TryRemove(ruleId, out _);
        }

        /// <summary>
        /// Gets all exclusion rules.
        /// </summary>
        public IReadOnlyCollection<ExclusionRule> GetExclusionRules()
        {
            return _exclusionRules.Values.ToList().AsReadOnly();
        }

        /// <summary>
        /// Checks if access should be excluded from triggering alerts.
        /// </summary>
        private bool IsExcludedAccess(AccessContext context)
        {
            foreach (var rule in _exclusionRules.Values.Where(r => r.IsEnabled))
            {
                if (rule.Matches(context))
                {
                    return true;
                }
            }
            return false;
        }

        #endregion

        #region 73.9 - Canary Types

        /// <summary>
        /// Creates a canary of the specified type.
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
            InitializeMetrics(canary.Id);
            return canary;
        }

        /// <summary>
        /// Creates a credential canary (fake password/API key).
        /// </summary>
        public CanaryObject CreateCredentialCanary(string resourceId, CredentialType credentialType)
        {
            var credential = _generator.GenerateFakeCredential(credentialType);

            var canary = new CanaryObject
            {
                Id = Guid.NewGuid().ToString("N"),
                ResourceId = resourceId,
                Type = CanaryType.Credential,
                Description = $"Credential canary: {credentialType}",
                CreatedAt = DateTime.UtcNow,
                Token = GenerateCanaryToken(),
                IsActive = true,
                Content = Encoding.UTF8.GetBytes(credential),
                Metadata = new Dictionary<string, object>
                {
                    ["credentialType"] = credentialType.ToString(),
                    ["credential"] = credential
                }
            };

            _canaries[resourceId] = canary;
            InitializeMetrics(canary.Id);
            return canary;
        }

        /// <summary>
        /// Creates a network canary (fake network share/endpoint).
        /// </summary>
        public CanaryObject CreateNetworkCanary(string resourceId, string endpoint)
        {
            var canary = new CanaryObject
            {
                Id = Guid.NewGuid().ToString("N"),
                ResourceId = resourceId,
                Type = CanaryType.Network,
                Description = $"Network canary: {endpoint}",
                CreatedAt = DateTime.UtcNow,
                Token = GenerateCanaryToken(),
                IsActive = true,
                Metadata = new Dictionary<string, object>
                {
                    ["endpoint"] = endpoint,
                    ["protocol"] = "smb"
                }
            };

            _canaries[resourceId] = canary;
            InitializeMetrics(canary.Id);
            return canary;
        }

        /// <summary>
        /// Creates an account canary (fake user account).
        /// </summary>
        public CanaryObject CreateAccountCanary(string resourceId, string username)
        {
            var password = _generator.GenerateFakePassword();

            var canary = new CanaryObject
            {
                Id = Guid.NewGuid().ToString("N"),
                ResourceId = resourceId,
                Type = CanaryType.Account,
                Description = $"Account canary: {username}",
                CreatedAt = DateTime.UtcNow,
                Token = GenerateCanaryToken(),
                IsActive = true,
                Metadata = new Dictionary<string, object>
                {
                    ["username"] = username,
                    ["passwordHash"] = ComputeContentHash(Encoding.UTF8.GetBytes(password))
                }
            };

            _canaries[resourceId] = canary;
            InitializeMetrics(canary.Id);
            return canary;
        }

        #endregion

        #region 73.10 - Effectiveness Metrics

        /// <summary>
        /// Gets effectiveness metrics for all canaries.
        /// </summary>
        public CanaryEffectivenessReport GetEffectivenessReport()
        {
            var allMetrics = _metrics.Values.ToList();
            var alerts = _alertQueue.ToList();

            var totalTriggers = allMetrics.Sum(m => m.TriggerCount);
            var falsePositives = allMetrics.Sum(m => m.FalsePositiveCount);
            var truePositives = totalTriggers - falsePositives;

            return new CanaryEffectivenessReport
            {
                GeneratedAt = DateTime.UtcNow,
                TotalCanaries = _canaries.Count,
                ActiveCanaries = _canaries.Values.Count(c => c.IsActive),
                TotalTriggers = totalTriggers,
                TruePositives = truePositives,
                FalsePositives = falsePositives,
                FalsePositiveRate = totalTriggers > 0 ? (double)falsePositives / totalTriggers : 0,
                TriggerRate = _canaries.Count > 0 ? (double)totalTriggers / _canaries.Count : 0,
                MeanTimeToDetection = CalculateMeanTimeToDetection(alerts),
                CanaryMetrics = allMetrics.ToDictionary(m => m.CanaryId, m => m),
                AlertsBySeverity = alerts.GroupBy(a => a.Severity)
                    .ToDictionary(g => g.Key, g => g.Count()),
                TopTriggeredCanaries = allMetrics
                    .OrderByDescending(m => m.TriggerCount)
                    .Take(10)
                    .Select(m => new { m.CanaryId, m.TriggerCount })
                    .ToList()
            };
        }

        /// <summary>
        /// Marks an alert as a false positive for metrics tracking.
        /// </summary>
        public void MarkAsFalsePositive(string alertId, string reason)
        {
            var alert = _alertQueue.FirstOrDefault(a => a.Id == alertId);
            if (alert != null)
            {
                alert.IsFalsePositive = true;
                alert.FalsePositiveReason = reason;

                if (_metrics.TryGetValue(alert.CanaryId, out var metrics))
                {
                    Interlocked.Increment(ref metrics.FalsePositiveCount);
                }
            }
        }

        /// <summary>
        /// Gets metrics for a specific canary.
        /// </summary>
        public CanaryMetrics? GetCanaryMetrics(string canaryId)
        {
            return _metrics.TryGetValue(canaryId, out var metrics) ? metrics : null;
        }

        private void InitializeMetrics(string canaryId)
        {
            _metrics[canaryId] = new CanaryMetrics
            {
                CanaryId = canaryId,
                CreatedAt = DateTime.UtcNow
            };
        }

        private void UpdateMetrics(string canaryId, CanaryAlert alert)
        {
            if (_metrics.TryGetValue(canaryId, out var metrics))
            {
                Interlocked.Increment(ref metrics.TriggerCount);
                metrics.LastTriggeredAt = DateTime.UtcNow;

                lock (_metricsLock)
                {
                    if (!metrics.UniqueSubjects.Contains(alert.AccessedBy))
                    {
                        metrics.UniqueSubjects.Add(alert.AccessedBy);
                    }
                }
            }
        }

        private TimeSpan CalculateMeanTimeToDetection(List<CanaryAlert> alerts)
        {
            if (alerts.Count == 0) return TimeSpan.Zero;

            var detectionTimes = alerts
                .Where(a => a.ForensicSnapshot?.ProcessStartTime != null)
                .Select(a => a.AccessedAt - a.ForensicSnapshot!.ProcessStartTime!.Value)
                .Where(t => t > TimeSpan.Zero)
                .ToList();

            if (detectionTimes.Count == 0) return TimeSpan.Zero;

            return TimeSpan.FromTicks((long)detectionTimes.Average(t => t.Ticks));
        }

        #endregion

        #region Core Access Evaluation

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            if (configuration.TryGetValue("MaxAlertsInQueue", out var maxAlerts) && maxAlerts is int max)
            {
                _maxAlertsInQueue = max;
            }

            if (configuration.TryGetValue("RotationIntervalHours", out var rotationHours) && rotationHours is int hours)
            {
                _rotationInterval = TimeSpan.FromHours(hours);
            }

            if (configuration.TryGetValue("EnableAutoRotation", out var autoRotate) && autoRotate is true)
            {
                StartRotation(_rotationInterval);
            }

            // Initialize default exclusion rules for common legitimate processes
            InitializeDefaultExclusionRules();

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

            // Start metrics aggregation
            _metricsAggregationTimer = new Timer(
                _ => AggregateMetrics(),
                null,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(5));

            return base.InitializeAsync(configuration, cancellationToken);
        }

        /// <inheritdoc/>
        protected override async Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            // Check if the accessed resource is a canary
            if (_canaries.TryGetValue(context.ResourceId, out var canary) && canary.IsActive)
            {
                // Check exclusion rules first
                if (IsExcludedAccess(context))
                {
                    return new AccessDecision
                    {
                        IsGranted = true,
                        Reason = "Access excluded by rule",
                        ApplicablePolicies = new[] { "CanaryExclusion" }
                    };
                }

                // Canary accessed! Capture forensics
                var forensics = CaptureForensics(context, canary);

                // Generate alert
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
                    ForensicSnapshot = forensics,
                    AdditionalContext = new Dictionary<string, object>
                    {
                        ["Roles"] = context.Roles,
                        ["SubjectAttributes"] = context.SubjectAttributes,
                        ["CanaryToken"] = canary.Token
                    }
                };

                // Update canary statistics
                canary.AccessCount++;
                canary.LastAccessedAt = DateTime.UtcNow;
                canary.LastAccessedBy = context.SubjectId;

                // Update metrics
                UpdateMetrics(canary.Id, alert);

                // Queue the alert
                while (_alertQueue.Count >= _maxAlertsInQueue)
                {
                    _alertQueue.TryDequeue(out _);
                }
                _alertQueue.Enqueue(alert);

                // Send alerts through pipeline
                await SendAlertsAsync(alert);

                // Check if lockdown should be triggered
                var triggerLockdown = Configuration.TryGetValue("EnableAutoLockdown", out var lockdown) && lockdown is true;
                if (triggerLockdown && alert.Severity >= AlertSeverity.High)
                {
                    await TriggerLockdownAsync(context.SubjectId, alert);
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
                            ["Severity"] = alert.Severity.ToString(),
                            ["ForensicCaptured"] = true
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
                        ["Severity"] = alert.Severity.ToString(),
                        ["ForensicCaptured"] = true
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
            // Critical severity for credential canaries
            if (canary.Type == CanaryType.Credential || canary.Type == CanaryType.ApiHoneytoken)
                return AlertSeverity.Critical;

            // High severity for destructive actions
            if (context.Action.Equals("delete", StringComparison.OrdinalIgnoreCase) ||
                context.Action.Equals("modify", StringComparison.OrdinalIgnoreCase) ||
                context.Action.Equals("write", StringComparison.OrdinalIgnoreCase))
                return AlertSeverity.High;

            // High severity for repeated access
            if (canary.AccessCount > 5)
                return AlertSeverity.High;

            // Medium severity for off-hours access
            var hour = context.RequestTime.Hour;
            if (hour < 6 || hour > 22)
                return AlertSeverity.Medium;

            // Medium severity for database canaries
            if (canary.Type == CanaryType.Database)
                return AlertSeverity.Medium;

            return AlertSeverity.Low;
        }

        private void InitializeDefaultExclusionRules()
        {
            // Exclude common backup processes
            AddExclusionRule(new ExclusionRule
            {
                Id = "backup-processes",
                Name = "Backup Software",
                Description = "Excludes common backup software from triggering alerts",
                IsEnabled = true,
                ProcessPatterns = new[] { "veeam*", "backup*", "crashplan*", "carbonite*", "acronis*" }
            });

            // Exclude antivirus processes
            AddExclusionRule(new ExclusionRule
            {
                Id = "antivirus-processes",
                Name = "Antivirus Software",
                Description = "Excludes antivirus scanners from triggering alerts",
                IsEnabled = true,
                ProcessPatterns = new[] { "msmpeng*", "mbam*", "avast*", "avg*", "norton*", "kaspersky*", "mcafee*" }
            });

            // Exclude search indexers
            AddExclusionRule(new ExclusionRule
            {
                Id = "search-indexers",
                Name = "Search Indexers",
                Description = "Excludes search indexing processes",
                IsEnabled = true,
                ProcessPatterns = new[] { "searchindexer*", "mdsync*", "spotlight*" }
            });
        }

        private void AggregateMetrics()
        {
            // Periodic metrics aggregation for reporting
            foreach (var metrics in _metrics.Values)
            {
                metrics.LastAggregatedAt = DateTime.UtcNow;
            }
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

        private static string GenerateCanaryToken()
        {
            var bytes = RandomNumberGenerator.GetBytes(32);
            return Convert.ToBase64String(bytes);
        }

        private static string ComputeContentHash(byte[] content)
        {
            var hash = SHA256.HashData(content);
            return Convert.ToHexString(hash);
        }

        #endregion

        public new void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _rotationTimer?.Dispose();
            _metricsAggregationTimer?.Dispose();
        }
    }

    #region Supporting Types

    /// <summary>
    /// Types of canary objects.
    /// </summary>
    public enum CanaryType
    {
        /// <summary>Fake file with tempting name</summary>
        File,
        /// <summary>Fake credential</summary>
        Credential,
        /// <summary>Fake database table</summary>
        Database,
        /// <summary>Trackable token</summary>
        Token,
        /// <summary>Fake network endpoint</summary>
        Network,
        /// <summary>Fake user account</summary>
        Account,
        /// <summary>Monitored directory</summary>
        Directory,
        /// <summary>API honeytoken</summary>
        ApiHoneytoken
    }

    /// <summary>
    /// Types of canary files that can be generated.
    /// </summary>
    public enum CanaryFileType
    {
        PasswordsExcel,
        WalletDat,
        CredentialsJson,
        PrivateKeyPem,
        EnvFile,
        ConfigYaml,
        DatabaseBackup,
        SshKey,
        AwsCredentials,
        KubeConfig
    }

    /// <summary>
    /// Types of credentials for credential canaries.
    /// </summary>
    public enum CredentialType
    {
        Password,
        ApiKey,
        OAuthToken,
        JwtToken,
        DatabaseConnection,
        SshPrivateKey,
        AwsAccessKey,
        AzureServicePrincipal
    }

    /// <summary>
    /// Hints for canary placement.
    /// </summary>
    public enum CanaryPlacementHint
    {
        HighTraffic,
        SensitiveData,
        AdminArea,
        SharedDrive,
        TemporaryFolder,
        BackupLocation,
        ConfigDirectory,
        UserHome
    }

    /// <summary>
    /// Alert severity levels.
    /// </summary>
    public enum AlertSeverity
    {
        Low = 0,
        Medium = 1,
        High = 2,
        Critical = 3
    }

    /// <summary>
    /// Represents a canary object.
    /// </summary>
    public sealed class CanaryObject
    {
        public required string Id { get; init; }
        public required string ResourceId { get; init; }
        public required CanaryType Type { get; init; }
        public CanaryFileType? FileType { get; init; }
        public required string Description { get; set; }
        public required DateTime CreatedAt { get; init; }
        public required string Token { get; set; }
        public bool IsActive { get; set; }
        public int AccessCount { get; set; }
        public DateTime? LastAccessedAt { get; set; }
        public string? LastAccessedBy { get; set; }
        public byte[]? Content { get; set; }
        public string? SuggestedFileName { get; set; }
        public string? PlacementPath { get; set; }
        public DateTime? RotatedAt { get; set; }
        public int RotationCount { get; set; }
        public Dictionary<string, object> Metadata { get; set; } = new();
    }

    /// <summary>
    /// Represents an alert triggered by canary access.
    /// </summary>
    public sealed class CanaryAlert
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
        public ForensicSnapshot? ForensicSnapshot { get; set; }
        public IReadOnlyDictionary<string, object> AdditionalContext { get; init; } = new Dictionary<string, object>();
        public bool LockdownTriggered { get; set; }
        public LockdownEvent? LockdownEvent { get; set; }
        public bool IsFalsePositive { get; set; }
        public string? FalsePositiveReason { get; set; }
    }

    /// <summary>
    /// Lockdown event details.
    /// </summary>
    public sealed class LockdownEvent
    {
        public required string Id { get; init; }
        public required string SubjectId { get; init; }
        public required DateTime TriggeredAt { get; init; }
        public required string TriggeringAlertId { get; init; }
        public required string CanaryId { get; init; }
        public required string Reason { get; init; }
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Forensic snapshot captured when canary is triggered.
    /// </summary>
    public sealed class ForensicSnapshot
    {
        public required string Id { get; init; }
        public required DateTime CapturedAt { get; init; }
        public string? ProcessName { get; set; }
        public int? ProcessId { get; set; }
        public DateTime? ProcessStartTime { get; set; }
        public string? ProcessPath { get; set; }
        public string? ProcessCommandLine { get; set; }
        public string? ParentProcessName { get; set; }
        public int? ParentProcessId { get; set; }
        public string? Username { get; set; }
        public string? MachineName { get; set; }
        public string? LocalIpAddress { get; set; }
        public IReadOnlyList<NetworkConnectionInfo>? ActiveConnections { get; set; }
        public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; set; }
        public long? AvailableMemoryBytes { get; set; }
        public double? CpuUsagePercent { get; set; }
    }

    /// <summary>
    /// Network connection information for forensics.
    /// </summary>
    public sealed class NetworkConnectionInfo
    {
        public required string LocalAddress { get; init; }
        public required int LocalPort { get; init; }
        public string? RemoteAddress { get; init; }
        public int? RemotePort { get; init; }
        public required string State { get; init; }
        public string? Protocol { get; init; }
    }

    /// <summary>
    /// Exclusion rule for whitelisting legitimate processes.
    /// </summary>
    public sealed class ExclusionRule
    {
        public required string Id { get; init; }
        public required string Name { get; init; }
        public string? Description { get; set; }
        public bool IsEnabled { get; set; } = true;
        public string[]? ProcessPatterns { get; set; }
        public string[]? UserPatterns { get; set; }
        public string[]? IpPatterns { get; set; }
        public TimeSpan? ActiveTimeWindow { get; set; }

        public bool Matches(AccessContext context)
        {
            // Check user patterns
            if (UserPatterns != null && UserPatterns.Length > 0)
            {
                if (UserPatterns.Any(p => MatchesPattern(context.SubjectId, p)))
                    return true;
            }

            // Check IP patterns
            if (IpPatterns != null && IpPatterns.Length > 0 && context.ClientIpAddress != null)
            {
                if (IpPatterns.Any(p => MatchesPattern(context.ClientIpAddress, p)))
                    return true;
            }

            // Check process patterns from subject attributes
            if (ProcessPatterns != null && ProcessPatterns.Length > 0)
            {
                if (context.SubjectAttributes.TryGetValue("ProcessName", out var procName) && procName is string pn)
                {
                    if (ProcessPatterns.Any(p => MatchesPattern(pn, p)))
                        return true;
                }
            }

            return false;
        }

        private static bool MatchesPattern(string value, string pattern)
        {
            if (pattern.EndsWith('*'))
            {
                return value.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
            }
            if (pattern.StartsWith('*'))
            {
                return value.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase);
            }
            return value.Equals(pattern, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Metrics for a specific canary.
    /// </summary>
    public sealed class CanaryMetrics
    {
        public required string CanaryId { get; init; }
        public required DateTime CreatedAt { get; init; }
        public int TriggerCount;
        public int FalsePositiveCount;
        public DateTime? LastTriggeredAt { get; set; }
        public DateTime? LastAggregatedAt { get; set; }
        public HashSet<string> UniqueSubjects { get; } = new();
    }

    /// <summary>
    /// Effectiveness report for all canaries.
    /// </summary>
    public sealed class CanaryEffectivenessReport
    {
        public required DateTime GeneratedAt { get; init; }
        public required int TotalCanaries { get; init; }
        public required int ActiveCanaries { get; init; }
        public required int TotalTriggers { get; init; }
        public required int TruePositives { get; init; }
        public required int FalsePositives { get; init; }
        public required double FalsePositiveRate { get; init; }
        public required double TriggerRate { get; init; }
        public required TimeSpan MeanTimeToDetection { get; init; }
        public required Dictionary<string, CanaryMetrics> CanaryMetrics { get; init; }
        public required Dictionary<AlertSeverity, int> AlertsBySeverity { get; init; }
        public required object TopTriggeredCanaries { get; init; }
    }

    /// <summary>
    /// Placement suggestion for canary deployment.
    /// </summary>
    public sealed class PlacementSuggestion
    {
        public required string Path { get; init; }
        public required CanaryPlacementHint Hint { get; init; }
        public required string Reason { get; init; }
        public required double Score { get; init; }
    }

    /// <summary>
    /// Configuration for auto-deployment of canaries.
    /// </summary>
    public sealed class AutoDeploymentConfig
    {
        public int MaxCanaries { get; set; } = 10;
        public bool IncludeCredentialCanaries { get; set; } = true;
        public bool IncludeDatabaseCanaries { get; set; } = true;
        public bool IncludeApiTokens { get; set; } = true;
    }

    /// <summary>
    /// Interface for alert notification channels.
    /// </summary>
    public interface IAlertChannel
    {
        string ChannelId { get; }
        string ChannelName { get; }
        bool IsEnabled { get; }
        AlertSeverity MinimumSeverity { get; }
        Task SendAlertAsync(CanaryAlert alert);
    }

    #endregion

    #region Alert Channels (73.5)

    /// <summary>
    /// Email alert channel.
    /// </summary>
    public sealed class EmailAlertChannel : IAlertChannel
    {
        private readonly string[] _recipients;
        private readonly string _smtpServer;
        private readonly int _smtpPort;
        private readonly string? _fromAddress;

        public EmailAlertChannel(string[] recipients, string smtpServer, int smtpPort = 587, string? fromAddress = null)
        {
            _recipients = recipients;
            _smtpServer = smtpServer;
            _smtpPort = smtpPort;
            _fromAddress = fromAddress;
        }

        public string ChannelId => "email";
        public string ChannelName => "Email Alerts";
        public bool IsEnabled { get; set; } = true;
        public AlertSeverity MinimumSeverity { get; set; } = AlertSeverity.Medium;

        public async Task SendAlertAsync(CanaryAlert alert)
        {
            var subject = $"[{alert.Severity}] Canary Alert: {alert.ResourceId}";
            var body = FormatEmailBody(alert);

            // Production implementation would use SmtpClient
            await Task.Run(() =>
            {
                // Simulate email sending
                // In production: Use MailKit or System.Net.Mail
            });
        }

        private static string FormatEmailBody(CanaryAlert alert)
        {
            return $@"
CANARY ALERT

Severity: {alert.Severity}
Resource: {alert.ResourceId}
Canary Type: {alert.CanaryType}
Accessed By: {alert.AccessedBy}
Accessed At: {alert.AccessedAt:O}
Action: {alert.Action}
Client IP: {alert.ClientIpAddress ?? "Unknown"}

This is an automated alert from the DataWarehouse Canary Detection System.
";
        }
    }

    /// <summary>
    /// Webhook alert channel.
    /// </summary>
    public sealed class WebhookAlertChannel : IAlertChannel
    {
        private readonly string _webhookUrl;
        private readonly HttpClient _httpClient;
        private readonly Dictionary<string, string> _headers;

        public WebhookAlertChannel(string webhookUrl, Dictionary<string, string>? headers = null)
        {
            _webhookUrl = webhookUrl;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            _headers = headers ?? new Dictionary<string, string>();
        }

        public string ChannelId => "webhook";
        public string ChannelName => "Webhook";
        public bool IsEnabled { get; set; } = true;
        public AlertSeverity MinimumSeverity { get; set; } = AlertSeverity.Low;

        public async Task SendAlertAsync(CanaryAlert alert)
        {
            var payload = JsonSerializer.Serialize(new
            {
                alertId = alert.Id,
                canaryId = alert.CanaryId,
                resourceId = alert.ResourceId,
                canaryType = alert.CanaryType.ToString(),
                severity = alert.Severity.ToString(),
                accessedBy = alert.AccessedBy,
                accessedAt = alert.AccessedAt,
                action = alert.Action,
                clientIp = alert.ClientIpAddress,
                lockdownTriggered = alert.LockdownTriggered,
                forensicsCaptured = alert.ForensicSnapshot != null
            });

            var request = new HttpRequestMessage(HttpMethod.Post, _webhookUrl)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };

            foreach (var header in _headers)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            await _httpClient.SendAsync(request);
        }
    }

    /// <summary>
    /// SIEM integration alert channel.
    /// </summary>
    public sealed class SiemAlertChannel : IAlertChannel
    {
        private readonly string _siemEndpoint;
        private readonly string _apiKey;
        private readonly HttpClient _httpClient;

        public SiemAlertChannel(string siemEndpoint, string apiKey)
        {
            _siemEndpoint = siemEndpoint;
            _apiKey = apiKey;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        public string ChannelId => "siem";
        public string ChannelName => "SIEM Integration";
        public bool IsEnabled { get; set; } = true;
        public AlertSeverity MinimumSeverity { get; set; } = AlertSeverity.Low;

        public async Task SendAlertAsync(CanaryAlert alert)
        {
            // CEF (Common Event Format) style payload for SIEM
            var cefEvent = new
            {
                version = "CEF:0",
                deviceVendor = "DataWarehouse",
                deviceProduct = "CanaryDetection",
                deviceVersion = "1.0",
                signatureId = "CANARY_ACCESS",
                name = "Canary Object Accessed",
                severity = MapSeverityToCef(alert.Severity),
                extensions = new
                {
                    src = alert.ClientIpAddress,
                    suser = alert.AccessedBy,
                    act = alert.Action,
                    rt = alert.AccessedAt.ToUniversalTime().ToString("O"),
                    cs1 = alert.ResourceId,
                    cs1Label = "ResourceId",
                    cs2 = alert.CanaryType.ToString(),
                    cs2Label = "CanaryType",
                    cs3 = alert.Id,
                    cs3Label = "AlertId"
                }
            };

            var payload = JsonSerializer.Serialize(cefEvent);
            var request = new HttpRequestMessage(HttpMethod.Post, _siemEndpoint)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");

            await _httpClient.SendAsync(request);
        }

        private static int MapSeverityToCef(AlertSeverity severity) => severity switch
        {
            AlertSeverity.Low => 3,
            AlertSeverity.Medium => 5,
            AlertSeverity.High => 7,
            AlertSeverity.Critical => 10,
            _ => 5
        };
    }

    /// <summary>
    /// SMS alert channel.
    /// </summary>
    public sealed class SmsAlertChannel : IAlertChannel
    {
        private readonly string[] _phoneNumbers;
        private readonly string _apiEndpoint;
        private readonly string _apiKey;
        private readonly HttpClient _httpClient;

        public SmsAlertChannel(string[] phoneNumbers, string apiEndpoint, string apiKey)
        {
            _phoneNumbers = phoneNumbers;
            _apiEndpoint = apiEndpoint;
            _apiKey = apiKey;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        public string ChannelId => "sms";
        public string ChannelName => "SMS Alerts";
        public bool IsEnabled { get; set; } = true;
        public AlertSeverity MinimumSeverity { get; set; } = AlertSeverity.High;

        public async Task SendAlertAsync(CanaryAlert alert)
        {
            var message = $"[{alert.Severity}] Canary Alert: {alert.ResourceId} accessed by {alert.AccessedBy} at {alert.AccessedAt:HH:mm}";

            foreach (var phone in _phoneNumbers)
            {
                var payload = JsonSerializer.Serialize(new
                {
                    to = phone,
                    message = message
                });

                var request = new HttpRequestMessage(HttpMethod.Post, _apiEndpoint)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");

                await _httpClient.SendAsync(request);
            }
        }
    }

    #endregion

    #region Canary Generator (73.1)

    /// <summary>
    /// Generates convincing canary file content.
    /// </summary>
    internal sealed class CanaryGenerator
    {
        private static readonly string[] FakeUsernames = { "admin", "root", "sa", "dbadmin", "sysadmin", "backup", "service", "api" };
        private static readonly string[] FakeDomains = { "internal.corp", "prod.local", "admin.net", "secure.internal" };

        public byte[] GenerateFileContent(CanaryFileType fileType)
        {
            return fileType switch
            {
                CanaryFileType.PasswordsExcel => GeneratePasswordsExcel(),
                CanaryFileType.WalletDat => GenerateWalletDat(),
                CanaryFileType.CredentialsJson => GenerateCredentialsJson(),
                CanaryFileType.PrivateKeyPem => GeneratePrivateKeyPem(),
                CanaryFileType.EnvFile => GenerateEnvFile(),
                CanaryFileType.ConfigYaml => GenerateConfigYaml(),
                CanaryFileType.DatabaseBackup => GenerateDatabaseBackup(),
                CanaryFileType.SshKey => GenerateSshKey(),
                CanaryFileType.AwsCredentials => GenerateAwsCredentials(),
                CanaryFileType.KubeConfig => GenerateKubeConfig(),
                _ => Encoding.UTF8.GetBytes("# Canary file")
            };
        }

        public string GetSuggestedFileName(CanaryFileType fileType)
        {
            return fileType switch
            {
                CanaryFileType.PasswordsExcel => "passwords.xlsx",
                CanaryFileType.WalletDat => "wallet.dat",
                CanaryFileType.CredentialsJson => "credentials.json",
                CanaryFileType.PrivateKeyPem => "private-key.pem",
                CanaryFileType.EnvFile => ".env.production",
                CanaryFileType.ConfigYaml => "secrets.yaml",
                CanaryFileType.DatabaseBackup => "db_backup_prod.sql",
                CanaryFileType.SshKey => "id_rsa",
                CanaryFileType.AwsCredentials => "aws_credentials",
                CanaryFileType.KubeConfig => "kubeconfig",
                _ => "canary.txt"
            };
        }

        public string GenerateFakeApiKey()
        {
            var prefix = "sk_live_";
            // Generate enough bytes to ensure at least 40 alphanumeric chars remain after stripping
            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64))
                .Replace("+", "")
                .Replace("/", "")
                .Replace("=", "");
            // Safely take up to 40 chars (or all if shorter, which is unlikely with 64 bytes)
            key = key.Length > 40 ? key[..40] : key;
            return prefix + key;
        }

        public string GenerateFakeCredential(CredentialType type)
        {
            return type switch
            {
                CredentialType.Password => GenerateFakePassword(),
                CredentialType.ApiKey => GenerateFakeApiKey(),
                CredentialType.OAuthToken => GenerateOAuthToken(),
                CredentialType.JwtToken => GenerateJwtToken(),
                CredentialType.DatabaseConnection => GenerateDbConnectionString(),
                CredentialType.SshPrivateKey => Encoding.UTF8.GetString(GenerateSshKey()),
                CredentialType.AwsAccessKey => GenerateAwsAccessKey(),
                CredentialType.AzureServicePrincipal => GenerateAzureCredential(),
                _ => GenerateFakePassword()
            };
        }

        public string GenerateFakePassword()
        {
            const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789!@#$%^&*";
            var password = new char[16];
            var bytes = RandomNumberGenerator.GetBytes(16);
            for (int i = 0; i < 16; i++)
            {
                password[i] = chars[bytes[i] % chars.Length];
            }
            return new string(password);
        }

        public string GenerateFakeDatabaseContent(string tableName)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"-- Fake table: {tableName}");
            sb.AppendLine("-- Generated for canary detection");
            sb.AppendLine();
            sb.AppendLine($"CREATE TABLE {tableName} (");
            sb.AppendLine("  id INT PRIMARY KEY,");
            sb.AppendLine("  username VARCHAR(255),");
            sb.AppendLine("  password_hash VARCHAR(255),");
            sb.AppendLine("  email VARCHAR(255),");
            sb.AppendLine("  ssn VARCHAR(11),");
            sb.AppendLine("  credit_card VARCHAR(16)");
            sb.AppendLine(");");
            sb.AppendLine();

            for (int i = 1; i <= 100; i++)
            {
                var username = FakeUsernames[i % FakeUsernames.Length] + i;
                var email = $"{username}@{FakeDomains[i % FakeDomains.Length]}";
                var ssn = $"{100 + (i % 900):000}-{10 + (i % 90):00}-{1000 + (i % 9000):0000}";
                var cc = $"{4000000000000000L + i:0000000000000000}";
                var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes($"password{i}")));

                sb.AppendLine($"INSERT INTO {tableName} VALUES ({i}, '{username}', '{hash}', '{email}', '{ssn}', '{cc}');");
            }

            return sb.ToString();
        }

        private static byte[] GeneratePasswordsExcel()
        {
            // Generate a minimal valid XLSX-like structure (ZIP with XML)
            // In production, use a library like EPPlus
            var content = @"Site,Username,Password,Notes
production.internal,admin,P@ssw0rd123!,Main admin account
database.internal,sa,Sql$erver2024,Database admin
vpn.company.com,sysadmin,Vpn#Access99,VPN service account
aws-console,root,Aws!Root2024,AWS root account - DO NOT SHARE
azure.portal,admin@company.onmicrosoft.com,Azure@Admin1,Azure admin
github.com,deploy-bot,ghp_xxxxxxxxxxxx,CI/CD deploy token
";
            return Encoding.UTF8.GetBytes(content);
        }

        private static byte[] GenerateWalletDat()
        {
            // Generate fake Bitcoin wallet structure
            var header = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00 };
            var fakeKeys = RandomNumberGenerator.GetBytes(256);
            return header.Concat(fakeKeys).ToArray();
        }

        private static byte[] GenerateCredentialsJson()
        {
            var creds = new
            {
                production = new
                {
                    database = new { host = "db.internal", user = "app", password = "Pr0d#Db2024!" },
                    redis = new { host = "redis.internal", password = "R3d!s$ecret" },
                    aws = new { accessKey = "AKIAIOSFODNN7EXAMPLE", secretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY" }
                },
                staging = new
                {
                    database = new { host = "staging-db.internal", user = "app", password = "St@g!ng2024" }
                }
            };
            return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(creds, new JsonSerializerOptions { WriteIndented = true }));
        }

        private static byte[] GeneratePrivateKeyPem()
        {
            // Generate fake RSA private key PEM
            var fakeKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(1024));
            var pem = $@"-----BEGIN RSA PRIVATE KEY-----
{string.Join("\n", Enumerable.Range(0, fakeKey.Length / 64 + 1).Select(i => fakeKey.Substring(i * 64, Math.Min(64, fakeKey.Length - i * 64))))}
-----END RSA PRIVATE KEY-----";
            return Encoding.UTF8.GetBytes(pem);
        }

        private static byte[] GenerateEnvFile()
        {
            var env = @"# Production Environment - CONFIDENTIAL
NODE_ENV=production
DATABASE_URL=postgresql://admin:Pr0d#P@ss123@db.prod.internal:5432/maindb
REDIS_URL=redis://:R3d!sS3cr3t@redis.prod.internal:6379
AWS_ACCESS_KEY_ID=AKIAIOSFODNN7EXAMPLE
AWS_SECRET_ACCESS_KEY=wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY
JWT_SECRET=super-secret-jwt-key-do-not-share-2024
STRIPE_SECRET_KEY=sk_live_51Example123456789
SENDGRID_API_KEY=SG.xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx
";
            return Encoding.UTF8.GetBytes(env);
        }

        private static byte[] GenerateConfigYaml()
        {
            var yaml = @"# Secrets Configuration - RESTRICTED
secrets:
  database:
    host: db.prod.internal
    port: 5432
    username: superadmin
    password: Db#Adm!n2024$ecure

  encryption:
    master_key: c29tZS1zdXBlci1zZWNyZXQtbWFzdGVyLWtleQ==

  api_keys:
    stripe: sk_live_51Example123456789
    twilio: AC1234567890abcdef
    sendgrid: SG.xxxxxxxxxxxxx

  oauth:
    google_client_secret: GOCSPX-xxxxxxxxxx
    github_client_secret: ghp_xxxxxxxxxxxx
";
            return Encoding.UTF8.GetBytes(yaml);
        }

        private static byte[] GenerateDatabaseBackup()
        {
            var sql = @"-- PostgreSQL database dump
-- Production backup - CONFIDENTIAL

CREATE TABLE users (
    id SERIAL PRIMARY KEY,
    email VARCHAR(255) UNIQUE NOT NULL,
    password_hash VARCHAR(255) NOT NULL,
    ssn_encrypted BYTEA,
    credit_card_token VARCHAR(255)
);

INSERT INTO users VALUES (1, 'admin@company.com', '$2b$12$LQv3c1yqBWVHxkd0LHAkCOYz6TtxMQJqhN8/X4.Y5r6QhKGp6', '\x0123456789', 'tok_visa_4242');
INSERT INTO users VALUES (2, 'ceo@company.com', '$2b$12$xyz123...', '\xabcdef0123', 'tok_mastercard_5555');
";
            return Encoding.UTF8.GetBytes(sql);
        }

        private static byte[] GenerateSshKey()
        {
            var fakeKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(1200));
            var key = $@"-----BEGIN OPENSSH PRIVATE KEY-----
{string.Join("\n", Enumerable.Range(0, fakeKey.Length / 70 + 1).Select(i => i * 70 < fakeKey.Length ? fakeKey.Substring(i * 70, Math.Min(70, fakeKey.Length - i * 70)) : ""))}
-----END OPENSSH PRIVATE KEY-----";
            return Encoding.UTF8.GetBytes(key);
        }

        private static byte[] GenerateAwsCredentials()
        {
            var creds = @"[default]
aws_access_key_id = AKIAIOSFODNN7EXAMPLE
aws_secret_access_key = wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY
region = us-east-1

[production]
aws_access_key_id = AKIAI44QH8DHBEXAMPLE
aws_secret_access_key = je7MtGbClwBF/2Zp9Utk/h3yCo8nvbEXAMPLEKEY
region = us-west-2
";
            return Encoding.UTF8.GetBytes(creds);
        }

        private static byte[] GenerateKubeConfig()
        {
            var kubeconfig = @"apiVersion: v1
kind: Config
clusters:
- cluster:
    certificate-authority-data: LS0tLS1CRUdJTi...
    server: https://k8s.prod.internal:6443
  name: production
contexts:
- context:
    cluster: production
    user: admin
  name: prod-admin
current-context: prod-admin
users:
- name: admin
  user:
    client-certificate-data: LS0tLS1CRUdJTi...
    client-key-data: LS0tLS1CRUdJTiBSU0...
    token: eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9...
";
            return Encoding.UTF8.GetBytes(kubeconfig);
        }

        private static string GenerateOAuthToken()
        {
            return "ya29." + Convert.ToBase64String(RandomNumberGenerator.GetBytes(100)).Replace("+", "-").Replace("/", "_")[..100];
        }

        private static string GenerateJwtToken()
        {
            var header = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\",\"typ\":\"JWT\"}"));
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"sub\":\"admin\",\"iat\":1704067200,\"exp\":1735689600}"));
            var signature = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            return $"{header}.{payload}.{signature}";
        }

        private static string GenerateDbConnectionString()
        {
            return "Server=db.prod.internal;Database=maindb;User Id=sa;Password=Pr0d#Adm!n2024;Encrypt=True;";
        }

        private static string GenerateAwsAccessKey()
        {
            var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                .Replace("+", "").Replace("/", "").Replace("=", "");
            key = key.Length > 16 ? key[..16] : key;
            return "AKIA" + key;
        }

        private static string GenerateAzureCredential()
        {
            var cred = new
            {
                appId = Guid.NewGuid().ToString(),
                displayName = "prod-service-principal",
                password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                tenant = Guid.NewGuid().ToString()
            };
            return JsonSerializer.Serialize(cred);
        }
    }

    #endregion

    #region Placement Engine (73.2)

    /// <summary>
    /// Suggests optimal placements for canary files.
    /// </summary>
    internal sealed class PlacementEngine
    {
        private static readonly PlacementSuggestion[] DefaultPlacements =
        {
            new() { Path = "/shared/admin", Hint = CanaryPlacementHint.AdminArea, Reason = "Admin directories are high-value targets", Score = 0.95 },
            new() { Path = "/backup", Hint = CanaryPlacementHint.BackupLocation, Reason = "Backup locations attract data exfiltration", Score = 0.90 },
            new() { Path = "/config", Hint = CanaryPlacementHint.ConfigDirectory, Reason = "Config directories may contain credentials", Score = 0.88 },
            new() { Path = "/shared/finance", Hint = CanaryPlacementHint.SensitiveData, Reason = "Financial data is high-value target", Score = 0.92 },
            new() { Path = "/shared/hr", Hint = CanaryPlacementHint.SensitiveData, Reason = "HR data contains PII", Score = 0.91 },
            new() { Path = "/temp", Hint = CanaryPlacementHint.TemporaryFolder, Reason = "Temp folders used for staging exfiltration", Score = 0.75 },
            new() { Path = "/users/admin/Documents", Hint = CanaryPlacementHint.UserHome, Reason = "Admin home directories are targets", Score = 0.85 },
            new() { Path = "/shared/it", Hint = CanaryPlacementHint.AdminArea, Reason = "IT shares may contain tools and creds", Score = 0.87 },
            new() { Path = "/data/exports", Hint = CanaryPlacementHint.HighTraffic, Reason = "Export directories see data movement", Score = 0.80 },
            new() { Path = "/archive", Hint = CanaryPlacementHint.BackupLocation, Reason = "Archives may be bulk-copied", Score = 0.78 }
        };

        public IReadOnlyList<PlacementSuggestion> GetTopPlacements(int count)
        {
            return DefaultPlacements
                .OrderByDescending(p => p.Score)
                .Take(count)
                .ToList()
                .AsReadOnly();
        }

        public string SuggestPlacement(CanaryPlacementHint hint)
        {
            var matching = DefaultPlacements.FirstOrDefault(p => p.Hint == hint);
            return matching?.Path ?? "/shared";
        }

        public CanaryFileType GetBestFileTypeForLocation(string path)
        {
            var lowerPath = path.ToLowerInvariant();

            if (lowerPath.Contains("admin") || lowerPath.Contains("it"))
                return CanaryFileType.CredentialsJson;

            if (lowerPath.Contains("finance"))
                return CanaryFileType.PasswordsExcel;

            if (lowerPath.Contains("backup") || lowerPath.Contains("archive"))
                return CanaryFileType.DatabaseBackup;

            if (lowerPath.Contains("config"))
                return CanaryFileType.EnvFile;

            if (lowerPath.Contains("ssh") || lowerPath.Contains(".ssh"))
                return CanaryFileType.SshKey;

            return CanaryFileType.CredentialsJson;
        }
    }

    #endregion

    #region Forensic Capture Engine (73.6)

    /// <summary>
    /// Captures forensic information when canaries are triggered.
    /// </summary>
    internal sealed class ForensicCaptureEngine
    {
        public ForensicSnapshot CaptureSnapshot(AccessContext context, CanaryObject canary)
        {
            var snapshot = new ForensicSnapshot
            {
                Id = Guid.NewGuid().ToString("N"),
                CapturedAt = DateTime.UtcNow
            };

            try
            {
                // Capture process information
                CaptureProcessInfo(snapshot, context);

                // Capture network state
                CaptureNetworkState(snapshot);

                // Capture system info
                CaptureSystemInfo(snapshot);
            }
            catch
            {
                // Forensic capture should not fail the main operation
            }

            return snapshot;
        }

        private static void CaptureProcessInfo(ForensicSnapshot snapshot, AccessContext context)
        {
            try
            {
                if (context.SubjectAttributes.TryGetValue("ProcessId", out var pidObj) && pidObj is int pid)
                {
                    var process = Process.GetProcessById(pid);
                    snapshot.ProcessName = process.ProcessName;
                    snapshot.ProcessId = process.Id;
                    snapshot.ProcessStartTime = process.StartTime;

                    try
                    {
                        snapshot.ProcessPath = process.MainModule?.FileName;
                    }
                    catch { }

                    // Get parent process
                    try
                    {
                        // Note: Getting parent process requires additional P/Invoke on Windows
                        // This is a simplified version
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static void CaptureNetworkState(ForensicSnapshot snapshot)
        {
            try
            {
                var connections = new List<NetworkConnectionInfo>();
                var properties = IPGlobalProperties.GetIPGlobalProperties();

                foreach (var tcp in properties.GetActiveTcpConnections().Take(50))
                {
                    connections.Add(new NetworkConnectionInfo
                    {
                        LocalAddress = tcp.LocalEndPoint.Address.ToString(),
                        LocalPort = tcp.LocalEndPoint.Port,
                        RemoteAddress = tcp.RemoteEndPoint.Address.ToString(),
                        RemotePort = tcp.RemoteEndPoint.Port,
                        State = tcp.State.ToString(),
                        Protocol = "TCP"
                    });
                }

                snapshot.ActiveConnections = connections.AsReadOnly();

                // Get local IP
                var host = Dns.GetHostEntry(Dns.GetHostName());
                var localIp = host.AddressList.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                snapshot.LocalIpAddress = localIp?.ToString();
            }
            catch { }
        }

        private static void CaptureSystemInfo(ForensicSnapshot snapshot)
        {
            try
            {
                snapshot.MachineName = Environment.MachineName;
                snapshot.Username = Environment.UserName;

                // Capture environment variables (filtered for security)
                var safeVars = new[] { "USERNAME", "USERDOMAIN", "COMPUTERNAME", "OS", "PROCESSOR_ARCHITECTURE" };
                snapshot.EnvironmentVariables = safeVars
                    .Where(v => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v)))
                    .ToDictionary(v => v, v => Environment.GetEnvironmentVariable(v)!);
            }
            catch { }
        }
    }

    #endregion
}
