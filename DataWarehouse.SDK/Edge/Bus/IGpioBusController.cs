using DataWarehouse.SDK.Contracts;
using System;

namespace DataWarehouse.SDK.Edge.Bus;

/// <summary>
/// Controller for GPIO (General Purpose Input/Output) pin operations.
/// </summary>
/// <remarks>
/// <para>
/// GPIO controllers provide low-level access to hardware pins for reading digital inputs
/// (sensors, buttons) and writing digital outputs (LEDs, relays). This interface abstracts
/// platform-specific GPIO access (Linux sysfs, libgpiod, Windows IoT, etc).
/// </para>
/// <para>
/// <strong>Pin Numbering:</strong> Pin numbers are board-specific physical pin numbers
/// (e.g., physical pin 11 on Raspberry Pi header). The controller uses <see cref="PinMapping"/>
/// to translate physical pins to logical (BCM/GPIO) pin numbers.
/// </para>
/// <para>
/// <strong>Pin Modes:</strong> Pins must be opened before use. Mode determines behavior:
/// <list type="bullet">
///   <item><description>Input: Read pin voltage state (High or Low)</description></item>
///   <item><description>Output: Write pin voltage state (High or Low)</description></item>
///   <item><description>InputPullUp: Input with internal pull-up resistor (default High)</description></item>
///   <item><description>InputPullDown: Input with internal pull-down resistor (default Low)</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Interrupts:</strong> Use <see cref="RegisterCallback"/> to receive notifications
/// when a pin's value changes (rising edge, falling edge, or both). Callbacks execute on
/// a background thread; use appropriate synchronization for shared state.
/// </para>
/// </remarks>
[SdkCompatibility("3.0.0", Notes = "Phase 36: GPIO bus controller interface (EDGE-01)")]
public interface IGpioBusController : IDisposable
{
    /// <summary>
    /// Opens a GPIO pin for read or write operations.
    /// </summary>
    /// <param name="pinNumber">Physical pin number (board-specific).</param>
    /// <param name="mode">Pin mode: Input, Output, InputPullUp, or InputPullDown.</param>
    /// <remarks>
    /// Pins must be opened before performing read/write operations. If the pin is already
    /// open, this method closes and reopens it with the new mode. All pins should be closed
    /// via <see cref="ClosePin"/> or Dispose when no longer needed.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if pinNumber is invalid for this board.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the pin cannot be opened (e.g., already in use by another process).</exception>
    void OpenPin(int pinNumber, PinMode mode);

    /// <summary>
    /// Closes a previously opened pin and releases associated resources.
    /// </summary>
    /// <param name="pinNumber">Physical pin number to close.</param>
    /// <remarks>
    /// Closing a pin that is not open is a no-op. Any registered callbacks for this pin
    /// are automatically unregistered.
    /// </remarks>
    void ClosePin(int pinNumber);

    /// <summary>
    /// Reads the current value of an input pin.
    /// </summary>
    /// <param name="pinNumber">Physical pin number to read.</param>
    /// <returns><see cref="PinValue.High"/> if the pin voltage is high (logic 1); otherwise <see cref="PinValue.Low"/>.</returns>
    /// <remarks>
    /// The pin must be opened in Input, InputPullUp, or InputPullDown mode before reading.
    /// Reading from an output pin may return the last written value (implementation-dependent).
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown if the pin is not open.</exception>
    PinValue Read(int pinNumber);

    /// <summary>
    /// Writes a value to an output pin.
    /// </summary>
    /// <param name="pinNumber">Physical pin number to write.</param>
    /// <param name="value"><see cref="PinValue.High"/> to set the pin voltage high (logic 1); <see cref="PinValue.Low"/> to set it low (logic 0).</param>
    /// <remarks>
    /// The pin must be opened in Output mode before writing. Writing to an input pin is a no-op.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown if the pin is not open in Output mode.</exception>
    void Write(int pinNumber, PinValue value);

    /// <summary>
    /// Registers a callback for pin value change events (interrupts).
    /// </summary>
    /// <param name="pinNumber">Physical pin number to monitor.</param>
    /// <param name="edge">Edge detection mode: Rising, Falling, or Both.</param>
    /// <param name="callback">Action to invoke when the pin value changes.</param>
    /// <remarks>
    /// <para>
    /// Callbacks execute on a background thread managed by the GPIO controller. Use appropriate
    /// synchronization (locks, concurrent collections) when accessing shared state from callbacks.
    /// </para>
    /// <para>
    /// Edge detection modes:
    /// <list type="bullet">
    ///   <item><description>Rising: Callback invoked when pin transitions from Low to High</description></item>
    ///   <item><description>Falling: Callback invoked when pin transitions from High to Low</description></item>
    ///   <item><description>Both: Callback invoked on any transition</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Only one callback can be registered per pin. Calling this method again for the same pin
    /// replaces the previous callback.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown if the pin is not open in an input mode.</exception>
    void RegisterCallback(int pinNumber, PinEventEdge edge, Action<PinValueChangedEventArgs> callback);

    /// <summary>
    /// Unregisters all callbacks for the specified pin.
    /// </summary>
    /// <param name="pinNumber">Physical pin number to stop monitoring.</param>
    /// <remarks>
    /// After unregistering, no further callbacks will be invoked for this pin until
    /// <see cref="RegisterCallback"/> is called again.
    /// </remarks>
    void UnregisterCallback(int pinNumber);

    /// <summary>
    /// Checks if a pin is currently open.
    /// </summary>
    /// <param name="pinNumber">Physical pin number to check.</param>
    /// <returns><c>true</c> if the pin is open; otherwise <c>false</c>.</returns>
    bool IsPinOpen(int pinNumber);
}

/// <summary>
/// GPIO pin mode configuration.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 36: GPIO pin mode (EDGE-01)")]
public enum PinMode
{
    /// <summary>
    /// Pin is configured as input (read-only, high-impedance).
    /// </summary>
    Input,

    /// <summary>
    /// Pin is configured as output (write-only, drive high or low).
    /// </summary>
    Output,

    /// <summary>
    /// Pin is configured as input with internal pull-up resistor (defaults to High).
    /// </summary>
    InputPullUp,

    /// <summary>
    /// Pin is configured as input with internal pull-down resistor (defaults to Low).
    /// </summary>
    InputPullDown
}

/// <summary>
/// GPIO pin value (digital logic level).
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 36: GPIO pin value (EDGE-01)")]
public enum PinValue
{
    /// <summary>
    /// Pin voltage is low (logic 0, typically 0V).
    /// </summary>
    Low = 0,

    /// <summary>
    /// Pin voltage is high (logic 1, typically 3.3V or 5V).
    /// </summary>
    High = 1
}

/// <summary>
/// GPIO pin event edge detection mode.
/// </summary>
[SdkCompatibility("3.0.0", Notes = "Phase 36: GPIO edge detection (EDGE-01)")]
public enum PinEventEdge
{
    /// <summary>
    /// Detect transitions from Low to High.
    /// </summary>
    Rising,

    /// <summary>
    /// Detect transitions from High to Low.
    /// </summary>
    Falling,

    /// <summary>
    /// Detect transitions in either direction (Low to High or High to Low).
    /// </summary>
    Both
}

/// <summary>
/// Event arguments for GPIO pin value change events.
/// </summary>
/// <param name="PinNumber">Physical pin number that changed.</param>
/// <param name="Edge">Edge type that triggered the event (Rising, Falling, or Both).</param>
[SdkCompatibility("3.0.0", Notes = "Phase 36: GPIO event args (EDGE-01)")]
public sealed record PinValueChangedEventArgs(int PinNumber, PinEventEdge Edge);
