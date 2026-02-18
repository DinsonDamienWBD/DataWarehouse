using DataWarehouse.SDK.AI;

namespace DataWarehouse.Plugins.UltimateIntelligence.Strategies.Features;

/// <summary>
/// Anomaly detection feature strategy.
/// Detects unusual patterns in data using AI and statistical methods.
/// </summary>
public sealed class AnomalyDetectionStrategy : FeatureStrategyBase
{
    /// <inheritdoc/>
    public override string StrategyId => "feature-anomaly-detection";

    /// <inheritdoc/>
    public override string StrategyName => "Anomaly Detection";

    /// <inheritdoc/>
    public override IntelligenceStrategyInfo Info => new()
    {
        ProviderName = "Anomaly Detection",
        Description = "AI-powered detection of unusual patterns and outliers in data",
        Capabilities = IntelligenceCapabilities.AnomalyDetection,
        ConfigurationRequirements = new[]
        {
            new ConfigurationRequirement { Key = "SensitivityThreshold", Description = "Anomaly sensitivity (0-1)", Required = false, DefaultValue = "0.95" },
            new ConfigurationRequirement { Key = "WindowSize", Description = "Time window for analysis", Required = false, DefaultValue = "100" },
            new ConfigurationRequirement { Key = "Method", Description = "Detection method (embedding|statistical|hybrid)", Required = false, DefaultValue = "hybrid" }
        },
        CostTier = 2,
        LatencyTier = 2,
        RequiresNetworkAccess = true,
        Tags = new[] { "anomaly-detection", "outlier", "monitoring", "security" }
    };

    private readonly List<float[]> _baselineEmbeddings = new();
    private float[]? _centroid;

    /// <summary>
    /// Establishes a baseline from normal data samples.
    /// </summary>
    public async Task EstablishBaselineAsync(
        IEnumerable<string> normalSamples,
        CancellationToken ct = default)
    {
        if (AIProvider == null)
            throw new InvalidOperationException("AI provider required for baseline establishment");

        await ExecuteWithTrackingAsync(async () =>
        {
            _baselineEmbeddings.Clear();

            foreach (var sample in normalSamples)
            {
                var embedding = await AIProvider.GetEmbeddingsAsync(sample, ct);
                _baselineEmbeddings.Add(embedding);
                RecordEmbeddings(1);
            }

            // Calculate centroid
            if (_baselineEmbeddings.Count > 0)
            {
                var dim = _baselineEmbeddings[0].Length;
                _centroid = new float[dim];

                foreach (var emb in _baselineEmbeddings)
                {
                    for (int i = 0; i < dim; i++)
                        _centroid[i] += emb[i];
                }

                for (int i = 0; i < dim; i++)
                    _centroid[i] /= _baselineEmbeddings.Count;
            }
        });
    }

