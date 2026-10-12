# 006 — Analytics reports

| | |
|---|---|
| Status | Implemented |
| AC prefix | `RPT` |
| Last verified | 2026-10-12 |
| Related specs | 005 (click tracking), 007 (rate limiting), 008 (cleanup erases history) |
| Plans | [006](../plans/006-click-analytics.md) |

## Purpose

Read-only statistics for one short link: all-time summary, clicks per day, per country and per browser.

## API contract

All on `AnalyticsController`, prefix `/api/v1/analytics`, rate-limit policy `read` (`src/Controllers/AnalyticsController.cs`). Dates are `yyyy-MM-dd`, UTC calendar days, both ends inclusive.

| Route | Query | Response |
|-------|-------|----------|
| `GET /{code}` | — | `AnalyticsResponse { clicks, uniqueVisitors, countries: { "US": 3, … } }` (all time) |
| `GET /{code}/daily` | `from?`, `to?` | `DailyClicksResponse { code, from, to, totalClicks, days: [{ date, clicks, uniqueVisitors }] }` |
| `GET /{code}/countries` | `from?`, `to?` | `CountryBreakdownResponse { code, totalClicks, unknownClicks, countries: [{ country, clicks, percentage }] }` |
| `GET /{code}/browsers` | `from?`, `to?` | `BrowserBreakdownResponse { code, totalClicks, browsers: [{ browser, clicks, percentage }] }` |

| Status | When |
|--------|------|
| 200 OK | Report produced (zero counts if no clicks). |
| 400 Bad Request | `from`/`to` not a valid date, `from > to`, or (daily) range > 366 days. |
| 404 Not Found | Unknown code. |
| 429 Too Many Requests | `read` limit exceeded. |

## Behaviour

**Summary** (`UrlShortenerService.GetAnalyticsAsync`, `ShortUrlRepository.GetClickStatsAsync`)
1. `clicks` = all rows; `uniqueVisitors` = distinct `VisitorHash`; `countries` excludes null countries (they still count in `clicks`) (`ShortUrlRepository.cs`).

**Daily** (`src/Services/ReportService.cs`)
2. Defaults: `to` = today (UTC); `from` = `to − 29` → 30 days inclusive. Either bound can be given alone.
3. Range is validated: `from ≤ to` and `to − from + 1 ≤ 366`.
4. SQL groups by `ClickedAt.Date` over `[from 00:00, to+1 00:00)` (`ShortUrlRepository.cs`). Every day in range is returned; days without clicks are zero-filled.
5. `uniqueVisitors` is per day; there is no range-wide unique count.

**Countries** (`ReportService.cs`)
6. `totalClicks` includes unknown; `unknownClicks` = rows with null country; list contains known countries only, sorted by clicks desc then code (ordinal).
7. `percentage` = clicks / totalClicks × 100, rounded to 1 decimal; 0 when total is 0.

**Browsers** (`ReportService.cs`)
8. Null browser (rows recorded before the column existed) is merged into `Unknown`. Sorted and percentaged like countries.

**Countries and browsers range rules** (`OptionalRange`)
9. Both bounds optional; no bound → all time. `from > to` and the 366-day cap are checked **only when both are given**.

**All reports**
10. The link is looked up without an expiry check: expired links that the cleanup job hasn't deleted yet still report.
11. **Supported date range:** a `from` or `to` at the edge of the supported range (for example `to=0001-01-01` or `to=9999-12-31`) is rejected with **400**: day arithmetic in `ReportService` is checked and reports an overflow as an `InvalidRequestException` rather than an unhandled exception.

## Edge cases and failure modes

- Clicks land in the background writer up to `FlushIntervalSeconds` after the redirect, so reports are eventually consistent (seconds).
- Deleting a link or cleanup removing it erases its history (cascade); reports then return 404.

## Configuration

Constants in code, not configuration: `ReportService.DefaultDays = 30`, `ReportService.MaxRangeDays = 366`.

## Acceptance criteria

