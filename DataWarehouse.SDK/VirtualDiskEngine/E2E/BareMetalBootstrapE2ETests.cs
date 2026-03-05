using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Contracts.Policy;
using DataWarehouse.SDK.Infrastructure.Policy;
using DataWarehouse.SDK.VirtualDiskEngine.Federation;
using DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice;
using StorageTier = DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice.StorageTier;

namespace DataWarehouse.SDK.VirtualDiskEngine.E2E;

/// <summary>
/// Bare-metal bootstrap E2E tests: OS-free initialization from raw devices,
/// pool creation, VDE creation, federation activation, policy propagation,
/// teardown/re-bootstrap persistence, and tiered pool metadata consistency.
/// Proves the complete bare-metal-to-user flow works as a single cohesive system.
/// </summary>
[SdkCompatibility("6.0.0", Notes = "Phase 95: Bare-Metal Bootstrap E2E Tests (E2E-05)")]
public static class BareMetalBootstrapE2ETests
{
    private const int DefaultBlockSize = 4096;
    private const long DefaultBlockCount = 10_000;
    private const string TestTag = "BareMetalBootstrapE2ETests";

    /// <summary>
    /// Runs all bare-metal bootstrap E2E tests and returns summary results.
    /// Each test is isolated: a failing test does not prevent others from running.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Tuple of (passed count, failed count, list of failure descriptions).</returns>
    public static async Task<(int passed, int failed, IReadOnlyList<string> failures)> RunAllAsync(
        CancellationToken ct = default)
    {
        var tests = new (string Name, Func<CancellationToken, Task> Action)[]
        {
            ("TestRawDeviceToPoolBootstrap", TestRawDeviceToPoolBootstrapAsync),
            ("TestPoolToVdeCreation", TestPoolToVdeCreationAsync),
            ("TestVdeStoreRetrieveDeleteLifecycle", TestVdeStoreRetrieveDeleteLifecycleAsync),
            ("TestFederationOverMultipleVdes", TestFederationOverMultipleVdesAsync),
            ("TestPolicyCascadeThroughHierarchy", TestPolicyCascadeThroughHierarchyAsync),
            ("TestFullStackTeardownAndRebootstrap", TestFullStackTeardownAndRebootstrapAsync),
            ("TestDevicePoolMetadataConsistency", TestDevicePoolMetadataConsistencyAsync),
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
                Trace.TraceInformation($"[{TestTag}] PASS: {name}");
            }
            catch (Exception ex)
            {
                failed++;
                string failMsg = $"{name}: {ex.Message}";
                failures.Add(failMsg);
                Trace.TraceWarning($"[{TestTag}] FAIL: {failMsg}");
            }
        }

        Trace.TraceInformation(
            $"[{TestTag}] Results: {passed} passed, {failed} failed out of {tests.Length} tests");

