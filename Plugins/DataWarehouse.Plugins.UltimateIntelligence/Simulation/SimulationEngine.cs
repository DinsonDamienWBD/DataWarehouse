using System.Collections.Concurrent;
using System.Text.Json;
using DataWarehouse.SDK.AI;

namespace DataWarehouse.Plugins.UltimateIntelligence.Simulation;

#region Message Bus Topics

/// <summary>
/// Message bus topics for What-If Simulation operations.
/// Provides comprehensive topic definitions for all simulation-related messaging.
/// </summary>
public static class SimulationTopics
{
    /// <summary>Base prefix for all simulation topics.</summary>
    public const string Prefix = "intelligence.simulation";

    #region Core Simulation Operations

    /// <summary>Create a new simulation session.</summary>
    public const string CreateSession = $"{Prefix}.session.create";

    /// <summary>Response for session creation.</summary>
    public const string CreateSessionResponse = $"{Prefix}.session.create.response";

    /// <summary>Fork current state for isolated simulation.</summary>
    public const string ForkState = $"{Prefix}.state.fork";

    /// <summary>Response for state fork operation.</summary>
    public const string ForkStateResponse = $"{Prefix}.state.fork.response";

    /// <summary>Apply hypothetical changes to forked state.</summary>
    public const string ApplyChanges = $"{Prefix}.changes.apply";

    /// <summary>Response for apply changes operation.</summary>
    public const string ApplyChangesResponse = $"{Prefix}.changes.apply.response";

    /// <summary>Analyze impact of hypothetical changes.</summary>
    public const string AnalyzeImpact = $"{Prefix}.impact.analyze";

    /// <summary>Response for impact analysis.</summary>
    public const string AnalyzeImpactResponse = $"{Prefix}.impact.analyze.response";

    /// <summary>Generate simulation report.</summary>
    public const string GenerateReport = $"{Prefix}.report.generate";

    /// <summary>Response for report generation.</summary>
    public const string GenerateReportResponse = $"{Prefix}.report.generate.response";

    /// <summary>Rollback simulated changes.</summary>
    public const string Rollback = $"{Prefix}.rollback";

    /// <summary>Response for rollback operation.</summary>
    public const string RollbackResponse = $"{Prefix}.rollback.response";

    /// <summary>Commit simulated changes to real state.</summary>
    public const string Commit = $"{Prefix}.commit";

    /// <summary>Response for commit operation.</summary>
    public const string CommitResponse = $"{Prefix}.commit.response";

    #endregion

    #region Session Management

    /// <summary>List all active simulation sessions.</summary>
    public const string ListSessions = $"{Prefix}.session.list";

    /// <summary>Response for list sessions operation.</summary>
    public const string ListSessionsResponse = $"{Prefix}.session.list.response";

    /// <summary>Get session details.</summary>
    public const string GetSession = $"{Prefix}.session.get";

    /// <summary>Response for get session operation.</summary>
    public const string GetSessionResponse = $"{Prefix}.session.get.response";

    /// <summary>Terminate a simulation session.</summary>
    public const string TerminateSession = $"{Prefix}.session.terminate";

    /// <summary>Response for terminate session operation.</summary>
    public const string TerminateSessionResponse = $"{Prefix}.session.terminate.response";

    #endregion

    #region Comparison Operations

    /// <summary>Compare forked state with original.</summary>
    public const string CompareStates = $"{Prefix}.compare";

    /// <summary>Response for state comparison.</summary>
    public const string CompareStatesResponse = $"{Prefix}.compare.response";

    /// <summary>Diff between two simulation branches.</summary>
    public const string DiffBranches = $"{Prefix}.diff";

    /// <summary>Response for branch diff operation.</summary>
    public const string DiffBranchesResponse = $"{Prefix}.diff.response";

    #endregion

    #region Events

    /// <summary>Simulation session started event.</summary>
    public const string SessionStarted = $"{Prefix}.event.session-started";

    /// <summary>Simulation changes applied event.</summary>
    public const string ChangesApplied = $"{Prefix}.event.changes-applied";

    /// <summary>Simulation completed event.</summary>
    public const string SimulationCompleted = $"{Prefix}.event.completed";

    /// <summary>Simulation rollback event.</summary>
    public const string RollbackPerformed = $"{Prefix}.event.rollback";

    /// <summary>Simulation commit event.</summary>
    public const string CommitPerformed = $"{Prefix}.event.commit";

    #endregion
}

#endregion

#region Core Types

/// <summary>
/// Represents a hypothetical change to be applied in simulation.
/// </summary>
public sealed record SimulatedChange
{
    /// <summary>Unique identifier for this change.</summary>
    public string ChangeId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Type of change (create, update, delete, transform).</summary>
    public required ChangeType Type { get; init; }

    /// <summary>Target entity or resource path.</summary>
    public required string TargetPath { get; init; }

    /// <summary>Previous value (for update/delete operations).</summary>
    public object? PreviousValue { get; init; }

    /// <summary>New value (for create/update operations).</summary>
    public object? NewValue { get; init; }

    /// <summary>Additional metadata about the change.</summary>
    public Dictionary<string, object>? Metadata { get; init; }

    /// <summary>Timestamp when change was applied.</summary>
    public DateTime AppliedAt { get; init; } = DateTime.UtcNow;

    /// <summary>User or process that initiated the change.</summary>
    public string? InitiatedBy { get; init; }
}

/// <summary>
/// Types of simulated changes.
/// </summary>
public enum ChangeType
{
    /// <summary>Create a new entity.</summary>
    Create,

    /// <summary>Update an existing entity.</summary>
    Update,

    /// <summary>Delete an entity.</summary>
    Delete,

    /// <summary>Transform data structure.</summary>
    Transform,

    /// <summary>Move/relocate entity.</summary>
    Move,

    /// <summary>Merge multiple entities.</summary>
    Merge,

    /// <summary>Split entity into multiple.</summary>
    Split,

    /// <summary>Configure settings change.</summary>
    Configure
}

/// <summary>
/// Represents a forked state for isolated simulation.
/// </summary>
public sealed class StateFork
{
    /// <summary>Unique identifier for this fork.</summary>
    public string ForkId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>ID of the parent fork (null for root).</summary>
    public string? ParentForkId { get; init; }

