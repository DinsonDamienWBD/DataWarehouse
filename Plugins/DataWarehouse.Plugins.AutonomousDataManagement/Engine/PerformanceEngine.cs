using DataWarehouse.SDK.AI;
using DataWarehouse.Plugins.AutonomousDataManagement.Models;
using DataWarehouse.Plugins.AutonomousDataManagement.Providers;
using System.Collections.Concurrent;
using System.Text.Json;

namespace DataWarehouse.Plugins.AutonomousDataManagement.Engine;

/// <summary>
/// Performance tuning engine for automatic optimization of system settings.
/// </summary>
public sealed class PerformanceEngine : IDisposable
{
    private readonly AIProviderSelector _providerSelector;
    private readonly QueryOptimizer _queryOptimizer;
    private readonly ConcurrentDictionary<string, PerformanceProfile> _profiles;
    private readonly ConcurrentDictionary<string, TuningConfiguration> _configurations;
    private readonly ConcurrentQueue<TuningEvent> _tuningHistory;
    private readonly PerformanceEngineConfig _config;
    private readonly Timer _tuningTimer;
    private readonly SemaphoreSlim _tuningLock;
    private bool _disposed;

    public PerformanceEngine(AIProviderSelector providerSelector, PerformanceEngineConfig? config = null)
    {
        _providerSelector = providerSelector ?? throw new ArgumentNullException(nameof(providerSelector));
        _config = config ?? new PerformanceEngineConfig();
        _queryOptimizer = new QueryOptimizer(_config.SlowQueryThresholdMs);
        _profiles = new ConcurrentDictionary<string, PerformanceProfile>();
        _configurations = new ConcurrentDictionary<string, TuningConfiguration>();
        _tuningHistory = new ConcurrentQueue<TuningEvent>();
        _tuningLock = new SemaphoreSlim(_config.MaxConcurrentTuningOps);

        _tuningTimer = new Timer(
            _ => _ = TuningCycleAsync(CancellationToken.None),
            null,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(_config.TuningIntervalMinutes));
    }

    /// <summary>
    /// Records performance metrics for a component.
    /// </summary>
    public void RecordMetrics(string componentId, PerformanceMetrics metrics)
    {
        var profile = _profiles.GetOrAdd(componentId, _ => new PerformanceProfile
        {
            ComponentId = componentId,
            WindowSize = _config.MetricsWindowSize
        });

        lock (profile)
        {
            profile.AddMetrics(metrics);
        }
    }

    /// <summary>
    /// Records a query execution for optimization analysis.
    /// </summary>
    public void RecordQuery(string queryHash, QueryExecutionInfo info)
    {
        _queryOptimizer.RecordQuery(queryHash, info);
    }

    /// <summary>
    /// Analyzes performance and returns tuning recommendations.
    /// </summary>
    public async Task<PerformanceAnalysisResult> AnalyzeAsync(
        string componentId,
        CancellationToken ct = default)
    {
        if (!_profiles.TryGetValue(componentId, out var profile))
        {
            return new PerformanceAnalysisResult
            {
                ComponentId = componentId,
                HasSufficientData = false,
                Reason = "No performance data available"
            };
        }

        List<TimestampedPerformanceMetrics> history;
        lock (profile)
        {
            history = profile.MetricsHistory.ToList();
        }

        if (history.Count < _config.MinDataPointsForAnalysis)
        {
            return new PerformanceAnalysisResult
            {
                ComponentId = componentId,
                HasSufficientData = false,
                Reason = $"Insufficient data ({history.Count} points, need {_config.MinDataPointsForAnalysis})"
            };
        }

        // Calculate performance metrics
        var avgLatency = history.Average(h => h.Metrics.LatencyMs);
        var p95Latency = CalculatePercentile(history.Select(h => h.Metrics.LatencyMs).ToList(), 95);
        var p99Latency = CalculatePercentile(history.Select(h => h.Metrics.LatencyMs).ToList(), 99);
        var avgThroughput = history.Average(h => h.Metrics.ThroughputOpsPerSec);
        var avgErrorRate = history.Average(h => h.Metrics.ErrorRate);

        // Identify bottlenecks
        var bottlenecks = IdentifyBottlenecks(history);

        // Get tuning recommendations
        var recommendations = await GetTuningRecommendationsAsync(
            componentId, history, bottlenecks, ct);

        // Get current configuration if available
        _configurations.TryGetValue(componentId, out var currentConfig);

        return new PerformanceAnalysisResult
        {
            ComponentId = componentId,
            HasSufficientData = true,
            AnalysisTime = DateTime.UtcNow,
            AverageLatencyMs = avgLatency,
            P95LatencyMs = p95Latency,
            P99LatencyMs = p99Latency,
            AverageThroughput = avgThroughput,
            AverageErrorRate = avgErrorRate,
            PerformanceScore = CalculatePerformanceScore(avgLatency, p99Latency, avgThroughput, avgErrorRate),
            Bottlenecks = bottlenecks,
            CurrentConfiguration = currentConfig,
            Recommendations = recommendations,
            QueryOptimizations = _queryOptimizer.GetOptimizationRecommendations().Take(10).ToList()
        };
    }

