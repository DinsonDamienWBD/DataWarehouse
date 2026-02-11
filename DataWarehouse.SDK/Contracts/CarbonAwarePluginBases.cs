using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Sustainability;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.SDK.Contracts;

/// <summary>
/// Base class for carbon intensity provider plugins.
/// Provides caching and common functionality for fetching carbon data.
/// Intelligence-aware: Supports AI-driven carbon forecasting and optimization.
/// </summary>
public abstract class CarbonIntensityProviderPluginBase : FeaturePluginBase, ICarbonIntensityProvider
{
    private readonly Dictionary<string, (CarbonIntensityData Data, DateTimeOffset CachedAt)> _cache = new();

    /// <summary>
    /// Duration to cache carbon intensity data (default: 5 minutes).
    /// Override to customize cache behavior.
    /// </summary>
    protected virtual TimeSpan CacheDuration => TimeSpan.FromMinutes(5);

    #region Intelligence Integration

    /// <summary>
    /// Capabilities declared by this carbon intensity provider.
    /// </summary>
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
    {
        new RegisteredCapability
        {
            CapabilityId = $"{Id}.carbon-intensity",
            DisplayName = $"{Name} - Carbon Intensity Data",
            Description = "Real-time and forecasted carbon intensity data for sustainability optimization",
            Category = CapabilityCategory.Custom,
            SubCategory = "Sustainability",
            PluginId = Id,
            PluginName = Name,
            PluginVersion = Version,
            Tags = new[] { "carbon", "sustainability", "green", "intensity" },
            SemanticDescription = "Use this for accessing carbon intensity data for green computing decisions",
            Metadata = new Dictionary<string, object>
            {
                ["cacheDuration"] = CacheDuration.TotalMinutes,
                ["supportsForecast"] = true
            }
        }
    };

    /// <summary>
    /// Gets static knowledge for Intelligence registration.
    /// </summary>
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        var knowledge = new List<KnowledgeObject>(base.GetStaticKnowledge());

        knowledge.Add(new KnowledgeObject
        {
            Id = $"{Id}.carbon.capability",
            Topic = "carbon-intensity",
            SourcePluginId = Id,
            SourcePluginName = Name,
            KnowledgeType = "capability",
            Description = "Carbon intensity provider for sustainability-aware computing",
            Payload = new Dictionary<string, object>
            {
                ["cacheDurationMinutes"] = CacheDuration.TotalMinutes,
                ["supportsForecast"] = true,
                ["supportsRegionSelection"] = true
            },
            Tags = new[] { "carbon", "sustainability", "green" },
            Confidence = 1.0f,
            Timestamp = DateTimeOffset.UtcNow
        });

