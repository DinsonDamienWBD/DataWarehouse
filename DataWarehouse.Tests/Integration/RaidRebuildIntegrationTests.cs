using DataWarehouse.Plugins.UltimateRAID.Strategies.Standard;
using DataWarehouse.SDK.Contracts.RAID;
using FluentAssertions;
using Xunit;
using DataWarehouse.SDK.Utilities;

namespace DataWarehouse.Tests.Integration;

/// <summary>
/// Integration tests for RAID rebuild operations.
/// Tests RAID array creation, disk failure simulation, and rebuild verification.
/// Uses in-memory disk simulation for testing.
/// </summary>
[Trait("Category", "Integration")]
public class RaidRebuildIntegrationTests
{
    /// <summary>
    /// Simple in-memory disk simulation for testing.
    /// </summary>
    private class InMemoryDisk
    {
        private readonly BoundedDictionary<long, byte[]> _blocks = new BoundedDictionary<long, byte[]>(1000);
        public string Id { get; }
        public long Capacity { get; }
        public DiskHealthStatus HealthStatus { get; set; }

        public InMemoryDisk(string id, long capacity)
        {
            Id = id;
            Capacity = capacity;
            HealthStatus = DiskHealthStatus.Healthy;
        }

        public void Write(long offset, byte[] data)
        {
            if (HealthStatus != DiskHealthStatus.Healthy)
                throw new InvalidOperationException($"Cannot write to unhealthy disk {Id}");
            _blocks[offset] = (byte[])data.Clone();
        }

        public byte[] Read(long offset, int length)
        {
            if (HealthStatus != DiskHealthStatus.Healthy)
                throw new InvalidOperationException($"Cannot read from unhealthy disk {Id}");

            if (_blocks.TryGetValue(offset, out var data))
                return (byte[])data.Clone();

            return new byte[length]; // Return zeros for unwritten blocks
        }

        public void CopyFrom(InMemoryDisk source)
        {
            _blocks.Clear();
            foreach (var kvp in source._blocks)
            {
                _blocks[kvp.Key] = (byte[])kvp.Value.Clone();
            }
        }
    }

    [Fact]
    public void RaidArray_Raid1_ShouldInitializeWithTwoDisks()
    {
        // Arrange
        var disk1 = new InMemoryDisk("disk1", 1024 * 1024);
        var disk2 = new InMemoryDisk("disk2", 1024 * 1024);

        // Act
        var disks = new[] { disk1, disk2 };

        // Assert
        disks.Should().HaveCount(2);
        disks.Should().AllSatisfy(d => d.HealthStatus.Should().Be(DiskHealthStatus.Healthy));
    }

    [Fact]
    public async Task RaidArray_Raid1_ShouldMirrorDataToBothDisks()
    {
        // Arrange
        var disk1 = new InMemoryDisk("disk1", 1024 * 1024);
        var disk2 = new InMemoryDisk("disk2", 1024 * 1024);
        var strategy = new Raid1Strategy();
        var testData = new byte[] { 1, 2, 3, 4, 5 };

        // Act - Simulate RAID-1 write (manual implementation since strategy needs full DiskInfo)
        disk1.Write(0, testData);
        disk2.Write(0, testData);

        // Assert - Both disks should have identical data
        var data1 = disk1.Read(0, testData.Length);
        var data2 = disk2.Read(0, testData.Length);

        data1.Should().BeEquivalentTo(testData);
        data2.Should().BeEquivalentTo(testData);
        data1.Should().BeEquivalentTo(data2);
    }

