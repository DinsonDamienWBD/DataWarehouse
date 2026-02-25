# Plugin: UltimateFilesystem
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.UltimateFilesystem

### File: Plugins/DataWarehouse.Plugins.UltimateFilesystem/UltimateFilesystemPlugin.cs
```csharp
public sealed class UltimateFilesystemPlugin : DataWarehouse.SDK.Contracts.Hierarchy.StoragePluginBase, IDisposable
{
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override PluginCategory Category;;
    protected override Task OnStartCoreAsync(CancellationToken ct);;
    protected override Task OnStopCoreAsync();;
    public string SemanticDescription;;
    public string[] SemanticTags;;
    public FilesystemStrategyRegistry Registry;;
    public string DefaultStrategy { get => _defaultStrategy; set => _defaultStrategy = value; }
    public bool AuditEnabled { get => _auditEnabled; set => _auditEnabled = value; }
    public UltimateFilesystemPlugin();
    public override async Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request);
    protected override List<PluginCapabilityDescriptor> GetCapabilities();
    protected override Dictionary<string, object> GetMetadata();
    public override Task OnMessageAsync(PluginMessage message);
    public override Task<DataWarehouse.SDK.Contracts.Storage.StorageObjectMetadata> StoreAsync(string key, Stream data, IDictionary<string, string>? metadata = null, CancellationToken ct = default);;
    public override Task<Stream> RetrieveAsync(string key, CancellationToken ct = default);;
    public override Task DeleteAsync(string key, CancellationToken ct = default);;
    public override Task<bool> ExistsAsync(string key, CancellationToken ct = default);;
    public override async IAsyncEnumerable<DataWarehouse.SDK.Contracts.Storage.StorageObjectMetadata> ListAsync(string? prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default);
    public override Task<DataWarehouse.SDK.Contracts.Storage.StorageObjectMetadata> GetMetadataAsync(string key, CancellationToken ct = default);;
    public override Task<DataWarehouse.SDK.Contracts.Storage.StorageHealthInfo> GetHealthAsync(CancellationToken ct = default);;
    protected override void Dispose(bool disposing);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateFilesystem/FilesystemStrategyBase.cs
```csharp
public sealed record FilesystemMetadata
{
}
    public required string FilesystemType { get; init; }
    public long TotalBytes { get; init; }
    public long AvailableBytes { get; init; }
    public long UsedBytes { get; init; }
    public int BlockSize { get; init; };
    public bool IsReadOnly { get; init; }
    public bool SupportsSparse { get; init; }
    public bool SupportsCompression { get; init; }
    public bool SupportsEncryption { get; init; }
    public bool SupportsDeduplication { get; init; }
    public bool SupportsSnapshots { get; init; }
    public string? MountPoint { get; init; }
    public DateTime DetectedAt { get; init; };
}
```
```csharp
public sealed record BlockIoOptions
{
}
    public bool DirectIo { get; init; }
    public bool AsyncIo { get; init; }
    public int BufferSize { get; init; };
    public int Priority { get; init; };
    public bool WriteThrough { get; init; }
    public bool ReadAhead { get; init; };
    public int MaxConcurrentOps { get; init; };
}
```
```csharp
public sealed record FilesystemStrategyCapabilities
{
}
    public required bool SupportsDirectIo { get; init; }
    public required bool SupportsAsyncIo { get; init; }
    public required bool SupportsMmap { get; init; }
    public required bool SupportsKernelBypass { get; init; }
    public required bool SupportsVectoredIo { get; init; }
    public required bool SupportsSparse { get; init; }
    public required bool SupportsAutoDetect { get; init; }
    public long MaxFileSize { get; init; };
}
```
```csharp
public interface IFilesystemStrategy
{
}
    string StrategyId { get; }
    string DisplayName { get; }
    FilesystemStrategyCategory Category { get; }
    FilesystemStrategyCapabilities Capabilities { get; }
    string SemanticDescription { get; }
    string[] Tags { get; }
    Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);;
    Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);;
    Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);;
    Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);;
    Task InitializeAsync(CancellationToken ct = default);;
    Task DisposeAsync();;
}
```
```csharp
public abstract class FilesystemStrategyBase : StrategyBase, IFilesystemStrategy
{
}
    public abstract override string StrategyId { get; }
    public abstract string DisplayName { get; }
    public override string Name;;
    public abstract FilesystemStrategyCategory Category { get; }
    public abstract FilesystemStrategyCapabilities Capabilities { get; }
    public abstract string SemanticDescription { get; }
    public abstract string[] Tags { get; }
    protected override async Task InitializeAsyncCore(CancellationToken cancellationToken);
    protected override async Task ShutdownAsyncCore(CancellationToken cancellationToken);
    protected virtual Task InitializeCoreAsync(CancellationToken ct);;
    protected virtual Task DisposeCoreAsync();;
    public abstract Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);;
    public abstract Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);;
    public abstract Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);;
    public abstract Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);;
}
```
```csharp
public sealed class FilesystemStrategyRegistry
{
}
    public void Register(IFilesystemStrategy strategy);
    public bool Unregister(string strategyId);;
    public IFilesystemStrategy? Get(string strategyId);;
    public IReadOnlyCollection<IFilesystemStrategy> GetAll();;
    public IReadOnlyCollection<IFilesystemStrategy> GetByCategory(FilesystemStrategyCategory category);;
    public int Count;;
    public int AutoDiscover(params System.Reflection.Assembly[] assemblies);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateFilesystem/Scaling/FilesystemScalingManager.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 88-11: Filesystem scaling with dynamic I/O scheduling and kernel bypass")]
