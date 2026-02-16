using DataWarehouse.SDK.Contracts;
using System;

namespace DataWarehouse.SDK.Edge.Bus;

/// <summary>
/// Controller for SPI (Serial Peripheral Interface) bus communication.
/// </summary>
/// <remarks>
/// <para>
/// SPI is a full-duplex synchronous serial bus protocol for high-speed peripherals (displays,
/// ADCs, DACs, flash memory). SPI uses four wires: MOSI (master out, slave in), MISO (master in,
/// slave out), SCLK (clock), and CS/SS (chip select).
/// </para>
/// <para>
/// <strong>Bus IDs:</strong> Linux systems expose SPI buses as /dev/spidevN.M devices, where
/// N is the bus ID and M is the chip select. SPI bus 0 (/dev/spidev0.0) is the default on
/// most Raspberry Pi and similar boards.
/// </para>
/// <para>
/// <strong>Chip Select:</strong> SPI supports multiple devices on the same bus, differentiated
/// by chip select lines (CS0, CS1, etc.). Each device has a unique chip select. Only one device
/// is active (CS low) at a time.
/// </para>
/// <para>
/// <strong>Clock and Mode:</strong> SPI clock frequency and mode (CPOL/CPHA) must match the
/// device requirements. Mode defines clock polarity and phase:
/// <list type="bullet">
///   <item><description>Mode 0: CPOL=0, CPHA=0 (clock idle low, sample on rising edge)</description></item>
///   <item><description>Mode 1: CPOL=0, CPHA=1 (clock idle low, sample on falling edge)</description></item>
///   <item><description>Mode 2: CPOL=1, CPHA=0 (clock idle high, sample on falling edge)</description></item>
///   <item><description>Mode 3: CPOL=1, CPHA=1 (clock idle high, sample on rising edge)</description></item>
/// </list>
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: SPI bus controller interface (EDGE-01)")]
public interface ISpiBusController : IDisposable
{
    /// <summary>
    /// Opens a connection to an SPI device.
    /// </summary>
    /// <param name="busId">SPI bus ID (e.g., 0 for /dev/spidev0 on Linux).</param>
    /// <param name="chipSelect">Chip select line (0-15).</param>
    /// <param name="clockFrequency">SPI clock frequency in Hz (e.g., 1000000 for 1 MHz).</param>
    /// <param name="mode">SPI mode (0-3, defines CPOL and CPHA).</param>
    /// <returns>An <see cref="ISpiDevice"/> instance for communicating with the device.</returns>
    /// <remarks>
    /// Clock frequency should not exceed the device's maximum rated frequency (check datasheet).
    /// Typical values: 1 MHz for low-speed devices, 10-50 MHz for high-speed devices.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if busId, chipSelect, or clockFrequency is out of range.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the device cannot be opened (hardware not present, permission denied).</exception>
    ISpiDevice OpenDevice(int busId, int chipSelect, int clockFrequency, SpiMode mode);
}

/// <summary>
/// Represents an open SPI device connection.
/// </summary>
/// <remarks>
/// <para>
/// SPI is a full-duplex protocol: data is transmitted and received simultaneously. Every
/// transfer operation writes data while reading data at the same time.
/// </para>
/// <para>
/// <strong>Thread Safety:</strong> SPI device instances are NOT thread-safe. Serialize all
/// operations on the same device or use external locking.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: SPI device interface (EDGE-01)")]
public interface ISpiDevice : IDisposable
{
    /// <summary>
    /// Performs a full-duplex SPI transfer (simultaneous write and read).
    /// </summary>
    /// <param name="writeBuffer">Data to write. All bytes in the span are transmitted.</param>
    /// <param name="readBuffer">Buffer to receive data. Must be the same length as writeBuffer.</param>
    /// <remarks>
    /// <para>
    /// SPI is full-duplex: while writeBuffer bytes are transmitted, readBuffer bytes are received
    /// simultaneously. The two buffers must have the same length. If you only need to write data,
    /// use <see cref="Write"/>. If you only need to read data, use <see cref="Read"/>.
    /// </para>
    /// <para>
    /// <strong>Example:</strong> To read a sensor register via SPI:
    /// <code>
    /// byte[] writeData = { 0x80, 0x00 }; // Command byte + dummy byte
    /// byte[] readData = new byte[2];
    /// device.Transfer(writeData, readData);
    /// // readData[1] contains the sensor value
    /// </code>
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">Thrown if writeBuffer and readBuffer lengths differ.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the transfer fails (bus error).</exception>
    void Transfer(ReadOnlySpan<byte> writeBuffer, Span<byte> readBuffer);

    /// <summary>
    /// Writes data to the SPI device (read buffer is ignored/discarded).
    /// </summary>
    /// <param name="data">Data to write. All bytes in the span are transmitted.</param>
    /// <remarks>
    /// This is a convenience method for write-only operations. Data received during the write
    /// is discarded. Internally, this performs a full-duplex transfer with a dummy read buffer.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown if the write fails (bus error).</exception>
    void Write(ReadOnlySpan<byte> data);

    /// <summary>
    /// Reads data from the SPI device (write buffer is all zeros).
    /// </summary>
    /// <param name="buffer">Buffer to receive data. The number of bytes read equals buffer.Length.</param>
    /// <remarks>
    /// This is a convenience method for read-only operations. Zeros are transmitted during the read.
    /// Internally, this performs a full-duplex transfer with a zero-filled write buffer.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown if the read fails (bus error).</exception>
    void Read(Span<byte> buffer);
}

/// <summary>
/// SPI mode configuration (clock polarity and phase).
/// </summary>
/// <remarks>
/// SPI mode determines clock polarity (CPOL) and phase (CPHA):
/// <list type="bullet">
///   <item><description>Mode 0 (CPOL=0, CPHA=0): Clock idle low, sample on rising edge</description></item>
///   <item><description>Mode 1 (CPOL=0, CPHA=1): Clock idle low, sample on falling edge</description></item>
///   <item><description>Mode 2 (CPOL=1, CPHA=0): Clock idle high, sample on falling edge</description></item>
///   <item><description>Mode 3 (CPOL=1, CPHA=1): Clock idle high, sample on rising edge</description></item>
/// </list>
/// Most devices use Mode 0 or Mode 3. Check the device datasheet for the correct mode.
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: SPI mode (EDGE-01)")]
public enum SpiMode
{
    /// <summary>
    /// Mode 0: CPOL=0, CPHA=0 (clock idle low, sample on rising edge).
    /// </summary>
    Mode0 = 0,

    /// <summary>
    /// Mode 1: CPOL=0, CPHA=1 (clock idle low, sample on falling edge).
    /// </summary>
    Mode1 = 1,

    /// <summary>
    /// Mode 2: CPOL=1, CPHA=0 (clock idle high, sample on falling edge).
    /// </summary>
    Mode2 = 2,

    /// <summary>
    /// Mode 3: CPOL=1, CPHA=1 (clock idle high, sample on rising edge).
    /// </summary>
    Mode3 = 3
}