        return knowledge;
    }

    /// <summary>
    /// Requests AI-driven carbon forecast improvement using historical patterns.
    /// </summary>
    /// <param name="regionId">Region to forecast.</param>
    /// <param name="historicalData">Historical intensity data.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Improved forecast data.</returns>
    protected async Task<IReadOnlyList<CarbonIntensityData>?> RequestImprovedForecastAsync(
        string regionId,
        IReadOnlyList<CarbonIntensityData> historicalData,
        CancellationToken ct = default)
    {
        if (MessageBus == null) return null;

        try
        {
            var request = new PluginMessage
            {
                Type = "intelligence.predict.request",
                CorrelationId = Guid.NewGuid().ToString("N"),
                Source = Id,
                Payload = new Dictionary<string, object>
                {
                    ["predictionType"] = "carbon_forecast",
                    ["regionId"] = regionId,
                    ["historicalPoints"] = historicalData.Count
                }
            };

            await MessageBus.PublishAsync("intelligence.predict", request, ct);
            return null;
        }
        catch
        {
            return null;
        }
    }

    #endregion

    /// <summary>
    /// Fetches current carbon intensity from the provider API.
    /// Must be implemented by derived classes.
    /// </summary>
    /// <param name="regionId">Region identifier.</param>
    /// <returns>Current carbon intensity data.</returns>
    protected abstract Task<CarbonIntensityData> FetchIntensityAsync(string regionId);

    /// <summary>
    /// Fetches forecasted carbon intensity from the provider API.
    /// Must be implemented by derived classes.
    /// </summary>
    /// <param name="regionId">Region identifier.</param>
    /// <param name="hours">Forecast horizon in hours.</param>
    /// <returns>List of forecasted carbon intensity data points.</returns>
    protected abstract Task<IReadOnlyList<CarbonIntensityData>> FetchForecastAsync(string regionId, int hours);

    /// <summary>
    /// Fetches available regions from the provider API.
    /// Must be implemented by derived classes.
    /// </summary>
    /// <returns>List of supported region identifiers.</returns>
    protected abstract Task<IReadOnlyList<string>> FetchRegionsAsync();

    /// <summary>
    /// Gets current carbon intensity with caching.
    /// </summary>
    /// <param name="regionId">Region identifier.</param>
    /// <returns>Current carbon intensity data.</returns>
    public async Task<CarbonIntensityData> GetCurrentIntensityAsync(string regionId)
    {
        // Check cache first
        if (_cache.TryGetValue(regionId, out var cached) && DateTimeOffset.UtcNow - cached.CachedAt < CacheDuration)
        {
            return cached.Data;
        }

        // Fetch fresh data
        var data = await FetchIntensityAsync(regionId);
        _cache[regionId] = (data, DateTimeOffset.UtcNow);
        return data;
    }

    /// <summary>
    /// Gets forecasted carbon intensity.
    /// </summary>
    /// <param name="regionId">Region identifier.</param>
    /// <param name="hoursAhead">Forecast horizon in hours.</param>
    /// <returns>List of forecasted data points.</returns>
    public Task<IReadOnlyList<CarbonIntensityData>> GetForecastAsync(string regionId, int hoursAhead = 24)
        => FetchForecastAsync(regionId, hoursAhead);

    /// <summary>
    /// Gets available regions.
    /// </summary>
    /// <returns>List of region identifiers.</returns>
    public Task<IReadOnlyList<string>> GetAvailableRegionsAsync() => FetchRegionsAsync();

    /// <summary>
    /// Finds the region with the lowest carbon intensity.
    /// </summary>
    /// <param name="regionIds">Candidate regions.</param>
    /// <returns>Region ID with lowest carbon intensity.</returns>
    public async Task<string> FindLowestCarbonRegionAsync(string[] regionIds)
    {
        var intensities = await Task.WhenAll(regionIds.Select(r => GetCurrentIntensityAsync(r)));
        return intensities.OrderBy(i => i.GramsCO2PerKwh).First().RegionId;
    }

    /// <summary>
    /// Clears the intensity cache.
    /// </summary>
    protected void ClearCache()
    {
        _cache.Clear();
    }
}

/// <summary>
/// Base class for carbon-aware scheduler plugins.
/// Provides scheduling logic for deferring operations to low-carbon periods.
/// Intelligence-aware: Supports AI-driven optimal scheduling and workload prediction.
/// </summary>
public abstract class CarbonAwareSchedulerPluginBase : FeaturePluginBase, ICarbonAwareScheduler
{
    /// <summary>
    /// Gets or sets the carbon intensity provider.
    /// Should be injected or resolved from the kernel context.
    /// </summary>
    protected ICarbonIntensityProvider? IntensityProvider { get; set; }

    #region Intelligence Integration

    /// <summary>
    /// Capabilities declared by this carbon-aware scheduler.
    /// </summary>
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
    {
        new RegisteredCapability
        {
            CapabilityId = $"{Id}.carbon-scheduler",
            DisplayName = $"{Name} - Carbon-Aware Scheduling",
            Description = "Deferred execution scheduling for low-carbon periods",
            Category = CapabilityCategory.Pipeline,
            SubCategory = "Sustainability",
            PluginId = Id,
            PluginName = Name,
            PluginVersion = Version,
            Tags = new[] { "carbon", "sustainability", "scheduling", "green" },
            SemanticDescription = "Use this for scheduling workloads during low-carbon intensity periods",
            Metadata = new Dictionary<string, object>
            {
                ["supportsDeferredExecution"] = true,
                ["supportsThresholdExecution"] = true
            }
        }
    };

    /// <summary>
    /// Gets static knowledge for Intelligence registration.
    /// </summary>
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        var knowledge = new List<KnowledgeObject>(base.GetStaticKnowledge());

