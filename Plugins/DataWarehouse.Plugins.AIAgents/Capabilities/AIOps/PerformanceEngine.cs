// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using DataWarehouse.SDK.AI;
using System.Collections.Concurrent;
using System.Text.Json;
using IExtendedAIProvider = DataWarehouse.Plugins.AIAgents.IExtendedAIProvider;

namespace DataWarehouse.Plugins.AIAgents.Capabilities.AIOps;

/// <summary>
/// Performance tuning engine for automatic optimization of system settings.
/// Uses statistical analysis combined with AI-enhanced recommendations to
/// identify bottlenecks and suggest tuning parameters.
/// </summary>
/// <remarks>
/// <para>
/// The performance engine provides:
/// - Real-time performance metrics recording
/// - Bottleneck identification and analysis
/// - Query performance optimization recommendations
/// - AI-enhanced tuning recommendations
/// - Automatic tuning with rollback capability
/// </para>
/// <para>
/// The engine gracefully degrades to rule-based recommendations when AI is unavailable.
/// </para>
/// </remarks>
public sealed class PerformanceEngine : IDisposable
{
    private readonly IExtendedAIProvider? _aiProvider;
    private readonly QueryOptimizer _queryOptimizer;
    private readonly ConcurrentDictionary<string, PerformanceProfile> _profiles;
    private readonly ConcurrentDictionary<string, TuningConfiguration> _configurations;
    private readonly ConcurrentQueue<TuningEvent> _tuningHistory;
    private readonly PerformanceEngineConfig _config;
    private readonly Timer? _tuningTimer;
    private readonly SemaphoreSlim _tuningLock;
    private bool _disposed;

    /// <summary>
    /// Creates a new performance engine instance.
    /// </summary>
    /// <param name="aiProvider">Optional AI provider for enhanced recommendations.</param>
    /// <param name="config">Engine configuration.</param>
    public PerformanceEngine(IExtendedAIProvider? aiProvider = null, PerformanceEngineConfig? config = null)
    {
        _aiProvider = aiProvider;
        _config = config ?? new PerformanceEngineConfig();
        _queryOptimizer = new QueryOptimizer(_config.SlowQueryThresholdMs);
        _profiles = new ConcurrentDictionary<string, PerformanceProfile>();
        _configurations = new ConcurrentDictionary<string, TuningConfiguration>();
        _tuningHistory = new ConcurrentQueue<TuningEvent>();
        _tuningLock = new SemaphoreSlim(_config.MaxConcurrentTuningOps);

        if (_config.AutoTuningEnabled)
        {
            _tuningTimer = new Timer(
                _ => _ = TuningCycleAsync(CancellationToken.None),
                null,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(_config.TuningIntervalMinutes));
        }
    }

    /// <summary>
    /// Records performance metrics for a component.
    /// </summary>
    /// <param name="componentId">The component identifier.</param>
    /// <param name="metrics">The metrics to record.</param>
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
    /// <param name="queryHash">The query hash identifier.</param>
    /// <param name="info">The query execution information.</param>
    public void RecordQuery(string queryHash, QueryExecutionInfo info)
    {
        _queryOptimizer.RecordQuery(queryHash, info);
    }

    /// <summary>
    /// Analyzes performance and returns tuning recommendations.
    /// </summary>
    /// <param name="componentId">The component identifier.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The performance analysis result.</returns>
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
    /// <param name="recommendation">The recommendation to apply.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The tuning result.</returns>
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