public sealed class FilesystemScalingManager : IScalableSubsystem, IDisposable
{
}
    public FilesystemScalingManager(ScalingLimits? initialLimits = null, int kernelBypassRedetectIntervalMs = DefaultKernelBypassRedetectIntervalMs, int defaultIoQuotaPerCaller = DefaultIoQuotaPerCaller);
    public IReadOnlyDictionary<string, object> GetScalingMetrics();
    public async Task ReconfigureLimitsAsync(ScalingLimits limits, CancellationToken ct = default);
    public ScalingLimits CurrentLimits;;
    public BackpressureState CurrentBackpressureState;;
    public async Task ScheduleIoAsync(Func<CancellationToken, Task<long>> operation, IoPriority priority = IoPriority.Normal, string? callerId = null, CancellationToken ct = default);
    public Task ReconfigureQueueDepthAsync(string strategyType, int queueDepth, CancellationToken ct = default);
    public int GetQueueDepth(string strategyType);
    public bool IsIoUringAvailable;;
    public bool IsDirectIoAvailable;;
    public void ForceKernelBypassRedetect();
    public void ConfigureCallerQuota(string callerId, int maxOperations);
    public void Dispose();
    internal sealed record IoQuotaInfo;
}
```
```csharp
private sealed record IoOperation
{
}
    public required Func<CancellationToken, Task<long>> Execute { get; init; }
    public required IoPriority Priority { get; init; }
    public string? CallerId { get; init; }
    public required TaskCompletionSource<long> Completion { get; init; }
}
```
```csharp
internal sealed record IoQuotaInfo
{
}
    public required string CallerId { get; init; }
    public required int MaxOperations { get; init; }
    public required int CurrentOperations { get; init; }
    public required DateTime WindowStartUtc { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateFilesystem/Strategies/FormatStrategies.cs
```csharp
public sealed class Fat32Strategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);
    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);
}
```
```csharp
public sealed class ExFatStrategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);
    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);
}
```
```csharp
public sealed class F2FsStrategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);
    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);
}
```
```csharp
public sealed class Ext3Strategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);
    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);
}
```
```csharp
public sealed class Ext2Strategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);
    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);
}
```
```csharp
public sealed class HfsStrategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);
    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);
}
```
```csharp
public sealed class Hammer2Strategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);
    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);
}
```
```csharp
public sealed class Ocfs2Strategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);
    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);
}
```
```csharp
public sealed class TmpfsStrategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);
    public override Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);
}
```
```csharp
public sealed class ProcfsStrategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);
    public override Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);
}
```
```csharp
public sealed class SysfsStrategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);
    public override Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateFilesystem/Strategies/UnixFuseFilesystemStrategy.cs
```csharp
public sealed class UnixFuseFilesystemStrategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);
    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateFilesystem/Strategies/DeviceDiscoveryStrategy.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 90: Device discovery strategy (BMDV-01/BMDV-08)")]
public sealed class DeviceDiscoveryStrategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    protected override Task InitializeCoreAsync(CancellationToken ct);
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);;
    public override Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);;
    public override Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);;
    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);
    public Task<IReadOnlyList<PhysicalDeviceInfo>> DiscoverAsync(DeviceDiscoveryOptions? options, CancellationToken ct);
    public DeviceTopologyTree BuildTopology(IReadOnlyList<PhysicalDeviceInfo> devices);
    public IReadOnlyList<NumaAffinityInfo> GetNumaAffinity(DeviceTopologyTree tree);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateFilesystem/Strategies/DetectionStrategies.cs