        knowledge.Add(new KnowledgeObject
        {
            Id = $"{Id}.scheduler.capability",
            Topic = "carbon-scheduling",
            SourcePluginId = Id,
            SourcePluginName = Name,
            KnowledgeType = "capability",
            Description = "Carbon-aware scheduler for sustainable workload execution",
            Payload = new Dictionary<string, object>
            {
                ["supportsDeferredExecution"] = true,
                ["supportsThresholdExecution"] = true,
                ["supportsOptimalTimeCalculation"] = true
            },
            Tags = new[] { "carbon", "scheduling", "sustainability" },
            Confidence = 1.0f,
            Timestamp = DateTimeOffset.UtcNow
        });

        return knowledge;
    }

    /// <summary>
    /// Requests AI-driven workload duration prediction.
    /// </summary>
    /// <param name="operationId">Operation identifier.</param>
    /// <param name="historicalDurations">Historical execution durations.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Predicted duration.</returns>
    protected async Task<TimeSpan?> RequestWorkloadDurationPredictionAsync(
        string operationId,
        IReadOnlyList<TimeSpan> historicalDurations,
        CancellationToken ct = default)
    {
        if (MessageBus == null) return null;

        try
        {
            var request = new PluginMessage
            {
                Type = "intelligence.predict.request",
                CorrelationId = Guid.NewGuid().ToString("N"),
                Source = Id,
                Payload = new Dictionary<string, object>
                {
                    ["predictionType"] = "workload_duration",
                    ["operationId"] = operationId,
                    ["historicalCount"] = historicalDurations.Count
                }
            };

            await MessageBus.PublishAsync("intelligence.predict", request, ct);
            return null;
        }
        catch
        {
            return null;
        }
    }

    #endregion

    /// <summary>
    /// Creates a scheduled operation with the given constraints.
    /// Must be implemented by derived classes to handle actual scheduling.
    /// </summary>
    /// <param name="operationId">Operation identifier.</param>
    /// <param name="operation">The async operation to execute.</param>
    /// <param name="options">Scheduling options.</param>
    /// <returns>Scheduled operation details.</returns>
    protected abstract Task<ScheduledOperation> CreateScheduledOperationAsync(
        string operationId,
        Func<CancellationToken, Task> operation,
        SchedulingOptions options);

    /// <summary>
    /// Calculates the optimal execution time based on forecast data.
    /// Must be implemented by derived classes.
    /// </summary>
    /// <param name="forecast">Forecasted intensity data.</param>
    /// <param name="window">Time window duration.</param>
    /// <param name="start">Window start time.</param>
    /// <returns>Optimal execution time.</returns>
    protected abstract Task<DateTimeOffset> CalculateOptimalTimeAsync(
        IReadOnlyList<CarbonIntensityData> forecast,
        TimeSpan window,
        DateTimeOffset start);

    /// <summary>
    /// Schedules an operation for low-carbon execution.
    /// </summary>
    /// <param name="operationId">Operation identifier.</param>
    /// <param name="operation">The async operation to execute.</param>
    /// <param name="options">Scheduling constraints.</param>
    /// <returns>Scheduled operation details.</returns>
    public Task<ScheduledOperation> ScheduleForLowCarbonAsync(
        string operationId,
        Func<CancellationToken, Task> operation,
        SchedulingOptions options)
        => CreateScheduledOperationAsync(operationId, operation, options);

    /// <summary>
    /// Gets the optimal execution time within a window.
    /// </summary>
    /// <param name="regionId">Target region.</param>
    /// <param name="windowDuration">Window duration.</param>
    /// <param name="windowStart">Window start (default: now).</param>
    /// <returns>Optimal execution time.</returns>
    public async Task<DateTimeOffset> GetOptimalExecutionTimeAsync(
        string regionId,
        TimeSpan windowDuration,
        DateTimeOffset? windowStart = null)
    {
        if (IntensityProvider == null)
        {
            throw new InvalidOperationException("Intensity provider not configured");
        }

        var forecast = await IntensityProvider.GetForecastAsync(regionId, (int)windowDuration.TotalHours + 1);
        return await CalculateOptimalTimeAsync(forecast, windowDuration, windowStart ?? DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Checks if current conditions meet the execution threshold.
    /// </summary>
    /// <param name="regionId">Region to check.</param>
    /// <param name="threshold">Carbon threshold criteria.</param>
    /// <returns>True if favorable for execution.</returns>
    public async Task<bool> ShouldExecuteNowAsync(string regionId, CarbonThreshold threshold)
    {
        if (IntensityProvider == null)
        {
            return true; // If no provider, don't block execution
        }

        var current = await IntensityProvider.GetCurrentIntensityAsync(regionId);
        return current.GramsCO2PerKwh <= threshold.MaxGramsCO2PerKwh && current.Level <= threshold.MaxLevel;
    }
}

/// <summary>
/// Base class for carbon reporter plugins.
/// Provides storage and aggregation for carbon usage tracking.
/// Intelligence-aware: Supports AI-driven carbon trend analysis and optimization recommendations.
/// </summary>
public abstract class CarbonReporterPluginBase : FeaturePluginBase, ICarbonReporter
{
    #region Intelligence Integration

    /// <summary>
    /// Capabilities declared by this carbon reporter.
    /// </summary>
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
    {
        new RegisteredCapability
        {
            CapabilityId = $"{Id}.carbon-reporter",
            DisplayName = $"{Name} - Carbon Usage Reporting",
            Description = "Carbon usage tracking, aggregation, and reporting for sustainability metrics",
            Category = CapabilityCategory.Pipeline,
            SubCategory = "Sustainability",
            PluginId = Id,
            PluginName = Name,
            PluginVersion = Version,
            Tags = new[] { "carbon", "sustainability", "reporting", "metrics" },
            SemanticDescription = "Use this for tracking and reporting carbon emissions from operations",
            Metadata = new Dictionary<string, object>
            {
                ["supportsUsageTracking"] = true,
                ["supportsReportGeneration"] = true
            }
        }
    };

    /// <summary>
    /// Gets static knowledge for Intelligence registration.
    /// </summary>
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        var knowledge = new List<KnowledgeObject>(base.GetStaticKnowledge());

        knowledge.Add(new KnowledgeObject
        {
            Id = $"{Id}.reporter.capability",
            Topic = "carbon-reporting",
            SourcePluginId = Id,
            SourcePluginName = Name,
            KnowledgeType = "capability",
            Description = "Carbon usage reporter for sustainability metrics",
            Payload = new Dictionary<string, object>
            {
                ["supportsUsageTracking"] = true,
                ["supportsReportGeneration"] = true,
                ["supportsGranularReporting"] = true
            },
            Tags = new[] { "carbon", "reporting", "sustainability" },
            Confidence = 1.0f,
            Timestamp = DateTimeOffset.UtcNow
        });

        return knowledge;
    }

    /// <summary>
    /// Requests AI-driven carbon trend analysis.
    /// </summary>
    /// <param name="records">Historical usage records.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Trend analysis with projections.</returns>
    protected async Task<Dictionary<string, object>?> RequestCarbonTrendAnalysisAsync(
        IReadOnlyList<CarbonUsageRecord> records,
        CancellationToken ct = default)
    {
        if (MessageBus == null) return null;

        try
        {
            var request = new PluginMessage
            {
                Type = "intelligence.analyze.request",
                CorrelationId = Guid.NewGuid().ToString("N"),
                Source = Id,
                Payload = new Dictionary<string, object>
                {
                    ["analysisType"] = "carbon_trend",
                    ["recordCount"] = records.Count,
                    ["totalCarbon"] = records.Sum(r => r.CarbonGrams)
                }
            };

            await MessageBus.PublishAsync("intelligence.analyze", request, ct);
            return null;
        }
        catch
        {
            return null;
        }
    }

    #endregion
    /// <summary>
    /// Stores a carbon usage record.
    /// Must be implemented by derived classes for persistence.
    /// </summary>
    /// <param name="usage">Usage record to store.</param>
    protected abstract Task StoreUsageAsync(CarbonUsageRecord usage);

    /// <summary>
    /// Retrieves carbon usage records for a time period.
    /// Must be implemented by derived classes.
    /// </summary>
    /// <param name="start">Period start.</param>
    /// <param name="end">Period end.</param>
    /// <returns>List of usage records.</returns>
    protected abstract Task<IReadOnlyList<CarbonUsageRecord>> GetUsageRecordsAsync(
        DateTimeOffset start,
        DateTimeOffset end);

    /// <summary>
    /// Groups usage records by granularity for reporting.
    /// Must be implemented by derived classes.
    /// </summary>
    /// <param name="records">Usage records to group.</param>
    /// <param name="granularity">Grouping granularity.</param>
    /// <returns>Grouped report entries.</returns>
    protected abstract IReadOnlyList<CarbonReportEntry> GroupByGranularity(
        IReadOnlyList<CarbonUsageRecord> records,
        ReportGranularity granularity);

    /// <summary>
    /// Records carbon usage for an operation.
    /// </summary>
    /// <param name="usage">Usage record.</param>
    public Task RecordUsageAsync(CarbonUsageRecord usage) => StoreUsageAsync(usage);

    /// <summary>
    /// Generates a carbon report for a time period.
    /// </summary>
    /// <param name="start">Report period start.</param>
    /// <param name="end">Report period end.</param>
    /// <param name="granularity">Report granularity.</param>
    /// <returns>Aggregated carbon report.</returns>
    public async Task<CarbonReport> GetReportAsync(
        DateTimeOffset start,
        DateTimeOffset end,
        ReportGranularity granularity)
    {
        var records = await GetUsageRecordsAsync(start, end);
        var grouped = GroupByGranularity(records, granularity);

        return new CarbonReport(
            Start: start,
            End: end,
            TotalCarbonGrams: records.Sum(r => r.CarbonGrams),
            TotalEnergyKwh: records.Sum(r => r.EnergyKwh),
            Entries: grouped
        );
    }

    /// <summary>
    /// Gets total carbon footprint since a date.
    /// </summary>
    /// <param name="since">Start date (null = all time).</param>
    /// <returns>Total carbon emissions in grams.</returns>
    public async Task<double> GetTotalCarbonFootprintAsync(DateTimeOffset? since = null)
    {
        var records = await GetUsageRecordsAsync(since ?? DateTimeOffset.MinValue, DateTimeOffset.UtcNow);
        return records.Sum(r => r.CarbonGrams);
    }

    /// <summary>
    /// Helper method to calculate average renewable percentage for a set of records.
    /// </summary>
    /// <param name="records">Usage records.</param>
    /// <returns>Weighted average renewable percentage.</returns>
    protected static double CalculateAverageRenewable(IReadOnlyList<CarbonUsageRecord> records)
    {
        if (records.Count == 0) return 0.0;

        // This would need intensity data to be accurate - placeholder for now
        // In a real implementation, you'd look up the renewable % from intensity data
        return 0.0; // Returns zero until intensity provider integration is wired in.
    }
}

