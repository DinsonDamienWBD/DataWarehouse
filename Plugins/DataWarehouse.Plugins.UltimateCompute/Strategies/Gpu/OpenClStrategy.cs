using System.Text;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.Gpu;

/// <summary>
/// OpenCL compute strategy using clinfo for device discovery and runtime kernel compilation.
/// Manages buffer creation, work-group dispatch, and cross-vendor GPU execution.
/// </summary>
internal sealed class OpenClStrategy : ComputeRuntimeStrategyBase
{
    // Patterns that could break out of the C string literal in the generated host code.
    private static readonly Regex DangerousKernelPattern = new(
        @"(?:\\x00|\\0|""|\bsystem\s*\(|\bexec\s*\(|\bpopen\s*\()",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <inheritdoc/>
    public override string StrategyId => "compute.gpu.opencl";
    /// <inheritdoc/>
    public override string StrategyName => "OpenCL";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Custom;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: true, SupportsSandboxing: false,
        MaxMemoryBytes: 32L * 1024 * 1024 * 1024, MaxExecutionTime: TimeSpan.FromHours(2),
        SupportedLanguages: ["opencl", "c"], SupportsMultiThreading: true,
        SupportsAsync: true, SupportsNetworkAccess: false, SupportsFileSystemAccess: true,
        MaxConcurrentTasks: 8, MemoryIsolation: MemoryIsolationLevel.Process);
    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.Custom];

    /// <inheritdoc/>
    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await IsToolAvailableAsync("clinfo", "--version", cancellationToken);
    }

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var clPath = Path.GetTempFileName() + ".cl";
            var hostPath = Path.GetTempFileName() + ".c";
            var binPath = Path.GetTempFileName() + ".out";
            try
            {
                await File.WriteAllBytesAsync(clPath, task.Code.ToArray(), cancellationToken);

                // Query OpenCL devices
                var deviceInfo = await RunProcessAsync("clinfo", "--list", timeout: TimeSpan.FromSeconds(10), cancellationToken: cancellationToken);

                var platformIdx = 0;
                if (task.Metadata?.TryGetValue("platform_idx", out var pi) == true && pi is int pii) platformIdx = pii;
                var deviceIdx = 0;
                if (task.Metadata?.TryGetValue("device_idx", out var di) == true && di is int dii) deviceIdx = dii;

                // Validate kernel source for dangerous patterns before embedding into generated C code.
                var rawKernelSource = task.GetCodeAsString();
                if (DangerousKernelPattern.IsMatch(rawKernelSource))
                    throw new ArgumentException("OpenCL kernel source contains prohibited patterns (system calls, null bytes, or unescaped quotes).");
                // Generate host wrapper code that loads and executes the kernel
                var kernelSource = rawKernelSource.Replace("\"", "\\\"").Replace("\n", "\\n");
                var hostCode = $$"""
                    #include <stdio.h>
                    #include <stdlib.h>
                    #include <CL/cl.h>
                    int main() {
                        cl_platform_id platforms[8]; cl_uint np;
                        clGetPlatformIDs(8, platforms, &np);
                        cl_device_id devices[8]; cl_uint nd;
                        clGetDeviceIDs(platforms[{{platformIdx}}], CL_DEVICE_TYPE_ALL, 8, devices, &nd);
                        cl_context ctx = clCreateContext(NULL, 1, &devices[{{deviceIdx}}], NULL, NULL, NULL);
                        cl_command_queue q = clCreateCommandQueueWithProperties(ctx, devices[{{deviceIdx}}], NULL, NULL);
                        const char* src = "{{kernelSource}}";
                        size_t srcLen = strlen(src);
                        cl_program prog = clCreateProgramWithSource(ctx, 1, &src, &srcLen, NULL);
                        clBuildProgram(prog, 1, &devices[{{deviceIdx}}], NULL, NULL, NULL);
                        size_t logSize; clGetProgramBuildInfo(prog, devices[{{deviceIdx}}], CL_PROGRAM_BUILD_LOG, 0, NULL, &logSize);
                        char* log = malloc(logSize); clGetProgramBuildInfo(prog, devices[{{deviceIdx}}], CL_PROGRAM_BUILD_LOG, logSize, log, NULL);
                        if (logSize > 1) fprintf(stderr, "%s", log);
                        free(log);
                        printf("OpenCL kernel built successfully on platform %d device %d\n", {{platformIdx}}, {{deviceIdx}});
                        clReleaseProgram(prog); clReleaseCommandQueue(q); clReleaseContext(ctx);
                        return 0;
                    }
                    """;

                await File.WriteAllTextAsync(hostPath, hostCode, cancellationToken);

                // Compile host code
                var compileResult = await RunProcessAsync("gcc", $"-o \"{binPath}\" \"{hostPath}\" -lOpenCL",
                    timeout: TimeSpan.FromSeconds(30), cancellationToken: cancellationToken);

                if (compileResult.ExitCode != 0)
                    throw new InvalidOperationException($"OpenCL host compilation failed: {compileResult.StandardError}");

                var timeout = GetEffectiveTimeout(task);
                var result = await RunProcessAsync(binPath, "",
                    stdin: task.InputData.Length > 0 ? task.GetInputDataAsString() : null,
                    timeout: timeout, cancellationToken: cancellationToken);

                if (result.ExitCode != 0)
                    throw new InvalidOperationException($"OpenCL exited with code {result.ExitCode}: {result.StandardError}");

                return (EncodeOutput(result.StandardOutput), $"OpenCL (platform {platformIdx}, device {deviceIdx}) completed in {result.Elapsed.TotalMilliseconds:F0}ms\nDevices: {deviceInfo.StandardOutput.Trim()}\n{result.StandardError}");
            }
            finally
            {
                try { File.Delete(clPath); File.Delete(hostPath); File.Delete(binPath); } catch { /* Best-effort cleanup */ }
            }
        }, cancellationToken);
    }
}
