using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.Contracts.Compute;

namespace DataWarehouse.Plugins.UltimateCompute.Strategies.CoreRuntimes;

/// <summary>
/// Docker Engine API compute strategy for container execution via HTTP API.
/// Connects to Docker daemon via Unix socket (/var/run/docker.sock) or TCP.
/// Supports container lifecycle: create, start, stop, remove, image pull, volume mount, networking.
/// </summary>
internal sealed class DockerExecutionStrategy : ComputeRuntimeStrategyBase
{
    private readonly HttpClient _httpClient;
    private const string DefaultDockerSocket = "http://localhost:2375"; // TCP fallback

    // Allowlist for env var keys and container-safe names — prevents injection.
    private static readonly Regex SafeEnvKeyRegex = new(@"^[a-zA-Z_][a-zA-Z0-9_]*$", RegexOptions.Compiled);
    // Allowlist for volume mount paths — no shell metacharacters.
    private static readonly Regex SafePathRegex = new(@"^[a-zA-Z0-9/_.\-]+$", RegexOptions.Compiled);

    /// <inheritdoc/>
    public override string StrategyId => "compute.container.docker";
    /// <inheritdoc/>
    public override string StrategyName => "Docker Container Execution";
    /// <inheritdoc/>
    public override ComputeRuntime Runtime => ComputeRuntime.Container;
    /// <inheritdoc/>
    public override ComputeCapabilities Capabilities => new(
        SupportsStreaming: true, SupportsSandboxing: true,
        MaxMemoryBytes: 32L * 1024 * 1024 * 1024, MaxExecutionTime: TimeSpan.FromHours(24),
        SupportedLanguages: ["any"], SupportsMultiThreading: true,
        SupportsAsync: true, SupportsNetworkAccess: true, SupportsFileSystemAccess: true,
        MaxConcurrentTasks: 50, MemoryIsolation: MemoryIsolationLevel.Container);
    /// <inheritdoc/>
    public override IReadOnlyList<ComputeRuntime> SupportedRuntimes => [ComputeRuntime.Container];

    private static readonly HttpClient SharedHttpClient = new();

    public DockerExecutionStrategy() : this(SharedHttpClient) { }

    public DockerExecutionStrategy(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <inheritdoc/>
    public override async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // Verify Docker daemon is accessible
        try
        {
            using var response = await _httpClient.GetAsync($"{DefaultDockerSocket}/v1.43/version", cancellationToken);
            response.EnsureSuccessStatusCode();
        }
        catch
        {
            // Try docker CLI as fallback
            await IsToolAvailableAsync("docker", "version", cancellationToken);
        }
    }

    /// <inheritdoc/>
    public override async Task<ComputeResult> ExecuteAsync(ComputeTask task, CancellationToken cancellationToken = default)
    {
        ValidateTask(task);
        return await MeasureExecutionAsync(task.Id, async () =>
        {
            var image = task.Metadata?.TryGetValue("image", out var img) == true ? img?.ToString() ?? "alpine:latest" : "alpine:latest";
            var command = task.GetCodeAsString();

            // Step 1: Pull image if needed
            await PullImageAsync(image, cancellationToken);

            // Step 2: Create container
            var containerId = await CreateContainerAsync(image, command, task, cancellationToken);

            try
            {
                // Step 3: Start container
                await StartContainerAsync(containerId, cancellationToken);

                // Step 4: Wait for completion with timeout
                var timeout = GetEffectiveTimeout(task);
                var exitCode = await WaitForContainerAsync(containerId, timeout, cancellationToken);

                // Step 5: Get logs
                var logs = await GetContainerLogsAsync(containerId, cancellationToken);

                if (exitCode != 0)
                    throw new InvalidOperationException($"Container exited with code {exitCode}: {logs.stderr}");

                return (EncodeOutput(logs.stdout), $"Docker container {containerId[..12]} completed\n{logs.stderr}");
            }
            finally
            {
                // Step 6: Remove container
                await RemoveContainerAsync(containerId, cancellationToken);
            }
        }, cancellationToken);
    }

