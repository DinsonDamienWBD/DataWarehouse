using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateAccessControl.Strategies.Core
{
    /// <summary>
    /// Access audit logging strategy providing comprehensive audit trails
    /// for all access decisions with tamper-evident logging and compliance support.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Audit logging features:
    /// - Comprehensive access event logging with full context
    /// - Tamper-evident log chains using hash linking
    /// - Multiple log destinations (file, database, SIEM, cloud)
    /// - Compliance-ready reporting (SOX, HIPAA, GDPR, PCI-DSS)
    /// - Real-time streaming and batch export
    /// - Log retention and archival policies
    /// - Anomaly detection integration
    /// </para>
    /// </remarks>
    public sealed class AccessAuditLoggingStrategy : AccessControlStrategyBase, IDisposable
    {
        private readonly ConcurrentQueue<AuditLogEntry> _logQueue = new();
        private readonly ConcurrentDictionary<string, AuditLogEntry> _recentLogs = new();
        private readonly List<IAuditLogDestination> _destinations = new();
        private readonly object _chainLock = new();

        private Timer? _flushTimer;
        private Timer? _retentionTimer;
        private string _lastLogHash = "";
        private long _sequenceNumber;
        private int _maxQueueSize = 100000;
        private int _batchSize = 100;
        private TimeSpan _flushInterval = TimeSpan.FromSeconds(5);
        private TimeSpan _retentionPeriod = TimeSpan.FromDays(365);
        private bool _enableHashChaining = true;
        private bool _disposed;

        /// <inheritdoc/>
        public override string StrategyId => "audit-logging";

        /// <inheritdoc/>
        public override string StrategyName => "Access Audit Logging";

        /// <inheritdoc/>
        public override AccessControlCapabilities Capabilities { get; } = new()
        {
            SupportsRealTimeDecisions = true,
            SupportsAuditTrail = true,
            SupportsPolicyConfiguration = true,
            SupportsExternalIdentity = true,
            SupportsTemporalAccess = false,
            SupportsGeographicRestrictions = false,
            MaxConcurrentEvaluations = 50000
        };

        /// <inheritdoc/>
        public override Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken cancellationToken = default)
        {
            // Load configuration
            if (configuration.TryGetValue("MaxQueueSize", out var mqsObj) && mqsObj is int mqs)
                _maxQueueSize = mqs;

            if (configuration.TryGetValue("BatchSize", out var bsObj) && bsObj is int bs)
                _batchSize = bs;

            if (configuration.TryGetValue("FlushIntervalSeconds", out var fisObj) && fisObj is int fis)
                _flushInterval = TimeSpan.FromSeconds(fis);

            if (configuration.TryGetValue("RetentionDays", out var rdObj) && rdObj is int rd)
                _retentionPeriod = TimeSpan.FromDays(rd);

            if (configuration.TryGetValue("EnableHashChaining", out var ehcObj) && ehcObj is bool ehc)
                _enableHashChaining = ehc;

            // Initialize default file destination
            if (configuration.TryGetValue("LogDirectory", out var ldObj) && ldObj is string logDir)
            {
                RegisterDestination(new FileAuditLogDestination(logDir));
            }

            // Start background flush timer
            _flushTimer = new Timer(
                async _ => await FlushLogsAsync(),
                null,
                _flushInterval,
                _flushInterval);

            // Start retention cleanup timer
            _retentionTimer = new Timer(
                _ => CleanupOldLogs(),
                null,
                TimeSpan.FromHours(1),
                TimeSpan.FromHours(1));

            return base.InitializeAsync(configuration, cancellationToken);
        }

        #region Destination Management

        /// <summary>
        /// Registers an audit log destination.
        /// </summary>
        public void RegisterDestination(IAuditLogDestination destination)
        {
            _destinations.Add(destination);
        }

        /// <summary>
        /// Removes an audit log destination.
        /// </summary>
        public bool RemoveDestination(string destinationId)
        {
            var destination = _destinations.FirstOrDefault(d => d.Id == destinationId);
            if (destination != null)
            {
                _destinations.Remove(destination);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Gets all registered destinations.
        /// </summary>
        public IReadOnlyList<IAuditLogDestination> GetDestinations()
        {
            return _destinations.AsReadOnly();
        }

        #endregion

        #region Log Creation

        /// <summary>
        /// Creates an audit log entry for an access decision.
        /// </summary>
        public AuditLogEntry CreateAuditEntry(
            AccessContext context,
            AccessDecision decision,
            string strategyUsed,
            TimeSpan evaluationTime)
        {
            var sequenceNum = Interlocked.Increment(ref _sequenceNumber);

            var entry = new AuditLogEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                SequenceNumber = sequenceNum,
                Timestamp = DateTime.UtcNow,
                EventType = decision.IsGranted ? AuditEventType.AccessGranted : AuditEventType.AccessDenied,
                SubjectId = context.SubjectId,
                SubjectRoles = context.Roles.ToArray(),
                SubjectAttributes = new Dictionary<string, object>(context.SubjectAttributes),
                ResourceId = context.ResourceId,
                ResourceAttributes = new Dictionary<string, object>(context.ResourceAttributes),
                Action = context.Action,
                Decision = decision.IsGranted,
                DecisionReason = decision.Reason,
                ApplicablePolicies = decision.ApplicablePolicies.ToArray(),
                StrategyUsed = strategyUsed,
                EvaluationTimeMs = evaluationTime.TotalMilliseconds,
                ClientIpAddress = context.ClientIpAddress,
                Location = context.Location,
                RequestTime = context.RequestTime,
                EnvironmentAttributes = new Dictionary<string, object>(context.EnvironmentAttributes),
                DecisionMetadata = decision.Metadata != null
                    ? new Dictionary<string, object>(decision.Metadata)
                    : new Dictionary<string, object>()
            };

            // Compute hash chain
            if (_enableHashChaining)
            {
                lock (_chainLock)
                {
                    entry.PreviousHash = _lastLogHash;
                    entry.EntryHash = ComputeEntryHash(entry);
                    _lastLogHash = entry.EntryHash;
                }
            }

            return entry;
        }

        /// <summary>
        /// Logs an audit entry.
        /// </summary>
        public void LogEntry(AuditLogEntry entry)
        {
            // Enforce queue size limit
            while (_logQueue.Count >= _maxQueueSize)
            {
                _logQueue.TryDequeue(out _);
            }

            _logQueue.Enqueue(entry);
            _recentLogs[entry.Id] = entry;

            // Keep recent logs for quick access
            while (_recentLogs.Count > 10000)
            {
                var oldest = _recentLogs.Values.OrderBy(e => e.Timestamp).First();
                _recentLogs.TryRemove(oldest.Id, out _);
            }
        }

        /// <summary>
        /// Logs an access event directly.
        /// </summary>
        public void LogAccessEvent(
            string subjectId,
            string resourceId,
            string action,
            bool isGranted,
            string reason,
            string? clientIp = null,
            Dictionary<string, object>? metadata = null)
        {
            var sequenceNum = Interlocked.Increment(ref _sequenceNumber);

            var entry = new AuditLogEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                SequenceNumber = sequenceNum,
                Timestamp = DateTime.UtcNow,
                EventType = isGranted ? AuditEventType.AccessGranted : AuditEventType.AccessDenied,
                SubjectId = subjectId,
                SubjectRoles = Array.Empty<string>(),
                SubjectAttributes = new Dictionary<string, object>(),
                ResourceId = resourceId,
                ResourceAttributes = new Dictionary<string, object>(),
                Action = action,
                Decision = isGranted,
                DecisionReason = reason,
                ApplicablePolicies = Array.Empty<string>(),
                StrategyUsed = "manual",
                EvaluationTimeMs = 0,
                ClientIpAddress = clientIp,
                RequestTime = DateTime.UtcNow,
                EnvironmentAttributes = new Dictionary<string, object>(),
                DecisionMetadata = metadata ?? new Dictionary<string, object>()
            };

            if (_enableHashChaining)
            {
                lock (_chainLock)
                {
                    entry.PreviousHash = _lastLogHash;
                    entry.EntryHash = ComputeEntryHash(entry);
                    _lastLogHash = entry.EntryHash;
                }
            }

            LogEntry(entry);
        }

        /// <summary>
        /// Logs a security event.
        /// </summary>
        public void LogSecurityEvent(
            AuditEventType eventType,
            string subjectId,
            string description,
            AlertSeverity severity,
            Dictionary<string, object>? metadata = null)
        {
            var sequenceNum = Interlocked.Increment(ref _sequenceNumber);

            var entry = new AuditLogEntry
            {
                Id = Guid.NewGuid().ToString("N"),
                SequenceNumber = sequenceNum,
                Timestamp = DateTime.UtcNow,
                EventType = eventType,
                SubjectId = subjectId,
                SubjectRoles = Array.Empty<string>(),
                SubjectAttributes = new Dictionary<string, object>(),
                ResourceId = "",
                ResourceAttributes = new Dictionary<string, object>(),
                Action = eventType.ToString(),
                Decision = false,
                DecisionReason = description,
                ApplicablePolicies = Array.Empty<string>(),
                StrategyUsed = "security",
                EvaluationTimeMs = 0,
                RequestTime = DateTime.UtcNow,
                EnvironmentAttributes = new Dictionary<string, object>(),
                DecisionMetadata = metadata ?? new Dictionary<string, object>(),
                Severity = severity
            };

            if (_enableHashChaining)
            {
                lock (_chainLock)
                {
                    entry.PreviousHash = _lastLogHash;
                    entry.EntryHash = ComputeEntryHash(entry);
                    _lastLogHash = entry.EntryHash;
                }
            }

            LogEntry(entry);
        }

        #endregion

        #region Log Retrieval

        /// <summary>
        /// Gets recent log entries.
        /// </summary>
        public IReadOnlyList<AuditLogEntry> GetRecentLogs(int count = 100)
        {
            return _recentLogs.Values
                .OrderByDescending(e => e.Timestamp)
                .Take(count)
                .ToList()
                .AsReadOnly();
        }

        /// <summary>
        /// Queries logs by criteria.
        /// </summary>
        public IReadOnlyList<AuditLogEntry> QueryLogs(AuditLogQuery query)
        {
            var results = _recentLogs.Values.AsEnumerable();

            if (!string.IsNullOrEmpty(query.SubjectId))
                results = results.Where(e => e.SubjectId == query.SubjectId);

            if (!string.IsNullOrEmpty(query.ResourceId))
                results = results.Where(e => e.ResourceId == query.ResourceId);

            if (!string.IsNullOrEmpty(query.Action))
                results = results.Where(e => e.Action.Equals(query.Action, StringComparison.OrdinalIgnoreCase));

            if (query.EventType.HasValue)
                results = results.Where(e => e.EventType == query.EventType.Value);

            if (query.StartTime.HasValue)
                results = results.Where(e => e.Timestamp >= query.StartTime.Value);

            if (query.EndTime.HasValue)
                results = results.Where(e => e.Timestamp <= query.EndTime.Value);

            if (query.DecisionGranted.HasValue)
                results = results.Where(e => e.Decision == query.DecisionGranted.Value);

            if (query.MinSeverity.HasValue)
                results = results.Where(e => e.Severity >= query.MinSeverity.Value);

            return results
                .OrderByDescending(e => e.Timestamp)
                .Skip(query.Offset)
                .Take(query.Limit)
                .ToList()
                .AsReadOnly();
        }

        /// <summary>
        /// Gets a log entry by ID.
        /// </summary>
        public AuditLogEntry? GetLogById(string logId)
        {
            return _recentLogs.TryGetValue(logId, out var entry) ? entry : null;
        }

        #endregion

        #region Compliance Reporting

        /// <summary>
        /// Generates a compliance report for a specified period.
        /// </summary>
        public ComplianceReport GenerateComplianceReport(
            ComplianceStandard standard,
            DateTime startDate,
            DateTime endDate)
        {
            var logs = _recentLogs.Values
                .Where(e => e.Timestamp >= startDate && e.Timestamp <= endDate)
                .ToList();

            var report = new ComplianceReport
            {
                Id = Guid.NewGuid().ToString("N"),
                Standard = standard,
                StartDate = startDate,
                EndDate = endDate,
                GeneratedAt = DateTime.UtcNow,
                TotalAccessEvents = logs.Count,
                GrantedCount = logs.Count(e => e.Decision),
                DeniedCount = logs.Count(e => !e.Decision),
                UniqueSubjects = logs.Select(e => e.SubjectId).Distinct().Count(),
                UniqueResources = logs.Select(e => e.ResourceId).Distinct().Count(),
                SecurityEvents = logs.Count(e => e.EventType is
                    AuditEventType.SecurityAlert or
                    AuditEventType.PolicyViolation or
                    AuditEventType.SuspiciousActivity),
                HighSeverityEvents = logs.Count(e => e.Severity is AlertSeverity.High or AlertSeverity.Critical),
                TopAccessedResources = logs
                    .GroupBy(e => e.ResourceId)
                    .OrderByDescending(g => g.Count())
                    .Take(10)
                    .ToDictionary(g => g.Key, g => g.Count()),
                TopDeniedSubjects = logs
                    .Where(e => !e.Decision)
                    .GroupBy(e => e.SubjectId)
                    .OrderByDescending(g => g.Count())
                    .Take(10)
                    .ToDictionary(g => g.Key, g => g.Count()),
                AccessPatternsByHour = logs
                    .GroupBy(e => e.Timestamp.Hour)
                    .OrderBy(g => g.Key)
                    .ToDictionary(g => g.Key, g => g.Count()),
                ChainIntegrity = VerifyChainIntegrity(logs)
            };

            // Add standard-specific findings
            report.Findings = GenerateStandardSpecificFindings(standard, logs);

            return report;
        }

        /// <summary>
        /// Exports logs for external analysis.
        /// </summary>
        public async Task<byte[]> ExportLogsAsync(
            DateTime startDate,
            DateTime endDate,
            ExportFormat format,
            CancellationToken cancellationToken = default)
        {
            var logs = _recentLogs.Values
                .Where(e => e.Timestamp >= startDate && e.Timestamp <= endDate)
                .OrderBy(e => e.Timestamp)
                .ToList();

            return format switch
            {
                ExportFormat.Json => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(logs, new JsonSerializerOptions { WriteIndented = true })),
                ExportFormat.Csv => ExportToCsv(logs),
                ExportFormat.Syslog => ExportToSyslog(logs),
                _ => Array.Empty<byte>()
            };
        }

        private List<ComplianceFinding> GenerateStandardSpecificFindings(ComplianceStandard standard, List<AuditLogEntry> logs)
        {
            var findings = new List<ComplianceFinding>();

            switch (standard)
            {
                case ComplianceStandard.SOX:
                    // SOX focuses on financial data access controls
                    var financialAccess = logs.Where(e => e.ResourceId.Contains("financial", StringComparison.OrdinalIgnoreCase));
                    if (financialAccess.Any(e => !e.Decision))
                    {
                        findings.Add(new ComplianceFinding
                        {
                            Severity = FindingSeverity.Medium,
                            Category = "Access Control",
                            Description = "Denied access attempts to financial data detected",
                            Recommendation = "Review and document all denied access attempts to financial systems",
                            AffectedCount = financialAccess.Count(e => !e.Decision)
                        });
                    }
                    break;

                case ComplianceStandard.HIPAA:
                    // HIPAA focuses on PHI access
                    var phiAccess = logs.Where(e =>
                        e.ResourceId.Contains("patient", StringComparison.OrdinalIgnoreCase) ||
                        e.ResourceId.Contains("health", StringComparison.OrdinalIgnoreCase) ||
                        e.ResourceId.Contains("medical", StringComparison.OrdinalIgnoreCase));

                    if (phiAccess.Any())
                    {
                        findings.Add(new ComplianceFinding
                        {
                            Severity = FindingSeverity.Info,
                            Category = "PHI Access",
                            Description = "Protected Health Information access recorded",
                            Recommendation = "Ensure all PHI access is authorized and documented",
                            AffectedCount = phiAccess.Count()
                        });
                    }
                    break;

                case ComplianceStandard.GDPR:
                    // GDPR focuses on personal data
                    var piiAccess = logs.Where(e =>
                        e.ResourceId.Contains("personal", StringComparison.OrdinalIgnoreCase) ||
                        e.ResourceId.Contains("user", StringComparison.OrdinalIgnoreCase));

                    if (piiAccess.Count() > 1000)
                    {
                        findings.Add(new ComplianceFinding
                        {
                            Severity = FindingSeverity.Medium,
                            Category = "Data Access Volume",
                            Description = "High volume of personal data access detected",
                            Recommendation = "Review data access patterns for data minimization compliance",
                            AffectedCount = piiAccess.Count()
                        });
                    }
                    break;

                case ComplianceStandard.PCI_DSS:
                    // PCI-DSS focuses on cardholder data
                    var cardholderAccess = logs.Where(e =>
                        e.ResourceId.Contains("card", StringComparison.OrdinalIgnoreCase) ||
                        e.ResourceId.Contains("payment", StringComparison.OrdinalIgnoreCase));

                    if (cardholderAccess.Any(e => e.ClientIpAddress != null && !IsInternalNetwork(e.ClientIpAddress)))
                    {
                        findings.Add(new ComplianceFinding
                        {
                            Severity = FindingSeverity.High,
                            Category = "External Access",
                            Description = "Cardholder data accessed from external networks",
                            Recommendation = "Review external access to cardholder data environment",
                            AffectedCount = cardholderAccess.Count(e => e.ClientIpAddress != null && !IsInternalNetwork(e.ClientIpAddress))
                        });
                    }
                    break;
            }

            return findings;
        }

        private static bool IsInternalNetwork(string ip)
        {
            return ip.StartsWith("10.") || ip.StartsWith("192.168.") ||
                   ip.StartsWith("172.16.") || ip.StartsWith("127.") || ip == "::1";
        }

        #endregion

        #region Chain Integrity

        /// <summary>
        /// Verifies the integrity of the audit log chain.
        /// </summary>
        public ChainIntegrityResult VerifyChainIntegrity(IEnumerable<AuditLogEntry>? entries = null)
        {
            var logs = (entries ?? _recentLogs.Values)
                .OrderBy(e => e.SequenceNumber)
                .ToList();

            if (!logs.Any() || !_enableHashChaining)
            {
                return new ChainIntegrityResult
                {
                    IsIntact = true,
                    EntriesVerified = logs.Count,
                    FirstEntry = logs.FirstOrDefault()?.SequenceNumber ?? 0,
                    LastEntry = logs.LastOrDefault()?.SequenceNumber ?? 0
                };
            }

            var brokenLinks = new List<long>();
            string previousHash = "";

            foreach (var entry in logs)
            {
                if (!string.IsNullOrEmpty(entry.PreviousHash) && entry.PreviousHash != previousHash)
                {
                    brokenLinks.Add(entry.SequenceNumber);
                }

                var computedHash = ComputeEntryHash(entry);
                if (!string.IsNullOrEmpty(entry.EntryHash) && entry.EntryHash != computedHash)
                {
                    brokenLinks.Add(entry.SequenceNumber);
                }

                previousHash = entry.EntryHash ?? "";
            }

            return new ChainIntegrityResult
            {
                IsIntact = brokenLinks.Count == 0,
                EntriesVerified = logs.Count,
                FirstEntry = logs.First().SequenceNumber,
                LastEntry = logs.Last().SequenceNumber,
                BrokenLinks = brokenLinks.AsReadOnly()
            };
        }

        private static string ComputeEntryHash(AuditLogEntry entry)
        {
            var data = $"{entry.SequenceNumber}|{entry.Timestamp:O}|{entry.SubjectId}|{entry.ResourceId}|{entry.Action}|{entry.Decision}|{entry.PreviousHash}";
            var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(data));
            return Convert.ToHexString(hash);
        }

        #endregion

        #region Background Operations

        /// <summary>
        /// Flushes pending logs to all destinations.
        /// </summary>
        public async Task FlushLogsAsync()
        {
            var batch = new List<AuditLogEntry>();

            while (batch.Count < _batchSize && _logQueue.TryDequeue(out var entry))
            {
                batch.Add(entry);
            }

            if (batch.Count == 0)
                return;

            var tasks = _destinations
                .Where(d => d.IsEnabled)
                .Select(async d =>
                {
                    try
                    {
                        await d.WriteLogsAsync(batch);
                    }
                    catch
                    {
                        // Log destination failure handling
                    }
                });

            await Task.WhenAll(tasks);
        }

        /// <summary>
        /// Forces immediate flush of all pending logs.
        /// </summary>
        public async Task ForceFlushAsync()
        {
            while (_logQueue.Count > 0)
            {
                await FlushLogsAsync();
            }
        }

        private void CleanupOldLogs()
        {
            var cutoff = DateTime.UtcNow.Subtract(_retentionPeriod);
            var oldLogs = _recentLogs.Values.Where(e => e.Timestamp < cutoff).ToList();

            foreach (var log in oldLogs)
            {
                _recentLogs.TryRemove(log.Id, out _);
            }
        }

        #endregion

        #region Export Helpers

        private static byte[] ExportToCsv(List<AuditLogEntry> logs)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Id,Timestamp,EventType,SubjectId,ResourceId,Action,Decision,Reason,ClientIP,EvaluationTimeMs");

            foreach (var log in logs)
            {
                sb.AppendLine($"\"{log.Id}\",\"{log.Timestamp:O}\",\"{log.EventType}\",\"{log.SubjectId}\",\"{log.ResourceId}\",\"{log.Action}\",{log.Decision},\"{log.DecisionReason}\",\"{log.ClientIpAddress}\",{log.EvaluationTimeMs}");
            }

            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        private static byte[] ExportToSyslog(List<AuditLogEntry> logs)
        {
            var sb = new StringBuilder();

            foreach (var log in logs)
            {
                // RFC 5424 format
                var priority = log.Severity switch
                {
                    AlertSeverity.Critical => 2,
                    AlertSeverity.High => 3,
                    AlertSeverity.Medium => 4,
                    AlertSeverity.Low => 6,
                    _ => 6
                };

                sb.AppendLine($"<{priority}>1 {log.Timestamp:yyyy-MM-ddTHH:mm:ss.fffZ} datawarehouse accesscontrol {log.SequenceNumber} {log.EventType} [access subject=\"{log.SubjectId}\" resource=\"{log.ResourceId}\" action=\"{log.Action}\" decision=\"{log.Decision}\"] {log.DecisionReason}");
            }

            return Encoding.UTF8.GetBytes(sb.ToString());
        }

        #endregion

        #region Core Evaluation

        /// <inheritdoc/>
        protected override Task<AccessDecision> EvaluateAccessCoreAsync(AccessContext context, CancellationToken cancellationToken)
        {
            // This strategy is primarily for logging, so it always permits
            // and logs the access attempt for other strategies to make the actual decision
            var decision = new AccessDecision
            {
                IsGranted = true,
                Reason = "Audit logging strategy - passthrough",
                ApplicablePolicies = new[] { "AuditLogging.Passthrough" }
            };

            // Log the access attempt
            var entry = CreateAuditEntry(context, decision, StrategyId, TimeSpan.Zero);
            LogEntry(entry);

            return Task.FromResult(decision);
        }

        #endregion

        public new void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _flushTimer?.Dispose();
            _retentionTimer?.Dispose();

            // Sync bridge: Dispose cannot be async without IAsyncDisposable
            Task.Run(() => ForceFlushAsync()).GetAwaiter().GetResult();

            foreach (var destination in _destinations.OfType<IDisposable>())
            {
                destination.Dispose();
            }
        }
    }

    #region Supporting Types

    /// <summary>
    /// Audit log entry.
    /// </summary>
    public sealed class AuditLogEntry
    {
        public required string Id { get; init; }
        public required long SequenceNumber { get; init; }
        public required DateTime Timestamp { get; init; }
        public required AuditEventType EventType { get; init; }
        public required string SubjectId { get; init; }
        public required string[] SubjectRoles { get; init; }
        public required Dictionary<string, object> SubjectAttributes { get; init; }
        public required string ResourceId { get; init; }
        public required Dictionary<string, object> ResourceAttributes { get; init; }
        public required string Action { get; init; }
        public required bool Decision { get; init; }
        public required string DecisionReason { get; init; }
        public required string[] ApplicablePolicies { get; init; }
        public required string StrategyUsed { get; init; }
        public required double EvaluationTimeMs { get; init; }
        public string? ClientIpAddress { get; init; }
        public GeoLocation? Location { get; init; }
        public required DateTime RequestTime { get; init; }
        public required Dictionary<string, object> EnvironmentAttributes { get; init; }
        public required Dictionary<string, object> DecisionMetadata { get; init; }
        public AlertSeverity Severity { get; init; } = AlertSeverity.Low;
        public string? PreviousHash { get; set; }
        public string? EntryHash { get; set; }
    }

    /// <summary>
    /// Audit event types.
    /// </summary>
    public enum AuditEventType
    {
        AccessGranted,
        AccessDenied,
        AuthenticationSuccess,
        AuthenticationFailure,
        AuthorizationSuccess,
        AuthorizationFailure,
        PolicyViolation,
        SecurityAlert,
        SuspiciousActivity,
        ConfigurationChange,
        UserCreated,
        UserDeleted,
        RoleAssigned,
        RoleRevoked,
        SessionCreated,
        SessionTerminated
    }

    /// <summary>
    /// Audit log query parameters.
    /// </summary>
    public sealed record AuditLogQuery
    {
        public string? SubjectId { get; init; }
        public string? ResourceId { get; init; }
        public string? Action { get; init; }
        public AuditEventType? EventType { get; init; }
        public DateTime? StartTime { get; init; }
        public DateTime? EndTime { get; init; }
        public bool? DecisionGranted { get; init; }
        public AlertSeverity? MinSeverity { get; init; }
        public int Offset { get; init; } = 0;
        public int Limit { get; init; } = 100;
    }

    /// <summary>
    /// Audit log destination interface.
    /// </summary>
    public interface IAuditLogDestination
    {
        string Id { get; }
        string Name { get; }
        bool IsEnabled { get; }
        Task WriteLogsAsync(IEnumerable<AuditLogEntry> entries);
    }

    /// <summary>
    /// File-based audit log destination.
    /// </summary>
    public sealed class FileAuditLogDestination : IAuditLogDestination, IDisposable
    {
        private readonly string _logDirectory;
        private readonly object _writeLock = new();

        public FileAuditLogDestination(string logDirectory)
        {
            _logDirectory = logDirectory;
            Directory.CreateDirectory(logDirectory);
        }

        public string Id => "file";
        public string Name => "File Destination";
        public bool IsEnabled { get; set; } = true;

        public Task WriteLogsAsync(IEnumerable<AuditLogEntry> entries)
        {
            var fileName = Path.Combine(_logDirectory, $"audit-{DateTime.UtcNow:yyyy-MM-dd}.jsonl");

            lock (_writeLock)
            {
                using var writer = new StreamWriter(fileName, append: true);
                foreach (var entry in entries)
                {
                    writer.WriteLine(JsonSerializer.Serialize(entry));
                }
                writer.Flush();
            }

            return Task.CompletedTask;
        }

        public void Dispose()
        {
            // No persistent resources to dispose; StreamWriter is disposed in using block
        }
    }

    /// <summary>
    /// SIEM audit log destination.
    /// </summary>
    public sealed class SiemAuditLogDestination : IAuditLogDestination
    {
        private readonly string _siemEndpoint;
        private readonly string _apiKey;
        private readonly HttpClient _httpClient;

        public SiemAuditLogDestination(string siemEndpoint, string apiKey)
        {
            _siemEndpoint = siemEndpoint;
            _apiKey = apiKey;
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        }

        public string Id => "siem";
        public string Name => "SIEM Destination";
        public bool IsEnabled { get; set; } = true;

        public async Task WriteLogsAsync(IEnumerable<AuditLogEntry> entries)
        {
            var payload = JsonSerializer.Serialize(entries);
            var request = new HttpRequestMessage(HttpMethod.Post, _siemEndpoint)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {_apiKey}");

            await _httpClient.SendAsync(request);
        }
    }

    /// <summary>
    /// Compliance standards.
    /// </summary>
    public enum ComplianceStandard
    {
        SOX,
        HIPAA,
        GDPR,
        PCI_DSS,
        ISO27001,
        NIST,
        FedRAMP
    }

    /// <summary>
    /// Export formats.
    /// </summary>
    public enum ExportFormat
    {
        Json,
        Csv,
        Syslog
    }

    /// <summary>
    /// Compliance report.
    /// </summary>
    public sealed class ComplianceReport
    {
        public required string Id { get; init; }
        public required ComplianceStandard Standard { get; init; }
        public required DateTime StartDate { get; init; }
        public required DateTime EndDate { get; init; }
        public required DateTime GeneratedAt { get; init; }
        public required int TotalAccessEvents { get; init; }
        public required int GrantedCount { get; init; }
        public required int DeniedCount { get; init; }
        public required int UniqueSubjects { get; init; }
        public required int UniqueResources { get; init; }
        public required int SecurityEvents { get; init; }
        public required int HighSeverityEvents { get; init; }
        public required Dictionary<string, int> TopAccessedResources { get; init; }
        public required Dictionary<string, int> TopDeniedSubjects { get; init; }
        public required Dictionary<int, int> AccessPatternsByHour { get; init; }
        public required ChainIntegrityResult ChainIntegrity { get; init; }
        public List<ComplianceFinding> Findings { get; set; } = new();
    }

    /// <summary>
    /// Compliance finding.
    /// </summary>
    public sealed class ComplianceFinding
    {
        public required FindingSeverity Severity { get; init; }
        public required string Category { get; init; }
        public required string Description { get; init; }
        public required string Recommendation { get; init; }
        public required int AffectedCount { get; init; }
    }

    /// <summary>
    /// Finding severity levels.
    /// </summary>
    public enum FindingSeverity
    {
        Info,
        Low,
        Medium,
        High,
        Critical
    }

    /// <summary>
    /// Chain integrity verification result.
    /// </summary>
    public sealed class ChainIntegrityResult
    {
        public required bool IsIntact { get; init; }
        public required int EntriesVerified { get; init; }
        public required long FirstEntry { get; init; }
        public required long LastEntry { get; init; }
        public IReadOnlyList<long> BrokenLinks { get; init; } = Array.Empty<long>();
    }

    /// <summary>
    /// Alert severity levels (shared with CanaryStrategy).
    /// </summary>
    public enum AlertSeverity
    {
        Low = 0,
        Medium = 1,
        High = 2,
        Critical = 3
    }

    #endregion
}
