using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Hierarchy;
using DataWarehouse.SDK.Contracts.IntelligenceAware;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateIoTIntegration;

/// <summary>
/// Ultimate IoT Integration Plugin - Comprehensive IoT management solution with 50+ strategies.
/// Implements T110 sub-tasks: Device Management, Sensor Data Ingestion, Protocol Support,
/// Device Provisioning, Analytics, Security, Edge Integration, and Data Transformation.
/// </summary>
/// <remarks>
/// <para>
/// Supported IoT capabilities across 8 categories:
/// </para>
/// <list type="bullet">
///   <item><b>110.1 Device Management</b>: Device registry, twins, lifecycle, firmware, fleet management</item>
///   <item><b>110.2 Sensor Data Ingestion</b>: Time-series, streaming, batch, buffered, aggregated ingestion</item>
///   <item><b>110.3 Protocol Support</b>: MQTT, CoAP, LwM2M, AMQP, HTTP, WebSocket, Modbus, OPC-UA</item>
///   <item><b>110.4 Device Provisioning</b>: Zero-touch, X.509, TPM, symmetric key, DPS</item>
///   <item><b>110.5 IoT Analytics</b>: Real-time, predictive, anomaly detection, pattern recognition</item>
///   <item><b>110.6 IoT Security</b>: Device authentication, encryption, certificate management, threat detection</item>
///   <item><b>110.7 Edge Integration</b>: Edge compute, fog computing, offline sync, local processing</item>
///   <item><b>110.8 Data Transformation</b>: Protocol translation, format conversion, enrichment, normalization</item>
/// </list>
/// </remarks>
public sealed class UltimateIoTIntegrationPlugin : StreamingPluginBase, IDisposable
{
    // NOTE(65.4-07): _registry is retained as a typed lookup layer for domain-specific strategy interfaces
    // (IDeviceManagementStrategy, ISensorIngestionStrategy, etc.). Strategies also registered via base-class
    // RegisterStrategy() for unified lifecycle management via PluginBase.StrategyRegistry.
    private readonly IoTStrategyRegistry _registry = new();
    private readonly BoundedDictionary<string, DeviceState> _deviceStates = new BoundedDictionary<string, DeviceState>(1000);
    private readonly BoundedDictionary<string, TelemetryBuffer> _telemetryBuffers = new BoundedDictionary<string, TelemetryBuffer>(1000);
    private readonly BoundedDictionary<string, long> _usageStats = new BoundedDictionary<string, long>(1000);
    private bool _disposed;

    // Statistics
    private long _totalDevicesManaged;
    private long _totalTelemetryMessages;
    private long _totalCommandsSent;
    private long _totalBytesIngested;
    private long _totalTransformations;

    /// <inheritdoc/>
    public override string Id => "com.datawarehouse.iot.ultimate";

    /// <inheritdoc/>
    public override string Name => "Ultimate IoT Integration";

    /// <inheritdoc/>
    public override string Version => "1.0.0";

    /// <inheritdoc/>
    public override PluginCategory Category => PluginCategory.FeatureProvider;

    /// <summary>
    /// Semantic description for AI discovery.
    /// </summary>
    public string SemanticDescription =>
        "Ultimate IoT integration plugin providing 50+ strategies for comprehensive IoT management. " +
        "Supports device management, sensor data ingestion, multiple protocols (MQTT, CoAP, LwM2M, OPC-UA), " +
        "zero-touch provisioning, real-time analytics, security, edge computing, and data transformation.";

    /// <summary>
    /// Semantic tags for AI discovery.
    /// </summary>
    public string[] SemanticTags => new[]
    {
        "iot", "device-management", "telemetry", "mqtt", "coap", "opcua", "modbus",
        "edge-computing", "sensor", "provisioning", "analytics", "security", "lwm2m"
    };

    /// <summary>
    /// Gets the IoT strategy registry (typed lookup thin wrapper).
    /// </summary>
    public IoTStrategyRegistry Registry => _registry;

    /// <summary>
    /// Initializes a new instance of the UltimateIoTIntegrationPlugin.
    /// </summary>
    public UltimateIoTIntegrationPlugin()
    {
        DiscoverAndRegisterStrategies();
    }

    #region 110.1 Device Management

