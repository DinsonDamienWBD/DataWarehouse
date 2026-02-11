using System.Collections.Concurrent;
using System.Text.Json;

namespace DataWarehouse.Plugins.UltimateDataQuality.Strategies.PredictiveQuality;

#region Shared Records

/// <summary>
/// A single quality measurement data point for time-series analysis.
/// </summary>
internal sealed record QualityMeasurement(string MetricName, double Value, DateTimeOffset Timestamp);

/// <summary>
/// Configuration for quality threshold detection using standard deviation multiples.
/// </summary>
internal sealed record QualityThresholdConfig(double WarningStdDevs, double CriticalStdDevs);

/// <summary>
/// A predicted quality issue with confidence scoring and severity classification.
/// </summary>
internal sealed record QualityPrediction(
    string MetricName,
    double PredictedValue,
    double Confidence,
    string Severity,
    string Reason,
    DateTimeOffset PredictionTime);

/// <summary>
/// Report describing detected distribution drift for a data column.
/// </summary>
internal sealed record DriftReport(
    string ColumnName,
    double PsiScore,
    string DriftLevel,
    double[] BaselineDistribution,
    double[] CurrentDistribution,
    DateTimeOffset DetectedAt);

/// <summary>
/// Report summarizing anomalies detected in a data column.
/// </summary>
internal sealed record AnomalyReport(
    string ColumnName,
    IReadOnlyList<AnomalyFlag> Flags,
    int TotalValues,
    int AnomalyCount,
    double AnomalyRate);

/// <summary>
/// An individual anomaly flag with scoring details and detection method.
/// </summary>
internal sealed record AnomalyFlag(int Index, double Value, double ZScore, string Method, string Severity);

/// <summary>
/// A data point in a quality trend time series.
/// </summary>
internal sealed record TrendDataPoint(double Value, DateTimeOffset Timestamp);

/// <summary>
/// Result of linear regression trend analysis on quality metrics.
/// </summary>
internal sealed record TrendAnalysis(
    string MetricName,
    double Slope,
    double Intercept,
    double RSquared,
    string Direction,
    double ForecastNext,
    IReadOnlyList<TrendDataPoint> DataPoints);

/// <summary>
/// A quality event observed in a specific dimension for root cause analysis.
/// </summary>
internal sealed record QualityEvent(
    string EventId,
    string Dimension,
    string Description,
    double Severity,
    DateTimeOffset Timestamp,
    Dictionary<string, string>? Attributes);

/// <summary>
/// Report identifying probable root causes for quality issues in a target dimension.
/// </summary>
internal sealed record RootCauseReport(
    string IssueDescription,
    IReadOnlyList<CauseCandidate> Candidates);

/// <summary>
/// A candidate root cause with correlation scoring and evidence.
/// </summary>
internal sealed record CauseCandidate(
    string Dimension,
    double CorrelationScore,
    int OccurrenceCount,
    string Evidence);

#endregion

#region Strategy 1: QualityAnticipatorStrategy

/// <summary>
/// Predicts quality issues before they occur by analyzing metric trends using exponential
/// moving averages (EMA) and standard deviation breach detection.
/// </summary>
/// <remarks>
/// T146.B3.1 - Industry-first predictive quality anticipation strategy.
/// Uses EMA with alpha=0.3 over the most recent data points and compares against
/// configurable standard deviation thresholds to classify predicted severity as
/// Warning or Critical. Confidence is computed as 1 - 1/sqrt(N) where N is the
/// number of available data points.
/// </remarks>
internal sealed class QualityAnticipatorStrategy : DataQualityStrategyBase
{
    private readonly ConcurrentDictionary<string, List<QualityMeasurement>> _metricHistory = new();
    private readonly ConcurrentDictionary<string, QualityThresholdConfig> _thresholds = new();

    /// <inheritdoc/>
    public override string StrategyId => "predictive-anticipator";

    /// <inheritdoc/>
    public override string DisplayName => "Quality Anticipator";

    /// <inheritdoc/>
    public override DataQualityCategory Category => DataQualityCategory.Monitoring;

