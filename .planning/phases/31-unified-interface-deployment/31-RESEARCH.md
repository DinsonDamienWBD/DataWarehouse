# Phase 31: Unified Interface & Deployment Modes - Research

**Researched:** 2026-02-14
**Domain:** CLI/GUI dynamic capability reflection, NLP query routing, deployment modes (client/live/install)
**Confidence:** HIGH

## Summary

Phase 31 wires together existing infrastructure that is already architected but not yet connected. The codebase has all the necessary components -- DataWarehouse.Shared (CommandExecutor, InstanceManager, MessageBridge, CapabilityManager), UltimateInterfacePlugin (68+ protocol strategies, IntelligenceAware NLP), DataWarehouseHost (Install/Connect/Embedded modes), and the CLI/GUI thin wrappers. The gap is that these components are not dynamically connected: CommandExecutor uses hardcoded command lists, MessageBridge.SendInProcessAsync returns mock data, ServerCommands use Task.Delay placeholders, deployment operations (CopyFiles, RegisterService, CreateAdmin) are stubs, and CLI/GUI have no mechanism for dynamic command reflection based on live plugin state.

This phase spans three parallel work streams: (A) Dynamic Capability Reflection -- subscribe to capability events and auto-generate commands, (B) Intelligence-Backed Queries -- route NLP through UltimateInterface with graceful degradation, and (C) Deployment Modes -- complete the stub implementations and add `dw live`, `dw install --from-usb`, and platform-specific service management.

**Primary recommendation:** Work in the three streams defined by the ROADMAP. Start with Stream A (DynamicCommandRegistry) since Streams B and C both depend on having real message routing. Stream C can proceed in parallel since it primarily involves DataWarehouseHost implementation work.

## Standard Stack

### Core (Already In Use)
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| System.CommandLine | 2.0.3 | CLI command parsing | Already migrated in Phase 22; uses SetAction pattern |
| Spectre.Console | (existing) | CLI rendering | Already used throughout CLI for tables, panels, progress |
| System.Text.Json | (built-in) | Serialization | Project decision: sole JSON serializer |
| .NET MAUI | 10.0 | GUI framework | Already exists as DataWarehouse.GUI |
| Serilog | (existing) | Launcher logging | Already configured in Launcher |

### Supporting (Already Available)
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| System.Diagnostics.Process | (built-in) | Platform service management | `sc create`, `systemctl`, `launchctl` |
| System.IO.DriveInfo | (built-in) | USB/removable media detection | Detecting portable media |
| System.Runtime.InteropServices | (built-in) | Platform detection | OperatingSystem.IsWindows/Linux/macOS |

### No New Dependencies Needed
All Phase 31 work uses existing project references and .NET BCL. No new NuGet packages required.

## Architecture Patterns

### Recommended Approach: Wire Existing Components

Phase 31 does NOT create new architectural abstractions. It connects existing components that are already in the right places.

### Key Existing Architecture

```
DataWarehouse.CLI/              # Thin wrapper, delegates to Shared
DataWarehouse.GUI/              # Thin wrapper (MAUI), delegates to Shared
DataWarehouse.Shared/           # Business logic layer
  Commands/
    CommandExecutor.cs          # CURRENTLY: hardcoded RegisterBuiltInCommands()
    CommandRegistry.cs          # CURRENTLY: static list of CommandDefinitions
    ICommand.cs                 # Command interface + CommandContext
    ServerCommands.cs           # CURRENTLY: Task.Delay stubs for start/stop
  InstanceManager.cs            # CURRENTLY: mock response in ExecuteAsync when disconnected
  MessageBridge.cs              # CURRENTLY: mock response in SendInProcessAsync
  CapabilityManager.cs          # HAS CapabilitiesChanged event, not subscribed to live events
  Services/
    NaturalLanguageProcessor.cs # Pattern-based + AI fallback NLP
DataWarehouse.Launcher/
  Integration/
    DataWarehouseHost.cs        # Install/Connect/Embedded/Service modes
    InstanceConnection.cs       # Local/Remote/Cluster connections
    ServiceHost.cs              # Service-only runtime
    EmbeddedAdapter.cs          # In-memory adapter
Plugins/
  UltimateInterface/            # 68+ strategies, IntelligenceAware, NLP
Metadata/Adapter/
  DataWarehouseHost.cs          # DUPLICATE of Launcher's DataWarehouseHost
```

### Pattern 1: DynamicCommandRegistry (Stream A)

