using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using UrlShortener.DTOs;
using UrlShortener.RateLimiting;
using UrlShortener.Services;

namespace UrlShortener.Controllers;

[ApiController]
[EnableRateLimiting(RateLimitPolicies.Read)]
[Route("api/v1/analytics")]
[Produces("application/json")]
public class AnalyticsController : ControllerBase
{
    private readonly IUrlShortenerService _service;
    private readonly IReportService _reports;

    public AnalyticsController(IUrlShortenerService service, IReportService reports)
    {
        _service = service;
        _reports = reports;
    }

    /// <summary>Total clicks, unique visitors and clicks per country for a short URL.</summary>
    [HttpGet(ApiRoutes.CodeSegment)]
    [ProducesResponseType(typeof(AnalyticsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<AnalyticsResponse>> Get(string code, CancellationToken ct)
    {
        var analytics = await _service.GetAnalyticsAsync(code, ct);
        return Ok(analytics);
    }

    /// <summary>Clicks and unique visitors per day (UTC). Defaults to the last 30 days; days with no clicks show zero.</summary>
    /// <param name="code">Short code.</param>
    /// <param name="from">First day, yyyy-MM-dd (inclusive). Optional.</param>
    /// <param name="to">Last day, yyyy-MM-dd (inclusive). Optional, defaults to today (UTC).</param>
    [HttpGet(ApiRoutes.CodeSegment + "/daily")]
    [ProducesResponseType(typeof(DailyClicksResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<DailyClicksResponse>> Daily(
        string code, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
        Ok(await _reports.GetDailyAsync(code, from, to, ct));

    /// <summary>Clicks per country, with share of total. Clicks without a country are counted as unknown.</summary>
    [HttpGet(ApiRoutes.CodeSegment + "/countries")]
    [ProducesResponseType(typeof(CountryBreakdownResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<CountryBreakdownResponse>> Countries(
        string code, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
        Ok(await _reports.GetCountriesAsync(code, from, to, ct));

    /// <summary>Clicks per browser family (Chrome, Edge, Firefox, Safari, Opera, Internet Explorer, Bot, Other, Unknown).</summary>
    [HttpGet(ApiRoutes.CodeSegment + "/browsers")]
    [ProducesResponseType(typeof(BrowserBreakdownResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<BrowserBreakdownResponse>> Browsers(
        string code, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
        Ok(await _reports.GetBrowsersAsync(code, from, to, ct));
}