```csharp
public sealed class AutoDetectStrategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);
    public override Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);
}
```
```csharp
public sealed class NtfsStrategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);
    public override Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateFilesystem/Strategies/DevicePoolStrategy.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 90: Device pool management strategy (BMDV-05/BMDV-06/BMDV-07/BMDV-10/BMDV-11/BMDV-12)")]
public sealed class DevicePoolStrategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task DisposeCoreAsync();
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);;
    public override Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);;
    public override Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);;
    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);
    public Task<DevicePoolDescriptor> CreatePoolAsync(string name, StorageTier? tier, LocalityTag? locality, IReadOnlyList<IPhysicalBlockDevice> devices, CancellationToken ct);
    public Task<IReadOnlyList<DevicePoolDescriptor>> GetPoolsAsync();
    public Task DeletePoolAsync(Guid poolId, CancellationToken ct);
    public Task<BootstrapResult> BootstrapAsync(CancellationToken ct);
    public Task StartHotSwapAsync(CancellationToken ct);
    public Task StopHotSwapAsync();
    public HotSwapManager? GetHotSwapManager();;
    public DevicePoolManager? GetPoolManager();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateFilesystem/Strategies/DeviceHealthStrategy.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 90: Device health monitoring strategy (BMDV-03/BMDV-04)")]
public sealed class DeviceHealthStrategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    protected override Task InitializeCoreAsync(CancellationToken ct);
    protected override async Task DisposeCoreAsync();
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);;
    public override Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);;
    public override Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);;
    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);
    public Task<PhysicalDeviceHealth> GetHealthAsync(string devicePath, BusType busType, CancellationToken ct);
    public FailurePrediction GetPrediction(string deviceId, PhysicalDeviceHealth health);
    public PhysicalDeviceManager? GetDeviceManager();;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateFilesystem/Strategies/NetworkFilesystemStrategies.cs
```csharp
public sealed class NfsStrategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);
    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);
}
```
```csharp
public sealed class SmbStrategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);
    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);
}
```
```csharp
public sealed class GlusterFsStrategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);
    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);
}
```
```csharp
public sealed class CephFsStrategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);
    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);
}
```
```csharp
public sealed class LustreStrategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);
    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);
}
```
```csharp
public sealed class GpfsStrategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);
    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);
}
```
```csharp
public sealed class BeeGfsStrategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);
    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);
}
```
```csharp
public sealed class Nfs3Strategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);;
    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);;
}
```
```csharp
public sealed class Nfs4Strategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);;
    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateFilesystem/Strategies/FilesystemOperations.cs
