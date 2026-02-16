using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Hardware;
using System;
using System.IO;

namespace DataWarehouse.SDK.Edge.Bus;

/// <summary>
/// Factory for creating bus controllers with automatic platform capability detection.
/// </summary>
/// <remarks>
/// <para>
/// The factory selects real bus controllers (GPIO, I2C, SPI) when hardware is present and
/// null controllers (no-op fallback) when hardware is absent. This enables code to run
/// on both IoT devices (Raspberry Pi, BeagleBone) and non-IoT platforms (servers, desktops)
/// without modification.
/// </para>
/// <para>
/// <strong>Capability Detection:</strong> The factory uses two methods:
/// <list type="number">
///   <item><description><see cref="IPlatformCapabilityRegistry"/> if provided (preferred)</description></item>
///   <item><description>Filesystem checks (/sys/class/gpio, /sys/class/i2c-adapter) as fallback</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Thread Safety:</strong> All factory methods are thread-safe and can be called
/// concurrently without external synchronization.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: Bus controller factory with capability detection (EDGE-01)")]
public static class BusControllerFactory
{
    /// <summary>
    /// Creates a GPIO bus controller, using real implementation if gpio capability is present,
    /// otherwise returns a null controller.
    /// </summary>
    /// <param name="registry">
    /// Optional platform capability registry. If null, performs filesystem-based detection.
    /// </param>
    /// <param name="pinMapping">
    /// Optional board-specific pin mapping. If null, defaults to Raspberry Pi 4.
    /// Ignored for null controllers.
    /// </param>
    /// <returns>
    /// An <see cref="IGpioBusController"/> instance. Either <see cref="GpioBusController"/>
    /// if GPIO hardware is detected, or <see cref="NullGpioBusController"/> otherwise.
    /// </returns>
    /// <remarks>
    /// <para>
    /// GPIO hardware detection:
    /// <list type="bullet">
    ///   <item><description>If registry provided: checks for "gpio" capability</description></item>
    ///   <item><description>If no registry: checks for /sys/class/gpio directory on Linux</description></item>
    ///   <item><description>Non-Linux platforms: assumes no GPIO (returns null controller)</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    public static IGpioBusController CreateGpioController(
        IPlatformCapabilityRegistry? registry = null,
        PinMapping? pinMapping = null)
    {
        bool hasGpio = false;

        // Check registry first if provided
        if (registry != null)
        {
            hasGpio = registry.HasCapability("gpio");
        }
        else
        {
            // Fallback: Check if running on Linux with GPIO support
            if (OperatingSystem.IsLinux() && Directory.Exists("/sys/class/gpio"))
            {
                hasGpio = true;
            }
        }

        return hasGpio
            ? new GpioBusController(pinMapping)
            : new NullGpioBusController();
    }

    /// <summary>
    /// Creates an I2C bus controller, using real implementation if i2c capability is present,
    /// otherwise returns a null controller.
    /// </summary>
    /// <param name="registry">
    /// Optional platform capability registry. If null, performs filesystem-based detection.
    /// </param>
    /// <returns>
    /// An <see cref="II2cBusController"/> instance. Either <see cref="I2cBusController"/>
    /// if I2C hardware is detected, or <see cref="NullI2cBusController"/> otherwise.
    /// </returns>
    /// <remarks>
    /// <para>
    /// I2C hardware detection:
    /// <list type="bullet">
    ///   <item><description>If registry provided: checks for "i2c" capability</description></item>
    ///   <item><description>If no registry: checks for /sys/class/i2c-adapter directory on Linux</description></item>
    ///   <item><description>Non-Linux platforms: assumes no I2C (returns null controller)</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    public static II2cBusController CreateI2cController(IPlatformCapabilityRegistry? registry = null)
    {
        bool hasI2c = false;

        // Check registry first if provided
        if (registry != null)
        {
            hasI2c = registry.HasCapability("i2c");
        }
        else
        {
            // Fallback: Check if running on Linux with I2C support
            if (OperatingSystem.IsLinux() && Directory.Exists("/sys/class/i2c-adapter"))
            {
                hasI2c = true;
            }
        }

        return hasI2c
            ? new I2cBusController()
            : new NullI2cBusController();
    }

    /// <summary>
    /// Creates an SPI bus controller, using real implementation if spi capability is present,
    /// otherwise returns a null controller.
    /// </summary>
    /// <param name="registry">
    /// Optional platform capability registry. If null, performs filesystem-based detection.
    /// </param>
    /// <returns>
    /// An <see cref="ISpiBusController"/> instance. Either <see cref="SpiBusController"/>
    /// if SPI hardware is detected, or <see cref="NullSpiBusController"/> otherwise.
    /// </returns>
    /// <remarks>
    /// <para>
    /// SPI hardware detection:
    /// <list type="bullet">
    ///   <item><description>If registry provided: checks for "spi" capability</description></item>
    ///   <item><description>If no registry: checks for /sys/class/spi_master directory on Linux</description></item>
    ///   <item><description>Non-Linux platforms: assumes no SPI (returns null controller)</description></item>
    /// </list>
    /// </para>
    /// </remarks>
    public static ISpiBusController CreateSpiController(IPlatformCapabilityRegistry? registry = null)
    {
        bool hasSpi = false;

        // Check registry first if provided
        if (registry != null)
        {
            hasSpi = registry.HasCapability("spi");
        }
        else
        {
            // Fallback: Check if running on Linux with SPI support
            if (OperatingSystem.IsLinux() && Directory.Exists("/sys/class/spi_master"))
            {
                hasSpi = true;
            }
        }

        return hasSpi
            ? new SpiBusController()
            : new NullSpiBusController();
    }
}