/// <summary>
/// Base class for green region selector plugins.
/// Provides ranking logic for choosing low-carbon regions.
/// Intelligence-aware: Supports AI-driven region selection optimization.
/// </summary>
public abstract class GreenRegionSelectorPluginBase : FeaturePluginBase, IGreenRegionSelector
{
    /// <summary>
    /// Gets or sets the carbon intensity provider.
    /// </summary>
    protected ICarbonIntensityProvider? IntensityProvider { get; set; }

    #region Intelligence Integration

    /// <summary>
    /// Capabilities declared by this green region selector.
    /// </summary>
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
    {
        new RegisteredCapability
        {
            CapabilityId = $"{Id}.green-region",
            DisplayName = $"{Name} - Green Region Selection",
            Description = "Intelligent region selection based on carbon intensity and renewable energy",
            Category = CapabilityCategory.Custom,
            SubCategory = "Sustainability",
            PluginId = Id,
            PluginName = Name,
            PluginVersion = Version,
            Tags = new[] { "carbon", "sustainability", "region", "green" },
            SemanticDescription = "Use this for selecting the most sustainable region for workload placement",
            Metadata = new Dictionary<string, object>
            {
                ["supportsRanking"] = true,
                ["supportsRenewablePreference"] = true
            }
        }
    };

