using DataWarehouse.SDK.Contracts;
using System;

namespace DataWarehouse.SDK.Edge.Bus;

/// <summary>
/// Null GPIO bus controller that provides graceful fallback on non-IoT platforms.
/// </summary>
/// <remarks>
/// This implementation performs no-ops for all operations and returns default values.
/// It never throws exceptions, enabling code that uses GPIO to run without modification
/// on platforms without GPIO hardware (servers, desktops, VMs).
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: Null GPIO controller for non-IoT platforms (EDGE-01)")]
public sealed class NullGpioBusController : IGpioBusController
{
    /// <inheritdoc/>
    public void OpenPin(int pinNumber, PinMode mode)
    {
        // No-op
    }

    /// <inheritdoc/>
    public void ClosePin(int pinNumber)
    {
        // No-op
    }

    /// <inheritdoc/>
    public PinValue Read(int pinNumber) => PinValue.Low;

    /// <inheritdoc/>
    public void Write(int pinNumber, PinValue value)
    {
        // No-op
    }

    /// <inheritdoc/>
    public void RegisterCallback(int pinNumber, PinEventEdge edge, Action<PinValueChangedEventArgs> callback)
    {
        // No-op - callbacks never invoked
    }

    /// <inheritdoc/>
    public void UnregisterCallback(int pinNumber)
    {
        // No-op
    }

    /// <inheritdoc/>
    public bool IsPinOpen(int pinNumber) => false;

    /// <inheritdoc/>
    public void Dispose()
    {
        // No-op
    }
}

/// <summary>
/// Null I2C bus controller that provides graceful fallback on non-IoT platforms.
/// </summary>
/// <remarks>
/// This implementation returns null devices that perform no-ops for all operations.
/// It never throws exceptions, enabling code that uses I2C to run without modification
/// on platforms without I2C hardware.
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: Null I2C controller for non-IoT platforms (EDGE-01)")]
public sealed class NullI2cBusController : II2cBusController
{
    /// <inheritdoc/>
    public II2cDevice OpenDevice(int busId, int deviceAddress) => new NullI2cDevice();

    /// <inheritdoc/>
    public void Dispose()
    {
        // No-op
    }
}

/// <summary>
/// Null I2C device that provides graceful fallback.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 36: Null I2C device (EDGE-01)")]
public sealed class NullI2cDevice : II2cDevice
{
    /// <inheritdoc/>
    public void Read(Span<byte> buffer) => buffer.Clear();

    /// <inheritdoc/>
    public void Write(ReadOnlySpan<byte> data)
    {
        // No-op
    }

    /// <inheritdoc/>
    public void WriteRead(ReadOnlySpan<byte> writeBuffer, Span<byte> readBuffer) => readBuffer.Clear();

    /// <inheritdoc/>
    public void Dispose()
    {
        // No-op
    }
}

/// <summary>
/// Null SPI bus controller that provides graceful fallback on non-IoT platforms.
/// </summary>
/// <remarks>
/// This implementation returns null devices that perform no-ops for all operations.
/// It never throws exceptions, enabling code that uses SPI to run without modification
/// on platforms without SPI hardware.
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: Null SPI controller for non-IoT platforms (EDGE-01)")]
public sealed class NullSpiBusController : ISpiBusController
{
    /// <inheritdoc/>
    public ISpiDevice OpenDevice(int busId, int chipSelect, int clockFrequency, SpiMode mode) => new NullSpiDevice();

    /// <inheritdoc/>
    public void Dispose()
    {
        // No-op
    }
}

/// <summary>
/// Null SPI device that provides graceful fallback.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 36: Null SPI device (EDGE-01)")]
public sealed class NullSpiDevice : ISpiDevice
{
    /// <inheritdoc/>
    public void Transfer(ReadOnlySpan<byte> writeBuffer, Span<byte> readBuffer) => readBuffer.Clear();

    /// <inheritdoc/>
    public void Write(ReadOnlySpan<byte> data)
    {
        // No-op
    }

    /// <inheritdoc/>
    public void Read(Span<byte> buffer) => buffer.Clear();

    /// <inheritdoc/>
    public void Dispose()
    {
        // No-op
    }
}
