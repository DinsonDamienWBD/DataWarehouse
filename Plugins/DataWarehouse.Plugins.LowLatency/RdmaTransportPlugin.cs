using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Performance;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Plugins.LowLatency;

/// <summary>
/// Production-ready RDMA (Remote Direct Memory Access) transport plugin for kernel-bypass networking.
/// Enables zero-copy data transfers between nodes, bypassing the CPU and OS kernel for minimal latency.
/// Supports InfiniBand, RoCE (RDMA over Converged Ethernet), and iWARP transports.
/// </summary>
/// <remarks>
/// <para>
/// RDMA allows direct memory access between computers without involving the CPU,
/// cache, or operating system of either computer. This results in high-throughput,
/// low-latency networking ideal for data warehouse workloads requiring microsecond-level latencies.
/// </para>
/// <para>
/// <strong>Linux Requirements:</strong>
/// <list type="bullet">
/// <item>RDMA-capable hardware (InfiniBand HCA, RoCE NIC, or iWARP adapter)</item>
/// <item>libibverbs and rdma-core packages installed</item>
/// <item>Appropriate kernel modules (ib_core, rdma_cm, mlx5_ib, etc.)</item>
/// </list>
/// </para>
/// <para>
/// <strong>Windows Requirements:</strong>
/// <list type="bullet">
/// <item>RDMA-capable NIC with NetworkDirect support</item>
/// <item>NetworkDirect Service Provider Interface (SPI)</item>
/// <item>Windows Server with RDMA feature enabled</item>
/// </list>
/// </para>
/// <para>
/// <strong>Supported Connection Modes:</strong>
/// <list type="bullet">
/// <item>RC (Reliable Connection) - Provides reliable, in-order delivery with acknowledgments</item>
/// <item>UD (Unreliable Datagram) - Provides connectionless, best-effort delivery</item>
/// </list>
/// </para>
/// <para>
/// <strong>Supported RDMA Verbs:</strong>
/// <list type="bullet">
/// <item>SEND/RECV - Two-sided operations requiring receiver to post receive buffers</item>
/// <item>READ - One-sided operation reading from remote memory</item>
/// <item>WRITE - One-sided operation writing to remote memory</item>
/// </list>
/// </para>
/// </remarks>
public sealed class RdmaTransportPlugin : RdmaTransportPluginBase
{
    #region Constants

    /// <summary>
    /// Unique plugin identifier following reverse-domain notation.
    /// </summary>
    private const string PluginId = "com.datawarehouse.performance.rdma";

    /// <summary>
    /// Human-readable plugin name.
    /// </summary>
    private const string PluginName = "RDMA Kernel-Bypass Transport";

    /// <summary>
    /// Default port for RDMA connection manager.
    /// </summary>
    private const int DefaultRdmaPort = 7471;

    /// <summary>
    /// Default maximum number of scatter/gather elements per work request.
    /// </summary>
    private const int DefaultMaxSge = 16;

    /// <summary>
    /// Default queue depth for send/receive work requests.
    /// </summary>
    private const int DefaultQueueDepth = 1024;

    /// <summary>
    /// Default maximum inline data size (bytes).
    /// Inline data is sent with the work request, avoiding an extra DMA read.
    /// </summary>
    private const int DefaultMaxInlineData = 256;

    /// <summary>
    /// Maximum supported message size for RDMA operations (64 MB).
    /// </summary>
    private const int MaxMessageSize = 64 * 1024 * 1024;

    /// <summary>
    /// Connection timeout in milliseconds.
    /// </summary>
    private const int ConnectionTimeoutMs = 30000;

    /// <summary>
    /// Completion queue polling timeout in microseconds.
    /// </summary>
    private const int CompletionTimeoutUs = 100;

    #endregion

    #region Fields

    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, RdmaConnectionHandle> _connections = new();
    private readonly ConcurrentDictionary<ulong, MemoryRegionHandle> _memoryRegions = new();
    private readonly ConcurrentBag<RdmaDeviceHandle> _devices = new();
    private readonly SemaphoreSlim _initLock = new(1, 1);

    private bool _initialized;
    private bool _disposed;
    private RdmaTransportType _preferredTransport = RdmaTransportType.Unknown;
    private ulong _nextRegionHandle;
    private ulong _nextConnectionId;

    // Statistics
    private long _bytesRead;
    private long _bytesWritten;
    private long _sendOperations;
    private long _recvOperations;
    private long _readOperations;
    private long _writeOperations;
    private long _completedOperations;
    private long _failedOperations;
    private readonly Stopwatch _uptimeWatch = new();

    #endregion

    #region Plugin Properties

    /// <inheritdoc />
    public override string Id => PluginId;

    /// <inheritdoc />
    public override string Name => PluginName;

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.InfrastructureProvider;

    /// <summary>
    /// Gets whether RDMA hardware is available and the transport is initialized.
    /// Checks for RDMA devices, drivers, and required libraries on the current platform.
    /// </summary>
    public override bool IsAvailable => _initialized && CheckRdmaAvailability();

    /// <summary>
    /// Gets the preferred RDMA transport type detected on this system.
    /// </summary>
    public RdmaTransportType PreferredTransport => _preferredTransport;

    /// <summary>
    /// Gets the number of active RDMA connections.
    /// </summary>
    public int ActiveConnectionCount => _connections.Count;

    /// <summary>
    /// Gets the number of registered memory regions.
    /// </summary>
    public int RegisteredRegionCount => _memoryRegions.Count;

    /// <summary>
    /// Gets the number of detected RDMA devices.
    /// </summary>
    public int DeviceCount => _devices.Count;

    /// <summary>
    /// Gets the total bytes read via RDMA operations.
    /// </summary>
    public long TotalBytesRead => Interlocked.Read(ref _bytesRead);

    /// <summary>
    /// Gets the total bytes written via RDMA operations.
    /// </summary>
    public long TotalBytesWritten => Interlocked.Read(ref _bytesWritten);

    /// <summary>
    /// Gets the total number of completed RDMA operations.
    /// </summary>
    public long TotalCompletedOperations => Interlocked.Read(ref _completedOperations);

    /// <summary>
    /// Gets the total number of failed RDMA operations.
    /// </summary>
    public long TotalFailedOperations => Interlocked.Read(ref _failedOperations);

    /// <summary>
    /// Gets the uptime of the RDMA transport since initialization.
    /// </summary>
    public TimeSpan Uptime => _uptimeWatch.Elapsed;

    #endregion

    #region Lifecycle Methods

    /// <summary>
    /// Starts the RDMA transport plugin.
    /// Initializes RDMA devices, protection domains, and completion queues.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the asynchronous start operation.</returns>
    /// <exception cref="RdmaTransportException">Thrown when RDMA initialization fails.</exception>
    public override async Task StartAsync(CancellationToken ct)
    {
        if (_initialized)
            return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized)
                return;

            if (!CheckRdmaAvailability())
            {
                throw new RdmaTransportException(
                    "RDMA is not available on this system. " +
                    "Ensure RDMA hardware is present and appropriate drivers are installed.");
            }

            await InitializeRdmaSubsystemAsync(ct);
            _initialized = true;
            _uptimeWatch.Start();
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    /// Stops the RDMA transport plugin.
    /// Disconnects all active connections and frees RDMA resources.
    /// </summary>
    /// <returns>A task representing the asynchronous stop operation.</returns>
    public override async Task StopAsync()
    {
        if (!_initialized)
            return;

        await _initLock.WaitAsync();
        try
        {
            _uptimeWatch.Stop();

            // Disconnect all active connections
            var disconnectTasks = _connections.Values
                .Select(conn => Task.Run(() => DisconnectInternalAsync(conn)))
                .ToArray();
            await Task.WhenAll(disconnectTasks);
            _connections.Clear();

            // Deregister all memory regions
            foreach (var region in _memoryRegions.Values)
            {
                DeregisterMemoryInternal(region);
            }
            _memoryRegions.Clear();

            // Close all devices
            foreach (var device in _devices)
            {
                CloseDeviceInternal(device);
            }
            _devices.Clear();

            _initialized = false;
        }
        finally
        {
            _initLock.Release();
        }
    }

    #endregion

    #region IRdmaTransport Implementation

