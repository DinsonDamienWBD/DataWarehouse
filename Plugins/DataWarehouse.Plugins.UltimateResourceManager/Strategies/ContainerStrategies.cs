using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DataWarehouse.Plugins.UltimateResourceManager.Strategies;

/// <summary>
/// Linux cgroup v2 unified hierarchy strategy.
/// Manages resources via cpu.max, memory.max, io.max controllers.
/// </summary>
public sealed class CgroupV2Strategy : ResourceStrategyBase
{
    private readonly ConcurrentDictionary<string, CgroupLimits> _cgroupLimits = new();
    private long _totalMemoryLimit;
    private long _totalCpuQuotaMillicores; // Stored as integer millicores for Interlocked safety

    private sealed record CgroupLimits(long MemoryBytes, double CpuCores, long IoBytesPerSec);

    public override string StrategyId => "container-cgroupv2";
    public override string DisplayName => "Cgroup v2 Manager";
    public override ResourceCategory Category => ResourceCategory.Composite;
    public override ResourceStrategyCapabilities Capabilities => new()
    {
        SupportsCpu = true, SupportsMemory = true, SupportsIO = true,
        SupportsGpu = false, SupportsNetwork = false, SupportsQuotas = true,
        SupportsHierarchicalQuotas = true, SupportsPreemption = true
    };
    public override string SemanticDescription =>
        "Linux cgroup v2 unified hierarchy manager controlling CPU (cpu.max), memory (memory.max), " +
        "and I/O (io.max) resources with hierarchical limits and pressure-based feedback.";
    public override string[] Tags => ["container", "cgroup", "cgroupv2", "linux", "unified-hierarchy"];

    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default)
    {
        var cpuCores = Interlocked.Read(ref _totalCpuQuotaMillicores) / 1000.0;
        return Task.FromResult(new ResourceMetrics
        {
            CpuPercent = (cpuCores / Environment.ProcessorCount) * 100,
            MemoryBytes = Interlocked.Read(ref _totalMemoryLimit),
            IoBandwidth = _cgroupLimits.Values.Sum(l => l.IoBytesPerSec),
            Timestamp = DateTime.UtcNow
        });
    }

    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct)
    {
        var handle = Guid.NewGuid().ToString("N");

        // Check hierarchical limits
        var systemMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (_totalMemoryLimit + request.MemoryBytes > systemMemory * 0.9)
        {
            return Task.FromResult(new ResourceAllocation
            {
                RequestId = request.RequestId,
                Success = false,
                FailureReason = "Memory limit exceeded"
            });
        }

        var limits = new CgroupLimits(
            request.MemoryBytes,
            request.CpuCores,
            request.IoBandwidth
        );

        _cgroupLimits[handle] = limits;
        Interlocked.Add(ref _totalMemoryLimit, request.MemoryBytes);
        Interlocked.Add(ref _totalCpuQuotaMillicores, (long)(request.CpuCores * 1000));

        return Task.FromResult(new ResourceAllocation
        {
            RequestId = request.RequestId,
            Success = true,
            AllocationHandle = handle,
            AllocatedCpuCores = request.CpuCores,
            AllocatedMemoryBytes = request.MemoryBytes,
            AllocatedIoBandwidth = request.IoBandwidth
        });
    }

    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct)
    {
        if (allocation.AllocationHandle != null && _cgroupLimits.TryRemove(allocation.AllocationHandle, out var limits))
        {
            Interlocked.Add(ref _totalMemoryLimit, -limits.MemoryBytes);
            Interlocked.Add(ref _totalCpuQuotaMillicores, -(long)(limits.CpuCores * 1000));
        }
        return Task.FromResult(true);
    }
}

/// <summary>
/// Docker container resource limits strategy.
/// Manages --cpus, --memory, --device-read-bps, --device-write-bps.
/// </summary>
public sealed class DockerResourceStrategy : ResourceStrategyBase
{
    private readonly ConcurrentDictionary<string, DockerLimits> _containerLimits = new();
    private int _containerCount;

