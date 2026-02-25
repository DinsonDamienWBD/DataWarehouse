using System;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;

namespace DataWarehouse.SDK.Infrastructure.Intelligence;

/// <summary>
/// Configuration options for creating an <see cref="AiPolicyIntelligenceSystem"/> via the factory.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 77: AI Policy Intelligence (AIPI-01 through AIPI-11)")]
public sealed record AiPolicyIntelligenceOptions
{
    /// <summary>
    /// Maximum CPU overhead percentage for the observation pipeline. Default 1.0%.
    /// </summary>
    public double MaxCpuOverheadPercent { get; init; } = 1.0;

    /// <summary>
    /// Ring buffer capacity for observation events. Must be a power of two. Default 8192.
    /// </summary>
    public int RingBufferCapacity { get; init; } = 8192;

    /// <summary>
    /// Interval between pipeline drain cycles. Default 100ms.
    /// </summary>
    public TimeSpan DrainInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Threat score threshold for ThreatDetector. Default 0.3.
    /// </summary>
    public double ThreatThreshold { get; init; } = 0.3;

    /// <summary>
    /// Cloud CPU cost per second in USD for cost analysis. Default $0.000012.
    /// </summary>
    public double CloudCpuCostPerSecondUsd { get; init; } = 0.000012;

    /// <summary>
    /// Default autonomy level for unconfigured features. Default <see cref="AiAutonomyLevel.Suggest"/>.
    /// </summary>
    public AiAutonomyLevel DefaultAutonomyLevel { get; init; } = AiAutonomyLevel.Suggest;

    /// <summary>
    /// Self-modification policy controlling AI access to its own configuration.
    /// Default: AllowSelfModification=false, RequiresQuorum=true.
    /// </summary>
    public AiSelfModificationPolicy SelfModificationPolicy { get; init; } = new();

    /// <summary>
    /// Optional quorum service for N-of-M approval of configuration changes.
    /// Null means no quorum enforcement available (fail-closed for modifications).
    /// </summary>
    public IQuorumService? QuorumService { get; init; }
}

/// <summary>
/// Unified entry point holding all AI Policy Intelligence pipeline components.
/// Created by <see cref="AiPolicyIntelligenceFactory"/> and provides lifecycle management
/// for the entire observation and advisory pipeline.
/// </summary>
/// <remarks>
/// The system owns all components and manages their lifecycle through <see cref="StartAsync"/>
/// and <see cref="StopAsync"/>. Dispose via <see cref="DisposeAsync"/> to cleanly shut down
/// all background processing.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 77: AI Policy Intelligence (AIPI-01 through AIPI-11)")]
public sealed class AiPolicyIntelligenceSystem : IAsyncDisposable
{
    /// <summary>Ring buffer for lock-free observation ingestion (AIPI-01).</summary>
    public AiObservationRingBuffer RingBuffer { get; }

    /// <summary>CPU overhead throttle controlling observation drop behavior (AIPI-01).</summary>
    public OverheadThrottle Throttle { get; }

    /// <summary>Async consumer pipeline draining the ring buffer (AIPI-01).</summary>
    public AiObservationPipeline Pipeline { get; }

    /// <summary>Hardware capability and resource state advisor (AIPI-02).</summary>
    public HardwareProbe Hardware { get; }

    /// <summary>Workload pattern and throughput advisor (AIPI-03).</summary>
    public WorkloadAnalyzer Workload { get; }

    /// <summary>Security threat detection advisor (AIPI-04).</summary>
    public ThreatDetector Threat { get; }

    /// <summary>Algorithm cost and billing projection advisor (AIPI-05).</summary>
    public CostAnalyzer Cost { get; }

    /// <summary>Data sensitivity and PII detection advisor (AIPI-06).</summary>
    public DataSensitivityAnalyzer Sensitivity { get; }

    /// <summary>Synthesizing policy advisor combining all 5 domain advisors (AIPI-07).</summary>
    public PolicyAdvisor Advisor { get; }

    /// <summary>Per-feature per-level autonomy configuration (AIPI-08).</summary>
    public AiAutonomyConfiguration AutonomyConfig { get; }

    /// <summary>Self-modification guard preventing AI from changing its own config (AIPI-11).</summary>
    public AiSelfModificationGuard SelfModificationGuard { get; }

    /// <summary>Currently active hybrid autonomy profile, if one has been applied (AIPI-09).</summary>
    public HybridAutonomyProfile? ActiveProfile { get; private set; }

    internal AiPolicyIntelligenceSystem(
        AiObservationRingBuffer ringBuffer,
        OverheadThrottle throttle,
        AiObservationPipeline pipeline,
        HardwareProbe hardware,
        WorkloadAnalyzer workload,
        ThreatDetector threat,
        CostAnalyzer cost,
        DataSensitivityAnalyzer sensitivity,
        PolicyAdvisor advisor,
        AiAutonomyConfiguration autonomyConfig,
        AiSelfModificationGuard selfModificationGuard)
    {
        RingBuffer = ringBuffer;
        Throttle = throttle;
        Pipeline = pipeline;
        Hardware = hardware;
        Workload = workload;
        Threat = threat;
        Cost = cost;
        Sensitivity = sensitivity;
        Advisor = advisor;
        AutonomyConfig = autonomyConfig;
        SelfModificationGuard = selfModificationGuard;
    }

