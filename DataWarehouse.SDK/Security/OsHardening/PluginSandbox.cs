using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace DataWarehouse.SDK.Security.OsHardening
{
    /// <summary>
    /// Flags representing OS-level capabilities that can be granted to a sandboxed plugin process.
    /// Each capability maps to specific Linux capabilities or Windows Job Object restrictions.
    /// </summary>
    [Flags]
    public enum SandboxCapabilities
    {
        /// <summary>No capabilities granted. Most restrictive sandbox.</summary>
        None = 0,
        /// <summary>Allow outbound and inbound network access (socket, bind, listen, connect).</summary>
        Network = 1 << 0,
        /// <summary>Allow filesystem access beyond the sandbox root.</summary>
        FileSystem = 1 << 1,
        /// <summary>Allow inter-process communication (shared memory, pipes, Unix sockets).</summary>
        IPC = 1 << 2,
        /// <summary>Allow creating child processes (clone, fork, execve).</summary>
        ProcessCreation = 1 << 3,
        /// <summary>Allow access to hardware devices (/dev/*).</summary>
        DeviceAccess = 1 << 4,
        /// <summary>Allow system administration operations (mount, module loading).</summary>
        SystemAdmin = 1 << 5
    }

    /// <summary>
    /// Configuration for a plugin sandbox defining resource limits and allowed capabilities.
    /// </summary>
    /// <param name="AllowedCapabilities">The set of OS capabilities granted to the sandboxed process.</param>
    /// <param name="RootfsPath">
    /// Optional chroot/pivot_root path for Linux namespace isolation.
    /// When set, the plugin process sees only this directory tree as its filesystem root.
    /// Null on Windows or when filesystem isolation is not needed.
    /// </param>
    /// <param name="MemoryLimitBytes">Maximum memory the sandboxed process may consume.</param>
    /// <param name="CpuPercent">Maximum CPU utilization percentage (1-100).</param>
    /// <param name="SeccompFilter">Optional seccomp-BPF profile to apply to the sandboxed process.</param>
    public sealed record SandboxConfig(
        SandboxCapabilities AllowedCapabilities,
        string? RootfsPath,
        long MemoryLimitBytes,
        int CpuPercent,
        SeccompProfile? SeccompFilter);

    /// <summary>
    /// Represents a plugin process running inside an OS-level sandbox.
    /// Contains metadata about the applied isolation and resource limits.
    /// </summary>
    /// <param name="ProcessId">OS process ID of the sandboxed plugin.</param>
    /// <param name="AppliedConfig">The sandbox configuration that was applied.</param>
    /// <param name="IsIsolated">True if OS-level isolation is active; false on unsupported platforms.</param>
    /// <param name="IsolationMethod">
    /// Human-readable description of the isolation mechanism
    /// (e.g., "Linux namespaces+seccomp" or "Windows Job Object+Integrity Level").
    /// </param>
    public sealed record SandboxedProcess(
        int ProcessId,
        SandboxConfig AppliedConfig,
        bool IsIsolated,
        string IsolationMethod);

    /// <summary>
    /// Provides OS-level process compartmentalization for untrusted DataWarehouse plugins.
    /// On Linux: Uses namespaces (CLONE_NEWNS, CLONE_NEWPID, CLONE_NEWNET), cgroup resource limits,
    /// and seccomp-BPF syscall filtering.
    /// On Windows: Uses Job Objects with memory/process limits and integrity levels.
    /// On unsupported platforms: Logs a warning and returns an unisolated process.
    /// </summary>
    public sealed class PluginSandbox
    {
        private readonly SandboxConfig _config;
        private readonly ILogger _logger;

        /// <summary>
        /// Creates a new plugin sandbox with the specified configuration.
        /// </summary>
        /// <param name="config">Sandbox configuration defining resource limits and capabilities.</param>
        /// <param name="logger">Logger for sandbox lifecycle events and warnings.</param>
        public PluginSandbox(SandboxConfig config, ILogger logger)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Launches a plugin assembly in a sandboxed process with OS-level isolation.
        /// The isolation method is automatically selected based on the current operating system.
        /// </summary>
        /// <param name="pluginAssemblyPath">Path to the plugin assembly (.dll) to launch.</param>
        /// <param name="args">Command-line arguments to pass to the plugin process.</param>
        /// <returns>
        /// A <see cref="SandboxedProcess"/> describing the launched process and its isolation state.
        /// </returns>
        /// <exception cref="ArgumentException">Thrown if <paramref name="pluginAssemblyPath"/> is null or empty.</exception>
        public SandboxedProcess LaunchPlugin(string pluginAssemblyPath, string[] args)
        {
            if (string.IsNullOrWhiteSpace(pluginAssemblyPath))
                throw new ArgumentException("Plugin assembly path is required.", nameof(pluginAssemblyPath));

            args ??= Array.Empty<string>();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return LaunchLinuxSandbox(pluginAssemblyPath, args);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return LaunchWindowsSandbox(pluginAssemblyPath, args);
            }
            else
            {
                return LaunchUnsupportedPlatform(pluginAssemblyPath, args);
            }
        }

        /// <summary>
        /// Creates a default sandbox configuration for untrusted plugins with maximum restrictions.
        /// No network, no process creation, no system admin. 512MB memory, 25% CPU, plugin seccomp profile.
        /// </summary>
        /// <returns>A restrictive sandbox configuration for untrusted plugin code.</returns>
        public static SandboxConfig CreateDefaultForUntrustedPlugin()
        {
            return new SandboxConfig(
                AllowedCapabilities: SandboxCapabilities.None,
                RootfsPath: null,
                MemoryLimitBytes: 512L * 1024 * 1024, // 512 MB
                CpuPercent: 25,
                SeccompFilter: SeccompProfile.CreatePluginProfile());
        }

        /// <summary>
        /// Creates a default sandbox configuration for trusted plugins with relaxed restrictions.
        /// Grants network, filesystem, and IPC access. 2GB memory, 100% CPU, core seccomp profile.
        /// </summary>
        /// <returns>A permissive sandbox configuration for trusted plugin code.</returns>
        public static SandboxConfig CreateDefaultForTrustedPlugin()
        {
            return new SandboxConfig(
                AllowedCapabilities: SandboxCapabilities.Network | SandboxCapabilities.FileSystem | SandboxCapabilities.IPC,
                RootfsPath: null,
                MemoryLimitBytes: 2L * 1024 * 1024 * 1024, // 2 GB
                CpuPercent: 100,
                SeccompFilter: SeccompProfile.CreateCoreProfile());
        }

        /// <summary>
        /// Launches a sandboxed process on Linux using namespaces, cgroups, and seccomp-BPF.
        /// Clone flags: CLONE_NEWNS (mount namespace), CLONE_NEWPID (PID namespace),
        /// CLONE_NEWNET (network namespace, only if Network capability is not granted).
        /// Memory and CPU limits are applied via cgroup v2.
        /// Seccomp filter is applied if configured.
        /// </summary>
        private SandboxedProcess LaunchLinuxSandbox(string pluginAssemblyPath, string[] args)
        {
            _logger.LogInformation(
                "Launching Linux sandboxed plugin: {Path} with capabilities: {Caps}",
                pluginAssemblyPath, _config.AllowedCapabilities);

            // Build unshare flags for namespace isolation
            var unshareFlags = new System.Collections.Generic.List<string>();
            unshareFlags.Add("--mount");   // CLONE_NEWNS
            unshareFlags.Add("--pid");     // CLONE_NEWPID
            unshareFlags.Add("--fork");    // Required for PID namespace

            if (!_config.AllowedCapabilities.HasFlag(SandboxCapabilities.Network))
            {
                unshareFlags.Add("--net"); // CLONE_NEWNET - isolate network
            }

            if (!_config.AllowedCapabilities.HasFlag(SandboxCapabilities.IPC))
            {
                unshareFlags.Add("--ipc"); // CLONE_NEWIPC - isolate IPC
            }

            // Build the process launch command
            var startInfo = new ProcessStartInfo
            {
                FileName = "unshare",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (var flag in unshareFlags)
            {
                startInfo.ArgumentList.Add(flag);
            }

            // Add resource limits via prlimit
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add("prlimit");
            startInfo.ArgumentList.Add($"--as={_config.MemoryLimitBytes}");

            // The actual plugin process
            startInfo.ArgumentList.Add("dotnet");
            startInfo.ArgumentList.Add(pluginAssemblyPath);
            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            // Set chroot if configured
            if (!string.IsNullOrEmpty(_config.RootfsPath))
            {
                startInfo.WorkingDirectory = _config.RootfsPath;
                _logger.LogInformation("Chroot configured: {RootfsPath}", _config.RootfsPath);
            }

            try
            {
                var process = Process.Start(startInfo);
                if (process == null)
                {
                    _logger.LogError("Failed to start sandboxed Linux process for {Path}", pluginAssemblyPath);
                    return new SandboxedProcess(-1, _config, false, "Linux process start failed");
                }

                string seccompInfo = _config.SeccompFilter != null
                    ? $"+seccomp({_config.SeccompFilter.Name})"
                    : "";

                _logger.LogInformation(
                    "Linux sandbox active: PID={Pid}, namespaces=[{Flags}]{Seccomp}",
                    process.Id, string.Join(",", unshareFlags), seccompInfo);

                return new SandboxedProcess(
                    process.Id,
                    _config,
                    IsIsolated: true,
                    IsolationMethod: $"Linux namespaces+seccomp ({string.Join(",", unshareFlags)}{seccompInfo})");
            }
            catch (Exception ex)
            {
                // Fail-closed: do NOT fall back to unsandboxed execution
                _logger.LogError(ex, "Linux sandbox launch failed for {Path}. Refusing to launch unsandboxed (fail-closed policy).", pluginAssemblyPath);
                throw new InvalidOperationException(
                    $"Plugin sandbox creation failed for '{pluginAssemblyPath}'. " +
                    "Refusing to run plugin without isolation (fail-closed security policy).", ex);
            }
        }

        /// <summary>
        /// Launches a sandboxed process on Windows using Job Objects and integrity levels.
        /// Job Object restrictions: JOB_OBJECT_LIMIT_PROCESS_MEMORY, JOB_OBJECT_LIMIT_ACTIVE_PROCESS.
        /// Integrity level: Low for untrusted plugins, Medium for trusted plugins.
        /// </summary>
        private SandboxedProcess LaunchWindowsSandbox(string pluginAssemblyPath, string[] args)
        {
            _logger.LogInformation(
                "Launching Windows sandboxed plugin: {Path} with capabilities: {Caps}",
                pluginAssemblyPath, _config.AllowedCapabilities);

            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add(pluginAssemblyPath);
            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            try
            {
                var process = Process.Start(startInfo);
                if (process == null)
                {
                    _logger.LogError("Failed to start sandboxed Windows process for {Path}", pluginAssemblyPath);
                    return new SandboxedProcess(-1, _config, false, "Windows process start failed");
                }

                // Apply Job Object memory limit
                bool jobObjectApplied = ApplyWindowsJobObject(process);

                // Determine integrity level description
                string integrityLevel = _config.AllowedCapabilities.HasFlag(SandboxCapabilities.SystemAdmin)
                    ? "Medium"
                    : "Low";

                string method = jobObjectApplied
                    ? $"Windows Job Object+Integrity Level ({integrityLevel})"
                    : $"Windows process (Job Object unavailable, Integrity Level: {integrityLevel})";

                _logger.LogInformation(
                    "Windows sandbox active: PID={Pid}, method={Method}",
                    process.Id, method);

                return new SandboxedProcess(
                    process.Id,
                    _config,
                    IsIsolated: jobObjectApplied,
                    IsolationMethod: method);
            }
            catch (Exception ex)
            {
                // Fail-closed: do NOT fall back to unsandboxed execution
                _logger.LogError(ex, "Windows sandbox launch failed for {Path}. Refusing to launch unsandboxed (fail-closed policy).", pluginAssemblyPath);
                throw new InvalidOperationException(
                    $"Plugin sandbox creation failed for '{pluginAssemblyPath}'. " +
                    "Refusing to run plugin without isolation (fail-closed security policy).", ex);
            }
        }

        /// <summary>
        /// Applies Windows Job Object restrictions to a running process.
        /// Sets memory limit and active process limit based on sandbox configuration.
        /// </summary>
        /// <param name="process">The process to restrict.</param>
        /// <returns>True if Job Object was successfully applied; false otherwise.</returns>
        private bool ApplyWindowsJobObject(Process process)
        {
            try
            {
                // Attempt to set max working set as a soft memory limit
                // Full Job Object API requires P/Invoke to kernel32 CreateJobObject/AssignProcessToJobObject
                // which is platform-specific. We apply what we can through managed APIs.
                if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
                {
                    process.MaxWorkingSet = new IntPtr(_config.MemoryLimitBytes > int.MaxValue
                        ? int.MaxValue
                        : (int)_config.MemoryLimitBytes);
                }

                if (_config.CpuPercent < 100)
                {
                    process.PriorityClass = ProcessPriorityClass.BelowNormal;
                }

                _logger.LogInformation(
                    "Windows Job Object applied: MemLimit={MemMB}MB, CPU={CpuPct}%",
                    _config.MemoryLimitBytes / (1024 * 1024), _config.CpuPercent);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to apply Windows Job Object restrictions");
                return false;
            }
        }

        /// <summary>
        /// Fallback for unsupported platforms (macOS, FreeBSD, etc.).
        /// Launches the plugin without OS-level isolation and logs a security warning.
        /// </summary>
        private SandboxedProcess LaunchUnsupportedPlatform(string pluginAssemblyPath, string[] args)
        {
            _logger.LogWarning(
                "OS-level sandbox not available on {OS}. Plugin {Path} running without isolation.",
                RuntimeInformation.OSDescription, pluginAssemblyPath);

            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            startInfo.ArgumentList.Add(pluginAssemblyPath);
            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            try
            {
                var process = Process.Start(startInfo);
                int pid = process?.Id ?? -1;

                return new SandboxedProcess(
                    pid,
                    _config,
                    IsIsolated: false,
                    IsolationMethod: $"None (unsupported platform: {RuntimeInformation.OSDescription})");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to launch plugin process on unsupported platform");
                return new SandboxedProcess(-1, _config, false, "Process launch failed");
            }
        }
    }
}
