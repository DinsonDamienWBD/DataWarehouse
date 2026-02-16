# Phase 36: Edge/IoT Hardware Integration - Research

## Overview

Phase 36 brings DataWarehouse to edge/IoT devices with direct hardware access. This phase builds on Phase 32's hardware probe infrastructure to enable GPIO/I2C/SPI bus control, real protocol clients (MQTT, CoAP), on-device ML inference via WASI-NN, flash memory management, memory-constrained operation, camera integration, and sensor network mesh support.

## Requirements Coverage

| Requirement | Title | Summary |
|-------------|-------|---------|
| EDGE-01 | GPIO/I2C/SPI Bus Abstraction | System.Device.Gpio wrappers for hardware bus access |
| EDGE-02 | Real MQTT Client | Production MQTT client with QoS 0/1/2, TLS, auto-reconnect |
| EDGE-03 | Real CoAP Client | CoAP client with resource discovery, observe, DTLS |
| EDGE-04 | WASI-NN Host Implementation | ONNX Runtime binding for on-device inference |
| EDGE-05 | Flash Translation Layer | Wear-leveling, bad-block management for raw NAND/NOR |
| EDGE-06 | Memory-Constrained Runtime | Bounded memory mode with ArrayPool, zero-alloc hot paths |
| EDGE-07 | Camera/Media Frame Grabber | USB/CSI/IP camera abstraction with zero-copy frames |
| EDGE-08 | Sensor Network Mesh | Zigbee, LoRA, BLE mesh with multi-hop routing |

## Key Technologies

### Hardware Bus Access (EDGE-01)

**System.Device.Gpio** (NuGet: System.Device.Gpio)
- Cross-platform GPIO/I2C/SPI abstraction
- Supports Raspberry Pi, BeagleBone, Jetson, Orange Pi
- Pin mapping via board-specific drivers
- Event-based GPIO interrupts

**Platform Support:**
- Linux: sysfs GPIO (/sys/class/gpio), I2C (/dev/i2c-*), SPI (/dev/spidev*)
- Windows 10 IoT Core: Lightning providers
- Fallback: NullGpioController for non-IoT platforms

### Protocol Clients (EDGE-02, EDGE-03)

**MQTTnet** (NuGet: MQTTnet)
- Full MQTT 3.1.1 and 5.0 support
- QoS 0/1/2 with in-flight message tracking
- TLS with client certificates
- WebSocket transport option
- Auto-reconnect with exponential backoff

**CoAP.NET** (NuGet: CoAP.NET or custom implementation)
- Confirmable (CON) and Non-confirmable (NON) messages
- Resource discovery via /.well-known/core
- Observe pattern for push notifications
- Block-wise transfer for large payloads
- DTLS 1.2 for security

### ML Inference (EDGE-04)

**ONNX Runtime** (NuGet: Microsoft.ML.OnnxRuntime)
- Cross-platform inference engine
- CPU, CUDA, DirectML execution providers
- Model format: ONNX (.onnx files)
- Batch inference support
- Model caching to avoid reload overhead

**WASI-NN Interface:**
- Standard WebAssembly System Interface for Neural Networks
- Provides load/init/compute operations
- DataWarehouse will host ONNX Runtime as WASI-NN backend

### Flash Translation Layer (EDGE-05)

**Flash Memory Management:**
- Wear-leveling: distribute writes evenly across blocks
- Bad-block management: mark and skip defective blocks
- Garbage collection: reclaim invalidated pages
- Write amplification factor (WAF) target: <2.0
- Extends Phase 33 VDE with flash-specific optimizations

**Raw Flash Access:**
- Linux: /dev/mtd* for raw NAND/NOR access
- Requires elevated privileges (CAP_SYS_RAWIO)
- Block erasure before write (NAND characteristic)
- Out-of-band (OOB) area for metadata (ECC, wear count)

### Memory-Constrained Runtime (EDGE-06)