            // Trim history
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
    /// <param name="eventId">The tuning event identifier to roll back.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The rollback result.</returns>
    public async Task<TuningResult> RollbackTuningAsync(string eventId, CancellationToken ct = default)
    {
        await Task.CompletedTask;

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
    /// <returns>The performance statistics.</returns>
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

    /// <summary>
    /// Runs a tuning cycle for all tracked components.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The tuning cycle result.</returns>
    public async Task<TuningCycleResult> TuningCycleAsync(CancellationToken ct = default)
    {
        if (!_config.AutoTuningEnabled)
        {
            return new TuningCycleResult
            {
                StartTime = DateTime.UtcNow,
                EndTime = DateTime.UtcNow,
                ComponentsAnalyzed = 0,
                TuningsApplied = 0
            };
        }

        var startTime = DateTime.UtcNow;
        var componentsAnalyzed = 0;
        var tuningsApplied = 0;

        foreach (var componentId in _profiles.Keys.Take(_config.MaxComponentsPerCycle))
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                componentsAnalyzed++;
                var analysis = await AnalyzeAsync(componentId, ct);

                if (analysis.HasSufficientData && analysis.PerformanceScore < _config.AutoTuningThreshold)
                {
                    // Apply highest-priority auto-applicable recommendation
                    var topRecommendation = analysis.Recommendations
                        .Where(r => r.AutoApply)
                        .OrderByDescending(r => r.Priority)
                        .FirstOrDefault();

                    if (topRecommendation != null)
                    {
                        var result = await ApplyTuningAsync(topRecommendation, ct);
                        if (result.Success)
                        {
                            tuningsApplied++;
                        }
                    }
                }
            }
            catch
            {
                // Continue with other components
            }
        }

        return new TuningCycleResult
        {
            StartTime = startTime,
            EndTime = DateTime.UtcNow,
            ComponentsAnalyzed = componentsAnalyzed,
            TuningsApplied = tuningsApplied
        };
    }

    #region Private Methods

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

        if (avgNetworkIO > 85)
        {
            bottlenecks.Add(new PerformanceBottleneck
            {
                Type = BottleneckType.NetworkIO,
                Severity = avgNetworkIO > 95 ? BottleneckSeverity.Critical : BottleneckSeverity.High,
                CurrentValue = avgNetworkIO,
                Threshold = 85,
                Description = $"Network I/O at {avgNetworkIO:F1}%"
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
        if (_config.AITuningEnabled && _aiProvider != null && _aiProvider.IsAvailable && bottlenecks.Any())
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
                AutoApply = bottleneck.Severity != BottleneckSeverity.Critical,
                Source = RecommendationSource.Rule
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
                AutoApply = false,
                Source = RecommendationSource.Rule
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
                AutoApply = true,
                Source = RecommendationSource.Rule
            },

            BottleneckType.NetworkIO => new TuningRecommendation
            {
                Id = Guid.NewGuid().ToString(),
                ComponentId = componentId,
                Category = "Network Optimization",
                Title = "Optimize Network Usage",
                Description = "Enable compression and optimize connection settings",
                Priority = TuningPriority.Medium,
                Parameters = new Dictionary<string, object>
                {
                    ["compressionEnabled"] = true,
                    ["maxConnections"] = 1000,
                    ["connectionTimeout"] = 30
                },
                ExpectedImprovement = "20-35% network efficiency improvement",
                AutoApply = true,
                Source = RecommendationSource.Rule
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
                AutoApply = true,
                Source = RecommendationSource.Rule
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
        if (_aiProvider == null)
            return new List<TuningRecommendation>();

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
{JsonSerializer.Serialize(recentMetrics, new JsonSerializerOptions { WriteIndented = true })}

Identified Bottlenecks:
{JsonSerializer.Serialize(bottlenecks.Select(b => new { b.Type, b.Severity, b.CurrentValue, b.Threshold }), new JsonSerializerOptions { WriteIndented = true })}

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

        var request = new AIRequest
        {
            Prompt = prompt,
            SystemMessage = "You are a performance tuning expert. Analyze system metrics and provide specific, actionable tuning recommendations with concrete parameter values.",
            MaxTokens = 2048,
            Temperature = 0.2f
        };

        var response = await _aiProvider.CompleteAsync(request, ct);

        if (response.Success && !string.IsNullOrEmpty(response.Content))
        {
            try
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
                            AutoApply = elem.TryGetProperty("autoApply", out var a) && a.GetBoolean(),
                            Source = RecommendationSource.AI
                        };
                    }).ToList();
                }
            }
            catch
            {
                // Fall through
            }
        }

        return new List<TuningRecommendation>();
    }

    private static double CalculatePercentile(List<double> values, int percentile)
    {
        if (values.Count == 0) return 0;

        var sorted = values.OrderBy(v => v).ToList();
        var index = (int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1;
        return sorted[Math.Max(0, Math.Min(index, sorted.Count - 1))];
    }

    private static double CalculatePerformanceScore(double avgLatency, double p99Latency, double throughput, double errorRate)
    {
        // Score from 0-100, higher is better
        var latencyScore = Math.Max(0, 100 - avgLatency / 10);
        var p99Score = Math.Max(0, 100 - p99Latency / 50);
        var throughputScore = Math.Min(100, throughput / 10);
        var errorScore = Math.Max(0, 100 - errorRate * 1000);

        return (latencyScore * 0.3 + p99Score * 0.2 + throughputScore * 0.3 + errorScore * 0.2);
    }

    #endregion

    /// <summary>
    /// Releases all resources used by the engine.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _tuningTimer?.Dispose();
        _tuningLock.Dispose();
    }
}

