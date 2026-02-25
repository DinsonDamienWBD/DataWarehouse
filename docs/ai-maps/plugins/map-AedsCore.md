# Plugin: AedsCore
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.AedsCore

### File: Plugins/DataWarehouse.Plugins.AedsCore/AedsCorePlugin.cs
```csharp
public class AedsCorePlugin : OrchestrationPluginBase
{
}
    public AedsCorePlugin(ILogger<AedsCorePlugin> logger);
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override string OrchestrationMode;;
    public override PluginCategory Category;;
    public async Task<ValidationResult> ValidateManifestAsync(IntentManifest manifest, CancellationToken ct = default);
    public async Task<bool> VerifySignatureAsync(IntentManifest manifest, CancellationToken ct = default);
    public int ComputePriorityScore(IntentManifest manifest);
    public async Task CacheManifestAsync(IntentManifest manifest, CancellationToken ct = default);
    public async Task<IntentManifest?> GetCachedManifestAsync(string manifestId, CancellationToken ct = default);
    public async Task<int> CleanupExpiredManifestsAsync(CancellationToken ct = default);
    public override Task StartAsync(CancellationToken ct);;
    public override Task StopAsync();;
    protected override void Dispose(bool disposing);
}
```
```csharp
public class ValidationResult
{
}
    public string ManifestId { get; set; };
    public bool IsValid { get; set; }
    public List<string> Errors { get; };
    public List<string> Warnings { get; };
    public void AddError(string error);
    public void AddWarning(string warning);
    public override string ToString();
}
```

