using FluentAssertions;
using UrlShortener.Services;

namespace UrlShortener.UnitTests.Services;

public class UrlNormalizerTests
{
    [Theory]
    [InlineData("https://www.example.com/very/long/url", "https://www.example.com/very/long/url")]
    [InlineData("HTTP://EXAMPLE.com", "http://example.com/")]
    [InlineData("HTTPS://Example.COM:443/a", "https://example.com/a")]
    [InlineData("  https://example.com/a  ", "https://example.com/a")]
    public void TryNormalize_ValidUrl_ReturnsNormalizedForm(string input, string expected)
    {
        var ok = UrlNormalizer.TryNormalize(input, out var normalized);

        ok.Should().BeTrue();
        normalized.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("example.com")]
    [InlineData("ftp://example.com/file")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("http://user:pass@example.com")]
    public void TryNormalize_InvalidOrUnsafeUrl_ReturnsFalse(string? input)
    {
        UrlNormalizer.TryNormalize(input, out _).Should().BeFalse();
    }

    [Fact]
    public void ComputeHash_ReturnsSixtyFourLowerCaseHexCharacters()
    {
        var hash = UrlNormalizer.ComputeHash("https://example.com/");

        hash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void ComputeHash_SameUrl_SameHash_DifferentUrl_DifferentHash()
    {
        UrlNormalizer.ComputeHash("https://example.com/a").Should().Be(UrlNormalizer.ComputeHash("https://example.com/a"));
        UrlNormalizer.ComputeHash("https://example.com/a").Should().NotBe(UrlNormalizer.ComputeHash("https://example.com/b"));
    }

    [Fact]
    public void EquivalentUrls_ProduceTheSameHash()
    {
        UrlNormalizer.TryNormalize("HTTPS://Example.COM:443/a", out var first);
        UrlNormalizer.TryNormalize("https://example.com/a", out var second);

        UrlNormalizer.ComputeHash(first).Should().Be(UrlNormalizer.ComputeHash(second));
    }
}