#region Configuration

/// <summary>
/// Configuration for the performance engine.
/// </summary>
public sealed class PerformanceEngineConfig
{
    /// <summary>Gets or sets the tuning interval in minutes.</summary>
    public int TuningIntervalMinutes { get; set; } = 15;

    /// <summary>Gets or sets the metrics window size.</summary>
    public int MetricsWindowSize { get; set; } = 1000;

    /// <summary>Gets or sets the minimum data points for analysis.</summary>
    public int MinDataPointsForAnalysis { get; set; } = 50;

    /// <summary>Gets or sets the slow query threshold in ms.</summary>
    public int SlowQueryThresholdMs { get; set; } = 1000;

    /// <summary>Gets or sets the high latency threshold in ms.</summary>
    public int HighLatencyThresholdMs { get; set; } = 500;

    /// <summary>Gets or sets whether auto-tuning is enabled.</summary>
    public bool AutoTuningEnabled { get; set; } = false;

    /// <summary>Gets or sets the auto-tuning score threshold.</summary>
    public double AutoTuningThreshold { get; set; } = 60;

    /// <summary>Gets or sets whether AI tuning is enabled.</summary>
    public bool AITuningEnabled { get; set; } = true;

    /// <summary>Gets or sets the maximum concurrent tuning operations.</summary>
    public int MaxConcurrentTuningOps { get; set; } = 5;

    /// <summary>Gets or sets the maximum components per tuning cycle.</summary>
    public int MaxComponentsPerCycle { get; set; } = 20;
}

#endregion

#region Query Optimizer

/// <summary>
/// Query performance optimizer.
/// </summary>
public sealed class QueryOptimizer
{
    private readonly ConcurrentDictionary<string, QueryMetrics> _queryMetrics;
    private readonly int _slowQueryThresholdMs;

    /// <summary>
    /// Creates a new query optimizer.
    /// </summary>
    /// <param name="slowQueryThresholdMs">Threshold for slow queries.</param>
    public QueryOptimizer(int slowQueryThresholdMs = 1000)
    {
        _queryMetrics = new ConcurrentDictionary<string, QueryMetrics>();
        _slowQueryThresholdMs = slowQueryThresholdMs;
    }

    /// <summary>
    /// Records query execution metrics.
    /// </summary>
    /// <param name="queryHash">The query hash identifier.</param>
    /// <param name="info">The query execution information.</param>
    public void RecordQuery(string queryHash, QueryExecutionInfo info)
    {
        var metrics = _queryMetrics.GetOrAdd(queryHash, _ => new QueryMetrics { QueryHash = queryHash });

        lock (metrics)
        {
            metrics.ExecutionCount++;
            metrics.TotalExecutionTimeMs += info.ExecutionTimeMs;
            metrics.TotalRowsScanned += info.RowsScanned;
            metrics.TotalRowsReturned += info.RowsReturned;
            metrics.TotalBytesRead += info.BytesRead;
            metrics.LastExecution = DateTime.UtcNow;

            if (metrics.QueryPattern == null && info.QueryText != null)
            {
                metrics.QueryPattern = info.QueryText;
            }

            metrics.ExecutionHistory.Add(new QueryExecution
            {
                Timestamp = DateTime.UtcNow,
                ExecutionTimeMs = info.ExecutionTimeMs,
                RowsScanned = info.RowsScanned,
                RowsReturned = info.RowsReturned
            });

            // Trim history
            while (metrics.ExecutionHistory.Count > 1000)
            {
                metrics.ExecutionHistory.RemoveAt(0);
            }
        }
    }

