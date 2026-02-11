using System.Diagnostics;
using System.Text;
using DataWarehouse.SDK.AI;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Compute;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Plugins.UltimateCompute;

/// <summary>
/// Abstract base class for all compute runtime strategies in the UltimateCompute plugin.
/// Provides CLI process execution, timing measurement, Intelligence integration, and
/// standard error handling for all runtime implementations.
/// </summary>
/// <remarks>
/// <para>
/// All 51+ compute strategies inherit from this base class, which provides:
/// </para>
/// <list type="bullet">
/// <item><description>CLI process runner via <see cref="RunProcessAsync"/> for invoking external runtimes</description></item>
/// <item><description>Execution timing via <see cref="MeasureExecutionAsync"/> wrapping Stopwatch</description></item>
/// <item><description>Intelligence integration via <see cref="ConfigureIntelligence"/> and message bus</description></item>
/// <item><description>Knowledge and capability registration for AI discovery</description></item>
/// <item><description>Standard error handling patterns for ComputeResult creation</description></item>
/// </list>
/// </remarks>
internal abstract class ComputeRuntimeStrategyBase : IComputeRuntimeStrategy
{
    /// <summary>
    /// Gets the unique strategy identifier used for registry lookup.
    /// </summary>
    public abstract string StrategyId { get; }

    /// <summary>
    /// Gets the human-readable display name for this strategy.
    /// </summary>
    public abstract string StrategyName { get; }

    /// <inheritdoc/>
    public abstract ComputeRuntime Runtime { get; }

    /// <inheritdoc/>
    public abstract ComputeCapabilities Capabilities { get; }

    /// <inheritdoc/>
    public abstract IReadOnlyList<ComputeRuntime> SupportedRuntimes { get; }