    /// <summary>
    /// Starts the observation pipeline background processing loop.
    /// </summary>
    /// <param name="ct">Cancellation token for the pipeline lifetime.</param>
    public Task StartAsync(CancellationToken ct = default)
    {
        return Pipeline.StartAsync(ct);
    }

    /// <summary>
    /// Stops the observation pipeline, performing a final drain of remaining observations.
    /// </summary>
    public Task StopAsync()
    {
        return Pipeline.StopAsync();
    }

    /// <summary>
    /// Attaches this system's ring buffer to an existing <see cref="ObservationEmitter"/>,
    /// enabling the emitter to write observations into the pipeline.
    /// </summary>
    /// <param name="emitter">The observation emitter to wire up.</param>
    public void AttachToEmitter(ObservationEmitter emitter)
    {
        if (emitter is null) throw new ArgumentNullException(nameof(emitter));
        emitter.AttachRingBuffer(RingBuffer);
    }

    /// <summary>
    /// Applies a hybrid autonomy profile to the system's autonomy configuration.
    /// </summary>
    /// <param name="profile">The profile to apply.</param>
    public void ApplyProfile(HybridAutonomyProfile profile)
    {
        ActiveProfile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Pipeline.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// Factory that wires all AI Policy Intelligence components together, providing turnkey
/// <see cref="AiPolicyIntelligenceSystem"/> creation via <see cref="Create"/> and
/// <see cref="CreateDefault"/>.
/// </summary>
/// <remarks>
/// Implements the final wiring for AIPI-01 through AIPI-11. The creation order ensures
/// all dependencies are satisfied:
/// <list type="number">
///   <item><description>Ring buffer (observation ingestion)</description></item>
///   <item><description>Overhead throttle (CPU budget enforcement)</description></item>
///   <item><description>5 domain advisors (hardware, workload, threat, cost, sensitivity)</description></item>
///   <item><description>Autonomy configuration (per-feature per-level settings)</description></item>
///   <item><description>Policy advisor (synthesizer consuming all 5 domain advisors)</description></item>
///   <item><description>Observation pipeline (async consumer connecting buffer to advisors)</description></item>
///   <item><description>Self-modification guard (safety layer wrapping configuration)</description></item>
/// </list>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 77: AI Policy Intelligence (AIPI-01 through AIPI-11)")]
public static class AiPolicyIntelligenceFactory
{
    /// <summary>
    /// Creates an <see cref="AiPolicyIntelligenceSystem"/> with the specified options.
    /// </summary>
    /// <param name="options">Configuration options for all pipeline components.</param>
    /// <returns>A fully wired system ready for <see cref="AiPolicyIntelligenceSystem.StartAsync"/>.</returns>
    public static AiPolicyIntelligenceSystem Create(AiPolicyIntelligenceOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));

        // 1. Ring buffer
        var ringBuffer = new AiObservationRingBuffer(options.RingBufferCapacity);

        // 2. Overhead throttle
        var throttle = new OverheadThrottle(options.MaxCpuOverheadPercent);

        // 3. Domain advisors
        var hardware = new HardwareProbe();
        var workload = new WorkloadAnalyzer();
        var threat = new ThreatDetector(options.ThreatThreshold);
        var cost = new CostAnalyzer(options.CloudCpuCostPerSecondUsd);
        var sensitivity = new DataSensitivityAnalyzer();

        // 4. Autonomy configuration
        var autonomyConfig = new AiAutonomyConfiguration(options.DefaultAutonomyLevel);

        // 5. Policy advisor (synthesizes all 5 domain advisors)
        var advisor = new PolicyAdvisor(hardware, workload, threat, cost, sensitivity, autonomyConfig);

        // 6. Observation pipeline
        var pipeline = new AiObservationPipeline(ringBuffer, throttle, options.DrainInterval);

        // 7. Register all 6 advisors with the pipeline (5 domain + policy advisor)
        pipeline.Advisors.Add(hardware);
        pipeline.Advisors.Add(workload);
        pipeline.Advisors.Add(threat);
        pipeline.Advisors.Add(cost);
        pipeline.Advisors.Add(sensitivity);
        pipeline.Advisors.Add(advisor);

        // 8. Self-modification guard
        var guard = new AiSelfModificationGuard(
            autonomyConfig,
            options.QuorumService,
            options.SelfModificationPolicy);

        // 9. Assemble the system
        return new AiPolicyIntelligenceSystem(
            ringBuffer, throttle, pipeline,
            hardware, workload, threat, cost, sensitivity,
            advisor, autonomyConfig, guard);
    }

    /// <summary>
    /// Creates an <see cref="AiPolicyIntelligenceSystem"/> with default options.
    /// Uses 1% CPU overhead, 8192-capacity ring buffer, 100ms drain interval,
    /// Suggest default autonomy, and self-modification blocked.
    /// </summary>
    /// <returns>A fully wired system with default configuration.</returns>
    public static AiPolicyIntelligenceSystem CreateDefault()
    {
        return Create(new AiPolicyIntelligenceOptions());
    }
}