    /// <summary>
    /// Gets optimization recommendations for slow queries.
    /// </summary>
    /// <returns>List of optimization recommendations.</returns>
    public List<QueryOptimizationRecommendation> GetOptimizationRecommendations()
    {
        var recommendations = new List<QueryOptimizationRecommendation>();

        foreach (var (hash, metrics) in _queryMetrics)
        {
            lock (metrics)
            {
                var avgTime = metrics.ExecutionCount > 0
                    ? metrics.TotalExecutionTimeMs / metrics.ExecutionCount
                    : 0;

                if (avgTime < _slowQueryThresholdMs && metrics.ExecutionCount < 100)
                    continue;

                var selectivity = metrics.TotalRowsReturned > 0 && metrics.TotalRowsScanned > 0
                    ? (double)metrics.TotalRowsReturned / metrics.TotalRowsScanned
                    : 1.0;

                // Check for full table scans
                if (selectivity < 0.01 && metrics.TotalRowsScanned > 10000)
                {
                    recommendations.Add(new QueryOptimizationRecommendation
                    {
                        QueryHash = hash,
                        QueryPattern = metrics.QueryPattern,
                        Type = QueryOptimizationType.AddIndex,
                        Description = "Query has low selectivity, consider adding an index",
                        EstimatedImprovement = 0.8,
                        Priority = QueryPriority.High,
                        CurrentAvgTimeMs = avgTime,
                        ExecutionCount = metrics.ExecutionCount
                    });
                }

                // Check for slow queries
                if (avgTime > _slowQueryThresholdMs)
                {
                    recommendations.Add(new QueryOptimizationRecommendation
                    {
                        QueryHash = hash,
                        QueryPattern = metrics.QueryPattern,
                        Type = QueryOptimizationType.QueryRewrite,
                        Description = $"Query averaging {avgTime:F0}ms, consider optimization",
                        EstimatedImprovement = 0.5,
                        Priority = avgTime > _slowQueryThresholdMs * 5 ? QueryPriority.Critical : QueryPriority.Medium,
                        CurrentAvgTimeMs = avgTime,
                        ExecutionCount = metrics.ExecutionCount
                    });
                }

                // Check for frequent queries that could benefit from caching
                if (metrics.ExecutionCount > 100 && avgTime > 100)
                {
                    recommendations.Add(new QueryOptimizationRecommendation
                    {
                        QueryHash = hash,
                        QueryPattern = metrics.QueryPattern,
                        Type = QueryOptimizationType.AddCache,
                        Description = $"Frequently executed query ({metrics.ExecutionCount} times), consider caching",
                        EstimatedImprovement = 0.9,
                        Priority = QueryPriority.Medium,
                        CurrentAvgTimeMs = avgTime,
                        ExecutionCount = metrics.ExecutionCount
                    });
                }
            }
        }

        return recommendations
            .OrderByDescending(r => r.Priority)
            .ThenByDescending(r => r.EstimatedImprovement * r.ExecutionCount)
            .ToList();
    }

    /// <summary>
    /// Gets query performance statistics.
    /// </summary>
    /// <returns>Query performance statistics.</returns>
    public QueryPerformanceStats GetPerformanceStats()
    {
        var allMetrics = _queryMetrics.Values.ToList();

        return new QueryPerformanceStats
        {
            TotalQueries = allMetrics.Sum(m => m.ExecutionCount),
            UniqueQueryCount = allMetrics.Count,
            SlowQueryCount = allMetrics.Count(m =>
                m.ExecutionCount > 0 && m.TotalExecutionTimeMs / m.ExecutionCount > _slowQueryThresholdMs),
            AverageExecutionTimeMs = allMetrics.Sum(m => m.TotalExecutionTimeMs) /
                Math.Max(1, allMetrics.Sum(m => m.ExecutionCount)),
            TotalRowsScanned = allMetrics.Sum(m => m.TotalRowsScanned),
            TotalRowsReturned = allMetrics.Sum(m => m.TotalRowsReturned),
            OverallSelectivity = allMetrics.Sum(m => m.TotalRowsReturned) /
                Math.Max(1.0, allMetrics.Sum(m => m.TotalRowsScanned)),
            TopSlowQueries = allMetrics
                .Where(m => m.ExecutionCount > 0)
                .OrderByDescending(m => m.TotalExecutionTimeMs / m.ExecutionCount)
                .Take(10)
                .Select(m => new SlowQueryInfo
                {
                    QueryHash = m.QueryHash,
                    QueryPattern = m.QueryPattern,
                    AvgTimeMs = m.TotalExecutionTimeMs / m.ExecutionCount,
                    ExecutionCount = m.ExecutionCount
                })
                .ToList()
        };
    }
}

