using FluentAssertions;
using FluentValidation;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using UrlShortener.Configurations;
using UrlShortener.Controllers;
using UrlShortener.DTOs;
using UrlShortener.Services;
using UrlShortener.Validators;

namespace UrlShortener.UnitTests.Controllers;

public class ShortenControllerTests
{
    private static readonly DateTime ExpiresAt = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IUrlShortenerService> _service = new();
    private readonly TestMetrics _telemetry = TestMetrics.Create();

    private ShortenController Create()
    {
        // Real validators so an invalid request fails exactly as it does in the app.
        var options = Options.Create(new ShortenerOptions());
        return new ShortenController(
            _service.Object,
            new CreateShortUrlRequestValidator(options),
            new UpdateShortUrlRequestValidator(options),
            _telemetry.Metrics);
    }

    // ---- Request body size limit (CRT-AC-17, UPD-AC-11) ----
    // The in-memory test server does not enforce Kestrel body limits, so the control is verified declaratively
    // (the attribute on the action) plus by the 413 status mapping in ExceptionHandlingMiddlewareTests.

    [Theory]
    [InlineData(nameof(ShortenController.Create))]
    [InlineData(nameof(ShortenController.Update))]
    public void WriteActions_CarryRequestSizeLimitOfMaxRequestBodyBytes(string action)
    {
        var method = typeof(ShortenController).GetMethod(action)!;
        var attribute = method.GetCustomAttributes(typeof(RequestSizeLimitAttribute), inherit: true)
            .Should().ContainSingle().Subject.Should().BeOfType<RequestSizeLimitAttribute>().Subject;

        // The attribute does not expose its byte count; build the filter it creates and see what limit it applies.
        var services = new ServiceCollection().AddLogging().AddControllers().Services.BuildServiceProvider();
        var filter = attribute.CreateInstance(services).Should().BeAssignableTo<IAuthorizationFilter>().Subject;
        var feature = new FakeMaxBodySizeFeature();
        var http = new DefaultHttpContext();
        http.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);
        var actionContext = new ActionContext(http, new RouteData(), new ActionDescriptor());

        filter.OnAuthorization(new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>()));

        feature.MaxRequestBodySize.Should().Be(ShortenController.MaxRequestBodyBytes);
    }

    private sealed class FakeMaxBodySizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; }
    }

    // ---- links.created metric (OBS-AC-01) ----

    [Theory]
    [InlineData(true, "created", typeof(CreatedResult))]
    [InlineData(false, "duplicate", typeof(OkObjectResult))]
    public async Task Create_Success_RecordsLinkCreatedWithResultTag(bool created, string expectedTag, Type expectedResult)
    {
        using var linksCreated = _telemetry.Collect<long>("urlshortener.links.created");
        _service.Setup(s => s.CreateAsync(It.IsAny<CreateShortUrlRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CreateResult(new ShortUrlResponse("http://short.test/100001", ExpiresAt), created));
        var sut = Create();

        var response = await sut.Create(new CreateShortUrlRequest("https://example.com/a"), CancellationToken.None);

        response.Result.Should().BeOfType(expectedResult);
        var measurement = linksCreated.GetMeasurementSnapshot().Should().ContainSingle().Subject;
        measurement.Value.Should().Be(1);
        measurement.Tags["result"].Should().Be(expectedTag);
    }

    [Fact]
    public async Task Create_InvalidRequest_RecordsNothing()
    {
        using var linksCreated = _telemetry.Collect<long>("urlshortener.links.created");
        var sut = Create();

        Func<Task> act = () => sut.Create(new CreateShortUrlRequest("not a url"), CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>();
        linksCreated.GetMeasurementSnapshot().Should().BeEmpty();
        _service.Verify(s => s.CreateAsync(It.IsAny<CreateShortUrlRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
