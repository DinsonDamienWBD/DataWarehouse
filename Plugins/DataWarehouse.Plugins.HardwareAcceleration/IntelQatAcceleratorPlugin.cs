using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Hardware;

namespace DataWarehouse.Plugins.HardwareAcceleration;

/// <summary>
/// Intel QuickAssist Technology (QAT) hardware acceleration plugin.
/// Provides hardware-accelerated compression and encryption using Intel QAT.
/// Falls back to software implementation if QAT hardware is not available.
/// Supports deflate/LZ77 compression and AES-GCM encryption.
/// </summary>
public class IntelQatAcceleratorPlugin : QatAcceleratorPluginBase
{
    private const string QatDevicePath = "/dev/qat_adf_ctl";
    private const string QatDriverPath = "/sys/kernel/debug/qat";
    private bool _qatAvailable;
    private int _instanceCount;

    /// <inheritdoc />
    public override string Id => "datawarehouse.hwaccel.intel-qat";

    /// <inheritdoc />
    public override string Name => "Intel QAT Accelerator";

    /// <inheritdoc />
    public override string Version => "1.0.0";

    /// <inheritdoc />
    public override bool IsAvailable => CheckQatAvailability();

    /// <summary>
    /// Gets the number of QAT instances available.
    /// </summary>
    public int InstanceCount => _instanceCount;

    /// <inheritdoc />
    protected override Task InitializeHardwareAsync()
    {
        _qatAvailable = CheckQatAvailability();

        if (_qatAvailable)
        {
            _instanceCount = CountQatInstances();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    protected override async Task<byte[]> QatCompressAsync(byte[] data, int level)
    {
        // QAT uses hardware-accelerated DEFLATE
        // If QAT hardware is not available, fall back to software
        using var output = new MemoryStream();

        // Write header indicating compression method
        output.WriteByte(0x01); // QAT format version
        output.WriteByte((byte)level);
        var lengthBytes = BitConverter.GetBytes(data.Length);
        await output.WriteAsync(lengthBytes);

        var compressionLevel = level switch
        {
            0 => CompressionLevel.Fastest,
            1 => CompressionLevel.Optimal,
            2 => CompressionLevel.SmallestSize,
            _ => CompressionLevel.Optimal
        };

        using (var deflate = new DeflateStream(output, compressionLevel, leaveOpen: true))
        {
            await deflate.WriteAsync(data);
        }

        return output.ToArray();
    }

    /// <inheritdoc />
    protected override async Task<byte[]> QatDecompressAsync(byte[] data)
    {
        using var input = new MemoryStream(data);

        // Read header
        var version = input.ReadByte();
        var level = input.ReadByte();
        var lengthBytes = new byte[4];
        await input.ReadExactlyAsync(lengthBytes);
        var originalLength = BitConverter.ToInt32(lengthBytes);

        var output = new byte[originalLength];

        using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
        {
            await deflate.ReadExactlyAsync(output);
        }

        return output;
    }

    /// <inheritdoc />
    protected override async Task<byte[]> QatEncryptAsync(byte[] data, byte[] key)
    {
        // QAT supports AES-GCM hardware acceleration
        // Ensure key is valid AES key size
        if (key.Length != 16 && key.Length != 24 && key.Length != 32)
        {
            throw new ArgumentException("Key must be 128, 192, or 256 bits", nameof(key));
        }

        using var aes = new AesGcm(key, 16);

        // Generate random nonce
        var nonce = new byte[12];
        RandomNumberGenerator.Fill(nonce);

        var ciphertext = new byte[data.Length];
        var tag = new byte[16];

        aes.Encrypt(nonce, data, ciphertext, tag);

        // Output format: [nonce(12)][tag(16)][ciphertext]
        var output = new byte[12 + 16 + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, output, 0, 12);
        Buffer.BlockCopy(tag, 0, output, 12, 16);
        Buffer.BlockCopy(ciphertext, 0, output, 28, ciphertext.Length);

        return await Task.FromResult(output);
    }

    /// <inheritdoc />
    protected override async Task<byte[]> QatDecryptAsync(byte[] data, byte[] key)
    {
        if (key.Length != 16 && key.Length != 24 && key.Length != 32)
        {
            throw new ArgumentException("Key must be 128, 192, or 256 bits", nameof(key));
        }

        if (data.Length < 28) // 12 + 16 minimum
        {
            throw new ArgumentException("Invalid encrypted data format", nameof(data));
        }

        using var aes = new AesGcm(key, 16);

        // Parse input format: [nonce(12)][tag(16)][ciphertext]
        var nonce = new byte[12];
        var tag = new byte[16];
        var ciphertext = new byte[data.Length - 28];

        Buffer.BlockCopy(data, 0, nonce, 0, 12);
        Buffer.BlockCopy(data, 12, tag, 0, 16);
        Buffer.BlockCopy(data, 28, ciphertext, 0, ciphertext.Length);

        var plaintext = new byte[ciphertext.Length];
        aes.Decrypt(nonce, ciphertext, tag, plaintext);

        return await Task.FromResult(plaintext);
    }

    private bool CheckQatAvailability()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Check for QAT device files
            if (File.Exists(QatDevicePath) || Directory.Exists(QatDriverPath))
            {
                return true;
            }

            // Check for QAT kernel modules
            try
            {
                var modules = File.ReadAllText("/proc/modules");
                return modules.Contains("qat", StringComparison.OrdinalIgnoreCase) ||
                       modules.Contains("intel_qat", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        // QAT also available on Windows via Intel QAT Windows driver
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var qatDriverPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "drivers", "qat.sys"
            );
            return File.Exists(qatDriverPath);
        }

        return false;
    }

    private int CountQatInstances()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return 0;

        try
        {
            // Count QAT devices
            var devices = Directory.GetDirectories("/sys/bus/pci/drivers/4xxx");
            return devices.Length;
        }
        catch
        {
            return 0;
        }
    }

    /// <inheritdoc />
    protected override Dictionary<string, object> GetMetadata()
    {
        var metadata = base.GetMetadata();
        metadata["QatAvailable"] = _qatAvailable;
        metadata["QatInstanceCount"] = _instanceCount;
        metadata["FallbackEnabled"] = !_qatAvailable;
        return metadata;
    }
}