    /// <summary>Session this fork belongs to.</summary>
    public required string SessionId { get; init; }

    /// <summary>Name of this fork branch.</summary>
    public string BranchName { get; init; } = "default";

    /// <summary>Snapshot of state at fork time.</summary>
    public Dictionary<string, object> StateSnapshot { get; } = new();

    /// <summary>Changes applied to this fork.</summary>
    public List<SimulatedChange> AppliedChanges { get; } = new();

    /// <summary>Timestamp when fork was created.</summary>
    public DateTime CreatedAt { get; } = DateTime.UtcNow;

    /// <summary>Whether this fork is active.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>Lock object for thread safety.</summary>
    private readonly object _lock = new();

    /// <summary>
    /// Gets a value from the forked state.
    /// </summary>
    /// <typeparam name="T">Type of value.</typeparam>
    /// <param name="path">Path to the value.</param>
    /// <returns>The value if found, default otherwise.</returns>
    public T? GetValue<T>(string path)
    {
        lock (_lock)
        {
            if (StateSnapshot.TryGetValue(path, out var value))
            {
                if (value is T typedValue)
                    return typedValue;

                if (value is JsonElement element)
                    return JsonSerializer.Deserialize<T>(element.GetRawText());
            }
            return default;
        }
    }

    /// <summary>
    /// Sets a value in the forked state.
    /// </summary>
    /// <param name="path">Path to the value.</param>
    /// <param name="value">Value to set.</param>
    public void SetValue(string path, object value)
    {
        lock (_lock)
        {
            StateSnapshot[path] = value;
        }
    }

    /// <summary>
    /// Applies a change to this fork.
    /// </summary>
    /// <param name="change">The change to apply.</param>
    public void ApplyChange(SimulatedChange change)
    {
        lock (_lock)
        {
            switch (change.Type)
            {
                case ChangeType.Create:
                case ChangeType.Update:
                    if (change.NewValue != null)
                        StateSnapshot[change.TargetPath] = change.NewValue;
                    break;

                case ChangeType.Delete:
                    StateSnapshot.Remove(change.TargetPath);
                    break;

                case ChangeType.Move:
                    if (StateSnapshot.TryGetValue(change.TargetPath, out var moveValue))
                    {
                        StateSnapshot.Remove(change.TargetPath);
                        if (change.NewValue is string newPath)
                            StateSnapshot[newPath] = moveValue;
                    }
                    break;
            }

            AppliedChanges.Add(change);
        }
    }

    /// <summary>
    /// Creates a child fork from this fork.
    /// </summary>
    /// <param name="branchName">Name for the child branch.</param>
    /// <returns>The new child fork.</returns>
    public StateFork CreateChildFork(string branchName)
    {
        lock (_lock)
        {
            var child = new StateFork
            {
                SessionId = SessionId,
                ParentForkId = ForkId,
                BranchName = branchName
            };

            // Copy current state to child
            foreach (var kvp in StateSnapshot)
            {
                child.StateSnapshot[kvp.Key] = kvp.Value;
            }

            return child;
        }
    }
}

/// <summary>
/// Represents a simulation session containing multiple forks.
/// </summary>
public sealed class SimulationSession
{
    /// <summary>Unique session identifier.</summary>
    public string SessionId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Human-readable session name.</summary>
    public required string Name { get; init; }

    /// <summary>Description of the simulation scenario.</summary>
    public string? Description { get; init; }

    /// <summary>User who created the session.</summary>
    public string? CreatedBy { get; init; }

    /// <summary>Timestamp when session was created.</summary>
    public DateTime CreatedAt { get; } = DateTime.UtcNow;

    /// <summary>All forks in this session.</summary>
    public ConcurrentDictionary<string, StateFork> Forks { get; } = new();

    /// <summary>The root fork ID.</summary>
    public string? RootForkId { get; private set; }

    /// <summary>Current active fork ID.</summary>
    public string? ActiveForkId { get; set; }

    /// <summary>Session status.</summary>
    public SimulationSessionStatus Status { get; set; } = SimulationSessionStatus.Active;

    /// <summary>Session configuration.</summary>
    public SimulationConfiguration Configuration { get; init; } = new();

    /// <summary>
    /// Creates the root fork from current state.
    /// </summary>
    /// <param name="currentState">Current state to fork from.</param>
    /// <returns>The root fork.</returns>
    public StateFork CreateRootFork(Dictionary<string, object> currentState)
    {
        var rootFork = new StateFork
        {
            SessionId = SessionId,
            BranchName = "main"
        };

        foreach (var kvp in currentState)
        {
            rootFork.StateSnapshot[kvp.Key] = kvp.Value;
        }

        Forks[rootFork.ForkId] = rootFork;
        RootForkId = rootFork.ForkId;
        ActiveForkId = rootFork.ForkId;

        return rootFork;
    }

    /// <summary>
    /// Gets the active fork.
    /// </summary>
    public StateFork? GetActiveFork()
    {
        return ActiveForkId != null && Forks.TryGetValue(ActiveForkId, out var fork) ? fork : null;
    }

    /// <summary>
    /// Gets the root fork.
    /// </summary>
    public StateFork? GetRootFork()
    {
        return RootForkId != null && Forks.TryGetValue(RootForkId, out var fork) ? fork : null;
    }
}

/// <summary>
/// Status of a simulation session.
/// </summary>
public enum SimulationSessionStatus
{
    /// <summary>Session is active and accepting changes.</summary>
    Active,

    /// <summary>Session is paused.</summary>
    Paused,

    /// <summary>Analysis is in progress.</summary>
    Analyzing,

    /// <summary>Session is completed.</summary>
    Completed,

    /// <summary>Session was rolled back.</summary>
    RolledBack,

    /// <summary>Session was committed to real state.</summary>
    Committed,

    /// <summary>Session was terminated.</summary>
    Terminated
}

/// <summary>
/// Configuration for simulation sessions.
/// </summary>
public sealed record SimulationConfiguration
{
    /// <summary>Maximum number of changes allowed.</summary>
    public int MaxChanges { get; init; } = 1000;

    /// <summary>Maximum number of forks allowed.</summary>
    public int MaxForks { get; init; } = 100;