#endregion

#region Types

/// <summary>
/// Performance metrics.
/// </summary>
public sealed class PerformanceMetrics
{
    /// <summary>Gets or sets the latency in milliseconds.</summary>
    public double LatencyMs { get; init; }

    /// <summary>Gets or sets the throughput in ops/sec.</summary>
    public double ThroughputOpsPerSec { get; init; }

    /// <summary>Gets or sets the CPU percentage.</summary>
    public double CpuPercent { get; init; }

    /// <summary>Gets or sets the memory percentage.</summary>
    public double MemoryPercent { get; init; }

    /// <summary>Gets or sets the disk I/O percentage.</summary>
    public double DiskIOPercent { get; init; }

    /// <summary>Gets or sets the network I/O percentage.</summary>
    public double NetworkIOPercent { get; init; }

    /// <summary>Gets or sets the error rate.</summary>
    public double ErrorRate { get; init; }

    /// <summary>Gets or sets the number of active connections.</summary>
    public int ActiveConnections { get; init; }
}

/// <summary>
/// Timestamped performance metrics.
/// </summary>
public sealed class TimestampedPerformanceMetrics
{
    /// <summary>Gets the timestamp.</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>Gets the metrics.</summary>
    public PerformanceMetrics Metrics { get; init; } = new();
}

/// <summary>
/// Performance profile for a component.
/// </summary>
public sealed class PerformanceProfile
{
    /// <summary>Gets or sets the component identifier.</summary>
    public string ComponentId { get; init; } = string.Empty;

    /// <summary>Gets or sets the window size.</summary>
    public int WindowSize { get; init; }

    /// <summary>Gets the metrics history.</summary>
    public List<TimestampedPerformanceMetrics> MetricsHistory { get; } = new();

    /// <summary>
    /// Adds metrics to the profile.
    /// </summary>
    /// <param name="metrics">The metrics to add.</param>
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

/// <summary>
/// Tuning configuration for a component.
/// </summary>
public sealed class TuningConfiguration
{
    /// <summary>Gets or sets the component identifier.</summary>
    public string ComponentId { get; init; } = string.Empty;

    /// <summary>Gets the configuration parameters.</summary>
    public Dictionary<string, object> Parameters { get; } = new();

    /// <summary>Gets or sets the last update time.</summary>
    public DateTime LastUpdated { get; set; }
}

/// <summary>
/// Types of bottlenecks.
/// </summary>
public enum BottleneckType
{
    /// <summary>CPU bottleneck.</summary>
    CPU,
    /// <summary>Memory bottleneck.</summary>
    Memory,
    /// <summary>Disk I/O bottleneck.</summary>
    DiskIO,
    /// <summary>Network I/O bottleneck.</summary>
    NetworkIO,
    /// <summary>Latency bottleneck.</summary>
    Latency,
    /// <summary>Throughput bottleneck.</summary>
    Throughput
}

/// <summary>
/// Bottleneck severity.
/// </summary>
public enum BottleneckSeverity
{
    /// <summary>Low severity.</summary>
    Low,
    /// <summary>Medium severity.</summary>
    Medium,
    /// <summary>High severity.</summary>
    High,
    /// <summary>Critical severity.</summary>
    Critical
}

/// <summary>
/// Performance bottleneck.
/// </summary>
public sealed class PerformanceBottleneck
{
    /// <summary>Gets or sets the type.</summary>
    public BottleneckType Type { get; init; }

