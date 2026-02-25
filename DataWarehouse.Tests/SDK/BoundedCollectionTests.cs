using DataWarehouse.SDK.Validation;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.SdkTests;

/// <summary>
/// Tests for SDK bounded/size-constrained infrastructure: SizeLimitOptions defaults and validation.
/// Note: BoundedDictionary and BoundedQueue do not exist in the SDK. These tests cover
/// SizeLimitOptions which provides all size boundary enforcement in the SDK.
/// </summary>
[Trait("Category", "Unit")]
public class BoundedCollectionTests
{
    #region SizeLimitOptions Defaults

    [Fact]
    public void Default_MaxMessageSizeBytes_ShouldBe10MB()
    {
        var opts = SizeLimitOptions.Default;
        opts.MaxMessageSizeBytes.Should().Be(10 * 1024 * 1024);
    }

    [Fact]
    public void Default_MaxKnowledgeObjectSizeBytes_ShouldBe1MB()
    {
        var opts = SizeLimitOptions.Default;
        opts.MaxKnowledgeObjectSizeBytes.Should().Be(1 * 1024 * 1024);
    }

    [Fact]
    public void Default_MaxCapabilityPayloadSizeBytes_ShouldBe256KB()
    {
        var opts = SizeLimitOptions.Default;
        opts.MaxCapabilityPayloadSizeBytes.Should().Be(256 * 1024);
    }

    [Fact]
    public void Default_MaxStringLength_ShouldBe10000()
    {
        SizeLimitOptions.Default.MaxStringLength.Should().Be(10_000);
    }

    [Fact]
    public void Default_MaxCollectionCount_ShouldBe10000()
    {
        SizeLimitOptions.Default.MaxCollectionCount.Should().Be(10_000);
    }

    [Fact]
    public void Default_MaxStorageKeyLength_ShouldBe1024()
    {
        SizeLimitOptions.Default.MaxStorageKeyLength.Should().Be(1_024);
    }

    [Fact]
    public void Default_MaxMetadataEntries_ShouldBe100()
    {
        SizeLimitOptions.Default.MaxMetadataEntries.Should().Be(100);
    }

    [Fact]
    public void Default_MaxStorageObjectSizeBytes_ShouldBe5GB()
    {
        SizeLimitOptions.Default.MaxStorageObjectSizeBytes.Should().Be(5L * 1024 * 1024 * 1024);
    }

    #endregion

    #region SizeLimitOptions Custom Values

    [Fact]
    public void Custom_ShouldAllowOverrides()
    {
        var opts = new SizeLimitOptions
        {
            MaxMessageSizeBytes = 1024,
            MaxKnowledgeObjectSizeBytes = 512,
            MaxStringLength = 100
        };
        opts.MaxMessageSizeBytes.Should().Be(1024);
        opts.MaxKnowledgeObjectSizeBytes.Should().Be(512);
        opts.MaxStringLength.Should().Be(100);
    }

    [Fact]
    public void Default_ShouldBeSingletonInstance()
    {
        var a = SizeLimitOptions.Default;
        var b = SizeLimitOptions.Default;
        a.Should().BeSameAs(b);
    }

    #endregion

    #region Guards with SizeLimits Integration

    [Fact]
    public void Guards_MaxLength_AtSizeLimitBoundary_ShouldPass()
    {
        var value = new string('x', SizeLimitOptions.Default.MaxStringLength);
        Guards.MaxLength(value, SizeLimitOptions.Default.MaxStringLength).Should().Be(value);
    }

    [Fact]
    public void Guards_MaxLength_ExceedsSizeLimitBoundary_ShouldFail()
    {
        var value = new string('x', SizeLimitOptions.Default.MaxStringLength + 1);
        var act = () => Guards.MaxLength(value, SizeLimitOptions.Default.MaxStringLength);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Guards_MaxSize_AtMessageSizeBoundary_ShouldPass()
    {
        using var stream = new MemoryStream(new byte[SizeLimitOptions.Default.MaxMessageSizeBytes]);
        Guards.MaxSize(stream, SizeLimitOptions.Default.MaxMessageSizeBytes).Should().NotBeNull();
    }

    [Fact]
    public void Guards_MaxCount_AtCollectionCountBoundary_ShouldPass()
    {
        var items = Enumerable.Range(0, SizeLimitOptions.Default.MaxCollectionCount).ToList();
        Guards.MaxCount<int>(items, SizeLimitOptions.Default.MaxCollectionCount).Should().NotBeNull();
    }

    [Fact]
    public void Guards_MaxCount_ExceedsCollectionCountBoundary_ShouldFail()
    {
        var items = Enumerable.Range(0, SizeLimitOptions.Default.MaxCollectionCount + 1).ToList();
        var act = () => Guards.MaxCount<int>(items, SizeLimitOptions.Default.MaxCollectionCount);
        act.Should().Throw<ArgumentException>();
    }

    #endregion
}