    /// <summary>
    /// Registers a new IoT device in the device registry.
    /// </summary>
    public async Task<DeviceRegistration> RegisterDeviceAsync(
        DeviceRegistrationRequest request,
        CancellationToken ct = default)
    {
        var strategy = _registry.GetStrategy<IDeviceManagementStrategy>(request.StrategyId ?? "device-registry");

        var registration = await strategy.RegisterDeviceAsync(request, ct);

        _deviceStates[registration.DeviceId] = new DeviceState
        {
            DeviceId = registration.DeviceId,
            Status = DeviceStatus.Registered,
            LastSeen = DateTimeOffset.UtcNow,
            Metadata = request.Metadata
        };

        Interlocked.Increment(ref _totalDevicesManaged);
        IncrementUsageStats("device-registration");

        return registration;
    }

    /// <summary>
    /// Gets device twin data (desired/reported state).
    /// </summary>
    public async Task<DeviceTwin> GetDeviceTwinAsync(
        string deviceId,
        string? strategyId = null,
        CancellationToken ct = default)
    {
        var strategy = _registry.GetStrategy<IDeviceManagementStrategy>(strategyId ?? "device-twin");
        return await strategy.GetDeviceTwinAsync(deviceId, ct);
    }

    /// <summary>
    /// Updates device twin desired properties.
    /// </summary>
    public async Task UpdateDeviceTwinAsync(
        string deviceId,
        Dictionary<string, object> desiredProperties,
        string? strategyId = null,
        CancellationToken ct = default)
    {
        var strategy = _registry.GetStrategy<IDeviceManagementStrategy>(strategyId ?? "device-twin");
        await strategy.UpdateDeviceTwinAsync(deviceId, desiredProperties, ct);
    }

    /// <summary>
    /// Lists all registered devices with optional filtering.
    /// </summary>
    public async Task<IEnumerable<DeviceInfo>> ListDevicesAsync(
        DeviceQuery? query = null,
        string? strategyId = null,
        CancellationToken ct = default)
    {
        var strategy = _registry.GetStrategy<IDeviceManagementStrategy>(strategyId ?? "device-registry");
        return await strategy.ListDevicesAsync(query ?? new DeviceQuery(), ct);
    }

    /// <summary>
    /// Initiates firmware update for a device or device group.
    /// </summary>
    public async Task<FirmwareUpdateResult> UpdateFirmwareAsync(
        FirmwareUpdateRequest request,
        CancellationToken ct = default)
    {
        var strategy = _registry.GetStrategy<IDeviceManagementStrategy>(request.StrategyId ?? "firmware-ota");
        return await strategy.UpdateFirmwareAsync(request, ct);
    }

    #endregion

    #region 110.2 Sensor Data Ingestion

    /// <summary>
    /// Ingests telemetry data from a device.
    /// </summary>
    public async Task<IngestionResult> IngestTelemetryAsync(
        TelemetryMessage message,
        string? strategyId = null,
        CancellationToken ct = default)
    {
        var strategy = _registry.GetStrategy<ISensorIngestionStrategy>(strategyId ?? "streaming-ingestion");

        var result = await strategy.IngestAsync(message, ct);

        // Update device state
        if (_deviceStates.TryGetValue(message.DeviceId, out var state))
        {
            state.LastSeen = DateTimeOffset.UtcNow;
            state.LastTelemetry = message;
        }

        Interlocked.Increment(ref _totalTelemetryMessages);
        Interlocked.Add(ref _totalBytesIngested, message.PayloadSize);
        IncrementUsageStats("telemetry-ingestion");

        return result;
    }

    /// <summary>
    /// Ingests a batch of telemetry messages.
    /// </summary>
    public async Task<BatchIngestionResult> IngestBatchAsync(
        IEnumerable<TelemetryMessage> messages,
        string? strategyId = null,
        CancellationToken ct = default)
    {
        var strategy = _registry.GetStrategy<ISensorIngestionStrategy>(strategyId ?? "batch-ingestion");
        var messageList = messages.ToList();

        var result = await strategy.IngestBatchAsync(messageList, ct);

        Interlocked.Add(ref _totalTelemetryMessages, messageList.Count);
        Interlocked.Add(ref _totalBytesIngested, messageList.Sum(m => m.PayloadSize));

        return result;
    }

    /// <summary>
    /// Subscribes to real-time telemetry stream from devices.
    /// </summary>
    public async IAsyncEnumerable<TelemetryMessage> SubscribeTelemetryAsync(
        TelemetrySubscription subscription,
        string? strategyId = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var strategy = _registry.GetStrategy<ISensorIngestionStrategy>(strategyId ?? "streaming-ingestion");

        await foreach (var message in strategy.SubscribeAsync(subscription, ct))
        {
            Interlocked.Increment(ref _totalTelemetryMessages);
            yield return message;
        }
    }

