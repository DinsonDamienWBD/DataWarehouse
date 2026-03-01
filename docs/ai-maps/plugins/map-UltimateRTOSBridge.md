# Plugin: UltimateRTOSBridge
> **CORE DEPENDENCY:** All plugins rely on the SDK. Resolve base classes in `../map-core.md`.
> **MESSAGE BUS CONTRACTS:** Look for `IEvent`, `IMessage`, or publish/subscribe signatures below.


## Project: DataWarehouse.Plugins.UltimateRTOSBridge

### File: Plugins/DataWarehouse.Plugins.UltimateRTOSBridge/IRtosStrategy.cs
```csharp
public interface IRtosStrategy
{
}
    string StrategyId { get; }
    string StrategyName { get; }
    RtosCapabilities Capabilities { get; }
    Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken ct = default);;
    Task<RtosOperationResult> ExecuteAsync(RtosOperationContext context, CancellationToken ct = default);;
}
```
```csharp
public record RtosCapabilities
{
}
    public bool SupportsDeterministicIo { get; init; }
    public bool SupportsRealTimeScheduling { get; init; }
    public bool SupportsSafetyCertifications { get; init; }
    public string[] SupportedStandards { get; init; };
    public int MaxGuaranteedLatencyMicroseconds { get; init; }
    public string[] SupportedPlatforms { get; init; };
    public bool SupportsFaultTolerance { get; init; }
    public bool SupportsWatchdog { get; init; }
    public Dictionary<string, object> Metadata { get; init; };
}
```
```csharp
public record RtosOperationContext
{
}
    public required RtosOperationType OperationType { get; init; }
    public required string ResourcePath { get; init; }
    public byte[]? Data { get; init; }
    public int Priority { get; init; };
    public long DeadlineMicroseconds { get; init; }
    public SafetyIntegrityLevel RequiredSil { get; init; };
    public Dictionary<string, object> Parameters { get; init; };
}
```
```csharp
public record RtosOperationResult
{
}
    public bool Success { get; init; }
    public byte[]? Data { get; init; }
    public long ActualLatencyMicroseconds { get; init; }
    public bool DeadlineMet { get; init; }
    public string? ErrorMessage { get; init; }
    public string? SafetyCertification { get; init; }
    public RtosAuditEntry? AuditEntry { get; init; }
    public Dictionary<string, object> Metadata { get; init; };
}
```
```csharp
public record RtosAuditEntry
{
}
    public required string OperationId { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public RtosOperationType OperationType { get; init; }
    public required string ResourcePath { get; init; }
    public SafetyIntegrityLevel AppliedSil { get; init; }
    public bool Success { get; init; }
    public long LatencyMicroseconds { get; init; }
    public string? DataHash { get; init; }
    public string? CertificationReference { get; init; }
}
```
```csharp
public abstract class RtosStrategyBase : StrategyBase, IRtosStrategy
{
}
    public abstract override string StrategyId { get; }
    public abstract string StrategyName { get; }
    public override string Name;;
    public abstract RtosCapabilities Capabilities { get; }
    protected Dictionary<string, object> Configuration { get; private set; };
    public virtual Task InitializeAsync(Dictionary<string, object> configuration, CancellationToken ct = default);
    public abstract Task<RtosOperationResult> ExecuteAsync(RtosOperationContext context, CancellationToken ct = default);;
    protected RtosAuditEntry CreateAuditEntry(RtosOperationContext context, bool success, long latencyMicroseconds, string? dataHash = null);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateRTOSBridge/UltimateRTOSBridgePlugin.cs
```csharp
public sealed class UltimateRTOSBridgePlugin : StreamingPluginBase, IDisposable
{
#endregion
}
    public override string Id;;
    public override string Name;;
    public override string Version;;
    public override PluginCategory Category;;
    public IReadOnlyCollection<IRtosStrategy> GetStrategies();;
    public IRtosStrategy? GetStrategy(string strategyId);
    public void RegisterStrategy(IRtosStrategy strategy);
    public void SetDefaultStrategy(string strategyId);
    public async Task<RtosOperationResult> ExecuteAsync(RtosOperationContext context, string? strategyId = null, CancellationToken cancellationToken = default);
    public override async Task StartAsync(CancellationToken ct);
    protected override async Task OnStartWithIntelligenceAsync(CancellationToken ct);
    protected override Task OnStartCoreAsync(CancellationToken ct);
    public override Task StopAsync();
    protected override IReadOnlyList<RegisteredCapability> DeclaredCapabilities
{
    get
    {
        var capabilities = new List<RegisteredCapability>
        {
            new()
            {
                CapabilityId = "rtos.ultimate",
                PluginId = Id,
                PluginName = Name,
                PluginVersion = Version,
                DisplayName = "Ultimate RTOS Bridge",
                Description = "Safety-critical RTOS integration with deterministic I/O and safety certifications",
                Category = SDK.Contracts.CapabilityCategory.Infrastructure,
                Tags = ["rtos", "real-time", "safety-critical", "embedded", "deterministic"]
            }
        };
        foreach (var(strategyId, strategy)in _strategies)
        {
            var tags = new List<string>
            {
                "rtos",
                strategyId
            };
            tags.AddRange(strategy.Capabilities.SupportedStandards.Take(3));
            capabilities.Add(new RegisteredCapability { CapabilityId = $"rtos.{strategyId}", PluginId = Id, PluginName = Name, PluginVersion = Version, DisplayName = strategy.StrategyName, Description = $"RTOS strategy: {strategy.StrategyName}", Category = SDK.Contracts.CapabilityCategory.Infrastructure, Tags = tags.ToArray() });
        }

        return capabilities.AsReadOnly();
    }
}
    protected override IReadOnlyList<KnowledgeObject> GetStaticKnowledge();
    protected override Dictionary<string, object> GetMetadata();
    public override async Task OnMessageAsync(PluginMessage message);
    public override Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request);
    protected override void Dispose(bool disposing);
    public override Task PublishAsync(string topic, Stream data, CancellationToken ct = default);;
    public override async IAsyncEnumerable<Dictionary<string, object>> SubscribeAsync(string topic, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default);
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateRTOSBridge/Strategies/RtosProtocolAdapters.cs
```csharp
public sealed class VxWorksProtocolAdapter : RtosStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override RtosCapabilities Capabilities;;
    public override async Task<RtosOperationResult> ExecuteAsync(RtosOperationContext context, CancellationToken ct = default);
}
```
```csharp
private class VxWorksQueue
{
}
    public const int MaxDepth = 4096;
    public ConcurrentQueue<byte[]> Messages { get; };
    public SemaphoreSlim MessageAvailable { get; };
}
```
```csharp
private class VxWorksWatchdog
{
}
    public required string ResourcePath { get; init; }
    public bool IsExpired { get; set; }
    public DateTimeOffset? LastKickTime { get; set; }
    public long KickCount;;
    public void IncrementKickCount();;
}
```
```csharp
public sealed class QnxProtocolAdapter : RtosStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override RtosCapabilities Capabilities;;
    public override async Task<RtosOperationResult> ExecuteAsync(RtosOperationContext context, CancellationToken ct = default);
}
```
```csharp
private class QnxChannel
{
}
    public ConcurrentQueue<QnxMessage> PendingMessages { get; };
    public SemaphoreSlim MessageReady { get; };
    public int PulseCount { get; set; }
}
```
```csharp
private class QnxMessage
{
}
    public byte[] Data { get; init; };
    public byte[]? ReplyData { get; set; }
    public SemaphoreSlim? ReplyReady { get; init; }
}
```
```csharp
public sealed class FreeRtosProtocolAdapter : RtosStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override RtosCapabilities Capabilities;;
    public override async Task<RtosOperationResult> ExecuteAsync(RtosOperationContext context, CancellationToken ct = default);
}
```
```csharp
private class FreeRtosQueue
{
}
    public ConcurrentQueue<byte[]> Items { get; };
    public SemaphoreSlim ItemAvailable { get; };
}
```
```csharp
public sealed class ZephyrProtocolAdapter : RtosStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override RtosCapabilities Capabilities;;
    public override async Task<RtosOperationResult> ExecuteAsync(RtosOperationContext context, CancellationToken ct = default);
}
```
```csharp
private class ZephyrMsgQueue
{
}
    public ConcurrentQueue<byte[]> Messages { get; };
    public SemaphoreSlim Ready { get; };
}
```
```csharp
private class ZephyrFifo
{
}
    public ConcurrentQueue<byte[]> Items { get; };
    public SemaphoreSlim Ready { get; };
}
```
```csharp
public sealed class IntegrityProtocolAdapter : RtosStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override RtosCapabilities Capabilities;;
    public override async Task<RtosOperationResult> ExecuteAsync(RtosOperationContext context, CancellationToken ct = default);
}
```
```csharp
private class IntegrityConnection
{
}
    public ConcurrentQueue<byte[]> Buffer { get; };
    public SemaphoreSlim DataReady { get; };
}
```
```csharp
private class IntegrityPartition
{
}
    public string PartitionId { get; init; };
    public int CpuBudgetPercent { get; set; };
    public bool IsAccessible { get; set; };
}
```
```csharp
public sealed class LynxOsProtocolAdapter : RtosStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override RtosCapabilities Capabilities;;
    public override async Task<RtosOperationResult> ExecuteAsync(RtosOperationContext context, CancellationToken ct = default);
}
```
```csharp
private class LynxQueue
{
}
    public ConcurrentQueue<byte[]> Messages { get; };
    public SemaphoreSlim Ready { get; };
}
```

