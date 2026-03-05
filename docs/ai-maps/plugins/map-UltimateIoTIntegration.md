# Plugin: UltimateIoTIntegration
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.UltimateIoTIntegration

### File: Plugins/DataWarehouse.Plugins.UltimateIoTIntegration/IoTStrategyBase.cs
```csharp
public sealed class IoTStrategyHealthReport
{
}
    public required IoTStrategyHealthStatus Status { get; init; }
    public required string StrategyId { get; init; }
    public long TotalOperations { get; init; }
    public long FailedOperations { get; init; }
    public DateTimeOffset? LastActivity { get; init; }
    public string? Details { get; init; }
}
```
```csharp
public abstract class IoTStrategyBase : StrategyBase, IIoTStrategyBase
{
}
    public abstract override string StrategyId { get; }
    public override string Name;;
    public abstract string StrategyName { get; }
    public abstract IoTStrategyCategory Category { get; }
    public abstract override string Description { get; }
    public virtual string[] Tags;;
    public virtual bool IsAvailable;;
    public virtual IoTStrategyHealthReport GetHealthReport();
    protected void RecordOperation();
    protected void RecordFailure();
    public override void ConfigureIntelligence(IMessageBus? messageBus);
    protected virtual void OnIntelligenceConfigured();
    public virtual IEnumerable<KnowledgeObject> GetKnowledge();
    public virtual IEnumerable<RegisteredCapability> GetCapabilities();
    protected async Task PublishMessage(string topic, PluginMessage message);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateIoTIntegration/IoTStrategyInterfaces.cs
```csharp
public interface IIoTStrategyBase
{
}
    string StrategyId { get; }
    string StrategyName { get; }
    IoTStrategyCategory Category { get; }
    string Description { get; }
    string[] Tags { get; }
    bool IsAvailable { get; }
    void ConfigureIntelligence(IMessageBus? messageBus);;
    IEnumerable<KnowledgeObject> GetKnowledge();;
    IEnumerable<RegisteredCapability> GetCapabilities();;
}
```
```csharp
public interface IDeviceManagementStrategy : IIoTStrategyBase
{
}
    Task<DeviceRegistration> RegisterDeviceAsync(DeviceRegistrationRequest request, CancellationToken ct = default);;
    Task<DeviceTwin> GetDeviceTwinAsync(string deviceId, CancellationToken ct = default);;
    Task UpdateDeviceTwinAsync(string deviceId, Dictionary<string, object> desiredProperties, CancellationToken ct = default);;
    Task<IEnumerable<DeviceInfo>> ListDevicesAsync(DeviceQuery query, CancellationToken ct = default);;
    Task<FirmwareUpdateResult> UpdateFirmwareAsync(FirmwareUpdateRequest request, CancellationToken ct = default);;
    Task<bool> DeleteDeviceAsync(string deviceId, CancellationToken ct = default);;
    Task<DeviceInfo?> GetDeviceAsync(string deviceId, CancellationToken ct = default);;
}
```
```csharp
public interface ISensorIngestionStrategy : IIoTStrategyBase
{
}
    Task<IngestionResult> IngestAsync(TelemetryMessage message, CancellationToken ct = default);;
    Task<BatchIngestionResult> IngestBatchAsync(IEnumerable<TelemetryMessage> messages, CancellationToken ct = default);;
    IAsyncEnumerable<TelemetryMessage> SubscribeAsync(TelemetrySubscription subscription, CancellationToken ct = default);;
    Task<IngestionStatistics> GetStatisticsAsync(CancellationToken ct = default);;
}
```
```csharp
public class IngestionStatistics
{
}
    public long TotalMessagesIngested { get; set; }
    public long TotalBytesIngested { get; set; }
    public double AverageLatencyMs { get; set; }
    public int ActiveStreams { get; set; }
    public DateTimeOffset LastIngestedAt { get; set; }
}
```
```csharp
public interface IProtocolStrategy : IIoTStrategyBase
{
}
    string ProtocolName { get; }
    int DefaultPort { get; }
    Task<CommandResult> SendCommandAsync(DeviceCommand command, CancellationToken ct = default);;
    Task PublishAsync(string topic, byte[] payload, ProtocolOptions options, CancellationToken ct = default);;
    IAsyncEnumerable<byte[]> SubscribeAsync(string topic, CancellationToken ct = default);;
    Task<CoApResponse> SendCoApAsync(string endpoint, CoApMethod method, string resourcePath, byte[]? payload, CancellationToken ct = default);;
    Task<ModbusResponse> ReadModbusAsync(string address, int slaveId, int registerAddress, int count, ModbusFunction function, CancellationToken ct = default);;
    Task<ModbusResponse> WriteModbusAsync(string address, int slaveId, int registerAddress, ushort[] values, CancellationToken ct = default);;
    Task<IEnumerable<OpcUaNode>> BrowseOpcUaAsync(string endpoint, string? nodeId, CancellationToken ct = default);;
    Task<object?> ReadOpcUaAsync(string endpoint, string nodeId, CancellationToken ct = default);;
}
```
```csharp
public interface IProvisioningStrategy : IIoTStrategyBase
{
}
    CredentialType[] SupportedCredentialTypes { get; }
    Task<ProvisioningResult> ProvisionAsync(ProvisioningRequest request, CancellationToken ct = default);;
    Task<DeviceCredentials> GenerateCredentialsAsync(string deviceId, CredentialType credentialType, CancellationToken ct = default);;
    Task<EnrollmentResult> EnrollAsync(EnrollmentRequest request, CancellationToken ct = default);;
    Task<bool> RevokeCredentialsAsync(string deviceId, CancellationToken ct = default);;
    Task<bool> ValidateCredentialsAsync(string deviceId, string credential, CancellationToken ct = default);;
}
```
```csharp
public interface IIoTAnalyticsStrategy : IIoTStrategyBase
{
}
    Task<AnomalyDetectionResult> DetectAnomaliesAsync(AnomalyDetectionRequest request, CancellationToken ct = default);;
    Task<PredictionResult> PredictAsync(PredictionRequest request, CancellationToken ct = default);;
    Task<StreamAnalyticsResult> AnalyzeStreamAsync(StreamAnalyticsQuery query, CancellationToken ct = default);;
    Task<PatternDetectionResult> DetectPatternsAsync(PatternDetectionRequest request, CancellationToken ct = default);;
    Task<Dictionary<string, double>> ComputeAggregationsAsync(string deviceId, string[] metrics, TimeSpan window, CancellationToken ct = default);;
}
```
```csharp
public interface IIoTSecurityStrategy : IIoTStrategyBase
{
}
    Task<AuthenticationResult> AuthenticateAsync(DeviceAuthenticationRequest request, CancellationToken ct = default);;
    Task<CredentialRotationResult> RotateCredentialsAsync(string deviceId, CancellationToken ct = default);;
    Task<SecurityAssessment> AssessSecurityAsync(string deviceId, CancellationToken ct = default);;
    Task<ThreatDetectionResult> DetectThreatsAsync(ThreatDetectionRequest request, CancellationToken ct = default);;
    Task<byte[]> EncryptAsync(string deviceId, byte[] data, CancellationToken ct = default);;
    Task<byte[]> DecryptAsync(string deviceId, byte[] data, CancellationToken ct = default);;
}
```
```csharp
public interface IEdgeIntegrationStrategy : IIoTStrategyBase
{
}
    Task<EdgeDeploymentResult> DeployModuleAsync(EdgeDeploymentRequest request, CancellationToken ct = default);;
    Task<SyncResult> SyncAsync(EdgeSyncRequest request, CancellationToken ct = default);;
    Task<EdgeComputeResult> ExecuteComputeAsync(EdgeComputeRequest request, CancellationToken ct = default);;
    Task<EdgeDeviceStatus> GetEdgeStatusAsync(string edgeDeviceId, CancellationToken ct = default);;
    Task<IEnumerable<EdgeModuleStatus>> ListModulesAsync(string edgeDeviceId, CancellationToken ct = default);;
    Task<bool> RemoveModuleAsync(string edgeDeviceId, string moduleName, CancellationToken ct = default);;
}
```
```csharp
public interface IDataTransformationStrategy : IIoTStrategyBase
{
}
    string[] SupportedSourceFormats { get; }
    string[] SupportedTargetFormats { get; }
    Task<TransformationResult> TransformAsync(TransformationRequest request, CancellationToken ct = default);;
    Task<ProtocolTranslationResult> TranslateProtocolAsync(ProtocolTranslationRequest request, CancellationToken ct = default);;
    Task<EnrichmentResult> EnrichAsync(EnrichmentRequest request, CancellationToken ct = default);;
    Task<NormalizationResult> NormalizeAsync(NormalizationRequest request, CancellationToken ct = default);;
    Task<bool> ValidateSchemaAsync(byte[] data, string schema, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateIoTIntegration/IoTTypes.cs
```csharp
public class DeviceRegistrationRequest
{
}
    public string? StrategyId { get; set; }
    public string DeviceId { get; set; };
    public string DeviceType { get; set; };
    public Dictionary<string, string> Metadata { get; set; };
    public Dictionary<string, object> InitialTwin { get; set; };
}
```
```csharp
public class DeviceRegistration
{
}
    public string DeviceId { get; set; };
    public bool Success { get; set; }
    public string? ConnectionString { get; set; }
    public string? PrimaryKey { get; set; }
    public string? SecondaryKey { get; set; }
    public DateTimeOffset RegisteredAt { get; set; }
}
```
```csharp
public class DeviceTwin
{
}
    public string DeviceId { get; set; };
    public Dictionary<string, object> DesiredProperties { get; set; };
    public Dictionary<string, object> ReportedProperties { get; set; };
    public long Version { get; set; }
    public DateTimeOffset LastUpdated { get; set; }
}
```
```csharp
public class DeviceQuery
{
}
    public string? DeviceType { get; set; }
    public DeviceStatus? Status { get; set; }
    public Dictionary<string, string>? Tags { get; set; }
    public int? Limit { get; set; }
    public string? ContinuationToken { get; set; }
}
```
```csharp
public class DeviceInfo
{
}
    public string DeviceId { get; set; };
    public string DeviceType { get; set; };
    public DeviceStatus Status { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public Dictionary<string, string> Metadata { get; set; };
}
```
```csharp
public class FirmwareUpdateRequest
{
}
    public string? StrategyId { get; set; }
    public string DeviceId { get; set; };
    public string? DeviceGroupId { get; set; }
    public string FirmwareVersion { get; set; };
    public string FirmwareUrl { get; set; };
    public string? Checksum { get; set; }
    public bool ForceUpdate { get; set; }
}
```
```csharp
public class FirmwareUpdateResult
{
}
    public bool Success { get; set; }
    public string? JobId { get; set; }
    public int DevicesTargeted { get; set; }
    public string? Message { get; set; }
}
```
```csharp
public class DeviceState
{
}
    public string DeviceId { get; set; };
    public DeviceStatus Status { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public string? PreferredProtocol { get; set; }
    public string? ProvisioningMethod { get; set; }
    public TelemetryMessage? LastTelemetry { get; set; }
    public Dictionary<string, string> Metadata { get; set; };
}
```
```csharp
public class TelemetryMessage
{
}
    public string DeviceId { get; set; };
    public string MessageId { get; set; };
    public DateTimeOffset Timestamp { get; set; };
    public Dictionary<string, object> Data { get; set; };
    public byte[]? RawPayload { get; set; }
    public int PayloadSize;;
    public Dictionary<string, string> Properties { get; set; };
}
```
```csharp
public class IngestionResult
{
}
    public bool Success { get; set; }
    public string MessageId { get; set; };
    public DateTimeOffset IngestedAt { get; set; }
    public string? PartitionKey { get; set; }
    public long? SequenceNumber { get; set; }
}
```
```csharp
public class BatchIngestionResult
{
}
    public bool Success { get; set; }
    public int TotalMessages { get; set; }
    public int SuccessfulMessages { get; set; }
    public int FailedMessages { get; set; }
    public List<string> FailedMessageIds { get; set; };
}
```
```csharp
public class TelemetrySubscription
{
}
    public string? DeviceId { get; set; }
    public string? DeviceGroupId { get; set; }
    public string? Topic { get; set; }
    public string? Filter { get; set; }
}
```
```csharp
public class TelemetryBuffer
{
}
    public string DeviceId { get; set; };
    public List<TelemetryMessage> Messages { get; set; };
    public DateTimeOffset LastFlush { get; set; }
}
```
```csharp
public class DeviceCommand
{
}
    public string DeviceId { get; set; };
    public string CommandName { get; set; };
    public Dictionary<string, object> Parameters { get; set; };
    public TimeSpan? Timeout { get; set; }
    public string? ResponseTopic { get; set; }
}
```
```csharp
public class CommandResult
{
}
    public bool Success { get; set; }
    public string CommandId { get; set; };
    public int StatusCode { get; set; }
    public string? Response { get; set; }
    public TimeSpan Latency { get; set; }
}
```
```csharp
public class ProtocolOptions
{
}
    public int QoS { get; set; }
    public bool Retain { get; set; }
    public TimeSpan? Timeout { get; set; }
    public Dictionary<string, string> Headers { get; set; };
}
```
```csharp
public class CoApResponse
{
}
    public int ResponseCode { get; set; }
    public byte[]? Payload { get; set; }
    public string? ContentFormat { get; set; }
    public Dictionary<string, string> Options { get; set; };
}
```
```csharp
public class ModbusResponse
{
}
    public bool Success { get; set; }
    public int SlaveId { get; set; }
    public int StartAddress { get; set; }
    public ushort[]? Registers { get; set; }
    public bool[]? Coils { get; set; }
    public string? ErrorMessage { get; set; }
}
```
```csharp
public class OpcUaNode
{
}
    public string NodeId { get; set; };
    public string DisplayName { get; set; };
    public string NodeClass { get; set; };
    public string? DataType { get; set; }
    public object? Value { get; set; }
    public bool HasChildren { get; set; }
}
```
```csharp
public class ProvisioningRequest
{
}
    public string? StrategyId { get; set; }
    public string DeviceId { get; set; };
    public string? RegistrationId { get; set; }
    public CredentialType CredentialType { get; set; };
    public string? GroupId { get; set; }
    public Dictionary<string, object> InitialTwin { get; set; };
}
```
```csharp
public class ProvisioningResult
{
}
    public bool Success { get; set; }
    public string DeviceId { get; set; };
    public string? AssignedHub { get; set; }
    public string? ConnectionString { get; set; }
    public DateTimeOffset ProvisionedAt { get; set; }
    public string? Message { get; set; }
}
```
```csharp
public class DeviceCredentials
{
}
    public string DeviceId { get; set; };
    public CredentialType Type { get; set; }
    public string? PrimaryKey { get; set; }
    public string? SecondaryKey { get; set; }
    public string? Certificate { get; set; }
    public string? PrivateKey { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
```
```csharp
public class EnrollmentRequest
{
}
    public string? StrategyId { get; set; }
    public string RegistrationId { get; set; };
    public string? GroupId { get; set; }
    public CredentialType AttestationType { get; set; }
    public string? EndorsementKey { get; set; }
    public string? Certificate { get; set; }
}
```
```csharp
public class EnrollmentResult
{
}
    public bool Success { get; set; }
    public string RegistrationId { get; set; };
    public string? EnrollmentGroupId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
```
```csharp
public class AnomalyDetectionRequest
{
}
    public string? StrategyId { get; set; }
    public string DeviceId { get; set; };
    public string? MetricName { get; set; }
    public DateTimeOffset? StartTime { get; set; }
    public DateTimeOffset? EndTime { get; set; }
    public double? Sensitivity { get; set; }
}
```
```csharp
public class AnomalyDetectionResult
{
}
    public bool Success { get; set; }
    public int AnomaliesDetected { get; set; }
    public AnomalySeverity Severity { get; set; }
    public double Confidence { get; set; }
    public List<DetectedAnomaly> Anomalies { get; set; };
}
```
```csharp
public class DetectedAnomaly
{
}
    public DateTimeOffset Timestamp { get; set; }
    public string MetricName { get; set; };
    public double ExpectedValue { get; set; }
    public double ActualValue { get; set; }
    public double Deviation { get; set; }
    public AnomalySeverity Severity { get; set; }
}
```
```csharp
public class PredictionRequest
{
}
    public string? StrategyId { get; set; }
    public string DeviceId { get; set; };
    public string MetricName { get; set; };
    public int HorizonMinutes { get; set; };
}
```
```csharp
public class PredictionResult
{
}
    public bool Success { get; set; }
    public string MetricName { get; set; };
    public List<PredictedValue> Predictions { get; set; };
    public double Confidence { get; set; }
}
```
```csharp
public class PredictedValue
{
}
    public DateTimeOffset Timestamp { get; set; }
    public double Value { get; set; }
    public double LowerBound { get; set; }
    public double UpperBound { get; set; }
}
```
```csharp
public class StreamAnalyticsQuery
{
}
    public string? StrategyId { get; set; }
    public string Query { get; set; };
    public string? InputSource { get; set; }
    public TimeSpan WindowSize { get; set; };
}
```
```csharp
public class StreamAnalyticsResult
{
}
    public bool Success { get; set; }
    public List<Dictionary<string, object>> Results { get; set; };
    public DateTimeOffset QueryTime { get; set; }
}
```
```csharp
public class PatternDetectionRequest
{
}
    public string? StrategyId { get; set; }
    public string DeviceId { get; set; };
    public string PatternType { get; set; };
    public Dictionary<string, object> Parameters { get; set; };
}
```
```csharp
public class PatternDetectionResult
{
}
    public bool Success { get; set; }
    public int PatternsFound { get; set; }
    public List<DetectedPattern> Patterns { get; set; };
}
```
```csharp
public class DetectedPattern
{
}
    public string PatternType { get; set; };
    public DateTimeOffset StartTime { get; set; }
    public DateTimeOffset EndTime { get; set; }
    public double Confidence { get; set; }
    public Dictionary<string, object> Attributes { get; set; };
}
```
```csharp
public class DeviceAuthenticationRequest
{
}
    public string? StrategyId { get; set; }
    public string DeviceId { get; set; };
    public CredentialType CredentialType { get; set; }
    public string? Credential { get; set; }
    public string? Certificate { get; set; }
}
```
```csharp
public class AuthenticationResult
{
}
    public bool Success { get; set; }
    public string DeviceId { get; set; };
    public string? Token { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public string? Message { get; set; }
}
```
```csharp
public class CredentialRotationResult
{
}
    public bool Success { get; set; }
    public string DeviceId { get; set; };
    public DeviceCredentials? NewCredentials { get; set; }
    public DateTimeOffset RotatedAt { get; set; }
}
```
```csharp
public class SecurityAssessment
{
}
    public string DeviceId { get; set; };
    public int SecurityScore { get; set; }
    public ThreatLevel ThreatLevel { get; set; }
    public List<SecurityFinding> Findings { get; set; };
    public DateTimeOffset AssessedAt { get; set; }
}
```
```csharp
public class SecurityFinding
{
}
    public string FindingId { get; set; };
    public string Title { get; set; };
    public string Description { get; set; };
    public ThreatLevel Severity { get; set; }
    public string? Remediation { get; set; }
}
```
```csharp
public class ThreatDetectionRequest
{
}
    public string? StrategyId { get; set; }
    public string? DeviceId { get; set; }
    public DateTimeOffset? StartTime { get; set; }
    public DateTimeOffset? EndTime { get; set; }
}
```
```csharp
public class ThreatDetectionResult
{
}
    public bool Success { get; set; }
    public int ThreatsDetected { get; set; }
    public List<DetectedThreat> Threats { get; set; };
}
```
```csharp
public class DetectedThreat
{
}
    public string ThreatId { get; set; };
    public string ThreatType { get; set; };
    public ThreatLevel Severity { get; set; }
    public string? AffectedDeviceId { get; set; }
    public DateTimeOffset DetectedAt { get; set; }
    public string Description { get; set; };
}
```
```csharp
public class EdgeDeploymentRequest
{
}
    public string? StrategyId { get; set; }
    public string EdgeDeviceId { get; set; };
    public string ModuleName { get; set; };
    public string ImageUri { get; set; };
    public Dictionary<string, object> ModuleSettings { get; set; };
    public Dictionary<string, string> EnvironmentVariables { get; set; };
}
```
```csharp
public class EdgeDeploymentResult
{
}
    public bool Success { get; set; }
    public string DeploymentId { get; set; };
    public string ModuleName { get; set; };
    public DateTimeOffset DeployedAt { get; set; }
    public string? Message { get; set; }
}
```
```csharp
public class EdgeSyncRequest
{
}
    public string? StrategyId { get; set; }
    public string EdgeDeviceId { get; set; };
    public string? DataPath { get; set; }
    public bool FullSync { get; set; }
}
```
```csharp
public class SyncResult
{
}
    public bool Success { get; set; }
    public int ItemsSynced { get; set; }
    public long BytesSynced { get; set; }
    public DateTimeOffset SyncedAt { get; set; }
}
```
```csharp
public class EdgeComputeRequest
{
}
    public string? StrategyId { get; set; }
    public string EdgeDeviceId { get; set; };
    public string FunctionName { get; set; };
    public Dictionary<string, object> Input { get; set; };
    public TimeSpan? Timeout { get; set; }
}
```
```csharp
public class EdgeComputeResult
{
}
    public bool Success { get; set; }
    public Dictionary<string, object> Output { get; set; };
    public TimeSpan ExecutionTime { get; set; }
    public string? Error { get; set; }
}
```
```csharp
public class EdgeDeviceStatus
{
}
    public string EdgeDeviceId { get; set; };
    public bool IsOnline { get; set; }
    public DateTimeOffset LastSeen { get; set; }
    public List<EdgeModuleStatus> Modules { get; set; };
    public EdgeResourceUsage ResourceUsage { get; set; };
}
```
```csharp
public class EdgeModuleStatus
{
}
    public string ModuleName { get; set; };
    public string Status { get; set; };
    public string Version { get; set; };
    public DateTimeOffset LastStarted { get; set; }
}
```
```csharp
public class EdgeResourceUsage
{
}
    public double CpuPercent { get; set; }
    public long MemoryUsedBytes { get; set; }
    public long DiskUsedBytes { get; set; }
    public double NetworkInBytes { get; set; }
    public double NetworkOutBytes { get; set; }
}
```
```csharp
public class TransformationRequest
{
}
    public string? StrategyId { get; set; }
    public string SourceFormat { get; set; };
    public string TargetFormat { get; set; };
    public byte[] InputData { get; set; };
    public Dictionary<string, object> Options { get; set; };
}
```
```csharp
public class TransformationResult
{
}
    public bool Success { get; set; }
    public byte[] OutputData { get; set; };
    public string TargetFormat { get; set; };
    public Dictionary<string, object> Metadata { get; set; };
}
```
```csharp
public class ProtocolTranslationRequest
{
}
    public string? StrategyId { get; set; }
    public string SourceProtocol { get; set; };
    public string TargetProtocol { get; set; };
    public byte[] Message { get; set; };
}
```
```csharp
public class ProtocolTranslationResult
{
}
    public bool Success { get; set; }
    public byte[] TranslatedMessage { get; set; };
    public string TargetProtocol { get; set; };
}
```
```csharp
public class EnrichmentRequest
{
}
    public string? StrategyId { get; set; }
    public TelemetryMessage Message { get; set; };
    public List<string> EnrichmentSources { get; set; };
}
```
```csharp
public class EnrichmentResult
{
}
    public bool Success { get; set; }
    public TelemetryMessage EnrichedMessage { get; set; };
    public Dictionary<string, object> AddedFields { get; set; };
}
```
```csharp
public class NormalizationRequest
{
}
    public string? StrategyId { get; set; }
    public TelemetryMessage Message { get; set; };
    public string TargetSchema { get; set; };
}
```
```csharp
public class NormalizationResult
{
}
    public bool Success { get; set; }
    public TelemetryMessage NormalizedMessage { get; set; };
    public List<string> MappedFields { get; set; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateIoTIntegration/UltimateIoTIntegrationPlugin.cs
```csharp
public sealed class UltimateIoTIntegrationPlugin : StreamingPluginBase, IDisposable
{
#endregion
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override PluginCategory Category;;
    public string SemanticDescription;;
    public string[] SemanticTags;;
    public IoTStrategyRegistry Registry;;
    public UltimateIoTIntegrationPlugin();
    public async Task<DeviceRegistration> RegisterDeviceAsync(DeviceRegistrationRequest request, CancellationToken ct = default);
    public async Task<DeviceTwin> GetDeviceTwinAsync(string deviceId, string? strategyId = null, CancellationToken ct = default);
    public async Task UpdateDeviceTwinAsync(string deviceId, Dictionary<string, object> desiredProperties, string? strategyId = null, CancellationToken ct = default);
    public async Task<IEnumerable<DeviceInfo>> ListDevicesAsync(DeviceQuery? query = null, string? strategyId = null, CancellationToken ct = default);
    public async Task<FirmwareUpdateResult> UpdateFirmwareAsync(FirmwareUpdateRequest request, CancellationToken ct = default);
    public async Task<IngestionResult> IngestTelemetryAsync(TelemetryMessage message, string? strategyId = null, CancellationToken ct = default);
    public async Task<BatchIngestionResult> IngestBatchAsync(IEnumerable<TelemetryMessage> messages, string? strategyId = null, CancellationToken ct = default);
    public async IAsyncEnumerable<TelemetryMessage> SubscribeTelemetryAsync(TelemetrySubscription subscription, string? strategyId = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default);
    public async Task<CommandResult> SendCommandAsync(DeviceCommand command, string? strategyId = null, CancellationToken ct = default);
    public async Task PublishMqttAsync(string topic, byte[] payload, MqttQoS qos = MqttQoS.AtLeastOnce, bool retain = false, CancellationToken ct = default);
    public async Task<CoApResponse> SendCoApRequestAsync(string deviceEndpoint, CoApMethod method, string resourcePath, byte[]? payload = null, CancellationToken ct = default);
    public async Task<ModbusResponse> ReadModbusAsync(string deviceAddress, int slaveId, int registerAddress, int registerCount, ModbusFunction function = ModbusFunction.ReadHoldingRegisters, CancellationToken ct = default);
    public async Task<IEnumerable<OpcUaNode>> BrowseOpcUaNodesAsync(string serverEndpoint, string? nodeId = null, CancellationToken ct = default);
    public async Task<ProvisioningResult> ProvisionDeviceAsync(ProvisioningRequest request, CancellationToken ct = default);
    public async Task<DeviceCredentials> GenerateCredentialsAsync(string deviceId, CredentialType credentialType = CredentialType.X509Certificate, string? strategyId = null, CancellationToken ct = default);
    public async Task<EnrollmentResult> EnrollDeviceAsync(EnrollmentRequest request, CancellationToken ct = default);
    public async Task<AnomalyDetectionResult> DetectAnomaliesAsync(AnomalyDetectionRequest request, CancellationToken ct = default);
    public async Task<PredictionResult> PredictTelemetryAsync(PredictionRequest request, CancellationToken ct = default);
    public async Task<StreamAnalyticsResult> AnalyzeStreamAsync(StreamAnalyticsQuery query, CancellationToken ct = default);
    public async Task<PatternDetectionResult> DetectPatternsAsync(PatternDetectionRequest request, CancellationToken ct = default);
    public async Task<AuthenticationResult> AuthenticateDeviceAsync(DeviceAuthenticationRequest request, CancellationToken ct = default);
    public async Task<CredentialRotationResult> RotateCredentialsAsync(string deviceId, string? strategyId = null, CancellationToken ct = default);
    public async Task<SecurityAssessment> AssessSecurityAsync(string deviceId, string? strategyId = null, CancellationToken ct = default);
    public async Task<ThreatDetectionResult> DetectThreatsAsync(ThreatDetectionRequest request, CancellationToken ct = default);
    public async Task<EdgeDeploymentResult> DeployEdgeModuleAsync(EdgeDeploymentRequest request, CancellationToken ct = default);
    public async Task<SyncResult> SyncEdgeDataAsync(EdgeSyncRequest request, CancellationToken ct = default);
    public async Task<EdgeComputeResult> ExecuteEdgeComputeAsync(EdgeComputeRequest request, CancellationToken ct = default);
    public async Task<EdgeDeviceStatus> GetEdgeStatusAsync(string edgeDeviceId, string? strategyId = null, CancellationToken ct = default);
    public async Task<TransformationResult> TransformDataAsync(TransformationRequest request, CancellationToken ct = default);
    public async Task<ProtocolTranslationResult> TranslateProtocolAsync(ProtocolTranslationRequest request, CancellationToken ct = default);
    public async Task<EnrichmentResult> EnrichTelemetryAsync(EnrichmentRequest request, CancellationToken ct = default);
    public async Task<NormalizationResult> NormalizeTelemetryAsync(NormalizationRequest request, CancellationToken ct = default);
    protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct);
    protected override Task OnStartWithoutIntelligenceAsync(CancellationToken ct);
    protected override Task OnStartCoreAsync(CancellationToken ct);
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities
{
    get
    {
        var capabilities = new List<RegisteredCapability>
        {
            new()
            {
                CapabilityId = $"{Id}.device-management",
                DisplayName = "IoT Device Management",
                Description = "Register, monitor, and manage IoT devices at scale",
                Category = SDK.Contracts.CapabilityCategory.Custom,
                SubCategory = "IoT",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[]
                {
                    "iot",
                    "device",
                    "management",
                    "registry",
                    "twin"
                }
            },
            new()
            {
                CapabilityId = $"{Id}.sensor-ingestion",
                DisplayName = "Sensor Data Ingestion",
                Description = "Ingest telemetry from millions of sensors with streaming and batch support",
                Category = SDK.Contracts.CapabilityCategory.Custom,
                SubCategory = "IoT",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[]
                {
                    "iot",
                    "sensor",
                    "telemetry",
                    "ingestion",
                    "streaming"
                }
            },
            new()
            {
                CapabilityId = $"{Id}.protocols",
                DisplayName = "IoT Protocol Support",
                Description = "Multi-protocol support: MQTT, CoAP, LwM2M, Modbus, OPC-UA",
                Category = SDK.Contracts.CapabilityCategory.Custom,
                SubCategory = "IoT",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[]
                {
                    "iot",
                    "mqtt",
                    "coap",
                    "lwm2m",
                    "modbus",
                    "opcua",
                    "protocol"
                }
            },
            new()
            {
                CapabilityId = $"{Id}.provisioning",
                DisplayName = "Device Provisioning",
                Description = "Zero-touch provisioning with X.509, TPM, and DPS support",
                Category = SDK.Contracts.CapabilityCategory.Custom,
                SubCategory = "IoT",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[]
                {
                    "iot",
                    "provisioning",
                    "x509",
                    "tpm",
                    "dps",
                    "zero-touch"
                }
            },
            new()
            {
                CapabilityId = $"{Id}.analytics",
                DisplayName = "IoT Analytics",
                Description = "Real-time analytics, anomaly detection, and predictive maintenance",
                Category = SDK.Contracts.CapabilityCategory.Custom,
                SubCategory = "IoT",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[]
                {
                    "iot",
                    "analytics",
                    "anomaly",
                    "prediction",
                    "pattern"
                }
            },
            new()
            {
                CapabilityId = $"{Id}.security",
                DisplayName = "IoT Security",
                Description = "Device authentication, encryption, and threat detection",
                Category = SDK.Contracts.CapabilityCategory.Custom,
                SubCategory = "IoT",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[]
                {
                    "iot",
                    "security",
                    "authentication",
                    "encryption",
                    "threat"
                }
            },
            new()
            {
                CapabilityId = $"{Id}.edge",
                DisplayName = "Edge Integration",
                Description = "Edge compute, fog computing, and offline-first sync",
                Category = SDK.Contracts.CapabilityCategory.Custom,
                SubCategory = "IoT",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[]
                {
                    "iot",
                    "edge",
                    "fog",
                    "compute",
                    "offline",
                    "sync"
                }
            },
            new()
            {
                CapabilityId = $"{Id}.transformation",
                DisplayName = "Data Transformation",
                Description = "Protocol translation, format conversion, and data enrichment",
                Category = SDK.Contracts.CapabilityCategory.Custom,
                SubCategory = "IoT",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                Tags = new[]
                {
                    "iot",
                    "transformation",
                    "translation",
                    "enrichment",
                    "normalization"
                }
            }
        };
        // Add capabilities from all strategies
        capabilities.AddRange(_registry.GetAllStrategyCapabilities());
        return capabilities;
    }
}
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge();
    public override async Task OnMessageAsync(PluginMessage message);
    protected override Dictionary<string, object> GetMetadata();
    protected override void Dispose(bool disposing);
    public override Task PublishAsync(string topic, Stream data, CancellationToken ct = default);;
    public override async IAsyncEnumerable<Dictionary<string, object>> SubscribeAsync(string topic, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateIoTIntegration/IoTStrategyRegistry.cs
```csharp
public sealed class IoTStrategyRegistry
{
}
    public int Count;;
    public void Register(IIoTStrategyBase strategy);
    public IIoTStrategyBase? GetStrategy(string strategyId);
    public T GetStrategy<T>(string strategyId)
    where T : class, IIoTStrategyBase;
    public IReadOnlyCollection<IIoTStrategyBase> GetAllStrategies();
    public IReadOnlyCollection<IIoTStrategyBase> GetByCategory(IoTStrategyCategory category);
    public IIoTStrategyBase? SelectBestStrategy(IoTStrategyCategory category);
    public Dictionary<string, int> GetCategorySummary();
    public void ConfigureIntelligence(IMessageBus? messageBus);
    public IEnumerable<RegisteredCapability> GetAllStrategyCapabilities();
    public IEnumerable<KnowledgeObject> GetAllStrategyKnowledge();
}
```
```csharp
public static class IoTTopics
{
}
    public const string IntelligenceDeviceRecommendation = "iot.intelligence.device.recommendation";
    public const string IntelligenceDeviceRecommendationResponse = "iot.intelligence.device.recommendation.response";
    public const string IntelligenceAnomalyDetection = "iot.intelligence.anomaly.detection";
    public const string IntelligenceAnomalyDetectionResponse = "iot.intelligence.anomaly.detection.response";
    public const string TelemetryIngested = "iot.telemetry.ingested";
    public const string DeviceRegistered = "iot.device.registered";
    public const string DeviceProvisioned = "iot.device.provisioned";
    public const string CommandSent = "iot.command.sent";
    public const string EdgeDeployed = "iot.edge.deployed";
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateIoTIntegration/Strategies/Edge/AdvancedEdgeStrategies.cs
```csharp
public sealed class EdgeCachingStrategy : EdgeIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public EdgeCachingStrategy(int maxEntries = 10000, EdgeCacheMode mode = EdgeCacheMode.WriteThrough);
    public EdgeCacheEntry? Get(string key);
    public void Put(string key, byte[] data, TimeSpan? ttl = null, Dictionary<string, string>? metadata = null);
    public bool Invalidate(string key);
    public IEnumerable<EdgeCacheEntry> FlushDirty();
    public EdgeCacheStats GetStats();
    public override Task<EdgeDeploymentResult> DeployModuleAsync(EdgeDeploymentRequest request, CancellationToken ct = default);;
    public override Task<SyncResult> SyncAsync(EdgeSyncRequest request, CancellationToken ct = default);
    public override Task<EdgeComputeResult> ExecuteComputeAsync(EdgeComputeRequest request, CancellationToken ct = default);;
    public override Task<EdgeDeviceStatus> GetEdgeStatusAsync(string edgeDeviceId, CancellationToken ct = default);;
}
```
```csharp
public sealed class EdgeCacheEntry
{
}
    public required string Key { get; init; }
    public required byte[] Data { get; init; }
    public int SizeBytes { get; init; }
    public DateTimeOffset CachedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public Dictionary<string, string> Metadata { get; init; };
    public bool IsDirty { get => _isDirty; set => _isDirty = value; }
    public long HitCount;;
    public void IncrementHitCount();;
}
```
```csharp
public sealed class EdgeCacheStats
{
}
    public int TotalEntries { get; init; }
    public long TotalSizeBytes { get; init; }
    public int DirtyEntries { get; init; }
    public int ExpiredEntries { get; init; }
    public long TotalHits { get; init; }
    public int MaxEntries { get; init; }
    public EdgeCacheMode Mode { get; init; }
}
```
```csharp
public sealed class OfflineSyncStrategy : EdgeIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public void RecordOfflineOperation(string key, OfflineOperationType type, byte[]? data = null);
    public async Task<OfflineSyncResult> ReplaySyncAsync(Func<OfflineOperation, Task<bool>> applyFunc, CancellationToken ct = default);
    public void SetOnlineStatus(bool isOnline);;
    public int PendingOperationCount;;
    public override Task<EdgeDeploymentResult> DeployModuleAsync(EdgeDeploymentRequest request, CancellationToken ct = default);;
    public override Task<SyncResult> SyncAsync(EdgeSyncRequest request, CancellationToken ct = default);;
    public override Task<EdgeComputeResult> ExecuteComputeAsync(EdgeComputeRequest request, CancellationToken ct = default);;
    public override Task<EdgeDeviceStatus> GetEdgeStatusAsync(string edgeDeviceId, CancellationToken ct = default);;
}
```
```csharp
public sealed class OfflineOperation
{
}
    public required string OperationId { get; init; }
    public required string Key { get; init; }
    public required OfflineOperationType Type { get; init; }
    public byte[]? Data { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public required string NodeId { get; init; }
    public Dictionary<string, long> VectorClock { get; init; };
}
```
```csharp
public sealed class OfflineSyncResult
{
}
    public int Applied { get; set; }
    public int Conflicts { get; set; }
    public int Failed { get; set; }
    public int PendingRemaining { get; set; }
    public List<string> ConflictedKeys { get; };
}
```
```csharp
public sealed class BandwidthEstimationStrategy : EdgeIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public BandwidthEstimationStrategy(int maxSamples = 100);
    public void RecordSample(long bytesTransferred, TimeSpan duration, BandwidthDirection direction);
    public BandwidthEstimate GetEstimate();
    public TransferSuggestion SuggestTransferParameters(long dataSize);
    public override Task<EdgeDeploymentResult> DeployModuleAsync(EdgeDeploymentRequest request, CancellationToken ct = default);;
    public override Task<SyncResult> SyncAsync(EdgeSyncRequest request, CancellationToken ct = default);;
    public override Task<EdgeComputeResult> ExecuteComputeAsync(EdgeComputeRequest request, CancellationToken ct = default);
    public override Task<EdgeDeviceStatus> GetEdgeStatusAsync(string edgeDeviceId, CancellationToken ct = default);;
}
```
```csharp
public sealed class BandwidthSample
{
}
    public long BytesTransferred { get; init; }
    public TimeSpan Duration { get; init; }
    public BandwidthDirection Direction { get; init; }
    public double Bps { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}
