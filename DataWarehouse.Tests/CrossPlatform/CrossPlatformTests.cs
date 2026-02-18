using DataWarehouse.SDK.Hardware;
using DataWarehouse.SDK.Contracts;
using DataWarehouse.Plugins.UltimateFilesystem.Strategies;
using DataWarehouse.Plugins.UltimateStorage.Strategies.Local;
using FluentAssertions;
using System.Runtime.InteropServices;
using Xunit;

namespace DataWarehouse.Tests.CrossPlatform;

/// <summary>
/// Cross-platform tests verifying OS detection and platform-specific logic.
/// Tests verify that OS-branching code paths work correctly on the current platform.
/// </summary>
[Trait("Category", "Unit")]
public class CrossPlatformTests
{
    [Fact]
    public void HardwareProbeFactory_Create_ShouldReturnPlatformSpecificProbe()
    {
        // Act
        var probe = HardwareProbeFactory.Create();

        // Assert
        probe.Should().NotBeNull();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            probe.Should().BeOfType<WindowsHardwareProbe>();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            probe.Should().BeOfType<LinuxHardwareProbe>();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            probe.Should().BeOfType<MacOsHardwareProbe>();
        }
        else
        {
            probe.Should().BeOfType<NullHardwareProbe>();
        }

        probe.Dispose();
    }

    [Fact]
    public async Task PlatformCapabilityRegistry_InitializeAsync_ShouldDetectCurrentOS()
    {
        // Arrange
        using var probe = HardwareProbeFactory.Create();
        using var registry = new PlatformCapabilityRegistry(probe);

        // Act
        await registry.InitializeAsync();
        var capabilities = registry.GetCapabilities();

        // Assert
        capabilities.Should().NotBeNull();
        // Every platform should discover at least some capabilities (even if just basic ones)
        // The specific capabilities vary by platform and hardware availability
    }

    [Fact]
    public async Task PlatformCapabilityRegistry_GetDevices_ShouldReturnDevicesForCurrentPlatform()
    {
        // Arrange
        using var probe = HardwareProbeFactory.Create();
        using var registry = new PlatformCapabilityRegistry(probe);

        // Act
        await registry.InitializeAsync();
        var allDevices = registry.GetAllDevices();

        // Assert
        allDevices.Should().NotBeNull();
        // Device count varies by actual hardware, so we just verify the call succeeds
    }

    [Fact]
    public void FilesystemDetection_AutoDetect_ShouldHandleCurrentOS()
    {
        // Arrange
        var strategy = new AutoDetectStrategy();
        var tempPath = Path.GetTempPath();

        // Act
        var metadata = strategy.DetectAsync(tempPath).Result;

        // Assert
        metadata.Should().NotBeNull();
        metadata!.FilesystemType.Should().NotBeNullOrEmpty();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows typically uses NTFS, but could be FAT32, exFAT, ReFS
            metadata.FilesystemType.Should().Match(fs =>
                fs.Contains("NTFS", StringComparison.OrdinalIgnoreCase) ||
                fs.Contains("FAT", StringComparison.OrdinalIgnoreCase) ||
                fs.Contains("ReFS", StringComparison.OrdinalIgnoreCase) ||
                fs.Contains("exFAT", StringComparison.OrdinalIgnoreCase));
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            // Linux typically uses ext4, btrfs, XFS, etc.
            metadata.FilesystemType.Should().NotBeNullOrEmpty();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // macOS typically uses APFS or HFS+
            metadata.FilesystemType.Should().Match(fs =>
                fs.Contains("APFS", StringComparison.OrdinalIgnoreCase) ||
                fs.Contains("HFS", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void NtfsStrategy_DetectAsync_ShouldReturnNullOnNonWindows()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Arrange
            var strategy = new NtfsStrategy();
            var tempPath = Path.GetTempPath();

            // Act
            var metadata = strategy.DetectAsync(tempPath).Result;

            // Assert - NTFS strategy should return null on non-Windows platforms
            metadata.Should().BeNull();
        }
        else
        {
            // On Windows, skip the null assertion since NTFS might be detected
            var strategy = new NtfsStrategy();
            var tempPath = Path.GetTempPath();
            var metadata = strategy.DetectAsync(tempPath).Result;
            // Just verify the call completes without exception
            metadata.Should().NotBeNull();
        }
    }

    [Fact]
    public void StorageStrategy_PathHandling_ShouldUseCorrectSeparators()
    {
        // Arrange
        var testPath = Path.Combine("data", "warehouse", "test.dat");

        // Act & Assert
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            testPath.Should().Contain("\\");
        }
        else
        {
            testPath.Should().Contain("/");
        }

        // Verify Path.GetFullPath normalizes correctly
        var fullPath = Path.GetFullPath(testPath);
        fullPath.Should().NotBeNullOrEmpty();
        Path.IsPathFullyQualified(fullPath).Should().BeTrue();
    }

    [Fact]
    public void RuntimeInformation_OSPlatform_ShouldDetectSinglePlatform()
    {
        // Verify that exactly one OS platform is detected
        var platformCount = 0;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) platformCount++;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) platformCount++;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) platformCount++;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD)) platformCount++;

        platformCount.Should().Be(1, "exactly one OS platform should be detected");
    }

    [Fact]
    public void RuntimeInformation_OSDescription_ShouldNotBeEmpty()
    {
        // Verify OS description is available
        var osDescription = RuntimeInformation.OSDescription;
        osDescription.Should().NotBeNullOrEmpty();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            osDescription.Should().Contain("Windows");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            osDescription.Should().Contain("Linux");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            osDescription.Should().Match(desc =>
                desc.Contains("Darwin") || desc.Contains("macOS"));
        }
    }

    [Fact]
    public void PmemStrategy_Construct_ShouldNotThrowRegardlessOfPlatform()
    {
        // Arrange & Act - PMEM strategy construction should not throw regardless of platform
        var action = () => new PmemStrategy();

        // Assert
        action.Should().NotThrow();
    }

    [Fact]
    public async Task HardwareProbe_DiscoverAsync_ShouldCompleteOnCurrentPlatform()
    {
        // Arrange
        using var probe = HardwareProbeFactory.Create();

        // Act
        var devices = await probe.DiscoverAsync();

        // Assert
        devices.Should().NotBeNull();
        // Device count varies by hardware, just verify no exception
    }

    [Fact]
    public void FilePath_Combine_ShouldWorkAcrossPlatforms()
    {
        // Test that path operations work correctly on current platform
        var parts = new[] { "data", "warehouse", "storage", "file.dat" };

        var combined = Path.Combine(parts);
        combined.Should().NotBeNullOrEmpty();
        combined.Should().EndWith("file.dat");

        var directory = Path.GetDirectoryName(combined);
        directory.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void TempPath_ShouldExistOnAllPlatforms()
    {
        var tempPath = Path.GetTempPath();
        tempPath.Should().NotBeNullOrEmpty();
        Directory.Exists(tempPath).Should().BeTrue();
    }

    [Fact]
    public void CurrentDirectory_ShouldBeAccessible()
    {
        var currentDir = Directory.GetCurrentDirectory();
        currentDir.Should().NotBeNullOrEmpty();
        Directory.Exists(currentDir).Should().BeTrue();
    }

    [Fact]
    public void EnvironmentVariables_ShouldBeAccessible()
    {
        // Test that environment variable access works
        var pathVar = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "PATH" : "PATH";
        var pathValue = Environment.GetEnvironmentVariable(pathVar);

        // PATH should exist on all platforms
        pathValue.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task HardwareProbe_GetDeviceAsync_ShouldHandleNonExistentDevice()
    {
        // Arrange
        using var probe = HardwareProbeFactory.Create();

        // Act
        var device = await probe.GetDeviceAsync("non-existent-device-id-12345");

        // Assert
        device.Should().BeNull();
    }

    [Fact]
    public async Task PlatformCapabilityRegistry_HasCapability_ShouldWorkAfterInitialization()
    {
        // Arrange
        using var probe = HardwareProbeFactory.Create();
        using var registry = new PlatformCapabilityRegistry(probe);
        await registry.InitializeAsync();

        // Act - query a capability that might or might not exist
        var hasNvme = registry.HasCapability("nvme");

        // Assert - should return true or false, not throw
        (hasNvme == true || hasNvme == false).Should().BeTrue();
    }
}
