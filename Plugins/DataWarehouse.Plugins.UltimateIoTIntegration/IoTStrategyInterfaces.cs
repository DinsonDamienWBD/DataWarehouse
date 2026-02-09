using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.Plugins.UltimateIoTIntegration;

/// <summary>
/// Base interface for all IoT strategies.
/// </summary>
public interface IIoTStrategyBase
{
    /// <summary>Strategy identifier.</summary>
    string StrategyId { get; }

    /// <summary>Display name.</summary>
    string StrategyName { get; }

    /// <summary>Strategy category.</summary>
    IoTStrategyCategory Category { get; }

    /// <summary>Strategy description.</summary>
    string Description { get; }

    /// <summary>Semantic tags for AI discovery.</summary>
    string[] Tags { get; }

    /// <summary>Whether the strategy is available.</summary>
    bool IsAvailable { get; }

    /// <summary>Configures Intelligence integration.</summary>
    void ConfigureIntelligence(IMessageBus? messageBus);

    /// <summary>Gets knowledge objects for AI.</summary>
    IEnumerable<KnowledgeObject> GetKnowledge();

    /// <summary>Gets registered capabilities.</summary>
    IEnumerable<RegisteredCapability> GetCapabilities();
}

#region 110.1 Device Management Strategies

/// <summary>
/// Interface for device management strategies (T110.1).
/// </summary>
public interface IDeviceManagementStrategy : IIoTStrategyBase
{
    /// <summary>Registers a new device.</summary>
    Task<DeviceRegistration> RegisterDeviceAsync(DeviceRegistrationRequest request, CancellationToken ct = default);

    /// <summary>Gets device twin.</summary>
    Task<DeviceTwin> GetDeviceTwinAsync(string deviceId, CancellationToken ct = default);

    /// <summary>Updates device twin desired properties.</summary>
    Task UpdateDeviceTwinAsync(string deviceId, Dictionary<string, object> desiredProperties, CancellationToken ct = default);

    /// <summary>Lists devices.</summary>
    Task<IEnumerable<DeviceInfo>> ListDevicesAsync(DeviceQuery query, CancellationToken ct = default);

    /// <summary>Updates device firmware.</summary>
    Task<FirmwareUpdateResult> UpdateFirmwareAsync(FirmwareUpdateRequest request, CancellationToken ct = default);

    /// <summary>Deletes a device.</summary>
    Task<bool> DeleteDeviceAsync(string deviceId, CancellationToken ct = default);

    /// <summary>Gets device by ID.</summary>
    Task<DeviceInfo?> GetDeviceAsync(string deviceId, CancellationToken ct = default);
}

#endregion

#region 110.2 Sensor Ingestion Strategies

/// <summary>
/// Interface for sensor data ingestion strategies (T110.2).
/// </summary>
public interface ISensorIngestionStrategy : IIoTStrategyBase
{
    /// <summary>Ingests a single telemetry message.</summary>
    Task<IngestionResult> IngestAsync(TelemetryMessage message, CancellationToken ct = default);

    /// <summary>Ingests a batch of telemetry messages.</summary>
    Task<BatchIngestionResult> IngestBatchAsync(IEnumerable<TelemetryMessage> messages, CancellationToken ct = default);

    /// <summary>Subscribes to telemetry stream.</summary>
    IAsyncEnumerable<TelemetryMessage> SubscribeAsync(TelemetrySubscription subscription, CancellationToken ct = default);

    /// <summary>Gets ingestion statistics.</summary>
    Task<IngestionStatistics> GetStatisticsAsync(CancellationToken ct = default);
}

/// <summary>
/// Ingestion statistics.
/// </summary>
public class IngestionStatistics
{
    public long TotalMessagesIngested { get; set; }
    public long TotalBytesIngested { get; set; }
    public double AverageLatencyMs { get; set; }
    public int ActiveStreams { get; set; }
    public DateTimeOffset LastIngestedAt { get; set; }
}

