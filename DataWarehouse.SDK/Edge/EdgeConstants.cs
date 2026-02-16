using DataWarehouse.SDK.Contracts;

namespace DataWarehouse.SDK.Edge;

/// <summary>
/// Constants for edge/IoT hardware bus operations.
/// </summary>
/// <remarks>
/// These constants define default bus IDs, pin limits, and other hardware-specific
/// constraints for GPIO, I2C, and SPI bus operations on edge devices.
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: Edge/IoT constants (EDGE-01)")]
public static class EdgeConstants
{
    /// <summary>
    /// Default I2C bus ID on Linux (/dev/i2c-1).
    /// </summary>
    /// <remarks>
    /// On most Raspberry Pi and similar boards, /dev/i2c-1 is the default user-accessible I2C bus.
    /// /dev/i2c-0 is often reserved for HAT EEPROM or internal use.
    /// </remarks>
    public const int DefaultI2cBusId = 1;

    /// <summary>
    /// Default SPI bus ID on Linux (/dev/spidev0.0).
    /// </summary>
    /// <remarks>
    /// SPI bus 0 with chip select 0 is the most common default configuration on Raspberry Pi
    /// and similar boards. Additional SPI buses may be available depending on hardware.
    /// </remarks>
    public const int DefaultSpiBusId = 0;

    /// <summary>
    /// Default GPIO chip ID on Linux (/dev/gpiochip0).
    /// </summary>
    /// <remarks>
    /// Most boards have a single GPIO chip (gpiochip0). Multi-chip configurations are rare
    /// but may occur on boards with GPIO expanders or multiple GPIO controllers.
    /// </remarks>
    public const int DefaultGpioChip = 0;

    /// <summary>
    /// Maximum number of GPIO pins supported.
    /// </summary>
    /// <remarks>
    /// This limit is conservative and covers most edge devices. Actual pin count varies by board:
    /// <list type="bullet">
    ///   <item><description>Raspberry Pi 4/5: 40 physical pins (28 GPIO)</description></item>
    ///   <item><description>BeagleBone Black: 92 GPIO pins</description></item>
    ///   <item><description>Jetson Nano: 40 physical pins</description></item>
    /// </list>
    /// </remarks>
    public const int MaxGpioPins = 256;

    /// <summary>
    /// Maximum I2C device address (7-bit addressing).
    /// </summary>
    /// <remarks>
    /// I2C uses 7-bit addressing, providing 128 possible addresses (0-127).
    /// Some addresses are reserved (0x00-0x07, 0x78-0x7F) for special purposes.
    /// </remarks>
    public const int MaxI2cDeviceAddress = 127;

    /// <summary>
    /// Maximum SPI chip select line number.
    /// </summary>
    /// <remarks>
    /// Most SPI controllers support 2-4 chip select lines. This limit allows for
    /// configurations with multiple chip selects or GPIO-based chip select lines.
    /// </remarks>
    public const int MaxSpiChipSelect = 15;
}