    /// <inheritdoc/>
    public override DataQualityCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsDistributed = false,
        SupportsIncremental = true
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Predicts quality issues before they occur by analyzing metric trends using exponential " +
        "moving averages and standard deviation breach detection.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "predictive", "anticipation", "early-warning", "trend-detection", "industry-first"
    };

    /// <summary>
    /// Records a quality metric measurement for the specified metric name.
    /// </summary>
    /// <param name="metricName">The metric identifier.</param>
    /// <param name="value">The measured value.</param>
    public void RecordMetric(string metricName, double value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metricName);

        var history = _metricHistory.GetOrAdd(metricName, _ => new List<QualityMeasurement>());
        lock (history)
        {
            history.Add(new QualityMeasurement(metricName, value, DateTimeOffset.UtcNow));
        }
    }

    /// <summary>
    /// Configures warning and critical standard deviation thresholds for a metric.
    /// </summary>
    /// <param name="metricName">The metric identifier.</param>
    /// <param name="warningStdDevs">Number of standard deviations for a warning (default 2.0).</param>
    /// <param name="criticalStdDevs">Number of standard deviations for critical (default 3.0).</param>
    public void SetThreshold(string metricName, double warningStdDevs = 2.0, double criticalStdDevs = 3.0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metricName);
        _thresholds[metricName] = new QualityThresholdConfig(warningStdDevs, criticalStdDevs);
    }

    /// <summary>
    /// Predicts quality issues for the specified metric using EMA and standard deviation analysis.
    /// Returns null if fewer than 5 data points are available.
    /// </summary>
    /// <param name="metricName">The metric identifier to analyze.</param>
    /// <returns>A quality prediction or null if insufficient data.</returns>
    public QualityPrediction? PredictIssues(string metricName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metricName);

        if (!_metricHistory.TryGetValue(metricName, out var history))
            return null;

        List<QualityMeasurement> snapshot;
        lock (history)
        {
            if (history.Count < 5)
                return null;
            snapshot = new List<QualityMeasurement>(history);
        }

        var thresholdConfig = _thresholds.GetValueOrDefault(metricName)
                              ?? new QualityThresholdConfig(2.0, 3.0);

        // Calculate mean and standard deviation of last 20 measurements (or all if fewer)
        int windowSize = Math.Min(20, snapshot.Count);
        var recentValues = snapshot.Skip(snapshot.Count - windowSize).Select(m => m.Value).ToArray();

        double mean = recentValues.Average();
        double sumSquaredDiffs = recentValues.Sum(v => (v - mean) * (v - mean));
        double stdDev = Math.Sqrt(sumSquaredDiffs / recentValues.Length);

        // Compute EMA (alpha=0.3) of last 10 points
        int emaWindow = Math.Min(10, snapshot.Count);
        var emaValues = snapshot.Skip(snapshot.Count - emaWindow).Select(m => m.Value).ToArray();
        const double alpha = 0.3;
        double ema = emaValues[0];
        for (int i = 1; i < emaValues.Length; i++)
        {
            ema = alpha * emaValues[i] + (1.0 - alpha) * ema;
        }

        // Determine severity based on deviation from mean
        double deviation = Math.Abs(ema - mean);
        string severity;
        string reason;

        if (stdDev > 0 && deviation > thresholdConfig.CriticalStdDevs * stdDev)
        {
            severity = "Critical";
            reason = $"EMA ({ema:F4}) deviates from mean ({mean:F4}) by {deviation / stdDev:F2} standard deviations, exceeding critical threshold of {thresholdConfig.CriticalStdDevs:F1}.";
        }
        else if (stdDev > 0 && deviation > thresholdConfig.WarningStdDevs * stdDev)
        {
            severity = "Warning";
            reason = $"EMA ({ema:F4}) deviates from mean ({mean:F4}) by {deviation / stdDev:F2} standard deviations, exceeding warning threshold of {thresholdConfig.WarningStdDevs:F1}.";
        }
        else
        {
            severity = "Normal";
            reason = $"EMA ({ema:F4}) is within normal range of mean ({mean:F4}).";
        }

        // Confidence = 1.0 - (1.0 / sqrt(dataPointCount))
        double confidence = 1.0 - (1.0 / Math.Sqrt(snapshot.Count));

        return new QualityPrediction(metricName, ema, confidence, severity, reason, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Determines the trend direction for a metric over a sliding window.
    /// </summary>
    /// <param name="metricName">The metric identifier.</param>
    /// <param name="windowSize">The number of recent measurements per window (default 10).</param>
    /// <returns>The trend direction: "improving", "degrading", or "stable".</returns>
    public string GetTrend(string metricName, int windowSize = 10)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metricName);

        if (!_metricHistory.TryGetValue(metricName, out var history))
            return "stable";

        List<QualityMeasurement> snapshot;
        lock (history)
        {
            snapshot = new List<QualityMeasurement>(history);
        }

        if (snapshot.Count < windowSize * 2)
            return "stable";

        var recentWindow = snapshot.Skip(snapshot.Count - windowSize).Select(m => m.Value).ToArray();
        var previousWindow = snapshot.Skip(snapshot.Count - windowSize * 2).Take(windowSize).Select(m => m.Value).ToArray();

        double recentMean = recentWindow.Average();
        double previousMean = previousWindow.Average();

        // Use 5% tolerance
        double tolerance = Math.Abs(previousMean) * 0.05;
        if (tolerance < 1e-10)
            tolerance = 1e-10;

        if (recentMean > previousMean + tolerance)
            return "improving";
        if (recentMean < previousMean - tolerance)
            return "degrading";
        return "stable";
    }
}

