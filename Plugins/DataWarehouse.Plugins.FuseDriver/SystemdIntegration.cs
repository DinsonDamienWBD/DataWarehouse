// <copyright file="SystemdIntegration.cs" company="DataWarehouse">
// Copyright (c) DataWarehouse. All rights reserved.
// Licensed under the MIT License.
// </copyright>

using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.Plugins.FuseDriver;

/// <summary>
/// Provides systemd integration for the FUSE driver including socket activation,
/// readiness notification, watchdog support, and journal logging.
/// </summary>
/// <remarks>
/// This class enables the FUSE driver to integrate with systemd service management:
/// - Socket activation allows on-demand service startup
/// - sd_notify signals service readiness and status
/// - Watchdog support enables automatic service restart on hang
/// - Journal logging provides structured logging to systemd journal
/// </remarks>
public sealed class SystemdIntegration : IDisposable
{
    private readonly FuseConfig _config;
    private readonly IKernelContext? _kernelContext;
    private readonly List<Socket> _activationSockets = new();
    private Timer? _watchdogTimer;
    private bool _notifiedReady;
    private bool _disposed;

    /// <summary>
    /// Gets a value indicating whether the process is running under systemd.
    /// </summary>
    public static bool IsRunningUnderSystemd =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NOTIFY_SOCKET")) ||
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("LISTEN_FDS"));

    /// <summary>
    /// Gets a value indicating whether socket activation is available.
    /// </summary>
    public bool IsSocketActivated { get; private set; }

    /// <summary>
    /// Gets the number of file descriptors passed via socket activation.
    /// </summary>
    public int ListenFdCount { get; private set; }

    /// <summary>
    /// Gets the watchdog interval in microseconds, or 0 if disabled.
    /// </summary>
    public ulong WatchdogUsec { get; private set; }

    /// <summary>
    /// Gets a value indicating whether watchdog is enabled.
    /// </summary>
    public bool IsWatchdogEnabled => WatchdogUsec > 0;

    /// <summary>
    /// Initializes a new instance of the <see cref="SystemdIntegration"/> class.
    /// </summary>
    /// <param name="config">The FUSE configuration.</param>
    /// <param name="kernelContext">Optional kernel context for logging.</param>
    public SystemdIntegration(FuseConfig config, IKernelContext? kernelContext = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _kernelContext = kernelContext;

        DetectSystemdEnvironment();
    }

    #region Socket Activation

    /// <summary>
    /// Initializes socket activation and retrieves passed file descriptors.
    /// </summary>
    /// <returns>True if socket activation was successful or not requested.</returns>
    public bool InitializeSocketActivation()
    {
        if (!IsRunningUnderSystemd)
        {
            _kernelContext?.LogDebug("Not running under systemd, socket activation skipped");
            return true;
        }

        var listenFds = Environment.GetEnvironmentVariable("LISTEN_FDS");
        var listenPid = Environment.GetEnvironmentVariable("LISTEN_PID");

        if (string.IsNullOrEmpty(listenFds))
        {
            _kernelContext?.LogDebug("No LISTEN_FDS environment variable, socket activation not requested");
            return true;
        }

        // Verify PID matches
        if (!string.IsNullOrEmpty(listenPid) && int.TryParse(listenPid, out var pid))
        {
            if (pid != Environment.ProcessId)
            {
                _kernelContext?.LogWarning($"LISTEN_PID ({pid}) does not match current process ({Environment.ProcessId})");
                return false;
            }
        }

        if (!int.TryParse(listenFds, out var fdCount) || fdCount <= 0)
        {
            _kernelContext?.LogWarning($"Invalid LISTEN_FDS value: {listenFds}");
            return false;
        }

        ListenFdCount = fdCount;
        IsSocketActivated = true;

        _kernelContext?.LogInfo($"Socket activation: {fdCount} file descriptors available");

        // File descriptors start at SD_LISTEN_FDS_START (3)
        const int SD_LISTEN_FDS_START = 3;

        for (var i = 0; i < fdCount; i++)
        {
            var fd = SD_LISTEN_FDS_START + i;
            try
            {
                // Create socket from file descriptor
                var socket = CreateSocketFromFd(fd);
                if (socket != null)
                {
                    _activationSockets.Add(socket);
                    _kernelContext?.LogDebug($"Acquired socket from fd {fd}");
                }
            }
            catch (Exception ex)
            {
                _kernelContext?.LogError($"Failed to acquire socket from fd {fd}", ex);
            }
        }

        // Clear environment variables to prevent child process inheritance
        Environment.SetEnvironmentVariable("LISTEN_FDS", null);
        Environment.SetEnvironmentVariable("LISTEN_PID", null);

        return true;
    }

    /// <summary>
    /// Gets the activation sockets.
    /// </summary>
    /// <returns>The list of sockets passed via socket activation.</returns>
    public IReadOnlyList<Socket> GetActivationSockets() => _activationSockets.AsReadOnly();

    /// <summary>
    /// Gets a socket by its listen file descriptor name.
    /// </summary>
    /// <param name="name">The socket name from the .socket unit.</param>
    /// <returns>The socket, or null if not found.</returns>
    public Socket? GetSocketByName(string name)
    {
        var names = Environment.GetEnvironmentVariable("LISTEN_FDNAMES");
        if (string.IsNullOrEmpty(names))
            return _activationSockets.FirstOrDefault();

        var nameList = names.Split(':');
        var index = Array.IndexOf(nameList, name);

        if (index >= 0 && index < _activationSockets.Count)
            return _activationSockets[index];

        return null;
    }

    private static Socket? CreateSocketFromFd(int fd)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return null;

        try
        {
            // On Linux, we can create a socket from an existing file descriptor
            // This is done through SafeSocketHandle
            var handle = new SafeSocketHandle(new IntPtr(fd), ownsHandle: true);
            return new Socket(handle);
        }
        catch
        {
            return null;
        }
    }

    #endregion

    #region Notify Protocol (sd_notify)

    /// <summary>
    /// Notifies systemd that the service is ready.
    /// </summary>
    /// <returns>True if notification was sent successfully.</returns>
    public bool NotifyReady()
    {
        if (_notifiedReady)
            return true;

        var result = Notify("READY=1");
        if (result)
        {
            _notifiedReady = true;
            _kernelContext?.LogInfo("Notified systemd: service ready");
        }

        return result;
    }

    /// <summary>
    /// Notifies systemd that the service is stopping.
    /// </summary>
    /// <returns>True if notification was sent successfully.</returns>
    public bool NotifyStopping()
    {
        var result = Notify("STOPPING=1");
        if (result)
        {
            _kernelContext?.LogInfo("Notified systemd: service stopping");
        }

        return result;
    }

    /// <summary>
    /// Notifies systemd that the service is reloading configuration.
    /// </summary>
    /// <returns>True if notification was sent successfully.</returns>
    public bool NotifyReloading()
    {
        var result = Notify("RELOADING=1");
        if (result)
        {
            _kernelContext?.LogInfo("Notified systemd: service reloading");
        }

        return result;
    }

    /// <summary>
    /// Updates the service status message shown by systemctl status.
    /// </summary>
    /// <param name="status">The status message.</param>
    /// <returns>True if notification was sent successfully.</returns>
    public bool NotifyStatus(string status)
    {
        return Notify($"STATUS={status}");
    }

    /// <summary>
    /// Notifies systemd of the main PID (for forking services).
    /// </summary>
    /// <param name="pid">The main process ID.</param>
    /// <returns>True if notification was sent successfully.</returns>
    public bool NotifyMainPid(int pid)
    {
        return Notify($"MAINPID={pid}");
    }

    /// <summary>
    /// Sends a watchdog ping to systemd.
    /// </summary>
    /// <returns>True if notification was sent successfully.</returns>
    public bool NotifyWatchdog()
    {
        return Notify("WATCHDOG=1");
    }

    /// <summary>
    /// Triggers a watchdog failure, causing systemd to restart the service.
    /// </summary>
    /// <returns>True if notification was sent successfully.</returns>
    public bool TriggerWatchdogFailure()
    {
        return Notify("WATCHDOG=trigger");
    }

    /// <summary>
    /// Notifies systemd of an error with an errno value.
    /// </summary>
    /// <param name="errno">The error number.</param>
    /// <returns>True if notification was sent successfully.</returns>
    public bool NotifyError(int errno)
    {
        return Notify($"ERRNO={errno}");
    }

    /// <summary>
    /// Notifies systemd of an error with an error message.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <returns>True if notification was sent successfully.</returns>
    public bool NotifyBusError(string message)
    {
        return Notify($"BUSERROR={message}");
    }

    /// <summary>
    /// Extends the startup or shutdown timeout.
    /// </summary>
    /// <param name="microseconds">The additional time in microseconds.</param>
    /// <returns>True if notification was sent successfully.</returns>
    public bool ExtendTimeout(ulong microseconds)
    {
        return Notify($"EXTEND_TIMEOUT_USEC={microseconds}");
    }

    /// <summary>
    /// Sends a custom notification to systemd.
    /// </summary>
    /// <param name="state">The notification state string.</param>
    /// <returns>True if the notification was sent successfully.</returns>
    public bool Notify(string state)
    {
        var notifySocket = Environment.GetEnvironmentVariable("NOTIFY_SOCKET");
        if (string.IsNullOrEmpty(notifySocket))
            return false;

        try
        {
            using var socket = CreateNotifySocket(notifySocket);
            if (socket == null)
                return false;

            var data = Encoding.UTF8.GetBytes(state);
            socket.Send(data);
            return true;
        }
        catch (Exception ex)
        {
            _kernelContext?.LogWarning($"Failed to send sd_notify: {ex.Message}");
            return false;
        }
    }

    private static Socket? CreateNotifySocket(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        // Handle abstract socket (starts with @)
        if (path.StartsWith('@'))
        {
            path = '\0' + path.Substring(1);
        }

        var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);

        try
        {
            var endpoint = new UnixDomainSocketEndPoint(path);
            socket.Connect(endpoint);
            return socket;
        }
        catch
        {
            socket.Dispose();
            return null;
        }
    }

    #endregion

    #region Watchdog Support

    /// <summary>
    /// Starts the watchdog timer if enabled.
    /// </summary>
    /// <returns>True if watchdog was started or not enabled.</returns>
    public bool StartWatchdog()
    {
        if (!IsWatchdogEnabled)
        {
            _kernelContext?.LogDebug("Watchdog not enabled");
            return true;
        }

        // Calculate ping interval (half of watchdog timeout)
        var intervalUsec = WatchdogUsec / 2;
        var intervalMs = (int)(intervalUsec / 1000);

        if (intervalMs < 100)
            intervalMs = 100; // Minimum 100ms

        _watchdogTimer = new Timer(
            _ => NotifyWatchdog(),
            null,
            intervalMs,
            intervalMs);

        _kernelContext?.LogInfo($"Watchdog started with interval {intervalMs}ms");
        return true;
    }

    /// <summary>
    /// Stops the watchdog timer.
    /// </summary>
    public void StopWatchdog()
    {
        _watchdogTimer?.Dispose();
        _watchdogTimer = null;
        _kernelContext?.LogDebug("Watchdog stopped");
    }

    #endregion

    #region Journal Logging

    /// <summary>
    /// Logs a message to the systemd journal.
    /// </summary>
    /// <param name="priority">The log priority (0-7, where 0 is emergency).</param>
    /// <param name="message">The message to log.</param>
    /// <param name="fields">Optional additional structured fields.</param>
    public void JournalPrint(JournalPriority priority, string message, IDictionary<string, string>? fields = null)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Fall back to regular logging on non-Linux
            _kernelContext?.LogInfo($"[{priority}] {message}");
            return;
        }

        try
        {
            var allFields = new Dictionary<string, string>
            {
                ["MESSAGE"] = message,
                ["PRIORITY"] = ((int)priority).ToString(),
                ["SYSLOG_IDENTIFIER"] = "datawarehouse-fuse"
            };

            if (fields != null)
            {
                foreach (var kvp in fields)
                {
                    allFields[kvp.Key.ToUpperInvariant()] = kvp.Value;
                }
            }

            SendJournalMessage(allFields);
        }
        catch (Exception ex)
        {
            // Fall back to regular logging
            _kernelContext?.LogWarning($"Failed to log to journal: {ex.Message}");
            _kernelContext?.LogInfo($"[{priority}] {message}");
        }
    }

    /// <summary>
    /// Logs an informational message to the journal.
    /// </summary>
    /// <param name="message">The message to log.</param>
    public void JournalInfo(string message) => JournalPrint(JournalPriority.Info, message);

    /// <summary>
    /// Logs a warning message to the journal.
    /// </summary>
    /// <param name="message">The message to log.</param>
    public void JournalWarning(string message) => JournalPrint(JournalPriority.Warning, message);

    /// <summary>
    /// Logs an error message to the journal.
    /// </summary>
    /// <param name="message">The message to log.</param>
    public void JournalError(string message) => JournalPrint(JournalPriority.Error, message);

    /// <summary>
    /// Logs a debug message to the journal.
    /// </summary>
    /// <param name="message">The message to log.</param>
    public void JournalDebug(string message) => JournalPrint(JournalPriority.Debug, message);

    private static void SendJournalMessage(IDictionary<string, string> fields)
    {
        const string JournalSocketPath = "/run/systemd/journal/socket";

        if (!File.Exists(JournalSocketPath))
            return;

        using var socket = new Socket(AddressFamily.Unix, SocketType.Dgram, ProtocolType.Unspecified);
        var endpoint = new UnixDomainSocketEndPoint(JournalSocketPath);

        try
        {
            socket.Connect(endpoint);

            // Build the message in journal native format
            var sb = new StringBuilder();
            foreach (var kvp in fields)
            {
                var key = kvp.Key;
                var value = kvp.Value;

                if (value.Contains('\n') || value.Any(c => c < 32))
                {
                    // Binary format for values with newlines or control characters
                    var valueBytes = Encoding.UTF8.GetBytes(value);
                    sb.Append(key);
                    sb.Append('\n');
                    sb.Append((char)(valueBytes.Length & 0xFF));
                    sb.Append((char)((valueBytes.Length >> 8) & 0xFF));
                    sb.Append((char)((valueBytes.Length >> 16) & 0xFF));
                    sb.Append((char)((valueBytes.Length >> 24) & 0xFF));
                    sb.Append((char)((valueBytes.Length >> 32) & 0xFF));
                    sb.Append((char)((valueBytes.Length >> 40) & 0xFF));
                    sb.Append((char)((valueBytes.Length >> 48) & 0xFF));
                    sb.Append((char)((valueBytes.Length >> 56) & 0xFF));
                    sb.Append(value);
                    sb.Append('\n');
                }
                else
                {
                    // Simple format: KEY=value\n
                    sb.Append(key);
                    sb.Append('=');
                    sb.Append(value);
                    sb.Append('\n');
                }
            }

            var data = Encoding.UTF8.GetBytes(sb.ToString());
            socket.Send(data);
        }
        catch
        {
            // Ignore journal errors
        }
    }

    #endregion

    #region Service File Generation

    /// <summary>
    /// Generates a systemd service unit file for the FUSE driver.
    /// </summary>
    /// <param name="options">The service generation options.</param>
    /// <returns>The service unit file content.</returns>
    public string GenerateServiceFile(ServiceFileOptions options)
    {
        var sb = new StringBuilder();

        // [Unit] section
        sb.AppendLine("[Unit]");
        sb.AppendLine($"Description={options.Description ?? "DataWarehouse FUSE Filesystem"}");

        if (options.Documentation != null)
        {
            sb.AppendLine($"Documentation={options.Documentation}");
        }

        sb.AppendLine("After=network.target local-fs.target");

        if (options.RequiredUnits?.Any() == true)
        {
            sb.AppendLine($"Requires={string.Join(" ", options.RequiredUnits)}");
        }

        if (options.WantedUnits?.Any() == true)
        {
            sb.AppendLine($"Wants={string.Join(" ", options.WantedUnits)}");
        }

        if (options.ConflictUnits?.Any() == true)
        {
            sb.AppendLine($"Conflicts={string.Join(" ", options.ConflictUnits)}");
        }

        sb.AppendLine();

        // [Service] section
        sb.AppendLine("[Service]");
        sb.AppendLine($"Type={options.ServiceType ?? "notify"}");
        sb.AppendLine($"ExecStart={options.ExecStart ?? "/usr/bin/datawarehouse-fuse --mount /mnt/datawarehouse"}");

        if (!string.IsNullOrEmpty(options.ExecStartPre))
        {
            sb.AppendLine($"ExecStartPre={options.ExecStartPre}");
        }

        if (!string.IsNullOrEmpty(options.ExecStartPost))
        {
            sb.AppendLine($"ExecStartPost={options.ExecStartPost}");
        }

        if (!string.IsNullOrEmpty(options.ExecStop))
        {
            sb.AppendLine($"ExecStop={options.ExecStop}");
        }

        if (!string.IsNullOrEmpty(options.ExecReload))
        {
            sb.AppendLine($"ExecReload={options.ExecReload}");
        }

        sb.AppendLine($"Restart={options.RestartPolicy ?? "on-failure"}");
        sb.AppendLine($"RestartSec={options.RestartSec}");

        if (options.WatchdogSec > 0)
        {
            sb.AppendLine($"WatchdogSec={options.WatchdogSec}");
        }

        if (options.TimeoutStartSec > 0)
        {
            sb.AppendLine($"TimeoutStartSec={options.TimeoutStartSec}");
        }

        if (options.TimeoutStopSec > 0)
        {
            sb.AppendLine($"TimeoutStopSec={options.TimeoutStopSec}");
        }

        // User/Group
        if (!string.IsNullOrEmpty(options.User))
        {
            sb.AppendLine($"User={options.User}");
        }

        if (!string.IsNullOrEmpty(options.Group))
        {
            sb.AppendLine($"Group={options.Group}");
        }

        // Working directory
        if (!string.IsNullOrEmpty(options.WorkingDirectory))
        {
            sb.AppendLine($"WorkingDirectory={options.WorkingDirectory}");
        }

        // Environment
        if (options.Environment?.Any() == true)
        {
            foreach (var env in options.Environment)
            {
                sb.AppendLine($"Environment=\"{env.Key}={env.Value}\"");
            }
        }

        // Security hardening
        if (options.EnableSecurityHardening)
        {
            sb.AppendLine();
            sb.AppendLine("# Security hardening");
            sb.AppendLine("NoNewPrivileges=yes");
            sb.AppendLine("ProtectSystem=strict");
            sb.AppendLine("ProtectHome=yes");
            sb.AppendLine("PrivateTmp=yes");
            sb.AppendLine("PrivateDevices=yes");
            sb.AppendLine("ProtectKernelTunables=yes");
            sb.AppendLine("ProtectKernelModules=yes");
            sb.AppendLine("ProtectControlGroups=yes");
            sb.AppendLine("RestrictRealtime=yes");
            sb.AppendLine("RestrictSUIDSGID=yes");

            // FUSE needs /dev/fuse
            sb.AppendLine("DeviceAllow=/dev/fuse rw");

            // Allow read-write to mount point
            sb.AppendLine($"ReadWritePaths={_config.MountPoint}");

            if (options.CapabilityBoundingSet?.Any() == true)
            {
                sb.AppendLine($"CapabilityBoundingSet={string.Join(" ", options.CapabilityBoundingSet)}");
            }
            else
            {
                sb.AppendLine("CapabilityBoundingSet=CAP_SYS_ADMIN");
            }
        }

        // Resource limits
        if (options.MemoryLimit != null)
        {
            sb.AppendLine($"MemoryLimit={options.MemoryLimit}");
        }

        if (options.CPUQuota != null)
        {
            sb.AppendLine($"CPUQuota={options.CPUQuota}");
        }

        sb.AppendLine();

        // [Install] section
        sb.AppendLine("[Install]");
        sb.AppendLine($"WantedBy={options.WantedBy ?? "multi-user.target"}");

        if (options.Alias != null)
        {
            sb.AppendLine($"Alias={options.Alias}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Generates a systemd socket unit file for socket activation.
    /// </summary>
    /// <param name="options">The socket generation options.</param>
    /// <returns>The socket unit file content.</returns>
    public string GenerateSocketFile(SocketFileOptions options)
    {
        var sb = new StringBuilder();

        // [Unit] section
        sb.AppendLine("[Unit]");
        sb.AppendLine($"Description={options.Description ?? "DataWarehouse FUSE Socket"}");
        sb.AppendLine();

        // [Socket] section
        sb.AppendLine("[Socket]");

        if (!string.IsNullOrEmpty(options.ListenStream))
        {
            sb.AppendLine($"ListenStream={options.ListenStream}");
        }

        if (!string.IsNullOrEmpty(options.ListenDatagram))
        {
            sb.AppendLine($"ListenDatagram={options.ListenDatagram}");
        }

        if (!string.IsNullOrEmpty(options.ListenSequentialPacket))
        {
            sb.AppendLine($"ListenSequentialPacket={options.ListenSequentialPacket}");
        }

        if (!string.IsNullOrEmpty(options.ListenFIFO))
        {
            sb.AppendLine($"ListenFIFO={options.ListenFIFO}");
        }

        if (!string.IsNullOrEmpty(options.SocketMode))
        {
            sb.AppendLine($"SocketMode={options.SocketMode}");
        }

        if (!string.IsNullOrEmpty(options.SocketUser))
        {
            sb.AppendLine($"SocketUser={options.SocketUser}");
        }

        if (!string.IsNullOrEmpty(options.SocketGroup))
        {
            sb.AppendLine($"SocketGroup={options.SocketGroup}");
        }

        if (options.Accept)
        {
            sb.AppendLine("Accept=yes");
        }

        if (options.MaxConnections > 0)
        {
            sb.AppendLine($"MaxConnections={options.MaxConnections}");
        }

        if (options.KeepAlive)
        {
            sb.AppendLine("KeepAlive=yes");
        }

        sb.AppendLine();

        // [Install] section
        sb.AppendLine("[Install]");
        sb.AppendLine($"WantedBy={options.WantedBy ?? "sockets.target"}");

        return sb.ToString();
    }

    #endregion

    #region Private Methods

    private void DetectSystemdEnvironment()
    {
        // Detect watchdog settings
        var watchdogUsec = Environment.GetEnvironmentVariable("WATCHDOG_USEC");
        var watchdogPid = Environment.GetEnvironmentVariable("WATCHDOG_PID");

        if (!string.IsNullOrEmpty(watchdogUsec) && ulong.TryParse(watchdogUsec, out var usec))
        {
            // Check if watchdog is for this process
            if (string.IsNullOrEmpty(watchdogPid) ||
                (int.TryParse(watchdogPid, out var pid) && pid == Environment.ProcessId))
            {
                WatchdogUsec = usec;
                _kernelContext?.LogInfo($"Watchdog detected: {usec} microseconds");
            }
        }
    }

    #endregion

    #region IDisposable

    /// <summary>
    /// Disposes the systemd integration resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        StopWatchdog();
        NotifyStopping();

        foreach (var socket in _activationSockets)
        {
            try
            {
                socket.Dispose();
            }
            catch
            {
                // Ignore disposal errors
            }
        }

        _activationSockets.Clear();
    }

    #endregion
}

/// <summary>
/// Journal log priority levels.
/// </summary>
public enum JournalPriority
{
    /// <summary>
    /// System is unusable.
    /// </summary>
    Emergency = 0,

    /// <summary>
    /// Action must be taken immediately.
    /// </summary>
    Alert = 1,

    /// <summary>
    /// Critical conditions.
    /// </summary>
    Critical = 2,

    /// <summary>
    /// Error conditions.
    /// </summary>
    Error = 3,

    /// <summary>
    /// Warning conditions.
    /// </summary>
    Warning = 4,

    /// <summary>
    /// Normal but significant conditions.
    /// </summary>
    Notice = 5,

    /// <summary>
    /// Informational messages.
    /// </summary>
    Info = 6,

    /// <summary>
    /// Debug-level messages.
    /// </summary>
    Debug = 7
}

/// <summary>
/// Options for generating a systemd service unit file.
/// </summary>
public sealed class ServiceFileOptions
{
    /// <summary>
    /// Gets or sets the service description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the documentation URL.
    /// </summary>
    public string? Documentation { get; set; }

    /// <summary>
    /// Gets or sets the required units.
    /// </summary>
    public IList<string>? RequiredUnits { get; set; }

    /// <summary>
    /// Gets or sets the wanted units.
    /// </summary>
    public IList<string>? WantedUnits { get; set; }

    /// <summary>
    /// Gets or sets the conflicting units.
    /// </summary>
    public IList<string>? ConflictUnits { get; set; }

    /// <summary>
    /// Gets or sets the service type (simple, forking, oneshot, notify, dbus, idle).
    /// </summary>
    public string? ServiceType { get; set; }

    /// <summary>
    /// Gets or sets the main executable command.
    /// </summary>
    public string? ExecStart { get; set; }

    /// <summary>
    /// Gets or sets the pre-start command.
    /// </summary>
    public string? ExecStartPre { get; set; }

    /// <summary>
    /// Gets or sets the post-start command.
    /// </summary>
    public string? ExecStartPost { get; set; }

    /// <summary>
    /// Gets or sets the stop command.
    /// </summary>
    public string? ExecStop { get; set; }

    /// <summary>
    /// Gets or sets the reload command.
    /// </summary>
    public string? ExecReload { get; set; }

    /// <summary>
    /// Gets or sets the restart policy.
    /// </summary>
    public string? RestartPolicy { get; set; }

    /// <summary>
    /// Gets or sets the restart delay in seconds.
    /// </summary>
    public int RestartSec { get; set; } = 5;

    /// <summary>
    /// Gets or sets the watchdog timeout in seconds.
    /// </summary>
    public int WatchdogSec { get; set; }

    /// <summary>
    /// Gets or sets the startup timeout in seconds.
    /// </summary>
    public int TimeoutStartSec { get; set; }

    /// <summary>
    /// Gets or sets the stop timeout in seconds.
    /// </summary>
    public int TimeoutStopSec { get; set; }

    /// <summary>
    /// Gets or sets the user to run as.
    /// </summary>
    public string? User { get; set; }

    /// <summary>
    /// Gets or sets the group to run as.
    /// </summary>
    public string? Group { get; set; }

    /// <summary>
    /// Gets or sets the working directory.
    /// </summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Gets or sets the environment variables.
    /// </summary>
    public IDictionary<string, string>? Environment { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to enable security hardening options.
    /// </summary>
    public bool EnableSecurityHardening { get; set; } = true;

    /// <summary>
    /// Gets or sets the capability bounding set.
    /// </summary>
    public IList<string>? CapabilityBoundingSet { get; set; }

    /// <summary>
    /// Gets or sets the memory limit.
    /// </summary>
    public string? MemoryLimit { get; set; }

    /// <summary>
    /// Gets or sets the CPU quota.
    /// </summary>
    public string? CPUQuota { get; set; }

    /// <summary>
    /// Gets or sets the install target.
    /// </summary>
    public string? WantedBy { get; set; }

    /// <summary>
    /// Gets or sets the service alias.
    /// </summary>
    public string? Alias { get; set; }
}

/// <summary>
/// Options for generating a systemd socket unit file.
/// </summary>
public sealed class SocketFileOptions
{
    /// <summary>
    /// Gets or sets the socket description.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Gets or sets the stream socket address.
    /// </summary>
    public string? ListenStream { get; set; }

    /// <summary>
    /// Gets or sets the datagram socket address.
    /// </summary>
    public string? ListenDatagram { get; set; }

    /// <summary>
    /// Gets or sets the sequential packet socket address.
    /// </summary>
    public string? ListenSequentialPacket { get; set; }

    /// <summary>
    /// Gets or sets the FIFO path.
    /// </summary>
    public string? ListenFIFO { get; set; }

    /// <summary>
    /// Gets or sets the socket file mode.
    /// </summary>
    public string? SocketMode { get; set; }

    /// <summary>
    /// Gets or sets the socket owner user.
    /// </summary>
    public string? SocketUser { get; set; }

    /// <summary>
    /// Gets or sets the socket owner group.
    /// </summary>
    public string? SocketGroup { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to accept connections.
    /// </summary>
    public bool Accept { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of connections.
    /// </summary>
    public int MaxConnections { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to enable keep-alive.
    /// </summary>
    public bool KeepAlive { get; set; }

    /// <summary>
    /// Gets or sets the install target.
    /// </summary>
    public string? WantedBy { get; set; }
}
