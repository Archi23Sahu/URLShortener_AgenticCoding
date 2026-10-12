using FluentAssertions;
using UrlShortener.Services;

namespace UrlShortener.UnitTests.Services;

public class Base62EncoderTests
{
    [Fact]
    public void Encode_Zero_ReturnsFirstAlphabetCharacter()
    {
        Base62Encoder.Encode(0).Should().Be("0");
    }

    [Theory]
    [InlineData(1, "1")]
    [InlineData(9, "9")]
    [InlineData(10, "A")]
    [InlineData(35, "Z")]
    [InlineData(36, "a")]
    [InlineData(61, "z")]
    [InlineData(62, "10")]
    [InlineData(3844, "100")]
    public void Encode_KnownValues_ReturnsExpectedCode(long value, string expected)
    {
        Base62Encoder.Encode(value).Should().Be(expected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(61)]
    [InlineData(62)]
    [InlineData(123_456_789)]
    [InlineData(long.MaxValue)]
    public void Decode_OfEncode_ReturnsOriginalValue(long value)
    {
        Base62Encoder.Decode(Base62Encoder.Encode(value)).Should().Be(value);
    }

    [Fact]
    public void Encode_NegativeValue_Throws()
    {
        Action act = () => Base62Encoder.Encode(-1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Encode_FirstTenThousandValues_AreUniqueAndCaseSensitive()
    {
        var codes = Enumerable.Range(0, 10_000).Select(i => Base62Encoder.Encode(i)).ToList();

        codes.Distinct(StringComparer.Ordinal).Should().HaveCount(10_000);
    }

    [Fact]
    public void Decode_UpperAndLowerCase_AreDifferentValues()
    {
        Base62Encoder.Decode("Ab").Should().NotBe(Base62Encoder.Decode("aB"));
    }

    [Theory]
    [InlineData("ab-c")]
    [InlineData("a b")]
    [InlineData("é")]
    public void Decode_InvalidCharacter_ThrowsFormatException(string code)
    {
        Action act = () => Base62Encoder.Decode(code);

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Decode_EmptyString_Throws()
    {
        Action act = () => Base62Encoder.Decode(string.Empty);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Decode_ValueLargerThanLong_ThrowsOverflow()
    {
        Action act = () => Base62Encoder.Decode("zzzzzzzzzzzz");

        act.Should().Throw<OverflowException>();
    }
}