    #endregion

    #region 110.3 Protocol Support (MQTT, CoAP, etc.)

    /// <summary>
    /// Sends a command to a device using the appropriate protocol.
    /// </summary>
    public async Task<CommandResult> SendCommandAsync(
        DeviceCommand command,
        string? strategyId = null,
        CancellationToken ct = default)
    {
        // Auto-select protocol strategy based on device capabilities
        var protocol = strategyId ?? DetermineProtocolStrategy(command.DeviceId);
        var strategy = _registry.GetStrategy<IProtocolStrategy>(protocol);

        var result = await strategy.SendCommandAsync(command, ct);

        Interlocked.Increment(ref _totalCommandsSent);
        IncrementUsageStats($"command-{protocol}");

        return result;
    }

    /// <summary>
    /// Publishes a message to an MQTT topic.
    /// </summary>
    public async Task PublishMqttAsync(
        string topic,
        byte[] payload,
        MqttQoS qos = MqttQoS.AtLeastOnce,
        bool retain = false,
        CancellationToken ct = default)
    {
        var strategy = _registry.GetStrategy<IProtocolStrategy>("mqtt");
        await strategy.PublishAsync(topic, payload, new ProtocolOptions
        {
            QoS = (int)qos,
            Retain = retain
        }, ct);
    }

    /// <summary>
    /// Sends a CoAP request to a device.
    /// </summary>
    public async Task<CoApResponse> SendCoApRequestAsync(
        string deviceEndpoint,
        CoApMethod method,
        string resourcePath,
        byte[]? payload = null,
        CancellationToken ct = default)
    {
        var strategy = _registry.GetStrategy<IProtocolStrategy>("coap");
        return await strategy.SendCoApAsync(deviceEndpoint, method, resourcePath, payload, ct);
    }

    /// <summary>
    /// Reads from a Modbus register.
    /// </summary>
    public async Task<ModbusResponse> ReadModbusAsync(
        string deviceAddress,
        int slaveId,
        int registerAddress,
        int registerCount,
        ModbusFunction function = ModbusFunction.ReadHoldingRegisters,
        CancellationToken ct = default)
    {
        var strategy = _registry.GetStrategy<IProtocolStrategy>("modbus");
        return await strategy.ReadModbusAsync(deviceAddress, slaveId, registerAddress, registerCount, function, ct);
    }

    /// <summary>
    /// Browses OPC-UA nodes.
    /// </summary>
    public async Task<IEnumerable<OpcUaNode>> BrowseOpcUaNodesAsync(
        string serverEndpoint,
        string? nodeId = null,
        CancellationToken ct = default)
    {
        var strategy = _registry.GetStrategy<IProtocolStrategy>("opcua");
        return await strategy.BrowseOpcUaAsync(serverEndpoint, nodeId, ct);
    }

    #endregion

    #region 110.4 Device Provisioning

    /// <summary>
    /// Provisions a device using zero-touch provisioning.
    /// </summary>
    public async Task<ProvisioningResult> ProvisionDeviceAsync(
        ProvisioningRequest request,
        CancellationToken ct = default)
    {
        var strategy = _registry.GetStrategy<IProvisioningStrategy>(request.StrategyId ?? "zero-touch");

        var result = await strategy.ProvisionAsync(request, ct);

        if (result.Success)
        {
            _deviceStates[result.DeviceId] = new DeviceState
            {
                DeviceId = result.DeviceId,
                Status = DeviceStatus.Provisioned,
                LastSeen = DateTimeOffset.UtcNow,
                ProvisioningMethod = request.StrategyId ?? "zero-touch"
            };

            Interlocked.Increment(ref _totalDevicesManaged);
        }

        IncrementUsageStats("device-provisioning");

        return result;
    }

    /// <summary>
    /// Generates device credentials (certificates, keys).
    /// </summary>
    public async Task<DeviceCredentials> GenerateCredentialsAsync(
        string deviceId,
        CredentialType credentialType = CredentialType.X509Certificate,
        string? strategyId = null,
        CancellationToken ct = default)
    {
        var strategy = _registry.GetStrategy<IProvisioningStrategy>(strategyId ?? "x509-provisioning");
        return await strategy.GenerateCredentialsAsync(deviceId, credentialType, ct);
    }

