# 003 — Update and delete a link

| | |
|---|---|
| Status | Implemented |
| AC prefix | `UPD` (update), `DEL` (delete) |
| Last verified | 2026-10-12 |
| Related specs | 001 (create), 004 (cache invalidation), 007 (rate limiting) |
| Plans | [001](../plans/001-database-design.md), [002](../plans/002-api-generation.md), [004](../plans/004-persistence-layer.md), [007](../plans/007-utc-expires-at.md), [009](../plans/009-delete-cascade-gaps.md) |

## Purpose

Lets a caller retarget an active short link (optionally resetting its expiry) or remove it together with its click history.

## API contract

Both routes are on `ShortenController`, rate-limit policy `write` (`src/Controllers/ShortenController.cs`). `{code}` uses `ApiRoutes.CodeSegment`.

### `PUT /api/v1/shorten/{code}`

Request (`UpdateShortUrlRequest`): `{ "url": "https://new.example.com/", "expiryDays": 10 }` — `expiryDays` optional.
Response: `ShortUrlResponse` (same code, new expiry if reset).

| Status | When |
|--------|------|
| 200 OK | Updated. |
| 400 Bad Request | Validation failed (same URL and `expiryDays` rules as create, `UpdateShortUrlRequestValidator.cs`). |
| 404 Not Found | No link with this code. |
| 409 Conflict | Another request changed the row between load and save (optimistic locking on `RowVersion`). |
| 410 Gone | Link exists but has expired. |
| 413 Payload Too Large | Request body larger than 8 KB (rejected by the server before validation). |
| 429 Too Many Requests | `write` limit exceeded. *(Not declared in the action's `[ProducesResponseType]`, but the class-level policy applies.)* |

### `DELETE /api/v1/shorten/{code}`

| Status | When |
|--------|------|
| 204 No Content | Deleted. |
| 404 Not Found | No link with this code. |
| 429 Too Many Requests | `write` limit exceeded *(also not declared on the action)*. |

## Behaviour

**Update** (`src/Services/UrlShortenerService.cs`)
1. Loads the row *tracked* (`GetByCodeForUpdateAsync`) so `RowVersion` is checked on save.
2. Expired (`ExpiresAt <= now`) → 410 and nothing is saved.
3. The new URL is normalized and its hash recomputed (same rules as spec 001).
4. If `expiryDays` is present, `ExpiresAt = now + expiryDays`; otherwise `ExpiresAt` is unchanged.
4a. The response `expiresAt` is UTC with a `Z` suffix in both cases — including when it is the unchanged value read from SQL (spec 001 behaviour 8).
4b. **Request body limit:** the same 8 KB limit applies to `PUT` (`[RequestSizeLimit]` on `Update`); a larger body is rejected with **413** before validation.
5. `DbUpdateConcurrencyException` is translated to `ConcurrencyConflictException` → 409 (`ShortUrlRepository.cs`).
6. After a successful update the cache entry is removed (spec 004).

**Delete** (`UrlShortenerService.cs`, `ShortUrlRepository.cs`)
1. Single `ExecuteDelete` by code — no load, no expiry check, so expired links can be deleted too.
2. `ClickEvents` rows go with it via `ON DELETE CASCADE` (`ShortUrlConfiguration.cs`).
3. After a successful delete the cache entry is removed (spec 004).

## Edge cases and failure modes

- **No duplicate detection on update.** Retargeting code A to a URL that code B already points to is allowed; afterwards a create for that URL returns whichever active link has the lowest Id.
- The HTTP API does not expose `RowVersion`, so a client cannot send an `If-Match`; 409 only arises from truly concurrent saves.
- A click queued just before delete fails its FK on insert; the click writer drops it without losing neighbouring clicks (spec 005).

## Configuration

`Shortener:MaxExpiryDays` (365) bounds `expiryDays` — see spec 001.

## Acceptance criteria

| ID | Given / When / Then | Tests |
|----|---------------------|-------|
| UPD-AC-01 | Given an active link, when PUT with a new URL, then 200 and the next redirect goes to the new URL. | `ApiEndToEndTests.FullLifecycle_Create_Redirect_Analytics_Update_Delete`, `UpdateShortUrlRequestValidatorTests.ValidRequest_Passes` |
| UPD-AC-02 | Given no `expiryDays`, when updated, then URL and hash change and `ExpiresAt` is unchanged. | `UrlShortenerServiceTests.UpdateAsync_ActiveLink_ChangesUrlAndHashAndKeepsExpiry` |
| UPD-AC-03 | Given `expiryDays = n`, when updated, then `ExpiresAt = now + n`. | `UrlShortenerServiceTests.UpdateAsync_WithExpiryDays_ResetsExpiry` |
| UPD-AC-04 | Given an unknown code, then 404. | `ApiEndToEndTests.Update_UnknownCode_Returns404`, `UrlShortenerServiceTests.UpdateAsync_UnknownCode_ThrowsNotFound` |
| UPD-AC-05 | Given an expired link, then 410 and nothing is saved. | `ApiEndToEndTests.Update_ExpiredLink_Returns410`, `UrlShortenerServiceTests.UpdateAsync_ExpiredLink_ThrowsAndDoesNotSave` |
| UPD-AC-06 | Given an unsafe/invalid URL, then 400 and the link still redirects to the old target. | `SecurityTests.Update_UnsafeUrl_Returns400_AndLinkIsUnchanged`, `UpdateShortUrlRequestValidatorTests.NonHttpUrl_Fails` |
| UPD-AC-07 | Given `expiryDays` > 365 or < 1, then 400. | `UpdateShortUrlRequestValidatorTests.ExpiryTooLarge_Fails`, `ApiEndToEndTests.Update_InvalidExpiryDays_Returns400` |
| UPD-AC-08 | Given two concurrent loads of the same row, when both save, then the second gets `ConcurrencyConflictException`. | `ApiEndToEndTests.Update_ConcurrentChange_ThrowsConcurrencyConflict` |
| UPD-AC-09 | Given a `ConcurrencyConflictException` from the service, then the HTTP response is 409 problem+json. | `ExceptionHandlingMiddlewareTests.InvokeAsync_ConcurrencyConflict_Returns409ProblemJsonWithTraceIdAndTitle` |
| UPD-AC-10 | Given an active link, when PUT without `expiryDays`, then the 200 response's `expiresAt` ends in `Z` and equals the link's previous expiry instant. | `ApiEndToEndTests.Update_WithoutExpiryDays_ReturnsUnchangedExpiresAtInUtc` |
| UPD-AC-11 | Same rule for PUT. | `ShortenControllerTests.WriteActions_CarryRequestSizeLimitOfMaxRequestBodyBytes` (Update case), `ExceptionHandlingMiddlewareTests.InvokeAsync_BadHttpRequest413_Returns413ProblemJsonWithoutExceptionText` |
| DEL-AC-01 | Given an existing link, when DELETE, then 204 and subsequent redirect and analytics return 404. | `ApiEndToEndTests.FullLifecycle_Create_Redirect_Analytics_Update_Delete`, `UrlShortenerServiceTests.DeleteAsync_ExistingCode_Deletes` |
| DEL-AC-02 | Given an unknown code, then 404. | `ApiEndToEndTests.FullLifecycle_Create_Redirect_Analytics_Update_Delete` (second delete), `UrlShortenerServiceTests.DeleteAsync_UnknownCode_ThrowsNotFound` |
| DEL-AC-03 | Given a link with recorded clicks, when deleted, then its `ClickEvents` rows are gone from the database. | `DeletionTests.Delete_LinkWithClicks_RemovesItsClickEvents` |
| DEL-AC-04 | Given an expired (not yet cleaned) link, when DELETE, then 204. | `ApiEndToEndTests.Delete_ExpiredLink_Returns204ThenRedirectIs404` |

## Known limitations

- No ownership or authentication: anyone who knows a code can update or delete it.
- Delete is permanent and erases analytics; there is no soft delete.
- Update does not check whether the new URL is already the target of another code (duplicate detection applies to create only).
- Update and delete are unauthenticated (the assessment lists authentication as out of scope); anyone who knows a code can use them.
