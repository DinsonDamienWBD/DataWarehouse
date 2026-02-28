using DataWarehouse.SDK.VirtualDiskEngine;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.SdkTests;

/// <summary>
/// Tests for Virtual Disk Engine public types: VdeOptions validation, VdeConstants, VdeHealthReport.
/// Note: Internal VDE types (FreeSpaceManager, WAL, Inode, B-Tree, CoW) are not directly
/// testable from the test project. These tests exercise the public configuration and health APIs.
/// </summary>
[Trait("Category", "Unit")]
public class VirtualDiskTests
{
    #region VdeOptions Defaults

    [Fact]
    public void VdeOptions_Defaults_ShouldBeCorrect()
    {
        var opts = new VdeOptions { ContainerPath = "/data/test.dwvd" };
        opts.BlockSize.Should().Be(4096);
        opts.TotalBlocks.Should().Be(1_048_576);
        opts.WalSizePercent.Should().Be(1);
        opts.MaxCachedInodes.Should().Be(10_000);
        opts.MaxCachedBTreeNodes.Should().Be(1_000);
        opts.MaxCachedChecksumBlocks.Should().Be(256);
        opts.CheckpointWalUtilizationPercent.Should().Be(75);
        opts.EnableChecksumVerification.Should().BeTrue();
        opts.AutoCreateContainer.Should().BeTrue();
    }

    #endregion

    #region VdeOptions Validation

    [Fact]
    public void Validate_ValidOptions_ShouldNotThrow()
    {
        var opts = new VdeOptions { ContainerPath = "/data/test.dwvd" };
        opts.Validate();
        opts.Should().NotBeNull("Validate should complete without throwing");
    }

    [Fact]
    public void Validate_EmptyContainerPath_ShouldThrow()
    {
        var opts = new VdeOptions { ContainerPath = "" };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*ContainerPath*");
    }

