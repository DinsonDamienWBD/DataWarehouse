using DataWarehouse.SDK.Infrastructure;
using DataWarehouse.SDK.Contracts;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Infrastructure;

/// <summary>
/// Tests for MetricsCollector functionality.
/// </summary>
public class MetricsCollectorTests
{
    #region Counter Tests

    [Fact]
    public void IncrementCounter_ShouldCreateAndIncrementCounter()
    {
        // Arrange
        var collector = new MetricsCollector();

        // Act
        collector.IncrementCounter("requests.total");
        collector.IncrementCounter("requests.total");
        collector.IncrementCounter("requests.total");

        // Assert
        var snapshot = collector.GetSnapshot();
        snapshot.Counters.Should().ContainKey("requests.total");
        snapshot.Counters["requests.total"].Value.Should().Be(3);
    }

    [Fact]
    public void IncrementCounter_ShouldIncrementBySpecifiedValue()
    {
        // Arrange
        var collector = new MetricsCollector();

        // Act
        collector.IncrementCounter("bytes.transferred", 1000);
        collector.IncrementCounter("bytes.transferred", 500);

        // Assert
        var snapshot = collector.GetSnapshot();
        snapshot.Counters["bytes.transferred"].Value.Should().Be(1500);
    }

    [Fact]
    public void IncrementCounter_ShouldSupportTags()
    {
        // Arrange
        var collector = new MetricsCollector();

        // Act
        collector.IncrementCounter("requests", 1, "method:GET", "status:200");
        collector.IncrementCounter("requests", 1, "method:POST", "status:201");

        // Assert
        var snapshot = collector.GetSnapshot();
        snapshot.Counters.Should().HaveCount(2);
    }

    [Fact]
    public void IncrementCounter_ShouldHandleSameTagsInDifferentOrder()
    {
        // Arrange
        var collector = new MetricsCollector();

        // Act - Same tags, different order should be same metric
        collector.IncrementCounter("requests", 1, "method:GET", "status:200");
        collector.IncrementCounter("requests", 1, "status:200", "method:GET");

        // Assert
        var snapshot = collector.GetSnapshot();
        snapshot.Counters.Should().HaveCount(1);
        snapshot.Counters.Values.First().Value.Should().Be(2);
    }

    [Fact]
    public void IncrementCounter_ShouldBeThreadSafe()
    {
        // Arrange
        var collector = new MetricsCollector();
        var tasks = new List<Task>();

        // Act - Concurrent increments
        for (int i = 0; i < 1000; i++)
        {
            tasks.Add(Task.Run(() => collector.IncrementCounter("concurrent.counter")));
        }

        Task.WhenAll(tasks).Wait();

        // Assert
        var snapshot = collector.GetSnapshot();
        snapshot.Counters["concurrent.counter"].Value.Should().Be(1000);
    }

    #endregion

    #region Gauge Tests

    [Fact]
    public void RecordGauge_ShouldStoreLatestValue()
    {
        // Arrange
        var collector = new MetricsCollector();

        // Act
        collector.RecordGauge("temperature", 20.5);
        collector.RecordGauge("temperature", 22.0);
        collector.RecordGauge("temperature", 21.3);

        // Assert
        var snapshot = collector.GetSnapshot();
        snapshot.Gauges["temperature"].Value.Should().Be(21.3);
    }

    [Fact]
    public void RecordGauge_ShouldSupportTags()
    {
        // Arrange
        var collector = new MetricsCollector();

        // Act
        collector.RecordGauge("memory.used", 1024, "process:web");
        collector.RecordGauge("memory.used", 512, "process:worker");

        // Assert
        var snapshot = collector.GetSnapshot();
        snapshot.Gauges.Should().HaveCount(2);
    }

    [Fact]
    public void RecordGauge_ShouldBeThreadSafe()
    {
        // Arrange
        var collector = new MetricsCollector();
        var tasks = new List<Task>();

        // Act
        for (int i = 0; i < 100; i++)
        {
            var value = i;
            tasks.Add(Task.Run(() => collector.RecordGauge("concurrent.gauge", value)));
        }

        Task.WhenAll(tasks).Wait();

        // Assert - Should have one of the values (last one wins)
        var snapshot = collector.GetSnapshot();
        snapshot.Gauges["concurrent.gauge"].Value.Should().BeInRange(0, 99);
    }

    #endregion

    #region Histogram Tests

    [Fact]
    public void RecordHistogram_ShouldTrackStatistics()
    {
        // Arrange
        var collector = new MetricsCollector();

        // Act
        collector.RecordHistogram("latency", 10);
        collector.RecordHistogram("latency", 20);
        collector.RecordHistogram("latency", 30);
        collector.RecordHistogram("latency", 40);
        collector.RecordHistogram("latency", 50);

        // Assert
        var snapshot = collector.GetSnapshot();
        var histogram = snapshot.Histograms["latency"];

        histogram.Count.Should().Be(5);
        histogram.Sum.Should().Be(150);
        histogram.Min.Should().Be(10);
        histogram.Max.Should().Be(50);
        histogram.Mean.Should().Be(30);
    }

