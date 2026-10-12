namespace UrlShortener.DTOs;

/// <param name="Code">The short code the report is for.</param>
/// <param name="From">First day included (UTC).</param>
/// <param name="To">Last day included (UTC).</param>
/// <param name="TotalClicks">Clicks in the whole range.</param>
/// <param name="Days">One entry per day in the range, including days with zero clicks.</param>
public record DailyClicksResponse(string Code, DateOnly From, DateOnly To, long TotalClicks, IReadOnlyList<DailyClickPoint> Days);

public record DailyClickPoint(DateOnly Date, long Clicks, long UniqueVisitors);

/// <param name="TotalClicks">All clicks in range, including those with an unknown country.</param>
/// <param name="UnknownClicks">Clicks where no country was available (no CDN/proxy country header).</param>
/// <param name="Countries">Known countries, most clicks first.</param>
public record CountryBreakdownResponse(string Code, long TotalClicks, long UnknownClicks, IReadOnlyList<CountryStat> Countries);

/// <param name="Percentage">Share of TotalClicks, rounded to one decimal place.</param>
public record CountryStat(string Country, long Clicks, double Percentage);

public record BrowserBreakdownResponse(string Code, long TotalClicks, IReadOnlyList<BrowserStat> Browsers);

/// <param name="Percentage">Share of TotalClicks, rounded to one decimal place.</param>
public record BrowserStat(string Browser, long Clicks, double Percentage);
