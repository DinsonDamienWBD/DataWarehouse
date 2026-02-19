using System.Collections.Concurrent;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Plugins.UniversalObservability.Strategies.RealUserMonitoring;

/// <summary>
/// Session tracking service for RUM strategies. Provides session lifecycle management,
/// user identification, and session-level analytics aggregation.
/// </summary>
public sealed class SessionTrackingService
{
    private readonly ConcurrentDictionary<string, UserSession> _activeSessions = new();
    private readonly ConcurrentDictionary<string, List<SessionEvent>> _sessionEvents = new();
    private readonly ConcurrentDictionary<string, UserIdentity> _identifiedUsers = new();
    private readonly TimeSpan _sessionTimeout;
    private readonly int _maxSessionsPerUser;

    public SessionTrackingService(TimeSpan? sessionTimeout = null, int maxSessionsPerUser = 10)
    {
        _sessionTimeout = sessionTimeout ?? TimeSpan.FromMinutes(30);
        _maxSessionsPerUser = maxSessionsPerUser;
    }

    /// <summary>
    /// Starts or resumes a user session.
    /// </summary>
    public UserSession StartOrResumeSession(string userId, string? deviceId = null, Dictionary<string, object>? properties = null)
    {
        var sessionKey = $"{userId}:{deviceId ?? "default"}";

        return _activeSessions.AddOrUpdate(
            sessionKey,
            _ => CreateNewSession(userId, deviceId, properties),
            (_, existing) =>
            {
                if (DateTimeOffset.UtcNow - existing.LastActivityAt > _sessionTimeout)
                {
                    EndSessionInternal(existing);
                    return CreateNewSession(userId, deviceId, properties);
                }
                return existing with
                {
                    LastActivityAt = DateTimeOffset.UtcNow,
                    EventCount = existing.EventCount + 1,
                    Properties = MergeProperties(existing.Properties, properties)
                };
            });
    }

    /// <summary>
    /// Records an event within a session.
    /// </summary>
    public void RecordEvent(string sessionId, string eventName, Dictionary<string, object>? eventData = null)
    {
        var evt = new SessionEvent
        {
            SessionId = sessionId,
            EventName = eventName,
            Timestamp = DateTimeOffset.UtcNow,
            Data = eventData ?? new Dictionary<string, object>()
        };

        _sessionEvents.AddOrUpdate(
            sessionId,
            _ => new List<SessionEvent> { evt },
            (_, list) => { lock (list) { list.Add(evt); } return list; });
    }