    [Fact]
    public void RecordHistogram_ShouldCalculatePercentiles()
    {
        // Arrange
        var collector = new MetricsCollector();

        // Act - Add 100 values from 1 to 100
        for (int i = 1; i <= 100; i++)
        {
            collector.RecordHistogram("response.time", i);
        }

        // Assert
        var snapshot = collector.GetSnapshot();
        var histogram = snapshot.Histograms["response.time"];

        histogram.P50.Should().BeApproximately(50, 2);
        histogram.P95.Should().BeApproximately(95, 2);
        histogram.P99.Should().BeApproximately(99, 2);
    }

    [Fact]
    public void RecordHistogram_ShouldHandleSingleValue()
    {
        // Arrange
        var collector = new MetricsCollector();

        // Act
        collector.RecordHistogram("single", 42);

        // Assert
        var snapshot = collector.GetSnapshot();
        var histogram = snapshot.Histograms["single"];

        histogram.Count.Should().Be(1);
        histogram.Min.Should().Be(42);
        histogram.Max.Should().Be(42);
        histogram.Mean.Should().Be(42);
        histogram.P50.Should().Be(42);
    }

    [Fact]
    public void RecordHistogram_ShouldSupportTags()
    {
        // Arrange
        var collector = new MetricsCollector();

        // Act
        collector.RecordHistogram("duration", 100, "operation:read");
        collector.RecordHistogram("duration", 200, "operation:write");

        // Assert
        var snapshot = collector.GetSnapshot();
        snapshot.Histograms.Should().HaveCount(2);
    }

    [Fact]
    public void RecordHistogram_ShouldBeThreadSafe()
    {
        // Arrange
        var collector = new MetricsCollector();
        var tasks = new List<Task>();

        // Act - Concurrent recordings
        for (int i = 0; i < 1000; i++)
        {
            var value = i;
            tasks.Add(Task.Run(() => collector.RecordHistogram("concurrent.histogram", value)));
        }

        Task.WhenAll(tasks).Wait();

        // Assert
        var snapshot = collector.GetSnapshot();
        snapshot.Histograms["concurrent.histogram"].Count.Should().Be(1000);
    }

    [Fact]
    public void RecordHistogram_ShouldUseReservoirSamplingWhenLimitExceeded()
    {
        // Arrange
        var limits = new KernelLimitsConfig { MaxHistogramSampleValues = 100 };
        var collector = new MetricsCollector(limits);

        // Act - Record more values than the limit
        for (int i = 0; i < 1000; i++)
        {
            collector.RecordHistogram("sampled", i);
        }

        // Assert - Count should still be accurate
        var snapshot = collector.GetSnapshot();
        var histogram = snapshot.Histograms["sampled"];

        histogram.Count.Should().Be(1000);
        histogram.Min.Should().Be(0);
        histogram.Max.Should().Be(999);
        // Percentiles will be approximate due to sampling
        histogram.P50.Should().BeInRange(200, 800);
    }

    #endregion

    #region Timer Tests

    [Fact]
    public void StartTimer_ShouldRecordDurationOnDispose()
    {
        // Arrange
        var collector = new MetricsCollector();

        // Act
        using (collector.StartTimer("operation.duration"))
        {
            Thread.Sleep(50);
        }

        // Assert
        var snapshot = collector.GetSnapshot();
        var histogram = snapshot.Histograms["operation.duration"];
        histogram.Count.Should().Be(1);
        histogram.Max.Should().BeGreaterThan(30); // At least 30ms (accounting for scheduling)
    }

    [Fact]
    public void StartTimer_ShouldSupportTags()
    {
        // Arrange
        var collector = new MetricsCollector();

        // Act
        using (collector.StartTimer("request.duration", "endpoint:/api/users"))
        {
            Thread.Sleep(10);
        }

        // Assert
        var snapshot = collector.GetSnapshot();
        snapshot.Histograms.Should().HaveCount(1);
        snapshot.Histograms.Values.First().Tags.Should().Contain("endpoint:/api/users");
    }