    /// <summary>Gets or sets the severity.</summary>
    public BottleneckSeverity Severity { get; init; }

    /// <summary>Gets or sets the current value.</summary>
    public double CurrentValue { get; init; }

    /// <summary>Gets or sets the threshold.</summary>
    public double Threshold { get; init; }

    /// <summary>Gets or sets the description.</summary>
    public string Description { get; init; } = string.Empty;
}

/// <summary>
/// Tuning priority.
/// </summary>
public enum TuningPriority
{
    /// <summary>Low priority.</summary>
    Low,
    /// <summary>Medium priority.</summary>
    Medium,
    /// <summary>High priority.</summary>
    High,
    /// <summary>Critical priority.</summary>
    Critical
}

/// <summary>
/// Tuning recommendation.
/// </summary>
public sealed class TuningRecommendation
{
    /// <summary>Gets or sets the recommendation identifier.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets or sets the component identifier.</summary>
    public string ComponentId { get; init; } = string.Empty;

    /// <summary>Gets or sets the category.</summary>
    public string Category { get; init; } = string.Empty;

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Gets or sets the description.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Gets or sets the priority.</summary>
    public TuningPriority Priority { get; init; }

    /// <summary>Gets or sets the parameters.</summary>
    public Dictionary<string, object> Parameters { get; init; } = new();

    /// <summary>Gets or sets the expected improvement.</summary>
    public string ExpectedImprovement { get; init; } = string.Empty;

    /// <summary>Gets or sets whether to auto-apply.</summary>
    public bool AutoApply { get; init; }

    /// <summary>Gets or sets the source.</summary>
    public RecommendationSource Source { get; init; }
}

/// <summary>
/// Tuning result.
/// </summary>
public sealed class TuningResult
{
    /// <summary>Gets or sets the recommendation identifier.</summary>
    public string RecommendationId { get; init; } = string.Empty;

    /// <summary>Gets or sets whether successful.</summary>
    public bool Success { get; init; }

    /// <summary>Gets or sets the execution time.</summary>
    public TimeSpan ExecutionTime { get; init; }

    /// <summary>Gets or sets the applied parameters.</summary>
    public Dictionary<string, object>? AppliedParameters { get; init; }

    /// <summary>Gets or sets the reason.</summary>
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// Tuning event record.
/// </summary>
public sealed class TuningEvent
{
    /// <summary>Gets or sets the event identifier.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Gets or sets the component identifier.</summary>
    public string ComponentId { get; init; } = string.Empty;

    /// <summary>Gets or sets the recommendation identifier.</summary>
    public string RecommendationId { get; init; } = string.Empty;

    /// <summary>Gets or sets the timestamp.</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>Gets or sets the parameters changed.</summary>
    public Dictionary<string, object> ParametersChanged { get; init; } = new();

    /// <summary>Gets or sets the previous values.</summary>
    public Dictionary<string, object> PreviousValues { get; init; } = new();

    /// <summary>Gets or sets whether successful.</summary>
    public bool Success { get; init; }

    /// <summary>Gets or sets the message.</summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// Performance analysis result.
/// </summary>
public sealed class PerformanceAnalysisResult
{
    /// <summary>Gets or sets the component identifier.</summary>
    public string ComponentId { get; init; } = string.Empty;

    /// <summary>Gets or sets whether there is sufficient data.</summary>
    public bool HasSufficientData { get; init; }

    /// <summary>Gets or sets the analysis time.</summary>
    public DateTime AnalysisTime { get; init; }

    /// <summary>Gets or sets the average latency in ms.</summary>
    public double AverageLatencyMs { get; init; }

    /// <summary>Gets or sets the P95 latency in ms.</summary>
    public double P95LatencyMs { get; init; }

    /// <summary>Gets or sets the P99 latency in ms.</summary>
    public double P99LatencyMs { get; init; }

    /// <summary>Gets or sets the average throughput.</summary>
    public double AverageThroughput { get; init; }

    /// <summary>Gets or sets the average error rate.</summary>
    public double AverageErrorRate { get; init; }

    /// <summary>Gets or sets the performance score.</summary>
    public double PerformanceScore { get; init; }

