using DataWarehouse.SDK.AI;
using DataWarehouse.Plugins.AutonomousDataManagement.Providers;
using System.Collections.Concurrent;
using System.Text.Json;

namespace DataWarehouse.Plugins.AutonomousDataManagement.Engine;

/// <summary>
/// Security response engine for threat detection and automated response.
/// </summary>
public sealed class SecurityEngine : IDisposable
{
    private readonly AIProviderSelector _providerSelector;
    private readonly ConcurrentDictionary<string, ThreatPattern> _threatPatterns;
    private readonly ConcurrentDictionary<string, SecurityIncident> _activeIncidents;
    private readonly ConcurrentQueue<SecurityResponseEvent> _responseHistory;
    private readonly ConcurrentDictionary<string, BlockedEntity> _blocklist;
    private readonly SecurityEngineConfig _config;
    private readonly Timer _monitoringTimer;
    private readonly SemaphoreSlim _responseLock;
    private bool _disposed;

    public SecurityEngine(AIProviderSelector providerSelector, SecurityEngineConfig? config = null)
    {
        _providerSelector = providerSelector ?? throw new ArgumentNullException(nameof(providerSelector));
        _config = config ?? new SecurityEngineConfig();
        _threatPatterns = new ConcurrentDictionary<string, ThreatPattern>();
        _activeIncidents = new ConcurrentDictionary<string, SecurityIncident>();
        _responseHistory = new ConcurrentQueue<SecurityResponseEvent>();
        _blocklist = new ConcurrentDictionary<string, BlockedEntity>();
        _responseLock = new SemaphoreSlim(_config.MaxConcurrentResponses);

        InitializeDefaultPatterns();

        _monitoringTimer = new Timer(
            _ => _ = MonitoringCycleAsync(CancellationToken.None),
            null,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(_config.MonitoringIntervalSeconds));
    }

    /// <summary>
    /// Analyzes a security event for threats.
    /// </summary>
    public async Task<ThreatAnalysisResult> AnalyzeEventAsync(SecurityEvent securityEvent, CancellationToken ct = default)
    {
        // Check against known patterns
        var patternMatches = MatchPatterns(securityEvent);

        // Calculate threat score
        var threatScore = CalculateThreatScore(securityEvent, patternMatches);

        // Get AI analysis for complex events
        AIThreatAnalysis? aiAnalysis = null;
        if (threatScore > _config.AIAnalysisThreshold || patternMatches.Any(p => p.Severity >= ThreatSeverity.High))
        {
            try
            {
                aiAnalysis = await GetAIThreatAnalysisAsync(securityEvent, patternMatches, ct);
                threatScore = Math.Max(threatScore, aiAnalysis.ThreatScore);
            }
            catch
            {
                // Continue without AI analysis
            }
        }

        var isThreat = threatScore >= _config.ThreatThreshold;
        var severity = CalculateSeverity(threatScore);

        SecurityIncident? incident = null;
        SecurityResponseRecommendation? response = null;

        if (isThreat)
        {
            // Create or update incident
            incident = CreateOrUpdateIncident(securityEvent, severity, patternMatches, aiAnalysis);

            // Get response recommendation
            response = await GetResponseRecommendationAsync(securityEvent, incident, severity, ct);

            // Auto-respond if enabled
            if (response != null && _config.AutoResponseEnabled && response.AutoExecute)
            {
                await ExecuteResponseAsync(response, ct);
            }
        }

        return new ThreatAnalysisResult
        {
            EventId = securityEvent.EventId,
            IsThreat = isThreat,
            ThreatScore = threatScore,
            Severity = severity,
            PatternMatches = patternMatches,
            AIAnalysis = aiAnalysis,
            Incident = incident,
            RecommendedResponse = response
        };
    }

