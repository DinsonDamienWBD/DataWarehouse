using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Moonshots;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.Plugins.UltimateDataGovernance.Moonshots;
using DataWarehouse.Plugins.UltimateDataGovernance.Moonshots.HealthProbes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace DataWarehouse.Tests.Moonshots;

/// <summary>
/// Tests for moonshot health probes and the health aggregator.
/// Validates probe creation, aggregation behavior across all status states,
/// and registry integration.
/// </summary>
public sealed class MoonshotHealthProbeTests
{
    private readonly MoonshotConfiguration _config = MoonshotConfigurationDefaults.CreateProductionDefaults();

    /// <summary>
    /// Creates a mock health probe that returns the specified readiness.
    /// </summary>
    private static IMoonshotHealthProbe CreateMockProbe(MoonshotId id, MoonshotReadiness readiness)
    {
        var probeMock = new Mock<IMoonshotHealthProbe>();
        probeMock.Setup(p => p.MoonshotId).Returns(id);
        probeMock.Setup(p => p.HealthCheckInterval).Returns(TimeSpan.FromSeconds(30));
        probeMock.Setup(p => p.CheckHealthAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MoonshotHealthReport(
                Id: id,
                Readiness: readiness,
                Summary: $"{id}: {readiness}",
                Components: new Dictionary<string, MoonshotComponentHealth>
                {
                    ["Main"] = new MoonshotComponentHealth("Main", readiness, $"{id} main component")
                },
                CheckedAt: DateTimeOffset.UtcNow,
                CheckDuration: TimeSpan.FromMilliseconds(5)));
        return probeMock.Object;
    }

    [Fact]
    public async Task HealthAggregator_AllProbesReady_ReturnsAllReady()
    {
        // Arrange: 10 probes all returning Ready
        var probes = Enum.GetValues<MoonshotId>()
            .Select(id => CreateMockProbe(id, MoonshotReadiness.Ready))
            .ToList();

        var registry = new MoonshotRegistryImpl();
        foreach (var id in Enum.GetValues<MoonshotId>())
        {
            registry.Register(new MoonshotRegistration(id, id.ToString(), $"{id} moonshot", MoonshotStatus.Initializing));
        }

        var aggregator = new MoonshotHealthAggregator(
            probes, registry, NullLogger<MoonshotHealthAggregator>.Instance);

        // Act
        var reports = await aggregator.CheckAllAsync(CancellationToken.None);

        // Assert: all 10 reports, all Ready
        Assert.Equal(10, reports.Count);
        Assert.All(reports, r => Assert.Equal(MoonshotReadiness.Ready, r.Readiness));
    }

    [Fact]
    public async Task HealthAggregator_OneProbeNotReady_ReportsDegraded()
    {
        // Arrange: 9 Ready + 1 NotReady
        var probes = Enum.GetValues<MoonshotId>()
            .Select(id => id == MoonshotId.DataConsciousness
                ? CreateMockProbe(id, MoonshotReadiness.NotReady)
                : CreateMockProbe(id, MoonshotReadiness.Ready))
            .ToList();

        var registry = new MoonshotRegistryImpl();
        foreach (var id in Enum.GetValues<MoonshotId>())
        {
            registry.Register(new MoonshotRegistration(id, id.ToString(), $"{id} moonshot", MoonshotStatus.Initializing));
        }

        var aggregator = new MoonshotHealthAggregator(
            probes, registry, NullLogger<MoonshotHealthAggregator>.Instance);

        // Act
        var reports = await aggregator.CheckAllAsync(CancellationToken.None);

        // Assert: 9 Ready + 1 NotReady
        Assert.Equal(10, reports.Count);
        var readyReports = reports.Where(r => r.Readiness == MoonshotReadiness.Ready).ToList();
        var notReadyReports = reports.Where(r => r.Readiness == MoonshotReadiness.NotReady).ToList();
        Assert.Equal(9, readyReports.Count);
        Assert.Single(notReadyReports);
        Assert.Equal(MoonshotId.DataConsciousness, notReadyReports[0].Id);
    }

