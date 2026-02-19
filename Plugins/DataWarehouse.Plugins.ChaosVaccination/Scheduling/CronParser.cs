using System;
using System.Collections.Generic;
using System.Linq;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.ChaosVaccination.Scheduling;

/// <summary>
/// Lightweight cron expression parser supporting standard 5-field cron format:
/// minute hour day-of-month month day-of-week.
///
/// Supported syntax:
/// - * (any value)
/// - */N (every N units)
/// - N-M (range)
/// - N,M,O (list)
/// - Combinations: N-M/S (range with step), N-M,O (range + list)
///
/// Examples:
/// - "0 */6 * * *" (every 6 hours at minute 0)
/// - "0 2 * * 1-5" (2:00 AM on weekdays)
/// - "*/30 * * * *" (every 30 minutes)
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 61: Vaccination scheduler")]
public static class CronParser
{
    private static readonly int[] MinuteRange = { 0, 59 };
    private static readonly int[] HourRange = { 0, 23 };
    private static readonly int[] DayOfMonthRange = { 1, 31 };
    private static readonly int[] MonthRange = { 1, 12 };
    private static readonly int[] DayOfWeekRange = { 0, 6 }; // 0 = Sunday

    /// <summary>
    /// Parses a standard 5-field cron expression into a <see cref="CronSchedule"/>.
    /// </summary>
    /// <param name="expression">The cron expression (minute hour day-of-month month day-of-week).</param>
    /// <returns>A parsed <see cref="CronSchedule"/> instance.</returns>
    /// <exception cref="FormatException">Thrown when the expression is invalid.</exception>
    public static CronSchedule Parse(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            throw new FormatException("Cron expression cannot be null or empty.");

        var fields = expression.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
            throw new FormatException(
                $"Cron expression must have exactly 5 fields (minute hour day-of-month month day-of-week), but got {fields.Length}: '{expression}'");

        var minutes = ParseField(fields[0], MinuteRange[0], MinuteRange[1], "minute");
        var hours = ParseField(fields[1], HourRange[0], HourRange[1], "hour");
        var daysOfMonth = ParseField(fields[2], DayOfMonthRange[0], DayOfMonthRange[1], "day-of-month");
        var months = ParseField(fields[3], MonthRange[0], MonthRange[1], "month");
        var daysOfWeek = ParseField(fields[4], DayOfWeekRange[0], DayOfWeekRange[1], "day-of-week");

        return new CronSchedule(expression, minutes, hours, daysOfMonth, months, daysOfWeek);
    }

    /// <summary>
    /// Parses a single cron field into a sorted set of allowed values.
    /// </summary>
    private static SortedSet<int> ParseField(string field, int min, int max, string fieldName)
    {
        var result = new SortedSet<int>();

        foreach (var part in field.Split(','))
        {
            var trimmed = part.Trim();
            if (string.IsNullOrEmpty(trimmed))
                throw new FormatException($"Empty element in {fieldName} field: '{field}'");

            // Handle step: */N or N-M/S
            int step = 1;
            var stepParts = trimmed.Split('/');
            if (stepParts.Length > 2)
                throw new FormatException($"Invalid step syntax in {fieldName} field: '{trimmed}'");

            if (stepParts.Length == 2)
            {
                if (!int.TryParse(stepParts[1], out step) || step <= 0)
                    throw new FormatException($"Invalid step value in {fieldName} field: '{stepParts[1]}'. Step must be a positive integer.");
                trimmed = stepParts[0];
            }

            if (trimmed == "*")
            {
                // Wildcard with optional step
                for (int i = min; i <= max; i += step)
                    result.Add(i);
            }
            else if (trimmed.Contains('-'))
            {
                // Range: N-M
                var rangeParts = trimmed.Split('-');
                if (rangeParts.Length != 2)
                    throw new FormatException($"Invalid range syntax in {fieldName} field: '{trimmed}'");

                if (!int.TryParse(rangeParts[0], out var rangeStart) || !int.TryParse(rangeParts[1], out var rangeEnd))
                    throw new FormatException($"Invalid range values in {fieldName} field: '{trimmed}'. Both start and end must be integers.");

                if (rangeStart < min || rangeStart > max)
                    throw new FormatException($"Range start {rangeStart} is out of bounds [{min}-{max}] in {fieldName} field.");
                if (rangeEnd < min || rangeEnd > max)
                    throw new FormatException($"Range end {rangeEnd} is out of bounds [{min}-{max}] in {fieldName} field.");
                if (rangeStart > rangeEnd)
                    throw new FormatException($"Range start {rangeStart} is greater than end {rangeEnd} in {fieldName} field.");

                for (int i = rangeStart; i <= rangeEnd; i += step)
                    result.Add(i);
            }
            else
            {
                // Single value
                if (!int.TryParse(trimmed, out var value))
                    throw new FormatException($"Invalid value '{trimmed}' in {fieldName} field. Expected an integer.");

                if (value < min || value > max)
                    throw new FormatException($"Value {value} is out of bounds [{min}-{max}] in {fieldName} field.");

                if (stepParts.Length == 2)
                {
                    // Single value with step: N/S means starting at N, every S
                    for (int i = value; i <= max; i += step)
                        result.Add(i);
                }
                else
                {
                    result.Add(value);
                }
            }
        }

        if (result.Count == 0)
            throw new FormatException($"No valid values produced for {fieldName} field: '{field}'");

        return result;
    }
}

