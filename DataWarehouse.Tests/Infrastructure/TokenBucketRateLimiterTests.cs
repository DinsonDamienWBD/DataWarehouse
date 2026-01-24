using DataWarehouse.SDK.Infrastructure;
using DataWarehouse.SDK.Contracts;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Infrastructure;

/// <summary>
/// Tests for TokenBucketRateLimiter behavior.
/// </summary>
public class TokenBucketRateLimiterTests
{
    #region Basic Rate Limiting

    [Fact]
    public async Task AcquireAsync_ShouldAllowWithinLimit()
    {
        // Arrange
        var config = new RateLimitConfig
        {
            PermitsPerWindow = 10,
            WindowDuration = TimeSpan.FromSeconds(1)
        };
        var limiter = new TokenBucketRateLimiter(config);

        // Act
        var result = await limiter.AcquireAsync("test", 1);

        // Assert
        result.IsAllowed.Should().BeTrue();
        result.RemainingPermits.Should().Be(9);
    }

    [Fact]
    public async Task AcquireAsync_ShouldDenyWhenExceeded()
    {
        // Arrange
        var config = new RateLimitConfig
        {
            PermitsPerWindow = 5,
            WindowDuration = TimeSpan.FromSeconds(1),
            BurstLimit = 0
        };
        var limiter = new TokenBucketRateLimiter(config);

        // Act - Exhaust all permits
        for (int i = 0; i < 5; i++)
        {
            await limiter.AcquireAsync("test", 1);
        }

        var result = await limiter.AcquireAsync("test", 1);

        // Assert
        result.IsAllowed.Should().BeFalse();
        result.RetryAfter.Should().NotBeNull();
        result.Reason.Should().Contain("Rate limit exceeded");
    }

