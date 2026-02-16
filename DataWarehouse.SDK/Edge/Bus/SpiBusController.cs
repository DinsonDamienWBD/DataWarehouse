using DataWarehouse.SDK.Contracts;
using System;
using System.Device.Spi;

namespace DataWarehouse.SDK.Edge.Bus;

/// <summary>
/// SPI bus controller implementation using System.Device.Spi.
/// </summary>
/// <remarks>
/// <para>
/// This implementation wraps <see cref="SpiDevice"/> from System.Device.Spi with DataWarehouse
/// semantics and error handling conventions.
/// </para>
/// <para>
/// <strong>Platform Support:</strong>
/// <list type="bullet">
///   <item><description>Linux: Uses /dev/spidevN.M devices for SPI bus access</description></item>
///   <item><description>Windows: Uses Windows.Devices.Spi API (Windows IoT only)</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Thread Safety:</strong> This class is thread-safe. Multiple devices can be opened
/// on the same bus concurrently. However, individual <see cref="ISpiDevice"/> instances are
/// NOT thread-safe (serialize operations or use external locking).
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: SPI wrapper using System.Device.Spi (EDGE-01)")]
public sealed class SpiBusController : ISpiBusController
{
    private bool _disposed;

    /// <inheritdoc/>
    public ISpiDevice OpenDevice(int busId, int chipSelect, int clockFrequency, SpiMode mode)
    {
        ThrowIfDisposed();

        if (busId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(busId), busId, "Bus ID must be non-negative");
        }

        if (chipSelect < 0 || chipSelect > EdgeConstants.MaxSpiChipSelect)
        {
            throw new ArgumentOutOfRangeException(
                nameof(chipSelect), chipSelect,
                $"Chip select must be between 0 and {EdgeConstants.MaxSpiChipSelect}");
        }

        if (clockFrequency <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(clockFrequency), clockFrequency, "Clock frequency must be positive");
        }

        try
        {
            var settings = new SpiConnectionSettings(busId, chipSelect)
            {
                ClockFrequency = clockFrequency,
                Mode = (System.Device.Spi.SpiMode)mode // Cast to System.Device.Spi.SpiMode
            };

            var device = SpiDevice.Create(settings);
            return new SpiDeviceWrapper(device);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to open SPI device at bus {busId}, chip select {chipSelect}, " +
                $"clock {clockFrequency} Hz, mode {mode}: {ex.Message}", ex);
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
            throw new ObjectDisposedException(nameof(SpiBusController));
        }
    }

    /// <summary>
    /// Wrapper around System.Device.Spi.SpiDevice implementing ISpiDevice.
    /// </summary>
    private sealed class SpiDeviceWrapper : ISpiDevice
    {
        private readonly SpiDevice _device;
        private bool _disposed;

        public SpiDeviceWrapper(SpiDevice device)
        {
            _device = device;
        }

        public void Transfer(ReadOnlySpan<byte> writeBuffer, Span<byte> readBuffer)
        {
            ThrowIfDisposed();

            if (writeBuffer.Length != readBuffer.Length)
            {
                throw new ArgumentException(
                    $"Write buffer length ({writeBuffer.Length}) must equal read buffer length ({readBuffer.Length})");
            }

            try
            {
                _device.TransferFullDuplex(writeBuffer, readBuffer);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"SPI transfer operation failed: {ex.Message}", ex);
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
                throw new InvalidOperationException($"SPI write operation failed: {ex.Message}", ex);
            }
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
                throw new InvalidOperationException($"SPI read operation failed: {ex.Message}", ex);
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
                throw new ObjectDisposedException(nameof(SpiDeviceWrapper));
            }
        }
    }
}