**Bounded Memory Mode:**
- Configurable memory ceiling (e.g., 64MB, 128MB, 256MB)
- ArrayPool<byte> for all allocations >1KB
- Zero-allocation hot paths for read/write
- GC pressure monitoring via GC.GetTotalMemory and GC.GetGCMemoryInfo
- Proactive cleanup when approaching ceiling

**Plugin Memory Budget:**
- Plugin loading checks available memory
- Reject plugin load if budget insufficient
- Track per-plugin memory usage via weak references

### Camera Integration (EDGE-07)

**DirectShow / V4L2:**
- Windows: DirectShow API for USB cameras
- Linux: Video4Linux2 (V4L2) for USB/CSI cameras
- IP cameras: RTSP/HTTP streaming

**Frame Buffer:**
- Zero-copy access to camera frame data
- Configurable resolution, pixel format (RGB24, YUV, etc.), frame rate
- Integrates with UltimateMedia plugin for processing

**NuGet Options:**
- OpenCvSharp4 (OpenCV wrapper) for camera access
- Custom V4L2 P/Invoke for Linux
- DirectShow interop for Windows

### Sensor Network Mesh (EDGE-08)

**Zigbee:**
- IEEE 802.15.4 low-power mesh
- Coordinator/Router/End-Device roles
- Multi-hop routing with AODV
- NuGet: Custom implementation or hardware-specific SDK

**LoRa/LoRaWAN:**
- Long-range, low-power wireless
- Star topology (LoRaWAN) or mesh (custom)
- Gateway aggregates sensor data
- NuGet: LoRaWAN.NetCore or hardware SDK

**BLE Mesh:**
- Bluetooth Low Energy mesh networking
- Flooding-based message relay
- Provisioning and configuration
- NuGet: InTheHand.BluetoothLE or platform Bluetooth APIs

**Integration with AEDS:**
- Mesh nodes as AEDS edge nodes
- Gateway as AEDS aggregation point
- Data routing to central DataWarehouse cluster

## Dependencies

### NuGet Packages (New)

| Package | Version | Purpose | License |
|---------|---------|---------|---------|
| System.Device.Gpio | 3.2.0+ | GPIO/I2C/SPI bus access | MIT |
| MQTTnet | 4.3.7+ | MQTT client | MIT |
| Microsoft.ML.OnnxRuntime | 1.20.0+ | ONNX inference | MIT |
| OpenCvSharp4 | 4.10.0+ | Camera capture (optional) | Apache 2.0 |

**CoAP:** Evaluate CoAP.NET or implement minimal CoAP client (RFC 7252).

**Mesh Protocols:** Evaluate hardware-specific SDKs or implement custom mesh logic.

### Phase Dependencies

- **Phase 32 (Complete):** Hardware probe infrastructure, IPlatformCapabilityRegistry
- **Phase 33 (Planned):** Virtual Disk Engine for FTL storage backend
- **UltimateStreaming Plugin:** MQTT/CoAP will integrate as streaming strategies
- **UltimateInterface Plugin:** CoAP resource endpoints
- **UltimateMedia Plugin:** Camera frame processing
- **AEDS Plugin:** Mesh network data distribution

## Architecture

### DataWarehouse.SDK/Edge Namespace

All new edge/IoT types go in `DataWarehouse.SDK/Edge/`:

```
DataWarehouse.SDK/Edge/
├── Bus/
│   ├── IGpioBusController.cs
│   ├── II2cBusController.cs
│   ├── ISpiiBusController.cs
│   ├── GpioBusController.cs (System.Device.Gpio wrapper)
│   └── NullBusController.cs (graceful fallback)
├── Protocols/
│   ├── IMqttClient.cs
│   ├── MqttClient.cs (MQTTnet wrapper)
│   ├── ICoApClient.cs
│   └── CoApClient.cs
├── Inference/
│   ├── IWasiNnHost.cs
│   ├── OnnxWasiNnHost.cs
│   └── InferenceSession.cs
├── Flash/
│   ├── IFlashTranslationLayer.cs
│   ├── FlashTranslationLayer.cs
│   ├── WearLevelingStrategy.cs
│   └── BadBlockManager.cs
├── Memory/
│   ├── BoundedMemoryRuntime.cs
│   └── MemoryBudgetTracker.cs
├── Camera/
│   ├── ICameraDevice.cs
│   ├── CameraFrameGrabber.cs
│   └── FrameBuffer.cs
└── Mesh/
    ├── IMeshNetwork.cs
    ├── ZigbeeMesh.cs
    ├── LoRaMesh.cs
    └── BleMesh.cs
```

