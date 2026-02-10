using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using DataWarehouse.SDK.Contracts.Observability;

namespace DataWarehouse.Tests.Infrastructure
{
    /// <summary>
    /// Unit tests for SDK observability strategy infrastructure types:
    /// IObservabilityStrategy, MetricValue, MetricType, MetricLabel,
    /// SpanContext, TraceContext, ObservabilityCapabilities, and supporting types.
    /// </summary>
    public class SdkObservabilityStrategyTests
    {
        #region IObservabilityStrategy Interface Tests

        [Fact]
        public void IObservabilityStrategy_DefinesMetricsAsyncMethod()
        {
            var method = typeof(IObservabilityStrategy).GetMethod("MetricsAsync");
            Assert.NotNull(method);
            Assert.Equal(typeof(Task), method!.ReturnType);
        }

        [Fact]
        public void IObservabilityStrategy_DefinesTracingAsyncMethod()
        {
            var method = typeof(IObservabilityStrategy).GetMethod("TracingAsync");
            Assert.NotNull(method);
            Assert.Equal(typeof(Task), method!.ReturnType);
        }

        [Fact]
        public void IObservabilityStrategy_DefinesLoggingAsyncMethod()
        {
            var method = typeof(IObservabilityStrategy).GetMethod("LoggingAsync");
            Assert.NotNull(method);
            Assert.Equal(typeof(Task), method!.ReturnType);
        }

        [Fact]
        public void IObservabilityStrategy_DefinesHealthCheckAsyncMethod()
        {
            var method = typeof(IObservabilityStrategy).GetMethod("HealthCheckAsync");
            Assert.NotNull(method);
            Assert.Equal(typeof(Task<HealthCheckResult>), method!.ReturnType);
        }

        [Fact]
        public void IObservabilityStrategy_DefinesCapabilitiesProperty()
        {
            var prop = typeof(IObservabilityStrategy).GetProperty("Capabilities");
            Assert.NotNull(prop);
            Assert.Equal(typeof(ObservabilityCapabilities), prop!.PropertyType);
        }