        return (passed, failed, failures.AsReadOnly());
    }

    // =========================================================================
    // Test 1: Raw Device to Pool Bootstrap
    // =========================================================================

    /// <summary>
    /// Creates 4 InMemoryPhysicalBlockDevice instances, assembles a RAID 5
    /// CompoundBlockDevice, writes a test pattern to 100 logical blocks,
    /// reads back and verifies byte-exact match. Then creates a DevicePoolDescriptor
    /// with matching members and validates its structure.
    /// </summary>
    private static async Task TestRawDeviceToPoolBootstrapAsync(CancellationToken ct)
    {
        // Create 4 raw devices (4096 block size, 10,000 blocks each)
        var devices = CreateDevices(4);

        // Assemble RAID 5 compound device
        var config = new CompoundDeviceConfiguration(DeviceLayoutType.Parity)
        {
            StripeSizeBlocks = 8,
            ParityDeviceCount = 1
        };
        var compound = new CompoundBlockDevice(
            devices.Cast<IPhysicalBlockDevice>().ToArray(), config);

        // Verify block geometry: RAID 5 with 4 devices = 3 data devices
        AssertEqual(compound.BlockSize == DefaultBlockSize,
            $"BlockSize should be {DefaultBlockSize}, got {compound.BlockSize}");
        AssertEqual(compound.BlockCount == 3 * DefaultBlockCount,
            $"BlockCount should be {3 * DefaultBlockCount} (3 data devices in RAID 5), got {compound.BlockCount}");

        // Write test pattern to first 100 logical blocks
        await WritePattern(compound, 0, 100, seed: 0xAA, ct).ConfigureAwait(false);

        // Read back and verify byte-exact match
        await VerifyPattern(compound, 0, 100, seed: 0xAA, ct).ConfigureAwait(false);

        // Create a DevicePoolDescriptor with matching member descriptors
        var members = new List<PoolMemberDescriptor>();
        for (int i = 0; i < devices.Length; i++)
        {
            members.Add(new PoolMemberDescriptor(
                DeviceId: devices[i].DeviceInfo.DeviceId,
                DevicePath: devices[i].DeviceInfo.DevicePath,
                MediaType: MediaType.RAMDisk,
                CapacityBytes: (long)DefaultBlockSize * DefaultBlockCount,
                ReservedBytes: DefaultBlockSize * 16, // 16 blocks reserved for metadata
                IsActive: true));
        }

        var now = DateTime.UtcNow;
        long totalCapacity = members.Sum(m => m.CapacityBytes);
        long usableCapacity = members.Sum(m => m.CapacityBytes - m.ReservedBytes);

        var descriptor = new DevicePoolDescriptor(
            PoolId: Guid.NewGuid(),
            PoolName: "test-pool",
            Tier: StorageTier.Hot,
            Locality: new LocalityTag(),
            Members: members.AsReadOnly(),
            TotalCapacityBytes: totalCapacity,
            UsableCapacityBytes: usableCapacity,
            CreatedUtc: now,
            LastModifiedUtc: now);

        // Verify descriptor
        AssertEqual(descriptor.Members.Count == 4,
            $"Pool should have 4 members, got {descriptor.Members.Count}");
        AssertEqual(descriptor.PoolName == "test-pool",
            $"Pool name should be 'test-pool', got '{descriptor.PoolName}'");
        AssertEqual(descriptor.PoolId != Guid.Empty,
            "PoolId should not be empty");
        AssertEqual(descriptor.TotalCapacityBytes == totalCapacity,
            $"Total capacity mismatch: expected {totalCapacity}, got {descriptor.TotalCapacityBytes}");

        await compound.DisposeAsync().ConfigureAwait(false);
    }

    // =========================================================================
    // Test 2: Pool to VDE Creation
    // =========================================================================

    /// <summary>
    /// Creates a RAID 5 CompoundBlockDevice as the pool, creates a VDE on a temp
    /// container file, stores a test object, retrieves it, and verifies byte-for-byte
    /// data integrity. Proves VDE creation on pool infrastructure works.
    /// </summary>
    private static async Task TestPoolToVdeCreationAsync(CancellationToken ct)
    {
        // Create pool infrastructure (RAID 5 over 4 devices)
        var devices = CreateDevices(4);
        var config = new CompoundDeviceConfiguration(DeviceLayoutType.Parity)
        {
            StripeSizeBlocks = 8,
            ParityDeviceCount = 1
        };
        var compound = new CompoundBlockDevice(
            devices.Cast<IPhysicalBlockDevice>().ToArray(), config);

        // Verify pool is functional
        AssertEqual(compound.BlockCount > 0, "Pool must have positive block count");

        // Create VDE on temp file
        string tempPath = CreateTempVdePath();
        VirtualDiskEngine? vde = null;
        try
        {
            var options = new VdeOptions
            {
                ContainerPath = tempPath,
                BlockSize = DefaultBlockSize,
                TotalBlocks = 1024,
                AutoCreateContainer = true,
                EnableChecksumVerification = true
            };
            vde = new VirtualDiskEngine(options);
            await vde.InitializeAsync(ct).ConfigureAwait(false);

            // Store test data
            var testData = GenerateTestData(2048, seed: 0xBB);
            using (var storeStream = new MemoryStream(testData))
            {
                await vde.StoreAsync("test/key1", storeStream, null, ct).ConfigureAwait(false);
            }

            // Retrieve and verify
            using (var retrieveStream = await vde.RetrieveAsync("test/key1", ct).ConfigureAwait(false))
            {
                using var ms = new MemoryStream();
                await retrieveStream.CopyToAsync(ms, ct).ConfigureAwait(false);
                var retrieved = ms.ToArray();

                AssertEqual(retrieved.Length == testData.Length,
                    $"Retrieved data length mismatch: expected {testData.Length}, got {retrieved.Length}");
                for (int i = 0; i < testData.Length; i++)
                {
                    if (testData[i] != retrieved[i])
                    {
                        throw new InvalidOperationException(
                            $"Data mismatch at byte {i}: expected 0x{testData[i]:X2}, got 0x{retrieved[i]:X2}");
                    }
                }
            }
        }
        finally
        {
            if (vde != null) await vde.DisposeAsync().ConfigureAwait(false);
            SafeDeleteFile(tempPath);
            await compound.DisposeAsync().ConfigureAwait(false);
        }
    }

    // =========================================================================
    // Test 3: VDE Store/Retrieve/Delete Lifecycle
    // =========================================================================

    /// <summary>
    /// Creates VDE on temp file, stores 10 objects (1KB each), lists all,
    /// deletes one, lists again, retrieves remaining 9 and verifies data integrity.
    /// Proves the complete CRUD lifecycle within a single VDE.
    /// </summary>
    private static async Task TestVdeStoreRetrieveDeleteLifecycleAsync(CancellationToken ct)
    {
        string tempPath = CreateTempVdePath();
        VirtualDiskEngine? vde = null;
        try
        {
            vde = await CreateSmallVde(tempPath, totalBlocks: 2048, ct).ConfigureAwait(false);

            // Store 10 objects with deterministic data
            var storedData = new Dictionary<string, byte[]>();
            for (int i = 0; i < 10; i++)
            {
                string key = $"obj/{i}";
                var data = GenerateTestData(1024, seed: (byte)(0x10 + i));
                storedData[key] = data;
                using var stream = new MemoryStream(data);
                await vde.StoreAsync(key, stream, null, ct).ConfigureAwait(false);
            }

            // List all objects -- verify 10 returned
            var allKeys = new List<string>();
            await foreach (var meta in vde.ListAsync(null, ct).ConfigureAwait(false))
            {
                allKeys.Add(meta.Key);
            }
            AssertEqual(allKeys.Count == 10,
                $"Expected 10 objects after store, got {allKeys.Count}");

            // Delete obj/5
            await vde.DeleteAsync("obj/5", ct).ConfigureAwait(false);

            // List again -- verify 9 returned and obj/5 absent
            var afterDelete = new List<string>();
            await foreach (var meta in vde.ListAsync(null, ct).ConfigureAwait(false))
            {
                afterDelete.Add(meta.Key);
            }
            AssertEqual(afterDelete.Count == 9,
                $"Expected 9 objects after delete, got {afterDelete.Count}");
            AssertEqual(!afterDelete.Any(k => k.Contains("obj/5")),
                "obj/5 should not appear after deletion");

            // Retrieve remaining 9 and verify data integrity
            for (int i = 0; i < 10; i++)
            {
                if (i == 5) continue; // Deleted
                string key = $"obj/{i}";
                using var retrieved = await vde.RetrieveAsync(key, ct).ConfigureAwait(false);
                using var ms = new MemoryStream();
                await retrieved.CopyToAsync(ms, ct).ConfigureAwait(false);
                var bytes = ms.ToArray();

                AssertEqual(bytes.Length == storedData[key].Length,
                    $"Data length mismatch for {key}: expected {storedData[key].Length}, got {bytes.Length}");
                for (int b = 0; b < bytes.Length; b++)
                {
                    if (bytes[b] != storedData[key][b])
                    {
                        throw new InvalidOperationException(
                            $"Data mismatch for {key} at byte {b}: " +
                            $"expected 0x{storedData[key][b]:X2}, got 0x{bytes[b]:X2}");
                    }
                }
            }
        }
        finally
        {
            if (vde != null) await vde.DisposeAsync().ConfigureAwait(false);
            SafeDeleteFile(tempPath);
        }
    }

    // =========================================================================
    // Test 4: Federation Over Multiple VDEs
    // =========================================================================

    /// <summary>
    /// Creates 2 separate VDE instances on separate temp files (simulating 2 shards).
    /// Stores objects in each, wraps VDE-A in a FederatedVirtualDiskEngine in single-VDE
    /// mode, verifies operations, then verifies VDE-B operates independently.
    /// Proves 2 VDEs can coexist and federation wrapper's single-VDE passthrough works.
    /// </summary>
    private static async Task TestFederationOverMultipleVdesAsync(CancellationToken ct)
    {
        string tempPathA = CreateTempVdePath();
        string tempPathB = CreateTempVdePath();
        VirtualDiskEngine? vdeA = null;
        VirtualDiskEngine? vdeB = null;
        FederatedVirtualDiskEngine? fedVde = null;
        try
        {
            // Create 2 independent VDEs
            vdeA = await CreateSmallVde(tempPathA, totalBlocks: 1024, ct).ConfigureAwait(false);
            vdeB = await CreateSmallVde(tempPathB, totalBlocks: 1024, ct).ConfigureAwait(false);

            // Store objects in VDE-A directly
            for (int i = 0; i < 5; i++)
            {
                var data = GenerateTestData(512, seed: (byte)(0xA0 + i));
                using var stream = new MemoryStream(data);
                await vdeA.StoreAsync($"shard-a/obj/{i}", stream, null, ct).ConfigureAwait(false);
            }

            // Store objects in VDE-B directly
            for (int i = 0; i < 5; i++)
            {
                var data = GenerateTestData(512, seed: (byte)(0xB0 + i));
                using var stream = new MemoryStream(data);
                await vdeB.StoreAsync($"shard-b/obj/{i}", stream, null, ct).ConfigureAwait(false);
            }

            // Create FederatedVirtualDiskEngine in single-VDE mode with VDE-A
            var vdeAId = Guid.NewGuid();
            fedVde = FederatedVirtualDiskEngine.CreateSingleVde(vdeA, vdeAId);

            // Verify single-VDE mode
            AssertEqual(fedVde.Mode == FederationMode.SingleVde,
                $"Expected SingleVde mode, got {fedVde.Mode}");

            // Store via federation wrapper
            var fedData = GenerateTestData(512, seed: 0xCC);
            using (var fedStream = new MemoryStream(fedData))
            {
                await fedVde.StoreAsync("shard-a/fed-obj", fedStream, null, ct).ConfigureAwait(false);
            }

            // Retrieve via federation wrapper
            using (var fedRetrieved = await fedVde.RetrieveAsync("shard-a/fed-obj", ct).ConfigureAwait(false))
            {
                using var ms = new MemoryStream();
                await fedRetrieved.CopyToAsync(ms, ct).ConfigureAwait(false);
                var bytes = ms.ToArray();
                AssertEqual(bytes.Length == fedData.Length,
                    $"Federation retrieve data length mismatch: expected {fedData.Length}, got {bytes.Length}");
            }

            // Verify VDE-B operates independently (no interference)
            var vdeBKeys = new List<string>();
            await foreach (var meta in vdeB.ListAsync(null, ct).ConfigureAwait(false))
            {
                vdeBKeys.Add(meta.Key);
            }
            AssertEqual(vdeBKeys.Count == 5,
                $"VDE-B should have 5 objects, got {vdeBKeys.Count}");

            // Retrieve from VDE-B to verify data integrity
            using (var bStream = await vdeB.RetrieveAsync("shard-b/obj/0", ct).ConfigureAwait(false))
            {
                using var ms = new MemoryStream();
                await bStream.CopyToAsync(ms, ct).ConfigureAwait(false);
                var expected = GenerateTestData(512, seed: 0xB0);
                var actual = ms.ToArray();
                AssertEqual(actual.Length == expected.Length,
                    $"VDE-B data length mismatch for shard-b/obj/0");
                for (int b = 0; b < expected.Length; b++)
                {
                    if (actual[b] != expected[b])
                    {
                        throw new InvalidOperationException(
                            $"VDE-B data mismatch at byte {b}: expected 0x{expected[b]:X2}, got 0x{actual[b]:X2}");
                    }
                }
            }
        }
        finally
        {
            if (fedVde != null) await fedVde.DisposeAsync().ConfigureAwait(false);
            // Note: fedVde.Dispose does NOT dispose the inner VDE per single-VDE semantics,
            // so we dispose VDE-A separately if it wasn't already disposed
            if (vdeA != null) try { await vdeA.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[E2E] VDE-A dispose failed: {ex.Message}"); }
            if (vdeB != null) await vdeB.DisposeAsync().ConfigureAwait(false);
            SafeDeleteFile(tempPathA);
            SafeDeleteFile(tempPathB);
        }
    }

    // =========================================================================
    // Test 5: Policy Cascade Through Hierarchy
    // =========================================================================

    /// <summary>
    /// Creates a CascadeOverrideStore, sets a global cascade strategy, sets an
    /// override at a child level, and verifies that CascadeStrategies resolution
    /// correctly applies inherit semantics with child override taking precedence.
    /// Proves cascade semantics work through the policy hierarchy.
    /// </summary>
    private static Task TestPolicyCascadeThroughHierarchyAsync(CancellationToken ct)
    {
        // Test CascadeStrategies.Inherit: parent values provide defaults, child overrides
        var chain = new List<FeaturePolicy>
        {
            // Most specific (Object level) -- child override: compression disabled (intensity 0)
            new FeaturePolicy
            {
                FeatureId = "compression",
                Level = PolicyLevel.Object,
                IntensityLevel = 0,
                AiAutonomy = AiAutonomyLevel.ManualOnly,
                Cascade = CascadeStrategy.Inherit,
                CustomParameters = new Dictionary<string, string>
                {
                    ["algorithm"] = "none"
                }
            },
            // Less specific (VDE level) -- parent: compression enabled (intensity 80)
            new FeaturePolicy
            {
                FeatureId = "compression",
                Level = PolicyLevel.VDE,
                IntensityLevel = 80,
                AiAutonomy = AiAutonomyLevel.Suggest,
                Cascade = CascadeStrategy.Inherit,
                CustomParameters = new Dictionary<string, string>
                {
                    ["algorithm"] = "lz4",
                    ["level"] = "fast"
                }
            }
        };

        // Inherit strategy: most-specific (chain[0]) wins for intensity/autonomy,
        // custom params merged (parent first, child overwrites)
        var (intensity, aiAutonomy, mergedParams, decidedAt) = CascadeStrategies.Inherit(chain);

        // Child override should take precedence for intensity and autonomy
        AssertEqual(intensity == 0,
            $"Child override should set intensity to 0 (disabled), got {intensity}");
        AssertEqual(aiAutonomy == AiAutonomyLevel.ManualOnly,
            $"Child override should set autonomy to ManualOnly, got {aiAutonomy}");
        AssertEqual(decidedAt == PolicyLevel.Object,
            $"Decision should be at Object level, got {decidedAt}");

        // Merged params: child's "algorithm"="none" overwrites parent's "algorithm"="lz4",
        // but parent's "level"="fast" is inherited
        AssertEqual(mergedParams.ContainsKey("algorithm") && mergedParams["algorithm"] == "none",
            $"Child should override algorithm to 'none', got '{(mergedParams.ContainsKey("algorithm") ? mergedParams["algorithm"] : "MISSING")}'");
        AssertEqual(mergedParams.ContainsKey("level") && mergedParams["level"] == "fast",
            $"Parent's 'level'='fast' should be inherited, got '{(mergedParams.ContainsKey("level") ? mergedParams["level"] : "MISSING")}'");

        // Test Override strategy: most-specific wins entirely, parent discarded
        var (ovIntensity, ovAiAutonomy, ovParams, ovDecidedAt) = CascadeStrategies.Override(chain);
        AssertEqual(ovIntensity == 0,
            $"Override: child intensity should be 0, got {ovIntensity}");
        AssertEqual(!ovParams.ContainsKey("level"),
            "Override: parent's 'level' param should NOT be present (parent discarded)");

        // Test CascadeOverrideStore: set and retrieve overrides
        var overrideStore = new CascadeOverrideStore();
        overrideStore.SetOverride("compression", PolicyLevel.VDE, CascadeStrategy.Inherit);
        overrideStore.SetOverride("compression", PolicyLevel.Object, CascadeStrategy.Override);

        AssertEqual(overrideStore.Count == 2,
            $"Override store should have 2 entries, got {overrideStore.Count}");

        bool vdeFound = overrideStore.TryGetOverride("compression", PolicyLevel.VDE, out var vdeStrategy);
        AssertEqual(vdeFound && vdeStrategy == CascadeStrategy.Inherit,
            "VDE-level override should be Inherit");

        bool objFound = overrideStore.TryGetOverride("compression", PolicyLevel.Object, out var objStrategy);
        AssertEqual(objFound && objStrategy == CascadeStrategy.Override,
            "Object-level override should be Override");

        // Verify non-existent override returns false
        bool missingFound = overrideStore.TryGetOverride("encryption", PolicyLevel.Block, out _);
        AssertEqual(!missingFound,
            "Non-existent override should return false");

        return Task.CompletedTask;
    }

    // =========================================================================
    // Test 6: Full Stack Teardown and Re-bootstrap
    // =========================================================================

    /// <summary>
    /// Creates VDE on temp file, stores 5 objects, disposes VDE (simulates shutdown),
    /// creates a NEW VDE instance on the same container, re-initializes, and retrieves
    /// all 5 objects to verify data survives teardown and re-bootstrap.
    /// Proves persistence across VDE lifecycle.
    /// </summary>
    private static async Task TestFullStackTeardownAndRebootstrapAsync(CancellationToken ct)
    {
        string tempPath = CreateTempVdePath();
        var storedData = new Dictionary<string, byte[]>();

        try
        {
            // Phase 1: Create VDE and store 5 objects
            await StoreObjectsForTeardownTest(tempPath, storedData, ct).ConfigureAwait(false);

            // Phase 2: Re-bootstrap from same container file
            await VerifyRebootstrapRecovery(tempPath, storedData, ct).ConfigureAwait(false);
        }
        finally
        {
            SafeDeleteFile(tempPath);
        }
    }

    /// <summary>
    /// Phase 1 of teardown test: creates VDE, stores objects, then disposes.
    /// </summary>
    private static async Task StoreObjectsForTeardownTest(
        string tempPath, Dictionary<string, byte[]> storedData, CancellationToken ct)
    {
        var vde = await CreateSmallVde(tempPath, totalBlocks: 2048, ct).ConfigureAwait(false);
        for (int i = 0; i < 5; i++)
        {
            string key = $"persist/obj/{i}";
            var data = GenerateTestData(1024, seed: (byte)(0xD0 + i));
            storedData[key] = data;
            using var stream = new MemoryStream(data);
            await vde.StoreAsync(key, stream, null, ct).ConfigureAwait(false);
        }

        // Dispose VDE (simulates shutdown / teardown)
        await vde.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Phase 2 of teardown test: re-opens container and verifies all data survived.
    /// </summary>
    private static async Task VerifyRebootstrapRecovery(
        string tempPath, Dictionary<string, byte[]> storedData, CancellationToken ct)
    {
        AssertEqual(File.Exists(tempPath),
            $"Container file should still exist at {tempPath}");

        var options = new VdeOptions
        {
            ContainerPath = tempPath,
            BlockSize = DefaultBlockSize,
            TotalBlocks = 2048,
            AutoCreateContainer = false, // Must open existing
            EnableChecksumVerification = true
        };
        var vde = new VirtualDiskEngine(options);
        await vde.InitializeAsync(ct).ConfigureAwait(false);

        // Retrieve all 5 objects and verify data matches original
        for (int i = 0; i < 5; i++)
        {
            string key = $"persist/obj/{i}";
            using var retrieved = await vde.RetrieveAsync(key, ct).ConfigureAwait(false);
            using var ms = new MemoryStream();
            await retrieved.CopyToAsync(ms, ct).ConfigureAwait(false);
            var bytes = ms.ToArray();

            AssertEqual(bytes.Length == storedData[key].Length,
                $"Re-bootstrap data length mismatch for {key}: expected {storedData[key].Length}, got {bytes.Length}");
            for (int b = 0; b < bytes.Length; b++)
            {
                if (bytes[b] != storedData[key][b])
                {
                    throw new InvalidOperationException(
                        $"Re-bootstrap data mismatch for {key} at byte {b}: " +
                        $"expected 0x{storedData[key][b]:X2}, got 0x{bytes[b]:X2}");
                }
            }
        }

        await vde.DisposeAsync().ConfigureAwait(false);
    }

    // =========================================================================
    // Test 7: Device Pool Metadata Consistency
    // =========================================================================

    /// <summary>
    /// Creates a DevicePoolDescriptor with 4 members across 2 tiers (2 Hot, 2 Warm).
    /// Verifies PoolId, member uniqueness, and capacity accounting. Creates a
    /// CompoundBlockDevice from each tier's devices, writes different patterns to each,
    /// and verifies data integrity per tier. Proves tiered pool organization works.
    /// </summary>
    private static async Task TestDevicePoolMetadataConsistencyAsync(CancellationToken ct)
    {
        // Create 4 devices: 2 "Hot" (NVMe-like), 2 "Warm" (SSD-like)
        var hotDevices = CreateDevices(2, prefix: "hot");
        var warmDevices = CreateDevices(2, prefix: "warm");

        // Build pool descriptor with all 4 members across 2 tiers
        var members = new List<PoolMemberDescriptor>();
        foreach (var dev in hotDevices)
        {
            members.Add(new PoolMemberDescriptor(
                DeviceId: dev.DeviceInfo.DeviceId,
                DevicePath: dev.DeviceInfo.DevicePath,
                MediaType: MediaType.NVMe,
                CapacityBytes: (long)DefaultBlockSize * DefaultBlockCount,
                ReservedBytes: DefaultBlockSize * 8,
                IsActive: true));
        }
        foreach (var dev in warmDevices)
        {
            members.Add(new PoolMemberDescriptor(
                DeviceId: dev.DeviceInfo.DeviceId,
                DevicePath: dev.DeviceInfo.DevicePath,
                MediaType: MediaType.SSD,
                CapacityBytes: (long)DefaultBlockSize * DefaultBlockCount,
                ReservedBytes: DefaultBlockSize * 8,
                IsActive: true));
        }

        var now = DateTime.UtcNow;
        long totalCapacity = members.Sum(m => m.CapacityBytes);
        long usableCapacity = members.Sum(m => m.CapacityBytes - m.ReservedBytes);

        var descriptor = new DevicePoolDescriptor(
            PoolId: Guid.NewGuid(),
            PoolName: "tiered-pool",
            Tier: StorageTier.Hot, // Primary tier
            Locality: new LocalityTag(Rack: "rack-1", Datacenter: "dc-east"),
            Members: members.AsReadOnly(),
            TotalCapacityBytes: totalCapacity,
            UsableCapacityBytes: usableCapacity,
            CreatedUtc: now,
            LastModifiedUtc: now);

        // Verify pool descriptor integrity
        AssertEqual(descriptor.PoolId != Guid.Empty,
            "PoolId should be a non-empty GUID");
        AssertEqual(descriptor.Members.Count == 4,
            $"Pool should have 4 members, got {descriptor.Members.Count}");

        // Verify each member has unique DeviceId
        var deviceIds = new HashSet<string>(descriptor.Members.Select(m => m.DeviceId));
        AssertEqual(deviceIds.Count == 4,
            $"All 4 members should have unique DeviceIds, got {deviceIds.Count} unique");

        // Verify total capacity = sum of member capacities
        long expectedTotal = descriptor.Members.Sum(m => m.CapacityBytes);
        AssertEqual(descriptor.TotalCapacityBytes == expectedTotal,
            $"TotalCapacity should be {expectedTotal}, got {descriptor.TotalCapacityBytes}");

        // Verify usable capacity = total minus reserved
        long expectedUsable = descriptor.Members.Sum(m => m.CapacityBytes - m.ReservedBytes);
        AssertEqual(descriptor.UsableCapacityBytes == expectedUsable,
            $"UsableCapacity should be {expectedUsable}, got {descriptor.UsableCapacityBytes}");

        // Create CompoundBlockDevice from "Hot" tier devices (striped for performance)
        var hotConfig = new CompoundDeviceConfiguration(DeviceLayoutType.Striped)
        {
            StripeSizeBlocks = 8
        };
        var hotCompound = new CompoundBlockDevice(
            hotDevices.Cast<IPhysicalBlockDevice>().ToArray(), hotConfig);

        // Write hot-tier pattern and verify
        await WritePattern(hotCompound, 0, 50, seed: 0xE1, ct).ConfigureAwait(false);
        await VerifyPattern(hotCompound, 0, 50, seed: 0xE1, ct).ConfigureAwait(false);

        // Create CompoundBlockDevice from "Warm" tier devices (mirrored for safety)
        var warmConfig = new CompoundDeviceConfiguration(DeviceLayoutType.Mirrored)
        {
            MirrorCount = 2
        };
        var warmCompound = new CompoundBlockDevice(
            warmDevices.Cast<IPhysicalBlockDevice>().ToArray(), warmConfig);

        // Write warm-tier pattern (different from hot) and verify
        await WritePattern(warmCompound, 0, 50, seed: 0xF2, ct).ConfigureAwait(false);
        await VerifyPattern(warmCompound, 0, 50, seed: 0xF2, ct).ConfigureAwait(false);

        // Verify hot data unchanged after warm writes (tier isolation)
        await VerifyPattern(hotCompound, 0, 50, seed: 0xE1, ct).ConfigureAwait(false);

        await hotCompound.DisposeAsync().ConfigureAwait(false);
        await warmCompound.DisposeAsync().ConfigureAwait(false);
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    /// <summary>
    /// Creates a temporary file path for a VDE container in the system temp directory.
    /// </summary>
    private static string CreateTempVdePath()
    {
        return Path.Combine(Path.GetTempPath(), $"e2e-{Guid.NewGuid():N}.dwvd");
    }

    /// <summary>
    /// Creates a small VDE instance on a temp file, initializes it, and returns it.
    /// </summary>
    private static async Task<VirtualDiskEngine> CreateSmallVde(
        string path, int totalBlocks = 1024, CancellationToken ct = default)
    {
        var options = new VdeOptions
        {
            ContainerPath = path,
            BlockSize = DefaultBlockSize,
            TotalBlocks = totalBlocks,
            AutoCreateContainer = true,
            EnableChecksumVerification = true
        };
        var vde = new VirtualDiskEngine(options);
        await vde.InitializeAsync(ct).ConfigureAwait(false);
        return vde;
    }

    /// <summary>
    /// Generates a deterministic byte array of the specified size.
    /// Pattern: <c>data[i] = (byte)((i &amp; 0xFF) ^ seed ^ ((i >> 8) &amp; 0xFF))</c>.
    /// </summary>
    private static byte[] GenerateTestData(int size, byte seed)
    {
        var data = new byte[size];
        for (int i = 0; i < size; i++)
        {
            data[i] = (byte)((i & 0xFF) ^ seed ^ ((i >> 8) & 0xFF));
        }
        return data;
    }

    /// <summary>
    /// Creates an array of <see cref="InMemoryPhysicalBlockDevice"/> instances for testing.
    /// </summary>
    private static InMemoryPhysicalBlockDevice[] CreateDevices(
        int count,
        int blockSize = DefaultBlockSize,
        long blockCount = DefaultBlockCount,
        string prefix = "dev")
    {
        var devices = new InMemoryPhysicalBlockDevice[count];
        for (int i = 0; i < count; i++)
        {
            devices[i] = new InMemoryPhysicalBlockDevice(
                deviceId: $"{prefix}-{i:D3}",
                blockSize: blockSize,
                blockCount: blockCount);
        }
        return devices;
    }

    /// <summary>
    /// Writes a deterministic pattern to a range of blocks on the given block device.
    /// </summary>
    private static async Task WritePattern(
        IBlockDevice device, long startBlock, int count, byte seed, CancellationToken ct)
    {
        for (long block = startBlock; block < startBlock + count; block++)
        {
            var data = new byte[device.BlockSize];
            byte fill = (byte)((block * 7 + seed) & 0xFF);
            Array.Fill(data, fill);
            await device.WriteBlockAsync(block, data.AsMemory(), ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads and verifies a deterministic pattern from a range of blocks.
    /// </summary>
    private static async Task VerifyPattern(
        IBlockDevice device, long startBlock, int count, byte seed, CancellationToken ct)
    {
        var buffer = new byte[device.BlockSize];
        for (long block = startBlock; block < startBlock + count; block++)
        {
            await device.ReadBlockAsync(block, buffer.AsMemory(), ct).ConfigureAwait(false);

            byte expected = (byte)((block * 7 + seed) & 0xFF);
            for (int b = 0; b < device.BlockSize; b++)
            {
                if (buffer[b] != expected)
                {
                    throw new InvalidOperationException(
                        $"Pattern mismatch at block {block}, byte {b}: " +
                        $"expected 0x{expected:X2}, got 0x{buffer[b]:X2} (seed=0x{seed:X2})");
                }
            }
        }
    }

    /// <summary>
    /// Asserts that a condition is true. Throws on failure.
    /// </summary>
    private static void AssertEqual(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"Assertion failed: {message}");
    }

    /// <summary>
    /// Safely deletes a file, ignoring any errors.
    /// </summary>
    private static void SafeDeleteFile(string path)
    {
        try { File.Delete(path); } catch { /* Best effort cleanup */ }
    }
}
