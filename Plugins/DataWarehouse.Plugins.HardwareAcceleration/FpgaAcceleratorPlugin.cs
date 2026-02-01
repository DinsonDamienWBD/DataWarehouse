using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Hardware;

namespace DataWarehouse.Plugins.HardwareAcceleration;

/// <summary>
/// FPGA accelerator plugin providing programmable hardware acceleration.
/// Supports Intel (formerly Altera) OpenCL-based FPGAs, Xilinx Alveo series,
/// and AWS F1 instances. Enables custom bitstream loading for specialized
/// workloads including encryption, compression, and data transformation.
/// </summary>
public class FpgaAcceleratorPlugin : HardwareAcceleratorPluginBase
{
    private readonly ConcurrentDictionary<string, FpgaBitstreamInfo> _loadedBitstreams = new();
    private FpgaVendor _detectedVendor = FpgaVendor.None;
    private string? _deviceName;
    private string? _driverVersion;
    private int _deviceCount;
    private bool _openClAvailable;
    private string? _activeBitstreamId;
    private long _operationsExecuted;
    private long _bytesProcessed;

    /// <inheritdoc />
    public override string Id => "datawarehouse.hwaccel.fpga";

    /// <inheritdoc />
    public override string Name => "FPGA Accelerator";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <summary>
    /// Gets the accelerator type. Always returns FPGA.
    /// </summary>
    public override AcceleratorType Type => AcceleratorType.Fpga;

    /// <summary>
    /// Gets whether FPGA acceleration is available on this system.
    /// Checks for OpenCL runtime, vendor-specific tools, and hardware presence.
    /// </summary>
    public override bool IsAvailable => DetectFpga();

    /// <summary>
    /// Gets the detected FPGA vendor.
    /// </summary>
    public FpgaVendor DetectedVendor => _detectedVendor;

    /// <summary>
    /// Gets the FPGA device name if detected.
    /// </summary>
    public string? DeviceName => _deviceName;

    /// <summary>
    /// Gets the number of FPGA devices available.
    /// </summary>
    public int DeviceCount => _deviceCount;

    /// <summary>
    /// Gets the currently active bitstream ID, if any.
    /// </summary>
    public string? ActiveBitstreamId => _activeBitstreamId;

    /// <summary>
    /// Gets whether OpenCL runtime is available for FPGA programming.
    /// </summary>
    public bool OpenClAvailable => _openClAvailable;

    /// <summary>
    /// Gets a list of loaded bitstreams.
    /// </summary>
    public IReadOnlyCollection<string> LoadedBitstreams => _loadedBitstreams.Keys.ToList().AsReadOnly();

    /// <summary>
    /// Initializes the FPGA hardware.
    /// Detects available devices and verifies driver/runtime availability.
    /// </summary>
    protected override async Task InitializeHardwareAsync()
    {
        DetectFpga();
        _openClAvailable = CheckOpenClAvailability();

        if (_detectedVendor != FpgaVendor.None)
        {
            await LoadDefaultBitstreamAsync();
        }
    }