**What:** New class in DataWarehouse.Shared that subscribes to capability change events via MessageBridge and maintains a live registry of available commands based on loaded plugins.

**When to use:** Replaces the static CommandRegistry and the hardcoded RegisterBuiltInCommands() in CommandExecutor.

**Key insight:** The SDK's PluginBase already has capability registration (`OnCapabilityRegistered`, `OnCapabilityUnregistered`, `OnAvailabilityChanged` events on PluginCapabilityRegistry). The kernel publishes `capability.changed`, `plugin.loaded`, `plugin.unloaded` events on the message bus. Shared just needs to subscribe to these events and translate them into command availability.

**Design:**
```csharp
// In DataWarehouse.Shared
public class DynamicCommandRegistry
{
    private readonly ConcurrentDictionary<string, DynamicCommandDefinition> _commands = new();
    private readonly MessageBridge _messageBridge;

    public event EventHandler<CommandsChangedEventArgs>? CommandsChanged;

    // Subscribe to message bus events
    public async Task StartListeningAsync(CancellationToken ct)
    {
        // Subscribe to capability.changed, plugin.loaded, plugin.unloaded
        // via MessageBridge
    }

    // Called when capability events arrive
    private void OnCapabilityChanged(CapabilityChangedEvent e)
    {
        // Add/remove commands based on capability
        // Fire CommandsChanged event
    }

    // CommandExecutor reads from this instead of static list
    public IEnumerable<DynamicCommandDefinition> GetAvailableCommands() { }
}
```

### Pattern 2: NLP Query Routing (Stream B)

**What:** Connect the existing NaturalLanguageProcessor (Shared) to UltimateInterfacePlugin's AI-backed intent parsing via the message bus through the kernel.

**Flow:**
```
User query → CLI/GUI → NaturalLanguageProcessor
  → Pattern match first (fast, free)
  → If low confidence AND connected to instance:
    → MessageBridge → Kernel → UltimateInterfacePlugin.HandleParseIntentAsync
    → UltimateInterfacePlugin uses IntelligenceAwarePluginBase AI socket
    → Queries Knowledge Bank for capability answers
    → Returns structured response
  → If Intelligence unavailable:
    → Falls back to keyword-based CapabilityRegistry lookup
```

**Key insight:** UltimateInterfacePlugin already has `HandleParseIntentAsync` (line 471-502), `HandleConversationAsync`, and `HandleDetectLanguageAsync` with full fallback behavior. NaturalLanguageProcessor already has `ProcessWithAIFallbackAsync`. The missing piece is connecting them through the message bus rather than having NLP use a local AI provider directly.

### Pattern 3: Real Deployment Operations (Stream C)

**What:** Replace stubs in DataWarehouseHost with real implementations.

**Key stubs to replace:**

| Method | Current State | Required Implementation |
|--------|---------------|------------------------|
| `CopyFilesAsync` | Returns Task.CompletedTask | Copy binaries, DLLs, configs from source to install path |
| `RegisterServiceAsync` | Logs only | `sc create` (Win), systemd unit (Linux), launchd plist (macOS) |
| `CreateAdminUserAsync` | Logs only | Create user record in security store using AccessControl plugin |
| `InitializePluginsAsync` | Creates dirs only | Copy plugin DLLs from source to plugins/ directory |
| `ServerStartCommand.ExecuteAsync` | Task.Delay(100) | Real kernel startup via DataWarehouseHost/KernelBuilder |
| `ServerStopCommand.ExecuteAsync` | Task.Delay(500) | Real kernel shutdown via GracefulShutdown |
| `MessageBridge.SendInProcessAsync` | Returns mock response | Real in-memory message queue dispatch |
| `InstanceManager.ExecuteAsync` | Mock when disconnected | Throw or return clear error |

### Anti-Patterns to Avoid

