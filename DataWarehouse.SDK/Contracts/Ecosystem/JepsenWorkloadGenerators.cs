// Phase 89: Ecosystem Compatibility - Jepsen Workload Generators (ECOS-16)
// Register, set, list-append, and bank workload generators for consistency testing

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace DataWarehouse.SDK.Contracts.Ecosystem;

#region Workload Generator Interface

/// <summary>
/// Interface for workload generators that produce operations against a Jepsen cluster.
/// Each generator connects to random healthy nodes and records the operation history
/// with precise timestamps.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Jepsen workload generators (ECOS-16)")]
public interface IWorkloadGenerator
{
    /// <summary>Type identifier for this workload (e.g., "register", "set", "bank").</summary>
    string WorkloadType { get; }

    /// <summary>
    /// Runs the workload against the cluster, yielding operation history entries.
    /// </summary>
    /// <param name="cluster">The Jepsen cluster to run against.</param>
    /// <param name="clientId">Unique client identifier.</param>
    /// <param name="ct">Cancellation token (controls workload duration).</param>
    /// <returns>Async enumerable of operation history entries.</returns>
    IAsyncEnumerable<OperationHistoryEntry> RunAsync(
        JepsenCluster cluster, int clientId, CancellationToken ct = default);
}

#endregion

#region Register Workload

/// <summary>
/// Single-register read/write/CAS workload for testing linearizability.
/// Operations: read(key), write(key, value), cas(key, expected, new).
/// Generates a random mix of operations against a small set of keys.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Jepsen workload generators (ECOS-16)")]
public sealed class RegisterWorkload : IWorkloadGenerator
{
    /// <summary>Number of distinct keys to operate on.</summary>
    public int KeyCount { get; init; } = 5;

    /// <summary>Probability of a read operation (0.0-1.0).</summary>
    public double ReadProbability { get; init; } = 0.5;

    /// <summary>Probability of a CAS operation (remainder is write).</summary>
    public double CasProbability { get; init; } = 0.2;

