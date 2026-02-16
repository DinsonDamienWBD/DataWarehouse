using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;

namespace DataWarehouse.SDK.Edge.Bus;

/// <summary>
/// Board-specific GPIO pin number mapping.
/// </summary>
/// <remarks>
/// <para>
/// Different boards use different pin numbering schemes:
/// <list type="bullet">
///   <item><description><strong>Physical numbering:</strong> Pin numbers on the physical header (1-40)</description></item>
///   <item><description><strong>Logical numbering (BCM):</strong> GPIO controller pin numbers (used by drivers)</description></item>
/// </list>
/// </para>
/// <para>
/// This class provides mappings from physical pin numbers to logical (BCM) GPIO numbers
/// for common edge/IoT boards. Using physical numbering makes code more portable across
/// boards with compatible pin layouts.
/// </para>
/// <para>
/// <strong>Supported Boards:</strong>
/// <list type="bullet">
///   <item><description>Raspberry Pi 4 and 5 (40-pin header, BCM numbering)</description></item>
///   <item><description>BeagleBone Black (partial mapping, placeholder for future expansion)</description></item>
///   <item><description>Jetson Nano (partial mapping, placeholder for future expansion)</description></item>
/// </list>
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: Board-specific pin mappings (EDGE-01)")]
public sealed class PinMapping
{
    private readonly Dictionary<int, int> _physicalToLogical;

    /// <summary>
    /// Gets the board name for this pin mapping.
    /// </summary>
    public string BoardName { get; }

    private PinMapping(string boardName, Dictionary<int, int> mapping)
    {
        BoardName = boardName;
        _physicalToLogical = mapping;
    }

    /// <summary>
    /// Translates a physical pin number to a logical (BCM) GPIO number.
    /// </summary>
    /// <param name="physicalPin">Physical pin number on the board header.</param>
    /// <returns>Logical GPIO number for the specified physical pin.</returns>
    /// <exception cref="ArgumentException">Thrown if the physical pin number is invalid for this board.</exception>
    public int GetLogicalPin(int physicalPin)
    {
        if (_physicalToLogical.TryGetValue(physicalPin, out var logical))
            return logical;
        throw new ArgumentException($"Invalid physical pin {physicalPin} for board {BoardName}", nameof(physicalPin));
    }

    /// <summary>
    /// Gets the Raspberry Pi 4 pin mapping (40-pin header, BCM numbering).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Raspberry Pi uses Broadcom (BCM) GPIO numbering. This mapping covers all GPIO pins
    /// on the 40-pin header. Power and ground pins are excluded.
    /// </para>
    /// <para>
    /// Pin layout reference: https://pinout.xyz/
    /// </para>
    /// </remarks>
    public static PinMapping RaspberryPi4 => new("Raspberry Pi 4", new()
    {
        // GPIO pins (BCM numbering)
        [3] = 2,   // I2C SDA
        [5] = 3,   // I2C SCL
        [7] = 4,   // GPCLK0
        [8] = 14,  // UART TX
        [10] = 15, // UART RX
        [11] = 17,
        [12] = 18, // PWM0
        [13] = 27,
        [15] = 22,
        [16] = 23,
        [18] = 24,
        [19] = 10, // SPI0 MOSI
        [21] = 9,  // SPI0 MISO
        [22] = 25,
        [23] = 11, // SPI0 SCLK
        [24] = 8,  // SPI0 CE0
        [26] = 7,  // SPI0 CE1
        [29] = 5,
        [31] = 6,
        [32] = 12, // PWM0 (alt)
        [33] = 13, // PWM1
        [35] = 19, // PWM1 (alt) / SPI1 MISO
        [36] = 16,
        [37] = 26,
        [38] = 20, // SPI1 MOSI
        [40] = 21  // SPI1 SCLK
    });

    /// <summary>
    /// Gets the Raspberry Pi 5 pin mapping (40-pin header, BCM numbering).
    /// </summary>
    /// <remarks>
    /// Raspberry Pi 5 uses the same 40-pin header layout as Raspberry Pi 4, with identical
    /// GPIO numbering. This is an alias for <see cref="RaspberryPi4"/>.
    /// </remarks>
    public static PinMapping RaspberryPi5 => RaspberryPi4; // Same GPIO mapping as RPi 4

    /// <summary>
    /// Gets the BeagleBone Black pin mapping (partial implementation).
    /// </summary>
    /// <remarks>
    /// <para>
    /// BeagleBone Black has 92 GPIO pins across two headers (P8 and P9). This is a partial
    /// mapping for commonly used pins. Full mapping will be added in future updates.
    /// </para>
    /// <para>
    /// <strong>Note:</strong> BeagleBone uses different GPIO numbering than Raspberry Pi.
    /// Refer to BeagleBone documentation for complete pin reference.
    /// </para>
    /// </remarks>
    public static PinMapping BeagleBoneBlack => new("BeagleBone Black", new()
    {
        // P8 header (examples)
        // TODO: Add complete BeagleBone pin mapping
        // BeagleBone GPIO numbering: GPIO = (bank * 32) + pin_in_bank
        // Example: GPIO1_12 = (1 * 32) + 12 = 44
        [7] = 66,  // P8_07: GPIO2_2
        [8] = 67,  // P8_08: GPIO2_3
        [9] = 69,  // P8_09: GPIO2_5
        [10] = 68  // P8_10: GPIO2_4
        // Partial mapping only - expand as needed
    });

    /// <summary>
    /// Gets the Jetson Nano pin mapping (partial implementation).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Jetson Nano has a 40-pin header similar to Raspberry Pi, but with different GPIO
    /// numbering (Tegra GPIO controller). This is a partial mapping for commonly used pins.
    /// Full mapping will be added in future updates.
    /// </para>
    /// <para>
    /// <strong>Note:</strong> Jetson uses Tegra GPIO numbering. Refer to Jetson Nano
    /// documentation for complete pin reference.
    /// </para>
    /// </remarks>
    public static PinMapping JetsonNano => new("Jetson Nano", new()
    {
        // 40-pin header (Tegra GPIO numbering)
        // TODO: Add complete Jetson Nano pin mapping
        // Example mappings (partial):
        [7] = 216,   // GPIO09 (Tegra numbering)
        [11] = 50,   // UART1_RTS
        [12] = 79,   // I2S0_SCLK
        [13] = 14,   // SPI1_SCK
        [15] = 194,  // GPIO12
        [16] = 232,  // SPI1_CS1
        [18] = 15,   // SPI1_CS0
        [19] = 16,   // SPI0_MOSI
        [21] = 17,   // SPI0_MISO
        [22] = 13,   // SPI1_MISO
        [23] = 18,   // SPI0_SCK
        [24] = 19    // SPI0_CS0
        // Partial mapping only - expand as needed
    });
}