```
```csharp
public sealed class BandwidthEstimate
{
}
    public double EstimatedBps { get; init; }
    public double UploadBps { get; init; }
    public double DownloadBps { get; init; }
    public double Confidence { get; init; }
    public int SampleCount { get; init; }
    public double Jitter { get; init; }
    public DateTimeOffset LastMeasured { get; init; }
}
```
```csharp
public sealed class TransferSuggestion
{
}
    public int ChunkSize { get; init; }
    public int ConcurrentStreams { get; init; }
    public bool UseCompression { get; init; }
    public TimeSpan EstimatedTransferTime { get; init; }
    public DataTransferPriority Priority { get; init; }
}
```
```csharp
public sealed class DataPrioritizationStrategy : EdgeIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public DataPrioritizationStrategy();
    public void AddClassificationRule(string name, DataClassificationRule rule);;
    public void Enqueue(string topic, byte[] data, Dictionary<string, string>? metadata = null);
    public PrioritizedDataItem? Dequeue();
    public IReadOnlyList<PrioritizedDataItem> DequeueBatch(long maxBytes);
    public Dictionary<DataTransferPriority, int> GetQueueDepths();
    public override Task<EdgeDeploymentResult> DeployModuleAsync(EdgeDeploymentRequest request, CancellationToken ct = default);;
    public override Task<SyncResult> SyncAsync(EdgeSyncRequest request, CancellationToken ct = default);
    public override Task<EdgeComputeResult> ExecuteComputeAsync(EdgeComputeRequest request, CancellationToken ct = default);;
    public override Task<EdgeDeviceStatus> GetEdgeStatusAsync(string edgeDeviceId, CancellationToken ct = default);;
}
```
```csharp
public sealed class DataClassificationRule
{
}
    public required string Pattern { get; init; }
    public required DataTransferPriority Priority { get; init; }
    public TimeSpan MaxLatency { get; init; };
}
```
```csharp
public sealed class PrioritizedDataItem
{
}
    public required string ItemId { get; init; }
    public required string Topic { get; init; }
    public required byte[] Data { get; init; }
    public required DataTransferPriority Priority { get; init; }
    public required DateTimeOffset EnqueuedAt { get; init; }
    public Dictionary<string, string> Metadata { get; init; };
}
```
```csharp
public sealed class EdgeAnalyticsStrategy : EdgeIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public void Ingest(string metricId, double value, DateTimeOffset? timestamp = null);
    public MetricAggregation GetAggregation(string metricId);
    public AnomalyResult CheckAnomaly(string metricId, double value);
    public TrendAnalysis GetTrend(string metricId);
    public override Task<EdgeDeploymentResult> DeployModuleAsync(EdgeDeploymentRequest request, CancellationToken ct = default);;
    public override Task<SyncResult> SyncAsync(EdgeSyncRequest request, CancellationToken ct = default);;
    public override Task<EdgeComputeResult> ExecuteComputeAsync(EdgeComputeRequest request, CancellationToken ct = default);
    public override Task<EdgeDeviceStatus> GetEdgeStatusAsync(string edgeDeviceId, CancellationToken ct = default);;
}
```
```csharp
public sealed class MetricAggregation
{
}
    public required string MetricId { get; init; }
    public int SampleCount { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public double Mean { get; init; }
    public double Median { get; init; }
    public double P95 { get; init; }
    public double P99 { get; init; }
    public double StdDev { get; init; }
    public DateTimeOffset WindowStart { get; init; }
    public DateTimeOffset WindowEnd { get; init; }
}
```
```csharp
public sealed class AnomalyResult
{
}
    public bool IsAnomaly { get; init; }
    public double Confidence { get; init; }
    public double ZScore { get; init; }
    public string? Reason { get; init; }
}
```
```csharp
public sealed class TrendAnalysis
{
}
    public required string MetricId { get; init; }
    public TrendDirection Direction { get; init; }
    public double Slope { get; init; }
    public double Intercept { get; init; }
    public int SampleCount { get; init; }
}
```
```csharp
internal sealed class SlidingWindow
{
}
    public DateTimeOffset OldestTimestamp { get; private set; };
    public DateTimeOffset NewestTimestamp { get; private set; };
    public SlidingWindow(TimeSpan windowSize);
    public void Add(double value, DateTimeOffset timestamp);
    public double[] GetValues();;
    public (double Value, DateTimeOffset Timestamp)[] GetTimedValues();;
}
```
```csharp
internal sealed class AnomalyDetector
{
}
    public void Update(double value);
    public AnomalyResult Check(double value);
}
```
```csharp
internal static class StatisticsExtensions
{
}
    public static double StandardDeviation(this double[] values);
    public static double StandardDeviation(this IEnumerable<double> values);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateIoTIntegration/Strategies/Edge/EdgeStrategies.cs
