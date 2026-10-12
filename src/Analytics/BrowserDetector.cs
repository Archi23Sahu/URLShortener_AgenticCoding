namespace UrlShortener.Analytics;

/// <summary>
/// Classifies a User-Agent header into a small, fixed set of browser families.
/// This is a deliberately simple heuristic (no external library). Order matters: most browsers put
/// "Chrome" and "Safari" in their User-Agent, so the more specific tokens (Edg, OPR, Firefox...) are checked first.
/// Known limits: it does not separate browser versions, and unusual browsers (for example Samsung Internet) fall under "Other".
/// </summary>
public static class BrowserDetector
{
    public const string Unknown = "Unknown";
    public const string Bot = "Bot";
    public const string Other = "Other";

    private static readonly string[] BotTokens =
    {
        "bot", "crawl", "spider", "slurp", "curl/", "wget/", "python-requests", "postmanruntime", "httpclient", "go-http-client"
    };

    public static string Detect(string? userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return Unknown;
        }

        if (Has(userAgent, BotTokens))
        {
            return Bot;
        }

        // Edge: "Edg/" (desktop), "EdgA/" (Android), "EdgiOS/" (iOS), "Edge/" (old EdgeHTML).
        if (Has(userAgent, "Edg/", "EdgA/", "EdgiOS/", "Edge/"))
        {
            return "Edge";
        }

        if (Has(userAgent, "OPR/", "Opera", "OPiOS/"))
        {
            return "Opera";
        }

        if (Has(userAgent, "Firefox/", "FxiOS/"))
        {
            return "Firefox";
        }

        if (Has(userAgent, "MSIE ", "Trident/"))
        {
            return "Internet Explorer";
        }

        if (Has(userAgent, "SamsungBrowser/"))
        {
            return Other;
        }

        // Chrome on iOS reports "CriOS/"; Chromium-based browsers report "Chrome/".
        if (Has(userAgent, "Chrome/", "CriOS/", "Chromium/"))
        {
            return "Chrome";
        }

        // Safari reports "Safari/" and, unlike Chromium browsers, has no "Chrome/" token.
        if (Has(userAgent, "Safari/"))
        {
            return "Safari";
        }

        return Other;
    }

    private static bool Has(string userAgent, params string[] tokens) =>
        tokens.Any(t => userAgent.Contains(t, StringComparison.OrdinalIgnoreCase));
}
