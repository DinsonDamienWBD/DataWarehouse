using DataWarehouse.SDK.Contracts;
using DataWarehouse.SDK.Primitives;
using DataWarehouse.SDK.Primitives.Configuration;
using DataWarehouse.SDK.Utilities;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.SdkTests;

/// <summary>
/// Concrete test implementation of PluginBase for testing abstract behavior.
/// </summary>
internal sealed class TestPlugin : PluginBase
{
    public override string Id => "test.plugin.unit";
    public override string Name => "Test Plugin";
    public override PluginCategory Category => PluginCategory.StorageProvider;
    public override string Version => "2.1.0";

    public bool InitializeCalled { get; private set; }
    public bool ExecuteCalled { get; private set; }
    public bool ShutdownCalled { get; private set; }

    public override async Task InitializeAsync(CancellationToken ct = default)
    {
        InitializeCalled = true;
        await base.InitializeAsync(ct);
    }

    public override Task ExecuteAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ExecuteCalled = true;
        return Task.CompletedTask;
    }

    public override async Task ShutdownAsync(CancellationToken ct = default)
    {
        ShutdownCalled = true;
        await base.ShutdownAsync(ct);
    }

    // Expose protected members for testing
    public bool TestIsDisposed => IsDisposed;

    public void TestThrowIfDisposed() => ThrowIfDisposed();
}

[Trait("Category", "Unit")]
public class PluginBaseTests
{
    [Fact]
    public void Id_ShouldReturnDeclaredId()
    {
        using var plugin = new TestPlugin();
        plugin.Id.Should().Be("test.plugin.unit");
    }

    [Fact]
    public void Name_ShouldReturnDeclaredName()
    {
        using var plugin = new TestPlugin();
        plugin.Name.Should().Be("Test Plugin");
    }

    [Fact]
    public void Category_ShouldReturnDeclaredCategory()
    {
        using var plugin = new TestPlugin();
        plugin.Category.Should().Be(PluginCategory.StorageProvider);
    }

    [Fact]
    public void Version_ShouldReturnDeclaredVersion()
    {
        using var plugin = new TestPlugin();
        plugin.Version.Should().Be("2.1.0");
    }

