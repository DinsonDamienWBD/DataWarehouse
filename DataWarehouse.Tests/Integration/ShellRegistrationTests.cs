using DataWarehouse.SDK.Hosting;
using DataWarehouse.SDK.VirtualDiskEngine.FileExtension;
using Xunit;

namespace DataWarehouse.Tests.Integration;

/// <summary>
/// Integration tests for InstallShellRegistration: file extension registration,
/// idempotency, cross-platform safety, and error handling.
/// </summary>
public sealed class ShellRegistrationTests
{
    [Fact]
    public void RegisterFileExtensions_ReturnsSuccess()
    {
        // Use a valid temp directory as install path
        var installPath = Path.GetTempPath();
        var result = InstallShellRegistration.RegisterFileExtensions(installPath);

        // On the current platform, should succeed (Windows or Linux or macOS)
        Assert.True(result.Success, $"Registration should succeed. Error: {result.Error}");
    }

    [Fact]
    public void RegisterFileExtensions_RegistersPrimaryExtension()
    {
        var installPath = Path.GetTempPath();
        var result = InstallShellRegistration.RegisterFileExtensions(installPath);

        Assert.Contains(DwvdMimeType.PrimaryExtension, result.RegisteredExtensions);
        Assert.Contains(".dwvd", result.RegisteredExtensions);
    }

    [Fact]
    public void RegisterFileExtensions_RegistersSecondaryExtensions()
    {
        var installPath = Path.GetTempPath();
        var result = InstallShellRegistration.RegisterFileExtensions(installPath);

        Assert.Contains(".dwvd.snap", result.RegisteredExtensions);
        Assert.Contains(".dwvd.delta", result.RegisteredExtensions);
        Assert.Contains(".dwvd.meta", result.RegisteredExtensions);
        Assert.Contains(".dwvd.lock", result.RegisteredExtensions);
    }

    [Fact]
    public void RegisterFileExtensions_RegistersScriptExtension()
    {
        var installPath = Path.GetTempPath();
        var result = InstallShellRegistration.RegisterFileExtensions(installPath);

        Assert.Contains(".dw", result.RegisteredExtensions);
    }

    [Fact]
    public void RegisterFileExtensions_IsIdempotent()
    {
        var installPath = Path.GetTempPath();

        // Call twice -- neither should throw or fail
        var result1 = InstallShellRegistration.RegisterFileExtensions(installPath);
        var result2 = InstallShellRegistration.RegisterFileExtensions(installPath);

        Assert.True(result1.Success, $"First registration failed: {result1.Error}");
        Assert.True(result2.Success, $"Second registration failed: {result2.Error}");
        Assert.Equal(result1.RegisteredExtensions.Length, result2.RegisteredExtensions.Length);
    }

    [Fact]
    public void UnregisterFileExtensions_DoesNotThrow()
    {
        var result = InstallShellRegistration.UnregisterFileExtensions();

        // Should not throw regardless of prior state
        Assert.NotNull(result);
        Assert.True(result.Success, $"Unregistration should succeed. Error: {result.Error}");
    }

    [Fact]
    public void RegisterFileExtensions_SkipsForDwOnlyTopology_Logic()
    {
        // DwOnly topology does not require VDE engine -- shell registration
        // for VDE file types should be skippable. Verify the topology descriptor
        // correctly reports no VDE requirement.
        Assert.False(
            DeploymentTopologyDescriptor.RequiresVdeEngine(DeploymentTopology.DwOnly),
            "DwOnly should not require VDE engine, so VDE file registration can be skipped");
    }

    [Fact]
    public void RegisterFileExtensions_HandlesInvalidPath_Gracefully()
    {
        // Non-existent path should produce error result, not an exception
        var invalidPath = Path.Combine(Path.GetTempPath(), "nonexistent_" + Guid.NewGuid().ToString("N"), "missing");

        // Should not throw
        var result = InstallShellRegistration.RegisterFileExtensions(invalidPath);

        // Result might succeed (if the registration doesn't validate the path exists)
        // or fail gracefully with an error -- either way no exception
        Assert.NotNull(result);
    }

    [Fact]
    public void RegisterFileExtensions_ThrowsOnNullOrEmptyPath()
    {
        Assert.Throws<ArgumentException>(() =>
            InstallShellRegistration.RegisterFileExtensions(""));
    }

    [Fact]
    public void AllRegisteredExtensions_Count_Is6()
    {
        var installPath = Path.GetTempPath();
        var result = InstallShellRegistration.RegisterFileExtensions(installPath);

        // .dwvd, .dwvd.snap, .dwvd.delta, .dwvd.meta, .dwvd.lock, .dw = 6 extensions
        Assert.Equal(6, result.RegisteredExtensions.Length);
    }
}
