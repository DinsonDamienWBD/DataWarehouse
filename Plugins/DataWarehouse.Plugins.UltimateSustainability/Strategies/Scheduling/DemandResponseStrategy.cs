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
        _checkTimer = new Timer(async _ => { try { await CheckForEventsAsync(); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Timer callback failed: {ex.Message}"); } }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
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

    /// <summary>
    /// DR API endpoint. When set, CheckForEventsAsync will attempt HTTP polling.
    /// </summary>
    public string? DrApiEndpoint { get; set; }

    /// <summary>
    /// DR API key for authentication.
    /// </summary>
    public string? DrApiKey { get; set; }

    private async Task CheckForEventsAsync()
    {
        // Check if current active event has ended first
        if (_activeEvent != null && DateTimeOffset.UtcNow >= _activeEvent.EndTime)
        {
            EndParticipation();
        }

        // Poll DR aggregator API if configured
        if (!string.IsNullOrEmpty(DrApiEndpoint) && _activeEvent == null)
        {
            await PollDrApiAsync();
        }
    }

    private async Task PollDrApiAsync()
    {
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            if (!string.IsNullOrEmpty(DrApiKey))
                http.DefaultRequestHeaders.Add("Authorization", $"Bearer {DrApiKey}");

            using var response = await http.GetAsync(DrApiEndpoint);
            if (!response.IsSuccessStatusCode) return;

            var content = await response.Content.ReadAsStringAsync();
            var json = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(content);

            // Parse standard DR notification format (OpenADR-compatible JSON)
            if (json.TryGetProperty("event", out var eventObj) &&
                eventObj.TryGetProperty("eventId", out var eventId))
            {
                var start = eventObj.TryGetProperty("startTime", out var st)
                    ? st.GetDateTimeOffset()
                    : DateTimeOffset.UtcNow;
                var end = eventObj.TryGetProperty("endTime", out var et)
                    ? et.GetDateTimeOffset()
                    : start.AddHours(2);
                var reduction = eventObj.TryGetProperty("reductionPercent", out var rp)
                    ? rp.GetInt32()
                    : 20;
                var incentive = eventObj.TryGetProperty("incentivePerKwh", out var ip)
                    ? ip.GetDouble()
                    : 0.0;

                if (DateTimeOffset.UtcNow < end)
                {
                    var drEvent = new DemandResponseEvent
                    {
                        EventId = eventId.GetString() ?? Guid.NewGuid().ToString("N"),
                        StartTime = start,
                        EndTime = end,
                        RequestedReductionPercent = reduction,
                        IncentivePerKwh = incentive
                    };

                    if (AutoRespond)
                    {
                        await RespondToEventAsync(drEvent);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DemandResponse] API poll failed: {ex.Message}");
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
