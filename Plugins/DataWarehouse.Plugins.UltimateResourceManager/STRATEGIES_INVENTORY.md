# UltimateResourceManager Strategy Inventory

## Total: 37 Strategies across 7 files

### CPU Strategies (5) - CpuStrategies.cs
1. `FairShareCpuStrategy` (cpu-fair-share) - Fair-share CPU scheduler with weight-based distribution
2. `PriorityCpuStrategy` (cpu-priority) - Priority-based CPU scheduler with preemption
3. `AffinityCpuStrategy` (cpu-affinity) - CPU affinity manager for cache locality
4. `RealTimeCpuStrategy` (cpu-realtime) - Real-time CPU scheduler with guaranteed time
5. `NumaCpuStrategy` (cpu-numa) - NUMA-aware scheduler co-locating CPU and memory

### Memory Strategies (4) - MemoryStrategies.cs
6. `CgroupMemoryStrategy` (memory-cgroup) - Cgroup-based memory manager with hierarchical limits
7. `BalloonMemoryStrategy` (memory-balloon) - Memory balloon manager for dynamic adjustment
8. `PressureAwareMemoryStrategy` (memory-pressure-aware) - Pressure-aware memory manager (PSI)
9. `HugePagesMemoryStrategy` (memory-hugepages) - Huge pages manager for large allocations

### I/O Strategies (4) - IoStrategies.cs
10. `DeadlineIoStrategy` (io-deadline) - Deadline-based I/O scheduler
11. `TokenBucketIoStrategy` (io-token-bucket) - Token bucket I/O throttling
12. `BandwidthLimitIoStrategy` (io-bandwidth-limit) - I/O bandwidth limiter
13. `PriorityIoStrategy` (io-priority) - Priority-based I/O scheduler

### GPU Strategies (4) - GpuStrategies.cs
14. `TimeSlicingGpuStrategy` (gpu-time-slicing) - GPU time-slicing for sharing
15. `MigGpuStrategy` (gpu-mig) - Multi-Instance GPU partitioning (NVIDIA)
16. `MpsGpuStrategy` (gpu-mps) - Multi-Process Service for concurrent kernels (NVIDIA)
17. `VgpuStrategy` (gpu-vgpu) - Virtual GPU for VMs

### Network Strategies (3) - NetworkStrategies.cs
18. `TokenBucketNetworkStrategy` (network-token-bucket) - Token bucket network throttling
19. `QosNetworkStrategy` (network-qos) - QoS-based network bandwidth management
20. `HierarchicalQuotaStrategy` (quota-hierarchical) - Hierarchical quota enforcement

### Composite Strategy (1) - NetworkStrategies.cs
21. `CompositeResourceStrategy` (composite-all) - Multi-resource composite strategy

### Power Strategies (8) - PowerStrategies.cs ⭐ NEW
22. `DvfsCpuStrategy` (power-dvfs) - Dynamic Voltage/Frequency Scaling, P-states
23. `CStateStrategy` (power-cstate) - CPU C-state management, wake latency budgets
24. `IntelRaplStrategy` (power-intel-rapl) - Intel RAPL power capping (package/core/uncore/DRAM)
25. `AmdRaplStrategy` (power-amd-rapl) - AMD RAPL power measurement for Zen
26. `CarbonAwareStrategy` (power-carbon-aware) - Carbon-aware scheduling, grid intensity tracking
27. `BatteryAwareStrategy` (power-battery-aware) - Battery level monitoring, AC/battery switching
28. `ThermalThrottleStrategy` (power-thermal) - Temperature monitoring, thermal throttling
29. `SuspendResumeStrategy` (power-suspend) - System suspend/resume, wake-on-LAN

### Container Strategies (8) - ContainerStrategies.cs ⭐ NEW
30. `CgroupV2Strategy` (container-cgroupv2) - Linux cgroup v2 unified hierarchy (cpu.max, memory.max, io.max)
31. `DockerResourceStrategy` (container-docker) - Docker resource limits, OOM handling
32. `KubernetesResourceStrategy` (container-k8s) - K8s requests/limits, QoS classes
33. `PodmanResourceStrategy` (container-podman) - Podman rootless containers, cgroup delegation
34. `WindowsJobObjectStrategy` (container-job-object) - Windows Job Object API for process groups
35. `WindowsContainerStrategy` (container-windows) - Windows containers (process/Hyper-V isolation)
36. `ProcessGroupStrategy` (container-pgroup) - Unix process groups (nice, ionice, cpulimit)
37. `NamespaceIsolationStrategy` (container-namespace) - Linux namespaces (pid, net, mount, user)

## Auto-Discovery
All strategies are automatically discovered via reflection (parameterless constructor pattern).
No manual registration required - just add the class and it's registered at runtime.

## Architecture
- All strategies extend `ResourceStrategyBase`
- Implement: `StrategyId`, `DisplayName`, `Category`, `Capabilities`, `SemanticDescription`, `Tags`
- Track allocations internally for proper release
- Real production logic (no mocks/stubs) per Rule 13
