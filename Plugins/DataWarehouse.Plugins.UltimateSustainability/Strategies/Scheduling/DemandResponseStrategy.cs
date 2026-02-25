namespace DataWarehouse.Plugins.UltimateSustainability.Strategies.Scheduling;

/// <summary>
/// Participates in grid demand response programs by reducing power consumption
/// during peak demand periods in exchange for financial incentives.
/// </summary>
public sealed class DemandResponseStrategy : SustainabilityStrategyBase
{
    private DemandResponseEvent? _activeEvent;
    private readonly List<DemandResponseEvent> _eventHistory = new();
    private Timer? _checkTimer;
    private readonly object _lock = new();

    /// <inheritdoc/>
    public override string StrategyId => "demand-response";
    /// <inheritdoc/>
    public override string DisplayName => "Demand Response";
    /// <inheritdoc/>
    public override SustainabilityCategory Category => SustainabilityCategory.Scheduling;
    /// <inheritdoc/>
    public override SustainabilityCapabilities Capabilities =>
        SustainabilityCapabilities.Scheduling | SustainabilityCapabilities.ExternalIntegration | SustainabilityCapabilities.ActiveControl;
    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Participates in grid demand response programs to reduce power during peak periods for incentives.";
    /// <inheritdoc/>
    public override string[] Tags => new[] { "demand-response", "grid", "peak", "incentive", "dr" };

    /// <summary>Auto-respond to DR events.</summary>
    public bool AutoRespond { get; set; } = true;
    /// <summary>Maximum power reduction percent.</summary>
    public int MaxReductionPercent { get; set; } = 30;

    /// <summary>Active DR event.</summary>
    public DemandResponseEvent? ActiveEvent { get { lock (_lock) return _activeEvent; } }

    /// <summary>Whether currently in DR event.</summary>
    public bool InDemandResponseEvent => _activeEvent != null;

    /// <inheritdoc/>
    protected override Task InitializeCoreAsync(CancellationToken ct)
    {
        _checkTimer = new Timer(async _ => await CheckForEventsAsync(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task DisposeCoreAsync()
    {
        _checkTimer?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Responds to a DR event.</summary>
    public async Task RespondToEventAsync(DemandResponseEvent drEvent, CancellationToken ct = default)
    {
        lock (_lock) _activeEvent = drEvent;
        // Would implement power reduction
        RecordOptimizationAction();
        await Task.CompletedTask;
        UpdateRecommendations();
    }

    /// <summary>Ends participation in current DR event.</summary>
    public void EndParticipation()
    {
        lock (_lock)
        {
            if (_activeEvent != null)
            {
                _eventHistory.Add(_activeEvent);
                _activeEvent = null;
            }
        }
        UpdateRecommendations();
    }

    private async Task CheckForEventsAsync()
    {
        // Would poll DR aggregator API
        // Simulate occasional events
        if (Random.Shared.NextDouble() < 0.01 && _activeEvent == null)
        {
            var drEvent = new DemandResponseEvent
            {
                EventId = Guid.NewGuid().ToString("N"),
                StartTime = DateTimeOffset.UtcNow,
                EndTime = DateTimeOffset.UtcNow.AddHours(2),
                RequestedReductionPercent = 20,
                IncentivePerKwh = 0.50
            };

            if (AutoRespond)
            {
                await RespondToEventAsync(drEvent);
            }
        }

        if (_activeEvent != null && DateTimeOffset.UtcNow >= _activeEvent.EndTime)
        {
            EndParticipation();
        }
    }

    private void UpdateRecommendations()
    {
        ClearRecommendations();
        if (_activeEvent != null)
        {
            AddRecommendation(new SustainabilityRecommendation
            {
                RecommendationId = $"{StrategyId}-active",
                Type = "ActiveEvent",
                Priority = 9,
                Description = $"Demand response event active. Reduce power by {_activeEvent.RequestedReductionPercent}%.",
                EstimatedCostSavingsUsd = _activeEvent.IncentivePerKwh * 10,
                CanAutoApply = true
            });
        }
    }
}

/// <summary>A demand response event.</summary>
public sealed record DemandResponseEvent
{
    public required string EventId { get; init; }
    public required DateTimeOffset StartTime { get; init; }
    public required DateTimeOffset EndTime { get; init; }
    public required int RequestedReductionPercent { get; init; }
    public required double IncentivePerKwh { get; init; }
}
