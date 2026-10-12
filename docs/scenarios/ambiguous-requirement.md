# Scenario 3: Ambiguous requirement

**Requirement as received:** *"Users want better reporting."*

That sentence doesn't say what to measure, over what period, or in what format. Building from it directly would have meant guessing, so the work started by turning the ambiguity into questions.

## 1. AI clarifies the requirement

Clarifying questions generated from the existing data model and the sentence above:

| Question | Why it matters |
|----------|----------------|
| Click counts? | Already available as the summary endpoint; is it enough? |
| Country-wise usage? | Needs the country captured at click time (header from a CDN/proxy). |
| Browser statistics? | Needs the User-Agent classified and **stored** at click time: historical clicks have no browser. |
| Daily reports? | Needs a time axis, a date range and a rule for days with no clicks. |
| Export functionality? | CSV/Excel is a separate feature with its own formats and size limits. |

## 2. Product owner's response

Needed: **daily clicks**, **country analytics**, **browser analytics**. Export was not requested and is out of scope.

## 3. Design proposal

| Decision | Choice | Reason |
|----------|--------|--------|
| Where it lives | A separate `ReportService`, not the cached URL service | Reporting changes must not touch the redirect hot path or its cache. |
| Time handling | UTC calendar days, `from`/`to` inclusive, default last 30 days, maximum 366 days | One unambiguous rule; the cap bounds query cost. |
| Empty days | Returned as zero | Clients can chart without filling gaps. |
| Unknown values | Country `null` and browser `null` reported as `unknownClicks` / `Unknown` | Pre-migration clicks have no browser; they must not disappear from totals. |
| Percentages | Share of total, one decimal | Matches how the dashboards present it. |
| Storage change | Add nullable `ClickEvents.Browser` (migration `AddBrowserToClickEvents`) | Country was already stored; browser was not. Nullable keeps existing rows valid. |

## 4. API design

```
GET /api/v1/analytics/{code}/daily?from=yyyy-MM-dd&to=yyyy-MM-dd
GET /api/v1/analytics/{code}/countries?from&to
GET /api/v1/analytics/{code}/browsers?from&to
```

Full contract, status codes and acceptance criteria: [spec 006](../specs/006-analytics-reports.md). The existing summary endpoint `GET /api/v1/analytics/{code}` is unchanged.

## 5. Engineer review

- **Backward compatibility:** the original response shape is untouched; new routes are additive.
- **Index support:** every report filters on `(ShortUrlId, ClickedAt)`, which is the existing composite index. No new index was needed.
- **Browser detection is a heuristic.** Order matters (Edge and Opera before Chrome, Chrome before Safari); Samsung Internet is reported as `Other`. Documented in the class and in spec 005.
- **Date-range rule:** the 366-day cap always applies to the daily report. Countries and browsers apply it only when both `from` and `to` are given; with a single open bound they are unbounded by design (documented in spec 006).

## 6. Implementation and validation

| Behaviour | Tests |
|-----------|-------|
| Default 30-day window, zero-filled days, single day | `ReportServiceTests.Daily_*`, `ReportEndpointTests.Daily_*` |
| `from > to`, range over the cap, malformed dates | `ReportEndpointTests.Reports_FromAfterTo_Return400`, `…Reports_NotADate_Return400`, `…Daily_RangeOverOneYear_Returns400` |
| Known vs unknown country, ordering, percentages | `ReportServiceTests.Countries_*`, `ReportEndpointTests.Countries_ReportsKnownAndUnknown` |
| Browser families and legacy `NULL` rows | `BrowserDetectorTests`, `ReportServiceTests.Browsers_*`, `ReportEndpointTests.Browsers_ReportsDetectedFamilies` |
| Unknown code | `ReportEndpointTests.Reports_UnknownCode_Return404` |
| Day boundaries in SQL (23:59:59 vs 00:00:00 UTC) | `ReportEndpointTests.Daily_ClicksAroundMidnight_LandOnDifferentDaysWithPerDayUniqueVisitors` |
| One-sided date ranges | `ReportServiceTests.Daily_OnlyFrom_EndsToday`, `ReportServiceTests.Daily_OnlyTo_StartsTwentyNineDaysBefore`, `ReportServiceTests.Countries_OnlyFromWithHugeSpan_PassesOpenUpperBound` |
| Expired but not yet cleaned-up links still report | `ReportEndpointTests.Reports_ExpiredLinkNotYetCleaned_StillReturnHistory` |