    /// <inheritdoc/>
    public abstract Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default);

    /// <inheritdoc/>
    public virtual Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public virtual Task DisposeAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }

    #region CLI Process Runner

    /// <summary>
    /// Result of a CLI process execution containing exit code, stdout, and stderr.
    /// </summary>
    /// <param name="ExitCode">The process exit code (0 typically indicates success).</param>
    /// <param name="StandardOutput">Captured standard output text.</param>
    /// <param name="StandardError">Captured standard error text.</param>
    /// <param name="Elapsed">Wall-clock time the process ran.</param>
    protected record ProcessResult(int ExitCode, string StandardOutput, string StandardError, TimeSpan Elapsed);

    /// <summary>
    /// Runs an external CLI process and captures its output.
    /// </summary>
    /// <param name="executable">The executable path or command name.</param>
    /// <param name="arguments">Command-line arguments.</param>
    /// <param name="stdin">Optional standard input to pipe to the process.</param>
    /// <param name="workingDirectory">Optional working directory for the process.</param>
    /// <param name="environment">Optional environment variables to set.</param>
    /// <param name="timeout">Maximum execution time; defaults to 5 minutes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="ProcessResult"/> with captured output and exit code.</returns>
    /// <exception cref="TimeoutException">Thrown if the process exceeds the timeout.</exception>
    /// <exception cref="OperationCanceledException">Thrown if the operation is cancelled.</exception>
    protected async Task<ProcessResult> RunProcessAsync(
        string executable,
        string arguments,
        string? stdin = null,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveTimeout = timeout ?? TimeSpan.FromMinutes(5);
        var sw = Stopwatch.StartNew();

        var psi = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin != null,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (workingDirectory != null)
            psi.WorkingDirectory = workingDirectory;

        if (environment != null)
        {
            foreach (var (key, value) in environment)
                psi.Environment[key] = value;
        }

        using var process = new Process { StartInfo = psi };
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
                stdoutBuilder.AppendLine(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
                stderrBuilder.AppendLine(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (stdin != null)
        {
            await process.StandardInput.WriteAsync(stdin);
            process.StandardInput.Close();
        }

        using var timeoutCts = new CancellationTokenSource(effectiveTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        try
        {
            await process.WaitForExitAsync(linkedCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException($"Process '{executable}' exceeded timeout of {effectiveTimeout.TotalSeconds:F0}s");
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw;
        }

        sw.Stop();
        return new ProcessResult(process.ExitCode, stdoutBuilder.ToString(), stderrBuilder.ToString(), sw.Elapsed);
    }

    /// <summary>
    /// Checks whether a CLI tool is available on the system PATH.
    /// </summary>
    /// <param name="executable">The executable name to check.</param>
    /// <param name="versionFlag">The flag to invoke for version check (default: "--version").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>True if the tool is available and responds; false otherwise.</returns>
    protected async Task<bool> IsToolAvailableAsync(string executable, string versionFlag = "--version", CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await RunProcessAsync(executable, versionFlag, timeout: TimeSpan.FromSeconds(10), cancellationToken: cancellationToken);
            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    #endregion

    #region Execution Measurement

    /// <summary>
    /// Wraps an async execution function with Stopwatch timing, returning a ComputeResult with measured execution time.
    /// </summary>
    /// <param name="taskId">The compute task ID for result tracking.</param>
    /// <param name="executeFunc">The async function that performs the computation and returns output bytes.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A <see cref="ComputeResult"/> with timing information.</returns>
    protected async Task<ComputeResult> MeasureExecutionAsync(
        string taskId,
        Func<Task<(byte[] output, string? logs)>> executeFunc,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (output, logs) = await executeFunc();
            sw.Stop();

            return new ComputeResult(
                TaskId: taskId,
                Success: true,
                OutputData: output,
                ExecutionTime: sw.Elapsed,
                ExitCode: 0,
                Logs: logs,
                MemoryUsed: GC.GetTotalMemory(false)
            );
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return ComputeResult.CreateCancelled(taskId, sw.Elapsed);
        }
        catch (TimeoutException ex)
        {
            sw.Stop();
            return ComputeResult.CreateFailure(taskId, ex.Message, ex.StackTrace, sw.Elapsed, -3);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return ComputeResult.CreateFailure(taskId, ex.Message, ex.ToString(), sw.Elapsed, -1);
        }
    }

    #endregion

    #region Intelligence Integration

    /// <summary>
    /// Message bus reference for Intelligence communication.
    /// </summary>
    protected IMessageBus? MessageBus { get; private set; }

    /// <summary>
    /// Configures Intelligence integration for this strategy via the message bus.
    /// Called by the plugin orchestrator during startup.
    /// </summary>
    /// <param name="messageBus">The message bus instance, or null if Intelligence is unavailable.</param>
    public void ConfigureIntelligence(IMessageBus? messageBus)
    {
        MessageBus = messageBus;
    }

    /// <summary>
    /// Gets whether Intelligence is available for enhanced decision-making.
    /// </summary>
    protected bool IsIntelligenceAvailable => MessageBus != null;

    /// <summary>
    /// Gets static knowledge about this strategy for AI discovery.
    /// </summary>
    /// <returns>A <see cref="KnowledgeObject"/> describing this strategy's capabilities.</returns>
    public virtual KnowledgeObject GetStrategyKnowledge()
    {
        return new KnowledgeObject
        {
            Id = $"compute.strategy.{StrategyId}",
            Topic = "compute-runtime",
            SourcePluginId = "com.datawarehouse.compute.ultimate",
            SourcePluginName = StrategyName,
            KnowledgeType = "capability",
            Description = $"{StrategyName} compute runtime strategy",
            Payload = new Dictionary<string, object>
            {
                ["runtime"] = Runtime.ToString(),
                ["supportsStreaming"] = Capabilities.SupportsStreaming,
                ["supportsSandboxing"] = Capabilities.SupportsSandboxing,
                ["supportsMultiThreading"] = Capabilities.SupportsMultiThreading,
                ["memoryIsolation"] = Capabilities.MemoryIsolation.ToString()
            },
            Tags = ["compute", "runtime", Runtime.ToString().ToLowerInvariant(), StrategyId]
        };
    }

    /// <summary>
    /// Gets the registered capability descriptor for this strategy.
    /// </summary>
    /// <returns>A <see cref="RegisteredCapability"/> for capability registry integration.</returns>
    public virtual RegisteredCapability GetStrategyCapability()
    {
        return new RegisteredCapability
        {
            CapabilityId = $"compute.{StrategyId}",
            DisplayName = StrategyName,
            Description = $"{StrategyName} compute runtime strategy for {Runtime} execution",
            Category = SDK.Contracts.CapabilityCategory.Compute,
            PluginId = "com.datawarehouse.compute.ultimate",
            PluginName = "Ultimate Compute",
            PluginVersion = "1.0.0",
            Tags = ["compute", "runtime", Runtime.ToString().ToLowerInvariant()]
        };
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Validates that a compute task is not null and has required fields.
    /// </summary>
    /// <param name="task">The task to validate.</param>
    /// <exception cref="ArgumentNullException">Thrown if task is null.</exception>
    /// <exception cref="ArgumentException">Thrown if task.Id or task.Language is empty.</exception>
    protected static void ValidateTask(ComputeTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (string.IsNullOrWhiteSpace(task.Id))
            throw new ArgumentException("Task ID is required", nameof(task));
        if (string.IsNullOrWhiteSpace(task.Language))
            throw new ArgumentException("Task language is required", nameof(task));
    }

    /// <summary>
    /// Gets the effective timeout for a compute task, using the task's timeout or a default.
    /// </summary>
    /// <param name="task">The compute task.</param>
    /// <param name="defaultTimeout">Default timeout if not specified on the task.</param>
    /// <returns>The effective timeout duration.</returns>
    protected static TimeSpan GetEffectiveTimeout(ComputeTask task, TimeSpan? defaultTimeout = null)
    {
        return task.Timeout ?? task.ResourceLimits?.MaxExecutionTime ?? defaultTimeout ?? TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// Gets the maximum memory in bytes for a compute task.
    /// </summary>
    /// <param name="task">The compute task.</param>
    /// <param name="defaultBytes">Default memory limit if not specified.</param>
    /// <returns>Memory limit in bytes.</returns>
    protected static long GetMaxMemoryBytes(ComputeTask task, long defaultBytes = 256 * 1024 * 1024)
    {
        return task.ResourceLimits?.MaxMemoryBytes ?? defaultBytes;
    }

    /// <summary>
    /// Encodes output text as UTF-8 bytes for ComputeResult.
    /// </summary>
    /// <param name="text">The text to encode.</param>
    /// <returns>UTF-8 encoded bytes.</returns>
    protected static byte[] EncodeOutput(string text)
    {
        return Encoding.UTF8.GetBytes(text);
    }

    #endregion
}
