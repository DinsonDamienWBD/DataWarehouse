using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateAccessControl.Features
{
    /// <summary>
    /// User behavioral analysis with baseline profiling and deviation scoring.
    /// Tracks user behavior patterns and detects deviations from established baselines.
    /// </summary>
    public sealed class BehavioralAnalysis
    {
        private readonly BoundedDictionary<string, UserProfile> _profiles = new BoundedDictionary<string, UserProfile>(1000);
        private readonly ConcurrentQueue<BehaviorAlert> _alerts = new();
        private readonly double _deviationThreshold = 2.5; // Standard deviations
        private readonly int _baselineMinimumSamples = 20;
        private readonly int _maxAlertHistorySize = 1000;

        /// <summary>
        /// Analyzes user behavior and detects deviations from baseline.
        /// </summary>
        /// <param name="userId">User identifier.</param>
        /// <param name="behavior">Current behavior snapshot.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Behavioral analysis result with deviation score.</returns>
        public Task<BehaviorAnalysisResult> AnalyzeBehaviorAsync(
            string userId,
            UserBehavior behavior,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException("User ID cannot be null or empty", nameof(userId));

            if (behavior == null)
                throw new ArgumentNullException(nameof(behavior));

            // Get or create profile
            var profile = _profiles.GetOrAdd(userId, _ => new UserProfile
            {
                UserId = userId,
                CreatedAt = DateTime.UtcNow,
                BaselineStatus = BaselineStatus.Building
            });

            // Calculate deviations
            var deviations = CalculateDeviations(behavior, profile);
            var maxDeviation = deviations.Any() ? deviations.Max(d => d.DeviationScore) : 0.0;
            var anomalyScore = CalculateAnomalyScore(deviations);
            var isAnomalous = maxDeviation > _deviationThreshold && profile.BaselineStatus == BaselineStatus.Established;

            // Update profile with current behavior
            UpdateProfile(profile, behavior);

            // Update baseline status
            if (profile.BehaviorSamples.Count >= _baselineMinimumSamples &&
                profile.BaselineStatus == BaselineStatus.Building)
            {
                profile.BaselineStatus = BaselineStatus.Established;
            }

            // Generate alert if anomalous
            if (isAnomalous)
            {
                var alert = new BehaviorAlert
                {
                    Id = Guid.NewGuid().ToString("N"),
                    UserId = userId,
                    Timestamp = DateTime.UtcNow,
                    AnomalyScore = anomalyScore,
                    Deviations = deviations,
                    Severity = anomalyScore >= 80 ? AlertSeverity.High :
                               anomalyScore >= 60 ? AlertSeverity.Medium : AlertSeverity.Low
                };

                _alerts.Enqueue(alert);

                // Prune old alerts
                while (_alerts.Count > _maxAlertHistorySize)
                {
                    _alerts.TryDequeue(out _);
                }
            }

            return Task.FromResult(new BehaviorAnalysisResult
            {
                UserId = userId,
                IsAnomalous = isAnomalous,
                AnomalyScore = anomalyScore,
                MaxDeviation = maxDeviation,
                Deviations = deviations,
                BaselineStatus = profile.BaselineStatus,
                SampleCount = profile.BehaviorSamples.Count,
                AnalyzedAt = DateTime.UtcNow
            });
        }

        private List<BehaviorDeviation> CalculateDeviations(UserBehavior behavior, UserProfile profile)
        {
            var deviations = new List<BehaviorDeviation>();

            if (profile.BehaviorSamples.Count < 5)
                return deviations; // Not enough data for baseline

            // Login time deviation
            var loginTimeDeviation = CalculateTimeDeviation(behavior.LoginTime, profile.LoginTimes);
            if (Math.Abs(loginTimeDeviation) > _deviationThreshold)
            {
                var avgHour = profile.LoginTimes.Select(t => t.Hour * 60 + t.Minute).Average() / 60.0;
                deviations.Add(new BehaviorDeviation
                {
                    BehaviorType = "LoginTime",
                    DeviationScore = Math.Abs(loginTimeDeviation),
                    Expected = $"{avgHour:F1}h",
                    Actual = $"{behavior.LoginTime.Hour}.{behavior.LoginTime.Minute:D2}h",
                    Description = "Unusual login time"
                });
            }

            // Session duration deviation
            if (behavior.SessionDurationMinutes > 0)
            {
                var durationDeviation = CalculateDeviation(behavior.SessionDurationMinutes, profile.SessionDurations);
                if (Math.Abs(durationDeviation) > _deviationThreshold)
                {
                    deviations.Add(new BehaviorDeviation
                    {
                        BehaviorType = "SessionDuration",
                        DeviationScore = Math.Abs(durationDeviation),
                        Expected = $"{profile.SessionDurations.Average():F0}min",
                        Actual = $"{behavior.SessionDurationMinutes}min",
                        Description = "Unusual session duration"
                    });
                }
            }

            // Actions per hour deviation
            var actionsDeviation = CalculateDeviation(behavior.ActionsPerHour, profile.ActionsPerHour);
            if (Math.Abs(actionsDeviation) > _deviationThreshold)
            {
                deviations.Add(new BehaviorDeviation
                {
                    BehaviorType = "ActionsPerHour",
                    DeviationScore = Math.Abs(actionsDeviation),
                    Expected = $"{profile.ActionsPerHour.Average():F1}/h",
                    Actual = $"{behavior.ActionsPerHour}/h",
                    Description = "Unusual activity level"
                });
            }

            // Location deviation
            if (!string.IsNullOrEmpty(behavior.Location))
            {
                var locationFrequency = (double)profile.Locations.Count(l => l == behavior.Location) / profile.Locations.Count;
                if (locationFrequency < 0.1) // Less than 10% of historical sessions
                {
                    deviations.Add(new BehaviorDeviation
                    {
                        BehaviorType = "Location",
                        DeviationScore = (1.0 - locationFrequency) * 5.0, // Scale to deviation units
                        Expected = string.Join(", ", profile.Locations.GroupBy(l => l).OrderByDescending(g => g.Count()).Take(3).Select(g => g.Key)),
                        Actual = behavior.Location,
                        Description = "Unusual or new location"
                    });
                }
            }

            // Failed login attempts deviation
            if (behavior.FailedLoginAttempts > 0)
            {
                var failedAttemptsDeviation = CalculateDeviation(behavior.FailedLoginAttempts, profile.FailedLoginAttempts);
                if (Math.Abs(failedAttemptsDeviation) > _deviationThreshold)
                {
                    deviations.Add(new BehaviorDeviation
                    {
                        BehaviorType = "FailedLogins",
                        DeviationScore = Math.Abs(failedAttemptsDeviation),
                        Expected = $"{profile.FailedLoginAttempts.Average():F1}",
                        Actual = $"{behavior.FailedLoginAttempts}",
                        Description = "Unusual number of failed login attempts"
                    });
                }
            }

            return deviations;
        }

        private double CalculateDeviation(double value, List<double> history)
        {
            if (history.Count < 2)
                return 0.0;

            var mean = history.Average();
            var variance = history.Select(x => Math.Pow(x - mean, 2)).Average();
            var stdDev = Math.Sqrt(variance);

            if (stdDev < 0.01)
                return 0.0;

            return (value - mean) / stdDev;
        }

        private double CalculateTimeDeviation(DateTime loginTime, List<DateTime> history)
        {
            if (history.Count < 2)
                return 0.0;

            // Convert to minutes since midnight
            var currentMinutes = loginTime.Hour * 60 + loginTime.Minute;
            var historicalMinutes = history.Select(t => t.Hour * 60 + t.Minute).ToList();

            return CalculateDeviation(currentMinutes, historicalMinutes.Select(m => (double)m).ToList());
        }

        private double CalculateAnomalyScore(List<BehaviorDeviation> deviations)
        {
            if (!deviations.Any())
                return 0.0;

            // Weight by deviation score and count
            var totalDeviation = deviations.Sum(d => d.DeviationScore);
            var countBonus = Math.Min(deviations.Count * 10.0, 30.0);

            return Math.Min((totalDeviation * 15.0) + countBonus, 100.0);
        }

        private void UpdateProfile(UserProfile profile, UserBehavior behavior)
        {
            profile.LastActivityAt = DateTime.UtcNow;
            profile.BehaviorSamples.Add(behavior.Timestamp);

            // Update login times (sliding window)
            profile.LoginTimes.Add(behavior.LoginTime);
            if (profile.LoginTimes.Count > 100)
                profile.LoginTimes.RemoveAt(0);

            // Update session durations
            if (behavior.SessionDurationMinutes > 0)
            {
                profile.SessionDurations.Add(behavior.SessionDurationMinutes);
                if (profile.SessionDurations.Count > 100)
                    profile.SessionDurations.RemoveAt(0);
            }

            // Update actions per hour
            profile.ActionsPerHour.Add(behavior.ActionsPerHour);
            if (profile.ActionsPerHour.Count > 100)
                profile.ActionsPerHour.RemoveAt(0);

            // Update locations
            if (!string.IsNullOrEmpty(behavior.Location))
            {
                profile.Locations.Add(behavior.Location);
                if (profile.Locations.Count > 50)
                    profile.Locations.RemoveAt(0);
            }

            // Update failed login attempts
            profile.FailedLoginAttempts.Add(behavior.FailedLoginAttempts);
            if (profile.FailedLoginAttempts.Count > 100)
                profile.FailedLoginAttempts.RemoveAt(0);
        }

        /// <summary>
        /// Gets user profile.
        /// </summary>
        public UserProfile? GetProfile(string userId)
        {
            return _profiles.TryGetValue(userId, out var profile) ? profile : null;
        }

        /// <summary>
        /// Gets recent behavior alerts.
        /// </summary>
        public IReadOnlyCollection<BehaviorAlert> GetRecentAlerts(int maxCount = 100)
        {
            return _alerts.Take(maxCount).ToList().AsReadOnly();
        }

        /// <summary>
        /// Clears user profile.
        /// </summary>
        public bool ClearProfile(string userId)
        {
            return _profiles.TryRemove(userId, out _);
        }
    }

    #region Supporting Types

    /// <summary>
    /// User behavior snapshot.
    /// </summary>
    public sealed class UserBehavior
    {
        public required DateTime Timestamp { get; init; }
        public required DateTime LoginTime { get; init; }
        public required int SessionDurationMinutes { get; init; }
        public required double ActionsPerHour { get; init; }
        public string? Location { get; init; }
        public required int FailedLoginAttempts { get; init; }
    }

    /// <summary>
    /// User behavioral profile.
    /// </summary>
    public sealed class UserProfile
    {
        public required string UserId { get; init; }
        public required DateTime CreatedAt { get; init; }
        public DateTime? LastActivityAt { get; set; }
        public required BaselineStatus BaselineStatus { get; set; }
        public List<DateTime> BehaviorSamples { get; init; } = new();
        public List<DateTime> LoginTimes { get; init; } = new();
        public List<double> SessionDurations { get; init; } = new();
        public List<double> ActionsPerHour { get; init; } = new();
        public List<string> Locations { get; init; } = new();
        public List<double> FailedLoginAttempts { get; init; } = new();
    }

    /// <summary>
    /// Baseline establishment status.
    /// </summary>
    public enum BaselineStatus
    {
        Building,
        Established,
        Updating
    }

    /// <summary>
    /// Behavior deviation.
    /// </summary>
    public sealed class BehaviorDeviation
    {
        public required string BehaviorType { get; init; }
        public required double DeviationScore { get; init; }
        public required string Expected { get; init; }
        public required string Actual { get; init; }
        public required string Description { get; init; }
    }

    /// <summary>
    /// Behavioral analysis result.
    /// </summary>
    public sealed class BehaviorAnalysisResult
    {
        public required string UserId { get; init; }
        public required bool IsAnomalous { get; init; }
        public required double AnomalyScore { get; init; }
        public required double MaxDeviation { get; init; }
        public required List<BehaviorDeviation> Deviations { get; init; }
        public required BaselineStatus BaselineStatus { get; init; }
        public required int SampleCount { get; init; }
        public required DateTime AnalyzedAt { get; init; }
    }

    /// <summary>
    /// Behavior alert.
    /// </summary>
    public sealed class BehaviorAlert
    {
        public required string Id { get; init; }
        public required string UserId { get; init; }
        public required DateTime Timestamp { get; init; }
        public required double AnomalyScore { get; init; }
        public required List<BehaviorDeviation> Deviations { get; init; }
        public required AlertSeverity Severity { get; init; }
    }

    /// <summary>
    /// Alert severity.
    /// </summary>
    public enum AlertSeverity
    {
        Low,
        Medium,
        High,
        Critical
    }

    #endregion
}
