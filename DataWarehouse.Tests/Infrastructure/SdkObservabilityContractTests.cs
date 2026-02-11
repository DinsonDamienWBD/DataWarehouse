using DataWarehouse.SDK.Contracts.Observability;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Infrastructure;

/// <summary>
/// Tests for SDK observability contracts (replaces MetricsCollectorTests and DistributedTracingTests).
/// Validates interface contracts and data types used by the observability subsystem.
/// </summary>
[Trait("Category", "Unit")]
public class SdkObservabilityContractTests
{
    [Fact]
    public void IObservabilityStrategy_ShouldExistInSdk()
    {
        var type = typeof(IObservabilityStrategy);
        type.Should().NotBeNull();
        type.IsInterface.Should().BeTrue();
    }

    [Fact]
    public void ObservabilityCapabilities_None_ShouldHaveNoFeatures()
    {
        var caps = ObservabilityCapabilities.None();

        caps.SupportsMetrics.Should().BeFalse();
        caps.SupportsTracing.Should().BeFalse();
        caps.SupportsLogging.Should().BeFalse();
        caps.SupportsDistributedTracing.Should().BeFalse();
        caps.SupportsAlerting.Should().BeFalse();
        caps.HasAnyCapability.Should().BeFalse();
        caps.HasFullObservability.Should().BeFalse();
        caps.SupportedExporters.Should().BeEmpty();
    }

    [Fact]
    public void ObservabilityCapabilities_Full_ShouldHaveAllFeatures()
    {
        var caps = ObservabilityCapabilities.Full("OpenTelemetry", "Prometheus");

        caps.SupportsMetrics.Should().BeTrue();
        caps.SupportsTracing.Should().BeTrue();
        caps.SupportsLogging.Should().BeTrue();
        caps.SupportsDistributedTracing.Should().BeTrue();
        caps.SupportsAlerting.Should().BeTrue();
        caps.HasAnyCapability.Should().BeTrue();
        caps.HasFullObservability.Should().BeTrue();
        caps.SupportedExporters.Should().HaveCount(2);
    }

    [Fact]
    public void ObservabilityCapabilities_PartialFeatures_ShouldReflectCorrectly()
    {
        var caps = new ObservabilityCapabilities(
            SupportsMetrics: true,
            SupportsTracing: false,
            SupportsLogging: true,
            SupportsDistributedTracing: false,
            SupportsAlerting: false,
            SupportedExporters: ["StatsD"]);

        caps.HasAnyCapability.Should().BeTrue();
        caps.HasFullObservability.Should().BeFalse();
    }

    [Fact]
    public void ObservabilityStrategyBase_ShouldBeAbstract()
    {
        var type = typeof(ObservabilityStrategyBase);
        type.IsAbstract.Should().BeTrue();
        type.GetInterfaces().Should().Contain(typeof(IObservabilityStrategy));
    }

    [Fact]
    public void ObservabilityStrategyBase_ShouldExposeCapabilities()
    {
        var type = typeof(ObservabilityStrategyBase);
        var prop = type.GetProperty("Capabilities");
        prop.Should().NotBeNull();
        prop!.PropertyType.Should().Be(typeof(ObservabilityCapabilities));
    }
}