```csharp
public abstract class EdgeIntegrationStrategyBase : IoTStrategyBase, IEdgeIntegrationStrategy
{
}
    public override IoTStrategyCategory Category;;
    public abstract Task<EdgeDeploymentResult> DeployModuleAsync(EdgeDeploymentRequest request, CancellationToken ct = default);;
    public abstract Task<SyncResult> SyncAsync(EdgeSyncRequest request, CancellationToken ct = default);;
    public abstract Task<EdgeComputeResult> ExecuteComputeAsync(EdgeComputeRequest request, CancellationToken ct = default);;
    public abstract Task<EdgeDeviceStatus> GetEdgeStatusAsync(string edgeDeviceId, CancellationToken ct = default);;
    public virtual Task<IEnumerable<EdgeModuleStatus>> ListModulesAsync(string edgeDeviceId, CancellationToken ct = default);
    public virtual Task<bool> RemoveModuleAsync(string edgeDeviceId, string moduleName, CancellationToken ct = default);
}
```
```csharp
public class EdgeDeploymentStrategy : EdgeIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override bool IsProductionReady;;
    public override Task<EdgeDeploymentResult> DeployModuleAsync(EdgeDeploymentRequest request, CancellationToken ct = default);
    public override Task<SyncResult> SyncAsync(EdgeSyncRequest request, CancellationToken ct = default);
    public override Task<EdgeComputeResult> ExecuteComputeAsync(EdgeComputeRequest request, CancellationToken ct = default);
    public override Task<EdgeDeviceStatus> GetEdgeStatusAsync(string edgeDeviceId, CancellationToken ct = default);
    public override Task<IEnumerable<EdgeModuleStatus>> ListModulesAsync(string edgeDeviceId, CancellationToken ct = default);
}
```
```csharp
public class EdgeSyncStrategy : EdgeIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override bool IsProductionReady;;
    public override Task<EdgeDeploymentResult> DeployModuleAsync(EdgeDeploymentRequest request, CancellationToken ct = default);
    public override Task<SyncResult> SyncAsync(EdgeSyncRequest request, CancellationToken ct = default);
    public override Task<EdgeComputeResult> ExecuteComputeAsync(EdgeComputeRequest request, CancellationToken ct = default);
    public override Task<EdgeDeviceStatus> GetEdgeStatusAsync(string edgeDeviceId, CancellationToken ct = default);
}
```
```csharp
public class EdgeComputeStrategy : EdgeIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override bool IsProductionReady;;
    public override Task<EdgeDeploymentResult> DeployModuleAsync(EdgeDeploymentRequest request, CancellationToken ct = default);
    public override Task<SyncResult> SyncAsync(EdgeSyncRequest request, CancellationToken ct = default);
    public override Task<EdgeComputeResult> ExecuteComputeAsync(EdgeComputeRequest request, CancellationToken ct = default);
    public override Task<EdgeDeviceStatus> GetEdgeStatusAsync(string edgeDeviceId, CancellationToken ct = default);
}
```
```csharp
public class EdgeMonitoringStrategy : EdgeIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override bool IsProductionReady;;
    public override Task<EdgeDeploymentResult> DeployModuleAsync(EdgeDeploymentRequest request, CancellationToken ct = default);
    public override Task<SyncResult> SyncAsync(EdgeSyncRequest request, CancellationToken ct = default);
    public override Task<EdgeComputeResult> ExecuteComputeAsync(EdgeComputeRequest request, CancellationToken ct = default);
    public override Task<EdgeDeviceStatus> GetEdgeStatusAsync(string edgeDeviceId, CancellationToken ct = default);
}
```
```csharp
public class FogComputingStrategy : EdgeIntegrationStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override bool IsProductionReady;;
    public override Task<EdgeDeploymentResult> DeployModuleAsync(EdgeDeploymentRequest request, CancellationToken ct = default);
    public override Task<SyncResult> SyncAsync(EdgeSyncRequest request, CancellationToken ct = default);
    public override Task<EdgeComputeResult> ExecuteComputeAsync(EdgeComputeRequest request, CancellationToken ct = default);
    public override Task<EdgeDeviceStatus> GetEdgeStatusAsync(string edgeDeviceId, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateIoTIntegration/Strategies/Transformation/TransformationStrategies.cs
```csharp
public abstract class DataTransformationStrategyBase : IoTStrategyBase, IDataTransformationStrategy
{
}
    public override IoTStrategyCategory Category;;
    public abstract string[] SupportedSourceFormats { get; }
    public abstract string[] SupportedTargetFormats { get; }
    public abstract Task<TransformationResult> TransformAsync(TransformationRequest request, CancellationToken ct = default);;
    public abstract Task<ProtocolTranslationResult> TranslateProtocolAsync(ProtocolTranslationRequest request, CancellationToken ct = default);;
    public abstract Task<EnrichmentResult> EnrichAsync(EnrichmentRequest request, CancellationToken ct = default);;
    public abstract Task<NormalizationResult> NormalizeAsync(NormalizationRequest request, CancellationToken ct = default);;
    public virtual Task<bool> ValidateSchemaAsync(byte[] data, string schema, CancellationToken ct = default);
}
```
```csharp
public class FormatConversionStrategy : DataTransformationStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override string[] SupportedSourceFormats;;
    public override string[] SupportedTargetFormats;;
    public override Task<TransformationResult> TransformAsync(TransformationRequest request, CancellationToken ct = default);
    public override Task<ProtocolTranslationResult> TranslateProtocolAsync(ProtocolTranslationRequest request, CancellationToken ct = default);
    public override Task<EnrichmentResult> EnrichAsync(EnrichmentRequest request, CancellationToken ct = default);
    public override Task<NormalizationResult> NormalizeAsync(NormalizationRequest request, CancellationToken ct = default);
}
```
```csharp
public class ProtocolTranslationStrategy : DataTransformationStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override string[] SupportedSourceFormats;;
    public override string[] SupportedTargetFormats;;
    public override Task<TransformationResult> TransformAsync(TransformationRequest request, CancellationToken ct = default);
    public override Task<ProtocolTranslationResult> TranslateProtocolAsync(ProtocolTranslationRequest request, CancellationToken ct = default);
    public override Task<EnrichmentResult> EnrichAsync(EnrichmentRequest request, CancellationToken ct = default);
    public override Task<NormalizationResult> NormalizeAsync(NormalizationRequest request, CancellationToken ct = default);
}
```
```csharp
public class DataEnrichmentStrategy : DataTransformationStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override string[] SupportedSourceFormats;;
    public override string[] SupportedTargetFormats;;
    public override Task<TransformationResult> TransformAsync(TransformationRequest request, CancellationToken ct = default);
    public override Task<ProtocolTranslationResult> TranslateProtocolAsync(ProtocolTranslationRequest request, CancellationToken ct = default);
    public override Task<EnrichmentResult> EnrichAsync(EnrichmentRequest request, CancellationToken ct = default);
    public override Task<NormalizationResult> NormalizeAsync(NormalizationRequest request, CancellationToken ct = default);
}
```
```csharp
public class DataNormalizationStrategy : DataTransformationStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override string[] SupportedSourceFormats;;
    public override string[] SupportedTargetFormats;;
    public override Task<TransformationResult> TransformAsync(TransformationRequest request, CancellationToken ct = default);
    public override Task<ProtocolTranslationResult> TranslateProtocolAsync(ProtocolTranslationRequest request, CancellationToken ct = default);
    public override Task<EnrichmentResult> EnrichAsync(EnrichmentRequest request, CancellationToken ct = default);
    public override Task<NormalizationResult> NormalizeAsync(NormalizationRequest request, CancellationToken ct = default);
    public override Task<bool> ValidateSchemaAsync(byte[] data, string schema, CancellationToken ct = default);
}
```
```csharp
public class SchemaMappingStrategy : DataTransformationStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override string[] SupportedSourceFormats;;
    public override string[] SupportedTargetFormats;;
    public override Task<TransformationResult> TransformAsync(TransformationRequest request, CancellationToken ct = default);
    public override Task<ProtocolTranslationResult> TranslateProtocolAsync(ProtocolTranslationRequest request, CancellationToken ct = default);
    public override Task<EnrichmentResult> EnrichAsync(EnrichmentRequest request, CancellationToken ct = default);
    public override Task<NormalizationResult> NormalizeAsync(NormalizationRequest request, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateIoTIntegration/Strategies/Security/SecurityStrategies.cs
```csharp
public abstract class IoTSecurityStrategyBase : IoTStrategyBase, IIoTSecurityStrategy
{
}
    protected readonly BoundedDictionary<string, string> DeviceTokens = new BoundedDictionary<string, string>(1000);
    public override IoTStrategyCategory Category;;
    public abstract Task<AuthenticationResult> AuthenticateAsync(DeviceAuthenticationRequest request, CancellationToken ct = default);;
    public abstract Task<CredentialRotationResult> RotateCredentialsAsync(string deviceId, CancellationToken ct = default);;
    public abstract Task<SecurityAssessment> AssessSecurityAsync(string deviceId, CancellationToken ct = default);;
    public abstract Task<ThreatDetectionResult> DetectThreatsAsync(ThreatDetectionRequest request, CancellationToken ct = default);;
    public virtual async Task<byte[]> EncryptAsync(string deviceId, byte[] data, CancellationToken ct = default);
    public virtual async Task<byte[]> DecryptAsync(string deviceId, byte[] data, CancellationToken ct = default);
}
```
```csharp
public class DeviceAuthenticationStrategy : IoTSecurityStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override Task<AuthenticationResult> AuthenticateAsync(DeviceAuthenticationRequest request, CancellationToken ct = default);
    public override Task<CredentialRotationResult> RotateCredentialsAsync(string deviceId, CancellationToken ct = default);
    public override Task<SecurityAssessment> AssessSecurityAsync(string deviceId, CancellationToken ct = default);
    public override Task<ThreatDetectionResult> DetectThreatsAsync(ThreatDetectionRequest request, CancellationToken ct = default);
}
```
```csharp
public class CredentialRotationStrategy : IoTSecurityStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override Task<AuthenticationResult> AuthenticateAsync(DeviceAuthenticationRequest request, CancellationToken ct = default);
    public override Task<CredentialRotationResult> RotateCredentialsAsync(string deviceId, CancellationToken ct = default);
    public override Task<SecurityAssessment> AssessSecurityAsync(string deviceId, CancellationToken ct = default);
    public override Task<ThreatDetectionResult> DetectThreatsAsync(ThreatDetectionRequest request, CancellationToken ct = default);
}
```
```csharp
public class SecurityAssessmentStrategy : IoTSecurityStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override Task<AuthenticationResult> AuthenticateAsync(DeviceAuthenticationRequest request, CancellationToken ct = default);
    public override Task<CredentialRotationResult> RotateCredentialsAsync(string deviceId, CancellationToken ct = default);
    public override Task<SecurityAssessment> AssessSecurityAsync(string deviceId, CancellationToken ct = default);
    public override Task<ThreatDetectionResult> DetectThreatsAsync(ThreatDetectionRequest request, CancellationToken ct = default);
}
```
```csharp
public class ThreatDetectionStrategy : IoTSecurityStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override Task<AuthenticationResult> AuthenticateAsync(DeviceAuthenticationRequest request, CancellationToken ct = default);
    public override Task<CredentialRotationResult> RotateCredentialsAsync(string deviceId, CancellationToken ct = default);
    public override Task<SecurityAssessment> AssessSecurityAsync(string deviceId, CancellationToken ct = default);
    public override Task<ThreatDetectionResult> DetectThreatsAsync(ThreatDetectionRequest request, CancellationToken ct = default);
}
```
```csharp
public class CertificateManagementStrategy : IoTSecurityStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override Task<AuthenticationResult> AuthenticateAsync(DeviceAuthenticationRequest request, CancellationToken ct = default);
    public override Task<CredentialRotationResult> RotateCredentialsAsync(string deviceId, CancellationToken ct = default);
    public override Task<SecurityAssessment> AssessSecurityAsync(string deviceId, CancellationToken ct = default);
    public override Task<ThreatDetectionResult> DetectThreatsAsync(ThreatDetectionRequest request, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateIoTIntegration/Strategies/Analytics/AnalyticsStrategies.cs
```csharp
public abstract class IoTAnalyticsStrategyBase : IoTStrategyBase, IIoTAnalyticsStrategy
{
}
    public override IoTStrategyCategory Category;;
    public abstract Task<AnomalyDetectionResult> DetectAnomaliesAsync(AnomalyDetectionRequest request, CancellationToken ct = default);;
    public abstract Task<PredictionResult> PredictAsync(PredictionRequest request, CancellationToken ct = default);;
    public abstract Task<StreamAnalyticsResult> AnalyzeStreamAsync(StreamAnalyticsQuery query, CancellationToken ct = default);;
    public abstract Task<PatternDetectionResult> DetectPatternsAsync(PatternDetectionRequest request, CancellationToken ct = default);;
    public virtual Task<Dictionary<string, double>> ComputeAggregationsAsync(string deviceId, string[] metrics, TimeSpan window, CancellationToken ct = default);
}
```
```csharp
public class AnomalyDetectionStrategy : IoTAnalyticsStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override Task<AnomalyDetectionResult> DetectAnomaliesAsync(AnomalyDetectionRequest request, CancellationToken ct = default);
    public override Task<PredictionResult> PredictAsync(PredictionRequest request, CancellationToken ct = default);
    public override Task<StreamAnalyticsResult> AnalyzeStreamAsync(StreamAnalyticsQuery query, CancellationToken ct = default);
    public override Task<PatternDetectionResult> DetectPatternsAsync(PatternDetectionRequest request, CancellationToken ct = default);
}
```
```csharp
public class PredictiveAnalyticsStrategy : IoTAnalyticsStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override Task<AnomalyDetectionResult> DetectAnomaliesAsync(AnomalyDetectionRequest request, CancellationToken ct = default);
    public override Task<PredictionResult> PredictAsync(PredictionRequest request, CancellationToken ct = default);
    public override Task<StreamAnalyticsResult> AnalyzeStreamAsync(StreamAnalyticsQuery query, CancellationToken ct = default);
    public override Task<PatternDetectionResult> DetectPatternsAsync(PatternDetectionRequest request, CancellationToken ct = default);
}
```
```csharp
public class StreamAnalyticsStrategy : IoTAnalyticsStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override Task<AnomalyDetectionResult> DetectAnomaliesAsync(AnomalyDetectionRequest request, CancellationToken ct = default);
    public override Task<PredictionResult> PredictAsync(PredictionRequest request, CancellationToken ct = default);
    public override Task<StreamAnalyticsResult> AnalyzeStreamAsync(StreamAnalyticsQuery query, CancellationToken ct = default);
    public override Task<PatternDetectionResult> DetectPatternsAsync(PatternDetectionRequest request, CancellationToken ct = default);
}
```
```csharp
public class PatternRecognitionStrategy : IoTAnalyticsStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override Task<AnomalyDetectionResult> DetectAnomaliesAsync(AnomalyDetectionRequest request, CancellationToken ct = default);
    public override Task<PredictionResult> PredictAsync(PredictionRequest request, CancellationToken ct = default);
    public override Task<StreamAnalyticsResult> AnalyzeStreamAsync(StreamAnalyticsQuery query, CancellationToken ct = default);
    public override Task<PatternDetectionResult> DetectPatternsAsync(PatternDetectionRequest request, CancellationToken ct = default);
}
```
```csharp
public class PredictiveMaintenanceStrategy : IoTAnalyticsStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override Task<AnomalyDetectionResult> DetectAnomaliesAsync(AnomalyDetectionRequest request, CancellationToken ct = default);
    public override Task<PredictionResult> PredictAsync(PredictionRequest request, CancellationToken ct = default);
    public override Task<StreamAnalyticsResult> AnalyzeStreamAsync(StreamAnalyticsQuery query, CancellationToken ct = default);
    public override Task<PatternDetectionResult> DetectPatternsAsync(PatternDetectionRequest request, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateIoTIntegration/Strategies/Protocol/ProtocolStrategies.cs
```csharp
public abstract class ProtocolStrategyBase : IoTStrategyBase, IProtocolStrategy
{
}
    public override IoTStrategyCategory Category;;
    public abstract string ProtocolName { get; }
    public abstract int DefaultPort { get; }
    public abstract Task<CommandResult> SendCommandAsync(DeviceCommand command, CancellationToken ct = default);;
    public abstract Task PublishAsync(string topic, byte[] payload, ProtocolOptions options, CancellationToken ct = default);;
    public abstract IAsyncEnumerable<byte[]> SubscribeAsync(string topic, CancellationToken ct = default);;
    public virtual Task<CoApResponse> SendCoApAsync(string endpoint, CoApMethod method, string resourcePath, byte[]? payload, CancellationToken ct = default);;
    public virtual Task<ModbusResponse> ReadModbusAsync(string address, int slaveId, int registerAddress, int count, ModbusFunction function, CancellationToken ct = default);;
    public virtual Task<ModbusResponse> WriteModbusAsync(string address, int slaveId, int registerAddress, ushort[] values, CancellationToken ct = default);;
    public virtual Task<IEnumerable<OpcUaNode>> BrowseOpcUaAsync(string endpoint, string? nodeId, CancellationToken ct = default);;
    public virtual Task<object?> ReadOpcUaAsync(string endpoint, string nodeId, CancellationToken ct = default);;
}
```
```csharp
public class MqttProtocolStrategy : ProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string ProtocolName;;
    public override int DefaultPort;;
    public override string Description;;
    public override string[] Tags;;
    public override Task<CommandResult> SendCommandAsync(DeviceCommand command, CancellationToken ct = default);
    public override Task PublishAsync(string topic, byte[] payload, ProtocolOptions options, CancellationToken ct = default);
    public override async IAsyncEnumerable<byte[]> SubscribeAsync(string topic, [EnumeratorCancellation] CancellationToken ct = default);
}
```
```csharp
public class CoApProtocolStrategy : ProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string ProtocolName;;
    public override int DefaultPort;;
    public override string Description;;
    public override string[] Tags;;
    public override Task<CommandResult> SendCommandAsync(DeviceCommand command, CancellationToken ct = default);
    public override Task PublishAsync(string topic, byte[] payload, ProtocolOptions options, CancellationToken ct = default);
    public override async IAsyncEnumerable<byte[]> SubscribeAsync(string topic, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<CoApResponse> SendCoApAsync(string endpoint, CoApMethod method, string resourcePath, byte[]? payload, CancellationToken ct = default);
}
```
```csharp
public class LwM2MProtocolStrategy : ProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string ProtocolName;;
    public override int DefaultPort;;
    public override string Description;;
    public override string[] Tags;;
    public override Task<CommandResult> SendCommandAsync(DeviceCommand command, CancellationToken ct = default);
    public override Task PublishAsync(string topic, byte[] payload, ProtocolOptions options, CancellationToken ct = default);
    public override async IAsyncEnumerable<byte[]> SubscribeAsync(string topic, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<CoApResponse> SendCoApAsync(string endpoint, CoApMethod method, string resourcePath, byte[]? payload, CancellationToken ct = default);
}
```
```csharp
public class ModbusProtocolStrategy : ProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string ProtocolName;;
    public override int DefaultPort;;
    public override string Description;;
    public override string[] Tags;;
    public override Task<CommandResult> SendCommandAsync(DeviceCommand command, CancellationToken ct = default);
    public override Task PublishAsync(string topic, byte[] payload, ProtocolOptions options, CancellationToken ct = default);
    public override async IAsyncEnumerable<byte[]> SubscribeAsync(string topic, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<ModbusResponse> ReadModbusAsync(string address, int slaveId, int registerAddress, int count, ModbusFunction function, CancellationToken ct = default);
    public override Task<ModbusResponse> WriteModbusAsync(string address, int slaveId, int registerAddress, ushort[] values, CancellationToken ct = default);
}
```
```csharp
public class OpcUaProtocolStrategy : ProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string ProtocolName;;
    public override int DefaultPort;;
    public override string Description;;
    public override string[] Tags;;
    public override Task<CommandResult> SendCommandAsync(DeviceCommand command, CancellationToken ct = default);
    public override Task PublishAsync(string topic, byte[] payload, ProtocolOptions options, CancellationToken ct = default);
    public override async IAsyncEnumerable<byte[]> SubscribeAsync(string topic, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<IEnumerable<OpcUaNode>> BrowseOpcUaAsync(string endpoint, string? nodeId, CancellationToken ct = default);
    public override Task<object?> ReadOpcUaAsync(string endpoint, string nodeId, CancellationToken ct = default);
}
```
```csharp
public class AmqpProtocolStrategy : ProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string ProtocolName;;
    public override int DefaultPort;;
    public override string Description;;
    public override string[] Tags;;
    public override Task<CommandResult> SendCommandAsync(DeviceCommand command, CancellationToken ct = default);
    public override Task PublishAsync(string topic, byte[] payload, ProtocolOptions options, CancellationToken ct = default);
    public override async IAsyncEnumerable<byte[]> SubscribeAsync(string topic, [EnumeratorCancellation] CancellationToken ct = default);
}
```
```csharp
public class HttpProtocolStrategy : ProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string ProtocolName;;
    public override int DefaultPort;;
    public override string Description;;
    public override string[] Tags;;
    public override Task<CommandResult> SendCommandAsync(DeviceCommand command, CancellationToken ct = default);
    public override Task PublishAsync(string topic, byte[] payload, ProtocolOptions options, CancellationToken ct = default);
    public override async IAsyncEnumerable<byte[]> SubscribeAsync(string topic, [EnumeratorCancellation] CancellationToken ct = default);
}
```
```csharp
public class WebSocketProtocolStrategy : ProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string ProtocolName;;
    public override int DefaultPort;;
    public override string Description;;
    public override string[] Tags;;
    public override Task<CommandResult> SendCommandAsync(DeviceCommand command, CancellationToken ct = default);
    public override Task PublishAsync(string topic, byte[] payload, ProtocolOptions options, CancellationToken ct = default);
    public override async IAsyncEnumerable<byte[]> SubscribeAsync(string topic, [EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateIoTIntegration/Strategies/Protocol/IndustrialProtocolStrategies.cs
```csharp
public class Dnp3ProtocolStrategy : ProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string ProtocolName;;
    public override int DefaultPort;;
    public override string Description;;
    public override string[] Tags;;
    public void RegisterOutstation(int address, string name);
    public Task<Dnp3PollResponse> IntegrityPollAsync(int outstationAddress, CancellationToken ct = default);
    public Task<Dnp3ControlResponse> ControlAsync(int outstationAddress, int pointIndex, Dnp3ControlCode controlCode, CancellationToken ct = default);
    public Task<bool> TimeSyncAsync(int outstationAddress, CancellationToken ct = default);
    public Task<Dnp3UnsolicitedEvent> GetUnsolicitedEventAsync(int outstationAddress, CancellationToken ct = default);
    public override Task<CommandResult> SendCommandAsync(DeviceCommand command, CancellationToken ct = default);
    public override Task PublishAsync(string topic, byte[] payload, ProtocolOptions options, CancellationToken ct = default);;
    public override async IAsyncEnumerable<byte[]> SubscribeAsync(string topic, [EnumeratorCancellation] CancellationToken ct = default);
}
```
```csharp
public readonly struct Dnp3ObjectHeader
{
}
    public byte Group { get; }
    public byte Variation { get; }
    public byte Qualifier { get; }
    public Dnp3ObjectHeader(byte group, byte variation, byte qualifier);
}
```
```csharp
public sealed class Dnp3PollResponse
{
}
    public bool Success { get; init; }
    public int OutstationAddress { get; init; }
    public Dictionary<int, bool> BinaryInputs { get; init; };
    public Dictionary<int, double> AnalogInputs { get; init; };
    public Dictionary<int, long> Counters { get; init; };
    public DateTimeOffset Timestamp { get; init; }
    public byte[]? RequestBytes { get; init; }
    public string? Error { get; init; }
}
```
```csharp
public sealed class Dnp3ControlResponse
{
}
    public bool Success { get; init; }
    public int OutstationAddress { get; init; }
    public int PointIndex { get; init; }
    public Dnp3ControlCode ControlCode { get; init; }
    public byte StatusCode { get; init; }
    public byte[]? RequestBytes { get; init; }
    public string? Error { get; init; }
}
```
```csharp
public sealed class Dnp3UnsolicitedEvent
{
}
    public int OutstationAddress { get; init; }
    public string EventType { get; init; };
    public int PointIndex { get; init; }
    public object? Value { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public byte Flags { get; init; }
}
```
```csharp
internal sealed class Dnp3OutstationState
{
}
    public int Address { get; init; }
    public string Name { get; init; };
    public bool IsOnline { get; set; }
    public DateTimeOffset LastTimeSync { get; set; }
    public BoundedDictionary<int, bool> BinaryInputs { get; };
    public BoundedDictionary<int, bool> BinaryOutputs { get; };
    public BoundedDictionary<int, double> AnalogInputs { get; };
    public BoundedDictionary<int, double> AnalogOutputs { get; };
    public BoundedDictionary<int, long> Counters { get; };
}
```
```csharp
public class Iec104ProtocolStrategy : ProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string ProtocolName;;
    public override int DefaultPort;;
    public override string Description;;
    public override string[] Tags;;
    public Task<Iec104Response> GeneralInterrogationAsync(string connectionId, int commonAddress, CancellationToken ct = default);
    public Task<Iec104Response> SingleCommandAsync(string connectionId, int commonAddress, int ioaAddress, bool value, CancellationToken ct = default);
    public Task<Iec104Response> ClockSyncAsync(string connectionId, int commonAddress, CancellationToken ct = default);
    public byte[] BuildStartDtAct();
    public byte[] BuildSFrame();
    public override Task<CommandResult> SendCommandAsync(DeviceCommand command, CancellationToken ct = default);
    public override Task PublishAsync(string topic, byte[] payload, ProtocolOptions options, CancellationToken ct = default);;
    public override async IAsyncEnumerable<byte[]> SubscribeAsync(string topic, [EnumeratorCancellation] CancellationToken ct = default);
}
```
```csharp
public sealed class Iec104Response
{
}
    public bool Success { get; init; }
    public int TypeId { get; init; }
    public int CommonAddress { get; init; }
    public List<Iec104InformationObject> InformationObjects { get; init; };
    public byte[]? RawApdu { get; init; }
}
```
```csharp
public sealed class Iec104InformationObject
{
}
    public int Address { get; init; }
    public int TypeId { get; init; }
    public double Value { get; init; }
    public byte Quality { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}
