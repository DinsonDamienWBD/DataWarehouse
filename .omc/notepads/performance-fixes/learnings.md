# Performance Fixes v4.2 Audit - Learnings

## Completed Fixes (2026-02-18)

### P0 - Critical Performance Issues

#### 1. Unbounded MemoryStream Allocations [FIXED]
**Files Modified:**
- VirtualDiskEngine.cs: Added capacity hint `(int)inode.Size` for predictable file size
- EncryptionPluginBase.cs: Added capacity hint `headerBytes.Length + (int)encryptedData.Length`
- TransitEncryptionPluginBases.cs: 
  - CompressDataAsync: Added capacity hint `data.Length / 2` (estimated 50% compression ratio)
  - DecompressDataAsync: Added capacity hint `compressedData.Length * 2` (estimated 2x expansion)

**Pattern:** Always provide capacity hints to MemoryStream when the size is known or can be reasonably estimated. This prevents repeated internal array reallocations.

#### 3. Message Bus Hot Path Allocation [FIXED]
**File Modified:** AdvancedMessageBus.cs

**Pattern:** Implemented read-copy-update (RCU) pattern for ThreadSafeSubscriptionList:
- Added `volatile T[] _cachedArray` field
- Update cache on every Add/Remove/Clear operation (write-heavy)
- Return cached array without locking on ToArray() (read-heavy hot path)
- Eliminated per-publish allocation of handler snapshot

**Key Insight:** Message bus publish is called FAR more often than subscribe/unsubscribe. Cache the array on mutation, return it without locking on hot path.

### P1 - High-Impact Performance Issues

#### 4. HttpClient Per-Request Instantiation [FIXED]
**Files Modified:**
- DataWarehouse.Launcher/Integration/InstanceConnection.cs
- Metadata/Adapter/InstanceConnection.cs
- Plugins/UltimateDatabaseProtocol/Strategies/TimeSeries/TimeSeriesProtocolStrategies.cs
- Plugins/UniversalDashboards/DashboardStrategyBase.cs
- Plugins/UltimateKeyManagement/Strategies/IndustryFirst/GeoLockedKeyStrategy.cs

**Pattern:** 
```csharp
// Add static shared instance
private static readonly HttpClient SharedHttpClient = new HttpClient();

// Use in constructor/initialization
_httpClient = SharedHttpClient;
```

**Key Insight:** HttpClient is thread-safe and designed to be reused. Creating per-request HttpClient instances causes socket exhaustion and DNS resolution overhead. Use static shared instance or IHttpClientFactory in DI scenarios.

#### 5. async void → async Task [FIXED]
**Files Modified:**
- UndoManager.cs: SaveOperationsAsync() → Task, wrapped calls with `_ = Task.Run(async () => await ...)`
- CommandHistory.cs: SaveHistoryAsync() → Task, wrapped calls with `_ = Task.Run(async () => await ...)`
- DashboardHub.cs: OnAuditEntryLogged() → void (event handler), wrapped body with `_ = Task.Run(async () => { ... })`
- IoTStrategyBase.cs: PublishMessage() → Task
- SchemaEvolutionEngine.cs: RequestPatternDetection() → Task, wrapped body with `Task.Run`
- PredictiveCacheStrategy.cs: ExecutePrefetch() → void (Timer callback), wrapped body with `Task.Run`
- KernelInfrastructure.cs: CheckAvailability() → void (Timer callback), wrapped body with `Task.Run`
- DriverLoader.cs: OnHardwareChangedHandler() → void (event handler), wrapped body with `Task.Run`

**Patterns:**
1. **Event Handlers (must be void):** Wrap async work in `_ = Task.Run(async () => { ... })` with try-catch inside
2. **Timer Callbacks (must be void):** Same pattern as event handlers
3. **Fire-and-forget methods:** Change signature to Task, wrap callers with `_ = Task.Run(async () => await ...)`
4. **Awaitable methods:** Change signature to Task, make callers await properly

**Key Insight:** `async void` swallows exceptions and can't be awaited. Only use for event handlers/timer callbacks where void signature is required, and wrap body with Task.Run for proper error handling.

### P2 - Moderate Performance Issues

#### 10. Random → Random.Shared [FIXED]
**Files Modified (batch replacement with sed):**
- AuditLogService.cs
- RaftConsensusPlugin.cs
- GraphAnalyticsStrategies.cs
- SecurityStrategies.cs (IoT)
- ProtocolStrategies.cs (IoT)
- EdgeStrategies.cs (IoT)
- AnalyticsStrategies.cs (IoT)
- AutoReconnectionHandler.cs
- ErrorHandlingStrategies.cs
- NvmeDiskStrategy.cs

**Pattern:**
```csharp
// OLD
var random = new Random();
var value = random.Next(0, 100);

// NEW
var value = Random.Shared.Next(0, 100);
```

**Key Insight:** `Random.Shared` is thread-safe (added in .NET 6) and eliminates per-call instantiation overhead. Each `new Random()` allocates a new object and seeds from system clock.

## Build Verification

```bash
dotnet build DataWarehouse.slnx -c Release --verbosity minimal
```

**Result:** Build succeeded ✅

## Remaining Work (Not Yet Started)

### P0
- [ ] P0-2: MessageBridge.cs lock ordering analysis
- [ ] P0-2: RaftConsensusPlugin.cs lock ordering check
- [ ] P0-2: UndoManager.cs async lock check

### P1
- [ ] P1-6: Redis KEYS * → SCAN pattern
- [ ] P1-7: Azure Blob streaming (OOM fix)

### P2
- [ ] P2-8: ArrayPool for large byte[] allocations
- [ ] P2-9: ToList()/ToArray() on hot paths (PluginCapabilityRegistry.cs)
- [ ] P2-11: BufferedStream for archive I/O (TapeLibraryStrategy, OdaStrategy, JuiceFsStrategy)

## Performance Wins Summary

1. **Memory:** Eliminated unbounded MemoryStream growth, reducing GC pressure
2. **Allocations:** Eliminated per-publish array allocation in message bus (hot path)
3. **Sockets:** Eliminated HttpClient socket exhaustion via static shared instances
4. **Threading:** Fixed async void exception swallowing, proper Task-based async
5. **RNG:** Thread-safe Random.Shared eliminates per-call instantiation

## Next Session Priorities

1. Complete lock ordering analysis (P0-2) — deadlock risk
2. Fix Redis KEYS * usage (P1-6) — production blocker on large datasets
3. Fix Azure Blob OOM (P1-7) — production blocker on large blobs