- **Duplicating DataWarehouseHost:** There are TWO copies (Launcher and Metadata/Adapter). Both have identical stubs. Fix the Launcher version; the Metadata/Adapter version is a reference example that should track the Launcher version or be removed.
- **Creating new message bus implementations:** The SDK already has IMessageBus and FederatedMessageBusBase (Phase 26). Use those for in-process messaging rather than building a new queue.
- **Hardcoding platform paths:** Use `OperatingSystem.IsWindows()`, `OperatingSystem.IsLinux()`, `OperatingSystem.IsMacOS()` for all platform-specific code.
- **Making CLI/GUI aware of plugin internals:** CLI/GUI must only talk to Shared. Shared talks to kernel via MessageBridge. Never bypass this.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| In-memory message queue | Custom queue for SendInProcessAsync | Channel<Message> from System.Threading.Channels | Thread-safe, bounded, backpressure-aware |
| Platform service management | Custom process spawner | System.Diagnostics.Process with sc/systemctl/launchctl | OS-native service management |
| USB detection | Custom WMI/dbus queries | DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Removable) | Built-in, cross-platform |
| JSON config management | Custom file format | System.Text.Json (already standardized) | Project-wide decision |
| NLP intent parsing | New NLP engine | Existing NaturalLanguageProcessor + UltimateInterfacePlugin | Already implemented, just need wiring |
| Capability event system | New event infrastructure | SDK's PluginCapabilityRegistry events + IMessageBus | Already has OnCapabilityRegistered/Unregistered |

**Key insight:** Nearly everything needed for Phase 31 already exists. The work is connecting, not creating.

## Common Pitfalls

### Pitfall 1: Two DataWarehouseHost Copies Diverging
**What goes wrong:** Launcher/Integration/DataWarehouseHost.cs and Metadata/Adapter/DataWarehouseHost.cs are nearly identical copies. Fixing one without the other creates silent divergence.
**Why it happens:** The Metadata/Adapter version was created as a reference example.
**How to avoid:** Treat Launcher as the source of truth. Update Metadata/Adapter to be a minimal example referencing Launcher types, or remove it and have CLI/GUI reference Launcher's DataWarehouseHost directly.
**Warning signs:** Builds pass but behavior differs between CLI and Launcher.

### Pitfall 2: MessageBridge TCP vs HTTP Protocol Mismatch
**What goes wrong:** MessageBridge uses raw TCP with length-prefixed JSON. RemoteInstanceConnection uses HTTP REST (`/api/v1/*`). Launcher exposes neither -- it runs an AdapterRunner.
**Why it happens:** Two connection protocols were designed independently.
**How to avoid:** For Phase 31, align on the HTTP/REST protocol since RemoteInstanceConnection already implements it. The Launcher needs to expose HTTP endpoints that match the `/api/v1/info`, `/api/v1/capabilities`, `/api/v1/message`, `/api/v1/execute` contract that RemoteInstanceConnection expects (DEPLOY-02).
**Warning signs:** Connect command succeeds but no data flows.

### Pitfall 3: Capability Models Diverge Between Shared and Launcher
**What goes wrong:** There are TWO `InstanceCapabilities` classes: `DataWarehouse.Shared.Models.InstanceCapabilities` (bool flags like SupportsEncryption) and `DataWarehouse.Launcher.Integration.InstanceCapabilities` (feature sets and plugin lists). They have different shapes.
**Why it happens:** Shared was built for CLI's initial static capability model. Launcher was built for dynamic discovery.
**How to avoid:** Unify on the Launcher model (or a new shared model) that supports dynamic capabilities. The Shared model with hardcoded `SupportsEncryption` booleans cannot represent arbitrary plugin capabilities.
**Warning signs:** CLI shows different capabilities than what the server actually has.

### Pitfall 4: Platform Service Registration Edge Cases
**What goes wrong:** `sc create` requires admin rights on Windows. `systemd` requires root on Linux. `launchd` has user-agent vs system-daemon distinction on macOS.
**Why it happens:** Platform service management has permission requirements that differ per OS.
**How to avoid:** Detect privileges before attempting service registration. Provide clear error messages suggesting `sudo` or admin elevation. Support both user-level and system-level service registration where the platform allows it.
**Warning signs:** Install works in dev but fails on real machines with permission errors.

### Pitfall 5: In-Process vs Remote Message Semantics
**What goes wrong:** In-process mode (Live/Embedded) should dispatch messages to the kernel directly. Remote mode should go over HTTP. If the code path differs too much, behavior diverges.
**Why it happens:** MessageBridge has separate `SendTcpAsync` and `SendInProcessAsync` paths.
**How to avoid:** Use the same message contract (the existing `Message` class) for both paths. For in-process, use System.Threading.Channels to bridge between Shared and the kernel's message bus. For remote, continue using HTTP.
**Warning signs:** Commands work in-process but fail remotely, or vice versa.

### Pitfall 6: DynamicCommandRegistry Startup Race
**What goes wrong:** CLI starts, subscribes to capability events, but plugins have already loaded and events were missed. CLI shows zero dynamic commands.
**Why it happens:** Event subscription happens after kernel initialization completes.
**How to avoid:** After subscribing to events, do an initial full capability query to populate the registry. Then use events for incremental updates.
**Warning signs:** Commands only appear after a plugin reload, not at startup.

