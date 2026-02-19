using DataWarehouse.Plugins.SelfEmulatingObjects;
using DataWarehouse.Plugins.SelfEmulatingObjects.WasmViewer;
using DataWarehouse.SDK.Primitives;
using Xunit;

namespace DataWarehouse.Tests.Plugins;

/// <summary>
/// Tests for SelfEmulatingObjects plugin.
/// Validates plugin identity, viewer library, and self-emulating object structures.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Plugin", "SelfEmulatingObjects")]
public class SelfEmulatingObjectsTests
{
    [Fact]
    public void SelfEmulatingObjectsPlugin_CanBeConstructed()
    {
        // Act
        var plugin = new SelfEmulatingObjectsPlugin();

        // Assert
        Assert.NotNull(plugin);
    }

    [Fact]
    public void SelfEmulatingObjectsPlugin_HasCorrectIdentity()
    {
        // Arrange
        var plugin = new SelfEmulatingObjectsPlugin();

        // Assert
        Assert.Equal("com.datawarehouse.selfemulating", plugin.Id);
        Assert.Equal("Self-Emulating Objects", plugin.Name);
        Assert.Equal("1.0.0", plugin.Version);
    }

    [Fact]
    public void SelfEmulatingObjectsPlugin_CategoryIsFeatureProvider()
    {
        // Arrange
        var plugin = new SelfEmulatingObjectsPlugin();

        // Assert
        Assert.Equal(PluginCategory.FeatureProvider, plugin.Category);
    }

    [Fact]
    public void SelfEmulatingObjectsPlugin_RuntimeTypeIsSelfEmulating()
    {
        // Arrange
        var plugin = new SelfEmulatingObjectsPlugin();

        // Assert
        Assert.Equal("SelfEmulating", plugin.RuntimeType);
    }

    [Fact]
    public void SelfEmulatingObject_CanBeCreatedWithRequiredProperties()
    {
        // Act
        var obj = new SelfEmulatingObject
        {
            Id = "test-001",
            Data = new byte[] { 0x89, 0x50, 0x4E, 0x47 }, // PNG magic bytes
            ViewerWasm = new byte[] { 0x00, 0x61, 0x73, 0x6D }, // WASM magic
            Format = "png",
            ViewerName = "ImageViewer",
            ViewerVersion = "1.0.0",
            CreatedAt = DateTime.UtcNow,
            Metadata = new Dictionary<string, string> { ["format"] = "png" }
        };

        // Assert
        Assert.Equal("test-001", obj.Id);
        Assert.Equal("png", obj.Format);
        Assert.Equal("ImageViewer", obj.ViewerName);
        Assert.NotEmpty(obj.Data);
        Assert.NotEmpty(obj.ViewerWasm);
    }

    [Fact]
    public void SelfEmulatingObjectSnapshot_CanBeCreated()
    {
        // Arrange
        var innerObj = new SelfEmulatingObject
        {
            Id = "obj-001",
            Data = new byte[] { 1, 2, 3 },
            ViewerWasm = new byte[] { 0x00, 0x61, 0x73, 0x6D },
            Format = "binary",
            ViewerName = "HexViewer",
            ViewerVersion = "1.0.0",
            CreatedAt = DateTime.UtcNow,
            Metadata = new Dictionary<string, string>()
        };

        // Act
        var snapshot = new SelfEmulatingObjectSnapshot
        {
            SnapshotId = "snap-001",
            ObjectId = "obj-001",
            Object = innerObj,
            CreatedAt = DateTime.UtcNow,
            Description = "Initial snapshot"
        };

        // Assert
        Assert.Equal("snap-001", snapshot.SnapshotId);
        Assert.Equal("obj-001", snapshot.ObjectId);
        Assert.NotNull(snapshot.Object);
        Assert.Equal("Initial snapshot", snapshot.Description);
    }

    [Fact]
    public void ViewerInfo_CanBeCreatedWithRequiredProperties()
    {
        // Act
        var viewer = new ViewerInfo
        {
            Format = "pdf",
            ViewerName = "PDFViewer",
            Version = "1.0.0",
            WasmBytes = new byte[] { 0x00, 0x61, 0x73, 0x6D },
            Description = "PDF viewer",
            SupportedFeatures = new[] { "page-navigation", "zoom" }
        };

        // Assert
        Assert.Equal("pdf", viewer.Format);
        Assert.Equal("PDFViewer", viewer.ViewerName);
        Assert.Contains("page-navigation", viewer.SupportedFeatures);
    }
}