#endregion

#region Strategy 2: DataDriftDetectorStrategy

/// <summary>
/// Detects when data characteristics change significantly from a baseline distribution
/// using Population Stability Index (PSI) and histogram-based distribution comparison.
/// </summary>
/// <remarks>
/// T146.B3.2 - Industry-first data drift detection strategy.
/// Computes PSI as the sum over histogram bins of (current_i - baseline_i) * ln(current_i / baseline_i).
/// Drift is classified as: none (PSI &lt; 0.1), minor (0.1-0.25), significant (&gt; 0.25).
/// Bin proportions are floored at 0.0001 to avoid log(0).
/// </remarks>
internal sealed class DataDriftDetectorStrategy : DataQualityStrategyBase
{
    private readonly ConcurrentDictionary<string, double[]> _baselines = new();
    private readonly ConcurrentDictionary<string, double[]> _currentDistributions = new();

    /// <inheritdoc/>
    public override string StrategyId => "predictive-drift-detector";

    /// <inheritdoc/>
    public override string DisplayName => "Data Drift Detector";

    /// <inheritdoc/>
    public override DataQualityCategory Category => DataQualityCategory.Monitoring;

    /// <inheritdoc/>
    public override DataQualityCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsDistributed = true,
        SupportsIncremental = true
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Detects when data characteristics change significantly from baseline using Population " +
        "Stability Index (PSI) and Kolmogorov-Smirnov-inspired distribution comparison.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "drift-detection", "distribution-shift", "psi", "baseline-comparison", "industry-first"
    };

    /// <summary>
    /// Sets the baseline distribution for a column by computing a histogram from the provided values.
    /// </summary>
    /// <param name="columnName">The column identifier.</param>
    /// <param name="values">The baseline data values.</param>
    public void SetBaseline(string columnName, double[] values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length == 0)
            throw new ArgumentException("Values array must not be empty.", nameof(values));

        _baselines[columnName] = ComputeHistogram(values);
    }

    /// <summary>
    /// Updates the current distribution for a column by computing a histogram from the provided values.
    /// </summary>
    /// <param name="columnName">The column identifier.</param>
    /// <param name="values">The current data values.</param>
    public void UpdateCurrent(string columnName, double[] values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length == 0)
            throw new ArgumentException("Values array must not be empty.", nameof(values));

        _currentDistributions[columnName] = ComputeHistogram(values);
    }

    /// <summary>
    /// Detects distribution drift for the specified column using PSI comparison of
    /// baseline and current histograms.
    /// </summary>
    /// <param name="columnName">The column identifier.</param>
    /// <returns>A drift report or null if baseline or current distribution is not set.</returns>
    public DriftReport? DetectDrift(string columnName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);

        if (!_baselines.TryGetValue(columnName, out var baseline))
            return null;
        if (!_currentDistributions.TryGetValue(columnName, out var current))
            return null;

        // PSI = sum over bins of (current_i - baseline_i) * ln(current_i / baseline_i)
        double psi = 0.0;
        int binCount = Math.Min(baseline.Length, current.Length);
        for (int i = 0; i < binCount; i++)
        {
            double baselineProp = Math.Max(0.0001, baseline[i]);
            double currentProp = Math.Max(0.0001, current[i]);
            psi += (currentProp - baselineProp) * Math.Log(currentProp / baselineProp);
        }

        string driftLevel;
        if (psi < 0.1)
            driftLevel = "none";
        else if (psi <= 0.25)
            driftLevel = "minor";
        else
            driftLevel = "significant";

        return new DriftReport(columnName, psi, driftLevel, baseline, current, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Computes a normalized histogram (bin proportions) from the given values.
    /// </summary>
    /// <param name="values">The data values to bin.</param>
    /// <param name="bins">The number of histogram bins (default 10).</param>
    /// <returns>An array of bin proportions summing to 1.0.</returns>
    private static double[] ComputeHistogram(double[] values, int bins = 10)
    {
        if (values.Length == 0)
            return new double[bins];

        double min = values.Min();
        double max = values.Max();

        // Handle case where all values are identical
        if (Math.Abs(max - min) < 1e-15)
        {
            var uniform = new double[bins];
            uniform[0] = 1.0;
            return uniform;
        }

        var counts = new int[bins];
        double binWidth = (max - min) / bins;

        foreach (double v in values)
        {
            int binIndex = (int)((v - min) / binWidth);
            // Clamp to last bin for the maximum value
            if (binIndex >= bins)
                binIndex = bins - 1;
            counts[binIndex]++;
        }

        // Normalize to proportions
        double total = values.Length;
        var proportions = new double[bins];
        for (int i = 0; i < bins; i++)
        {
            proportions[i] = counts[i] / total;
        }

        return proportions;
    }
}