        [Fact]
        public void IObservabilityStrategy_ImplementsIDisposable()
        {
            Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(IObservabilityStrategy)));
        }

        #endregion

        #region MetricType Enum Tests

        [Fact]
        public void MetricType_Has4Values()
        {
            var values = Enum.GetValues<MetricType>();
            Assert.Equal(4, values.Length);
        }

        [Theory]
        [InlineData(MetricType.Counter, 0)]
        [InlineData(MetricType.Gauge, 1)]
        [InlineData(MetricType.Histogram, 2)]
        [InlineData(MetricType.Summary, 3)]
        public void MetricType_HasCorrectValues(MetricType type, int expected)
        {
            Assert.Equal(expected, (int)type);
        }

        #endregion

        #region MetricValue Tests

        [Fact]
        public void MetricValue_CounterFactory_CreatesCounterMetric()
        {
            var metric = MetricValue.Counter("http_requests_total", 42);

            Assert.Equal("http_requests_total", metric.Name);
            Assert.Equal(42, metric.Value);
            Assert.Equal(MetricType.Counter, metric.Type);
            Assert.Empty(metric.Labels);
        }

        [Fact]
        public void MetricValue_GaugeFactory_CreatesGaugeMetric()
        {
            var metric = MetricValue.Gauge("memory_usage_bytes", 1024 * 1024);

            Assert.Equal("memory_usage_bytes", metric.Name);
            Assert.Equal(1024 * 1024, metric.Value);
            Assert.Equal(MetricType.Gauge, metric.Type);
        }

        [Fact]
        public void MetricValue_HistogramFactory_CreatesHistogramMetric()
        {
            var metric = MetricValue.Histogram("request_duration_seconds", 0.125);

            Assert.Equal("request_duration_seconds", metric.Name);
            Assert.Equal(0.125, metric.Value);
            Assert.Equal(MetricType.Histogram, metric.Type);
        }

        [Fact]
        public void MetricValue_SummaryFactory_CreatesSummaryMetric()
        {
            var metric = MetricValue.Summary("query_time_ms", 50.5, unit: "milliseconds");

            Assert.Equal("query_time_ms", metric.Name);
            Assert.Equal(50.5, metric.Value);
            Assert.Equal(MetricType.Summary, metric.Type);
            Assert.Equal("milliseconds", metric.Unit);
        }

        [Fact]
        public void MetricValue_WithLabels_SetsLabelsCorrectly()
        {
            var labels = MetricLabel.Create(("method", "GET"), ("status_code", "200"));
            var metric = MetricValue.Counter("http_requests", 10, labels);

            Assert.Equal(2, metric.Labels.Count);
            Assert.Equal("method", metric.Labels[0].Name);
            Assert.Equal("GET", metric.Labels[0].Value);
        }

        #endregion

        #region MetricLabel Tests

        [Fact]
        public void MetricLabel_Construction_SetsNameAndValue()
        {
            var label = new MetricLabel("method", "POST");

            Assert.Equal("method", label.Name);
            Assert.Equal("POST", label.Value);
        }

        [Fact]
        public void MetricLabel_Create_ReturnsCorrectLabels()
        {
            var labels = MetricLabel.Create(
                ("endpoint", "/api/users"),
                ("status", "200"));

            Assert.Equal(2, labels.Count);
            Assert.Equal("endpoint", labels[0].Name);
            Assert.Equal("/api/users", labels[0].Value);
        }

        [Fact]
        public void MetricLabel_FromDictionary_ConvertsCorrectly()
        {
            var dict = new Dictionary<string, string>
            {
                ["region"] = "us-east-1",
                ["service"] = "api-gateway"
            };

            var labels = MetricLabel.FromDictionary(dict);

            Assert.Equal(2, labels.Count);
        }

        #endregion

        #region SpanContext and TraceTypes Tests

        [Fact]
        public void SpanContext_CreateRoot_GeneratesIds()
        {
            var span = SpanContext.CreateRoot("GET /api/users", SpanKind.Server);

            Assert.NotNull(span.TraceId);
            Assert.NotNull(span.SpanId);
            Assert.Null(span.ParentSpanId);
            Assert.Equal("GET /api/users", span.OperationName);
            Assert.Equal(SpanKind.Server, span.Kind);
            Assert.Equal(SpanStatus.Ok, span.Status);
        }

        [Fact]
        public void SpanContext_CreateChild_InheritsTraceId()
        {
            var root = SpanContext.CreateRoot("parent-op");
            var child = root.CreateChild("child-op", SpanKind.Client);

            Assert.Equal(root.TraceId, child.TraceId);
            Assert.Equal(root.SpanId, child.ParentSpanId);
            Assert.NotEqual(root.SpanId, child.SpanId);
            Assert.Equal("child-op", child.OperationName);
            Assert.Equal(SpanKind.Client, child.Kind);
        }

        [Fact]
        public void SpanKind_Has5Values()
        {
            var values = Enum.GetValues<SpanKind>();
            Assert.Equal(5, values.Length);
        }

        [Fact]
        public void SpanStatus_Has3Values()
        {
            var values = Enum.GetValues<SpanStatus>();
            Assert.Equal(3, values.Length);
        }

        [Fact]
        public void TraceContext_ToTraceparent_FormatsCorrectly()
        {
            var context = new TraceContext("abc123", "def456", 0x01);
            var traceparent = context.ToTraceparent();

            Assert.Equal("00-abc123-def456-01", traceparent);
        }

        [Fact]
        public void TraceContext_ParseTraceparent_ParsesCorrectly()
        {
            var context = TraceContext.ParseTraceparent("00-traceId123-spanId456-01");

            Assert.Equal("traceId123", context.TraceId);
            Assert.Equal("spanId456", context.SpanId);
            Assert.Equal(0x01, context.TraceFlags);
            Assert.True(context.IsSampled);
        }

        [Fact]
        public void TraceContext_IsSampled_ReturnsFalseForZeroFlags()
        {
            var context = new TraceContext("trace1", "span1", 0x00);
            Assert.False(context.IsSampled);
        }

        [Fact]
        public void SpanEvent_Construction_SetsProperties()
        {
            var timestamp = DateTimeOffset.UtcNow;
            var attrs = new Dictionary<string, object> { ["exception.type"] = "TimeoutException" };
            var spanEvent = new SpanEvent("exception", timestamp, attrs);

            Assert.Equal("exception", spanEvent.Name);
            Assert.Equal(timestamp, spanEvent.Timestamp);
            Assert.NotNull(spanEvent.Attributes);
        }

        #endregion

        #region ObservabilityCapabilities Tests

        [Fact]
        public void ObservabilityCapabilities_None_HasAllFeaturesDisabled()
        {
            var caps = ObservabilityCapabilities.None();

            Assert.False(caps.SupportsMetrics);
            Assert.False(caps.SupportsTracing);
            Assert.False(caps.SupportsLogging);
            Assert.False(caps.SupportsDistributedTracing);
            Assert.False(caps.SupportsAlerting);
            Assert.Empty(caps.SupportedExporters);
            Assert.False(caps.HasAnyCapability);
            Assert.False(caps.HasFullObservability);
        }

        [Fact]
        public void ObservabilityCapabilities_Full_HasAllFeaturesEnabled()
        {
            var caps = ObservabilityCapabilities.Full("OpenTelemetry", "Prometheus");

            Assert.True(caps.SupportsMetrics);
            Assert.True(caps.SupportsTracing);
            Assert.True(caps.SupportsLogging);
            Assert.True(caps.SupportsDistributedTracing);
            Assert.True(caps.SupportsAlerting);
            Assert.Equal(2, caps.SupportedExporters.Count);
            Assert.True(caps.HasAnyCapability);
            Assert.True(caps.HasFullObservability);
        }

        [Fact]
        public void ObservabilityCapabilities_SupportsExporter_IsCaseInsensitive()
        {
            var caps = ObservabilityCapabilities.Full("Prometheus", "Jaeger");

            Assert.True(caps.SupportsExporter("prometheus"));
            Assert.True(caps.SupportsExporter("PROMETHEUS"));
            Assert.True(caps.SupportsExporter("Jaeger"));
            Assert.False(caps.SupportsExporter("Zipkin"));
        }

        [Fact]
        public void ObservabilityCapabilities_HasAnyCapability_TrueWithPartialFeatures()
        {
            var caps = new ObservabilityCapabilities(
                SupportsMetrics: true,
                SupportsTracing: false,
                SupportsLogging: false,
                SupportsDistributedTracing: false,
                SupportsAlerting: false,
                SupportedExporters: Array.Empty<string>());

            Assert.True(caps.HasAnyCapability);
            Assert.False(caps.HasFullObservability);
        }

        #endregion

        #region LogEntry and LogLevel Tests

        [Fact]
        public void LogLevel_Has7Levels()
        {
            var values = Enum.GetValues<LogLevel>();
            Assert.Equal(7, values.Length);
        }

        [Fact]
        public void LogEntry_Construction_SetsProperties()
        {
            var timestamp = DateTimeOffset.UtcNow;
            var props = new Dictionary<string, object> { ["correlationId"] = "abc123" };
            var entry = new LogEntry(timestamp, LogLevel.Warning, "Slow query detected", props);

            Assert.Equal(timestamp, entry.Timestamp);
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.Equal("Slow query detected", entry.Message);
            Assert.NotNull(entry.Properties);
        }

        #endregion

        #region HealthCheckResult Tests

        [Fact]
        public void HealthCheckResult_Construction_SetsProperties()
        {
            var result = new HealthCheckResult(true, "All systems operational");

            Assert.True(result.IsHealthy);
            Assert.Equal("All systems operational", result.Description);
            Assert.Null(result.Data);
        }

        [Fact]
        public void HealthCheckResult_WithData_SetsProperties()
        {
            var data = new Dictionary<string, object> { ["latencyMs"] = 15.0 };
            var result = new HealthCheckResult(false, "High latency", data);

            Assert.False(result.IsHealthy);
            Assert.NotNull(result.Data);
            Assert.Equal(15.0, result.Data!["latencyMs"]);
        }

        #endregion
    }
}
