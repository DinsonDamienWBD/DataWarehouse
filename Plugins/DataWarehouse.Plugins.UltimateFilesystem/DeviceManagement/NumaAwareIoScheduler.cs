using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Utilities;
using DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.Plugins.UltimateFilesystem.DeviceManagement;

/// <summary>
/// Result of scheduling analysis for a device I/O operation, indicating the target
/// NUMA node, preferred CPU core, and whether the operation would cross NUMA boundaries.
/// </summary>
/// <param name="TargetNumaNode">The NUMA node where the target device resides.</param>
/// <param name="PreferredCpuCore">Suggested CPU core on the target NUMA node for affinity (null if unknown).</param>
/// <param name="IsCrossNuma">True if the calling thread is on a different NUMA node than the target device.</param>
[SdkCompatibility("6.0.0", Notes = "Phase 90: NUMA-aware I/O scheduling (BMDV-09)")]
public sealed record IoSchedulingResult(
    int TargetNumaNode,
    int? PreferredCpuCore,
    bool IsCrossNuma);

/// <summary>
/// NUMA-aware I/O scheduler that routes operations to the CPU closest to the target device's
/// NUMA node. Provides scheduling hints and best-effort NUMA-affinitized I/O execution
/// to minimize cross-NUMA memory traffic for storage operations.
/// </summary>
/// <remarks>
/// Thread affinity is best-effort: if the platform does not support processor affinity
/// or the operation fails, I/O proceeds on the current thread. This scheduler is designed
/// for downstream consumers like CompoundBlockDevice (Phase 91) to minimize cross-NUMA traffic.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 90: NUMA-aware I/O scheduling (BMDV-09)")]
public sealed class NumaAwareIoScheduler
{
    private readonly DeviceTopologyTree _topology;
    private readonly IReadOnlyList<NumaAffinityInfo> _numaInfo;
    private readonly BoundedDictionary<string, int> _deviceNumaMap = new(500);
    private readonly Dictionary<int, IReadOnlyList<int>> _numaCores;
    private readonly Dictionary<int, int> _coreRoundRobin = new();

    /// <summary>
    /// Creates a new NUMA-aware I/O scheduler from the given topology and NUMA affinity info.
    /// </summary>
    /// <param name="topology">Device topology tree for device-to-NUMA mapping.</param>
    /// <param name="numaInfo">NUMA affinity information including CPU core assignments.</param>
    public NumaAwareIoScheduler(DeviceTopologyTree topology, IReadOnlyList<NumaAffinityInfo> numaInfo)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(numaInfo);

        _topology = topology;
        _numaInfo = numaInfo;