    /// <summary>Gets or sets the bottlenecks.</summary>
    public List<PerformanceBottleneck> Bottlenecks { get; init; } = new();

    /// <summary>Gets or sets the current configuration.</summary>
    public TuningConfiguration? CurrentConfiguration { get; init; }

    /// <summary>Gets or sets the recommendations.</summary>
    public List<TuningRecommendation> Recommendations { get; init; } = new();

    /// <summary>Gets or sets query optimizations.</summary>
    public List<QueryOptimizationRecommendation> QueryOptimizations { get; init; } = new();

    /// <summary>Gets or sets the reason.</summary>
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// Performance statistics.
/// </summary>
public sealed class PerformanceStatistics
{
    /// <summary>Gets or sets the number of tracked components.</summary>
    public int TrackedComponents { get; init; }

    /// <summary>Gets or sets the total tuning events.</summary>
    public int TotalTuningEvents { get; init; }

    /// <summary>Gets or sets successful tunings.</summary>
    public int SuccessfulTunings { get; init; }

    /// <summary>Gets or sets component stats.</summary>
    public Dictionary<string, ComponentPerformanceStats> ComponentStats { get; init; } = new();

    /// <summary>Gets or sets query performance.</summary>
    public QueryPerformanceStats QueryPerformance { get; init; } = new();

    /// <summary>Gets or sets recent tuning events.</summary>
    public List<TuningEvent> RecentTuningEvents { get; init; } = new();
}

/// <summary>
/// Component performance stats.
/// </summary>
public sealed class ComponentPerformanceStats
{
    /// <summary>Gets or sets the component identifier.</summary>
    public string ComponentId { get; init; } = string.Empty;

    /// <summary>Gets or sets the data points count.</summary>
    public int DataPoints { get; init; }

    /// <summary>Gets or sets the average latency.</summary>
    public double AverageLatencyMs { get; init; }

    /// <summary>Gets or sets the average throughput.</summary>
    public double AverageThroughput { get; init; }

    /// <summary>Gets or sets the average error rate.</summary>
    public double AverageErrorRate { get; init; }

    /// <summary>Gets or sets the last update time.</summary>
    public DateTime LastUpdated { get; init; }
}

/// <summary>
/// Tuning cycle result.
/// </summary>
public sealed class TuningCycleResult
{
    /// <summary>Gets or sets the start time.</summary>
    public DateTime StartTime { get; init; }

    /// <summary>Gets or sets the end time.</summary>
    public DateTime EndTime { get; init; }

    /// <summary>Gets the duration.</summary>
    public TimeSpan Duration => EndTime - StartTime;

    /// <summary>Gets or sets components analyzed count.</summary>
    public int ComponentsAnalyzed { get; init; }

    /// <summary>Gets or sets tunings applied count.</summary>
    public int TuningsApplied { get; init; }
}

#region Query Types

/// <summary>
/// Query execution information.
/// </summary>
public sealed class QueryExecutionInfo
{
    /// <summary>Gets or sets the query text.</summary>
    public string? QueryText { get; init; }

    /// <summary>Gets or sets the execution time in ms.</summary>
    public long ExecutionTimeMs { get; init; }

    /// <summary>Gets or sets the rows scanned.</summary>
    public long RowsScanned { get; init; }

    /// <summary>Gets or sets the rows returned.</summary>
    public long RowsReturned { get; init; }

    /// <summary>Gets or sets the bytes read.</summary>
    public long BytesRead { get; init; }
}

/// <summary>
/// Query metrics.
/// </summary>
public sealed class QueryMetrics
{
    /// <summary>Gets or sets the query hash.</summary>
    public string QueryHash { get; init; } = string.Empty;

    /// <summary>Gets or sets the query pattern.</summary>
    public string? QueryPattern { get; set; }

    /// <summary>Gets or sets the execution count.</summary>
    public long ExecutionCount { get; set; }

    /// <summary>Gets or sets the total execution time in ms.</summary>
    public long TotalExecutionTimeMs { get; set; }

    /// <summary>Gets or sets the total rows scanned.</summary>
    public long TotalRowsScanned { get; set; }

