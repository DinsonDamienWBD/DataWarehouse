using DataWarehouse.Plugins.UltimateDataLineage;
using FluentAssertions;
using Xunit;

namespace DataWarehouse.Tests.Plugins;

[Trait("Category", "Unit")]
public class UltimateDataLineageTests
{
    [Fact]
    public void Plugin_ShouldInstantiateWithStableIdentity()
    {
        var plugin = new UltimateDataLineagePlugin();
        plugin.Id.Should().NotBeNullOrWhiteSpace();
        plugin.Name.Should().Contain("Data Lineage");
        plugin.Version.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void LineageCategory_ShouldCoverAllTrackingTypes()
    {
        var categories = Enum.GetValues<LineageCategory>();
        categories.Should().Contain(LineageCategory.Origin);
        categories.Should().Contain(LineageCategory.Transformation);
        categories.Should().Contain(LineageCategory.Consumption);
        categories.Should().Contain(LineageCategory.Impact);
        categories.Should().Contain(LineageCategory.Provenance);
        categories.Should().Contain(LineageCategory.Audit);
        categories.Should().Contain(LineageCategory.Compliance);
    }

    [Fact]
    public void LineageNode_ShouldBeConstructableWithRequiredFields()
    {
        var node = new LineageNode
        {
            NodeId = "dataset-001",
            Name = "Sales Data",
            NodeType = "dataset",
            SourceSystem = "ERP",
            Path = "/data/sales",
            Tags = ["financial", "pii"]
        };
        node.NodeId.Should().Be("dataset-001");
        node.Name.Should().Be("Sales Data");
        node.NodeType.Should().Be("dataset");
        node.SourceSystem.Should().Be("ERP");
        node.Tags.Should().Contain("pii");
        node.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void LineageEdge_ShouldLinkNodes()
    {
        var edge = new LineageEdge
        {
            EdgeId = "edge-001",
            SourceNodeId = "src-001",
            TargetNodeId = "tgt-001",
            EdgeType = "transforms",
            TransformationDetails = "Aggregate by region",
            TransformationCode = "SELECT region, SUM(amount) FROM sales GROUP BY region"
        };
        edge.SourceNodeId.Should().Be("src-001");
        edge.TargetNodeId.Should().Be("tgt-001");
        edge.EdgeType.Should().Be("transforms");
        edge.TransformationCode.Should().Contain("GROUP BY");
    }

    [Fact]
    public void LineageNode_DefaultTimestamps_ShouldBeUtcNow()
    {
        var before = DateTime.UtcNow;
        var node = new LineageNode { NodeId = "n1", Name = "N1", NodeType = "test" };
        var after = DateTime.UtcNow;

        node.CreatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        node.ModifiedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }
}