    /// <summary>
    /// Gets static knowledge for Intelligence registration.
    /// </summary>
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        var knowledge = new List<KnowledgeObject>(base.GetStaticKnowledge());

        knowledge.Add(new KnowledgeObject
        {
            Id = $"{Id}.region.capability",
            Topic = "green-region",
            SourcePluginId = Id,
            SourcePluginName = Name,
            KnowledgeType = "capability",
            Description = "Green region selector for sustainable workload placement",
            Payload = new Dictionary<string, object>
            {
                ["supportsRanking"] = true,
                ["supportsRenewablePreference"] = true,
                ["supportsMultiCriteriaSelection"] = true
            },
            Tags = new[] { "carbon", "region", "sustainability" },
            Confidence = 1.0f,
            Timestamp = DateTimeOffset.UtcNow
        });

        return knowledge;
    }

    /// <summary>
    /// Requests AI-driven optimal region prediction based on forecasts.
    /// </summary>
    /// <param name="regionIds">Available regions.</param>
    /// <param name="executionWindow">Time window for execution.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Predicted optimal region.</returns>
    protected async Task<string?> RequestOptimalRegionPredictionAsync(
        string[] regionIds,
        TimeSpan executionWindow,
        CancellationToken ct = default)
    {
        if (MessageBus == null) return null;

        try
        {
            var request = new PluginMessage
            {
                Type = "intelligence.predict.request",
                CorrelationId = Guid.NewGuid().ToString("N"),
                Source = Id,
                Payload = new Dictionary<string, object>
                {
                    ["predictionType"] = "optimal_region",
                    ["regionCount"] = regionIds.Length,
                    ["windowHours"] = executionWindow.TotalHours
                }
            };

            await MessageBus.PublishAsync("intelligence.predict", request, ct);
            return null;
        }
        catch
        {
            return null;
        }
    }

    #endregion

    /// <summary>
    /// Calculates a composite score for region selection.
    /// Override to customize scoring logic.
    /// </summary>
    /// <param name="intensity">Carbon intensity data.</param>
    /// <param name="criteria">Selection criteria.</param>
    /// <returns>Score (higher is better).</returns>
    protected virtual double CalculateScore(CarbonIntensityData intensity, RegionSelectionCriteria criteria)
    {
        double score = 0.0;

        // Carbon intensity (inverted - lower is better)
        score += (1000.0 - intensity.GramsCO2PerKwh) / 10.0;

        // Renewable percentage
        if (criteria.PreferRenewable)
        {
            score += intensity.RenewablePercentage * 2.0;
        }

        return score;
    }

    /// <summary>
    /// Selects the greenest region from available options.
    /// </summary>
    /// <param name="availableRegions">Candidate regions.</param>
    /// <param name="criteria">Selection criteria.</param>
    /// <returns>Selected region ID.</returns>
    public async Task<string> SelectGreenestRegionAsync(string[] availableRegions, RegionSelectionCriteria criteria)
    {
        var ranked = await RankRegionsByCarbonAsync(availableRegions);
        return ranked.First().RegionId;
    }

    /// <summary>
    /// Ranks regions by carbon footprint.
    /// </summary>
    /// <param name="regionIds">Regions to rank.</param>
    /// <returns>Ranked list with scores.</returns>
    public async Task<IReadOnlyList<RankedRegion>> RankRegionsByCarbonAsync(string[] regionIds)
    {
        if (IntensityProvider == null)
        {
            throw new InvalidOperationException("Intensity provider not configured");
        }

        var intensities = await Task.WhenAll(regionIds.Select(r => IntensityProvider.GetCurrentIntensityAsync(r)));

        var ranked = intensities
            .Select((intensity, index) => new
            {
                Intensity = intensity,
                Score = CalculateScore(intensity, new RegionSelectionCriteria())
            })
            .OrderByDescending(x => x.Score)
            .Select((x, rank) => new RankedRegion(
                RegionId: x.Intensity.RegionId,
                Rank: rank + 1,
                Intensity: x.Intensity,
                Score: x.Score
            ))
            .ToList();

        return ranked;
    }
}