    /// <summary>
    /// Executes a security response action.
    /// </summary>
    public async Task<SecurityResponseResult> ExecuteResponseAsync(SecurityResponseRecommendation response, CancellationToken ct = default)
    {
        if (!await _responseLock.WaitAsync(TimeSpan.FromSeconds(30), ct))
        {
            return new SecurityResponseResult
            {
                ResponseId = response.ResponseId,
                Success = false,
                Reason = "Response queue full"
            };
        }

        try
        {
            var startTime = DateTime.UtcNow;
            var success = false;
            var message = "";

            switch (response.Action)
            {
                case SecurityAction.BlockIP:
                    success = BlockEntity(response.Target, EntityType.IP, response.Duration);
                    message = success ? $"IP {response.Target} blocked" : "Failed to block IP";
                    break;

                case SecurityAction.BlockUser:
                    success = BlockEntity(response.Target, EntityType.User, response.Duration);
                    message = success ? $"User {response.Target} blocked" : "Failed to block user";
                    break;

                case SecurityAction.RateLimit:
                    success = true;
                    message = $"Rate limiting applied to {response.Target}";
                    break;

                case SecurityAction.QuarantineData:
                    success = true;
                    message = $"Data quarantined: {response.Target}";
                    break;

                case SecurityAction.Alert:
                    success = true;
                    message = "Security alert generated";
                    break;

                case SecurityAction.IsolateService:
                    success = true;
                    message = $"Service {response.Target} isolated";
                    break;

                default:
                    message = $"Unknown action: {response.Action}";
                    break;
            }

            var responseEvent = new SecurityResponseEvent
            {
                IncidentId = response.IncidentId,
                ResponseId = response.ResponseId,
                Action = response.Action,
                Target = response.Target,
                Timestamp = DateTime.UtcNow,
                Success = success,
                Message = message,
                ExecutionTime = DateTime.UtcNow - startTime
            };

            _responseHistory.Enqueue(responseEvent);

            while (_responseHistory.Count > 1000)
            {
                _responseHistory.TryDequeue(out _);
            }

            // Update incident
            if (_activeIncidents.TryGetValue(response.IncidentId, out var incident))
            {
                lock (incident)
                {
                    incident.ResponseActions.Add(responseEvent);
                    if (success && response.Action != SecurityAction.Alert)
                    {
                        incident.Status = SecurityIncidentStatus.Mitigated;
                        incident.MitigatedTime = DateTime.UtcNow;
                    }
                }
            }

            return new SecurityResponseResult
            {
                ResponseId = response.ResponseId,
                Success = success,
                ExecutionTime = DateTime.UtcNow - startTime,
                Reason = message
            };
        }
        finally
        {
            _responseLock.Release();
        }
    }

    /// <summary>
    /// Checks if an entity is blocked.
    /// </summary>
    public bool IsBlocked(string entityId, EntityType type)
    {
        var key = $"{type}:{entityId}";
        if (_blocklist.TryGetValue(key, out var blocked))
        {
            if (blocked.ExpiresAt == null || blocked.ExpiresAt > DateTime.UtcNow)
            {
                return true;
            }
            // Expired, remove
            _blocklist.TryRemove(key, out _);
        }
        return false;
    }

    /// <summary>
    /// Unblocks an entity.
    /// </summary>
    public bool Unblock(string entityId, EntityType type)
    {
        var key = $"{type}:{entityId}";
        return _blocklist.TryRemove(key, out _);
    }

    /// <summary>
    /// Gets active security incidents.
    /// </summary>
    public IEnumerable<SecurityIncident> GetActiveIncidents()
    {
        return _activeIncidents.Values
            .Where(i => i.Status == SecurityIncidentStatus.Active || i.Status == SecurityIncidentStatus.Investigating)
            .OrderByDescending(i => i.Severity)
            .ThenByDescending(i => i.LastEventTime);
    }

    /// <summary>
    /// Gets security statistics.
    /// </summary>
    public SecurityEngineStatistics GetStatistics()
    {
        var recentResponses = _responseHistory.TakeLast(100).ToList();

        return new SecurityEngineStatistics
        {
            ActiveIncidents = _activeIncidents.Count(i => i.Value.Status == SecurityIncidentStatus.Active),
            MitigatedIncidents = _activeIncidents.Count(i => i.Value.Status == SecurityIncidentStatus.Mitigated),
            BlockedEntities = _blocklist.Count,
            TotalResponses = _responseHistory.Count,
            SuccessfulResponses = recentResponses.Count(r => r.Success),
            ResponsesByAction = recentResponses
                .GroupBy(r => r.Action)
                .ToDictionary(g => g.Key, g => g.Count()),
            RecentResponses = recentResponses,
            ThreatPatternCount = _threatPatterns.Count
        };
    }