    [Fact]
    public async Task AcquireAsync_ShouldRefillOverTime()
    {
        // Arrange
        var config = new RateLimitConfig
        {
            PermitsPerWindow = 10,
            WindowDuration = TimeSpan.FromMilliseconds(100),
            BurstLimit = 0
        };
        var limiter = new TokenBucketRateLimiter(config);

        // Exhaust permits
        for (int i = 0; i < 10; i++)
        {
            await limiter.AcquireAsync("test", 1);
        }

        // Wait for refill
        await Task.Delay(150);

        // Act
        var result = await limiter.AcquireAsync("test", 1);

        // Assert
        result.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task AcquireAsync_ShouldAllowBurst()
    {
        // Arrange
        var config = new RateLimitConfig
        {
            PermitsPerWindow = 10,
            WindowDuration = TimeSpan.FromSeconds(1),
            BurstLimit = 5 // Allow 5 extra permits for burst
        };
        var limiter = new TokenBucketRateLimiter(config);

        // Act - Request more than PermitsPerWindow but within burst
        var results = new List<RateLimitResult>();
        for (int i = 0; i < 15; i++)
        {
            results.Add(await limiter.AcquireAsync("test", 1));
        }

        // Assert - First 15 should be allowed (10 + 5 burst)
        results.Take(15).Should().AllSatisfy(r => r.IsAllowed.Should().BeTrue());
    }

    [Fact]
    public async Task AcquireAsync_ShouldTrackMultiplePermits()
    {
        // Arrange
        var config = new RateLimitConfig
        {
            PermitsPerWindow = 100,
            WindowDuration = TimeSpan.FromSeconds(1)
        };
        var limiter = new TokenBucketRateLimiter(config);

        // Act - Request 50 permits at once
        var result = await limiter.AcquireAsync("test", 50);

        // Assert
        result.IsAllowed.Should().BeTrue();
        result.RemainingPermits.Should().Be(50);
    }

    [Fact]
    public async Task AcquireAsync_ShouldDenyWhenRequestExceedsRemaining()
    {
        // Arrange
        var config = new RateLimitConfig
        {
            PermitsPerWindow = 10,
            WindowDuration = TimeSpan.FromSeconds(1),
            BurstLimit = 0
        };
        var limiter = new TokenBucketRateLimiter(config);

        // Act - Request more than available
        var result = await limiter.AcquireAsync("test", 20);

        // Assert
        result.IsAllowed.Should().BeFalse();
    }

    #endregion

    #region Key Isolation

    [Fact]
    public async Task AcquireAsync_ShouldIsolateDifferentKeys()
    {
        // Arrange
        var config = new RateLimitConfig
        {
            PermitsPerWindow = 5,
            WindowDuration = TimeSpan.FromSeconds(1)
        };
        var limiter = new TokenBucketRateLimiter(config);

        // Act - Exhaust permits for key1
        for (int i = 0; i < 5; i++)
        {
            await limiter.AcquireAsync("key1", 1);
        }

        // key1 should be exhausted, key2 should be fresh
        var result1 = await limiter.AcquireAsync("key1", 1);
        var result2 = await limiter.AcquireAsync("key2", 1);

        // Assert
        result1.IsAllowed.Should().BeFalse();
        result2.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Configure_ShouldApplyCustomConfigPerKey()
    {
        // Arrange
        var defaultConfig = new RateLimitConfig
        {
            PermitsPerWindow = 10,
            WindowDuration = TimeSpan.FromSeconds(1)
        };
        var limiter = new TokenBucketRateLimiter(defaultConfig);

        // Configure custom limit for specific key
        limiter.Configure("premium", new RateLimitConfig
        {
            PermitsPerWindow = 100,
            WindowDuration = TimeSpan.FromSeconds(1)
        });

        // Act
        var normalStatus = limiter.GetStatus("normal");
        var premiumStatus = limiter.GetStatus("premium");

        // Assert
        normalStatus.MaxPermits.Should().Be(10);
        premiumStatus.MaxPermits.Should().Be(100);
    }

    #endregion

    #region Status and Reset

    [Fact]
    public async Task GetStatus_ShouldReturnCurrentState()
    {
        // Arrange
        var config = new RateLimitConfig
        {
            PermitsPerWindow = 100,
            WindowDuration = TimeSpan.FromMinutes(1)
        };
        var limiter = new TokenBucketRateLimiter(config);

        await limiter.AcquireAsync("test", 30);

        // Act
        var status = limiter.GetStatus("test");

        // Assert
        status.Key.Should().Be("test");
        status.MaxPermits.Should().Be(100);
        status.CurrentPermits.Should().Be(70);
        status.WindowDuration.Should().Be(TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task GetStatus_ShouldReturnDefaultForUnknownKey()
    {
        // Arrange
        var config = new RateLimitConfig
        {
            PermitsPerWindow = 50,
            WindowDuration = TimeSpan.FromSeconds(30)
        };
        var limiter = new TokenBucketRateLimiter(config);

        // Act
        var status = limiter.GetStatus("unknown");

        // Assert
        status.CurrentPermits.Should().Be(50);
        status.MaxPermits.Should().Be(50);
    }

    [Fact]
    public async Task Reset_ShouldRestoreAllPermits()
    {
        // Arrange
        var config = new RateLimitConfig
        {
            PermitsPerWindow = 10,
            WindowDuration = TimeSpan.FromMinutes(1)
        };
        var limiter = new TokenBucketRateLimiter(config);

        // Exhaust permits
        for (int i = 0; i < 10; i++)
        {
            await limiter.AcquireAsync("test", 1);
        }

        (await limiter.AcquireAsync("test", 1)).IsAllowed.Should().BeFalse();

        // Act
        limiter.Reset("test");

        // Assert
        var result = await limiter.AcquireAsync("test", 1);
        result.IsAllowed.Should().BeTrue();
    }

    #endregion

    #region RetryAfter Calculation

    [Fact]
    public async Task AcquireAsync_ShouldCalculateRetryAfter()
    {
        // Arrange
        var config = new RateLimitConfig
        {
            PermitsPerWindow = 10,
            WindowDuration = TimeSpan.FromSeconds(10),
            BurstLimit = 0
        };
        var limiter = new TokenBucketRateLimiter(config);

        // Exhaust all permits
        for (int i = 0; i < 10; i++)
        {
            await limiter.AcquireAsync("test", 1);
        }

        // Act
        var result = await limiter.AcquireAsync("test", 1);

        // Assert
        result.IsAllowed.Should().BeFalse();
        result.RetryAfter.Should().NotBeNull();
        result.RetryAfter!.Value.TotalSeconds.Should().BeGreaterThan(0);
        result.RetryAfter!.Value.TotalSeconds.Should().BeLessOrEqualTo(2); // Should be ~1 second
    }

    [Fact]
    public async Task AcquireAsync_RetryAfterShouldBeProportionalToPermitsNeeded()
    {
        // Arrange
        var config = new RateLimitConfig
        {
            PermitsPerWindow = 10,
            WindowDuration = TimeSpan.FromSeconds(10)
        };
        var limiter = new TokenBucketRateLimiter(config);

        // Exhaust all permits
        for (int i = 0; i < 10; i++)
        {
            await limiter.AcquireAsync("test", 1);
        }

        // Act
        var result1 = await limiter.AcquireAsync("test", 1);
        var result5 = await limiter.AcquireAsync("test", 5);

        // Assert - Requesting 5 permits should have longer retry than 1 permit
        result5.RetryAfter!.Value.Should().BeGreaterThanOrEqualTo(result1.RetryAfter!.Value);
    }

    #endregion

    #region Thread Safety

    [Fact]
    public async Task AcquireAsync_ShouldBeThreadSafe()
    {
        // Arrange
        var config = new RateLimitConfig
        {
            PermitsPerWindow = 100,
            WindowDuration = TimeSpan.FromMinutes(1),
            BurstLimit = 0
        };
        var limiter = new TokenBucketRateLimiter(config);

        var tasks = new List<Task<RateLimitResult>>();
        var allowedCount = 0;

        // Act - 200 concurrent requests (more than limit)
        for (int i = 0; i < 200; i++)
        {
            tasks.Add(Task.Run(async () => await limiter.AcquireAsync("test", 1)));
        }

        var results = await Task.WhenAll(tasks);

        foreach (var result in results)
        {
            if (result.IsAllowed)
                Interlocked.Increment(ref allowedCount);
        }

        // Assert - Exactly 100 should be allowed
        allowedCount.Should().Be(100);
    }

    [Fact]
    public async Task AcquireAsync_ShouldBeThreadSafeAcrossMultipleKeys()
    {
        // Arrange
        var config = new RateLimitConfig
        {
            PermitsPerWindow = 50,
            WindowDuration = TimeSpan.FromMinutes(1)
        };
        var limiter = new TokenBucketRateLimiter(config);

        var tasks = new List<Task>();

        // Act - Concurrent requests to different keys
        for (int i = 0; i < 100; i++)
        {
            var keyIndex = i % 10;
            tasks.Add(Task.Run(async () => await limiter.AcquireAsync($"key{keyIndex}", 1)));
        }

        // Assert - Should not throw
        await Task.WhenAll(tasks);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task AcquireAsync_ShouldHandleZeroPermits()
    {
        // Arrange
        var limiter = new TokenBucketRateLimiter();

        // Act
        var result = await limiter.AcquireAsync("test", 0);

        // Assert - 0 permits should always be allowed
        result.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task AcquireAsync_ShouldHandleVeryLargePermitRequest()
    {
        // Arrange
        var config = new RateLimitConfig
        {
            PermitsPerWindow = 100,
            WindowDuration = TimeSpan.FromSeconds(1)
        };
        var limiter = new TokenBucketRateLimiter(config);

        // Act
        var result = await limiter.AcquireAsync("test", 1000);

        // Assert
        result.IsAllowed.Should().BeFalse();
        result.RetryAfter.Should().NotBeNull();
    }

    [Fact]
    public async Task AcquireAsync_ShouldHandleEmptyKey()
    {
        // Arrange
        var limiter = new TokenBucketRateLimiter();

        // Act - Empty string is a valid key
        var result = await limiter.AcquireAsync("", 1);

        // Assert
        result.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Limiter_ShouldHandleCancellation()
    {
        // Arrange
        var limiter = new TokenBucketRateLimiter();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert - Should complete immediately (no actual async work)
        var result = await limiter.AcquireAsync("test", 1, cts.Token);
        result.IsAllowed.Should().BeTrue();
    }

    #endregion
}
