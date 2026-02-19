using DataWarehouse.SDK.Validation;
using FluentAssertions;
using System.Text.RegularExpressions;
using Xunit;

namespace DataWarehouse.Tests.SdkTests;

[Trait("Category", "Unit")]
public class GuardsTests
{
    #region NotNull

    [Fact]
    public void NotNull_WithValidObject_ShouldReturnSameObject()
    {
        var obj = new object();
        Guards.NotNull(obj).Should().BeSameAs(obj);
    }

    [Fact]
    public void NotNull_WithNull_ShouldThrowArgumentNullException()
    {
        object? obj = null;
        var act = () => Guards.NotNull(obj);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void NotNull_WithString_ShouldReturnSameString()
    {
        var s = "hello";
        Guards.NotNull(s).Should().Be("hello");
    }

    #endregion

    #region NotNullOrWhiteSpace

    [Fact]
    public void NotNullOrWhiteSpace_WithValidString_ShouldReturnSameString()
    {
        Guards.NotNullOrWhiteSpace("hello").Should().Be("hello");
    }

    [Fact]
    public void NotNullOrWhiteSpace_WithNull_ShouldThrow()
    {
        var act = () => Guards.NotNullOrWhiteSpace(null);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void NotNullOrWhiteSpace_WithEmpty_ShouldThrow()
    {
        var act = () => Guards.NotNullOrWhiteSpace("");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void NotNullOrWhiteSpace_WithWhitespace_ShouldThrow()
    {
        var act = () => Guards.NotNullOrWhiteSpace("   ");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void NotNullOrWhiteSpace_WithTab_ShouldThrow()
    {
        var act = () => Guards.NotNullOrWhiteSpace("\t");
        act.Should().Throw<ArgumentException>();
    }

    #endregion

    #region InRange

    [Fact]
    public void InRange_Int_WithinRange_ShouldReturnValue()
    {
        Guards.InRange(5, 1, 10).Should().Be(5);
    }

    [Fact]
    public void InRange_Int_AtMin_ShouldReturnValue()
    {
        Guards.InRange(1, 1, 10).Should().Be(1);
    }

    [Fact]
    public void InRange_Int_AtMax_ShouldReturnValue()
    {
        Guards.InRange(10, 1, 10).Should().Be(10);
    }

    [Fact]
    public void InRange_Int_BelowMin_ShouldThrow()
    {
        var act = () => Guards.InRange(0, 1, 10);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void InRange_Int_AboveMax_ShouldThrow()
    {
        var act = () => Guards.InRange(11, 1, 10);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void InRange_Long_WithinRange_ShouldReturnValue()
    {
        Guards.InRange(5L, 1L, 10L).Should().Be(5L);
    }

    [Fact]
    public void InRange_Double_WithinRange_ShouldReturnValue()
    {
        Guards.InRange(5.5, 1.0, 10.0).Should().Be(5.5);
    }

    [Fact]
    public void InRange_Double_BoundaryPrecision_ShouldWork()
    {
        Guards.InRange(1.0, 1.0, 1.0).Should().Be(1.0);
    }

    #endregion

    #region Positive

    [Fact]
    public void Positive_WithPositiveInt_ShouldReturnValue()
    {
        Guards.Positive(1).Should().Be(1);
    }

    [Fact]
    public void Positive_WithZero_ShouldThrow()
    {
        var act = () => Guards.Positive(0);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Positive_WithNegative_ShouldThrow()
    {
        var act = () => Guards.Positive(-1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    #endregion

    #region SafePath

    [Fact]
    public void SafePath_WithValidPath_ShouldReturnPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "test.txt");
        Guards.SafePath(path).Should().Be(path);
    }

    [Fact]
    public void SafePath_WithTraversal_DotDot_ShouldThrow()
    {
        var act = () => Guards.SafePath("../../../etc/passwd");
        act.Should().Throw<ArgumentException>().WithMessage("*traversal*");
    }

    [Fact]
    public void SafePath_WithTraversal_Backslash_ShouldThrow()
    {
        var act = () => Guards.SafePath("..\\..\\windows\\system32");
        act.Should().Throw<ArgumentException>().WithMessage("*traversal*");
    }

    [Fact]
    public void SafePath_WithEncodedTraversal_ShouldThrow()
    {
        var act = () => Guards.SafePath("%2e%2e/etc/passwd");
        act.Should().Throw<ArgumentException>().WithMessage("*traversal*");
    }

    [Fact]
    public void SafePath_WithDoubleEncodedTraversal_ShouldThrow()
    {
        var act = () => Guards.SafePath("%252e%252e/etc/passwd");
        act.Should().Throw<ArgumentException>().WithMessage("*traversal*");
    }

    [Fact]
    public void SafePath_WithNull_ShouldThrow()
    {
        var act = () => Guards.SafePath(null!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SafePath_WithEmpty_ShouldThrow()
    {
        var act = () => Guards.SafePath("");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SafePath_WithWhitespace_ShouldThrow()
    {
        var act = () => Guards.SafePath("   ");
        act.Should().Throw<ArgumentException>();
    }

    #endregion

    #region MaxLength

    [Fact]
    public void MaxLength_WithinLimit_ShouldReturnValue()
    {
        Guards.MaxLength("hello", 10).Should().Be("hello");
    }

    [Fact]
    public void MaxLength_AtLimit_ShouldReturnValue()
    {
        Guards.MaxLength("hello", 5).Should().Be("hello");
    }

    [Fact]
    public void MaxLength_ExceedsLimit_ShouldThrow()
    {
        var act = () => Guards.MaxLength("hello world", 5);
        act.Should().Throw<ArgumentException>().WithMessage("*exceeds maximum*");
    }

    [Fact]
    public void MaxLength_WithNull_ShouldReturnNull()
    {
        // null values pass through MaxLength
        Guards.MaxLength(null, 10).Should().BeNull();
    }

    #endregion

    #region MaxSize (Stream)

    [Fact]
    public void MaxSize_StreamWithinLimit_ShouldReturnStream()
    {
        using var stream = new MemoryStream(new byte[100]);
        Guards.MaxSize(stream, 1000).Should().BeSameAs(stream);
    }

    [Fact]
    public void MaxSize_StreamAtLimit_ShouldReturnStream()
    {
        using var stream = new MemoryStream(new byte[100]);
        Guards.MaxSize(stream, 100).Should().BeSameAs(stream);
    }

    [Fact]
    public void MaxSize_StreamExceedsLimit_ShouldThrow()
    {
        using var stream = new MemoryStream(new byte[1000]);
        var act = () => Guards.MaxSize(stream, 100);
        act.Should().Throw<ArgumentException>().WithMessage("*exceeds maximum*");
    }

    [Fact]
    public void MaxSize_NullStream_ShouldThrow()
    {
        var act = () => Guards.MaxSize(null!, 100);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region MaxCount

    [Fact]
    public void MaxCount_WithinLimit_ShouldReturnCollection()
    {
        var list = new List<int> { 1, 2, 3 };
        Guards.MaxCount<int>(list, 10).Should().BeSameAs(list);
    }

    [Fact]
    public void MaxCount_AtLimit_ShouldReturnCollection()
    {
        var list = new List<int> { 1, 2, 3 };
        Guards.MaxCount<int>(list, 3).Should().BeSameAs(list);
    }

    [Fact]
    public void MaxCount_ExceedsLimit_ShouldThrow()
    {
        var list = new List<int> { 1, 2, 3, 4, 5 };
        var act = () => Guards.MaxCount<int>(list, 3);
        act.Should().Throw<ArgumentException>().WithMessage("*exceeds maximum*");
    }

    [Fact]
    public void MaxCount_NullCollection_ShouldThrow()
    {
        var act = () => Guards.MaxCount<int>(null!, 10);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region MatchesPattern

    [Fact]
    public void MatchesPattern_ValidMatch_ShouldReturnValue()
    {
        var pattern = new Regex(@"^\d+$", RegexOptions.None, Guards.DefaultRegexTimeout);
        Guards.MatchesPattern("12345", pattern).Should().Be("12345");
    }

    [Fact]
    public void MatchesPattern_NoMatch_ShouldThrow()
    {
        var pattern = new Regex(@"^\d+$", RegexOptions.None, Guards.DefaultRegexTimeout);
        var act = () => Guards.MatchesPattern("abc", pattern);
        act.Should().Throw<ArgumentException>().WithMessage("*does not match*");
    }

    [Fact]
    public void MatchesPattern_NullValue_ShouldThrow()
    {
        var pattern = new Regex(@"^\d+$", RegexOptions.None, Guards.DefaultRegexTimeout);
        var act = () => Guards.MatchesPattern(null!, pattern);
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region DefaultRegexTimeout

    [Fact]
    public void DefaultRegexTimeout_ShouldBe100ms()
    {
        Guards.DefaultRegexTimeout.Should().Be(TimeSpan.FromMilliseconds(100));
    }

    #endregion
}
