using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Plugins.HardwareAcceleration;

/// <summary>
/// NVMe over Fabrics (NVMe-oF) plugin for high-performance remote storage access.
/// Supports TCP, RDMA, and Fibre Channel transport types for accessing remote NVMe storage.
/// Enables both initiator (client) and target (server) mode operations.
/// </summary>
/// <remarks>
/// <para>
/// NVMe-oF extends the NVMe protocol to operate over network fabrics, enabling
/// high-performance access to remote NVMe storage with near-local latency and throughput.
/// </para>
/// <para>
/// This plugin requires:
/// - Linux with nvme-cli package installed
/// - For target mode: nvmet kernel module and configfs
/// - For RDMA transport: RDMA-capable hardware and drivers
/// </para>
/// </remarks>
public partial class NvmeOfPlugin : FeaturePluginBase
{
    #region Constants

    private const string NvmeCliPath = "nvme";
    private const string NvmetConfigPath = "/sys/kernel/config/nvmet";
    private const int DefaultNvmePort = 4420;
    private const int CommandTimeoutMs = 30000;

    #endregion

    #region Fields

    private readonly object _lock = new();
    private bool _initialized;
    private bool _nvmeCliAvailable;
    private bool _targetModeAvailable;
    private string _nvmeCliVersion = string.Empty;
    private readonly ConcurrentDictionary<string, NvmeOfConnection> _connections = new();
    private readonly ConcurrentDictionary<string, NvmeOfSubsystem> _subsystems = new();
    private long _discoverOperations;
    private long _connectOperations;
    private long _disconnectOperations;

    #endregion

    #region Plugin Properties

    /// <inheritdoc />
    public override string Id => "datawarehouse.hwaccel.nvme-of";

    /// <inheritdoc />
    public override string Name => "NVMe over Fabrics";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.OrchestrationProvider;

    /// <summary>
    /// Gets whether NVMe-oF capabilities are available on this system.
    /// Checks for nvme-cli and optionally nvmet (target mode).
    /// </summary>
    public bool IsAvailable => CheckNvmeOfAvailability();

    /// <summary>
    /// Gets whether target mode is available (for exporting local NVMe devices).
    /// Requires nvmet kernel module and configfs.
    /// </summary>
    public bool TargetModeAvailable => _targetModeAvailable;

    /// <summary>
    /// Gets the nvme-cli version if available.
    /// </summary>
    public string NvmeCliVersion => _nvmeCliVersion;

    /// <summary>
    /// Gets the number of active connections.
    /// </summary>
    public int ActiveConnections => _connections.Count;

    /// <summary>
    /// Gets the number of configured subsystems (target mode).
    /// </summary>
    public int ConfiguredSubsystems => _subsystems.Count;

    #endregion

    #region Lifecycle Methods

    /// <inheritdoc />
    public override async Task StartAsync(CancellationToken ct)
    {
        if (_initialized) return;

        await Task.Run(() =>
        {
            lock (_lock)
            {
                if (_initialized) return;

                CheckNvmeOfAvailability();
                if (_nvmeCliAvailable)
                {
                    _nvmeCliVersion = GetNvmeCliVersion();
                    _targetModeAvailable = CheckTargetModeAvailability();

                    // Load existing connections
                    LoadExistingConnections();

                    // Load existing subsystems if in target mode
                    if (_targetModeAvailable)
                    {
                        LoadExistingSubsystems();
                    }
                }

                _initialized = true;
            }
        }, ct);
    }

    /// <inheritdoc />
    public override async Task StopAsync()
    {
        await Task.Run(() =>
        {
            lock (_lock)
            {
                _connections.Clear();
                _subsystems.Clear();
                _initialized = false;
            }
        });
    }

    #endregion

    #region Discovery Operations

