# Phase 36-01 Summary: GPIO/I2C/SPI Bus Abstractions

**Status**: Complete
**Phase**: 36 (Edge/IoT Hardware)
**Plan**: 01 (Bus controller abstractions)
**Wave**: 1

## Objective
Implement GPIO/I2C/SPI bus abstractions (EDGE-01) wrapping System.Device.Gpio with DataWarehouse plugin semantics and graceful fallback for non-IoT platforms.

## Files Created

### 1. DataWarehouse.SDK/Edge/EdgeConstants.cs (73 lines)
Constants for edge/IoT hardware bus operations.
- Default bus IDs: I2C bus 1, SPI bus 0, GPIO chip 0
- Hardware limits: 256 GPIO pins, 127 I2C addresses, 15 SPI chip selects

### 2. DataWarehouse.SDK/Edge/Bus/IGpioBusController.cs (215 lines)
GPIO bus controller interface with pin operations.
- `OpenPin()`, `ClosePin()`: Pin lifecycle management
- `Read()`, `Write()`: Digital I/O operations
- `RegisterCallback()`: Interrupt-driven edge detection (rising/falling/both)
- Supporting types: PinMode, PinValue, PinEventEdge, PinValueChangedEventArgs

### 3. DataWarehouse.SDK/Edge/Bus/II2cBusController.cs (99 lines)
I2C bus controller interface for peripheral communication.
- `OpenDevice()`: Creates II2cDevice connection (bus ID + 7-bit address)
- `II2cDevice`: Read, Write, WriteRead operations
- Atomic write-read for register-based devices

### 4. DataWarehouse.SDK/Edge/Bus/ISpiBusController.cs (169 lines)
SPI bus controller interface for full-duplex peripherals.
- `OpenDevice()`: Creates ISpiDevice connection (bus, chip select, clock, mode)
- `ISpiDevice`: Transfer (full-duplex), Write, Read operations
- SpiMode enum (0-3): CPOL/CPHA clock configuration

### 5. DataWarehouse.SDK/Edge/Bus/PinMapping.cs (142 lines)
Board-specific GPIO pin number mappings.
- `RaspberryPi4`: Complete 40-pin header mapping (physical → BCM GPIO)
- `RaspberryPi5`: Alias to RaspberryPi4 (same layout)
- `BeagleBoneBlack`, `JetsonNano`: Partial mappings (placeholders for future expansion)

### 6. DataWarehouse.SDK/Edge/Bus/GpioBusController.cs (295 lines)
Real GPIO controller using System.Device.Gpio.GpioController.
- Wraps GpioController with pin mapping translation (physical → logical)
- Stores System.Device.Gpio callback handlers for proper unregistration
- Thread-safe with internal locking
- Error handling: wraps platform exceptions in InvalidOperationException

### 7. DataWarehouse.SDK/Edge/Bus/I2cBusController.cs (125 lines)
Real I2C controller using System.Device.I2c.I2cDevice.
- Wraps I2cDevice with DataWarehouse error conventions
- Inner class I2cDeviceWrapper implements II2cDevice
- Validates bus ID and device address ranges

### 8. DataWarehouse.SDK/Edge/Bus/SpiBusController.cs (153 lines)
Real SPI controller using System.Device.Spi.SpiDevice.
- Wraps SpiDevice with DataWarehouse error conventions
- Inner class SpiDeviceWrapper implements ISpiDevice
- Validates buffer lengths in Transfer (must be equal)

### 9. DataWarehouse.SDK/Edge/Bus/NullBusController.cs (145 lines)
Graceful no-op fallback for all three bus types.
- `NullGpioBusController`: Returns PinValue.Low, IsPinOpen = false
- `NullI2cBusController`, `NullSpiBusController`: Return null devices
- `NullI2cDevice`, `NullSpiDevice`: Clear read buffers, ignore writes
- Never throw exceptions (enables code to run on non-IoT platforms)

### 10. DataWarehouse.SDK/Edge/Bus/BusControllerFactory.cs (131 lines)
Factory with automatic platform capability detection.
- `CreateGpioController()`: Checks "gpio" capability or /sys/class/gpio
- `CreateI2cController()`: Checks "i2c" capability or /sys/class/i2c-adapter
- `CreateSpiController()`: Checks "spi" capability or /sys/class/spi_master
- Returns real controllers when hardware present, null controllers otherwise

## NuGet Dependencies Added

- **System.Device.Gpio v3.2.0**: GPIO/I2C/SPI hardware access library
  - Added to DataWarehouse.SDK.csproj ItemGroup

## Key Design Decisions

1. **Physical Pin Numbering**: GPIO uses physical header pin numbers (1-40)
   - PinMapping translates physical → logical (BCM) GPIO numbers
   - Makes code more portable across boards with compatible layouts

2. **Graceful Fallback**: Null controllers enable code to run on any platform
   - No exceptions thrown (returns defaults: Low, false, zero bytes)
   - Factory auto-detects hardware via IPlatformCapabilityRegistry or filesystem

3. **System.Device.Gpio Integration**: Wraps existing .NET IoT library
   - GpioController uses logical (BCM) numbering internally
   - I2cDevice, SpiDevice wrapped with DataWarehouse error conventions

4. **Thread Safety**:
   - GpioBusController: Thread-safe with internal locking
   - I2cBusController, SpiBusController: Thread-safe factory, but devices are NOT thread-safe
   - Documented in XML comments for clarity

5. **Callback Handling**: GpioBusController stores System.Device.Gpio handlers
   - Required for proper UnregisterCallbackForPinValueChangedEvent(pin, handler)
   - Translates PinEventTypes → PinEventEdge for DataWarehouse semantics

## Verification

- SDK build: 0 errors, 0 warnings
- Full solution build: 0 errors, 0 warnings
- All 10 files created with comprehensive XML documentation
- SdkCompatibility attributes applied to all public types
- System.Device.Gpio package integrated successfully

## Platform Support

**Real Controllers (Linux with hardware)**:
- Raspberry Pi 4/5: Full GPIO/I2C/SPI support via System.Device.Gpio
- BeagleBone Black: Partial pin mapping (expand as needed)
- Jetson Nano: Partial pin mapping (expand as needed)

**Null Controllers (non-IoT platforms)**:
- Windows (desktop/server): No-op fallback
- Linux (servers, VMs without GPIO): No-op fallback
- Any platform without hardware: No-op fallback

## Integration Points

- **Phase 32 (Hardware Discovery)**: Factory uses IPlatformCapabilityRegistry for capability detection
- **Future Edge Phases**: All higher-level edge features (camera, sensors, mesh networks) will use these bus controllers

## Supported Operations

**GPIO**:
- Digital input/output (High/Low)
- Pull-up/pull-down resistors
- Interrupt-driven edge detection (rising/falling/both)

**I2C**:
- Read/Write transactions
- Atomic write-read for register-based devices
- 7-bit addressing (0-127)

**SPI**:
- Full-duplex transfers (simultaneous read/write)
- Modes 0-3 (CPOL/CPHA configuration)
- Variable clock frequency

## Next Steps

Phase 36-02 will implement camera abstractions (V4L2 on Linux, Media Foundation on Windows) for video capture on edge devices.
