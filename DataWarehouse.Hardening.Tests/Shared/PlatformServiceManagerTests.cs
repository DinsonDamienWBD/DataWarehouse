// Hardening tests for Shared findings: PlatformServiceManager
// Findings: 48 (CRITICAL) Command injection, 49 (LOW) PID naming
using DataWarehouse.Shared.Services;

namespace DataWarehouse.Hardening.Tests.Shared;

/// <summary>
/// Tests for PlatformServiceManager hardening findings.
/// </summary>
public class PlatformServiceManagerTests
{
    /// <summary>
    /// Finding 48 (CRITICAL): Command injection via unsanitized service names.
    /// Service names are interpolated into sc/systemctl/launchctl commands.
    /// Fixed by adding SanitizeServiceName validation on all public methods.
    /// </summary>
    [Theory]
    [InlineData("valid-service; rm -rf /")]
    [InlineData("service & echo pwned")]
    [InlineData("svc | cat /etc/passwd")]
    [InlineData("svc`whoami`")]
    [InlineData("svc$(id)")]
    [InlineData("svc\ninjected")]
    [InlineData("svc\rinjected")]
    public void Finding048_CommandInjectionPrevented(string maliciousName)
    {
        Assert.Throws<ArgumentException>(() =>
            PlatformServiceManager.SanitizeServiceName(maliciousName));
    }

    [Fact]
    public void Finding048_ValidServiceNameAccepted()
    {
        var result = PlatformServiceManager.SanitizeServiceName("DataWarehouse-Server");
        Assert.Equal("DataWarehouse-Server", result);
    }

    [Fact]
    public void Finding048_EmptyServiceNameRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            PlatformServiceManager.SanitizeServiceName(""));
        Assert.Throws<ArgumentException>(() =>
            PlatformServiceManager.SanitizeServiceName("  "));
    }

    [Fact]
    public void Finding048_NullServiceNameRejected()
    {
        Assert.Throws<ArgumentNullException>(() =>
            PlatformServiceManager.SanitizeServiceName(null!));
    }

    [Fact]
    public void Finding048_OverlongServiceNameRejected()
    {
        var longName = new string('a', 257);
        Assert.Throws<ArgumentException>(() =>
            PlatformServiceManager.SanitizeServiceName(longName));
    }

    /// <summary>
    /// Finding 49: PID -> Pid property rename on ServiceStatus.
    /// </summary>
    [Fact]
    public void Finding049_PidPropertyRenamed()
    {
        var status = new ServiceStatus
        {
            State = "Running",
            IsInstalled = true,
            IsRunning = true,
            Pid = 12345
        };

        Assert.Equal(12345, status.Pid);
    }
}
