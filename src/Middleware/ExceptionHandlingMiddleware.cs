using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using UrlShortener.Exceptions;

namespace UrlShortener.Middleware;

/// <summary>
/// Global exception handling. Turns domain exceptions into RFC 7807 problem responses
/// and hides internal details of unexpected failures.
/// </summary>
public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            _logger.LogInformation("Request cancelled by the client: {Path}", context.Request.Path);
            context.Response.StatusCode = 499;
        }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            await WriteProblemAsync(context, ex);
        }
    }

    private async Task WriteProblemAsync(HttpContext context, Exception ex)
    {
        var (status, title) = ex switch
        {
            ValidationException => (StatusCodes.Status400BadRequest, "Validation failed"),
            InvalidRequestException => (StatusCodes.Status400BadRequest, "Invalid request"),
            NotFoundException => (StatusCodes.Status404NotFound, "Short URL not found"),
            LinkExpiredException => (StatusCodes.Status410Gone, "Short URL has expired"),
            ConcurrencyConflictException => (StatusCodes.Status409Conflict, "Concurrent modification"),
            // Raised by the server for requests it rejects itself (for example a body over the size limit).
            BadHttpRequestException bad => (bad.StatusCode is >= 400 and < 500 ? bad.StatusCode : StatusCodes.Status400BadRequest, "Request rejected"),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred")
        };

        if (status >= 500)
        {
            _logger.LogError(ex, "Unhandled exception for {Method} {Path}", context.Request.Method, context.Request.Path);
        }
        else if (ex is NotFoundException or LinkExpiredException)
        {
            // Expected and high-volume (every unknown code): don't let a probe flood the log.
            _logger.LogDebug("Request failed with {Status}: {Message}", status, ex.Message);
        }
        else
        {
            _logger.LogInformation("Request rejected with {Status}: {Message}", status, ex.Message);
        }

        ProblemDetails problem = ex is ValidationException validation
            ? new ValidationProblemDetails(
                validation.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()))
            {
                Status = status,
                Title = title
            }
            : new ProblemDetails
            {
                Status = status,
                Title = title,
                // Never leak internals for unexpected errors; server-generated rejection text isn't ours to expose.
                Detail = status >= 500 || ex is BadHttpRequestException ? null : ex.Message
            };

        problem.Extensions["traceId"] = context.TraceIdentifier;

        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsync(JsonSerializer.Serialize(problem, problem.GetType()));
    }
}