    /// <summary>
    /// Discovers available NVMe-oF targets at the specified address.
    /// Uses the NVMe discovery controller to enumerate available subsystems.
    /// </summary>
    /// <param name="transport">Transport type (tcp, rdma, fc).</param>
    /// <param name="address">IP address or hostname of the discovery controller.</param>
    /// <param name="port">Port number (default: 8009 for discovery).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of discovered NVMe-oF targets.</returns>
    /// <exception cref="ArgumentNullException">Thrown when transport or address is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when NVMe-oF is not available.</exception>
    /// <exception cref="NvmeOfException">Thrown when discovery fails.</exception>
    public async Task<IReadOnlyList<NvmeOfDiscoveryEntry>> DiscoverTargetsAsync(
        NvmeOfTransport transport,
        string address,
        int port = 8009,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (!IsAvailable)
        {
            throw new InvalidOperationException(
                "NVMe-oF is not available on this system. " +
                "Ensure nvme-cli is installed.");
        }

        var transportStr = GetTransportString(transport);

        return await Task.Run(async () =>
        {
            Interlocked.Increment(ref _discoverOperations);

            var args = $"discover -t {transportStr} -a {address} -s {port} -o json";
            var (exitCode, stdout, stderr) = await ExecuteNvmeCommandAsync(args, cancellationToken);

            if (exitCode != 0)
            {
                // Non-zero exit might mean no targets found, which is not an error
                if (stderr.Contains("no records", StringComparison.OrdinalIgnoreCase) ||
                    stdout.Contains("no records", StringComparison.OrdinalIgnoreCase))
                {
                    return Array.Empty<NvmeOfDiscoveryEntry>();
                }

                throw new NvmeOfException(
                    $"NVMe-oF discovery failed: {stderr}",
                    transport,
                    address,
                    port);
            }

            return ParseDiscoveryOutput(stdout, transport, address);
        }, cancellationToken);
    }

    #endregion

    #region Connection Management

    /// <summary>
    /// Connects to an NVMe-oF target subsystem.
    /// Creates a connection to the remote NVMe subsystem and makes its namespaces available locally.
    /// </summary>
    /// <param name="nqn">NVMe Qualified Name of the target subsystem.</param>
    /// <param name="transport">Transport type (tcp, rdma, fc).</param>
    /// <param name="address">IP address or hostname of the target.</param>
    /// <param name="port">Port number (default: 4420).</param>
    /// <param name="options">Optional connection parameters.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Connection information including assigned local device paths.</returns>
    /// <exception cref="ArgumentNullException">Thrown when required parameters are null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when NVMe-oF is not available.</exception>
    /// <exception cref="NvmeOfException">Thrown when connection fails.</exception>
    public async Task<NvmeOfConnection> ConnectAsync(
        string nqn,
        NvmeOfTransport transport,
        string address,
        int port = DefaultNvmePort,
        NvmeOfConnectOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nqn);
        ArgumentNullException.ThrowIfNull(address);

        if (!IsAvailable)
        {
            throw new InvalidOperationException(
                "NVMe-oF is not available on this system.");
        }

        var transportStr = GetTransportString(transport);
        options ??= new NvmeOfConnectOptions();