    /// <summary>
    /// Enrolls a device with Device Provisioning Service.
    /// </summary>
    public async Task<EnrollmentResult> EnrollDeviceAsync(
        EnrollmentRequest request,
        CancellationToken ct = default)
    {
        var strategy = _registry.GetStrategy<IProvisioningStrategy>(request.StrategyId ?? "dps-enrollment");
        return await strategy.EnrollAsync(request, ct);
    }

    #endregion

    #region 110.5 IoT Analytics

    /// <summary>
    /// Analyzes telemetry data for anomalies.
    /// </summary>
    public async Task<AnomalyDetectionResult> DetectAnomaliesAsync(
        AnomalyDetectionRequest request,
        CancellationToken ct = default)
    {
        var strategy = _registry.GetStrategy<IIoTAnalyticsStrategy>(request.StrategyId ?? "anomaly-detection");
        return await strategy.DetectAnomaliesAsync(request, ct);
    }

    /// <summary>
    /// Predicts future telemetry values.
    /// </summary>
    public async Task<PredictionResult> PredictTelemetryAsync(
        PredictionRequest request,
        CancellationToken ct = default)
    {
        var strategy = _registry.GetStrategy<IIoTAnalyticsStrategy>(request.StrategyId ?? "predictive-analytics");
        return await strategy.PredictAsync(request, ct);
    }

    /// <summary>
    /// Performs real-time stream analytics.
    /// </summary>
    public async Task<StreamAnalyticsResult> AnalyzeStreamAsync(
        StreamAnalyticsQuery query,
        CancellationToken ct = default)
    {
        var strategy = _registry.GetStrategy<IIoTAnalyticsStrategy>(query.StrategyId ?? "stream-analytics");
        return await strategy.AnalyzeStreamAsync(query, ct);
    }

    /// <summary>
    /// Detects patterns in device telemetry.
    /// </summary>
    public async Task<PatternDetectionResult> DetectPatternsAsync(
        PatternDetectionRequest request,
        CancellationToken ct = default)
    {
        var strategy = _registry.GetStrategy<IIoTAnalyticsStrategy>(request.StrategyId ?? "pattern-recognition");
        return await strategy.DetectPatternsAsync(request, ct);
    }

    #endregion

    #region 110.6 IoT Security

    /// <summary>
    /// Authenticates a device.
    /// </summary>
    public async Task<AuthenticationResult> AuthenticateDeviceAsync(
        DeviceAuthenticationRequest request,
        CancellationToken ct = default)
    {
        var strategy = _registry.GetStrategy<IIoTSecurityStrategy>(request.StrategyId ?? "device-auth");
        return await strategy.AuthenticateAsync(request, ct);
    }

    /// <summary>
    /// Rotates device credentials.
    /// </summary>
    public async Task<CredentialRotationResult> RotateCredentialsAsync(
        string deviceId,
        string? strategyId = null,
        CancellationToken ct = default)
    {
        var strategy = _registry.GetStrategy<IIoTSecurityStrategy>(strategyId ?? "credential-rotation");
        return await strategy.RotateCredentialsAsync(deviceId, ct);
    }

    /// <summary>
    /// Assesses device security posture.
    /// </summary>
    public async Task<SecurityAssessment> AssessSecurityAsync(
        string deviceId,
        string? strategyId = null,
        CancellationToken ct = default)
    {
        var strategy = _registry.GetStrategy<IIoTSecurityStrategy>(strategyId ?? "security-assessment");
        return await strategy.AssessSecurityAsync(deviceId, ct);
    }

    /// <summary>
    /// Detects potential security threats.
    /// </summary>
    public async Task<ThreatDetectionResult> DetectThreatsAsync(
        ThreatDetectionRequest request,
        CancellationToken ct = default)
    {
        var strategy = _registry.GetStrategy<IIoTSecurityStrategy>(request.StrategyId ?? "threat-detection");
        return await strategy.DetectThreatsAsync(request, ct);
    }

    #endregion

    #region 110.7 Edge Integration

    /// <summary>
    /// Deploys a module to an edge device.
    /// </summary>
    public async Task<EdgeDeploymentResult> DeployEdgeModuleAsync(
        EdgeDeploymentRequest request,
        CancellationToken ct = default)
    {
        var strategy = _registry.GetStrategy<IEdgeIntegrationStrategy>(request.StrategyId ?? "edge-deployment");
        return await strategy.DeployModuleAsync(request, ct);
    }

