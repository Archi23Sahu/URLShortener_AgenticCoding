using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UrlShortener.Exceptions;
using UrlShortener.Middleware;

namespace UrlShortener.UnitTests.Middleware;

public class ExceptionHandlingMiddlewareTests
{
    private static async Task<(DefaultHttpContext Context, JsonElement Body)> InvokeAsync(Exception toThrow)
    {
        var context = new DefaultHttpContext();
        context.TraceIdentifier = "trace-123";
        context.Response.Body = new MemoryStream();

        var middleware = new ExceptionHandlingMiddleware(
            _ => throw toThrow,
            NullLogger<ExceptionHandlingMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var json = await reader.ReadToEndAsync();
        return (context, JsonDocument.Parse(json).RootElement.Clone());
    }

    private sealed class CapturingLogger : ILogger<ExceptionHandlingMiddleware>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    private static async Task<List<(LogLevel Level, string Message)>> LogsFromAsync(Exception toThrow)
    {
        var logger = new CapturingLogger();
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await new ExceptionHandlingMiddleware(_ => throw toThrow, logger).InvokeAsync(context);

        return logger.Entries;
    }

    // ---- UPD-AC-09: concurrency conflict maps to 409 ----

    [Fact]
    public async Task InvokeAsync_ConcurrencyConflict_Returns409ProblemJsonWithTraceIdAndTitle()
    {
        var (context, body) = await InvokeAsync(new ConcurrencyConflictException(new Exception("x")));

        context.Response.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        context.Response.ContentType.Should().Be("application/problem+json");
        body.GetProperty("traceId").GetString().Should().Be("trace-123");
        body.GetProperty("title").GetString().Should().Be("Concurrent modification");
        body.GetProperty("status").GetInt32().Should().Be(409);
    }

    // ---- Other mappings ----

    [Fact]
    public async Task InvokeAsync_NotFound_Returns404()
    {
        var (context, _) = await InvokeAsync(new NotFoundException("abc123"));

        context.Response.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        context.Response.ContentType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task InvokeAsync_LinkExpired_Returns410()
    {
        var (context, _) = await InvokeAsync(new LinkExpiredException("abc123"));

        context.Response.StatusCode.Should().Be(StatusCodes.Status410Gone);
        context.Response.ContentType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task InvokeAsync_InvalidRequest_Returns400WithMessage()
    {
        var (context, body) = await InvokeAsync(new InvalidRequestException("bad input"));

        context.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        context.Response.ContentType.Should().Be("application/problem+json");
        body.GetProperty("detail").GetString().Should().Be("bad input");
    }

    // ---- CRT-AC-17 / UPD-AC-11: oversized body is rejected by the server with 413 ----
    // The in-memory test server doesn't enforce Kestrel body limits, so the control is verified declaratively
    // (ShortenControllerTests: [RequestSizeLimit] on the actions) plus by this status mapping.

    [Fact]
    public async Task InvokeAsync_BadHttpRequest413_Returns413ProblemJsonWithoutExceptionText()
    {
        var (context, body) = await InvokeAsync(
            new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge));

        context.Response.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        context.Response.ContentType.Should().Be("application/problem+json");
        body.GetProperty("traceId").GetString().Should().Be("trace-123");
        body.GetRawText().Should().NotContain("Request body too large");
        body.TryGetProperty("detail", out _).Should().BeFalse("the exception text is server-generated and must not be echoed");
    }

    // ---- RED-AC-11: log levels ----

    [Fact]
    public async Task InvokeAsync_NotFoundAndExpired_LogNothingAboveDebug()
    {
        var notFound = await LogsFromAsync(new NotFoundException("abc123"));
        var expired = await LogsFromAsync(new LinkExpiredException("abc123"));

        notFound.Should().NotBeEmpty().And.OnlyContain(e => e.Level <= LogLevel.Debug);
        expired.Should().NotBeEmpty().And.OnlyContain(e => e.Level <= LogLevel.Debug);
    }

    [Fact]
    public async Task InvokeAsync_InvalidRequest_LogsAtInformationNotWarning()
    {
        var logs = await LogsFromAsync(new InvalidRequestException("bad input"));

        logs.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Information);
    }

    [Fact]
    public async Task InvokeAsync_UnexpectedException_LogsAtError()
    {
        var logs = await LogsFromAsync(new InvalidOperationException("boom"));

        logs.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Error);
    }

    [Fact]
    public async Task InvokeAsync_ArgumentExceptionFromElsewhere_Returns500WithoutLeakingMessage()
    {
        // Only deliberate InvalidRequestExceptions are client errors; an ArgumentException from framework
        // or library code is a server fault and its message (parameter names, internals) must not be echoed.
        var (context, body) = await InvokeAsync(new ArgumentException("internal parameter 'connectionString'"));

        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        body.GetRawText().Should().NotContain("connectionString");
    }

    [Fact]
    public async Task InvokeAsync_UnexpectedException_Returns500WithoutLeakingMessage()
    {
        var (context, body) = await InvokeAsync(
            new InvalidOperationException("Server=prod-sql;Password=secret-connection-string"));

        context.Response.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        context.Response.ContentType.Should().Be("application/problem+json");
        body.GetRawText().Should().NotContain("secret-connection-string");
        body.GetRawText().Should().NotContain("InvalidOperationException");
        body.GetProperty("traceId").GetString().Should().Be("trace-123");
    }
}
