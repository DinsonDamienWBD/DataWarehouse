using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Infrastructure.Intelligence;

/// <summary>
/// Data classification levels for sensitivity assessment.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 77: AI Policy Intelligence (AIPI-06)")]
public enum DataClassification
{
    /// <summary>Data intended for public consumption.</summary>
    Public = 0,

    /// <summary>Internal-use data not intended for external distribution.</summary>
    Internal = 1,

    /// <summary>Confidential data requiring access controls.</summary>
    Confidential = 2,

    /// <summary>Restricted data with strict access and handling requirements.</summary>
    Restricted = 3,

    /// <summary>Top secret data requiring maximum security controls.</summary>
    TopSecret = 4
}

/// <summary>
/// A sensitivity signal detected from observation data analysis.
/// </summary>
/// <param name="SignalType">Type of sensitivity signal (e.g., "pii_email", "classification_restricted").</param>
/// <param name="Confidence">Confidence in this signal, 0.0 to 1.0.</param>
/// <param name="PluginId">The plugin whose observations triggered this signal.</param>
/// <param name="DetectedAt">When this signal was detected.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 77: AI Policy Intelligence (AIPI-06)")]
public sealed record SensitivitySignal(
    string SignalType,
    double Confidence,
    string PluginId,
    DateTimeOffset DetectedAt
);

/// <summary>
/// Composite sensitivity profile computed from active signals.
/// </summary>
/// <param name="HighestClassification">The most sensitive data classification detected.</param>
/// <param name="PiiDetected">Whether any PII signals are currently active.</param>
/// <param name="ActiveSignals">Currently active sensitivity signals.</param>
/// <param name="SensitivityScore">Overall sensitivity score from 0.0 (safe) to 1.0 (top secret).</param>
/// <param name="AssessedAt">When this profile was computed.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 77: AI Policy Intelligence (AIPI-06)")]
public sealed record SensitivityProfile(
    DataClassification HighestClassification,
    bool PiiDetected,
    List<SensitivitySignal> ActiveSignals,
    double SensitivityScore,
    DateTimeOffset AssessedAt
);