    /// <summary>
    /// Detects anomalies in a data sample.
    /// </summary>
    public async Task<AnomalyResult> DetectAsync(
        string sample,
        CancellationToken ct = default)
    {
        if (AIProvider == null)
            throw new InvalidOperationException("AI provider required for anomaly detection");

        return await ExecuteWithTrackingAsync(async () =>
        {
            var method = GetConfig("Method") ?? "hybrid";
            var threshold = float.Parse(GetConfig("SensitivityThreshold") ?? "0.95");

            var embedding = await AIProvider.GetEmbeddingsAsync(sample, ct);
            RecordEmbeddings(1);

            var isAnomaly = false;
            var anomalyScore = 0f;
            var reasons = new List<string>();

            // Embedding-based detection
            if (method is "embedding" or "hybrid" && _centroid != null)
            {
                var distance = CalculateCosineSimilarity(embedding, _centroid);
                anomalyScore = 1 - distance;

                if (anomalyScore > (1 - threshold))
                {
                    isAnomaly = true;
                    reasons.Add($"Embedding distance from baseline: {anomalyScore:F3}");
                }
            }

            // AI-based detection for hybrid mode
            if (method is "hybrid" && AIProvider != null)
            {
                var prompt = $@"Analyze this data sample for anomalies or unusual patterns:

Sample:
{sample}

Consider:
1. Is this data unusual or unexpected?
2. Are there any suspicious patterns?
3. Does it deviate from normal behavior?

Return JSON:
{{
  ""is_anomaly"": true/false,
  ""confidence"": 0.0-1.0,
  ""reasons"": [""reason1"", ""reason2""]
}}";

                var response = await AIProvider.CompleteAsync(new AIRequest
                {
                    Prompt = prompt,
                    MaxTokens = 300,
                    Temperature = 0.2f
                }, ct);

                RecordTokens(response.Usage?.TotalTokens ?? 0);

                var aiResult = ParseAnomalyResponse(response.Content);
                if (aiResult.IsAnomaly)
                {
                    isAnomaly = true;
                    anomalyScore = Math.Max(anomalyScore, aiResult.Confidence);
                    reasons.AddRange(aiResult.Reasons);
                }
            }

            return new AnomalyResult
            {
                Sample = sample.Length > 100 ? sample.Substring(0, 100) + "..." : sample,
                IsAnomaly = isAnomaly,
                AnomalyScore = anomalyScore,
                Reasons = reasons,
                DetectedAt = DateTime.UtcNow
            };
        });
    }

    /// <summary>
    /// Detects anomalies in a batch of samples.
    /// </summary>
    public async Task<IEnumerable<AnomalyResult>> DetectBatchAsync(
        IEnumerable<string> samples,
        CancellationToken ct = default)
    {
        var results = new List<AnomalyResult>();
        foreach (var sample in samples)
        {
            results.Add(await DetectAsync(sample, ct));
        }
        return results;
    }

    /// <summary>
    /// Analyzes time-series data for anomalies.
    /// </summary>
    public async Task<TimeSeriesAnomalyResult> DetectTimeSeriesAnomaliesAsync(
        IEnumerable<(DateTime Timestamp, double Value)> timeSeries,
        CancellationToken ct = default)
    {
        return await ExecuteWithTrackingAsync(async () =>
        {
            var data = timeSeries.ToList();
            var windowSize = int.Parse(GetConfig("WindowSize") ?? "100");
            var threshold = float.Parse(GetConfig("SensitivityThreshold") ?? "0.95");

            var anomalies = new List<TimeSeriesAnomaly>();

            // Calculate statistics
            var values = data.Select(d => d.Value).ToList();
            var mean = values.Average();
            var stdDev = Math.Sqrt(values.Sum(v => Math.Pow(v - mean, 2)) / values.Count);

            // Z-score based detection
            for (int i = 0; i < data.Count; i++)
            {
                var zScore = Math.Abs((data[i].Value - mean) / stdDev);
                var normalizedScore = 1 - (1 / (1 + Math.Exp(-(zScore - 3)))); // Sigmoid normalization

                if (normalizedScore > (1 - threshold))
                {
                    anomalies.Add(new TimeSeriesAnomaly
                    {
                        Timestamp = data[i].Timestamp,
                        Value = data[i].Value,
                        ExpectedValue = mean,
                        AnomalyScore = (float)normalizedScore,
                        Reason = $"Z-score: {zScore:F2} (>3 standard deviations)"
                    });
                }
            }

            // Optional AI analysis for complex patterns
            if (AIProvider != null && anomalies.Count > 0)
            {
                var prompt = $@"Analyze these time series anomalies:

Data points flagged as anomalous:
{string.Join("\n", anomalies.Take(5).Select(a => $"- {a.Timestamp}: {a.Value} (expected ~{a.ExpectedValue:F2})"))}

Provide insights on possible causes and patterns.";

                var response = await AIProvider.CompleteAsync(new AIRequest
                {
                    Prompt = prompt,
                    MaxTokens = 200
                }, ct);

                RecordTokens(response.Usage?.TotalTokens ?? 0);
            }

            return new TimeSeriesAnomalyResult
            {
                TotalDataPoints = data.Count,
                AnomaliesDetected = anomalies.Count,
                AnomalyRate = data.Count > 0 ? (float)anomalies.Count / data.Count : 0,
                Anomalies = anomalies,
                Statistics = new TimeSeriesStatistics
                {
                    Mean = mean,
                    StandardDeviation = stdDev,
                    Min = values.Min(),
                    Max = values.Max()
                }
            };
        });
    }

