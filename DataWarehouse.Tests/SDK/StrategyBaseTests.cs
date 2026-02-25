using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives.Configuration;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.SdkTests;

/// <summary>
/// Concrete test implementation of StrategyBase for testing abstract behavior.
/// </summary>
internal sealed class TestStrategy : StrategyBase
{
    public override string StrategyId => "test.strategy.unit";
    public override string Name => "Test Strategy";

    public bool InitCoreCalled { get; set; }
    public bool ShutdownCoreCalled { get; set; }

    protected override Task InitializeAsyncCore(CancellationToken cancellationToken)
    {
        InitCoreCalled = true;
        return Task.CompletedTask;
    }

    protected override Task ShutdownAsyncCore(CancellationToken cancellationToken)
    {
        ShutdownCoreCalled = true;
        return Task.CompletedTask;
    }

    // Expose protected members for testing
    public bool TestIsInitialized => IsInitialized;
    public void TestEnsureNotDisposed() => EnsureNotDisposed();
    public void TestThrowIfNotInitialized() => ThrowIfNotInitialized();
    public void TestIncrementCounter(string name) => IncrementCounter(name);
    public long TestGetCounter(string name) => GetCounter(name);
    public IReadOnlyDictionary<string, long> TestGetAllCounters() => GetAllCounters();
    public void TestResetCounters() => ResetCounters();
}

[Trait("Category", "Unit")]
public class StrategyBaseTests
{
    [Fact]
    public void StrategyId_ShouldReturnDeclaredId()
    {
        using var strategy = new TestStrategy();
        strategy.StrategyId.Should().Be("test.strategy.unit");
    }

    [Fact]
    public void Name_ShouldReturnDeclaredName()
    {
        using var strategy = new TestStrategy();
        strategy.Name.Should().Be("Test Strategy");
    }

    [Fact]
    public void Description_ShouldReturnDefaultDescription()
    {
        using var strategy = new TestStrategy();
        strategy.Description.Should().Be("Test Strategy strategy");
    }

    [Fact]
    public void Characteristics_ShouldReturnEmptyByDefault()
    {
        using var strategy = new TestStrategy();
        strategy.Characteristics.Should().BeEmpty();
    }

    [Fact]
    public async Task InitializeAsync_ShouldCallInitCore()
    {
        using var strategy = new TestStrategy();
        await strategy.InitializeAsync();
        strategy.InitCoreCalled.Should().BeTrue();
        strategy.TestIsInitialized.Should().BeTrue();
    }

    [Fact]
    public async Task InitializeAsync_IsIdempotent()
    {
        using var strategy = new TestStrategy();
        await strategy.InitializeAsync();
        strategy.InitCoreCalled = false; // Reset to detect second call
        await strategy.InitializeAsync();
        strategy.InitCoreCalled.Should().BeFalse("InitCore should not be called again");
    }

    [Fact]
    public async Task ShutdownAsync_ShouldCallShutdownCore()
    {
        using var strategy = new TestStrategy();
        await strategy.InitializeAsync();
        await strategy.ShutdownAsync();
        strategy.ShutdownCoreCalled.Should().BeTrue();
        strategy.TestIsInitialized.Should().BeFalse();
    }

    [Fact]
    public async Task ShutdownAsync_WithoutInitialize_ShouldNotCallCore()
    {
        using var strategy = new TestStrategy();
        await strategy.ShutdownAsync();
        strategy.ShutdownCoreCalled.Should().BeFalse();
    }

    [Fact]
    public void Dispose_ShouldWork()
    {
        var strategy = new TestStrategy();
        strategy.Dispose();
        strategy.Should().NotBeNull("Dispose should complete without throwing");
    }

    [Fact]
    public void Dispose_DoubleShouldBeSafe()
    {
        var strategy = new TestStrategy();
        strategy.Dispose();
        strategy.Dispose();
        strategy.Should().NotBeNull("Double Dispose should be safe");
    }

    [Fact]
    public async Task DisposeAsync_ShouldWork()
    {
        var strategy = new TestStrategy();
        await strategy.DisposeAsync();
        strategy.Should().NotBeNull("DisposeAsync should complete without throwing");
    }

    [Fact]
    public void EnsureNotDisposed_ShouldNotThrowWhenAlive()
    {
        using var strategy = new TestStrategy();
        var act = () => strategy.TestEnsureNotDisposed();
        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureNotDisposed_ShouldThrowAfterDispose()
    {
        var strategy = new TestStrategy();
        strategy.Dispose();
        var act = () => strategy.TestEnsureNotDisposed();
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task InitializeAsync_AfterDispose_ShouldThrow()
    {
        var strategy = new TestStrategy();
        strategy.Dispose();
        var act = async () => await strategy.InitializeAsync();
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public void ThrowIfNotInitialized_ShouldThrowWhenNotInitialized()
    {
        using var strategy = new TestStrategy();
        var act = () => strategy.TestThrowIfNotInitialized();
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task ThrowIfNotInitialized_ShouldNotThrowAfterInit()
    {
        using var strategy = new TestStrategy();
        await strategy.InitializeAsync();
        var act = () => strategy.TestThrowIfNotInitialized();
        act.Should().NotThrow();
    }

    [Fact]
    public void IncrementCounter_ShouldTrackCount()
    {
        using var strategy = new TestStrategy();
        strategy.TestIncrementCounter("reads");
        strategy.TestIncrementCounter("reads");
        strategy.TestIncrementCounter("writes");

        strategy.TestGetCounter("reads").Should().Be(2);
        strategy.TestGetCounter("writes").Should().Be(1);
    }

    [Fact]
    public void GetCounter_ShouldReturnZeroForUnknown()
    {
        using var strategy = new TestStrategy();
        strategy.TestGetCounter("nonexistent").Should().Be(0);
    }

    [Fact]
    public void GetAllCounters_ShouldReturnSnapshot()
    {
        using var strategy = new TestStrategy();
        strategy.TestIncrementCounter("a");
        strategy.TestIncrementCounter("b");

        var all = strategy.TestGetAllCounters();
        all.Should().ContainKey("a");
        all.Should().ContainKey("b");
    }

    [Fact]
    public void ResetCounters_ShouldClearAll()
    {
        using var strategy = new TestStrategy();
        strategy.TestIncrementCounter("x");
        strategy.TestResetCounters();
        strategy.TestGetCounter("x").Should().Be(0);
        strategy.TestGetAllCounters().Should().BeEmpty();
    }

    [Fact]
    public void ConfigureIntelligence_ShouldAcceptNull()
    {
        using var strategy = new TestStrategy();
        strategy.ConfigureIntelligence(null);
        strategy.Should().NotBeNull("ConfigureIntelligence(null) should not throw");
    }

    [Fact]
    public async Task Lifecycle_InitShutdownReinit()
    {
        using var strategy = new TestStrategy();
        await strategy.InitializeAsync();
        strategy.TestIsInitialized.Should().BeTrue();

        await strategy.ShutdownAsync();
        strategy.TestIsInitialized.Should().BeFalse();

        // Should be able to reinitialize
        strategy.InitCoreCalled = false;
        await strategy.InitializeAsync();
        strategy.InitCoreCalled.Should().BeTrue();
        strategy.TestIsInitialized.Should().BeTrue();
    }
}
