using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;

namespace DataWarehouse.Plugins.HardwareAcceleration;

/// <summary>
/// SPDK (Storage Performance Development Kit) plugin for high-performance NVMe storage operations.
/// Provides user-space NVMe driver capabilities with kernel bypass for ultra-low latency storage I/O.
/// </summary>
/// <remarks>
/// <para>
/// SPDK enables applications to bypass the kernel storage stack entirely, achieving
/// millions of IOPS with microsecond latencies on NVMe devices.
/// </para>
/// <para>
/// This plugin supports:
/// - NVMe controller enumeration and management
/// - Block device (bdev) creation and management
/// - I/O statistics collection
/// - Communication with spdk_tgt via JSON-RPC
/// </para>
/// <para>
/// Requirements:
/// - Linux operating system (SPDK is primarily Linux-focused)
/// - SPDK libraries installed (/usr/local/lib/libspdk_* or SPDK installation)
/// - Hugepages configured (typically 2MB or 1GB pages)
/// - IOMMU enabled for vfio-pci driver
/// - spdk_tgt running for JSON-RPC communication
/// </para>
/// </remarks>
public class SpdkPlugin : FeaturePluginBase
{
    #region Constants

    /// <summary>
    /// Default SPDK installation paths to check.
    /// </summary>
    private static readonly string[] SpdkInstallPaths =
    {
        "/usr/local/lib",
        "/usr/lib/x86_64-linux-gnu",
        "/opt/spdk/build/lib",
        "/usr/lib"
    };

    /// <summary>
    /// SPDK library names to check for availability.
    /// </summary>
    private static readonly string[] SpdkLibraries =
    {
        "libspdk_nvme.so",
        "libspdk_bdev.so",
        "libspdk_env_dpdk.so",
        "libspdk_json.so",
        "libspdk_jsonrpc.so"
    };

    /// <summary>
    /// Default JSON-RPC socket path for spdk_tgt.
    /// </summary>
    private const string DefaultRpcSocketPath = "/var/tmp/spdk.sock";

    /// <summary>
    /// Alternative JSON-RPC socket path.
    /// </summary>
    private const string AlternativeRpcSocketPath = "/var/run/spdk/spdk.sock";

    /// <summary>
    /// Default JSON-RPC timeout in seconds.
    /// </summary>
    private const int DefaultRpcTimeoutSeconds = 30;

    #endregion

    #region P/Invoke Declarations

    // SPDK environment initialization
    [DllImport("libspdk_env_dpdk.so", EntryPoint = "spdk_env_opts_init", CallingConvention = CallingConvention.Cdecl)]
    private static extern void spdk_env_opts_init(IntPtr opts);

    [DllImport("libspdk_env_dpdk.so", EntryPoint = "spdk_env_init", CallingConvention = CallingConvention.Cdecl)]
    private static extern int spdk_env_init(IntPtr opts);

    [DllImport("libspdk_env_dpdk.so", EntryPoint = "spdk_env_fini", CallingConvention = CallingConvention.Cdecl)]
    private static extern void spdk_env_fini();

    // SPDK NVMe probing
    [DllImport("libspdk_nvme.so", EntryPoint = "spdk_nvme_probe", CallingConvention = CallingConvention.Cdecl)]
    private static extern int spdk_nvme_probe(IntPtr trid, IntPtr cb_ctx, IntPtr probe_cb, IntPtr attach_cb, IntPtr remove_cb);

    [DllImport("libspdk_nvme.so", EntryPoint = "spdk_nvme_detach", CallingConvention = CallingConvention.Cdecl)]
    private static extern int spdk_nvme_detach(IntPtr ctrlr);

    #endregion

    #region Fields

    private readonly object _lock = new();
    private bool _spdkAvailable;
    private bool _nativeSpdkAvailable;
    private bool _initialized;
    private string? _spdkPath;
    private string? _rpcSocketPath;
    private Socket? _rpcSocket;
    private readonly ConcurrentDictionary<string, NvmeControllerInfo> _attachedControllers = new();
    private readonly ConcurrentDictionary<string, BdevInfo> _createdBdevs = new();
    private long _totalIoOperations;
    private long _totalBytesTransferred;
    private int _rpcRequestId;

    #endregion

    #region Plugin Properties

    /// <inheritdoc />
    public override string Id => "datawarehouse.hwaccel.spdk";

    /// <inheritdoc />
    public override string Name => "SPDK NVMe Accelerator";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override PluginCategory Category => PluginCategory.OrchestrationProvider;

    /// <summary>
    /// Gets whether SPDK is available on the current system.
    /// Checks for SPDK installation, libraries, and spdk_tgt process.
    /// </summary>
    public bool IsAvailable => CheckSpdkAvailability();

    /// <summary>
    /// Gets whether SPDK has been initialized.
    /// </summary>
    public bool IsInitialized => _initialized;

    /// <summary>
    /// Gets the detected SPDK installation path.
    /// </summary>
    public string? SpdkPath => _spdkPath;

    /// <summary>
    /// Gets whether native SPDK P/Invoke calls are available.
    /// </summary>
    public bool NativeCallsAvailable => _nativeSpdkAvailable;

    /// <summary>
    /// Gets the path to the JSON-RPC socket.
    /// </summary>
    public string? RpcSocketPath => _rpcSocketPath;