        return await Task.Run(async () =>
        {
            Interlocked.Increment(ref _connectOperations);

            // Build connect command
            var args = $"connect -t {transportStr} -a {address} -s {port} -n {nqn}";

            if (options.QueueDepth > 0)
            {
                args += $" -Q {options.QueueDepth}";
            }

            if (options.NumberOfQueues > 0)
            {
                args += $" -i {options.NumberOfQueues}";
            }

            if (options.KeepAliveTimeout > 0)
            {
                args += $" -k {options.KeepAliveTimeout}";
            }

            if (options.ReconnectDelay > 0)
            {
                args += $" -c {options.ReconnectDelay}";
            }

            if (options.ControllerLossTimeout > 0)
            {
                args += $" -l {options.ControllerLossTimeout}";
            }

            if (!string.IsNullOrEmpty(options.HostNqn))
            {
                args += $" -q {options.HostNqn}";
            }

            if (!string.IsNullOrEmpty(options.HostId))
            {
                args += $" -I {options.HostId}";
            }

            if (options.Duplicate)
            {
                args += " -D";
            }

            var (exitCode, stdout, stderr) = await ExecuteNvmeCommandAsync(args, cancellationToken);

            if (exitCode != 0)
            {
                throw new NvmeOfException(
                    $"Failed to connect to NVMe-oF target: {stderr}",
                    transport,
                    address,
                    port);
            }

            // Create connection object
            var connectionId = $"{nqn}@{address}:{port}";
            var connection = new NvmeOfConnection
            {
                ConnectionId = connectionId,
                Nqn = nqn,
                Transport = transport,
                Address = address,
                Port = port,
                State = NvmeOfConnectionState.Connected,
                ConnectedAt = DateTime.UtcNow,
                Options = options
            };

            // Try to find the assigned device
            await Task.Delay(100, cancellationToken); // Brief delay for device to appear
            connection.LocalDevices = await FindConnectedDevicesAsync(nqn, cancellationToken);

            _connections[connectionId] = connection;

            return connection;
        }, cancellationToken);
    }

    /// <summary>
    /// Disconnects from an NVMe-oF target subsystem.
    /// </summary>
    /// <param name="nqn">NVMe Qualified Name of the subsystem to disconnect.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="ArgumentNullException">Thrown when nqn is null.</exception>
    /// <exception cref="NvmeOfException">Thrown when disconnection fails.</exception>
    public async Task DisconnectAsync(
        string nqn,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nqn);

        if (!IsAvailable)
        {
            throw new InvalidOperationException(
                "NVMe-oF is not available on this system.");
        }

        await Task.Run(async () =>
        {
            Interlocked.Increment(ref _disconnectOperations);

            var args = $"disconnect -n {nqn}";
            var (exitCode, stdout, stderr) = await ExecuteNvmeCommandAsync(args, cancellationToken);

            if (exitCode != 0 && !stderr.Contains("not found", StringComparison.OrdinalIgnoreCase))
            {
                throw new NvmeOfException($"Failed to disconnect from NVMe-oF target: {stderr}");
            }

            // Remove from our tracking
            var toRemove = _connections.Where(kvp => kvp.Value.Nqn == nqn).Select(kvp => kvp.Key).ToList();
            foreach (var key in toRemove)
            {
                _connections.TryRemove(key, out _);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Lists all active NVMe-oF connections on this system.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of active connections.</returns>
    public async Task<IReadOnlyList<NvmeOfConnection>> ListConnectionsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return Array.Empty<NvmeOfConnection>();
        }

        return await Task.Run(async () =>
        {
            // Get list from nvme-cli
            var args = "list-subsys -o json";
            var (exitCode, stdout, stderr) = await ExecuteNvmeCommandAsync(args, cancellationToken);

            if (exitCode != 0)
            {
                // Return cached connections if command fails
                return _connections.Values.ToList().AsReadOnly();
            }

            var connections = ParseSubsystemList(stdout);

            // Update our cache
            foreach (var conn in connections)
            {
                _connections[conn.ConnectionId] = conn;
            }

            return connections.AsReadOnly();
        }, cancellationToken);
    }

    #endregion

    #region Target Mode Operations

    /// <summary>
    /// Creates an NVMe-oF subsystem for exporting local storage (target mode).
    /// Requires nvmet kernel module and root privileges.
    /// </summary>
    /// <param name="nqn">NVMe Qualified Name for the new subsystem.</param>
    /// <param name="allowAnyHost">Whether to allow connections from any host.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Information about the created subsystem.</returns>
    /// <exception cref="ArgumentNullException">Thrown when nqn is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when target mode is not available.</exception>
    /// <exception cref="NvmeOfException">Thrown when subsystem creation fails.</exception>
    public async Task<NvmeOfSubsystem> CreateSubsystemAsync(
        string nqn,
        bool allowAnyHost = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(nqn);

        if (!_targetModeAvailable)
        {
            throw new InvalidOperationException(
                "NVMe-oF target mode is not available. " +
                "Ensure nvmet kernel module is loaded and configfs is mounted.");
        }

        // Validate NQN format
        if (!IsValidNqn(nqn))
        {
            throw new ArgumentException(
                "Invalid NQN format. Expected format: nqn.yyyy-mm.reverse.domain:identifier",
                nameof(nqn));
        }

        return await Task.Run(() =>
        {
            var subsystemPath = Path.Combine(NvmetConfigPath, "subsystems", nqn);

            try
            {
                // Create subsystem directory
                if (!Directory.Exists(subsystemPath))
                {
                    Directory.CreateDirectory(subsystemPath);
                }

                // Configure allow_any_host
                var allowAnyHostPath = Path.Combine(subsystemPath, "attr_allow_any_host");
                if (File.Exists(allowAnyHostPath))
                {
                    File.WriteAllText(allowAnyHostPath, allowAnyHost ? "1" : "0");
                }

                var subsystem = new NvmeOfSubsystem
                {
                    Nqn = nqn,
                    AllowAnyHost = allowAnyHost,
                    CreatedAt = DateTime.UtcNow,
                    State = NvmeOfSubsystemState.Created,
                    Namespaces = new List<NvmeOfNamespace>()
                };

                _subsystems[nqn] = subsystem;

                return subsystem;
            }
            catch (Exception ex)
            {
                throw new NvmeOfException($"Failed to create NVMe-oF subsystem: {ex.Message}", ex);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Exports a local block device as an NVMe-oF namespace.
    /// Makes the device accessible to remote NVMe-oF initiators.
    /// </summary>
    /// <param name="subsystemNqn">NQN of the subsystem to add the namespace to.</param>
    /// <param name="devicePath">Path to the block device to export (e.g., "/dev/nvme0n1").</param>
    /// <param name="namespaceId">Optional namespace ID (auto-assigned if 0).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Information about the exported namespace.</returns>
    /// <exception cref="ArgumentNullException">Thrown when required parameters are null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when target mode is not available.</exception>
    /// <exception cref="NvmeOfException">Thrown when namespace creation fails.</exception>
    public async Task<NvmeOfNamespace> ExportNamespaceAsync(
        string subsystemNqn,
        string devicePath,
        int namespaceId = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subsystemNqn);
        ArgumentNullException.ThrowIfNull(devicePath);

        if (!_targetModeAvailable)
        {
            throw new InvalidOperationException(
                "NVMe-oF target mode is not available.");
        }

        if (!File.Exists(devicePath) && !devicePath.StartsWith("/dev/"))
        {
            throw new ArgumentException($"Device not found: {devicePath}", nameof(devicePath));
        }

        return await Task.Run(() =>
        {
            var subsystemPath = Path.Combine(NvmetConfigPath, "subsystems", subsystemNqn);
            if (!Directory.Exists(subsystemPath))
            {
                throw new NvmeOfException($"Subsystem not found: {subsystemNqn}");
            }

            // Find next available namespace ID if not specified
            var namespacesPath = Path.Combine(subsystemPath, "namespaces");
            if (namespaceId == 0)
            {
                namespaceId = 1;
                if (Directory.Exists(namespacesPath))
                {
                    var existing = Directory.GetDirectories(namespacesPath)
                        .Select(d => int.TryParse(Path.GetFileName(d), out var id) ? id : 0)
                        .DefaultIfEmpty(0)
                        .Max();
                    namespaceId = existing + 1;
                }
            }

            var nsPath = Path.Combine(namespacesPath, namespaceId.ToString());

            try
            {
                // Create namespace directory
                Directory.CreateDirectory(nsPath);

                // Set device path
                var devicePathFile = Path.Combine(nsPath, "device_path");
                File.WriteAllText(devicePathFile, devicePath);

                // Enable the namespace
                var enableFile = Path.Combine(nsPath, "enable");
                File.WriteAllText(enableFile, "1");

                var ns = new NvmeOfNamespace
                {
                    NamespaceId = namespaceId,
                    DevicePath = devicePath,
                    Enabled = true,
                    CreatedAt = DateTime.UtcNow
                };

                // Update subsystem in cache
                if (_subsystems.TryGetValue(subsystemNqn, out var subsystem))
                {
                    var namespaces = subsystem.Namespaces.ToList();
                    namespaces.Add(ns);
                    subsystem.Namespaces = namespaces;
                }

                return ns;
            }
            catch (Exception ex) when (ex is not NvmeOfException)
            {
                throw new NvmeOfException($"Failed to export namespace: {ex.Message}", ex);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Creates a port for the NVMe-oF target to listen on.
    /// </summary>
    /// <param name="portId">Port identifier.</param>
    /// <param name="transport">Transport type.</param>
    /// <param name="address">Address to bind to (e.g., "0.0.0.0").</param>
    /// <param name="servicePort">TCP/RDMA port number.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Information about the created port.</returns>
    public async Task<NvmeOfPort> CreatePortAsync(
        int portId,
        NvmeOfTransport transport,
        string address,
        int servicePort = DefaultNvmePort,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (!_targetModeAvailable)
        {
            throw new InvalidOperationException(
                "NVMe-oF target mode is not available.");
        }

        return await Task.Run(() =>
        {
            var portPath = Path.Combine(NvmetConfigPath, "ports", portId.ToString());

            try
            {
                Directory.CreateDirectory(portPath);

                // Set transport type
                File.WriteAllText(Path.Combine(portPath, "addr_trtype"), GetTransportString(transport));

                // Set address family (ipv4 or ipv6)
                var addrFam = address.Contains(':') ? "ipv6" : "ipv4";
                File.WriteAllText(Path.Combine(portPath, "addr_adrfam"), addrFam);

                // Set address
                File.WriteAllText(Path.Combine(portPath, "addr_traddr"), address);

                // Set service ID (port)
                File.WriteAllText(Path.Combine(portPath, "addr_trsvcid"), servicePort.ToString());

                return new NvmeOfPort
                {
                    PortId = portId,
                    Transport = transport,
                    Address = address,
                    ServicePort = servicePort,
                    AddressFamily = addrFam,
                    CreatedAt = DateTime.UtcNow
                };
            }
            catch (Exception ex)
            {
                throw new NvmeOfException($"Failed to create port: {ex.Message}", ex);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Links a subsystem to a port, making it accessible via that port.
    /// </summary>
    /// <param name="portId">Port identifier.</param>
    /// <param name="subsystemNqn">NQN of the subsystem to link.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task LinkSubsystemToPortAsync(
        int portId,
        string subsystemNqn,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subsystemNqn);

        if (!_targetModeAvailable)
        {
            throw new InvalidOperationException(
                "NVMe-oF target mode is not available.");
        }

        await Task.Run(() =>
        {
            var portPath = Path.Combine(NvmetConfigPath, "ports", portId.ToString());
            var subsystemPath = Path.Combine(NvmetConfigPath, "subsystems", subsystemNqn);

            if (!Directory.Exists(portPath))
            {
                throw new NvmeOfException($"Port not found: {portId}");
            }

            if (!Directory.Exists(subsystemPath))
            {
                throw new NvmeOfException($"Subsystem not found: {subsystemNqn}");
            }

            var linkPath = Path.Combine(portPath, "subsystems", subsystemNqn);

            try
            {
                // Create symbolic link
                if (!Directory.Exists(Path.GetDirectoryName(linkPath)))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
                }

                // On Linux, create symlink
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = "ln",
                        Arguments = $"-s {subsystemPath} {linkPath}",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false
                    };

                    using var process = Process.Start(psi);
                    process?.WaitForExit(5000);
                }
            }
            catch (Exception ex)
            {
                throw new NvmeOfException($"Failed to link subsystem to port: {ex.Message}", ex);
            }
        }, cancellationToken);
    }

    #endregion

    #region Private Methods

    private bool CheckNvmeOfAvailability()
    {
        if (_nvmeCliAvailable)
        {
            return true;
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return false;
        }

        // Check for nvme-cli
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "which",
                Arguments = "nvme",
                RedirectStandardOutput = true,
                UseShellExecute = false
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                process.WaitForExit(5000);
                if (process.ExitCode == 0)
                {
                    _nvmeCliAvailable = true;
                    return true;
                }
            }
        }
        catch
        {
            // nvme-cli not found
        }

        // Check common paths
        var nvmePaths = new[]
        {
            "/usr/sbin/nvme",
            "/usr/bin/nvme",
            "/sbin/nvme",
            "/bin/nvme"
        };

        foreach (var path in nvmePaths)
        {
            if (File.Exists(path))
            {
                _nvmeCliAvailable = true;
                return true;
            }
        }

        return false;
    }

    private bool CheckTargetModeAvailability()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return false;
        }

        // Check for nvmet configfs
        if (Directory.Exists(NvmetConfigPath))
        {
            return true;
        }

        // Check for nvmet kernel module
        try
        {
            if (File.Exists("/proc/modules"))
            {
                var modules = File.ReadAllText("/proc/modules");
                if (modules.Contains("nvmet", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Ignore
        }

        return false;
    }

    private string GetNvmeCliVersion()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = NvmeCliPath,
                Arguments = "version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            using var process = Process.Start(psi);
            if (process != null)
            {
                var output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);

                // Parse version from output like "nvme version 2.0"
                var match = VersionRegex().Match(output);
                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }
        }
        catch
        {
            // Ignore errors
        }

        return "unknown";
    }

    [GeneratedRegex(@"version\s+(\d+\.\d+(?:\.\d+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();

    private static string GetTransportString(NvmeOfTransport transport)
    {
        return transport switch
        {
            NvmeOfTransport.Tcp => "tcp",
            NvmeOfTransport.Rdma => "rdma",
            NvmeOfTransport.FibreChannel => "fc",
            _ => "tcp"
        };
    }

    private async Task<(int exitCode, string stdout, string stderr)> ExecuteNvmeCommandAsync(
        string arguments,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = NvmeCliPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        var completed = await Task.WhenAny(
            process.WaitForExitAsync(cancellationToken),
            Task.Delay(CommandTimeoutMs, cancellationToken));

        if (!process.HasExited)
        {
            process.Kill();
            throw new TimeoutException($"nvme command timed out: {arguments}");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return (process.ExitCode, stdout, stderr);
    }

    private static IReadOnlyList<NvmeOfDiscoveryEntry> ParseDiscoveryOutput(
        string output,
        NvmeOfTransport transport,
        string queryAddress)
    {
        var entries = new List<NvmeOfDiscoveryEntry>();

        try
        {
            // Try to parse as JSON first (nvme-cli 2.0+)
            if (output.TrimStart().StartsWith('{') || output.TrimStart().StartsWith('['))
            {
                var json = JsonDocument.Parse(output);
                // Parse JSON discovery output
                // Structure varies by nvme-cli version
            }
            else
            {
                // Parse text output
                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                NvmeOfDiscoveryEntry? current = null;

                foreach (var line in lines)
                {
                    var trimmed = line.Trim();

                    if (trimmed.StartsWith("=====") && current != null)
                    {
                        entries.Add(current);
                        current = null;
                        continue;
                    }

                    if (trimmed.StartsWith("subnqn:", StringComparison.OrdinalIgnoreCase))
                    {
                        current = new NvmeOfDiscoveryEntry
                        {
                            Transport = transport,
                            QueryAddress = queryAddress
                        };
                        current.Nqn = trimmed.Substring(7).Trim();
                    }
                    else if (current != null)
                    {
                        if (trimmed.StartsWith("traddr:", StringComparison.OrdinalIgnoreCase))
                        {
                            current.Address = trimmed.Substring(7).Trim();
                        }
                        else if (trimmed.StartsWith("trsvcid:", StringComparison.OrdinalIgnoreCase))
                        {
                            if (int.TryParse(trimmed.Substring(8).Trim(), out var port))
                            {
                                current.Port = port;
                            }
                        }
                        else if (trimmed.StartsWith("subtype:", StringComparison.OrdinalIgnoreCase))
                        {
                            current.SubsystemType = trimmed.Substring(8).Trim();
                        }
                    }
                }

                if (current != null)
                {
                    entries.Add(current);
                }
            }
        }
        catch
        {
            // Return empty list on parse error
        }

        return entries.AsReadOnly();
    }

    private static List<NvmeOfConnection> ParseSubsystemList(string output)
    {
        var connections = new List<NvmeOfConnection>();

        try
        {
            if (output.TrimStart().StartsWith('{') || output.TrimStart().StartsWith('['))
            {
                // Parse JSON output
                // Structure: {"Subsystems": [{"NQN": "...", "Paths": [...]}]}
                var json = JsonDocument.Parse(output);

                if (json.RootElement.TryGetProperty("Subsystems", out var subsystems))
                {
                    foreach (var subsys in subsystems.EnumerateArray())
                    {
                        var nqn = subsys.GetProperty("NQN").GetString() ?? string.Empty;

                        if (subsys.TryGetProperty("Paths", out var paths))
                        {
                            foreach (var path in paths.EnumerateArray())
                            {
                                var transport = NvmeOfTransport.Tcp;
                                if (path.TryGetProperty("Transport", out var trType))
                                {
                                    transport = trType.GetString()?.ToLowerInvariant() switch
                                    {
                                        "rdma" => NvmeOfTransport.Rdma,
                                        "fc" => NvmeOfTransport.FibreChannel,
                                        _ => NvmeOfTransport.Tcp
                                    };
                                }

                                var address = path.TryGetProperty("Address", out var addr)
                                    ? addr.GetString() ?? string.Empty
                                    : string.Empty;

                                var conn = new NvmeOfConnection
                                {
                                    ConnectionId = $"{nqn}@{address}",
                                    Nqn = nqn,
                                    Transport = transport,
                                    Address = address,
                                    State = NvmeOfConnectionState.Connected,
                                    ConnectedAt = DateTime.UtcNow
                                };

                                connections.Add(conn);
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Return empty on parse error
        }

        return connections;
    }

    private void LoadExistingConnections()
    {
        // Will be populated on first ListConnectionsAsync call
    }

    private void LoadExistingSubsystems()
    {
        if (!Directory.Exists(NvmetConfigPath))
        {
            return;
        }

        var subsystemsPath = Path.Combine(NvmetConfigPath, "subsystems");
        if (!Directory.Exists(subsystemsPath))
        {
            return;
        }

        try
        {
            foreach (var subsysDir in Directory.GetDirectories(subsystemsPath))
            {
                var nqn = Path.GetFileName(subsysDir);
                var subsystem = new NvmeOfSubsystem
                {
                    Nqn = nqn,
                    State = NvmeOfSubsystemState.Active,
                    CreatedAt = Directory.GetCreationTimeUtc(subsysDir)
                };

                // Check allow_any_host
                var allowAnyHostPath = Path.Combine(subsysDir, "attr_allow_any_host");
                if (File.Exists(allowAnyHostPath))
                {
                    subsystem.AllowAnyHost = File.ReadAllText(allowAnyHostPath).Trim() == "1";
                }

                // Load namespaces
                var namespacesPath = Path.Combine(subsysDir, "namespaces");
                if (Directory.Exists(namespacesPath))
                {
                    var namespaces = new List<NvmeOfNamespace>();
                    foreach (var nsDir in Directory.GetDirectories(namespacesPath))
                    {
                        if (int.TryParse(Path.GetFileName(nsDir), out var nsId))
                        {
                            var ns = new NvmeOfNamespace
                            {
                                NamespaceId = nsId
                            };

                            var devicePathFile = Path.Combine(nsDir, "device_path");
                            if (File.Exists(devicePathFile))
                            {
                                ns.DevicePath = File.ReadAllText(devicePathFile).Trim();
                            }

                            var enableFile = Path.Combine(nsDir, "enable");
                            if (File.Exists(enableFile))
                            {
                                ns.Enabled = File.ReadAllText(enableFile).Trim() == "1";
                            }

                            namespaces.Add(ns);
                        }
                    }
                    subsystem.Namespaces = namespaces;
                }

                _subsystems[nqn] = subsystem;
            }
        }
        catch
        {
            // Best effort loading
        }
    }

    private async Task<List<string>> FindConnectedDevicesAsync(
        string nqn,
        CancellationToken cancellationToken)
    {
        var devices = new List<string>();

        try
        {
            var args = "list -o json";
            var (exitCode, stdout, stderr) = await ExecuteNvmeCommandAsync(args, cancellationToken);

            if (exitCode == 0 && !string.IsNullOrEmpty(stdout))
            {
                var json = JsonDocument.Parse(stdout);
                if (json.RootElement.TryGetProperty("Devices", out var devArray))
                {
                    foreach (var dev in devArray.EnumerateArray())
                    {
                        if (dev.TryGetProperty("SubsystemNQN", out var subsysNqn) &&
                            subsysNqn.GetString() == nqn)
                        {
                            if (dev.TryGetProperty("DevicePath", out var devPath))
                            {
                                devices.Add(devPath.GetString() ?? string.Empty);
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore errors finding devices
        }

        return devices;
    }

    private static bool IsValidNqn(string nqn)
    {
        // NQN format: nqn.yyyy-mm.reverse.domain:identifier
        // Example: nqn.2024-01.com.example:storage1
        if (string.IsNullOrWhiteSpace(nqn))
        {
            return false;
        }

        if (!nqn.StartsWith("nqn.", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Basic validation - could be more strict
        return nqn.Length >= 12 && nqn.Contains('.');
    }

    /// <inheritdoc />
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "NVMeoF";
        metadata["IsAvailable"] = IsAvailable;
        metadata["TargetModeAvailable"] = _targetModeAvailable;
        metadata["NvmeCliVersion"] = _nvmeCliVersion;
        metadata["ActiveConnections"] = _connections.Count;
        metadata["ConfiguredSubsystems"] = _subsystems.Count;
        metadata["DiscoverOperations"] = Interlocked.Read(ref _discoverOperations);
        metadata["ConnectOperations"] = Interlocked.Read(ref _connectOperations);
        metadata["DisconnectOperations"] = Interlocked.Read(ref _disconnectOperations);
        metadata["SupportedTransports"] = "TCP, RDMA, FC";
        return metadata;
    }

    #endregion
}

#region Supporting Types

/// <summary>
/// NVMe-oF transport types.
/// </summary>
public enum NvmeOfTransport
{
    /// <summary>TCP/IP transport.</summary>
    Tcp,

    /// <summary>RDMA transport (InfiniBand, RoCE, iWARP).</summary>
    Rdma,

    /// <summary>Fibre Channel transport.</summary>
    FibreChannel
}

/// <summary>
/// NVMe-oF connection state.
/// </summary>
public enum NvmeOfConnectionState
{
    /// <summary>Connection is being established.</summary>
    Connecting,

    /// <summary>Connection is active.</summary>
    Connected,

    /// <summary>Connection is being disconnected.</summary>
    Disconnecting,

    /// <summary>Connection has been disconnected.</summary>
    Disconnected,

    /// <summary>Connection is in error state.</summary>
    Error
}

/// <summary>
/// NVMe-oF subsystem state (target mode).
/// </summary>
public enum NvmeOfSubsystemState
{
    /// <summary>Subsystem is created but not active.</summary>
    Created,

    /// <summary>Subsystem is active and accepting connections.</summary>
    Active,

    /// <summary>Subsystem is disabled.</summary>
    Disabled
}

/// <summary>
/// NVMe-oF discovery entry returned from target discovery.
/// </summary>
public class NvmeOfDiscoveryEntry
{
    /// <summary>Gets or sets the NQN of the discovered subsystem.</summary>
    public string Nqn { get; set; } = string.Empty;

    /// <summary>Gets or sets the transport type.</summary>
    public NvmeOfTransport Transport { get; set; }

    /// <summary>Gets or sets the target address.</summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>Gets or sets the target port.</summary>
    public int Port { get; set; }

    /// <summary>Gets or sets the subsystem type.</summary>
    public string SubsystemType { get; set; } = string.Empty;

    /// <summary>Gets or sets the address used for the discovery query.</summary>
    public string QueryAddress { get; set; } = string.Empty;
}

/// <summary>
/// Options for NVMe-oF connections.
/// </summary>
public class NvmeOfConnectOptions
{
    /// <summary>Gets or sets the queue depth (I/O queue size).</summary>
    public int QueueDepth { get; set; }

    /// <summary>Gets or sets the number of I/O queues.</summary>
    public int NumberOfQueues { get; set; }

    /// <summary>Gets or sets the keep-alive timeout in seconds.</summary>
    public int KeepAliveTimeout { get; set; }

    /// <summary>Gets or sets the reconnect delay in seconds.</summary>
    public int ReconnectDelay { get; set; }

    /// <summary>Gets or sets the controller loss timeout in seconds.</summary>
    public int ControllerLossTimeout { get; set; }

    /// <summary>Gets or sets the host NQN.</summary>
    public string? HostNqn { get; set; }

    /// <summary>Gets or sets the host ID.</summary>
    public string? HostId { get; set; }

    /// <summary>Gets or sets whether to allow duplicate connections.</summary>
    public bool Duplicate { get; set; }
}

/// <summary>
/// Represents an active NVMe-oF connection.
/// </summary>
public class NvmeOfConnection
{
    /// <summary>Gets or sets the unique connection identifier.</summary>
    public string ConnectionId { get; init; } = string.Empty;

    /// <summary>Gets or sets the NQN of the connected subsystem.</summary>
    public string Nqn { get; init; } = string.Empty;

    /// <summary>Gets or sets the transport type.</summary>
    public NvmeOfTransport Transport { get; init; }

    /// <summary>Gets or sets the target address.</summary>
    public string Address { get; init; } = string.Empty;

    /// <summary>Gets or sets the target port.</summary>
    public int Port { get; init; }

    /// <summary>Gets or sets the connection state.</summary>
    public NvmeOfConnectionState State { get; set; }

    /// <summary>Gets or sets the connection timestamp.</summary>
    public DateTime ConnectedAt { get; init; }

    /// <summary>Gets or sets the local device paths.</summary>
    public List<string> LocalDevices { get; set; } = new();

    /// <summary>Gets or sets the connection options.</summary>
    public NvmeOfConnectOptions? Options { get; init; }
}

/// <summary>
/// Represents an NVMe-oF subsystem (target mode).
/// </summary>
public class NvmeOfSubsystem
{
    /// <summary>Gets or sets the NQN.</summary>
    public string Nqn { get; init; } = string.Empty;

    /// <summary>Gets or sets whether any host can connect.</summary>
    public bool AllowAnyHost { get; set; }

    /// <summary>Gets or sets the subsystem state.</summary>
    public NvmeOfSubsystemState State { get; set; }

    /// <summary>Gets or sets the creation timestamp.</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>Gets or sets the list of namespaces.</summary>
    public IList<NvmeOfNamespace> Namespaces { get; set; } = new List<NvmeOfNamespace>();
}

/// <summary>
/// Represents an NVMe-oF namespace (exported device).
/// </summary>
public class NvmeOfNamespace
{
    /// <summary>Gets or sets the namespace ID.</summary>
    public int NamespaceId { get; init; }

    /// <summary>Gets or sets the device path being exported.</summary>
    public string DevicePath { get; set; } = string.Empty;

    /// <summary>Gets or sets whether the namespace is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the creation timestamp.</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>Gets or sets the namespace size in bytes.</summary>
    public long Size { get; set; }
}

/// <summary>
/// Represents an NVMe-oF port (target mode).
/// </summary>
public class NvmeOfPort
{
    /// <summary>Gets or sets the port identifier.</summary>
    public int PortId { get; init; }

    /// <summary>Gets or sets the transport type.</summary>
    public NvmeOfTransport Transport { get; init; }

    /// <summary>Gets or sets the bind address.</summary>
    public string Address { get; init; } = string.Empty;

    /// <summary>Gets or sets the service port number.</summary>
    public int ServicePort { get; init; }

    /// <summary>Gets or sets the address family (ipv4 or ipv6).</summary>
    public string AddressFamily { get; init; } = "ipv4";

    /// <summary>Gets or sets the creation timestamp.</summary>
    public DateTime CreatedAt { get; init; }
}

/// <summary>
/// Exception thrown for NVMe-oF specific errors.
/// </summary>
public class NvmeOfException : Exception
{
    /// <summary>Gets the transport type involved in the error.</summary>
    public NvmeOfTransport? Transport { get; }

    /// <summary>Gets the address involved in the error.</summary>
    public string? Address { get; }

    /// <summary>Gets the port involved in the error.</summary>
    public int? Port { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="NvmeOfException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    public NvmeOfException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NvmeOfException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    public NvmeOfException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NvmeOfException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="transport">The transport type.</param>
    /// <param name="address">The target address.</param>
    /// <param name="port">The target port.</param>
    public NvmeOfException(string message, NvmeOfTransport transport, string address, int port)
        : base(message)
    {
        Transport = transport;
        Address = address;
        Port = port;
    }
}

#endregion