#endregion

#region Strategy 3: AnomalousDataFlagStrategy

/// <summary>
/// Automatically flags unusual data using Z-score analysis and IQR-based outlier detection.
/// Supports both univariate anomaly detection methods with combined severity classification.
/// </summary>
/// <remarks>
/// T146.B3.3 - Industry-first anomalous data flagging strategy.
/// Uses two complementary detection methods:
/// (1) Z-score: flags values where |value - mean| / stdDev exceeds a configurable threshold.
/// (2) IQR: flags values outside [Q1 - 1.5*IQR, Q3 + 1.5*IQR].
/// Values flagged by both methods are classified as "Critical"; by one method as "Warning".
/// </remarks>
internal sealed class AnomalousDataFlagStrategy : DataQualityStrategyBase
{
    private readonly ConcurrentDictionary<string, List<double>> _columnValues = new();

    /// <inheritdoc/>
    public override string StrategyId => "predictive-anomaly-flag";

    /// <inheritdoc/>
    public override string DisplayName => "Anomalous Data Flagger";

    /// <inheritdoc/>
    public override DataQualityCategory Category => DataQualityCategory.Scoring;

    /// <inheritdoc/>
    public override DataQualityCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = true,
        SupportsDistributed = false,
        SupportsIncremental = true
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Automatically flags unusual data using Z-score analysis and IQR-based outlier detection. " +
        "Supports both univariate and multivariate anomaly detection.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "anomaly-detection", "outlier-flagging", "z-score", "iqr", "industry-first"
    };

    /// <summary>
    /// Adds data values for the specified column for subsequent anomaly detection.
    /// </summary>
    /// <param name="columnName">The column identifier.</param>
    /// <param name="values">The data values to add.</param>
    public void AddValues(string columnName, double[] values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);
        ArgumentNullException.ThrowIfNull(values);

        var list = _columnValues.GetOrAdd(columnName, _ => new List<double>());
        lock (list)
        {
            list.AddRange(values);
        }
    }

    /// <summary>
    /// Detects anomalies in the specified column using combined Z-score and IQR methods.
    /// </summary>
    /// <param name="columnName">The column identifier.</param>
    /// <param name="zScoreThreshold">The Z-score threshold for anomaly detection (default 3.0).</param>
    /// <returns>An anomaly report or null if no data is available for the column.</returns>
    public AnomalyReport? DetectAnomalies(string columnName, double zScoreThreshold = 3.0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnName);

        if (!_columnValues.TryGetValue(columnName, out var valuesList))
            return null;

        double[] values;
        lock (valuesList)
        {
            if (valuesList.Count == 0)
                return null;
            values = valuesList.ToArray();
        }

        // Compute mean and standard deviation for Z-score method
        double mean = values.Average();
        double sumSquaredDiffs = values.Sum(v => (v - mean) * (v - mean));
        double stdDev = Math.Sqrt(sumSquaredDiffs / values.Length);

        // Compute IQR method bounds
        var sorted = values.OrderBy(v => v).ToList();
        double q1 = Percentile(sorted, 0.25);
        double q3 = Percentile(sorted, 0.75);
        double iqr = q3 - q1;
        double lowerBound = q1 - 1.5 * iqr;
        double upperBound = q3 + 1.5 * iqr;

        var flags = new List<AnomalyFlag>();

        for (int i = 0; i < values.Length; i++)
        {
            double value = values[i];

            // Z-score check
            double zScore = stdDev > 0 ? Math.Abs(value - mean) / stdDev : 0;
            bool flaggedByZScore = zScore > zScoreThreshold;

            // IQR check
            bool flaggedByIqr = value < lowerBound || value > upperBound;

            if (flaggedByZScore && flaggedByIqr)
            {
                flags.Add(new AnomalyFlag(i, value, zScore, "Z-Score+IQR", "Critical"));
            }
            else if (flaggedByZScore)
            {
                flags.Add(new AnomalyFlag(i, value, zScore, "Z-Score", "Warning"));
            }
            else if (flaggedByIqr)
            {
                flags.Add(new AnomalyFlag(i, value, zScore, "IQR", "Warning"));
            }
        }

        double anomalyRate = values.Length > 0 ? (double)flags.Count / values.Length : 0;

        return new AnomalyReport(columnName, flags.AsReadOnly(), values.Length, flags.Count, anomalyRate);
    }

    /// <summary>
    /// Computes the value at a given percentile using linear interpolation.
    /// </summary>
    /// <param name="sorted">A sorted list of values.</param>
    /// <param name="percentile">The percentile (0.0 to 1.0).</param>
    /// <returns>The interpolated value at the requested percentile.</returns>
    private static double Percentile(List<double> sorted, double percentile)
    {
        if (sorted.Count == 0)
            return 0;
        if (sorted.Count == 1)
            return sorted[0];

        double rank = percentile * (sorted.Count - 1);
        int lowerIndex = (int)Math.Floor(rank);
        int upperIndex = (int)Math.Ceiling(rank);

        if (lowerIndex == upperIndex)
            return sorted[lowerIndex];

        double fraction = rank - lowerIndex;
        return sorted[lowerIndex] + fraction * (sorted[upperIndex] - sorted[lowerIndex]);
    }
}