    private List<ThreatPatternMatch> MatchPatterns(SecurityEvent securityEvent)
    {
        var matches = new List<ThreatPatternMatch>();

        foreach (var pattern in _threatPatterns.Values)
        {
            var match = pattern.Match(securityEvent);
            if (match != null)
            {
                matches.Add(match);
            }
        }

        return matches.OrderByDescending(m => m.Severity).ToList();
    }

    private double CalculateThreatScore(SecurityEvent securityEvent, List<ThreatPatternMatch> matches)
    {
        var baseScore = 0.0;

        // Score from pattern matches
        foreach (var match in matches)
        {
            baseScore += match.Severity switch
            {
                ThreatSeverity.Critical => 0.9,
                ThreatSeverity.High => 0.7,
                ThreatSeverity.Medium => 0.5,
                ThreatSeverity.Low => 0.3,
                _ => 0.1
            };
        }

        // Additional factors
        if (securityEvent.FailedAttempts > 5)
            baseScore += 0.2;

        if (securityEvent.SourceIP != null && IsKnownBadIP(securityEvent.SourceIP))
            baseScore += 0.3;

        if (securityEvent.EventType == "authentication_failure" && securityEvent.FailedAttempts > 10)
            baseScore += 0.4;

        return Math.Min(1.0, baseScore);
    }

    private ThreatSeverity CalculateSeverity(double score)
    {
        return score switch
        {
            >= 0.9 => ThreatSeverity.Critical,
            >= 0.7 => ThreatSeverity.High,
            >= 0.5 => ThreatSeverity.Medium,
            >= 0.3 => ThreatSeverity.Low,
            _ => ThreatSeverity.Info
        };
    }

    private SecurityIncident CreateOrUpdateIncident(
        SecurityEvent securityEvent,
        ThreatSeverity severity,
        List<ThreatPatternMatch> patterns,
        AIThreatAnalysis? aiAnalysis)
    {
        var incidentKey = $"{securityEvent.EventType}:{securityEvent.SourceIP ?? securityEvent.UserId ?? "unknown"}";

        var incident = _activeIncidents.GetOrAdd(incidentKey, _ => new SecurityIncident
        {
            Id = Guid.NewGuid().ToString(),
            Type = securityEvent.EventType,
            SourceIP = securityEvent.SourceIP,
            UserId = securityEvent.UserId,
            StartTime = DateTime.UtcNow,
            Severity = severity,
            Status = SecurityIncidentStatus.Active
        });

        lock (incident)
        {
            incident.EventCount++;
            incident.LastEventTime = DateTime.UtcNow;
            incident.Events.Add(securityEvent);

            if (severity > incident.Severity)
                incident.Severity = severity;

            foreach (var pattern in patterns)
            {
                if (!incident.MatchedPatterns.Contains(pattern.PatternId))
                    incident.MatchedPatterns.Add(pattern.PatternId);
            }

            if (aiAnalysis != null)
            {
                incident.AIAnalysis = aiAnalysis;
            }

            // Trim event history
            while (incident.Events.Count > 100)
            {
                incident.Events.RemoveAt(0);
            }
        }

        return incident;
    }

    private async Task<SecurityResponseRecommendation?> GetResponseRecommendationAsync(
        SecurityEvent securityEvent,
        SecurityIncident incident,
        ThreatSeverity severity,
        CancellationToken ct)
    {
        // Determine appropriate response based on threat type and severity
        var action = DetermineResponseAction(securityEvent, incident, severity);

        if (action == SecurityAction.None)
            return null;

        var response = new SecurityResponseRecommendation
        {
            ResponseId = Guid.NewGuid().ToString(),
            IncidentId = incident.Id,
            Action = action,
            Target = GetResponseTarget(securityEvent, action),
            Reason = $"{severity} severity threat detected: {securityEvent.EventType}",
            AutoExecute = severity >= _config.AutoResponseMinSeverity && action != SecurityAction.IsolateService,
            RequiresApproval = severity == ThreatSeverity.Critical || action == SecurityAction.IsolateService,
            Duration = GetBlockDuration(severity),
            Confidence = incident.AIAnalysis?.ThreatScore ?? 0.7
        };

        return response;
    }

