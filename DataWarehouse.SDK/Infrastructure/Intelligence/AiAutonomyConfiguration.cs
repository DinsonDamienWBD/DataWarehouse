using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;
using DataWarehouse.SDK.Infrastructure.Policy.Performance;

namespace DataWarehouse.SDK.Infrastructure.Intelligence;

/// <summary>
/// Provides per-feature per-policy-level AI autonomy configuration with 470 individually addressable
/// configuration points (94 features x 5 <see cref="PolicyLevel"/> values).
/// </summary>
/// <remarks>
/// Implements AIPI-08. Each configuration point is addressed by a composite key formatted as
/// <c>{featureId}:{PolicyLevel}</c> (e.g., "encryption:VDE", "compression:Block"). Unknown feature
/// IDs are allowed for forward compatibility but generate a warning via the returned boolean on set
/// operations. Thread-safe via <see cref="ConcurrentDictionary{TKey, TValue}"/>.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 77: AI Policy Intelligence (AIPI-08)")]
public sealed class AiAutonomyConfiguration
{
    private readonly ConcurrentDictionary<string, AiAutonomyLevel> _config = new(StringComparer.Ordinal);
    private readonly AiAutonomyLevel _defaultLevel;

    /// <summary>
    /// Total number of addressable configuration points (94 features x 5 policy levels).
    /// </summary>
    public int TotalConfigPoints => CheckClassificationTable.TotalFeatures * PolicyLevelValues.Length;

    /// <summary>
    /// Number of explicitly configured (non-default) configuration points.
    /// </summary>
    public int ConfiguredPointCount => _config.Count;

    /// <summary>
    /// Cached array of all <see cref="PolicyLevel"/> enum values for iteration.
    /// </summary>
    private static readonly PolicyLevel[] PolicyLevelValues = Enum.GetValues<PolicyLevel>();

    /// <summary>
    /// Creates a new AiAutonomyConfiguration with the specified default autonomy level.
    /// </summary>
    /// <param name="defaultLevel">
    /// The autonomy level returned for unconfigured points. Defaults to <see cref="AiAutonomyLevel.Suggest"/>.
    /// </param>
    public AiAutonomyConfiguration(AiAutonomyLevel defaultLevel = AiAutonomyLevel.Suggest)
    {
        _defaultLevel = defaultLevel;
    }

    /// <summary>
    /// Gets the configured autonomy level for a specific feature at a specific policy level.
    /// Returns the default level if the point has not been explicitly configured.
    /// </summary>
    /// <param name="featureId">The feature identifier (e.g., "encryption", "compression").</param>
    /// <param name="level">The policy level (Block, Chunk, Object, Container, VDE).</param>
    /// <returns>The configured <see cref="AiAutonomyLevel"/>, or the default if not explicitly set.</returns>
    public AiAutonomyLevel GetAutonomy(string featureId, PolicyLevel level)
    {
        if (featureId is null) throw new ArgumentNullException(nameof(featureId));

        string key = FormatKey(featureId, level);
        return _config.TryGetValue(key, out var autonomy) ? autonomy : _defaultLevel;
    }

    /// <summary>
    /// Sets the autonomy level for a specific feature at a specific policy level.
    /// </summary>
    /// <param name="featureId">The feature identifier.</param>
    /// <param name="level">The policy level.</param>
    /// <param name="autonomy">The autonomy level to configure.</param>
    public void SetAutonomy(string featureId, PolicyLevel level, AiAutonomyLevel autonomy)
    {
        if (featureId is null) throw new ArgumentNullException(nameof(featureId));

        string key = FormatKey(featureId, level);
        _config[key] = autonomy;
    }

    /// <summary>
    /// Sets the autonomy level for a feature across all 5 policy levels.
    /// </summary>
    /// <param name="featureId">The feature identifier.</param>
    /// <param name="autonomy">The autonomy level to apply at every policy level.</param>
    public void SetAutonomyForFeature(string featureId, AiAutonomyLevel autonomy)
    {
        if (featureId is null) throw new ArgumentNullException(nameof(featureId));

        for (int i = 0; i < PolicyLevelValues.Length; i++)
        {
            string key = FormatKey(featureId, PolicyLevelValues[i]);
            _config[key] = autonomy;
        }
    }

