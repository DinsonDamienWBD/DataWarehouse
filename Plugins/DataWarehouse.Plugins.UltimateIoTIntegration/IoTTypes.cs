using System;
using System.Collections.Generic;

namespace DataWarehouse.Plugins.UltimateIoTIntegration;

#region Enums

/// <summary>
/// Categories of IoT strategies.
/// </summary>
public enum IoTStrategyCategory
{
    /// <summary>Device management strategies (110.1)</summary>
    DeviceManagement,
    /// <summary>Sensor data ingestion strategies (110.2)</summary>
    SensorIngestion,
    /// <summary>Protocol support strategies (110.3)</summary>
    Protocol,
    /// <summary>Device provisioning strategies (110.4)</summary>
    Provisioning,
    /// <summary>IoT analytics strategies (110.5)</summary>
    Analytics,
    /// <summary>IoT security strategies (110.6)</summary>
    Security,
    /// <summary>Edge integration strategies (110.7)</summary>
    EdgeIntegration,
    /// <summary>Data transformation strategies (110.8)</summary>
    DataTransformation
}

/// <summary>
/// Device status values.
/// </summary>
public enum DeviceStatus
{
    Unknown,
    Registered,
    Provisioned,
    Connected,
    Disconnected,
    Disabled,
    Retired
}

/// <summary>
/// MQTT Quality of Service levels.
/// </summary>
public enum MqttQoS
{
    AtMostOnce = 0,
    AtLeastOnce = 1,
    ExactlyOnce = 2
}

/// <summary>
/// CoAP methods.
/// </summary>
public enum CoApMethod
{
    GET,
    POST,
    PUT,
    DELETE
}

/// <summary>
/// Modbus function codes.
/// </summary>
public enum ModbusFunction
{
    ReadCoils = 1,
    ReadDiscreteInputs = 2,
    ReadHoldingRegisters = 3,
    ReadInputRegisters = 4,
    WriteSingleCoil = 5,
    WriteSingleRegister = 6,
    WriteMultipleCoils = 15,
    WriteMultipleRegisters = 16
}

/// <summary>
/// Credential types for device provisioning.
/// </summary>
public enum CredentialType
{
    SymmetricKey,
    X509Certificate,
    TPMEndorsementKey,
    SasToken
}

/// <summary>
/// Anomaly severity levels.
/// </summary>
public enum AnomalySeverity
{
    None,
    Low,
    Medium,
    High,
    Critical
}

/// <summary>
/// Security threat levels.
/// </summary>
public enum ThreatLevel
{
    None,
    Low,
    Medium,
    High,
    Critical
}

#endregion

#region Device Management Types (110.1)