    /// <summary>
    /// Applies a tuning recommendation.
    /// </summary>
    public async Task<TuningResult> ApplyTuningAsync(
        TuningRecommendation recommendation,
        CancellationToken ct = default)
    {
        if (!await _tuningLock.WaitAsync(TimeSpan.FromSeconds(30), ct))
        {
            return new TuningResult
            {
                RecommendationId = recommendation.Id,
                Success = false,
                Reason = "Tuning queue full"
            };
        }

        try
        {
            var startTime = DateTime.UtcNow;

            // Get or create configuration
            var config = _configurations.GetOrAdd(recommendation.ComponentId, _ => new TuningConfiguration
            {
                ComponentId = recommendation.ComponentId
            });

            // Store previous values for rollback
            var previousValues = new Dictionary<string, object>(config.Parameters);

            // Apply new parameters
            foreach (var (key, value) in recommendation.Parameters)
            {
                config.Parameters[key] = value;
            }
            config.LastUpdated = DateTime.UtcNow;

            // Record tuning event
            var tuningEvent = new TuningEvent
            {
                Id = Guid.NewGuid().ToString(),
                ComponentId = recommendation.ComponentId,
                RecommendationId = recommendation.Id,
                Timestamp = DateTime.UtcNow,
                ParametersChanged = recommendation.Parameters,
                PreviousValues = previousValues,
                Success = true,
                Message = $"Applied {recommendation.Parameters.Count} parameter changes"
            };

            _tuningHistory.Enqueue(tuningEvent);

            while (_tuningHistory.Count > 1000)
            {
                _tuningHistory.TryDequeue(out _);
            }

            return new TuningResult
            {
                RecommendationId = recommendation.Id,
                Success = true,
                ExecutionTime = DateTime.UtcNow - startTime,
                AppliedParameters = recommendation.Parameters,
                Reason = $"Successfully applied tuning for {recommendation.Category}"
            };
        }
        finally
        {
            _tuningLock.Release();
        }
    }

    /// <summary>
    /// Rolls back a tuning change.
    /// </summary>
    public async Task<TuningResult> RollbackTuningAsync(string eventId, CancellationToken ct = default)
    {
        var tuningEvent = _tuningHistory.FirstOrDefault(e => e.Id == eventId);
        if (tuningEvent == null)
        {
            return new TuningResult
            {
                Success = false,
                Reason = "Tuning event not found"
            };
        }

        if (!_configurations.TryGetValue(tuningEvent.ComponentId, out var config))
        {
            return new TuningResult
            {
                Success = false,
                Reason = "Configuration not found"
            };
        }

        // Restore previous values
        foreach (var (key, value) in tuningEvent.PreviousValues)
        {
            config.Parameters[key] = value;
        }
        config.LastUpdated = DateTime.UtcNow;

        return new TuningResult
        {
            RecommendationId = tuningEvent.RecommendationId,
            Success = true,
            Reason = "Successfully rolled back tuning changes"
        };
    }

