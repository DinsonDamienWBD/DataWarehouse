using DataWarehouse.SDK.Contracts;
using System;

namespace DataWarehouse.SDK.Hardware
{
    /// <summary>
    /// Hardware device type classification flags.
    /// Multiple flags can be combined to represent devices with multiple capabilities.
    /// </summary>
    [Flags]
    [SdkCompatibility("3.0.0", Notes = "Phase 32: Hardware device type classification (HAL-02)")]
    public enum HardwareDeviceType
    {
        /// <summary>No device type specified.</summary>
        None = 0,

        /// <summary>PCI or PCIe bus device.</summary>
        PciDevice = 1,

        /// <summary>USB-connected device.</summary>
        UsbDevice = 2,

        /// <summary>NVMe storage controller.</summary>
        NvmeController = 4,

        /// <summary>NVMe namespace (logical storage unit).</summary>
        NvmeNamespace = 8,

        /// <summary>GPU compute accelerator (CUDA, ROCm, DirectML).</summary>
        GpuAccelerator = 16,

        /// <summary>Block storage device (HDD, SSD, virtual disk).</summary>
        BlockDevice = 32,

        /// <summary>Network interface card.</summary>
        NetworkAdapter = 64,

        /// <summary>GPIO pin controller (edge/IoT).</summary>
        GpioController = 128,

        /// <summary>I2C bus controller.</summary>
        I2cBus = 256,

        /// <summary>SPI bus controller.</summary>
        SpiBus = 512,

        /// <summary>Trusted Platform Module (TPM).</summary>
        TpmDevice = 1024,

        /// <summary>Hardware Security Module (HSM).</summary>
        HsmDevice = 2048,

        /// <summary>Intel QuickAssist Technology accelerator.</summary>
        QatAccelerator = 4096,

        /// <summary>Serial or UART port.</summary>
        SerialPort = 8192,

        /// <summary>SCSI controller (including paravirtualized variants).</summary>
        ScsiController = 16384
    }
}