/// <summary>
/// Represents a parsed cron schedule that can determine next occurrence times
/// and check if a given time matches the schedule.
/// </summary>
[SdkCompatibility("5.0.0", Notes = "Phase 61: Vaccination scheduler")]
public sealed class CronSchedule
{
    private readonly string _expression;
    private readonly SortedSet<int> _minutes;
    private readonly SortedSet<int> _hours;
    private readonly SortedSet<int> _daysOfMonth;
    private readonly SortedSet<int> _months;
    private readonly SortedSet<int> _daysOfWeek;

    internal CronSchedule(
        string expression,
        SortedSet<int> minutes,
        SortedSet<int> hours,
        SortedSet<int> daysOfMonth,
        SortedSet<int> months,
        SortedSet<int> daysOfWeek)
    {
        _expression = expression;
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = daysOfMonth;
        _months = months;
        _daysOfWeek = daysOfWeek;
    }

    /// <summary>
    /// Gets the original cron expression string.
    /// </summary>
    public string Expression => _expression;

    /// <summary>
    /// Checks whether the specified time matches this cron schedule (minute-level precision).
    /// </summary>
    /// <param name="time">The time to check.</param>
    /// <returns>True if the time matches the schedule.</returns>
    public bool Matches(DateTimeOffset time)
    {
        return _minutes.Contains(time.Minute)
            && _hours.Contains(time.Hour)
            && _daysOfMonth.Contains(time.Day)
            && _months.Contains(time.Month)
            && _daysOfWeek.Contains((int)time.DayOfWeek);
    }

    /// <summary>
    /// Computes the next occurrence of this schedule after the specified time.
    /// Searches up to 4 years ahead to handle leap year and complex schedule combinations.
    /// </summary>
    /// <param name="after">The time after which to find the next occurrence.</param>
    /// <returns>The next matching time, or null if no match found within 4 years.</returns>
    public DateTimeOffset? GetNextOccurrence(DateTimeOffset after)
    {
        // Start from the next minute boundary
        var candidate = new DateTimeOffset(
            after.Year, after.Month, after.Day,
            after.Hour, after.Minute, 0, after.Offset)
            .AddMinutes(1);

        // Search up to 4 years ahead (enough for any valid cron pattern)
        var maxSearch = after.AddYears(4);

        while (candidate <= maxSearch)
        {
            // Fast-forward month if not matching
            if (!_months.Contains(candidate.Month))
            {
                var nextMonth = GetNextValue(_months, candidate.Month);
                if (nextMonth == null || nextMonth.Value <= candidate.Month)
                {
                    // Wrap to next year
                    candidate = new DateTimeOffset(
                        candidate.Year + 1, _months.Min, 1, 0, 0, 0, candidate.Offset);
                }
                else
                {
                    candidate = new DateTimeOffset(
                        candidate.Year, nextMonth.Value, 1, 0, 0, 0, candidate.Offset);
                }
                continue;
            }

            // Fast-forward day if not matching day-of-month or day-of-week
            if (!_daysOfMonth.Contains(candidate.Day) || !_daysOfWeek.Contains((int)candidate.DayOfWeek))
            {
                candidate = candidate.AddDays(1);
                candidate = new DateTimeOffset(
                    candidate.Year, candidate.Month, candidate.Day, 0, 0, 0, candidate.Offset);
                continue;
            }

            // Fast-forward hour if not matching
            if (!_hours.Contains(candidate.Hour))
            {
                var nextHour = GetNextValue(_hours, candidate.Hour);
                if (nextHour == null || nextHour.Value <= candidate.Hour)
                {
                    // Wrap to next day
                    candidate = candidate.AddDays(1);
                    candidate = new DateTimeOffset(
                        candidate.Year, candidate.Month, candidate.Day, _hours.Min, 0, 0, candidate.Offset);
                }
                else
                {
                    candidate = new DateTimeOffset(
                        candidate.Year, candidate.Month, candidate.Day,
                        nextHour.Value, 0, 0, candidate.Offset);
                }
                continue;
            }

            // Fast-forward minute if not matching
            if (!_minutes.Contains(candidate.Minute))
            {
                var nextMinute = GetNextValue(_minutes, candidate.Minute);
                if (nextMinute == null || nextMinute.Value <= candidate.Minute)
                {
                    // Wrap to next hour
                    candidate = candidate.AddHours(1);
                    candidate = new DateTimeOffset(
                        candidate.Year, candidate.Month, candidate.Day,
                        candidate.Hour, 0, 0, candidate.Offset);
                }
                else
                {
                    candidate = new DateTimeOffset(
                        candidate.Year, candidate.Month, candidate.Day,
                        candidate.Hour, nextMinute.Value, 0, candidate.Offset);
                }
                continue;
            }

            // All fields match
            return candidate;
        }

        return null; // No match within 4 years
    }

    /// <summary>
    /// Gets the next value in the sorted set that is greater than the current value.
    /// Returns null if no greater value exists.
    /// </summary>
    private static int? GetNextValue(SortedSet<int> values, int current)
    {
        foreach (var v in values)
        {
            if (v > current)
                return v;
        }
        return null;
    }

    /// <inheritdoc/>
    public override string ToString() => _expression;
}