    /// <summary>
    /// Gets performance statistics.
    /// </summary>
    public PerformanceStatistics GetStatistics()
    {
        var componentStats = new Dictionary<string, ComponentPerformanceStats>();

        foreach (var (componentId, profile) in _profiles)
        {
            lock (profile)
            {
                if (profile.MetricsHistory.Count == 0) continue;

                var history = profile.MetricsHistory.ToList();
                componentStats[componentId] = new ComponentPerformanceStats
                {
                    ComponentId = componentId,
                    DataPoints = history.Count,
                    AverageLatencyMs = history.Average(h => h.Metrics.LatencyMs),
                    AverageThroughput = history.Average(h => h.Metrics.ThroughputOpsPerSec),
                    AverageErrorRate = history.Average(h => h.Metrics.ErrorRate),
                    LastUpdated = history.Last().Timestamp
                };
            }
        }

        var queryStats = _queryOptimizer.GetPerformanceStats();

        return new PerformanceStatistics
        {
            TrackedComponents = _profiles.Count,
            TotalTuningEvents = _tuningHistory.Count,
            SuccessfulTunings = _tuningHistory.Count(e => e.Success),
            ComponentStats = componentStats,
            QueryPerformance = queryStats,
            RecentTuningEvents = _tuningHistory.TakeLast(20).ToList()
        };
    }

    private async Task TuningCycleAsync(CancellationToken ct)
    {
        if (!_config.AutoTuningEnabled) return;

        foreach (var componentId in _profiles.Keys.Take(_config.MaxComponentsPerCycle))
        {
            try
            {
                var analysis = await AnalyzeAsync(componentId, ct);

                if (analysis.HasSufficientData && analysis.PerformanceScore < _config.AutoTuningThreshold)
                {
                    // Apply highest-priority recommendation
                    var topRecommendation = analysis.Recommendations
                        .Where(r => r.AutoApply)
                        .OrderByDescending(r => r.Priority)
                        .FirstOrDefault();

                    if (topRecommendation != null)
                    {
                        await ApplyTuningAsync(topRecommendation, ct);
                    }
                }
            }
            catch
            {
                // Continue with other components
            }
        }
    }

    private List<PerformanceBottleneck> IdentifyBottlenecks(List<TimestampedPerformanceMetrics> history)
    {
        var bottlenecks = new List<PerformanceBottleneck>();

        var avgLatency = history.Average(h => h.Metrics.LatencyMs);
        var avgCpu = history.Average(h => h.Metrics.CpuPercent);
        var avgMemory = history.Average(h => h.Metrics.MemoryPercent);
        var avgDiskIO = history.Average(h => h.Metrics.DiskIOPercent);
        var avgNetworkIO = history.Average(h => h.Metrics.NetworkIOPercent);

        if (avgCpu > 80)
        {
            bottlenecks.Add(new PerformanceBottleneck
            {
                Type = BottleneckType.CPU,
                Severity = avgCpu > 90 ? BottleneckSeverity.Critical : BottleneckSeverity.High,
                CurrentValue = avgCpu,
                Threshold = 80,
                Description = $"CPU utilization at {avgCpu:F1}%"
            });
        }

        if (avgMemory > 85)
        {
            bottlenecks.Add(new PerformanceBottleneck
            {
                Type = BottleneckType.Memory,
                Severity = avgMemory > 95 ? BottleneckSeverity.Critical : BottleneckSeverity.High,
                CurrentValue = avgMemory,
                Threshold = 85,
                Description = $"Memory utilization at {avgMemory:F1}%"
            });
        }

        if (avgDiskIO > 80)
        {
            bottlenecks.Add(new PerformanceBottleneck
            {
                Type = BottleneckType.DiskIO,
                Severity = avgDiskIO > 95 ? BottleneckSeverity.Critical : BottleneckSeverity.High,
                CurrentValue = avgDiskIO,
                Threshold = 80,
                Description = $"Disk I/O at {avgDiskIO:F1}%"
            });
        }

        if (avgLatency > _config.HighLatencyThresholdMs)
        {
            bottlenecks.Add(new PerformanceBottleneck
            {
                Type = BottleneckType.Latency,
                Severity = avgLatency > _config.HighLatencyThresholdMs * 2 ? BottleneckSeverity.High : BottleneckSeverity.Medium,
                CurrentValue = avgLatency,
                Threshold = _config.HighLatencyThresholdMs,
                Description = $"Average latency at {avgLatency:F0}ms"
            });
        }

        return bottlenecks.OrderByDescending(b => b.Severity).ToList();
    }