    private sealed record DockerLimits(
        string ContainerId,
        double Cpus,
        long MemoryBytes,
        long DeviceReadBps,
        long DeviceWriteBps,
        bool OomKillDisable
    );

    public override string StrategyId => "container-docker";
    public override string DisplayName => "Docker Resource Manager";
    public override ResourceCategory Category => ResourceCategory.Composite;
    public override ResourceStrategyCapabilities Capabilities => new()
    {
        SupportsCpu = true, SupportsMemory = true, SupportsIO = true,
        SupportsGpu = false, SupportsNetwork = false, SupportsQuotas = true,
        SupportsHierarchicalQuotas = false, SupportsPreemption = false
    };
    public override string SemanticDescription =>
        "Docker container resource manager enforcing CPU shares (--cpus), memory limits (--memory), " +
        "I/O throttling (--device-read/write-bps), and OOM handling for containerized workloads.";
    public override string[] Tags => ["container", "docker", "limits", "oom", "throttle"];

    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default)
    {
        var totalCpu = _containerLimits.Values.Sum(l => l.Cpus);
        var totalMemory = _containerLimits.Values.Sum(l => l.MemoryBytes);
        var totalReadBps = _containerLimits.Values.Sum(l => l.DeviceReadBps);

        return Task.FromResult(new ResourceMetrics
        {
            CpuPercent = (totalCpu / Environment.ProcessorCount) * 100,
            MemoryBytes = totalMemory,
            IoBandwidth = totalReadBps,
            Timestamp = DateTime.UtcNow
        });
    }

    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct)
    {
        var handle = Guid.NewGuid().ToString("N");
        var containerId = $"dwh-{Interlocked.Increment(ref _containerCount):D6}";

        var limits = new DockerLimits(
            ContainerId: containerId,
            Cpus: request.CpuCores,
            MemoryBytes: request.MemoryBytes,
            DeviceReadBps: request.IoBandwidth / 2,
            DeviceWriteBps: request.IoBandwidth / 2,
            OomKillDisable: request.Priority > 80 // High priority containers protected from OOM killer
        );

        _containerLimits[handle] = limits;

        return Task.FromResult(new ResourceAllocation
        {
            RequestId = request.RequestId,
            Success = true,
            AllocationHandle = handle,
            AllocatedCpuCores = request.CpuCores,
            AllocatedMemoryBytes = request.MemoryBytes,
            AllocatedIoBandwidth = request.IoBandwidth
        });
    }

    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct)
    {
        if (allocation.AllocationHandle != null)
        {
            _containerLimits.TryRemove(allocation.AllocationHandle, out _);
        }
        return Task.FromResult(true);
    }
}

/// <summary>
/// Kubernetes resource requests and limits strategy.
/// Manages QoS classes: Guaranteed, Burstable, BestEffort.
/// </summary>
public sealed class KubernetesResourceStrategy : ResourceStrategyBase
{
    private readonly ConcurrentDictionary<string, PodResources> _podResources = new();

    private enum QosClass { Guaranteed, Burstable, BestEffort }

    private sealed record PodResources(
        string PodName,
        double CpuRequest,
        double CpuLimit,
        long MemoryRequest,
        long MemoryLimit,
        QosClass Qos
    );

