using Lagerkraft.WmsCore.Layout;

namespace Lagerkraft.WmsCore.Tests;

public sealed class LocationCodePatternTests
{
    private const string Pattern = "{aisle}-{rack:2}-{level:2}-{bin:2}";

    [Theory]
    [InlineData("aisle", "A", true)]
    [InlineData("aisle", "A-01", false)]
    [InlineData("rack", "A-01", true)]
    [InlineData("rack", "A-1", false)]
    [InlineData("level", "A-01-03", true)]
    [InlineData("bin", "A-01-03-02", true)]
    [InlineData("bin", "A-1-3-2", false)]
    [InlineData("floor", "A-01-03-02", true)]
    public void IsValid_DefaultPattern(string type, string code, bool expected) =>
        LocationCodePattern.IsValid(Pattern, type, code).ShouldBe(expected);

    [Fact]
    public void LtreeLabel_ReplacesHyphen() =>
        LocationCodePattern.LtreeLabel("A-01").ShouldBe("A_01");
}