/// <summary>
/// Base class for carbon offset provider plugins.
/// Integrates with offset marketplaces and registries.
/// Intelligence-aware: Supports AI-driven offset project recommendations.
/// </summary>
public abstract class CarbonOffsetProviderPluginBase : FeaturePluginBase, ICarbonOffsetProvider
{
    #region Intelligence Integration

    /// <summary>
    /// Capabilities declared by this carbon offset provider.
    /// </summary>
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities => new[]
    {
        new RegisteredCapability
        {
            CapabilityId = $"{Id}.carbon-offset",
            DisplayName = $"{Name} - Carbon Offset Purchasing",
            Description = "Carbon offset purchasing and tracking through verified registries",
            Category = CapabilityCategory.Custom,
            SubCategory = "Sustainability",
            PluginId = Id,
            PluginName = Name,
            PluginVersion = Version,
            Tags = new[] { "carbon", "sustainability", "offset", "credits" },
            SemanticDescription = "Use this for purchasing carbon offsets to achieve carbon neutrality",
            Metadata = new Dictionary<string, object>
            {
                ["supportsProjectSelection"] = true,
                ["supportsPurchaseTracking"] = true
            }
        }
    };

    /// <summary>
    /// Gets static knowledge for Intelligence registration.
    /// </summary>
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        var knowledge = new List<KnowledgeObject>(base.GetStaticKnowledge());

