# DataWarehouse.Plugins.LowLatency

Ultra-low latency storage and I/O plugins for sub-millisecond performance requirements.

## Plugins Included

### 1. OptaneLowLatencyStoragePlugin

**ID:** `com.datawarehouse.performance.optane`
**Category:** Storage Provider
**Latency Tier:** Ultra-Low (<100μs read, <200μs write)

Intel Optane/Persistent Memory optimized storage plugin providing:
- Direct memory access bypassing OS page cache
- Sub-100μs read latency
- Sub-200μs write latency
- Optional durability guarantees with persistent memory flush
- Pre-warming capabilities for predictable latency
- Comprehensive latency statistics (P50, P99)

**Configuration:**
- `PmemDevicePath`: Path to persistent memory device (e.g., `/dev/dax0.0`)
- `UseDirectIo`: Enable direct I/O to bypass page cache (default: true)

**Use Cases:**
- Ultra-low latency data caching
- Financial trading systems
- Real-time analytics
- High-frequency data capture

### 2. LinuxIoUringPlugin

**ID:** `com.datawarehouse.performance.iouring`
**Category:** Infrastructure Provider
**Platform:** Linux 5.1+

Linux io_uring based async I/O provider for ultra-low overhead operations:
- Kernel-bypass I/O with batched submission and completion
- Zero-copy data transfers
- Efficient polling-based completion model
- Reduced syscall overhead

**Requirements:**
- Linux kernel 5.1 or higher
- io_uring support enabled in kernel

**Note:** Current implementation is a placeholder. Production deployment requires:
- liburing integration or direct syscall implementation
- io_uring ring buffer management
- Completion queue polling logic

### 3. WindowsNumaAllocatorPlugin

**ID:** `com.datawarehouse.performance.numa.windows`
**Category:** Infrastructure Provider
**Platform:** Windows

NUMA-aware memory allocator for Windows systems:
- Explicit memory placement across NUMA nodes
- Thread affinity management per NUMA node
- Minimizes cross-node memory access latency
- Automatic NUMA topology detection

**Features:**
- `AllocateOnNode(size, numaNode)`: Allocate memory on specific NUMA node
- `SetThreadAffinityAsync(numaNode)`: Pin thread to NUMA node
- `NodeCount`: Total NUMA nodes in system
- `CurrentNode`: Current thread's NUMA node

**Use Cases:**
- Multi-socket server optimization
- Reducing memory access latency
- High-performance computing workloads
- Database engine memory management

## Architecture

All plugins extend from SDK base classes:
- `LowLatencyStoragePluginBase` - Base for ultra-low latency storage
- `IoUringProviderPluginBase` - Base for Linux io_uring providers
- `NumaAllocatorPluginBase` - Base for NUMA-aware allocators

## Performance Targets

| Plugin | Read Latency | Write Latency | Platform |
|--------|-------------|---------------|----------|
| OptaneLowLatencyStorage | <100μs | <200μs | Any (Optane PMEM) |
| LinuxIoUring | <50μs | <100μs | Linux 5.1+ |
| WindowsNumaAllocator | N/A (allocator) | N/A (allocator) | Windows |

## Integration

Plugins are automatically discovered by the DataWarehouse kernel through the plugin system. To enable:

1. Ensure plugin assembly is in the plugins directory
2. Configure via kernel configuration file
3. Plugins will be loaded based on platform availability

## Hardware Requirements

### OptaneLowLatencyStorage
- Intel Optane Persistent Memory DIMMs (recommended)
- Or any persistent memory device with DAX support
- Linux: `/dev/dax` device
- Windows: Persistent memory namespace

### LinuxIoUring
- Linux kernel 5.1 or higher
- io_uring support enabled

### WindowsNumaAllocator
- Multi-socket Windows system (optimal)
- Works on single-socket systems (degraded to standard allocation)

## Future Enhancements

1. **OptaneLowLatencyStorage**
   - Add RDMA support for remote persistent memory access
   - Implement tiered caching with DRAM + Optane
   - Add compression for Optane-backed storage

2. **LinuxIoUring**
   - Complete liburing integration
   - Add fixed buffer registration
   - Implement polling mode for lowest latency

3. **WindowsNumaAllocator**
   - Add huge page support
   - Implement memory pool management
   - Add NUMA distance-aware allocation

## Documentation

See SDK documentation for:
- `ILowLatencyStorage` interface
- `IIoUringProvider` interface
- `INumaAllocator` interface
- Performance tuning guide
- Hardware compatibility matrix