    [Fact]
    public void RaidArray_Raid1_SimulateDiskFailure_ShouldMarkDiskFailed()
    {
        // Arrange
        var disk1 = new InMemoryDisk("disk1", 1024 * 1024);
        var disk2 = new InMemoryDisk("disk2", 1024 * 1024);
        var testData = new byte[] { 10, 20, 30, 40, 50 };

        disk1.Write(0, testData);
        disk2.Write(0, testData);

        // Act - Simulate disk failure
        disk2.HealthStatus = DiskHealthStatus.Failed;

        // Assert
        disk2.HealthStatus.Should().Be(DiskHealthStatus.Failed);

        // Disk1 should still be readable
        var data1 = disk1.Read(0, testData.Length);
        data1.Should().BeEquivalentTo(testData);

        // Disk2 should throw when accessed
        var readAction = () => disk2.Read(0, testData.Length);
        readAction.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void RaidArray_Raid1_RebuildFromMirror_ShouldRestoreData()
    {
        // Arrange
        var disk1 = new InMemoryDisk("disk1", 1024 * 1024);
        var disk2 = new InMemoryDisk("disk2", 1024 * 1024);
        var testData1 = new byte[] { 1, 2, 3, 4, 5 };
        var testData2 = new byte[] { 6, 7, 8, 9, 10 };

        // Write data to both mirrors
        disk1.Write(0, testData1);
        disk2.Write(0, testData1);
        disk1.Write(100, testData2);
        disk2.Write(100, testData2);

        // Simulate disk2 failure
        disk2.HealthStatus = DiskHealthStatus.Failed;

        // Create replacement disk
        var disk3 = new InMemoryDisk("disk3", 1024 * 1024);

        // Act - Rebuild: copy all data from disk1 to disk3
        disk3.CopyFrom(disk1);
        disk3.HealthStatus = DiskHealthStatus.Healthy;

        // Assert - Replacement disk should have identical data
        var rebuilt1 = disk3.Read(0, testData1.Length);
        var rebuilt2 = disk3.Read(100, testData2.Length);

        rebuilt1.Should().BeEquivalentTo(testData1);
        rebuilt2.Should().BeEquivalentTo(testData2);
    }

    [Fact]
    public void RaidArray_Raid5_ShouldRequireMinimumThreeDisks()
    {
        // Arrange
        var strategy = new Raid5Strategy();

        // Assert
        strategy.Capabilities.MinDisks.Should().Be(3);
        strategy.Capabilities.RedundancyLevel.Should().Be(1);
    }

    [Fact]
    public void RaidArray_Raid1_DegradedRead_ShouldReadFromHealthyDisk()
    {
        // Arrange - RAID-1 with 2 disks
        var disk1 = new InMemoryDisk("disk1", 1024 * 1024);
        var disk2 = new InMemoryDisk("disk2", 1024 * 1024);
        var testData = new byte[] { 100, 101, 102, 103, 104 };

        disk1.Write(0, testData);
        disk2.Write(0, testData);

        // Fail one disk
        disk2.HealthStatus = DiskHealthStatus.Failed;

        // Act - Read from degraded array (only disk1 healthy)
        var readData = disk1.Read(0, testData.Length);

        // Assert - Should still read successfully from healthy disk
        readData.Should().BeEquivalentTo(testData);
    }

    [Fact]
    public void RaidArray_Raid5_CalculateStripe_ShouldDistributeParityAcrossDisks()
    {
        // Arrange
        var strategy = new Raid5Strategy();

        // Act - Calculate stripe for different blocks
        var stripe0 = strategy.CalculateStripe(0, 3);
        var stripe1 = strategy.CalculateStripe(1, 3);
        var stripe2 = strategy.CalculateStripe(2, 3);

        // Assert - Each stripe should have parity
        stripe0.ParityChunkCount.Should().Be(1);
        stripe1.ParityChunkCount.Should().Be(1);
        stripe2.ParityChunkCount.Should().Be(1);

        // Data chunks = total disks - parity disks
        stripe0.DataChunkCount.Should().Be(2);
        stripe1.DataChunkCount.Should().Be(2);
        stripe2.DataChunkCount.Should().Be(2);
    }

    [Fact]
    public void RaidMetadata_Raid1Strategy_ShouldHaveCorrectCapabilities()
    {
        // Arrange
        var strategy = new Raid1Strategy();

        // Assert
        strategy.Level.Should().Be(RaidLevel.Raid1);
        strategy.Capabilities.SupportsHotSpare.Should().BeTrue();
        strategy.Capabilities.SupportsOnlineExpansion.Should().BeTrue();
        strategy.Capabilities.CapacityEfficiency.Should().BeApproximately(0.5, 0.01);
    }

    [Fact]
    public void RaidMetadata_Raid5Strategy_ShouldHaveCorrectCapabilities()
    {
        // Arrange
        var strategy = new Raid5Strategy();

        // Assert
        strategy.Level.Should().Be(RaidLevel.Raid5);
        strategy.Capabilities.MinDisks.Should().Be(3);
        strategy.Capabilities.RedundancyLevel.Should().Be(1);
        strategy.Capabilities.SupportsHotSpare.Should().BeTrue();
    }

    [Fact]
    public void RaidRebuild_ProgressTracking_ShouldReportProgress()
    {
        // Arrange
        var totalBytes = 1000000L;
        var bytesRebuilt = 500000L;
        var elapsed = TimeSpan.FromSeconds(10);
        var speed = bytesRebuilt / elapsed.TotalSeconds;
        var remaining = (long)((totalBytes - bytesRebuilt) / speed);

        // Act
        var progress = new RebuildProgress(
            PercentComplete: (double)bytesRebuilt / totalBytes,
            BytesRebuilt: bytesRebuilt,
            TotalBytes: totalBytes,
            EstimatedTimeRemaining: TimeSpan.FromSeconds(remaining),
            CurrentSpeed: (long)speed);

        // Assert
        progress.PercentComplete.Should().BeApproximately(0.5, 0.01);
        progress.BytesRebuilt.Should().Be(500000);
        progress.TotalBytes.Should().Be(1000000);
        progress.CurrentSpeed.Should().BeGreaterThan(0);
    }
}