    /// <summary>
    /// Synchronizes data between edge and cloud.
    /// </summary>
    public async Task<SyncResult> SyncEdgeDataAsync(
        EdgeSyncRequest request,
        CancellationToken ct = default)
    {
        var strategy = _registry.GetStrategy<IEdgeIntegrationStrategy>(request.StrategyId ?? "edge-sync");
        return await strategy.SyncAsync(request, ct);
    }

    /// <summary>
    /// Executes a computation on an edge device.
    /// </summary>
    public async Task<EdgeComputeResult> ExecuteEdgeComputeAsync(
        EdgeComputeRequest request,
        CancellationToken ct = default)
    {
        var strategy = _registry.GetStrategy<IEdgeIntegrationStrategy>(request.StrategyId ?? "edge-compute");
        return await strategy.ExecuteComputeAsync(request, ct);
    }

    /// <summary>
    /// Gets edge device status and health.
    /// </summary>
    public async Task<EdgeDeviceStatus> GetEdgeStatusAsync(
        string edgeDeviceId,
        string? strategyId = null,
        CancellationToken ct = default)
    {
        var strategy = _registry.GetStrategy<IEdgeIntegrationStrategy>(strategyId ?? "edge-monitoring");
        return await strategy.GetEdgeStatusAsync(edgeDeviceId, ct);
    }

    #endregion

    #region 110.8 Data Transformation

    /// <summary>
    /// Transforms telemetry data from one format to another.
    /// </summary>
    public async Task<TransformationResult> TransformDataAsync(
        TransformationRequest request,
        CancellationToken ct = default)
    {
        var strategy = _registry.GetStrategy<IDataTransformationStrategy>(request.StrategyId ?? "format-conversion");

        var result = await strategy.TransformAsync(request, ct);

        Interlocked.Increment(ref _totalTransformations);
        IncrementUsageStats("data-transformation");

        return result;
    }

    /// <summary>
    /// Translates between IoT protocols.
    /// </summary>
    public async Task<ProtocolTranslationResult> TranslateProtocolAsync(
        ProtocolTranslationRequest request,
        CancellationToken ct = default)
    {
        var strategy = _registry.GetStrategy<IDataTransformationStrategy>(request.StrategyId ?? "protocol-translation");
        return await strategy.TranslateProtocolAsync(request, ct);
    }

    /// <summary>
    /// Enriches telemetry with additional context.
    /// </summary>
    public async Task<EnrichmentResult> EnrichTelemetryAsync(
        EnrichmentRequest request,
        CancellationToken ct = default)
    {
        var strategy = _registry.GetStrategy<IDataTransformationStrategy>(request.StrategyId ?? "data-enrichment");
        return await strategy.EnrichAsync(request, ct);
    }

    /// <summary>
    /// Normalizes telemetry data to a standard schema.
    /// </summary>
    public async Task<NormalizationResult> NormalizeTelemetryAsync(
        NormalizationRequest request,
        CancellationToken ct = default)
    {
        var strategy = _registry.GetStrategy<IDataTransformationStrategy>(request.StrategyId ?? "data-normalization");
        return await strategy.NormalizeAsync(request, ct);
    }

    #endregion

    #region Plugin Lifecycle

