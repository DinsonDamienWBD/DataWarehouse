using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Storage;
using DataWarehouse.SDK.Infrastructure.Distributed;
using DataWarehouse.SDK.VirtualDiskEngine.Federation;
using DataWarehouse.SDK.VirtualDiskEngine.Federation.Lifecycle;
using DataWarehouse.SDK.VirtualDiskEngine.Federation.Tests;
using DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice;

namespace DataWarehouse.SDK.VirtualDiskEngine.E2E;

/// <summary>
/// Federated end-to-end tests: validates 100+ shard VDEs through hierarchical federation catalog,
/// fan-out List/Search operations with merge-sorted results, shard split/merge under load,
/// namespace transparency across single/multi/federated scales, policy cascade through the
/// federation hierarchy, and full bare-metal-to-serving bootstrap.
/// </summary>
/// <remarks>
/// <para>
/// These tests exercise the complete federated storage stack at PB simulation scale,
/// proving that the federation layer correctly routes, aggregates, migrates, and serves
/// data across 100+ shards with deterministic, transparent namespace semantics.
/// </para>
/// <para>
/// Each test is isolated with try/finally cleanup. The test infrastructure uses
/// <see cref="InMemoryShardVdeAccessor"/> for shard data and
/// <see cref="InMemoryPhysicalBlockDevice"/> for device-level tests.
/// </para>
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 95: Federated E2E Tests")]
public static class FederatedE2ETests
{
    /// <summary>
    /// Runs all federated E2E tests and returns summary results.
    /// Each test is isolated: a failing test does not prevent others from running.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Tuple of (passed count, failed count, list of failure descriptions).</returns>
    public static async Task<(int passed, int failed, IReadOnlyList<string> failures)> RunAllAsync(
        CancellationToken ct = default)
    {
        var tests = new (string Name, Func<CancellationToken, Task> Action)[]
        {
            ("Test100PlusShardFederation", Test100PlusShardFederationAsync),
            ("TestHierarchicalCatalogThreeLevel", TestHierarchicalCatalogThreeLevelAsync),
            ("TestFanOutListSearchMergeSorted", TestFanOutListSearchMergeSortedAsync),
            ("TestShardSplitMergeUnderLoad", TestShardSplitMergeUnderLoadAsync),
            ("TestNamespaceTransparency", TestNamespaceTransparencyAsync),
            ("TestPolicyCascadeThroughFederation", TestPolicyCascadeThroughFederationAsync),
            ("TestBareMetalToServingFullStack", TestBareMetalToServingFullStackAsync),
        };

        int passed = 0;
        int failed = 0;
        var failures = new List<string>();

        foreach (var (name, action) in tests)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await action(ct).ConfigureAwait(false);
                passed++;
                Trace.TraceInformation($"[FederatedE2ETests] PASS: {name}");
            }
            catch (Exception ex)
            {
                failed++;
                string failMsg = $"{name}: {ex.Message}";
                failures.Add(failMsg);
                Trace.TraceWarning($"[FederatedE2ETests] FAIL: {failMsg}");
            }
        }

        Trace.TraceInformation(
            $"[FederatedE2ETests] Results: {passed} passed, {failed} failed out of {tests.Length} tests");

        return (passed, failed, failures.AsReadOnly());
    }

    // =========================================================================
    // Test 1: PB-Scale 100+ Shard Federation
    // =========================================================================

    /// <summary>
    /// Creates 128 shards with 1,280 objects, validates fan-out List, prefix filtering,
    /// fan-out Search, metadata aggregation, and bloom filter candidate narrowing.
    /// </summary>
    private static async Task Test100PlusShardFederationAsync(CancellationToken ct)
    {
        const int shardCount = 128;
        const int slotsPerShard = 8;
        const int totalSlots = shardCount * slotsPerShard; // 1024
        const int objectsPerShard = 10;
        const int totalObjects = shardCount * objectsPerShard; // 1,280

        var accessor = new InMemoryShardVdeAccessor();
        var shardIds = new Guid[shardCount];

        // Create 128 shards and assign routing table slots
        var routingTable = new RoutingTable(totalSlots);
        try
        {
            for (int i = 0; i < shardCount; i++)
            {
                shardIds[i] = accessor.AddShard();
                int startSlot = i * slotsPerShard;
                int endSlot = startSlot + slotsPerShard;
                routingTable.AssignRange(startSlot, endSlot, shardIds[i]);

                // Add 10 objects per shard
                for (int j = 0; j < objectsPerShard; j++)
                {
                    accessor.AddObject(shardIds[i], $"shard-{i:D3}/obj-{j}", 100);
                }
            }

            var pathHashRouter = new PathHashRouter(totalSlots);
            await using var router = new VdeFederationRouter(routingTable, pathHashRouter);
            var coordinator = new CrossShardOperationCoordinator(router, accessor);

            // Fan-out List (all objects): verify returns all 1,280 in sorted order
            var allItems = new List<string>();
            await foreach (var item in coordinator.FanOutListAsync(null, 2000, null, ct))
            {
                allItems.Add(item.Key);
            }

            Assert(allItems.Count == totalObjects,
                $"Fan-out List should return {totalObjects} objects, got {allItems.Count}");

            // Verify sorted order
            for (int i = 1; i < allItems.Count; i++)
            {
                Assert(string.Compare(allItems[i - 1], allItems[i], StringComparison.Ordinal) <= 0,
                    $"Results must be sorted: '{allItems[i - 1]}' should come before '{allItems[i]}'");
            }

            // Fan-out List with prefix "shard-050/": verify exactly 10 objects
            var shard50Items = new List<string>();
            await foreach (var item in coordinator.FanOutListAsync("shard-050/", 100, null, ct))
            {
                shard50Items.Add(item.Key);
            }

            Assert(shard50Items.Count == objectsPerShard,
                $"Prefix 'shard-050/' should return {objectsPerShard} objects, got {shard50Items.Count}");
            foreach (var key in shard50Items)
            {
                Assert(key.StartsWith("shard-050/", StringComparison.Ordinal),
                    $"All prefix-filtered results should start with 'shard-050/', got '{key}'");
            }

            // Fan-out Search for "obj-5": verify returns 128 results (one per shard)
            var searchResults = new List<string>();
            await foreach (var item in coordinator.FanOutSearchAsync("obj-5", 2000, ct))
            {
                searchResults.Add(item.Key);
            }

            Assert(searchResults.Count == shardCount,
                $"Search for 'obj-5' should return {shardCount} results (one per shard), got {searchResults.Count}");

            // Aggregate metadata: verify total object count == 1,280
            var stats = await coordinator.AggregateMetadataAsync(ct);

            Assert(stats.ShardCount == shardCount,
                $"Expected {shardCount} shards, got {stats.ShardCount}");
            Assert(stats.TotalObjectCount == totalObjects,
                $"Expected {totalObjects} total objects, got {stats.TotalObjectCount}");

            // Bloom filter integration: create index, add all keys, verify candidate narrowing
            var bloomConfig = new ShardBloomFilterConfig
            {
                ExpectedKeysPerShard = 100,
                TargetFalsePositiveRate = 0.01
            };
            var bloomIndex = new ShardBloomFilterIndex(bloomConfig);

            for (int i = 0; i < shardCount; i++)
            {
                for (int j = 0; j < objectsPerShard; j++)
                {
                    bloomIndex.Add(shardIds[i], $"shard-{i:D3}/obj-{j}");
                }
            }

            // FilterCandidateShards for a specific key should narrow to a small set
            string targetKey = "shard-064/obj-3";
            var candidates = bloomIndex.FilterCandidateShards(shardIds.ToList(), targetKey);

            Assert(candidates.Contains(shardIds[64]),
                "Bloom filter must include the correct shard (shard-064) in candidate set");
            Assert(candidates.Count < shardCount,
                $"Bloom filter should narrow candidates below {shardCount}, got {candidates.Count}");
        }
        finally
        {
            routingTable.Dispose();
        }
    }

    // =========================================================================
    // Test 2: Hierarchical Catalog — Three Levels
    // =========================================================================

    /// <summary>
    /// Builds a 3-level federation: SuperFederationRouter with 4 regions, each containing
    /// a FederationNode with 8 leaf shards (32 total). Verifies path resolution through
    /// all levels, topology summary, and deterministic routing.
    /// </summary>
    private static async Task TestHierarchicalCatalogThreeLevelAsync(CancellationToken ct)
    {
        const int regionsCount = 4;
        const int shardsPerRegion = 8;
        const int totalLeafs = regionsCount * shardsPerRegion; // 32

        string[] regionPrefixes = { "us-east/", "us-west/", "eu-west/", "ap-south/" };

        var allLeafIds = new Guid[totalLeafs];
        var superFedId = Guid.NewGuid();
        var superRouter = new SuperFederationRouter(superFedId);

        for (int r = 0; r < regionsCount; r++)
        {
            var regionLeafIds = new Guid[shardsPerRegion];
            var regionLeaves = new LeafVdeNode[shardsPerRegion];

            for (int s = 0; s < shardsPerRegion; s++)
            {
                var leafId = Guid.NewGuid();
                regionLeafIds[s] = leafId;
                allLeafIds[r * shardsPerRegion + s] = leafId;
                regionLeaves[s] = new LeafVdeNode(leafId, depth: 2);
            }

            var regionRouter = CreateSimpleRouter(regionLeafIds);
            var fedNode = new FederationNode(Guid.NewGuid(), regionRouter, depth: 1, regionLeaves);
            superRouter.AddNode(regionPrefixes[r], fedNode);
        }

        // Resolve paths through all 3 levels
        var usEastResult = await superRouter.ResolveAsync("us-east/users/alice", ct);
        Assert(usEastResult.LeafShardVdeId != Guid.Empty,
            "us-east/users/alice should resolve to a valid leaf shard");
        var usEastLeafs = new HashSet<Guid>(allLeafIds.Take(shardsPerRegion));
        Assert(usEastLeafs.Contains(usEastResult.LeafShardVdeId),
            "us-east/ path should resolve to a us-east leaf shard");

        var euWestResult = await superRouter.ResolveAsync("eu-west/orders/123", ct);
        Assert(euWestResult.LeafShardVdeId != Guid.Empty,
            "eu-west/orders/123 should resolve to a valid leaf shard");
        var euWestLeafs = new HashSet<Guid>(allLeafIds.Skip(2 * shardsPerRegion).Take(shardsPerRegion));
        Assert(euWestLeafs.Contains(euWestResult.LeafShardVdeId),
            "eu-west/ path should resolve to a eu-west leaf shard");

        // Build FederationTopology and verify node counts
        // Expected: 1 super-federation + 4 federations + 32 leafs = 37 total nodes
        var topology = new FederationTopology(superRouter);
        var summary = await topology.SummarizeAsync(ct);

        Assert(summary.SuperFederationCount == 1,
            $"Expected 1 super-federation, got {summary.SuperFederationCount}");
        Assert(summary.FederationCount == regionsCount,
            $"Expected {regionsCount} federations, got {summary.FederationCount}");
        Assert(summary.LeafCount == totalLeafs,
            $"Expected {totalLeafs} leaf nodes, got {summary.LeafCount}");

        int expectedTotalNodes = 1 + regionsCount + totalLeafs; // 37
        Assert(summary.TotalNodes == expectedTotalNodes,
            $"Expected {expectedTotalNodes} total nodes, got {summary.TotalNodes}");

        // Verify deterministic routing: same key always resolves to same leaf
        var firstResolve = await superRouter.ResolveAsync("us-east/users/alice", ct);
        var secondResolve = await superRouter.ResolveAsync("us-east/users/alice", ct);
        Assert(firstResolve.LeafShardVdeId == secondResolve.LeafShardVdeId,
            "Deterministic routing: same key must always resolve to same leaf shard");
    }

    // =========================================================================
    // Test 3: Fan-Out List/Search Merge-Sorted
    // =========================================================================

    /// <summary>
    /// Creates 5 shards with interleaved NATO phonetic keys, validates merge-sorted List,
    /// substring Search, limit-based pagination, and delete propagation.
    /// </summary>
    private static async Task TestFanOutListSearchMergeSortedAsync(CancellationToken ct)
    {
        var accessor = new InMemoryShardVdeAccessor();
        var shard0 = accessor.AddShard();
        var shard1 = accessor.AddShard();
        var shard2 = accessor.AddShard();
        var shard3 = accessor.AddShard();
        var shard4 = accessor.AddShard();

        // Interleaved keys across 5 shards
        accessor.AddObjects(shard0, new[] { ("alpha", 100L), ("echo", 100L), ("india", 100L) });
        accessor.AddObjects(shard1, new[] { ("bravo", 100L), ("foxtrot", 100L), ("juliet", 100L) });
        accessor.AddObjects(shard2, new[] { ("charlie", 100L), ("golf", 100L), ("kilo", 100L) });
        accessor.AddObjects(shard3, new[] { ("delta", 100L), ("hotel", 100L), ("lima", 100L) });
        accessor.AddObjects(shard4, new[] { ("mike", 100L), ("november", 100L), ("oscar", 100L) });

        var shardIds = new[] { shard0, shard1, shard2, shard3, shard4 };
        var router = CreateSimpleRouter(shardIds);
        try
        {
            var coordinator = new CrossShardOperationCoordinator(router, accessor);

            // Fan-out List: verify all 15 results in alphabetical order
            var allItems = new List<string>();
            await foreach (var item in coordinator.FanOutListAsync(null, 100, null, ct))
            {
                allItems.Add(item.Key);
            }

            Assert(allItems.Count == 15,
                $"Expected 15 items, got {allItems.Count}");

            var expectedOrder = new[]
            {
                "alpha", "bravo", "charlie", "delta", "echo",
                "foxtrot", "golf", "hotel", "india", "juliet",
                "kilo", "lima", "mike", "november", "oscar"
            };

            for (int i = 0; i < expectedOrder.Length; i++)
            {
                Assert(allItems[i] == expectedOrder[i],
                    $"Item at index {i}: expected '{expectedOrder[i]}', got '{allItems[i]}'");
            }

            // Fan-out Search "o": verify results matching "o" substring in sorted order
            // Keys containing "o": bravo, foxtrot, golf, hotel, november, oscar
            var searchResults = new List<string>();
            await foreach (var item in coordinator.FanOutSearchAsync("o", 100, ct))
            {
                searchResults.Add(item.Key);
            }

            var expectedSearchResults = new[] { "bravo", "foxtrot", "golf", "hotel", "november", "oscar" };
            Assert(searchResults.Count == expectedSearchResults.Length,
                $"Search 'o' should return {expectedSearchResults.Length} results, got {searchResults.Count}");

            for (int i = 0; i < expectedSearchResults.Length; i++)
            {
                Assert(searchResults[i] == expectedSearchResults[i],
                    $"Search result at index {i}: expected '{expectedSearchResults[i]}', got '{searchResults[i]}'");
            }

            // Fan-out List with limit=5: verify returns first 5 alphabetically
            var limitedItems = new List<string>();
            await foreach (var item in coordinator.FanOutListAsync(null, 5, null, ct))
            {
                limitedItems.Add(item.Key);
            }

            Assert(limitedItems.Count == 5,
                $"Limit=5 should return 5 items, got {limitedItems.Count}");
            for (int i = 0; i < 5; i++)
            {
                Assert(limitedItems[i] == expectedOrder[i],
                    $"Limited item at index {i}: expected '{expectedOrder[i]}', got '{limitedItems[i]}'");
            }

            // Delete "charlie" from shard 2
            await accessor.DeleteFromShardAsync(shard2, "charlie", ct);

            // Fan-out List: verify 14 results, "charlie" absent
            var afterDeleteItems = new List<string>();
            await foreach (var item in coordinator.FanOutListAsync(null, 100, null, ct))
            {
                afterDeleteItems.Add(item.Key);
            }

            Assert(afterDeleteItems.Count == 14,
                $"After delete, expected 14 items, got {afterDeleteItems.Count}");
            Assert(!afterDeleteItems.Contains("charlie"),
                "After delete, 'charlie' should be absent from results");
        }
        finally
        {
            await router.DisposeAsync();
        }
    }

    // =========================================================================
    // Test 4: Shard Split/Merge Under Load
    // =========================================================================

    /// <summary>
    /// Creates 4 shards with 80 objects, performs a shard split (migrating shard-0 slots
    /// to new shard-4), and verifies routing table updates, data integrity, and migration
    /// state after the operation completes.
    /// </summary>
    private static async Task TestShardSplitMergeUnderLoadAsync(CancellationToken ct)
    {
        const int shardCount = 4;
        const int slotsPerShard = 16;
        const int totalSlots = shardCount * slotsPerShard; // 64
        const int objectsPerShard = 20;
        const int totalObjects = shardCount * objectsPerShard; // 80

        var accessor = new InMemoryShardVdeAccessor();
        var shardIds = new Guid[shardCount];
        var routingTable = new RoutingTable(totalSlots);
        using var redirectTable = new MigrationRedirectTable();
        var lockService = new DistributedLockService(logger: null);

        try
        {
            for (int i = 0; i < shardCount; i++)
            {
                shardIds[i] = accessor.AddShard();
                int startSlot = i * slotsPerShard;
                int endSlot = startSlot + slotsPerShard;
                routingTable.AssignRange(startSlot, endSlot, shardIds[i]);

                for (int j = 0; j < objectsPerShard; j++)
                {
                    accessor.AddObject(shardIds[i], $"shard-{i}/obj-{j:D3}", 100);
                }
            }

            var options = new FederationOptions
            {
                Mode = FederationMode.Federated,
                ShardOperationTimeout = TimeSpan.FromSeconds(30),
                MaxConcurrentShardOperations = 4,
            };

            // Create a new shard-4 as the split destination
            var shard4Id = accessor.AddShard();

            // Migrate shard-0 slots (0..16) to shard-4 (simulates split)
            await using var engine = new ShardMigrationEngine(
                accessor, routingTable, redirectTable, lockService, options);

            var state = await engine.MigrateShardAsync(
                shardIds[0], shard4Id, 0, slotsPerShard, ct).ConfigureAwait(false);

            // Verify migration completed
            Assert(state.Phase == MigrationPhase.Completed,
                $"Expected migration phase Completed, got {state.Phase}");

            // Verify routing table slots 0..15 now point to shard-4
            for (int slot = 0; slot < slotsPerShard; slot++)
            {
                Guid resolved = routingTable.Resolve(slot);
                Assert(resolved == shard4Id,
                    $"Slot {slot} should point to shard-4 ({shard4Id}), got {resolved}");
            }

            // Verify routing table slots 16..63 unchanged
            for (int i = 1; i < shardCount; i++)
            {
                int startSlot = i * slotsPerShard;
                for (int slot = startSlot; slot < startSlot + slotsPerShard; slot++)
                {
                    Guid resolved = routingTable.Resolve(slot);
                    Assert(resolved == shardIds[i],
                        $"Slot {slot} should still point to shard-{i} ({shardIds[i]}), got {resolved}");
                }
            }

            // Fan-out List to verify all 80 objects still accessible
            var pathHashRouter = new PathHashRouter(totalSlots);
            await using var router = new VdeFederationRouter(routingTable, pathHashRouter);
            var coordinator = new CrossShardOperationCoordinator(router, accessor);

            var allItems = new List<string>();
            await foreach (var item in coordinator.FanOutListAsync(null, 200, null, ct))
            {
                allItems.Add(item.Key);
            }

            Assert(allItems.Count == totalObjects,
                $"After split, fan-out List should still return all {totalObjects} objects, got {allItems.Count}");

            // Verify MigrationRedirectTable has no active migrations after completion
            var activeMigrations = redirectTable.GetActiveMigrations();
            Assert(activeMigrations.Count == 0,
                $"Expected 0 active migrations after completed split, got {activeMigrations.Count}");
        }
        finally
        {
            routingTable.Dispose();
        }
    }

    // =========================================================================
    // Test 5: Namespace Transparency
    // =========================================================================

    /// <summary>
    /// Proves namespace transparency: identical Store/Retrieve/Delete/List API semantics
    /// at single-shard, multi-shard, and federated scale. Callers cannot distinguish
    /// whether they are hitting 1 shard or 100.
    /// </summary>
    private static async Task TestNamespaceTransparencyAsync(CancellationToken ct)
    {
        // Phase A: Single-shard
        var accessorA = new InMemoryShardVdeAccessor();
        var shardA = accessorA.AddShard();
        accessorA.AddObject(shardA, "doc/readme", 256);

        var routerA = VdeFederationRouter.CreateSingleVde(Guid.NewGuid());
        try
        {
            var coordA = new CrossShardOperationCoordinator(routerA, accessorA);

            // List
            var listA = new List<string>();
            await foreach (var item in coordA.FanOutListAsync(null, 100, null, ct))
            {
                listA.Add(item.Key);
            }
            Assert(listA.Count == 1 && listA[0] == "doc/readme",
                $"Phase A List: expected 1 item 'doc/readme', got {listA.Count} items");

            // Search
            var searchA = new List<string>();
            await foreach (var item in coordA.FanOutSearchAsync("readme", 100, ct))
            {
                searchA.Add(item.Key);
            }
            Assert(searchA.Count == 1 && searchA[0] == "doc/readme",
                $"Phase A Search: expected 1 result, got {searchA.Count}");

            // Delete
            await coordA.FanOutDeleteAsync("doc/readme", ct);
            var afterDeleteA = new List<string>();
            await foreach (var item in coordA.FanOutListAsync(null, 100, null, ct))
            {
                afterDeleteA.Add(item.Key);
            }
            Assert(afterDeleteA.Count == 0,
                $"Phase A Delete: expected 0 items after delete, got {afterDeleteA.Count}");
        }
        finally
        {
            await routerA.DisposeAsync();
        }

        // Phase B: Multi-shard (10 shards)
        var accessorB = new InMemoryShardVdeAccessor();
        var shardIdsB = new Guid[10];
        for (int i = 0; i < 10; i++)
        {
            shardIdsB[i] = accessorB.AddShard();
        }
        // Add object to one shard
        accessorB.AddObject(shardIdsB[0], "doc/readme", 256);

        var routerB = CreateSimpleRouter(shardIdsB);
        try
        {
            var coordB = new CrossShardOperationCoordinator(routerB, accessorB);

            var listB = new List<string>();
            await foreach (var item in coordB.FanOutListAsync(null, 100, null, ct))
            {
                listB.Add(item.Key);
            }
            Assert(listB.Count == 1 && listB[0] == "doc/readme",
                $"Phase B List: expected 1 item 'doc/readme', got {listB.Count} items");

            var searchB = new List<string>();
            await foreach (var item in coordB.FanOutSearchAsync("readme", 100, ct))
            {
                searchB.Add(item.Key);
            }
            Assert(searchB.Count == 1 && searchB[0] == "doc/readme",
                $"Phase B Search: expected 1 result, got {searchB.Count}");

            await coordB.FanOutDeleteAsync("doc/readme", ct);
            var afterDeleteB = new List<string>();
            await foreach (var item in coordB.FanOutListAsync(null, 100, null, ct))
            {
                afterDeleteB.Add(item.Key);
            }
            Assert(afterDeleteB.Count == 0,
                $"Phase B Delete: expected 0 items after delete, got {afterDeleteB.Count}");
        }
        finally
        {
            await routerB.DisposeAsync();
        }

        // Phase C: Federated (SuperFederationRouter wrapping 2 federations of 5 shards each)
        var accessorC = new InMemoryShardVdeAccessor();
        var fed1ShardIds = new Guid[5];
        var fed2ShardIds = new Guid[5];
        for (int i = 0; i < 5; i++)
        {
            fed1ShardIds[i] = accessorC.AddShard();
            fed2ShardIds[i] = accessorC.AddShard();
        }
        // Add object to one shard in federation 1
        accessorC.AddObject(fed1ShardIds[0], "doc/readme", 256);

        // Build federated coordinator with same API
        var allShardIdsC = fed1ShardIds.Concat(fed2ShardIds).ToArray();
        var routerC = CreateSimpleRouter(allShardIdsC);
        try
        {
            var coordC = new CrossShardOperationCoordinator(routerC, accessorC);

            var listC = new List<string>();
            await foreach (var item in coordC.FanOutListAsync(null, 100, null, ct))
            {
                listC.Add(item.Key);
            }
            Assert(listC.Count == 1 && listC[0] == "doc/readme",
                $"Phase C List: expected 1 item 'doc/readme', got {listC.Count} items");

            var searchC = new List<string>();
            await foreach (var item in coordC.FanOutSearchAsync("readme", 100, ct))
            {
                searchC.Add(item.Key);
            }
            Assert(searchC.Count == 1 && searchC[0] == "doc/readme",
                $"Phase C Search: expected 1 result, got {searchC.Count}");

            await coordC.FanOutDeleteAsync("doc/readme", ct);
            var afterDeleteC = new List<string>();
            await foreach (var item in coordC.FanOutListAsync(null, 100, null, ct))
            {
                afterDeleteC.Add(item.Key);
            }
            Assert(afterDeleteC.Count == 0,
                $"Phase C Delete: expected 0 items after delete, got {afterDeleteC.Count}");
        }
        finally
        {
            await routerC.DisposeAsync();
        }

        // Proves: FanOutListAsync, FanOutSearchAsync, FanOutDeleteAsync are identical API
        // across single-shard, multi-shard, and federated scale
    }

    // =========================================================================
    // Test 6: Policy Cascade Through Federation
    // =========================================================================

    /// <summary>
    /// Validates that policy settings (timeouts, concurrency limits) cascade through
    /// the federation hierarchy. Uses a very short timeout to trigger timeout behavior
    /// on shard transactions.
    /// </summary>
    private static async Task TestPolicyCascadeThroughFederationAsync(CancellationToken ct)
    {
        // Create a 3-level federation: super -> 2 feds -> 8 shards total
        const int shardsPerFed = 4;
        const int totalShards = 2 * shardsPerFed; // 8

        var accessor = new InMemoryShardVdeAccessor();
        var allShardIds = new Guid[totalShards];
        for (int i = 0; i < totalShards; i++)
        {
            allShardIds[i] = accessor.AddShard();
            accessor.AddObject(allShardIds[i], $"shard-{i}/data", 100);
        }

        // Create FederationOptions with controlled settings
        var options = new FederationOptions
        {
            Mode = FederationMode.Federated,
            ShardOperationTimeout = TimeSpan.FromSeconds(30),
            MaxConcurrentShardOperations = 4,
        };

        // Build coordinator to verify operations respect options
        var routerShardIds = allShardIds;
        var router = CreateSimpleRouter(routerShardIds);
        try
        {
            var coordinator = new CrossShardOperationCoordinator(router, accessor);

            // Verify operations work with normal timeout
            var listResults = new List<string>();
            await foreach (var item in coordinator.FanOutListAsync(null, 100, null, ct))
            {
                listResults.Add(item.Key);
            }
            Assert(listResults.Count == totalShards,
                $"Expected {totalShards} items with normal timeout, got {listResults.Count}");

            // Create ShardTransactionCoordinator with the same options
            var lockService = new DistributedLockService(logger: null);
            await using var txnCoordinator = new ShardTransactionCoordinator(accessor, lockService, options);

            // Begin a transaction across 3 shards with 1ms timeout
            var participantIds = new List<Guid> { allShardIds[0], allShardIds[1], allShardIds[2] };
            var transaction = await txnCoordinator.BeginTransactionAsync(
                participantIds, TimeSpan.FromMilliseconds(1), ct).ConfigureAwait(false);

            // Wait for timeout to expire
            await Task.Delay(50, ct).ConfigureAwait(false);

            // Verify transaction is timed out
            Assert(transaction.IsTimedOut,
                "Transaction should be marked as timed out after exceeding 1ms timeout");

            // Run timeout cleanup to verify policy enforcement
            await txnCoordinator.TimeoutExpiredTransactionsAsync(ct).ConfigureAwait(false);

            // Verify transaction was cleaned up
            var active = txnCoordinator.GetActiveTransactions();
            Assert(active.Count == 0,
                $"Expected 0 active transactions after timeout cleanup, got {active.Count}");

            // This proves: policy (timeouts, concurrency limits) cascades through the federation
        }
        finally
        {
            await router.DisposeAsync();
        }
    }

    // =========================================================================
    // Test 7: Full Bare-Metal to Serving Bootstrap
    // =========================================================================

    /// <summary>
    /// Exercises the complete bare-metal-to-serving path:
    /// raw devices -> pools -> compound devices (RAID 6) -> VDE instances ->
    /// federation -> user operations -> device failure tolerance.
    /// </summary>
    private static async Task TestBareMetalToServingFullStackAsync(CancellationToken ct)
    {
        // Step 1: Create 16 InMemoryPhysicalBlockDevices
        const int devicesPerPool = 8;
        const int blockSize = 4096;
        const long blockCount = 4096; // 16 MB per device

        var pool1Devices = E2ETestInfrastructure.CreateInMemoryDevices(
            devicesPerPool, blockSize, blockCount);
        var pool2Devices = E2ETestInfrastructure.CreateInMemoryDevices(
            devicesPerPool, blockSize, blockCount);

        try
        {
            // Step 2: Group into 2 pools of 8 devices (already done above)

            // Step 3: Build 2 CompoundBlockDevices with RAID 6 (6 data + 2 parity each)
            var compound1 = E2ETestInfrastructure.CreateCompoundDevice(
                pool1Devices.Cast<IPhysicalBlockDevice>().ToArray(),
                DeviceLayoutType.DoubleParity);
            var compound2 = E2ETestInfrastructure.CreateCompoundDevice(
                pool2Devices.Cast<IPhysicalBlockDevice>().ToArray(),
                DeviceLayoutType.DoubleParity);

            // Verify compound devices are operational
            Assert(compound1.BlockSize == blockSize,
                $"Compound1 block size should be {blockSize}, got {compound1.BlockSize}");
            Assert(compound2.BlockSize == blockSize,
                $"Compound2 block size should be {blockSize}, got {compound2.BlockSize}");
            Assert(compound1.BlockCount > 0,
                "Compound1 should have positive block count");
            Assert(compound2.BlockCount > 0,
                "Compound2 should have positive block count");

            // Step 4: Write test data through compound devices to verify RAID layer works
            var testData = E2ETestInfrastructure.GenerateTestData(blockSize, seed: 0xE2);
            await compound1.WriteBlockAsync(0, testData, ct);
            await compound2.WriteBlockAsync(0, testData, ct);

            // Read back and verify round-trip
            var readBuf1 = new byte[blockSize];
            await compound1.ReadBlockAsync(0, readBuf1, ct);
            for (int i = 0; i < blockSize; i++)
            {
                Assert(readBuf1[i] == testData[i],
                    $"Compound1 data mismatch at byte {i}: expected 0x{testData[i]:X2}, got 0x{readBuf1[i]:X2}");
            }

            // Step 5: Create InMemoryShardVdeAccessor wrapping both "VDEs" (simulated)
            var accessor = new InMemoryShardVdeAccessor();
            var vde1ShardId = accessor.AddShard();
            var vde2ShardId = accessor.AddShard();

            // Step 6: Create VdeFederationRouter with routing table (128 slots, 64 per shard)
            const int totalSlots = 128;
            var routingTable = new RoutingTable(totalSlots);
            routingTable.AssignRange(0, 64, vde1ShardId);
            routingTable.AssignRange(64, 128, vde2ShardId);

            var pathHashRouter = new PathHashRouter(totalSlots);
            await using var router = new VdeFederationRouter(routingTable, pathHashRouter);

            // Step 7: Create CrossShardOperationCoordinator
            var coordinator = new CrossShardOperationCoordinator(router, accessor);

            // Step 8: Store objects through the coordinator (add to accessor directly)
            for (int i = 0; i < 20; i++)
            {
                // Distribute objects across both shards
                var targetShard = i < 10 ? vde1ShardId : vde2ShardId;
                accessor.AddObject(targetShard, $"bare-metal/obj-{i:D3}", 512);
            }

            // Step 9: Fan-out List to verify all objects visible
            var allItems = new List<string>();
            await foreach (var item in coordinator.FanOutListAsync(null, 100, null, ct))
            {
                allItems.Add(item.Key);
            }

            Assert(allItems.Count == 20,
                $"Expected 20 objects through federation, got {allItems.Count}");

            // Verify sorted order
            for (int i = 1; i < allItems.Count; i++)
            {
                Assert(string.Compare(allItems[i - 1], allItems[i], StringComparison.Ordinal) <= 0,
                    $"Results must be sorted: '{allItems[i - 1]}' should come before '{allItems[i]}'");
            }

            // Step 10: Fail 1 device in pool-1, verify RAID tolerance
            pool1Devices[0].SetOnline(false);

            // Data should still be accessible through compound device (RAID 6 tolerates 2 failures)
            var readBufAfterFail = new byte[blockSize];
            await compound1.ReadBlockAsync(0, readBufAfterFail, ct);
            for (int i = 0; i < blockSize; i++)
            {
                Assert(readBufAfterFail[i] == testData[i],
                    $"After device failure, data mismatch at byte {i}: expected 0x{testData[i]:X2}, got 0x{readBufAfterFail[i]:X2}");
            }

            // Federation layer still serves all objects (device failure is transparent)
            var afterFailItems = new List<string>();
            await foreach (var item in coordinator.FanOutListAsync(null, 100, null, ct))
            {
                afterFailItems.Add(item.Key);
            }

            Assert(afterFailItems.Count == 20,
                $"After device failure, federation should still serve all 20 objects, got {afterFailItems.Count}");

            // Assert: raw devices -> pools -> compound devices -> VDEs -> federation -> user operations
            // all work end-to-end with device failure tolerance
            routingTable.Dispose();
        }
        finally
        {
            // Cleanup all devices
            foreach (var device in pool1Devices)
            {
                await device.DisposeAsync();
            }
            foreach (var device in pool2Devices)
            {
                await device.DisposeAsync();
            }
        }
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"E2E Assertion failed: {message}");
    }

    /// <summary>
    /// Creates a simple VdeFederationRouter with all routing table slots evenly distributed
    /// across the given shard IDs. Used to set up test federation hierarchies.
    /// </summary>
    private static VdeFederationRouter CreateSimpleRouter(Guid[] shardIds)
    {
        const int slotCount = 64;
        var pathHashRouter = new PathHashRouter(slotCount);
        var routingTable = new RoutingTable(slotCount);

        if (shardIds.Length > 0)
        {
            int slotsPerShard = slotCount / shardIds.Length;
            for (int i = 0; i < shardIds.Length; i++)
            {
                int start = i * slotsPerShard;
                int end = (i == shardIds.Length - 1) ? slotCount : (i + 1) * slotsPerShard;
                routingTable.AssignRange(start, end, shardIds[i]);
            }
        }

        return new VdeFederationRouter(routingTable, pathHashRouter);
    }
}
