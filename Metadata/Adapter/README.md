# DataWarehouse Kernel Adapter Pattern

**Namespace:** `DataWarehouse.Integration`

A lightweight, copyable adapter pattern for integrating kernel-based applications into any .NET project.

## What Is This?

The Adapter pattern provides a standardized interface for running, managing, and monitoring kernel-based applications. It decouples your application logic from the launcher infrastructure, enabling:

- **Plug-and-play integration** - Copy 3 files, implement 1 interface, done
- **Lifecycle management** - Standardized initialization, start, stop, dispose
- **State monitoring** - Event-driven state changes with detailed logging
- **Metrics collection** - Built-in statistics and custom metrics support
- **Testing support** - Easy mocking and testing with adapter interfaces

## Files Included

| File | Purpose |
|------|---------|
| `IKernelAdapter.cs` | Core interface, enums, and data classes |
| `AdapterFactory.cs` | Factory for registering and creating adapters |
| `AdapterRunner.cs` | Runner for executing adapters with lifecycle management |
| `README.md` | This integration guide |

## Quick Start

### 1. Copy Files to Your Project

Copy the entire `Adapter` folder into your .NET project:

```
YourProject/
  Integration/           <- or any namespace you prefer
    IKernelAdapter.cs
    AdapterFactory.cs
    AdapterRunner.cs
```

### 2. Update Namespace (Optional)

If you want a different namespace than `DataWarehouse.Integration`, do a find-replace across all 3 files:

```
Find:    namespace DataWarehouse.Integration;
Replace: namespace YourCompany.YourProject.Integration;
```

### 3. Implement IKernelAdapter

Create your adapter implementation:

```csharp
using DataWarehouse.Integration;
using Microsoft.Extensions.Logging;

namespace YourProject;

public class MyAppAdapter : IKernelAdapter
{
    private readonly ILogger<MyAppAdapter> _logger;
    private KernelState _state = KernelState.Uninitialized;
    private string _kernelId = "";
    private DateTime _startedAt;

    public string KernelId => _kernelId;
    public KernelState State => _state;

    public event EventHandler<KernelStateChangedEventArgs>? StateChanged;

    public Task InitializeAsync(AdapterOptions options, CancellationToken cancellationToken = default)
    {
        _kernelId = options.KernelId;
        _logger = options.LoggerFactory?.CreateLogger<MyAppAdapter>()
                  ?? NullLogger<MyAppAdapter>.Instance;

        SetState(KernelState.Initializing);

        // Your initialization logic here
        // - Load configuration
        // - Initialize dependencies
        // - Validate settings

        SetState(KernelState.Ready);
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        SetState(KernelState.Running);
        _startedAt = DateTime.UtcNow;

        // Your startup logic here
        // - Start background tasks
        // - Begin processing
        // - Connect to services

        _logger.LogInformation("MyApp kernel started: {KernelId}", _kernelId);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        SetState(KernelState.Stopping);

        // Your shutdown logic here
        // - Stop background tasks
        // - Flush buffers
        // - Disconnect from services

        SetState(KernelState.Stopped);
        _logger.LogInformation("MyApp kernel stopped: {KernelId}", _kernelId);
        return Task.CompletedTask;
    }

    public KernelStats GetStats()
    {
        return new KernelStats
        {
            KernelId = _kernelId,
            State = _state,
            StartedAt = _startedAt,
            Uptime = DateTime.UtcNow - _startedAt,
            PluginCount = 0,           // Your metrics here
            OperationsProcessed = 0,   // Your metrics here
            BytesProcessed = 0,        // Your metrics here
            CustomMetrics = new Dictionary<string, object>
            {
                ["YourCustomMetric"] = "value"
            }
        };
    }

    private void SetState(KernelState newState)
    {
        var previousState = _state;
        _state = newState;
        StateChanged?.Invoke(this, new KernelStateChangedEventArgs
        {
            PreviousState = previousState,
            NewState = newState
        });
    }

    public async ValueTask DisposeAsync()
    {
        // Your cleanup logic here
        await Task.CompletedTask;
    }
}
```

### 4. Register and Run

In your application's entry point:

```csharp
using DataWarehouse.Integration;
using Microsoft.Extensions.Logging;

// Setup logging
var loggerFactory = LoggerFactory.Create(builder =>
{
    builder.AddConsole();
    builder.SetMinimumLevel(LogLevel.Information);
});

// Register your adapter
AdapterFactory.Register("MyApp", () => new MyAppAdapter());

// Configure options
var options = new AdapterOptions
{
    KernelId = "myapp-001",
    OperatingMode = "Production",
    ConfigPath = "config.json",
    PluginPath = "./plugins",
    CustomConfig = new Dictionary<string, object>
    {
        ["ConnectionString"] = "your-connection-string",
        ["MaxWorkers"] = 10
    }
};

// Run the adapter
var runner = new AdapterRunner(loggerFactory);
var exitCode = await runner.RunAsync(options, "MyApp");

return exitCode;
```

## Advanced Usage

### Custom Configuration Options

Extend `AdapterOptions` for custom configuration:

```csharp
public class MyAppOptions : AdapterOptions
{
    public string DatabaseConnectionString { get; set; } = "";
    public int MaxConcurrentTasks { get; set; } = 10;
    public TimeSpan HealthCheckInterval { get; set; } = TimeSpan.FromMinutes(1);
}

// In your adapter
public Task InitializeAsync(AdapterOptions options, CancellationToken cancellationToken = default)
{
    var myOptions = options as MyAppOptions
                    ?? throw new ArgumentException("Expected MyAppOptions");

    // Use strongly-typed options
    var connString = myOptions.DatabaseConnectionString;
    // ...
}
```

### Multiple Adapter Types

Register and switch between different adapter implementations:

```csharp
// Register multiple adapters
AdapterFactory.Register("Production", () => new ProductionAdapter());
AdapterFactory.Register("Development", () => new DevelopmentAdapter());
AdapterFactory.Register("Testing", () => new MockAdapter());

// Set default
AdapterFactory.SetDefault("Production");

// Run specific adapter
await runner.RunAsync(options, "Development");

// Or use default
await runner.RunAsync(options);
```

### State Monitoring

Monitor adapter state changes:

```csharp
adapter.StateChanged += (sender, e) =>
{
    Console.WriteLine($"State: {e.PreviousState} -> {e.NewState}");

    if (e.Exception != null)
    {
        Console.WriteLine($"Error: {e.Exception.Message}");
    }

    if (e.Message != null)
    {
        Console.WriteLine($"Message: {e.Message}");
    }
};
```

### Metrics Collection

Collect and expose kernel metrics:

```csharp
// In your adapter
private long _operationsProcessed = 0;
private long _bytesProcessed = 0;

public void ProcessOperation(byte[] data)
{
    Interlocked.Increment(ref _operationsProcessed);
    Interlocked.Add(ref _bytesProcessed, data.Length);
}

public KernelStats GetStats()
{
    return new KernelStats
    {
        KernelId = _kernelId,
        State = _state,
        StartedAt = _startedAt,
        Uptime = DateTime.UtcNow - _startedAt,
        OperationsProcessed = _operationsProcessed,
        BytesProcessed = _bytesProcessed,
        CustomMetrics = new Dictionary<string, object>
        {
            ["ErrorRate"] = CalculateErrorRate(),
            ["AvgLatency"] = CalculateAvgLatency(),
            ["QueueDepth"] = GetQueueDepth()
        }
    };
}

// Expose metrics endpoint
app.MapGet("/metrics", () =>
{
    var stats = runner.CurrentAdapter?.GetStats();
    return Results.Ok(stats);
});
```

### Graceful Shutdown

Handle shutdown signals:

```csharp
var runner = new AdapterRunner(loggerFactory);

// Handle Ctrl+C
Console.CancelKeyPress += (sender, e) =>
{
    e.Cancel = true;
    runner.RequestShutdown();
};

// Handle SIGTERM
AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
{
    runner.RequestShutdown();
};

await runner.RunAsync(options);
```

### Testing with Mock Adapters

Create mock adapters for testing:

```csharp
public class MockAdapter : IKernelAdapter
{
    public string KernelId => "mock-adapter";
    public KernelState State { get; private set; } = KernelState.Uninitialized;

    public event EventHandler<KernelStateChangedEventArgs>? StateChanged;

    public Task InitializeAsync(AdapterOptions options, CancellationToken ct = default)
    {
        State = KernelState.Ready;
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        State = KernelState.Running;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        State = KernelState.Stopped;
        return Task.CompletedTask;
    }

    public KernelStats GetStats() => new() { KernelId = KernelId, State = State };

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

// Use in tests
[Test]
public async Task TestAdapterLifecycle()
{
    AdapterFactory.Register("Mock", () => new MockAdapter());

    var runner = new AdapterRunner();
    var options = new AdapterOptions();

    var task = runner.RunAsync(options, "Mock");
    await Task.Delay(100);

    Assert.AreEqual(KernelState.Running, runner.CurrentAdapter?.State);

    runner.RequestShutdown();
    await task;
}
```

## Dependencies

- **Microsoft.Extensions.Logging** - For logging infrastructure
- **Microsoft.Extensions.Logging.Abstractions** - For NullLogger fallback

Both are standard .NET libraries, no additional dependencies required.

## Architecture

```
┌─────────────────────────────────────────────────────┐
│                  AdapterRunner                      │
│  - Lifecycle orchestration                          │
│  - Cancellation handling                            │
│  - Logging integration                              │
└──────────────────┬──────────────────────────────────┘
                   │ manages
                   ▼
┌─────────────────────────────────────────────────────┐
│              AdapterFactory                         │
│  - Adapter registration                             │
│  - Instance creation                                │
│  - Type management                                  │
└──────────────────┬──────────────────────────────────┘
                   │ creates
                   ▼
┌─────────────────────────────────────────────────────┐
│             IKernelAdapter                          │
│  - Your implementation                              │
│  - Business logic                                   │
│  - State management                                 │
└─────────────────────────────────────────────────────┘
```

## Best Practices

1. **Single Responsibility** - Each adapter handles one kernel/service type
2. **State Transitions** - Always transition through proper states (Uninitialized -> Initializing -> Ready -> Running)
3. **Error Handling** - Set `State = Failed` on errors and raise `StateChanged` event with exception
4. **Async Operations** - Use async/await for all I/O operations
5. **Cancellation** - Respect cancellation tokens in all async methods
6. **Dispose Pattern** - Clean up resources in `DisposeAsync()`
7. **Logging** - Use injected `ILogger` for all diagnostic output
8. **Metrics** - Keep metrics lightweight and update atomically

## Integration Checklist

- [ ] Copy 3 adapter files to your project
- [ ] Update namespace if needed
- [ ] Implement `IKernelAdapter` for your kernel
- [ ] Register adapter with `AdapterFactory`
- [ ] Create `AdapterOptions` configuration
- [ ] Run adapter with `AdapterRunner`
- [ ] Handle state changes and metrics
- [ ] Test adapter lifecycle
- [ ] Implement graceful shutdown
- [ ] Add error handling and logging

## Support

This is a copyable pattern - modify as needed for your use case. The code is self-contained with no external dependencies beyond standard .NET libraries.

For the original DataWarehouse implementation, see:
- `DataWarehouse.Launcher/Adapters/` - Original source
- `DataWarehouse.Launcher/WarehouseAdapter.cs` - Reference implementation
