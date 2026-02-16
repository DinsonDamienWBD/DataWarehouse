using DataWarehouse.SDK.Contracts;
using System;
using System.Collections.Generic;
using System.Device.Gpio;

namespace DataWarehouse.SDK.Edge.Bus;

/// <summary>
/// GPIO bus controller implementation using System.Device.Gpio.
/// </summary>
/// <remarks>
/// <para>
/// This implementation wraps <see cref="GpioController"/> from System.Device.Gpio with DataWarehouse
/// semantics and board-specific pin mapping. Physical pin numbers are translated to logical (BCM)
/// GPIO numbers via <see cref="PinMapping"/>.
/// </para>
/// <para>
/// <strong>Platform Support:</strong>
/// <list type="bullet">
///   <item><description>Linux: Uses libgpiod (/dev/gpiochip0) for GPIO access</description></item>
///   <item><description>Windows: Uses Windows.Devices.Gpio API (Windows IoT only)</description></item>
///   <item><description>Other platforms: May use sysfs (/sys/class/gpio) as fallback</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Thread Safety:</strong> This class is thread-safe. Multiple threads can call GPIO
/// operations concurrently with internal locking.
/// </para>
/// <para>
/// <strong>Error Handling:</strong> GPIO operations may fail due to hardware issues, permission
/// problems, or pins already in use. Failures are reported via exceptions (not silent failures).
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: GPIO wrapper using System.Device.Gpio (EDGE-01)")]
public sealed class GpioBusController : IGpioBusController
{
    private readonly GpioController _controller;
    private readonly PinMapping _pinMapping;
    private readonly Dictionary<int, PinChangeEventHandler> _sysCallbacks;
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="GpioBusController"/> class.
    /// </summary>
    /// <param name="pinMapping">
    /// Board-specific pin mapping. If null, defaults to <see cref="PinMapping.RaspberryPi4"/>.
    /// </param>
    /// <remarks>
    /// The controller uses logical (BCM) numbering scheme internally. Physical pin numbers
    /// are translated via the provided pin mapping.
    /// </remarks>
    public GpioBusController(PinMapping? pinMapping = null)
    {
        _pinMapping = pinMapping ?? PinMapping.RaspberryPi4;
        _controller = new GpioController(PinNumberingScheme.Logical); // Use BCM numbering
        _sysCallbacks = new Dictionary<int, PinChangeEventHandler>();
    }

    /// <inheritdoc/>
    public void OpenPin(int pinNumber, PinMode mode)
    {
        ThrowIfDisposed();

        lock (_lock)
        {
            int logicalPin = _pinMapping.GetLogicalPin(pinNumber);

            try
            {
                // Close pin if already open
                if (_controller.IsPinOpen(logicalPin))
                {
                    _controller.ClosePin(logicalPin);
                }

                // Map PinMode to System.Device.Gpio.PinMode
                var sysMode = mode switch
                {
                    PinMode.Input => System.Device.Gpio.PinMode.Input,
                    PinMode.Output => System.Device.Gpio.PinMode.Output,
                    PinMode.InputPullUp => System.Device.Gpio.PinMode.InputPullUp,
                    PinMode.InputPullDown => System.Device.Gpio.PinMode.InputPullDown,
                    _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Invalid pin mode")
                };

                _controller.OpenPin(logicalPin, sysMode);
            }
            catch (Exception ex) when (ex is not ArgumentException)
            {
                // Wrap platform-specific exceptions in InvalidOperationException
                throw new InvalidOperationException(
                    $"Failed to open GPIO pin {pinNumber} (logical {logicalPin}) in mode {mode}: {ex.Message}", ex);
            }
        }
    }

    /// <inheritdoc/>
    public void ClosePin(int pinNumber)
    {
        ThrowIfDisposed();

        lock (_lock)
        {
            int logicalPin = _pinMapping.GetLogicalPin(pinNumber);

            if (_controller.IsPinOpen(logicalPin))
            {
                // Unregister callbacks before closing
                if (_sysCallbacks.TryGetValue(logicalPin, out var handler))
                {
                    _controller.UnregisterCallbackForPinValueChangedEvent(logicalPin, handler);
                    _sysCallbacks.Remove(logicalPin);
                }

                _controller.ClosePin(logicalPin);
            }
        }
    }