    /// <summary>
    /// Sets the autonomy level for all 94 features at a specific policy level.
    /// </summary>
    /// <param name="level">The policy level to configure.</param>
    /// <param name="autonomy">The autonomy level to apply to every feature at this level.</param>
    public void SetAutonomyForLevel(PolicyLevel level, AiAutonomyLevel autonomy)
    {
        // Iterate all known features from the classification table
        foreach (CheckTiming timing in Enum.GetValues<CheckTiming>())
        {
            var features = CheckClassificationTable.GetFeaturesByTiming(timing);
            for (int i = 0; i < features.Count; i++)
            {
                string key = FormatKey(features[i], level);
                _config[key] = autonomy;
            }
        }
    }

    /// <summary>
    /// Sets the autonomy level for all features in a specific <see cref="CheckTiming"/> category.
    /// Uses <see cref="CheckClassificationTable.GetFeaturesByTiming"/> to resolve feature membership.
    /// </summary>
    /// <param name="timing">The timing category (e.g., ConnectTime for security-critical features).</param>
    /// <param name="autonomy">The autonomy level to apply.</param>
    public void SetAutonomyForCategory(CheckTiming timing, AiAutonomyLevel autonomy)
    {
        var features = CheckClassificationTable.GetFeaturesByTiming(timing);

        for (int fi = 0; fi < features.Count; fi++)
        {
            for (int li = 0; li < PolicyLevelValues.Length; li++)
            {
                string key = FormatKey(features[fi], PolicyLevelValues[li]);
                _config[key] = autonomy;
            }
        }
    }

    // LOW-469: Cache the timing values array to avoid Enum.GetValues<CheckTiming>() allocation per call.
    private static readonly CheckTiming[] _allCheckTimings = Enum.GetValues<CheckTiming>();

    /// <summary>
    /// Checks whether a feature ID is recognized in the <see cref="CheckClassificationTable"/>.
    /// Unknown features are still allowed for forward compatibility.
    /// </summary>
    /// <param name="featureId">The feature identifier to check.</param>
    /// <returns>True if the feature is recognized; false if unknown (forward-compatible).</returns>
    public bool IsFeatureKnown(string featureId)
    {
        if (featureId is null) throw new ArgumentNullException(nameof(featureId));

        // LOW-469: Use pre-cached timing array; iterate O(T) timings × O(F/T) features per timing.
        foreach (CheckTiming timing in _allCheckTimings)
        {
            var features = CheckClassificationTable.GetFeaturesByTiming(timing);
            for (int i = 0; i < features.Count; i++)
            {
                if (string.Equals(features[i], featureId, StringComparison.Ordinal))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Exports all explicitly configured autonomy points as a dictionary.
    /// Keys are composite <c>{featureId}:{PolicyLevel}</c> strings.
    /// </summary>
    /// <returns>A snapshot of all configured points.</returns>
    public Dictionary<string, AiAutonomyLevel> ExportConfiguration()
    {
        var result = new Dictionary<string, AiAutonomyLevel>(StringComparer.Ordinal);
        foreach (var kvp in _config)
        {
            result[kvp.Key] = kvp.Value;
        }
        return result;
    }

    /// <summary>
    /// Bulk-imports autonomy configuration from a dictionary. Merges with existing configuration;
    /// imported values overwrite any existing values for the same keys.
    /// </summary>
    /// <param name="config">The configuration to import. Keys must be <c>{featureId}:{PolicyLevel}</c> format.</param>
    public void ImportConfiguration(IReadOnlyDictionary<string, AiAutonomyLevel> config)
    {
        if (config is null) throw new ArgumentNullException(nameof(config));

        foreach (var kvp in config)
        {
            _config[kvp.Key] = kvp.Value;
        }
    }

    /// <summary>
    /// Formats a composite key from feature ID and policy level.
    /// </summary>
    private static string FormatKey(string featureId, PolicyLevel level)
    {
        return $"{featureId}:{level}";
    }
}