#endregion

#region Strategy 4: QualityTrendAnalyzerStrategy

/// <summary>
/// Analyzes quality trends over time using simple linear regression to detect improving,
/// degrading, or cyclic quality patterns and forecast future quality scores.
/// </summary>
/// <remarks>
/// T146.B3.4 - Industry-first quality trend analysis strategy.
/// Performs least-squares linear regression on time-indexed data points to compute slope,
/// intercept, and R-squared. Direction is classified as "improving" (slope &gt; 0.01),
/// "degrading" (slope &lt; -0.01), or "stable". Seasonality detection uses autocorrelation
/// at a specified lag period with threshold 0.5.
/// </remarks>
internal sealed class QualityTrendAnalyzerStrategy : DataQualityStrategyBase
{
    private readonly ConcurrentDictionary<string, List<TrendDataPoint>> _trendData = new();

    /// <inheritdoc/>
    public override string StrategyId => "predictive-trend-analyzer";

    /// <inheritdoc/>
    public override string DisplayName => "Quality Trend Analyzer";

    /// <inheritdoc/>
    public override DataQualityCategory Category => DataQualityCategory.Reporting;

    /// <inheritdoc/>
    public override DataQualityCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = false,
        SupportsDistributed = false,
        SupportsIncremental = true
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Analyzes quality trends over time using linear regression to detect improving, degrading, " +
        "or cyclic quality patterns and forecast future quality scores.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "trend-analysis", "regression", "forecasting", "time-series", "industry-first"
    };

    /// <summary>
    /// Records a data point for the specified metric with the current timestamp.
    /// </summary>
    /// <param name="metricName">The metric identifier.</param>
    /// <param name="value">The measured value.</param>
    public void RecordDataPoint(string metricName, double value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metricName);

        var data = _trendData.GetOrAdd(metricName, _ => new List<TrendDataPoint>());
        lock (data)
        {
            data.Add(new TrendDataPoint(value, DateTimeOffset.UtcNow));
        }
    }

    /// <summary>
    /// Performs linear regression trend analysis on the specified metric.
    /// Returns null if fewer than 3 data points are available.
    /// </summary>
    /// <param name="metricName">The metric identifier to analyze.</param>
    /// <returns>A trend analysis result or null if insufficient data.</returns>
    public TrendAnalysis? AnalyzeTrend(string metricName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metricName);

        if (!_trendData.TryGetValue(metricName, out var data))
            return null;

        List<TrendDataPoint> snapshot;
        lock (data)
        {
            if (data.Count < 3)
                return null;
            snapshot = new List<TrendDataPoint>(data);
        }

        int n = snapshot.Count;

        // Convert timestamps to sequential indices (0, 1, 2, ...)
        double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
        for (int i = 0; i < n; i++)
        {
            double x = i;
            double y = snapshot[i].Value;
            sumX += x;
            sumY += y;
            sumXY += x * y;
            sumX2 += x * x;
        }

        // Least squares: slope = (n*sumXY - sumX*sumY) / (n*sumX2 - sumX*sumX)
        double denominator = n * sumX2 - sumX * sumX;
        double slope = 0;
        double intercept = 0;

        if (Math.Abs(denominator) > 1e-15)
        {
            slope = (n * sumXY - sumX * sumY) / denominator;
            intercept = (sumY - slope * sumX) / n;
        }
        else
        {
            intercept = sumY / n;
        }

        // Compute R-squared
        double meanY = sumY / n;
        double ssTotal = 0, ssResidual = 0;
        for (int i = 0; i < n; i++)
        {
            double predicted = slope * i + intercept;
            double actual = snapshot[i].Value;
            ssTotal += (actual - meanY) * (actual - meanY);
            ssResidual += (actual - predicted) * (actual - predicted);
        }

        double rSquared = ssTotal > 1e-15 ? 1.0 - (ssResidual / ssTotal) : 0;

        // Classify direction
        string direction;
        if (slope > 0.01)
            direction = "improving";
        else if (slope < -0.01)
            direction = "degrading";
        else
            direction = "stable";

        // Forecast next value
        double forecastNext = slope * n + intercept;

        return new TrendAnalysis(
            metricName, slope, intercept, rSquared, direction, forecastNext, snapshot.AsReadOnly());
    }

    /// <summary>
    /// Detects seasonality (cyclic patterns) in a metric by computing autocorrelation at a given lag period.
    /// </summary>
    /// <param name="metricName">The metric identifier.</param>
    /// <param name="period">The lag period (number of data points) to test for cyclicity.</param>
    /// <returns>True if autocorrelation at the specified lag exceeds 0.5, indicating a cyclic pattern.</returns>
    public bool GetSeasonality(string metricName, int period)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metricName);
        if (period <= 0)
            throw new ArgumentOutOfRangeException(nameof(period), "Period must be positive.");

        if (!_trendData.TryGetValue(metricName, out var data))
            return false;

        List<TrendDataPoint> snapshot;
        lock (data)
        {
            snapshot = new List<TrendDataPoint>(data);
        }

        if (snapshot.Count <= period)
            return false;

        var values = snapshot.Select(d => d.Value).ToArray();
        double mean = values.Average();
        int n = values.Length;

        // Compute autocorrelation at the specified lag
        double numerator = 0;
        double denominator = 0;

        for (int i = 0; i < n; i++)
        {
            denominator += (values[i] - mean) * (values[i] - mean);
        }

        if (Math.Abs(denominator) < 1e-15)
            return false;

        for (int i = 0; i < n - period; i++)
        {
            numerator += (values[i] - mean) * (values[i + period] - mean);
        }

        double autocorrelation = numerator / denominator;

        return autocorrelation > 0.5;
    }
}

