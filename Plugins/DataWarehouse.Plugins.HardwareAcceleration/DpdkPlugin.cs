using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Plugins.HardwareAcceleration;

/// <summary>
/// DPDK (Data Plane Development Kit) plugin for high-performance packet processing.
/// Provides user-space networking capabilities with kernel bypass for ultra-low latency I/O.
/// </summary>
/// <remarks>
/// <para>
/// DPDK enables applications to bypass the kernel network stack entirely, achieving
/// multi-million packet per second throughput with microsecond latencies.
/// </para>
/// <para>
/// This plugin supports:
/// - EAL (Environment Abstraction Layer) initialization
/// - NIC port enumeration and binding to DPDK-compatible drivers
/// - Packet statistics collection
/// - Driver binding/unbinding (vfio-pci, igb_uio, uio_pci_generic)
/// </para>
/// <para>
/// Requirements:
/// - Linux operating system (DPDK is primarily Linux-focused)
/// - DPDK libraries installed (/usr/local/lib/librte_* or via RTE_SDK)
/// - Hugepages configured (typically 2MB or 1GB pages)
/// - IOMMU enabled for vfio-pci driver
/// - Root privileges for port binding operations
/// </para>
/// </remarks>
public class DpdkPlugin : FeaturePluginBase
{
    #region Constants

    /// <summary>
    /// Default DPDK installation paths to check.
    /// </summary>
    private static readonly string[] DpdkInstallPaths =
    {
        "/usr/local/share/dpdk",
        "/usr/share/dpdk",
        "/opt/dpdk",
        "/usr/local/lib",
        "/usr/lib/x86_64-linux-gnu"
    };

    /// <summary>
    /// DPDK library names to check for availability.
    /// </summary>
    private static readonly string[] DpdkLibraries =
    {
        "librte_eal.so",
        "librte_ethdev.so",
        "librte_mempool.so",
        "librte_mbuf.so",
        "librte_ring.so"
    };

    /// <summary>
    /// DPDK-compatible PMD (Poll Mode Driver) types.
    /// </summary>
    private static readonly string[] DpdkDrivers =
    {
        "vfio-pci",      // Recommended: IOMMU-based, secure
        "igb_uio",       // Legacy: requires igb_uio kernel module
        "uio_pci_generic" // Generic UIO driver
    };

    #endregion

    #region P/Invoke Declarations

    // librte_eal P/Invoke declarations
    [DllImport("librte_eal.so", EntryPoint = "rte_eal_init", CallingConvention = CallingConvention.Cdecl)]
    private static extern int rte_eal_init(int argc, IntPtr argv);

    [DllImport("librte_eal.so", EntryPoint = "rte_eal_cleanup", CallingConvention = CallingConvention.Cdecl)]
    private static extern int rte_eal_cleanup();

    [DllImport("librte_eal.so", EntryPoint = "rte_eal_process_type", CallingConvention = CallingConvention.Cdecl)]
    private static extern int rte_eal_process_type();

    [DllImport("librte_ethdev.so", EntryPoint = "rte_eth_dev_count_avail", CallingConvention = CallingConvention.Cdecl)]
    private static extern ushort rte_eth_dev_count_avail();

    [DllImport("librte_ethdev.so", EntryPoint = "rte_eth_dev_info_get", CallingConvention = CallingConvention.Cdecl)]
    private static extern int rte_eth_dev_info_get(ushort port_id, IntPtr dev_info);

    [DllImport("librte_ethdev.so", EntryPoint = "rte_eth_stats_get", CallingConvention = CallingConvention.Cdecl)]
    private static extern int rte_eth_stats_get(ushort port_id, ref RteEthStats stats);

    [DllImport("librte_ethdev.so", EntryPoint = "rte_eth_stats_reset", CallingConvention = CallingConvention.Cdecl)]
    private static extern int rte_eth_stats_reset(ushort port_id);

    /// <summary>
    /// DPDK ethernet statistics structure.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RteEthStats
    {
        public ulong ipackets;   // Total number of successfully received packets
        public ulong opackets;   // Total number of successfully transmitted packets
        public ulong ibytes;     // Total number of successfully received bytes
        public ulong obytes;     // Total number of successfully transmitted bytes
        public ulong imissed;    // Total of RX packets dropped by the HW
        public ulong ierrors;    // Total number of erroneous received packets
        public ulong oerrors;    // Total number of failed transmitted packets
        public ulong rx_nombuf;  // Total number of RX mbuf allocation failures
    }

    #endregion

    #region Fields

    private readonly object _lock = new();
    private bool _ealInitialized;
    private bool _dpdkAvailable;
    private bool _nativeDpdkAvailable;
    private string? _dpdkPath;
    private string? _rteSDkPath;
    private readonly ConcurrentDictionary<string, DpdkPortInfo> _boundPorts = new();
    private long _totalPacketsProcessed;
    private long _totalBytesProcessed;

    #endregion

    #region Plugin Properties

    /// <inheritdoc />
    public override string Id => "datawarehouse.hwaccel.dpdk";

    /// <inheritdoc />
    public override string Name => "DPDK Network Accelerator";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.OrchestrationProvider;

