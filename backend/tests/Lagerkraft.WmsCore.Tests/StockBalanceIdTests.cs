using Lagerkraft.WmsCore.Api.Data;

namespace Lagerkraft.WmsCore.Tests;

public sealed class StockBalanceIdTests
{
    [Fact]
    public void For_KnownPair_MatchesRfc4122Vector()
    {
        var hu = Guid.Parse("01900000-0000-7000-8000-000000000001");
        var article = Guid.Parse("01900000-0000-7000-8000-000000000002");
        StockBalanceId.For(hu, article).ShouldBe(Guid.Parse("f4faf703-c1f7-5805-9e1f-aed95cc6831f"));
    }

    [Fact]
    public void For_SameHuTwoArticles_DistinctIds()
    {
        var hu = Guid.Parse("01900000-0000-7000-8000-000000000010");
        var a1 = Guid.Parse("01900000-0000-7000-8000-000000000011");
        var a2 = Guid.Parse("01900000-0000-7000-8000-000000000012");
        var left = StockBalanceId.For(hu, a1);
        var right = StockBalanceId.For(hu, a2);
        left.ShouldNotBe(right);
    }
}