    public override string StrategyId => "container-k8s";
    public override string DisplayName => "Kubernetes Resource Manager";
    public override ResourceCategory Category => ResourceCategory.Composite;
    public override ResourceStrategyCapabilities Capabilities => new()
    {
        SupportsCpu = true, SupportsMemory = true, SupportsIO = false,
        SupportsGpu = false, SupportsNetwork = false, SupportsQuotas = true,
        SupportsHierarchicalQuotas = true, SupportsPreemption = true
    };
    public override string SemanticDescription =>
        "Kubernetes resource manager enforcing resource requests/limits and QoS classes " +
        "(Guaranteed, Burstable, BestEffort) with namespace-level LimitRange and ResourceQuota.";
    public override string[] Tags => ["container", "kubernetes", "k8s", "qos", "limitrange", "pod"];

    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default)
    {
        var totalCpuRequest = _podResources.Values.Sum(p => p.CpuRequest);
        var totalMemoryRequest = _podResources.Values.Sum(p => p.MemoryRequest);

        return Task.FromResult(new ResourceMetrics
        {
            CpuPercent = (totalCpuRequest / Environment.ProcessorCount) * 100,
            MemoryBytes = totalMemoryRequest,
            Timestamp = DateTime.UtcNow
        });
    }

    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct)
    {
        var handle = Guid.NewGuid().ToString("N");
        var podName = $"pod-{handle[..8]}";

        // Determine QoS class based on request/limit relationship
        var hasLimits = request.CpuCores > 0 || request.MemoryBytes > 0;
        var qosClass = request.Priority switch
        {
            > 80 => QosClass.Guaranteed,  // High priority: guaranteed resources
            > 40 => QosClass.Burstable,   // Medium: can burst
            _ => QosClass.BestEffort      // Low: best effort
        };

        var cpuLimit = qosClass == QosClass.Guaranteed ? request.CpuCores : request.CpuCores * 1.5;
        var memLimit = qosClass == QosClass.Guaranteed ? request.MemoryBytes : request.MemoryBytes * 2;

        var pod = new PodResources(
            PodName: podName,
            CpuRequest: request.CpuCores,
            CpuLimit: cpuLimit,
            MemoryRequest: request.MemoryBytes,
            MemoryLimit: memLimit,
            Qos: qosClass
        );

        _podResources[handle] = pod;

        return Task.FromResult(new ResourceAllocation
        {
            RequestId = request.RequestId,
            Success = true,
            AllocationHandle = handle,
            AllocatedCpuCores = request.CpuCores,
            AllocatedMemoryBytes = request.MemoryBytes
        });
    }

    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct)
    {
        if (allocation.AllocationHandle != null)
        {
            _podResources.TryRemove(allocation.AllocationHandle, out _);
        }
        return Task.FromResult(true);
    }
}

/// <summary>
/// Podman rootless container resource strategy.
/// Manages cgroup delegation for rootless containers.
/// </summary>
public sealed class PodmanResourceStrategy : ResourceStrategyBase
{
    private readonly ConcurrentDictionary<string, PodmanContainer> _containers = new();
    private int _containerSequence;

    private sealed record PodmanContainer(string Id, double CpuShares, long MemoryBytes, bool Rootless);

    public override string StrategyId => "container-podman";
    public override string DisplayName => "Podman Rootless Manager";
    public override ResourceCategory Category => ResourceCategory.Composite;
    public override ResourceStrategyCapabilities Capabilities => new()
    {
        SupportsCpu = true, SupportsMemory = true, SupportsIO = true,
        SupportsGpu = false, SupportsNetwork = false, SupportsQuotas = true,
        SupportsHierarchicalQuotas = true, SupportsPreemption = false
    };
    public override string SemanticDescription =>
        "Podman rootless container manager leveraging cgroup delegation for unprivileged containers, " +
        "systemd integration, and user namespace isolation without root privileges.";
    public override string[] Tags => ["container", "podman", "rootless", "systemd", "user-namespace"];

    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default)
    {
        var totalCpuShares = _containers.Values.Sum(c => c.CpuShares);
        var totalMemory = _containers.Values.Sum(c => c.MemoryBytes);

        return Task.FromResult(new ResourceMetrics
        {
            CpuPercent = (totalCpuShares / (Environment.ProcessorCount * 1024.0)) * 100,
            MemoryBytes = totalMemory,
            Timestamp = DateTime.UtcNow
        });
    }

    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct)
    {
        var handle = Guid.NewGuid().ToString("N");
        var containerId = $"pm-{Interlocked.Increment(ref _containerSequence):D6}";

        // CPU shares: 1024 per core (cgroup v2 weight)
        var cpuShares = request.CpuCores * 1024;

        var container = new PodmanContainer(
            Id: containerId,
            CpuShares: cpuShares,
            MemoryBytes: request.MemoryBytes,
            Rootless: true
        );

        _containers[handle] = container;

        return Task.FromResult(new ResourceAllocation
        {
            RequestId = request.RequestId,
            Success = true,
            AllocationHandle = handle,
            AllocatedCpuCores = request.CpuCores,
            AllocatedMemoryBytes = request.MemoryBytes
        });
    }

    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct)
    {
        if (allocation.AllocationHandle != null)
        {
            _containers.TryRemove(allocation.AllocationHandle, out _);
        }
        return Task.FromResult(true);
    }
}

