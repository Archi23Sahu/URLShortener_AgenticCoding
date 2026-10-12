using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using static UrlShortener.IntegrationTests.TestHelpers;

namespace UrlShortener.IntegrationTests;

public class SecurityTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    private readonly HttpClient _client;

    public SecurityTests(ApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateNoRedirectClient();
    }

    // ---- Dangerous schemes and credentials: rejected at the door --------------------------------

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(document.cookie)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("file://C:/Windows/win.ini")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("ftp://example.com/file")]
    [InlineData("gopher://example.com")]
    [InlineData("http://user:pass@example.com/")]
    [InlineData("https://admin@example.com/")]
    [InlineData("not a url")]
    [InlineData("//example.com/path")]
    [InlineData("example.com")]
    [InlineData("http://")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_UnsafeOrInvalidUrl_Returns400(string url)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/shorten", new { url });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Update_UnsafeUrl_Returns400_AndLinkIsUnchanged()
    {
        var target = UniqueUrl();
        var (code, _) = await CreateAsync(_client, target);

        var response = await _client.PutAsJsonAsync($"/api/v1/shorten/{code}", new { url = "javascript:alert(1)" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await VisitAsync(_client, code)).Headers.Location!.ToString().Should().Be(target);
    }

    // ---- SQL injection ---------------------------------------------------------------------------

    [Theory]
    [InlineData("https://example.com/?q=1'; DROP TABLE ShortUrls;--")]
    [InlineData("https://example.com/?q=' OR '1'='1")]
    [InlineData("https://example.com/?q=1 UNION SELECT * FROM ClickEvents")]
    public async Task Create_SqlInjectionInUrl_IsStoredAsPlainData_AndDatabaseSurvives(string url)
    {
        var response = await _client.PostAsJsonAsync("/api/v1/shorten", new { url });

        ((int)response.StatusCode).Should().BeLessThan(500);

        // The service and table still work afterwards.
        var (code, _) = await CreateAsync(_client);
        (await VisitAsync(_client, code)).StatusCode.Should().Be(HttpStatusCode.Found);
        (await _client.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("/1';DROP TABLE ShortUrls;--")]
    [InlineData("/abc'%20OR%20'1'='1")]
    [InlineData("/api/v1/analytics/abc'%20OR%20'1'='1")]
    [InlineData("/api/v1/analytics/1;WAITFOR%20DELAY%20'0:0:5'--/daily")]
    public async Task Code_SqlInjection_NeverReachesTheDatabase_Returns404(string path)
    {
        var response = await _client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---- XSS -------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_ScriptTagInBody_IsRejected_AndNotEchoedBack()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/shorten", new { url = "<script>alert(1)</script>" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("<script>");
    }

    [Fact]
    public async Task Create_ScriptTagInsideValidUrl_IsEscapedInStoredAndRedirectedUrl()
    {
        var (code, body) = await CreateAsync(_client, "https://example.com/<script>alert(1)</script>?a=\"onmouseover=\"x");

        body.ShortUrl.Should().NotContain("<").And.NotContain(">");
        var redirect = await VisitAsync(_client, code);
        redirect.StatusCode.Should().Be(HttpStatusCode.Found);
        redirect.Headers.Location!.OriginalString.Should().NotContain("<").And.NotContain(">").And.NotContain("\"");
    }

    [Fact]
    public async Task ErrorResponses_DoNotEchoRawScriptFromThePath()
    {
        var response = await _client.GetAsync("/api/v1/analytics/%3Cscript%3Ealert(1)%3C/script%3E");

        (await response.Content.ReadAsStringAsync()).Should().NotContain("<script>");
    }

    // ---- Header injection ------------------------------------------------------------------------

    [Fact]
    public async Task Create_LineBreakInUrl_CannotInjectResponseHeaders()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/shorten",
            new { url = "https://example.com/a%0d%0aSet-Cookie:%20hacked=1" });

        ((int)response.StatusCode).Should().BeLessThan(500);

        if (response.StatusCode == HttpStatusCode.Created)
        {
            var code = CodeOf((await response.Content.ReadFromJsonAsync<UrlShortener.DTOs.ShortUrlResponse>())!);
            var redirect = await VisitAsync(_client, code);
            redirect.Headers.Contains("Set-Cookie").Should().BeFalse();
        }
    }

    // ---- Path traversal in the code --------------------------------------------------------------

    [Theory]
    [InlineData("/..%2f..%2fetc%2fpasswd")]
    [InlineData("/%2e%2e/%2e%2e/etc/passwd")]
    [InlineData("/..%5c..%5cwindows%5cwin.ini")]
    [InlineData("/api/v1/analytics/..%2f..%2fsecret")]
    [InlineData("/api/v1/shorten/..%2f..%2fx")]    
    public async Task Code_PathTraversal_IsNotFound_NeverServerError(string path)
    {
        var get = await _client.GetAsync(path);
        var delete = await _client.DeleteAsync(path);

        new[] { get.StatusCode, delete.StatusCode }.Should().OnlyContain(s =>
            s == HttpStatusCode.NotFound || s == HttpStatusCode.BadRequest || s == HttpStatusCode.MethodNotAllowed);
        (await get.Content.ReadAsStringAsync()).Should().NotContain("root:");
    }

    [Theory]
    [InlineData("")]
    [InlineData("1234567890123")]          // 13 chars: one more than the 12-character limit
    [InlineData("abc-def")]
    [InlineData("abc def")]
    [InlineData("%C3%A9%C3%A9%C3%A9")]     // non-ASCII
    public async Task Code_InvalidFormat_Returns404(string code)
    {
        var response = await _client.GetAsync("/" + code);

        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
    }

    // ---- Oversized and malformed input -----------------------------------------------------------

    [Fact]
    public async Task Create_UrlOver2048Characters_Returns400()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/shorten", new { url = "https://example.com/" + new string('a', 2100) });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_UrlOfExactlyTheLimit_IsAccepted()
    {
        var prefix = "https://example.com/";
        var url = prefix + new string('b', 2048 - prefix.Length);

        var response = await _client.PostAsJsonAsync("/api/v1/shorten", new { url });

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Created, HttpStatusCode.OK);
    }

    [Fact]
    public async Task Create_TwoMegabyteBody_Returns400_NotServerError()
    {
        var response = await _client.PostAsJsonAsync("/api/v1/shorten", new { url = "https://example.com/" + new string('c', 2_000_000) });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{\"url\": 123}")]
    [InlineData("{\"url\": null}")]
    [InlineData("{\"url\": \"https://example.com\", \"expiryDays\": \"soon\"}")]
    public async Task Create_MalformedJson_Returns400(string json)
    {
        var response = await _client.PostAsync("/api/v1/shorten", new StringContent(json, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Create_WrongContentType_Returns415()
    {
        var response = await _client.PostAsync("/api/v1/shorten", new StringContent("url=https://example.com", Encoding.UTF8, "text/plain"));

        response.StatusCode.Should().Be(HttpStatusCode.UnsupportedMediaType);
    }

    // ---- Error responses leak nothing ------------------------------------------------------------

    [Theory]
    [InlineData("/zzzzzz9")]
    [InlineData("/api/v1/analytics/zzzzzz9")]
    public async Task ErrorBodies_DoNotLeakStackTracesOrConnectionStrings(string path)
    {
        var text = await (await _client.GetAsync(path)).Content.ReadAsStringAsync();

        text.Should().NotContain("   at ").And.NotContain("Password").And.NotContain("Server=").And.NotContain("System.");
    }

    // ---- Expired link ----------------------------------------------------------------------------

    [Fact]
    public async Task ExpiredLink_Returns410_AndIsNotRedirected()
    {
        var code = await InsertLinkAsync(_factory, DateTime.UtcNow.AddSeconds(-1));

        var response = await VisitAsync(_client, code);

        response.StatusCode.Should().Be(HttpStatusCode.Gone);
        response.Headers.Location.Should().BeNull();
    }

    [Fact]
    public async Task ExpiredLink_StillReturns410OnSecondVisit_NotFromCache()
    {
        var code = await InsertLinkAsync(_factory, DateTime.UtcNow.AddSeconds(-1));

        (await VisitAsync(_client, code)).StatusCode.Should().Be(HttpStatusCode.Gone);
        (await VisitAsync(_client, code)).StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task LinkThatExpiresSoon_Redirects_ThenGone()
    {
        // Inserted with 2 seconds left: first visit redirects (and may be cached), later visit must be 410.
        var code = await InsertLinkAsync(_factory, DateTime.UtcNow.AddSeconds(2));

        (await VisitAsync(_client, code)).StatusCode.Should().Be(HttpStatusCode.Found);
        await Task.Delay(TimeSpan.FromSeconds(3));
        (await VisitAsync(_client, code)).StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task Code_NullByte_IsRejectedBeforeReachingTheApp()
    {
        // The in-memory test server refuses a null byte in the path outright; real Kestrel answers 400.
        var act = () => _client.GetAsync("/%00");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*null characters*");
    }
}