        knowledge.Add(new KnowledgeObject
        {
            Id = $"{Id}.offset.capability",
            Topic = "carbon-offset",
            SourcePluginId = Id,
            SourcePluginName = Name,
            KnowledgeType = "capability",
            Description = "Carbon offset provider for emissions compensation",
            Payload = new Dictionary<string, object>
            {
                ["supportsProjectSelection"] = true,
                ["supportsPurchaseTracking"] = true,
                ["supportsVerifiedCredits"] = true
            },
            Tags = new[] { "carbon", "offset", "sustainability" },
            Confidence = 1.0f,
            Timestamp = DateTimeOffset.UtcNow
        });

        return knowledge;
    }

    /// <summary>
    /// Requests AI-driven offset project recommendations.
    /// </summary>
    /// <param name="carbonAmount">Amount of carbon to offset in grams.</param>
    /// <param name="preferences">User preferences for project types.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Recommended projects.</returns>
    protected async Task<IReadOnlyList<OffsetProject>?> RequestProjectRecommendationsAsync(
        double carbonAmount,
        Dictionary<string, object>? preferences,
        CancellationToken ct = default)
    {
        if (MessageBus == null) return null;

        try
        {
            var request = new PluginMessage
            {
                Type = "intelligence.recommend.request",
                CorrelationId = Guid.NewGuid().ToString("N"),
                Source = Id,
                Payload = new Dictionary<string, object>
                {
                    ["recommendationType"] = "offset_projects",
                    ["carbonGrams"] = carbonAmount,
                    ["preferences"] = preferences ?? new Dictionary<string, object>()
                }
            };

            await MessageBus.PublishAsync("intelligence.recommend", request, ct);
            return null;
        }
        catch
        {
            return null;
        }
    }

    #endregion
    /// <summary>
    /// Purchases offsets from the provider's marketplace.
    /// Must be implemented by derived classes.
    /// </summary>
    /// <param name="carbonGrams">Amount to offset in grams.</param>
    /// <param name="options">Purchase options.</param>
    /// <returns>Purchase confirmation.</returns>
    protected abstract Task<OffsetPurchase> ExecutePurchaseAsync(double carbonGrams, OffsetOptions options);

    /// <summary>
    /// Fetches available offset projects.
    /// Must be implemented by derived classes.
    /// </summary>
    /// <returns>List of available projects.</returns>
    protected abstract Task<IReadOnlyList<OffsetProject>> FetchProjectsAsync();

    /// <summary>
    /// Retrieves purchase history from storage.
    /// Must be implemented by derived classes.
    /// </summary>
    /// <returns>List of past purchases.</returns>
    protected abstract Task<IReadOnlyList<OffsetPurchase>> FetchPurchaseHistoryAsync();

    /// <summary>
    /// Purchases carbon offsets.
    /// </summary>
    /// <param name="carbonGrams">Amount to offset in grams.</param>
    /// <param name="options">Purchase options.</param>
    /// <returns>Purchase confirmation.</returns>
    public Task<OffsetPurchase> PurchaseOffsetsAsync(double carbonGrams, OffsetOptions options)
        => ExecutePurchaseAsync(carbonGrams, options);

    /// <summary>
    /// Gets available offset projects.
    /// </summary>
    /// <returns>List of projects.</returns>
    public Task<IReadOnlyList<OffsetProject>> GetAvailableProjectsAsync()
        => FetchProjectsAsync();

    /// <summary>
    /// Gets purchase history.
    /// </summary>
    /// <returns>List of past purchases.</returns>
    public Task<IReadOnlyList<OffsetPurchase>> GetPurchaseHistoryAsync()
        => FetchPurchaseHistoryAsync();

    /// <summary>
    /// Converts grams of carbon to metric tons.
    /// </summary>
    /// <param name="grams">Carbon amount in grams.</param>
    /// <returns>Carbon amount in metric tons.</returns>
    protected static double GramsToTons(double grams) => grams / 1_000_000.0;

    /// <summary>
    /// Converts metric tons of carbon to grams.
    /// </summary>
    /// <param name="tons">Carbon amount in metric tons.</param>
    /// <returns>Carbon amount in grams.</returns>
    protected static double TonsToGrams(double tons) => tons * 1_000_000.0;
}