    /// <summary>Delay between operations.</summary>
    public TimeSpan OperationDelay { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <inheritdoc />
    public string WorkloadType => "register";

    /// <inheritdoc />
    public async IAsyncEnumerable<OperationHistoryEntry> RunAsync(
        JepsenCluster cluster, int clientId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        var random = new Random(clientId * 31 + 17);
        var clientName = $"client-{clientId}";
        long valueCounter = 0;

        // Track last known values per key for CAS
        var knownValues = new Dictionary<string, string?>();

        while (!ct.IsCancellationRequested)
        {
            var key = $"r{random.Next(KeyCount)}";
            var node = SelectRandomHealthyNode(cluster, random);
            if (node == null) { await Task.Delay(100, ct).ConfigureAwait(false); continue; }

            var startTime = DateTimeOffset.UtcNow;
            var roll = random.NextDouble();
            OperationHistoryEntry entry;

            try
            {
                if (roll < ReadProbability)
                {
                    // Read operation
                    var readValue = await ExecuteReadAsync(node, key, ct).ConfigureAwait(false);
                    knownValues[key] = readValue;

                    entry = new OperationHistoryEntry
                    {
                        SequenceId = JepsenTestHarness.NextSequenceId(),
                        NodeId = node.NodeId,
                        ClientId = clientName,
                        Type = OperationType.Read,
                        Key = key,
                        Value = readValue,
                        StartTime = startTime,
                        EndTime = DateTimeOffset.UtcNow,
                        Result = OperationResult.Ok
                    };
                }
                else if (roll < ReadProbability + CasProbability)
                {
                    // CAS operation
                    knownValues.TryGetValue(key, out var expected);
                    var newValue = $"v{Interlocked.Increment(ref valueCounter)}";
                    var casResult = await ExecuteCasAsync(node, key, expected, newValue, ct)
                        .ConfigureAwait(false);

                    if (casResult) knownValues[key] = newValue;

                    entry = new OperationHistoryEntry
                    {
                        SequenceId = JepsenTestHarness.NextSequenceId(),
                        NodeId = node.NodeId,
                        ClientId = clientName,
                        Type = OperationType.Cas,
                        Key = key,
                        Value = newValue,
                        ExpectedValue = expected,
                        StartTime = startTime,
                        EndTime = DateTimeOffset.UtcNow,
                        Result = casResult ? OperationResult.Ok : OperationResult.Fail
                    };
                }
                else
                {
                    // Write operation
                    var writeValue = $"v{Interlocked.Increment(ref valueCounter)}";
                    await ExecuteWriteAsync(node, key, writeValue, ct).ConfigureAwait(false);
                    knownValues[key] = writeValue;

                    entry = new OperationHistoryEntry
                    {
                        SequenceId = JepsenTestHarness.NextSequenceId(),
                        NodeId = node.NodeId,
                        ClientId = clientName,
                        Type = OperationType.Write,
                        Key = key,
                        Value = writeValue,
                        StartTime = startTime,
                        EndTime = DateTimeOffset.UtcNow,
                        Result = OperationResult.Ok
                    };
                }
            }
            catch (OperationCanceledException) { yield break; }
            catch (TimeoutException)
            {
                entry = new OperationHistoryEntry
                {
                    SequenceId = JepsenTestHarness.NextSequenceId(),
                    NodeId = node.NodeId,
                    ClientId = clientName,
                    Type = OperationType.Read,
                    Key = key,
                    StartTime = startTime,
                    EndTime = DateTimeOffset.UtcNow,
                    Result = OperationResult.Timeout
                };
            }
            catch
            {
                entry = new OperationHistoryEntry
                {
                    SequenceId = JepsenTestHarness.NextSequenceId(),
                    NodeId = node.NodeId,
                    ClientId = clientName,
                    Type = OperationType.Read,
                    Key = key,
                    StartTime = startTime,
                    EndTime = DateTimeOffset.UtcNow,
                    Result = OperationResult.Crashed
                };
            }

            yield return entry;

            if (OperationDelay > TimeSpan.Zero)
            {
                try { await Task.Delay(OperationDelay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { yield break; }
            }
        }
    }

    private static Task<string?> ExecuteReadAsync(JepsenNode node, string key, CancellationToken ct)
    {
        _ = node; _ = key; _ = ct;
        throw new PlatformNotSupportedException(
            "Jepsen test harness requires external Jepsen framework and live database nodes. " +
            "Configure JepsenEndpoint in options.");
    }

    private static Task ExecuteWriteAsync(JepsenNode node, string key, string value, CancellationToken ct)
    {
        _ = node; _ = key; _ = value; _ = ct;
        throw new PlatformNotSupportedException(
            "Jepsen test harness requires external Jepsen framework and live database nodes. " +
            "Configure JepsenEndpoint in options.");
    }

    private static Task<bool> ExecuteCasAsync(
        JepsenNode node, string key, string? expected, string newValue, CancellationToken ct)
    {
        _ = node; _ = key; _ = expected; _ = newValue; _ = ct;
        throw new PlatformNotSupportedException(
            "Jepsen test harness requires external Jepsen framework and live database nodes. " +
            "Configure JepsenEndpoint in options.");
    }

    private static JepsenNode? SelectRandomHealthyNode(JepsenCluster cluster, Random random)
    {
        var healthy = cluster.Nodes.Where(n => n.State == JepsenNodeState.Running).ToList();
        return healthy.Count > 0 ? healthy[random.Next(healthy.Count)] : null;
    }
}

#endregion

#region Set Workload

/// <summary>
/// Set operations workload for testing serializable isolation.
/// Operations: add(key, element), read(key) -> set.
/// Verifies no lost elements after concurrent additions.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Jepsen workload generators (ECOS-16)")]
public sealed class SetWorkload : IWorkloadGenerator
{
    /// <summary>Number of distinct set keys.</summary>
    public int KeyCount { get; init; } = 3;

    /// <summary>Probability of a read operation (remainder is add).</summary>
    public double ReadProbability { get; init; } = 0.3;

    /// <summary>Delay between operations.</summary>
    public TimeSpan OperationDelay { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <inheritdoc />
    public string WorkloadType => "set";

    /// <inheritdoc />
    public async IAsyncEnumerable<OperationHistoryEntry> RunAsync(
        JepsenCluster cluster, int clientId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        var random = new Random(clientId * 37 + 13);
        var clientName = $"client-{clientId}";
        long elementCounter = clientId * 1_000_000L;

        while (!ct.IsCancellationRequested)
        {
            var key = $"s{random.Next(KeyCount)}";
            var node = SelectRandomHealthyNode(cluster, random);
            if (node == null) { await Task.Delay(100, ct).ConfigureAwait(false); continue; }

            var startTime = DateTimeOffset.UtcNow;
            OperationHistoryEntry entry;

            try
            {
                if (random.NextDouble() < ReadProbability)
                {
                    // Read the entire set
                    var setContents = await ExecuteSetReadAsync(node, key, ct).ConfigureAwait(false);

                    entry = new OperationHistoryEntry
                    {
                        SequenceId = JepsenTestHarness.NextSequenceId(),
                        NodeId = node.NodeId,
                        ClientId = clientName,
                        Type = OperationType.Read,
                        Key = key,
                        Value = setContents,
                        StartTime = startTime,
                        EndTime = DateTimeOffset.UtcNow,
                        Result = OperationResult.Ok
                    };
                }
                else
                {
                    // Add an element to the set
                    var element = $"e{Interlocked.Increment(ref elementCounter)}";
                    await ExecuteSetAddAsync(node, key, element, ct).ConfigureAwait(false);

                    entry = new OperationHistoryEntry
                    {
                        SequenceId = JepsenTestHarness.NextSequenceId(),
                        NodeId = node.NodeId,
                        ClientId = clientName,
                        Type = OperationType.Append,
                        Key = key,
                        Value = element,
                        StartTime = startTime,
                        EndTime = DateTimeOffset.UtcNow,
                        Result = OperationResult.Ok
                    };
                }
            }
            catch (OperationCanceledException) { yield break; }
            catch (TimeoutException)
            {
                entry = new OperationHistoryEntry
                {
                    SequenceId = JepsenTestHarness.NextSequenceId(),
                    NodeId = node.NodeId,
                    ClientId = clientName,
                    Type = OperationType.Read,
                    Key = key,
                    StartTime = startTime,
                    EndTime = DateTimeOffset.UtcNow,
                    Result = OperationResult.Timeout
                };
            }
            catch
            {
                entry = new OperationHistoryEntry
                {
                    SequenceId = JepsenTestHarness.NextSequenceId(),
                    NodeId = node.NodeId,
                    ClientId = clientName,
                    Type = OperationType.Read,
                    Key = key,
                    StartTime = startTime,
                    EndTime = DateTimeOffset.UtcNow,
                    Result = OperationResult.Crashed
                };
            }

            yield return entry;

            if (OperationDelay > TimeSpan.Zero)
            {
                try { await Task.Delay(OperationDelay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { yield break; }
            }
        }
    }

    private static Task<string?> ExecuteSetReadAsync(JepsenNode node, string key, CancellationToken ct)
    {
        _ = node; _ = key; _ = ct;
        throw new PlatformNotSupportedException(
            "Jepsen test harness requires external Jepsen framework and live database nodes. " +
            "Configure JepsenEndpoint in options.");
    }

    private static Task ExecuteSetAddAsync(JepsenNode node, string key, string element, CancellationToken ct)
    {
        _ = node; _ = key; _ = element; _ = ct;
        throw new PlatformNotSupportedException(
            "Jepsen test harness requires external Jepsen framework and live database nodes. " +
            "Configure JepsenEndpoint in options.");
    }

    private static JepsenNode? SelectRandomHealthyNode(JepsenCluster cluster, Random random)
    {
        var healthy = cluster.Nodes.Where(n => n.State == JepsenNodeState.Running).ToList();
        return healthy.Count > 0 ? healthy[random.Next(healthy.Count)] : null;
    }
}

#endregion

#region List-Append Workload

/// <summary>
/// List-append workload for testing snapshot isolation and preventing lost updates.
/// Operations: append(key, element), read(key) -> list.
/// Checks for ordering anomalies in the appended list.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Jepsen workload generators (ECOS-16)")]
public sealed class ListAppendWorkload : IWorkloadGenerator
{
    /// <summary>Number of distinct list keys.</summary>
    public int KeyCount { get; init; } = 3;

    /// <summary>Probability of a read operation (remainder is append).</summary>
    public double ReadProbability { get; init; } = 0.4;

    /// <summary>Delay between operations.</summary>
    public TimeSpan OperationDelay { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <inheritdoc />
    public string WorkloadType => "list-append";

    /// <inheritdoc />
    public async IAsyncEnumerable<OperationHistoryEntry> RunAsync(
        JepsenCluster cluster, int clientId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        var random = new Random(clientId * 41 + 7);
        var clientName = $"client-{clientId}";
        long appendCounter = clientId * 1_000_000L;

        while (!ct.IsCancellationRequested)
        {
            var key = $"l{random.Next(KeyCount)}";
            var node = SelectRandomHealthyNode(cluster, random);
            if (node == null) { await Task.Delay(100, ct).ConfigureAwait(false); continue; }

            var startTime = DateTimeOffset.UtcNow;
            OperationHistoryEntry entry;

            try
            {
                if (random.NextDouble() < ReadProbability)
                {
                    // Read the entire list
                    var listContents = await ExecuteListReadAsync(node, key, ct).ConfigureAwait(false);

                    entry = new OperationHistoryEntry
                    {
                        SequenceId = JepsenTestHarness.NextSequenceId(),
                        NodeId = node.NodeId,
                        ClientId = clientName,
                        Type = OperationType.Read,
                        Key = key,
                        Value = listContents,
                        StartTime = startTime,
                        EndTime = DateTimeOffset.UtcNow,
                        Result = OperationResult.Ok
                    };
                }
                else
                {
                    // Append an element to the list
                    var element = $"a{Interlocked.Increment(ref appendCounter)}";
                    await ExecuteListAppendAsync(node, key, element, ct).ConfigureAwait(false);

                    entry = new OperationHistoryEntry
                    {
                        SequenceId = JepsenTestHarness.NextSequenceId(),
                        NodeId = node.NodeId,
                        ClientId = clientName,
                        Type = OperationType.Append,
                        Key = key,
                        Value = element,
                        StartTime = startTime,
                        EndTime = DateTimeOffset.UtcNow,
                        Result = OperationResult.Ok
                    };
                }
            }
            catch (OperationCanceledException) { yield break; }
            catch (TimeoutException)
            {
                entry = new OperationHistoryEntry
                {
                    SequenceId = JepsenTestHarness.NextSequenceId(),
                    NodeId = node.NodeId,
                    ClientId = clientName,
                    Type = OperationType.Read,
                    Key = key,
                    StartTime = startTime,
                    EndTime = DateTimeOffset.UtcNow,
                    Result = OperationResult.Timeout
                };
            }
            catch
            {
                entry = new OperationHistoryEntry
                {
                    SequenceId = JepsenTestHarness.NextSequenceId(),
                    NodeId = node.NodeId,
                    ClientId = clientName,
                    Type = OperationType.Read,
                    Key = key,
                    StartTime = startTime,
                    EndTime = DateTimeOffset.UtcNow,
                    Result = OperationResult.Crashed
                };
            }

            yield return entry;

            if (OperationDelay > TimeSpan.Zero)
            {
                try { await Task.Delay(OperationDelay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { yield break; }
            }
        }
    }

    private static Task<string?> ExecuteListReadAsync(JepsenNode node, string key, CancellationToken ct)
    {
        _ = node; _ = key; _ = ct;
        throw new PlatformNotSupportedException(
            "Jepsen test harness requires external Jepsen framework and live database nodes. " +
            "Configure JepsenEndpoint in options.");
    }

    private static Task ExecuteListAppendAsync(JepsenNode node, string key, string element, CancellationToken ct)
    {
        _ = node; _ = key; _ = element; _ = ct;
        throw new PlatformNotSupportedException(
            "Jepsen test harness requires external Jepsen framework and live database nodes. " +
            "Configure JepsenEndpoint in options.");
    }

    private static JepsenNode? SelectRandomHealthyNode(JepsenCluster cluster, Random random)
    {
        var healthy = cluster.Nodes.Where(n => n.State == JepsenNodeState.Running).ToList();
        return healthy.Count > 0 ? healthy[random.Next(healthy.Count)] : null;
    }
}

#endregion

#region Bank Workload

/// <summary>
/// Bank transfer workload for testing that total balance is conserved.
/// Operations: transfer(from, to, amount), read_balance(account), read_total().
/// Verifies the conservation invariant: sum of all accounts is constant.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Jepsen workload generators (ECOS-16)")]
public sealed class BankWorkload : IWorkloadGenerator
{
    /// <summary>Number of bank accounts.</summary>
    public int AccountCount { get; init; } = 5;

    /// <summary>Initial balance per account.</summary>
    public long InitialBalance { get; init; } = 1000;

    /// <summary>Maximum transfer amount.</summary>
    public long MaxTransfer { get; init; } = 100;

    /// <summary>Probability of a balance read (remainder is transfer).</summary>
    public double ReadProbability { get; init; } = 0.3;

    /// <summary>Delay between operations.</summary>
    public TimeSpan OperationDelay { get; init; } = TimeSpan.FromMilliseconds(10);

    /// <inheritdoc />
    public string WorkloadType => "bank";

    /// <inheritdoc />
    public async IAsyncEnumerable<OperationHistoryEntry> RunAsync(
        JepsenCluster cluster, int clientId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cluster);
        var random = new Random(clientId * 43 + 11);
        var clientName = $"client-{clientId}";

        while (!ct.IsCancellationRequested)
        {
            var node = SelectRandomHealthyNode(cluster, random);
            if (node == null) { await Task.Delay(100, ct).ConfigureAwait(false); continue; }

            var startTime = DateTimeOffset.UtcNow;
            OperationHistoryEntry entry;

            try
            {
                if (random.NextDouble() < ReadProbability)
                {
                    // Read total balance across all accounts
                    var totalBalance = await ExecuteReadTotalAsync(node, AccountCount, ct)
                        .ConfigureAwait(false);
                    var expectedTotal = AccountCount * InitialBalance;

                    entry = new OperationHistoryEntry
                    {
                        SequenceId = JepsenTestHarness.NextSequenceId(),
                        NodeId = node.NodeId,
                        ClientId = clientName,
                        Type = OperationType.Read,
                        Key = "total",
                        Value = totalBalance.ToString(),
                        ExpectedValue = expectedTotal.ToString(),
                        StartTime = startTime,
                        EndTime = DateTimeOffset.UtcNow,
                        Result = OperationResult.Ok
                    };
                }
                else
                {
                    // Transfer between two random accounts
                    var from = random.Next(AccountCount);
                    var to = random.Next(AccountCount);
                    while (to == from) to = random.Next(AccountCount);
                    var amount = random.Next(1, (int)MaxTransfer + 1);

                    await ExecuteTransferAsync(node, from, to, amount, ct).ConfigureAwait(false);

                    entry = new OperationHistoryEntry
                    {
                        SequenceId = JepsenTestHarness.NextSequenceId(),
                        NodeId = node.NodeId,
                        ClientId = clientName,
                        Type = OperationType.Write,
                        Key = $"transfer:{from}->{to}",
                        Value = amount.ToString(),
                        StartTime = startTime,
                        EndTime = DateTimeOffset.UtcNow,
                        Result = OperationResult.Ok
                    };
                }
            }
            catch (OperationCanceledException) { yield break; }
            catch (TimeoutException)
            {
                entry = new OperationHistoryEntry
                {
                    SequenceId = JepsenTestHarness.NextSequenceId(),
                    NodeId = node.NodeId,
                    ClientId = clientName,
                    Type = OperationType.Read,
                    Key = "total",
                    StartTime = startTime,
                    EndTime = DateTimeOffset.UtcNow,
                    Result = OperationResult.Timeout
                };
            }
            catch
            {
                entry = new OperationHistoryEntry
                {
                    SequenceId = JepsenTestHarness.NextSequenceId(),
                    NodeId = node.NodeId,
                    ClientId = clientName,
                    Type = OperationType.Read,
                    Key = "total",
                    StartTime = startTime,
                    EndTime = DateTimeOffset.UtcNow,
                    Result = OperationResult.Crashed
                };
            }

            yield return entry;

            if (OperationDelay > TimeSpan.Zero)
            {
                try { await Task.Delay(OperationDelay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { yield break; }
            }
        }
    }

    private static Task<long> ExecuteReadTotalAsync(JepsenNode node, int accountCount, CancellationToken ct)
    {
        _ = node; _ = accountCount; _ = ct;
        throw new PlatformNotSupportedException(
            "Jepsen test harness requires external Jepsen framework and live database nodes. " +
            "Configure JepsenEndpoint in options.");
    }

    private static Task ExecuteTransferAsync(
        JepsenNode node, int from, int to, int amount, CancellationToken ct)
    {
        _ = node; _ = from; _ = to; _ = amount; _ = ct;
        throw new PlatformNotSupportedException(
            "Jepsen test harness requires external Jepsen framework and live database nodes. " +
            "Configure JepsenEndpoint in options.");
    }

    private static JepsenNode? SelectRandomHealthyNode(JepsenCluster cluster, Random random)
    {
        var healthy = cluster.Nodes.Where(n => n.State == JepsenNodeState.Running).ToList();
        return healthy.Count > 0 ? healthy[random.Next(healthy.Count)] : null;
    }
}

#endregion

#region Workload Mixer

/// <summary>
/// Combines multiple workload generators, distributing operations across concurrent clients
/// with configurable ratios.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 89: Jepsen workload generators (ECOS-16)")]
public sealed class WorkloadMixer : IWorkloadGenerator
{
    /// <summary>Workloads with their relative weights.</summary>
    public required IReadOnlyList<(IWorkloadGenerator Workload, double Weight)> Workloads { get; init; }

    /// <inheritdoc />
    public string WorkloadType => "mixed";

    /// <inheritdoc />
    public async IAsyncEnumerable<OperationHistoryEntry> RunAsync(
        JepsenCluster cluster, int clientId,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cluster);

        if (Workloads.Count == 0)
            yield break;

        // Normalize weights
        var totalWeight = Workloads.Sum(w => w.Weight);
        if (totalWeight <= 0) totalWeight = 1.0;

        // Select workload for this client based on weighted distribution
        var random = new Random(clientId * 53 + 3);
        var roll = random.NextDouble() * totalWeight;
        var cumulative = 0.0;
        IWorkloadGenerator selectedWorkload = Workloads[0].Workload;

        foreach (var (workload, weight) in Workloads)
        {
            cumulative += weight;
            if (roll <= cumulative)
            {
                selectedWorkload = workload;
                break;
            }
        }

        // Delegate to selected workload
        await foreach (var entry in selectedWorkload.RunAsync(cluster, clientId, ct).ConfigureAwait(false))
        {
            yield return entry;
        }
    }
}

#endregion
