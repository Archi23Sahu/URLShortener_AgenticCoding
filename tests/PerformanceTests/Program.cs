using System.Diagnostics;
using System.Net.Http.Json;

// Load tests (requirement: 1000 concurrent requests, redirect throughput, database load).
//
//   dotnet run --project tests/PerformanceTests -c Release -- [baseUrl] [requests] [concurrency] [mode]
//   defaults:                                                  https://localhost:7037  1000  1000  redirect
//
// Modes:
//   redirect  GET /{code} on one warmed-up link: redirect throughput, mostly Redis cache hits.
//   create    POST /api/v1/shorten with a new URL each time: database write load (insert + code update per request).
//   mixed     80% redirects over 20 links, 10% creates, 10% analytics reads: a realistic blend hitting cache and database.
//
// Run the API with rate limiting OFF (it is on by default, and all requests come from one IP):
//   PowerShell:  $env:RateLimit__Enabled="false"; dotnet run --project src --urls https://localhost:7037
//   Compose:     uncomment RateLimit__Enabled in docker-compose.yml

var baseUrl = args.Length > 0 ? args[0].TrimEnd('/') : "https://localhost:7037";
var total = args.Length > 1 ? int.Parse(args[1]) : 1000;
var concurrency = args.Length > 2 ? int.Parse(args[2]) : 1000;
var mode = args.Length > 3 ? args[3].ToLowerInvariant() : "redirect";

if (mode is not ("redirect" or "create" or "mixed"))
{
    Console.Error.WriteLine($"Unknown mode '{mode}'. Use redirect, create or mixed.");
    return 2;
}

var handler = new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    MaxConnectionsPerServer = int.MaxValue,
    PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    // Local development certificate only.
    SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true }
};
using var client = new HttpClient(handler) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(60) };

Console.WriteLine($"Target: {baseUrl}   requests: {total}   concurrency: {concurrency}   mode: {mode}");

// 1. Links to redirect to (redirect/mixed), each warmed up once so the first cache miss is not in the numbers.
var codes = new List<string>();
if (mode != "create")
{
    for (var i = 0; i < (mode == "mixed" ? 20 : 1); i++)
    {
        var code = await CreateLinkAsync(client);
        (await client.GetAsync("/" + code)).Dispose();
        codes.Add(code);
    }

    Console.WriteLine($"Test links: {string.Join(", ", codes)}");
}

// 2. Fire all requests, at most `concurrency` at the same time.
var samples = new Sample[total];
using var gate = new SemaphoreSlim(concurrency);
var clock = Stopwatch.StartNew();

var tasks = Enumerable.Range(0, total).Select(async i =>
{
    var operation = PickOperation(mode, i);
    await gate.WaitAsync();
    var sw = Stopwatch.StartNew();
    int status;
    try
    {
        using var response = operation switch
        {
            "create" => await client.PostAsJsonAsync("/api/v1/shorten", new { url = $"https://example.com/perf-{Guid.NewGuid():N}" }),
            "analytics" => await client.GetAsync($"/api/v1/analytics/{codes[i % codes.Count]}", HttpCompletionOption.ResponseHeadersRead),
            _ => await client.GetAsync("/" + codes[i % codes.Count], HttpCompletionOption.ResponseHeadersRead)
        };
        status = (int)response.StatusCode;
    }
    catch
    {
        status = 0; // connection error / timeout
    }
    finally
    {
        gate.Release();
    }

    samples[i] = new Sample(operation, status, sw.Elapsed.TotalMilliseconds);
}).ToArray();

await Task.WhenAll(tasks);
clock.Stop();

// 3. Report, overall and per operation.
Console.WriteLine();
Console.WriteLine($"Total time        : {clock.Elapsed.TotalSeconds:F2} s");
Console.WriteLine($"Throughput        : {total / clock.Elapsed.TotalSeconds:F0} requests/s");

var unexpected = 0;
foreach (var group in samples.GroupBy(s => s.Operation).OrderBy(g => g.Key))
{
    var sorted = group.Select(s => s.LatencyMs).OrderBy(x => x).ToArray();
    double P(double percent) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(percent / 100 * sorted.Length) - 1)];
    var expected = ExpectedStatus(group.Key);
    var ok = group.Count(s => s.Status == expected);
    var limited = group.Count(s => s.Status == 429);
    var failed = group.Count(s => s.Status == 0);
    var other = group.Count() - ok - limited - failed;
    unexpected += limited + failed + other;

    Console.WriteLine();
    Console.WriteLine($"[{group.Key}] {group.Count()} requests");
    Console.WriteLine($"  Latency p50/p95/p99/max : {P(50):F1} / {P(95):F1} / {P(99):F1} / {sorted[^1]:F1} ms");
    Console.WriteLine($"  {expected} (expected)       : {ok}");
    Console.WriteLine($"  429 rate limited      : {limited}{(limited > 0 ? "   <-- rate limiting is ON; restart the API with RateLimit__Enabled=false" : "")}");
    Console.WriteLine($"  Other status          : {other}");
    Console.WriteLine($"  Connection errors     : {failed}");
}

Console.WriteLine();
Console.WriteLine("Server-side view: open the Aspire dashboard (http://127.0.0.1:18888) for request duration, cache hit/miss");
Console.WriteLine("and click-queue metrics, or read Redis counters: docker compose exec redis redis-cli INFO stats");

return unexpected == 0 ? 0 : 1;

static string PickOperation(string mode, int i) => mode switch
{
    "create" => "create",
    "mixed" => (i % 10) switch { 0 => "create", 1 => "analytics", _ => "redirect" },
    _ => "redirect"
};

static int ExpectedStatus(string operation) => operation switch
{
    "create" => 201,
    "analytics" => 200,
    _ => 302
};

static async Task<string> CreateLinkAsync(HttpClient client)
{
    var create = await client.PostAsJsonAsync("/api/v1/shorten", new { url = $"https://example.com/perf-{Guid.NewGuid():N}" });
    create.EnsureSuccessStatusCode();
    var created = await create.Content.ReadFromJsonAsync<Created>();
    return created!.ShortUrl[(created.ShortUrl.LastIndexOf('/') + 1)..];
}

record Created(string ShortUrl, DateTime ExpiresAt);

record Sample(string Operation, int Status, double LatencyMs);