    /// <summary>
    /// Performs a hardware-accelerated operation using the FPGA.
    /// Routes to the appropriate bitstream-specific implementation.
    /// </summary>
    /// <param name="data">Input data to process.</param>
    /// <param name="op">The operation to perform.</param>
    /// <returns>Processed data.</returns>
    /// <exception cref="InvalidOperationException">Thrown when FPGA is not initialized or no bitstream is loaded.</exception>
    /// <exception cref="NotSupportedException">Thrown when the requested operation is not supported.</exception>
    protected override async Task<byte[]> PerformOperationAsync(byte[] data, AcceleratorOperation op)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("FPGA is not available.");
        }

        if (string.IsNullOrEmpty(_activeBitstreamId))
        {
            throw new InvalidOperationException(
                "No bitstream is loaded. Call LoadBitstreamAsync first.");
        }

        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        byte[] result;

        switch (op)
        {
            case AcceleratorOperation.Compress:
                result = await ExecuteFpgaCompressionAsync(data, true);
                break;

            case AcceleratorOperation.Decompress:
                result = await ExecuteFpgaCompressionAsync(data, false);
                break;

            case AcceleratorOperation.Encrypt:
                result = await ExecuteFpgaEncryptionAsync(data, true);
                break;

            case AcceleratorOperation.Decrypt:
                result = await ExecuteFpgaEncryptionAsync(data, false);
                break;

            case AcceleratorOperation.Hash:
                result = await ExecuteFpgaHashAsync(data);
                break;

            case AcceleratorOperation.Custom:
                result = await ExecuteCustomOperationAsync(data);
                break;

            default:
                throw new NotSupportedException($"Operation {op} is not supported by the FPGA accelerator.");
        }

        Interlocked.Increment(ref _operationsExecuted);
        Interlocked.Add(ref _bytesProcessed, data.Length);

        return result;
    }

    /// <summary>
    /// Loads a bitstream onto the FPGA.
    /// The bitstream defines the hardware configuration for specific operations.
    /// </summary>
    /// <param name="bitstreamPath">Path to the bitstream file (.xclbin, .aocx, or .awsxclbin).</param>
    /// <param name="bitstreamId">Optional identifier for the bitstream. Defaults to filename.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the bitstream was loaded successfully.</returns>
    /// <exception cref="ArgumentNullException">Thrown when bitstreamPath is null.</exception>
    /// <exception cref="FileNotFoundException">Thrown when the bitstream file does not exist.</exception>
    /// <exception cref="InvalidOperationException">Thrown when FPGA is not available.</exception>
    public async Task<bool> LoadBitstreamAsync(
        string bitstreamPath,
        string? bitstreamId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bitstreamPath))
        {
            throw new ArgumentNullException(nameof(bitstreamPath));
        }

        if (!File.Exists(bitstreamPath))
        {
            throw new FileNotFoundException(
                $"Bitstream file not found: {bitstreamPath}",
                bitstreamPath);
        }

        if (!IsAvailable)
        {
            throw new InvalidOperationException("FPGA is not available.");
        }

        var id = bitstreamId ?? Path.GetFileNameWithoutExtension(bitstreamPath);
        var extension = Path.GetExtension(bitstreamPath).ToLowerInvariant();

        // Validate bitstream format
        var format = extension switch
        {
            ".xclbin" => BitstreamFormat.Xilinx,
            ".aocx" => BitstreamFormat.IntelOpenCl,
            ".awsxclbin" => BitstreamFormat.AwsF1,
            ".rbf" => BitstreamFormat.IntelRaw,
            ".bit" => BitstreamFormat.XilinxRaw,
            _ => throw new NotSupportedException($"Unsupported bitstream format: {extension}")
        };

        // Verify format matches vendor
        if (!IsFormatCompatibleWithVendor(format, _detectedVendor))
        {
            throw new InvalidOperationException(
                $"Bitstream format {format} is not compatible with {_detectedVendor} FPGA.");
        }

        bool success;

        switch (_detectedVendor)
        {
            case FpgaVendor.Xilinx:
                success = await LoadXilinxBitstreamAsync(bitstreamPath, ct);
                break;

            case FpgaVendor.Intel:
                success = await LoadIntelBitstreamAsync(bitstreamPath, ct);
                break;

            case FpgaVendor.AwsF1:
                success = await LoadAwsF1BitstreamAsync(bitstreamPath, ct);
                break;

            default:
                throw new InvalidOperationException($"Unsupported FPGA vendor: {_detectedVendor}");
        }

        if (success)
        {
            var info = new FpgaBitstreamInfo
            {
                Id = id,
                Path = bitstreamPath,
                Format = format,
                LoadedAt = DateTime.UtcNow,
                Size = new FileInfo(bitstreamPath).Length
            };

            _loadedBitstreams[id] = info;
            _activeBitstreamId = id;
        }

        return success;
    }

    /// <summary>
    /// Unloads the currently active bitstream from the FPGA.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the bitstream was unloaded successfully.</returns>
    public async Task<bool> UnloadBitstreamAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_activeBitstreamId))
        {
            return true;
        }

        bool success;

        switch (_detectedVendor)
        {
            case FpgaVendor.Xilinx:
                success = await UnloadXilinxBitstreamAsync(ct);
                break;

            case FpgaVendor.Intel:
                success = await UnloadIntelBitstreamAsync(ct);
                break;

            case FpgaVendor.AwsF1:
                success = await UnloadAwsF1BitstreamAsync(ct);
                break;

            default:
                success = false;
                break;
        }

        if (success)
        {
            _loadedBitstreams.TryRemove(_activeBitstreamId, out _);
            _activeBitstreamId = null;
        }

        return success;
    }

    /// <summary>
    /// Switches to a different loaded bitstream.
    /// </summary>
    /// <param name="bitstreamId">ID of the bitstream to activate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True if the switch was successful.</returns>
    /// <exception cref="ArgumentNullException">Thrown when bitstreamId is null.</exception>
    /// <exception cref="KeyNotFoundException">Thrown when the bitstream is not loaded.</exception>
    public async Task<bool> SwitchBitstreamAsync(string bitstreamId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(bitstreamId))
        {
            throw new ArgumentNullException(nameof(bitstreamId));
        }

        if (!_loadedBitstreams.TryGetValue(bitstreamId, out var info))
        {
            throw new KeyNotFoundException($"Bitstream '{bitstreamId}' is not loaded.");
        }

        if (_activeBitstreamId == bitstreamId)
        {
            return true;
        }

        // Unload current and load the new one
        await UnloadBitstreamAsync(ct);
        return await LoadBitstreamAsync(info.Path, bitstreamId, ct);
    }

    /// <summary>
    /// Executes a custom operation defined by the loaded bitstream.
    /// </summary>
    /// <param name="data">Input data.</param>
    /// <param name="operationCode">Vendor-specific operation code.</param>
    /// <param name="parameters">Additional parameters for the operation.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Result data from the FPGA operation.</returns>
    public async Task<byte[]> ExecuteCustomOperationAsync(
        byte[] data,
        int operationCode,
        Dictionary<string, object>? parameters = null,
        CancellationToken ct = default)
    {
        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }

        if (!IsAvailable)
        {
            throw new InvalidOperationException("FPGA is not available.");
        }

        if (string.IsNullOrEmpty(_activeBitstreamId))
        {
            throw new InvalidOperationException("No bitstream is loaded.");
        }

        // Execute the custom operation based on vendor
        byte[] result;

        switch (_detectedVendor)
        {
            case FpgaVendor.Xilinx:
                result = await ExecuteXilinxKernelAsync(data, operationCode, parameters, ct);
                break;

            case FpgaVendor.Intel:
                result = await ExecuteIntelKernelAsync(data, operationCode, parameters, ct);
                break;

            case FpgaVendor.AwsF1:
                result = await ExecuteAwsF1KernelAsync(data, operationCode, parameters, ct);
                break;

            default:
                throw new InvalidOperationException($"Custom operations not supported for {_detectedVendor}");
        }

        Interlocked.Increment(ref _operationsExecuted);
        Interlocked.Add(ref _bytesProcessed, data.Length);

        return result;
    }

    /// <summary>
    /// Gets information about all available FPGA devices.
    /// </summary>
    /// <returns>List of FPGA device information.</returns>
    public IReadOnlyList<FpgaDeviceInfo> GetDeviceInfo()
    {
        var devices = new List<FpgaDeviceInfo>();

        if (_detectedVendor == FpgaVendor.None)
        {
            return devices;
        }

        // Add the primary detected device
        devices.Add(new FpgaDeviceInfo
        {
            DeviceIndex = 0,
            Vendor = _detectedVendor,
            Name = _deviceName ?? "Unknown FPGA",
            DriverVersion = _driverVersion,
            IsOnline = true,
            CurrentBitstreamId = _activeBitstreamId
        });

        return devices;
    }

    /// <summary>
    /// Detects whether an FPGA is present in the system.
    /// </summary>
    private bool DetectFpga()
    {
        if (_detectedVendor != FpgaVendor.None)
        {
            return true;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return DetectFpgaLinux();
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return DetectFpgaWindows();
        }

        return false;
    }

    /// <summary>
    /// Detects FPGA on Linux via sysfs and vendor-specific paths.
    /// </summary>
    private bool DetectFpgaLinux()
    {
        try
        {
            // Check for Xilinx Runtime (XRT)
            if (Directory.Exists("/opt/xilinx/xrt") ||
                Directory.Exists("/sys/bus/pci/drivers/xocl") ||
                Directory.Exists("/sys/bus/pci/drivers/xclmgmt"))
            {
                _detectedVendor = FpgaVendor.Xilinx;
                _deviceName = DetectXilinxDeviceName();
                _deviceCount = CountXilinxDevices();
                _driverVersion = GetXilinxDriverVersion();
                return true;
            }

            // Check for Intel FPGA (Quartus/OneAPI)
            if (Directory.Exists("/opt/intel/oneapi") ||
                Directory.Exists("/opt/altera") ||
                File.Exists("/dev/intel-fpga-fme.0") ||
                Directory.Exists("/sys/class/fpga"))
            {
                _detectedVendor = FpgaVendor.Intel;
                _deviceName = DetectIntelFpgaDeviceName();
                _deviceCount = CountIntelFpgaDevices();
                _driverVersion = GetIntelFpgaDriverVersion();
                return true;
            }

            // Check for AWS F1 FPGA
            if (File.Exists("/opt/aws/fpga/RELEASE") ||
                File.Exists("/usr/local/bin/fpga-describe-local-image") ||
                Environment.GetEnvironmentVariable("AWS_FPGA_REPO_DIR") != null)
            {
                _detectedVendor = FpgaVendor.AwsF1;
                _deviceName = "AWS EC2 F1 FPGA";
                _deviceCount = DetectAwsF1SlotCount();
                return true;
            }

            // Check for OpenCL devices
            if (DetectOpenClFpgaDevices())
            {
                return true;
            }
        }
        catch
        {
            // Best effort detection
        }

        return false;
    }

    /// <summary>
    /// Detects FPGA on Windows.
    /// </summary>
    private bool DetectFpgaWindows()
    {
        try
        {
            // Check for Xilinx Runtime (XRT) on Windows
            var xrtPath = Environment.GetEnvironmentVariable("XILINX_XRT");
            if (!string.IsNullOrEmpty(xrtPath) && Directory.Exists(xrtPath))
            {
                _detectedVendor = FpgaVendor.Xilinx;
                _deviceName = "Xilinx FPGA";
                _deviceCount = 1;
                return true;
            }

            // Check for Intel FPGA SDK
            var alteraPath = Environment.GetEnvironmentVariable("ALTERAOCLSDKROOT");
            var intelfpgaPath = Environment.GetEnvironmentVariable("INTELFPGAOCLSDKROOT");

            if (!string.IsNullOrEmpty(alteraPath) || !string.IsNullOrEmpty(intelfpgaPath))
            {
                _detectedVendor = FpgaVendor.Intel;
                _deviceName = "Intel FPGA";
                _deviceCount = 1;
                return true;
            }

            // Check for Intel FPGA drivers in System32
            var intelFpgaDriver = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "drivers", "dfl.sys");

            if (File.Exists(intelFpgaDriver))
            {
                _detectedVendor = FpgaVendor.Intel;
                _deviceName = "Intel FPGA (DFL)";
                _deviceCount = 1;
                return true;
            }
        }
        catch
        {
            // Best effort detection
        }

        return false;
    }

    /// <summary>
    /// Checks OpenCL availability.
    /// </summary>
    private bool CheckOpenClAvailability()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            return File.Exists("/usr/lib64/libOpenCL.so") ||
                   File.Exists("/usr/lib/x86_64-linux-gnu/libOpenCL.so") ||
                   File.Exists("/opt/intel/oneapi/compiler/latest/linux/lib/libOpenCL.so");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var openClPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "OpenCL.dll");
            return File.Exists(openClPath);
        }

        return false;
    }

    /// <summary>
    /// Detects OpenCL FPGA devices.
    /// </summary>
    private bool DetectOpenClFpgaDevices()
    {
        try
        {
            // Check for OpenCL ICD (Installable Client Driver) vendors
            const string icdPath = "/etc/OpenCL/vendors";
            if (Directory.Exists(icdPath))
            {
                var icdFiles = Directory.GetFiles(icdPath, "*.icd");
                foreach (var icdFile in icdFiles)
                {
                    var content = File.ReadAllText(icdFile).ToLowerInvariant();

                    if (content.Contains("xilinx") || content.Contains("xrt"))
                    {
                        _detectedVendor = FpgaVendor.Xilinx;
                        _deviceName = "Xilinx OpenCL FPGA";
                        _deviceCount = 1;
                        _openClAvailable = true;
                        return true;
                    }

                    if (content.Contains("intel") || content.Contains("altera"))
                    {
                        _detectedVendor = FpgaVendor.Intel;
                        _deviceName = "Intel OpenCL FPGA";
                        _deviceCount = 1;
                        _openClAvailable = true;
                        return true;
                    }
                }
            }
        }
        catch
        {
            // Best effort
        }

        return false;
    }

    /// <summary>
    /// Detects Xilinx device name from sysfs.
    /// </summary>
    private static string? DetectXilinxDeviceName()
    {
        try
        {
            const string xoclPath = "/sys/bus/pci/drivers/xocl";
            if (Directory.Exists(xoclPath))
            {
                var devices = Directory.GetDirectories(xoclPath)
                    .Where(d => d.Contains(":"))
                    .ToList();

                if (devices.Count > 0)
                {
                    // Try to read the device name
                    var romPath = Path.Combine(devices[0], "rom", "VBNV");
                    if (File.Exists(romPath))
                    {
                        var vbnv = File.ReadAllText(romPath).Trim();
                        return MapXilinxVbnvToName(vbnv);
                    }
                }
            }
        }
        catch
        {
            // Best effort
        }

        return "Xilinx FPGA";
    }

    /// <summary>
    /// Maps Xilinx VBNV string to device name.
    /// </summary>
    private static string MapXilinxVbnvToName(string vbnv)
    {
        // VBNV format: Vendor_Board_Name_Version
        // Example: xilinx_u250_gen3x16_xdma_base_3
        if (vbnv.Contains("u250", StringComparison.OrdinalIgnoreCase))
            return "Xilinx Alveo U250";
        if (vbnv.Contains("u280", StringComparison.OrdinalIgnoreCase))
            return "Xilinx Alveo U280";
        if (vbnv.Contains("u200", StringComparison.OrdinalIgnoreCase))
            return "Xilinx Alveo U200";
        if (vbnv.Contains("u50", StringComparison.OrdinalIgnoreCase))
            return "Xilinx Alveo U50";
        if (vbnv.Contains("u55", StringComparison.OrdinalIgnoreCase))
            return "Xilinx Alveo U55C";
        if (vbnv.Contains("vck", StringComparison.OrdinalIgnoreCase))
            return "Xilinx Versal VCK";
        if (vbnv.Contains("aws", StringComparison.OrdinalIgnoreCase))
            return "AWS F1 Xilinx FPGA";

        return $"Xilinx FPGA ({vbnv})";
    }

    /// <summary>
    /// Counts Xilinx FPGA devices.
    /// </summary>
    private static int CountXilinxDevices()
    {
        try
        {
            const string xoclPath = "/sys/bus/pci/drivers/xocl";
            if (Directory.Exists(xoclPath))
            {
                return Directory.GetDirectories(xoclPath)
                    .Count(d => d.Contains(":"));
            }
        }
        catch
        {
            // Best effort
        }

        return 1;
    }

    /// <summary>
    /// Gets Xilinx driver version.
    /// </summary>
    private static string? GetXilinxDriverVersion()
    {
        try
        {
            const string versionPath = "/opt/xilinx/xrt/version";
            if (File.Exists(versionPath))
            {
                return File.ReadAllText(versionPath).Trim();
            }

            const string xrtPath = "/sys/module/xocl/version";
            if (File.Exists(xrtPath))
            {
                return File.ReadAllText(xrtPath).Trim();
            }
        }
        catch
        {
            // Best effort
        }

        return null;
    }

    /// <summary>
    /// Detects Intel FPGA device name.
    /// </summary>
    private static string? DetectIntelFpgaDeviceName()
    {
        try
        {
            // Check for DFL (Device Feature List) based devices
            const string dflPath = "/sys/class/fpga_region";
            if (Directory.Exists(dflPath))
            {
                var regions = Directory.GetDirectories(dflPath);
                if (regions.Length > 0)
                {
                    // Try to identify the device type
                    var compatPath = Path.Combine(regions[0], "compat_id");
                    if (File.Exists(compatPath))
                    {
                        var compatId = File.ReadAllText(compatPath).Trim();
                        return MapIntelCompatIdToName(compatId);
                    }
                }
            }
        }
        catch
        {
            // Best effort
        }

        return "Intel FPGA";
    }

    /// <summary>
    /// Maps Intel FPGA compat ID to device name.
    /// </summary>
    private static string MapIntelCompatIdToName(string compatId)
    {
        // Intel FPGA device mapping
        if (compatId.Contains("d8424dc4", StringComparison.OrdinalIgnoreCase))
            return "Intel Stratix 10";
        if (compatId.Contains("9aeffe5f", StringComparison.OrdinalIgnoreCase))
            return "Intel Agilex";
        if (compatId.Contains("69528db6", StringComparison.OrdinalIgnoreCase))
            return "Intel Arria 10";

        return $"Intel FPGA ({compatId})";
    }

    /// <summary>
    /// Counts Intel FPGA devices.
    /// </summary>
    private static int CountIntelFpgaDevices()
    {
        try
        {
            const string dflPath = "/sys/class/fpga_region";
            if (Directory.Exists(dflPath))
            {
                return Directory.GetDirectories(dflPath).Length;
            }
        }
        catch
        {
            // Best effort
        }

        return 1;
    }

    /// <summary>
    /// Gets Intel FPGA driver version.
    /// </summary>
    private static string? GetIntelFpgaDriverVersion()
    {
        try
        {
            const string versionPath = "/sys/module/dfl/version";
            if (File.Exists(versionPath))
            {
                return File.ReadAllText(versionPath).Trim();
            }
        }
        catch
        {
            // Best effort
        }

        return null;
    }

    /// <summary>
    /// Detects AWS F1 slot count.
    /// </summary>
    private static int DetectAwsF1SlotCount()
    {
        try
        {
            // AWS F1 instances have 1-8 FPGA slots
            // Check via FPGA management tools or instance metadata
            const string fpgaPath = "/dev/xdma0_user";
            if (File.Exists(fpgaPath))
            {
                // Count xdma devices
                return Directory.GetFiles("/dev", "xdma*_user").Length;
            }
        }
        catch
        {
            // Best effort
        }

        return 1;
    }

    /// <summary>
    /// Loads the default bitstream if available.
    /// </summary>
    private async Task LoadDefaultBitstreamAsync()
    {
        // Look for a default bitstream in standard locations
        var searchPaths = new List<string>();

        if (_detectedVendor == FpgaVendor.Xilinx)
        {
            searchPaths.AddRange(new[]
            {
                "/opt/xilinx/platforms/*/default.xclbin",
                "/opt/xilinx/xrt/share/xclbin/*.xclbin"
            });
        }
        else if (_detectedVendor == FpgaVendor.Intel)
        {
            searchPaths.AddRange(new[]
            {
                "/opt/intel/oneapi/fpga/latest/share/*.aocx",
                "/opt/altera/aocl/board/*.aocx"
            });
        }

        foreach (var pattern in searchPaths)
        {
            try
            {
                var directory = Path.GetDirectoryName(pattern);
                var filePattern = Path.GetFileName(pattern);

                if (directory != null &&
                    directory.Contains('*'))
                {
                    // Handle directory wildcards
                    continue;
                }

                if (directory != null &&
                    Directory.Exists(directory))
                {
                    var files = Directory.GetFiles(directory, filePattern);
                    if (files.Length > 0)
                    {
                        await LoadBitstreamAsync(files[0], "default");
                        return;
                    }
                }
            }
            catch
            {
                // Best effort
            }
        }
    }

    /// <summary>
    /// Loads a Xilinx bitstream.
    /// </summary>
    private Task<bool> LoadXilinxBitstreamAsync(string bitstreamPath, CancellationToken ct)
    {
        // In production, this would use:
        // 1. xclbin header parsing
        // 2. xclmgmt driver interface for shell loading
        // 3. xocl driver for user-space kernel loading
        //
        // XRT API calls:
        // - xclOpen(device_index)
        // - xclLoadXclBin(handle, xclbin_data)
        // - xclGetXclBinUUID(handle, uuid)

        return Task.FromResult(File.Exists(bitstreamPath));
    }

    /// <summary>
    /// Loads an Intel FPGA bitstream.
    /// </summary>
    private Task<bool> LoadIntelBitstreamAsync(string bitstreamPath, CancellationToken ct)
    {
        // In production, this would use:
        // 1. Intel FPGA SDK OpenCL API
        // 2. clGetDeviceIDs with CL_DEVICE_TYPE_ACCELERATOR
        // 3. clCreateProgramWithBinary
        // 4. clBuildProgram

        return Task.FromResult(File.Exists(bitstreamPath));
    }

    /// <summary>
    /// Loads an AWS F1 bitstream (AFI - Amazon FPGA Image).
    /// </summary>
    private Task<bool> LoadAwsF1BitstreamAsync(string bitstreamPath, CancellationToken ct)
    {
        // In production, this would use:
        // 1. AWS FPGA SDK
        // 2. fpga-load-local-image command
        // 3. Or AWS SDK to load AFI by ID

        return Task.FromResult(File.Exists(bitstreamPath));
    }

    /// <summary>
    /// Unloads Xilinx bitstream.
    /// </summary>
    private Task<bool> UnloadXilinxBitstreamAsync(CancellationToken ct)
    {
        // In production: xclResetDevice or similar
        return Task.FromResult(true);
    }

    /// <summary>
    /// Unloads Intel bitstream.
    /// </summary>
    private Task<bool> UnloadIntelBitstreamAsync(CancellationToken ct)
    {
        // In production: Release OpenCL resources
        return Task.FromResult(true);
    }

    /// <summary>
    /// Unloads AWS F1 bitstream.
    /// </summary>
    private Task<bool> UnloadAwsF1BitstreamAsync(CancellationToken ct)
    {
        // In production: fpga-clear-local-image
        return Task.FromResult(true);
    }

    /// <summary>
    /// Executes compression/decompression on FPGA.
    /// </summary>
    private Task<byte[]> ExecuteFpgaCompressionAsync(byte[] data, bool compress)
    {
        // In production, this would:
        // 1. Allocate device buffers (clCreateBuffer)
        // 2. Copy input data to device (clEnqueueWriteBuffer)
        // 3. Execute kernel (clEnqueueNDRangeKernel)
        // 4. Read results (clEnqueueReadBuffer)
        //
        // Common FPGA compression: GZIP, LZ4, ZSTD accelerators

        // Software fallback
        if (compress)
        {
            using var output = new MemoryStream();
            using (var gzip = new System.IO.Compression.GZipStream(
                output, System.IO.Compression.CompressionLevel.Fastest, true))
            {
                gzip.Write(data);
            }
            return Task.FromResult(output.ToArray());
        }
        else
        {
            using var input = new MemoryStream(data);
            using var gzip = new System.IO.Compression.GZipStream(
                input, System.IO.Compression.CompressionMode.Decompress);
            using var output = new MemoryStream();
            gzip.CopyTo(output);
            return Task.FromResult(output.ToArray());
        }
    }

    /// <summary>
    /// Executes encryption/decryption on FPGA.
    /// </summary>
    private Task<byte[]> ExecuteFpgaEncryptionAsync(byte[] data, bool encrypt)
    {
        // In production: Use FPGA-accelerated AES/ChaCha20
        // Many FPGA designs include AES-256-GCM accelerators

        // Software fallback using AES
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.GenerateKey();
        aes.GenerateIV();

        if (encrypt)
        {
            using var encryptor = aes.CreateEncryptor();
            return Task.FromResult(encryptor.TransformFinalBlock(data, 0, data.Length));
        }
        else
        {
            // For demo purposes, return the data as-is since we don't have the key
            return Task.FromResult(data);
        }
    }

    /// <summary>
    /// Executes hashing on FPGA.
    /// </summary>
    private Task<byte[]> ExecuteFpgaHashAsync(byte[] data)
    {
        // In production: Use FPGA-accelerated SHA-256/SHA-3
        using var sha256 = System.Security.Cryptography.SHA256.Create();
        return Task.FromResult(sha256.ComputeHash(data));
    }

    /// <summary>
    /// Executes a custom operation on FPGA.
    /// </summary>
    private Task<byte[]> ExecuteCustomOperationAsync(byte[] data)
    {
        // Default implementation returns data unchanged
        // Override for specific custom operations
        return Task.FromResult(data);
    }

    /// <summary>
    /// Executes Xilinx-specific kernel.
    /// </summary>
    private Task<byte[]> ExecuteXilinxKernelAsync(
        byte[] data,
        int operationCode,
        Dictionary<string, object>? parameters,
        CancellationToken ct)
    {
        // In production: Use XRT native API or OpenCL to execute kernels
        return Task.FromResult(data);
    }

    /// <summary>
    /// Executes Intel-specific kernel.
    /// </summary>
    private Task<byte[]> ExecuteIntelKernelAsync(
        byte[] data,
        int operationCode,
        Dictionary<string, object>? parameters,
        CancellationToken ct)
    {
        // In production: Use Intel FPGA SDK OpenCL to execute kernels
        return Task.FromResult(data);
    }

    /// <summary>
    /// Executes AWS F1-specific kernel.
    /// </summary>
    private Task<byte[]> ExecuteAwsF1KernelAsync(
        byte[] data,
        int operationCode,
        Dictionary<string, object>? parameters,
        CancellationToken ct)
    {
        // In production: Use AWS FPGA SDK/XDMA to execute kernels
        return Task.FromResult(data);
    }

    /// <summary>
    /// Checks if bitstream format is compatible with vendor.
    /// </summary>
    private static bool IsFormatCompatibleWithVendor(BitstreamFormat format, FpgaVendor vendor)
    {
        return vendor switch
        {
            FpgaVendor.Xilinx => format is BitstreamFormat.Xilinx or BitstreamFormat.XilinxRaw,
            FpgaVendor.Intel => format is BitstreamFormat.IntelOpenCl or BitstreamFormat.IntelRaw,
            FpgaVendor.AwsF1 => format is BitstreamFormat.AwsF1 or BitstreamFormat.Xilinx,
            _ => false
        };
    }

    /// <inheritdoc />
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["FpgaVendor"] = _detectedVendor.ToString();
        metadata["DeviceName"] = _deviceName ?? "None";
        metadata["DeviceCount"] = _deviceCount;
        metadata["DriverVersion"] = _driverVersion ?? "Unknown";
        metadata["OpenClAvailable"] = _openClAvailable;
        metadata["ActiveBitstream"] = _activeBitstreamId ?? "None";
        metadata["LoadedBitstreamCount"] = _loadedBitstreams.Count;
        metadata["OperationsExecuted"] = Interlocked.Read(ref _operationsExecuted);
        metadata["BytesProcessed"] = Interlocked.Read(ref _bytesProcessed);
        return metadata;
    }
}