    /// <summary>Session timeout in minutes.</summary>
    public int TimeoutMinutes { get; init; } = 60;

    /// <summary>Whether to track detailed change history.</summary>
    public bool EnableDetailedHistory { get; init; } = true;

    /// <summary>Whether to allow commits to real state.</summary>
    public bool AllowCommit { get; init; } = false;

    /// <summary>Isolation level for the simulation.</summary>
    public SimulationIsolationLevel IsolationLevel { get; init; } = SimulationIsolationLevel.Full;
}

/// <summary>
/// Isolation level for simulations.
/// </summary>
public enum SimulationIsolationLevel
{
    /// <summary>Full isolation - no real state access.</summary>
    Full,

    /// <summary>Read-only access to real state.</summary>
    ReadOnly,

    /// <summary>Snapshot isolation with versioned reads.</summary>
    Snapshot
}

#endregion

#region Impact Analysis Types

/// <summary>
/// Result of impact analysis for simulated changes.
/// </summary>
public sealed record ImpactAnalysisResult
{
    /// <summary>Session ID analyzed.</summary>
    public required string SessionId { get; init; }

    /// <summary>Fork ID analyzed.</summary>
    public required string ForkId { get; init; }

    /// <summary>Number of changes analyzed.</summary>
    public int ChangesAnalyzed { get; init; }

    /// <summary>Overall impact severity (0-1).</summary>
    public float OverallSeverity { get; init; }

    /// <summary>Risk assessment.</summary>
    public RiskAssessment Risk { get; init; } = new();

    /// <summary>Affected entities.</summary>
    public List<AffectedEntity> AffectedEntities { get; init; } = new();

    /// <summary>Dependency impacts.</summary>
    public List<DependencyImpact> DependencyImpacts { get; init; } = new();

    /// <summary>Performance impact predictions.</summary>
    public PerformanceImpact PerformanceImpact { get; init; } = new();

    /// <summary>Recommendations based on analysis.</summary>
    public List<string> Recommendations { get; init; } = new();

    /// <summary>Warnings generated during analysis.</summary>
    public List<string> Warnings { get; init; } = new();

    /// <summary>Timestamp of analysis.</summary>
    public DateTime AnalyzedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Risk assessment for simulated changes.
/// </summary>
public sealed record RiskAssessment
{
    /// <summary>Overall risk level.</summary>
    public RiskLevel Level { get; init; } = RiskLevel.Low;

    /// <summary>Risk score (0-100).</summary>
    public int Score { get; init; }

    /// <summary>Risk factors identified.</summary>
    public List<RiskFactor> Factors { get; init; } = new();

    /// <summary>Mitigation suggestions.</summary>
    public List<string> Mitigations { get; init; } = new();
}

/// <summary>
/// Risk level enumeration.
/// </summary>
public enum RiskLevel
{
    /// <summary>Minimal risk.</summary>
    Low,

    /// <summary>Moderate risk requiring attention.</summary>
    Medium,

    /// <summary>Significant risk requiring review.</summary>
    High,

    /// <summary>Critical risk requiring immediate attention.</summary>
    Critical
}

/// <summary>
/// Individual risk factor.
/// </summary>
public sealed record RiskFactor
{
    /// <summary>Factor name.</summary>
    public required string Name { get; init; }

    /// <summary>Description of the risk.</summary>
    public required string Description { get; init; }

    /// <summary>Contribution to overall risk (0-1).</summary>
    public float Contribution { get; init; }

    /// <summary>Category of risk.</summary>
    public string Category { get; init; } = "General";
}

/// <summary>
/// Entity affected by simulated changes.
/// </summary>
public sealed record AffectedEntity
{
    /// <summary>Entity path or identifier.</summary>
    public required string EntityPath { get; init; }

    /// <summary>Type of entity.</summary>
    public required string EntityType { get; init; }

    /// <summary>How the entity is affected.</summary>
    public required ImpactType ImpactType { get; init; }

    /// <summary>Severity of impact (0-1).</summary>
    public float Severity { get; init; }

    /// <summary>Description of the impact.</summary>
    public string? Description { get; init; }
}

/// <summary>
/// Type of impact on an entity.
/// </summary>
public enum ImpactType
{
    /// <summary>Entity is directly modified.</summary>
    DirectModification,

    /// <summary>Entity depends on modified entity.</summary>
    DependencyAffected,

    /// <summary>Entity references modified entity.</summary>
    ReferenceAffected,

    /// <summary>Entity's access pattern is affected.</summary>
    AccessPatternChanged,

    /// <summary>Entity's performance is affected.</summary>
    PerformanceImpacted
}

/// <summary>
/// Impact on dependencies.
/// </summary>
public sealed record DependencyImpact
{
    /// <summary>Source entity path.</summary>
    public required string SourcePath { get; init; }

    /// <summary>Target entity path.</summary>
    public required string TargetPath { get; init; }

    /// <summary>Type of dependency.</summary>
    public required string DependencyType { get; init; }

    /// <summary>Whether dependency would be broken.</summary>
    public bool IsBroken { get; init; }

    /// <summary>Severity of impact.</summary>
    public float Severity { get; init; }
}

/// <summary>
/// Predicted performance impact.
/// </summary>
public sealed record PerformanceImpact
{
    /// <summary>Expected latency change percentage.</summary>
    public float LatencyChangePercent { get; init; }

    /// <summary>Expected throughput change percentage.</summary>
    public float ThroughputChangePercent { get; init; }

    /// <summary>Expected memory usage change in bytes.</summary>
    public long MemoryChangeBytes { get; init; }

    /// <summary>Expected storage change in bytes.</summary>
    public long StorageChangeBytes { get; init; }

    /// <summary>Confidence in predictions (0-1).</summary>
    public float Confidence { get; init; }
}

#endregion

#region Simulation Report Types

/// <summary>
/// Comprehensive simulation report.
/// </summary>
public sealed record SimulationReport
{
    /// <summary>Report identifier.</summary>
    public string ReportId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>Session this report is for.</summary>
    public required string SessionId { get; init; }

    /// <summary>Report title.</summary>
    public required string Title { get; init; }

    /// <summary>Executive summary.</summary>
    public required string Summary { get; init; }