    /// <inheritdoc/>
    protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct)
    {
        await base.OnStartWithIntelligenceAsync(ct);

        // Configure Intelligence for all strategies
        _registry.ConfigureIntelligence(MessageBus);

        // Register capabilities with Intelligence
        if (MessageBus != null)
        {
            await MessageBus.PublishAsync(IntelligenceTopics.QueryCapability, new PluginMessage
            {
                Type = "capability.register",
                Source = Id,
                Payload = new Dictionary<string, object>
                {
                    ["pluginId"] = Id,
                    ["pluginName"] = Name,
                    ["pluginType"] = "iot-integration",
                    ["capabilities"] = new Dictionary<string, object>
                    {
                        ["strategyCount"] = _registry.Count,
                        ["categories"] = _registry.GetCategorySummary(),
                        ["supportedProtocols"] = new[] { "MQTT", "CoAP", "LwM2M", "AMQP", "Modbus", "OPC-UA", "HTTP", "WebSocket" },
                        ["supportsDeviceManagement"] = true,
                        ["supportsSensorIngestion"] = true,
                        ["supportsProvisioning"] = true,
                        ["supportsAnalytics"] = true,
                        ["supportsSecurity"] = true,
                        ["supportsEdgeIntegration"] = true,
                        ["supportsDataTransformation"] = true
                    },
                    ["semanticDescription"] = SemanticDescription,
                    ["tags"] = SemanticTags
                }
            }, ct);

            SubscribeToIntelligenceRequests();
        }
    }

    /// <inheritdoc/>
    protected override Task OnStartWithoutIntelligenceAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    protected override Task OnStartCoreAsync(CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    #endregion

    #region Capability Registration

    /// <inheritdoc/>
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
                    Tags = new[] { "iot", "device", "management", "registry", "twin" }
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
                    Tags = new[] { "iot", "sensor", "telemetry", "ingestion", "streaming" }
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
                    Tags = new[] { "iot", "mqtt", "coap", "lwm2m", "modbus", "opcua", "protocol" }
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
                    Tags = new[] { "iot", "provisioning", "x509", "tpm", "dps", "zero-touch" }
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
                    Tags = new[] { "iot", "analytics", "anomaly", "prediction", "pattern" }
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
                    Tags = new[] { "iot", "security", "authentication", "encryption", "threat" }
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
                    Tags = new[] { "iot", "edge", "fog", "compute", "offline", "sync" }
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
                    Tags = new[] { "iot", "transformation", "translation", "enrichment", "normalization" }
                }
            };

            // Add capabilities from all strategies
            capabilities.AddRange(_registry.GetAllStrategyCapabilities());

            return capabilities;
        }
    }

    /// <inheritdoc/>
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge()
    {
        var knowledge = new List<KnowledgeObject>(base.GetStaticKnowledge())
        {
            new()
            {
                Id = $"{Id}.overview",
                Topic = "iot-integration",
                SourcePluginId = Id,
                SourcePluginName = Name,
                KnowledgeType = "capability",
                Description = $"Ultimate IoT Integration with {_registry.Count} strategies",
                Payload = new Dictionary<string, object>
                {
                    ["strategyCount"] = _registry.Count,
                    ["categories"] = _registry.GetCategorySummary(),
                    ["supportedProtocols"] = new[] { "MQTT", "CoAP", "LwM2M", "AMQP", "Modbus", "OPC-UA", "HTTP", "WebSocket" },
                    ["capabilities"] = new[]
                    {
                        "device-management", "sensor-ingestion", "protocols",
                        "provisioning", "analytics", "security", "edge", "transformation"
                    }
                },
                Tags = SemanticTags,
                Confidence = 1.0f,
                Timestamp = DateTimeOffset.UtcNow
            }
        };

        knowledge.AddRange(_registry.GetAllStrategyKnowledge());

        return knowledge;
    }

    #endregion

    #region Message Handling

    /// <inheritdoc/>
    public override async Task OnMessageAsync(PluginMessage message)
    {
        switch (message.Type)
        {
            case "iot.device.register":
                await HandleDeviceRegisterAsync(message);
                break;
            case "iot.telemetry.ingest":
                await HandleTelemetryIngestAsync(message);
                break;
            case "iot.command.send":
                await HandleCommandSendAsync(message);
                break;
            case "iot.device.provision":
                await HandleDeviceProvisionAsync(message);
                break;
            case "iot.analytics.detect":
                await HandleAnalyticsDetectAsync(message);
                break;
            case "iot.security.authenticate":
                await HandleSecurityAuthenticateAsync(message);
                break;
            case "iot.edge.deploy":
                await HandleEdgeDeployAsync(message);
                break;
            case "iot.transform":
                await HandleTransformAsync(message);
                break;
            case "iot.list.strategies":
                await HandleListStrategiesAsync(message);
                break;
            case "iot.statistics":
                await HandleStatisticsAsync(message);
                break;
            default:
                await base.OnMessageAsync(message);
                break;
        }
    }

    private async Task HandleDeviceRegisterAsync(PluginMessage message)
    {
        var request = DeserializeRequest<DeviceRegistrationRequest>(message.Payload);
        var result = await RegisterDeviceAsync(request);
        message.Payload["result"] = result;
    }

    private async Task HandleTelemetryIngestAsync(PluginMessage message)
    {
        var telemetry = DeserializeRequest<TelemetryMessage>(message.Payload);
        var result = await IngestTelemetryAsync(telemetry);
        message.Payload["result"] = result;
    }

    private async Task HandleCommandSendAsync(PluginMessage message)
    {
        var command = DeserializeRequest<DeviceCommand>(message.Payload);
        var result = await SendCommandAsync(command);
        message.Payload["result"] = result;
    }

    private async Task HandleDeviceProvisionAsync(PluginMessage message)
    {
        var request = DeserializeRequest<ProvisioningRequest>(message.Payload);
        var result = await ProvisionDeviceAsync(request);
        message.Payload["result"] = result;
    }

    private async Task HandleAnalyticsDetectAsync(PluginMessage message)
    {
        var request = DeserializeRequest<AnomalyDetectionRequest>(message.Payload);
        var result = await DetectAnomaliesAsync(request);
        message.Payload["result"] = result;
    }

    private async Task HandleSecurityAuthenticateAsync(PluginMessage message)
    {
        var request = DeserializeRequest<DeviceAuthenticationRequest>(message.Payload);
        var result = await AuthenticateDeviceAsync(request);
        message.Payload["result"] = result;
    }

    private async Task HandleEdgeDeployAsync(PluginMessage message)
    {
        var request = DeserializeRequest<EdgeDeploymentRequest>(message.Payload);
        var result = await DeployEdgeModuleAsync(request);
        message.Payload["result"] = result;
    }

    private async Task HandleTransformAsync(PluginMessage message)
    {
        var request = DeserializeRequest<TransformationRequest>(message.Payload);
        var result = await TransformDataAsync(request);
        message.Payload["result"] = result;
    }

    private Task HandleListStrategiesAsync(PluginMessage message)
    {
        message.Payload["strategies"] = _registry.GetAllStrategies()
            .Select(s => new Dictionary<string, object>
            {
                ["id"] = s.StrategyId,
                ["name"] = s.StrategyName,
                ["category"] = s.Category.ToString(),
                ["description"] = s.Description
            }).ToList();
        message.Payload["count"] = _registry.Count;
        return Task.CompletedTask;
    }

    private Task HandleStatisticsAsync(PluginMessage message)
    {
        message.Payload["totalDevicesManaged"] = Interlocked.Read(ref _totalDevicesManaged);
        message.Payload["totalTelemetryMessages"] = Interlocked.Read(ref _totalTelemetryMessages);
        message.Payload["totalCommandsSent"] = Interlocked.Read(ref _totalCommandsSent);
        message.Payload["totalBytesIngested"] = Interlocked.Read(ref _totalBytesIngested);
        message.Payload["totalTransformations"] = Interlocked.Read(ref _totalTransformations);
        message.Payload["activeDevices"] = _deviceStates.Count;
        message.Payload["usageByStrategy"] = new Dictionary<string, long>(_usageStats);
        return Task.CompletedTask;
    }

    private static T DeserializeRequest<T>(Dictionary<string, object> payload) where T : new()
    {
        var result = new T();
        var type = typeof(T);
        foreach (var kvp in payload)
        {
            var prop = type.GetProperty(kvp.Key,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (prop == null || !prop.CanWrite) continue;
            try
            {
                var targetType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                var value = kvp.Value == null ? null
                    : targetType.IsInstanceOfType(kvp.Value) ? kvp.Value
                    : Convert.ChangeType(kvp.Value, targetType);
                prop.SetValue(result, value);
            }
            catch { /* Skip incompatible fields */ }
        }
        return result;
    }

    #endregion

    #region Intelligence Integration

    private void SubscribeToIntelligenceRequests()
    {
        if (MessageBus == null) return;

        // Subscribe to device recommendation requests
        MessageBus.Subscribe(IoTTopics.IntelligenceDeviceRecommendation, async msg =>
        {
            if (msg.Payload.TryGetValue("scenario", out var scenarioObj) && scenarioObj is string scenario)
            {
                var recommendation = RecommendStrategy(scenario, msg.Payload);
                await MessageBus.PublishAsync(IoTTopics.IntelligenceDeviceRecommendationResponse, new PluginMessage
                {
                    Type = "iot.recommendation.response",
                    CorrelationId = msg.CorrelationId,
                    Source = Id,
                    Payload = recommendation
                });
            }
        });

        // Subscribe to anomaly detection requests
        MessageBus.Subscribe(IoTTopics.IntelligenceAnomalyDetection, async msg =>
        {
            if (msg.Payload.TryGetValue("deviceId", out var deviceIdObj) && deviceIdObj is string deviceId)
            {
                var result = await DetectAnomaliesAsync(new AnomalyDetectionRequest
                {
                    DeviceId = deviceId,
                    StrategyId = "anomaly-detection"
                });

                await MessageBus.PublishAsync(IoTTopics.IntelligenceAnomalyDetectionResponse, new PluginMessage
                {
                    Type = "iot.anomaly.response",
                    CorrelationId = msg.CorrelationId,
                    Source = Id,
                    Payload = new Dictionary<string, object>
                    {
                        ["success"] = true,
                        ["deviceId"] = deviceId,
                        ["anomaliesDetected"] = result.AnomaliesDetected,
                        ["severity"] = result.Severity.ToString(),
                        ["confidence"] = result.Confidence
                    }
                });
            }
        });
    }

    private Dictionary<string, object> RecommendStrategy(string scenario, Dictionary<string, object> context)
    {
        var category = scenario.ToLowerInvariant() switch
        {
            "high-frequency" or "streaming" => IoTStrategyCategory.SensorIngestion,
            "edge" or "offline" => IoTStrategyCategory.EdgeIntegration,
            "security" or "authentication" => IoTStrategyCategory.Security,
            "analytics" or "anomaly" => IoTStrategyCategory.Analytics,
            "provisioning" or "onboarding" => IoTStrategyCategory.Provisioning,
            _ => IoTStrategyCategory.DeviceManagement
        };

        var strategy = _registry.SelectBestStrategy(category);

        return new Dictionary<string, object>
        {
            ["success"] = strategy != null,
            ["recommendedStrategy"] = strategy?.StrategyId ?? "device-registry",
            ["strategyName"] = strategy?.StrategyName ?? "Device Registry",
            ["category"] = category.ToString(),
            ["reasoning"] = $"Selected based on scenario '{scenario}' requirements"
        };
    }

    #endregion

    #region Helper Methods

    private string DetermineProtocolStrategy(string deviceId)
    {
        if (_deviceStates.TryGetValue(deviceId, out var state))
        {
            return state.PreferredProtocol ?? "mqtt";
        }
        return "mqtt"; // Default to MQTT
    }

    private void IncrementUsageStats(string operation)
    {
        _usageStats.AddOrUpdate(operation, 1, (_, count) => count + 1);
    }

    private void DiscoverAndRegisterStrategies()
    {
        var strategyTypes = GetType().Assembly
            .GetTypes()
            .Where(t => !t.IsAbstract && typeof(IIoTStrategyBase).IsAssignableFrom(t));

        foreach (var strategyType in strategyTypes)
        {
            try
            {
                if (Activator.CreateInstance(strategyType) is IIoTStrategyBase strategy)
                {
                    // Register in domain registry for typed dispatch (IDeviceManagementStrategy, ISensorIngestionStrategy, etc.)
                    _registry.Register(strategy);

                    // Also register via PluginBase base-class registry for unified strategy lifecycle (AD-65.4)
                    if (strategy is DataWarehouse.SDK.Contracts.IStrategy baseStrategy)
                        RegisterStrategy(baseStrategy);
                }
            }
            catch
            {

                // Strategy failed to instantiate, skip
                System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
            }
        }
    }

    #endregion

    #region Metadata

    /// <inheritdoc/>
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["Description"] = "Ultimate IoT Integration plugin with 50+ strategies";
        metadata["StrategyCount"] = _registry.Count;
        metadata["Categories"] = _registry.GetCategorySummary();
        metadata["SupportedProtocols"] = new[] { "MQTT", "CoAP", "LwM2M", "AMQP", "Modbus", "OPC-UA", "HTTP", "WebSocket" };
        metadata["ActiveDevices"] = _deviceStates.Count;
        metadata["TotalTelemetryMessages"] = Interlocked.Read(ref _totalTelemetryMessages);
        return metadata;
    }

    #endregion

    /// <summary>
    /// Disposes resources.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_disposed) return;
            _disposed = true;
            _deviceStates.Clear();
            _telemetryBuffers.Clear();
            _usageStats.Clear();
        }
        base.Dispose(disposing);
    }


    #region Hierarchy StreamingPluginBase Abstract Methods
    /// <inheritdoc/>
    public override Task PublishAsync(string topic, Stream data, CancellationToken ct = default)
        => Task.CompletedTask;
    /// <inheritdoc/>
    public override async IAsyncEnumerable<Dictionary<string, object>> SubscribeAsync(string topic, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    { await Task.CompletedTask; yield break; }
    #endregion
}