### File: Plugins/DataWarehouse.Plugins.UltimateRTOSBridge/Strategies/DeterministicIoStrategies.cs
```csharp
public sealed class DeterministicIoStrategy : RtosStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override RtosCapabilities Capabilities;;
    public override async Task<RtosOperationResult> ExecuteAsync(RtosOperationContext context, CancellationToken ct = default);
}
```
```csharp
private class IoChannel
{
}
    public ConcurrentQueue<byte[]> Buffer { get; };
    public SemaphoreSlim DataReady { get; };
    public long ReadCount;;
    public long WriteCount;;
    public long DeadlineMissCount;;
    public void IncrementReadCount();;
    public void IncrementWriteCount();;
    public void IncrementDeadlineMissCount();;
    public long MinLatencyMicroseconds { get; set; };
    public long MaxLatencyMicroseconds { get; set; }
    public List<long> LatencySamples { get; };
    public object LatencyLock { get; };
}
```
```csharp
private record ScheduledOperation
{
}
    public required RtosOperationContext Context { get; init; }
    public DateTimeOffset ScheduledAt { get; init; }
}
```
```csharp
public sealed class SafetyCertificationStrategy : RtosStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override RtosCapabilities Capabilities;;
    public override async Task<RtosOperationResult> ExecuteAsync(RtosOperationContext context, CancellationToken ct = default);
}
```
```csharp
private class SafetyContext
{
}
    public required string ResourcePath { get; init; }
    public SafetyIntegrityLevel CurrentSil { get; set; };
    public double DiagnosticCoveragePercent { get; set; };
    public double SafeFailureFractionPercent { get; set; };
    public DateTimeOffset? LastVerificationTime { get; set; }
}
```
```csharp
private record SafetyViolation
{
}
    public DateTimeOffset Timestamp { get; init; }
    public required string ResourcePath { get; init; }
    public SafetyIntegrityLevel RequestedSil { get; init; }
    public required string Message { get; init; }
}
```
```csharp
public sealed class WatchdogIntegrationStrategy : RtosStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override RtosCapabilities Capabilities;;
    public override async Task<RtosOperationResult> ExecuteAsync(RtosOperationContext context, CancellationToken ct = default);
}
```
```csharp
private class WatchdogContext
{
}
    public required string ResourcePath { get; init; }
    public bool IsEnabled { get; set; }
    public int TimeoutMs { get; set; };
    public bool IsExpired { get; set; }
    public long KickCount;;
    public long ExpiryCount;;
    public void IncrementKickCount();;
    public void IncrementExpiryCount();;
    public DateTimeOffset? LastKickTime { get; set; }
    public DateTimeOffset? LastRecoveryTime { get; set; }
    public string RecoveryAction { get; set; };
}
```
```csharp
public sealed class PriorityInversionPreventionStrategy : RtosStrategyBase
{
}
    public override string StrategyId;;
    public override string StrategyName;;
    public override RtosCapabilities Capabilities;;
    public override async Task<RtosOperationResult> ExecuteAsync(RtosOperationContext context, CancellationToken ct = default);
}
```
```csharp
private class ResourceLock
{
}
    public required string ResourcePath { get; init; }
    public SemaphoreSlim Semaphore { get; };
    public int? HolderTaskId { get; set; }
    public int? InheritedPriority { get; set; }
    public long AcquisitionCount { get; set; }
}
```
```csharp
private class TaskInfo
{
}
    public int TaskId { get; init; }
    public int BasePriority { get; init; }
    public int EffectivePriority { get; set; }
}
```