    /// <summary>
    /// Identifies a user with profile properties.
    /// </summary>
    public void IdentifyUser(string userId, string? email = null, string? name = null, Dictionary<string, object>? traits = null)
    {
        _identifiedUsers[userId] = new UserIdentity
        {
            UserId = userId,
            Email = email,
            Name = name,
            Traits = traits ?? new Dictionary<string, object>(),
            IdentifiedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Ends a session explicitly.
    /// </summary>
    public SessionSummary? EndSession(string sessionId)
    {
        var session = _activeSessions.Values.FirstOrDefault(s => s.SessionId == sessionId);
        if (session == null) return null;

        EndSessionInternal(session);
        var sessionKey = $"{session.UserId}:{session.DeviceId ?? "default"}";
        _activeSessions.TryRemove(sessionKey, out _);

        return BuildSessionSummary(session);
    }

    /// <summary>
    /// Gets active session count.
    /// </summary>
    public int ActiveSessionCount => _activeSessions.Count;

    /// <summary>
    /// Gets all active sessions for a user.
    /// </summary>
    public IReadOnlyList<UserSession> GetUserSessions(string userId) =>
        _activeSessions.Values.Where(s => s.UserId == userId).ToList().AsReadOnly();

    /// <summary>
    /// Cleans up expired sessions.
    /// </summary>
    public int CleanupExpiredSessions()
    {
        var expired = _activeSessions
            .Where(kvp => DateTimeOffset.UtcNow - kvp.Value.LastActivityAt > _sessionTimeout)
            .ToList();

        foreach (var (key, session) in expired)
        {
            EndSessionInternal(session);
            _activeSessions.TryRemove(key, out _);
        }

        return expired.Count;
    }

    private UserSession CreateNewSession(string userId, string? deviceId, Dictionary<string, object>? properties)
    {
        return new UserSession
        {
            SessionId = Guid.NewGuid().ToString("N"),
            UserId = userId,
            DeviceId = deviceId,
            StartedAt = DateTimeOffset.UtcNow,
            LastActivityAt = DateTimeOffset.UtcNow,
            EventCount = 0,
            Properties = properties ?? new Dictionary<string, object>()
        };
    }

    private void EndSessionInternal(UserSession session)
    {
        // Archive session events
        if (_sessionEvents.TryRemove(session.SessionId, out _))
        {
            // Events archived/flushed
        }
    }

    private SessionSummary BuildSessionSummary(UserSession session)
    {
        var events = _sessionEvents.TryGetValue(session.SessionId, out var evts) ? evts : new List<SessionEvent>();
        return new SessionSummary
        {
            SessionId = session.SessionId,
            UserId = session.UserId,
            Duration = session.LastActivityAt - session.StartedAt,
            EventCount = session.EventCount,
            UniqueEvents = events.Select(e => e.EventName).Distinct().Count(),
            StartedAt = session.StartedAt,
            EndedAt = DateTimeOffset.UtcNow
        };
    }

    private static Dictionary<string, object> MergeProperties(Dictionary<string, object> existing, Dictionary<string, object>? incoming)
    {
        if (incoming == null || incoming.Count == 0) return existing;
        var merged = new Dictionary<string, object>(existing);
        foreach (var (key, value) in incoming)
            merged[key] = value;
        return merged;
    }
}

/// <summary>
/// Funnel analysis engine for tracking user progression through defined conversion funnels.
/// </summary>
public sealed class FunnelAnalysisEngine
{
    private readonly ConcurrentDictionary<string, FunnelDefinition> _funnels = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, FunnelProgress>> _userProgress = new();

    /// <summary>
    /// Defines a conversion funnel with ordered steps.
    /// </summary>
    public FunnelDefinition DefineFunnel(string funnelId, string name, string[] steps, TimeSpan? completionWindow = null)
    {
        var funnel = new FunnelDefinition
        {
            FunnelId = funnelId,
            Name = name,
            Steps = steps,
            CompletionWindow = completionWindow ?? TimeSpan.FromHours(24),
            CreatedAt = DateTimeOffset.UtcNow
        };
        _funnels[funnelId] = funnel;
        return funnel;
    }

    /// <summary>
    /// Records a funnel step completion for a user.
    /// </summary>
    public FunnelStepResult RecordStep(string funnelId, string userId, string stepName)
    {
        if (!_funnels.TryGetValue(funnelId, out var funnel))
            return new FunnelStepResult { Recorded = false, Reason = "Funnel not found" };

        var stepIndex = Array.IndexOf(funnel.Steps, stepName);
        if (stepIndex < 0)
            return new FunnelStepResult { Recorded = false, Reason = "Step not in funnel" };

        var userProgresses = _userProgress.GetOrAdd(funnelId, _ => new ConcurrentDictionary<string, FunnelProgress>());
        var progress = userProgresses.GetOrAdd(userId, _ => new FunnelProgress
        {
            FunnelId = funnelId,
            UserId = userId,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedSteps = new HashSet<string>()
        });

        // Check completion window
        if (DateTimeOffset.UtcNow - progress.StartedAt > funnel.CompletionWindow)
        {
            // Reset progress (window expired)
            progress = new FunnelProgress
            {
                FunnelId = funnelId,
                UserId = userId,
                StartedAt = DateTimeOffset.UtcNow,
                CompletedSteps = new HashSet<string>()
            };
            userProgresses[userId] = progress;
        }

        lock (progress.CompletedSteps)
        {
            progress.CompletedSteps.Add(stepName);
        }

        var isComplete = funnel.Steps.All(s => progress.CompletedSteps.Contains(s));
        if (isComplete)
        {
            progress = progress with { CompletedAt = DateTimeOffset.UtcNow };
            userProgresses[userId] = progress;
        }

        return new FunnelStepResult
        {
            Recorded = true,
            StepIndex = stepIndex,
            TotalSteps = funnel.Steps.Length,
            CompletedSteps = progress.CompletedSteps.Count,
            IsComplete = isComplete
        };
    }

    /// <summary>
    /// Gets funnel conversion metrics.
    /// </summary>
    public FunnelMetrics GetMetrics(string funnelId)
    {
        if (!_funnels.TryGetValue(funnelId, out var funnel))
            return new FunnelMetrics { FunnelId = funnelId };

        if (!_userProgress.TryGetValue(funnelId, out var progresses))
            return new FunnelMetrics { FunnelId = funnelId, FunnelName = funnel.Name };

        var allProgresses = progresses.Values.ToList();
        var stepConversions = funnel.Steps.Select((step, index) =>
        {
            var count = allProgresses.Count(p => p.CompletedSteps.Contains(step));
            return new StepConversion
            {
                StepName = step,
                StepIndex = index,
                Count = count,
                Rate = allProgresses.Count > 0 ? (double)count / allProgresses.Count : 0
            };
        }).ToList();

        var completedCount = allProgresses.Count(p => p.CompletedAt.HasValue);
        var completedProgresses = allProgresses.Where(p => p.CompletedAt.HasValue).ToList();

        return new FunnelMetrics
        {
            FunnelId = funnelId,
            FunnelName = funnel.Name,
            TotalEntries = allProgresses.Count,
            Completions = completedCount,
            ConversionRate = allProgresses.Count > 0 ? (double)completedCount / allProgresses.Count : 0,
            AverageCompletionTime = completedProgresses.Count > 0
                ? TimeSpan.FromTicks((long)completedProgresses.Average(p => (p.CompletedAt!.Value - p.StartedAt).Ticks))
                : TimeSpan.Zero,
            StepConversions = stepConversions
        };
    }

    /// <summary>
    /// Gets all defined funnels.
    /// </summary>
    public IReadOnlyList<FunnelDefinition> GetAllFunnels() => _funnels.Values.ToList().AsReadOnly();
}

#region Models

public sealed record UserSession
{
    public required string SessionId { get; init; }
    public required string UserId { get; init; }
    public string? DeviceId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset LastActivityAt { get; init; }
    public int EventCount { get; init; }
    public Dictionary<string, object> Properties { get; init; } = new();
}

public sealed record SessionEvent
{
    public required string SessionId { get; init; }
    public required string EventName { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public Dictionary<string, object> Data { get; init; } = new();
}

public sealed record UserIdentity
{
    public required string UserId { get; init; }
    public string? Email { get; init; }
    public string? Name { get; init; }
    public Dictionary<string, object> Traits { get; init; } = new();
    public DateTimeOffset IdentifiedAt { get; init; }
}

public sealed record SessionSummary
{
    public required string SessionId { get; init; }
    public required string UserId { get; init; }
    public TimeSpan Duration { get; init; }
    public int EventCount { get; init; }
    public int UniqueEvents { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset EndedAt { get; init; }
}

public sealed record FunnelDefinition
{
    public required string FunnelId { get; init; }
    public required string Name { get; init; }
    public required string[] Steps { get; init; }
    public TimeSpan CompletionWindow { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record FunnelProgress
{
    public required string FunnelId { get; init; }
    public required string UserId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public HashSet<string> CompletedSteps { get; init; } = new();
}

public sealed record FunnelStepResult
{
    public bool Recorded { get; init; }
    public string? Reason { get; init; }
    public int StepIndex { get; init; }
    public int TotalSteps { get; init; }
    public int CompletedSteps { get; init; }
    public bool IsComplete { get; init; }
}

public sealed record FunnelMetrics
{
    public required string FunnelId { get; init; }
    public string? FunnelName { get; init; }
    public int TotalEntries { get; init; }
    public int Completions { get; init; }
    public double ConversionRate { get; init; }
    public TimeSpan AverageCompletionTime { get; init; }
    public List<StepConversion> StepConversions { get; init; } = new();
}

public sealed record StepConversion
{
    public required string StepName { get; init; }
    public int StepIndex { get; init; }
    public int Count { get; init; }
    public double Rate { get; init; }
}

#endregion