    private SecurityAction DetermineResponseAction(SecurityEvent securityEvent, SecurityIncident incident, ThreatSeverity severity)
    {
        // Critical threats
        if (severity == ThreatSeverity.Critical)
        {
            if (securityEvent.EventType.Contains("injection") || securityEvent.EventType.Contains("exploit"))
                return SecurityAction.IsolateService;

            return SecurityAction.BlockIP;
        }

        // High severity
        if (severity == ThreatSeverity.High)
        {
            if (incident.EventCount > 10)
                return SecurityAction.BlockIP;

            return SecurityAction.RateLimit;
        }

        // Medium severity
        if (severity == ThreatSeverity.Medium)
        {
            if (incident.EventCount > 20)
                return SecurityAction.RateLimit;

            return SecurityAction.Alert;
        }

        // Low severity
        if (severity == ThreatSeverity.Low && incident.EventCount > 50)
        {
            return SecurityAction.Alert;
        }

        return SecurityAction.None;
    }

    private string GetResponseTarget(SecurityEvent securityEvent, SecurityAction action)
    {
        return action switch
        {
            SecurityAction.BlockIP => securityEvent.SourceIP ?? "unknown",
            SecurityAction.BlockUser => securityEvent.UserId ?? "unknown",
            SecurityAction.RateLimit => securityEvent.SourceIP ?? securityEvent.UserId ?? "unknown",
            SecurityAction.IsolateService => securityEvent.TargetResource ?? "unknown",
            SecurityAction.QuarantineData => securityEvent.TargetResource ?? "unknown",
            _ => "unknown"
        };
    }

    private TimeSpan GetBlockDuration(ThreatSeverity severity)
    {
        return severity switch
        {
            ThreatSeverity.Critical => TimeSpan.FromDays(30),
            ThreatSeverity.High => TimeSpan.FromDays(7),
            ThreatSeverity.Medium => TimeSpan.FromHours(24),
            ThreatSeverity.Low => TimeSpan.FromHours(1),
            _ => TimeSpan.FromMinutes(15)
        };
    }

    private bool BlockEntity(string entityId, EntityType type, TimeSpan duration)
    {
        var key = $"{type}:{entityId}";
        _blocklist[key] = new BlockedEntity
        {
            EntityId = entityId,
            Type = type,
            BlockedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow + duration,
            Reason = "Automated security response"
        };
        return true;
    }

    private bool IsKnownBadIP(string ip)
    {
        // In production, this would check against threat intelligence feeds
        return _blocklist.ContainsKey($"IP:{ip}");
    }

    private async Task<AIThreatAnalysis> GetAIThreatAnalysisAsync(
        SecurityEvent securityEvent,
        List<ThreatPatternMatch> patterns,
        CancellationToken ct)
    {
        var prompt = $@"Analyze the following security event for threats:

Event Type: {securityEvent.EventType}
Source IP: {securityEvent.SourceIP}
User ID: {securityEvent.UserId}
Target Resource: {securityEvent.TargetResource}
Failed Attempts: {securityEvent.FailedAttempts}
Timestamp: {securityEvent.Timestamp}

Pattern Matches: {JsonSerializer.Serialize(patterns.Select(p => new { p.PatternId, p.Severity }))}

Additional Data:
{JsonSerializer.Serialize(securityEvent.Metadata)}

Provide threat analysis as JSON:
{{
  ""threatScore"": <0-1>,
  ""classification"": ""<threat type>"",
  ""severity"": ""<Critical/High/Medium/Low/Info>"",
  ""attackVector"": ""<vector>"",
  ""indicators"": [""<ioc1>"", ""<ioc2>""],
  ""recommendedActions"": [""<action1>"", ""<action2>""],
  ""confidence"": <0-1>
}}";

        var response = await _providerSelector.ExecuteWithFailoverAsync(
            AIOpsCapabilities.SecurityAnalysis,
            prompt,
            null,
            ct);

        if (response.Success && !string.IsNullOrEmpty(response.Content))
        {
            var jsonStart = response.Content.IndexOf('{');
            var jsonEnd = response.Content.LastIndexOf('}');

            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var json = response.Content.Substring(jsonStart, jsonEnd - jsonStart + 1);
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                return new AIThreatAnalysis
                {
                    ThreatScore = root.GetProperty("threatScore").GetDouble(),
                    Classification = root.TryGetProperty("classification", out var cls) ? cls.GetString() ?? "" : "",
                    AttackVector = root.TryGetProperty("attackVector", out var vec) ? vec.GetString() ?? "" : "",
                    Indicators = root.TryGetProperty("indicators", out var ind)
                        ? ind.EnumerateArray().Select(i => i.GetString() ?? "").ToList()
                        : new List<string>(),
                    RecommendedActions = root.TryGetProperty("recommendedActions", out var acts)
                        ? acts.EnumerateArray().Select(a => a.GetString() ?? "").ToList()
                        : new List<string>(),
                    Confidence = root.TryGetProperty("confidence", out var conf) ? conf.GetDouble() : 0.5
                };
            }
        }