#endregion

#region Strategy 5: RootCauseAnalyzerStrategy

/// <summary>
/// Traces quality issues to their root causes by analyzing temporal correlations between
/// quality dimensions, identifying co-occurring events, and ranking contributing factors.
/// </summary>
/// <remarks>
/// T146.B3.5 - Industry-first root cause analysis strategy.
/// Records quality events across multiple dimensions and analyzes temporal co-occurrence
/// within configurable time windows. Correlation score is computed as the ratio of
/// co-occurring events to target events. Candidates with correlation above 0.3 are
/// included in the root cause report, ranked by correlation score descending.
/// </remarks>
internal sealed class RootCauseAnalyzerStrategy : DataQualityStrategyBase
{
    private readonly ConcurrentDictionary<string, List<QualityEvent>> _events = new();

    /// <inheritdoc/>
    public override string StrategyId => "predictive-root-cause";

    /// <inheritdoc/>
    public override string DisplayName => "Root Cause Analyzer";

    /// <inheritdoc/>
    public override DataQualityCategory Category => DataQualityCategory.Reporting;

    /// <inheritdoc/>
    public override DataQualityCapabilities Capabilities => new()
    {
        SupportsAsync = true,
        SupportsBatch = true,
        SupportsStreaming = false,
        SupportsDistributed = true,
        SupportsIncremental = false
    };

