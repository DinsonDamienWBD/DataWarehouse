// Phase 89: Ecosystem Compatibility - Jepsen Fault Injection (ECOS-16)
// Network partition, process kill, clock skew, and disk corruption fault injectors

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Ecosystem;

#region Interfaces and Records

/// <summary>
/// Interface for fault injectors that can inject and heal faults on Jepsen cluster nodes.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Jepsen fault injection (ECOS-16)")]
public interface IFaultInjector
{
    /// <summary>Type identifier for this fault (e.g., "network-partition", "process-kill").</summary>
    string FaultType { get; }

    /// <summary>
    /// Injects the fault into the cluster targeting specific nodes.
    /// </summary>
    /// <param name="cluster">The Jepsen cluster.</param>
    /// <param name="target">Which nodes to affect and for how long.</param>
    /// <param name="ct">Cancellation token.</param>
    Task InjectAsync(JepsenCluster cluster, FaultTarget target, CancellationToken ct = default);

    /// <summary>
    /// Heals (removes) the injected fault from targeted nodes.
    /// </summary>
    /// <param name="cluster">The Jepsen cluster.</param>
    /// <param name="target">Which nodes were affected.</param>
    /// <param name="ct">Cancellation token.</param>
    Task HealAsync(JepsenCluster cluster, FaultTarget target, CancellationToken ct = default);
}

/// <summary>
/// Specifies which nodes to target with a fault and for how long.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Jepsen fault injection (ECOS-16)")]
public sealed record FaultTarget
{
    /// <summary>Node IDs to target (e.g., ["n1", "n2"]).</summary>
    public required IReadOnlyList<string> NodeIds { get; init; }

    /// <summary>How long the fault should last before auto-healing.</summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// A scheduled fault injection entry within a test plan.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Jepsen fault injection (ECOS-16)")]
public sealed record FaultScheduleEntry
{
    /// <summary>The fault injector to use.</summary>
    public required IFaultInjector Injector { get; init; }

    /// <summary>Target nodes and duration.</summary>
    public required FaultTarget Target { get; init; }

    /// <summary>When to start this fault (relative to test start).</summary>
    public TimeSpan StartAfter { get; init; } = TimeSpan.Zero;

    /// <summary>How long to keep the fault active (overrides target duration for scheduling).</summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(10);
}

#endregion

#region Partition Mode

/// <summary>
/// Mode for network partition fault injection.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Jepsen fault injection (ECOS-16)")]
public enum PartitionMode
{
    /// <summary>Split cluster into majority (N/2+1) and minority.</summary>
    MajorityMinority = 0,

    /// <summary>Split cluster into two equal halves.</summary>
    HalfAndHalf = 1,

    /// <summary>Isolate a single node from all others.</summary>
    Isolate = 2
}

#endregion

#region Network Partition Fault

/// <summary>
/// Injects network partitions by creating iptables rules to block traffic between node groups.
/// Uses <c>docker exec {container} iptables -A INPUT -s {ip} -j DROP</c> for injection
/// and <c>iptables -D</c> for healing.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Jepsen fault injection (ECOS-16)")]
public sealed class NetworkPartitionFault : IFaultInjector
{
    /// <summary>How to partition the nodes.</summary>
    public PartitionMode Mode { get; init; } = PartitionMode.MajorityMinority;

    /// <inheritdoc />
    public string FaultType => "network-partition";