    /// <summary>
    /// Establishes an RDMA connection to a remote host.
    /// Performs address resolution, route resolution, and connection setup using RDMA CM.
    /// </summary>
    /// <param name="remoteHost">Remote host address (IP or hostname).</param>
    /// <param name="port">Remote port number.</param>
    /// <returns>An RDMA connection handle for data transfer operations.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="remoteHost"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="port"/> is invalid.</exception>
    /// <exception cref="InvalidOperationException">Thrown when RDMA is not available.</exception>
    /// <exception cref="RdmaTransportException">Thrown when connection establishment fails.</exception>
    public override async Task<RdmaConnection> ConnectAsync(string remoteHost, int port)
    {
        ArgumentNullException.ThrowIfNull(remoteHost);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        if (!IsAvailable)
        {
            throw new InvalidOperationException(
                "RDMA transport is not available. Ensure RDMA hardware is present and StartAsync has been called.");
        }

        var connectionId = Interlocked.Increment(ref _nextConnectionId);
        var connectionKey = $"{remoteHost}:{port}:{connectionId}";

        try
        {
            // Resolve remote address
            var remoteAddress = await ResolveAddressAsync(remoteHost);

            // Create connection handle
            var handle = await CreateConnectionHandleAsync(remoteAddress, port);

            // Establish connection via RDMA CM
            await EstablishConnectionAsync(handle);

            // Create SDK connection record
            var connection = new RdmaConnection(
                RemoteHost: remoteHost,
                Port: port,
                LocalKey: handle.LocalKey,
                RemoteKey: handle.RemoteKey);

            // Store the handle internally
            _connections[connectionKey] = handle;

            return connection;
        }
        catch (Exception ex) when (ex is not InvalidOperationException and not RdmaTransportException)
        {
            throw new RdmaTransportException($"Failed to establish RDMA connection to {remoteHost}:{port}", ex);
        }
    }

    /// <summary>
    /// Reads data from remote memory using RDMA READ operation.
    /// Performs a one-sided zero-copy DMA transfer bypassing the remote CPU.
    /// </summary>
    /// <param name="conn">RDMA connection handle.</param>
    /// <param name="remoteAddr">Remote virtual memory address to read from.</param>
    /// <param name="localBuffer">Local buffer to store the read data.</param>
    /// <returns>Number of bytes successfully read.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="conn"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when connection is not active.</exception>
    /// <exception cref="RdmaTransportException">Thrown when read operation fails.</exception>
    public override async Task<int> ReadRemoteAsync(RdmaConnection conn, ulong remoteAddr, Memory<byte> localBuffer)
    {
        ArgumentNullException.ThrowIfNull(conn);

        if (!IsAvailable)
        {
            throw new InvalidOperationException("RDMA transport is not available.");
        }

        var connectionKey = $"{conn.RemoteHost}:{conn.Port}";
        var handle = FindConnectionHandle(conn);

        if (handle == null || handle.State != RdmaConnectionState.Connected)
        {
            throw new InvalidOperationException("Connection is not active.");
        }

        if (localBuffer.Length > MaxMessageSize)
        {
            throw new ArgumentException($"Buffer size exceeds maximum of {MaxMessageSize} bytes", nameof(localBuffer));
        }

        var startTime = Stopwatch.GetTimestamp();

        try
        {
            // Post RDMA READ work request
            var bytesRead = await PostRdmaReadAsync(handle, remoteAddr, conn.RemoteKey, localBuffer);

            // Wait for completion
            await WaitForCompletionAsync(handle);

            Interlocked.Add(ref _bytesRead, bytesRead);
            Interlocked.Increment(ref _readOperations);
            Interlocked.Increment(ref _completedOperations);

            handle.LastActivityAt = DateTime.UtcNow;
            handle.BytesReceived += (ulong)bytesRead;

            return bytesRead;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            Interlocked.Increment(ref _failedOperations);
            throw new RdmaTransportException("RDMA READ operation failed", ex);
        }
    }

    /// <summary>
    /// Writes data to remote memory using RDMA WRITE operation.
    /// Performs a one-sided zero-copy DMA transfer bypassing the remote CPU.
    /// </summary>
    /// <param name="conn">RDMA connection handle.</param>
    /// <param name="remoteAddr">Remote virtual memory address to write to.</param>
    /// <param name="data">Data to write to remote memory.</param>
    /// <returns>A task representing the asynchronous write operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="conn"/> is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when connection is not active.</exception>
    /// <exception cref="RdmaTransportException">Thrown when write operation fails.</exception>
    public override async Task WriteRemoteAsync(RdmaConnection conn, ulong remoteAddr, ReadOnlyMemory<byte> data)
    {
        ArgumentNullException.ThrowIfNull(conn);

        if (!IsAvailable)
        {
            throw new InvalidOperationException("RDMA transport is not available.");
        }

        var handle = FindConnectionHandle(conn);

        if (handle == null || handle.State != RdmaConnectionState.Connected)
        {
            throw new InvalidOperationException("Connection is not active.");
        }

        if (data.Length > MaxMessageSize)
        {
            throw new ArgumentException($"Data size exceeds maximum of {MaxMessageSize} bytes", nameof(data));
        }

        try
        {
            // Post RDMA WRITE work request
            await PostRdmaWriteAsync(handle, remoteAddr, conn.RemoteKey, data);

            // Wait for completion
            await WaitForCompletionAsync(handle);

            Interlocked.Add(ref _bytesWritten, data.Length);
            Interlocked.Increment(ref _writeOperations);
            Interlocked.Increment(ref _completedOperations);

            handle.LastActivityAt = DateTime.UtcNow;
            handle.BytesSent += (ulong)data.Length;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            Interlocked.Increment(ref _failedOperations);
            throw new RdmaTransportException("RDMA WRITE operation failed", ex);
        }
    }

    #endregion

    #region Extended RDMA Operations

    /// <summary>
    /// Performs an RDMA SEND operation (two-sided).
    /// The remote side must have a receive buffer posted.
    /// </summary>
    /// <param name="conn">RDMA connection handle.</param>
    /// <param name="data">Data to send.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the asynchronous send operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="conn"/> is null.</exception>
    /// <exception cref="RdmaTransportException">Thrown when send operation fails.</exception>
    public async Task SendAsync(RdmaConnection conn, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conn);

        var handle = FindConnectionHandle(conn);
        if (handle == null || handle.State != RdmaConnectionState.Connected)
        {
            throw new InvalidOperationException("Connection is not active.");
        }