| ID | Given / When / Then | Tests |
|----|---------------------|-------|
| RPT-AC-01 | Given clicks on a link, when GET summary, then clicks, unique visitors and known-country counts are returned. | `ApiEndToEndTests.FullLifecycle_Create_Redirect_Analytics_Update_Delete`, `UrlShortenerServiceTests.GetAnalyticsAsync_ExistingCode_MapsRepositoryStats` |
| RPT-AC-02 | Given an unknown code, then every report endpoint returns 404. | `ReportEndpointTests.Reports_UnknownCode_Return404`, `ReportServiceTests.AllReports_UnknownCode_ThrowNotFound`, `UrlShortenerServiceTests.GetAnalyticsAsync_UnknownCode_ThrowsNotFound` |
| RPT-AC-03 | Given no dates, when GET daily, then 30 entries ending today (UTC), including today's clicks. | `ReportEndpointTests.Daily_DefaultRange_IsThirtyDaysEndingTodayWithTodaysClicks`, `ReportServiceTests.Daily_NoDates_UsesLastThirtyDaysEndingToday` |
| RPT-AC-04 | Given an explicit range, then exactly one entry per day, zero-filled. | `ReportEndpointTests.Daily_ExplicitRange_ReturnsOneEntryPerDay`, `ReportServiceTests.Daily_FillsMissingDaysWithZero`, `ReportServiceTests.Daily_SingleDay_ReturnsOneEntry` |
| RPT-AC-05 | Given `from > to`, then 400 for daily, countries and browsers. | `ReportEndpointTests.Reports_FromAfterTo_Return400`, `ReportServiceTests.Daily_FromAfterTo_Throws`, `ReportServiceTests.Countries_FromAfterTo_Throws`, `ExceptionHandlingMiddlewareTests.InvokeAsync_InvalidRequest_Returns400WithMessage` |
| RPT-AC-06 | Given a daily range of 367 days, then 400; of exactly 366, then 200. | `ReportEndpointTests.Daily_RangeOverOneYear_Returns400`, `ReportServiceTests.Daily_RangeLongerThanLimit_Throws`, `ReportServiceTests.Daily_ExactlyMaxRange_IsAllowed` |
| RPT-AC-07 | Given `from=not-a-date`, then 400. | `ReportEndpointTests.Reports_NotADate_Return400` |
| RPT-AC-08 | Countries: known countries sorted by clicks with percentages; unknown counted separately; no clicks → zeros. | `ReportEndpointTests.Countries_ReportsKnownAndUnknown`, `ReportServiceTests.Countries_CountsUnknownSeparatelyAndSortsByClicks`, `ReportServiceTests.Countries_NoClicks_ReturnsEmptyWithZeroTotals` |
| RPT-AC-09 | Inclusive `from..to` is passed to the repository as half-open `[from, to+1)`. | `ReportServiceTests.Countries_PassesInclusiveRangeAsHalfOpenInterval` |
| RPT-AC-10 | Browsers: null merged into `Unknown`; percentages rounded to 1 decimal. | `ReportEndpointTests.Browsers_ReportsDetectedFamilies`, `ReportServiceTests.Browsers_NullBrowserIsReportedAsUnknownAndMergedWithExistingUnknown`, `ReportServiceTests.Browsers_PercentagesRoundToOneDecimal` |
| RPT-AC-11 | Given only `from` for daily, then `to` = today; given only `to`, then `from = to − 29`. | `ReportServiceTests.Daily_OnlyFrom_EndsToday`, `ReportServiceTests.Daily_OnlyTo_StartsTwentyNineDaysBefore` |
| RPT-AC-12 | Given clicks at 23:59:59 and 00:00:00 UTC, then they land on different days (real SQL date grouping). | `ReportEndpointTests.Daily_ClicksAroundMidnight_LandOnDifferentDaysWithPerDayUniqueVisitors` |
| RPT-AC-13 | Given an expired link not yet cleaned up, then reports still return 200 with its history. | `ReportEndpointTests.Reports_ExpiredLinkNotYetCleaned_StillReturnHistory` |
| RPT-AC-14 | Given countries/browsers with only `from` (or only `to`), then the open side is unbounded and no range-length check applies. | `ReportServiceTests.Countries_OnlyFromWithHugeSpan_PassesOpenUpperBound`, `ReportServiceTests.Countries_OnlyTo_PassesOpenLowerBound`, `ReportServiceTests.Browsers_OnlyFromWithHugeSpan_PassesOpenUpperBound`, `ReportServiceTests.Browsers_OnlyTo_PassesOpenLowerBound` |
| RPT-AC-15 | Given a date at the edge of the supported range (`to=0001-01-01`, `to=9999-12-31`), when requesting daily, countries or browsers, then 400 problem+json, never 500. | `ReportServiceTests.Daily_ToAtEdgeOfSupportedRange_ThrowsInvalidRequestNotArgumentOutOfRange`, `ReportServiceTests.Countries_ToIsMaxDate_ThrowsInvalidRequest`, `ReportServiceTests.Browsers_ToIsMaxDate_ThrowsInvalidRequest`, `ReportEndpointTests.Reports_ToAtEdgeOfSupportedRange_Return400ProblemJson` |

## Known limitations

- No range-wide unique-visitor count in the daily report.
- No authentication: anyone with a code can read its analytics.
- Countries and browsers apply the 366-day cap only when both `from` and `to` are given; a single open bound is unbounded by design (daily always bounds its range).