### Integration with Existing Plugins

| Plugin | Integration Point | How |
|--------|-------------------|-----|
| UltimateStreaming | MQTT/CoAP clients | Expose as `MqttStreamingStrategy`, `CoApStreamingStrategy` |
| UltimateInterface | CoAP resources | Expose as `CoApResourceStrategy` |
| UltimateMedia | Camera frames | Expose as `CameraFrameSource` |
| AEDS | Sensor mesh | Expose as `MeshNetworkAdapter` |
| Plugins (all) | Memory budget | All plugins respect BoundedMemoryRuntime |

### Platform Detection

Use Phase 32's `IPlatformCapabilityRegistry` to detect:

- `gpio` capability → enable GPIO controllers
- `i2c` capability → enable I2C controllers
- `spi` capability → enable SPI controllers
- `camera` capability → enable frame grabbers

Fallback to Null* implementations when capability absent (no throws, graceful degradation).

## Implementation Waves

### Wave 1: Foundation (Plans 01, 02)
- GPIO/I2C/SPI bus abstraction (EDGE-01)
- MQTT client integration (EDGE-02)

### Wave 2: Protocol & Inference (Plans 03, 04)
- CoAP client integration (EDGE-03)
- WASI-NN + ONNX Runtime host (EDGE-04)

### Wave 3: Storage & Resource Management (Plans 05, 06)
- Flash Translation Layer (EDGE-05)
- Memory-constrained runtime (EDGE-06)

### Wave 4: Capture & Mesh (Plans 07, 08)
- Camera/media frame grabber (EDGE-07)
- Sensor network mesh (EDGE-08)

## Success Criteria

1. **Hardware Bus Access:** Read I2C sensor, toggle GPIO LED, communicate with SPI device on Raspberry Pi
2. **MQTT:** Publish 10K messages/sec at QoS 1, auto-reconnect within 5 seconds
3. **CoAP:** Discover resources, GET/PUT/POST/DELETE, observe resource changes
4. **WASI-NN:** Load ONNX model, run inference with >95% accuracy match
5. **FTL:** Write 10x device capacity with WAF <2.0, survive 5% bad blocks
6. **Memory Budget:** Run with 64MB ceiling, zero OOM, zero Gen2 GC
7. **Camera:** Capture 30fps 1080p, zero-copy frame access
8. **Mesh:** 10-node LoRA mesh with multi-hop routing, battery node sleep 90%

## Notes

- All System.Device.Gpio code must be guarded by platform checks (OperatingSystem.IsLinux/IsWindows)
- MQTT/CoAP clients provide graceful fallback when network unavailable
- WASI-NN host provides CPU-only fallback when GPU unavailable
- FTL requires root/admin for raw flash access — provide warning if permission denied
- Memory budget enforcement is opt-in via configuration (BoundedMemoryMode=true)
- Camera support is best-effort (DirectShow on Windows, V4L2 on Linux, graceful fallback on unsupported platforms)
- Mesh protocols are platform-specific (require hardware dongles or onboard radios)

## References

- [System.Device.Gpio Documentation](https://learn.microsoft.com/en-us/dotnet/iot/)
- [MQTTnet GitHub](https://github.com/dotnet/MQTTnet)
- [RFC 7252: CoAP](https://datatracker.ietf.org/doc/html/rfc7252)
- [ONNX Runtime](https://onnxruntime.ai/)
- [WASI-NN Proposal](https://github.com/WebAssembly/wasi-nn)
- [Flash Translation Layer Concepts](https://en.wikipedia.org/wiki/Flash_translation_layer)