    [Fact]
    public async Task HealthAggregator_UpdatesRegistry()
    {
        // Arrange: mock probes and a real registry
        var probes = Enum.GetValues<MoonshotId>()
            .Select(id => CreateMockProbe(id, MoonshotReadiness.Ready))
            .ToList();

        var registryMock = new Mock<IMoonshotRegistry>();

        var aggregator = new MoonshotHealthAggregator(
            probes, registryMock.Object, NullLogger<MoonshotHealthAggregator>.Instance);

        // Act
        await aggregator.CheckAllAsync(CancellationToken.None);

        // Assert: UpdateHealthReport called 10 times (once per probe)
        registryMock.Verify(
            r => r.UpdateHealthReport(It.IsAny<MoonshotId>(), It.IsAny<MoonshotHealthReport>()),
            Times.Exactly(10));
    }

    [Theory]
    [InlineData(MoonshotId.UniversalTags)]
    [InlineData(MoonshotId.DataConsciousness)]
    [InlineData(MoonshotId.CompliancePassports)]
    [InlineData(MoonshotId.SovereigntyMesh)]
    [InlineData(MoonshotId.ZeroGravityStorage)]
    [InlineData(MoonshotId.CryptoTimeLocks)]
    [InlineData(MoonshotId.SemanticSync)]
    [InlineData(MoonshotId.ChaosVaccination)]
    [InlineData(MoonshotId.CarbonAwareLifecycle)]
    [InlineData(MoonshotId.UniversalFabric)]
    public void HealthProbe_HasCorrectId(MoonshotId id)
    {
        // Arrange: create actual probe via mock bus
        var busMock = new Mock<IMessageBus>();
        var probe = CreateActualProbe(id, busMock.Object);

        // Assert
        Assert.Equal(id, probe.MoonshotId);
    }

    [Fact]
    public async Task HealthProbe_BusTimeout_ReportsNotReady()
    {
        // Arrange: mock bus that always times out (cancels after timeout)
        var busMock = new Mock<IMessageBus>();
        busMock.Setup(b => b.SendAsync(It.IsAny<string>(), It.IsAny<PluginMessage>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MessageResponse.Error("Request timed out", "TIMEOUT"));
        busMock.Setup(b => b.SendAsync(It.IsAny<string>(), It.IsAny<PluginMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("Bus timeout simulated"));

        var probe = new TagsHealthProbe(
            busMock.Object,
            _config,
            NullLogger<TagsHealthProbe>.Instance);

        // Act
        var report = await probe.CheckHealthAsync(CancellationToken.None);

        // Assert: overall readiness should not be Ready due to timeout
        Assert.Equal(MoonshotId.UniversalTags, report.Id);
        Assert.NotEqual(MoonshotReadiness.Ready, report.Readiness);
    }

    /// <summary>
    /// Creates the actual health probe implementation for the given moonshot.
    /// </summary>
    private IMoonshotHealthProbe CreateActualProbe(MoonshotId id, IMessageBus bus) => id switch
    {
        MoonshotId.UniversalTags => new TagsHealthProbe(bus, _config, NullLogger<TagsHealthProbe>.Instance),
        MoonshotId.DataConsciousness => new ConsciousnessHealthProbe(bus, _config, NullLogger<ConsciousnessHealthProbe>.Instance),
        MoonshotId.CompliancePassports => new ComplianceHealthProbe(bus, _config, NullLogger<ComplianceHealthProbe>.Instance),
        MoonshotId.SovereigntyMesh => new SovereigntyHealthProbe(bus, _config, NullLogger<SovereigntyHealthProbe>.Instance),
        MoonshotId.ZeroGravityStorage => new PlacementHealthProbe(bus, _config, NullLogger<PlacementHealthProbe>.Instance),
        MoonshotId.CryptoTimeLocks => new TimeLockHealthProbe(bus, _config, NullLogger<TimeLockHealthProbe>.Instance),
        MoonshotId.SemanticSync => new SemanticSyncHealthProbe(bus, _config, NullLogger<SemanticSyncHealthProbe>.Instance),
        MoonshotId.ChaosVaccination => new ChaosHealthProbe(bus, _config, NullLogger<ChaosHealthProbe>.Instance),
        MoonshotId.CarbonAwareLifecycle => new CarbonHealthProbe(bus, _config, NullLogger<CarbonHealthProbe>.Instance),
        MoonshotId.UniversalFabric => new FabricHealthProbe(bus, _config, NullLogger<FabricHealthProbe>.Instance),
        _ => throw new ArgumentOutOfRangeException(nameof(id))
    };
}