    private async Task<List<TuningRecommendation>> GetTuningRecommendationsAsync(
        string componentId,
        List<TimestampedPerformanceMetrics> history,
        List<PerformanceBottleneck> bottlenecks,
        CancellationToken ct)
    {
        var recommendations = new List<TuningRecommendation>();

        // Generate basic recommendations from bottlenecks
        foreach (var bottleneck in bottlenecks)
        {
            var rec = GenerateBottleneckRecommendation(componentId, bottleneck);
            if (rec != null)
            {
                recommendations.Add(rec);
            }
        }

        // Get AI-enhanced recommendations for complex tuning
        if (_config.AITuningEnabled && bottlenecks.Any())
        {
            try
            {
                var aiRecommendations = await GetAITuningRecommendationsAsync(
                    componentId, history, bottlenecks, ct);
                recommendations.AddRange(aiRecommendations);
            }
            catch
            {
                // Continue with basic recommendations
            }
        }

        return recommendations.OrderByDescending(r => r.Priority).ToList();
    }

    private TuningRecommendation? GenerateBottleneckRecommendation(string componentId, PerformanceBottleneck bottleneck)
    {
        return bottleneck.Type switch
        {
            BottleneckType.CPU => new TuningRecommendation
            {
                Id = Guid.NewGuid().ToString(),
                ComponentId = componentId,
                Category = "CPU Optimization",
                Title = "Reduce CPU Usage",
                Description = "Increase connection pooling and enable query caching to reduce CPU load",
                Priority = TuningPriority.High,
                Parameters = new Dictionary<string, object>
                {
                    ["connectionPoolSize"] = 100,
                    ["queryCacheEnabled"] = true,
                    ["queryCacheSizeMB"] = 256
                },
                ExpectedImprovement = "20-30% CPU reduction",
                AutoApply = bottleneck.Severity != BottleneckSeverity.Critical
            },

            BottleneckType.Memory => new TuningRecommendation
            {
                Id = Guid.NewGuid().ToString(),
                ComponentId = componentId,
                Category = "Memory Optimization",
                Title = "Optimize Memory Usage",
                Description = "Adjust buffer sizes and garbage collection settings",
                Priority = TuningPriority.High,
                Parameters = new Dictionary<string, object>
                {
                    ["bufferPoolSizeMB"] = 512,
                    ["gcMode"] = "server",
                    ["maxMemoryCacheMB"] = 1024
                },
                ExpectedImprovement = "15-25% memory optimization",
                AutoApply = false
            },

            BottleneckType.DiskIO => new TuningRecommendation
            {
                Id = Guid.NewGuid().ToString(),
                ComponentId = componentId,
                Category = "I/O Optimization",
                Title = "Optimize Disk I/O",
                Description = "Increase buffer sizes and enable write-ahead logging",
                Priority = TuningPriority.High,
                Parameters = new Dictionary<string, object>
                {
                    ["ioBufferSizeKB"] = 64,
                    ["writeAheadLogEnabled"] = true,
                    ["asyncIOEnabled"] = true
                },
                ExpectedImprovement = "25-40% I/O improvement",
                AutoApply = true
            },

            BottleneckType.Latency => new TuningRecommendation
            {
                Id = Guid.NewGuid().ToString(),
                ComponentId = componentId,
                Category = "Latency Optimization",
                Title = "Reduce Response Latency",
                Description = "Enable response caching and optimize connection settings",
                Priority = TuningPriority.Medium,
                Parameters = new Dictionary<string, object>
                {
                    ["responseCacheSeconds"] = 60,
                    ["keepAliveEnabled"] = true,
                    ["tcpNoDelay"] = true
                },
                ExpectedImprovement = "30-50% latency reduction",
                AutoApply = true
            },

            _ => null
        };
    }