## Code Examples

### Example 1: Real SendInProcessAsync (Replace Mock)

```csharp
// Source: Pattern based on existing MessageBridge structure
// Replace the mock in MessageBridge.SendInProcessAsync
private readonly Channel<(Message request, TaskCompletionSource<Message?> response)>? _inProcessChannel;

private async Task<Message?> SendInProcessAsync(Message message)
{
    if (_inProcessChannel == null)
        throw new InvalidOperationException("In-process channel not initialized");

    var tcs = new TaskCompletionSource<Message?>();
    await _inProcessChannel.Writer.WriteAsync((message, tcs));

    // Wait for kernel to process and respond
    return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(30));
}
```

### Example 2: Platform Service Registration (Windows)

```csharp
// Source: Pattern for DEPLOY-07
private async Task RegisterWindowsServiceAsync(InstallConfiguration config, CancellationToken ct)
{
    var exePath = Path.Combine(config.InstallPath, "DataWarehouse.Launcher.exe");
    var serviceName = "DataWarehouse";
    var displayName = "DataWarehouse Service";

    var startInfo = new ProcessStartInfo
    {
        FileName = "sc",
        Arguments = $"create {serviceName} binPath= \"{exePath}\" DisplayName= \"{displayName}\" start= auto",
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };

    using var process = Process.Start(startInfo)
        ?? throw new InvalidOperationException("Failed to start sc command");
    await process.WaitForExitAsync(ct);

    if (process.ExitCode != 0)
    {
        var error = await process.StandardError.ReadToEndAsync(ct);
        throw new InvalidOperationException($"Service registration failed: {error}");
    }
}
```

### Example 3: Systemd Unit File (Linux)

```csharp
// Source: Pattern for DEPLOY-07
private async Task RegisterLinuxServiceAsync(InstallConfiguration config, CancellationToken ct)
{
    var exePath = Path.Combine(config.InstallPath, "DataWarehouse.Launcher");
    var unitContent = $"""
        [Unit]
        Description=DataWarehouse Service
        After=network.target

        [Service]
        Type=notify
        ExecStart={exePath}
        WorkingDirectory={config.InstallPath}
        Restart=always
        RestartSec=10
        User=datawarehouse

        [Install]
        WantedBy=multi-user.target
        """;

    var unitPath = "/etc/systemd/system/datawarehouse.service";
    await File.WriteAllTextAsync(unitPath, unitContent, ct);

    // Reload systemd and enable
    await RunProcessAsync("systemctl", "daemon-reload", ct);
    if (config.AutoStart)
    {
        await RunProcessAsync("systemctl", "enable datawarehouse", ct);
    }
}
```

### Example 4: USB/Removable Media Detection

```csharp
// Source: Pattern for DEPLOY-04
public static class PortableMediaDetector
{
    public static bool IsRunningFromRemovableMedia()
    {
        var exePath = AppContext.BaseDirectory;
        var drives = DriveInfo.GetDrives();

        foreach (var drive in drives)
        {
            if (drive.DriveType == DriveType.Removable &&
                exePath.StartsWith(drive.RootDirectory.FullName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public static string? FindLocalLiveInstance(int[] portsToCheck)
    {
        // Check known ports for a running DW instance
        foreach (var port in portsToCheck)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                var response = client.GetAsync($"http://localhost:{port}/api/v1/info").Result;
                if (response.IsSuccessStatusCode)
                    return $"http://localhost:{port}";
            }
            catch { /* Port not responding */ }
        }
        return null;
    }
}
```

## State of the Art (Current Codebase State)