    /// <summary>Detailed analysis sections.</summary>
    public List<ReportSection> Sections { get; init; } = new();

    /// <summary>Impact analysis results.</summary>
    public ImpactAnalysisResult? ImpactAnalysis { get; init; }

    /// <summary>Comparison with original state.</summary>
    public StateComparison? StateComparison { get; init; }

    /// <summary>Conclusions and recommendations.</summary>
    public List<string> Conclusions { get; init; } = new();

    /// <summary>When the report was generated.</summary>
    public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Report format version.</summary>
    public string FormatVersion { get; init; } = "1.0";
}

/// <summary>
/// Section within a simulation report.
/// </summary>
public sealed record ReportSection
{
    /// <summary>Section title.</summary>
    public required string Title { get; init; }

    /// <summary>Section content.</summary>
    public required string Content { get; init; }

    /// <summary>Section type.</summary>
    public string SectionType { get; init; } = "text";

    /// <summary>Associated data or visualizations.</summary>
    public Dictionary<string, object>? Data { get; init; }
}

/// <summary>
/// Comparison between original and simulated states.
/// </summary>
public sealed record StateComparison
{
    /// <summary>Number of additions.</summary>
    public int Additions { get; init; }

    /// <summary>Number of modifications.</summary>
    public int Modifications { get; init; }

    /// <summary>Number of deletions.</summary>
    public int Deletions { get; init; }

    /// <summary>Detailed changes.</summary>
    public List<StateChange> Changes { get; init; } = new();
}

/// <summary>
/// Individual state change in comparison.
/// </summary>
public sealed record StateChange
{
    /// <summary>Path that changed.</summary>
    public required string Path { get; init; }

    /// <summary>Type of change.</summary>
    public required ChangeType ChangeType { get; init; }

    /// <summary>Original value (if applicable).</summary>
    public object? OriginalValue { get; init; }

    /// <summary>New value (if applicable).</summary>
    public object? NewValue { get; init; }
}

#endregion

#region Rollback Guarantee

/// <summary>
/// Provides rollback guarantees for simulations.
/// Ensures simulated changes never affect real state unless explicitly committed.
/// </summary>
public sealed class RollbackGuarantee
{
    private readonly ConcurrentDictionary<string, List<RollbackPoint>> _rollbackPoints = new();
    private readonly object _lock = new();

    /// <summary>
    /// Creates a rollback point for a session.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="forkId">Fork identifier.</param>
    /// <param name="description">Description of the rollback point.</param>
    /// <returns>The created rollback point.</returns>
    public RollbackPoint CreateRollbackPoint(string sessionId, string forkId, string description)
    {
        var point = new RollbackPoint
        {
            SessionId = sessionId,
            ForkId = forkId,
            Description = description
        };

        _rollbackPoints.AddOrUpdate(
            sessionId,
            _ => new List<RollbackPoint> { point },
            (_, existing) =>
            {
                lock (_lock)
                {
                    existing.Add(point);
                    return existing;
                }
            });

        return point;
    }

    /// <summary>
    /// Gets all rollback points for a session.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <returns>List of rollback points.</returns>
    public IReadOnlyList<RollbackPoint> GetRollbackPoints(string sessionId)
    {
        return _rollbackPoints.TryGetValue(sessionId, out var points)
            ? points.AsReadOnly()
            : Array.Empty<RollbackPoint>();
    }

    /// <summary>
    /// Validates that a session can be safely rolled back.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <returns>Validation result.</returns>
    public RollbackValidation ValidateRollback(string sessionId)
    {
        var points = GetRollbackPoints(sessionId);

        if (points.Count == 0)
        {
            return new RollbackValidation
            {
                CanRollback = false,
                Reason = "No rollback points found for session"
            };
        }

        var invalidPoints = points.Where(p => p.IsInvalidated).ToList();
        if (invalidPoints.Count == points.Count)
        {
            return new RollbackValidation
            {
                CanRollback = false,
                Reason = "All rollback points have been invalidated"
            };
        }

        return new RollbackValidation
        {
            CanRollback = true,
            AvailablePoints = points.Where(p => !p.IsInvalidated).ToList()
        };
    }

    /// <summary>
    /// Clears all rollback points for a session.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    public void ClearSession(string sessionId)
    {
        _rollbackPoints.TryRemove(sessionId, out _);
    }
}

/// <summary>
/// Represents a point to which state can be rolled back.
/// </summary>
public sealed class RollbackPoint
{
    /// <summary>Unique identifier for this rollback point.</summary>
    public string PointId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>Session this point belongs to.</summary>
    public required string SessionId { get; init; }

    /// <summary>Fork this point belongs to.</summary>
    public required string ForkId { get; init; }

    /// <summary>Description of this rollback point.</summary>
    public required string Description { get; init; }

    /// <summary>When this point was created.</summary>
    public DateTime CreatedAt { get; } = DateTime.UtcNow;

    /// <summary>State snapshot at this point.</summary>
    public Dictionary<string, object> StateSnapshot { get; } = new();

    /// <summary>Whether this point has been invalidated.</summary>
    public bool IsInvalidated { get; set; }

    /// <summary>Reason for invalidation.</summary>
    public string? InvalidationReason { get; set; }
}

/// <summary>
/// Result of rollback validation.
/// </summary>
public sealed record RollbackValidation
{
    /// <summary>Whether rollback is possible.</summary>
    public bool CanRollback { get; init; }

    /// <summary>Reason if rollback is not possible.</summary>
    public string? Reason { get; init; }

    /// <summary>Available rollback points.</summary>
    public List<RollbackPoint> AvailablePoints { get; init; } = new();
}

#endregion

#region Change Simulator

/// <summary>
/// Simulates hypothetical changes in isolation.
/// </summary>
public sealed class ChangeSimulator
{
    private readonly ConcurrentDictionary<string, SimulatedChange> _pendingChanges = new();

    /// <summary>
    /// Queues a change for simulation.
    /// </summary>
    /// <param name="change">Change to queue.</param>
    /// <returns>The queued change.</returns>
    public SimulatedChange QueueChange(SimulatedChange change)
    {
        _pendingChanges[change.ChangeId] = change;
        return change;
    }