    private async Task<List<TuningRecommendation>> GetAITuningRecommendationsAsync(
        string componentId,
        List<TimestampedPerformanceMetrics> history,
        List<PerformanceBottleneck> bottlenecks,
        CancellationToken ct)
    {
        var recentMetrics = history.TakeLast(20).Select(h => new
        {
            latency = h.Metrics.LatencyMs,
            throughput = h.Metrics.ThroughputOpsPerSec,
            cpu = h.Metrics.CpuPercent,
            memory = h.Metrics.MemoryPercent,
            errorRate = h.Metrics.ErrorRate
        });

        var prompt = $@"Analyze the following performance data and provide tuning recommendations:

Component: {componentId}

Recent Performance Metrics:
{JsonSerializer.Serialize(recentMetrics)}

Identified Bottlenecks:
{JsonSerializer.Serialize(bottlenecks.Select(b => new { b.Type, b.Severity, b.CurrentValue, b.Threshold }))}

Provide 2-3 specific tuning recommendations as JSON:
[
  {{
    ""category"": ""<category>"",
    ""title"": ""<brief title>"",
    ""description"": ""<detailed recommendation>"",
    ""parameters"": {{ ""paramName"": ""value"" }},
    ""expectedImprovement"": ""<expected benefit>"",
    ""priority"": ""<High/Medium/Low>"",
    ""autoApply"": <true/false>
  }}
]";

        var response = await _providerSelector.ExecuteWithFailoverAsync(
            AIOpsCapabilities.PerformanceTuning,
            prompt,
            null,
            ct);

        if (response.Success && !string.IsNullOrEmpty(response.Content))
        {
            var jsonStart = response.Content.IndexOf('[');
            var jsonEnd = response.Content.LastIndexOf(']');

            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var json = response.Content.Substring(jsonStart, jsonEnd - jsonStart + 1);
                var doc = JsonDocument.Parse(json);

                return doc.RootElement.EnumerateArray().Select(elem =>
                {
                    var parameters = new Dictionary<string, object>();
                    if (elem.TryGetProperty("parameters", out var paramsElem))
                    {
                        foreach (var prop in paramsElem.EnumerateObject())
                        {
                            parameters[prop.Name] = prop.Value.ValueKind switch
                            {
                                JsonValueKind.Number => prop.Value.GetDouble(),
                                JsonValueKind.True => true,
                                JsonValueKind.False => false,
                                _ => prop.Value.GetString() ?? ""
                            };
                        }
                    }

                    return new TuningRecommendation
                    {
                        Id = Guid.NewGuid().ToString(),
                        ComponentId = componentId,
                        Category = elem.TryGetProperty("category", out var c) ? c.GetString() ?? "" : "",
                        Title = elem.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                        Description = elem.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                        Parameters = parameters,
                        ExpectedImprovement = elem.TryGetProperty("expectedImprovement", out var i) ? i.GetString() ?? "" : "",
                        Priority = Enum.TryParse<TuningPriority>(
                            elem.TryGetProperty("priority", out var p) ? p.GetString() : "Medium",
                            out var priority) ? priority : TuningPriority.Medium,
                        AutoApply = elem.TryGetProperty("autoApply", out var a) && a.GetBoolean()
                    };
                }).ToList();
            }
        }

        return new List<TuningRecommendation>();
    }

    private double CalculatePercentile(List<double> values, int percentile)
    {
        if (values.Count == 0) return 0;

        var sorted = values.OrderBy(v => v).ToList();
        var index = (int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1;
        return sorted[Math.Max(0, Math.Min(index, sorted.Count - 1))];
    }

    private double CalculatePerformanceScore(double avgLatency, double p99Latency, double throughput, double errorRate)
    {
        // Score from 0-100, higher is better
        var latencyScore = Math.Max(0, 100 - avgLatency / 10);
        var p99Score = Math.Max(0, 100 - p99Latency / 50);
        var throughputScore = Math.Min(100, throughput / 10);
        var errorScore = Math.Max(0, 100 - errorRate * 1000);

        return (latencyScore * 0.3 + p99Score * 0.2 + throughputScore * 0.3 + errorScore * 0.2);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _tuningTimer.Dispose();
        _tuningLock.Dispose();
    }
}

#region Supporting Types

public sealed class PerformanceEngineConfig
{
    public int TuningIntervalMinutes { get; set; } = 15;
    public int MetricsWindowSize { get; set; } = 1000;
    public int MinDataPointsForAnalysis { get; set; } = 50;
    public int SlowQueryThresholdMs { get; set; } = 1000;
    public int HighLatencyThresholdMs { get; set; } = 500;
    public bool AutoTuningEnabled { get; set; } = false;
    public double AutoTuningThreshold { get; set; } = 60; // Performance score threshold
    public bool AITuningEnabled { get; set; } = true;
    public int MaxConcurrentTuningOps { get; set; } = 5;
    public int MaxComponentsPerCycle { get; set; } = 20;
}