```
```csharp
internal sealed class Iec104ConnectionState
{
}
    public string ConnectionId { get; init; };
    public bool IsConnected { get; set; }
    public int SendSeq { get; set; }
    public int RecvSeq { get; set; }
}
```
```csharp
public class CanBusProtocolStrategy : ProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string ProtocolName;;
    public override int DefaultPort;;
    public override string Description;;
    public override string[] Tags;;
    public CanFrame ParseStandardFrame(byte[] rawFrame);
    public CanFrame ParseExtendedFrame(byte[] rawFrame);
    public byte[] BuildStandardFrame(uint id, byte[] data);
    public J1939Message DecodeJ1939(CanFrame frame);
    public void RegisterSignal(uint canId, CanSignalDefinition signal);
    public double? DecodeSignal(CanFrame frame, string signalName);
    public byte[] TranslateToMqtt(CanFrame frame);
    public override Task<CommandResult> SendCommandAsync(DeviceCommand command, CancellationToken ct = default);
    public override Task PublishAsync(string topic, byte[] payload, ProtocolOptions options, CancellationToken ct = default);;
    public override async IAsyncEnumerable<byte[]> SubscribeAsync(string topic, [EnumeratorCancellation] CancellationToken ct = default);
}
```
```csharp
public sealed class CanFrame
{
}
    public uint Id { get; init; }
    public bool IsExtended { get; init; }
    public bool IsRemoteRequest { get; init; }
    public int DataLengthCode { get; init; }
    public byte[] Data { get; init; };
    public DateTimeOffset Timestamp { get; init; }
}
```
```csharp
public sealed class J1939Message
{
}
    public int Pgn { get; init; }
    public int SourceAddress { get; init; }
    public int DestinationAddress { get; init; }
    public int Priority { get; init; }
    public byte[] Data { get; init; };
    public string PgnName { get; init; };
    public DateTimeOffset Timestamp { get; init; }
}
```
```csharp
public sealed class CanSignalDefinition
{
}
    public required string Name { get; init; }
    public int StartBit { get; init; }
    public int BitLength { get; init; }
    public bool IsBigEndian { get; init; }
    public double Factor { get; init; };
    public double Offset { get; init; }
    public string Unit { get; init; };
    public double MinValue { get; init; }
    public double MaxValue { get; init; }
}
```
```csharp
internal sealed class J1939SourceAddress
{
}
    public int Address { get; init; }
    public string Name { get; init; };
    public long IdentityNumber { get; init; }
}
```
```csharp
public class EnhancedModbusStrategy : ProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string ProtocolName;;
    public override int DefaultPort;;
    public override string Description;;
    public override string[] Tags;;
    public Task<float> ReadFloatAsync(string address, int slaveId, int registerAddress, bool bigEndian = true, CancellationToken ct = default);
    public Task<double> ReadDoubleAsync(string address, int slaveId, int registerAddress, CancellationToken ct = default);
    public Task<long> ReadBcdAsync(string address, int slaveId, int registerAddress, int registerCount, CancellationToken ct = default);
    public Task<ModbusResponse> WriteFloatAsync(string address, int slaveId, int registerAddress, float value, bool bigEndian = true, CancellationToken ct = default);
    public Task<ModbusResponse> WriteSingleCoilAsync(string address, int slaveId, int coilAddress, bool value, CancellationToken ct = default);
    public Task<ModbusResponse> WriteSingleRegisterAsync(string address, int slaveId, int registerAddress, ushort value, CancellationToken ct = default);
    public Task<ModbusResponse> WriteMultipleCoilsAsync(string address, int slaveId, int startAddress, bool[] values, CancellationToken ct = default);
    public Task<ModbusResponse> ReadWriteMultipleRegistersAsync(string address, int slaveId, int readAddress, int readCount, int writeAddress, ushort[] writeValues, CancellationToken ct = default);
    public byte[] BuildRtuFrame(int slaveId, byte functionCode, byte[] data);
    public string BuildAsciiFrame(int slaveId, byte functionCode, byte[] data);
    public void RegisterDeviceMap(int slaveId, ModbusDeviceMap map);
    public override Task<CommandResult> SendCommandAsync(DeviceCommand command, CancellationToken ct = default);
    public override Task PublishAsync(string topic, byte[] payload, ProtocolOptions options, CancellationToken ct = default);;
    public override async IAsyncEnumerable<byte[]> SubscribeAsync(string topic, [EnumeratorCancellation] CancellationToken ct = default);
}
```
```csharp
public sealed class ModbusDeviceMap
{
}
    public required string DeviceName { get; init; }
    public int SlaveId { get; init; }
    public List<ModbusRegisterMapping> Registers { get; init; };
    public TimeSpan PollInterval { get; init; };
}
```
```csharp
public sealed class ModbusRegisterMapping
{
}
    public required string Name { get; init; }
    public int Address { get; init; }
    public int Count { get; init; };
    public ModbusFunction Function { get; init; };
    public ModbusDataType DataType { get; init; };
    public double Scale { get; init; };
    public double Offset { get; init; }
    public string Unit { get; init; };
}
```
```csharp
public class EnhancedOpcUaStrategy : ProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string ProtocolName;;
    public override int DefaultPort;;
    public override string Description;;
    public override string[] Tags;;
    public Task<OpcUaSubscription> CreateSubscriptionAsync(string endpoint, TimeSpan publishingInterval, CancellationToken ct = default);
    public Task<bool> AddMonitoredItemAsync(string subscriptionId, string nodeId, TimeSpan samplingInterval, OpcUaDataChangeFilter? filter = null, CancellationToken ct = default);
    public Task<IReadOnlyList<OpcUaEventNotification>> GetEventNotificationsAsync(CancellationToken ct = default);
    public Task<OpcUaHistoricalReadResult> ReadHistoricalAsync(string endpoint, string nodeId, DateTimeOffset startTime, DateTimeOffset endTime, int maxValues = 100, CancellationToken ct = default);
    public Task<OpcUaMethodCallResult> CallMethodAsync(string endpoint, string objectNodeId, string methodNodeId, OpcUaMethodArgument[] inputArguments, CancellationToken ct = default);
    public Task<bool> DeleteSubscriptionAsync(string subscriptionId, CancellationToken ct = default);
    public override Task<CommandResult> SendCommandAsync(DeviceCommand command, CancellationToken ct = default);
    public override Task PublishAsync(string topic, byte[] payload, ProtocolOptions options, CancellationToken ct = default);;
    public override async IAsyncEnumerable<byte[]> SubscribeAsync(string topic, [EnumeratorCancellation] CancellationToken ct = default);
    public override Task<IEnumerable<OpcUaNode>> BrowseOpcUaAsync(string endpoint, string? nodeId, CancellationToken ct = default);
    public override Task<object?> ReadOpcUaAsync(string endpoint, string nodeId, CancellationToken ct = default);;
}
```
```csharp
public sealed class OpcUaSubscription
{
}
    public required string SubscriptionId { get; init; }
    public required string Endpoint { get; init; }
    public TimeSpan PublishingInterval { get; init; }
    public BoundedDictionary<string, OpcUaMonitoredItem> MonitoredItems { get; init; };
    public DateTimeOffset CreatedAt { get; init; }
    public bool IsActive { get; set; }
}
```
```csharp
public sealed class OpcUaMonitoredItem
{
}
    public required string NodeId { get; init; }
    public TimeSpan SamplingInterval { get; init; }
    public OpcUaDataChangeFilter Filter { get; init; };
    public object? LastValue { get; set; }
    public DateTimeOffset LastTimestamp { get; set; }
}
```
```csharp
public sealed class OpcUaDataChangeFilter
{
}
    public OpcUaDataChangeTrigger Trigger { get; init; };
    public OpcUaDeadbandType DeadbandType { get; init; };
    public double DeadbandValue { get; init; }
}
```
```csharp
public sealed class OpcUaEventNotification
{
}
    public required string NodeId { get; init; }
    public required string EventType { get; init; }
    public object? Value { get; init; }
    public DateTimeOffset SourceTimestamp { get; init; }
    public DateTimeOffset ServerTimestamp { get; init; }
    public OpcUaStatusCode Quality { get; init; }
}
```
```csharp
public sealed class OpcUaHistoricalReadResult
{
}
    public bool Success { get; init; }
    public string NodeId { get; init; };
    public List<OpcUaHistoricalValue> Values { get; init; };
    public string? ContinuationPoint { get; init; }
}
```
```csharp
public sealed class OpcUaHistoricalValue
{
}
    public DateTimeOffset Timestamp { get; init; }
    public object? Value { get; init; }
    public OpcUaStatusCode Quality { get; init; }
}
```
```csharp
public sealed class OpcUaMethodCallResult
{
}
    public bool Success { get; init; }
    public OpcUaStatusCode StatusCode { get; init; }
    public OpcUaMethodArgument[]? OutputArguments { get; init; }
    public string? Error { get; init; }
}
```
```csharp
public sealed class OpcUaMethodArgument
{
}
    public required string Name { get; init; }
    public object? Value { get; init; }
    public string DataType { get; init; };
    public bool IsOptional { get; init; }
}
```
```csharp
internal sealed class OpcUaHistoricalData
{
}
    public List<(DateTimeOffset Timestamp, object? Value)> Values { get; };
}
```
```csharp
public class RtosBridgeStrategy : ProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string ProtocolName;;
    public override int DefaultPort;;
    public override string Description;;
    public override string[] Tags;;
    public RtosPlatform DetectPlatform();
    public Task<RtosTaskHandle> CreateTaskAsync(string name, int priority, int stackSizeBytes, CancellationToken ct = default);
    public Task<RtosMemoryBlock> AllocateMemoryAsync(int sizeBytes, RtosMemoryPool pool = RtosMemoryPool.Default, CancellationToken ct = default);
    public Task RegisterInterruptAsync(int irqNumber, Action handler, CancellationToken ct = default);
    public Task<RtosSchedulerStats> GetSchedulerStatsAsync(CancellationToken ct = default);
    public override Task<CommandResult> SendCommandAsync(DeviceCommand command, CancellationToken ct = default);
    public override Task PublishAsync(string topic, byte[] payload, ProtocolOptions options, CancellationToken ct = default);;
    public override async IAsyncEnumerable<byte[]> SubscribeAsync(string topic, [EnumeratorCancellation] CancellationToken ct = default);
}
```
```csharp
public sealed class RtosTaskHandle
{
}
    public required string TaskId { get; init; }
    public required string Name { get; init; }
    public int Priority { get; init; }
    public int StackSizeBytes { get; init; }
    public RtosPlatform Platform { get; init; }
    public RtosTaskRunState State { get; set; }
}
```
```csharp
public sealed class RtosMemoryBlock
{
}
    public ulong Address { get; init; }
    public int SizeBytes { get; init; }
    public RtosMemoryPool Pool { get; init; }
    public RtosPlatform Platform { get; init; }
    public DateTimeOffset AllocatedAt { get; init; }
}
```
```csharp
public sealed class RtosSchedulerStats
{
}
    public RtosPlatform Platform { get; init; }
    public int ActiveTasks { get; init; }
    public int ReadyTasks { get; init; }
    public int BlockedTasks { get; init; }
    public int TotalTasks { get; init; }
    public long UptimeMs { get; init; }
    public double IdlePercent { get; init; }
}
```
```csharp
internal sealed class RtosTaskState
{
}
    public required RtosTaskHandle Handle { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateIoTIntegration/Strategies/Protocol/MedicalDeviceStrategies.cs
```csharp
public sealed class Hl7V2ProtocolStrategy : ProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override string ProtocolName;;
    public override int DefaultPort;;
    public override Task<CommandResult> SendCommandAsync(DeviceCommand command, CancellationToken ct = default);;
    public override Task PublishAsync(string topic, byte[] payload, ProtocolOptions options, CancellationToken ct = default);;
    public override async IAsyncEnumerable<byte[]> SubscribeAsync(string topic, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default);
    public Hl7Message ParseMessage(string rawMessage);
    public string GenerateAck(Hl7Message originalMessage, string ackCode = "AA", string? textMessage = null);
    public string GenerateOruR01(string patientId, string patientName, IEnumerable<(string ObservationId, string Value, string Units)> observations);
    public Task<ProtocolConnectionResult> ConnectAsync(ProtocolConnectionRequest request, CancellationToken ct = default);
    public Task<ProtocolMessage> SendMessageAsync(ProtocolSendRequest request, CancellationToken ct = default);
    public Task<ProtocolMessage?> ReceiveMessageAsync(ProtocolReceiveRequest request, CancellationToken ct = default);
    public Task DisconnectAsync(string connectionId, CancellationToken ct = default);
}
```
```csharp
public sealed class Hl7Message
{
}
    public string RawMessage { get; init; };
    public string? MessageType { get; set; }
    public string? MessageControlId { get; set; }
    public string? ProcessingId { get; set; }
    public string? VersionId { get; set; }
    public string? SendingApplication { get; set; }
    public string? SendingFacility { get; set; }
    public string? ReceivingApplication { get; set; }
    public string? ReceivingFacility { get; set; }
    public string? DateTimeOfMessage { get; set; }
    public List<Hl7Segment> Segments { get; };
    public Hl7Segment? GetSegment(string segmentId);;
    public IEnumerable<Hl7Segment> GetSegments(string segmentId);;
}
```
```csharp
public sealed class Hl7Segment
{
}
    public string SegmentId { get; init; };
    public List<Hl7Field> Fields { get; };
    public string GetField(int index);;
}
```
```csharp
public sealed class Hl7Field
{
}
    public string Value { get; init; };
    public List<List<Hl7Component>> Repetitions { get; };
}
```
```csharp
public sealed class Hl7Component
{
}
    public string Value { get; init; };
    public string[] SubComponents { get; init; };
}
```
```csharp
public sealed class DicomNetworkStrategy : ProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override bool IsProductionReady;;
    public override string ProtocolName;;
    public override int DefaultPort;;
    public override Task<CommandResult> SendCommandAsync(DeviceCommand command, CancellationToken ct = default);;
    public override Task PublishAsync(string topic, byte[] payload, ProtocolOptions options, CancellationToken ct = default);;
    public override async IAsyncEnumerable<byte[]> SubscribeAsync(string topic, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default);
    public DicomAssociation NegotiateAssociation(string callingAe, string calledAe, string host, int port, DicomSopClass[] requestedSopClasses);
    public DicomResponse CEcho(string associationId);
    public DicomResponse CStore(string associationId, string sopClassUid, string sopInstanceUid, byte[] dataSet);
    public IEnumerable<DicomDataset> CFind(string associationId, DicomQueryLevel level, Dictionary<string, string> matchingKeys);
    public DicomMoveResponse CMove(string associationId, DicomQueryLevel level, Dictionary<string, string> matchingKeys, string destinationAe);
    public void ReleaseAssociation(string associationId);
    public Task<ProtocolConnectionResult> ConnectAsync(ProtocolConnectionRequest request, CancellationToken ct = default);
    public Task<ProtocolMessage> SendMessageAsync(ProtocolSendRequest request, CancellationToken ct = default);
    public Task<ProtocolMessage?> ReceiveMessageAsync(ProtocolReceiveRequest request, CancellationToken ct = default);;
    public Task DisconnectAsync(string connectionId, CancellationToken ct = default);
}
```
```csharp
public sealed class DicomAssociation
{
}
    public required string AssociationId { get; init; }
    public required string CallingAeTitle { get; init; }
    public required string CalledAeTitle { get; init; }
    public required string RemoteHost { get; init; }
    public required int RemotePort { get; init; }
    public DicomAssociationState State { get; set; }
    public int MaxPduSize { get; init; }
    public DicomPresentationContext[] AcceptedPresentationContexts { get; init; };
    public DateTimeOffset NegotiatedAt { get; init; }
}
```
```csharp
public sealed class DicomPresentationContext
{
}
    public byte PresentationContextId { get; init; }
    public required string AbstractSyntax { get; init; }
    public string[] TransferSyntaxes { get; init; };
    public DicomPresentationContextResult Result { get; init; }
}
```
```csharp
public static class DicomTransferSyntax
{
}
    public const string ImplicitVRLittleEndian = "1.2.840.10008.1.2";
    public const string ExplicitVRLittleEndian = "1.2.840.10008.1.2.1";
    public const string ExplicitVRBigEndian = "1.2.840.10008.1.2.2";
    public const string Jpeg2000Lossless = "1.2.840.10008.1.2.4.90";
    public const string Jpeg2000 = "1.2.840.10008.1.2.4.91";
}
```
```csharp
public sealed class DicomSopClass
{
}
    public string Uid { get; }
    public string Name { get; }
    public static readonly DicomSopClass Verification = new("1.2.840.10008.1.1", "Verification");
    public static readonly DicomSopClass CtImageStorage = new("1.2.840.10008.5.1.4.1.1.2", "CT Image Storage");
    public static readonly DicomSopClass MrImageStorage = new("1.2.840.10008.5.1.4.1.1.4", "MR Image Storage");
    public static readonly DicomSopClass UsImageStorage = new("1.2.840.10008.5.1.4.1.1.6.1", "Ultrasound Image Storage");
    public static readonly DicomSopClass CrImageStorage = new("1.2.840.10008.5.1.4.1.1.1", "CR Image Storage");
    public static readonly DicomSopClass DxImageStorage = new("1.2.840.10008.5.1.4.1.1.1.1", "Digital X-Ray Image Storage");
    public static readonly DicomSopClass NmImageStorage = new("1.2.840.10008.5.1.4.1.1.20", "Nuclear Medicine Image Storage");
    public static readonly DicomSopClass PetImageStorage = new("1.2.840.10008.5.1.4.1.1.128", "PET Image Storage");
    public static readonly DicomSopClass PatientRootQR = new("1.2.840.10008.5.1.4.1.2.1.1", "Patient Root Query/Retrieve - FIND");
    public static readonly DicomSopClass StudyRootQR = new("1.2.840.10008.5.1.4.1.2.2.1", "Study Root Query/Retrieve - FIND");
}
```
```csharp
public sealed class DicomResponse
{
}
    public DicomStatus Status { get; init; }
    public string? AffectedSopClassUid { get; init; }
    public string? AffectedSopInstanceUid { get; init; }
    public ushort MessageId { get; init; }
    public string? ErrorComment { get; init; }
}
```
```csharp
public sealed class DicomMoveResponse
{
}
    public DicomStatus Status { get; init; }
    public int RemainingSubOperations { get; init; }
    public int CompletedSubOperations { get; init; }
    public int FailedSubOperations { get; init; }
    public int WarningSubOperations { get; init; }
}
```
```csharp
public sealed class DicomDataset
{
}
    public Dictionary<string, string> Elements { get; };
    public string? GetValue(string tag);;
}
```
```csharp
public sealed class FhirR4Strategy : ProtocolStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override string ProtocolName;;
    public override int DefaultPort;;
    public override Task<CommandResult> SendCommandAsync(DeviceCommand command, CancellationToken ct = default);;
    public override Task PublishAsync(string topic, byte[] payload, ProtocolOptions options, CancellationToken ct = default);;
    public override async IAsyncEnumerable<byte[]> SubscribeAsync(string topic, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default);
    public FhirValidationResult ValidateResource(string resourceType, Dictionary<string, object> resource);
    public Dictionary<string, object> CreateBundle(string bundleType, IEnumerable<Dictionary<string, object>> entries);
    public Task<ProtocolConnectionResult> ConnectAsync(ProtocolConnectionRequest request, CancellationToken ct = default);
    public Task<ProtocolMessage> SendMessageAsync(ProtocolSendRequest request, CancellationToken ct = default);
    public Task<ProtocolMessage?> ReceiveMessageAsync(ProtocolReceiveRequest request, CancellationToken ct = default);;
    public Task DisconnectAsync(string connectionId, CancellationToken ct = default);;
}
```
```csharp
public sealed class FhirResourceDefinition
{
}
    public required string ResourceType { get; init; }
    public string[] RequiredElements { get; init; };
    public FhirCodeBinding[] CodeSystemBindings { get; init; };
}
```
```csharp
public sealed class FhirCodeBinding
{
}
    public required string Element { get; init; }
    public required FhirBindingStrength Strength { get; init; }
    public string[] ValidCodes { get; init; };
}
```
```csharp
public sealed class FhirValidationResult
{
}
    public required string ResourceType { get; init; }
    public bool IsValid { get; set; }
    public List<FhirValidationIssue> Issues { get; };
}
```
```csharp
public sealed class FhirValidationIssue
{
}
    public required FhirIssueSeverity Severity { get; init; }
    public required string Code { get; init; }
    public required string Diagnostics { get; init; }
    public string? Expression { get; init; }
}
```
```csharp
public sealed class ProtocolConnectionRequest
{
}
    public required string Endpoint { get; init; }
    public int? Port { get; init; }
    public Dictionary<string, string>? Headers { get; init; }
}
```
```csharp
public sealed class ProtocolConnectionResult
{
}
    public required bool Success { get; init; }
    public string? ConnectionId { get; init; }
    public string? ProtocolVersion { get; init; }
    public string? Message { get; init; }
}
```
```csharp
public sealed class ProtocolSendRequest
{
}
    public required string Topic { get; init; }
    public byte[]? Payload { get; init; }
    public Dictionary<string, string>? Properties { get; init; }
}
```
```csharp
public sealed class ProtocolReceiveRequest
{
}
    public required string Topic { get; init; }
    public TimeSpan? Timeout { get; init; }
}
```
```csharp
public sealed class ProtocolMessage
{
}
    public string MessageId { get; init; };
    public string? Topic { get; init; }
    public byte[]? Payload { get; init; }
    public DateTimeOffset Timestamp { get; init; };
    public Dictionary<string, string>? Properties { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateIoTIntegration/Strategies/SensorIngestion/SensorIngestionStrategies.cs
```csharp
public abstract class SensorIngestionStrategyBase : IoTStrategyBase, ISensorIngestionStrategy
{
}
    protected long TotalMessagesIngested;
    protected long TotalBytesIngested;
    protected readonly BoundedDictionary<string, Channel<TelemetryMessage>> Subscriptions = new BoundedDictionary<string, Channel<TelemetryMessage>>(1000);
    public override IoTStrategyCategory Category;;
    public abstract Task<IngestionResult> IngestAsync(TelemetryMessage message, CancellationToken ct = default);;
    public abstract Task<BatchIngestionResult> IngestBatchAsync(IEnumerable<TelemetryMessage> messages, CancellationToken ct = default);;
    public abstract IAsyncEnumerable<TelemetryMessage> SubscribeAsync(TelemetrySubscription subscription, CancellationToken ct = default);;
    public virtual Task<IngestionStatistics> GetStatisticsAsync(CancellationToken ct = default);
}
```
```csharp
public class StreamingIngestionStrategy : SensorIngestionStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override Task<IngestionResult> IngestAsync(TelemetryMessage message, CancellationToken ct = default);
    public override async Task<BatchIngestionResult> IngestBatchAsync(IEnumerable<TelemetryMessage> messages, CancellationToken ct = default);
    public override async IAsyncEnumerable<TelemetryMessage> SubscribeAsync(TelemetrySubscription subscription, [EnumeratorCancellation] CancellationToken ct = default);
}
```
```csharp
public class BatchIngestionStrategy : SensorIngestionStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override Task<IngestionResult> IngestAsync(TelemetryMessage message, CancellationToken ct = default);
    public override Task<BatchIngestionResult> IngestBatchAsync(IEnumerable<TelemetryMessage> messages, CancellationToken ct = default);
    public override async IAsyncEnumerable<TelemetryMessage> SubscribeAsync(TelemetrySubscription subscription, [EnumeratorCancellation] CancellationToken ct = default);
}
```
```csharp
public class BufferedIngestionStrategy : SensorIngestionStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override Task<IngestionResult> IngestAsync(TelemetryMessage message, CancellationToken ct = default);
    public override Task<BatchIngestionResult> IngestBatchAsync(IEnumerable<TelemetryMessage> messages, CancellationToken ct = default);
    public override async IAsyncEnumerable<TelemetryMessage> SubscribeAsync(TelemetrySubscription subscription, [EnumeratorCancellation] CancellationToken ct = default);
}
```
```csharp
public class TimeSeriesIngestionStrategy : SensorIngestionStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override Task<IngestionResult> IngestAsync(TelemetryMessage message, CancellationToken ct = default);
    public override Task<BatchIngestionResult> IngestBatchAsync(IEnumerable<TelemetryMessage> messages, CancellationToken ct = default);
    public override async IAsyncEnumerable<TelemetryMessage> SubscribeAsync(TelemetrySubscription subscription, [EnumeratorCancellation] CancellationToken ct = default);
}
```
```csharp
public class AggregatingIngestionStrategy : SensorIngestionStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override Task<IngestionResult> IngestAsync(TelemetryMessage message, CancellationToken ct = default);
    public override Task<BatchIngestionResult> IngestBatchAsync(IEnumerable<TelemetryMessage> messages, CancellationToken ct = default);
    public override async IAsyncEnumerable<TelemetryMessage> SubscribeAsync(TelemetrySubscription subscription, [EnumeratorCancellation] CancellationToken ct = default);
}
```
```csharp
private class AggregationWindow
{
}
    public int Count;
    public BoundedDictionary<string, double> Sum = new BoundedDictionary<string, double>(1000);
    public BoundedDictionary<string, double> Min = new BoundedDictionary<string, double>(1000);
    public BoundedDictionary<string, double> Max = new BoundedDictionary<string, double>(1000);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateIoTIntegration/Strategies/Provisioning/ProvisioningStrategies.cs
