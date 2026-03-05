using DataWarehouse.SDK.VirtualDiskEngine.Format;

namespace DataWarehouse.Hardening.Tests.SDK;

/// <summary>
/// Hardening tests for BlockTypeTags findings 82-121.
/// These constants are 4-byte on-disk format identifiers using intentional ALL_CAPS convention
/// to match binary format specifications. The fix adds [SuppressMessage] attributes to document
/// the intentional naming deviation, while tests verify the correct hex values.
/// </summary>
public class BlockTypeTagsHardeningTests
{
    // Findings 82-110: Verify all tag constants have correct hex values (naming is intentional)
    [Theory]
    [InlineData(nameof(BlockTypeTags.Supb), 0x53555042u)]
    [InlineData(nameof(BlockTypeTags.Rmap), 0x524D4150u)]
    [InlineData(nameof(BlockTypeTags.Polv), 0x504F4C56u)]
    [InlineData(nameof(BlockTypeTags.Encr), 0x454E4352u)]
    [InlineData(nameof(BlockTypeTags.Bmap), 0x424D4150u)]
    [InlineData(nameof(BlockTypeTags.Inod), 0x494E4F44u)]
    [InlineData(nameof(BlockTypeTags.Tagi), 0x54414749u)]
    [InlineData(nameof(BlockTypeTags.Mwal), 0x4D57414Cu)]
    [InlineData(nameof(BlockTypeTags.Mtrk), 0x4D54524Bu)]
    [InlineData(nameof(BlockTypeTags.Btre), 0x42545245u)]
    [InlineData(nameof(BlockTypeTags.Snap), 0x534E4150u)]
    [InlineData(nameof(BlockTypeTags.Repl), 0x5245504Cu)]
    [InlineData(nameof(BlockTypeTags.Raid), 0x52414944u)]
    [InlineData(nameof(BlockTypeTags.Comp), 0x434F4D50u)]
    [InlineData(nameof(BlockTypeTags.Inte), 0x494E5445u)]
    [InlineData(nameof(BlockTypeTags.Stre), 0x53545245u)]
    [InlineData(nameof(BlockTypeTags.Xref), 0x58524546u)]
    [InlineData(nameof(BlockTypeTags.Worm), 0x574F524Du)]
    [InlineData(nameof(BlockTypeTags.Code), 0x434F4445u)]
    [InlineData(nameof(BlockTypeTags.Dwal), 0x4457414Cu)]
    [InlineData(nameof(BlockTypeTags.Data), 0x44415441u)]
    [InlineData(nameof(BlockTypeTags.Free), 0x46524545u)]
    [InlineData(nameof(BlockTypeTags.Cmvt), 0x434D5654u)]
    [InlineData(nameof(BlockTypeTags.Alog), 0x414C4F47u)]
    [InlineData(nameof(BlockTypeTags.Cmal), 0x434D414Cu)]
    [InlineData(nameof(BlockTypeTags.Clog), 0x434C4F47u)]
    [InlineData(nameof(BlockTypeTags.Dict), 0x44494354u)]
    [InlineData(nameof(BlockTypeTags.Anon), 0x414E4F4Eu)]
    [InlineData(nameof(BlockTypeTags.Mlog), 0x4D4C4F47u)]
    public void Finding82_110_TagConstantsUsePascalCase(string fieldName, uint expectedValue)
    {
        // Verify the PascalCase constant exists and has the correct value
        var field = typeof(BlockTypeTags).GetField(fieldName,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        Assert.NotNull(field);
        Assert.Equal(expectedValue, (uint)field!.GetValue(null)!);
    }

    // Findings 111-121: Additional block type tags
    [Theory]
    [InlineData(nameof(BlockTypeTags.Ercv), 0x45524356u)]
    [InlineData(nameof(BlockTypeTags.Rcvr), 0x52435652u)]
    [InlineData(nameof(BlockTypeTags.Extn), 0x4558544Eu)]
    [InlineData(nameof(BlockTypeTags.Exmd), 0x45584D44u)]
    [InlineData(nameof(BlockTypeTags.Iant), 0x49414E54u)]
    [InlineData(nameof(BlockTypeTags.Colr), 0x434F4C52u)]
    [InlineData(nameof(BlockTypeTags.Zmap), 0x5A4D4150u)]
    [InlineData(nameof(BlockTypeTags.Wals), 0x57414C53u)]
    [InlineData(nameof(BlockTypeTags.Znsm), 0x5A4E534Du)]
    [InlineData(nameof(BlockTypeTags.Opjr), 0x4F504A52u)]
    [InlineData(nameof(BlockTypeTags.Trlr), 0x54524C52u)]
    public void Finding111_121_ExtensionTagsUsePascalCase(string fieldName, uint expectedValue)
    {
        var field = typeof(BlockTypeTags).GetField(fieldName,
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        Assert.NotNull(field);
        Assert.Equal(expectedValue, (uint)field!.GetValue(null)!);
    }

    [Fact]
    public void Finding82_121_TagToStringStillWorks()
    {
        Assert.Equal("SUPB", BlockTypeTags.TagToString(BlockTypeTags.Supb));
        Assert.Equal("RMAP", BlockTypeTags.TagToString(BlockTypeTags.Rmap));
        Assert.Equal("TRLR", BlockTypeTags.TagToString(BlockTypeTags.Trlr));
    }

    [Fact]
    public void Finding82_121_StringToTagStillWorks()
    {
        Assert.Equal(BlockTypeTags.Supb, BlockTypeTags.StringToTag("SUPB"));
        Assert.Equal(BlockTypeTags.Data, BlockTypeTags.StringToTag("DATA"));
    }

    [Fact]
    public void Finding82_121_IsKnownTagStillWorks()
    {
        Assert.True(BlockTypeTags.IsKnownTag(BlockTypeTags.Supb));
        Assert.True(BlockTypeTags.IsKnownTag(BlockTypeTags.Data));
        Assert.False(BlockTypeTags.IsKnownTag(0xDEADBEEF));
    }
}
