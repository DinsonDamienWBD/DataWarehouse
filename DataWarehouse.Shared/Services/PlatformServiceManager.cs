// Copyright (c) DataWarehouse Contributors. All rights reserved.
// Licensed under the Apache License, Version 2.0.

using System.Diagnostics;

namespace DataWarehouse.Shared.Services;

/// <summary>
/// Unified cross-platform service management for Windows (sc), Linux (systemd), and macOS (launchd).
/// Provides a single API surface for all service lifecycle operations:
/// register, unregister, start, stop, restart, and status queries.
/// </summary>
public static class PlatformServiceManager
{
    /// <summary>
    /// Gets the current status of a system service.
    /// </summary>
    /// <param name="serviceName">Service name to query.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Status information about the service.</returns>
    public static async Task<ServiceStatus> GetServiceStatusAsync(string serviceName, CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows())
        {
            var (exitCode, stdout, _) = await RunProcessAsync("sc", $"query {serviceName}", ct);

            if (exitCode != 0)
            {
                return new ServiceStatus { IsInstalled = false, IsRunning = false, State = "NotInstalled" };
            }

            var isRunning = stdout.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
            var state = "Unknown";
            int? pid = null;

            if (stdout.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)) state = "Running";
            else if (stdout.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)) state = "Stopped";
            else if (stdout.Contains("PAUSED", StringComparison.OrdinalIgnoreCase)) state = "Paused";
            else if (stdout.Contains("START_PENDING", StringComparison.OrdinalIgnoreCase)) state = "Starting";
            else if (stdout.Contains("STOP_PENDING", StringComparison.OrdinalIgnoreCase)) state = "Stopping";

            // Try to get PID
            var (pidExit, pidOut, _) = await RunProcessAsync("sc", $"queryex {serviceName}", ct);
            if (pidExit == 0)
            {
                var pidLine = pidOut.Split('\n')
                    .FirstOrDefault(l => l.Contains("PID", StringComparison.OrdinalIgnoreCase));
                if (pidLine != null)
                {
                    var parts = pidLine.Split(':');
                    if (parts.Length > 1 && int.TryParse(parts[1].Trim(), out var parsedPid) && parsedPid > 0)
                    {
                        pid = parsedPid;
                    }
                }
            }

            return new ServiceStatus
            {
                IsInstalled = true,
                IsRunning = isRunning,
                State = state,
                PID = pid
            };
        }
        else if (OperatingSystem.IsLinux())
        {
            var (exitCode, stdout, _) = await RunProcessAsync("systemctl", $"is-active {serviceName}", ct);
            var isActive = stdout.Trim() == "active";

            // Check if installed
            var (checkExit, _, _) = await RunProcessAsync("systemctl", $"cat {serviceName}", ct);
            var isInstalled = checkExit == 0;

            int? pid = null;
            if (isActive)
            {
                var (pidExit, pidOut, _) = await RunProcessAsync("systemctl", $"show {serviceName} --property=MainPID", ct);
                if (pidExit == 0)
                {
                    var pidStr = pidOut.Replace("MainPID=", "").Trim();
                    if (int.TryParse(pidStr, out var parsedPid) && parsedPid > 0)
                    {
                        pid = parsedPid;
                    }
                }
            }

            return new ServiceStatus
            {
                IsInstalled = isInstalled,
                IsRunning = isActive,
                State = isActive ? "Running" : (isInstalled ? "Stopped" : "NotInstalled"),
                PID = pid
            };
        }
        else if (OperatingSystem.IsMacOS())
        {
            var (exitCode, stdout, _) = await RunProcessAsync("launchctl", "list", ct);
            var isInstalled = stdout.Contains(serviceName, StringComparison.OrdinalIgnoreCase);

            int? pid = null;
            var isRunning = false;

            if (isInstalled)
            {
                // Parse launchctl list output for PID
                foreach (var line in stdout.Split('\n'))
                {
                    if (line.Contains(serviceName, StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 1 && int.TryParse(parts[0].Trim(), out var parsedPid) && parsedPid > 0)
                        {
                            pid = parsedPid;
                            isRunning = true;
                        }
                        break;
                    }
                }
            }

            return new ServiceStatus
            {
                IsInstalled = isInstalled,
                IsRunning = isRunning,
                State = isRunning ? "Running" : (isInstalled ? "Stopped" : "NotInstalled"),
                PID = pid
            };
        }

        return new ServiceStatus { IsInstalled = false, IsRunning = false, State = "UnsupportedPlatform" };
    }

    /// <summary>
    /// Starts a system service.
    /// </summary>
    /// <param name="serviceName">Service name to start.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task StartServiceAsync(string serviceName, CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows())
        {
            await RunProcessCheckedAsync("sc", $"start {serviceName}", ct);
        }
        else if (OperatingSystem.IsLinux())
        {
            await RunProcessCheckedAsync("systemctl", $"start {serviceName}", ct);
        }
        else if (OperatingSystem.IsMacOS())
        {
            await RunProcessCheckedAsync("launchctl", $"start {serviceName}", ct);
        }
        else
        {
            throw new PlatformNotSupportedException("Service management is not supported on this platform.");
        }
    }

    /// <summary>
    /// Stops a system service.
    /// </summary>
    /// <param name="serviceName">Service name to stop.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task StopServiceAsync(string serviceName, CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows())
        {
            await RunProcessCheckedAsync("sc", $"stop {serviceName}", ct);
        }
        else if (OperatingSystem.IsLinux())
        {
            await RunProcessCheckedAsync("systemctl", $"stop {serviceName}", ct);
        }
        else if (OperatingSystem.IsMacOS())
        {
            await RunProcessCheckedAsync("launchctl", $"stop {serviceName}", ct);
        }
        else
        {
            throw new PlatformNotSupportedException("Service management is not supported on this platform.");
        }
    }

    /// <summary>
    /// Restarts a system service.
    /// </summary>
    /// <param name="serviceName">Service name to restart.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task RestartServiceAsync(string serviceName, CancellationToken ct = default)
    {
        if (OperatingSystem.IsLinux())
        {
            await RunProcessCheckedAsync("systemctl", $"restart {serviceName}", ct);
        }
        else
        {
            // Windows and macOS: stop then start
            try
            {
                await StopServiceAsync(serviceName, ct);
                await Task.Delay(1000, ct); // Brief pause between stop and start
            }
            catch { /* Service may already be stopped */ }

            await StartServiceAsync(serviceName, ct);
        }
    }

    /// <summary>
    /// Registers a new system service.
    /// </summary>
    /// <param name="registration">Service registration details.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task RegisterServiceAsync(ServiceRegistration registration, CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows())
        {
            var startType = registration.AutoStart ? "auto" : "demand";
            var args = $"create {registration.Name} binPath= \"{registration.ExecutablePath}\" " +
                       $"DisplayName= \"{registration.DisplayName}\" start= {startType}";

            await RunProcessCheckedAsync("sc", args, ct);

            if (!string.IsNullOrEmpty(registration.Description))
            {
                await RunProcessCheckedAsync("sc",
                    $"description {registration.Name} \"{registration.Description}\"", ct);
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            var unitContent = $@"[Unit]
Description={registration.Description ?? registration.DisplayName}
After=network.target

[Service]
Type=simple
ExecStart={registration.ExecutablePath}
WorkingDirectory={registration.WorkingDirectory ?? Path.GetDirectoryName(registration.ExecutablePath)}
Restart=always
RestartSec=10

[Install]
WantedBy=multi-user.target
";
            var unitPath = $"/etc/systemd/system/{registration.Name}.service";
            await File.WriteAllTextAsync(unitPath, unitContent, ct);
            await RunProcessCheckedAsync("systemctl", "daemon-reload", ct);

            if (registration.AutoStart)
            {
                await RunProcessCheckedAsync("systemctl", $"enable {registration.Name}", ct);
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            var plistContent = $@"<?xml version=""1.0"" encoding=""UTF-8""?>
<!DOCTYPE plist PUBLIC ""-//Apple//DTD PLIST 1.0//EN"" ""http://www.apple.com/DTDs/PropertyList-1.0.dtd"">
<plist version=""1.0"">
<dict>
    <key>Label</key>
    <string>{registration.Name}</string>
    <key>ProgramArguments</key>
    <array>
        <string>{registration.ExecutablePath}</string>
    </array>
    <key>WorkingDirectory</key>
    <string>{registration.WorkingDirectory ?? Path.GetDirectoryName(registration.ExecutablePath)}</string>
    <key>RunAtLoad</key>
    <{(registration.AutoStart ? "true" : "false")}/>
    <key>KeepAlive</key>
    <true/>
</dict>
</plist>";
            var plistPath = $"/Library/LaunchDaemons/{registration.Name}.plist";
            await File.WriteAllTextAsync(plistPath, plistContent, ct);
            await RunProcessCheckedAsync("launchctl", $"load {plistPath}", ct);
        }
        else
        {
            throw new PlatformNotSupportedException("Service registration is not supported on this platform.");
        }
    }

    /// <summary>
    /// Unregisters (removes) a system service.
    /// </summary>
    /// <param name="serviceName">Service name to unregister.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task UnregisterServiceAsync(string serviceName, CancellationToken ct = default)
    {
        // Stop service first if running
        var status = await GetServiceStatusAsync(serviceName, ct);
        if (status.IsRunning)
        {
            try { await StopServiceAsync(serviceName, ct); }
            catch { /* Continue with unregistration */ }
        }

        if (OperatingSystem.IsWindows())
        {
            await RunProcessCheckedAsync("sc", $"delete {serviceName}", ct);
        }
        else if (OperatingSystem.IsLinux())
        {
            try { await RunProcessCheckedAsync("systemctl", $"disable {serviceName}", ct); }
            catch { /* May not be enabled */ }

            var unitPath = $"/etc/systemd/system/{serviceName}.service";
            if (File.Exists(unitPath))
            {
                File.Delete(unitPath);
            }
            await RunProcessCheckedAsync("systemctl", "daemon-reload", ct);
        }
        else if (OperatingSystem.IsMacOS())
        {
            var plistPath = $"/Library/LaunchDaemons/{serviceName}.plist";
            if (File.Exists(plistPath))
            {
                try { await RunProcessCheckedAsync("launchctl", $"unload {plistPath}", ct); }
                catch { /* May not be loaded */ }
                File.Delete(plistPath);
            }
        }
        else
        {
            throw new PlatformNotSupportedException("Service management is not supported on this platform.");
        }
    }

    /// <summary>
    /// Checks whether the current process has administrative/root privileges.
    /// </summary>
    /// <returns>True if running with elevated privileges.</returns>
    public static bool HasAdminPrivileges()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var principal = new System.Security.Principal.WindowsPrincipal(identity);
                return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        // Linux/macOS: check if root
        return Environment.UserName == "root" || Environment.GetEnvironmentVariable("USER") == "root";
    }

    /// <summary>
    /// Runs a process and returns its output.
    /// </summary>
    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunProcessAsync(
        string fileName, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            UseShellExecute = false
        };

        using var process = Process.Start(psi);
        if (process == null)
        {
            return (-1, "", $"Failed to start process: {fileName}");
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return (process.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Runs a process and throws if it exits with a non-zero code.
    /// </summary>
    private static async Task RunProcessCheckedAsync(string fileName, string arguments, CancellationToken ct)
    {
        var (exitCode, _, stderr) = await RunProcessAsync(fileName, arguments, ct);

        if (exitCode != 0)
        {
            throw new InvalidOperationException(
                $"Process '{fileName} {arguments}' failed with exit code {exitCode}: {stderr}");
        }
    }
}

/// <summary>
/// Status information about a system service.
/// </summary>
public sealed record ServiceStatus
{
    /// <summary>Whether the service is registered/installed.</summary>
    public bool IsInstalled { get; init; }

    /// <summary>Whether the service is currently running.</summary>
    public bool IsRunning { get; init; }

    /// <summary>Service state description (Running, Stopped, NotInstalled, etc.).</summary>
    public required string State { get; init; }

    /// <summary>Process ID if running.</summary>
    public int? PID { get; init; }
}

/// <summary>
/// Registration details for creating a new system service.
/// </summary>
public sealed record ServiceRegistration
{
    /// <summary>Service name (used for sc/systemctl/launchctl).</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable display name.</summary>
    public required string DisplayName { get; init; }

    /// <summary>Full path to the service executable.</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>Working directory for the service.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Whether to start automatically on boot.</summary>
    public bool AutoStart { get; init; }

    /// <summary>Service description.</summary>
    public string? Description { get; init; }
}
