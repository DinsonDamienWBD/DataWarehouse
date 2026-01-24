using DataWarehouse.SDK.Infrastructure;
using DataWarehouse.SDK.Contracts;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Infrastructure;

/// <summary>
/// Comprehensive tests for CircuitBreakerPolicy state transitions and behavior.
/// </summary>
public class CircuitBreakerPolicyTests
{
    #region State Transitions

    [Fact]
    public async Task CircuitBreaker_ShouldStartInClosedState()
    {
        // Arrange
        var policy = new CircuitBreakerPolicy("test");

        // Assert
        policy.State.Should().Be(CircuitState.Closed);
    }

    [Fact]
    public async Task CircuitBreaker_ShouldOpenAfterFailureThreshold()
    {
        // Arrange
        var config = new ResiliencePolicyConfig
        {
            FailureThreshold = 3,
            FailureWindow = TimeSpan.FromMinutes(1),
            MaxRetries = 0 // Disable retries for this test
        };
        var policy = new CircuitBreakerPolicy("test", config);

        // Act - Cause 3 failures
        for (int i = 0; i < 3; i++)
        {
            try
            {
                await policy.ExecuteAsync(_ => throw new Exception("Failure"), CancellationToken.None);
            }
            catch { }
        }

        // Assert
        policy.State.Should().Be(CircuitState.Open);
    }

    [Fact]
    public async Task CircuitBreaker_ShouldRejectRequestsWhenOpen()
    {
        // Arrange
        var config = new ResiliencePolicyConfig
        {
            FailureThreshold = 1,
            MaxRetries = 0,
            BreakDuration = TimeSpan.FromMinutes(1)
        };
        var policy = new CircuitBreakerPolicy("test", config);

        // Open the circuit
        try { await policy.ExecuteAsync(_ => throw new Exception("Open"), CancellationToken.None); } catch { }

        // Act & Assert
        var act = async () => await policy.ExecuteAsync(_ => Task.FromResult(42), CancellationToken.None);
        await act.Should().ThrowAsync<CircuitBreakerOpenException>();
    }

    [Fact]
    public async Task CircuitBreaker_ShouldTransitionToHalfOpenAfterBreakDuration()
    {
        // Arrange
        var config = new ResiliencePolicyConfig
        {
            FailureThreshold = 1,
            MaxRetries = 0,
            BreakDuration = TimeSpan.FromMilliseconds(100)
        };
        var policy = new CircuitBreakerPolicy("test", config);

        // Open the circuit
        try { await policy.ExecuteAsync(_ => throw new Exception(), CancellationToken.None); } catch { }
        policy.State.Should().Be(CircuitState.Open);

        // Act - Wait for break duration
        await Task.Delay(150);

        // Try to execute - should transition to half-open
        var result = await policy.ExecuteAsync(_ => Task.FromResult(42), CancellationToken.None);

        // Assert
        result.Should().Be(42);
        policy.State.Should().Be(CircuitState.Closed); // Success closes circuit
    }

    [Fact]
    public async Task CircuitBreaker_ShouldCloseAfterSuccessInHalfOpen()
    {
        // Arrange
        var config = new ResiliencePolicyConfig
        {
            FailureThreshold = 1,
            MaxRetries = 0,
            BreakDuration = TimeSpan.FromMilliseconds(50)
        };
        var policy = new CircuitBreakerPolicy("test", config);

        // Open circuit
        try { await policy.ExecuteAsync(_ => throw new Exception(), CancellationToken.None); } catch { }
        await Task.Delay(100);

        // Act - Success in half-open
        var result = await policy.ExecuteAsync(_ => Task.FromResult("success"), CancellationToken.None);

        // Assert
        policy.State.Should().Be(CircuitState.Closed);
    }

