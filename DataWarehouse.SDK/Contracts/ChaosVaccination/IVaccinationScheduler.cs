using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.ChaosVaccination
{
    /// <summary>
    /// Contract for scheduling recurring chaos vaccination experiments.
    /// Supports cron-based and interval-based scheduling with time window constraints.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination types")]
    public interface IVaccinationScheduler
    {
        /// <summary>
        /// Adds a new vaccination schedule.
        /// </summary>
        /// <param name="schedule">The schedule to add.</param>
        /// <param name="ct">Cancellation token for the operation.</param>
        Task AddScheduleAsync(VaccinationSchedule schedule, CancellationToken ct = default);

        /// <summary>
        /// Removes a vaccination schedule by its ID.
        /// </summary>
        /// <param name="scheduleId">The ID of the schedule to remove.</param>
        /// <param name="ct">Cancellation token for the operation.</param>
        Task RemoveScheduleAsync(string scheduleId, CancellationToken ct = default);

        /// <summary>
        /// Gets all configured vaccination schedules.
        /// </summary>
        /// <param name="ct">Cancellation token for the operation.</param>
        /// <returns>All vaccination schedules.</returns>
        Task<IReadOnlyList<VaccinationSchedule>> GetSchedulesAsync(CancellationToken ct = default);

        /// <summary>
        /// Gets a specific vaccination schedule by its ID.
        /// </summary>
        /// <param name="scheduleId">The ID of the schedule to retrieve.</param>
        /// <param name="ct">Cancellation token for the operation.</param>
        /// <returns>The vaccination schedule, or null if not found.</returns>
        Task<VaccinationSchedule?> GetScheduleAsync(string scheduleId, CancellationToken ct = default);

        /// <summary>
        /// Enables or disables a vaccination schedule.
        /// </summary>
        /// <param name="scheduleId">The ID of the schedule to update.</param>
        /// <param name="enabled">Whether the schedule should be enabled.</param>
        /// <param name="ct">Cancellation token for the operation.</param>
        Task EnableScheduleAsync(string scheduleId, bool enabled, CancellationToken ct = default);

        /// <summary>
        /// Gets the next scheduled run time for a vaccination schedule.
        /// </summary>
        /// <param name="scheduleId">The ID of the schedule.</param>
        /// <param name="ct">Cancellation token for the operation.</param>
        /// <returns>The next run time, or null if the schedule is disabled or not found.</returns>
        Task<DateTimeOffset?> GetNextRunTimeAsync(string scheduleId, CancellationToken ct = default);
    }

    /// <summary>
    /// Defines a recurring vaccination schedule with time window constraints.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination types")]
    public record VaccinationSchedule
    {
        /// <summary>
        /// Unique identifier for the schedule.
        /// </summary>
        public required string Id { get; init; }

        /// <summary>
        /// Human-readable name for the schedule.
        /// </summary>
        public required string Name { get; init; }

        /// <summary>
        /// Cron expression for schedule timing. Null if using interval-based scheduling.
        /// </summary>
        public string? CronExpression { get; init; }

        /// <summary>
        /// Interval in milliseconds between runs. Null if using cron-based scheduling.
        /// </summary>
        public long? IntervalMs { get; init; }

        /// <summary>
        /// The experiments to run on this schedule.
        /// </summary>
        public ScheduledExperiment[] Experiments { get; init; } = Array.Empty<ScheduledExperiment>();

        /// <summary>
        /// Whether this schedule is currently enabled.
        /// </summary>
        public bool Enabled { get; init; } = true;

        /// <summary>
        /// Maximum number of experiments from this schedule that can run concurrently.
        /// </summary>
        public int MaxConcurrent { get; init; } = 1;

        /// <summary>
        /// Optional time windows during which this schedule is allowed to execute.
        /// Null means the schedule can execute at any time.
        /// </summary>
        public TimeWindow[]? TimeWindows { get; init; }

        /// <summary>
        /// Validates mutual exclusion between CronExpression and IntervalMs and returns any errors.
        /// Exactly one of these must be set for a valid schedule.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Thrown if both CronExpression and IntervalMs are null, or both are set simultaneously.
        /// </exception>
        public void Validate()
        {
            if (CronExpression == null && IntervalMs == null)
                throw new InvalidOperationException(
                    $"VaccinationSchedule '{Id}': either CronExpression or IntervalMs must be set.");
            if (CronExpression != null && IntervalMs != null)
                throw new InvalidOperationException(
                    $"VaccinationSchedule '{Id}': CronExpression and IntervalMs are mutually exclusive; set only one.");
        }
    }

    /// <summary>
    /// An experiment within a vaccination schedule, with weighting and immunity requirements.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination types")]
    public record ScheduledExperiment
    {
        /// <summary>
        /// The experiment definition to run.
        /// </summary>
        public required ChaosExperiment Experiment { get; init; }

        /// <summary>
        /// Relative weight for experiment selection when multiple experiments are scheduled.
        /// Higher values increase selection probability. Default: 1.0.
        /// </summary>
        public double Weight { get; init; } = 1.0;

        /// <summary>
        /// If true, this experiment must pass for the system to be considered "vaccinated"
        /// against the associated fault type.
        /// </summary>
        public bool RequiredImmunity { get; init; } = false;
    }

    /// <summary>
    /// A time window defining when vaccination experiments are allowed to execute.
    /// </summary>
    [SdkCompatibility("5.0.0", Notes = "Phase 61: Chaos vaccination types")]
    public record TimeWindow
    {
        /// <summary>
        /// The day of week this window applies to. Null means any day.
        /// </summary>
        public DayOfWeek? DayOfWeek { get; init; }

        /// <summary>
        /// The start hour (0-23) of the window in the specified time zone.
        /// </summary>
        public required int StartHour { get; init; }

        /// <summary>
        /// The end hour (0-23) of the window in the specified time zone.
        /// </summary>
        public required int EndHour { get; init; }

        /// <summary>
        /// The IANA time zone identifier for this window (e.g., "America/New_York").
        /// </summary>
        public required string TimeZone { get; init; }

        /// <summary>
        /// Validates the time window values.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">
        /// Thrown if StartHour or EndHour are outside the 0-23 range.
        /// </exception>
        public void Validate()
        {
            if (StartHour < 0 || StartHour > 23)
                throw new ArgumentOutOfRangeException(nameof(StartHour),
                    StartHour, "StartHour must be in range 0-23.");
            if (EndHour < 0 || EndHour > 23)
                throw new ArgumentOutOfRangeException(nameof(EndHour),
                    EndHour, "EndHour must be in range 0-23.");
        }
    }
}