```csharp
public abstract class ProvisioningStrategyBase : IoTStrategyBase, IProvisioningStrategy
{
}
    protected readonly BoundedDictionary<string, DeviceCredentials> Credentials = new BoundedDictionary<string, DeviceCredentials>(1000);
    public override IoTStrategyCategory Category;;
    public abstract CredentialType[] SupportedCredentialTypes { get; }
    public abstract Task<ProvisioningResult> ProvisionAsync(ProvisioningRequest request, CancellationToken ct = default);;
    public abstract Task<DeviceCredentials> GenerateCredentialsAsync(string deviceId, CredentialType credentialType, CancellationToken ct = default);;
    public abstract Task<EnrollmentResult> EnrollAsync(EnrollmentRequest request, CancellationToken ct = default);;
    public virtual Task<bool> RevokeCredentialsAsync(string deviceId, CancellationToken ct = default);
    public virtual Task<bool> ValidateCredentialsAsync(string deviceId, string credential, CancellationToken ct = default);
    protected static string GenerateKey(int length = 32);
}
```
```csharp
public class ZeroTouchProvisioningStrategy : ProvisioningStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override CredentialType[] SupportedCredentialTypes;;
    public override Task<ProvisioningResult> ProvisionAsync(ProvisioningRequest request, CancellationToken ct = default);
    public override Task<DeviceCredentials> GenerateCredentialsAsync(string deviceId, CredentialType credentialType, CancellationToken ct = default);
    public override Task<EnrollmentResult> EnrollAsync(EnrollmentRequest request, CancellationToken ct = default);
}
```
```csharp
public class X509ProvisioningStrategy : ProvisioningStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override CredentialType[] SupportedCredentialTypes;;
    public override Task<ProvisioningResult> ProvisionAsync(ProvisioningRequest request, CancellationToken ct = default);
    public override Task<DeviceCredentials> GenerateCredentialsAsync(string deviceId, CredentialType credentialType, CancellationToken ct = default);
    public override Task<EnrollmentResult> EnrollAsync(EnrollmentRequest request, CancellationToken ct = default);
}
```
```csharp
public class TpmProvisioningStrategy : ProvisioningStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override CredentialType[] SupportedCredentialTypes;;
    public override Task<ProvisioningResult> ProvisionAsync(ProvisioningRequest request, CancellationToken ct = default);
    public override Task<DeviceCredentials> GenerateCredentialsAsync(string deviceId, CredentialType credentialType, CancellationToken ct = default);
    public override Task<EnrollmentResult> EnrollAsync(EnrollmentRequest request, CancellationToken ct = default);
}
```
```csharp
public class DpsEnrollmentStrategy : ProvisioningStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override CredentialType[] SupportedCredentialTypes;;
    public override Task<ProvisioningResult> ProvisionAsync(ProvisioningRequest request, CancellationToken ct = default);
    public override Task<DeviceCredentials> GenerateCredentialsAsync(string deviceId, CredentialType credentialType, CancellationToken ct = default);
    public override Task<EnrollmentResult> EnrollAsync(EnrollmentRequest request, CancellationToken ct = default);
}
```
```csharp
public class SymmetricKeyProvisioningStrategy : ProvisioningStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override CredentialType[] SupportedCredentialTypes;;
    public override Task<ProvisioningResult> ProvisionAsync(ProvisioningRequest request, CancellationToken ct = default);
    public override Task<DeviceCredentials> GenerateCredentialsAsync(string deviceId, CredentialType credentialType, CancellationToken ct = default);
    public override Task<EnrollmentResult> EnrollAsync(EnrollmentRequest request, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateIoTIntegration/Strategies/Hardware/BusControllerStrategies.cs
```csharp
public abstract class HardwareBusStrategyBase : IoTStrategyBase, IHardwareBusStrategy
{
}
    public override IoTStrategyCategory Category;;
    public abstract string BusType { get; }
    public abstract Task<bool> InitializeAsync(BusConfiguration config, CancellationToken ct = default);;
    public abstract new Task ShutdownAsync(CancellationToken ct = default);;
}
```
```csharp
public class GpioControllerStrategy : HardwareBusStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string BusType;;
    public override string Description;;
    public override string[] Tags;;
    public override Task<bool> InitializeAsync(BusConfiguration config, CancellationToken ct = default);
    public override Task ShutdownAsync(CancellationToken ct = default);
    public Task<GpioPinResult> ConfigurePinAsync(int pinNumber, PinMode mode, PullMode pull = PullMode.None, CancellationToken ct = default);
    public Task<bool> DigitalReadAsync(int pinNumber, CancellationToken ct = default);
    public Task DigitalWriteAsync(int pinNumber, bool value, CancellationToken ct = default);
    public Task SetPwmAsync(int pinNumber, double dutyCycle, int frequencyHz = 1000, CancellationToken ct = default);
}
```
```csharp
public class I2cControllerStrategy : HardwareBusStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string BusType;;
    public override string Description;;
    public override string[] Tags;;
    public override Task<bool> InitializeAsync(BusConfiguration config, CancellationToken ct = default);
    public override Task ShutdownAsync(CancellationToken ct = default);
    public async Task<byte[]> ScanBusAsync(CancellationToken ct = default);
    public async Task<byte[]> ReadRegisterAsync(byte deviceAddress, byte registerAddress, int byteCount, CancellationToken ct = default);
    public async Task WriteRegisterAsync(byte deviceAddress, byte registerAddress, byte[] data, CancellationToken ct = default);
}
```
```csharp
public class SpiControllerStrategy : HardwareBusStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string BusType;;
    public override string Description;;
    public override string[] Tags;;
    public override Task<bool> InitializeAsync(BusConfiguration config, CancellationToken ct = default);
    public override Task ShutdownAsync(CancellationToken ct = default);
    public async Task<byte[]> TransferAsync(byte[] writeData, int readLength = -1, CancellationToken ct = default);
    public Task SetModeAsync(SpiMode mode, CancellationToken ct = default);
}
```
```csharp
public interface IHardwareBusStrategy
{
}
    string BusType { get; }
    Task<bool> InitializeAsync(BusConfiguration config, CancellationToken ct = default);;
    Task ShutdownAsync(CancellationToken ct = default);;
}
```
```csharp
public class BusConfiguration
{
}
    public int BusFrequency { get; set; }
    public SpiMode? SpiMode { get; set; }
    public int ChipSelectPin { get; set; };
}
```
```csharp
public struct GpioPinState
{
}
    public int PinNumber;
    public PinMode Mode;
    public PullMode Pull;
    public bool Value;
    public double PwmDutyCycle;
    public int PwmFrequency;
}
```
```csharp
public class GpioPinResult
{
}
    public bool Success { get; set; }
    public int PinNumber { get; set; }
    public string? ErrorMessage { get; set; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateIoTIntegration/Strategies/DeviceManagement/DeviceManagementStrategies.cs
```csharp
public abstract class DeviceManagementStrategyBase : IoTStrategyBase, IDeviceManagementStrategy
{
}
    protected readonly BoundedDictionary<string, DeviceInfo> Devices = new BoundedDictionary<string, DeviceInfo>(1000);
    protected readonly BoundedDictionary<string, DeviceTwin> DeviceTwins = new BoundedDictionary<string, DeviceTwin>(1000);
    public override IoTStrategyCategory Category;;
    public abstract Task<DeviceRegistration> RegisterDeviceAsync(DeviceRegistrationRequest request, CancellationToken ct = default);;
    public abstract Task<DeviceTwin> GetDeviceTwinAsync(string deviceId, CancellationToken ct = default);;
    public abstract Task UpdateDeviceTwinAsync(string deviceId, Dictionary<string, object> desiredProperties, CancellationToken ct = default);;
    public abstract Task<IEnumerable<DeviceInfo>> ListDevicesAsync(DeviceQuery query, CancellationToken ct = default);;
    public abstract Task<FirmwareUpdateResult> UpdateFirmwareAsync(FirmwareUpdateRequest request, CancellationToken ct = default);;
    public abstract Task<bool> DeleteDeviceAsync(string deviceId, CancellationToken ct = default);;
    public abstract Task<DeviceInfo?> GetDeviceAsync(string deviceId, CancellationToken ct = default);;
}
```
```csharp
public class DeviceRegistryStrategy : DeviceManagementStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override Task<DeviceRegistration> RegisterDeviceAsync(DeviceRegistrationRequest request, CancellationToken ct = default);
    public override Task<DeviceTwin> GetDeviceTwinAsync(string deviceId, CancellationToken ct = default);
    public override Task UpdateDeviceTwinAsync(string deviceId, Dictionary<string, object> desiredProperties, CancellationToken ct = default);
    public override Task<IEnumerable<DeviceInfo>> ListDevicesAsync(DeviceQuery query, CancellationToken ct = default);
    public override Task<FirmwareUpdateResult> UpdateFirmwareAsync(FirmwareUpdateRequest request, CancellationToken ct = default);
    public override Task<bool> DeleteDeviceAsync(string deviceId, CancellationToken ct = default);
    public override Task<DeviceInfo?> GetDeviceAsync(string deviceId, CancellationToken ct = default);
}
```
```csharp
public class DeviceTwinStrategy : DeviceManagementStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override Task<DeviceRegistration> RegisterDeviceAsync(DeviceRegistrationRequest request, CancellationToken ct = default);
    public override Task<DeviceTwin> GetDeviceTwinAsync(string deviceId, CancellationToken ct = default);
    public override Task UpdateDeviceTwinAsync(string deviceId, Dictionary<string, object> desiredProperties, CancellationToken ct = default);
    public override Task<IEnumerable<DeviceInfo>> ListDevicesAsync(DeviceQuery query, CancellationToken ct = default);
    public override Task<FirmwareUpdateResult> UpdateFirmwareAsync(FirmwareUpdateRequest request, CancellationToken ct = default);
    public override Task<bool> DeleteDeviceAsync(string deviceId, CancellationToken ct = default);
    public override Task<DeviceInfo?> GetDeviceAsync(string deviceId, CancellationToken ct = default);
    public async Task<SyncResult> SyncAsync(string deviceId, Dictionary<string, object> sensorData, CancellationToken ct = default);
    public Task<ProjectedState> ProjectAsync(string deviceId, TimeSpan horizon, CancellationToken ct = default);
    public Task<SimulationResult> SimulateAsync(string deviceId, Dictionary<string, object> parameterChanges, CancellationToken ct = default);
}
```
```csharp
public class FleetManagementStrategy : DeviceManagementStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override Task<DeviceRegistration> RegisterDeviceAsync(DeviceRegistrationRequest request, CancellationToken ct = default);
    public override Task<DeviceTwin> GetDeviceTwinAsync(string deviceId, CancellationToken ct = default);
    public override Task UpdateDeviceTwinAsync(string deviceId, Dictionary<string, object> desiredProperties, CancellationToken ct = default);
    public override Task<IEnumerable<DeviceInfo>> ListDevicesAsync(DeviceQuery query, CancellationToken ct = default);
    public override Task<FirmwareUpdateResult> UpdateFirmwareAsync(FirmwareUpdateRequest request, CancellationToken ct = default);
    public override Task<bool> DeleteDeviceAsync(string deviceId, CancellationToken ct = default);
    public override Task<DeviceInfo?> GetDeviceAsync(string deviceId, CancellationToken ct = default);
}
```
```csharp
public class FirmwareOtaStrategy : DeviceManagementStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override Task<DeviceRegistration> RegisterDeviceAsync(DeviceRegistrationRequest request, CancellationToken ct = default);
    public override Task<DeviceTwin> GetDeviceTwinAsync(string deviceId, CancellationToken ct = default);
    public override Task UpdateDeviceTwinAsync(string deviceId, Dictionary<string, object> desiredProperties, CancellationToken ct = default);
    public override Task<IEnumerable<DeviceInfo>> ListDevicesAsync(DeviceQuery query, CancellationToken ct = default);
    public override Task<FirmwareUpdateResult> UpdateFirmwareAsync(FirmwareUpdateRequest request, CancellationToken ct = default);
    public override Task<bool> DeleteDeviceAsync(string deviceId, CancellationToken ct = default);
    public override Task<DeviceInfo?> GetDeviceAsync(string deviceId, CancellationToken ct = default);
}
```
```csharp
public class DeviceLifecycleStrategy : DeviceManagementStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override string Description;;
    public override string[] Tags;;
    public override Task<DeviceRegistration> RegisterDeviceAsync(DeviceRegistrationRequest request, CancellationToken ct = default);
    public override Task<DeviceTwin> GetDeviceTwinAsync(string deviceId, CancellationToken ct = default);
    public override Task UpdateDeviceTwinAsync(string deviceId, Dictionary<string, object> desiredProperties, CancellationToken ct = default);
    public override Task<IEnumerable<DeviceInfo>> ListDevicesAsync(DeviceQuery query, CancellationToken ct = default);
    public override Task<FirmwareUpdateResult> UpdateFirmwareAsync(FirmwareUpdateRequest request, CancellationToken ct = default);
    public override Task<bool> DeleteDeviceAsync(string deviceId, CancellationToken ct = default);
    public override Task<DeviceInfo?> GetDeviceAsync(string deviceId, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateIoTIntegration/Strategies/DeviceManagement/ContinuousSyncService.cs
```csharp
internal class DeviceSyncState
{
}
    public required DeviceTwin Twin { get; set; }
    public BoundedDictionary<string, BoundedList<TimestampedValue>> PropertyHistory { get; };
    public DateTimeOffset LastSyncAt { get; set; }
    public long SyncCount { get; set; }
}
```
```csharp
internal class BoundedList<T>
{
}
    public BoundedList(int maxSize);
    public void Add(T item);
    public List<T> GetAll();
    public int Count
{
    get
    {
        lock (_lock)
        {
            return _items.Count;
        }
    }
}
}
```
```csharp
public class ContinuousSyncService
{
}
    public ContinuousSyncService(ContinuousSyncOptions? options = null);
    public void RegisterTwin(string deviceId, DeviceTwin twin);
    public void UnregisterTwin(string deviceId);
    public async Task<SyncResult> SyncSensorDataAsync(string deviceId, Dictionary<string, object> sensorReadings, CancellationToken ct = default);
    internal DeviceSyncState? GetSyncState(string deviceId);
    public IEnumerable<string> GetRegisteredDevices();
}
```
```csharp
public class StateProjectionEngine
{
}
    public StateProjectionEngine(ContinuousSyncService syncService);
    public async Task<ProjectedState> ProjectStateAsync(string deviceId, TimeSpan horizon, CancellationToken ct = default);
}
```
```csharp
public class WhatIfSimulator
{
}
    public WhatIfSimulator(ContinuousSyncService syncService, StateProjectionEngine projectionEngine);
    public async Task<SimulationResult> SimulateAsync(string deviceId, Dictionary<string, object> parameterChanges, CancellationToken ct = default);
}
```
```csharp
internal static class DeviceManagementHelper
{
}
    internal static bool TryConvertToDouble(object value, out double result);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateIoTIntegration/Strategies/SensorFusion/WeightedAverageFusion.cs