    [Fact]
    public async Task CircuitBreaker_ShouldReopenAfterFailureInHalfOpen()
    {
        // Arrange
        var config = new ResiliencePolicyConfig
        {
            FailureThreshold = 1,
            MaxRetries = 0,
            BreakDuration = TimeSpan.FromMilliseconds(50)
        };
        var policy = new CircuitBreakerPolicy("test", config);

        // Open circuit
        try { await policy.ExecuteAsync(_ => throw new Exception(), CancellationToken.None); } catch { }
        await Task.Delay(100);

        // Act - Failure in half-open
        try
        {
            await policy.ExecuteAsync<int>(_ => throw new Exception("fail again"), CancellationToken.None);
        }
        catch { }

        // Assert - Should be open again
        policy.State.Should().Be(CircuitState.Open);
    }

    [Fact]
    public async Task Reset_ShouldCloseCircuitImmediately()
    {
        // Arrange
        var config = new ResiliencePolicyConfig
        {
            FailureThreshold = 1,
            MaxRetries = 0,
            BreakDuration = TimeSpan.FromHours(1) // Very long
        };
        var policy = new CircuitBreakerPolicy("test", config);

        // Open circuit
        try { await policy.ExecuteAsync(_ => throw new Exception(), CancellationToken.None); } catch { }
        policy.State.Should().Be(CircuitState.Open);

        // Act
        policy.Reset();

        // Assert
        policy.State.Should().Be(CircuitState.Closed);

        // Should be able to execute
        var result = await policy.ExecuteAsync(_ => Task.FromResult(42), CancellationToken.None);
        result.Should().Be(42);
    }

    #endregion

    #region Retry Behavior

    [Fact]
    public async Task CircuitBreaker_ShouldRetryOnTransientFailure()
    {
        // Arrange
        var config = new ResiliencePolicyConfig
        {
            MaxRetries = 3,
            RetryBaseDelay = TimeSpan.FromMilliseconds(10),
            RetryMaxDelay = TimeSpan.FromMilliseconds(100),
            FailureThreshold = 10 // High threshold so circuit doesn't open
        };
        var policy = new CircuitBreakerPolicy("test", config);

        var attempts = 0;

        // Act - Fail twice, then succeed
        var result = await policy.ExecuteAsync(_ =>
        {
            attempts++;
            if (attempts < 3)
                throw new IOException("Transient failure");
            return Task.FromResult("success");
        }, CancellationToken.None);

        // Assert
        result.Should().Be("success");
        attempts.Should().Be(3);
    }