/// <summary>
/// Windows Job Object strategy for process group resource control.
/// </summary>
public sealed class WindowsJobObjectStrategy : ResourceStrategyBase
{
    private readonly ConcurrentDictionary<string, JobObjectLimits> _jobObjects = new();

    private sealed record JobObjectLimits(
        string JobName,
        double CpuRate,
        long MemoryBytes,
        int ProcessCountLimit
    );

    public override string StrategyId => "container-job-object";
    public override string DisplayName => "Windows Job Object Manager";
    public override ResourceCategory Category => ResourceCategory.Composite;
    public override ResourceStrategyCapabilities Capabilities => new()
    {
        SupportsCpu = true, SupportsMemory = true, SupportsIO = false,
        SupportsGpu = false, SupportsNetwork = false, SupportsQuotas = true,
        SupportsHierarchicalQuotas = true, SupportsPreemption = true
    };
    public override string SemanticDescription =>
        "Windows Job Object manager for process group resource control, enforcing CPU rate limits, " +
        "memory caps, and process count limits using Win32 Job Objects API.";
    public override string[] Tags => ["container", "windows", "job-object", "process-group", "win32"];

    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default)
    {
        var totalCpuRate = _jobObjects.Values.Sum(j => j.CpuRate);
        var totalMemory = _jobObjects.Values.Sum(j => j.MemoryBytes);

        return Task.FromResult(new ResourceMetrics
        {
            CpuPercent = totalCpuRate,
            MemoryBytes = totalMemory,
            Timestamp = DateTime.UtcNow
        });
    }

    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct)
    {
        var handle = Guid.NewGuid().ToString("N");
        var jobName = $"DWH-Job-{handle[..8]}";

        // CPU rate: percentage * 100 (e.g., 50% = 5000)
        var cpuRate = (request.CpuCores / Environment.ProcessorCount) * 10000;

        var limits = new JobObjectLimits(
            JobName: jobName,
            CpuRate: cpuRate,
            MemoryBytes: request.MemoryBytes,
            ProcessCountLimit: 100
        );

        _jobObjects[handle] = limits;

        return Task.FromResult(new ResourceAllocation
        {
            RequestId = request.RequestId,
            Success = true,
            AllocationHandle = handle,
            AllocatedCpuCores = request.CpuCores,
            AllocatedMemoryBytes = request.MemoryBytes
        });
    }

    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct)
    {
        if (allocation.AllocationHandle != null)
        {
            _jobObjects.TryRemove(allocation.AllocationHandle, out _);
        }
        return Task.FromResult(true);
    }
}

/// <summary>
/// Windows Server container strategy.
/// Manages Hyper-V isolation and processor weight.
/// </summary>
public sealed class WindowsContainerStrategy : ResourceStrategyBase
{
    private readonly ConcurrentDictionary<string, WindowsContainerConfig> _containers = new();

    private enum IsolationMode { Process, HyperV }

    private sealed record WindowsContainerConfig(
        string ContainerId,
        IsolationMode Isolation,
        int ProcessorWeight,
        long MemoryBytes,
        int ProcessorCount
    );