```csharp
public sealed class WeightedAverageFusion
{
}
    public void Configure(Dictionary<string, double> sensorWeights);
    public FusedReading Fuse(SensorReading[] readings);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateIoTIntegration/Strategies/SensorFusion/MatrixMath.cs
```csharp
public sealed class Matrix
{
}
    public Matrix(int rows, int cols);
    public Matrix(double[, ] data);
    public int Rows { get; }
    public int Cols { get; }
    public double this[int row, int col] { get => _data[row, col]; set => _data[row, col] = value; };
    public static Matrix Identity(int n);
    public static Matrix Multiply(Matrix a, Matrix b);
    public static Matrix Transpose(Matrix m);
    public static Matrix Add(Matrix a, Matrix b);
    public static Matrix Subtract(Matrix a, Matrix b);
    public static Matrix ScalarMultiply(double s, Matrix m);
    public static Matrix Inverse(Matrix m);
    public double[] ToArray();
    public static Matrix FromArray(double[] values);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateIoTIntegration/Strategies/SensorFusion/SensorFusionEngine.cs
```csharp
public sealed class SensorFusionEngine
{
}
    public SensorFusionEngine(FusionPipelineConfig? config = null);
    public KalmanFilter? KalmanFilter;;
    public ComplementaryFilter? ComplementaryFilter;;
    public WeightedAverageFusion? WeightedAverageFusion;;
    public VotingFusion? VotingFusion;;
    public async Task<FusedReading> ProcessAsync(SensorReading[] readings, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateIoTIntegration/Strategies/SensorFusion/SensorFusionStrategy.cs
```csharp
public sealed class SensorFusionStrategy : IoTStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override IoTStrategyCategory Category;;
    public override string Description;;
    public override string[] Tags;;
    public void Initialize(FusionPipelineConfig? config = null);
    public async Task<FusedReading> ProcessSensorDataAsync(SensorReading[] readings, CancellationToken ct = default);
    public SensorFusionEngine GetEngine();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateIoTIntegration/Strategies/SensorFusion/KalmanFilter.cs