    private static float CalculateCosineSimilarity(float[] a, float[] b)
    {
        float dot = 0, normA = 0, normB = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        var denominator = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denominator == 0 ? 0 : dot / denominator;
    }

    private static (bool IsAnomaly, float Confidence, List<string> Reasons) ParseAnomalyResponse(string response)
    {
        try
        {
            var jsonStart = response.IndexOf('{');
            var jsonEnd = response.LastIndexOf('}');
            if (jsonStart >= 0 && jsonEnd > jsonStart)
            {
                var json = response.Substring(jsonStart, jsonEnd - jsonStart + 1);
                var doc = System.Text.Json.JsonDocument.Parse(json);

                var isAnomaly = doc.RootElement.TryGetProperty("is_anomaly", out var a) && a.GetBoolean();
                var confidence = doc.RootElement.TryGetProperty("confidence", out var c) ? c.GetSingle() : 0.5f;
                var reasons = doc.RootElement.TryGetProperty("reasons", out var r)
                    ? r.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                    : new List<string>();

                return (isAnomaly, confidence, reasons);
            }
        }
        catch { /* Parsing failure — return default not anomaly */ }

        return (false, 0, new List<string>());
    }
}

/// <summary>
/// Result of anomaly detection.
/// </summary>
public sealed class AnomalyResult
{
    /// <summary>Truncated sample for reference.</summary>
    public string Sample { get; init; } = "";

    /// <summary>Whether the sample is anomalous.</summary>
    public bool IsAnomaly { get; init; }

    /// <summary>Anomaly score (0-1, higher = more anomalous).</summary>
    public float AnomalyScore { get; init; }

    /// <summary>Reasons for anomaly classification.</summary>
    public List<string> Reasons { get; init; } = new();

    /// <summary>When the anomaly was detected.</summary>
    public DateTime DetectedAt { get; init; }
}

/// <summary>
/// Result of time series anomaly detection.
/// </summary>
public sealed class TimeSeriesAnomalyResult
{
    /// <summary>Total data points analyzed.</summary>
    public int TotalDataPoints { get; init; }

    /// <summary>Number of anomalies detected.</summary>
    public int AnomaliesDetected { get; init; }

    /// <summary>Anomaly rate (0-1).</summary>
    public float AnomalyRate { get; init; }

    /// <summary>Detected anomalies.</summary>
    public List<TimeSeriesAnomaly> Anomalies { get; init; } = new();

    /// <summary>Time series statistics.</summary>
    public TimeSeriesStatistics Statistics { get; init; } = new();
}

/// <summary>
/// Individual time series anomaly.
/// </summary>
public sealed class TimeSeriesAnomaly
{
    /// <summary>Timestamp of anomaly.</summary>
    public DateTime Timestamp { get; init; }

    /// <summary>Actual value.</summary>
    public double Value { get; init; }

    /// <summary>Expected value.</summary>
    public double ExpectedValue { get; init; }

    /// <summary>Anomaly score.</summary>
    public float AnomalyScore { get; init; }

    /// <summary>Reason for classification.</summary>
    public string Reason { get; init; } = "";
}

/// <summary>
/// Time series statistics.
/// </summary>
public sealed class TimeSeriesStatistics
{
    public double Mean { get; init; }
    public double StandardDeviation { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
}