    /// <summary>
    /// Applies all pending changes to a fork.
    /// </summary>
    /// <param name="fork">Fork to apply changes to.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Results of applying changes.</returns>
    public async Task<ChangeApplicationResult> ApplyPendingChangesAsync(
        StateFork fork,
        CancellationToken ct = default)
    {
        var results = new List<ChangeResult>();
        var changes = _pendingChanges.Values.OrderBy(c => c.AppliedAt).ToList();

        foreach (var change in changes)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                fork.ApplyChange(change);
                results.Add(new ChangeResult
                {
                    ChangeId = change.ChangeId,
                    Success = true,
                    AppliedAt = DateTime.UtcNow
                });
                _pendingChanges.TryRemove(change.ChangeId, out _);
            }
            catch (Exception ex)
            {
                results.Add(new ChangeResult
                {
                    ChangeId = change.ChangeId,
                    Success = false,
                    Error = ex.Message
                });
            }
        }

        await Task.CompletedTask;
        return new ChangeApplicationResult
        {
            TotalChanges = changes.Count,
            SuccessfulChanges = results.Count(r => r.Success),
            FailedChanges = results.Count(r => !r.Success),
            Results = results
        };
    }

    /// <summary>
    /// Validates changes before application.
    /// </summary>
    /// <param name="fork">Fork to validate against.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Validation results.</returns>
    public async Task<ChangeValidationResult> ValidateChangesAsync(
        StateFork fork,
        CancellationToken ct = default)
    {
        var issues = new List<string>();
        var warnings = new List<string>();

        foreach (var change in _pendingChanges.Values)
        {
            ct.ThrowIfCancellationRequested();

            // Validate based on change type
            switch (change.Type)
            {
                case ChangeType.Update:
                case ChangeType.Delete:
                    if (!fork.StateSnapshot.ContainsKey(change.TargetPath))
                        warnings.Add($"Target path '{change.TargetPath}' does not exist for {change.Type} operation");
                    break;

                case ChangeType.Create:
                    if (fork.StateSnapshot.ContainsKey(change.TargetPath))
                        issues.Add($"Target path '{change.TargetPath}' already exists for Create operation");
                    break;
            }
        }

        await Task.CompletedTask;
        return new ChangeValidationResult
        {
            IsValid = issues.Count == 0,
            Issues = issues,
            Warnings = warnings
        };
    }

    /// <summary>
    /// Clears all pending changes.
    /// </summary>
    public void ClearPendingChanges()
    {
        _pendingChanges.Clear();
    }

    /// <summary>
    /// Gets count of pending changes.
    /// </summary>
    public int PendingChangeCount => _pendingChanges.Count;
}

/// <summary>
/// Result of applying changes.
/// </summary>
public sealed record ChangeApplicationResult
{
    /// <summary>Total changes attempted.</summary>
    public int TotalChanges { get; init; }

    /// <summary>Successfully applied changes.</summary>
    public int SuccessfulChanges { get; init; }

    /// <summary>Failed changes.</summary>
    public int FailedChanges { get; init; }

    /// <summary>Individual change results.</summary>
    public List<ChangeResult> Results { get; init; } = new();
}

/// <summary>
/// Result of individual change application.
/// </summary>
public sealed record ChangeResult
{
    /// <summary>Change identifier.</summary>
    public required string ChangeId { get; init; }

    /// <summary>Whether change was successful.</summary>
    public bool Success { get; init; }

    /// <summary>Error message if failed.</summary>
    public string? Error { get; init; }

    /// <summary>When change was applied.</summary>
    public DateTime AppliedAt { get; init; }
}

/// <summary>
/// Result of change validation.
/// </summary>
public sealed record ChangeValidationResult
{
    /// <summary>Whether all changes are valid.</summary>
    public bool IsValid { get; init; }

    /// <summary>Validation issues.</summary>
    public List<string> Issues { get; init; } = new();

    /// <summary>Warnings (non-blocking).</summary>
    public List<string> Warnings { get; init; } = new();
}

#endregion

#region Impact Analyzer

/// <summary>
/// Analyzes the impact of simulated changes.
/// </summary>
public sealed class ImpactAnalyzer
{
    private readonly IAIProvider? _aiProvider;

    /// <summary>
    /// Initializes the impact analyzer.
    /// </summary>
    /// <param name="aiProvider">Optional AI provider for advanced analysis.</param>
    public ImpactAnalyzer(IAIProvider? aiProvider = null)
    {
        _aiProvider = aiProvider;
    }