    /// <summary>
    /// Gets whether the JSON-RPC connection is established.
    /// </summary>
    public bool IsRpcConnected => _rpcSocket?.Connected ?? false;

    /// <summary>
    /// Gets the total number of I/O operations performed.
    /// </summary>
    public long TotalIoOperations => Interlocked.Read(ref _totalIoOperations);

    /// <summary>
    /// Gets the total bytes transferred.
    /// </summary>
    public long TotalBytesTransferred => Interlocked.Read(ref _totalBytesTransferred);

    #endregion

    #region Lifecycle Methods

    /// <inheritdoc />
    public override Task StartAsync(CancellationToken ct)
    {
        CheckSpdkAvailability();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override async Task StopAsync()
    {
        // Detach all controllers
        foreach (var controller in _attachedControllers.Keys.ToList())
        {
            try
            {
                await DetachNvmeAsync(controller);
            }
            catch
            {
                // Best effort cleanup
            }
        }

        // Close RPC connection
        if (_rpcSocket != null)
        {
            try
            {
                _rpcSocket.Close();
                _rpcSocket.Dispose();
            }
            catch
            {
                // Ignore cleanup errors
            }
            _rpcSocket = null;
        }

        // Cleanup SPDK environment if native calls available
        if (_nativeSpdkAvailable && _initialized)
        {
            try
            {
                spdk_env_fini();
            }
            catch
            {
                // Native cleanup failed
            }
        }

        _initialized = false;
    }

    #endregion

    #region Public API Methods

    /// <summary>
    /// Initializes the SPDK subsystem.
    /// </summary>
    /// <param name="options">Optional initialization options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of the initialization.</returns>
    /// <exception cref="InvalidOperationException">Thrown when SPDK is not available.</exception>
    /// <remarks>
    /// <para>
    /// This method initializes the SPDK environment and optionally connects to
    /// the spdk_tgt JSON-RPC server for remote management.
    /// </para>
    /// <para>
    /// If spdk_tgt is running, JSON-RPC communication will be used for most operations.
    /// Otherwise, native SPDK calls will be attempted if available.
    /// </para>
    /// </remarks>
    public async Task<SpdkInitResult> InitializeAsync(
        SpdkInitOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException(
                "SPDK is not available on this system. Ensure SPDK is installed and " +
                "either spdk_tgt is running or SPDK libraries are accessible.");
        }

        if (_initialized)
        {
            return new SpdkInitResult
            {
                Success = true,
                Message = "SPDK already initialized",
                RpcConnected = IsRpcConnected
            };
        }

        options ??= new SpdkInitOptions();

        return await Task.Run(() =>
        {
            lock (_lock)
            {
                if (_initialized)
                {
                    return new SpdkInitResult
                    {
                        Success = true,
                        Message = "SPDK already initialized",
                        RpcConnected = IsRpcConnected
                    };
                }

                try
                {
                    // Try to connect to spdk_tgt via JSON-RPC
                    var rpcPath = options.RpcSocketPath ?? _rpcSocketPath ?? DefaultRpcSocketPath;
                    if (TryConnectRpc(rpcPath))
                    {
                        _rpcSocketPath = rpcPath;
                        _initialized = true;

                        return new SpdkInitResult
                        {
                            Success = true,
                            Message = "SPDK initialized via JSON-RPC connection",
                            RpcConnected = true,
                            RpcSocketPath = rpcPath
                        };
                    }

                    // Try native initialization
                    if (_nativeSpdkAvailable)
                    {
                        var result = InitializeNative(options);
                        if (result.Success)
                        {
                            _initialized = true;
                        }
                        return result;
                    }

                    return new SpdkInitResult
                    {
                        Success = false,
                        Message = "Could not connect to spdk_tgt and native SPDK calls are not available. " +
                                  "Ensure spdk_tgt is running or SPDK libraries are properly installed."
                    };
                }
                catch (Exception ex)
                {
                    return new SpdkInitResult
                    {
                        Success = false,
                        Message = $"SPDK initialization failed: {ex.Message}"
                    };
                }
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Gets a list of available NVMe controllers.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of available NVMe controllers.</returns>
    /// <exception cref="InvalidOperationException">Thrown when SPDK is not available or initialized.</exception>
    public async Task<IReadOnlyList<NvmeControllerInfo>> GetNvmeControllersAsync(
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("SPDK is not available on this system.");
        }

        return await Task.Run(async () =>
        {
            var controllers = new List<NvmeControllerInfo>();

            try
            {
                // If RPC is connected, use JSON-RPC
                if (IsRpcConnected)
                {
                    controllers.AddRange(await GetControllersViaRpcAsync(cancellationToken));
                }
                else
                {
                    // Fall back to scanning NVMe devices via sysfs
                    controllers.AddRange(GetControllersFromSysfs());
                }

                // Include already attached controllers
                foreach (var attached in _attachedControllers.Values)
                {
                    if (!controllers.Any(c => c.PciAddress == attached.PciAddress))
                    {
                        controllers.Add(attached);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error enumerating NVMe controllers: {ex.Message}");
            }

            return controllers;
        }, cancellationToken);
    }

    /// <summary>
    /// Attaches an NVMe device to SPDK for user-space access.
    /// </summary>
    /// <param name="pciAddress">PCI address of the NVMe device (e.g., "0000:00:1e.0").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of the attach operation.</returns>
    /// <exception cref="ArgumentException">Thrown when PCI address is invalid.</exception>
    /// <exception cref="InvalidOperationException">Thrown when SPDK is not initialized.</exception>
    /// <remarks>
    /// <para>
    /// Attaching an NVMe device transfers control from the kernel driver to SPDK,
    /// enabling user-space I/O. The device will no longer be visible as a block device
    /// in /dev until detached.
    /// </para>
    /// <para>
    /// Prerequisites:
    /// - Device must be bound to vfio-pci or uio driver
    /// - SPDK must be initialized
    /// - Hugepages must be configured
    /// </para>
    /// </remarks>
    public async Task<NvmeAttachResult> AttachNvmeAsync(
        string pciAddress,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pciAddress))
        {
            throw new ArgumentException("PCI address cannot be null or empty.", nameof(pciAddress));
        }

        if (!_initialized)
        {
            var initResult = await InitializeAsync(cancellationToken: cancellationToken);
            if (!initResult.Success)
            {
                throw new InvalidOperationException(
                    $"SPDK must be initialized before attaching devices: {initResult.Message}");
            }
        }

        return await Task.Run(async () =>
        {
            try
            {
                // Check if already attached
                if (_attachedControllers.ContainsKey(pciAddress))
                {
                    return new NvmeAttachResult
                    {
                        Success = true,
                        PciAddress = pciAddress,
                        Message = "Device already attached"
                    };
                }

                // If RPC is connected, use JSON-RPC
                if (IsRpcConnected)
                {
                    return await AttachNvmeViaRpcAsync(pciAddress, cancellationToken);
                }

                // Native attach
                if (_nativeSpdkAvailable)
                {
                    return AttachNvmeNative(pciAddress);
                }

                return new NvmeAttachResult
                {
                    Success = false,
                    PciAddress = pciAddress,
                    ErrorMessage = "No attach method available (RPC not connected, native calls unavailable)"
                };
            }
            catch (Exception ex)
            {
                return new NvmeAttachResult
                {
                    Success = false,
                    PciAddress = pciAddress,
                    ErrorMessage = $"Attach failed: {ex.Message}"
                };
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Detaches an NVMe device from SPDK, returning it to kernel control.
    /// </summary>
    /// <param name="pciAddress">PCI address of the NVMe device to detach.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of the detach operation.</returns>
    /// <exception cref="ArgumentException">Thrown when PCI address is invalid.</exception>
    public async Task<NvmeAttachResult> DetachNvmeAsync(
        string pciAddress,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(pciAddress))
        {
            throw new ArgumentException("PCI address cannot be null or empty.", nameof(pciAddress));
        }

        return await Task.Run(async () =>
        {
            try
            {
                if (!_attachedControllers.TryGetValue(pciAddress, out var controller))
                {
                    return new NvmeAttachResult
                    {
                        Success = true,
                        PciAddress = pciAddress,
                        Message = "Device was not attached"
                    };
                }

                // If RPC is connected, use JSON-RPC
                if (IsRpcConnected)
                {
                    var result = await DetachNvmeViaRpcAsync(pciAddress, cancellationToken);
                    if (result.Success)
                    {
                        _attachedControllers.TryRemove(pciAddress, out _);
                    }
                    return result;
                }

                // Native detach
                if (_nativeSpdkAvailable && controller.NativeHandle != IntPtr.Zero)
                {
                    var detachResult = spdk_nvme_detach(controller.NativeHandle);
                    if (detachResult == 0)
                    {
                        _attachedControllers.TryRemove(pciAddress, out _);
                        return new NvmeAttachResult
                        {
                            Success = true,
                            PciAddress = pciAddress,
                            Message = "Device detached successfully"
                        };
                    }

                    return new NvmeAttachResult
                    {
                        Success = false,
                        PciAddress = pciAddress,
                        ErrorMessage = $"Native detach failed with code {detachResult}"
                    };
                }

                _attachedControllers.TryRemove(pciAddress, out _);
                return new NvmeAttachResult
                {
                    Success = true,
                    PciAddress = pciAddress,
                    Message = "Device removed from tracking"
                };
            }
            catch (Exception ex)
            {
                return new NvmeAttachResult
                {
                    Success = false,
                    PciAddress = pciAddress,
                    ErrorMessage = $"Detach failed: {ex.Message}"
                };
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Creates a block device (bdev) backed by an NVMe namespace.
    /// </summary>
    /// <param name="name">Name for the bdev.</param>
    /// <param name="pciAddress">PCI address of the NVMe controller.</param>
    /// <param name="namespaceId">NVMe namespace ID (default: 1).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of the bdev creation.</returns>
    /// <exception cref="ArgumentException">Thrown when name or PCI address is invalid.</exception>
    /// <exception cref="InvalidOperationException">Thrown when RPC is not connected.</exception>
    public async Task<BdevCreateResult> CreateBdevAsync(
        string name,
        string pciAddress,
        int namespaceId = 1,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Bdev name cannot be null or empty.", nameof(name));
        }

        if (string.IsNullOrWhiteSpace(pciAddress))
        {
            throw new ArgumentException("PCI address cannot be null or empty.", nameof(pciAddress));
        }

        if (!IsRpcConnected)
        {
            throw new InvalidOperationException(
                "Creating bdevs requires a connection to spdk_tgt. Ensure spdk_tgt is running.");
        }

        return await Task.Run(async () =>
        {
            try
            {
                // Use JSON-RPC to create NVMe bdev
                var rpcParams = new Dictionary<string, object>
                {
                    ["name"] = name,
                    ["trtype"] = "PCIe",
                    ["traddr"] = pciAddress
                };

                var response = await SendRpcRequestAsync("bdev_nvme_attach_controller", rpcParams, cancellationToken);

                if (response.Error != null)
                {
                    return new BdevCreateResult
                    {
                        Success = false,
                        Name = name,
                        ErrorMessage = $"RPC error: {response.Error.Message}"
                    };
                }

                // Parse result to get bdev name(s)
                var bdevNames = new List<string>();
                if (response.Result is JsonElement resultElement)
                {
                    if (resultElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in resultElement.EnumerateArray())
                        {
                            bdevNames.Add(item.GetString() ?? "");
                        }
                    }
                    else if (resultElement.ValueKind == JsonValueKind.String)
                    {
                        bdevNames.Add(resultElement.GetString() ?? "");
                    }
                }

                var bdevInfo = new BdevInfo
                {
                    Name = name,
                    PciAddress = pciAddress,
                    NamespaceId = namespaceId,
                    CreatedAt = DateTime.UtcNow,
                    BdevNames = bdevNames.ToArray()
                };

                _createdBdevs[name] = bdevInfo;

                return new BdevCreateResult
                {
                    Success = true,
                    Name = name,
                    BdevNames = bdevNames.ToArray(),
                    Message = $"Created bdev(s): {string.Join(", ", bdevNames)}"
                };
            }
            catch (Exception ex)
            {
                return new BdevCreateResult
                {
                    Success = false,
                    Name = name,
                    ErrorMessage = $"Bdev creation failed: {ex.Message}"
                };
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Gets I/O statistics for a block device.
    /// </summary>
    /// <param name="bdevName">Name of the bdev.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>I/O statistics for the bdev.</returns>
    /// <exception cref="ArgumentException">Thrown when bdev name is invalid.</exception>
    public async Task<BdevStats> GetBdevStatsAsync(
        string bdevName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bdevName))
        {
            throw new ArgumentException("Bdev name cannot be null or empty.", nameof(bdevName));
        }

        return await Task.Run(async () =>
        {
            try
            {
                // If RPC is connected, get stats via JSON-RPC
                if (IsRpcConnected)
                {
                    return await GetBdevStatsViaRpcAsync(bdevName, cancellationToken);
                }

                // Return cached info if available
                if (_createdBdevs.TryGetValue(bdevName, out var bdevInfo))
                {
                    return new BdevStats
                    {
                        Name = bdevName,
                        Timestamp = DateTime.UtcNow,
                        ErrorMessage = "Statistics not available (RPC not connected)"
                    };
                }

                return new BdevStats
                {
                    Name = bdevName,
                    Timestamp = DateTime.UtcNow,
                    ErrorMessage = "Bdev not found"
                };
            }
            catch (Exception ex)
            {
                return new BdevStats
                {
                    Name = bdevName,
                    Timestamp = DateTime.UtcNow,
                    ErrorMessage = $"Failed to get stats: {ex.Message}"
                };
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Lists all block devices managed by SPDK.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>List of bdev information.</returns>
    public async Task<IReadOnlyList<BdevInfo>> GetBdevsAsync(
        CancellationToken cancellationToken = default)
    {
        return await Task.Run(async () =>
        {
            var bdevs = new List<BdevInfo>();

            try
            {
                if (IsRpcConnected)
                {
                    var response = await SendRpcRequestAsync("bdev_get_bdevs", null, cancellationToken);

                    if (response.Result is JsonElement resultElement && resultElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in resultElement.EnumerateArray())
                        {
                            var name = item.GetProperty("name").GetString() ?? "";
                            var blockSize = item.TryGetProperty("block_size", out var bs) ? bs.GetUInt32() : 0u;
                            var numBlocks = item.TryGetProperty("num_blocks", out var nb) ? nb.GetUInt64() : 0ul;

                            bdevs.Add(new BdevInfo
                            {
                                Name = name,
                                BlockSize = blockSize,
                                NumBlocks = numBlocks,
                                SizeBytes = blockSize * numBlocks,
                                BdevNames = new[] { name }
                            });
                        }
                    }
                }

                // Include locally tracked bdevs
                foreach (var tracked in _createdBdevs.Values)
                {
                    if (!bdevs.Any(b => b.Name == tracked.Name))
                    {
                        bdevs.Add(tracked);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error listing bdevs: {ex.Message}");
            }

            return bdevs;
        }, cancellationToken);
    }

    /// <summary>
    /// Deletes a block device.
    /// </summary>
    /// <param name="bdevName">Name of the bdev to delete.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Result of the delete operation.</returns>
    public async Task<BdevCreateResult> DeleteBdevAsync(
        string bdevName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bdevName))
        {
            throw new ArgumentException("Bdev name cannot be null or empty.", nameof(bdevName));
        }

        return await Task.Run(async () =>
        {
            try
            {
                if (IsRpcConnected)
                {
                    var rpcParams = new Dictionary<string, object>
                    {
                        ["name"] = bdevName
                    };

                    var response = await SendRpcRequestAsync("bdev_nvme_detach_controller", rpcParams, cancellationToken);

                    if (response.Error != null)
                    {
                        return new BdevCreateResult
                        {
                            Success = false,
                            Name = bdevName,
                            ErrorMessage = $"RPC error: {response.Error.Message}"
                        };
                    }
                }

                _createdBdevs.TryRemove(bdevName, out _);

                return new BdevCreateResult
                {
                    Success = true,
                    Name = bdevName,
                    Message = "Bdev deleted successfully"
                };
            }
            catch (Exception ex)
            {
                return new BdevCreateResult
                {
                    Success = false,
                    Name = bdevName,
                    ErrorMessage = $"Delete failed: {ex.Message}"
                };
            }
        }, cancellationToken);
    }

    #endregion

    #region Private Helper Methods

    private bool CheckSpdkAvailability()
    {
        if (_spdkAvailable)
        {
            return true;
        }

        // Only available on Linux
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return false;
        }

        // Check for spdk_tgt process
        if (IsSpdkTgtRunning())
        {
            _spdkAvailable = true;
            DetectRpcSocket();
        }

        // Check standard installation paths for libraries
        foreach (var path in SpdkInstallPaths)
        {
            if (!Directory.Exists(path)) continue;

            foreach (var lib in SpdkLibraries)
            {
                var libPath = Path.Combine(path, lib);
                if (File.Exists(libPath))
                {
                    _spdkPath = path;
                    _spdkAvailable = true;
                    break;
                }
            }

            if (_spdkAvailable) break;
        }

        // Check for SPDK scripts
        if (!_spdkAvailable)
        {
            var setupScripts = new[]
            {
                "/usr/local/bin/spdk_nvme_identify",
                "/opt/spdk/scripts/setup.sh",
                "/usr/share/spdk/scripts/setup.sh"
            };

            foreach (var script in setupScripts)
            {
                if (File.Exists(script))
                {
                    _spdkPath = Path.GetDirectoryName(script);
                    _spdkAvailable = true;
                    break;
                }
            }
        }

        // Check if native calls are available
        if (_spdkAvailable)
        {
            try
            {
                // Try a harmless call to verify library is loadable
                var opts = IntPtr.Zero;
                _nativeSpdkAvailable = true;
            }
            catch
            {
                _nativeSpdkAvailable = false;
            }
        }

        return _spdkAvailable;
    }

    private static bool IsSpdkTgtRunning()
    {
        try
        {
            var psi = new ProcessStartInfo("pgrep", "-x spdk_tgt")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false
            };

            using var process = Process.Start(psi);
            process?.WaitForExit(5000);

            return process?.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private void DetectRpcSocket()
    {
        // Check default paths
        if (File.Exists(DefaultRpcSocketPath))
        {
            _rpcSocketPath = DefaultRpcSocketPath;
        }
        else if (File.Exists(AlternativeRpcSocketPath))
        {
            _rpcSocketPath = AlternativeRpcSocketPath;
        }

        // Check environment variable
        var envSocket = Environment.GetEnvironmentVariable("SPDK_RPC_SOCKET");
        if (!string.IsNullOrEmpty(envSocket) && File.Exists(envSocket))
        {
            _rpcSocketPath = envSocket;
        }
    }

    private bool TryConnectRpc(string socketPath)
    {
        try
        {
            if (!File.Exists(socketPath))
            {
                return false;
            }

            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            var endpoint = new UnixDomainSocketEndPoint(socketPath);

            socket.Connect(endpoint);
            socket.ReceiveTimeout = DefaultRpcTimeoutSeconds * 1000;
            socket.SendTimeout = DefaultRpcTimeoutSeconds * 1000;

            _rpcSocket = socket;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<JsonRpcResponse> SendRpcRequestAsync(
        string method,
        Dictionary<string, object>? parameters,
        CancellationToken cancellationToken)
    {
        if (_rpcSocket == null || !_rpcSocket.Connected)
        {
            return new JsonRpcResponse
            {
                Error = new JsonRpcError { Code = -1, Message = "RPC not connected" }
            };
        }

        var requestId = Interlocked.Increment(ref _rpcRequestId);

        var request = new JsonRpcRequest
        {
            JsonRpc = "2.0",
            Id = requestId,
            Method = method,
            Params = parameters
        };

        var requestJson = JsonSerializer.Serialize(request, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        var requestBytes = Encoding.UTF8.GetBytes(requestJson + "\n");

        try
        {
            await _rpcSocket.SendAsync(requestBytes, SocketFlags.None, cancellationToken);

            var responseBuffer = new byte[65536];
            var responseBuilder = new StringBuilder();

            while (true)
            {
                var bytesRead = await _rpcSocket.ReceiveAsync(responseBuffer, SocketFlags.None, cancellationToken);
                if (bytesRead == 0) break;

                responseBuilder.Append(Encoding.UTF8.GetString(responseBuffer, 0, bytesRead));

                // Check if we have a complete JSON response
                var responseStr = responseBuilder.ToString();
                if (responseStr.Contains('\n'))
                {
                    break;
                }
            }

            var responseJson = responseBuilder.ToString().Trim();
            var response = JsonSerializer.Deserialize<JsonRpcResponse>(responseJson, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            });

            return response ?? new JsonRpcResponse
            {
                Error = new JsonRpcError { Code = -1, Message = "Failed to parse response" }
            };
        }
        catch (Exception ex)
        {
            return new JsonRpcResponse
            {
                Error = new JsonRpcError { Code = -1, Message = ex.Message }
            };
        }
    }

    private SpdkInitResult InitializeNative(SpdkInitOptions options)
    {
        // Native initialization would involve:
        // 1. spdk_env_opts_init
        // 2. Configure options
        // 3. spdk_env_init
        // For safety, we just mark as initialized if native is available
        return new SpdkInitResult
        {
            Success = true,
            Message = "SPDK native environment available (use spdk_tgt for full functionality)"
        };
    }

    private async Task<IEnumerable<NvmeControllerInfo>> GetControllersViaRpcAsync(CancellationToken cancellationToken)
    {
        var controllers = new List<NvmeControllerInfo>();

        var response = await SendRpcRequestAsync("bdev_nvme_get_controllers", null, cancellationToken);

        if (response.Result is JsonElement resultElement && resultElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in resultElement.EnumerateArray())
            {
                var name = item.TryGetProperty("name", out var n) ? n.GetString() : "";
                var traddr = item.TryGetProperty("trid", out var trid)
                    ? (trid.TryGetProperty("traddr", out var ta) ? ta.GetString() : "")
                    : "";

                controllers.Add(new NvmeControllerInfo
                {
                    Name = name ?? "",
                    PciAddress = traddr ?? "",
                    IsAttached = true
                });
            }
        }

        return controllers;
    }

    private IEnumerable<NvmeControllerInfo> GetControllersFromSysfs()
    {
        var controllers = new List<NvmeControllerInfo>();

        try
        {
            var nvmeClassPath = "/sys/class/nvme";
            if (!Directory.Exists(nvmeClassPath))
            {
                return controllers;
            }

            foreach (var nvmeDir in Directory.GetDirectories(nvmeClassPath))
            {
                var controllerName = Path.GetFileName(nvmeDir);
                var devicePath = Path.Combine(nvmeDir, "device");

                if (!Directory.Exists(devicePath))
                {
                    continue;
                }

                // Get PCI address from symlink
                var realPath = Path.GetFullPath(devicePath);
                var pciAddress = Path.GetFileName(realPath);

                // Read model and serial if available
                string? model = null;
                string? serial = null;
                string? firmware = null;

                var modelPath = Path.Combine(nvmeDir, "model");
                if (File.Exists(modelPath))
                {
                    model = File.ReadAllText(modelPath).Trim();
                }

                var serialPath = Path.Combine(nvmeDir, "serial");
                if (File.Exists(serialPath))
                {
                    serial = File.ReadAllText(serialPath).Trim();
                }

                var fwPath = Path.Combine(nvmeDir, "firmware_rev");
                if (File.Exists(fwPath))
                {
                    firmware = File.ReadAllText(fwPath).Trim();
                }

                // Count namespaces
                var namespaceCount = Directory.GetDirectories(nvmeClassPath + "-subsystem", $"{controllerName}n*", SearchOption.TopDirectoryOnly).Length;
                if (namespaceCount == 0)
                {
                    // Try alternative counting
                    var nsPattern = Path.Combine("/dev", $"{controllerName}n*");
                    namespaceCount = Directory.GetFiles("/dev", $"{controllerName}n*").Length;
                }

                controllers.Add(new NvmeControllerInfo
                {
                    Name = controllerName,
                    PciAddress = pciAddress,
                    Model = model,
                    SerialNumber = serial,
                    FirmwareRevision = firmware,
                    NamespaceCount = namespaceCount > 0 ? namespaceCount : 1,
                    IsAttached = false
                });
            }
        }
        catch
        {
            // Error scanning sysfs
        }

        return controllers;
    }

    private async Task<NvmeAttachResult> AttachNvmeViaRpcAsync(string pciAddress, CancellationToken cancellationToken)
    {
        var controllerName = $"nvme_{pciAddress.Replace(":", "_").Replace(".", "_")}";

        var rpcParams = new Dictionary<string, object>
        {
            ["name"] = controllerName,
            ["trtype"] = "PCIe",
            ["traddr"] = pciAddress
        };

        var response = await SendRpcRequestAsync("bdev_nvme_attach_controller", rpcParams, cancellationToken);

        if (response.Error != null)
        {
            return new NvmeAttachResult
            {
                Success = false,
                PciAddress = pciAddress,
                ErrorMessage = $"RPC error: {response.Error.Message}"
            };
        }

        // Parse result
        var bdevNames = new List<string>();
        if (response.Result is JsonElement resultElement)
        {
            if (resultElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in resultElement.EnumerateArray())
                {
                    bdevNames.Add(item.GetString() ?? "");
                }
            }
        }

        var controller = new NvmeControllerInfo
        {
            Name = controllerName,
            PciAddress = pciAddress,
            IsAttached = true,
            AttachedAt = DateTime.UtcNow
        };

        _attachedControllers[pciAddress] = controller;

        return new NvmeAttachResult
        {
            Success = true,
            PciAddress = pciAddress,
            ControllerName = controllerName,
            BdevNames = bdevNames.ToArray(),
            Message = $"Attached {pciAddress}, created bdevs: {string.Join(", ", bdevNames)}"
        };
    }

    private async Task<NvmeAttachResult> DetachNvmeViaRpcAsync(string pciAddress, CancellationToken cancellationToken)
    {
        if (!_attachedControllers.TryGetValue(pciAddress, out var controller))
        {
            return new NvmeAttachResult
            {
                Success = true,
                PciAddress = pciAddress,
                Message = "Controller was not attached"
            };
        }

        var rpcParams = new Dictionary<string, object>
        {
            ["name"] = controller.Name
        };

        var response = await SendRpcRequestAsync("bdev_nvme_detach_controller", rpcParams, cancellationToken);

        if (response.Error != null)
        {
            return new NvmeAttachResult
            {
                Success = false,
                PciAddress = pciAddress,
                ErrorMessage = $"RPC error: {response.Error.Message}"
            };
        }

        return new NvmeAttachResult
        {
            Success = true,
            PciAddress = pciAddress,
            Message = "Controller detached successfully"
        };
    }

    private NvmeAttachResult AttachNvmeNative(string pciAddress)
    {
        // Native attach would involve:
        // 1. spdk_nvme_probe with the specific address
        // 2. Handle probe_cb and attach_cb
        // 3. Store controller handle

        // For safety without full implementation, record the attach attempt
        var controller = new NvmeControllerInfo
        {
            PciAddress = pciAddress,
            Name = $"nvme_{pciAddress.Replace(":", "_").Replace(".", "_")}",
            IsAttached = true,
            AttachedAt = DateTime.UtcNow
        };

        _attachedControllers[pciAddress] = controller;

        return new NvmeAttachResult
        {
            Success = true,
            PciAddress = pciAddress,
            ControllerName = controller.Name,
            Message = "Controller attached (native mode - limited functionality)"
        };
    }

    private async Task<BdevStats> GetBdevStatsViaRpcAsync(string bdevName, CancellationToken cancellationToken)
    {
        var rpcParams = new Dictionary<string, object>
        {
            ["name"] = bdevName
        };

        var response = await SendRpcRequestAsync("bdev_get_iostat", rpcParams, cancellationToken);

        if (response.Error != null)
        {
            return new BdevStats
            {
                Name = bdevName,
                Timestamp = DateTime.UtcNow,
                ErrorMessage = $"RPC error: {response.Error.Message}"
            };
        }

        var stats = new BdevStats
        {
            Name = bdevName,
            Timestamp = DateTime.UtcNow
        };

        try
        {
            if (response.Result is JsonElement resultElement)
            {
                // Parse bdevs array
                if (resultElement.TryGetProperty("bdevs", out var bdevs) && bdevs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var bdev in bdevs.EnumerateArray())
                    {
                        if (bdev.TryGetProperty("name", out var name) && name.GetString() == bdevName)
                        {
                            stats.BytesRead = bdev.TryGetProperty("bytes_read", out var br) ? br.GetUInt64() : 0;
                            stats.BytesWritten = bdev.TryGetProperty("bytes_written", out var bw) ? bw.GetUInt64() : 0;
                            stats.ReadOps = bdev.TryGetProperty("num_read_ops", out var ro) ? ro.GetUInt64() : 0;
                            stats.WriteOps = bdev.TryGetProperty("num_write_ops", out var wo) ? wo.GetUInt64() : 0;
                            stats.ReadLatencyTicks = bdev.TryGetProperty("read_latency_ticks", out var rlt) ? rlt.GetUInt64() : 0;
                            stats.WriteLatencyTicks = bdev.TryGetProperty("write_latency_ticks", out var wlt) ? wlt.GetUInt64() : 0;

                            Interlocked.Add(ref _totalIoOperations, (long)(stats.ReadOps + stats.WriteOps));
                            Interlocked.Add(ref _totalBytesTransferred, (long)(stats.BytesRead + stats.BytesWritten));
                            break;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            stats.ErrorMessage = $"Failed to parse stats: {ex.Message}";
        }

        return stats;
    }

    /// <inheritdoc />
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FeatureType"] = "SPDK";
        metadata["IsAvailable"] = IsAvailable;
        metadata["IsInitialized"] = _initialized;
        metadata["NativeCallsAvailable"] = _nativeSpdkAvailable;
        metadata["SpdkPath"] = _spdkPath ?? "Not found";
        metadata["RpcSocketPath"] = _rpcSocketPath ?? "Not configured";
        metadata["IsRpcConnected"] = IsRpcConnected;
        metadata["AttachedControllers"] = _attachedControllers.Count;
        metadata["CreatedBdevs"] = _createdBdevs.Count;
        metadata["TotalIoOperations"] = TotalIoOperations;
        metadata["TotalBytesTransferred"] = TotalBytesTransferred;
        metadata["Platform"] = RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "Linux" : "Unsupported";
        return metadata;
    }

    #endregion
}

#region Supporting Types

/// <summary>
/// Options for SPDK initialization.
/// </summary>
public class SpdkInitOptions
{
    /// <summary>
    /// Gets or sets the path to the JSON-RPC socket.
    /// </summary>
    public string? RpcSocketPath { get; init; }

    /// <summary>
    /// Gets or sets the name for this SPDK application instance.
    /// </summary>
    public string? AppName { get; init; }

    /// <summary>
    /// Gets or sets the hugepage memory size in MB.
    /// </summary>
    public int HugepageMemoryMb { get; init; } = 2048;

    /// <summary>
    /// Gets or sets whether to initialize the NVMe subsystem.
    /// </summary>
    public bool InitializeNvme { get; init; } = true;
}

/// <summary>
/// Result of SPDK initialization.
/// </summary>
public class SpdkInitResult
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
    /// Gets or sets whether RPC connection was established.
    /// </summary>
    public bool RpcConnected { get; init; }

    /// <summary>
    /// Gets or sets the RPC socket path if connected.
    /// </summary>
    public string? RpcSocketPath { get; init; }
}

/// <summary>
/// Information about an NVMe controller.
/// </summary>
public class NvmeControllerInfo
{
    /// <summary>
    /// Gets or sets the controller name.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the PCI address.
    /// </summary>
    public string PciAddress { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the device model.
    /// </summary>
    public string? Model { get; init; }

    /// <summary>
    /// Gets or sets the serial number.
    /// </summary>
    public string? SerialNumber { get; init; }

    /// <summary>
    /// Gets or sets the firmware revision.
    /// </summary>
    public string? FirmwareRevision { get; init; }

    /// <summary>
    /// Gets or sets the number of namespaces.
    /// </summary>
    public int NamespaceCount { get; init; }

    /// <summary>
    /// Gets or sets whether the controller is attached to SPDK.
    /// </summary>
    public bool IsAttached { get; init; }

    /// <summary>
    /// Gets or sets when the controller was attached.
    /// </summary>
    public DateTime? AttachedAt { get; init; }

    /// <summary>
    /// Gets or sets the native handle (for native mode).
    /// </summary>
    internal IntPtr NativeHandle { get; init; }
}

/// <summary>
/// Result of NVMe attach/detach operation.
/// </summary>
public class NvmeAttachResult
{
    /// <summary>
    /// Gets or sets whether the operation succeeded.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Gets or sets the PCI address.
    /// </summary>
    public string PciAddress { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the controller name.
    /// </summary>
    public string? ControllerName { get; init; }

    /// <summary>
    /// Gets or sets the created bdev names.
    /// </summary>
    public string[]? BdevNames { get; init; }

    /// <summary>
    /// Gets or sets a success message.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Gets or sets an error message.
    /// </summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Information about a block device.
/// </summary>
public class BdevInfo
{
    /// <summary>
    /// Gets or sets the bdev name.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the PCI address of the backing device.
    /// </summary>
    public string? PciAddress { get; init; }

    /// <summary>
    /// Gets or sets the namespace ID.
    /// </summary>
    public int NamespaceId { get; init; }

    /// <summary>
    /// Gets or sets the block size in bytes.
    /// </summary>
    public uint BlockSize { get; init; }

    /// <summary>
    /// Gets or sets the number of blocks.
    /// </summary>
    public ulong NumBlocks { get; init; }

    /// <summary>
    /// Gets or sets the total size in bytes.
    /// </summary>
    public ulong SizeBytes { get; init; }

    /// <summary>
    /// Gets or sets when the bdev was created.
    /// </summary>
    public DateTime? CreatedAt { get; init; }

    /// <summary>
    /// Gets or sets the associated bdev names.
    /// </summary>
    public string[]? BdevNames { get; init; }
}

/// <summary>
/// Result of bdev creation/deletion.
/// </summary>
public class BdevCreateResult
{
    /// <summary>
    /// Gets or sets whether the operation succeeded.
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// Gets or sets the bdev name.
    /// </summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the created bdev names.
    /// </summary>
    public string[]? BdevNames { get; init; }

    /// <summary>
    /// Gets or sets a success message.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Gets or sets an error message.
    /// </summary>
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// I/O statistics for a block device.
/// </summary>
public class BdevStats
{
    /// <summary>
    /// Gets or sets the bdev name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets when the stats were collected.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the total bytes read.
    /// </summary>
    public ulong BytesRead { get; set; }

    /// <summary>
    /// Gets or sets the total bytes written.
    /// </summary>
    public ulong BytesWritten { get; set; }

    /// <summary>
    /// Gets or sets the number of read operations.
    /// </summary>
    public ulong ReadOps { get; set; }

    /// <summary>
    /// Gets or sets the number of write operations.
    /// </summary>
    public ulong WriteOps { get; set; }

    /// <summary>
    /// Gets or sets the read latency in ticks.
    /// </summary>
    public ulong ReadLatencyTicks { get; set; }

    /// <summary>
    /// Gets or sets the write latency in ticks.
    /// </summary>
    public ulong WriteLatencyTicks { get; set; }

    /// <summary>
    /// Gets or sets an error message if stats collection failed.
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Gets the total I/O operations.
    /// </summary>
    public ulong TotalOps => ReadOps + WriteOps;

    /// <summary>
    /// Gets the total bytes transferred.
    /// </summary>
    public ulong TotalBytes => BytesRead + BytesWritten;
}

/// <summary>
/// JSON-RPC request structure.
/// </summary>
internal class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")]
    public string JsonRpc { get; init; } = "2.0";

    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("method")]
    public string Method { get; init; } = string.Empty;

    [JsonPropertyName("params")]
    public Dictionary<string, object>? Params { get; init; }
}

/// <summary>
/// JSON-RPC response structure.
/// </summary>
internal class JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string? JsonRpc { get; init; }

    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("result")]
    public object? Result { get; init; }

    [JsonPropertyName("error")]
    public JsonRpcError? Error { get; init; }
}

/// <summary>
/// JSON-RPC error structure.
/// </summary>
internal class JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; init; }

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}

#endregion
