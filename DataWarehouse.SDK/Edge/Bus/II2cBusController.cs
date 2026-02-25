using DataWarehouse.SDK.Contracts;
using System;

namespace DataWarehouse.SDK.Edge.Bus;

/// <summary>
/// Controller for I2C (Inter-Integrated Circuit) bus communication.
/// </summary>
/// <remarks>
/// <para>
/// I2C is a multi-device bus protocol for low-speed peripherals (sensors, displays, EEPROMs).
/// The bus uses two wires: SDA (data) and SCL (clock). Each device has a unique 7-bit address
/// (0-127), allowing up to 112 devices per bus (after reserved addresses).
/// </para>
/// <para>
/// <strong>Bus IDs:</strong> Linux systems expose I2C buses as /dev/i2c-N devices. Bus ID 1
/// (/dev/i2c-1) is the default on most Raspberry Pi and similar boards.
/// </para>
/// <para>
/// <strong>Device Addressing:</strong> I2C uses 7-bit addressing. Device addresses are typically
/// specified in datasheets. Common ranges: 0x08-0x77 (excluding reserved addresses 0x00-0x07,
/// 0x78-0x7F).
/// </para>
/// <para>
/// <strong>Operations:</strong> I2C supports read, write, and combined write-read transactions.
/// The write-read operation is atomic (no bus release between write and read), essential for
/// register-based peripherals.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: I2C bus controller interface (EDGE-01)")]
public interface II2cBusController : IDisposable
{
    /// <summary>
    /// Opens a connection to an I2C device.
    /// </summary>
    /// <param name="busId">I2C bus ID (e.g., 1 for /dev/i2c-1 on Linux).</param>
    /// <param name="deviceAddress">7-bit I2C device address (0-127).</param>
    /// <returns>An <see cref="II2cDevice"/> instance for communicating with the device.</returns>
    /// <remarks>
    /// Multiple devices can be opened on the same bus (different addresses). The returned
    /// device should be disposed when no longer needed to release the bus connection.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if busId or deviceAddress is out of range.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the bus or device cannot be opened (e.g., hardware not present, permission denied).</exception>
    II2cDevice OpenDevice(int busId, int deviceAddress);
}

/// <summary>
/// Represents an open I2C device connection.
/// </summary>
/// <remarks>
/// <para>
/// I2C devices support read, write, and write-read operations. Operations may fail if the
/// device does not respond (NAK) or if bus arbitration fails.
/// </para>
/// <para>
/// <strong>Thread Safety:</strong> I2C device instances are NOT thread-safe. Serialize all
/// operations on the same device or use external locking.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: I2C device interface (EDGE-01)")]
public interface II2cDevice : IDisposable
{
    /// <summary>
    /// Reads bytes from the I2C device.
    /// </summary>
    /// <param name="buffer">Buffer to receive data. The number of bytes read equals buffer.Length.</param>
    /// <remarks>
    /// This operation sends a read request to the device and fills the buffer with the response.
    /// If the device does not respond (NAK), an exception is thrown.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown if the read operation fails (device NAK, bus error).</exception>
    void Read(Span<byte> buffer);

    /// <summary>
    /// Writes bytes to the I2C device.
    /// </summary>
    /// <param name="data">Data to write. All bytes in the span are written.</param>
    /// <remarks>
    /// This operation sends a write request to the device with the specified data. If the device
    /// does not acknowledge (NAK), an exception is thrown.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown if the write operation fails (device NAK, bus error).</exception>
    void Write(ReadOnlySpan<byte> data);

    /// <summary>
    /// Writes data then reads response in a combined transaction (no bus release).
    /// </summary>
    /// <param name="writeBuffer">Data to write (typically a register address or command).</param>
    /// <param name="readBuffer">Buffer to receive response data.</param>
    /// <remarks>
    /// <para>
    /// This operation is atomic: the bus is not released between write and read. This is essential
    /// for register-based devices where you write a register address and immediately read its value.
    /// </para>
    /// <para>
    /// <strong>Example:</strong> To read a sensor register:
    /// <code>
    /// byte[] writeData = { 0x3C }; // Register address
    /// byte[] readData = new byte[2]; // 16-bit register value
    /// device.WriteRead(writeData, readData);
    /// </code>
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown if the operation fails (device NAK, bus error).</exception>
    void WriteRead(ReadOnlySpan<byte> writeBuffer, Span<byte> readBuffer);
}