#endregion

#region 110.3 Protocol Strategies

/// <summary>
/// Interface for IoT protocol strategies (T110.3).
/// </summary>
public interface IProtocolStrategy : IIoTStrategyBase
{
    /// <summary>Supported protocol name.</summary>
    string ProtocolName { get; }

    /// <summary>Default port.</summary>
    int DefaultPort { get; }

    /// <summary>Sends a command to a device.</summary>
    Task<CommandResult> SendCommandAsync(DeviceCommand command, CancellationToken ct = default);

    /// <summary>Publishes a message.</summary>
    Task PublishAsync(string topic, byte[] payload, ProtocolOptions options, CancellationToken ct = default);

    /// <summary>Subscribes to a topic.</summary>
    IAsyncEnumerable<byte[]> SubscribeAsync(string topic, CancellationToken ct = default);

    /// <summary>Sends a CoAP request.</summary>
    Task<CoApResponse> SendCoApAsync(string endpoint, CoApMethod method, string resourcePath, byte[]? payload, CancellationToken ct = default);

    /// <summary>Reads Modbus registers.</summary>
    Task<ModbusResponse> ReadModbusAsync(string address, int slaveId, int registerAddress, int count, ModbusFunction function, CancellationToken ct = default);

    /// <summary>Writes Modbus registers.</summary>
    Task<ModbusResponse> WriteModbusAsync(string address, int slaveId, int registerAddress, ushort[] values, CancellationToken ct = default);

    /// <summary>Browses OPC-UA nodes.</summary>
    Task<IEnumerable<OpcUaNode>> BrowseOpcUaAsync(string endpoint, string? nodeId, CancellationToken ct = default);

    /// <summary>Reads OPC-UA node value.</summary>
    Task<object?> ReadOpcUaAsync(string endpoint, string nodeId, CancellationToken ct = default);
}

#endregion

#region 110.4 Provisioning Strategies

/// <summary>
/// Interface for device provisioning strategies (T110.4).
/// </summary>
public interface IProvisioningStrategy : IIoTStrategyBase
{
    /// <summary>Supported credential types.</summary>
    CredentialType[] SupportedCredentialTypes { get; }

    /// <summary>Provisions a device.</summary>
    Task<ProvisioningResult> ProvisionAsync(ProvisioningRequest request, CancellationToken ct = default);

    /// <summary>Generates device credentials.</summary>
    Task<DeviceCredentials> GenerateCredentialsAsync(string deviceId, CredentialType credentialType, CancellationToken ct = default);

    /// <summary>Enrolls a device.</summary>
    Task<EnrollmentResult> EnrollAsync(EnrollmentRequest request, CancellationToken ct = default);

    /// <summary>Revokes device credentials.</summary>
    Task<bool> RevokeCredentialsAsync(string deviceId, CancellationToken ct = default);

    /// <summary>Validates credentials.</summary>
    Task<bool> ValidateCredentialsAsync(string deviceId, string credential, CancellationToken ct = default);
}

#endregion

#region 110.5 Analytics Strategies

/// <summary>
/// Interface for IoT analytics strategies (T110.5).
/// </summary>
public interface IIoTAnalyticsStrategy : IIoTStrategyBase
{
    /// <summary>Detects anomalies in telemetry.</summary>
    Task<AnomalyDetectionResult> DetectAnomaliesAsync(AnomalyDetectionRequest request, CancellationToken ct = default);

    /// <summary>Predicts future values.</summary>
    Task<PredictionResult> PredictAsync(PredictionRequest request, CancellationToken ct = default);

    /// <summary>Analyzes stream data.</summary>
    Task<StreamAnalyticsResult> AnalyzeStreamAsync(StreamAnalyticsQuery query, CancellationToken ct = default);

    /// <summary>Detects patterns.</summary>
    Task<PatternDetectionResult> DetectPatternsAsync(PatternDetectionRequest request, CancellationToken ct = default);

