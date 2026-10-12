using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using UrlShortener.Analytics;
using UrlShortener.Exceptions;
using UrlShortener.Observability;
using UrlShortener.RateLimiting;
using UrlShortener.Services;

namespace UrlShortener.Controllers;

[ApiController]
[EnableRateLimiting(RateLimitPolicies.Read)]
public class RedirectController : ControllerBase
{
    private readonly IUrlShortenerService _service;
    private readonly ClickTracker _clickTracker;
    private readonly UrlShortenerMetrics _metrics;

    public RedirectController(IUrlShortenerService service, ClickTracker clickTracker, UrlShortenerMetrics metrics)
    {
        _service = service;
        _clickTracker = clickTracker;
        _metrics = metrics;
    }

    /// <summary>Redirects to the original URL with HTTP 302.</summary>
    [HttpGet("/" + ApiRoutes.CodeSegment)]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status410Gone)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> RedirectToTarget(string code, CancellationToken ct)
    {
        ResolvedLink link;
        try
        {
            link = await _service.ResolveAsync(code, ct);
        }
        catch (NotFoundException)
        {
            _metrics.Redirect(RedirectOutcome.NotFound);
            throw;
        }
        catch (LinkExpiredException)
        {
            _metrics.Redirect(RedirectOutcome.Expired);
            throw;
        }

        _metrics.Redirect(RedirectOutcome.Found);

        // Queue the click in memory and return immediately; the background worker writes it to the
        // database later. Only successful redirects are counted (404 and 410 never reach this line).
        _clickTracker.Track(HttpContext, link.Id);

        // 302 (temporary) on purpose: links can be updated, expire or be deleted,
        // and a 301 would let browsers cache the old target forever.
        return Redirect(link.Url);
    }
}