    [Fact]
    public async Task CircuitBreaker_ShouldNotRetryNonRetryableExceptions()
    {
        // Arrange
        var config = new ResiliencePolicyConfig
        {
            MaxRetries = 3,
            FailureThreshold = 10
        };
        var policy = new CircuitBreakerPolicy("test", config);

        var attempts = 0;

        // Act & Assert - ArgumentException is non-retryable
        var act = async () => await policy.ExecuteAsync<int>(_ =>
        {
            attempts++;
            throw new ArgumentException("Invalid argument");
        }, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentException>();
        attempts.Should().Be(1); // No retries
    }

    [Fact]
    public async Task CircuitBreaker_ShouldRespectMaxRetries()
    {
        // Arrange
        var config = new ResiliencePolicyConfig
        {
            MaxRetries = 2,
            RetryBaseDelay = TimeSpan.FromMilliseconds(1),
            FailureThreshold = 10
        };
        var policy = new CircuitBreakerPolicy("test", config);

        var attempts = 0;

        // Act & Assert - Should fail after 3 attempts (1 initial + 2 retries)
        var act = async () => await policy.ExecuteAsync<int>(_ =>
        {
            attempts++;
            throw new IOException("Always fails");
        }, CancellationToken.None);

        await act.Should().ThrowAsync<IOException>();
        attempts.Should().Be(3);
    }

    #endregion

    #region Timeout Behavior

    [Fact]
    public async Task CircuitBreaker_ShouldTimeoutSlowOperations()
    {
        // Arrange
        var config = new ResiliencePolicyConfig
        {
            Timeout = TimeSpan.FromMilliseconds(100),
            MaxRetries = 0,
            FailureThreshold = 10
        };
        var policy = new CircuitBreakerPolicy("test", config);

        // Act & Assert
        var act = async () => await policy.ExecuteAsync(async _ =>
        {
            await Task.Delay(5000);
            return 42;
        }, CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
    }

    [Fact]
    public async Task CircuitBreaker_ShouldTrackTimeoutStatistics()
    {
        // Arrange
        var config = new ResiliencePolicyConfig
        {
            Timeout = TimeSpan.FromMilliseconds(50),
            MaxRetries = 0,
            FailureThreshold = 10
        };
        var policy = new CircuitBreakerPolicy("test", config);

        // Act - Cause timeout
        try
        {
            await policy.ExecuteAsync(async _ =>
            {
                await Task.Delay(1000);
                return 42;
            }, CancellationToken.None);
        }
        catch { }

        // Assert
        var stats = policy.GetStatistics();
        stats.TimeoutExecutions.Should().Be(1);
    }

    #endregion

    #region Statistics

    [Fact]
    public async Task GetStatistics_ShouldTrackSuccessfulExecutions()
    {
        // Arrange
        var policy = new CircuitBreakerPolicy("test");

        // Act
        await policy.ExecuteAsync(_ => Task.FromResult(1), CancellationToken.None);
        await policy.ExecuteAsync(_ => Task.FromResult(2), CancellationToken.None);
        await policy.ExecuteAsync(_ => Task.FromResult(3), CancellationToken.None);

        // Assert
        var stats = policy.GetStatistics();
        stats.TotalExecutions.Should().Be(3);
        stats.SuccessfulExecutions.Should().Be(3);
        stats.FailedExecutions.Should().Be(0);
    }

    [Fact]
    public async Task GetStatistics_ShouldTrackFailedExecutions()
    {
        // Arrange
        var config = new ResiliencePolicyConfig { MaxRetries = 0, FailureThreshold = 10 };
        var policy = new CircuitBreakerPolicy("test", config);

        // Act
        for (int i = 0; i < 5; i++)
        {
            try { await policy.ExecuteAsync<int>(_ => throw new Exception(), CancellationToken.None); } catch { }
        }

        // Assert
        var stats = policy.GetStatistics();
        stats.FailedExecutions.Should().Be(5);
    }

    [Fact]
    public async Task GetStatistics_ShouldTrackCircuitBreakerRejections()
    {
        // Arrange
        var config = new ResiliencePolicyConfig
        {
            FailureThreshold = 1,
            MaxRetries = 0,
            BreakDuration = TimeSpan.FromMinutes(1)
        };
        var policy = new CircuitBreakerPolicy("test", config);

        // Open circuit
        try { await policy.ExecuteAsync<int>(_ => throw new Exception(), CancellationToken.None); } catch { }

        // Act - Try to execute multiple times while open
        for (int i = 0; i < 3; i++)
        {
            try { await policy.ExecuteAsync(_ => Task.FromResult(42), CancellationToken.None); } catch { }
        }

        // Assert
        var stats = policy.GetStatistics();
        stats.CircuitBreakerRejections.Should().Be(3);
    }

    [Fact]
    public async Task GetStatistics_ShouldTrackRetryAttempts()
    {
        // Arrange
        var config = new ResiliencePolicyConfig
        {
            MaxRetries = 2,
            RetryBaseDelay = TimeSpan.FromMilliseconds(1),
            FailureThreshold = 10
        };
        var policy = new CircuitBreakerPolicy("test", config);

        // Act - Fail all attempts
        try { await policy.ExecuteAsync<int>(_ => throw new IOException(), CancellationToken.None); } catch { }

        // Assert
        var stats = policy.GetStatistics();
        stats.RetryAttempts.Should().Be(2);
    }

    [Fact]
    public async Task GetStatistics_ShouldTrackAverageExecutionTime()
    {
        // Arrange
        var policy = new CircuitBreakerPolicy("test");

        // Act
        await policy.ExecuteAsync(async _ =>
        {
            await Task.Delay(50);
            return 1;
        }, CancellationToken.None);

        // Assert
        var stats = policy.GetStatistics();
        stats.AverageExecutionTime.Should().BeGreaterThan(TimeSpan.FromMilliseconds(30));
    }

    #endregion

    #region ResiliencePolicyManager Tests

    [Fact]
    public void PolicyManager_ShouldCreatePoliciesOnDemand()
    {
        // Arrange
        var manager = new ResiliencePolicyManager();

        // Act
        var policy1 = manager.GetPolicy("policy1");
        var policy2 = manager.GetPolicy("policy2");
        var policy1Again = manager.GetPolicy("policy1");

        // Assert
        policy1.Should().NotBeNull();
        policy2.Should().NotBeNull();
        policy1.Should().BeSameAs(policy1Again);
    }

    [Fact]
    public void PolicyManager_ShouldUseRegisteredConfig()
    {
        // Arrange
        var manager = new ResiliencePolicyManager();
        var customConfig = new ResiliencePolicyConfig
        {
            FailureThreshold = 10,
            MaxRetries = 5
        };

        // Act
        manager.RegisterPolicy("custom", customConfig);
        var policy = manager.GetPolicy("custom");

        // Assert
        policy.Should().NotBeNull();
        policy.PolicyId.Should().Be("custom");
    }

    [Fact]
    public void PolicyManager_ResetAll_ShouldResetAllPolicies()
    {
        // Arrange
        var config = new ResiliencePolicyConfig { FailureThreshold = 1, MaxRetries = 0 };
        var manager = new ResiliencePolicyManager(config);

        var policy1 = manager.GetPolicy("p1");
        var policy2 = manager.GetPolicy("p2");

        // Open both circuits
        try { policy1.ExecuteAsync<int>(_ => throw new Exception(), CancellationToken.None).Wait(); } catch { }
        try { policy2.ExecuteAsync<int>(_ => throw new Exception(), CancellationToken.None).Wait(); } catch { }

        policy1.State.Should().Be(CircuitState.Open);
        policy2.State.Should().Be(CircuitState.Open);

        // Act
        manager.ResetAll();

        // Assert
        policy1.State.Should().Be(CircuitState.Closed);
        policy2.State.Should().Be(CircuitState.Closed);
    }

    [Fact]
    public void PolicyManager_GetPolicyKeys_ShouldReturnAllKeys()
    {
        // Arrange
        var manager = new ResiliencePolicyManager();
        manager.GetPolicy("a");
        manager.GetPolicy("b");
        manager.GetPolicy("c");

        // Act
        var keys = manager.GetPolicyKeys().ToList();

        // Assert
        keys.Should().BeEquivalentTo(["a", "b", "c"]);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task CircuitBreaker_ShouldHandleCancellation()
    {
        // Arrange
        var policy = new CircuitBreakerPolicy("test");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        var act = async () => await policy.ExecuteAsync(_ => Task.FromResult(42), cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task CircuitBreaker_ShouldBeThreadSafe()
    {
        // Arrange
        var config = new ResiliencePolicyConfig { FailureThreshold = 100, MaxRetries = 0 };
        var policy = new CircuitBreakerPolicy("test", config);

        var tasks = new List<Task>();
        var successCount = 0;

        // Act - Concurrent executions
        for (int i = 0; i < 100; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                var result = await policy.ExecuteAsync(_ => Task.FromResult(1), CancellationToken.None);
                Interlocked.Add(ref successCount, result);
            }));
        }

        await Task.WhenAll(tasks);

        // Assert
        successCount.Should().Be(100);
        var stats = policy.GetStatistics();
        stats.TotalExecutions.Should().Be(100);
    }

    #endregion
}
