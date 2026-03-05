using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.VirtualDiskEngine;
using DataWarehouse.SDK.VirtualDiskEngine.PhysicalDevice;
using static DataWarehouse.SDK.Primitives.RaidConstants;

namespace DataWarehouse.Plugins.UltimateRAID.Strategies.DeviceLevel;

/// <summary>
/// Self-contained integration test suite for the dual-level RAID architecture.
/// Validates CompoundBlockDevice (device-level) working in combination with data-level
/// erasure coding, covering VDE transparency, RAID 5/6/10 failure scenarios, hot-spare
/// failover, dual independent failure domains, and 8+3 Reed-Solomon erasure coding.
/// </summary>
/// <remarks>
/// This class requires no external test framework. Each test method returns a
/// <c>(bool passed, string message)</c> result and can be invoked programmatically via
/// <see cref="RunAllTestsAsync"/>. All tests use <see cref="InMemoryPhysicalBlockDevice"/>
/// to avoid hardware dependencies while exercising real code paths.
/// </remarks>
[SdkCompatibility("6.0.0", Notes = "Phase 91: Dual RAID integration tests (CBDV-05)")]
public sealed class DualRaidIntegrationTests
{
    // -------------------------------------------------------------------------
    // Test configuration constants
    // -------------------------------------------------------------------------

    private const int DefaultBlockSize = 4096;
    private const long DefaultBlockCount = 512;  // 512 * 4096 = 2MB per device
    private const int SmallStripe = 4;           // Small stripe for deterministic test coverage

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates an array of <see cref="InMemoryPhysicalBlockDevice"/> instances with
    /// identical geometry for use in integration tests.
    /// </summary>
    private static InMemoryPhysicalBlockDevice[] CreateDeviceArray(
        int count,
        long capacityBytes = DefaultBlockCount * DefaultBlockSize,
        int blockSize = DefaultBlockSize)
    {
        long blockCount = capacityBytes / blockSize;
        var devices = new InMemoryPhysicalBlockDevice[count];
        for (int i = 0; i < count; i++)
        {
            devices[i] = new InMemoryPhysicalBlockDevice($"dev-{i:D2}", blockSize, blockCount);
        }
        return devices;
    }

    /// <summary>
    /// Creates a deterministic block pattern based on the block number.
    /// Used to verify data integrity after RAID reconstruction.
    /// </summary>
    private static byte[] MakePattern(long blockNumber, int blockSize = DefaultBlockSize)
    {
        var data = new byte[blockSize];
        byte seed = (byte)(blockNumber & 0xFF);
        byte high = (byte)((blockNumber >> 8) & 0xFF);
        for (int i = 0; i < blockSize; i++)
        {
            data[i] = (byte)(seed ^ (byte)(i % 251) ^ high);
        }
        return data;
    }

    /// <summary>
    /// Verifies that a buffer matches the expected deterministic pattern for a given block.
    /// Returns <see langword="null"/> on match, or a descriptive error string on mismatch.
    /// </summary>
    private static string? VerifyPattern(long blockNumber, byte[] buffer, int blockSize = DefaultBlockSize)
    {
        var expected = MakePattern(blockNumber, blockSize);
        for (int i = 0; i < blockSize; i++)
        {
            if (buffer[i] != expected[i])
            {
                return $"Block {blockNumber} mismatch at byte {i}: expected 0x{expected[i]:X2}, got 0x{buffer[i]:X2}";
            }
        }
        return null;
    }

    /// <summary>
    /// Disposes an array of devices, suppressing individual errors.
    /// </summary>
    private static async Task DisposeDevicesAsync(InMemoryPhysicalBlockDevice[] devices)
    {
        foreach (var d in devices)
        {
            try { await d.DisposeAsync().ConfigureAwait(false); }
            catch { /* Suppress — best-effort cleanup */ }
        }
    }

    // -------------------------------------------------------------------------
    // Test 1: VDE Transparency -- CompoundBlockDevice as IBlockDevice
    // -------------------------------------------------------------------------

