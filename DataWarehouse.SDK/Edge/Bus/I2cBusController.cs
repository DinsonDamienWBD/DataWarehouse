using DataWarehouse.SDK.Contracts;
using System;
using System.Device.I2c;

namespace DataWarehouse.SDK.Edge.Bus;

/// <summary>
/// I2C bus controller implementation using System.Device.I2c.
/// </summary>
/// <remarks>
/// <para>
/// This implementation wraps <see cref="I2cDevice"/> from System.Device.I2c with DataWarehouse
/// semantics and error handling conventions.
/// </para>
/// <para>
/// <strong>Platform Support:</strong>
/// <list type="bullet">
///   <item><description>Linux: Uses /dev/i2c-N devices for I2C bus access</description></item>
///   <item><description>Windows: Uses Windows.Devices.I2c API (Windows IoT only)</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Thread Safety:</strong> This class is thread-safe. Multiple devices can be opened
/// on the same bus concurrently. However, individual <see cref="II2cDevice"/> instances are
/// NOT thread-safe (serialize operations or use external locking).
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: I2C wrapper using System.Device.I2c (EDGE-01)")]
public sealed class I2cBusController : II2cBusController
{
    private bool _disposed;

    /// <inheritdoc/>
    public II2cDevice OpenDevice(int busId, int deviceAddress)
    {
        ThrowIfDisposed();

        if (busId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(busId), busId, "Bus ID must be non-negative");
        }

        if (deviceAddress < 0 || deviceAddress > EdgeConstants.MaxI2cDeviceAddress)
        {
            throw new ArgumentOutOfRangeException(
                nameof(deviceAddress), deviceAddress,
                $"Device address must be between 0 and {EdgeConstants.MaxI2cDeviceAddress}");
        }

        try
        {
            var settings = new I2cConnectionSettings(busId, deviceAddress);
            var device = I2cDevice.Create(settings);
            return new I2cDeviceWrapper(device);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to open I2C device at bus {busId}, address 0x{deviceAddress:X2}: {ex.Message}", ex);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _disposed = true;
        // No cleanup needed - devices manage their own resources
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(I2cBusController));
        }
    }

    /// <summary>
    /// Wrapper around System.Device.I2c.I2cDevice implementing II2cDevice.
    /// </summary>
    private sealed class I2cDeviceWrapper : II2cDevice
    {
        private readonly I2cDevice _device;
        private bool _disposed;

        public I2cDeviceWrapper(I2cDevice device)
        {
            _device = device;
        }

        public void Read(Span<byte> buffer)
        {
            ThrowIfDisposed();

            try
            {
                _device.Read(buffer);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"I2C read operation failed: {ex.Message}", ex);
            }
        }

        public void Write(ReadOnlySpan<byte> data)
        {
            ThrowIfDisposed();

            try
            {
                _device.Write(data);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"I2C write operation failed: {ex.Message}", ex);
            }
        }

        public void WriteRead(ReadOnlySpan<byte> writeBuffer, Span<byte> readBuffer)
        {
            ThrowIfDisposed();

            try
            {
                _device.WriteRead(writeBuffer, readBuffer);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"I2C write-read operation failed: {ex.Message}", ex);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _device.Dispose();
            _disposed = true;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(I2cDeviceWrapper));
            }
        }
    }
}
