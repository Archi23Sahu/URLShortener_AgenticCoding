# 002 — Redirect

| | |
|---|---|
| Status | Implemented |
| AC prefix | `RED` |
| Last verified | 2026-10-12 |
| Related specs | 004 (cache), 005 (click tracking), 007 (rate limiting), 008 (cleanup) |
| Plans | [002](../plans/002-api-generation.md), [005](../plans/005-redis-cache.md), [008](../plans/008-click-pipeline-gaps.md) |

## Purpose

The hot path: a visitor opens `{BaseUrl}/{code}` and is sent to the original URL. It must be fast, never slowed by analytics, and must refuse expired links.

## API contract

`GET /{code}` — rate-limit policy `read` (`src/Controllers/RedirectController.cs`)

Route constraint `ApiRoutes.CodeSegment`: 1–12 characters of `[0-9A-Za-z]` (`src/Controllers/ApiRoutes.cs`).

| Status | When |
|--------|------|
| 302 Found | Active link. `Location` = stored (normalized) URL. |
| 404 Not Found | Code matches the route but no link exists (`application/problem+json` with `traceId`), **or** the path doesn't match the route constraint (plain 404, never reaches a controller). |
| 410 Gone | Link exists but `ExpiresAt <= now`. No `Location` header. |
| 429 Too Many Requests | `read` rate limit exceeded (spec 007). |
| 499 | Client disconnected mid-request (logged at Information, `ExceptionHandlingMiddleware.cs`). |

## Behaviour

1. `IUrlShortenerService.ResolveAsync` is called — in production this is the cache decorator (spec 004), which falls through to `UrlShortenerService.ResolveAsync` on a miss (`Program.cs`).
2. Lookup is read-only (`AsNoTracking`) by exact, case-sensitive code (`ShortUrlRepository.cs`, collation `SQL_Latin1_General_CP1_CS_AS`).
3. A link is expired when `ExpiresAt <= now` — the boundary instant counts as expired (`UrlShortenerService.cs`).
4. Only after a successful resolve is the click queued (`RedirectController.cs`); 404/410 throw before that line, so they are never counted.
5. The response is **302**, deliberately not 301: links can be updated, expire or be deleted, and browsers cache 301s permanently (`RedirectController.cs`).
6. Error bodies are RFC 7807 `ProblemDetails` with `traceId`; 5xx bodies never include exception details (`ExceptionHandlingMiddleware.cs`).
7. **Log levels:** expected failures (unknown or expired code) are logged at Debug so a probe cannot flood the log; other client faults (validation, conflicts) at Information; only unexpected exceptions are logged as errors with a stack trace. Rate-limit rejections are logged at Debug (the request metrics carry the 429 count).

## Edge cases and failure modes

- Path traversal (`/../x`), SQL fragments, null bytes, and codes longer than 12 characters fail the route constraint → 404, never 500.
- Redis down → served from SQL (spec 004). Click queue full → redirect still succeeds, click dropped (spec 005).
- An expired link stays 410 until the cleanup job deletes it after the grace period; afterwards it is 404 (spec 008).

## Configuration

None specific. See spec 004 (cache TTL) and spec 007 (rate limits).

## Acceptance criteria

| ID | Given / When / Then | Tests |
|----|---------------------|-------|
| RED-AC-01 | Given an active link, when `GET /{code}`, then 302 with `Location` = stored URL. | `ApiEndToEndTests.FullLifecycle_Create_Redirect_Analytics_Update_Delete`, `UrlShortenerServiceTests.ResolveAsync_ActiveLink_ReturnsIdAndOriginalUrl` |
| RED-AC-02 | Given an unknown well-formed code, then 404 `application/problem+json` containing `traceId`. | `ApiEndToEndTests.Redirect_UnknownCode_Returns404ProblemJson`, `UrlShortenerServiceTests.ResolveAsync_UnknownCode_ThrowsNotFound`, `ExceptionHandlingMiddlewareTests.InvokeAsync_NotFound_Returns404` |
| RED-AC-03 | Given an expired link, then 410 and no `Location` header. | `ApiEndToEndTests.Redirect_ExpiredLink_Returns410`, `SecurityTests.ExpiredLink_Returns410_AndIsNotRedirected`, `UrlShortenerServiceTests.ResolveAsync_ExpiredLink_ThrowsLinkExpired`, `ExceptionHandlingMiddlewareTests.InvokeAsync_LinkExpired_Returns410` |
| RED-AC-04 | Given `ExpiresAt == now`, then the link is treated as expired. | `UrlShortenerServiceTests.ResolveAsync_LinkExpiringExactlyNow_IsTreatedAsExpired` |
| RED-AC-05 | Given a link that expires a few seconds from now, then it redirects now and returns 410 after expiry. | `SecurityTests.LinkThatExpiresSoon_Redirects_ThenGone` |
| RED-AC-06 | Given a malformed code (traversal, SQL, null byte, too long, bad chars), then 404 and never 5xx. | `SecurityTests.Code_InvalidFormat_Returns404`, `SecurityTests.Code_PathTraversal_IsNotFound_NeverServerError`, `SecurityTests.Code_SqlInjection_NeverReachesTheDatabase_Returns404`, `SecurityTests.Code_NullByte_IsRejectedBeforeReachingTheApp` |
| RED-AC-07 | Given codes `abc123` and `ABC123`, then they resolve independently (case-sensitive). | `CachedUrlShortenerServiceTests.Resolve_CodesAreCaseSensitive`, `Base62EncoderTests.Decode_UpperAndLowerCase_AreDifferentValues`, `ApiEndToEndTests.Redirect_CodesDifferingOnlyByCase_ResolveIndependently` (real SQL collation) |
| RED-AC-08 | Given a successful redirect, then exactly one click is queued; given a 404 or 410, then none. | Positive: `ApiEndToEndTests.FullLifecycle_Create_Redirect_Analytics_Update_Delete`, `RedirectControllerTests.Redirect_KnownCode_QueuesOneClickAndReturns302` — negative (404/410 record no click): `RedirectControllerTests.Redirect_UnknownCode_QueuesNoClick`, `RedirectControllerTests.Redirect_ExpiredLink_QueuesNoClick` |
| RED-AC-09 | Given any error response, then the body leaks no stack trace or connection string and does not echo raw script from the path. | `SecurityTests.ErrorBodies_DoNotLeakStackTracesOrConnectionStrings`, `SecurityTests.ErrorResponses_DoNotEchoRawScriptFromThePath`, `ExceptionHandlingMiddlewareTests.InvokeAsync_UnexpectedException_Returns500WithoutLeakingMessage`, `ExceptionHandlingMiddlewareTests.InvokeAsync_ArgumentExceptionFromElsewhere_Returns500WithoutLeakingMessage` |
| RED-AC-10 | Given a stored URL that contained CR/LF, then the redirect cannot inject extra response headers. | `SecurityTests.Create_LineBreakInUrl_CannotInjectResponseHeaders` |
| RED-AC-11 | Given an unknown or expired code, then the failure is logged at Debug at most (other client faults at Information). | `ExceptionHandlingMiddlewareTests.InvokeAsync_NotFoundAndExpired_LogNothingAboveDebug`, `ExceptionHandlingMiddlewareTests.InvokeAsync_InvalidRequest_LogsAtInformationNotWarning`, `ExceptionHandlingMiddlewareTests.InvokeAsync_UnexpectedException_LogsAtError` |

## Known limitations

- The redirect target is any http/https URL the creator chose — the service is by nature an open redirector for those schemes. There is no domain allow/deny list.
