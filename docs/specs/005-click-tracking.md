# 005 — Click tracking

| | |
|---|---|
| Status | Implemented |
| AC prefix | `CLK` |
| Last verified | 2026-10-12 |
| Related specs | 002 (redirect), 006 (reports), 003 (delete races) |
| Plans | [001](../plans/001-database-design.md), [006](../plans/006-click-analytics.md), [008](../plans/008-click-pipeline-gaps.md), [011](../plans/011-polly-resilience.md) |

## Purpose

Records one `ClickEvent` per successful redirect (time, anonymous visitor id, country, browser) without adding I/O or failure modes to the redirect itself.

## API contract

No endpoints. Pipeline:

```
RedirectController ─► ClickTracker.Track ─► ChannelClickQueue (bounded) ─► ClickRecorderBackgroundService ─► ClickEvents
     (request thread, no I/O)                (in memory)                     (hosted service, batched inserts)
```

Stored row (`src/Entities/ClickEvent.cs`): `ShortUrlId`, `ClickedAt` (UTC, time of redirect), `VisitorHash` (`char(64)`), `Country` (`char(2)`, nullable), `Browser` (`varchar(30)`, nullable for legacy rows).

## Behaviour

**Capture** (`src/Analytics/ClickTracker.cs`) — called only after a successful resolve (spec 002).
1. `VisitorHash = SHA-256("{ip ?? "unknown"}|{userAgent}|{salt}")`, 64 lower-case hex chars; the raw IP is never stored (`VisitorHasher.cs`).
2. `Country` comes from the `Analytics:CountryHeader` request header: exactly two ASCII letters, upper-cased; `XX` (unknown) and `T1` (Tor) become `null`; anything else → `null` (`CountryResolver.cs`).
3. `Browser` is one of `Chrome, Edge, Firefox, Safari, Opera, Internet Explorer, Bot, Other, Unknown`. Bot tokens are checked first, then specific tokens before generic ones (Edge/Opera before Chrome, Chrome before Safari); empty UA → `Unknown` (`BrowserDetector.cs`).
4. `TryEnqueue` never blocks. Queue full → click dropped, warning logged (`ClickTracker.cs`). Any exception is caught and logged; `Track` never throws.

**Queue** (`ClickQueue.cs`): bounded `Channel` of `Analytics:QueueCapacity`, many writers, single reader.

**Writer** (`src/BackgroundServices/ClickRecorderBackgroundService.cs`)
5. Waits for the first click, then collects until `BatchSize` clicks **or** `FlushIntervalSeconds` elapse, whichever first.
6. Each insert uses a fresh DI scope and `DbContext`; flushes use `CancellationToken.None` so an in-flight batch isn't abandoned.
7. If a batch insert fails, each click is retried individually; a click that still fails is logged and dropped. Typical cause: FK violation because the link was deleted after its click was queued.
   Click inserts (spec 010, RSL-AC-04..06): inserts go through `IClickEventWriter`; transient failures are retried as a whole batch via Polly and dropped with one log if they persist; only non-transient failures fall back to row-by-row.
8. On graceful shutdown the remaining queue is drained and written in batches, within the host's 30 s shutdown timeout (compose sets `stop_grace_period: 35s`). Hosted services stop in reverse registration order, so the web server may still accept redirects while the writer drains; clicks enqueued after the final read are lost (at-most-once delivery).

## Edge cases and failure modes

| Situation | Result |
|-----------|--------|
| Queue full (traffic spike / slow SQL) | Click dropped, redirect unaffected. |
| SQL down | Batch and per-row inserts fail; those clicks are lost (logged). |
| Link deleted after click queued | That click dropped; neighbours in the batch survive. |
| Process crash / kill -9 | Everything still in the queue is lost (documented trade-off). |
| Request with no User-Agent / no IP | Hash still computed (`unknown`, empty UA); browser `Unknown`. |

## Configuration

| Key | Default | Effect |
|-----|---------|--------|
| `Analytics:Salt` | `dev-only-change-me` | Mixed into visitor hash. **Must be overridden in real environments**; changing it resets unique-visitor counts. |
| `Analytics:QueueCapacity` | `10000` (min 1) | Max clicks buffered in memory. |
| `Analytics:BatchSize` | `100` (min 1) | Max clicks per insert. |
| `Analytics:FlushIntervalSeconds` | `2` (min 1) | Max wait before a partial batch is written. |
| `Analytics:CountryHeader` | `CF-IPCountry` | Header holding the 2-letter country; empty → country always null. |

## Acceptance criteria

