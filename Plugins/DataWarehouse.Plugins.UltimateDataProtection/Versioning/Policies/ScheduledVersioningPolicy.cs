namespace DataWarehouse.Plugins.UltimateDataProtection.Versioning.Policies
{
    /// <summary>
    /// Scheduled versioning policy - versions are created at configured time intervals.
    /// Supports hourly, daily, weekly, and custom schedule expressions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This policy is suitable for:
    /// </para>
    /// <list type="bullet">
    ///   <item>Regular backup schedules</item>
    ///   <item>Compliance requirements with fixed intervals</item>
    ///   <item>Predictable storage growth patterns</item>
    ///   <item>Off-hours versioning to minimize impact</item>
    /// </list>
    /// <para>
    /// Schedule expressions support:
    /// </para>
    /// <list type="bullet">
    ///   <item>"hourly" - Every hour</item>
    ///   <item>"daily" - Once per day at midnight</item>
    ///   <item>"daily@HH:MM" - Daily at specific time</item>
    ///   <item>"weekly" - Every Sunday at midnight</item>
    ///   <item>"weekly@DDD" - Every week on specified day (MON, TUE, etc.)</item>
    ///   <item>"monthly" - First day of each month</item>
    ///   <item>Cron expressions for advanced scheduling</item>
    /// </list>
    /// </remarks>
    public sealed class ScheduledVersioningPolicy : VersioningPolicyBase
    {
        private DateTimeOffset _lastScheduledVersion = DateTimeOffset.MinValue;

        /// <inheritdoc/>
        public override string PolicyId => "scheduled-versioning";

        /// <inheritdoc/>
        public override string PolicyName => "Scheduled Versioning";

        /// <inheritdoc/>
        public override string Description =>
            "Versions are created automatically at configured intervals. " +
            "Supports hourly, daily, weekly, and custom cron schedules.";

        /// <inheritdoc/>
        public override VersioningMode Mode => VersioningMode.Scheduled;

        /// <summary>
        /// Determines if a version should be created based on the schedule.
        /// </summary>
        /// <param name="context">Version context.</param>
        /// <param name="ct">Cancellation token.</param>
        /// <returns>True if schedule indicates version should be created.</returns>
        public override Task<bool> ShouldCreateVersionAsync(VersionContext context, CancellationToken ct = default)
        {
            // Check if we're due for a scheduled version
            var now = DateTimeOffset.UtcNow;
            var scheduleExpression = Settings.ScheduleExpression ?? "daily";

            var nextScheduledTime = CalculateNextScheduledTime(_lastScheduledVersion, scheduleExpression);

            if (now >= nextScheduledTime)
            {
                // Check if change is significant enough (even for scheduled versions)
                if (context.ChangeSize == 0)
                {
                    // No changes since last version - skip this scheduled run
                    return Task.FromResult(false);
                }

                // Update last scheduled time
                _lastScheduledVersion = now;
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }

        /// <summary>
        /// Calculates the next scheduled time based on the schedule expression.
        /// </summary>
        /// <param name="lastRun">Last scheduled run time.</param>
        /// <param name="expression">Schedule expression.</param>
        /// <returns>Next scheduled time.</returns>
        private static DateTimeOffset CalculateNextScheduledTime(DateTimeOffset lastRun, string expression)
        {
            var now = DateTimeOffset.UtcNow;

            // Handle special cases for first run
            if (lastRun == DateTimeOffset.MinValue)
            {
                // First run - return a time in the past to trigger immediately
                return now.AddSeconds(-1);
            }

            var normalized = expression.ToLowerInvariant().Trim();

            // Parse schedule expressions
            if (normalized == "hourly")
            {
                return lastRun.AddHours(1);
            }

            if (normalized == "daily" || normalized.StartsWith("daily@"))
            {
                var targetTime = TimeSpan.Zero; // Default: midnight
                if (normalized.StartsWith("daily@"))
                {
                    var timePart = normalized.Substring(6);
                    if (TimeSpan.TryParse(timePart, out var parsed))
                    {
                        targetTime = parsed;
                    }
                }

                var nextDay = lastRun.Date.AddDays(1).Add(targetTime);
                return new DateTimeOffset(nextDay, TimeSpan.Zero);
            }

            if (normalized == "weekly" || normalized.StartsWith("weekly@"))
            {
                var targetDay = DayOfWeek.Sunday; // Default
                if (normalized.StartsWith("weekly@"))
                {
                    var dayPart = normalized.Substring(7).ToUpperInvariant();
                    targetDay = dayPart switch
                    {
                        "MON" => DayOfWeek.Monday,
                        "TUE" => DayOfWeek.Tuesday,
                        "WED" => DayOfWeek.Wednesday,
                        "THU" => DayOfWeek.Thursday,
                        "FRI" => DayOfWeek.Friday,
                        "SAT" => DayOfWeek.Saturday,
                        "SUN" => DayOfWeek.Sunday,
                        _ => DayOfWeek.Sunday
                    };
                }

                var daysUntilNext = ((int)targetDay - (int)lastRun.DayOfWeek + 7) % 7;
                if (daysUntilNext == 0) daysUntilNext = 7; // Next week if same day
                return lastRun.Date.AddDays(daysUntilNext);
            }

            if (normalized == "monthly")
            {
                var nextMonth = new DateTime(lastRun.Year, lastRun.Month, 1).AddMonths(1);
                return new DateTimeOffset(nextMonth, TimeSpan.Zero);
            }

            // For intervals like "4h", "30m", "12h"
            if (TryParseInterval(normalized, out var interval))
            {
                return lastRun.Add(interval);
            }

            // Default: daily
            return lastRun.AddDays(1);
        }

        /// <summary>
        /// Tries to parse an interval expression.
        /// </summary>
        private static bool TryParseInterval(string expression, out TimeSpan interval)
        {
            interval = TimeSpan.Zero;

            if (string.IsNullOrEmpty(expression) || expression.Length < 2)
                return false;

            var unit = expression[^1];
            var valuePart = expression[..^1];

            if (!int.TryParse(valuePart, out var value))
                return false;

            interval = unit switch
            {
                'm' => TimeSpan.FromMinutes(value),
                'h' => TimeSpan.FromHours(value),
                'd' => TimeSpan.FromDays(value),
                'w' => TimeSpan.FromDays(value * 7),
                _ => TimeSpan.Zero
            };

            return interval > TimeSpan.Zero;
        }

        /// <inheritdoc/>
        public override TimeSpan? GetMinimumInterval()
        {
            // Return the schedule interval as minimum
            var expression = Settings.ScheduleExpression ?? "daily";

            if (TryParseInterval(expression.ToLowerInvariant(), out var interval))
                return interval;

            return expression.ToLowerInvariant() switch
            {
                "hourly" => TimeSpan.FromHours(1),
                "daily" => TimeSpan.FromDays(1),
                "weekly" => TimeSpan.FromDays(7),
                "monthly" => TimeSpan.FromDays(30),
                _ => TimeSpan.FromDays(1)
            };
        }

        /// <summary>
        /// Gets the schedule description.
        /// </summary>
        public string GetScheduleDescription()
        {
            var expression = Settings.ScheduleExpression ?? "daily";
            return expression.ToLowerInvariant() switch
            {
                "hourly" => "Every hour",
                "daily" => "Every day at midnight UTC",
                "weekly" => "Every Sunday at midnight UTC",
                "monthly" => "First day of each month at midnight UTC",
                _ when expression.StartsWith("daily@") => $"Every day at {expression.Substring(6)} UTC",
                _ when expression.StartsWith("weekly@") => $"Every {expression.Substring(7)} at midnight UTC",
                _ when TryParseInterval(expression, out var interval) => $"Every {interval.TotalHours} hours",
                _ => $"Custom schedule: {expression}"
            };
        }
    }
}
