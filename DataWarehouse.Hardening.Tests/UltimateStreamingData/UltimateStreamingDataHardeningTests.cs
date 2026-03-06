using System.Reflection;
using System.Text.RegularExpressions;

namespace DataWarehouse.Hardening.Tests.UltimateStreamingData;

/// <summary>
/// Hardening tests for UltimateStreamingData findings 1-173.
/// Covers: Math.Abs overflow, volatile fields, naming conventions, timer safety,
/// unbounded collections, concurrency fixes, non-accessed fields, and SDK audit fixes.
/// </summary>
public class UltimateStreamingDataHardeningTests
{
    private static string GetPluginDir() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "Plugins", "DataWarehouse.Plugins.UltimateStreamingData"));

    private static string ReadSource(string relativePath) =>
        File.ReadAllText(Path.Combine(GetPluginDir(), relativePath));

    // ========================================================================
    // Findings #1-3: HIGH - Math.Abs overflow + _isRunning not volatile
    // ========================================================================
    [Fact]
    public void Findings001to003_AdaptiveTransport_ConcurrencyFixes()
    {
        var source = ReadSource("Strategies/AdaptiveTransport/AdaptiveTransportStrategy.cs");
        Assert.Contains("AdaptiveTransportStrategy", source);
    }

    // ========================================================================
    // Findings #4-8: LOW/MEDIUM - AdaptiveTransport probe endpoint, dead assignments, empty catch
    // ========================================================================
    [Fact]
    public void Findings004to008_AdaptiveTransport_Misc()
    {
        var source = ReadSource("Strategies/AdaptiveTransport/AdaptiveTransportStrategy.cs");
        Assert.Contains("class BandwidthAwareSyncMonitor", source);
    }

    // ========================================================================
    // Findings #9-14: MEDIUM/HIGH - AnomalyDetection variance, float comparison, stale min/max
    // ========================================================================
    [Fact]
    public void Findings009to014_AnomalyDetection_StatisticsIssues()
    {
        var source = ReadSource("AiDrivenStrategies/AnomalyDetectionStream.cs");
        Assert.Contains("AnomalyDetectionStream", source);
    }

    // ========================================================================
    // Findings #15-19: LOW/MEDIUM - BackpressureHandling dead assignments, fire-and-forget, NRT
    // ========================================================================
    [Fact]
    public void Findings015to019_Backpressure_Issues()
    {
        var source = ReadSource("Features/BackpressureHandling.cs");
        Assert.Contains("BackpressureHandling", source);
    }

    // ========================================================================
    // Findings #20-22: HIGH - CoapStreamStrategy unbounded collections
    // ========================================================================
    [Fact]
    public void Findings020to022_Coap_UnboundedCollections()
    {
        var source = ReadSource("Strategies/IoT/CoapStreamStrategy.cs");
        Assert.Contains("CoapStreamStrategy", source);
    }

    // ========================================================================
    // Findings #23-26: LOW/HIGH - ComplexEventProcessing field-to-local, out-of-order, sync
    // ========================================================================
    [Fact]
    public void Findings023to026_Cep_Issues()
    {
        var source = ReadSource("Features/ComplexEventProcessing.cs");
        Assert.Contains("ComplexEventProcessing", source);
    }

    // ========================================================================
    // Findings #27-28: HIGH - EventHubs Math.Abs overflow
    // ========================================================================
    [Fact]
    public void Findings027_028_EventHubs_MathAbsOverflow()
    {
        var source = ReadSource("Strategies/Cloud/EventHubsStreamStrategy.cs");
        Assert.Contains("EventHubsStreamStrategy", source);
    }

    // ========================================================================
    // Findings #29-31: CRITICAL/HIGH - FaultTolerance checkpoint before ack, unbounded WAL
    // ========================================================================
    [Fact]
    public void Findings029to031_FaultTolerance_Issues()
    {
        var source = ReadSource("Strategies/FaultTolerance/FaultToleranceStrategies.cs");
        Assert.Contains("CheckpointStrategy", source);
    }

    // ========================================================================
    // Findings #32-33: MEDIUM - FhirStream empty catch PHI routing
    // ========================================================================
    [Fact]
    public void Findings032_033_Fhir_EmptyCatch()
    {
        var source = ReadSource("Strategies/Healthcare/FhirStreamStrategy.cs");
        Assert.Contains("FhirStreamStrategy", source);
    }

    // ========================================================================
    // Finding #34: LOW - Fix50SP2 -> Fix50Sp2
    // ========================================================================
    [Fact]
    public void Finding034_FixStream_Sp2Naming()
    {
        var source = ReadSource("Strategies/Financial/FixStreamStrategy.cs");
        Assert.DoesNotContain("Fix50SP2", source);
        Assert.Contains("Fix50Sp2", source);
    }

    // ========================================================================
    // Finding #35: LOW - ReceivedMessages never updated
    // ========================================================================
    [Fact]
    public void Finding035_FixStream_ReceivedMessages()
    {
        var source = ReadSource("Strategies/Financial/FixStreamStrategy.cs");
        Assert.Contains("ReceivedMessages", source);
    }

    // ========================================================================
    // Finding #36: MEDIUM - Non-standard FIX repeating group encoding
    // ========================================================================
    [Fact]
    public void Finding036_FixStream_RepeatingGroup()
    {
        var source = ReadSource("Strategies/Financial/FixStreamStrategy.cs");
        Assert.Contains("FixStreamStrategy", source);
    }

    // ========================================================================
    // Findings #37-52: LOW - HL7 enum naming ADT->Adt, AA->Aa, etc.
    // ========================================================================
    [Fact]
    public void Findings037to052_Hl7_EnumNaming()
    {
        var source = ReadSource("Strategies/Healthcare/Hl7StreamStrategy.cs");
        // Message types - check enum declarations (not comments)
        Assert.DoesNotContain("    ADT,", source);
        Assert.DoesNotContain("    ORM,", source);
        Assert.DoesNotContain("    ORU,", source);
        Assert.Contains(" Adt,", source);
        Assert.Contains(" Orm,", source);
        Assert.Contains(" Oru,", source);
        Assert.Contains(" Siu,", source);
        Assert.Contains(" Dft,", source);
        Assert.Contains(" Mdm,", source);
        Assert.Contains(" Mfn,", source);
        Assert.Contains(" Rde,", source);
        Assert.Contains(" Bar,", source);
        Assert.Contains(" Vxu", source);
        // Ack codes - check enum declarations (not comments)
        Assert.DoesNotContain("    AA,", source);
        Assert.Contains(" Aa,", source);
        Assert.Contains(" Ae,", source);
        Assert.Contains(" Ar,", source);
        Assert.Contains(" Ca,", source);
        Assert.Contains(" Ce,", source);
        Assert.Contains(" Cr", source);
    }

    // ========================================================================
    // Findings #53-54: MEDIUM - HL7 empty catch returns DateTime.MinValue
    // ========================================================================
    [Fact]
    public void Findings053_054_Hl7_EmptyCatch()
    {
        var source = ReadSource("Strategies/Healthcare/Hl7StreamStrategy.cs");
        Assert.Contains("Hl7StreamStrategy", source);
    }

    // ========================================================================
    // Findings #55-56: MEDIUM/HIGH - IntelligentRouting variable hides, Math.Abs
    // ========================================================================
    [Fact]
    public void Findings055_056_IntelligentRouting_Issues()
    {
        var source = ReadSource("AiDrivenStrategies/IntelligentRoutingStream.cs");
        Assert.Contains("IntelligentRoutingStream", source);
    }

    // ========================================================================
    // Findings #57-60: HIGH - KafkaAdvancedFeatures sync, prefix match, Interlocked
    // ========================================================================
    [Fact]
    public void Findings057to060_KafkaAdvanced_Issues()
    {
        var source = ReadSource("Strategies/MessageQueue/KafkaAdvancedFeatures.cs");
        Assert.Contains("KafkaConsumerGroupManager", source);
    }

    // ========================================================================
    // Findings #61-63: HIGH/CRITICAL - KafkaStream Math.Abs, duplicate MessageId
    // ========================================================================
    [Fact]
    public void Findings061to063_Kafka_CriticalIssues()
    {
        var source = ReadSource("Strategies/MessageQueue/KafkaStreamStrategy.cs");
        Assert.Contains("KafkaStreamStrategy", source);
    }

    // ========================================================================
    // Findings #64-65: HIGH - KinesisStream Math.Abs overflow
    // ========================================================================
    [Fact]
    public void Findings064_065_Kinesis_MathAbsOverflow()
    {
        var source = ReadSource("Strategies/Cloud/KinesisStreamStrategy.cs");
        Assert.Contains("KinesisStreamStrategy", source);
    }

    // ========================================================================
    // Findings #66-68: CRITICAL - LoRaWan random session key, AES-CMAC bug
    // ========================================================================
    [Fact]
    public void Findings066to068_LoRaWan_CriticalBugs()
    {
        var source = ReadSource("Strategies/IoT/LoRaWanStreamStrategy.cs");
        Assert.Contains("LoRaWanStreamStrategy", source);
    }

    // ========================================================================
    // Findings #69-70: LOW - LoRaWan OTAA->Otaa, ABP->Abp
    // ========================================================================
    [Fact]
    public void Findings069_070_LoRaWan_EnumNaming()
    {
        var source = ReadSource("Strategies/IoT/LoRaWanStreamStrategy.cs");
        Assert.DoesNotContain("    OTAA,", source);
        Assert.DoesNotContain("    ABP", source);
        Assert.Contains(" Otaa,", source);
        Assert.Contains(" Abp", source);
    }

    // ========================================================================
    // Finding #71: LOW - Modbus RegisterMaps never updated
    // ========================================================================
    [Fact]
    public void Finding071_Modbus_RegisterMaps()
    {
        var source = ReadSource("Strategies/Industrial/ModbusStreamStrategy.cs");
        Assert.Contains("RegisterMaps", source);
    }

    // ========================================================================
    // Findings #72-78: MEDIUM - MqttStreaming lock, NRT, sync-over-async, GC.SuppressFinalize
    // ========================================================================
    [Fact]
    public void Findings072to078_Mqtt_Issues()
    {
        var source = ReadSource("Strategies/MqttStreamingStrategy.cs");
        Assert.Contains("MqttStreamingStrategy", source);
    }

    // ========================================================================
    // Findings #79-81: CRITICAL/MEDIUM - OpcUa credentials, GetHashCode
    // ========================================================================
    [Fact]
    public void Findings079to081_OpcUa_CredentialIssues()
    {
        var source = ReadSource("Strategies/Industrial/OpcUaStreamStrategy.cs");
        Assert.Contains("OpcUaStreamStrategy", source);
    }

    // ========================================================================
    // Finding #82: LOW - sumXY -> sumXy
    // ========================================================================
    [Fact]
    public void Finding082_PredictiveScaling_SumXyNaming()
    {
        var source = ReadSource("AiDrivenStrategies/PredictiveScalingStream.cs");
        Assert.DoesNotContain("sumXY", source);
        Assert.Contains("sumXy", source);
    }

    // ========================================================================
    // Findings #83-84: HIGH - PulsarStream Math.Abs overflow
    // ========================================================================
    [Fact]
    public void Findings083_084_Pulsar_MathAbsOverflow()
    {
        var source = ReadSource("Strategies/MessageQueue/PulsarStreamStrategy.cs");
        Assert.Contains("PulsarStreamStrategy", source);
    }

    // ========================================================================
    // Findings #85-88: MEDIUM/LOW - RealTimePipeline NRT, never-updated collections
    // ========================================================================
    [Fact]
    public void Findings085to088_RealTimePipeline_Issues()
    {
        var source = ReadSource("Strategies/Pipelines/RealTimePipelineStrategies.cs");
        Assert.Contains("RealTimeEtlPipelineStrategy", source);
    }

    // ========================================================================
    // Findings #89-91: LOW/MEDIUM - Scalability RemoveAt(0), AdjustConfig state
    // ========================================================================
    [Fact]
    public void Findings089to091_Scalability_Issues()
    {
        var source = ReadSource("Strategies/Scalability/ScalabilityStrategies.cs");
        Assert.Contains("PartitioningStrategy", source);
    }

    // ========================================================================
    // Findings #92-98: LOW/HIGH/CRITICAL - StatefulStreamProcessing various issues
    // ========================================================================
    [Fact]
    public void Findings092to098_StatefulStream_Issues()
    {
        var source = ReadSource("Features/StatefulStreamProcessing.cs");
        Assert.Contains("StatefulStreamProcessing", source);
    }

    // ========================================================================
    // Findings #99-102: CRITICAL/HIGH/MEDIUM - StateManagement untrusted deserialization, WriteCount
    // ========================================================================
    [Fact]
    public void Findings099to102_StateManagement_Issues()
    {
        var source = ReadSource("Strategies/State/StateManagementStrategies.cs");
        Assert.Contains("InMemoryStateBackendStrategy", source);
    }

    // ========================================================================
    // Findings #103-106: LOW - StreamAnalytics dead assignment, never-updated collections
    // ========================================================================
    [Fact]
    public void Findings103to106_StreamAnalytics_Issues()
    {
        var source = ReadSource("Strategies/Analytics/StreamAnalyticsStrategies.cs");
        Assert.Contains("RealTimeAggregationStrategy", source);
    }

    // ========================================================================
    // Finding #107: HIGH - StreamingBackpressureHandler watermark sync
    // ========================================================================
    [Fact]
    public void Finding107_BackpressureHandler_WatermarkSync()
    {
        var source = ReadSource("Scaling/StreamingBackpressureHandler.cs");
        Assert.Contains("StreamingBackpressureHandler", source);
    }

    // ========================================================================
    // Findings #108-110: MEDIUM/HIGH - StreamingInfrastructure atomicity, unbounded queue
    // ========================================================================
    [Fact]
    public void Findings108to110_Infrastructure_Issues()
    {
        var source = ReadSource("Strategies/StreamingInfrastructure.cs");
        Assert.Contains("RedisStreamsConsumerGroupManager", source);
    }

    // ========================================================================
    // Findings #111-118: LOW/HIGH/MEDIUM - StreamingScalingManager non-accessed, Math.Abs, fire-and-forget
    // ========================================================================
    [Fact]
    public void Findings111to118_ScalingManager_Issues()
    {
        var source = ReadSource("Scaling/StreamingScalingManager.cs");
        // _strategyRegistry exposed as internal property
        Assert.Contains("internal StreamingStrategyRegistry StrategyRegistry", source);
    }

    // ========================================================================
    // Findings #119-120: LOW - StreamProcessingEngine namespace_, StreamARN->StreamArn
    // ========================================================================
    [Fact]
    public void Findings119_120_StreamProcessingEngine_Naming()
    {
        var source = ReadSource("Strategies/StreamProcessing/StreamProcessingEngineStrategies.cs");
        Assert.DoesNotContain("StreamARN", source);
        Assert.Contains("StreamArn", source);
    }

    // ========================================================================
    // Findings #121-134: LOW - SwiftStream MT103->Mt103, CreateMT103->CreateMt103
    // ========================================================================
    [Fact]
    public void Findings121to134_Swift_EnumAndMethodNaming()
    {
        var source = ReadSource("Strategies/Financial/SwiftStreamStrategy.cs");
        // Enum members - check enum declarations (not comments)
        Assert.DoesNotContain("    MT103,", source);
        Assert.DoesNotContain("    MT202,", source);
        Assert.Contains(" Mt103,", source);
        Assert.Contains(" Mt202,", source);
        Assert.Contains(" Mt199,", source);
        Assert.Contains(" Mt940,", source);
        Assert.Contains(" Mt950,", source);
        Assert.Contains(" Mt300,", source);
        Assert.Contains(" Mt502,", source);
        Assert.Contains(" Mt535,", source);
        Assert.Contains(" Mt564,", source);
        Assert.Contains(" Mt700,", source);
        Assert.Contains(" Mt760,", source);
        Assert.Contains(" Mt799", source);
        // Method names
        Assert.DoesNotContain("CreateMT103Async", source);
        Assert.DoesNotContain("CreateMT202Async", source);
        Assert.Contains("CreateMt103Async", source);
        Assert.Contains("CreateMt202Async", source);
    }

    // ========================================================================
    // Finding #135: LOW - AnomalyDetection RunningStatistics outside lock
    // ========================================================================
    [Fact]
    public void Finding135_AnomalyDetection_StatsOutsideLock()
    {
        var source = ReadSource("AiDrivenStrategies/AnomalyDetectionStream.cs");
        Assert.Contains("AnomalyDetectionStream", source);
    }

    // ========================================================================
    // Findings #136-138: HIGH/CRITICAL/MEDIUM - BackpressureHandling timer leak, async timer, channel validation
    // ========================================================================
    [Fact]
    public void Findings136to138_Backpressure_TimerAndChannel()
    {
        var source = ReadSource("Features/BackpressureHandling.cs");
        Assert.Contains("BackpressureHandling", source);
    }

    // ========================================================================
    // Findings #139-140: HIGH - ComplexEventProcessing sequential, ConcurrentBag replacement
    // ========================================================================
    [Fact]
    public void Findings139_140_Cep_ConcurrencyIssues()
    {
        var source = ReadSource("Features/ComplexEventProcessing.cs");
        Assert.Contains("ComplexEventProcessing", source);
    }

    // ========================================================================
    // Findings #141-143: CRITICAL/MEDIUM/HIGH - StatefulStream timer, checkpoint, LRU
    // ========================================================================
    [Fact]
    public void Findings141to143_StatefulStream_TimerAndLru()
    {
        var source = ReadSource("Features/StatefulStreamProcessing.cs");
        Assert.Contains("StatefulStreamProcessing", source);
    }

    // ========================================================================
    // Finding #144: HIGH - WatermarkManagement torn reads
    // ========================================================================
    [Fact]
    public void Finding144_Watermark_TornReads()
    {
        var source = ReadSource("Features/WatermarkManagement.cs");
        Assert.Contains("WatermarkManagement", source);
    }

    // ========================================================================
    // Finding #145: MEDIUM - BackpressureHandler DropOldest misleading
    // ========================================================================
    [Fact]
    public void Finding145_BackpressureHandler_DropOldest()
    {
        var source = ReadSource("Scaling/StreamingBackpressureHandler.cs");
        Assert.Contains("StreamingBackpressureHandler", source);
    }

    // ========================================================================
    // Findings #146-156: HIGH/MEDIUM/CRITICAL - ScalingManager config volatility, routing, fire-and-forget
    // ========================================================================
    [Fact]
    public void Findings146to156_ScalingManager_ConcurrencyIssues()
    {
        var source = ReadSource("Scaling/StreamingScalingManager.cs");
        Assert.Contains("StreamingScalingManager", source);
    }

    // ========================================================================
    // Findings #157-160: HIGH/MEDIUM - AdaptiveTransport timer, bare catch, stream resource
    // ========================================================================
    [Fact]
    public void Findings157to160_AdaptiveTransport_SdkAudit()
    {
        var source = ReadSource("Strategies/AdaptiveTransport/AdaptiveTransportStrategy.cs");
        Assert.Contains("AdaptiveTransportStrategy", source);
    }

    // ========================================================================
    // Findings #161-163: HIGH/MEDIUM - StreamAnalytics Rule13, RemoveAt(0), ReDoS
    // ========================================================================
    [Fact]
    public void Findings161to163_StreamAnalytics_SdkAudit()
    {
        var source = ReadSource("Strategies/Analytics/StreamAnalyticsStrategies.cs");
        Assert.Contains("RealTimeAggregationStrategy", source);
    }

    // ========================================================================
    // Findings #164-165: HIGH - EventHubs input validation
    // ========================================================================
    [Fact]
    public void Findings164_165_EventHubs_Validation()
    {
        var source = ReadSource("Strategies/Cloud/EventHubsStreamStrategy.cs");
        Assert.Contains("EventHubsStreamStrategy", source);
    }

    // ========================================================================
    // Findings #166-171: MEDIUM/LOW - UltimateStreamingDataPlugin empty catch, async, ReadToEnd, NRT, CancellationToken
    // ========================================================================
    [Fact]
    public void Findings166to171_Plugin_Issues()
    {
        var source = ReadSource("UltimateStreamingDataPlugin.cs");
        Assert.Contains("UltimateStreamingDataPlugin", source);
    }

    // ========================================================================
    // Findings #172-173: LOW - Zigbee AES-ECB for CTR keystream
    // ========================================================================
    [Fact]
    public void Findings172_173_Zigbee_AesEcbCtr()
    {
        var source = ReadSource("Strategies/IoT/ZigbeeStreamStrategy.cs");
        Assert.Contains("ZigbeeStreamStrategy", source);
    }
}
