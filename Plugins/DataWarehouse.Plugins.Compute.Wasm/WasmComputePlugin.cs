using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.Compute.Wasm;

/// <summary>
/// Production-ready WebAssembly compute-on-storage plugin.
/// Provides serverless function execution directly within the storage layer.
/// Implements a stack-based WASM interpreter with full sandboxing and resource management.
/// </summary>
/// <remarks>
/// This plugin implements Tasks 70.1-70.10:
/// - 70.1: WASM Runtime Integration with stack-based interpreter
/// - 70.2: Function Registry with version management
/// - 70.3: Sandboxed Execution with resource limits
/// - 70.4: Data Binding API for storage access
/// - 70.5: Trigger System (OnWrite, OnRead, OnSchedule, OnEvent)
/// - 70.6: Function Chaining for pipeline composition
/// - 70.7: Hot Reload with version history
/// - 70.8: Metrics and Logging
/// - 70.9: Pre-built Templates
/// - 70.10: Multi-language Support
/// </remarks>
public class WasmComputePlugin : WasmFunctionPluginBase
{
    #region Constants

    private const int DefaultPageSize = 65536; // 64KB WASM page size
    private const int MaxPages = 256; // Maximum 16MB memory by default
    private const int MaxStackDepth = 1024;
    private const int MaxCallDepth = 256;

    #endregion

    #region Fields

    private readonly BoundedDictionary<string, WasmModule> _modules = new BoundedDictionary<string, WasmModule>(1000);
    private readonly BoundedDictionary<string, List<FunctionVersion>> _versionHistory = new BoundedDictionary<string, List<FunctionVersion>>(1000);
    private readonly BoundedDictionary<string, WasmFunctionChain> _chains = new BoundedDictionary<string, WasmFunctionChain>(1000);
    private readonly BoundedDictionary<string, ScheduledFunction> _scheduledFunctions = new BoundedDictionary<string, ScheduledFunction>(1000);
    private readonly BoundedDictionary<string, EventSubscription> _eventSubscriptions = new BoundedDictionary<string, EventSubscription>(1000);
    private readonly BoundedDictionary<string, ExecutionMetrics> _metrics = new BoundedDictionary<string, ExecutionMetrics>(1000);
    private readonly BoundedDictionary<string, List<ExecutionLogEntry>> _executionLogs = new BoundedDictionary<string, List<ExecutionLogEntry>>(1000);
    private readonly ConcurrentQueue<PrioritizedExecution> _executionQueue = new();
    private readonly BoundedDictionary<string, FunctionTemplate> _templates = new BoundedDictionary<string, FunctionTemplate>(1000);

    private readonly SemaphoreSlim _executionSemaphore;
    private readonly object _schedulerLock = new();
    private Timer? _schedulerTimer;
    private IKernelContext? _kernelContext;
    private bool _disposed;

    #endregion

    #region Constructor

    /// <summary>
    /// Initializes a new instance of the WASM Compute plugin.
    /// </summary>
    public WasmComputePlugin()
    {
        _executionSemaphore = new SemaphoreSlim(MaxConcurrentExecutions, MaxConcurrentExecutions);
        InitializeTemplates();
    }

    #endregion

    #region Plugin Identity

    /// <inheritdoc />
    public override string Id => "com.datawarehouse.compute.wasm";

    /// <inheritdoc />
    public override string Name => "WASM Compute-on-Storage";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.DataTransformationProvider;

    /// <inheritdoc />
    public override IReadOnlyList<string> SupportedFeatures => new[]
    {
        "bulk-memory",
        "mutable-global",
        "sign-ext",
        "multi-value",
        "reference-types",
        "simd"
    };

    /// <inheritdoc />
    public override int MaxConcurrentExecutions => 100;

    #endregion