    /// <inheritdoc />
    public async Task InjectAsync(JepsenCluster cluster, FaultTarget target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(target);

        var (partitioned, reachable) = ComputePartition(cluster, target);

        // For each partitioned node, drop packets from all reachable nodes
        foreach (var isolated in partitioned)
        {
            foreach (var peer in reachable)
            {
                await DockerExecAsync(
                    isolated.ContainerName,
                    $"iptables -A INPUT -s {peer.IpAddress} -j DROP",
                    ct).ConfigureAwait(false);

                await DockerExecAsync(
                    isolated.ContainerName,
                    $"iptables -A OUTPUT -d {peer.IpAddress} -j DROP",
                    ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async Task HealAsync(JepsenCluster cluster, FaultTarget target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(target);

        var (partitioned, reachable) = ComputePartition(cluster, target);

        // Remove iptables rules
        foreach (var isolated in partitioned)
        {
            foreach (var peer in reachable)
            {
                try
                {
                    await DockerExecAsync(
                        isolated.ContainerName,
                        $"iptables -D INPUT -s {peer.IpAddress} -j DROP",
                        ct).ConfigureAwait(false);

                    await DockerExecAsync(
                        isolated.ContainerName,
                        $"iptables -D OUTPUT -d {peer.IpAddress} -j DROP",
                        ct).ConfigureAwait(false);
                }
                catch
                {
                    // Rule may not exist if injection was partial
                }
            }
        }
    }

    private (IReadOnlyList<JepsenNode> Partitioned, IReadOnlyList<JepsenNode> Reachable)
        ComputePartition(JepsenCluster cluster, FaultTarget target)
    {
        var targetSet = target.NodeIds.ToHashSet();
        List<JepsenNode> partitioned;
        List<JepsenNode> reachable;

        switch (Mode)
        {
            case PartitionMode.Isolate:
                partitioned = cluster.Nodes.Where(n => targetSet.Contains(n.NodeId)).ToList();
                reachable = cluster.Nodes.Where(n => !targetSet.Contains(n.NodeId)).ToList();
                break;

            case PartitionMode.HalfAndHalf:
                var half = cluster.Nodes.Count / 2;
                partitioned = cluster.Nodes.Take(half).ToList();
                reachable = cluster.Nodes.Skip(half).ToList();
                break;

            case PartitionMode.MajorityMinority:
            default:
                var majoritySize = (cluster.Nodes.Count / 2) + 1;
                partitioned = cluster.Nodes.Skip(majoritySize).ToList();
                reachable = cluster.Nodes.Take(majoritySize).ToList();
                break;
        }

        return (partitioned, reachable);
    }

    private static async Task DockerExecAsync(string containerName, string command, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"exec {containerName} /bin/sh -c \"{command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
    }
}

#endregion

#region Process Kill Fault

/// <summary>
/// Kills the DataWarehouse process inside the container using <c>kill -9</c>.
/// Heals by restarting the process with <c>/start.sh</c>.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Jepsen fault injection (ECOS-16)")]
public sealed class ProcessKillFault : IFaultInjector
{
    /// <summary>Process name to kill (default: "dw-server").</summary>
    public string ProcessName { get; init; } = "dw-server";

    /// <inheritdoc />
    public string FaultType => "process-kill";

    /// <inheritdoc />
    public async Task InjectAsync(JepsenCluster cluster, FaultTarget target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(target);

        var targetSet = target.NodeIds.ToHashSet();
        var nodes = cluster.Nodes.Where(n => targetSet.Contains(n.NodeId));

        foreach (var node in nodes)
        {
            // Find and kill the DW process
            await DockerExecAsync(
                node.ContainerName,
                $"pkill -9 -f {ProcessName}",
                ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task HealAsync(JepsenCluster cluster, FaultTarget target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(target);

        var targetSet = target.NodeIds.ToHashSet();
        var nodes = cluster.Nodes.Where(n => targetSet.Contains(n.NodeId));

        foreach (var node in nodes)
        {
            // Restart the DW process
            await DockerExecAsync(
                node.ContainerName,
                "/start.sh",
                ct).ConfigureAwait(false);
        }
    }

    private static async Task DockerExecAsync(string containerName, string command, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"exec {containerName} /bin/sh -c \"{command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        // Process kill may return non-zero if process not found — that's acceptable
    }
}

#endregion

#region Clock Skew Fault

/// <summary>
/// Skews the system clock on selected nodes using <c>date -s</c>.
/// Configurable skew amount (default 500ms to 30s). Heals by resetting to NTP time.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Jepsen fault injection (ECOS-16)")]
public sealed class ClockSkewFault : IFaultInjector
{
    /// <summary>Minimum clock skew amount.</summary>
    public TimeSpan MinSkew { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Maximum clock skew amount.</summary>
    public TimeSpan MaxSkew { get; init; } = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    public string FaultType => "clock-skew";

    /// <inheritdoc />
    public async Task InjectAsync(JepsenCluster cluster, FaultTarget target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(target);

        var targetSet = target.NodeIds.ToHashSet();
        var nodes = cluster.Nodes.Where(n => targetSet.Contains(n.NodeId));
        var random = new Random();

        foreach (var node in nodes)
        {
            // Random skew between min and max
            var skewMs = random.Next(
                (int)MinSkew.TotalMilliseconds,
                (int)MaxSkew.TotalMilliseconds + 1);
            var skewSeconds = skewMs / 1000.0;

            // Apply positive or negative skew randomly
            var direction = random.Next(2) == 0 ? "+" : "-";

            await DockerExecAsync(
                node.ContainerName,
                $"date -s \"{direction}{skewSeconds} seconds\"",
                ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task HealAsync(JepsenCluster cluster, FaultTarget target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(target);

        var targetSet = target.NodeIds.ToHashSet();
        var nodes = cluster.Nodes.Where(n => targetSet.Contains(n.NodeId));

        foreach (var node in nodes)
        {
            // Reset clock via NTP
            try
            {
                await DockerExecAsync(
                    node.ContainerName,
                    "ntpdate -b pool.ntp.org",
                    ct).ConfigureAwait(false);
            }
            catch
            {
                // Fallback: use hwclock if ntpdate not available
                try
                {
                    await DockerExecAsync(
                        node.ContainerName,
                        "hwclock --hctosys",
                        ct).ConfigureAwait(false);
                }
                catch
                {
                    // Best effort clock reset
                }
            }
        }
    }

    private static async Task DockerExecAsync(string containerName, string command, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"exec {containerName} /bin/sh -c \"{command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
    }
}

#endregion

#region Disk Corruption Fault

/// <summary>
/// Corrupts random bytes in VDE data files using <c>dd</c> with <c>/dev/urandom</c>.
/// Healing relies on DW's own corruption detection and recovery mechanisms.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Jepsen fault injection (ECOS-16)")]
public sealed class DiskCorruptionFault : IFaultInjector
{
    /// <summary>Path to VDE data files inside the container.</summary>
    public string DataPath { get; init; } = "/var/lib/dw/data";

    /// <summary>Number of bytes to corrupt per injection.</summary>
    public int CorruptionBytes { get; init; } = 64;

    /// <summary>Random seed for reproducible corruption (null for random).</summary>
    public int? RandomSeed { get; init; }

    /// <inheritdoc />
    public string FaultType => "disk-corruption";

    /// <inheritdoc />
    public async Task InjectAsync(JepsenCluster cluster, FaultTarget target, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        ArgumentNullException.ThrowIfNull(target);

        var targetSet = target.NodeIds.ToHashSet();
        var nodes = cluster.Nodes.Where(n => targetSet.Contains(n.NodeId));
        var random = RandomSeed.HasValue ? new Random(RandomSeed.Value) : new Random();

        foreach (var node in nodes)
        {
            // Find VDE data files
            var offset = random.Next(4096, 1024 * 1024); // Random offset past headers

            // Corrupt random bytes in the data file
            await DockerExecAsync(
                node.ContainerName,
                $"dd if=/dev/urandom of={DataPath}/default.dwvd bs=1 count={CorruptionBytes} seek={offset} conv=notrunc 2>/dev/null",
                ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task HealAsync(JepsenCluster cluster, FaultTarget target, CancellationToken ct = default)
    {
        // Disk corruption healing relies on DW's built-in corruption detection
        // and recovery mechanisms (Merkle tree verification, extent checksums,
        // Raft-based replication for data repair).
        // No external healing action needed — the system must self-heal.
        return Task.CompletedTask;
    }

    private static async Task DockerExecAsync(string containerName, string command, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = $"exec {containerName} /bin/sh -c \"{command}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
    }
}

#endregion
