using DataWarehouse.SDK.VirtualDiskEngine.AdaptiveIndex;

namespace DataWarehouse.Hardening.Tests.SDK;

/// <summary>
/// Hardening tests for ArtNode findings 38-45.
/// Verifies that ART node types work correctly after field renaming.
/// </summary>
public class ArtNodeHardeningTests
{
    [Fact]
    public void Finding38_45_Node4AddAndFindChild()
    {
        var node = new ArtNode.Node4();
        var leaf = new ArtNode.Leaf(new byte[] { 1, 2, 3 }, 42);
        var result = node.AddChild(0x41, leaf);
        Assert.Same(node, result); // Should return same node (not grown)
        Assert.Equal(1, result.ChildCount);

        var found = result.FindChild(0x41);
        Assert.Same(leaf, found);
    }

    [Fact]
    public void Finding38_45_Node4GrowsToNode16()
    {
        var node = new ArtNode.Node4();
        ArtNode current = node;
        for (byte i = 0; i < 5; i++)
        {
            current = current.AddChild(i, new ArtNode.Leaf(new byte[] { i }, i));
        }
        // After 5 children, should have grown to Node16
        Assert.IsType<ArtNode.Node16>(current);
        Assert.Equal(5, current.ChildCount);
    }

    [Fact]
    public void Finding38_45_Node16FindChildWorksAfterRename()
    {
        ArtNode current = new ArtNode.Node4();
        for (byte i = 0; i < 10; i++)
        {
            current = current.AddChild(i, new ArtNode.Leaf(new byte[] { i }, i));
        }
        Assert.IsType<ArtNode.Node16>(current);

        for (byte i = 0; i < 10; i++)
        {
            var child = current.FindChild(i);
            Assert.NotNull(child);
            Assert.IsType<ArtNode.Leaf>(child);
        }
    }

    [Fact]
    public void Finding38_45_Node48FindChildWorksAfterRename()
    {
        ArtNode current = new ArtNode.Node4();
        for (byte i = 0; i < 20; i++)
        {
            current = current.AddChild(i, new ArtNode.Leaf(new byte[] { i }, i));
        }
        Assert.IsType<ArtNode.Node48>(current);

        for (byte i = 0; i < 20; i++)
        {
            Assert.NotNull(current.FindChild(i));
        }
    }

    [Fact]
    public void Finding38_45_Node256FindChildWorksAfterRename()
    {
        ArtNode current = new ArtNode.Node4();
        for (int i = 0; i < 50; i++)
        {
            current = current.AddChild((byte)i, new ArtNode.Leaf(new byte[] { (byte)i }, i));
        }
        Assert.IsType<ArtNode.Node256>(current);

        for (int i = 0; i < 50; i++)
        {
            Assert.NotNull(current.FindChild((byte)i));
        }
    }

    [Fact]
    public void Finding38_45_NodeRemoveChildAndShrink()
    {
        ArtNode current = new ArtNode.Node4();
        for (byte i = 0; i < 5; i++)
        {
            current = current.AddChild(i, new ArtNode.Leaf(new byte[] { i }, i));
        }
        Assert.IsType<ArtNode.Node16>(current);

        // Remove children to trigger shrink back to Node4
        current = current.RemoveChild(4);
        current = current.RemoveChild(3);
        Assert.IsType<ArtNode.Node4>(current);
        Assert.Equal(3, current.ChildCount);
    }

    [Fact]
    public void Finding38_45_ForEachChildIteratesInOrder()
    {
        var node = new ArtNode.Node4();
        ArtNode current = node;
        current = current.AddChild(0x03, new ArtNode.Leaf(new byte[] { 3 }, 3));
        current = current.AddChild(0x01, new ArtNode.Leaf(new byte[] { 1 }, 1));
        current = current.AddChild(0x02, new ArtNode.Leaf(new byte[] { 2 }, 2));

        var keys = new List<byte>();
        current.ForEachChild((k, _) => keys.Add(k));
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, keys.ToArray());
    }

    [Fact]
    public void Finding38_45_LeafMatchesCorrectKey()
    {
        var leaf = new ArtNode.Leaf(new byte[] { 1, 2, 3 }, 42);
        Assert.True(leaf.Matches(new byte[] { 1, 2, 3 }));
        Assert.False(leaf.Matches(new byte[] { 1, 2, 4 }));
        Assert.False(leaf.Matches(new byte[] { 1, 2 }));
    }

    [Fact]
    public void Finding38_45_CheckPrefixWorks()
    {
        var node = new ArtNode.Node4
        {
            Prefix = new byte[] { 0x41, 0x42, 0x43 },
            PrefixLength = 3
        };
        Assert.Equal(3, node.CheckPrefix(new byte[] { 0x41, 0x42, 0x43, 0x44 }, 0));
        Assert.Equal(2, node.CheckPrefix(new byte[] { 0x41, 0x42, 0xFF, 0x44 }, 0));
        Assert.Equal(0, node.CheckPrefix(new byte[] { 0xFF, 0x42, 0x43, 0x44 }, 0));
    }
}