    #region Lifecycle

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct)
    {
        // Start the scheduler timer for scheduled functions
        _schedulerTimer = new Timer(
            ExecuteScheduledFunctions,
            null,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));

        LogInfo("WASM Compute plugin started");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override async Task StopAsync()
    {
        if (_schedulerTimer != null)
        {
            await _schedulerTimer.DisposeAsync();
            _schedulerTimer = null;
        }

        // Wait for all executions to complete with timeout
        var timeout = TimeSpan.FromSeconds(30);
        var completed = await Task.Run(() =>
        {
            for (int i = 0; i < MaxConcurrentExecutions; i++)
            {
                if (!_executionSemaphore.Wait(timeout))
                    return false;
            }
            return true;
        });

        if (!completed)
        {
            LogWarning("Some executions did not complete within timeout during shutdown");
        }

        // Release all acquired semaphores
        for (int i = 0; i < MaxConcurrentExecutions; i++)
        {
            try { _executionSemaphore.Release(); } catch { /* Best-effort release */ }
        }

        LogInfo("WASM Compute plugin stopped");
    }

    /// <inheritdoc />
    public override Task<HandshakeResponse> OnHandshakeAsync(HandshakeRequest request)
    {
        _kernelContext = request.Context;
        return base.OnHandshakeAsync(request);
    }

    #endregion

    #region Module Storage (70.2)

    /// <inheritdoc />
    protected override async Task StoreModuleAsync(string functionId, byte[] wasmBytes, CancellationToken ct)
    {
        var module = ParseWasmModule(wasmBytes, functionId);
        _modules[functionId] = module;

        // Track version history
        if (!_versionHistory.TryGetValue(functionId, out var history))
        {
            history = new List<FunctionVersion>();
            _versionHistory[functionId] = history;
        }

        var versionId = Guid.NewGuid().ToString("N");
        history.Add(new FunctionVersion
        {
            VersionId = versionId,
            FunctionId = functionId,
            DeployedAt = DateTime.UtcNow,
            ModuleHash = ComputeModuleHash(wasmBytes),
            SizeBytes = wasmBytes.Length
        });

        // Persist version to kernel storage for rollback capability
        if (_kernelContext?.Storage != null)
        {
            var versionPath = $"wasm/functions/{functionId}/versions/{versionId}.wasm";
            try
            {
                await _kernelContext.Storage.SaveAsync(versionPath, wasmBytes);
                LogInfo($"Stored WASM module {functionId} version {versionId}, size: {wasmBytes.Length} bytes");
            }
            catch (Exception ex)
            {
                LogWarning($"Failed to persist version {versionId} to storage: {ex.Message}");
                // Continue anyway - in-memory version is still available
            }
        }
        else
        {
            LogInfo($"Stored WASM module {functionId} (in-memory only), size: {wasmBytes.Length} bytes");
        }
    }

    /// <inheritdoc />
    protected override Task<byte[]?> LoadModuleAsync(string functionId, CancellationToken ct)
    {
        if (_modules.TryGetValue(functionId, out var module))
        {
            return Task.FromResult<byte[]?>(module.RawBytes);
        }
        return Task.FromResult<byte[]?>(null);
    }

    /// <inheritdoc />
    protected override Task RemoveModuleAsync(string functionId, CancellationToken ct)
    {
        _modules.TryRemove(functionId, out _);
        _metrics.TryRemove(functionId, out _);
        _executionLogs.TryRemove(functionId, out _);
        _scheduledFunctions.TryRemove(functionId, out _);

        // Remove event subscriptions
        var subsToRemove = _eventSubscriptions
            .Where(kvp => kvp.Value.FunctionId == functionId)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in subsToRemove)
        {
            _eventSubscriptions.TryRemove(key, out _);
        }

        LogInfo($"Removed WASM module {functionId}");
        return Task.CompletedTask;
    }

    #endregion

    #region WASM Execution (70.1, 70.3)

    /// <inheritdoc />
    protected override async Task<WasmExecutionResult> ExecuteWasmModuleAsync(
        byte[] wasmBytes,
        WasmFunctionMetadata metadata,
        WasmExecutionContext context,
        CancellationToken ct)
    {
        var startTime = DateTime.UtcNow;
        var logs = new List<string>();
        long peakMemory = 0;
        long instructionCount = 0;

        if (!await _executionSemaphore.WaitAsync(TimeSpan.FromSeconds(5), ct))
        {
            return new WasmExecutionResult
            {
                ExecutionId = context.ExecutionId,
                FunctionId = metadata.FunctionId,
                Status = WasmExecutionStatus.OutOfResources,
                ErrorMessage = "Execution queue full - too many concurrent executions"
            };
        }

        try
        {
            if (!_modules.TryGetValue(metadata.FunctionId, out var module))
            {
                module = ParseWasmModule(wasmBytes, metadata.FunctionId);
            }

            // Create sandboxed execution environment
            var sandbox = new WasmSandbox(
                metadata.ResourceLimits,
                metadata.AllowedPaths,
                metadata.AllowNetworkAccess,
                _kernelContext);

            // Create the virtual machine
            var vm = new WasmVirtualMachine(module, sandbox, metadata.Environment);

            // Bind storage APIs if paths are allowed
            if (metadata.AllowedPaths.Count > 0)
            {
                BindStorageApis(vm, metadata.AllowedPaths, context);
            }

            logs.Add($"[{DateTime.UtcNow:HH:mm:ss.fff}] Starting execution of {metadata.Name} v{metadata.Version}");
            logs.Add($"[{DateTime.UtcNow:HH:mm:ss.fff}] Trigger: {context.Trigger}, Input size: {context.InputData.Length} bytes");

            // Load input data into WASM memory
            var inputPtr = vm.AllocateMemory(context.InputData.Length);
            vm.WriteMemory(inputPtr, context.InputData);

            // Execute the entry point
            byte[] outputData;
            try
            {
                var result = await vm.ExecuteAsync(
                    metadata.EntryPoint,
                    new object[] { inputPtr, context.InputData.Length },
                    ct);

                peakMemory = vm.PeakMemoryUsage;
                instructionCount = vm.InstructionsExecuted;

                // Read output from result pointer
                if (result is int outputPtr && outputPtr > 0)
                {
                    var outputLen = vm.ReadInt32(outputPtr);
                    outputData = vm.ReadMemory(outputPtr + 4, outputLen);
                }
                else
                {
                    outputData = Array.Empty<byte>();
                }

                logs.Add($"[{DateTime.UtcNow:HH:mm:ss.fff}] Execution completed successfully");
                logs.Add($"[{DateTime.UtcNow:HH:mm:ss.fff}] Output size: {outputData.Length} bytes");
                logs.AddRange(vm.Logs);
            }
            catch (WasmExecutionException ex) when (ex.Type == WasmExceptionType.MemoryLimit)
            {
                logs.Add($"[{DateTime.UtcNow:HH:mm:ss.fff}] ERROR: Memory limit exceeded");
                return new WasmExecutionResult
                {
                    ExecutionId = context.ExecutionId,
                    FunctionId = metadata.FunctionId,
                    Status = WasmExecutionStatus.MemoryLimit,
                    ErrorMessage = ex.Message,
                    Logs = logs,
                    PeakMemoryBytes = vm.PeakMemoryUsage,
                    InstructionsExecuted = vm.InstructionsExecuted
                };
            }
            catch (WasmExecutionException ex) when (ex.Type == WasmExceptionType.InstructionLimit)
            {
                logs.Add($"[{DateTime.UtcNow:HH:mm:ss.fff}] ERROR: Instruction limit exceeded");
                return new WasmExecutionResult
                {
                    ExecutionId = context.ExecutionId,
                    FunctionId = metadata.FunctionId,
                    Status = WasmExecutionStatus.OutOfResources,
                    ErrorMessage = ex.Message,
                    Logs = logs,
                    PeakMemoryBytes = vm.PeakMemoryUsage,
                    InstructionsExecuted = vm.InstructionsExecuted
                };
            }

            var executionTime = DateTime.UtcNow - startTime;

            // Update metrics (70.8)
            UpdateMetrics(metadata.FunctionId, executionTime, peakMemory, instructionCount, true);

            // Log execution (70.8)
            LogExecution(metadata.FunctionId, context, executionTime, true, null);

            return new WasmExecutionResult
            {
                ExecutionId = context.ExecutionId,
                FunctionId = metadata.FunctionId,
                Status = WasmExecutionStatus.Success,
                OutputData = outputData,
                ExecutionTime = executionTime,
                PeakMemoryBytes = peakMemory,
                InstructionsExecuted = instructionCount,
                CpuTimeMs = (int)executionTime.TotalMilliseconds,
                Logs = logs,
                CompletedAt = DateTime.UtcNow
            };
        }
        catch (OperationCanceledException)
        {
            logs.Add($"[{DateTime.UtcNow:HH:mm:ss.fff}] Execution cancelled");
            UpdateMetrics(metadata.FunctionId, DateTime.UtcNow - startTime, peakMemory, instructionCount, false);
            LogExecution(metadata.FunctionId, context, DateTime.UtcNow - startTime, false, "Cancelled");

            return new WasmExecutionResult
            {
                ExecutionId = context.ExecutionId,
                FunctionId = metadata.FunctionId,
                Status = WasmExecutionStatus.Cancelled,
                ErrorMessage = "Execution was cancelled",
                Logs = logs
            };
        }
        catch (Exception ex)
        {
            logs.Add($"[{DateTime.UtcNow:HH:mm:ss.fff}] ERROR: {ex.Message}");
            UpdateMetrics(metadata.FunctionId, DateTime.UtcNow - startTime, peakMemory, instructionCount, false);
            LogExecution(metadata.FunctionId, context, DateTime.UtcNow - startTime, false, ex.Message);

            return WasmExecutionResult.Failure(
                context.ExecutionId,
                metadata.FunctionId,
                ex.Message,
                ex.StackTrace);
        }
        finally
        {
            _executionSemaphore.Release();
        }
    }

    /// <inheritdoc />
    public override Task<WasmValidationResult> ValidateModuleAsync(byte[] wasmBytes, CancellationToken ct)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var detectedFeatures = new List<string>();
        var exportedFunctions = new List<string>();
        long estimatedMemory = 0;

        try
        {
            // Validate WASM magic number and version
            if (wasmBytes.Length < 8)
            {
                errors.Add("Module too small to be valid WASM");
                return Task.FromResult(new WasmValidationResult
                {
                    IsValid = false,
                    Errors = errors
                });
            }

            // Check magic number: \0asm
            if (wasmBytes[0] != 0x00 || wasmBytes[1] != 0x61 ||
                wasmBytes[2] != 0x73 || wasmBytes[3] != 0x6D)
            {
                errors.Add("Invalid WASM magic number - expected \\0asm");
                return Task.FromResult(new WasmValidationResult
                {
                    IsValid = false,
                    Errors = errors
                });
            }

            // Check version (should be 1 for MVP WASM)
            var version = BitConverter.ToUInt32(wasmBytes, 4);
            if (version != 1)
            {
                warnings.Add($"Non-standard WASM version: {version}");
            }

            // Parse module sections
            var parser = new WasmModuleParser(wasmBytes);
            var parseResult = parser.Parse();

            // Analyze module
            foreach (var export in parseResult.Exports)
            {
                if (export.Kind == WasmExportKind.Function)
                {
                    exportedFunctions.Add(export.Name);
                }
            }

            // Check for required features
            detectedFeatures.AddRange(parseResult.RequiredFeatures);

            // Estimate memory requirements
            if (parseResult.MemorySection != null)
            {
                var minPages = parseResult.MemorySection.MinPages;
                var maxPages = parseResult.MemorySection.MaxPages ?? MaxPages;
                estimatedMemory = minPages * DefaultPageSize;

                if (maxPages > MaxPages)
                {
                    warnings.Add($"Module requests up to {maxPages} memory pages, but runtime limits to {MaxPages}");
                }
            }

            // Check for _start or specified entry point
            if (!exportedFunctions.Contains("_start"))
            {
                warnings.Add("Module does not export _start function - specify EntryPoint in metadata");
            }

            // Validate imports
            foreach (var import in parseResult.Imports)
            {
                if (!IsSupportedImport(import))
                {
                    errors.Add($"Unsupported import: {import.Module}.{import.Name}");
                }
            }

            return Task.FromResult(new WasmValidationResult
            {
                IsValid = errors.Count == 0,
                Errors = errors,
                Warnings = warnings,
                DetectedFeatures = detectedFeatures,
                ExportedFunctions = exportedFunctions,
                EstimatedMemoryBytes = estimatedMemory
            });
        }
        catch (Exception ex)
        {
            errors.Add($"Failed to parse WASM module: {ex.Message}");
            return Task.FromResult(new WasmValidationResult
            {
                IsValid = false,
                Errors = errors
            });
        }
    }

    #endregion

    #region Trigger System (70.5)

    /// <summary>
    /// Handles OnWrite trigger for storage objects.
    /// Automatically invokes registered functions when data is written.
    /// </summary>
    /// <param name="path">The storage path being written to.</param>
    /// <param name="data">The data being written.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Modified data after function execution, or original data if no functions match.</returns>
    public async Task<byte[]> OnWriteTriggerAsync(string path, byte[] data, CancellationToken ct = default)
    {
        var functions = await ListFunctionsAsync(FunctionTrigger.OnWrite, ct);
        var matchingFunctions = functions
            .Where(f => MatchesPath(f.AllowedPaths, path))
            .OrderBy(f => GetPriority(f))
            .ToList();

        if (matchingFunctions.Count == 0)
        {
            return data;
        }

        var currentData = data;
        foreach (var func in matchingFunctions)
        {
            var context = new WasmExecutionContext
            {
                FunctionId = func.FunctionId,
                Trigger = FunctionTrigger.OnWrite,
                TriggerPath = path,
                InputData = currentData
            };

            var result = await ExecuteFunctionAsync(func.FunctionId, context, ct);
            if (result.Status == WasmExecutionStatus.Success && result.OutputData.Length > 0)
            {
                currentData = result.OutputData;
            }
        }

        return currentData;
    }

    /// <summary>
    /// Handles OnRead trigger for storage objects.
    /// Automatically invokes registered functions when data is read.
    /// </summary>
    /// <param name="path">The storage path being read from.</param>
    /// <param name="data">The data being read.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Transformed data after function execution.</returns>
    public async Task<byte[]> OnReadTriggerAsync(string path, byte[] data, CancellationToken ct = default)
    {
        var functions = await ListFunctionsAsync(FunctionTrigger.OnRead, ct);
        var matchingFunctions = functions
            .Where(f => MatchesPath(f.AllowedPaths, path))
            .OrderBy(f => GetPriority(f))
            .ToList();

        if (matchingFunctions.Count == 0)
        {
            return data;
        }

        var currentData = data;
        foreach (var func in matchingFunctions)
        {
            var context = new WasmExecutionContext
            {
                FunctionId = func.FunctionId,
                Trigger = FunctionTrigger.OnRead,
                TriggerPath = path,
                InputData = currentData
            };

            var result = await ExecuteFunctionAsync(func.FunctionId, context, ct);
            if (result.Status == WasmExecutionStatus.Success && result.OutputData.Length > 0)
            {
                currentData = result.OutputData;
            }
        }

        return currentData;
    }

    /// <summary>
    /// Triggers functions in response to an event.
    /// </summary>
    /// <param name="eventName">Name of the event.</param>
    /// <param name="eventData">Event payload data.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Results from all triggered functions.</returns>
    public async Task<IReadOnlyList<WasmExecutionResult>> OnEventTriggerAsync(
        string eventName,
        byte[] eventData,
        CancellationToken ct = default)
    {
        var results = new List<WasmExecutionResult>();
        var functions = await ListFunctionsAsync(FunctionTrigger.OnEvent, ct);

        var matchingFunctions = functions
            .Where(f => f.EventPatterns.Any(p => MatchesEventPattern(p, eventName)))
            .ToList();

        foreach (var func in matchingFunctions)
        {
            var context = new WasmExecutionContext
            {
                FunctionId = func.FunctionId,
                Trigger = FunctionTrigger.OnEvent,
                TriggerEvent = eventName,
                InputData = eventData
            };

            var result = await ExecuteFunctionAsync(func.FunctionId, context, ct);
            results.Add(result);
        }

        return results;
    }

    /// <summary>
    /// Registers a function for scheduled execution.
    /// </summary>
    /// <param name="functionId">ID of the function to schedule.</param>
    /// <param name="cronExpression">Cron expression for scheduling.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task RegisterScheduledFunctionAsync(
        string functionId,
        string cronExpression,
        CancellationToken ct = default)
    {
        var schedule = new ScheduledFunction
        {
            FunctionId = functionId,
            CronExpression = cronExpression,
            LastExecuted = null,
            NextExecution = CalculateNextExecution(cronExpression, DateTime.UtcNow)
        };

        _scheduledFunctions[functionId] = schedule;
        LogInfo($"Registered scheduled function {functionId} with cron: {cronExpression}");

        return Task.CompletedTask;
    }

    private void ExecuteScheduledFunctions(object? state)
    {
        if (_disposed) return;

        var now = DateTime.UtcNow;
        var dueSchedules = _scheduledFunctions.Values
            .Where(s => s.NextExecution <= now)
            .ToList();

        foreach (var schedule in dueSchedules)
        {
            // Queue for execution
            _executionQueue.Enqueue(new PrioritizedExecution
            {
                FunctionId = schedule.FunctionId,
                Priority = ExecutionPriority.Normal,
                Context = new WasmExecutionContext
                {
                    FunctionId = schedule.FunctionId,
                    Trigger = FunctionTrigger.OnSchedule,
                    InputData = Array.Empty<byte>()
                }
            });

            // Update schedule
            schedule.LastExecuted = now;
            schedule.NextExecution = CalculateNextExecution(schedule.CronExpression, now);
        }

        // Process execution queue
        _ = ProcessExecutionQueueAsync();
    }

    private async Task ProcessExecutionQueueAsync()
    {
        while (_executionQueue.TryDequeue(out var execution))
        {
            try
            {
                await ExecuteFunctionAsync(execution.FunctionId, execution.Context, CancellationToken.None);
            }
            catch (Exception ex)
            {
                LogError($"Failed to execute scheduled function {execution.FunctionId}: {ex.Message}");
            }
        }
    }

    #endregion

    #region Function Chaining (70.6)

    /// <summary>
    /// Creates a function chain that executes multiple functions in sequence.
    /// Output of each function becomes input to the next.
    /// </summary>
    /// <param name="chainId">Unique identifier for the chain.</param>
    /// <param name="functionIds">Ordered list of function IDs to execute.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<WasmFunctionChain> CreateChainAsync(
        string chainId,
        IReadOnlyList<string> functionIds,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(chainId))
            throw new ArgumentException("Chain ID cannot be empty", nameof(chainId));

        if (functionIds.Count == 0)
            throw new ArgumentException("Chain must contain at least one function", nameof(functionIds));

        // Validate all functions exist
        foreach (var funcId in functionIds)
        {
            if (!_modules.ContainsKey(funcId))
            {
                throw new KeyNotFoundException($"Function {funcId} not found");
            }
        }

        var chain = new WasmFunctionChain
        {
            ChainId = chainId,
            FunctionIds = functionIds.ToList(),
            CreatedAt = DateTime.UtcNow,
            LastExecuted = null,
            ExecutionCount = 0
        };

        _chains[chainId] = chain;
        LogInfo($"Created function chain {chainId} with {functionIds.Count} functions");

        return Task.FromResult(chain);
    }

    /// <summary>
    /// Executes a function chain.
    /// </summary>
    /// <param name="chainId">ID of the chain to execute.</param>
    /// <param name="inputData">Input data for the first function.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result containing output from the last function in the chain.</returns>
    public async Task<ChainExecutionResult> ExecuteChainAsync(
        string chainId,
        byte[] inputData,
        CancellationToken ct = default)
    {
        if (!_chains.TryGetValue(chainId, out var chain))
        {
            return new ChainExecutionResult
            {
                ChainId = chainId,
                Success = false,
                ErrorMessage = "Chain not found"
            };
        }

        var startTime = DateTime.UtcNow;
        var stepResults = new List<ChainStepResult>();
        var currentData = inputData;

        foreach (var funcId in chain.FunctionIds)
        {
            var stepStart = DateTime.UtcNow;
            var context = new WasmExecutionContext
            {
                FunctionId = funcId,
                Trigger = FunctionTrigger.Manual,
                InputData = currentData,
                CorrelationId = chainId
            };

            var result = await ExecuteFunctionAsync(funcId, context, ct);

            stepResults.Add(new ChainStepResult
            {
                FunctionId = funcId,
                Status = result.Status,
                Duration = DateTime.UtcNow - stepStart,
                ErrorMessage = result.ErrorMessage
            });

            if (result.Status != WasmExecutionStatus.Success)
            {
                // Chain fails on first error
                return new ChainExecutionResult
                {
                    ChainId = chainId,
                    Success = false,
                    ErrorMessage = $"Chain failed at step {funcId}: {result.ErrorMessage}",
                    StepResults = stepResults,
                    TotalDuration = DateTime.UtcNow - startTime
                };
            }

            currentData = result.OutputData;
        }

        // Update chain statistics
        chain.LastExecuted = DateTime.UtcNow;
        chain.ExecutionCount++;

        return new ChainExecutionResult
        {
            ChainId = chainId,
            Success = true,
            OutputData = currentData,
            StepResults = stepResults,
            TotalDuration = DateTime.UtcNow - startTime
        };
    }

    /// <summary>
    /// Gets all defined function chains.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of all chains.</returns>
    public Task<IReadOnlyList<WasmFunctionChain>> ListChainsAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<WasmFunctionChain>>(_chains.Values.ToList());
    }

    /// <summary>
    /// Deletes a function chain.
    /// </summary>
    /// <param name="chainId">ID of the chain to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the chain was deleted.</returns>
    public Task<bool> DeleteChainAsync(string chainId, CancellationToken ct = default)
    {
        return Task.FromResult(_chains.TryRemove(chainId, out _));
    }

    #endregion

    #region Hot Reload (70.7)

    /// <summary>
    /// Hot-reloads a function with new code without downtime.
    /// Maintains version history for rollback capability.
    /// </summary>
    /// <param name="functionId">ID of the function to reload.</param>
    /// <param name="newWasmBytes">New WASM module bytes.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Updated function metadata.</returns>
    public async Task<WasmFunctionMetadata> HotReloadFunctionAsync(
        string functionId,
        byte[] newWasmBytes,
        CancellationToken ct = default)
    {
        var existingMetadata = await GetFunctionAsync(functionId, ct);
        if (existingMetadata == null)
        {
            throw new KeyNotFoundException($"Function {functionId} not found");
        }

        // Validate new module before deploying
        var validation = await ValidateModuleAsync(newWasmBytes, ct);
        if (!validation.IsValid)
        {
            throw new InvalidOperationException(
                $"New module validation failed: {string.Join(", ", validation.Errors)}");
        }

        // Store new version
        var newVersion = new FunctionVersion
        {
            VersionId = Guid.NewGuid().ToString("N"),
            FunctionId = functionId,
            DeployedAt = DateTime.UtcNow,
            ModuleHash = ComputeModuleHash(newWasmBytes),
            SizeBytes = newWasmBytes.Length,
            PreviousVersionId = _versionHistory.TryGetValue(functionId, out var history) && history.Count > 0
                ? history[^1].VersionId
                : null
        };

        // Atomically update the module
        var newModule = ParseWasmModule(newWasmBytes, functionId);
        _modules[functionId] = newModule;

        // Update version history
        if (!_versionHistory.TryGetValue(functionId, out history))
        {
            history = new List<FunctionVersion>();
            _versionHistory[functionId] = history;
        }
        history.Add(newVersion);

        // Increment version in metadata
        var newVersionString = IncrementVersion(existingMetadata.Version);
        var updatedMetadata = new WasmFunctionMetadata
        {
            FunctionId = functionId,
            Name = existingMetadata.Name,
            Version = newVersionString,
            Description = existingMetadata.Description,
            Author = existingMetadata.Author,
            EntryPoint = existingMetadata.EntryPoint,
            Triggers = existingMetadata.Triggers,
            ScheduleExpression = existingMetadata.ScheduleExpression,
            EventPatterns = existingMetadata.EventPatterns,
            ResourceLimits = existingMetadata.ResourceLimits,
            Environment = existingMetadata.Environment,
            AllowedPaths = existingMetadata.AllowedPaths,
            AllowNetworkAccess = existingMetadata.AllowNetworkAccess,
            DeployedAt = existingMetadata.DeployedAt,
            LastInvokedAt = existingMetadata.LastInvokedAt,
            InvocationCount = existingMetadata.InvocationCount,
            Tags = existingMetadata.Tags
        };

        LogInfo($"Hot-reloaded function {functionId} to version {newVersionString}");

        return await UpdateFunctionAsync(functionId, newWasmBytes, updatedMetadata, ct);
    }

    /// <summary>
    /// Rolls back a function to a previous version.
    /// </summary>
    /// <param name="functionId">ID of the function to rollback.</param>
    /// <param name="versionId">Target version ID, or null for previous version.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Rolled back function metadata.</returns>
    public async Task<WasmFunctionMetadata> RollbackFunctionAsync(
        string functionId,
        string? versionId = null,
        CancellationToken ct = default)
    {
        if (!_versionHistory.TryGetValue(functionId, out var history) || history.Count < 2)
        {
            throw new InvalidOperationException("No previous version available for rollback");
        }

        FunctionVersion targetVersion;
        if (versionId == null)
        {
            // Rollback to previous version
            targetVersion = history[^2];
        }
        else
        {
            targetVersion = history.FirstOrDefault(v => v.VersionId == versionId)
                ?? throw new KeyNotFoundException($"Version {versionId} not found");
        }

        // Use kernel storage service to retrieve the persisted version
        if (_kernelContext?.Storage == null)
        {
            throw new InvalidOperationException("Storage service not available for version rollback");
        }

        // Construct storage path for versioned module
        var versionPath = $"wasm/functions/{functionId}/versions/{targetVersion.VersionId}.wasm";

        try
        {
            // Load the version bytes from storage
            var versionBytes = await _kernelContext.Storage.LoadBytesAsync(versionPath);
            if (versionBytes == null)
            {
                throw new InvalidOperationException(
                    $"Version {targetVersion.VersionId} bytes not found in storage. " +
                    "Version history exists but module bytes were not persisted.");
            }

            // Get current metadata
            var currentMetadata = await GetFunctionAsync(functionId, ct);
            if (currentMetadata == null)
            {
                throw new KeyNotFoundException($"Function {functionId} not found");
            }

            // Hot reload with the versioned bytes
            var rolledBackMetadata = await HotReloadFunctionAsync(functionId, versionBytes, ct);

            LogInfo($"Rolled back function {functionId} to version {targetVersion.VersionId} " +
                   $"(deployed at {targetVersion.DeployedAt:yyyy-MM-dd HH:mm:ss} UTC)");

            return rolledBackMetadata;
        }
        catch (Exception ex) when (ex is not InvalidOperationException && ex is not KeyNotFoundException)
        {
            throw new InvalidOperationException(
                $"Failed to rollback function {functionId} to version {targetVersion.VersionId}: {ex.Message}",
                ex);
        }
    }

    /// <summary>
    /// Gets version history for a function.
    /// </summary>
    /// <param name="functionId">ID of the function.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of versions, newest first.</returns>
    public Task<IReadOnlyList<FunctionVersion>> GetVersionHistoryAsync(
        string functionId,
        CancellationToken ct = default)
    {
        if (_versionHistory.TryGetValue(functionId, out var history))
        {
            return Task.FromResult<IReadOnlyList<FunctionVersion>>(
                history.OrderByDescending(v => v.DeployedAt).ToList());
        }
        return Task.FromResult<IReadOnlyList<FunctionVersion>>(Array.Empty<FunctionVersion>());
    }

    #endregion

    #region Metrics & Logging (70.8)

    /// <summary>
    /// Gets detailed execution metrics for a function.
    /// </summary>
    /// <param name="functionId">ID of the function.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Execution metrics.</returns>
    public Task<ExecutionMetrics> GetExecutionMetricsAsync(
        string functionId,
        CancellationToken ct = default)
    {
        if (_metrics.TryGetValue(functionId, out var metrics))
        {
            return Task.FromResult(metrics);
        }
        return Task.FromResult(new ExecutionMetrics { FunctionId = functionId });
    }

    /// <summary>
    /// Gets execution logs for a function.
    /// </summary>
    /// <param name="functionId">ID of the function.</param>
    /// <param name="limit">Maximum number of log entries.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of log entries, newest first.</returns>
    public Task<IReadOnlyList<ExecutionLogEntry>> GetExecutionLogsAsync(
        string functionId,
        int limit = 100,
        CancellationToken ct = default)
    {
        if (_executionLogs.TryGetValue(functionId, out var logs))
        {
            return Task.FromResult<IReadOnlyList<ExecutionLogEntry>>(
                logs.OrderByDescending(l => l.Timestamp).Take(limit).ToList());
        }
        return Task.FromResult<IReadOnlyList<ExecutionLogEntry>>(Array.Empty<ExecutionLogEntry>());
    }

    /// <summary>
    /// Gets aggregate metrics across all functions.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Aggregate metrics.</returns>
    public Task<AggregateMetrics> GetAggregateMetricsAsync(CancellationToken ct = default)
    {
        var allMetrics = _metrics.Values.ToList();

        return Task.FromResult(new AggregateMetrics
        {
            TotalFunctions = _modules.Count,
            TotalExecutions = allMetrics.Sum(m => m.TotalExecutions),
            TotalSuccessfulExecutions = allMetrics.Sum(m => m.SuccessfulExecutions),
            TotalFailedExecutions = allMetrics.Sum(m => m.FailedExecutions),
            TotalTimeoutExecutions = allMetrics.Sum(m => m.TimeoutExecutions),
            AverageExecutionTimeMs = allMetrics.Count > 0
                ? allMetrics.Average(m => m.AverageExecutionTimeMs)
                : 0,
            TotalMemoryBytesUsed = allMetrics.Sum(m => m.PeakMemoryBytes),
            TotalInstructionsExecuted = allMetrics.Sum(m => m.TotalInstructionsExecuted),
            ActiveExecutions = MaxConcurrentExecutions - _executionSemaphore.CurrentCount
        });
    }

    private void UpdateMetrics(
        string functionId,
        TimeSpan executionTime,
        long memoryBytes,
        long instructions,
        bool success)
    {
        _metrics.AddOrUpdate(
            functionId,
            _ => new ExecutionMetrics
            {
                FunctionId = functionId,
                TotalExecutions = 1,
                SuccessfulExecutions = success ? 1 : 0,
                FailedExecutions = success ? 0 : 1,
                LastExecutionTime = DateTime.UtcNow,
                AverageExecutionTimeMs = executionTime.TotalMilliseconds,
                MinExecutionTimeMs = executionTime.TotalMilliseconds,
                MaxExecutionTimeMs = executionTime.TotalMilliseconds,
                PeakMemoryBytes = memoryBytes,
                TotalInstructionsExecuted = instructions
            },
            (_, existing) =>
            {
                var total = existing.TotalExecutions + 1;
                return new ExecutionMetrics
                {
                    FunctionId = functionId,
                    TotalExecutions = total,
                    SuccessfulExecutions = existing.SuccessfulExecutions + (success ? 1 : 0),
                    FailedExecutions = existing.FailedExecutions + (success ? 0 : 1),
                    TimeoutExecutions = existing.TimeoutExecutions,
                    LastExecutionTime = DateTime.UtcNow,
                    AverageExecutionTimeMs = (existing.AverageExecutionTimeMs * existing.TotalExecutions +
                                              executionTime.TotalMilliseconds) / total,
                    MinExecutionTimeMs = Math.Min(existing.MinExecutionTimeMs, executionTime.TotalMilliseconds),
                    MaxExecutionTimeMs = Math.Max(existing.MaxExecutionTimeMs, executionTime.TotalMilliseconds),
                    PeakMemoryBytes = Math.Max(existing.PeakMemoryBytes, memoryBytes),
                    TotalInstructionsExecuted = existing.TotalInstructionsExecuted + instructions
                };
            });
    }

    private void LogExecution(
        string functionId,
        WasmExecutionContext context,
        TimeSpan duration,
        bool success,
        string? errorMessage)
    {
        if (!_executionLogs.TryGetValue(functionId, out var logs))
        {
            logs = new List<ExecutionLogEntry>();
            _executionLogs[functionId] = logs;
        }

        // Keep only last 1000 entries per function
        while (logs.Count >= 1000)
        {
            logs.RemoveAt(0);
        }

        logs.Add(new ExecutionLogEntry
        {
            ExecutionId = context.ExecutionId,
            FunctionId = functionId,
            Trigger = context.Trigger,
            TriggerPath = context.TriggerPath,
            TriggerEvent = context.TriggerEvent,
            Timestamp = DateTime.UtcNow,
            DurationMs = duration.TotalMilliseconds,
            Success = success,
            ErrorMessage = errorMessage,
            InputSizeBytes = context.InputData.Length,
            InitiatedBy = context.InitiatedBy,
            CorrelationId = context.CorrelationId
        });
    }

    #endregion

    #region Pre-built Templates (70.9)

    private void InitializeTemplates()
    {
        // JSON Transformer template
        _templates["json-transform"] = new FunctionTemplate
        {
            TemplateId = "json-transform",
            Name = "JSON Transformer",
            Description = "Transforms JSON documents using JSONPath expressions",
            Category = TemplateCategory.DataTransformation,
            SourceLanguage = "AssemblyScript",
            RequiredInputs = new[] { "inputPath", "outputMapping" },
            DefaultResourceLimits = WasmResourceLimits.Minimal
        };

        // Data Validator template
        _templates["data-validator"] = new FunctionTemplate
        {
            TemplateId = "data-validator",
            Name = "Data Validator",
            Description = "Validates data against JSON Schema",
            Category = TemplateCategory.Validation,
            SourceLanguage = "AssemblyScript",
            RequiredInputs = new[] { "schema" },
            DefaultResourceLimits = WasmResourceLimits.Minimal
        };

        // Image Resizer template
        _templates["image-resize"] = new FunctionTemplate
        {
            TemplateId = "image-resize",
            Name = "Image Resizer",
            Description = "Resizes images to specified dimensions",
            Category = TemplateCategory.MediaProcessing,
            SourceLanguage = "Rust",
            RequiredInputs = new[] { "width", "height", "format" },
            DefaultResourceLimits = WasmResourceLimits.Default
        };

        // Text Processor template
        _templates["text-process"] = new FunctionTemplate
        {
            TemplateId = "text-process",
            Name = "Text Processor",
            Description = "Processes text with regex, transformations, and encoding",
            Category = TemplateCategory.DataTransformation,
            SourceLanguage = "AssemblyScript",
            RequiredInputs = new[] { "operation" },
            DefaultResourceLimits = WasmResourceLimits.Minimal
        };

        // Compression template
        _templates["compress"] = new FunctionTemplate
        {
            TemplateId = "compress",
            Name = "Data Compressor",
            Description = "Compresses data using gzip, brotli, or zstd",
            Category = TemplateCategory.DataTransformation,
            SourceLanguage = "Rust",
            RequiredInputs = new[] { "algorithm", "level" },
            DefaultResourceLimits = WasmResourceLimits.Default
        };

        // Encryption template
        _templates["encrypt"] = new FunctionTemplate
        {
            TemplateId = "encrypt",
            Name = "Data Encryptor",
            Description = "Encrypts data using AES-256-GCM",
            Category = TemplateCategory.Security,
            SourceLanguage = "Rust",
            RequiredInputs = new[] { "keyId" },
            DefaultResourceLimits = WasmResourceLimits.Minimal
        };

        // Hash Generator template
        _templates["hash"] = new FunctionTemplate
        {
            TemplateId = "hash",
            Name = "Hash Generator",
            Description = "Generates cryptographic hashes (SHA-256, SHA-512, BLAKE3)",
            Category = TemplateCategory.Security,
            SourceLanguage = "Rust",
            RequiredInputs = new[] { "algorithm" },
            DefaultResourceLimits = WasmResourceLimits.Minimal
        };

        // ETL Pipeline template
        _templates["etl-pipeline"] = new FunctionTemplate
        {
            TemplateId = "etl-pipeline",
            Name = "ETL Pipeline",
            Description = "Extract, Transform, Load pipeline for data integration",
            Category = TemplateCategory.DataIntegration,
            SourceLanguage = "AssemblyScript",
            RequiredInputs = new[] { "source", "transforms", "destination" },
            DefaultResourceLimits = WasmResourceLimits.Generous
        };

        // Aggregator template
        _templates["aggregator"] = new FunctionTemplate
        {
            TemplateId = "aggregator",
            Name = "Data Aggregator",
            Description = "Aggregates data with sum, count, average, min, max operations",
            Category = TemplateCategory.Analytics,
            SourceLanguage = "AssemblyScript",
            RequiredInputs = new[] { "groupBy", "aggregations" },
            DefaultResourceLimits = WasmResourceLimits.Default
        };

        // Notification Handler template
        _templates["notification"] = new FunctionTemplate
        {
            TemplateId = "notification",
            Name = "Notification Handler",
            Description = "Sends notifications via webhook, email, or messaging",
            Category = TemplateCategory.Integration,
            SourceLanguage = "AssemblyScript",
            RequiredInputs = new[] { "channel", "template" },
            DefaultResourceLimits = WasmResourceLimits.Minimal
        };
    }

    /// <summary>
    /// Gets all available function templates.
    /// </summary>
    /// <param name="category">Optional category filter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>List of templates.</returns>
    public Task<IReadOnlyList<FunctionTemplate>> GetTemplatesAsync(
        TemplateCategory? category = null,
        CancellationToken ct = default)
    {
        IEnumerable<FunctionTemplate> templates = _templates.Values;

        if (category.HasValue)
        {
            templates = templates.Where(t => t.Category == category.Value);
        }

        return Task.FromResult<IReadOnlyList<FunctionTemplate>>(templates.ToList());
    }

    /// <summary>
    /// Gets a specific template by ID.
    /// </summary>
    /// <param name="templateId">Template ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Template, or null if not found.</returns>
    public Task<FunctionTemplate?> GetTemplateAsync(
        string templateId,
        CancellationToken ct = default)
    {
        _templates.TryGetValue(templateId, out var template);
        return Task.FromResult<FunctionTemplate?>(template);
    }

    #endregion

    #region Multi-language Support (70.10)

    /// <summary>
    /// Gets supported source languages for WASM compilation.
    /// </summary>
    public IReadOnlyList<LanguageInfo> SupportedLanguages => new[]
    {
        new LanguageInfo
        {
            Language = "Rust",
            LanguageVersion = "1.70+",
            Toolchain = "wasm32-unknown-unknown target",
            CompilerCommand = "cargo build --target wasm32-unknown-unknown --release",
            Notes = "Best performance, full WASM feature support"
        },
        new LanguageInfo
        {
            Language = "AssemblyScript",
            LanguageVersion = "0.27+",
            Toolchain = "asc compiler",
            CompilerCommand = "asc entry.ts -o output.wasm --optimize",
            Notes = "TypeScript-like syntax, easy learning curve"
        },
        new LanguageInfo
        {
            Language = "C/C++",
            LanguageVersion = "C11/C++17",
            Toolchain = "Emscripten or WASI SDK",
            CompilerCommand = "emcc source.c -o output.wasm -s STANDALONE_WASM",
            Notes = "Wide ecosystem, existing library support"
        },
        new LanguageInfo
        {
            Language = "Go",
            LanguageVersion = "1.21+",
            Toolchain = "GOOS=wasip1 GOARCH=wasm",
            CompilerCommand = "GOOS=wasip1 GOARCH=wasm go build -o output.wasm",
            Notes = "Larger binary size, good concurrency support"
        },
        new LanguageInfo
        {
            Language = "Zig",
            LanguageVersion = "0.11+",
            Toolchain = "zig build with wasm32-freestanding",
            CompilerCommand = "zig build-exe -target wasm32-freestanding",
            Notes = "Zero-cost abstractions, C interop"
        },
        new LanguageInfo
        {
            Language = "Python",
            LanguageVersion = "3.11+",
            Toolchain = "py2wasm or micropython",
            CompilerCommand = "py2wasm script.py -o output.wasm",
            Notes = "Experimental, limited stdlib support"
        }
    };

    #endregion

    #region Data Binding API (70.4)

    private void BindStorageApis(WasmVirtualMachine vm, IReadOnlyList<string> allowedPaths, WasmExecutionContext context)
    {
        // Bind storage read function
        vm.RegisterHostFunction("storage", "read", async (object[] args) =>
        {
            var pathPtr = (int)args[0];
            var pathLen = (int)args[1];
            var path = vm.ReadString(pathPtr, pathLen);

            if (!IsPathAllowed(path, allowedPaths))
            {
                throw new UnauthorizedAccessException($"Access to path '{path}' not allowed");
            }

            if (_kernelContext?.Storage == null)
            {
                throw new InvalidOperationException("Storage service not available");
            }

            var data = await _kernelContext.Storage.LoadBytesAsync(path);
            if (data == null)
            {
                return -1; // Not found
            }

            var resultPtr = vm.AllocateMemory(data.Length + 4);
            vm.WriteInt32(resultPtr, data.Length);
            vm.WriteMemory(resultPtr + 4, data);
            return resultPtr;
        });

        // Bind storage write function
        vm.RegisterHostFunction("storage", "write", async (object[] args) =>
        {
            var pathPtr = (int)args[0];
            var pathLen = (int)args[1];
            var dataPtr = (int)args[2];
            var dataLen = (int)args[3];

            var path = vm.ReadString(pathPtr, pathLen);
            var data = vm.ReadMemory(dataPtr, dataLen);

            if (!IsPathAllowed(path, allowedPaths))
            {
                throw new UnauthorizedAccessException($"Access to path '{path}' not allowed");
            }

            if (_kernelContext?.Storage == null)
            {
                throw new InvalidOperationException("Storage service not available");
            }

            await _kernelContext.Storage.SaveAsync(path, data);
            return 0; // Success
        });

        // Bind storage exists function
        vm.RegisterHostFunction("storage", "exists", async (object[] args) =>
        {
            var pathPtr = (int)args[0];
            var pathLen = (int)args[1];
            var path = vm.ReadString(pathPtr, pathLen);

            if (!IsPathAllowed(path, allowedPaths))
            {
                return 0; // Treat as not found for security
            }

            if (_kernelContext?.Storage == null)
            {
                throw new InvalidOperationException("Storage service not available");
            }

            return await _kernelContext.Storage.ExistsAsync(path) ? 1 : 0;
        });

        // Bind storage delete function
        vm.RegisterHostFunction("storage", "delete", async (object[] args) =>
        {
            var pathPtr = (int)args[0];
            var pathLen = (int)args[1];
            var path = vm.ReadString(pathPtr, pathLen);

            if (!IsPathAllowed(path, allowedPaths))
            {
                throw new UnauthorizedAccessException($"Access to path '{path}' not allowed");
            }

            if (_kernelContext?.Storage == null)
            {
                throw new InvalidOperationException("Storage service not available");
            }

            return await _kernelContext.Storage.DeleteAsync(path) ? 1 : 0;
        });

        // Bind storage list function
        vm.RegisterHostFunction("storage", "list", async (object[] args) =>
        {
            var prefixPtr = (int)args[0];
            var prefixLen = (int)args[1];
            var limit = (int)args[2];
            var prefix = vm.ReadString(prefixPtr, prefixLen);

            if (!IsPathAllowed(prefix, allowedPaths))
            {
                throw new UnauthorizedAccessException($"Access to prefix '{prefix}' not allowed");
            }

            if (_kernelContext?.Storage == null)
            {
                throw new InvalidOperationException("Storage service not available");
            }

            var items = await _kernelContext.Storage.ListAsync(prefix, limit);
            var json = JsonSerializer.Serialize(items.Select(i => i.Path).ToArray());
            var jsonBytes = Encoding.UTF8.GetBytes(json);

            var resultPtr = vm.AllocateMemory(jsonBytes.Length + 4);
            vm.WriteInt32(resultPtr, jsonBytes.Length);
            vm.WriteMemory(resultPtr + 4, jsonBytes);
            return resultPtr;
        });

        // Bind logging function
        vm.RegisterHostFunction("env", "log", (object[] args) =>
        {
            var msgPtr = (int)args[0];
            var msgLen = (int)args[1];
            var message = vm.ReadString(msgPtr, msgLen);
            vm.AddLog($"[WASM] {message}");
            return Task.FromResult<object?>(null);
        });

        // Bind timestamp function
        vm.RegisterHostFunction("env", "timestamp", (object[] args) =>
        {
            return Task.FromResult<object?>(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        });

        // Bind random function
        vm.RegisterHostFunction("env", "random", (object[] args) =>
        {
            return Task.FromResult<object?>(Random.Shared.NextDouble());
        });
    }

    private static bool IsPathAllowed(string path, IReadOnlyList<string> allowedPaths)
    {
        if (allowedPaths.Count == 0) return false;

        // Normalize path
        path = path.Replace('\\', '/').TrimStart('/');

        foreach (var allowed in allowedPaths)
        {
            var normalizedAllowed = allowed.Replace('\\', '/').TrimStart('/');

            if (normalizedAllowed.EndsWith("/*"))
            {
                // Prefix match
                var prefix = normalizedAllowed[..^2];
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (normalizedAllowed == "*")
            {
                // Allow all
                return true;
            }
            else if (path.Equals(normalizedAllowed, StringComparison.OrdinalIgnoreCase))
            {
                // Exact match
                return true;
            }
        }

        return false;
    }

    #endregion

    #region Helper Methods

    private WasmModule ParseWasmModule(byte[] wasmBytes, string functionId)
    {
        var parser = new WasmModuleParser(wasmBytes);
        var parseResult = parser.Parse();

        return new WasmModule
        {
            FunctionId = functionId,
            RawBytes = wasmBytes,
            Types = parseResult.Types,
            Imports = parseResult.Imports,
            Functions = parseResult.Functions,
            Tables = parseResult.Tables,
            Memories = parseResult.Memories,
            Globals = parseResult.Globals,
            Exports = parseResult.Exports,
            StartFunction = parseResult.StartFunction,
            Elements = parseResult.Elements,
            Code = parseResult.Code,
            Data = parseResult.Data
        };
    }

    private static string ComputeModuleHash(byte[] wasmBytes)
    {
        // Note: Bus delegation not available in this context; using direct crypto
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(wasmBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool MatchesPath(IReadOnlyList<string> patterns, string path)
    {
        if (patterns.Count == 0) return true; // No pattern means match all

        foreach (var pattern in patterns)
        {
            if (pattern == "*") return true;
            if (pattern.EndsWith("/*") && path.StartsWith(pattern[..^2])) return true;
            if (path.Equals(pattern, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool MatchesEventPattern(string pattern, string eventName)
    {
        if (pattern == "*") return true;
        if (pattern.EndsWith(".*"))
        {
            var prefix = pattern[..^2];
            return eventName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        return eventName.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static int GetPriority(WasmFunctionMetadata metadata)
    {
        if (metadata.Tags.TryGetValue("priority", out var priorityStr) &&
            int.TryParse(priorityStr, out var priority))
        {
            return priority;
        }
        return 100; // Default priority
    }

    private static DateTime CalculateNextExecution(string cronExpression, DateTime after)
    {
        // Simple cron parser for common patterns
        // Format: minute hour day month dayOfWeek
        var parts = cronExpression.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5)
        {
            throw new ArgumentException($"Invalid cron expression: {cronExpression}");
        }

        // For simplicity, handle common cases
        var minute = ParseCronField(parts[0], 0, 59);
        var hour = ParseCronField(parts[1], 0, 23);

        var next = after.AddMinutes(1);
        next = new DateTime(next.Year, next.Month, next.Day, next.Hour, next.Minute, 0, DateTimeKind.Utc);

        // Find next matching time
        for (int i = 0; i < 1440; i++) // Check up to 24 hours
        {
            if ((minute.Contains(next.Minute) || minute.Contains(-1)) &&
                (hour.Contains(next.Hour) || hour.Contains(-1)))
            {
                return next;
            }
            next = next.AddMinutes(1);
        }

        return after.AddHours(1); // Default to 1 hour
    }

    private static HashSet<int> ParseCronField(string field, int min, int max)
    {
        var result = new HashSet<int>();

        if (field == "*")
        {
            result.Add(-1); // Wildcard marker
            return result;
        }

        foreach (var part in field.Split(','))
        {
            if (part.Contains('/'))
            {
                // Step values: */5 or 1-30/5
                var stepParts = part.Split('/');
                var step = int.Parse(stepParts[1]);
                var start = stepParts[0] == "*" ? min : int.Parse(stepParts[0]);
                for (int i = start; i <= max; i += step)
                {
                    result.Add(i);
                }
            }
            else if (part.Contains('-'))
            {
                // Range: 1-5
                var rangeParts = part.Split('-');
                var start = int.Parse(rangeParts[0]);
                var end = int.Parse(rangeParts[1]);
                for (int i = start; i <= end; i++)
                {
                    result.Add(i);
                }
            }
            else
            {
                result.Add(int.Parse(part));
            }
        }

        return result;
    }

    private static string IncrementVersion(string version)
    {
        var parts = version.Split('.');
        if (parts.Length >= 3 && int.TryParse(parts[2], out var patch))
        {
            parts[2] = (patch + 1).ToString();
            return string.Join('.', parts);
        }
        return version + ".1";
    }

    private bool IsSupportedImport(WasmImport import)
    {
        // Supported import modules
        var supportedModules = new HashSet<string>
        {
            "env",      // Environment functions
            "storage",  // Storage API
            "wasi_snapshot_preview1", // WASI
            "wasi"      // WASI legacy
        };

        return supportedModules.Contains(import.Module);
    }

    private void LogInfo(string message)
    {
        _kernelContext?.LogInfo($"[WasmCompute] {message}");
    }

    private void LogWarning(string message)
    {
        _kernelContext?.LogWarning($"[WasmCompute] {message}");
    }

    private void LogError(string message)
    {
        _kernelContext?.LogError($"[WasmCompute] {message}");
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes the plugin resources.
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_disposed) return;
            _disposed = true;

            _schedulerTimer?.Dispose();
            _executionSemaphore.Dispose();

            _modules.Clear();
            _versionHistory.Clear();
            _chains.Clear();
            _scheduledFunctions.Clear();
            _eventSubscriptions.Clear();
            _metrics.Clear();
            _executionLogs.Clear();
        }
        base.Dispose(disposing);
    }

    #endregion

    #region Metadata

    /// <inheritdoc />
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "WasmCompute";
        metadata["SupportedLanguages"] = SupportedLanguages.Select(l => l.Language).ToArray();
        metadata["MaxConcurrentExecutions"] = MaxConcurrentExecutions;
        metadata["SupportedFeatures"] = SupportedFeatures;
        metadata["SupportsHotReload"] = true;
        metadata["SupportsFunctionChaining"] = true;
        metadata["SupportsScheduledExecution"] = true;
        metadata["SupportsEventTriggers"] = true;
        metadata["TemplateCount"] = _templates.Count;
        return metadata;
    }

    #endregion
}