public sealed class PerformanceMetrics
{
    public double LatencyMs { get; init; }
    public double ThroughputOpsPerSec { get; init; }
    public double CpuPercent { get; init; }
    public double MemoryPercent { get; init; }
    public double DiskIOPercent { get; init; }
    public double NetworkIOPercent { get; init; }
    public double ErrorRate { get; init; }
    public int ActiveConnections { get; init; }
}

public sealed class TimestampedPerformanceMetrics
{
    public DateTime Timestamp { get; init; }
    public PerformanceMetrics Metrics { get; init; } = new();
}

public sealed class PerformanceProfile
{
    public string ComponentId { get; init; } = string.Empty;
    public int WindowSize { get; init; }
    public List<TimestampedPerformanceMetrics> MetricsHistory { get; } = new();

    public void AddMetrics(PerformanceMetrics metrics)
    {
        MetricsHistory.Add(new TimestampedPerformanceMetrics
        {
            Timestamp = DateTime.UtcNow,
            Metrics = metrics
        });

        while (MetricsHistory.Count > WindowSize)
        {
            MetricsHistory.RemoveAt(0);
        }
    }
}

public sealed class TuningConfiguration
{
    public string ComponentId { get; init; } = string.Empty;
    public Dictionary<string, object> Parameters { get; } = new();
    public DateTime LastUpdated { get; set; }
}

public enum BottleneckType
{
    CPU,
    Memory,
    DiskIO,
    NetworkIO,
    Latency,
    Throughput
}

public enum BottleneckSeverity
{
    Low,
    Medium,
    High,
    Critical
}

public sealed class PerformanceBottleneck
{
    public BottleneckType Type { get; init; }
    public BottleneckSeverity Severity { get; init; }
    public double CurrentValue { get; init; }
    public double Threshold { get; init; }
    public string Description { get; init; } = string.Empty;
}

public enum TuningPriority
{
    Low,
    Medium,
    High,
    Critical
}

public sealed class TuningRecommendation
{
    public string Id { get; init; } = string.Empty;
    public string ComponentId { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public TuningPriority Priority { get; init; }
    public Dictionary<string, object> Parameters { get; init; } = new();
    public string ExpectedImprovement { get; init; } = string.Empty;
    public bool AutoApply { get; init; }
}

public sealed class TuningResult
{
    public string RecommendationId { get; init; } = string.Empty;
    public bool Success { get; init; }
    public TimeSpan ExecutionTime { get; init; }
    public Dictionary<string, object>? AppliedParameters { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed class TuningEvent
{
    public string Id { get; init; } = string.Empty;
    public string ComponentId { get; init; } = string.Empty;
    public string RecommendationId { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; }
    public Dictionary<string, object> ParametersChanged { get; init; } = new();
    public Dictionary<string, object> PreviousValues { get; init; } = new();
    public bool Success { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class PerformanceAnalysisResult
{
    public string ComponentId { get; init; } = string.Empty;
    public bool HasSufficientData { get; init; }
    public DateTime AnalysisTime { get; init; }
    public double AverageLatencyMs { get; init; }
    public double P95LatencyMs { get; init; }
    public double P99LatencyMs { get; init; }
    public double AverageThroughput { get; init; }
    public double AverageErrorRate { get; init; }
    public double PerformanceScore { get; init; }
    public List<PerformanceBottleneck> Bottlenecks { get; init; } = new();
    public TuningConfiguration? CurrentConfiguration { get; init; }
    public List<TuningRecommendation> Recommendations { get; init; } = new();
    public List<QueryOptimizationRecommendation> QueryOptimizations { get; init; } = new();
    public string Reason { get; init; } = string.Empty;
}

public sealed class PerformanceStatistics
{
    public int TrackedComponents { get; init; }
    public int TotalTuningEvents { get; init; }
    public int SuccessfulTunings { get; init; }
    public Dictionary<string, ComponentPerformanceStats> ComponentStats { get; init; } = new();
    public QueryPerformanceStats QueryPerformance { get; init; } = new();
    public List<TuningEvent> RecentTuningEvents { get; init; } = new();
}

public sealed class ComponentPerformanceStats
{
    public string ComponentId { get; init; } = string.Empty;
    public int DataPoints { get; init; }
    public double AverageLatencyMs { get; init; }
    public double AverageThroughput { get; init; }
    public double AverageErrorRate { get; init; }
    public DateTime LastUpdated { get; init; }
}

#endregion