    /// <inheritdoc/>
    public PinValue Read(int pinNumber)
    {
        ThrowIfDisposed();

        int logicalPin = _pinMapping.GetLogicalPin(pinNumber);

        if (!_controller.IsPinOpen(logicalPin))
        {
            throw new InvalidOperationException($"GPIO pin {pinNumber} (logical {logicalPin}) is not open");
        }

        try
        {
            var value = _controller.Read(logicalPin);
            return value == System.Device.Gpio.PinValue.High ? PinValue.High : PinValue.Low;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to read GPIO pin {pinNumber} (logical {logicalPin}): {ex.Message}", ex);
        }
    }

    /// <inheritdoc/>
    public void Write(int pinNumber, PinValue value)
    {
        ThrowIfDisposed();

        int logicalPin = _pinMapping.GetLogicalPin(pinNumber);

        if (!_controller.IsPinOpen(logicalPin))
        {
            throw new InvalidOperationException($"GPIO pin {pinNumber} (logical {logicalPin}) is not open");
        }

        try
        {
            var sysValue = value == PinValue.High
                ? System.Device.Gpio.PinValue.High
                : System.Device.Gpio.PinValue.Low;
            _controller.Write(logicalPin, sysValue);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to write GPIO pin {pinNumber} (logical {logicalPin}): {ex.Message}", ex);
        }
    }

    /// <inheritdoc/>
    public void RegisterCallback(int pinNumber, PinEventEdge edge, Action<PinValueChangedEventArgs> callback)
    {
        ThrowIfDisposed();

        lock (_lock)
        {
            int logicalPin = _pinMapping.GetLogicalPin(pinNumber);

            if (!_controller.IsPinOpen(logicalPin))
            {
                throw new InvalidOperationException(
                    $"GPIO pin {pinNumber} (logical {logicalPin}) is not open in an input mode");
            }

            try
            {
                // Map PinEventEdge to System.Device.Gpio.PinEventTypes
                var sysEdge = edge switch
                {
                    PinEventEdge.Rising => PinEventTypes.Rising,
                    PinEventEdge.Falling => PinEventTypes.Falling,
                    PinEventEdge.Both => PinEventTypes.Rising | PinEventTypes.Falling,
                    _ => throw new ArgumentOutOfRangeException(nameof(edge), edge, "Invalid edge type")
                };

                // Create wrapper callback and store for later unregistration
                PinChangeEventHandler sysCallback = (sender, args) =>
                {
                    // Translate System.Device.Gpio.PinEventTypes to PinEventEdge
                    var ourEdge = args.ChangeType switch
                    {
                        PinEventTypes.Rising => PinEventEdge.Rising,
                        PinEventTypes.Falling => PinEventEdge.Falling,
                        _ => PinEventEdge.Both
                    };

                    callback(new PinValueChangedEventArgs(pinNumber, ourEdge));
                };

                _sysCallbacks[logicalPin] = sysCallback;

                // Register callback with System.Device.Gpio
                _controller.RegisterCallbackForPinValueChangedEvent(logicalPin, sysEdge, sysCallback);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Failed to register callback for GPIO pin {pinNumber} (logical {logicalPin}): {ex.Message}", ex);
            }
        }
    }

    /// <inheritdoc/>
    public void UnregisterCallback(int pinNumber)
    {
        ThrowIfDisposed();

        lock (_lock)
        {
            int logicalPin = _pinMapping.GetLogicalPin(pinNumber);

            if (_sysCallbacks.TryGetValue(logicalPin, out var handler))
            {
                _controller.UnregisterCallbackForPinValueChangedEvent(logicalPin, handler);
                _sysCallbacks.Remove(logicalPin);
            }
        }
    }

    /// <inheritdoc/>
    public bool IsPinOpen(int pinNumber)
    {
        ThrowIfDisposed();

        int logicalPin = _pinMapping.GetLogicalPin(pinNumber);
        return _controller.IsPinOpen(logicalPin);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
            return;

        lock (_lock)
        {
            if (_disposed)
                return;

            // Unregister all callbacks
            foreach (var (pin, handler) in _sysCallbacks)
            {
                _controller.UnregisterCallbackForPinValueChangedEvent(pin, handler);
            }
            _sysCallbacks.Clear();

            _controller.Dispose();
            _disposed = true;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(GpioBusController));
        }
    }
}