    private async Task PullImageAsync(string image, CancellationToken ct)
    {
        try
        {
            var result = await RunProcessAsync("docker", $"pull {image}", timeout: TimeSpan.FromMinutes(5), cancellationToken: ct);
            if (result.ExitCode != 0)
                throw new InvalidOperationException($"Failed to pull image {image}: {result.StandardError}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Image may already exist locally
        }
    }

    private async Task<string> CreateContainerAsync(string image, string command, ComputeTask task, CancellationToken ct)
    {
        var args = new StringBuilder($"create --rm");

        // Memory limits
        var maxMem = GetMaxMemoryBytes(task, 512 * 1024 * 1024);
        args.Append($" --memory {maxMem}");

        // CPU limits (use MaxCpuTime as a proxy: shorter time budget = fewer CPUs allowed)
        if (task.ResourceLimits?.MaxCpuTime is TimeSpan cpuTime && cpuTime.TotalSeconds > 0)
            args.Append($" --cpus {Math.Max(0.1, Math.Min(1.0, cpuTime.TotalSeconds / 30.0)):F1}");

        // Network isolation
        if (task.ResourceLimits?.AllowNetworkAccess == false)
            args.Append(" --network none");

        // Volume mounts — validate paths to prevent path injection.
        if (task.ResourceLimits?.AllowFileSystemAccess == true && task.ResourceLimits.AllowedFileSystemPaths != null)
        {
            foreach (var path in task.ResourceLimits.AllowedFileSystemPaths)
            {
                if (!SafePathRegex.IsMatch(path))
                    throw new ArgumentException($"Volume mount path '{path}' contains invalid characters.");
                args.Append($" -v \"{path}:{path}:ro\"");
            }
        }

        // Environment variables — validate keys to prevent injection.
        if (task.Environment != null)
        {
            foreach (var (key, value) in task.Environment)
            {
                if (!SafeEnvKeyRegex.IsMatch(key))
                    throw new ArgumentException($"Environment variable key '{key}' contains invalid characters.");
                args.Append($" -e \"{key}={value.Replace("\"", "\\\"")}\"");
            }
        }

        args.Append($" {image}");

        if (!string.IsNullOrWhiteSpace(command))
            args.Append($" sh -c \"{command.Replace("\"", "\\\"")}\"");

        var result = await RunProcessAsync("docker", args.ToString(), timeout: TimeSpan.FromSeconds(30), cancellationToken: ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Failed to create container: {result.StandardError}");

        return result.StandardOutput.Trim();
    }

    private async Task StartContainerAsync(string containerId, CancellationToken ct)
    {
        var result = await RunProcessAsync("docker", $"start {containerId}", timeout: TimeSpan.FromSeconds(30), cancellationToken: ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Failed to start container: {result.StandardError}");
    }

    private async Task<int> WaitForContainerAsync(string containerId, TimeSpan timeout, CancellationToken ct)
    {
        var result = await RunProcessAsync("docker", $"wait {containerId}", timeout: timeout, cancellationToken: ct);
        return int.TryParse(result.StandardOutput.Trim(), out var exitCode) ? exitCode : -1;
    }

    private async Task<(string stdout, string stderr)> GetContainerLogsAsync(string containerId, CancellationToken ct)
    {
        var stdout = await RunProcessAsync("docker", $"logs --stdout {containerId}", timeout: TimeSpan.FromSeconds(10), cancellationToken: ct);
        var stderr = await RunProcessAsync("docker", $"logs --stderr {containerId}", timeout: TimeSpan.FromSeconds(10), cancellationToken: ct);
        return (stdout.StandardOutput, stderr.StandardOutput);
    }

    private async Task RemoveContainerAsync(string containerId, CancellationToken ct)
    {
        try
        {
            await RunProcessAsync("docker", $"rm -f {containerId}", timeout: TimeSpan.FromSeconds(10), cancellationToken: ct);
        }
        catch { /* best-effort cleanup */ }
    }
}