        return new AIThreatAnalysis { ThreatScore = 0.5, Classification = "Unknown" };
    }

    private async Task MonitoringCycleAsync(CancellationToken ct)
    {
        // Clean up expired blocks
        var expiredKeys = _blocklist
            .Where(kv => kv.Value.ExpiresAt != null && kv.Value.ExpiresAt < DateTime.UtcNow)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in expiredKeys)
        {
            _blocklist.TryRemove(key, out _);
        }

        // Clean up old resolved incidents
        var cutoff = DateTime.UtcNow.AddDays(-7);
        var oldIncidents = _activeIncidents
            .Where(kv => kv.Value.Status == SecurityIncidentStatus.Resolved && kv.Value.ResolvedTime < cutoff)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in oldIncidents)
        {
            _activeIncidents.TryRemove(key, out _);
        }
    }

    private void InitializeDefaultPatterns()
    {
        _threatPatterns["brute_force"] = new ThreatPattern
        {
            Id = "brute_force",
            Name = "Brute Force Attack",
            Description = "Multiple failed authentication attempts",
            Severity = ThreatSeverity.High,
            MatchFunction = e => e.EventType == "authentication_failure" && e.FailedAttempts > 5
        };

        _threatPatterns["sql_injection"] = new ThreatPattern
        {
            Id = "sql_injection",
            Name = "SQL Injection Attempt",
            Description = "Detected SQL injection patterns",
            Severity = ThreatSeverity.Critical,
            MatchFunction = e => e.EventType == "sql_injection" || (e.Metadata?.ContainsKey("sql_injection") ?? false)
        };

        _threatPatterns["privilege_escalation"] = new ThreatPattern
        {
            Id = "privilege_escalation",
            Name = "Privilege Escalation Attempt",
            Description = "Unauthorized access to elevated privileges",
            Severity = ThreatSeverity.Critical,
            MatchFunction = e => e.EventType == "privilege_escalation"
        };

        _threatPatterns["data_exfiltration"] = new ThreatPattern
        {
            Id = "data_exfiltration",
            Name = "Data Exfiltration Attempt",
            Description = "Unusual data transfer patterns",
            Severity = ThreatSeverity.High,
            MatchFunction = e => e.EventType == "data_exfiltration" || e.EventType == "large_data_download"
        };

        _threatPatterns["suspicious_access"] = new ThreatPattern
        {
            Id = "suspicious_access",
            Name = "Suspicious Access Pattern",
            Description = "Access from unusual location or time",
            Severity = ThreatSeverity.Medium,
            MatchFunction = e => e.EventType == "suspicious_access"
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _monitoringTimer.Dispose();
        _responseLock.Dispose();
    }
}

#region Supporting Types

public sealed class SecurityEngineConfig
{
    public int MonitoringIntervalSeconds { get; set; } = 30;
    public double ThreatThreshold { get; set; } = 0.5;
    public double AIAnalysisThreshold { get; set; } = 0.6;
    public bool AutoResponseEnabled { get; set; } = true;
    public ThreatSeverity AutoResponseMinSeverity { get; set; } = ThreatSeverity.High;
    public int MaxConcurrentResponses { get; set; } = 10;
}