    [Fact]
    public async Task InitializeAsync_ShouldCallDerivedInitialize()
    {
        using var plugin = new TestPlugin();
        await plugin.InitializeAsync();
        plugin.InitializeCalled.Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldCallDerivedExecute()
    {
        using var plugin = new TestPlugin();
        await plugin.ExecuteAsync();
        plugin.ExecuteCalled.Should().BeTrue();
    }

    [Fact]
    public async Task ShutdownAsync_ShouldCallDerivedShutdown()
    {
        using var plugin = new TestPlugin();
        await plugin.ShutdownAsync();
        plugin.ShutdownCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Lifecycle_InitializeExecuteShutdown_InOrder()
    {
        using var plugin = new TestPlugin();
        await plugin.InitializeAsync();
        plugin.InitializeCalled.Should().BeTrue();

        await plugin.ExecuteAsync();
        plugin.ExecuteCalled.Should().BeTrue();

        await plugin.ShutdownAsync();
        plugin.ShutdownCalled.Should().BeTrue();
    }

    [Fact]
    public void Dispose_ShouldSetIsDisposed()
    {
        var plugin = new TestPlugin();
        plugin.TestIsDisposed.Should().BeFalse();
        plugin.Dispose();
        plugin.TestIsDisposed.Should().BeTrue();
    }

    [Fact]
    public void Dispose_DoubleShouldBeSafe()
    {
        var plugin = new TestPlugin();
        plugin.Dispose();
        plugin.Dispose(); // Should not throw
        plugin.TestIsDisposed.Should().BeTrue();
    }

    [Fact]
    public async Task DisposeAsync_ShouldSetIsDisposed()
    {
        var plugin = new TestPlugin();
        await plugin.DisposeAsync();
        plugin.TestIsDisposed.Should().BeTrue();
    }

    [Fact]
    public void ThrowIfDisposed_ShouldNotThrowWhenAlive()
    {
        using var plugin = new TestPlugin();
        var act = () => plugin.TestThrowIfDisposed();
        act.Should().NotThrow();
    }

    [Fact]
    public void ThrowIfDisposed_ShouldThrowAfterDispose()
    {
        var plugin = new TestPlugin();
        plugin.Dispose();
        var act = () => plugin.TestThrowIfDisposed();
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task CheckHealthAsync_ShouldReturnHealthyByDefault()
    {
        using var plugin = new TestPlugin();
        var result = await plugin.CheckHealthAsync();
        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task InitializeAsync_WithCancelledToken_ShouldThrow()
    {
        using var plugin = new TestPlugin();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = async () => await plugin.InitializeAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ExecuteAsync_WithCancelledToken_ShouldThrow()
    {
        using var plugin = new TestPlugin();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = async () => await plugin.ExecuteAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ActivateAsync_ShouldCompleteByDefault()
    {
        using var plugin = new TestPlugin();
        await plugin.ActivateAsync();
        plugin.Should().NotBeNull("ActivateAsync no-op should complete without error");
    }

    [Fact]
    public async Task ActivateAsync_WithCancelledToken_ShouldThrow()
    {
        using var plugin = new TestPlugin();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = async () => await plugin.ActivateAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task OnMessageAsync_ShouldCompleteByDefault()
    {
        using var plugin = new TestPlugin();
        var message = new PluginMessage { Type = "test" };
        await plugin.OnMessageAsync(message);
        plugin.Should().NotBeNull("OnMessageAsync no-op should complete without error");
    }

    [Fact]
    public void SetMessageBus_ShouldAcceptNull()
    {
        using var plugin = new TestPlugin();
        plugin.SetMessageBus(null);
        plugin.Should().NotBeNull("SetMessageBus(null) should not throw");
    }

    [Fact]
    public void SetCapabilityRegistry_ShouldAcceptNull()
    {
        using var plugin = new TestPlugin();
        plugin.SetCapabilityRegistry(null);
        plugin.Should().NotBeNull("SetCapabilityRegistry(null) should not throw");
    }

    [Fact]
    public void InjectConfiguration_ShouldThrowOnNull()
    {
        using var plugin = new TestPlugin();
        var act = () => plugin.InjectConfiguration(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void InjectConfiguration_ShouldAcceptValidConfig()
    {
        using var plugin = new TestPlugin();
        var config = ConfigurationPresets.CreateStandard();
        plugin.InjectConfiguration(config);
        plugin.Should().NotBeNull("InjectConfiguration should accept valid config without throwing");
    }

    [Fact]
    public void GetRegistrationKnowledge_ShouldReturnNullWhenNoCapabilities()
    {
        using var plugin = new TestPlugin();
        var knowledge = plugin.GetRegistrationKnowledge();
        // TestPlugin has no capabilities, returns null
        knowledge.Should().BeNull();
    }

    [Fact]
    public async Task OnHandshakeAsync_ShouldReturnSuccessfulResponse()
    {
        using var plugin = new TestPlugin();
        var request = new HandshakeRequest { Timestamp = DateTime.UtcNow };
        var response = await plugin.OnHandshakeAsync(request);

        response.Should().NotBeNull();
        response.Success.Should().BeTrue();
        response.PluginId.Should().Be("test.plugin.unit");
        response.Name.Should().Be("Test Plugin");
        response.ReadyState.Should().Be(PluginReadyState.Ready);
    }

    [Fact]
    public async Task CheckHealthAsync_WithCancelledToken_ShouldThrow()
    {
        using var plugin = new TestPlugin();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var act = async () => await plugin.CheckHealthAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void InjectKernelServices_ShouldAcceptAllNulls()
    {
        using var plugin = new TestPlugin();
        plugin.InjectKernelServices(null, null, null);
        plugin.Should().NotBeNull("InjectKernelServices with nulls should not throw");
    }

    [Fact]
    public void ParseSemanticVersion_ShouldParsePlainVersion()
    {
        // Access via handshake which calls ParseSemanticVersion
        using var plugin = new TestPlugin();
        // Version is "2.1.0" so handshake response should parse it
        var task = plugin.OnHandshakeAsync(new HandshakeRequest { Timestamp = DateTime.UtcNow });
        task.Wait();
        var response = task.Result;
        response.Version.Major.Should().Be(2);
        response.Version.Minor.Should().Be(1);
        response.Version.Build.Should().Be(0);
    }
}
