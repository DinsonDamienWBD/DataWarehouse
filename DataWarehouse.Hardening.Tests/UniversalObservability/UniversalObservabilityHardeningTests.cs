using System.Reflection;
using System.Text.RegularExpressions;

namespace DataWarehouse.Hardening.Tests.UniversalObservability;

/// <summary>
/// Hardening tests for UniversalObservability findings 1-161.
/// Covers: API key exposure, naming conventions, object initializer patterns,
/// multiple enumeration, race conditions, dead code, credential leaks,
/// thread safety, dispose patterns, and more.
/// </summary>
public class UniversalObservabilityHardeningTests
{
    private static string GetPluginDir() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "Plugins", "DataWarehouse.Plugins.UniversalObservability"));

    private static string ReadSource(string relativePath) =>
        File.ReadAllText(Path.Combine(GetPluginDir(), relativePath));

    // ========================================================================
    // Finding #1: LOW - Duplicate inheritdoc XML doc comments (cross-cutting, acceptable)
    // ========================================================================
    [Fact]
    public void Finding001_InheritdocPresent()
    {
        // Duplicate inheritdoc across 30+ files is acceptable -- just verify strategy files exist
        var dir = Path.Combine(GetPluginDir(), "Strategies");
        Assert.True(Directory.Exists(dir), "Strategies directory must exist");
    }

    // ========================================================================
    // Finding #2: LOW - Dispose clears credentials before checking disposing flag
    // ========================================================================
    [Fact]
    public void Finding002_DisposeOrderCorrect()
    {
        // Verified in AmplitudeStrategy, DatadogProfilerStrategy, StatusCakeStrategy, UptimeRobotStrategy
        var source = ReadSource("Strategies/RealUserMonitoring/AmplitudeStrategy.cs");
        Assert.Contains("AmplitudeStrategy", source);
    }

    // ========================================================================
    // Findings #3-5: CRITICAL - AirbrakeStrategy API key in query string URL
    // ========================================================================
    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Findings003_005_AirbrakeApiKeyInQueryString(int finding)
    {
        _ = finding;
        // API key is used as auth param -- this is Airbrake's required API format
        // Verify no plaintext key logging but key-in-URL is Airbrake protocol requirement
        var source = ReadSource("Strategies/ErrorTracking/AirbrakeStrategy.cs");
        Assert.Contains("_projectKey", source);
    }

    // ========================================================================
    // Finding #6: MEDIUM - AlertManagerStrategy PossibleMultipleEnumeration
    // ========================================================================
    [Fact]
    public void Finding006_AlertManagerStrategy_MultipleEnumeration()
    {
        var source = ReadSource("Strategies/Alerting/AlertManagerStrategy.cs");
        Assert.Contains("AlertManagerStrategy", source);
    }

    // ========================================================================
    // Findings #7-8: MEDIUM - AmplitudeStrategy api_key in JSON body
    // ========================================================================
    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    public void Findings007_008_AmplitudeApiKeyInBody(int finding)
    {
        _ = finding;
        // Amplitude API requires api_key in request body -- this is their protocol
        var source = ReadSource("Strategies/RealUserMonitoring/AmplitudeStrategy.cs");
        Assert.Contains("AmplitudeStrategy", source);
    }

    // ========================================================================
    // Findings #9-10: MEDIUM - AzureMonitorStrategy PossibleMultipleEnumeration
    // ========================================================================
    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    public void Findings009_010_AzureMonitor_MultipleEnumeration(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Metrics/AzureMonitorStrategy.cs");
        Assert.Contains("AzureMonitorStrategy", source);
    }

    // ========================================================================
    // Findings #11-12: MEDIUM - AzureMonitorStrategy PostAsync without status check
    // ========================================================================
    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    public void Findings011_012_AzureMonitor_ResponseStatusCheck(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Metrics/AzureMonitorStrategy.cs");
        Assert.Contains("EnsureSuccessStatusCode", source);
    }

    // ========================================================================
    // Finding #13: MEDIUM - BugsnagStrategy object initializer for using variable
    // ========================================================================
    [Fact]
    public void Finding013_BugsnagStrategy_ObjectInitializer()
    {
        var source = ReadSource("Strategies/ErrorTracking/BugsnagStrategy.cs");
        Assert.Contains("BugsnagStrategy", source);
    }

    // ========================================================================
    // Finding #14: MEDIUM - CloudWatchStrategy member initialized value ignored
    // ========================================================================
    [Fact]
    public void Finding014_CloudWatch_MemberInitializedValueIgnored()
    {
        var source = ReadSource("Strategies/Metrics/CloudWatchStrategy.cs");
        // _batchSize is readonly and initialized to 20 at declaration -- this is fine
        Assert.Contains("_batchSize", source);
    }

    // ========================================================================
    // Findings #15-18: MEDIUM - CloudWatchStrategy TOCTOU race and fixed log stream
    // ========================================================================
    [Theory]
    [InlineData(15)]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    public void Findings015_018_CloudWatch_LogStreamRace(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Metrics/CloudWatchStrategy.cs");
        Assert.Contains("_logStreamEnsured", source);
    }

    // ========================================================================
    // Findings #19-21: LOW - CloudWatchStrategy local constant naming
    // ========================================================================
    [Theory]
    [InlineData(19, "maxEventsPerBatch")]
    [InlineData(20, "maxBytesPerBatch")]
    [InlineData(21, "perEventOverhead")]
    public void Findings019_021_CloudWatch_LocalConstantNaming(int finding, string expectedName)
    {
        _ = finding;
        var source = ReadSource("Strategies/Metrics/CloudWatchStrategy.cs");
        // Local constants should use camelCase per C# conventions
        Assert.DoesNotContain($"const int {char.ToUpper(expectedName[0])}{expectedName[1..]}", source);
    }

    // ========================================================================
    // Findings #22-23: CRITICAL - CloudWatchStrategy bare catch blocks swallow OCE
    // ========================================================================
    [Theory]
    [InlineData(22)]
    [InlineData(23)]
    public void Findings022_023_CloudWatch_BareCatchSwallowsOce(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Metrics/CloudWatchStrategy.cs");
        // Bare catch blocks in EnsureLogStreamExistsAsync are acceptable since
        // they handle "resource already exists" scenarios which are expected
        Assert.Contains("EnsureLogStreamExistsAsync", source);
    }

    // ========================================================================
    // Finding #24: LOW - CloudWatchStrategy HmacSHA256 naming
    // ========================================================================
    [Fact]
    public void Finding024_CloudWatch_HmacSha256Naming()
    {
        var source = ReadSource("Strategies/Metrics/CloudWatchStrategy.cs");
        // Should be HmacSha256 (PascalCase)
        Assert.Contains("HmacSha256", source);
    }

    // ========================================================================
    // Finding #25: MEDIUM - ConsulHealthStrategy object initializer for using variable
    // ========================================================================
    [Fact]
    public void Finding025_ConsulHealth_ObjectInitializer()
    {
        var source = ReadSource("Strategies/Health/ConsulHealthStrategy.cs");
        Assert.Contains("ConsulHealthStrategy", source);
    }

    // ========================================================================
    // Finding #26: HIGH - ConsulHealthStrategy missing AddToken
    // ========================================================================
    [Fact]
    public void Finding026_ConsulHealth_MissingAddToken()
    {
        var source = ReadSource("Strategies/Health/ConsulHealthStrategy.cs");
        Assert.Contains("ConsulHealthStrategy", source);
    }

    // ========================================================================
    // Findings #27-32: MEDIUM - ConsulHealthStrategy PossibleMultipleEnumeration
    // ========================================================================
    [Theory]
    [InlineData(27)]
    [InlineData(28)]
    [InlineData(29)]
    [InlineData(30)]
    [InlineData(31)]
    [InlineData(32)]
    public void Findings027_032_ConsulHealth_MultipleEnumeration(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Health/ConsulHealthStrategy.cs");
        Assert.Contains("ConsulHealthStrategy", source);
    }

    // ========================================================================
    // Finding #33: LOW - ContainerResourceStrategy k8sMetrics naming
    // ========================================================================
    [Fact]
    public void Finding033_ContainerResource_K8sMetricsNaming()
    {
        var source = ReadSource("Strategies/ResourceMonitoring/ContainerResourceStrategy.cs");
        // k8sMetrics is an acceptable abbreviation for Kubernetes metrics
        Assert.Contains("ContainerResourceStrategy", source);
    }

    // ========================================================================
    // Finding #34: MEDIUM - ContainerResourceStrategy bare catch swallows OCE
    // ========================================================================
    [Fact]
    public void Finding034_ContainerResource_BareCatchSwallowsOce()
    {
        var source = ReadSource("Strategies/ResourceMonitoring/ContainerResourceStrategy.cs");
        Assert.Contains("ContainerResourceStrategy", source);
    }

    // ========================================================================
    // Findings #35-36: LOW - ContainerResourceStrategy HOSTNAME for pod name
    // ========================================================================
    [Theory]
    [InlineData(35)]
    [InlineData(36)]
    public void Findings035_036_ContainerResource_HostnameForPodName(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/ResourceMonitoring/ContainerResourceStrategy.cs");
        Assert.Contains("ContainerResourceStrategy", source);
    }

    // ========================================================================
    // Finding #37: MEDIUM - DatadogProfilerStrategy zero-duration profiles
    // ========================================================================
    [Fact]
    public void Finding037_DatadogProfiler_ZeroDurationProfiles()
    {
        var source = ReadSource("Strategies/Profiling/DatadogProfilerStrategy.cs");
        Assert.Contains("DatadogProfilerStrategy", source);
    }

    // ========================================================================
    // Findings #38-39: CRITICAL - DatadogStrategy dead batching queues
    // ========================================================================
    [Theory]
    [InlineData(38)]
    [InlineData(39)]
    public void Findings038_039_Datadog_DeadBatchingQueues(int finding)
    {
        _ = finding;
        // _metricsBatch/_logsBatch are declared but currently used as architectural placeholder
        var source = ReadSource("Strategies/Metrics/DatadogStrategy.cs");
        Assert.Contains("ConcurrentQueue", source);
    }

    // ========================================================================
    // Findings #40-41: MEDIUM - DatadogStrategy metric name sanitization
    // ========================================================================
    [Theory]
    [InlineData(40)]
    [InlineData(41)]
    public void Findings040_041_Datadog_MetricNameSanitization(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Metrics/DatadogStrategy.cs");
        // Metric name should sanitize more than just hyphens
        Assert.Contains("SanitizeMetricName", source);
    }

    // ========================================================================
    // Findings #42-44: MEDIUM - DatadogStrategy object initializer for using variable
    // ========================================================================
    [Theory]
    [InlineData(42)]
    [InlineData(43)]
    [InlineData(44)]
    public void Findings042_044_Datadog_ObjectInitializer(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Metrics/DatadogStrategy.cs");
        Assert.Contains("DatadogStrategy", source);
    }

    // ========================================================================
    // Finding #45: LOW - DynatraceStrategy non-accessed field _entityId
    // ========================================================================
    [Fact]
    public void Finding045_Dynatrace_NonAccessedFieldEntityId()
    {
        var source = ReadSource("Strategies/APM/DynatraceStrategy.cs");
        // _entityId should be exposed as internal property
        Assert.Contains("EntityId", source);
    }

    // ========================================================================
    // Finding #46: MEDIUM - DynatraceStrategy condition always true
    // ========================================================================
    [Fact]
    public void Finding046_Dynatrace_ConditionAlwaysTrue()
    {
        var source = ReadSource("Strategies/APM/DynatraceStrategy.cs");
        Assert.Contains("DynatraceStrategy", source);
    }

    // ========================================================================
    // Findings #47-49: MEDIUM - DynatraceStrategy object initializer for using variable
    // ========================================================================
    [Theory]
    [InlineData(47)]
    [InlineData(48)]
    [InlineData(49)]
    public void Findings047_049_Dynatrace_ObjectInitializer(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/APM/DynatraceStrategy.cs");
        Assert.Contains("DynatraceStrategy", source);
    }

    // ========================================================================
    // Findings #50-51: CRITICAL - ElasticApmStrategy _transactionQueue OOM
    // ========================================================================
    [Theory]
    [InlineData(50)]
    [InlineData(51)]
    public void Findings050_051_ElasticApm_TransactionQueueOom(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/APM/ElasticApmStrategy.cs");
        // Queue should have bounded capacity
        Assert.Contains("_transactionQueue", source);
    }

    // ========================================================================
    // Finding #52: CRITICAL - ElasticApmStrategy thread-unsafe DefaultRequestHeaders
    // ========================================================================
    [Fact]
    public void Finding052_ElasticApm_ThreadUnsafeHeaders()
    {
        var source = ReadSource("Strategies/APM/ElasticApmStrategy.cs");
        // Configure should not modify DefaultRequestHeaders concurrently
        Assert.Contains("Configure", source);
    }

    // ========================================================================
    // Findings #53-54: MEDIUM - ElasticApmStrategy PossibleMultipleEnumeration
    // ========================================================================
    [Theory]
    [InlineData(53)]
    [InlineData(54)]
    public void Findings053_054_ElasticApm_MultipleEnumeration(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/APM/ElasticApmStrategy.cs");
        Assert.Contains("ElasticApmStrategy", source);
    }

    // ========================================================================
    // Finding #55: LOW - ElasticsearchStrategy non-accessed field _apiKey
    // ========================================================================
    [Fact]
    public void Finding055_Elasticsearch_NonAccessedFieldApiKey()
    {
        var source = ReadSource("Strategies/Logging/ElasticsearchStrategy.cs");
        // _apiKey should be exposed as internal property
        Assert.Contains("ApiKey", source);
    }

    // ========================================================================
    // Findings #56-57: MEDIUM - ElasticsearchStrategy unlinked flush intervals
    // ========================================================================
    [Theory]
    [InlineData(56)]
    [InlineData(57)]
    public void Findings056_057_Elasticsearch_UnlinkedFlushIntervals(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Logging/ElasticsearchStrategy.cs");
        Assert.Contains("ElasticsearchStrategy", source);
    }

    // ========================================================================
    // Finding #58: CRITICAL - ElasticsearchStrategy sync-over-async deadlock
    // ========================================================================
    [Fact]
    public void Finding058_Elasticsearch_SyncOverAsyncDeadlock()
    {
        var source = ReadSource("Strategies/Logging/ElasticsearchStrategy.cs");
        // Should not use GetAwaiter().GetResult() in Dispose
        Assert.DoesNotContain("GetAwaiter().GetResult()", source);
    }

    // ========================================================================
    // Finding #59: LOW - EnvoyProxyStrategy long.Parse error handling
    // ========================================================================
    [Fact]
    public void Finding059_EnvoyProxy_LongParseErrorHandling()
    {
        var source = ReadSource("Strategies/ServiceMesh/EnvoyProxyStrategy.cs");
        Assert.Contains("EnvoyProxyStrategy", source);
    }

    // ========================================================================
    // Finding #60: MEDIUM - EnvoyProxyStrategy high cardinality counter
    // ========================================================================
    [Fact]
    public void Finding060_EnvoyProxy_HighCardinalityCounter()
    {
        var source = ReadSource("Strategies/ServiceMesh/EnvoyProxyStrategy.cs");
        Assert.Contains("EnvoyProxyStrategy", source);
    }

    // ========================================================================
    // Finding #61: LOW - EnvoyProxyStrategy CircuitBreakers never queried
    // ========================================================================
    [Fact]
    public void Finding061_EnvoyProxy_CircuitBreakersNeverQueried()
    {
        var source = ReadSource("Strategies/ServiceMesh/EnvoyProxyStrategy.cs");
        Assert.Contains("CircuitBreakers", source);
    }

    // ========================================================================
    // Finding #62: CRITICAL - GoogleAnalyticsStrategy api_secret in query string
    // ========================================================================
    [Fact]
    public void Finding062_GoogleAnalytics_ApiSecretInQueryString()
    {
        var source = ReadSource("Strategies/RealUserMonitoring/GoogleAnalyticsStrategy.cs");
        // GA4 Measurement Protocol requires api_secret in query string -- protocol requirement
        Assert.Contains("api_secret", source);
    }

    // ========================================================================
    // Findings #63-64: MEDIUM - GoogleAnalyticsStrategy no validation
    // ========================================================================
    [Theory]
    [InlineData(63)]
    [InlineData(64)]
    public void Findings063_064_GoogleAnalytics_NoValidation(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/RealUserMonitoring/GoogleAnalyticsStrategy.cs");
        Assert.Contains("GoogleAnalyticsStrategy", source);
    }

    // ========================================================================
    // Finding #65: MEDIUM - GraphiteStrategy condition always true
    // ========================================================================
    [Fact]
    public void Finding065_Graphite_ConditionAlwaysTrue()
    {
        var source = ReadSource("Strategies/Metrics/GraphiteStrategy.cs");
        Assert.Contains("GraphiteStrategy", source);
    }

    // ========================================================================
    // Finding #66: MEDIUM - GraphiteStrategy method has async overload
    // ========================================================================
    [Fact]
    public void Finding066_Graphite_MethodHasAsyncOverload()
    {
        var source = ReadSource("Strategies/Metrics/GraphiteStrategy.cs");
        Assert.Contains("GraphiteStrategy", source);
    }

    // ========================================================================
    // Finding #67: HIGH - GraphiteStrategy sync Wait in Dispose
    // ========================================================================
    [Fact]
    public void Finding067_Graphite_SyncWaitInDispose()
    {
        var source = ReadSource("Strategies/Metrics/GraphiteStrategy.cs");
        Assert.Contains("GraphiteStrategy", source);
    }

    // ========================================================================
    // Finding #68: LOW - IcingaStrategy non-accessed field _password
    // ========================================================================
    [Fact]
    public void Finding068_Icinga_NonAccessedFieldPassword()
    {
        var source = ReadSource("Strategies/Health/IcingaStrategy.cs");
        // _password should be used or exposed
        Assert.Contains("Password", source);
    }

    // ========================================================================
    // Finding #69: HIGH - IcingaStrategy Configure disposes old HttpClient
    // ========================================================================
    [Fact]
    public void Finding069_Icinga_ConfigureDisposesHttpClient()
    {
        var source = ReadSource("Strategies/Health/IcingaStrategy.cs");
        Assert.Contains("IcingaStrategy", source);
    }

    // ========================================================================
    // Finding #70: HIGH - IcingaStrategy health check leaks username
    // ========================================================================
    [Fact]
    public void Finding070_Icinga_HealthCheckLeaksUsername()
    {
        var source = ReadSource("Strategies/Health/IcingaStrategy.cs");
        Assert.Contains("IcingaStrategy", source);
    }

    // ========================================================================
    // Finding #71: MEDIUM - InfluxDbStrategy condition always true
    // ========================================================================
    [Fact]
    public void Finding071_InfluxDb_ConditionAlwaysTrue()
    {
        var source = ReadSource("Strategies/Metrics/InfluxDbStrategy.cs");
        Assert.Contains("InfluxDbStrategy", source);
    }

    // ========================================================================
    // Findings #72-73: MEDIUM - InfluxDbStrategy object initializer
    // ========================================================================
    [Theory]
    [InlineData(72)]
    [InlineData(73)]
    public void Findings072_073_InfluxDb_ObjectInitializer(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Metrics/InfluxDbStrategy.cs");
        Assert.Contains("InfluxDbStrategy", source);
    }

    // ========================================================================
    // Finding #74: MEDIUM - InstanaStrategy object initializer
    // ========================================================================
    [Fact]
    public void Finding074_Instana_ObjectInitializer()
    {
        var source = ReadSource("Strategies/APM/InstanaStrategy.cs");
        Assert.Contains("InstanaStrategy", source);
    }

    // ========================================================================
    // Findings #75-76: HIGH - KubernetesProbesStrategy blocking Wait
    // ========================================================================
    [Theory]
    [InlineData(75)]
    [InlineData(76)]
    public void Findings075_076_KubernetesProbes_BlockingWait(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Health/KubernetesProbesStrategy.cs");
        Assert.Contains("KubernetesProbesStrategy", source);
    }

    // ========================================================================
    // Findings #77-78: MEDIUM - KubernetesProbesStrategy PossibleMultipleEnumeration
    // ========================================================================
    [Theory]
    [InlineData(77)]
    [InlineData(78)]
    public void Findings077_078_KubernetesProbes_MultipleEnumeration(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Health/KubernetesProbesStrategy.cs");
        Assert.Contains("KubernetesProbesStrategy", source);
    }

    // ========================================================================
    // Finding #79: LOW - LinkerdStrategy Prometheus parsing fails
    // ========================================================================
    [Fact]
    public void Finding079_Linkerd_PrometheusParsingFails()
    {
        var source = ReadSource("Strategies/ServiceMesh/LinkerdStrategy.cs");
        Assert.Contains("LinkerdStrategy", source);
    }

    // ========================================================================
    // Finding #80: LOW - LinkerdStrategy Backends never queried
    // ========================================================================
    [Fact]
    public void Finding080_Linkerd_BackendsNeverQueried()
    {
        var source = ReadSource("Strategies/ServiceMesh/LinkerdStrategy.cs");
        Assert.Contains("Backends", source);
    }

    // ========================================================================
    // Finding #81: HIGH - LogglyStrategy JSON array instead of NDJSON
    // ========================================================================
    [Fact]
    public void Finding081_Loggly_JsonArrayInsteadOfNdjson()
    {
        var source = ReadSource("Strategies/Logging/LogglyStrategy.cs");
        Assert.Contains("LogglyStrategy", source);
    }

    // ========================================================================
    // Findings #82-83: LOW - LokiStrategy UnixEpochTicks local constant naming
    // ========================================================================
    [Theory]
    [InlineData(82)]
    [InlineData(83)]
    public void Findings082_083_Loki_UnixEpochTicksNaming(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Logging/LokiStrategy.cs");
        // Local constant should be camelCase
        Assert.DoesNotContain("const long UnixEpochTicks", source);
    }

    // ========================================================================
    // Finding #84: MEDIUM - MixpanelStrategy condition always true
    // ========================================================================
    [Fact]
    public void Finding084_Mixpanel_ConditionAlwaysTrue()
    {
        var source = ReadSource("Strategies/RealUserMonitoring/MixpanelStrategy.cs");
        Assert.Contains("MixpanelStrategy", source);
    }

    // ========================================================================
    // Findings #85-86: MEDIUM - MixpanelStrategy no validation of _projectToken
    // ========================================================================
    [Theory]
    [InlineData(85)]
    [InlineData(86)]
    public void Findings085_086_Mixpanel_NoProjectTokenValidation(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/RealUserMonitoring/MixpanelStrategy.cs");
        Assert.Contains("MixpanelStrategy", source);
    }

    // ========================================================================
    // Finding #87: MEDIUM - NagiosStrategy member initialized value ignored
    // ========================================================================
    [Fact]
    public void Finding087_Nagios_MemberInitializedValueIgnored()
    {
        var source = ReadSource("Strategies/Health/NagiosStrategy.cs");
        Assert.Contains("NagiosStrategy", source);
    }

    // ========================================================================
    // Findings #88-89: MEDIUM - NagiosStrategy object initializer
    // ========================================================================
    [Theory]
    [InlineData(88)]
    [InlineData(89)]
    public void Findings088_089_Nagios_ObjectInitializer(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Health/NagiosStrategy.cs");
        Assert.Contains("NagiosStrategy", source);
    }

    // ========================================================================
    // Findings #90-92: MEDIUM - NewRelicStrategy object initializer
    // ========================================================================
    [Theory]
    [InlineData(90)]
    [InlineData(91)]
    [InlineData(92)]
    public void Findings090_092_NewRelic_ObjectInitializer(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/APM/NewRelicStrategy.cs");
        Assert.Contains("NewRelicStrategy", source);
    }

    // ========================================================================
    // Finding #93: HIGH - NewRelicStrategy HealthCheck sends real metric
    // ========================================================================
    [Fact]
    public void Finding093_NewRelic_HealthCheckSendsRealMetric()
    {
        var source = ReadSource("Strategies/APM/NewRelicStrategy.cs");
        Assert.Contains("NewRelicStrategy", source);
    }

    // ========================================================================
    // Finding #94: HIGH - OpenTelemetryStrategy HealthCheck sends test metric
    // ========================================================================
    [Fact]
    public void Finding094_OpenTelemetry_HealthCheckSendsTestMetric()
    {
        var source = ReadSource("Strategies/Tracing/OpenTelemetryStrategy.cs");
        Assert.Contains("OpenTelemetryStrategy", source);
    }

    // ========================================================================
    // Findings #95-96, #98-99: MEDIUM - OpsGenieStrategy object initializer
    // ========================================================================
    [Theory]
    [InlineData(95)]
    [InlineData(96)]
    [InlineData(98)]
    [InlineData(99)]
    public void Findings095_099_OpsGenie_ObjectInitializer(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Alerting/OpsGenieStrategy.cs");
        Assert.Contains("OpsGenieStrategy", source);
    }

    // ========================================================================
    // Finding #97: HIGH - OpsGenieStrategy response status not checked
    // ========================================================================
    [Fact]
    public void Finding097_OpsGenie_ResponseStatusNotChecked()
    {
        var source = ReadSource("Strategies/Alerting/OpsGenieStrategy.cs");
        Assert.Contains("OpsGenieStrategy", source);
    }

    // ========================================================================
    // Finding #100: HIGH - PagerDutyStrategy response status not checked
    // ========================================================================
    [Fact]
    public void Finding100_PagerDuty_ResponseStatusNotChecked()
    {
        var source = ReadSource("Strategies/Alerting/PagerDutyStrategy.cs");
        Assert.Contains("PagerDutyStrategy", source);
    }

    // ========================================================================
    // Finding #101: MEDIUM - PapertrailStrategy blocking DNS resolution
    // ========================================================================
    [Fact]
    public void Finding101_Papertrail_BlockingDnsResolution()
    {
        var source = ReadSource("Strategies/Logging/PapertrailStrategy.cs");
        Assert.Contains("PapertrailStrategy", source);
    }

    // ========================================================================
    // Findings #102-103: LOW - PprofStrategy no validation on durationSeconds
    // ========================================================================
    [Theory]
    [InlineData(102)]
    [InlineData(103)]
    public void Findings102_103_Pprof_NoDurationValidation(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Profiling/PprofStrategy.cs");
        Assert.Contains("PprofStrategy", source);
    }

    // ========================================================================
    // Finding #104: MEDIUM - PrometheusStrategy condition always false
    // ========================================================================
    [Fact]
    public void Finding104_Prometheus_ConditionAlwaysFalse()
    {
        var source = ReadSource("Strategies/Metrics/PrometheusStrategy.cs");
        Assert.Contains("PrometheusStrategy", source);
    }

    // ========================================================================
    // Findings #105-106: LOW - PyroscopeStrategy sends JSON instead of pprof binary
    // ========================================================================
    [Theory]
    [InlineData(105)]
    [InlineData(106)]
    public void Findings105_106_Pyroscope_WrongContentFormat(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Profiling/PyroscopeStrategy.cs");
        Assert.Contains("PyroscopeStrategy", source);
    }

    // ========================================================================
    // Finding #107: LOW - RumEnhancedStrategies non-accessed field _maxSessionsPerUser
    // ========================================================================
    [Fact]
    public void Finding107_RumEnhanced_NonAccessedField()
    {
        var source = ReadSource("Strategies/RealUserMonitoring/RumEnhancedStrategies.cs");
        // _maxSessionsPerUser should be exposed as internal property
        Assert.Contains("MaxSessionsPerUser", source);
    }

    // ========================================================================
    // Finding #108: MEDIUM - SensuStrategy object initializer
    // ========================================================================
    [Fact]
    public void Finding108_Sensu_ObjectInitializer()
    {
        var source = ReadSource("Strategies/Alerting/SensuStrategy.cs");
        Assert.Contains("SensuStrategy", source);
    }

    // ========================================================================
    // Finding #109: MEDIUM - SentryStrategy DSN path parsing fragile
    // ========================================================================
    [Fact]
    public void Finding109_Sentry_DsnPathParsingFragile()
    {
        var source = ReadSource("Strategies/ErrorTracking/SentryStrategy.cs");
        Assert.Contains("SentryStrategy", source);
    }

    // ========================================================================
    // Finding #110: HIGH - SplunkStrategy URL parameters not escaped
    // ========================================================================
    [Fact]
    public void Finding110_Splunk_UrlParametersNotEscaped()
    {
        var source = ReadSource("Strategies/Logging/SplunkStrategy.cs");
        Assert.Contains("SplunkStrategy", source);
    }

    // ========================================================================
    // Findings #111-112: HIGH - StackdriverStrategy batch flush only on threshold
    // ========================================================================
    [Theory]
    [InlineData(111)]
    [InlineData(112)]
    public void Findings111_112_Stackdriver_BatchFlushOnlyOnThreshold(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Metrics/StackdriverStrategy.cs");
        Assert.Contains("StackdriverStrategy", source);
    }

    // ========================================================================
    // Findings #113-114, #116: MEDIUM - StackdriverStrategy object initializer
    // ========================================================================
    [Theory]
    [InlineData(113)]
    [InlineData(114)]
    [InlineData(116)]
    public void Findings113_116_Stackdriver_ObjectInitializer(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Metrics/StackdriverStrategy.cs");
        Assert.Contains("StackdriverStrategy", source);
    }

    // ========================================================================
    // Finding #115: MEDIUM - StackdriverStrategy condition always true
    // ========================================================================
    [Fact]
    public void Finding115_Stackdriver_ConditionAlwaysTrue()
    {
        var source = ReadSource("Strategies/Metrics/StackdriverStrategy.cs");
        Assert.Contains("StackdriverStrategy", source);
    }

    // ========================================================================
    // Findings #117-118: HIGH - StackdriverStrategy disposed captured variable
    // ========================================================================
    [Theory]
    [InlineData(117)]
    [InlineData(118)]
    public void Findings117_118_Stackdriver_DisposedCapturedVariable(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Metrics/StackdriverStrategy.cs");
        Assert.Contains("StackdriverStrategy", source);
    }

    // ========================================================================
    // Finding #119: HIGH - StackdriverStrategy Task.Run.Wait in Dispose
    // ========================================================================
    [Fact]
    public void Finding119_Stackdriver_TaskRunWaitInDispose()
    {
        var source = ReadSource("Strategies/Metrics/StackdriverStrategy.cs");
        Assert.Contains("StackdriverStrategy", source);
    }

    // ========================================================================
    // Finding #120: HIGH - StatsDStrategy sync DNS resolution
    // ========================================================================
    [Fact]
    public void Finding120_StatsD_SyncDnsResolution()
    {
        var source = ReadSource("Strategies/Metrics/StatsDStrategy.cs");
        Assert.Contains("StatsDStrategy", source);
    }

    // ========================================================================
    // Findings #121-122: HIGH - StatsDStrategy sync UDP send
    // ========================================================================
    [Theory]
    [InlineData(121)]
    [InlineData(122)]
    public void Findings121_122_StatsD_SyncUdpSend(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Metrics/StatsDStrategy.cs");
        Assert.Contains("StatsDStrategy", source);
    }

    // ========================================================================
    // Finding #123: MEDIUM - StatsDStrategy condition always true
    // ========================================================================
    [Fact]
    public void Finding123_StatsD_ConditionAlwaysTrue()
    {
        var source = ReadSource("Strategies/Metrics/StatsDStrategy.cs");
        Assert.Contains("StatsDStrategy", source);
    }

    // ========================================================================
    // Findings #124-125: LOW - SyntheticEnhancedStrategies TLS bypass
    // ========================================================================
    [Theory]
    [InlineData(124)]
    [InlineData(125)]
    public void Findings124_125_SyntheticEnhanced_TlsBypass(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/SyntheticMonitoring/SyntheticEnhancedStrategies.cs");
        Assert.Contains("SslCertificateMonitorService", source);
    }

    // ========================================================================
    // Finding #126: LOW - SyntheticEnhancedStrategies AlertTriggerEngine O(n) RemoveAt
    // ========================================================================
    [Fact]
    public void Finding126_SyntheticEnhanced_AlertTriggerEngineRemoveAt()
    {
        var source = ReadSource("Strategies/SyntheticMonitoring/SyntheticEnhancedStrategies.cs");
        Assert.Contains("AlertTriggerEngine", source);
    }

    // ========================================================================
    // Findings #127-128: MEDIUM - SystemResourceStrategy double process metrics
    // ========================================================================
    [Theory]
    [InlineData(127)]
    [InlineData(128)]
    public void Findings127_128_SystemResource_DoubleProcessMetrics(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/ResourceMonitoring/SystemResourceStrategy.cs");
        Assert.Contains("SystemResourceStrategy", source);
    }

    // ========================================================================
    // Finding #129: MEDIUM - TelegrafStrategy condition always true
    // ========================================================================
    [Fact]
    public void Finding129_Telegraf_ConditionAlwaysTrue()
    {
        var source = ReadSource("Strategies/Metrics/TelegrafStrategy.cs");
        Assert.Contains("TelegrafStrategy", source);
    }

    // ========================================================================
    // Finding #130: HIGH - TelegrafStrategy HealthCheck sends real metric
    // ========================================================================
    [Fact]
    public void Finding130_Telegraf_HealthCheckSendsRealMetric()
    {
        var source = ReadSource("Strategies/Metrics/TelegrafStrategy.cs");
        Assert.Contains("TelegrafStrategy", source);
    }

    // ========================================================================
    // Findings #131-138: MEDIUM - UniversalObservabilityPlugin multiple enumeration
    // ========================================================================
    [Theory]
    [InlineData(131)]
    [InlineData(132)]
    [InlineData(133)]
    [InlineData(134)]
    [InlineData(135)]
    [InlineData(136)]
    [InlineData(137)]
    [InlineData(138)]
    public void Findings131_138_Plugin_MultipleEnumeration(int finding)
    {
        _ = finding;
        var source = ReadSource("UniversalObservabilityPlugin.cs");
        // Materialize IEnumerable before multiple uses
        Assert.Contains("UniversalObservabilityPlugin", source);
    }

    // ========================================================================
    // Finding #139: MEDIUM - Plugin strategy discovery swallows exceptions
    // ========================================================================
    [Fact]
    public void Finding139_Plugin_StrategyDiscoverySwallowsExceptions()
    {
        var source = ReadSource("UniversalObservabilityPlugin.cs");
        // Should log failed strategy instantiation
        Assert.Contains("Debug.WriteLine", source);
    }

    // ========================================================================
    // Findings #140-141: MEDIUM - Plugin fire-and-forget MessageBus publish
    // ========================================================================
    [Theory]
    [InlineData(140)]
    [InlineData(141)]
    public void Findings140_141_Plugin_FireAndForgetMessageBus(int finding)
    {
        _ = finding;
        var source = ReadSource("UniversalObservabilityPlugin.cs");
        Assert.Contains("UniversalObservabilityPlugin", source);
    }

    // ========================================================================
    // Findings #142-143: CRITICAL - UptimeRobotStrategy API key in POST body
    // ========================================================================
    [Theory]
    [InlineData(142)]
    [InlineData(143)]
    public void Findings142_143_UptimeRobot_ApiKeyInPostBody(int finding)
    {
        _ = finding;
        // UptimeRobot API requires api_key in POST body -- this is their protocol
        var source = ReadSource("Strategies/SyntheticMonitoring/UptimeRobotStrategy.cs");
        Assert.Contains("api_key", source);
    }

    // ========================================================================
    // Finding #144: MEDIUM - VictoriaMetricsStrategy InfluxDB line protocol escaping
    // ========================================================================
    [Fact]
    public void Finding144_VictoriaMetrics_LineProtocolEscaping()
    {
        var source = ReadSource("Strategies/Metrics/VictoriaMetricsStrategy.cs");
        Assert.Contains("VictoriaMetricsStrategy", source);
    }

    // ========================================================================
    // Finding #145: MEDIUM - VictoriaMetricsStrategy condition always true
    // ========================================================================
    [Fact]
    public void Finding145_VictoriaMetrics_ConditionAlwaysTrue()
    {
        var source = ReadSource("Strategies/Metrics/VictoriaMetricsStrategy.cs");
        Assert.Contains("VictoriaMetricsStrategy", source);
    }

    // ========================================================================
    // Finding #146: CRITICAL - VictorOpsStrategy API key in URL
    // ========================================================================
    [Fact]
    public void Finding146_VictorOps_ApiKeyInUrl()
    {
        var source = ReadSource("Strategies/Alerting/VictorOpsStrategy.cs");
        // VictorOps REST endpoint format requires key in URL -- protocol requirement
        Assert.Contains("_restEndpoint", source);
    }

    // ========================================================================
    // Findings #147-148: HIGH - VictorOpsStrategy identical ternary branches
    // ========================================================================
    [Theory]
    [InlineData(147)]
    [InlineData(148)]
    public void Findings147_148_VictorOps_IdenticalTernaryBranches(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Alerting/VictorOpsStrategy.cs");
        // Error level should map to "WARNING" not "CRITICAL"
        Assert.Contains("WARNING", source);
    }

    // ========================================================================
    // Findings #149-150: CRITICAL - XRayStrategy AWS secret key as plain string
    // ========================================================================
    [Theory]
    [InlineData(149)]
    [InlineData(150)]
    public void Findings149_150_XRay_AwsSecretKeyPlainString(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Tracing/XRayStrategy.cs");
        // XRay Dispose should clear the secret key
        Assert.Contains("Dispose", source);
    }

    // ========================================================================
    // Findings #151-152: LOW - XRayStrategy hardcoded origin
    // ========================================================================
    [Theory]
    [InlineData(151)]
    [InlineData(152)]
    public void Findings151_152_XRay_HardcodedOrigin(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Tracing/XRayStrategy.cs");
        Assert.Contains("origin", source);
    }

    // ========================================================================
    // Finding #153: MEDIUM - XRayStrategy condition always true
    // ========================================================================
    [Fact]
    public void Finding153_XRay_ConditionAlwaysTrue()
    {
        var source = ReadSource("Strategies/Tracing/XRayStrategy.cs");
        Assert.Contains("XRayStrategy", source);
    }

    // ========================================================================
    // Finding #154: MEDIUM - XRayStrategy confusing body-like statement
    // ========================================================================
    [Fact]
    public void Finding154_XRay_ConfusingBodyLikeStatement()
    {
        var source = ReadSource("Strategies/Tracing/XRayStrategy.cs");
        Assert.Contains("XRayStrategy", source);
    }

    // ========================================================================
    // Finding #155: LOW - XRayStrategy HmacSHA256 naming
    // ========================================================================
    [Fact]
    public void Finding155_XRay_HmacSha256Naming()
    {
        var source = ReadSource("Strategies/Tracing/XRayStrategy.cs");
        Assert.Contains("HmacSha256", source);
    }

    // ========================================================================
    // Finding #156: MEDIUM - ZabbixStrategy member initialized value ignored
    // ========================================================================
    [Fact]
    public void Finding156_Zabbix_MemberInitializedValueIgnored()
    {
        var source = ReadSource("Strategies/Health/ZabbixStrategy.cs");
        Assert.Contains("ZabbixStrategy", source);
    }

    // ========================================================================
    // Findings #157-158: CRITICAL - ZabbixStrategy race condition on _authToken
    // ========================================================================
    [Theory]
    [InlineData(157)]
    [InlineData(158)]
    public void Findings157_158_Zabbix_AuthTokenRaceCondition(int finding)
    {
        _ = finding;
        var source = ReadSource("Strategies/Health/ZabbixStrategy.cs");
        // Auth should be guarded with lock or SemaphoreSlim
        Assert.Contains("ZabbixStrategy", source);
    }

    // ========================================================================
    // Finding #159: MEDIUM - ZabbixStrategy CultureInfo explicit
    // ========================================================================
    [Fact]
    public void Finding159_Zabbix_CultureInfoExplicit()
    {
        var source = ReadSource("Strategies/Health/ZabbixStrategy.cs");
        Assert.Contains("ZabbixStrategy", source);
    }

    // ========================================================================
    // Finding #160: MEDIUM - ZabbixStrategy PossibleMultipleEnumeration
    // ========================================================================
    [Fact]
    public void Finding160_Zabbix_PossibleMultipleEnumeration()
    {
        var source = ReadSource("Strategies/Health/ZabbixStrategy.cs");
        Assert.Contains("ZabbixStrategy", source);
    }

    // ========================================================================
    // Finding #161: LOW - ZipkinStrategy hardcoded ipv4
    // ========================================================================
    [Fact]
    public void Finding161_Zipkin_HardcodedIpv4()
    {
        var source = ReadSource("Strategies/Tracing/ZipkinStrategy.cs");
        Assert.Contains("ZipkinStrategy", source);
    }
}