| ID | Given / When / Then | Tests |
|----|---------------------|-------|
| CLK-AC-01 | Given a successful redirect, then one `ClickEvent` eventually appears for that link. | `ApiEndToEndTests.FullLifecycle_Create_Redirect_Analytics_Update_Delete` |
| CLK-AC-02 | Given a request, when tracked, then the queued record carries link id, current UTC time, visitor hash and country. | `ClickTrackerTests.Track_QueuesClickWithLinkIdTimeHashAndCountry` |
| CLK-AC-03 | Given a User-Agent, then the recorded browser family matches the detector. | `ClickTrackerBrowserTests.Track_StoresBrowserDetectedFromUserAgent`, `BrowserDetectorTests.Detect_RealUserAgents_ReturnsExpectedFamily`, `BrowserDetectorTests.Detect_MissingUserAgent_ReturnsUnknown`, `ReportEndpointTests.Browsers_ReportsDetectedFamilies` |
| CLK-AC-04 | The visitor hash is deterministic, 64 lower-case hex, changes with any input, never contains the raw IP, tolerates null inputs. | `VisitorHasherTests.Compute_SameInputs_GiveSameHash`, `VisitorHasherTests.Compute_IsSixtyFourLowerCaseHexCharacters`, `VisitorHasherTests.Compute_AnyInputChange_ChangesHash`, `VisitorHasherTests.Compute_DoesNotContainRawIp`, `VisitorHasherTests.Compute_NullInputs_DoNotThrow` |
| CLK-AC-05 | Country header: valid code → upper-case; invalid, `XX`, `T1`, missing, or header name unset → null. | `HeaderCountryResolverTests.Resolve_ValidCode_ReturnsUpperCase`, `HeaderCountryResolverTests.Resolve_InvalidOrUnknown_ReturnsNull`, `HeaderCountryResolverTests.Resolve_MissingHeader_ReturnsNull`, `HeaderCountryResolverTests.Resolve_HeaderNameNotConfigured_ReturnsNull`, `ReportEndpointTests.Countries_ReportsKnownAndUnknown` |
| CLK-AC-06 | Given a full queue, then `TryEnqueue` returns false without blocking and `Track` does not throw. | `ChannelClickQueueTests.TryEnqueue_WhenFull_ReturnsFalseWithoutBlocking`, `ChannelClickQueueTests.TryEnqueue_WithSpace_ReturnsTrueAndItemCanBeRead`, `ChannelClickQueueTests.TryEnqueue_AfterReading_HasSpaceAgain`, `ClickTrackerTests.Track_QueueFull_DoesNotThrow` |
| CLK-AC-07 | Given the queue throws, then `Track` swallows and logs. | `ClickTrackerTests.Track_QueueThrows_DoesNotThrow` |
| CLK-AC-08 | Given N ≥ BatchSize queued clicks, then they are written in batches of ≤ BatchSize; given fewer, then they are written within FlushInterval. | `ClickRecorderBackgroundServiceTests.Flush_MoreClicksThanBatchSize_WritesBatchesOfAtMostBatchSizeInOrder`, `ClickRecorderBackgroundServiceTests.Flush_PartialBatch_IsWrittenByTheFlushIntervalWithoutShutdown` |
| CLK-AC-09 | Given a batch where one click references a deleted link, then all other clicks in the batch are stored. | `ClickPipelineTests.FlushBatch_ClickForMissingLink_OtherClicksAreStored` |
| CLK-AC-10 | Given clicks still queued at graceful shutdown, then they are written before the service stops. | `ClickShutdownTests.Shutdown_QueuedClicks_AreWrittenBeforeStop` |
| CLK-AC-11 | Given two redirects from the same IP + UA, then unique visitors = 1 and clicks = 2. | `ClickPipelineTests.Summary_TwoVisitsWithSameUserAgent_CountsTwoClicksOneVisitor`, `ClickPipelineTests.Summary_VisitsWithDifferentUserAgents_CountAsDifferentVisitors` |

## Known limitations

- At-most-once delivery: crashes and full queues lose clicks.
- Unique visitor = IP + UA, so users behind one NAT with the same browser merge, and UA changes split one person into two.
- Bots are counted as clicks (reported as browser `Bot`, not filtered).
- `RemoteIpAddress` is the direct peer; behind a reverse proxy without forwarded-headers middleware every visitor hashes with the proxy's IP.
- The country header is trusted as-is. Without a CDN/proxy that overwrites it, clients can set any country.
- Behind a reverse proxy without forwarded-headers handling every visitor shares the proxy's IP for hashing and rate limiting; deployments must terminate at the app or add forwarded-headers support.
- The shipped `Analytics:Salt` is a development value and is not rejected at startup; set it in every real environment.