/// <summary>
/// Detects PII patterns and data classification signals from observation events.
/// Monitors metric patterns for PII indicators (email, SSN, phone, HIPAA PHI, GDPR personal data)
/// and classification-level signals to produce a real-time sensitivity profile.
/// </summary>
/// <remarks>
/// Implements AIPI-06. Monitors observations matching:
/// <list type="bullet">
///   <item><description>pii_* metrics: PII detection signals from plugin observations</description></item>
///   <item><description>classification_* metrics: data classification level indicators</description></item>
///   <item><description>sensitivity_* metrics: general sensitivity indicators</description></item>
///   <item><description>AnomalyType containing "pii": anomaly-based PII detection</description></item>
/// </list>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 77: AI Policy Intelligence (AIPI-06)")]
public sealed class DataSensitivityAnalyzer : IAiAdvisor
{
    /// <summary>
    /// Sliding window for signal retention.
    /// </summary>
    private static readonly TimeSpan SignalWindow = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Known PII signal types and their base confidence levels.
    /// </summary>
    private static readonly Dictionary<string, double> PiiSignalTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["pii_email"] = 0.8,
        ["pii_ssn"] = 0.95,
        ["pii_phone"] = 0.7,
        ["hipaa_phi"] = 0.9,
        ["gdpr_personal"] = 0.85
    };

    private readonly ConcurrentQueue<(SensitivitySignal Signal, DateTimeOffset ExpiresAt)> _signalWindow = new();

    private volatile SensitivityProfile _currentProfile;

    /// <summary>
    /// Creates a new DataSensitivityAnalyzer.
    /// </summary>
    public DataSensitivityAnalyzer()
    {
        _currentProfile = new SensitivityProfile(
            DataClassification.Public,
            false,
            new List<SensitivitySignal>(),
            0.0,
            DateTimeOffset.UtcNow);
    }

    /// <inheritdoc />
    public string AdvisorId => "data_sensitivity_analyzer";

    /// <summary>
    /// The current sensitivity profile. Updated atomically after each observation batch.
    /// </summary>
    public SensitivityProfile CurrentProfile => _currentProfile;

    /// <summary>
    /// True if the highest detected classification requires encryption (Confidential or above).
    /// </summary>
    public bool RequiresEncryption => _currentProfile.HighestClassification >= DataClassification.Confidential;

    /// <summary>
    /// True if PII has been detected or classification is Restricted or above, requiring audit trails.
    /// </summary>
    public bool RequiresAuditTrail => _currentProfile.PiiDetected ||
                                       _currentProfile.HighestClassification >= DataClassification.Restricted;

    /// <inheritdoc />
    public Task ProcessObservationsAsync(IReadOnlyList<ObservationEvent> batch, CancellationToken ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset expiresAt = now + SignalWindow;

        for (int i = 0; i < batch.Count; i++)
        {
            var obs = batch[i];

            // PII signals: metrics starting with "pii_" or named "pii_detected"
            if (obs.MetricName.StartsWith("pii_", StringComparison.OrdinalIgnoreCase) ||
                obs.MetricName.Equals("pii_detected", StringComparison.OrdinalIgnoreCase))
            {
                string signalType = DeterminePiiSignalType(obs.MetricName);
                double confidence = PiiSignalTypes.TryGetValue(signalType, out var baseConf)
                    ? baseConf
                    : 0.7; // Default PII confidence

                _signalWindow.Enqueue((new SensitivitySignal(signalType, confidence, obs.PluginId, now), expiresAt));
            }

            // Anomaly-based PII detection
            if (obs.AnomalyType is not null &&
                obs.AnomalyType.Contains("pii", StringComparison.OrdinalIgnoreCase))
            {
                _signalWindow.Enqueue((new SensitivitySignal(
                    "pii_anomaly",
                    0.75,
                    obs.PluginId,
                    now), expiresAt));
            }

            // Classification signals: metrics starting with "classification_" or "data_classification"
            if (obs.MetricName.StartsWith("classification_", StringComparison.OrdinalIgnoreCase) ||
                obs.MetricName.Equals("data_classification", StringComparison.OrdinalIgnoreCase))
            {
                DataClassification classification = MapValueToClassification(obs.Value);
                string signalType = $"classification_{classification.ToString().ToLowerInvariant()}";
                double confidence = MapClassificationToConfidence(classification);

                _signalWindow.Enqueue((new SensitivitySignal(signalType, confidence, obs.PluginId, now), expiresAt));
            }

            // General sensitivity signals
            if (obs.MetricName.StartsWith("sensitivity_", StringComparison.OrdinalIgnoreCase))
            {
                double confidence = Math.Clamp(obs.Value / 100.0, 0.0, 1.0);
                _signalWindow.Enqueue((new SensitivitySignal(
                    obs.MetricName.ToLowerInvariant(),
                    confidence,
                    obs.PluginId,
                    now), expiresAt));
            }
        }

        // Evict expired signals
        while (_signalWindow.TryPeek(out var oldest) && oldest.ExpiresAt < now)
        {
            _signalWindow.TryDequeue(out _);
        }

        // Rebuild profile from active signals
        RebuildProfile(now);

        return Task.CompletedTask;
    }

    private void RebuildProfile(DateTimeOffset now)
    {
        var activeSignals = new List<SensitivitySignal>();
        bool piiDetected = false;
        DataClassification highest = DataClassification.Public;
        double maxConfidence = 0.0;

        foreach (var (signal, _) in _signalWindow)
        {
            activeSignals.Add(signal);

            if (signal.Confidence > maxConfidence)
                maxConfidence = signal.Confidence;

            // Check for PII signals
            if (signal.SignalType.StartsWith("pii_", StringComparison.OrdinalIgnoreCase))
            {
                piiDetected = true;
            }

            // Check for classification signals
            if (signal.SignalType.StartsWith("classification_", StringComparison.OrdinalIgnoreCase))
            {
                DataClassification signalClassification = ParseClassificationFromSignal(signal.SignalType);
                if (signalClassification > highest)
                    highest = signalClassification;
            }
        }

        // PII presence implies at least Confidential
        if (piiDetected && highest < DataClassification.Confidential)
        {
            highest = DataClassification.Confidential;
        }

        _currentProfile = new SensitivityProfile(
            highest,
            piiDetected,
            activeSignals,
            maxConfidence,
            now);
    }

    /// <summary>
    /// Determines the specific PII signal type from the metric name.
    /// </summary>
    private static string DeterminePiiSignalType(string metricName)
    {
        if (metricName.Contains("email", StringComparison.OrdinalIgnoreCase))
            return "pii_email";
        if (metricName.Contains("ssn", StringComparison.OrdinalIgnoreCase))
            return "pii_ssn";
        if (metricName.Contains("phone", StringComparison.OrdinalIgnoreCase))
            return "pii_phone";
        if (metricName.Contains("hipaa", StringComparison.OrdinalIgnoreCase) ||
            metricName.Contains("phi", StringComparison.OrdinalIgnoreCase))
            return "hipaa_phi";
        if (metricName.Contains("gdpr", StringComparison.OrdinalIgnoreCase) ||
            metricName.Contains("personal", StringComparison.OrdinalIgnoreCase))
            return "gdpr_personal";

        // Generic PII detected
        return "pii_detected";
    }

    /// <summary>
    /// Maps a numeric observation value to a DataClassification enum value.
    /// </summary>
    private static DataClassification MapValueToClassification(double value)
    {
        int level = (int)Math.Round(value);
        return level switch
        {
            <= 0 => DataClassification.Public,
            1 => DataClassification.Internal,
            2 => DataClassification.Confidential,
            3 => DataClassification.Restricted,
            >= 4 => DataClassification.TopSecret
        };
    }

    /// <summary>
    /// Maps a classification level to a confidence score for signal creation.
    /// </summary>
    private static double MapClassificationToConfidence(DataClassification classification) => classification switch
    {
        DataClassification.Public => 0.3,
        DataClassification.Internal => 0.5,
        DataClassification.Confidential => 0.7,
        DataClassification.Restricted => 0.85,
        DataClassification.TopSecret => 0.95,
        _ => 0.5
    };

    /// <summary>
    /// Parses a DataClassification from a signal type like "classification_restricted".
    /// </summary>
    private static DataClassification ParseClassificationFromSignal(string signalType)
    {
        // Remove "classification_" prefix
        string suffix = signalType.Length > "classification_".Length
            ? signalType["classification_".Length..]
            : string.Empty;

        return suffix.ToLowerInvariant() switch
        {
            "public" => DataClassification.Public,
            "internal" => DataClassification.Internal,
            "confidential" => DataClassification.Confidential,
            "restricted" => DataClassification.Restricted,
            "topsecret" => DataClassification.TopSecret,
            _ => DataClassification.Public
        };
    }
}