| Component | Current State | Target State (Phase 31) | Gap |
|-----------|--------------|------------------------|-----|
| CommandExecutor | Hardcoded 30+ commands in RegisterBuiltInCommands() | Reads from DynamicCommandRegistry | Need DynamicCommandRegistry + event subscription |
| CommandRegistry | Static list, capability-filtered | Replaced by DynamicCommandRegistry | Complete replacement |
| CapabilityManager | HasFeature with hardcoded switch | Dynamic feature list from live capabilities | Need to subscribe to capability events |
| MessageBridge.SendInProcessAsync | Returns mock response | Real in-memory message dispatch | Need Channel-based implementation |
| ServerStartCommand | Task.Delay(100) placeholder | Real kernel startup via KernelBuilder | Need to use DataWarehouseHost or KernelBuilder |
| ServerStopCommand | Task.Delay(500) placeholder | Real kernel shutdown | Need to call kernel.DisposeAsync() |
| InstanceManager.ExecuteAsync | Mock response when disconnected | Error when disconnected | Simple fix: throw instead of mock |
| DataWarehouseHost.CopyFilesAsync | Task.CompletedTask stub | Real file copy | Need Directory.Copy equivalent |
| DataWarehouseHost.RegisterServiceAsync | Log only | Real sc/systemctl/launchctl | Need platform-specific Process calls |
| DataWarehouseHost.CreateAdminUserAsync | Log only | Real security store integration | Need AccessControl plugin integration |
| DataWarehouseHost.InitializePluginsAsync | Creates dirs only | Copy plugin DLLs | Need file copy logic |
| Launcher HTTP endpoints | None exposed | /api/v1/* REST endpoints | Need Kestrel/ASP.NET minimal API |
| CLI `dw live` command | Does not exist | Start embedded DW, PersistData=false | Need new CLI command |
| CLI `dw install --from-usb` | Does not exist | Copy from USB, remap paths | Need USB validation + copy + remap |
| NLP routing to UltimateInterface | Not connected | Queries route through message bus | Need MessageBridge integration |
| GUI dynamic commands | Not implemented | Reads from DynamicCommandRegistry | Same registry as CLI |

## Dependency Analysis

### Phase Dependencies (SATISFIED)
- **Phase 27**: UltimateInterface migrated to new hierarchy -- ASSUMED completed before Phase 31 starts
- **Phase 22**: CLI System.CommandLine fix -- COMPLETED (Phase 22-04, SetAction pattern)

### Internal Dependencies Between Plans
- **31-01 (DynamicCommandRegistry)** -- no dependencies, can start first
- **31-02 (NLP routing)** -- soft dependency on 31-01 (NLP can route commands that DynamicCommandRegistry provides)
- **31-03 (Remove mocks/stubs)** -- foundation for 31-04, 31-05, 31-06, 31-07
- **31-04 (Launcher endpoint alignment)** -- depends on 31-03 (real ServerCommands)
- **31-05 (Live mode)** -- depends on 31-03 (real SendInProcessAsync) and 31-04 (launcher endpoints)
- **31-06 (Install mode clean)** -- depends on 31-03 (real CopyFiles, RegisterService, etc.)
- **31-07 (Install from USB)** -- depends on 31-06 (clean install must work first)
- **31-08 (Platform services)** -- depends on 31-06 (service registration is part of install)

### Recommended Execution Order
```
31-01 ─────────────────────────────────────> (parallel)
31-02 ─────────────────────────────────────> (parallel, soft dep on 01)
31-03 ──> 31-04 ──> 31-05                   (sequential)
31-03 ──> 31-06 ──> 31-07                   (sequential, parallel with 04-05)
31-03 ──> 31-08                              (can be part of 06 or separate)
```

## Key Implementation Details

### DynamicCommandRegistry Design (31-01)

The DynamicCommandRegistry must:
1. Subscribe to `capability.changed`, `plugin.loaded`, `plugin.unloaded` via MessageBridge
2. On `plugin.loaded`: query the plugin's capabilities and generate command definitions
3. On `plugin.unloaded`: remove that plugin's commands
4. On `capability.changed`: update specific capability-dependent commands
5. Expose an observable collection that CLI and GUI both read
6. On initial connect: do a full capability query to bootstrap the registry
7. Thread-safe: ConcurrentDictionary for command storage

### CapabilityManager Unification (31-01)

The current CapabilityManager has hardcoded feature names:
```csharp
return featureName.ToLowerInvariant() switch
{
    "encryption" => _capabilities.SupportsEncryption,
    "compression" => _capabilities.SupportsCompression,
    // ... hardcoded list
};
```

This must change to a dynamic model that reads from the DynamicCommandRegistry or from a live capabilities set received from the kernel.

### Launcher HTTP Endpoint Exposure (31-04)

The Launcher currently runs as a service-only daemon with no HTTP listener. For DEPLOY-02, the Launcher must expose:
- `GET /api/v1/info` -- instance info (id, version, mode)
- `GET /api/v1/capabilities` -- capabilities discovery
- `POST /api/v1/message` -- message dispatch to kernel
- `POST /api/v1/execute` -- command execution

Use ASP.NET Core minimal APIs (already available in .NET 10) within the Launcher. The ServiceHost should optionally expose a Kestrel HTTP server alongside the kernel. This aligns with RemoteInstanceConnection's expected protocol.

### In-Memory Message Bridge (31-03)

For in-process/embedded mode, MessageBridge.SendInProcessAsync must dispatch to the kernel's IMessageBus. Options:
1. **Channel-based**: Use `Channel<T>` for async producer-consumer between Shared and Kernel
2. **Direct reference**: If in-process, MessageBridge holds a reference to the kernel's IMessageBus (simpler but couples)
3. **Named pipe**: In-process IPC via System.IO.Pipes (decoupled but more overhead)

Recommendation: Use option 1 (Channel-based) for true decoupling with minimal overhead. The kernel consumer reads from the channel, dispatches to plugins, and writes responses back.

## Open Questions

1. **Launcher HTTP dependency**
   - What we know: Launcher currently has no HTTP server. RemoteInstanceConnection expects REST endpoints.
   - What's unclear: Whether to add ASP.NET Core web host dependency to Launcher or use raw HttpListener.
   - Recommendation: Use ASP.NET Core minimal APIs since the project already targets .NET 10 and Microsoft.Extensions.* is already referenced. Kestrel is lightweight and production-ready.

2. **CapabilityManager model unification**
   - What we know: Two competing InstanceCapabilities models (Shared.Models vs Launcher.Integration).
   - What's unclear: Whether to consolidate into SDK (since types like ConnectionTarget are already in SDK.Hosting) or keep in Shared.
   - Recommendation: Keep Shared.Models.InstanceCapabilities but refactor it to use a dynamic `HashSet<string>` for features instead of hardcoded bool properties. This aligns with how the Launcher model works.

3. **DataWarehouseHost duplication**
   - What we know: Two nearly identical DataWarehouseHost classes exist (Launcher and Metadata/Adapter).
   - What's unclear: Whether CLI should reference Launcher's version or keep a separate one.
   - Recommendation: CLI should reference the Launcher project for DataWarehouseHost. The Metadata/Adapter copy should be flagged for removal in Phase 31 cleanup. Currently CLI uses `DataWarehouse.Integration` namespace which resolves to the Metadata/Adapter version.

4. **GUI integration scope**
   - What we know: DataWarehouse.GUI uses MAUI and has basic scaffolding.
   - What's unclear: How deep the GUI dynamic reflection needs to go in Phase 31.
   - Recommendation: CLI-05 requires both CLI and GUI to read from DynamicCommandRegistry. The GUI needs to subscribe to CommandsChanged and update its UI. Exact MAUI UI work is likely limited to wiring the shared registry, not building new UI components.

## Sources

### Primary (HIGH confidence)
- Codebase inspection: `DataWarehouse.Shared/Commands/CommandExecutor.cs` -- hardcoded commands
- Codebase inspection: `DataWarehouse.Shared/MessageBridge.cs` -- mock SendInProcessAsync (line 197-218)
- Codebase inspection: `DataWarehouse.Launcher/Integration/DataWarehouseHost.cs` -- stub methods
- Codebase inspection: `DataWarehouse.CLI/Commands/ServerCommands.cs` -- Task.Delay placeholders
- Codebase inspection: `Plugins/UltimateInterface/UltimateInterfacePlugin.cs` -- NLP handlers exist
- Architecture document: `.planning/ARCHITECTURE_DECISIONS.md` -- AD-09 and AD-10

### Secondary (MEDIUM confidence)
- Codebase inspection: `DataWarehouse.Shared/CapabilityManager.cs` -- hardcoded feature switch
- Codebase inspection: `DataWarehouse.Launcher/Integration/InstanceConnection.cs` -- RemoteInstanceConnection HTTP protocol
- Codebase inspection: `DataWarehouse.Shared/Services/NaturalLanguageProcessor.cs` -- AI fallback path

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH -- all libraries already in use, no new dependencies
- Architecture: HIGH -- AD-09 and AD-10 provide explicit design, codebase confirms existing components
- Gap analysis: HIGH -- direct code inspection of stubs, mocks, and missing connections
- Platform service management: MEDIUM -- patterns are standard but edge cases (permissions, macOS launchd) need runtime validation
- GUI integration depth: MEDIUM -- MAUI scaffolding exists but dynamic reflection integration is lightly explored

**Research date:** 2026-02-14
**Valid until:** 2026-03-14 (stable -- internal architecture, no external API changes)
