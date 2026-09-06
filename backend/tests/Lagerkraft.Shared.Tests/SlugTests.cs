using Lagerkraft.Shared;

namespace Lagerkraft.Shared.Tests;

public sealed class SlugTests
{
    [Theory]
    [InlineData("acme")]
    [InlineData("acme-lager")]
    [InlineData("a1b")]
    [InlineData("abc")]
    public void TryCreate_ValidSlug_Succeeds(string raw)
    {
        Slug.TryCreate(raw, out var slug).ShouldBeTrue();
        slug.Value.ShouldBe(raw);
    }

    [Theory]
    [InlineData("--acme--", "acme")]
    [InlineData("  ACME  ", "acme")]
    [InlineData("acme--lager", "acme-lager")]
    [InlineData("-acme-lager-", "acme-lager")]
    public void TryCreate_PaddedOrCased_Normalizes(string raw, string expected)
    {
        Slug.TryCreate(raw, out var slug).ShouldBeTrue();
        slug.Value.ShouldBe(expected);
    }

    [Theory]
    [InlineData("www")]
    [InlineData("API")]
    [InlineData("status")]
    [InlineData("admin")]
    [InlineData("app")]
    [InlineData("docs")]
    [InlineData("--admin--")]
    public void TryCreate_ReservedWord_Fails(string raw)
    {
        Slug.TryCreate(raw, out _).ShouldBeFalse();
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("a")]
    [InlineData("")]
    [InlineData("abcdefghijklmnopqrstuvwxyz01234")]
    public void TryCreate_TooShortOrTooLong_Fails(string raw)
    {
        Slug.TryCreate(raw, out _).ShouldBeFalse();
    }
}