### File: Plugins/DataWarehouse.Plugins.AedsCore/ClientCourierPlugin.cs
```csharp
[PluginProfile(ServiceProfileType.Client)]
public class ClientCourierPlugin : PlatformPluginBase
{
}
    public ClientCourierPlugin(ILogger<ClientCourierPlugin> logger);
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override string PlatformDomain;;
    public override PluginCategory Category;;
    public event EventHandler<ManifestReceivedEventArgs>? ManifestReceived;
    public event EventHandler<FileChangedEventArgs>? FileChanged;
    public async Task StartAsync(IControlPlaneTransport controlPlane, IDataPlaneTransport dataPlane, SentinelConfig sentinelConfig, ExecutorConfig executorConfig, CancellationToken ct = default);
    public async Task StopCourierAsync();
    public override Task StopAsync();;
    public IReadOnlyList<WatchedFile> GetWatchedFiles();
    public override Task StartAsync(CancellationToken ct);;
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.AedsCore/Http2DataPlanePlugin.cs
```csharp
public class Http2DataPlanePlugin : DataPlaneTransportPluginBase
{
}
    public Http2DataPlanePlugin(ILogger<Http2DataPlanePlugin> logger);
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override PluginCategory Category;;
    public override string TransportId;;
    protected override async Task<Stream> FetchPayloadAsync(string payloadId, DataPlaneConfig config, IProgress<TransferProgress>? progress, CancellationToken ct);
    protected override async Task<Stream> FetchDeltaAsync(string payloadId, string baseVersion, DataPlaneConfig config, IProgress<TransferProgress>? progress, CancellationToken ct);
    protected override async Task<string> PushPayloadAsync(Stream data, PayloadMetadata metadata, DataPlaneConfig config, IProgress<TransferProgress>? progress, CancellationToken ct);
    protected override async Task<bool> CheckExistsAsync(string payloadId, DataPlaneConfig config, CancellationToken ct);
    protected override async Task<PayloadDescriptor?> FetchInfoAsync(string payloadId, DataPlaneConfig config, CancellationToken ct);
    public override Task StartAsync(CancellationToken ct);;
    public override Task StopAsync();;
    protected override void Dispose(bool disposing);
}
```
```csharp
private class UploadResult
{
}
    public string PayloadId { get; set; };
    public string ContentHash { get; set; };
    public long SizeBytes { get; set; }
}
```
```csharp
internal class ProgressReportingStream : Stream
{
}
    public ProgressReportingStream(Stream innerStream, long totalBytes, IProgress<TransferProgress> progress, ILogger logger);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _innerStream.Position; set => _innerStream.Position = value; }
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken);
    public override int Read(byte[] buffer, int offset, int count);
    public override void Flush();;
    public override Task FlushAsync(CancellationToken cancellationToken);;
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    public override void Write(byte[] buffer, int offset, int count);;
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.AedsCore/IntentManifestSignerPlugin.cs
```csharp
public sealed class IntentManifestSignerPlugin : FeaturePluginBase
{
}
    public IntentManifestSignerPlugin(ILogger<IntentManifestSignerPlugin> logger);
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override string FeatureCategory;;
    public override PluginCategory Category;;
    public async Task<ManifestSignature> SignManifestAsync(IntentManifest manifest, string keyId, string algorithm, bool isReleaseKey, CancellationToken ct = default);
    public override Task StartAsync(CancellationToken ct);
    public override Task StopAsync();
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.AedsCore/ServerDispatcherPlugin.cs
```csharp
[PluginProfile(ServiceProfileType.Server)]
public class ServerDispatcherPlugin : ServerDispatcherPluginBase
{
}
    public ServerDispatcherPlugin(ILogger<ServerDispatcherPlugin> logger);
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override PluginCategory Category;;
    protected override async Task<string> EnqueueJobAsync(IntentManifest manifest, CancellationToken ct);
    protected override async Task ProcessJobAsync(string jobId, CancellationToken ct);
    protected override async Task<AedsClient> CreateClientAsync(ClientRegistration registration, CancellationToken ct);
    protected override async Task<DistributionChannel> CreateChannelInternalAsync(ChannelCreation channel, CancellationToken ct);
    public async Task UpdateHeartbeatAsync(string clientId, ClientStatus status, CancellationToken ct = default);
    public async Task<bool> SubscribeClientAsync(string clientId, string channelId, CancellationToken ct = default);
    public override Task StartAsync(CancellationToken ct);;
    public override Task StopAsync();;
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.AedsCore/Adapters/MeshNetworkAdapter.cs
```csharp
[SdkCompatibility("3.0.0", Notes = "Phase 36: Mesh network adapter for AEDS (EDGE-08)")]
public sealed class MeshNetworkAdapter : IDisposable, IAsyncDisposable
{
}
    public event EventHandler<DataReceivedEventArgs>? OnDataReceived;
    public event EventHandler<MeshTopology>? OnTopologyChanged;
    public MeshNetworkAdapter(MeshSettings settings);
    public async Task StartAsync(CancellationToken ct = default);
    public async Task StopAsync(CancellationToken ct = default);
    public async Task SendDataAsync(byte[] data, string destination, CancellationToken ct = default);
    public async Task<MeshTopology> DiscoverTopologyAsync(CancellationToken ct = default);
    public void Dispose();
    public async ValueTask DisposeAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.AedsCore/ControlPlane/GrpcControlPlanePlugin.cs
```csharp
[PluginProfile(ServiceProfileType.Server)]
public class GrpcControlPlanePlugin : ControlPlaneTransportPluginBase
{
}
    public GrpcControlPlanePlugin(ILogger<GrpcControlPlanePlugin> logger);
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override PluginCategory Category;;
    public override string TransportId;;
    protected override async Task EstablishConnectionAsync(ControlPlaneConfig config, CancellationToken ct);
    protected override async Task TransmitManifestAsync(IntentManifest manifest, CancellationToken ct);
    protected override async IAsyncEnumerable<IntentManifest> ListenForManifestsAsync([EnumeratorCancellation] CancellationToken ct);
    protected override async Task TransmitHeartbeatAsync(HeartbeatMessage heartbeat, CancellationToken ct);
    protected override async Task JoinChannelAsync(string channelId, CancellationToken ct);
    protected override async Task LeaveChannelAsync(string channelId, CancellationToken ct);
    protected override async Task CloseConnectionAsync();
    public override Task StartAsync(CancellationToken ct);;
    public override Task StopAsync();;
    protected override void Dispose(bool disposing);
}
```
```csharp
public class ControlMessage
{
}
    public string Type { get; set; };
    public Google.Protobuf.ByteString Payload { get; set; };
}
```
```csharp
public static class AedsControlPlane
{
}
    public class AedsControlPlaneClient;
}
```
```csharp
public class AedsControlPlaneClient
{
}
    public AedsControlPlaneClient(Grpc.Core.CallInvoker callInvoker);
    public AedsControlPlaneClient(GrpcChannel channel) : this(channel.CreateCallInvoker());
    public AsyncDuplexStreamingCall<ControlMessage, ControlMessage> StreamManifests(Metadata? headers = null, DateTime? deadline = null, CancellationToken cancellationToken = default);
}
```

### File: Plugins/DataWarehouse.Plugins.AedsCore/ControlPlane/MqttControlPlanePlugin.cs
```csharp
[PluginProfile(ServiceProfileType.Server)]
public class MqttControlPlanePlugin : ControlPlaneTransportPluginBase
{
}
    public MqttControlPlanePlugin(ILogger<MqttControlPlanePlugin> logger);
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override PluginCategory Category;;
    public override string TransportId;;
    protected override async Task EstablishConnectionAsync(ControlPlaneConfig config, CancellationToken ct);
    protected override async Task TransmitManifestAsync(IntentManifest manifest, CancellationToken ct);
    protected override async IAsyncEnumerable<IntentManifest> ListenForManifestsAsync([EnumeratorCancellation] CancellationToken ct);
    protected override async Task TransmitHeartbeatAsync(HeartbeatMessage heartbeat, CancellationToken ct);
    protected override async Task JoinChannelAsync(string channelId, CancellationToken ct);
    protected override async Task LeaveChannelAsync(string channelId, CancellationToken ct);
    protected override async Task CloseConnectionAsync();
    public override Task StartAsync(CancellationToken ct);;
    public override Task StopAsync();;
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.AedsCore/ControlPlane/WebSocketControlPlanePlugin.cs
```csharp
[PluginProfile(ServiceProfileType.Server)]
public class WebSocketControlPlanePlugin : ControlPlaneTransportPluginBase
{
}
    public WebSocketControlPlanePlugin(ILogger<WebSocketControlPlanePlugin> logger);
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override PluginCategory Category;;
    public override string TransportId;;
    protected override async Task EstablishConnectionAsync(ControlPlaneConfig config, CancellationToken ct);
    public void AddAllowedOrigin(string origin);
    protected override async Task TransmitManifestAsync(IntentManifest manifest, CancellationToken ct);
    protected override async IAsyncEnumerable<IntentManifest> ListenForManifestsAsync([EnumeratorCancellation] CancellationToken ct);
    protected override async Task TransmitHeartbeatAsync(HeartbeatMessage heartbeat, CancellationToken ct);
    protected override async Task JoinChannelAsync(string channelId, CancellationToken ct);
    protected override async Task LeaveChannelAsync(string channelId, CancellationToken ct);
    protected override async Task CloseConnectionAsync();
    public override Task StartAsync(CancellationToken ct);;
    public override Task StopAsync();;
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.AedsCore/DataPlane/Http3DataPlanePlugin.cs
```csharp
public class Http3DataPlanePlugin : DataPlaneTransportPluginBase
{
}
    public Http3DataPlanePlugin(ILogger<Http3DataPlanePlugin> logger);
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override PluginCategory Category;;
    public override string TransportId;;
    protected override async Task<Stream> FetchPayloadAsync(string payloadId, DataPlaneConfig config, IProgress<TransferProgress>? progress, CancellationToken ct);
    protected override async Task<Stream> FetchDeltaAsync(string payloadId, string baseVersion, DataPlaneConfig config, IProgress<TransferProgress>? progress, CancellationToken ct);
    protected override async Task<string> PushPayloadAsync(Stream data, PayloadMetadata metadata, DataPlaneConfig config, IProgress<TransferProgress>? progress, CancellationToken ct);
    protected override async Task<bool> CheckExistsAsync(string payloadId, DataPlaneConfig config, CancellationToken ct);
    protected override async Task<PayloadDescriptor?> FetchInfoAsync(string payloadId, DataPlaneConfig config, CancellationToken ct);
    public override Task StartAsync(CancellationToken ct);;
    public override Task StopAsync();;
    protected override void Dispose(bool disposing);
}
```
```csharp
private class UploadResult
{
}
    public string PayloadId { get; set; };
    public string ContentHash { get; set; };
    public long SizeBytes { get; set; }
}
```
```csharp
private class ProgressReportingStream : Stream
{
}
    public ProgressReportingStream(Stream innerStream, long totalBytes, IProgress<TransferProgress> progress, DateTime startTime, ILogger logger);
    public override bool CanRead;;
    public override bool CanSeek;;
    public override bool CanWrite;;
    public override long Length;;
    public override long Position { get => _innerStream.Position; set => _innerStream.Position = value; }
    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken);
    public override int Read(byte[] buffer, int offset, int count);
    public override void Flush();;
    public override Task FlushAsync(CancellationToken cancellationToken);;
    public override long Seek(long offset, SeekOrigin origin);;
    public override void SetLength(long value);;
    public override void Write(byte[] buffer, int offset, int count);;
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.AedsCore/DataPlane/QuicDataPlanePlugin.cs
```csharp
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
[SupportedOSPlatform("windows")]
public class QuicDataPlanePlugin : DataPlaneTransportPluginBase
{
}
    public QuicDataPlanePlugin(ILogger<QuicDataPlanePlugin> logger);
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override PluginCategory Category;;
    public override string TransportId;;
    protected override async Task<Stream> FetchPayloadAsync(string payloadId, DataPlaneConfig config, IProgress<TransferProgress>? progress, CancellationToken ct);
    protected override async Task<Stream> FetchDeltaAsync(string payloadId, string baseVersion, DataPlaneConfig config, IProgress<TransferProgress>? progress, CancellationToken ct);
    protected override async Task<string> PushPayloadAsync(Stream data, PayloadMetadata metadata, DataPlaneConfig config, IProgress<TransferProgress>? progress, CancellationToken ct);
    protected override async Task<bool> CheckExistsAsync(string payloadId, DataPlaneConfig config, CancellationToken ct);
    protected override async Task<PayloadDescriptor?> FetchInfoAsync(string payloadId, DataPlaneConfig config, CancellationToken ct);
    public override Task StartAsync(CancellationToken ct);;
    public override async Task StopAsync();
}
```
```csharp
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macOS")]
[SupportedOSPlatform("windows")]
private class QuicConnectionPool : IAsyncDisposable
{
}
    public QuicConnectionPool(string serverUrl, ILogger logger);
    public async Task<QuicConnection> GetConnectionAsync(DataPlaneConfig config, CancellationToken ct);
    public async ValueTask DisposeAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.AedsCore/DataPlane/WebTransportDataPlanePlugin.cs
```csharp
[DataWarehouse.SDK.Contracts.SdkCompatibility("3.0.0", Notes = "Phase 36: Pending .NET WebTransport API availability")]
public class WebTransportDataPlanePlugin : DataPlaneTransportPluginBase
{
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override PluginCategory Category;;
    public override string TransportId;;
    protected override Task<Stream> FetchPayloadAsync(string payloadId, DataPlaneConfig config, IProgress<TransferProgress>? progress, CancellationToken ct);;
    protected override Task<Stream> FetchDeltaAsync(string payloadId, string baseVersion, DataPlaneConfig config, IProgress<TransferProgress>? progress, CancellationToken ct);;
    protected override Task<string> PushPayloadAsync(Stream data, PayloadMetadata metadata, DataPlaneConfig config, IProgress<TransferProgress>? progress, CancellationToken ct);;
    protected override Task<bool> CheckExistsAsync(string payloadId, DataPlaneConfig config, CancellationToken ct);;
    protected override Task<PayloadDescriptor?> FetchInfoAsync(string payloadId, DataPlaneConfig config, CancellationToken ct);;
    public override Task StartAsync(CancellationToken ct);;
    public override Task StopAsync();;
}
```

### File: Plugins/DataWarehouse.Plugins.AedsCore/Extensions/CodeSigningPlugin.cs
```csharp
public sealed class CodeSigningPlugin : SecurityPluginBase
{
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override string SecurityDomain;;
    public override PluginCategory Category;;
    public async Task<VerificationResult> VerifyReleaseKeyAsync(IntentManifest manifest, CancellationToken ct = default);
    public bool ValidateCertificateChain(string[] certificateChain);
    public override Task StartAsync(CancellationToken ct);
    public override Task StopAsync();
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.AedsCore/Extensions/DeltaSyncPlugin.cs
```csharp
public sealed class DeltaSyncPlugin : DataManagementPluginBase
{
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override string DataManagementDomain;;
    public override PluginCategory Category;;
    public async Task<DeltaDescriptor> ComputeDeltaAsync(Stream baseStream, Stream targetStream, CancellationToken ct = default);
    public async Task<Stream> ApplyDeltaAsync(Stream baseStream, DeltaDescriptor delta, CancellationToken ct = default);
    public bool IsWorthwhile(long baseSize, long targetSize, long deltaSize);
    public async Task<string[]> GenerateSignatureAsync(Stream stream, CancellationToken ct = default);
    public static bool IsEnabled(ClientCapabilities capabilities);
    public override Task StartAsync(CancellationToken ct);
    public override Task StopAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.AedsCore/Extensions/GlobalDeduplicationPlugin.cs
```csharp
public sealed class GlobalDeduplicationPlugin : DataManagementPluginBase
{
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override string DataManagementDomain;;
    public override PluginCategory Category;;
    public Task<bool> CheckIfExistsLocallyAsync(string contentHash, CancellationToken ct = default);
    public async Task<List<string>> QueryServerForDedupAsync(string contentHash, CancellationToken ct = default);
    public async Task RegisterContentHashAsync(string contentHash, string payloadId, CancellationToken ct = default);
    public override Task StartAsync(CancellationToken ct);
    public override Task StopAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.AedsCore/Extensions/MulePlugin.cs
```csharp
public sealed class MulePlugin : DataManagementPluginBase
{
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override string DataManagementDomain;;
    public override PluginCategory Category;;
    public async Task ExportManifestsAsync(string[] manifestIds, string usbMountPath, CancellationToken ct = default);
    public async Task<int> ImportManifestsAsync(string usbMountPath, CancellationToken ct = default);
    public Dictionary<string, string> ValidateMuleIntegrity(string usbMountPath);
    public static bool IsEnabled(ClientCapabilities capabilities);
    public override Task StartAsync(CancellationToken ct);
    public override Task StopAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.AedsCore/Extensions/NotificationPlugin.cs
```csharp
public sealed class NotificationPlugin : PlatformPluginBase
{
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override string PlatformDomain;;
    public override PluginCategory Category;;
    public async Task ShowNotificationAsync(IntentManifest manifest, CancellationToken ct = default);
    public PolicyAction AcknowledgeNotification(string manifestId, bool userApproved);
    public static bool IsEnabled(ClientCapabilities capabilities);
    public override Task StartAsync(CancellationToken ct);
    public override Task StopAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.AedsCore/Extensions/PolicyEnginePlugin.cs
```csharp
public sealed class PolicyEnginePlugin : SecurityPluginBase, IClientPolicyEngine
{
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override string SecurityDomain;;
    public override PluginCategory Category;;
    public Task<PolicyDecision> EvaluateAsync(IntentManifest manifest, PolicyContext context);
    public Task LoadPolicyAsync(string policyPath);
    public void AddRule(PolicyRule rule);
    public override Task StartAsync(CancellationToken ct);
    public override Task StopAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.AedsCore/Extensions/PreCogPlugin.cs
```csharp
public sealed class PreCogPlugin : DataManagementPluginBase
{
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override string DataManagementDomain;;
    public override PluginCategory Category;;
    public async Task<List<PredictedContent>> PredictContentAsync(Dictionary<string, object> context, CancellationToken ct = default);
    public async Task PrefetchAsync(string payloadId, CancellationToken ct = default);
    public void LearnFromDownload(string payloadId, DateTimeOffset timestamp, bool wasUseful);
    public override Task StartAsync(CancellationToken ct);
    public override Task StopAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.AedsCore/Extensions/SwarmIntelligencePlugin.cs
```csharp
public sealed class SwarmIntelligencePlugin : OrchestrationPluginBase
{
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override string OrchestrationMode;;
    public override PluginCategory Category;;
    public async Task<List<PeerInfo>> GetPeerListAsync(string payloadId, CancellationToken ct = default);
    public async Task<Dictionary<int, byte[]>> DownloadFromPeersAsync(string payloadId, List<PeerInfo> peers, int totalChunks, IProgress<TransferProgress>? progress = null, CancellationToken ct = default);
    public async Task AnnouncePeerAvailabilityAsync(string payloadId, bool available, CancellationToken ct = default);
    public void RegisterLocalChunks(string payloadId, int[] chunkIndices);
    public void UpdatePeerCache(string payloadId, List<PeerInfo> peers);
    public static bool IsEnabled(ClientCapabilities capabilities);
    public override Task StartAsync(CancellationToken ct);
    public override Task StopAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.AedsCore/Extensions/ZeroTrustPairingPlugin.cs
```csharp
public sealed class ZeroTrustPairingPlugin : SecurityPluginBase
{
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override string SecurityDomain;;
    public override PluginCategory Category;;
    public string GeneratePairingPIN();
    public async Task<AedsClient> RegisterClientAsync(string clientName, ClientCapabilities capabilities, CancellationToken ct = default);
    public async Task ElevateTrustAsync(string clientId, string adminVerifiedPIN, ClientTrustLevel newLevel, CancellationToken ct = default);
    public bool VerifyPairing(string clientId);
    public override Task StartAsync(CancellationToken ct);
    public override Task StopAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.AedsCore/Scaling/AedsScalingManager.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-07: AEDS scaling manager with bounded caches, partitioned jobs, per-collection locks")]
public sealed class AedsScalingManager : IScalableSubsystem, IDisposable
{
}
    public const int DefaultManifestsCacheSize = 10_000;
    public const int DefaultValidationsCacheSize = 50_000;
    public const int DefaultJobsCacheSize = 100_000;
    public const int DefaultClientsCacheSize = 10_000;
    public const int DefaultChannelsCacheSize = 1_000;
    public const int MinChunkSizeBytes = 4 * 1024;
    public const int MaxChunkSizeBytesLimit = 16 * 1024 * 1024;
    public const int DefaultChunkSizeBytes = 64 * 1024;
    public AedsScalingManager(ScalingLimits? initialLimits = null, int? partitionCount = null);
    public byte[]? GetManifest(string key);
    public void PutManifest(string key, byte[] data);
    public byte[]? GetValidation(string key);
    public void PutValidation(string key, byte[] data);
    public byte[]? GetJob(string key);
    public void PutJob(string key, byte[] data);
    public byte[]? GetClient(string key);
    public void PutClient(string key, byte[] data);
    public byte[]? GetChannel(string key);
    public void PutChannel(string key, byte[] data);
    public async Task AcquireCollectionLockAsync(string collectionName, CancellationToken ct = default);
    public void ReleaseCollectionLock(string collectionName);
    public void EnqueueJob(AedsJob job);
    public IReadOnlyDictionary<int, int> GetPartitionDepths();
    public int TotalQueuedJobs;;
    public int MaxConcurrentChunks;;
    public int ChunkSizeBytes;;
    public void RecordChunkThroughput(long bytesTransferred, TimeSpan elapsed);
    public void AutoAdjustChunkParameters();
    public IReadOnlyDictionary<string, object> GetScalingMetrics();
    public Task ReconfigureLimitsAsync(ScalingLimits limits, CancellationToken ct = default);
    public ScalingLimits CurrentLimits
{
    get
    {
        lock (_configLock)
        {
            return _currentLimits;
        }
    }
}
    public BackpressureState CurrentBackpressureState
{
    get
    {
        int totalQueued = TotalQueuedJobs;
        if (_currentLimits.MaxQueueDepth == 0)
            return BackpressureState.Normal;
        double utilization = (double)totalQueued / _currentLimits.MaxQueueDepth;
        return utilization switch
        {
            >= 0.80 => BackpressureState.Critical,
            >= 0.50 => BackpressureState.Warning,
            _ => BackpressureState.Normal
        };
    }
}
    public void Dispose();
}
```
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-07: AEDS job for partitioned queue processing")]
public sealed class AedsJob
{
}
    public required string JobId { get; init; }
    public required string CollectionName { get; init; }
    public required string OperationType { get; init; }
    public byte[]? Payload { get; init; }
    public DateTimeOffset CreatedAt { get; init; };
    public Func<CancellationToken, Task>? Handler { get; init; }
    internal async Task ExecuteAsync(CancellationToken ct);
}
```
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-07: Sliding window throughput tracker for AEDS chunk sizing")]
internal sealed class SlidingWindowThroughput
{
}
    public void Record(long bytes, TimeSpan elapsed);
    public double GetAverageBytesPerSecond();
}
```
