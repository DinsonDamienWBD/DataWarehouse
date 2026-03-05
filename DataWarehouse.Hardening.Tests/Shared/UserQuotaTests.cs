// Hardening tests for Shared findings: UserQuota
// Findings: 58 (LOW) Missing input validation, 59 (CRITICAL) RecordUsage race condition
using DataWarehouse.Shared.Models;

namespace DataWarehouse.Hardening.Tests.Shared;

/// <summary>
/// Tests for UserQuota hardening findings.
/// </summary>
public class UserQuotaTests
{
    /// <summary>
    /// Finding 58: Missing input validation on RecordUsage parameters.
    /// Fixed by adding ArgumentOutOfRangeException for negative values.
    /// </summary>
    [Fact]
    public void Finding058_RecordUsageValidatesNegativeInputTokens()
    {
        var quota = new UserQuota { UserId = "test-user" };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            quota.RecordUsage(-1, 100, 0.01m));
    }

    [Fact]
    public void Finding058_RecordUsageValidatesNegativeOutputTokens()
    {
        var quota = new UserQuota { UserId = "test-user" };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            quota.RecordUsage(100, -1, 0.01m));
    }

    [Fact]
    public void Finding058_RecordUsageValidatesNegativeCost()
    {
        var quota = new UserQuota { UserId = "test-user" };
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            quota.RecordUsage(100, 100, -0.01m));
    }

    [Fact]
    public void Finding058_RecordUsageAcceptsValidInput()
    {
        var quota = new UserQuota { UserId = "test-user" };
        quota.RecordUsage(100, 200, 0.05m);
        Assert.Equal(1, quota.RequestsToday);
        Assert.Equal(300, quota.TokensToday);
        Assert.Equal(0.05m, quota.SpentThisMonth);
    }

    /// <summary>
    /// Finding 59 (CRITICAL): RecordUsage mutates RequestsToday/TokensToday/SpentThisMonth
    /// without synchronization. Fixed by adding lock(_usageLock) around mutations.
    /// </summary>
    [Fact]
    public void Finding059_RecordUsageIsThreadSafe()
    {
        var quota = new UserQuota { UserId = "test-user" };
        const int iterations = 1000;

        // Run RecordUsage from multiple threads concurrently
        Parallel.For(0, iterations, _ =>
        {
            quota.RecordUsage(10, 20, 0.01m);
        });

        // With lock-based synchronization, all increments should be accounted for
        Assert.Equal(iterations, quota.RequestsToday);
        Assert.Equal(iterations * 30L, quota.TokensToday);
        Assert.Equal(iterations * 0.01m, quota.SpentThisMonth);
    }
}