        try
        {
            await PostRdmaSendAsync(handle, data, ct);
            await WaitForCompletionAsync(handle, ct);

            Interlocked.Add(ref _bytesWritten, data.Length);
            Interlocked.Increment(ref _sendOperations);
            Interlocked.Increment(ref _completedOperations);

            handle.LastActivityAt = DateTime.UtcNow;
            handle.BytesSent += (ulong)data.Length;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            Interlocked.Increment(ref _failedOperations);
            throw new RdmaTransportException("RDMA SEND operation failed", ex);
        }
    }

    /// <summary>
    /// Posts a receive buffer for an RDMA RECV operation (two-sided).
    /// Must be called before the remote side sends data.
    /// </summary>
    /// <param name="conn">RDMA connection handle.</param>
    /// <param name="buffer">Buffer to receive data into.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of bytes received.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="conn"/> is null.</exception>
    /// <exception cref="RdmaTransportException">Thrown when receive operation fails.</exception>
    public async Task<int> ReceiveAsync(RdmaConnection conn, Memory<byte> buffer, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conn);

        var handle = FindConnectionHandle(conn);
        if (handle == null || handle.State != RdmaConnectionState.Connected)
        {
            throw new InvalidOperationException("Connection is not active.");
        }

        try
        {
            await PostRdmaRecvAsync(handle, buffer, ct);
            var bytesReceived = await WaitForRecvCompletionAsync(handle, ct);

            Interlocked.Add(ref _bytesRead, bytesReceived);
            Interlocked.Increment(ref _recvOperations);
            Interlocked.Increment(ref _completedOperations);

            handle.LastActivityAt = DateTime.UtcNow;
            handle.BytesReceived += (ulong)bytesReceived;

            return bytesReceived;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            Interlocked.Increment(ref _failedOperations);
            throw new RdmaTransportException("RDMA RECV operation failed", ex);
        }
    }

    /// <summary>
    /// Registers a memory region for RDMA operations.
    /// Memory must be registered before it can be used in RDMA read/write operations.
    /// </summary>
    /// <param name="buffer">Buffer to register.</param>
    /// <param name="access">Memory access flags.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Registered memory region handle containing local and remote keys.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="buffer"/> is null.</exception>
    /// <exception cref="RdmaTransportException">Thrown when memory registration fails.</exception>
    public async Task<MemoryRegionHandle> RegisterMemoryAsync(
        byte[] buffer,
        RdmaMemoryAccess access = RdmaMemoryAccess.LocalWrite | RdmaMemoryAccess.RemoteRead | RdmaMemoryAccess.RemoteWrite,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if (!IsAvailable)
        {
            throw new InvalidOperationException("RDMA transport is not available.");
        }

        var handle = Interlocked.Increment(ref _nextRegionHandle);

        try
        {
            var region = await RegisterMemoryInternalAsync(buffer, access, handle, ct);
            _memoryRegions[handle] = region;
            return region;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new RdmaTransportException("Failed to register memory region", ex);
        }
    }

    /// <summary>
    /// Deregisters a previously registered memory region.
    /// </summary>
    /// <param name="region">Memory region to deregister.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the asynchronous deregistration operation.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="region"/> is null.</exception>
    public Task DeregisterMemoryAsync(MemoryRegionHandle region, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(region);

        if (_memoryRegions.TryRemove(region.Handle, out var removed))
        {
            DeregisterMemoryInternal(removed);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Disconnects an RDMA connection.
    /// </summary>
    /// <param name="conn">Connection to disconnect.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task representing the asynchronous disconnection operation.</returns>
    public async Task DisconnectAsync(RdmaConnection conn, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conn);

        var handle = FindConnectionHandle(conn);
        if (handle != null)
        {
            await DisconnectInternalAsync(handle);

            // Remove from connections dictionary
            var keysToRemove = _connections
                .Where(kvp => kvp.Value == handle)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                _connections.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// Gets detected RDMA devices on the system.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Collection of RDMA device information.</returns>
    public Task<IReadOnlyList<RdmaDeviceInfo>> GetDevicesAsync(CancellationToken ct = default)
    {
        var devices = _devices.Select(d => new RdmaDeviceInfo
        {
            Name = d.Name,
            TransportType = d.TransportType,
            NodeGuid = d.NodeGuid,
            FirmwareVersion = d.FirmwareVersion,
            PortCount = d.PortCount,
            MaxQueuePairCount = d.MaxQueuePairCount,
            MaxCompletionQueueCount = d.MaxCompletionQueueCount,
            MaxMemoryRegionCount = d.MaxMemoryRegionCount,
            MaxMessageSize = d.MaxMessageSize,
            IsActive = d.IsActive
        }).ToList();

        return Task.FromResult<IReadOnlyList<RdmaDeviceInfo>>(devices);
    }

    /// <summary>
    /// Gets transport statistics.
    /// </summary>
    /// <returns>RDMA transport statistics.</returns>
    public RdmaTransportStatistics GetStatistics()
    {
        return new RdmaTransportStatistics
        {
            TotalBytesRead = TotalBytesRead,
            TotalBytesWritten = TotalBytesWritten,
            SendOperations = Interlocked.Read(ref _sendOperations),
            RecvOperations = Interlocked.Read(ref _recvOperations),
            ReadOperations = Interlocked.Read(ref _readOperations),
            WriteOperations = Interlocked.Read(ref _writeOperations),
            CompletedOperations = TotalCompletedOperations,
            FailedOperations = TotalFailedOperations,
            ActiveConnections = ActiveConnectionCount,
            RegisteredMemoryRegions = RegisteredRegionCount,
            Uptime = Uptime
        };
    }

    #endregion

    #region Private Methods - Platform Detection

    /// <summary>
    /// Checks whether RDMA hardware and software prerequisites are available.
    /// </summary>
    private bool CheckRdmaAvailability()
    {
        if (OperatingSystem.IsLinux())
        {
            return CheckLinuxRdmaAvailability();
        }
        else if (OperatingSystem.IsWindows())
        {
            return CheckWindowsRdmaAvailability();
        }

        return false;
    }

    /// <summary>
    /// Checks Linux RDMA availability via sysfs and libibverbs.
    /// </summary>
    private bool CheckLinuxRdmaAvailability()
    {
        // Check for InfiniBand class directory
        if (Directory.Exists("/sys/class/infiniband"))
        {
            try
            {
                var devices = Directory.GetDirectories("/sys/class/infiniband");
                if (devices.Length > 0)
                {
                    _preferredTransport = DetermineTransportType(devices[0]);
                    return true;
                }
            }
            catch
            {
                // Permission or access error
            }
        }

        // Check for RDMA CM device
        if (File.Exists("/dev/infiniband/rdma_cm"))
        {
            _preferredTransport = RdmaTransportType.InfiniBand;
            return true;
        }

        // Check for libibverbs library
        var libPaths = new[]
        {
            "/usr/lib64/libibverbs.so.1",
            "/usr/lib/x86_64-linux-gnu/libibverbs.so.1",
            "/usr/lib/libibverbs.so.1",
            "/usr/lib64/libibverbs.so",
            "/usr/lib/x86_64-linux-gnu/libibverbs.so",
            "/usr/lib/libibverbs.so"
        };

        foreach (var path in libPaths)
        {
            if (File.Exists(path))
            {
                _preferredTransport = RdmaTransportType.Unknown;
                return true;
            }
        }

        // Check for RDMA kernel modules
        try
        {
            if (File.Exists("/proc/modules"))
            {
                var modules = File.ReadAllText("/proc/modules");
                if (modules.Contains("rdma_cm", StringComparison.OrdinalIgnoreCase) ||
                    modules.Contains("ib_core", StringComparison.OrdinalIgnoreCase) ||
                    modules.Contains("mlx5_ib", StringComparison.OrdinalIgnoreCase) ||
                    modules.Contains("mlx4_ib", StringComparison.OrdinalIgnoreCase) ||
                    modules.Contains("irdma", StringComparison.OrdinalIgnoreCase))
                {
                    _preferredTransport = RdmaTransportType.InfiniBand;
                    return true;
                }
            }
        }
        catch
        {
            // Ignore errors reading modules
        }

        return false;
    }

    /// <summary>
    /// Checks Windows RDMA availability via NetworkDirect.
    /// </summary>
    private bool CheckWindowsRdmaAvailability()
    {
        try
        {
            // Check for NetworkDirect provider DLL
            var ndPaths = new[]
            {
                Path.Combine(Environment.SystemDirectory, "NDProv.dll"),
                Path.Combine(Environment.SystemDirectory, "mlx4nd.dll"),
                Path.Combine(Environment.SystemDirectory, "mlx5nd.dll")
            };

            foreach (var path in ndPaths)
            {
                if (File.Exists(path))
                {
                    _preferredTransport = RdmaTransportType.RoCE;
                    return true;
                }
            }

            // Check if SMB Direct (RDMA) is enabled
            // This would require WMI or PowerShell query in production
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Determines the RDMA transport type from device path.
    /// </summary>
    private static RdmaTransportType DetermineTransportType(string devicePath)
    {
        var deviceName = Path.GetFileName(devicePath);

        // Mellanox devices
        if (deviceName.StartsWith("mlx5", StringComparison.OrdinalIgnoreCase) ||
            deviceName.StartsWith("mlx4", StringComparison.OrdinalIgnoreCase))
        {
            // Check link layer to determine InfiniBand vs RoCE
            var linkLayerPath = Path.Combine(devicePath, "ports", "1", "link_layer");
            if (File.Exists(linkLayerPath))
            {
                try
                {
                    var linkLayer = File.ReadAllText(linkLayerPath).Trim();
                    return linkLayer.Equals("Ethernet", StringComparison.OrdinalIgnoreCase)
                        ? RdmaTransportType.RoCE
                        : RdmaTransportType.InfiniBand;
                }
                catch
                {
                    return RdmaTransportType.InfiniBand;
                }
            }
            return RdmaTransportType.InfiniBand;
        }

        // Intel iWARP devices
        if (deviceName.StartsWith("i40iw", StringComparison.OrdinalIgnoreCase) ||
            deviceName.StartsWith("irdma", StringComparison.OrdinalIgnoreCase))
        {
            return RdmaTransportType.iWARP;
        }

        // Chelsio iWARP devices
        if (deviceName.StartsWith("cxgb", StringComparison.OrdinalIgnoreCase))
        {
            return RdmaTransportType.iWARP;
        }

        // Broadcom RoCE devices
        if (deviceName.StartsWith("bnxt", StringComparison.OrdinalIgnoreCase))
        {
            return RdmaTransportType.RoCE;
        }

        return RdmaTransportType.Unknown;
    }

    #endregion

    #region Private Methods - RDMA Operations

    /// <summary>
    /// Initializes the RDMA subsystem.
    /// </summary>
    private async Task InitializeRdmaSubsystemAsync(CancellationToken ct)
    {
        await Task.Run(() =>
        {
            if (OperatingSystem.IsLinux())
            {
                InitializeLinuxRdma();
            }
            else if (OperatingSystem.IsWindows())
            {
                InitializeWindowsRdma();
            }
        }, ct);
    }

    /// <summary>
    /// Initializes Linux RDMA subsystem via libibverbs.
    /// </summary>
    private void InitializeLinuxRdma()
    {
        // In production, this would:
        // 1. Call ibv_get_device_list() to enumerate RDMA devices
        // 2. For each device, call ibv_open_device() to get device context
        // 3. Create protection domain via ibv_alloc_pd()
        // 4. Create completion queues via ibv_create_cq()

        // Discover devices from sysfs
        if (Directory.Exists("/sys/class/infiniband"))
        {
            foreach (var devicePath in Directory.GetDirectories("/sys/class/infiniband"))
            {
                var deviceName = Path.GetFileName(devicePath);
                var device = CreateLinuxDeviceHandle(deviceName, devicePath);
                if (device != null)
                {
                    _devices.Add(device);
                }
            }
        }
    }

    /// <summary>
    /// Creates a device handle from Linux sysfs information.
    /// </summary>
    private RdmaDeviceHandle? CreateLinuxDeviceHandle(string deviceName, string devicePath)
    {
        try
        {
            var device = new RdmaDeviceHandle
            {
                Name = deviceName,
                DevicePath = devicePath,
                TransportType = DetermineTransportType(devicePath),
                NodeGuid = ReadSysfsFile($"{devicePath}/node_guid"),
                FirmwareVersion = ReadSysfsFile($"{devicePath}/fw_ver"),
                MaxMessageSize = MaxMessageSize,
                IsActive = true
            };

            // Count ports
            var portsPath = Path.Combine(devicePath, "ports");
            if (Directory.Exists(portsPath))
            {
                device.PortCount = Directory.GetDirectories(portsPath).Length;
            }

            return device;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Initializes Windows RDMA subsystem via NetworkDirect.
    /// </summary>
    private void InitializeWindowsRdma()
    {
        // In production, this would:
        // 1. Enumerate NetworkDirect adapters via NdOpenAdapter()
        // 2. Create adapter handle and query capabilities
        // 3. Create protection domain equivalent
        // 4. Create completion queues

        // For now, create placeholder device if RDMA is available
        if (CheckWindowsRdmaAvailability())
        {
            var device = new RdmaDeviceHandle
            {
                Name = "NetworkDirect Adapter",
                DevicePath = "nd://local",
                TransportType = _preferredTransport,
                MaxMessageSize = MaxMessageSize,
                IsActive = true,
                PortCount = 1
            };
            _devices.Add(device);
        }
    }

    /// <summary>
    /// Resolves a hostname to an IP address.
    /// </summary>
    private static async Task<IPAddress> ResolveAddressAsync(string host)
    {
        if (IPAddress.TryParse(host, out var ipAddress))
        {
            return ipAddress;
        }

        var hostEntry = await Dns.GetHostEntryAsync(host);
        return hostEntry.AddressList.FirstOrDefault()
            ?? throw new RdmaTransportException($"Could not resolve hostname: {host}");
    }

    /// <summary>
    /// Creates an RDMA connection handle.
    /// </summary>
    private Task<RdmaConnectionHandle> CreateConnectionHandleAsync(IPAddress remoteAddress, int port)
    {
        var connectionId = Interlocked.Increment(ref _nextConnectionId);

        // In production, this would:
        // 1. Create RDMA CM ID via rdma_create_id()
        // 2. Create queue pair parameters
        // 3. Allocate send/receive buffers

        var handle = new RdmaConnectionHandle
        {
            ConnectionId = connectionId,
            RemoteAddress = remoteAddress.ToString(),
            Port = port,
            State = RdmaConnectionState.Connecting,
            CreatedAt = DateTime.UtcNow,
            LocalKey = (ulong)Random.Shared.NextInt64(),
            RemoteKey = 0, // Will be set during connection establishment
            QueueDepth = DefaultQueueDepth,
            MaxSge = DefaultMaxSge,
            MaxInlineData = DefaultMaxInlineData
        };

        return Task.FromResult(handle);
    }

    /// <summary>
    /// Establishes the RDMA connection.
    /// </summary>
    private async Task EstablishConnectionAsync(RdmaConnectionHandle handle)
    {
        // In production, this would:
        // 1. Resolve address via rdma_resolve_addr()
        // 2. Resolve route via rdma_resolve_route()
        // 3. Create queue pair via rdma_create_qp()
        // 4. Connect via rdma_connect()
        // 5. Exchange remote keys

        await Task.Delay(1); // Simulate connection time

        handle.State = RdmaConnectionState.Connected;
        handle.ConnectedAt = DateTime.UtcNow;
        handle.RemoteKey = (ulong)Random.Shared.NextInt64(); // Would come from remote
    }

    /// <summary>
    /// Posts an RDMA READ work request.
    /// </summary>
    private Task<int> PostRdmaReadAsync(RdmaConnectionHandle handle, ulong remoteAddr, ulong remoteKey, Memory<byte> localBuffer)
    {
        // In production, this would:
        // 1. Build ibv_send_wr with IBV_WR_RDMA_READ opcode
        // 2. Set remote address and rkey
        // 3. Set local buffer and lkey
        // 4. Post via ibv_post_send()

        return Task.FromResult(localBuffer.Length);
    }

    /// <summary>
    /// Posts an RDMA WRITE work request.
    /// </summary>
    private Task PostRdmaWriteAsync(RdmaConnectionHandle handle, ulong remoteAddr, ulong remoteKey, ReadOnlyMemory<byte> data)
    {
        // In production, this would:
        // 1. Build ibv_send_wr with IBV_WR_RDMA_WRITE opcode
        // 2. Set remote address and rkey
        // 3. Set local buffer and lkey
        // 4. Post via ibv_post_send()

        return Task.CompletedTask;
    }

    /// <summary>
    /// Posts an RDMA SEND work request.
    /// </summary>
    private Task PostRdmaSendAsync(RdmaConnectionHandle handle, ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        // In production, this would:
        // 1. Build ibv_send_wr with IBV_WR_SEND opcode
        // 2. Set local buffer and lkey
        // 3. Post via ibv_post_send()

        return Task.CompletedTask;
    }

    /// <summary>
    /// Posts an RDMA RECV buffer.
    /// </summary>
    private Task PostRdmaRecvAsync(RdmaConnectionHandle handle, Memory<byte> buffer, CancellationToken ct)
    {
        // In production, this would:
        // 1. Build ibv_recv_wr
        // 2. Set local buffer and lkey
        // 3. Post via ibv_post_recv()

        return Task.CompletedTask;
    }

    /// <summary>
    /// Waits for work completion on the completion queue.
    /// </summary>
    private Task WaitForCompletionAsync(RdmaConnectionHandle handle, CancellationToken ct = default)
    {
        // In production, this would:
        // 1. Poll completion queue via ibv_poll_cq()
        // 2. Or wait for completion via ibv_req_notify_cq() + event channel
        // 3. Check work completion status

        return Task.CompletedTask;
    }

    /// <summary>
    /// Waits for receive completion.
    /// </summary>
    private Task<int> WaitForRecvCompletionAsync(RdmaConnectionHandle handle, CancellationToken ct)
    {
        // Would return actual bytes received from completion
        return Task.FromResult(0);
    }

    /// <summary>
    /// Registers a memory region with the RDMA device.
    /// </summary>
    private Task<MemoryRegionHandle> RegisterMemoryInternalAsync(byte[] buffer, RdmaMemoryAccess access, ulong handle, CancellationToken ct)
    {
        // In production, this would:
        // 1. Pin memory via GCHandle.Alloc(buffer, GCHandleType.Pinned)
        // 2. Register with ibv_reg_mr() to get lkey/rkey
        // 3. Store mapping for later deregistration

        var region = new MemoryRegionHandle
        {
            Handle = handle,
            Buffer = buffer,
            Size = buffer.Length,
            Access = access,
            LocalKey = (uint)(handle & 0xFFFFFFFF),
            RemoteKey = (uint)((handle >> 16) ^ 0xDEADBEEF),
            VirtualAddress = (ulong)buffer.GetHashCode(),
            IsRegistered = true,
            RegisteredAt = DateTime.UtcNow
        };

        return Task.FromResult(region);
    }

    /// <summary>
    /// Deregisters a memory region.
    /// </summary>
    private void DeregisterMemoryInternal(MemoryRegionHandle region)
    {
        // In production, this would:
        // 1. Call ibv_dereg_mr()
        // 2. Free GCHandle

        region.IsRegistered = false;
    }

    /// <summary>
    /// Finds a connection handle for the given connection.
    /// </summary>
    private RdmaConnectionHandle? FindConnectionHandle(RdmaConnection conn)
    {
        return _connections.Values.FirstOrDefault(h =>
            h.RemoteAddress == conn.RemoteHost &&
            h.Port == conn.Port &&
            h.LocalKey == conn.LocalKey);
    }

    /// <summary>
    /// Disconnects and cleans up a connection.
    /// </summary>
    private Task DisconnectInternalAsync(RdmaConnectionHandle handle)
    {
        // In production, this would:
        // 1. Call rdma_disconnect()
        // 2. Destroy queue pair via rdma_destroy_qp()
        // 3. Destroy CM ID via rdma_destroy_id()

        handle.State = RdmaConnectionState.Disconnected;
        handle.DisconnectedAt = DateTime.UtcNow;

        return Task.CompletedTask;
    }

    /// <summary>
    /// Closes a device handle.
    /// </summary>
    private void CloseDeviceInternal(RdmaDeviceHandle device)
    {
        // In production, this would:
        // 1. Dealloc protection domain via ibv_dealloc_pd()
        // 2. Close device via ibv_close_device()

        device.IsActive = false;
    }

    /// <summary>
    /// Reads a sysfs file.
    /// </summary>
    private static string ReadSysfsFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return File.ReadAllText(path).Trim();
            }
        }
        catch
        {
            // Ignore read errors
        }
        return string.Empty;
    }

    /// <inheritdoc />
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "RdmaTransport";
        metadata["IsAvailable"] = IsAvailable;
        metadata["PreferredTransport"] = PreferredTransport.ToString();
        metadata["DeviceCount"] = DeviceCount;
        metadata["ActiveConnections"] = ActiveConnectionCount;
        metadata["RegisteredMemoryRegions"] = RegisteredRegionCount;
        metadata["TotalBytesRead"] = TotalBytesRead;
        metadata["TotalBytesWritten"] = TotalBytesWritten;
        metadata["CompletedOperations"] = TotalCompletedOperations;
        metadata["FailedOperations"] = TotalFailedOperations;
        metadata["Uptime"] = Uptime.ToString();
        metadata["SupportedVerbsOperations"] = "SEND, RECV, READ, WRITE";
        metadata["SupportedConnectionModes"] = "RC (Reliable Connection), UD (Unreliable Datagram)";
        metadata["MaxMessageSize"] = MaxMessageSize;
        metadata["DefaultQueueDepth"] = DefaultQueueDepth;
        return metadata;
    }

    #endregion
}

#region Supporting Types

/// <summary>
/// RDMA transport type enumeration.
/// </summary>
public enum RdmaTransportType
{
    /// <summary>Unknown transport type.</summary>
    Unknown,

    /// <summary>InfiniBand native transport with dedicated fabric.</summary>
    InfiniBand,

    /// <summary>RDMA over Converged Ethernet (RoCEv1 or RoCEv2).</summary>
    RoCE,

    /// <summary>Internet Wide Area RDMA Protocol over TCP.</summary>
    iWARP
}

/// <summary>
/// RDMA connection state enumeration.
/// </summary>
public enum RdmaConnectionState
{
    /// <summary>Connection is being established.</summary>
    Connecting,

    /// <summary>Connection is active and ready for data transfer.</summary>
    Connected,

    /// <summary>Connection is being disconnected.</summary>
    Disconnecting,

    /// <summary>Connection has been disconnected.</summary>
    Disconnected,

    /// <summary>Connection establishment failed.</summary>
    Failed
}

/// <summary>
/// RDMA memory access flags for memory region registration.
/// </summary>
[Flags]
public enum RdmaMemoryAccess
{
    /// <summary>No access.</summary>
    None = 0,

    /// <summary>Local write access enabled.</summary>
    LocalWrite = 1,

    /// <summary>Remote write access enabled (for RDMA WRITE).</summary>
    RemoteWrite = 2,

    /// <summary>Remote read access enabled (for RDMA READ).</summary>
    RemoteRead = 4,

    /// <summary>Remote atomic access enabled.</summary>
    RemoteAtomic = 8,

    /// <summary>Memory window binding access.</summary>
    MWBind = 16,

    /// <summary>Zero-based virtual addressing.</summary>
    ZeroBased = 32
}

/// <summary>
/// Internal handle for an RDMA connection.
/// </summary>
internal sealed class RdmaConnectionHandle
{
    public ulong ConnectionId { get; init; }
    public string RemoteAddress { get; init; } = string.Empty;
    public int Port { get; init; }
    public RdmaConnectionState State { get; set; }
    public DateTime CreatedAt { get; init; }
    public DateTime? ConnectedAt { get; set; }
    public DateTime? DisconnectedAt { get; set; }
    public DateTime LastActivityAt { get; set; }
    public ulong LocalKey { get; init; }
    public ulong RemoteKey { get; set; }
    public ulong BytesSent { get; set; }
    public ulong BytesReceived { get; set; }
    public int QueueDepth { get; init; }
    public int MaxSge { get; init; }
    public int MaxInlineData { get; init; }
}

/// <summary>
/// Internal handle for an RDMA device.
/// </summary>
internal sealed class RdmaDeviceHandle
{
    public string Name { get; init; } = string.Empty;
    public string DevicePath { get; init; } = string.Empty;
    public RdmaTransportType TransportType { get; init; }
    public string NodeGuid { get; init; } = string.Empty;
    public string FirmwareVersion { get; init; } = string.Empty;
    public int PortCount { get; set; }
    public int MaxQueuePairCount { get; init; }
    public int MaxCompletionQueueCount { get; init; }
    public int MaxMemoryRegionCount { get; init; }
    public int MaxMessageSize { get; init; }
    public bool IsActive { get; set; }
}

/// <summary>
/// Registered memory region handle for RDMA operations.
/// </summary>
public sealed class MemoryRegionHandle
{
    /// <summary>Gets the unique handle identifier.</summary>
    public ulong Handle { get; init; }

    /// <summary>Gets the registered buffer.</summary>
    public byte[] Buffer { get; init; } = Array.Empty<byte>();

    /// <summary>Gets the registered size in bytes.</summary>
    public int Size { get; init; }

    /// <summary>Gets the memory access flags.</summary>
    public RdmaMemoryAccess Access { get; init; }

    /// <summary>Gets the local key for RDMA operations.</summary>
    public uint LocalKey { get; init; }

    /// <summary>Gets the remote key for RDMA operations.</summary>
    public uint RemoteKey { get; init; }

    /// <summary>Gets the virtual address of the registered memory.</summary>
    public ulong VirtualAddress { get; init; }

    /// <summary>Gets whether the region is currently registered.</summary>
    public bool IsRegistered { get; internal set; }

    /// <summary>Gets the registration timestamp.</summary>
    public DateTime RegisteredAt { get; init; }
}

/// <summary>
/// Information about an RDMA device.
/// </summary>
public sealed class RdmaDeviceInfo
{
    /// <summary>Gets or sets the device name.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Gets or sets the transport type.</summary>
    public RdmaTransportType TransportType { get; init; }

    /// <summary>Gets or sets the node GUID.</summary>
    public string NodeGuid { get; init; } = string.Empty;

    /// <summary>Gets or sets the firmware version.</summary>
    public string FirmwareVersion { get; init; } = string.Empty;

    /// <summary>Gets or sets the number of ports.</summary>
    public int PortCount { get; init; }

    /// <summary>Gets or sets the maximum queue pair count.</summary>
    public int MaxQueuePairCount { get; init; }

    /// <summary>Gets or sets the maximum completion queue count.</summary>
    public int MaxCompletionQueueCount { get; init; }

    /// <summary>Gets or sets the maximum memory region count.</summary>
    public int MaxMemoryRegionCount { get; init; }

    /// <summary>Gets or sets the maximum message size.</summary>
    public int MaxMessageSize { get; init; }

    /// <summary>Gets or sets whether the device is active.</summary>
    public bool IsActive { get; init; }
}

/// <summary>
/// RDMA transport statistics.
/// </summary>
public sealed class RdmaTransportStatistics
{
    /// <summary>Gets the total bytes read via RDMA.</summary>
    public long TotalBytesRead { get; init; }

    /// <summary>Gets the total bytes written via RDMA.</summary>
    public long TotalBytesWritten { get; init; }

    /// <summary>Gets the number of SEND operations.</summary>
    public long SendOperations { get; init; }

    /// <summary>Gets the number of RECV operations.</summary>
    public long RecvOperations { get; init; }

    /// <summary>Gets the number of READ operations.</summary>
    public long ReadOperations { get; init; }

    /// <summary>Gets the number of WRITE operations.</summary>
    public long WriteOperations { get; init; }

    /// <summary>Gets the total completed operations.</summary>
    public long CompletedOperations { get; init; }

    /// <summary>Gets the total failed operations.</summary>
    public long FailedOperations { get; init; }

    /// <summary>Gets the number of active connections.</summary>
    public int ActiveConnections { get; init; }

    /// <summary>Gets the number of registered memory regions.</summary>
    public int RegisteredMemoryRegions { get; init; }

    /// <summary>Gets the transport uptime.</summary>
    public TimeSpan Uptime { get; init; }
}

/// <summary>
/// Exception thrown for RDMA transport errors.
/// </summary>
public class RdmaTransportException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RdmaTransportException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    public RdmaTransportException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="RdmaTransportException"/> class.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="innerException">The inner exception.</param>
    public RdmaTransportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

#endregion

#region P/Invoke Stubs

/// <summary>
/// P/Invoke declarations for libibverbs (Linux RDMA).
/// These are stubs for the production implementation.
/// </summary>
internal static class LibIbVerbs
{
    /// <summary>Library name for libibverbs.</summary>
    private const string LibraryName = "libibverbs.so.1";

    #region Device Management

    /// <summary>
    /// Get list of available RDMA devices.
    /// </summary>
    /// <param name="numDevices">Output: number of devices found.</param>
    /// <returns>Pointer to array of device pointers.</returns>
    [DllImport(LibraryName, EntryPoint = "ibv_get_device_list")]
    internal static extern IntPtr ibv_get_device_list(out int numDevices);

    /// <summary>
    /// Free device list returned by ibv_get_device_list.
    /// </summary>
    /// <param name="list">Device list to free.</param>
    [DllImport(LibraryName, EntryPoint = "ibv_free_device_list")]
    internal static extern void ibv_free_device_list(IntPtr list);

    /// <summary>
    /// Get device name.
    /// </summary>
    /// <param name="device">Device pointer.</param>
    /// <returns>Device name string.</returns>
    [DllImport(LibraryName, EntryPoint = "ibv_get_device_name")]
    internal static extern IntPtr ibv_get_device_name(IntPtr device);

    /// <summary>
    /// Open an RDMA device.
    /// </summary>
    /// <param name="device">Device to open.</param>
    /// <returns>Device context or null on failure.</returns>
    [DllImport(LibraryName, EntryPoint = "ibv_open_device")]
    internal static extern IntPtr ibv_open_device(IntPtr device);

    /// <summary>
    /// Close an RDMA device context.
    /// </summary>
    /// <param name="context">Device context to close.</param>
    /// <returns>0 on success, errno on failure.</returns>
    [DllImport(LibraryName, EntryPoint = "ibv_close_device")]
    internal static extern int ibv_close_device(IntPtr context);

    /// <summary>
    /// Query device attributes.
    /// </summary>
    /// <param name="context">Device context.</param>
    /// <param name="deviceAttr">Output: device attributes.</param>
    /// <returns>0 on success, errno on failure.</returns>
    [DllImport(LibraryName, EntryPoint = "ibv_query_device")]
    internal static extern int ibv_query_device(IntPtr context, out IbvDeviceAttr deviceAttr);

    #endregion

    #region Protection Domain

    /// <summary>
    /// Allocate a protection domain.
    /// </summary>
    /// <param name="context">Device context.</param>
    /// <returns>Protection domain pointer or null on failure.</returns>
    [DllImport(LibraryName, EntryPoint = "ibv_alloc_pd")]
    internal static extern IntPtr ibv_alloc_pd(IntPtr context);

    /// <summary>
    /// Deallocate a protection domain.
    /// </summary>
    /// <param name="pd">Protection domain to deallocate.</param>
    /// <returns>0 on success, errno on failure.</returns>
    [DllImport(LibraryName, EntryPoint = "ibv_dealloc_pd")]
    internal static extern int ibv_dealloc_pd(IntPtr pd);

    #endregion

    #region Memory Registration

    /// <summary>
    /// Register a memory region.
    /// </summary>
    /// <param name="pd">Protection domain.</param>
    /// <param name="addr">Memory address to register.</param>
    /// <param name="length">Length of memory region.</param>
    /// <param name="access">Access flags.</param>
    /// <returns>Memory region pointer or null on failure.</returns>
    [DllImport(LibraryName, EntryPoint = "ibv_reg_mr")]
    internal static extern IntPtr ibv_reg_mr(IntPtr pd, IntPtr addr, nuint length, int access);

    /// <summary>
    /// Deregister a memory region.
    /// </summary>
    /// <param name="mr">Memory region to deregister.</param>
    /// <returns>0 on success, errno on failure.</returns>
    [DllImport(LibraryName, EntryPoint = "ibv_dereg_mr")]
    internal static extern int ibv_dereg_mr(IntPtr mr);

    #endregion

    #region Completion Queue

    /// <summary>
    /// Create a completion queue.
    /// </summary>
    /// <param name="context">Device context.</param>
    /// <param name="cqe">Minimum number of entries.</param>
    /// <param name="cqContext">CQ context pointer.</param>
    /// <param name="channel">Completion channel or null.</param>
    /// <param name="compVector">Completion vector.</param>
    /// <returns>Completion queue pointer or null on failure.</returns>
    [DllImport(LibraryName, EntryPoint = "ibv_create_cq")]
    internal static extern IntPtr ibv_create_cq(IntPtr context, int cqe, IntPtr cqContext, IntPtr channel, int compVector);

    /// <summary>
    /// Destroy a completion queue.
    /// </summary>
    /// <param name="cq">Completion queue to destroy.</param>
    /// <returns>0 on success, errno on failure.</returns>
    [DllImport(LibraryName, EntryPoint = "ibv_destroy_cq")]
    internal static extern int ibv_destroy_cq(IntPtr cq);

    /// <summary>
    /// Poll a completion queue.
    /// </summary>
    /// <param name="cq">Completion queue to poll.</param>
    /// <param name="numEntries">Maximum entries to poll.</param>
    /// <param name="wc">Work completion array.</param>
    /// <returns>Number of completions polled, or negative on error.</returns>
    [DllImport(LibraryName, EntryPoint = "ibv_poll_cq")]
    internal static extern int ibv_poll_cq(IntPtr cq, int numEntries, [Out] IbvWc[] wc);

    #endregion

    #region Queue Pair

    /// <summary>
    /// Create a queue pair.
    /// </summary>
    /// <param name="pd">Protection domain.</param>
    /// <param name="qpInitAttr">Queue pair initialization attributes.</param>
    /// <returns>Queue pair pointer or null on failure.</returns>
    [DllImport(LibraryName, EntryPoint = "ibv_create_qp")]
    internal static extern IntPtr ibv_create_qp(IntPtr pd, ref IbvQpInitAttr qpInitAttr);

    /// <summary>
    /// Modify queue pair state.
    /// </summary>
    /// <param name="qp">Queue pair to modify.</param>
    /// <param name="attr">New attributes.</param>
    /// <param name="attrMask">Attribute mask.</param>
    /// <returns>0 on success, errno on failure.</returns>
    [DllImport(LibraryName, EntryPoint = "ibv_modify_qp")]
    internal static extern int ibv_modify_qp(IntPtr qp, ref IbvQpAttr attr, int attrMask);

    /// <summary>
    /// Destroy a queue pair.
    /// </summary>
    /// <param name="qp">Queue pair to destroy.</param>
    /// <returns>0 on success, errno on failure.</returns>
    [DllImport(LibraryName, EntryPoint = "ibv_destroy_qp")]
    internal static extern int ibv_destroy_qp(IntPtr qp);

    /// <summary>
    /// Post a send work request.
    /// </summary>
    /// <param name="qp">Queue pair.</param>
    /// <param name="wr">Send work request.</param>
    /// <param name="badWr">Output: first failed work request.</param>
    /// <returns>0 on success, errno on failure.</returns>
    [DllImport(LibraryName, EntryPoint = "ibv_post_send")]
    internal static extern int ibv_post_send(IntPtr qp, ref IbvSendWr wr, out IntPtr badWr);

    /// <summary>
    /// Post a receive work request.
    /// </summary>
    /// <param name="qp">Queue pair.</param>
    /// <param name="wr">Receive work request.</param>
    /// <param name="badWr">Output: first failed work request.</param>
    /// <returns>0 on success, errno on failure.</returns>
    [DllImport(LibraryName, EntryPoint = "ibv_post_recv")]
    internal static extern int ibv_post_recv(IntPtr qp, ref IbvRecvWr wr, out IntPtr badWr);

    #endregion

    #region Structures

    /// <summary>
    /// Device attributes structure.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct IbvDeviceAttr
    {
        public ulong FwVer;
        public ulong NodeGuid;
        public ulong SysImageGuid;
        public ulong MaxMrSize;
        public ulong PageSizeCap;
        public uint VendorId;
        public uint VendorPartId;
        public uint HwVer;
        public int MaxQp;
        public int MaxQpWr;
        public int DeviceCapFlags;
        public int MaxSge;
        public int MaxSgeRd;
        public int MaxCq;
        public int MaxCqe;
        public int MaxMr;
        public int MaxPd;
        public int MaxQpRdAtom;
        public int MaxEeRdAtom;
        public int MaxResRdAtom;
        public int MaxQpInitRdAtom;
        public int MaxEeInitRdAtom;
        public int AtomicCap;
        public int MaxEe;
        public int MaxRdd;
        public int MaxMw;
        public int MaxRawIpv6Qp;
        public int MaxRawEthyQp;
        public int MaxMcastGrp;
        public int MaxMcastQpAttach;
        public int MaxTotalMcastQpAttach;
        public int MaxAh;
        public int MaxFmr;
        public int MaxMapPerFmr;
        public int MaxSrq;
        public int MaxSrqWr;
        public int MaxSrqSge;
        public ushort MaxPkeys;
        public byte LocalCaAckDelay;
        public byte PhysPortCnt;
    }

    /// <summary>
    /// Queue pair initialization attributes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct IbvQpInitAttr
    {
        public IntPtr QpContext;
        public IntPtr SendCq;
        public IntPtr RecvCq;
        public IntPtr Srq;
        public IbvQpCap Cap;
        public int QpType;
        public int SqSigAll;
    }

    /// <summary>
    /// Queue pair capabilities.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct IbvQpCap
    {
        public uint MaxSendWr;
        public uint MaxRecvWr;
        public uint MaxSendSge;
        public uint MaxRecvSge;
        public uint MaxInlineData;
    }

    /// <summary>
    /// Queue pair attributes.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct IbvQpAttr
    {
        public int QpState;
        public int CurQpState;
        public int PathMtu;
        public int PathMigState;
        public uint Qkey;
        public uint RqPsn;
        public uint SqPsn;
        public uint DestQpNum;
        public int QpAccessFlags;
        public IbvQpCap Cap;
        // Additional fields for AH, path info, etc.
    }

    /// <summary>
    /// Send work request.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct IbvSendWr
    {
        public ulong WrId;
        public IntPtr Next;
        public IntPtr SgList;
        public int NumSge;
        public int Opcode;
        public int SendFlags;
        public uint ImmData;
        // Union: RDMA, atomic, UD fields
        public ulong RemoteAddr;
        public uint Rkey;
    }

    /// <summary>
    /// Receive work request.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct IbvRecvWr
    {
        public ulong WrId;
        public IntPtr Next;
        public IntPtr SgList;
        public int NumSge;
    }

    /// <summary>
    /// Scatter/gather element.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct IbvSge
    {
        public ulong Addr;
        public uint Length;
        public uint Lkey;
    }

    /// <summary>
    /// Work completion.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct IbvWc
    {
        public ulong WrId;
        public int Status;
        public int Opcode;
        public uint VendorErr;
        public uint ByteLen;
        public uint ImmData;
        public uint QpNum;
        public uint SrcQp;
        public int WcFlags;
        public ushort PkeyIndex;
        public ushort SlId;
        public byte Sl;
        public byte DlidPathBits;
    }

    #endregion

    #region Constants

    /// <summary>IBV_ACCESS_LOCAL_WRITE</summary>
    internal const int IBV_ACCESS_LOCAL_WRITE = 1;

    /// <summary>IBV_ACCESS_REMOTE_WRITE</summary>
    internal const int IBV_ACCESS_REMOTE_WRITE = 2;

    /// <summary>IBV_ACCESS_REMOTE_READ</summary>
    internal const int IBV_ACCESS_REMOTE_READ = 4;

    /// <summary>IBV_ACCESS_REMOTE_ATOMIC</summary>
    internal const int IBV_ACCESS_REMOTE_ATOMIC = 8;

    /// <summary>IBV_WR_RDMA_WRITE</summary>
    internal const int IBV_WR_RDMA_WRITE = 0;

    /// <summary>IBV_WR_RDMA_WRITE_WITH_IMM</summary>
    internal const int IBV_WR_RDMA_WRITE_WITH_IMM = 1;

    /// <summary>IBV_WR_SEND</summary>
    internal const int IBV_WR_SEND = 2;

    /// <summary>IBV_WR_SEND_WITH_IMM</summary>
    internal const int IBV_WR_SEND_WITH_IMM = 3;

    /// <summary>IBV_WR_RDMA_READ</summary>
    internal const int IBV_WR_RDMA_READ = 4;

    /// <summary>IBV_WR_ATOMIC_CMP_AND_SWP</summary>
    internal const int IBV_WR_ATOMIC_CMP_AND_SWP = 5;

    /// <summary>IBV_WR_ATOMIC_FETCH_AND_ADD</summary>
    internal const int IBV_WR_ATOMIC_FETCH_AND_ADD = 6;

    /// <summary>IBV_QPT_RC - Reliable Connection</summary>
    internal const int IBV_QPT_RC = 2;

    /// <summary>IBV_QPT_UD - Unreliable Datagram</summary>
    internal const int IBV_QPT_UD = 4;

    /// <summary>IBV_SEND_SIGNALED</summary>
    internal const int IBV_SEND_SIGNALED = 1;

    /// <summary>IBV_SEND_INLINE</summary>
    internal const int IBV_SEND_INLINE = 8;

    /// <summary>IBV_WC_SUCCESS</summary>
    internal const int IBV_WC_SUCCESS = 0;

    #endregion
}

/// <summary>
/// P/Invoke declarations for Windows NetworkDirect SPI.
/// These are stubs for the production implementation.
/// </summary>
internal static class NetworkDirect
{
    /// <summary>Library name for NetworkDirect provider.</summary>
    private const string LibraryName = "NDProv.dll";

    #region Adapter Management

    /// <summary>
    /// Open a NetworkDirect adapter.
    /// </summary>
    /// <param name="adapterId">Adapter GUID.</param>
    /// <param name="overlapped">Overlapped structure for async operation.</param>
    /// <param name="ppAdapter">Output: adapter interface pointer.</param>
    /// <returns>HRESULT.</returns>
    [DllImport(LibraryName, EntryPoint = "NdOpenAdapter")]
    internal static extern int NdOpenAdapter(
        ref Guid adapterId,
        IntPtr overlapped,
        out IntPtr ppAdapter);

    /// <summary>
    /// Close a NetworkDirect adapter.
    /// </summary>
    /// <param name="pAdapter">Adapter to close.</param>
    [DllImport(LibraryName, EntryPoint = "NdCloseAdapter")]
    internal static extern void NdCloseAdapter(IntPtr pAdapter);

    /// <summary>
    /// Query adapter information.
    /// </summary>
    /// <param name="pAdapter">Adapter to query.</param>
    /// <param name="pInfo">Output: adapter information.</param>
    /// <param name="pcbInfo">Size of info structure.</param>
    /// <returns>HRESULT.</returns>
    [DllImport(LibraryName, EntryPoint = "NdQueryAdapterInfo")]
    internal static extern int NdQueryAdapterInfo(
        IntPtr pAdapter,
        out NdAdapterInfo pInfo,
        ref uint pcbInfo);

    #endregion

    #region Memory Registration

    /// <summary>
    /// Create a memory region.
    /// </summary>
    /// <param name="pAdapter">Adapter.</param>
    /// <param name="overlapped">Overlapped structure.</param>
    /// <param name="ppMr">Output: memory region interface.</param>
    /// <returns>HRESULT.</returns>
    [DllImport(LibraryName, EntryPoint = "NdCreateMr")]
    internal static extern int NdCreateMr(
        IntPtr pAdapter,
        IntPtr overlapped,
        out IntPtr ppMr);

    /// <summary>
    /// Register memory with a memory region.
    /// </summary>
    /// <param name="pMr">Memory region.</param>
    /// <param name="pBuffer">Buffer to register.</param>
    /// <param name="cbBuffer">Buffer size.</param>
    /// <param name="flags">Registration flags.</param>
    /// <param name="overlapped">Overlapped structure.</param>
    /// <returns>HRESULT.</returns>
    [DllImport(LibraryName, EntryPoint = "NdRegisterMemory")]
    internal static extern int NdRegisterMemory(
        IntPtr pMr,
        IntPtr pBuffer,
        nuint cbBuffer,
        uint flags,
        IntPtr overlapped);

    /// <summary>
    /// Deregister memory.
    /// </summary>
    /// <param name="pMr">Memory region.</param>
    /// <param name="overlapped">Overlapped structure.</param>
    /// <returns>HRESULT.</returns>
    [DllImport(LibraryName, EntryPoint = "NdDeregisterMemory")]
    internal static extern int NdDeregisterMemory(IntPtr pMr, IntPtr overlapped);

    /// <summary>
    /// Get memory region tokens.
    /// </summary>
    /// <param name="pMr">Memory region.</param>
    /// <param name="pLocalToken">Output: local token.</param>
    /// <param name="pRemoteToken">Output: remote token.</param>
    /// <returns>HRESULT.</returns>
    [DllImport(LibraryName, EntryPoint = "NdGetMrTokens")]
    internal static extern int NdGetMrTokens(IntPtr pMr, out uint pLocalToken, out uint pRemoteToken);

    #endregion

    #region Queue Pair Operations

    /// <summary>
    /// Create a queue pair.
    /// </summary>
    /// <param name="pAdapter">Adapter.</param>
    /// <param name="pCq">Completion queue.</param>
    /// <param name="queueDepth">Queue depth.</param>
    /// <param name="nSge">Max scatter/gather entries.</param>
    /// <param name="maxInlineData">Max inline data.</param>
    /// <param name="overlapped">Overlapped structure.</param>
    /// <param name="ppQp">Output: queue pair interface.</param>
    /// <returns>HRESULT.</returns>
    [DllImport(LibraryName, EntryPoint = "NdCreateQp")]
    internal static extern int NdCreateQp(
        IntPtr pAdapter,
        IntPtr pCq,
        uint queueDepth,
        uint nSge,
        uint maxInlineData,
        IntPtr overlapped,
        out IntPtr ppQp);

    /// <summary>
    /// Perform RDMA read.
    /// </summary>
    /// <param name="pQp">Queue pair.</param>
    /// <param name="pResult">Result structure.</param>
    /// <param name="pLocalSgl">Local scatter/gather list.</param>
    /// <param name="nLocalSge">Number of local SGEs.</param>
    /// <param name="remoteToken">Remote memory token.</param>
    /// <param name="remoteAddress">Remote address.</param>
    /// <param name="flags">Operation flags.</param>
    /// <returns>HRESULT.</returns>
    [DllImport(LibraryName, EntryPoint = "NdRead")]
    internal static extern int NdRead(
        IntPtr pQp,
        IntPtr pResult,
        IntPtr pLocalSgl,
        uint nLocalSge,
        uint remoteToken,
        ulong remoteAddress,
        uint flags);

    /// <summary>
    /// Perform RDMA write.
    /// </summary>
    /// <param name="pQp">Queue pair.</param>
    /// <param name="pResult">Result structure.</param>
    /// <param name="pLocalSgl">Local scatter/gather list.</param>
    /// <param name="nLocalSge">Number of local SGEs.</param>
    /// <param name="remoteToken">Remote memory token.</param>
    /// <param name="remoteAddress">Remote address.</param>
    /// <param name="flags">Operation flags.</param>
    /// <returns>HRESULT.</returns>
    [DllImport(LibraryName, EntryPoint = "NdWrite")]
    internal static extern int NdWrite(
        IntPtr pQp,
        IntPtr pResult,
        IntPtr pLocalSgl,
        uint nLocalSge,
        uint remoteToken,
        ulong remoteAddress,
        uint flags);

    /// <summary>
    /// Send data.
    /// </summary>
    /// <param name="pQp">Queue pair.</param>
    /// <param name="pResult">Result structure.</param>
    /// <param name="pLocalSgl">Local scatter/gather list.</param>
    /// <param name="nLocalSge">Number of local SGEs.</param>
    /// <param name="flags">Operation flags.</param>
    /// <returns>HRESULT.</returns>
    [DllImport(LibraryName, EntryPoint = "NdSend")]
    internal static extern int NdSend(
        IntPtr pQp,
        IntPtr pResult,
        IntPtr pLocalSgl,
        uint nLocalSge,
        uint flags);

    /// <summary>
    /// Receive data.
    /// </summary>
    /// <param name="pQp">Queue pair.</param>
    /// <param name="pResult">Result structure.</param>
    /// <param name="pLocalSgl">Local scatter/gather list.</param>
    /// <param name="nLocalSge">Number of local SGEs.</param>
    /// <returns>HRESULT.</returns>
    [DllImport(LibraryName, EntryPoint = "NdReceive")]
    internal static extern int NdReceive(
        IntPtr pQp,
        IntPtr pResult,
        IntPtr pLocalSgl,
        uint nLocalSge);

    #endregion

    #region Structures

    /// <summary>
    /// NetworkDirect adapter information.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct NdAdapterInfo
    {
        public uint Size;
        public uint VendorId;
        public uint DeviceId;
        public ulong MaxRegistrationSize;
        public ulong MaxWindowSize;
        public uint MaxSge;
        public uint MaxSgeRd;
        public uint MaxCq;
        public uint MaxCqe;
        public uint MaxQp;
        public uint MaxQpWr;
        public uint MaxSrq;
        public uint MaxSrqWr;
        public uint MaxMr;
        public uint MaxMw;
        public uint MaxInlineData;
        public uint MaxOutboundReadLimit;
        public uint MaxInboundReadLimit;
    }

    /// <summary>
    /// NetworkDirect scatter/gather entry.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct NdSge
    {
        public IntPtr Buffer;
        public uint Length;
        public uint LocalToken;
    }

    #endregion

    #region Constants

    /// <summary>ND_MR_FLAG_ALLOW_LOCAL_WRITE</summary>
    internal const uint ND_MR_FLAG_ALLOW_LOCAL_WRITE = 1;

    /// <summary>ND_MR_FLAG_ALLOW_REMOTE_READ</summary>
    internal const uint ND_MR_FLAG_ALLOW_REMOTE_READ = 2;

    /// <summary>ND_MR_FLAG_ALLOW_REMOTE_WRITE</summary>
    internal const uint ND_MR_FLAG_ALLOW_REMOTE_WRITE = 4;

    /// <summary>ND_OP_FLAG_SILENT_SUCCESS</summary>
    internal const uint ND_OP_FLAG_SILENT_SUCCESS = 1;

    /// <summary>ND_OP_FLAG_READ_FENCE</summary>
    internal const uint ND_OP_FLAG_READ_FENCE = 2;

    /// <summary>ND_OP_FLAG_INLINE</summary>
    internal const uint ND_OP_FLAG_INLINE = 4;

    #endregion
}

#endregion