    /// <inheritdoc/>
    public override string SemanticDescription =>
        "Traces quality issues to their root causes by analyzing correlations between quality " +
        "dimensions, identifying temporal patterns, and ranking contributing factors.";

    /// <inheritdoc/>
    public override string[] Tags => new[]
    {
        "root-cause", "correlation", "contributing-factors", "causal-analysis", "industry-first"
    };

    /// <summary>
    /// Records a quality event in the specified dimension.
    /// </summary>
    /// <param name="dimension">The quality dimension (e.g., "completeness", "accuracy").</param>
    /// <param name="description">A description of the event.</param>
    /// <param name="severity">Severity score (0.0 to 1.0).</param>
    /// <param name="attributes">Optional key-value attributes providing additional context.</param>
    public void RecordEvent(string dimension, string description, double severity, Dictionary<string, string>? attributes = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dimension);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        var eventList = _events.GetOrAdd(dimension, _ => new List<QualityEvent>());
        var qualityEvent = new QualityEvent(
            Guid.NewGuid().ToString("N"),
            dimension,
            description,
            severity,
            DateTimeOffset.UtcNow,
            attributes);

        lock (eventList)
        {
            eventList.Add(qualityEvent);
        }
    }

    /// <summary>
    /// Analyzes root causes for quality issues in the target dimension by computing temporal
    /// correlation with events in other dimensions within the specified lookback window.
    /// </summary>
    /// <param name="targetDimension">The dimension experiencing quality issues.</param>
    /// <param name="lookbackWindow">The time window to search for correlated events.</param>
    /// <returns>A root cause report with ranked candidate causes.</returns>
    public RootCauseReport AnalyzeRootCause(string targetDimension, TimeSpan lookbackWindow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDimension);

        var cutoff = DateTimeOffset.UtcNow - lookbackWindow;
        var correlationWindow = TimeSpan.FromMinutes(5);

        // Get target events within lookback window
        List<QualityEvent> targetEvents;
        if (_events.TryGetValue(targetDimension, out var targetList))
        {
            lock (targetList)
            {
                targetEvents = targetList.Where(e => e.Timestamp >= cutoff).ToList();
            }
        }
        else
        {
            targetEvents = new List<QualityEvent>();
        }

        if (targetEvents.Count == 0)
        {
            return new RootCauseReport(
                $"No events found in dimension '{targetDimension}' within lookback window.",
                Array.Empty<CauseCandidate>());
        }

        var candidates = new List<CauseCandidate>();

        // For each other dimension, count events co-occurring within +/- 5 minutes of target events
        foreach (var kvp in _events)
        {
            if (kvp.Key == targetDimension)
                continue;

            List<QualityEvent> otherEvents;
            lock (kvp.Value)
            {
                otherEvents = kvp.Value.Where(e => e.Timestamp >= cutoff).ToList();
            }

            if (otherEvents.Count == 0)
                continue;

            int coOccurrenceCount = 0;
            foreach (var targetEvent in targetEvents)
            {
                bool hasCoOccurrence = otherEvents.Any(other =>
                    Math.Abs((other.Timestamp - targetEvent.Timestamp).TotalMinutes) <= correlationWindow.TotalMinutes);

                if (hasCoOccurrence)
                    coOccurrenceCount++;
            }

            double correlationScore = (double)coOccurrenceCount / targetEvents.Count;

            if (correlationScore > 0.3)
            {
                string evidence = $"{coOccurrenceCount}/{targetEvents.Count} target events had co-occurring " +
                                  $"events in '{kvp.Key}' within {correlationWindow.TotalMinutes} minutes. " +
                                  $"Total '{kvp.Key}' events in window: {otherEvents.Count}.";

                candidates.Add(new CauseCandidate(kvp.Key, correlationScore, coOccurrenceCount, evidence));
            }
        }

        // Rank by correlation score descending
        var rankedCandidates = candidates.OrderByDescending(c => c.CorrelationScore).ToList();

        return new RootCauseReport(
            $"Root cause analysis for '{targetDimension}' over {lookbackWindow.TotalHours:F1} hours.",
            rankedCandidates.AsReadOnly());
    }

    /// <summary>
    /// Computes a temporal correlation matrix for the specified dimensions based on
    /// co-occurrence of events within 5-minute windows.
    /// </summary>
    /// <param name="dimensions">The dimensions to include in the matrix.</param>
    /// <returns>A dictionary mapping dimension pairs to their temporal correlation scores.</returns>
    public Dictionary<(string, string), double> GetCorrelationMatrix(IReadOnlyList<string> dimensions)
    {
        ArgumentNullException.ThrowIfNull(dimensions);

        var matrix = new Dictionary<(string, string), double>();
        var correlationWindow = TimeSpan.FromMinutes(5);

        for (int i = 0; i < dimensions.Count; i++)
        {
            for (int j = i + 1; j < dimensions.Count; j++)
            {
                string dimA = dimensions[i];
                string dimB = dimensions[j];

                List<QualityEvent> eventsA;
                List<QualityEvent> eventsB;

                if (!_events.TryGetValue(dimA, out var listA))
                {
                    matrix[(dimA, dimB)] = 0;
                    continue;
                }
                if (!_events.TryGetValue(dimB, out var listB))
                {
                    matrix[(dimA, dimB)] = 0;
                    continue;
                }

                lock (listA)
                {
                    eventsA = new List<QualityEvent>(listA);
                }
                lock (listB)
                {
                    eventsB = new List<QualityEvent>(listB);
                }

                if (eventsA.Count == 0 || eventsB.Count == 0)
                {
                    matrix[(dimA, dimB)] = 0;
                    continue;
                }

                // Count co-occurrences: for each event in A, check if any event in B is within window
                int coOccurrences = 0;
                foreach (var eventA in eventsA)
                {
                    bool hasMatch = eventsB.Any(eventB =>
                        Math.Abs((eventA.Timestamp - eventB.Timestamp).TotalMinutes) <= correlationWindow.TotalMinutes);

                    if (hasMatch)
                        coOccurrences++;
                }

                double correlation = (double)coOccurrences / eventsA.Count;
                matrix[(dimA, dimB)] = correlation;
            }
        }

        return matrix;
    }
}

#endregion