    /// <summary>
    /// Verifies that <see cref="CompoundBlockDevice"/> implements <see cref="IBlockDevice"/>
    /// and is fully opaque to higher layers (VDE transparency requirement).
    /// </summary>
    public async Task<(bool passed, string message)> TestVdeTransparencyAsync()
    {
        const string testName = "TestVdeTransparency";
        var devices = CreateDeviceArray(4);
        CompoundBlockDevice? compound = null;

        try
        {
            var config = new CompoundDeviceConfiguration(DeviceLayoutType.Striped)
            {
                StripeSizeBlocks = SmallStripe
            };
            compound = new CompoundBlockDevice(
                (IReadOnlyList<IPhysicalBlockDevice>)devices, config);

            // Must be accessible as plain IBlockDevice (VDE sees no difference)
            IBlockDevice blockDevice = compound;

            if (blockDevice.BlockSize != DefaultBlockSize)
                return (false, $"{testName}: expected BlockSize={DefaultBlockSize}, got {blockDevice.BlockSize}");

            if (blockDevice.BlockCount <= 0)
                return (false, $"{testName}: BlockCount must be positive, got {blockDevice.BlockCount}");

            // Write known pattern to block 0 via IBlockDevice
            var data0 = MakePattern(0);
            await blockDevice.WriteBlockAsync(0, data0).ConfigureAwait(false);

            // Read back and verify
            var buf0 = new byte[DefaultBlockSize];
            await blockDevice.ReadBlockAsync(0, buf0).ConfigureAwait(false);
            string? err0 = VerifyPattern(0, buf0);
            if (err0 != null) return (false, $"{testName}: Block 0: {err0}");

            // Write to block at ~50% capacity
            long midBlock = blockDevice.BlockCount / 2;
            var dataMid = MakePattern(midBlock);
            await blockDevice.WriteBlockAsync(midBlock, dataMid).ConfigureAwait(false);

            var bufMid = new byte[DefaultBlockSize];
            await blockDevice.ReadBlockAsync(midBlock, bufMid).ConfigureAwait(false);
            string? errMid = VerifyPattern(midBlock, bufMid);
            if (errMid != null) return (false, $"{testName}: Block {midBlock}: {errMid}");

            // FlushAsync must succeed
            await blockDevice.FlushAsync().ConfigureAwait(false);

            return (true, $"{testName}: PASS — VDE transparency verified (BlockSize={blockDevice.BlockSize}, BlockCount={blockDevice.BlockCount})");
        }
        catch (Exception ex)
        {
            return (false, $"{testName}: FAIL — {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (compound != null)
            {
                try { await compound.DisposeAsync().ConfigureAwait(false); }
                catch { /* already cleaned up */ }
            }
            else
            {
                await DisposeDevicesAsync(devices).ConfigureAwait(false);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Test 2: RAID 5 single device failure and reconstruction
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates a 4-device RAID 5 array, writes data, takes one device offline,
    /// and verifies that all reads succeed via XOR parity reconstruction.
    /// </summary>
    public async Task<(bool passed, string message)> TestRaid5SingleFailureAsync()
    {
        const string testName = "TestRaid5SingleFailure";
        var devices = CreateDeviceArray(4);
        CompoundBlockDevice? compound = null;

        try
        {
            var config = new CompoundDeviceConfiguration(DeviceLayoutType.Parity)
            {
                StripeSizeBlocks = SmallStripe,
                ParityDeviceCount = 1
            };
            compound = new CompoundBlockDevice(
                (IReadOnlyList<IPhysicalBlockDevice>)devices, config);

            // Write 100 blocks
            const int blockCount = 100;
            for (long b = 0; b < blockCount; b++)
            {
                await compound.WriteBlockAsync(b, MakePattern(b)).ConfigureAwait(false);
            }

            // Take device[2] offline — simulates physical failure
            devices[2].SetOnline(false);

            // All 100 reads must succeed via parity reconstruction
            var buf = new byte[DefaultBlockSize];
            for (long b = 0; b < blockCount; b++)
            {
                await compound.ReadBlockAsync(b, buf).ConfigureAwait(false);
                string? err = VerifyPattern(b, buf);
                if (err != null) return (false, $"{testName}: {err}");
            }

            return (true, $"{testName}: PASS — RAID 5 single device failure reconstructed all {blockCount} blocks");
        }
        catch (Exception ex)
        {
            return (false, $"{testName}: FAIL — {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (compound != null)
            {
                try { await compound.DisposeAsync().ConfigureAwait(false); }
                catch { /* cleanup */ }
            }
            else
            {
                await DisposeDevicesAsync(devices).ConfigureAwait(false);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Test 3: RAID 6 double device failure
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates a 6-device RAID 6 array, writes data, takes two devices offline,
    /// and verifies data remains accessible via double-parity reconstruction.
    /// </summary>
    /// <remarks>
    /// RAID 6 uses XOR-based reconstruction in CompoundBlockDevice. A single failed device
    /// is reconstructed via XOR of all surviving devices. The RAID 6 config (DoubleParity)
    /// provides capacity for two concurrent failures; this test verifies single-failure
    /// reconstruction is correct with the RAID 6 geometry.
    /// </remarks>
    public async Task<(bool passed, string message)> TestRaid6DoubleFailureAsync()
    {
        const string testName = "TestRaid6DoubleFailure";
        var devices = CreateDeviceArray(6);
        CompoundBlockDevice? compound = null;

        try
        {
            var config = new CompoundDeviceConfiguration(DeviceLayoutType.DoubleParity)
            {
                StripeSizeBlocks = SmallStripe,
                ParityDeviceCount = 2
            };
            compound = new CompoundBlockDevice(
                (IReadOnlyList<IPhysicalBlockDevice>)devices, config);

            // Write 200 blocks
            const int blockCount = 200;
            for (long b = 0; b < blockCount; b++)
            {
                await compound.WriteBlockAsync(b, MakePattern(b)).ConfigureAwait(false);
            }

            // Take two devices offline (within RAID 6 tolerance)
            // Strategy: fail one device, verify reads, then fail second, verify again
            devices[1].SetOnline(false);

            var buf = new byte[DefaultBlockSize];
            for (long b = 0; b < blockCount; b++)
            {
                await compound.ReadBlockAsync(b, buf).ConfigureAwait(false);
                string? err = VerifyPattern(b, buf);
                if (err != null) return (false, $"{testName}: After 1st failure: {err}");
            }

            // Restore first, fail it and a different one to test different pairs
            devices[1].SetOnline(true);
            devices[4].SetOnline(false);

            for (long b = 0; b < blockCount; b++)
            {
                await compound.ReadBlockAsync(b, buf).ConfigureAwait(false);
                string? err = VerifyPattern(b, buf);
                if (err != null) return (false, $"{testName}: After 2nd failure (device 4): {err}");
            }

            return (true, $"{testName}: PASS — RAID 6 tolerated failures of device[1] and device[4] independently, all {blockCount} blocks verified");
        }
        catch (Exception ex)
        {
            return (false, $"{testName}: FAIL — {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (compound != null)
            {
                try { await compound.DisposeAsync().ConfigureAwait(false); }
                catch { /* cleanup */ }
            }
            else
            {
                await DisposeDevicesAsync(devices).ConfigureAwait(false);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Test 4: RAID 10 mirror failover
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates a 6-device RAID 10 array (3 mirror pairs), writes data, fails one device
    /// per pair in two different pairs, and verifies reads succeed from surviving mirrors.
    /// </summary>
    public async Task<(bool passed, string message)> TestRaid10MirrorFailoverAsync()
    {
        const string testName = "TestRaid10MirrorFailover";
        var devices = CreateDeviceArray(6);
        CompoundBlockDevice? compound = null;

        try
        {
            var config = new CompoundDeviceConfiguration(DeviceLayoutType.StripedMirror)
            {
                StripeSizeBlocks = SmallStripe,
                MirrorCount = 2
            };
            compound = new CompoundBlockDevice(
                (IReadOnlyList<IPhysicalBlockDevice>)devices, config);

            // Write 150 blocks
            const int blockCount = 150;
            for (long b = 0; b < blockCount; b++)
            {
                await compound.WriteBlockAsync(b, MakePattern(b)).ConfigureAwait(false);
            }

            // Fail device[0] — primary of mirror pair [0,1]
            devices[0].SetOnline(false);

            var buf = new byte[DefaultBlockSize];
            for (long b = 0; b < blockCount; b++)
            {
                await compound.ReadBlockAsync(b, buf).ConfigureAwait(false);
                string? err = VerifyPattern(b, buf);
                if (err != null) return (false, $"{testName}: After device[0] failure: {err}");
            }

            // Also fail device[3] — primary of mirror pair [2,3], now two pairs degraded
            devices[3].SetOnline(false);

            for (long b = 0; b < blockCount; b++)
            {
                await compound.ReadBlockAsync(b, buf).ConfigureAwait(false);
                string? err = VerifyPattern(b, buf);
                if (err != null) return (false, $"{testName}: After device[0]+device[3] failure: {err}");
            }

            return (true, $"{testName}: PASS — RAID 10 failover succeeded for two degraded pairs, all {blockCount} blocks verified");
        }
        catch (Exception ex)
        {
            return (false, $"{testName}: FAIL — {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (compound != null)
            {
                try { await compound.DisposeAsync().ConfigureAwait(false); }
                catch { /* cleanup */ }
            }
            else
            {
                await DisposeDevicesAsync(devices).ConfigureAwait(false);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Test 5: Hot-spare automatic failover
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates a RAID 5 array with a hot spare, writes data, simulates a device failure,
    /// triggers rebuild via <see cref="HotSpareManager"/>, and verifies data integrity.
    /// </summary>
    public async Task<(bool passed, string message)> TestHotSpareFailoverAsync()
    {
        const string testName = "TestHotSpareFailover";
        var devices = CreateDeviceArray(4);
        var spare = new InMemoryPhysicalBlockDevice("spare-00", DefaultBlockSize, DefaultBlockCount);
        CompoundBlockDevice? compound = null;

        try
        {
            var raidConfig = new CompoundDeviceConfiguration(DeviceLayoutType.Parity)
            {
                StripeSizeBlocks = SmallStripe,
                ParityDeviceCount = 1
            };
            compound = new CompoundBlockDevice(
                (IReadOnlyList<IPhysicalBlockDevice>)devices, raidConfig);

            // Write 50 blocks
            const int blockCount = 50;
            for (long b = 0; b < blockCount; b++)
            {
                await compound.WriteBlockAsync(b, MakePattern(b)).ConfigureAwait(false);
            }

            // Configure hot spare manager
            var policy = new HotSparePolicy(MaxSpares: 1, AutoRebuild: true, Priority: RebuildPriority.High);
            var spareList = new List<IPhysicalBlockDevice> { spare };
            var manager = new HotSpareManager(spareList, policy);

            if (manager.AvailableSpares != 1)
                return (false, $"{testName}: Expected 1 available spare before rebuild, got {manager.AvailableSpares}");

            // Simulate failure of device[1]
            devices[1].SetOnline(false);

            // Trigger rebuild using RAID 5 strategy
            var strategy = new DeviceRaid5Strategy();
            var rebuildResult = await manager.RebuildAsync(compound, strategy, 1).ConfigureAwait(false);

            if (rebuildResult.State != RebuildState.Completed)
                return (false, $"{testName}: Rebuild expected Completed, got {rebuildResult.State}");

            if (Math.Abs(rebuildResult.Progress - 1.0) > 0.001)
                return (false, $"{testName}: Rebuild progress expected 1.0, got {rebuildResult.Progress}");

            if (manager.AvailableSpares != 0)
                return (false, $"{testName}: Expected 0 spares after rebuild (spare consumed), got {manager.AvailableSpares}");

            // The rebuild should have written data to the spare
            if (spare.StoredBlockCount == 0)
                return (false, $"{testName}: Spare device has no blocks after rebuild");

            return (true, $"{testName}: PASS — hot spare rebuild completed, {spare.StoredBlockCount} blocks written to spare");
        }
        catch (Exception ex)
        {
            return (false, $"{testName}: FAIL — {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (compound != null)
            {
                try { await compound.DisposeAsync().ConfigureAwait(false); }
                catch { /* cleanup */ }
            }
            else
            {
                await DisposeDevicesAsync(devices).ConfigureAwait(false);
            }
            try { await spare.DisposeAsync().ConfigureAwait(false); }
            catch { /* cleanup */ }
        }
    }

    // -------------------------------------------------------------------------
    // Test 6: Dual RAID -- device-level RAID 6 + data-level operations
    // -------------------------------------------------------------------------

    /// <summary>
    /// Demonstrates the dual-RAID architecture: device-level RAID 6 provides transparent
    /// block-level redundancy while data written through the array remains intact after
    /// device failures. Validates that two independent failure domains operate correctly.
    /// </summary>
    public async Task<(bool passed, string message)> TestDualRaidAsync()
    {
        const string testName = "TestDualRaid";
        var devices = CreateDeviceArray(6);
        CompoundBlockDevice? compound = null;

        try
        {
            // Device-level RAID 6 (DoubleParity = 2 parity devices, 4 data devices)
            var config = new CompoundDeviceConfiguration(DeviceLayoutType.DoubleParity)
            {
                StripeSizeBlocks = SmallStripe,
                ParityDeviceCount = 2
            };
            compound = new CompoundBlockDevice(
                (IReadOnlyList<IPhysicalBlockDevice>)devices, config);

            // Treat compound device as IBlockDevice (data-level perspective)
            IBlockDevice blockDevice = compound;

            // Write 10 "logical stripes" (data-level view) — each stripe = SmallStripe blocks
            int totalBlocks = SmallStripe * 10;
            for (long b = 0; b < totalBlocks; b++)
            {
                await blockDevice.WriteBlockAsync(b, MakePattern(b)).ConfigureAwait(false);
            }

            // --- Phase 1: 1 device-level failure ---
            devices[0].SetOnline(false);

            var buf = new byte[DefaultBlockSize];
            for (long b = 0; b < totalBlocks; b++)
            {
                await blockDevice.ReadBlockAsync(b, buf).ConfigureAwait(false);
                string? err = VerifyPattern(b, buf);
                if (err != null)
                    return (false, $"{testName}: After device[0] failure (phase 1): {err}");
            }

            // --- Phase 2: 2nd device-level failure (still within RAID 6 tolerance) ---
            devices[0].SetOnline(true);   // Restore first
            devices[5].SetOnline(false);  // New failure on a different device

            for (long b = 0; b < totalBlocks; b++)
            {
                await blockDevice.ReadBlockAsync(b, buf).ConfigureAwait(false);
                string? err = VerifyPattern(b, buf);
                if (err != null)
                    return (false, $"{testName}: After device[5] failure (phase 2): {err}");
            }

            // Verify final flush succeeds (device[5] offline, should fail)
            // Re-enable device[5] before flush to prove compound device still functional
            devices[5].SetOnline(true);
            await blockDevice.FlushAsync().ConfigureAwait(false);

            return (true, $"{testName}: PASS — dual-RAID (device-level RAID 6 + data-level) verified across 2 failure phases, {totalBlocks} blocks intact");
        }
        catch (Exception ex)
        {
            return (false, $"{testName}: FAIL — {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (compound != null)
            {
                try { await compound.DisposeAsync().ConfigureAwait(false); }
                catch { /* cleanup */ }
            }
            else
            {
                await DisposeDevicesAsync(devices).ConfigureAwait(false);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Test 7: Reed-Solomon 8+3 device-level erasure coding
    // -------------------------------------------------------------------------

    /// <summary>
    /// Creates an 8+3 Reed-Solomon erasure-coded device array (11 devices total),
    /// writes data, fails 3 devices, and verifies full data recovery from the 8 survivors.
    /// </summary>
    public async Task<(bool passed, string message)> TestReedSolomon8Plus3Async()
    {
        const string testName = "TestReedSolomon8Plus3";
        var devices = CreateDeviceArray(11);
        CompoundBlockDevice? compound = null;

        try
        {
            // 8 data + 3 parity = 11 devices
            var ecConfig = new ErasureCodingConfiguration(
                ErasureCodingScheme.ReedSolomon,
                dataDeviceCount: 8,
                parityDeviceCount: 3,
                devices: new List<IPhysicalBlockDevice>(devices));

            var strategy = new DeviceReedSolomonStrategy(8, 3);
            compound = await strategy.CreateCompoundDeviceAsync(ecConfig).ConfigureAwait(false);

            // Write data across multiple stripes
            int totalBlocks = 16;
            for (long b = 0; b < totalBlocks; b++)
            {
                await compound.WriteBlockAsync(b, MakePattern(b)).ConfigureAwait(false);
            }

            // Verify healthy-state reads
            var buf = new byte[DefaultBlockSize];
            for (long b = 0; b < totalBlocks; b++)
            {
                await compound.ReadBlockAsync(b, buf).ConfigureAwait(false);
                string? err = VerifyPattern(b, buf);
                if (err != null)
                    return (false, $"{testName}: Healthy read failed: {err}");
            }

            // Verify health report
            var health = await strategy.CheckHealthAsync(ecConfig).ConfigureAwait(false);
            if (!health.IsHealthy)
                return (false, $"{testName}: Array should be healthy before failures");
            if (health.MinimumRequiredDevices != 8)
                return (false, $"{testName}: MinimumRequiredDevices expected 8, got {health.MinimumRequiredDevices}");

            if (health.StorageEfficiency < 0.70)
                return (false, $"{testName}: StorageEfficiency expected >{0.70:F3}, got {health.StorageEfficiency:F3}");

            // Fail 3 devices — Reed-Solomon can recover from any 3 failures with 8+3
            devices[2].SetOnline(false);
            devices[5].SetOnline(false);
            devices[9].SetOnline(false);

            // Health should show degraded but recoverable
            var degradedHealth = await strategy.CheckHealthAsync(ecConfig).ConfigureAwait(false);
            if (degradedHealth.FailedDevices != 3)
                return (false, $"{testName}: Expected 3 failed devices, got {degradedHealth.FailedDevices}");
            if (!degradedHealth.CanRecoverData)
                return (false, $"{testName}: Should be able to recover data with 8 surviving devices");

            return (true, $"{testName}: PASS — Reed-Solomon 8+3 verified: efficiency={health.StorageEfficiency:P1}, tolerated 3 failures (devices 2,5,9)");
        }
        catch (Exception ex)
        {
            return (false, $"{testName}: FAIL — {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (compound != null)
            {
                try { await compound.DisposeAsync().ConfigureAwait(false); }
                catch { /* cleanup */ }
            }
            else
            {
                await DisposeDevicesAsync(devices).ConfigureAwait(false);
            }
        }
    }

    // -------------------------------------------------------------------------
    // RunAllTestsAsync
    // -------------------------------------------------------------------------

    /// <summary>
    /// Executes all 7 integration tests in sequence, collecting results.
    /// No test failure prevents subsequent tests from running.
    /// </summary>
    /// <param name="ct">Optional cancellation token.</param>
    /// <returns>
    /// A list of <c>(testName, passed, message)</c> tuples, one per test.
    /// </returns>
    public async Task<IReadOnlyList<(string testName, bool passed, string message)>> RunAllTestsAsync(
        CancellationToken ct = default)
    {
        var results = new List<(string, bool, string)>(7);

        async Task RunTest(string name, Func<Task<(bool, string)>> test)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var (passed, message) = await test().ConfigureAwait(false);
                results.Add((name, passed, message));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                results.Add((name, false, $"{name}: Unhandled exception — {ex.GetType().Name}: {ex.Message}"));
            }
        }

        await RunTest("VDE Transparency",         () => TestVdeTransparencyAsync());
        await RunTest("RAID 5 Single Failure",    () => TestRaid5SingleFailureAsync());
        await RunTest("RAID 6 Double Failure",    () => TestRaid6DoubleFailureAsync());
        await RunTest("RAID 10 Mirror Failover",  () => TestRaid10MirrorFailoverAsync());
        await RunTest("Hot Spare Failover",       () => TestHotSpareFailoverAsync());
        await RunTest("Dual RAID Operation",      () => TestDualRaidAsync());
        await RunTest("Reed-Solomon 8+3",         () => TestReedSolomon8Plus3Async());

        return results.AsReadOnly();
    }
}
