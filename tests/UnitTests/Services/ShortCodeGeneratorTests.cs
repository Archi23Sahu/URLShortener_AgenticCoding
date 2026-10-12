using FluentAssertions;
using UrlShortener.Services;

namespace UrlShortener.UnitTests.Services;

public class ShortCodeGeneratorTests
{
    [Fact]
    public void FromId_FirstId_ReturnsSixCharacterCode()
    {
        ShortCodeGenerator.FromId(1).Should().Be("100001");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1_000)]
    [InlineData(1_000_000)]
    [InlineData(int.MaxValue)]
    public void FromId_AlwaysAtLeastSixAndAtMostTwelveCharacters(long id)
    {
        var code = ShortCodeGenerator.FromId(id);

        code.Length.Should().BeInRange(6, 12); // 12 = size of the Code column
    }

    [Fact]
    public void FromId_FirstFiveThousandIds_AreUnique()
    {
        var codes = Enumerable.Range(1, 5_000).Select(i => ShortCodeGenerator.FromId(i)).ToList();

        codes.Distinct(StringComparer.Ordinal).Should().HaveCount(5_000);
    }

    [Fact]
    public void FromId_RoundTripsThroughDecode()
    {
        var code = ShortCodeGenerator.FromId(42);

        (Base62Encoder.Decode(code) - ShortCodeGenerator.MinOffset).Should().Be(42);
    }
}