    /// <summary>Gets or sets the total rows returned.</summary>
    public long TotalRowsReturned { get; set; }

    /// <summary>Gets or sets the total bytes read.</summary>
    public long TotalBytesRead { get; set; }

    /// <summary>Gets or sets the last execution time.</summary>
    public DateTime LastExecution { get; set; }

    /// <summary>Gets the execution history.</summary>
    public List<QueryExecution> ExecutionHistory { get; } = new();
}

/// <summary>
/// Query execution record.
/// </summary>
public sealed class QueryExecution
{
    /// <summary>Gets or sets the timestamp.</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>Gets or sets the execution time in ms.</summary>
    public long ExecutionTimeMs { get; init; }

    /// <summary>Gets or sets the rows scanned.</summary>
    public long RowsScanned { get; init; }

    /// <summary>Gets or sets the rows returned.</summary>
    public long RowsReturned { get; init; }
}

/// <summary>
/// Query optimization type.
/// </summary>
public enum QueryOptimizationType
{
    /// <summary>Add index.</summary>
    AddIndex,
    /// <summary>Rewrite query.</summary>
    QueryRewrite,
    /// <summary>Add caching.</summary>
    AddCache,
    /// <summary>Add partitioning.</summary>
    Partitioning,
    /// <summary>Add materialization.</summary>
    Materialization
}

/// <summary>
/// Query priority.
/// </summary>
public enum QueryPriority
{
    /// <summary>Low priority.</summary>
    Low,
    /// <summary>Medium priority.</summary>
    Medium,
    /// <summary>High priority.</summary>
    High,
    /// <summary>Critical priority.</summary>
    Critical
}

/// <summary>
/// Query optimization recommendation.
/// </summary>
public sealed class QueryOptimizationRecommendation
{
    /// <summary>Gets or sets the query hash.</summary>
    public string QueryHash { get; init; } = string.Empty;

    /// <summary>Gets or sets the query pattern.</summary>
    public string? QueryPattern { get; init; }

    /// <summary>Gets or sets the optimization type.</summary>
    public QueryOptimizationType Type { get; init; }

    /// <summary>Gets or sets the description.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Gets or sets the estimated improvement.</summary>
    public double EstimatedImprovement { get; init; }

    /// <summary>Gets or sets the priority.</summary>
    public QueryPriority Priority { get; init; }

    /// <summary>Gets or sets the current avg time in ms.</summary>
    public double CurrentAvgTimeMs { get; init; }

    /// <summary>Gets or sets the execution count.</summary>
    public long ExecutionCount { get; init; }
}

/// <summary>
/// Query performance stats.
/// </summary>
public sealed class QueryPerformanceStats
{
    /// <summary>Gets or sets the total queries.</summary>
    public long TotalQueries { get; init; }

    /// <summary>Gets or sets the unique query count.</summary>
    public int UniqueQueryCount { get; init; }

    /// <summary>Gets or sets the slow query count.</summary>
    public int SlowQueryCount { get; init; }

    /// <summary>Gets or sets the average execution time in ms.</summary>
    public double AverageExecutionTimeMs { get; init; }

    /// <summary>Gets or sets the total rows scanned.</summary>
    public long TotalRowsScanned { get; init; }

    /// <summary>Gets or sets the total rows returned.</summary>
    public long TotalRowsReturned { get; init; }

    /// <summary>Gets or sets the overall selectivity.</summary>
    public double OverallSelectivity { get; init; }

    /// <summary>Gets or sets the top slow queries.</summary>
    public List<SlowQueryInfo> TopSlowQueries { get; init; } = new();
}

/// <summary>
/// Slow query info.
/// </summary>
public sealed class SlowQueryInfo
{
    /// <summary>Gets or sets the query hash.</summary>
    public string QueryHash { get; init; } = string.Empty;

    /// <summary>Gets or sets the query pattern.</summary>
    public string? QueryPattern { get; init; }

    /// <summary>Gets or sets the average time in ms.</summary>
    public double AvgTimeMs { get; init; }

    /// <summary>Gets or sets the execution count.</summary>
    public long ExecutionCount { get; init; }
}

#endregion

#endregion