    /// <summary>Computes aggregations.</summary>
    Task<Dictionary<string, double>> ComputeAggregationsAsync(string deviceId, string[] metrics, TimeSpan window, CancellationToken ct = default);
}

#endregion

#region 110.6 Security Strategies

/// <summary>
/// Interface for IoT security strategies (T110.6).
/// </summary>
public interface IIoTSecurityStrategy : IIoTStrategyBase
{
    /// <summary>Authenticates a device.</summary>
    Task<AuthenticationResult> AuthenticateAsync(DeviceAuthenticationRequest request, CancellationToken ct = default);

    /// <summary>Rotates device credentials.</summary>
    Task<CredentialRotationResult> RotateCredentialsAsync(string deviceId, CancellationToken ct = default);

    /// <summary>Assesses device security.</summary>
    Task<SecurityAssessment> AssessSecurityAsync(string deviceId, CancellationToken ct = default);

    /// <summary>Detects security threats.</summary>
    Task<ThreatDetectionResult> DetectThreatsAsync(ThreatDetectionRequest request, CancellationToken ct = default);

    /// <summary>Encrypts device communication.</summary>
    Task<byte[]> EncryptAsync(string deviceId, byte[] data, CancellationToken ct = default);

    /// <summary>Decrypts device communication.</summary>
    Task<byte[]> DecryptAsync(string deviceId, byte[] data, CancellationToken ct = default);
}

#endregion

#region 110.7 Edge Integration Strategies

/// <summary>
/// Interface for edge integration strategies (T110.7).
/// </summary>
public interface IEdgeIntegrationStrategy : IIoTStrategyBase
{
    /// <summary>Deploys a module to edge.</summary>
    Task<EdgeDeploymentResult> DeployModuleAsync(EdgeDeploymentRequest request, CancellationToken ct = default);

    /// <summary>Syncs edge data.</summary>
    Task<SyncResult> SyncAsync(EdgeSyncRequest request, CancellationToken ct = default);

    /// <summary>Executes edge compute.</summary>
    Task<EdgeComputeResult> ExecuteComputeAsync(EdgeComputeRequest request, CancellationToken ct = default);

    /// <summary>Gets edge device status.</summary>
    Task<EdgeDeviceStatus> GetEdgeStatusAsync(string edgeDeviceId, CancellationToken ct = default);

    /// <summary>Lists edge modules.</summary>
    Task<IEnumerable<EdgeModuleStatus>> ListModulesAsync(string edgeDeviceId, CancellationToken ct = default);

    /// <summary>Removes an edge module.</summary>
    Task<bool> RemoveModuleAsync(string edgeDeviceId, string moduleName, CancellationToken ct = default);
}

#endregion

#region 110.8 Data Transformation Strategies

/// <summary>
/// Interface for data transformation strategies (T110.8).
/// </summary>
public interface IDataTransformationStrategy : IIoTStrategyBase
{
    /// <summary>Supported source formats.</summary>
    string[] SupportedSourceFormats { get; }

    /// <summary>Supported target formats.</summary>
    string[] SupportedTargetFormats { get; }

    /// <summary>Transforms data.</summary>
    Task<TransformationResult> TransformAsync(TransformationRequest request, CancellationToken ct = default);

    /// <summary>Translates between protocols.</summary>
    Task<ProtocolTranslationResult> TranslateProtocolAsync(ProtocolTranslationRequest request, CancellationToken ct = default);

    /// <summary>Enriches telemetry data.</summary>
    Task<EnrichmentResult> EnrichAsync(EnrichmentRequest request, CancellationToken ct = default);

    /// <summary>Normalizes telemetry data.</summary>
    Task<NormalizationResult> NormalizeAsync(NormalizationRequest request, CancellationToken ct = default);

    /// <summary>Validates data against schema.</summary>
    Task<bool> ValidateSchemaAsync(byte[] data, string schema, CancellationToken ct = default);
}

#endregion