    [Fact]
    public void StartTimer_ShouldHandleMultipleConcurrentTimers()
    {
        // Arrange
        var collector = new MetricsCollector();
        var tasks = new List<Task>();

        // Act
        for (int i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                using (collector.StartTimer("concurrent.timer"))
                {
                    Thread.Sleep(Random.Shared.Next(10, 50));
                }
            }));
        }

        Task.WhenAll(tasks).Wait();

        // Assert
        var snapshot = collector.GetSnapshot();
        snapshot.Histograms["concurrent.timer"].Count.Should().Be(10);
    }

    #endregion

    #region Snapshot and Reset

    [Fact]
    public void GetSnapshot_ShouldIncludeTimestamp()
    {
        // Arrange
        var collector = new MetricsCollector();
        var beforeTime = DateTime.UtcNow;

        // Act
        collector.IncrementCounter("test");
        var snapshot = collector.GetSnapshot();

        // Assert
        snapshot.Timestamp.Should().BeOnOrAfter(beforeTime);
        snapshot.Timestamp.Should().BeOnOrBefore(DateTime.UtcNow);
    }

    [Fact]
    public void GetSnapshot_ShouldReturnEmptyCollectionsInitially()
    {
        // Arrange
        var collector = new MetricsCollector();

        // Act
        var snapshot = collector.GetSnapshot();

        // Assert
        snapshot.Counters.Should().BeEmpty();
        snapshot.Gauges.Should().BeEmpty();
        snapshot.Histograms.Should().BeEmpty();
    }

    [Fact]
    public void Reset_ShouldClearAllMetrics()
    {
        // Arrange
        var collector = new MetricsCollector();
        collector.IncrementCounter("counter");
        collector.RecordGauge("gauge", 100);
        collector.RecordHistogram("histogram", 50);

        // Act
        collector.Reset();

        // Assert
        var snapshot = collector.GetSnapshot();
        snapshot.Counters.Should().BeEmpty();
        snapshot.Gauges.Should().BeEmpty();
        snapshot.Histograms.Should().BeEmpty();
    }

    #endregion

    #region Configurable Limits

    [Fact]
    public void Constructor_ShouldAcceptCustomLimits()
    {
        // Arrange
        var limits = new KernelLimitsConfig
        {
            MaxHistogramSampleValues = 50
        };

        // Act
        var collector = new MetricsCollector(limits);

        // Assert - Should not throw
        collector.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_ShouldThrowOnNullLimits()
    {
        // Act & Assert
        var act = () => new MetricsCollector(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_ShouldUseDefaultLimitsWhenNotSpecified()
    {
        // Act
        var collector = new MetricsCollector();

        // Assert - Record many values, should work with default limit
        for (int i = 0; i < 10001; i++)
        {
            collector.RecordHistogram("default.limit", i);
        }

        var snapshot = collector.GetSnapshot();
        snapshot.Histograms["default.limit"].Count.Should().Be(10001);
    }

    #endregion

    #region Metric Names

    [Fact]
    public void KernelMetrics_ShouldHaveConsistentNaming()
    {
        // Assert - All kernel metrics should follow naming convention
        KernelMetrics.OperationsTotal.Should().StartWith("kernel.");
        KernelMetrics.OperationLatency.Should().StartWith("kernel.");
        KernelMetrics.CircuitBreakerOpen.Should().StartWith("kernel.");
        KernelMetrics.MessagesPublished.Should().StartWith("kernel.");
    }

    [Fact]
    public void Metrics_ShouldSupportSpecialCharactersInNames()
    {
        // Arrange
        var collector = new MetricsCollector();

        // Act
        collector.IncrementCounter("http.requests.2xx");
        collector.IncrementCounter("api/v1/users.count");
        collector.IncrementCounter("cache:hits");

        // Assert
        var snapshot = collector.GetSnapshot();
        snapshot.Counters.Should().HaveCount(3);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Metrics_ShouldHandleNegativeValues()
    {
        // Arrange
        var collector = new MetricsCollector();

        // Act
        collector.RecordGauge("temperature", -20.5);
        collector.RecordHistogram("delta", -100);

        // Assert
        var snapshot = collector.GetSnapshot();
        snapshot.Gauges["temperature"].Value.Should().Be(-20.5);
        snapshot.Histograms["delta"].Min.Should().Be(-100);
    }

    [Fact]
    public void Metrics_ShouldHandleVeryLargeValues()
    {
        // Arrange
        var collector = new MetricsCollector();

        // Act
        collector.IncrementCounter("large.counter", long.MaxValue / 2);
        collector.RecordGauge("large.gauge", double.MaxValue / 2);

        // Assert
        var snapshot = collector.GetSnapshot();
        snapshot.Counters["large.counter"].Value.Should().BeGreaterThan(0);
        snapshot.Gauges["large.gauge"].Value.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Metrics_ShouldHandleEmptyName()
    {
        // Arrange
        var collector = new MetricsCollector();

        // Act - Empty name is technically valid
        collector.IncrementCounter("");

        // Assert
        var snapshot = collector.GetSnapshot();
        snapshot.Counters.Should().ContainKey("");
    }

    [Fact]
    public void Metrics_ShouldHandleEmptyTags()
    {
        // Arrange
        var collector = new MetricsCollector();

        // Act
        collector.IncrementCounter("no.tags");
        collector.IncrementCounter("empty.tags", 1, Array.Empty<string>());

        // Assert
        var snapshot = collector.GetSnapshot();
        snapshot.Counters.Should().HaveCount(2);
    }

    #endregion
}
