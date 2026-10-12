using FluentAssertions;
using Microsoft.Extensions.Options;
using UrlShortener.Configurations;
using UrlShortener.DTOs;
using UrlShortener.Validators;

namespace UrlShortener.UnitTests.Validators;

public class CreateShortUrlRequestValidatorTests
{
    private readonly CreateShortUrlRequestValidator _validator =
        new(Options.Create(new ShortenerOptions { MaxExpiryDays = 365 }));

    [Theory]
    [InlineData("https://www.example.com/very/long/url")]
    [InlineData("http://example.com")]
    [InlineData("https://example.com/search?q=a%20b&x=1#frag")]
    public void ValidUrl_Passes(string url)
    {
        _validator.Validate(new CreateShortUrlRequest(url)).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a url")]
    [InlineData("ftp://example.com/file")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("http://user:pass@example.com")]
    public void InvalidUrl_Fails(string url)
    {
        var result = _validator.Validate(new CreateShortUrlRequest(url));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().OnlyContain(e => e.PropertyName == nameof(CreateShortUrlRequest.Url));
    }

    [Fact]
    public void NullUrl_Fails()
    {
        _validator.Validate(new CreateShortUrlRequest(null!)).IsValid.Should().BeFalse();
    }

    [Fact]
    public void UrlLongerThan2048Characters_Fails()
    {
        var url = "https://example.com/" + new string('a', 2100);

        _validator.Validate(new CreateShortUrlRequest(url)).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(365)]
    public void ValidExpiry_Passes(int? days)
    {
        _validator.Validate(new CreateShortUrlRequest("https://example.com", days)).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(366)]
    public void ExpiryOutsideAllowedRange_Fails(int days)
    {
        var result = _validator.Validate(new CreateShortUrlRequest("https://example.com", days));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(e => e.PropertyName == nameof(CreateShortUrlRequest.ExpiryDays));
    }
}

public class UpdateShortUrlRequestValidatorTests
{
    private readonly UpdateShortUrlRequestValidator _validator =
        new(Options.Create(new ShortenerOptions { MaxExpiryDays = 365 }));

    [Fact]
    public void ValidRequest_Passes()
    {
        _validator.Validate(new UpdateShortUrlRequest("https://example.com/new", 10)).IsValid.Should().BeTrue();
    }

    [Fact]
    public void NonHttpUrl_Fails()
    {
        _validator.Validate(new UpdateShortUrlRequest("javascript:alert(1)")).IsValid.Should().BeFalse();
    }

    [Fact]
    public void ExpiryTooLarge_Fails()
    {
        _validator.Validate(new UpdateShortUrlRequest("https://example.com", 1000)).IsValid.Should().BeFalse();
    }
}