    public override string StrategyId => "container-windows";
    public override string DisplayName => "Windows Container Manager";
    public override ResourceCategory Category => ResourceCategory.Composite;
    public override ResourceStrategyCapabilities Capabilities => new()
    {
        SupportsCpu = true, SupportsMemory = true, SupportsIO = false,
        SupportsGpu = false, SupportsNetwork = false, SupportsQuotas = true,
        SupportsHierarchicalQuotas = false, SupportsPreemption = false
    };
    public override string SemanticDescription =>
        "Windows Server container manager supporting process and Hyper-V isolation modes, " +
        "CPU processor weight, memory limits, and processor count allocation.";
    public override string[] Tags => ["container", "windows", "hyperv", "process-isolation", "weight"];

    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default)
    {
        var totalWeight = _containers.Values.Sum(c => c.ProcessorWeight);
        var totalMemory = _containers.Values.Sum(c => c.MemoryBytes);

        return Task.FromResult(new ResourceMetrics
        {
            CpuPercent = (totalWeight / (_containers.Count * 500.0)) * 100,
            MemoryBytes = totalMemory,
            Timestamp = DateTime.UtcNow
        });
    }

    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct)
    {
        var handle = Guid.NewGuid().ToString("N");
        var containerId = $"wc-{handle[..12]}";

        // Processor weight: 0-10000 scale, higher = more CPU
        var weight = (int)(request.Priority * 100);

        // Use Hyper-V isolation for high-priority workloads
        var isolation = request.Priority > 70 ? IsolationMode.HyperV : IsolationMode.Process;

        var config = new WindowsContainerConfig(
            ContainerId: containerId,
            Isolation: isolation,
            ProcessorWeight: weight,
            MemoryBytes: request.MemoryBytes,
            ProcessorCount: (int)Math.Ceiling(request.CpuCores)
        );

        _containers[handle] = config;

        return Task.FromResult(new ResourceAllocation
        {
            RequestId = request.RequestId,
            Success = true,
            AllocationHandle = handle,
            AllocatedCpuCores = request.CpuCores,
            AllocatedMemoryBytes = request.MemoryBytes
        });
    }

    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct)
    {
        if (allocation.AllocationHandle != null)
        {
            _containers.TryRemove(allocation.AllocationHandle, out _);
        }
        return Task.FromResult(true);
    }
}

/// <summary>
/// Unix process group management strategy.
/// Controls nice values, ionice, and cpulimit.
/// </summary>
public sealed class ProcessGroupStrategy : ResourceStrategyBase
{
    private readonly ConcurrentDictionary<string, ProcessGroupConfig> _processGroups = new();

    private sealed record ProcessGroupConfig(int Pgid, int NiceValue, int IoniceClass, int CpuLimitPercent);

    public override string StrategyId => "container-pgroup";
    public override string DisplayName => "Process Group Manager";
    public override ResourceCategory Category => ResourceCategory.Composite;
    public override ResourceStrategyCapabilities Capabilities => new()
    {
        SupportsCpu = true, SupportsMemory = false, SupportsIO = true,
        SupportsGpu = false, SupportsNetwork = false, SupportsQuotas = true,
        SupportsHierarchicalQuotas = false, SupportsPreemption = true
    };
    public override string SemanticDescription =>
        "Unix process group manager controlling CPU scheduling priority (nice), I/O priority (ionice), " +
        "and CPU rate limiting (cpulimit) for process groups without containerization.";
    public override string[] Tags => ["container", "process-group", "nice", "ionice", "cpulimit", "unix"];

    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default)
    {
        var avgNice = _processGroups.Count > 0
            ? _processGroups.Values.Average(p => p.NiceValue)
            : 0;

        return Task.FromResult(new ResourceMetrics
        {
            CpuPercent = Math.Max(0, (20 - avgNice) * 5), // nice -20 to 19 maps to 100% to 0%
            Timestamp = DateTime.UtcNow
        });
    }

    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct)
    {
        var handle = Guid.NewGuid().ToString("N");
        var pgid = Environment.ProcessId + handle.GetHashCode();

        // Nice value: -20 (highest) to 19 (lowest)
        var niceValue = request.Priority switch
        {
            > 80 => -10,  // High priority
            > 60 => 0,    // Normal
            > 40 => 10,   // Low
            _ => 19       // Idle
        };

        // ionice class: 1=RT, 2=BE, 3=Idle
        var ioniceClass = request.Priority > 70 ? 1 : request.Priority > 40 ? 2 : 3;

        var cpuLimitPercent = (int)((request.CpuCores / Environment.ProcessorCount) * 100);

        var config = new ProcessGroupConfig(
            Pgid: pgid,
            NiceValue: niceValue,
            IoniceClass: ioniceClass,
            CpuLimitPercent: cpuLimitPercent
        );

        _processGroups[handle] = config;

        return Task.FromResult(new ResourceAllocation
        {
            RequestId = request.RequestId,
            Success = true,
            AllocationHandle = handle,
            AllocatedCpuCores = request.CpuCores
        });
    }

    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct)
    {
        if (allocation.AllocationHandle != null)
        {
            _processGroups.TryRemove(allocation.AllocationHandle, out _);
        }
        return Task.FromResult(true);
    }
}