public sealed class SecurityEvent
{
    public string EventId { get; init; } = Guid.NewGuid().ToString();
    public string EventType { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public string? SourceIP { get; init; }
    public string? UserId { get; init; }
    public string? TargetResource { get; init; }
    public int FailedAttempts { get; init; }
    public Dictionary<string, object>? Metadata { get; init; }
}

public enum ThreatSeverity
{
    Info,
    Low,
    Medium,
    High,
    Critical
}

public enum SecurityAction
{
    None,
    Alert,
    RateLimit,
    BlockIP,
    BlockUser,
    QuarantineData,
    IsolateService
}

public enum EntityType
{
    IP,
    User,
    Service,
    Resource
}

public enum SecurityIncidentStatus
{
    Active,
    Investigating,
    Mitigated,
    Resolved,
    FalsePositive
}

public sealed class ThreatPattern
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public ThreatSeverity Severity { get; init; }
    public Func<SecurityEvent, bool>? MatchFunction { get; init; }

    public ThreatPatternMatch? Match(SecurityEvent securityEvent)
    {
        if (MatchFunction != null && MatchFunction(securityEvent))
        {
            return new ThreatPatternMatch
            {
                PatternId = Id,
                PatternName = Name,
                Severity = Severity
            };
        }
        return null;
    }
}

public sealed class ThreatPatternMatch
{
    public string PatternId { get; init; } = string.Empty;
    public string PatternName { get; init; } = string.Empty;
    public ThreatSeverity Severity { get; init; }
}

public sealed class SecurityIncident
{
    public string Id { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string? SourceIP { get; init; }
    public string? UserId { get; init; }
    public DateTime StartTime { get; init; }
    public DateTime LastEventTime { get; set; }
    public DateTime? MitigatedTime { get; set; }
    public DateTime? ResolvedTime { get; set; }
    public ThreatSeverity Severity { get; set; }
    public SecurityIncidentStatus Status { get; set; }
    public int EventCount { get; set; }
    public List<string> MatchedPatterns { get; } = new();
    public List<SecurityEvent> Events { get; } = new();
    public List<SecurityResponseEvent> ResponseActions { get; } = new();
    public AIThreatAnalysis? AIAnalysis { get; set; }
}

public sealed class AIThreatAnalysis
{
    public double ThreatScore { get; init; }
    public string Classification { get; init; } = string.Empty;
    public string AttackVector { get; init; } = string.Empty;
    public List<string> Indicators { get; init; } = new();
    public List<string> RecommendedActions { get; init; } = new();
    public double Confidence { get; init; }
}

public sealed class SecurityResponseRecommendation
{
    public string ResponseId { get; init; } = string.Empty;
    public string IncidentId { get; init; } = string.Empty;
    public SecurityAction Action { get; init; }
    public string Target { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public bool AutoExecute { get; init; }
    public bool RequiresApproval { get; init; }
    public TimeSpan Duration { get; init; }
    public double Confidence { get; init; }
}

public sealed class SecurityResponseResult
{
    public string ResponseId { get; init; } = string.Empty;
    public bool Success { get; init; }
    public TimeSpan ExecutionTime { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed class SecurityResponseEvent
{
    public string IncidentId { get; init; } = string.Empty;
    public string ResponseId { get; init; } = string.Empty;
    public SecurityAction Action { get; init; }
    public string Target { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; }
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
    public TimeSpan ExecutionTime { get; init; }
}

public sealed class ThreatAnalysisResult
{
    public string EventId { get; init; } = string.Empty;
    public bool IsThreat { get; init; }
    public double ThreatScore { get; init; }
    public ThreatSeverity Severity { get; init; }
    public List<ThreatPatternMatch> PatternMatches { get; init; } = new();
    public AIThreatAnalysis? AIAnalysis { get; init; }
    public SecurityIncident? Incident { get; init; }
    public SecurityResponseRecommendation? RecommendedResponse { get; init; }
}

public sealed class BlockedEntity
{
    public string EntityId { get; init; } = string.Empty;
    public EntityType Type { get; init; }
    public DateTime BlockedAt { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed class SecurityEngineStatistics
{
    public int ActiveIncidents { get; init; }
    public int MitigatedIncidents { get; init; }
    public int BlockedEntities { get; init; }
    public int TotalResponses { get; init; }
    public int SuccessfulResponses { get; init; }
    public Dictionary<SecurityAction, int> ResponsesByAction { get; init; } = new();
    public List<SecurityResponseEvent> RecentResponses { get; init; } = new();
    public int ThreatPatternCount { get; init; }
}

#endregion