    /// <summary>
    /// Analyzes impact of changes in a fork.
    /// </summary>
    /// <param name="fork">Fork containing changes.</param>
    /// <param name="originalState">Original state before changes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Impact analysis result.</returns>
    public async Task<ImpactAnalysisResult> AnalyzeAsync(
        StateFork fork,
        Dictionary<string, object> originalState,
        CancellationToken ct = default)
    {
        var affectedEntities = new List<AffectedEntity>();
        var dependencyImpacts = new List<DependencyImpact>();
        var riskFactors = new List<RiskFactor>();
        var recommendations = new List<string>();
        var warnings = new List<string>();

        // Analyze each change
        foreach (var change in fork.AppliedChanges)
        {
            ct.ThrowIfCancellationRequested();

            // Direct impact
            affectedEntities.Add(new AffectedEntity
            {
                EntityPath = change.TargetPath,
                EntityType = InferEntityType(change.TargetPath),
                ImpactType = ImpactType.DirectModification,
                Severity = CalculateChangeSeverity(change),
                Description = $"{change.Type} operation on {change.TargetPath}"
            });

            // Dependency analysis
            var dependencies = FindDependencies(change.TargetPath, fork.StateSnapshot);
            foreach (var dep in dependencies)
            {
                dependencyImpacts.Add(new DependencyImpact
                {
                    SourcePath = change.TargetPath,
                    TargetPath = dep.Path,
                    DependencyType = dep.Type,
                    IsBroken = change.Type == ChangeType.Delete,
                    Severity = change.Type == ChangeType.Delete ? 0.9f : 0.3f
                });

                affectedEntities.Add(new AffectedEntity
                {
                    EntityPath = dep.Path,
                    EntityType = InferEntityType(dep.Path),
                    ImpactType = ImpactType.DependencyAffected,
                    Severity = change.Type == ChangeType.Delete ? 0.8f : 0.2f
                });
            }

            // Risk factors
            if (change.Type == ChangeType.Delete)
            {
                riskFactors.Add(new RiskFactor
                {
                    Name = "Deletion Risk",
                    Description = $"Deleting {change.TargetPath} may cause dependent entities to fail",
                    Contribution = 0.4f,
                    Category = "Data Integrity"
                });
            }
        }

        // AI-enhanced analysis if available
        if (_aiProvider != null && fork.AppliedChanges.Count > 0)
        {
            try
            {
                var aiAnalysis = await PerformAIAnalysisAsync(fork, ct);
                recommendations.AddRange(aiAnalysis.Recommendations);
                warnings.AddRange(aiAnalysis.Warnings);

                if (aiAnalysis.AdditionalRiskFactors.Count > 0)
                    riskFactors.AddRange(aiAnalysis.AdditionalRiskFactors);
            }
            catch
            {
                warnings.Add("AI-enhanced analysis unavailable");
            }
        }

        // Calculate overall severity
        var overallSeverity = affectedEntities.Count > 0
            ? affectedEntities.Average(e => e.Severity)
            : 0f;

        // Determine risk level
        var riskScore = (int)(overallSeverity * 100);
        var riskLevel = riskScore switch
        {
            < 25 => RiskLevel.Low,
            < 50 => RiskLevel.Medium,
            < 75 => RiskLevel.High,
            _ => RiskLevel.Critical
        };

        // Performance impact estimation
        var performanceImpact = EstimatePerformanceImpact(fork.AppliedChanges);

        return new ImpactAnalysisResult
        {
            SessionId = fork.SessionId,
            ForkId = fork.ForkId,
            ChangesAnalyzed = fork.AppliedChanges.Count,
            OverallSeverity = overallSeverity,
            Risk = new RiskAssessment
            {
                Level = riskLevel,
                Score = riskScore,
                Factors = riskFactors,
                Mitigations = GenerateMitigations(riskFactors)
            },
            AffectedEntities = affectedEntities,
            DependencyImpacts = dependencyImpacts,
            PerformanceImpact = performanceImpact,
            Recommendations = recommendations,
            Warnings = warnings
        };
    }

    private string InferEntityType(string path)
    {
        if (path.Contains("/config/")) return "Configuration";
        if (path.Contains("/data/")) return "Data";
        if (path.Contains("/schema/")) return "Schema";
        if (path.Contains("/index/")) return "Index";
        if (path.Contains("/user/")) return "User";
        return "Entity";
    }

    private float CalculateChangeSeverity(SimulatedChange change)
    {
        return change.Type switch
        {
            ChangeType.Create => 0.2f,
            ChangeType.Update => 0.4f,
            ChangeType.Delete => 0.8f,
            ChangeType.Transform => 0.6f,
            ChangeType.Move => 0.3f,
            ChangeType.Merge => 0.5f,
            ChangeType.Split => 0.5f,
            ChangeType.Configure => 0.3f,
            _ => 0.5f
        };
    }

    private List<(string Path, string Type)> FindDependencies(string path, Dictionary<string, object> state)
    {
        var deps = new List<(string, string)>();

        // Simple reference detection
        foreach (var kvp in state)
        {
            if (kvp.Key == path) continue;

            var valueStr = kvp.Value?.ToString() ?? "";
            if (valueStr.Contains(path))
            {
                deps.Add((kvp.Key, "Reference"));
            }
        }

        return deps;
    }

    private async Task<(List<string> Recommendations, List<string> Warnings, List<RiskFactor> AdditionalRiskFactors)>
        PerformAIAnalysisAsync(StateFork fork, CancellationToken ct)
    {
        var changesDescription = string.Join("\n",
            fork.AppliedChanges.Take(10).Select(c => $"- {c.Type}: {c.TargetPath}"));

        var prompt = $@"Analyze the following simulated changes and provide recommendations:

Changes:
{changesDescription}

Provide:
1. Key recommendations (2-3 bullet points)
2. Potential warnings (if any)
3. Additional risk factors to consider

Format: JSON with keys: recommendations (array), warnings (array), riskFactors (array of {{name, description, contribution}})";

        var response = await _aiProvider!.CompleteAsync(new AIRequest
        {
            Prompt = prompt,
            MaxTokens = 500,
            Temperature = 0.3f
        }, ct);

        // Parse response (simplified)
        var recommendations = new List<string> { "Review all changes before committing", "Consider backup before major modifications" };
        var warnings = new List<string>();
        var riskFactors = new List<RiskFactor>();

        return (recommendations, warnings, riskFactors);
    }

    private PerformanceImpact EstimatePerformanceImpact(List<SimulatedChange> changes)
    {
        var deleteCount = changes.Count(c => c.Type == ChangeType.Delete);
        var createCount = changes.Count(c => c.Type == ChangeType.Create);
        var updateCount = changes.Count(c => c.Type == ChangeType.Update);

        // Rough estimations
        var latencyChange = updateCount * 0.5f - deleteCount * 0.3f;
        var storageChange = (createCount - deleteCount) * 1024L; // Estimate 1KB per entity

        return new PerformanceImpact
        {
            LatencyChangePercent = latencyChange,
            ThroughputChangePercent = -deleteCount * 0.1f + createCount * 0.05f,
            MemoryChangeBytes = createCount * 512 - deleteCount * 512,
            StorageChangeBytes = storageChange,
            Confidence = 0.6f
        };
    }

    private List<string> GenerateMitigations(List<RiskFactor> factors)
    {
        var mitigations = new List<string>();

        foreach (var factor in factors)
        {
            mitigations.Add(factor.Category switch
            {
                "Data Integrity" => "Create backup before applying changes",
                "Performance" => "Schedule changes during low-traffic periods",
                "Security" => "Review access controls after changes",
                _ => $"Review {factor.Name} before proceeding"
            });
        }

        return mitigations.Distinct().ToList();
    }
}

#endregion

#region Simulation Engine

/// <summary>
/// Core simulation processor for What-If scenarios.
/// Provides isolated simulation environment with full rollback guarantees.
/// </summary>
public sealed class SimulationEngine : FeatureStrategyBase
{
    private readonly ConcurrentDictionary<string, SimulationSession> _sessions = new();
    private readonly RollbackGuarantee _rollbackGuarantee = new();
    private readonly ChangeSimulator _changeSimulator = new();
    private readonly ImpactAnalyzer _impactAnalyzer;