```csharp
public sealed record FilesystemEntry
{
}
    public required string Path { get; init; }
    public required string Name { get; init; }
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTime CreatedUtc { get; init; }
    public DateTime ModifiedUtc { get; init; }
    public DateTime AccessedUtc { get; init; }
    public FileAttributes Attributes { get; init; }
    public string? Owner { get; init; }
    public string? Permissions { get; init; }
    public long InodeNumber { get; init; }
    public int LinkCount { get; init; };
    public IReadOnlyDictionary<string, byte[]>? ExtendedAttributes { get; init; }
}
```
```csharp
public sealed record FormatOptions
{
}
    public int BlockSize { get; init; };
    public string? Label { get; init; }
    public Guid? Uuid { get; init; }
    public JournalMode JournalMode { get; init; };
    public int InodeSize { get; init; };
    public int ReservedBlocksPercent { get; init; };
    public bool EnableInlineData { get; init; };
    public bool Enable64Bit { get; init; };
    public bool EnableEncryption { get; init; }
    public bool EnableCompression { get; init; }
    public string? CompressionAlgorithm { get; init; }
    public bool EnableChecksums { get; init; };
    public CaseSensitivity CaseSensitivity { get; init; };
    public long? SizeLimit { get; init; }
}
```
```csharp
public sealed record MountOptions
{
}
    public bool ReadOnly { get; init; }
    public bool NoAccessTime { get; init; };
    public bool Sync { get; init; }
    public bool Discard { get; init; }
    public IReadOnlyDictionary<string, string>? AdditionalOptions { get; init; }
}
```
```csharp
public sealed class MountedFilesystem
{
}
    public required string FilesystemType { get; init; }
    public required string DevicePath { get; init; }
    public required string MountPoint { get; init; }
    public bool IsReadOnly { get; init; }
    public DateTime MountedAt { get; init; };
    public SuperblockDetails? Superblock { get; init; }
    public FilesystemMetadata? Metadata { get; init; }
    public MountOptions? Options { get; init; }
}
```
```csharp
public interface IFilesystemOperations
{
}
    Task<MountedFilesystem> FormatAsync(string devicePath, FormatOptions? options = null, CancellationToken ct = default);;
    Task<MountedFilesystem> MountAsync(string devicePath, string mountPoint, MountOptions? options = null, CancellationToken ct = default);;
    Task UnmountAsync(string mountPoint, CancellationToken ct = default);;
    Task<FilesystemEntry> CreateFileAsync(string path, byte[] content, CancellationToken ct = default);;
    Task<FilesystemEntry> CreateDirectoryAsync(string path, CancellationToken ct = default);;
    Task<byte[]> ReadFileAsync(string path, CancellationToken ct = default);;
    Task WriteFileAsync(string path, byte[] content, CancellationToken ct = default);;
    Task DeleteAsync(string path, bool recursive = false, CancellationToken ct = default);;
    Task<IReadOnlyList<FilesystemEntry>> ListDirectoryAsync(string path, CancellationToken ct = default);;
    Task<FilesystemEntry> GetAttributesAsync(string path, CancellationToken ct = default);;
    Task SetAttributesAsync(string path, FileAttributes attributes, CancellationToken ct = default);;
}
```
```csharp
public abstract class FilesystemOperationsBase : IFilesystemOperations
{
}
    protected abstract string FilesystemType { get; }
    protected abstract long MaxFileSize { get; }
    protected virtual int DefaultBlockSize;;
    public virtual async Task<MountedFilesystem> FormatAsync(string devicePath, FormatOptions? options = null, CancellationToken ct = default);
    protected abstract Task WriteSuperblockAsync(string devicePath, Guid uuid, string label, int blockSize, FormatOptions options, CancellationToken ct);;
    public virtual Task<MountedFilesystem> MountAsync(string devicePath, string mountPoint, MountOptions? options = null, CancellationToken ct = default);
    public virtual Task UnmountAsync(string mountPoint, CancellationToken ct = default);
    public virtual async Task<FilesystemEntry> CreateFileAsync(string path, byte[] content, CancellationToken ct = default);
    public virtual Task<FilesystemEntry> CreateDirectoryAsync(string path, CancellationToken ct = default);
    public virtual async Task<byte[]> ReadFileAsync(string path, CancellationToken ct = default);
    public virtual async Task WriteFileAsync(string path, byte[] content, CancellationToken ct = default);
    public virtual Task DeleteAsync(string path, bool recursive = false, CancellationToken ct = default);
    public virtual Task<IReadOnlyList<FilesystemEntry>> ListDirectoryAsync(string path, CancellationToken ct = default);
    public virtual Task<FilesystemEntry> GetAttributesAsync(string path, CancellationToken ct = default);
    public virtual Task SetAttributesAsync(string path, FileAttributes attributes, CancellationToken ct = default);
    protected void ValidateFileSize(long size);
    protected static FilesystemEntry CreateEntryFromFileInfo(FileInfo info);
}
```
```csharp
public sealed class Ext4Operations : FilesystemOperationsBase
{
}
    protected override string FilesystemType;;
    protected override long MaxFileSize;;
    protected override int DefaultBlockSize;;
    protected override async Task WriteSuperblockAsync(string devicePath, Guid uuid, string label, int blockSize, FormatOptions options, CancellationToken ct);
    public override async Task<FilesystemEntry> CreateFileAsync(string path, byte[] content, CancellationToken ct = default);
}
```
```csharp
public sealed class NtfsOperations : FilesystemOperationsBase
{
}
    protected override string FilesystemType;;
    protected override long MaxFileSize;;
    protected override int DefaultBlockSize;;
    protected override async Task WriteSuperblockAsync(string devicePath, Guid uuid, string label, int blockSize, FormatOptions options, CancellationToken ct);
    public override async Task<FilesystemEntry> CreateFileAsync(string path, byte[] content, CancellationToken ct = default);
}
```
```csharp
public sealed class XfsOperations : FilesystemOperationsBase
{
}
    protected override string FilesystemType;;
    protected override long MaxFileSize;;
    protected override int DefaultBlockSize;;
    protected override async Task WriteSuperblockAsync(string devicePath, Guid uuid, string label, int blockSize, FormatOptions options, CancellationToken ct);
}
```
```csharp
public sealed class BtrfsOperations : FilesystemOperationsBase
{
}
    protected override string FilesystemType;;
    protected override long MaxFileSize;;
    protected override int DefaultBlockSize;;
    protected override async Task WriteSuperblockAsync(string devicePath, Guid uuid, string label, int blockSize, FormatOptions options, CancellationToken ct);
    public override async Task WriteFileAsync(string path, byte[] content, CancellationToken ct = default);
}
```
```csharp
public sealed class ZfsOperations : FilesystemOperationsBase
{
}
    protected override string FilesystemType;;
    protected override long MaxFileSize;;
    protected override int DefaultBlockSize;;
    protected override async Task WriteSuperblockAsync(string devicePath, Guid uuid, string label, int blockSize, FormatOptions options, CancellationToken ct);
    public override async Task WriteFileAsync(string path, byte[] content, CancellationToken ct = default);
}
```
```csharp
public sealed class ApfsOperations : FilesystemOperationsBase
{
}
    protected override string FilesystemType;;
    protected override long MaxFileSize;;
    protected override int DefaultBlockSize;;
    protected override async Task WriteSuperblockAsync(string devicePath, Guid uuid, string label, int blockSize, FormatOptions options, CancellationToken ct);
}
```
```csharp
public sealed class Fat32Operations : FilesystemOperationsBase
{
}
    protected override string FilesystemType;;
    protected override long MaxFileSize;;
    protected override int DefaultBlockSize;;
    protected override async Task WriteSuperblockAsync(string devicePath, Guid uuid, string label, int blockSize, FormatOptions options, CancellationToken ct);
    public override Task SetAttributesAsync(string path, FileAttributes attributes, CancellationToken ct = default);
}
```
```csharp
public sealed class ExFatOperations : FilesystemOperationsBase
{
}
    protected override string FilesystemType;;
    protected override long MaxFileSize;;
    protected override int DefaultBlockSize;;
    protected override async Task WriteSuperblockAsync(string devicePath, Guid uuid, string label, int blockSize, FormatOptions options, CancellationToken ct);
}
```
```csharp
public sealed class F2fsOperations : FilesystemOperationsBase
{
}
    protected override string FilesystemType;;
    protected override long MaxFileSize;;
    protected override int DefaultBlockSize;;
    protected override async Task WriteSuperblockAsync(string devicePath, Guid uuid, string label, int blockSize, FormatOptions options, CancellationToken ct);
}
```
```csharp
public sealed class TmpfsOperations : FilesystemOperationsBase
{
}
    protected override string FilesystemType;;
    protected override long MaxFileSize;;
    protected override int DefaultBlockSize;;
    protected override Task WriteSuperblockAsync(string devicePath, Guid uuid, string label, int blockSize, FormatOptions options, CancellationToken ct);
    public override Task<MountedFilesystem> FormatAsync(string devicePath, FormatOptions? options = null, CancellationToken ct = default);
    public override Task<FilesystemEntry> CreateFileAsync(string path, byte[] content, CancellationToken ct = default);
    public override Task<byte[]> ReadFileAsync(string path, CancellationToken ct = default);
    public override Task WriteFileAsync(string path, byte[] content, CancellationToken ct = default);
    public override Task DeleteAsync(string path, bool recursive = false, CancellationToken ct = default);
    public override Task<IReadOnlyList<FilesystemEntry>> ListDirectoryAsync(string path, CancellationToken ct = default);
}
```
```csharp
public sealed class BufferedIoDriverStrategy : FilesystemStrategyBase
{
}
    public BufferedIoDriverStrategy() : this(DefaultReadAheadSize, DefaultWriteBufferSize);
    public BufferedIoDriverStrategy(int readAheadSize, int writeBufferSize);
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);;
    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);;
}
```
```csharp
public sealed class SpdkDriverStrategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);;
    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateFilesystem/Strategies/DriverStrategies.cs
```csharp
public sealed class PosixDriverStrategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);;
    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);;
}
```
```csharp
public sealed class DirectIoDriverStrategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);;
    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);;
}
```
```csharp
public sealed class IoUringDriverStrategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);;
    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);;
}
```
```csharp
public sealed class MmapDriverStrategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);;
    public override Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);;
}
```
```csharp
public sealed class WindowsNativeDriverStrategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);;
    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);;
}
```
```csharp
public sealed class AsyncIoDriverStrategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);;
    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);;
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateFilesystem/Strategies/WindowsWinFspFilesystemStrategy.cs
```csharp
public sealed class WindowsWinFspFilesystemStrategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);
    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateFilesystem/Strategies/FilesystemAdvancedFeatures.cs
```csharp
public sealed class ContentAddressableStorageLayer
{
}
    public ContentAddressableStorageLayer(IMessageBus? messageBus = null);
    public async Task<string> StoreBlockAsync(byte[] data, string sourceFile, CancellationToken ct = default);
    public byte[]? RetrieveBlock(string contentHash);
    public void RemoveReference(string sourceFile, string contentHash);
    public GarbageCollectionResult CollectGarbage();
    public IReadOnlyList<byte[]> RabinChunk(byte[] data, int targetChunkSize = 8192, int minChunkSize = 2048, int maxChunkSize = 32768);
    public DeduplicationStats GetStats();;
}
```
```csharp
public sealed class CasBlock
{
}
    public required string ContentHash { get; init; }
    public required byte[] Data { get; init; }
    public int Size { get; init; }
    public int ReferenceCount { get; set; }
    public DateTime StoredAt { get; init; }
}
```
```csharp
public sealed record CasReference
{
}
    public required string SourceFile { get; init; }
    public required string ContentHash { get; init; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record GarbageCollectionResult
{
}
    public int BlocksReclaimed { get; init; }
    public long BytesReclaimed { get; init; }
    public int RemainingBlocks { get; init; }
    public DateTime CollectedAt { get; init; }
}
```
```csharp
public sealed record DeduplicationStats
{
}
    public long TotalUniqueBlocks { get; init; }
    public long StoredBytes { get; init; }
    public long DeduplicatedBytes { get; init; }
    public double DeduplicationRatio { get; init; }
    public int TotalReferences { get; init; }
}
```
```csharp
public sealed class BlockLevelEncryptionManager
{
}
    public BlockLevelEncryptionManager(IMessageBus? messageBus = null);
    public EncryptionHeader CreateHeader(string volumeId, string cipher = "aes-xts-plain64", int keySize = 256);
    public int AddKeySlot(string volumeId, string passphrase);
    public async Task<byte[]> EncryptBlockAsync(string volumeId, byte[] data, long blockOffset, CancellationToken ct = default);
    public async Task<byte[]> DecryptBlockAsync(string volumeId, byte[] encryptedData, long blockOffset, CancellationToken ct = default);
    public PerFileEncryptionInfo CreatePerFileEncryption(string volumeId, string filePath);
    public EncryptionMetrics GetMetrics();;
}
```
```csharp
public sealed class EncryptionHeader
{
}
    public required string VolumeId { get; init; }
    public int Version { get; init; }
    public required string Cipher { get; init; }
    public int KeySizeBits { get; init; }
    public required byte[] MasterKeySalt { get; init; }
    public List<KeySlot> KeySlots { get; init; };
    public DateTime CreatedAt { get; init; }
    public bool UseHardwareAcceleration { get; init; }
}
```
```csharp
public sealed class KeySlot
{
}
    public int SlotIndex { get; init; }
    public bool IsActive { get; init; }
    public string Kdf { get; init; };
    public int KdfIterations { get; init; }
    public int KdfMemoryKb { get; init; }
    public byte[] Salt { get; init; };
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record PerFileEncryptionInfo
{
}
    public required string VolumeId { get; init; }
    public required string FilePath { get; init; }
    public required byte[] FileKeyWrapped { get; init; }
    public required string Algorithm { get; init; }
    public DateTime CreatedAt { get; init; }
}
```
```csharp
public sealed record EncryptionMetrics
{
}
    public long BlocksEncrypted { get; init; }
    public long BlocksDecrypted { get; init; }
    public bool AesNiAvailable { get; init; }
    public int ActiveVolumes { get; init; }
}
```
```csharp
public sealed class DistributedHaFilesystemManager
{
}
    public void RegisterNode(string nodeId, long capacityBytes, string region);
    public LeaderElectionResult ElectLeader(string candidateId);
    public void Heartbeat(string nodeId, long usedBytes);
    public IReadOnlyList<string> DetectUnhealthyNodes(TimeSpan threshold);
    public IReadOnlyList<RebalanceMovement> Rebalance();
    public string? CurrentLeader;;
    public ClusterHealth GetClusterHealth();;
}
```
```csharp
public sealed class FilesystemNode
{
}
    public required string NodeId { get; init; }
    public long CapacityBytes { get; init; }
    public long UsedBytes { get; set; }
    public required string Region { get; init; }
    public bool IsHealthy { get; set; }
    public DateTime LastHeartbeat { get; set; }
    public double Utilization;;
}
```
```csharp
public sealed record DataPlacement
{
}
    public required string DataId { get; init; }
    public required List<string> NodeIds { get; init; }
    public int ReplicationFactor { get; init; }
}
```
```csharp
public sealed record LeaderElectionResult
{
}
    public bool Success { get; init; }
    public string? LeaderId { get; init; }
    public long Term { get; init; }
    public int VotesReceived { get; init; }
    public int VotesNeeded { get; init; }
}
```
```csharp
public sealed record RebalanceMovement
{
}
    public required string SourceNode { get; init; }
    public required string TargetNode { get; init; }
    public long BytesToMove { get; init; }
}
```
```csharp
public sealed record ClusterHealth
{
}
    public int TotalNodes { get; init; }
    public int HealthyNodes { get; init; }
    public string? LeaderId { get; init; }
    public long CurrentTerm { get; init; }
    public long TotalCapacityBytes { get; init; }
    public long TotalUsedBytes { get; init; }
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateFilesystem/Strategies/SpecializedStrategies.cs
```csharp
public sealed class ContainerPackedStrategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);;
    public override Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);;
}
```
```csharp
public sealed class OverlayFsStrategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);;
    public override Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);;
}
```
```csharp
public sealed class BlockCacheStrategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);;
    public override Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);;
}
```
```csharp
public sealed class QuotaEnforcementStrategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);;
    public override Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);;
    public void SetQuota(string path, long limitBytes);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateFilesystem/Strategies/SuperblockDetectionStrategies.cs
```csharp
public sealed record SuperblockDetails
{
}
    public string? MagicSignature { get; init; }
    public string? Label { get; init; }
    public string? Uuid { get; init; }
    public string? Version { get; init; }
    public string[] FeatureFlags { get; init; };
    public int InodeSize { get; init; }
    public long TotalInodes { get; init; }
    public long JournalSize { get; init; }
    public string? CompressionAlgorithm { get; init; }
    public string? ChecksumAlgorithm { get; init; }
    public string? RaidProfile { get; init; }
    public DateTime? CreatedAt { get; init; }
    public DateTime? LastMountedAt { get; init; }
    public DateTime? LastWrittenAt { get; init; }
    public int MountCount { get; init; }
    public int MaxMountCount { get; init; }
    public string? State { get; init; }
    public int GroupCount { get; init; }
    public long BlocksPerGroup { get; init; }
    public long TotalBlocks { get; init; }
    public long FreeBlocks { get; init; }
    public long ReservedBlocks { get; init; }
    public string? EncryptionMode { get; init; }
    public int SnapshotCount { get; init; }
    public string? PoolName { get; init; }
    public string? DatasetName { get; init; }
}
```
```csharp
internal static class SuperblockReader
{
}
    public static byte[]? ReadBytes(string path, long offset, int length);
    public static string? ResolveDevicePath(string path);
    public static ushort ReadUInt16LE(byte[] data, int offset);;
    public static uint ReadUInt32LE(byte[] data, int offset);;
    public static ulong ReadUInt64LE(byte[] data, int offset);;
    public static uint ReadUInt32BE(byte[] data, int offset);;
    public static ulong ReadUInt64BE(byte[] data, int offset);;
    public static ushort ReadUInt16BE(byte[] data, int offset);;
    public static string ReadString(byte[] data, int offset, int maxLength);
    public static Guid ReadUuid(byte[] data, int offset);
    public static DateTime? FromUnixTime(uint timestamp);
}
```
```csharp
public sealed class Ext4SuperblockStrategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public SuperblockDetails? LastSuperblockDetails;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);
    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);
}
```
```csharp
public sealed class BtrfsSuperblockStrategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public SuperblockDetails? LastSuperblockDetails;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);
    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);
}
```
```csharp
public sealed class XfsSuperblockStrategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public SuperblockDetails? LastSuperblockDetails;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);
    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);
}
```
```csharp
public sealed class ZfsSuperblockStrategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public SuperblockDetails? LastSuperblockDetails;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);
    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);
}
```
```csharp
public sealed class ApfsSuperblockStrategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public SuperblockDetails? LastSuperblockDetails;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);
    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);
}
```
```csharp
public sealed class RefsSuperblockStrategy : FilesystemStrategyBase
{
}
    public override string StrategyId;;
    public override string DisplayName;;
    public override FilesystemStrategyCategory Category;;
    public override FilesystemStrategyCapabilities Capabilities;;
    public override string SemanticDescription;;
    public override string[] Tags;;
    public SuperblockDetails? LastSuperblockDetails;;
    public override Task<FilesystemMetadata?> DetectAsync(string path, CancellationToken ct = default);
    public override async Task<byte[]> ReadBlockAsync(string path, long offset, int length, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task WriteBlockAsync(string path, long offset, byte[] data, BlockIoOptions? options = null, CancellationToken ct = default);
    public override async Task<FilesystemMetadata> GetMetadataAsync(string path, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateFilesystem/DeviceManagement/DevicePoolManager.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 90: Device pool lifecycle management (BMDV-05/BMDV-06/BMDV-07)")]
public sealed class DevicePoolManager : IAsyncDisposable
{
}
    public DevicePoolManager(PoolMetadataCodec codec);
    public async Task<DevicePoolDescriptor> CreatePoolAsync(string poolName, StorageTier? explicitTier, LocalityTag? locality, IReadOnlyList<IPhysicalBlockDevice> devices, CancellationToken ct = default);
    public Task<DevicePoolDescriptor?> GetPoolAsync(Guid poolId);
    public Task<DevicePoolDescriptor?> GetPoolByNameAsync(string poolName);
    public Task<IReadOnlyList<DevicePoolDescriptor>> GetAllPoolsAsync();
    public async Task AddDeviceToPoolAsync(Guid poolId, IPhysicalBlockDevice device, CancellationToken ct = default);
    public async Task RemoveDeviceFromPoolAsync(Guid poolId, string deviceId, CancellationToken ct = default);
    public async Task DeletePoolAsync(Guid poolId, CancellationToken ct = default);
    public async Task<IReadOnlyList<DevicePoolDescriptor>> ScanForPoolsAsync(IReadOnlyList<IPhysicalBlockDevice> devices, CancellationToken ct = default);
    public async Task UpdatePoolLocalityAsync(Guid poolId, LocalityTag locality, CancellationToken ct = default);
    public Task<IReadOnlyList<DevicePoolDescriptor>> GetPoolsByTierAsync(StorageTier tier);
    public Task<IReadOnlyList<DevicePoolDescriptor>> GetPoolsByLocalityAsync(string? rack = null, string? datacenter = null, string? region = null);
    public ValueTask DisposeAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateFilesystem/DeviceManagement/SmartMonitor.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 90: SMART health monitoring (BMDV-03)")]
public sealed class SmartMonitor
{
}
    public SmartMonitor(ILogger? logger = null);
    public async Task<PhysicalDeviceHealth> ReadSmartAttributesAsync(string devicePath, BusType busType, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateFilesystem/DeviceManagement/DeviceTopologyMapper.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 90: Device topology mapping (BMDV-08)")]
public sealed class DeviceTopologyMapper
{
}
    public DeviceTopologyTree BuildTopology(IReadOnlyList<PhysicalDeviceInfo> devices);
    public IReadOnlyList<TopologyNode> GetDevicesByController(DeviceTopologyTree tree, string controllerNodeId);
    public TopologyNode? FindDeviceNode(DeviceTopologyTree tree, string deviceId);
    public IReadOnlyList<string> GetSiblingDevices(DeviceTopologyTree tree, string deviceId);
    public IReadOnlyList<NumaAffinityInfo> GetNumaAffinity(DeviceTopologyTree tree);
    internal static IReadOnlyList<int> ParseCpuList(string cpuList);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateFilesystem/DeviceManagement/BaremetalBootstrap.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 90: Bare-metal bootstrap (BMDV-12)")]
public sealed class BaremetalBootstrap
{
}
    public BaremetalBootstrap(DeviceDiscoveryService discoveryService, DevicePoolManager poolManager, DeviceJournal journal, PhysicalDeviceManager deviceManager, ILogger? logger = null);
    public async Task<BootstrapResult> BootstrapFromRawDevicesAsync(CancellationToken ct = default);
    public async Task<DevicePoolDescriptor> InitializeNewSystemAsync(string poolName, IReadOnlyList<IPhysicalBlockDevice> devices, StorageTier? tier = null, LocalityTag? locality = null, CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateFilesystem/DeviceManagement/PoolMetadataCodec.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 90: Pool metadata binary codec (BMDV-07)")]
public sealed class PoolMetadataCodec
{
}
    public byte[] SerializePoolMetadata(DevicePoolDescriptor pool);
    public DevicePoolDescriptor? DeserializePoolMetadata(ReadOnlySpan<byte> data);
    public bool ValidatePoolMetadata(ReadOnlySpan<byte> data);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateFilesystem/DeviceManagement/NumaAwareIoScheduler.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 90: NUMA-aware I/O scheduling (BMDV-09)")]
public sealed class NumaAwareIoScheduler
{
}
    public NumaAwareIoScheduler(DeviceTopologyTree topology, IReadOnlyList<NumaAffinityInfo> numaInfo);
    public IoSchedulingResult GetSchedulingHint(string deviceId);
    public async Task<T> ScheduleIoAsync<T>(string deviceId, Func<CancellationToken, Task<T>> ioOperation, CancellationToken ct = default);
    public IReadOnlyDictionary<string, IoSchedulingResult> GetAllHints();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateFilesystem/DeviceManagement/PhysicalDeviceManager.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 90: Device manager config (BMDV-03/BMDV-04)")]
public sealed record DeviceManagerConfig(TimeSpan? HealthPollInterval = null, TimeSpan? DiscoveryInterval = null, bool AutoDiscoveryEnabled = true, FailurePredictionConfig? PredictionConfig = null)
{
}
    public TimeSpan EffectiveHealthPollInterval;;
    public TimeSpan EffectiveDiscoveryInterval;;
}
```
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 90: Device lifecycle and health orchestration (BMDV-03/BMDV-04)")]
public sealed class PhysicalDeviceManager : IAsyncDisposable
{
}
    public Action<string, PhysicalDeviceHealth>? OnHealthUpdate { get; set; }
    public Action<string, FailurePrediction>? OnFailurePrediction { get; set; }
    public Action<string, DeviceStatus, DeviceStatus>? OnStatusChange { get; set; }
    public Action<PhysicalDeviceInfo>? OnDeviceDiscovered { get; set; }
    public Action<string>? OnDeviceRemoved { get; set; }
    public PhysicalDeviceManager(DeviceDiscoveryService discoveryService, SmartMonitor smartMonitor, FailurePredictionEngine predictionEngine, DeviceManagerConfig? config = null, ILogger? logger = null);
    public Task StartAsync(CancellationToken ct = default);
    public async Task StopAsync();
    public Task<IReadOnlyList<ManagedDevice>> GetDevicesAsync();
    public Task<ManagedDevice?> GetDeviceAsync(string deviceId);
    public async Task ForceHealthCheckAsync(string deviceId, CancellationToken ct = default);
    public async Task ForceDiscoveryAsync(CancellationToken ct = default);
    public Task RegisterDeviceAsync(PhysicalDeviceInfo info);
    public Task UnregisterDeviceAsync(string deviceId);
    public async ValueTask DisposeAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateFilesystem/DeviceManagement/FailurePredictionEngine.cs
```csharp
internal sealed record DeviceEwmaState
{
}
    public double EwmaTemperature { get; set; }
    public double EwmaErrorRate { get; set; }
    public double EwmaWearRate { get; set; }
    public DateTime LastUpdate { get; set; }
    public int SampleCount { get; set; }
    public long LastUncorrectableErrors { get; set; }
    public double LastWearLevelPercent { get; set; }
}
```
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 90: EWMA failure prediction (BMDV-04)")]
public sealed class FailurePredictionEngine
{
}
    public FailurePredictionEngine(FailurePredictionConfig? config = null);
    public FailurePrediction UpdateAndPredict(string deviceId, PhysicalDeviceHealth health);
    public void Reset(string deviceId);
    public IReadOnlyDictionary<string, FailurePrediction> GetAllPredictions();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateFilesystem/DeviceManagement/HotSwapManager.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 90: Hot-swap device management (BMDV-11)")]
public sealed class HotSwapManager : IAsyncDisposable
{
}
    public Action<PhysicalDeviceInfo>? OnNewDeviceAvailable { get; set; }
    public Action<Guid, string>? OnRebuildRequired { get; set; }
    public Action<string, DeviceStatus>? OnDeviceDegraded { get; set; }
    public Action<string, RebuildState>? OnRebuildProgress { get; set; }
    public HotSwapManager(PhysicalDeviceManager deviceManager, DevicePoolManager poolManager, DeviceJournal journal, HotSwapConfig? config = null, ILogger? logger = null);
    public Task StartAsync(CancellationToken ct = default);
    public Task StopAsync();
    public Task<IReadOnlyList<RebuildState>> GetActiveRebuildsAsync();
    public async ValueTask DisposeAsync();
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateFilesystem/DeviceManagement/DeviceJournal.cs
```csharp
[SdkCompatibility("6.0.0", Notes = "Phase 90: Device lifecycle journal (BMDV-10)")]
public sealed class DeviceJournal : IAsyncDisposable
{
}
    public async Task<long> WriteIntentAsync(JournalEntryType type, Guid poolId, string? deviceId, byte[]? payload, IPhysicalBlockDevice journalDevice, CancellationToken ct = default);
    public async Task CommitAsync(long sequenceNumber, IPhysicalBlockDevice journalDevice, CancellationToken ct = default);
    public async Task RollbackAsync(long sequenceNumber, IPhysicalBlockDevice journalDevice, CancellationToken ct = default);
    public async Task<IReadOnlyList<JournalEntry>> ReadJournalAsync(IPhysicalBlockDevice journalDevice, CancellationToken ct = default);
    public async Task<IReadOnlyList<JournalEntry>> GetUncommittedIntentsAsync(IPhysicalBlockDevice journalDevice, CancellationToken ct = default);
    public ValueTask DisposeAsync();
}
```