    [Fact]
    public void Validate_BlockSizeTooSmall_ShouldThrow()
    {
        var opts = new VdeOptions { ContainerPath = "/test.dwvd", BlockSize = 256 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*BlockSize*");
    }

    [Fact]
    public void Validate_BlockSizeTooLarge_ShouldThrow()
    {
        var opts = new VdeOptions { ContainerPath = "/test.dwvd", BlockSize = 131072 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*BlockSize*");
    }

    [Fact]
    public void Validate_BlockSizeNotPowerOfTwo_ShouldThrow()
    {
        var opts = new VdeOptions { ContainerPath = "/test.dwvd", BlockSize = 3000 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*power of 2*");
    }

    [Theory]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(2048)]
    [InlineData(4096)]
    [InlineData(8192)]
    [InlineData(16384)]
    [InlineData(32768)]
    [InlineData(65536)]
    public void Validate_ValidBlockSizes_ShouldNotThrow(int blockSize)
    {
        var opts = new VdeOptions { ContainerPath = "/test.dwvd", BlockSize = blockSize };
        opts.Validate();
        opts.BlockSize.Should().Be(blockSize);
    }

    [Fact]
    public void Validate_ZeroTotalBlocks_ShouldThrow()
    {
        var opts = new VdeOptions { ContainerPath = "/test.dwvd", TotalBlocks = 0 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*TotalBlocks*");
    }

    [Fact]
    public void Validate_NegativeTotalBlocks_ShouldThrow()
    {
        var opts = new VdeOptions { ContainerPath = "/test.dwvd", TotalBlocks = -1 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*TotalBlocks*");
    }

    [Fact]
    public void Validate_WalSizePercentTooLow_ShouldThrow()
    {
        var opts = new VdeOptions { ContainerPath = "/test.dwvd", WalSizePercent = 0 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*WalSizePercent*");
    }

    [Fact]
    public void Validate_WalSizePercentTooHigh_ShouldThrow()
    {
        var opts = new VdeOptions { ContainerPath = "/test.dwvd", WalSizePercent = 51 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*WalSizePercent*");
    }

    [Fact]
    public void Validate_NegativeCachedInodes_ShouldThrow()
    {
        var opts = new VdeOptions { ContainerPath = "/test.dwvd", MaxCachedInodes = -1 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*MaxCachedInodes*");
    }

    [Fact]
    public void Validate_NegativeCachedBTreeNodes_ShouldThrow()
    {
        var opts = new VdeOptions { ContainerPath = "/test.dwvd", MaxCachedBTreeNodes = -1 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*MaxCachedBTreeNodes*");
    }

    [Fact]
    public void Validate_NegativeCachedChecksumBlocks_ShouldThrow()
    {
        var opts = new VdeOptions { ContainerPath = "/test.dwvd", MaxCachedChecksumBlocks = -1 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*MaxCachedChecksumBlocks*");
    }

    [Fact]
    public void Validate_CheckpointPercentTooLow_ShouldThrow()
    {
        var opts = new VdeOptions { ContainerPath = "/test.dwvd", CheckpointWalUtilizationPercent = 0 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*CheckpointWalUtilizationPercent*");
    }

    [Fact]
    public void Validate_CheckpointPercentTooHigh_ShouldThrow()
    {
        var opts = new VdeOptions { ContainerPath = "/test.dwvd", CheckpointWalUtilizationPercent = 101 };
        var act = () => opts.Validate();
        act.Should().Throw<ArgumentException>().WithMessage("*CheckpointWalUtilizationPercent*");
    }

    #endregion

    #region VdeConstants

    [Fact]
    public void VdeConstants_MagicBytes_ShouldBeDWVD()
    {
        VdeConstants.MagicBytes.Should().Be(0x44575644u);
    }

    [Fact]
    public void VdeConstants_FormatVersion_ShouldBe1()
    {
        VdeConstants.FormatVersion.Should().Be(1);
    }

    [Fact]
    public void VdeConstants_DefaultBlockSize_ShouldBe4096()
    {
        VdeConstants.DefaultBlockSize.Should().Be(4096);
    }

    [Fact]
    public void VdeConstants_MinBlockSize_ShouldBe512()
    {
        VdeConstants.MinBlockSize.Should().Be(512);
    }

    [Fact]
    public void VdeConstants_MaxBlockSize_ShouldBe65536()
    {
        VdeConstants.MaxBlockSize.Should().Be(65536);
    }

    [Fact]
    public void VdeConstants_SuperblockSize_ShouldBe512()
    {
        VdeConstants.SuperblockSize.Should().Be(512);
    }

    #endregion

    #region VdeHealthReport

    [Fact]
    public void VdeHealthReport_UsagePercent_ShouldCalculateCorrectly()
    {
        var report = new VdeHealthReport
        {
            BlockSize = 4096,
            TotalBlocks = 1000,
            FreeBlocks = 250,
            UsedBlocks = 750,
            TotalInodes = 100,
            AllocatedInodes = 50,
            WalUtilizationPercent = 10.0,
            ChecksumErrorCount = 0,
            SnapshotCount = 2,
            HealthStatus = "Healthy",
            GeneratedAtUtc = DateTimeOffset.UtcNow
        };

        report.UsagePercent.Should().Be(75.0);
    }

    [Fact]
    public void VdeHealthReport_UsagePercent_ZeroTotal_ShouldReturnZero()
    {
        var report = new VdeHealthReport
        {
            BlockSize = 4096,
            TotalBlocks = 0,
            FreeBlocks = 0,
            UsedBlocks = 0,
            TotalInodes = 0,
            AllocatedInodes = 0,
            WalUtilizationPercent = 0,
            ChecksumErrorCount = 0,
            SnapshotCount = 0,
            HealthStatus = "Healthy",
            GeneratedAtUtc = DateTimeOffset.UtcNow
        };

        report.UsagePercent.Should().Be(0.0);
    }

    [Fact]
    public void DetermineHealthStatus_Healthy_ShouldReturnHealthy()
    {
        var status = VdeHealthReport.DetermineHealthStatus(1000, 500, 30.0, 0);
        status.Should().Be("Healthy");
    }

    [Fact]
    public void DetermineHealthStatus_HighCapacity_ShouldReturnDegraded()
    {
        // Usage > 80% = Degraded
        var status = VdeHealthReport.DetermineHealthStatus(1000, 150, 30.0, 0);
        status.Should().Be("Degraded");
    }

    [Fact]
    public void DetermineHealthStatus_HighWal_ShouldReturnDegraded()
    {
        var status = VdeHealthReport.DetermineHealthStatus(1000, 500, 80.0, 0);
        status.Should().Be("Degraded");
    }

    [Fact]
    public void DetermineHealthStatus_VeryHighCapacity_ShouldReturnCritical()
    {
        // Usage > 95% = Critical
        var status = VdeHealthReport.DetermineHealthStatus(1000, 40, 30.0, 0);
        status.Should().Be("Critical");
    }

    [Fact]
    public void DetermineHealthStatus_ChecksumErrors_ShouldReturnCritical()
    {
        var status = VdeHealthReport.DetermineHealthStatus(1000, 500, 30.0, 1);
        status.Should().Be("Critical");
    }

    [Fact]
    public void DetermineHealthStatus_VeryHighWal_ShouldReturnCritical()
    {
        var status = VdeHealthReport.DetermineHealthStatus(1000, 500, 91.0, 0);
        status.Should().Be("Critical");
    }

    [Fact]
    public void ToStorageHealthInfo_ShouldConvertCorrectly()
    {
        var report = new VdeHealthReport
        {
            BlockSize = 4096,
            TotalBlocks = 1000,
            FreeBlocks = 500,
            UsedBlocks = 500,
            TotalInodes = 100,
            AllocatedInodes = 50,
            WalUtilizationPercent = 10.0,
            ChecksumErrorCount = 0,
            SnapshotCount = 1,
            HealthStatus = "Healthy",
            GeneratedAtUtc = DateTimeOffset.UtcNow
        };

        var healthInfo = report.ToStorageHealthInfo();
        healthInfo.Status.Should().Be(DataWarehouse.SDK.Contracts.Storage.HealthStatus.Healthy);
        healthInfo.TotalCapacity.Should().BeGreaterThan(0);
        healthInfo.AvailableCapacity.Should().BeGreaterThan(0);
    }

    #endregion
}