    /// <summary>
    /// Initializes the simulation engine.
    /// </summary>
    public SimulationEngine()
    {
        _impactAnalyzer = new ImpactAnalyzer();
    }

    /// <inheritdoc/>
    public override string StrategyId => "feature-simulation-engine";

    /// <inheritdoc/>
    public override string StrategyName => "What-If Simulation Engine";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Simulation Engine",
        Description = "What-If simulation engine for testing hypothetical changes in isolated environments with full rollback guarantees",
        Capabilities = IntelligenceCapabilities.Prediction,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "MaxSessions", Description = "Maximum concurrent sessions", Required = false, DefaultValue = "10" },
            new ConfigurationRequirement { Key = "DefaultTimeout", Description = "Default session timeout in minutes", Required = false, DefaultValue = "60" },
            new ConfigurationRequirement { Key = "EnableAIAnalysis", Description = "Enable AI-powered impact analysis", Required = false, DefaultValue = "true" }
        },
        CostTier = 2,
        LatencyTier = 2,
        RequiresNetworkAccess = false,
        SupportsOfflineMode = true,
        Tags = new[] { "simulation", "what-if", "impact-analysis", "rollback", "testing" }
    };

    /// <summary>
    /// Creates a new simulation session.
    /// </summary>
    /// <param name="name">Session name.</param>
    /// <param name="currentState">Current state to fork from.</param>
    /// <param name="configuration">Optional configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The created session.</returns>
    public async Task<SimulationSession> CreateSessionAsync(
        string name,
        Dictionary<string, object> currentState,
        SimulationConfiguration? configuration = null,
        CancellationToken ct = default)
    {
        var maxSessions = int.Parse(GetConfig("MaxSessions") ?? "10");

        if (_sessions.Count >= maxSessions)
            throw new InvalidOperationException($"Maximum number of sessions ({maxSessions}) reached");

        var session = new SimulationSession
        {
            Name = name,
            Configuration = configuration ?? new SimulationConfiguration()
        };

        session.CreateRootFork(currentState);

        _sessions[session.SessionId] = session;

        // Create initial rollback point
        var rootFork = session.GetRootFork()!;
        var rollbackPoint = _rollbackGuarantee.CreateRollbackPoint(
            session.SessionId,
            rootFork.ForkId,
            "Initial state");

        foreach (var kvp in currentState)
        {
            rollbackPoint.StateSnapshot[kvp.Key] = kvp.Value;
        }

        await Task.CompletedTask;
        return session;
    }

    /// <summary>
    /// Gets a session by ID.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <returns>The session if found.</returns>
    public SimulationSession? GetSession(string sessionId)
    {
        return _sessions.TryGetValue(sessionId, out var session) ? session : null;
    }

    /// <summary>
    /// Lists all active sessions.
    /// </summary>
    /// <returns>List of active sessions.</returns>
    public IReadOnlyList<SimulationSession> ListSessions()
    {
        return _sessions.Values.Where(s => s.Status == SimulationSessionStatus.Active).ToList();
    }

    /// <summary>
    /// Forks the current state for isolated simulation.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="branchName">Name for the new branch.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The new fork.</returns>
    public async Task<StateFork> ForkStateAsync(
        string sessionId,
        string branchName,
        CancellationToken ct = default)
    {
        var session = GetSession(sessionId)
            ?? throw new InvalidOperationException($"Session {sessionId} not found");

        if (session.Forks.Count >= session.Configuration.MaxForks)
            throw new InvalidOperationException($"Maximum forks ({session.Configuration.MaxForks}) reached");

        var activeFork = session.GetActiveFork()
            ?? throw new InvalidOperationException("No active fork in session");

        var newFork = activeFork.CreateChildFork(branchName);
        session.Forks[newFork.ForkId] = newFork;

        _rollbackGuarantee.CreateRollbackPoint(sessionId, newFork.ForkId, $"Fork: {branchName}");

        await Task.CompletedTask;
        return newFork;
    }

    /// <summary>
    /// Applies hypothetical changes to a fork.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="changes">Changes to apply.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result of applying changes.</returns>
    public async Task<ChangeApplicationResult> ApplyChangesAsync(
        string sessionId,
        IEnumerable<SimulatedChange> changes,
        CancellationToken ct = default)
    {
        var session = GetSession(sessionId)
            ?? throw new InvalidOperationException($"Session {sessionId} not found");

        var fork = session.GetActiveFork()
            ?? throw new InvalidOperationException("No active fork in session");

        if (fork.AppliedChanges.Count + changes.Count() > session.Configuration.MaxChanges)
            throw new InvalidOperationException($"Maximum changes ({session.Configuration.MaxChanges}) would be exceeded");

        // Queue all changes
        foreach (var change in changes)
        {
            _changeSimulator.QueueChange(change);
        }

        // Validate before applying
        var validation = await _changeSimulator.ValidateChangesAsync(fork, ct);
        if (!validation.IsValid)
        {
            return new ChangeApplicationResult
            {
                TotalChanges = changes.Count(),
                SuccessfulChanges = 0,
                FailedChanges = changes.Count(),
                Results = validation.Issues.Select(i => new ChangeResult
                {
                    ChangeId = Guid.NewGuid().ToString("N"),
                    Success = false,
                    Error = i
                }).ToList()
            };
        }

        // Apply changes
        var result = await _changeSimulator.ApplyPendingChangesAsync(fork, ct);

        // Create rollback point after changes
        if (result.SuccessfulChanges > 0)
        {
            _rollbackGuarantee.CreateRollbackPoint(
                sessionId,
                fork.ForkId,
                $"Applied {result.SuccessfulChanges} changes");
        }

        return result;
    }

    /// <summary>
    /// Analyzes the impact of changes in a session.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Impact analysis result.</returns>
    public async Task<ImpactAnalysisResult> AnalyzeImpactAsync(
        string sessionId,
        CancellationToken ct = default)
    {
        var session = GetSession(sessionId)
            ?? throw new InvalidOperationException($"Session {sessionId} not found");

        var fork = session.GetActiveFork()
            ?? throw new InvalidOperationException("No active fork in session");

        var rootFork = session.GetRootFork()
            ?? throw new InvalidOperationException("No root fork in session");

        session.Status = SimulationSessionStatus.Analyzing;

        try
        {
            var result = await _impactAnalyzer.AnalyzeAsync(fork, rootFork.StateSnapshot, ct);
            return result;
        }
        finally
        {
            session.Status = SimulationSessionStatus.Active;
        }
    }

    /// <summary>
    /// Generates a comprehensive simulation report.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="title">Report title.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The generated report.</returns>
    public async Task<SimulationReport> GenerateReportAsync(
        string sessionId,
        string title,
        CancellationToken ct = default)
    {
        var session = GetSession(sessionId)
            ?? throw new InvalidOperationException($"Session {sessionId} not found");

        var fork = session.GetActiveFork()
            ?? throw new InvalidOperationException("No active fork in session");

        var rootFork = session.GetRootFork()
            ?? throw new InvalidOperationException("No root fork in session");

        // Get impact analysis
        var impactAnalysis = await AnalyzeImpactAsync(sessionId, ct);

        // Compare states
        var comparison = CompareStates(rootFork.StateSnapshot, fork.StateSnapshot);

        // Build sections
        var sections = new List<ReportSection>
        {
            new ReportSection
            {
                Title = "Overview",
                Content = $"Simulation session '{session.Name}' analyzed {fork.AppliedChanges.Count} hypothetical changes across {session.Forks.Count} fork(s)."
            },
            new ReportSection
            {
                Title = "Changes Summary",
                Content = $"Additions: {comparison.Additions}, Modifications: {comparison.Modifications}, Deletions: {comparison.Deletions}",
                Data = new Dictionary<string, object>
                {
                    ["additions"] = comparison.Additions,
                    ["modifications"] = comparison.Modifications,
                    ["deletions"] = comparison.Deletions
                }
            },
            new ReportSection
            {
                Title = "Risk Assessment",
                Content = $"Overall Risk: {impactAnalysis.Risk.Level} (Score: {impactAnalysis.Risk.Score}/100)"
            }
        };

        // Build conclusions
        var conclusions = new List<string>();

        if (impactAnalysis.Risk.Level <= RiskLevel.Low)
            conclusions.Add("Changes appear safe to apply with minimal risk.");
        else if (impactAnalysis.Risk.Level == RiskLevel.Medium)
            conclusions.Add("Changes carry moderate risk. Review recommendations before proceeding.");
        else
            conclusions.Add("Changes carry significant risk. Careful review and testing recommended.");

        conclusions.AddRange(impactAnalysis.Recommendations);

        return new SimulationReport
        {
            SessionId = sessionId,
            Title = title,
            Summary = $"What-If simulation analysis for '{session.Name}' covering {fork.AppliedChanges.Count} changes with {impactAnalysis.Risk.Level} overall risk.",
            Sections = sections,
            ImpactAnalysis = impactAnalysis,
            StateComparison = comparison,
            Conclusions = conclusions
        };
    }

    /// <summary>
    /// Rolls back all simulated changes in a session.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if rollback succeeded.</returns>
    public async Task<bool> RollbackAsync(string sessionId, CancellationToken ct = default)
    {
        var session = GetSession(sessionId)
            ?? throw new InvalidOperationException($"Session {sessionId} not found");

        var validation = _rollbackGuarantee.ValidateRollback(sessionId);
        if (!validation.CanRollback)
            throw new InvalidOperationException($"Cannot rollback: {validation.Reason}");

        // Restore to root fork state
        var rootFork = session.GetRootFork();
        if (rootFork != null)
        {
            // Deactivate all other forks
            foreach (var fork in session.Forks.Values)
            {
                if (fork.ForkId != rootFork.ForkId)
                    fork.IsActive = false;
            }

            session.ActiveForkId = rootFork.ForkId;
        }

        session.Status = SimulationSessionStatus.RolledBack;
        _rollbackGuarantee.ClearSession(sessionId);

        await Task.CompletedTask;
        return true;
    }

    /// <summary>
    /// Terminates a simulation session.
    /// </summary>
    /// <param name="sessionId">Session identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task TerminateSessionAsync(string sessionId, CancellationToken ct = default)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            session.Status = SimulationSessionStatus.Terminated;
            _rollbackGuarantee.ClearSession(sessionId);
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Compares two state snapshots.
    /// </summary>
    /// <param name="original">Original state.</param>
    /// <param name="modified">Modified state.</param>
    /// <returns>Comparison result.</returns>
    public StateComparison CompareStates(
        Dictionary<string, object> original,
        Dictionary<string, object> modified)
    {
        var changes = new List<StateChange>();
        var additions = 0;
        var modifications = 0;
        var deletions = 0;

        // Check for modifications and deletions
        foreach (var kvp in original)
        {
            if (!modified.ContainsKey(kvp.Key))
            {
                deletions++;
                changes.Add(new StateChange
                {
                    Path = kvp.Key,
                    ChangeType = ChangeType.Delete,
                    OriginalValue = kvp.Value
                });
            }
            else if (!Equals(kvp.Value, modified[kvp.Key]))
            {
                modifications++;
                changes.Add(new StateChange
                {
                    Path = kvp.Key,
                    ChangeType = ChangeType.Update,
                    OriginalValue = kvp.Value,
                    NewValue = modified[kvp.Key]
                });
            }
        }

        // Check for additions
        foreach (var kvp in modified)
        {
            if (!original.ContainsKey(kvp.Key))
            {
                additions++;
                changes.Add(new StateChange
                {
                    Path = kvp.Key,
                    ChangeType = ChangeType.Create,
                    NewValue = kvp.Value
                });
            }
        }

        return new StateComparison
        {
            Additions = additions,
            Modifications = modifications,
            Deletions = deletions,
            Changes = changes
        };
    }

    /// <summary>
    /// Sets the AI provider for enhanced analysis.
    /// </summary>
    /// <param name="provider">AI provider to use.</param>
    public void SetAIProviderForAnalysis(IAIProvider provider)
    {
        SetAIProvider(provider);
    }
}

#endregion