```csharp
public sealed class KalmanFilter
{
}
    public bool IsInitialized
{
    get
    {
        lock (_stateLock)
        {
            return _state != null;
        }
    }
}
    public void Initialize(double[] initialPosition);
    public void Predict(double dt);
    public void Update(double[] measurement, double[] measurementNoise);
    public double[] GetEstimate();
    public double[] GetVelocity();
    public double[] GetCovariance();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateIoTIntegration/Strategies/SensorFusion/TemporalAligner.cs
```csharp
public sealed class TemporalAligner
{
}
    public TemporalAligner(int maxBufferSize = 100, double toleranceMs = 1.0);
    public Task<SensorReading[]> AlignAsync(SensorReading[] readings, DateTimeOffset targetTimestamp, CancellationToken ct = default);
    public void ClearBuffer(string sensorId);
    public void ClearAllBuffers();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateIoTIntegration/Strategies/SensorFusion/VotingFusion.cs
```csharp
public sealed class VotingFusion
{
}
    public FusedReading Vote(SensorReading[] readings, double tolerancePercent = 5.0);
    public string[] GetFaultySensors();
    public void ClearFaultTracking();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateIoTIntegration/Strategies/SensorFusion/ComplementaryFilter.cs
```csharp
public sealed class ComplementaryFilter
{
}
    public ComplementaryFilter(double alpha = 0.98);
    public double[] Update(double[] accelerometer, double[] gyroscope, double dt);
    public void Reset();
    public double[] GetOrientation();
}
```
