using DataWarehouse.Plugins.KubernetesCsi;
using DataWarehouse.SDK.Primitives;
using Xunit;

namespace DataWarehouse.Tests.Plugins;

/// <summary>
/// Tests for Kubernetes CSI plugin.
/// Validates plugin identity, CSI protocol, and volume lifecycle types.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Plugin", "KubernetesCsi")]
public class KubernetesCsiTests
{
    [Fact]
    public void KubernetesCsiPlugin_CanBeConstructed()
    {
        // Act
        var plugin = new KubernetesCsiPlugin();

        // Assert
        Assert.NotNull(plugin);
    }

    [Fact]
    public void KubernetesCsiPlugin_HasCorrectIdentity()
    {
        // Arrange
        var plugin = new KubernetesCsiPlugin();

        // Assert
        Assert.Equal("com.datawarehouse.csi.driver", plugin.Id);
        Assert.Equal("Kubernetes CSI Driver Plugin", plugin.Name);
        Assert.Equal("1.0.0", plugin.Version);
    }

    [Fact]
    public void KubernetesCsiPlugin_CategoryIsStorageProvider()
    {
        // Arrange
        var plugin = new KubernetesCsiPlugin();

        // Assert
        Assert.Equal(PluginCategory.StorageProvider, plugin.Category);
    }

    [Fact]
    public void KubernetesCsiPlugin_ProtocolIsCsi()
    {
        // Arrange
        var plugin = new KubernetesCsiPlugin();

        // Assert
        Assert.Equal("csi", plugin.Protocol);
    }

    [Fact]
    public void KubernetesCsiPlugin_PortIsNull()
    {
        // Arrange
        var plugin = new KubernetesCsiPlugin();

        // Assert - CSI uses Unix domain sockets, not TCP ports
        Assert.Null(plugin.Port);
    }
}