        // Build NUMA core lookup
        _numaCores = new Dictionary<int, IReadOnlyList<int>>();
        foreach (var info in numaInfo)
        {
            _numaCores[info.NumaNode] = info.CpuCores;
            _coreRoundRobin[info.NumaNode] = 0;

            // Pre-populate device -> NUMA mapping from affinity info
            foreach (var deviceId in info.DeviceIds)
            {
                _deviceNumaMap[deviceId] = info.NumaNode;
            }
        }
    }

    /// <summary>
    /// Gets the scheduling hint for an I/O operation targeting the specified device.
    /// Determines the target NUMA node, suggests a preferred CPU core, and detects
    /// whether the operation would cross NUMA boundaries from the calling thread.
    /// </summary>
    /// <param name="deviceId">The DeviceId of the target device.</param>
    /// <returns>Scheduling result with NUMA affinity information.</returns>
    public IoSchedulingResult GetSchedulingHint(string deviceId)
    {
        ArgumentNullException.ThrowIfNull(deviceId);

        var targetNuma = GetDeviceNumaNode(deviceId);
        var callerNuma = GetCurrentThreadNumaNode();
        var isCrossNuma = callerNuma.HasValue && callerNuma.Value != targetNuma;
        int? preferredCore = GetNextPreferredCore(targetNuma);

        return new IoSchedulingResult(targetNuma, preferredCore, isCrossNuma);
    }

    /// <summary>
    /// Schedules an I/O operation with best-effort NUMA affinity. If the calling thread
    /// is on a different NUMA node than the target device and the platform supports
    /// processor affinity, the operation is dispatched to a thread affinitized to the
    /// target NUMA node's preferred core. Otherwise, the operation runs on the current thread.
    /// </summary>
    /// <typeparam name="T">The return type of the I/O operation.</typeparam>
    /// <param name="deviceId">The DeviceId of the target device.</param>
    /// <param name="ioOperation">The async I/O operation to execute.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The result of the I/O operation.</returns>
    public async Task<T> ScheduleIoAsync<T>(string deviceId, Func<CancellationToken, Task<T>> ioOperation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(deviceId);
        ArgumentNullException.ThrowIfNull(ioOperation);

        var hint = GetSchedulingHint(deviceId);

        // If not cross-NUMA or no preferred core, just run on current thread
        if (!hint.IsCrossNuma || !hint.PreferredCpuCore.HasValue)
        {
            return await ioOperation(ct).ConfigureAwait(false);
        }

        // Attempt NUMA-affinitized execution via TaskFactory with LongRunning
        // to get a dedicated thread we can set affinity on
        var preferredCore = hint.PreferredCpuCore.Value;

        return await Task.Factory.StartNew(
            () =>
            {
                TrySetThreadAffinity(preferredCore);
                return ioOperation(ct);
            },
            ct,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap().ConfigureAwait(false);
    }

    /// <summary>
    /// Returns cached scheduling hints for all known devices.
    /// </summary>
    /// <returns>Dictionary mapping DeviceId to IoSchedulingResult for all devices with NUMA info.</returns>
    public IReadOnlyDictionary<string, IoSchedulingResult> GetAllHints()
    {
        var hints = new Dictionary<string, IoSchedulingResult>();
        foreach (var info in _numaInfo)
        {
            foreach (var deviceId in info.DeviceIds)
            {
                hints[deviceId] = GetSchedulingHint(deviceId);
            }
        }
        return hints;
    }

    private int GetDeviceNumaNode(string deviceId)
    {
        if (_deviceNumaMap.TryGetValue(deviceId, out var numa))
            return numa;

        // Fallback: search topology tree
        var mapper = new DeviceTopologyMapper();
        var node = mapper.FindDeviceNode(_topology, deviceId);
        var numaNode = node?.NumaNode ?? 0;
        _deviceNumaMap[deviceId] = numaNode;
        return numaNode;
    }

    private static int? GetCurrentThreadNumaNode()
    {
        try
        {
            // Use Thread.GetCurrentProcessorId to determine current CPU,
            // then map to NUMA node. This is best-effort.
            var processorId = Thread.GetCurrentProcessorId();

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return GetLinuxNumaNodeForProcessor(processorId);
            }

            // On other platforms, NUMA detection is not straightforward without P/Invoke.
            // Return null to indicate unknown.
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static int? GetLinuxNumaNodeForProcessor(int processorId)
    {
        try
        {
            // On Linux, /sys/devices/system/cpu/cpu{N}/topology/physical_package_id
            // or /sys/devices/system/node/node*/cpulist can be used.
            // We check which NUMA node contains this processor.
            var nodeDir = "/sys/devices/system/node";
            if (!System.IO.Directory.Exists(nodeDir)) return null;

            foreach (var nodePath in System.IO.Directory.GetDirectories(nodeDir, "node*"))
            {
                var cpuListPath = System.IO.Path.Combine(nodePath, "cpulist");
                if (!System.IO.File.Exists(cpuListPath)) continue;

                var cpuListText = System.IO.File.ReadAllText(cpuListPath).Trim();
                var cores = DeviceTopologyMapper.ParseCpuList(cpuListText);
                if (cores.Contains(processorId))
                {
                    var nodeName = System.IO.Path.GetFileName(nodePath);
                    if (nodeName.StartsWith("node", StringComparison.Ordinal)
                        && int.TryParse(nodeName.AsSpan(4), out var nodeNum))
                    {
                        return nodeNum;
                    }
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private int? GetNextPreferredCore(int numaNode)
    {
        if (!_numaCores.TryGetValue(numaNode, out var cores) || cores.Count == 0)
            return null;

        lock (_coreRoundRobin)
        {
            if (!_coreRoundRobin.TryGetValue(numaNode, out var index))
                index = 0;

            var core = cores[index % cores.Count];
            _coreRoundRobin[numaNode] = (index + 1) % cores.Count;
            return core;
        }
    }

    private static void TrySetThreadAffinity(int coreId)
    {
        try
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                // On Linux, thread affinity would require sched_setaffinity P/Invoke.
                // This is deferred to a future enhancement; for now, best-effort on Windows only.
                return;
            }

            TrySetWindowsThreadAffinity(coreId);
        }
        catch
        {

            // Best-effort: if we can't set affinity, just continue
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void TrySetWindowsThreadAffinity(int coreId)
    {
        try
        {
            // Best-effort processor affinity via ProcessThread.ProcessorAffinity
            var process = Process.GetCurrentProcess();
            var currentThreadId = Environment.CurrentManagedThreadId;

            // Find the OS thread. Note: managed thread ID may not directly map to OS thread.
            // This is inherently best-effort on .NET.
            foreach (ProcessThread thread in process.Threads)
            {
                try
                {
                    // Set affinity on the current process thread as approximation
                    if (thread.Id == currentThreadId || thread.ThreadState == System.Diagnostics.ThreadState.Running)
                    {
                        thread.ProcessorAffinity = new IntPtr(1L << coreId);
                        break;
                    }
                }
                catch
                {

                    // Affinity setting failed; continue without affinity (best-effort)
                    System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
                }
            }
        }
        catch
        {

            // Best-effort: if we can't set affinity, just continue
            System.Diagnostics.Debug.WriteLine("[Warning] caught exception in catch block");
        }
    }
}