/// <summary>
/// Device registration request.
/// </summary>
public class DeviceRegistrationRequest
{
    public string? StrategyId { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceType { get; set; } = string.Empty;
    public Dictionary<string, string> Metadata { get; set; } = new();
    public Dictionary<string, object> InitialTwin { get; set; } = new();
}

/// <summary>
/// Device registration result.
/// </summary>
public class DeviceRegistration
{
    public string DeviceId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? ConnectionString { get; set; }
    public string? PrimaryKey { get; set; }
    public string? SecondaryKey { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }
}

/// <summary>
/// Device twin (desired/reported state).
/// </summary>
public class DeviceTwin
{
    public string DeviceId { get; set; } = string.Empty;
    public Dictionary<string, object> DesiredProperties { get; set; } = new();
    public Dictionary<string, object> ReportedProperties { get; set; } = new();
    public long Version { get; set; }
    public DateTimeOffset LastUpdated { get; set; }
}

/// <summary>
/// Device query for filtering.
/// </summary>
public class DeviceQuery
{
    public string? DeviceType { get; set; }
    public DeviceStatus? Status { get; set; }
    public Dictionary<string, string>? Tags { get; set; }
    public int? Limit { get; set; }
    public string? ContinuationToken { get; set; }
}

/// <summary>
/// Device information.
/// </summary>
public class DeviceInfo
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceType { get; set; } = string.Empty;
    public DeviceStatus Status { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
}

/// <summary>
/// Firmware update request.
/// </summary>
public class FirmwareUpdateRequest
{
    public string? StrategyId { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string? DeviceGroupId { get; set; }
    public string FirmwareVersion { get; set; } = string.Empty;
    public string FirmwareUrl { get; set; } = string.Empty;
    public string? Checksum { get; set; }
    public bool ForceUpdate { get; set; }
}

/// <summary>
/// Firmware update result.
/// </summary>
public class FirmwareUpdateResult
{
    public bool Success { get; set; }
    public string? JobId { get; set; }
    public int DevicesTargeted { get; set; }
    public string? Message { get; set; }
}

/// <summary>
/// Internal device state tracking.
/// </summary>
public class DeviceState
{
    public string DeviceId { get; set; } = string.Empty;
    public DeviceStatus Status { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public string? PreferredProtocol { get; set; }
    public string? ProvisioningMethod { get; set; }
    public TelemetryMessage? LastTelemetry { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
}

#endregion

#region Sensor Ingestion Types (110.2)

/// <summary>
/// Telemetry message from a device.
/// </summary>
public class TelemetryMessage
{
    public string DeviceId { get; set; } = string.Empty;
    public string MessageId { get; set; } = Guid.NewGuid().ToString();
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    public Dictionary<string, object> Data { get; set; } = new();
    public byte[]? RawPayload { get; set; }
    public int PayloadSize => RawPayload?.Length ?? 0;
    public Dictionary<string, string> Properties { get; set; } = new();
}

/// <summary>
/// Ingestion result.
/// </summary>
public class IngestionResult
{
    public bool Success { get; set; }
    public string MessageId { get; set; } = string.Empty;
    public DateTimeOffset IngestedAt { get; set; }
    public string? PartitionKey { get; set; }
    public long? SequenceNumber { get; set; }
}

/// <summary>
/// Batch ingestion result.
/// </summary>
public class BatchIngestionResult
{
    public bool Success { get; set; }
    public int TotalMessages { get; set; }
    public int SuccessfulMessages { get; set; }
    public int FailedMessages { get; set; }
    public List<string> FailedMessageIds { get; set; } = new();
}

/// <summary>
/// Telemetry subscription.
/// </summary>
public class TelemetrySubscription
{
    public string? DeviceId { get; set; }
    public string? DeviceGroupId { get; set; }
    public string? Topic { get; set; }
    public string? Filter { get; set; }
}

/// <summary>
/// Internal telemetry buffer for batching.
/// </summary>
public class TelemetryBuffer
{
    public string DeviceId { get; set; } = string.Empty;
    public List<TelemetryMessage> Messages { get; set; } = new();
    public DateTimeOffset LastFlush { get; set; }
}

#endregion

#region Protocol Types (110.3)

/// <summary>
/// Device command.
/// </summary>
public class DeviceCommand
{
    public string DeviceId { get; set; } = string.Empty;
    public string CommandName { get; set; } = string.Empty;
    public Dictionary<string, object> Parameters { get; set; } = new();
    public TimeSpan? Timeout { get; set; }
    public string? ResponseTopic { get; set; }
}

/// <summary>
/// Command result.
/// </summary>
public class CommandResult
{
    public bool Success { get; set; }
    public string CommandId { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public string? Response { get; set; }
    public TimeSpan Latency { get; set; }
}

/// <summary>
/// Protocol options.
/// </summary>
public class ProtocolOptions
{
    public int QoS { get; set; }
    public bool Retain { get; set; }
    public TimeSpan? Timeout { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();
}

/// <summary>
/// CoAP response.
/// </summary>
public class CoApResponse
{
    public int ResponseCode { get; set; }
    public byte[]? Payload { get; set; }
    public string? ContentFormat { get; set; }
    public Dictionary<string, string> Options { get; set; } = new();
}

/// <summary>
/// Modbus response.
/// </summary>
public class ModbusResponse
{
    public bool Success { get; set; }
    public int SlaveId { get; set; }
    public int StartAddress { get; set; }
    public ushort[]? Registers { get; set; }
    public bool[]? Coils { get; set; }
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// OPC-UA node.
/// </summary>
public class OpcUaNode
{
    public string NodeId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string NodeClass { get; set; } = string.Empty;
    public string? DataType { get; set; }
    public object? Value { get; set; }
    public bool HasChildren { get; set; }
}

#endregion

#region Provisioning Types (110.4)

/// <summary>
/// Provisioning request.
/// </summary>
public class ProvisioningRequest
{
    public string? StrategyId { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string? RegistrationId { get; set; }
    public CredentialType CredentialType { get; set; } = CredentialType.SymmetricKey;
    public string? GroupId { get; set; }
    public Dictionary<string, object> InitialTwin { get; set; } = new();
}

/// <summary>
/// Provisioning result.
/// </summary>
public class ProvisioningResult
{
    public bool Success { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string? AssignedHub { get; set; }
    public string? ConnectionString { get; set; }
    public DateTimeOffset ProvisionedAt { get; set; }
    public string? Message { get; set; }
}

/// <summary>
/// Device credentials.
/// </summary>
public class DeviceCredentials
{
    public string DeviceId { get; set; } = string.Empty;
    public CredentialType Type { get; set; }
    public string? PrimaryKey { get; set; }
    public string? SecondaryKey { get; set; }
    public string? Certificate { get; set; }
    public string? PrivateKey { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
/// Enrollment request.
/// </summary>
public class EnrollmentRequest
{
    public string? StrategyId { get; set; }
    public string RegistrationId { get; set; } = string.Empty;
    public string? GroupId { get; set; }
    public CredentialType AttestationType { get; set; }
    public string? EndorsementKey { get; set; }
    public string? Certificate { get; set; }
}

/// <summary>
/// Enrollment result.
/// </summary>
public class EnrollmentResult
{
    public bool Success { get; set; }
    public string RegistrationId { get; set; } = string.Empty;
    public string? EnrollmentGroupId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

#endregion

#region Analytics Types (110.5)

/// <summary>
/// Anomaly detection request.
/// </summary>
public class AnomalyDetectionRequest
{
    public string? StrategyId { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string? MetricName { get; set; }
    public DateTimeOffset? StartTime { get; set; }
    public DateTimeOffset? EndTime { get; set; }
    public double? Sensitivity { get; set; }
}

/// <summary>
/// Anomaly detection result.
/// </summary>
public class AnomalyDetectionResult
{
    public bool Success { get; set; }
    public int AnomaliesDetected { get; set; }
    public AnomalySeverity Severity { get; set; }
    public double Confidence { get; set; }
    public List<DetectedAnomaly> Anomalies { get; set; } = new();
}

/// <summary>
/// Detected anomaly.
/// </summary>
public class DetectedAnomaly
{
    public DateTimeOffset Timestamp { get; set; }
    public string MetricName { get; set; } = string.Empty;
    public double ExpectedValue { get; set; }
    public double ActualValue { get; set; }
    public double Deviation { get; set; }
    public AnomalySeverity Severity { get; set; }
}

/// <summary>
/// Prediction request.
/// </summary>
public class PredictionRequest
{
    public string? StrategyId { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string MetricName { get; set; } = string.Empty;
    public int HorizonMinutes { get; set; } = 60;
}

/// <summary>
/// Prediction result.
/// </summary>
public class PredictionResult
{
    public bool Success { get; set; }
    public string MetricName { get; set; } = string.Empty;
    public List<PredictedValue> Predictions { get; set; } = new();
    public double Confidence { get; set; }
}

/// <summary>
/// Predicted value.
/// </summary>
public class PredictedValue
{
    public DateTimeOffset Timestamp { get; set; }
    public double Value { get; set; }
    public double LowerBound { get; set; }
    public double UpperBound { get; set; }
}

/// <summary>
/// Stream analytics query.
/// </summary>
public class StreamAnalyticsQuery
{
    public string? StrategyId { get; set; }
    public string Query { get; set; } = string.Empty;
    public string? InputSource { get; set; }
    public TimeSpan WindowSize { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Stream analytics result.
/// </summary>
public class StreamAnalyticsResult
{
    public bool Success { get; set; }
    public List<Dictionary<string, object>> Results { get; set; } = new();
    public DateTimeOffset QueryTime { get; set; }
}

/// <summary>
/// Pattern detection request.
/// </summary>
public class PatternDetectionRequest
{
    public string? StrategyId { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string PatternType { get; set; } = string.Empty;
    public Dictionary<string, object> Parameters { get; set; } = new();
}

/// <summary>
/// Pattern detection result.
/// </summary>
public class PatternDetectionResult
{
    public bool Success { get; set; }
    public int PatternsFound { get; set; }
    public List<DetectedPattern> Patterns { get; set; } = new();
}

/// <summary>
/// Detected pattern.
/// </summary>
public class DetectedPattern
{
    public string PatternType { get; set; } = string.Empty;
    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset EndTime { get; set; }
    public double Confidence { get; set; }
    public Dictionary<string, object> Attributes { get; set; } = new();
}

#endregion

#region Security Types (110.6)

/// <summary>
/// Device authentication request.
/// </summary>
public class DeviceAuthenticationRequest
{
    public string? StrategyId { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public CredentialType CredentialType { get; set; }
    public string? Credential { get; set; }
    public string? Certificate { get; set; }
}

/// <summary>
/// Authentication result.
/// </summary>
public class AuthenticationResult
{
    public bool Success { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public string? Token { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? Message { get; set; }
}

/// <summary>
/// Credential rotation result.
/// </summary>
public class CredentialRotationResult
{
    public bool Success { get; set; }
    public string DeviceId { get; set; } = string.Empty;
    public DeviceCredentials? NewCredentials { get; set; }
    public DateTimeOffset RotatedAt { get; set; }
}

/// <summary>
/// Security assessment.
/// </summary>
public class SecurityAssessment
{
    public string DeviceId { get; set; } = string.Empty;
    public int SecurityScore { get; set; }
    public ThreatLevel ThreatLevel { get; set; }
    public List<SecurityFinding> Findings { get; set; } = new();
    public DateTimeOffset AssessedAt { get; set; }
}

/// <summary>
/// Security finding.
/// </summary>
public class SecurityFinding
{
    public string FindingId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ThreatLevel Severity { get; set; }
    public string? Remediation { get; set; }
}

/// <summary>
/// Threat detection request.
/// </summary>
public class ThreatDetectionRequest
{
    public string? StrategyId { get; set; }
    public string? DeviceId { get; set; }
    public DateTimeOffset? StartTime { get; set; }
    public DateTimeOffset? EndTime { get; set; }
}

/// <summary>
/// Threat detection result.
/// </summary>
public class ThreatDetectionResult
{
    public bool Success { get; set; }
    public int ThreatsDetected { get; set; }
    public List<DetectedThreat> Threats { get; set; } = new();
}

/// <summary>
/// Detected threat.
/// </summary>
public class DetectedThreat
{
    public string ThreatId { get; set; } = string.Empty;
    public string ThreatType { get; set; } = string.Empty;
    public ThreatLevel Severity { get; set; }
    public string? AffectedDeviceId { get; set; }
    public DateTimeOffset DetectedAt { get; set; }
    public string Description { get; set; } = string.Empty;
}

#endregion

#region Edge Integration Types (110.7)

/// <summary>
/// Edge deployment request.
/// </summary>
public class EdgeDeploymentRequest
{
    public string? StrategyId { get; set; }
    public string EdgeDeviceId { get; set; } = string.Empty;
    public string ModuleName { get; set; } = string.Empty;
    public string ImageUri { get; set; } = string.Empty;
    public Dictionary<string, object> ModuleSettings { get; set; } = new();
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();
}

/// <summary>
/// Edge deployment result.
/// </summary>
public class EdgeDeploymentResult
{
    public bool Success { get; set; }
    public string DeploymentId { get; set; } = string.Empty;
    public string ModuleName { get; set; } = string.Empty;
    public DateTimeOffset DeployedAt { get; set; }
    public string? Message { get; set; }
}

/// <summary>
/// Edge sync request.
/// </summary>
public class EdgeSyncRequest
{
    public string? StrategyId { get; set; }
    public string EdgeDeviceId { get; set; } = string.Empty;
    public string? DataPath { get; set; }
    public bool FullSync { get; set; }
}

/// <summary>
/// Sync result.
/// </summary>
public class SyncResult
{
    public bool Success { get; set; }
    public int ItemsSynced { get; set; }
    public long BytesSynced { get; set; }
    public DateTimeOffset SyncedAt { get; set; }
}

/// <summary>
/// Edge compute request.
/// </summary>
public class EdgeComputeRequest
{
    public string? StrategyId { get; set; }
    public string EdgeDeviceId { get; set; } = string.Empty;
    public string FunctionName { get; set; } = string.Empty;
    public Dictionary<string, object> Input { get; set; } = new();
    public TimeSpan? Timeout { get; set; }
}

/// <summary>
/// Edge compute result.
/// </summary>
public class EdgeComputeResult
{
    public bool Success { get; set; }
    public Dictionary<string, object> Output { get; set; } = new();
    public TimeSpan ExecutionTime { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Edge device status.
/// </summary>
public class EdgeDeviceStatus
{
    public string EdgeDeviceId { get; set; } = string.Empty;
    public bool IsOnline { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public List<EdgeModuleStatus> Modules { get; set; } = new();
    public EdgeResourceUsage ResourceUsage { get; set; } = new();
}

/// <summary>
/// Edge module status.
/// </summary>
public class EdgeModuleStatus
{
    public string ModuleName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public DateTimeOffset LastStarted { get; set; }
}

/// <summary>
/// Edge resource usage.
/// </summary>
public class EdgeResourceUsage
{
    public double CpuPercent { get; set; }
    public long MemoryUsedBytes { get; set; }
    public long DiskUsedBytes { get; set; }
    public double NetworkInBytes { get; set; }
    public double NetworkOutBytes { get; set; }
}

#endregion

#region Data Transformation Types (110.8)

/// <summary>
/// Transformation request.
/// </summary>
public class TransformationRequest
{
    public string? StrategyId { get; set; }
    public string SourceFormat { get; set; } = string.Empty;
    public string TargetFormat { get; set; } = string.Empty;
    public byte[] InputData { get; set; } = Array.Empty<byte>();
    public Dictionary<string, object> Options { get; set; } = new();
}

/// <summary>
/// Transformation result.
/// </summary>
public class TransformationResult
{
    public bool Success { get; set; }
    public byte[] OutputData { get; set; } = Array.Empty<byte>();
    public string TargetFormat { get; set; } = string.Empty;
    public Dictionary<string, object> Metadata { get; set; } = new();
}

/// <summary>
/// Protocol translation request.
/// </summary>
public class ProtocolTranslationRequest
{
    public string? StrategyId { get; set; }
    public string SourceProtocol { get; set; } = string.Empty;
    public string TargetProtocol { get; set; } = string.Empty;
    public byte[] Message { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// Protocol translation result.
/// </summary>
public class ProtocolTranslationResult
{
    public bool Success { get; set; }
    public byte[] TranslatedMessage { get; set; } = Array.Empty<byte>();
    public string TargetProtocol { get; set; } = string.Empty;
}

/// <summary>
/// Enrichment request.
/// </summary>
public class EnrichmentRequest
{
    public string? StrategyId { get; set; }
    public TelemetryMessage Message { get; set; } = new();
    public List<string> EnrichmentSources { get; set; } = new();
}

/// <summary>
/// Enrichment result.
/// </summary>
public class EnrichmentResult
{
    public bool Success { get; set; }
    public TelemetryMessage EnrichedMessage { get; set; } = new();
    public Dictionary<string, object> AddedFields { get; set; } = new();
}

/// <summary>
/// Normalization request.
/// </summary>
public class NormalizationRequest
{
    public string? StrategyId { get; set; }
    public TelemetryMessage Message { get; set; } = new();
    public string TargetSchema { get; set; } = string.Empty;
}

/// <summary>
/// Normalization result.
/// </summary>
public class NormalizationResult
{
    public bool Success { get; set; }
    public TelemetryMessage NormalizedMessage { get; set; } = new();
    public List<string> MappedFields { get; set; } = new();
}

#endregion