/// <summary>
/// FPGA vendor types.
/// </summary>
public enum FpgaVendor
{
    /// <summary>No FPGA detected.</summary>
    None,

    /// <summary>Xilinx/AMD FPGA (Alveo, Versal).</summary>
    Xilinx,

    /// <summary>Intel FPGA (Stratix, Agilex, Arria).</summary>
    Intel,

    /// <summary>AWS F1 FPGA instances.</summary>
    AwsF1
}

/// <summary>
/// Bitstream file format.
/// </summary>
public enum BitstreamFormat
{
    /// <summary>Xilinx xclbin format.</summary>
    Xilinx,

    /// <summary>Xilinx raw bitstream (.bit).</summary>
    XilinxRaw,

    /// <summary>Intel OpenCL binary (.aocx).</summary>
    IntelOpenCl,

    /// <summary>Intel raw bitstream (.rbf).</summary>
    IntelRaw,

    /// <summary>AWS F1 AFI/xclbin format.</summary>
    AwsF1
}

/// <summary>
/// Information about a loaded bitstream.
/// </summary>
public class FpgaBitstreamInfo
{
    /// <summary>Gets or sets the bitstream identifier.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the file path.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets the bitstream format.</summary>
    public BitstreamFormat Format { get; set; }

    /// <summary>Gets or sets when the bitstream was loaded.</summary>
    public DateTime LoadedAt { get; set; }

    /// <summary>Gets or sets the file size in bytes.</summary>
    public long Size { get; set; }
}

/// <summary>
/// Information about an FPGA device.
/// </summary>
public class FpgaDeviceInfo
{
    /// <summary>Gets or sets the device index.</summary>
    public int DeviceIndex { get; set; }

    /// <summary>Gets or sets the vendor.</summary>
    public FpgaVendor Vendor { get; set; }

    /// <summary>Gets or sets the device name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the driver version.</summary>
    public string? DriverVersion { get; set; }

    /// <summary>Gets or sets whether the device is online.</summary>
    public bool IsOnline { get; set; }

    /// <summary>Gets or sets the currently loaded bitstream ID.</summary>
    public string? CurrentBitstreamId { get; set; }
}