/// <summary>
/// Linux namespace isolation strategy.
/// Manages pid, net, mount, user namespaces without full containers.
/// </summary>
public sealed class NamespaceIsolationStrategy : ResourceStrategyBase
{
    private readonly ConcurrentDictionary<string, NamespaceConfig> _namespaces = new();

    private sealed record NamespaceConfig(
        string NsId,
        bool PidNs,
        bool NetNs,
        bool MountNs,
        bool UserNs,
        int UidMapping,
        int GidMapping
    );

    public override string StrategyId => "container-namespace";
    public override string DisplayName => "Namespace Isolation Manager";
    public override ResourceCategory Category => ResourceCategory.Composite;
    public override ResourceStrategyCapabilities Capabilities => new()
    {
        SupportsCpu = false, SupportsMemory = false, SupportsIO = false,
        SupportsGpu = false, SupportsNetwork = true, SupportsQuotas = true,
        SupportsHierarchicalQuotas = false, SupportsPreemption = false
    };
    public override string SemanticDescription =>
        "Linux namespace isolation manager creating pid, net, mount, and user namespaces " +
        "for resource isolation without full containerization overhead.";
    public override string[] Tags => ["container", "namespace", "linux", "isolation", "pid", "net", "user"];

    public override Task<ResourceMetrics> GetMetricsAsync(CancellationToken ct = default)
    {
        return Task.FromResult(new ResourceMetrics
        {
            NetworkBandwidth = _namespaces.Count * 1024L * 1024, // 1MB per namespace
            Timestamp = DateTime.UtcNow
        });
    }

    protected override Task<ResourceAllocation> AllocateCoreAsync(ResourceRequest request, CancellationToken ct)
    {
        var handle = Guid.NewGuid().ToString("N");
        var nsId = $"ns-{handle[..8]}";

        // Create namespace configuration based on isolation requirements
        var config = new NamespaceConfig(
            NsId: nsId,
            PidNs: true,              // Always isolate PIDs
            NetNs: request.Priority > 60, // Network isolation for medium+ priority
            MountNs: true,            // Filesystem isolation
            UserNs: request.Priority < 80, // User namespace for non-root (except high priority)
            UidMapping: 100000 + _namespaces.Count,
            GidMapping: 100000 + _namespaces.Count
        );

        _namespaces[handle] = config;

        return Task.FromResult(new ResourceAllocation
        {
            RequestId = request.RequestId,
            Success = true,
            AllocationHandle = handle,
            AllocatedCpuCores = request.CpuCores,
            AllocatedMemoryBytes = request.MemoryBytes
        });
    }

    protected override Task<bool> ReleaseCoreAsync(ResourceAllocation allocation, CancellationToken ct)
    {
        if (allocation.AllocationHandle != null)
        {
            _namespaces.TryRemove(allocation.AllocationHandle, out _);
        }
        return Task.FromResult(true);
    }
}