    /// <summary>
    /// Gets whether DPDK is available on the current system.
    /// Checks for DPDK installation, libraries, and kernel support.
    /// </summary>
    public bool IsAvailable => CheckDpdkAvailability();

    /// <summary>
    /// Gets whether the EAL has been initialized.
    /// </summary>
    public bool IsEalInitialized => _ealInitialized;

    /// <summary>
    /// Gets the detected DPDK installation path.
    /// </summary>
    public string? DpdkPath => _dpdkPath;

    /// <summary>
    /// Gets the RTE_SDK environment variable value if set.
    /// </summary>
    public string? RteSdkPath => _rteSDkPath;

    /// <summary>
    /// Gets whether native DPDK P/Invoke calls are available.
    /// </summary>
    public bool NativeCallsAvailable => _nativeDpdkAvailable;

    /// <summary>
    /// Gets the total number of packets processed through DPDK.
    /// </summary>
    public long TotalPacketsProcessed => Interlocked.Read(ref _totalPacketsProcessed);

    /// <summary>
    /// Gets the total number of bytes processed through DPDK.
    /// </summary>
    public long TotalBytesProcessed => Interlocked.Read(ref _totalBytesProcessed);

    #endregion

    #region Lifecycle Methods

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct)
    {
        CheckDpdkAvailability();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override async Task StopAsync()
    {
        if (_ealInitialized)
        {
            // Unbind all ports before cleanup
            foreach (var port in _boundPorts.Keys.ToList())
            {
                try
                {
                    await UnbindPortAsync(port);
                }
                catch
                {
                    // Best effort cleanup
                }
            }

            // Cleanup EAL if native calls available
            if (_nativeDpdkAvailable)
            {
                try
                {
                    rte_eal_cleanup();
                }
                catch
                {
                    // Native cleanup failed, continue
                }
            }

            _ealInitialized = false;
        }
    }

    #endregion

    #region Public API Methods

    /// <summary>
    /// Initializes the DPDK Environment Abstraction Layer (EAL).
    /// </summary>
    /// <param name="args">EAL initialization arguments (e.g., "-l 0-3", "-n 4", "--socket-mem=1024").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Task containing the initialization result.</returns>
    /// <exception cref="InvalidOperationException">Thrown when DPDK is not available or EAL init fails.</exception>
    /// <remarks>
    /// <para>Common EAL arguments include:</para>
    /// <list type="bullet">
    /// <item><description>-l cores: CPU cores to use (e.g., "-l 0-3" for cores 0-3)</description></item>
    /// <item><description>-n channels: Number of memory channels</description></item>
    /// <item><description>--socket-mem=MB: Hugepage memory per NUMA socket</description></item>
    /// <item><description>--proc-type=primary|secondary: Process type</description></item>
    /// <item><description>--file-prefix=prefix: Shared memory file prefix</description></item>
    /// <item><description>-a PCI: Allow-list specific PCI device</description></item>
    /// <item><description>-b PCI: Block-list specific PCI device</description></item>
    /// </list>
    /// </remarks>
    public async Task<DpdkInitResult> InitializeEalAsync(
        string[] args,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException(
                "DPDK is not available on this system. Ensure DPDK is installed and " +
                "the RTE_SDK environment variable is set or DPDK libraries are in the standard paths.");
        }

        if (_ealInitialized)
        {
            return new DpdkInitResult
            {
                Success = true,
                Message = "EAL already initialized",
                ProcessType = "primary"
            };
        }

        DpdkInitResult? earlyReturn = null;
        lock (_lock)
        {
            if (_ealInitialized)
            {
                earlyReturn = new DpdkInitResult
                {
                    Success = true,
                    Message = "EAL already initialized",
                    ProcessType = "primary"
                };
            }
        }

        if (earlyReturn != null)
        {
            return earlyReturn;
        }

        return await Task.Run(async () =>
        {
            try
            {
                // Try native initialization first
                if (_nativeDpdkAvailable)
                {
                    return InitializeEalNative(args);
                }

                // Fall back to shell-based initialization check
                return await InitializeEalShellAsync(args, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new DpdkInitResult
                {
                    Success = false,
                    Message = $"EAL initialization failed: {ex.Message}",
                    ErrorCode = -1
                };
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets a list of available DPDK-capable network ports.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of available DPDK ports with their information.</returns>
    /// <exception cref="InvalidOperationException">Thrown when DPDK is not available.</exception>
    public async Task<IReadOnlyList<DpdkPortInfo>> GetAvailablePortsAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("DPDK is not available on this system.");
        }

        return await Task.Run(() =>
        {
            var ports = new List<DpdkPortInfo>();

            try
            {
                // If EAL is initialized and native calls available, use DPDK API
                if (_ealInitialized && _nativeDpdkAvailable)
                {
                    ports.AddRange(GetPortsNative());
                }
                else
                {
                    // Fall back to scanning PCI devices via sysfs
                    ports.AddRange(GetPortsFromSysfs());
                }

                // Also include already bound ports
                foreach (var boundPort in _boundPorts.Values)
                {
                    if (!ports.Any(p => p.PciAddress == boundPort.PciAddress))
                    {
                        ports.Add(boundPort);
                    }
                }
            }
            catch (Exception ex)
            {
                // Log error but return empty list
                System.Diagnostics.Debug.WriteLine($"Error enumerating DPDK ports: {ex.Message}");
            }

            return ports;
        }, cancellationToken);
    }

    /// <summary>
    /// Binds a network port to a DPDK-compatible driver.
    /// </summary>
    /// <param name="pciAddress">PCI address of the port (e.g., "0000:00:1f.6").</param>
    /// <param name="driver">DPDK driver to use (vfio-pci, igb_uio, or uio_pci_generic).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of the bind operation.</returns>
    /// <exception cref="ArgumentException">Thrown when PCI address or driver is invalid.</exception>
    /// <exception cref="InvalidOperationException">Thrown when DPDK is not available or binding fails.</exception>
    /// <remarks>
    /// <para>Driver requirements:</para>
    /// <list type="bullet">
    /// <item><description>vfio-pci: Requires IOMMU enabled, most secure option</description></item>
    /// <item><description>igb_uio: Requires igb_uio kernel module loaded</description></item>
    /// <item><description>uio_pci_generic: Built-in UIO driver, less features</description></item>
    /// </list>
    /// <para>Note: Binding requires root privileges and will take the device away from the kernel driver.</para>
    /// </remarks>
    public async Task<DpdkBindResult> BindPortAsync(
        string pciAddress,
        string driver,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pciAddress))
        {
            throw new ArgumentException("PCI address cannot be null or empty.", nameof(pciAddress));
        }

        if (string.IsNullOrWhiteSpace(driver))
        {
            throw new ArgumentException("Driver cannot be null or empty.", nameof(driver));
        }

        if (!DpdkDrivers.Contains(driver))
        {
            throw new ArgumentException(
                $"Invalid DPDK driver '{driver}'. Valid options: {string.Join(", ", DpdkDrivers)}",
                nameof(driver));
        }

        if (!IsAvailable)
        {
            throw new InvalidOperationException("DPDK is not available on this system.");
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            throw new PlatformNotSupportedException("Port binding is only supported on Linux.");
        }

        return await Task.Run(() =>
        {
            try
            {
                // Validate PCI address exists
                var devicePath = $"/sys/bus/pci/devices/{pciAddress}";
                if (!Directory.Exists(devicePath))
                {
                    return new DpdkBindResult
                    {
                        Success = false,
                        PciAddress = pciAddress,
                        ErrorMessage = $"PCI device not found: {pciAddress}"
                    };
                }

                // Check if driver module is loaded
                if (!IsDriverModuleLoaded(driver))
                {
                    // Try to load the module
                    var loadResult = LoadDriverModule(driver);
                    if (!loadResult.Success)
                    {
                        return new DpdkBindResult
                        {
                            Success = false,
                            PciAddress = pciAddress,
                            ErrorMessage = $"Driver module '{driver}' is not loaded and could not be loaded: {loadResult.ErrorMessage}"
                        };
                    }
                }

                // Unbind from current driver first
                var currentDriver = GetCurrentDriver(pciAddress);
                if (!string.IsNullOrEmpty(currentDriver) && currentDriver != driver)
                {
                    var unbindResult = UnbindFromDriver(pciAddress, currentDriver);
                    if (!unbindResult)
                    {
                        return new DpdkBindResult
                        {
                            Success = false,
                            PciAddress = pciAddress,
                            PreviousDriver = currentDriver,
                            ErrorMessage = $"Failed to unbind from current driver '{currentDriver}'"
                        };
                    }
                }

                // Bind to new driver
                var bindSuccess = BindToDriver(pciAddress, driver);
                if (!bindSuccess)
                {
                    return new DpdkBindResult
                    {
                        Success = false,
                        PciAddress = pciAddress,
                        PreviousDriver = currentDriver,
                        ErrorMessage = $"Failed to bind to driver '{driver}'"
                    };
                }

                // Record the bound port
                var portInfo = new DpdkPortInfo
                {
                    PciAddress = pciAddress,
                    Driver = driver,
                    PreviousDriver = currentDriver,
                    IsBound = true,
                    BoundAt = DateTime.UtcNow
                };
                _boundPorts[pciAddress] = portInfo;

                return new DpdkBindResult
                {
                    Success = true,
                    PciAddress = pciAddress,
                    Driver = driver,
                    PreviousDriver = currentDriver,
                    Message = $"Successfully bound {pciAddress} to {driver}"
                };
            }
            catch (Exception ex)
            {
                return new DpdkBindResult
                {
                    Success = false,
                    PciAddress = pciAddress,
                    ErrorMessage = $"Bind operation failed: {ex.Message}"
                };
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Unbinds a network port from DPDK and returns it to the kernel driver.
    /// </summary>
    /// <param name="pciAddress">PCI address of the port to unbind.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of the unbind operation.</returns>
    /// <exception cref="ArgumentException">Thrown when PCI address is invalid.</exception>
    public async Task<DpdkBindResult> UnbindPortAsync(
        string pciAddress,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pciAddress))
        {
            throw new ArgumentException("PCI address cannot be null or empty.", nameof(pciAddress));
        }

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            throw new PlatformNotSupportedException("Port unbinding is only supported on Linux.");
        }

        return await Task.Run(() =>
        {
            try
            {
                _boundPorts.TryGetValue(pciAddress, out var portInfo);
                var currentDriver = GetCurrentDriver(pciAddress);

                if (string.IsNullOrEmpty(currentDriver))
                {
                    return new DpdkBindResult
                    {
                        Success = true,
                        PciAddress = pciAddress,
                        Message = "Device is not currently bound to any driver"
                    };
                }

                // Unbind from current driver
                var unbindResult = UnbindFromDriver(pciAddress, currentDriver);
                if (!unbindResult)
                {
                    return new DpdkBindResult
                    {
                        Success = false,
                        PciAddress = pciAddress,
                        ErrorMessage = $"Failed to unbind from driver '{currentDriver}'"
                    };
                }

                // Try to rebind to original kernel driver
                var originalDriver = portInfo?.PreviousDriver;
                if (!string.IsNullOrEmpty(originalDriver))
                {
                    BindToDriver(pciAddress, originalDriver);
                }
                else
                {
                    // Trigger driver probe to let kernel find appropriate driver
                    TriggerDriverProbe(pciAddress);
                }

                _boundPorts.TryRemove(pciAddress, out _);

                return new DpdkBindResult
                {
                    Success = true,
                    PciAddress = pciAddress,
                    PreviousDriver = currentDriver,
                    Driver = originalDriver,
                    Message = $"Successfully unbound {pciAddress} from {currentDriver}"
                };
            }
            catch (Exception ex)
            {
                return new DpdkBindResult
                {
                    Success = false,
                    PciAddress = pciAddress,
                    ErrorMessage = $"Unbind operation failed: {ex.Message}"
                };
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Gets packet and byte statistics for a DPDK port.
    /// </summary>
    /// <param name="portId">DPDK port ID or PCI address.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Port statistics including packets, bytes, errors.</returns>
    /// <exception cref="ArgumentException">Thrown when port ID is invalid.</exception>
    /// <exception cref="InvalidOperationException">Thrown when EAL is not initialized or statistics cannot be retrieved.</exception>
    public async Task<DpdkPortStats> GetPortStatsAsync(
        string portId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(portId))
        {
            throw new ArgumentException("Port ID cannot be null or empty.", nameof(portId));
        }

        return await Task.Run(() =>
        {
            try
            {
                // If EAL is initialized and we have a numeric port ID, use native API
                if (_ealInitialized && _nativeDpdkAvailable && ushort.TryParse(portId, out var numericPortId))
                {
                    return GetPortStatsNative(numericPortId);
                }

                // Try to get stats from sysfs for bound ports
                if (_boundPorts.TryGetValue(portId, out var portInfo))
                {
                    return GetPortStatsFromSysfs(portInfo.PciAddress);
                }

                // Return empty stats if not found
                return new DpdkPortStats
                {
                    PortId = portId,
                    Timestamp = DateTime.UtcNow,
                    ErrorMessage = "Port not found or statistics not available"
                };
            }
            catch (Exception ex)
            {
                return new DpdkPortStats
                {
                    PortId = portId,
                    Timestamp = DateTime.UtcNow,
                    ErrorMessage = $"Failed to get port statistics: {ex.Message}"
                };
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Checks if hugepages are configured on the system.
    /// </summary>
    /// <returns>Hugepage configuration information.</returns>
    public async Task<HugepageInfo> GetHugepageInfoAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var info = new HugepageInfo();

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                info.IsConfigured = false;
                info.ErrorMessage = "Hugepages are only supported on Linux";
                return info;
            }

            try
            {
                // Read 2MB hugepages info
                var hp2mbPath = "/sys/kernel/mm/hugepages/hugepages-2048kB";
                if (Directory.Exists(hp2mbPath))
                {
                    var totalPath = Path.Combine(hp2mbPath, "nr_hugepages");
                    var freePath = Path.Combine(hp2mbPath, "free_hugepages");

                    if (File.Exists(totalPath))
                    {
                        info.Total2MbPages = int.Parse(File.ReadAllText(totalPath).Trim());
                    }
                    if (File.Exists(freePath))
                    {
                        info.Free2MbPages = int.Parse(File.ReadAllText(freePath).Trim());
                    }
                }

                // Read 1GB hugepages info
                var hp1gbPath = "/sys/kernel/mm/hugepages/hugepages-1048576kB";
                if (Directory.Exists(hp1gbPath))
                {
                    var totalPath = Path.Combine(hp1gbPath, "nr_hugepages");
                    var freePath = Path.Combine(hp1gbPath, "free_hugepages");

                    if (File.Exists(totalPath))
                    {
                        info.Total1GbPages = int.Parse(File.ReadAllText(totalPath).Trim());
                    }
                    if (File.Exists(freePath))
                    {
                        info.Free1GbPages = int.Parse(File.ReadAllText(freePath).Trim());
                    }
                }

                info.IsConfigured = info.Total2MbPages > 0 || info.Total1GbPages > 0;
                info.TotalMemoryMb = (info.Total2MbPages * 2) + (info.Total1GbPages * 1024);
                info.FreeMemoryMb = (info.Free2MbPages * 2) + (info.Free1GbPages * 1024);
            }
            catch (Exception ex)
            {
                info.IsConfigured = false;
                info.ErrorMessage = $"Failed to read hugepage info: {ex.Message}";
            }

            return info;
        }, cancellationToken);
    }

    #endregion

    #region Private Helper Methods

    private bool CheckDpdkAvailability()
    {
        if (_dpdkAvailable)
        {
            return true;
        }

        // Only available on Linux
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return false;
        }

        // Check RTE_SDK environment variable
        _rteSDkPath = Environment.GetEnvironmentVariable("RTE_SDK");
        if (!string.IsNullOrEmpty(_rteSDkPath) && Directory.Exists(_rteSDkPath))
        {
            _dpdkPath = _rteSDkPath;
            _dpdkAvailable = true;
        }

        // Check standard installation paths
        foreach (var path in DpdkInstallPaths)
        {
            if (Directory.Exists(path))
            {
                // Check for DPDK libraries
                foreach (var lib in DpdkLibraries)
                {
                    var libPath = Path.Combine(path, lib);
                    if (File.Exists(libPath))
                    {
                        _dpdkPath = path;
                        _dpdkAvailable = true;
                        break;
                    }
                }
            }

            if (_dpdkAvailable) break;
        }

        // Check for DPDK tools (dpdk-devbind.py)
        if (!_dpdkAvailable)
        {
            var devbindPaths = new[]
            {
                "/usr/local/bin/dpdk-devbind.py",
                "/usr/bin/dpdk-devbind.py",
                "/usr/share/dpdk/usertools/dpdk-devbind.py"
            };

            foreach (var path in devbindPaths)
            {
                if (File.Exists(path))
                {
                    _dpdkPath = Path.GetDirectoryName(path);
                    _dpdkAvailable = true;
                    break;
                }
            }
        }

        // Check if native calls are available
        if (_dpdkAvailable)
        {
            try
            {
                // Try to load the library to verify it's usable
                var testCount = rte_eth_dev_count_avail();
                _nativeDpdkAvailable = true;
            }
            catch
            {
                _nativeDpdkAvailable = false;
            }
        }

        return _dpdkAvailable;
    }

    private DpdkInitResult InitializeEalNative(string[] args)
    {
        try
        {
            // Prepare arguments for rte_eal_init
            var fullArgs = new List<string> { "datawarehouse-dpdk" };
            fullArgs.AddRange(args);

            // Allocate native memory for arguments
            var argc = fullArgs.Count;
            var argvPtrs = new IntPtr[argc + 1];

            for (int i = 0; i < argc; i++)
            {
                argvPtrs[i] = Marshal.StringToHGlobalAnsi(fullArgs[i]);
            }
            argvPtrs[argc] = IntPtr.Zero;

            var argv = Marshal.AllocHGlobal(IntPtr.Size * (argc + 1));
            Marshal.Copy(argvPtrs, 0, argv, argc + 1);

            try
            {
                var result = rte_eal_init(argc, argv);

                if (result < 0)
                {
                    return new DpdkInitResult
                    {
                        Success = false,
                        Message = "rte_eal_init failed",
                        ErrorCode = result
                    };
                }

                _ealInitialized = true;

                var processType = rte_eal_process_type();
                return new DpdkInitResult
                {
                    Success = true,
                    Message = "EAL initialized successfully",
                    ProcessType = processType == 0 ? "primary" : "secondary",
                    LcoresInitialized = result
                };
            }
            finally
            {
                // Free native memory
                for (int i = 0; i < argc; i++)
                {
                    Marshal.FreeHGlobal(argvPtrs[i]);
                }
                Marshal.FreeHGlobal(argv);
            }
        }
        catch (Exception ex)
        {
            return new DpdkInitResult
            {
                Success = false,
                Message = $"Native EAL init failed: {ex.Message}",
                ErrorCode = -1
            };
        }
    }

    private async Task<DpdkInitResult> InitializeEalShellAsync(string[] args, CancellationToken cancellationToken)
    {
        // Shell-based initialization just validates the environment
        // Actual EAL init requires native calls
        var hugepages = await GetHugepageInfoAsync(cancellationToken).ConfigureAwait(false);

        if (!hugepages.IsConfigured)
        {
            return new DpdkInitResult
            {
                Success = false,
                Message = "Hugepages not configured. DPDK requires hugepages for memory management.",
                ErrorCode = -1
            };
        }

        // Mark as initialized for non-native operations
        _ealInitialized = true;

        return new DpdkInitResult
        {
            Success = true,
            Message = "EAL environment validated (native calls not available, using shell fallback)",
            ProcessType = "shell-fallback"
        };
    }

    private IEnumerable<DpdkPortInfo> GetPortsNative()
    {
        var ports = new List<DpdkPortInfo>();

        try
        {
            var count = rte_eth_dev_count_avail();
            for (ushort i = 0; i < count; i++)
            {
                ports.Add(new DpdkPortInfo
                {
                    PortId = i,
                    IsBound = true,
                    Driver = "dpdk-pmd"
                });
            }
        }
        catch
        {
            // Native call failed
        }

        return ports;
    }

    private IEnumerable<DpdkPortInfo> GetPortsFromSysfs()
    {
        var ports = new List<DpdkPortInfo>();

        try
        {
            var netClassPath = "/sys/class/net";
            if (!Directory.Exists(netClassPath))
            {
                return ports;
            }

            foreach (var netInterface in Directory.GetDirectories(netClassPath))
            {
                var devicePath = Path.Combine(netInterface, "device");
                if (!Directory.Exists(devicePath))
                {
                    continue;
                }

                // Get PCI address from symlink
                var realPath = Path.GetFullPath(devicePath);
                var pciAddress = Path.GetFileName(realPath);

                if (!pciAddress.Contains(':'))
                {
                    continue;
                }

                var vendorPath = Path.Combine(devicePath, "vendor");
                var deviceIdPath = Path.Combine(devicePath, "device");

                if (!File.Exists(vendorPath) || !File.Exists(deviceIdPath))
                {
                    continue;
                }

                var vendorId = File.ReadAllText(vendorPath).Trim();
                var deviceId = File.ReadAllText(deviceIdPath).Trim();

                // Check if this is a network device that DPDK might support
                // Common vendors: Intel (0x8086), Mellanox (0x15b3), Broadcom (0x14e4)
                var supportedVendors = new[] { "0x8086", "0x15b3", "0x14e4", "0x1af4" };
                if (!supportedVendors.Contains(vendorId.ToLowerInvariant()))
                {
                    continue;
                }

                var currentDriver = GetCurrentDriver(pciAddress);
                var interfaceName = Path.GetFileName(netInterface);

                ports.Add(new DpdkPortInfo
                {
                    PciAddress = pciAddress,
                    InterfaceName = interfaceName,
                    VendorId = vendorId,
                    DeviceId = deviceId,
                    Driver = currentDriver,
                    IsBound = !string.IsNullOrEmpty(currentDriver) && DpdkDrivers.Contains(currentDriver),
                    VendorName = GetVendorName(vendorId)
                });
            }
        }
        catch
        {
            // Error scanning sysfs
        }

        return ports;
    }

    private static string GetVendorName(string vendorId)
    {
        return vendorId.ToLowerInvariant() switch
        {
            "0x8086" => "Intel",
            "0x15b3" => "Mellanox/NVIDIA",
            "0x14e4" => "Broadcom",
            "0x1af4" => "Red Hat Virtio",
            _ => "Unknown"
        };
    }

    private static string? GetCurrentDriver(string pciAddress)
    {
        try
        {
            var driverPath = $"/sys/bus/pci/devices/{pciAddress}/driver";
            if (Directory.Exists(driverPath))
            {
                var realPath = Path.GetFullPath(driverPath);
                return Path.GetFileName(realPath);
            }
        }
        catch
        {
            // Error reading driver
        }

        return null;
    }

    private static bool IsDriverModuleLoaded(string driver)
    {
        try
        {
            var modulesPath = "/proc/modules";
            if (File.Exists(modulesPath))
            {
                var modules = File.ReadAllText(modulesPath);
                var moduleName = driver.Replace("-", "_");
                return modules.Contains(moduleName, StringComparison.OrdinalIgnoreCase);
            }
        }
        catch
        {
            // Error reading modules
        }

        return false;
    }

    private static (bool Success, string? ErrorMessage) LoadDriverModule(string driver)
    {
        try
        {
            var moduleName = driver.Replace("-", "_");
            var psi = new ProcessStartInfo("modprobe", moduleName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            using var process = Process.Start(psi);
            process?.WaitForExit(10000);

            if (process?.ExitCode == 0)
            {
                return (true, null);
            }

            var error = process?.StandardError.ReadToEnd();
            return (false, error);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static bool UnbindFromDriver(string pciAddress, string driver)
    {
        try
        {
            var unbindPath = $"/sys/bus/pci/drivers/{driver}/unbind";
            if (File.Exists(unbindPath))
            {
                File.WriteAllText(unbindPath, pciAddress);
                return true;
            }
        }
        catch
        {
            // Error unbinding
        }

        return false;
    }

    private static bool BindToDriver(string pciAddress, string driver)
    {
        try
        {
            // First, write the new driver_override
            var overridePath = $"/sys/bus/pci/devices/{pciAddress}/driver_override";
            if (File.Exists(overridePath))
            {
                File.WriteAllText(overridePath, driver);
            }

            // Then bind to the driver
            var bindPath = $"/sys/bus/pci/drivers/{driver}/bind";
            if (File.Exists(bindPath))
            {
                File.WriteAllText(bindPath, pciAddress);
                return true;
            }

            // Try using new_id interface
            var newIdPath = $"/sys/bus/pci/drivers/{driver}/new_id";
            if (File.Exists(newIdPath))
            {
                var devicePath = $"/sys/bus/pci/devices/{pciAddress}";
                var vendorId = File.ReadAllText(Path.Combine(devicePath, "vendor")).Trim().Replace("0x", "");
                var deviceId = File.ReadAllText(Path.Combine(devicePath, "device")).Trim().Replace("0x", "");

                File.WriteAllText(newIdPath, $"{vendorId} {deviceId}");
                return true;
            }
        }
        catch
        {
            // Error binding
        }

        return false;
    }

    private static void TriggerDriverProbe(string pciAddress)
    {
        try
        {
            var driverProbePath = "/sys/bus/pci/drivers_probe";
            if (File.Exists(driverProbePath))
            {
                File.WriteAllText(driverProbePath, pciAddress);
            }
        }
        catch
        {
            // Error triggering probe
        }
    }

    private DpdkPortStats GetPortStatsNative(ushort portId)
    {
        var stats = new RteEthStats();
        var result = rte_eth_stats_get(portId, ref stats);

        if (result != 0)
        {
            return new DpdkPortStats
            {
                PortId = portId.ToString(),
                Timestamp = DateTime.UtcNow,
                ErrorMessage = $"Failed to get stats: error code {result}"
            };
        }

        Interlocked.Add(ref _totalPacketsProcessed, (long)(stats.ipackets + stats.opackets));
        Interlocked.Add(ref _totalBytesProcessed, (long)(stats.ibytes + stats.obytes));

        return new DpdkPortStats
        {
            PortId = portId.ToString(),
            Timestamp = DateTime.UtcNow,
            RxPackets = stats.ipackets,
            TxPackets = stats.opackets,
            RxBytes = stats.ibytes,
            TxBytes = stats.obytes,
            RxMissed = stats.imissed,
            RxErrors = stats.ierrors,
            TxErrors = stats.oerrors,
            RxNoMbuf = stats.rx_nombuf
        };
    }

    private static DpdkPortStats GetPortStatsFromSysfs(string pciAddress)
    {
        var stats = new DpdkPortStats
        {
            PortId = pciAddress,
            Timestamp = DateTime.UtcNow
        };

        try
        {
            // Try to find the network interface associated with this PCI device
            var netPath = $"/sys/bus/pci/devices/{pciAddress}/net";
            if (!Directory.Exists(netPath))
            {
                stats.ErrorMessage = "No network interface found for this device";
                return stats;
            }

            var interfaces = Directory.GetDirectories(netPath);
            if (interfaces.Length == 0)
            {
                stats.ErrorMessage = "No network interface found for this device";
                return stats;
            }

            var interfaceName = Path.GetFileName(interfaces[0]);
            var statsPath = $"/sys/class/net/{interfaceName}/statistics";

            if (Directory.Exists(statsPath))
            {
                stats.RxPackets = ReadStatFile(Path.Combine(statsPath, "rx_packets"));
                stats.TxPackets = ReadStatFile(Path.Combine(statsPath, "tx_packets"));
                stats.RxBytes = ReadStatFile(Path.Combine(statsPath, "rx_bytes"));
                stats.TxBytes = ReadStatFile(Path.Combine(statsPath, "tx_bytes"));
                stats.RxErrors = ReadStatFile(Path.Combine(statsPath, "rx_errors"));
                stats.TxErrors = ReadStatFile(Path.Combine(statsPath, "tx_errors"));
                stats.RxMissed = ReadStatFile(Path.Combine(statsPath, "rx_dropped"));
            }
        }
        catch (Exception ex)
        {
            stats.ErrorMessage = $"Failed to read stats: {ex.Message}";
        }

        return stats;
    }

    private static ulong ReadStatFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var content = File.ReadAllText(path).Trim();
                if (ulong.TryParse(content, out var value))
                {
                    return value;
                }
            }
        }
        catch
        {
            // Error reading stat file
        }

        return 0;
    }

    /// <inheritdoc />
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "DPDK";
        metadata["IsAvailable"] = IsAvailable;
        metadata["IsEalInitialized"] = _ealInitialized;
        metadata["NativeCallsAvailable"] = _nativeDpdkAvailable;
        metadata["DpdkPath"] = _dpdkPath ?? "Not found";
        metadata["RteSdkPath"] = _rteSDkPath ?? "Not set";
        metadata["BoundPorts"] = _boundPorts.Count;
        metadata["TotalPacketsProcessed"] = TotalPacketsProcessed;
        metadata["TotalBytesProcessed"] = TotalBytesProcessed;
        metadata["Platform"] = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "Linux" : "Unsupported";
        return metadata;
    }

    #endregion
}

#region Supporting Types

/// <summary>
/// Result of DPDK EAL initialization.
/// </summary>
public class DpdkInitResult
{
    /// <summary>
    /// Gets or sets whether initialization succeeded.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Gets or sets a message describing the result.
    /// </summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the error code if initialization failed.
    /// </summary>
    public int ErrorCode { get; init; }

    /// <summary>
    /// Gets or sets the DPDK process type (primary or secondary).
    /// </summary>
    public string? ProcessType { get; init; }

    /// <summary>
    /// Gets or sets the number of lcores initialized.
    /// </summary>
    public int LcoresInitialized { get; init; }
}

/// <summary>
/// Information about a DPDK-capable network port.
/// </summary>
public class DpdkPortInfo
{
    /// <summary>
    /// Gets or sets the DPDK port ID (numeric).
    /// </summary>
    public int PortId { get; init; } = -1;

    /// <summary>
    /// Gets or sets the PCI address (e.g., "0000:00:1f.6").
    /// </summary>
    public string PciAddress { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the kernel interface name (e.g., "eth0").
    /// </summary>
    public string? InterfaceName { get; init; }

    /// <summary>
    /// Gets or sets the PCI vendor ID.
    /// </summary>
    public string? VendorId { get; init; }

    /// <summary>
    /// Gets or sets the vendor name.
    /// </summary>
    public string? VendorName { get; init; }

    /// <summary>
    /// Gets or sets the PCI device ID.
    /// </summary>
    public string? DeviceId { get; init; }

    /// <summary>
    /// Gets or sets the current driver.
    /// </summary>
    public string? Driver { get; init; }

    /// <summary>
    /// Gets or sets the previous driver (before DPDK binding).
    /// </summary>
    public string? PreviousDriver { get; init; }

    /// <summary>
    /// Gets or sets whether the port is bound to a DPDK driver.
    /// </summary>
    public bool IsBound { get; init; }

    /// <summary>
    /// Gets or sets when the port was bound to DPDK.
    /// </summary>
    public DateTime? BoundAt { get; init; }

    /// <summary>
    /// Gets or sets the MAC address.
    /// </summary>
    public string? MacAddress { get; init; }

    /// <summary>
    /// Gets or sets the link speed in Mbps.
    /// </summary>
    public int LinkSpeedMbps { get; init; }
}

/// <summary>
/// Result of a DPDK port bind/unbind operation.
/// </summary>
public class DpdkBindResult
{
    /// <summary>
    /// Gets or sets whether the operation succeeded.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Gets or sets the PCI address of the port.
    /// </summary>
    public string PciAddress { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the driver the port is now bound to.
    /// </summary>
    public string? Driver { get; init; }

    /// <summary>
    /// Gets or sets the previous driver.
    /// </summary>
    public string? PreviousDriver { get; init; }

    /// <summary>
    /// Gets or sets a success message.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Gets or sets an error message if the operation failed.
    /// </summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Statistics for a DPDK port.
/// </summary>
public class DpdkPortStats
{
    /// <summary>
    /// Gets or sets the port identifier.
    /// </summary>
    public string PortId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the statistics were collected.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the total received packets.
    /// </summary>
    public ulong RxPackets { get; set; }

    /// <summary>
    /// Gets or sets the total transmitted packets.
    /// </summary>
    public ulong TxPackets { get; set; }

    /// <summary>
    /// Gets or sets the total received bytes.
    /// </summary>
    public ulong RxBytes { get; set; }

    /// <summary>
    /// Gets or sets the total transmitted bytes.
    /// </summary>
    public ulong TxBytes { get; set; }

    /// <summary>
    /// Gets or sets the number of missed RX packets.
    /// </summary>
    public ulong RxMissed { get; set; }

    /// <summary>
    /// Gets or sets the number of RX errors.
    /// </summary>
    public ulong RxErrors { get; set; }

    /// <summary>
    /// Gets or sets the number of TX errors.
    /// </summary>
    public ulong TxErrors { get; set; }

    /// <summary>
    /// Gets or sets the number of RX mbuf allocation failures.
    /// </summary>
    public ulong RxNoMbuf { get; set; }

    /// <summary>
    /// Gets or sets an error message if stats collection failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Gets the total packets (RX + TX).
    /// </summary>
    public ulong TotalPackets => RxPackets + TxPackets;

    /// <summary>
    /// Gets the total bytes (RX + TX).
    /// </summary>
    public ulong TotalBytes => RxBytes + TxBytes;

    /// <summary>
    /// Gets the total errors (RX + TX).
    /// </summary>
    public ulong TotalErrors => RxErrors + TxErrors;
}

/// <summary>
/// Information about hugepage configuration.
/// </summary>
public class HugepageInfo
{
    /// <summary>
    /// Gets or sets whether hugepages are configured.
    /// </summary>
    public bool IsConfigured { get; set; }

    /// <summary>
    /// Gets or sets the total number of 2MB hugepages.
    /// </summary>
    public int Total2MbPages { get; set; }

    /// <summary>
    /// Gets or sets the number of free 2MB hugepages.
    /// </summary>
    public int Free2MbPages { get; set; }

    /// <summary>
    /// Gets or sets the total number of 1GB hugepages.
    /// </summary>
    public int Total1GbPages { get; set; }

    /// <summary>
    /// Gets or sets the number of free 1GB hugepages.
    /// </summary>
    public int Free1GbPages { get; set; }

    /// <summary>
    /// Gets or sets the total hugepage memory in MB.
    /// </summary>
    public long TotalMemoryMb { get; set; }

    /// <summary>
    /// Gets or sets the free hugepage memory in MB.
    /// </summary>
    public long FreeMemoryMb { get; set; }

    /// <summary>
    /// Gets or sets an error message if hugepage info couldn't be read.
    /// </summary>
    public string? ErrorMessage { get; set; }
}

#endregion
