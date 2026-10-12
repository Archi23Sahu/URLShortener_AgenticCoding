# 001 — Create short URL

| | |
|---|---|
| Status | Implemented |
| AC prefix | `CRT` |
| Last verified | 2026-10-12 |
| Related specs | 002 (redirect), 007 (rate limiting) |
| Plans | [001](../plans/001-database-design.md), [002](../plans/002-api-generation.md), [003](../plans/003-short-code-algorithm.md), [004](../plans/004-persistence-layer.md), [007](../plans/007-utc-expires-at.md) |

## Purpose

Turns a long http/https URL into a short link `{BaseUrl}/{code}` that expires after a configurable number of days. If the same URL already has an active link, that link is returned instead of minting a new code.

## API contract

`POST /api/v1/shorten` — rate-limit policy `write` (`src/Controllers/ShortenController.cs`)

Request (`src/DTOs/ShortUrlDtos.cs`):

```json
{ "url": "https://example.com/very/long/path", "expiryDays": 7 }
```

Response body (`ShortUrlResponse`): `{ "shortUrl": "http://localhost:8080/100001", "expiresAt": "2026-10-18T10:00:00Z" }`

| Status | When |
|--------|------|
| 201 Created | New link created. `Location` header = `shortUrl`. |
| 200 OK | An active link for the same normalized URL already exists; it is returned unchanged. |
| 400 Bad Request | Validation failed (`ValidationProblemDetails`, errors keyed by property), malformed JSON, or body too large. |
| 413 Payload Too Large | Request body larger than 8 KB (rejected by the server before validation). |
| 415 Unsupported Media Type | `Content-Type` is not JSON. |
| 429 Too Many Requests | `write` rate limit exceeded (spec 007). |

## Behaviour

1. The controller validates with `CreateShortUrlRequestValidator` before calling the service (`ShortenController.cs`).
2. **URL rules** (`src/Validators/UrlRules.cs`, `src/Services/UrlNormalizer.cs`):
   - required and not whitespace;
   - must parse as an absolute URI after trimming;
   - scheme must be `http` or `https` (blocks `javascript:`, `data:`, `file:`, `ftp:` …);
   - must not contain user info (`user:pass@host`);
   - the *normalized* form must be ≤ 2048 characters.
3. **Normalization** uses `Uri.AbsoluteUri`: lower-cases scheme and host, drops default ports, escapes the rest (`UrlNormalizer.cs`). The normalized form is what gets stored and redirected to.
4. **ExpiryDays** is optional; when present it must be 1..`Shortener:MaxExpiryDays` (`CreateShortUrlRequestValidator.cs`). When absent, `Shortener:DefaultExpiryDays` is used. `ExpiresAt = now(UTC) + days` (`UrlShortenerService.cs`).
5. **Duplicate detection** (`UrlShortenerService.cs`, `ShortUrlRepository.cs`): SHA-256 of the normalized URL is looked up together with an exact match on the normalized URL and `ExpiresAt > now`; the lowest-Id match wins. On a hit the existing link is returned with **200** and its existing `ExpiresAt` — the request's `expiryDays` is ignored.
6. **Code generation** (`ShortUrlRepository.cs`, `src/Services/ShortCodeGenerator.cs`): inside one transaction the row is inserted with a unique placeholder code (`"tmp"` + 9 hex chars), then `Code = Base62(Id + 62^5)` is saved. Codes are therefore ≥ 6 characters, unique, case-sensitive (`SQL_Latin1_General_CP1_CS_AS`, `ShortUrlConfiguration.cs`) and fit `varchar(12)`.
7. `shortUrl` = `Shortener:BaseUrl` with trailing `/` trimmed + `/` + code (`UrlShortenerService.cs`).
8. **All timestamps in responses are UTC** and serialize as ISO-8601 with a `Z` suffix, whether the value was just computed or read back from SQL Server. `datetime2` stores no offset, so values read through EF must be marked UTC. Implemented by `UtcDateTimeConverter`, registered for every `DateTime` property in `AppDbContext.ConfigureConventions` (`src/Repositories/AppDbContext.cs`).
9. **Request body limit:** a request body larger than 8 KB (`ShortenController.MaxRequestBodyBytes`, enforced by `[RequestSizeLimit]` on `Create`) is rejected by the server with **413** before validation. `ExceptionHandlingMiddleware` maps the server's `BadHttpRequestException` to its own status and never echoes its text.

## Edge cases and failure modes

- An **expired** link for the same URL does not count as a duplicate; a new code is created. The hash index is non-unique on purpose (`ShortUrlConfiguration.cs`).
- Two **concurrent** creates of the same new URL can both miss the duplicate check and produce two codes (no unique constraint on the hash).
- URL containing SQL or `<script>` is stored as data (parameterized EF queries; the script is percent-escaped by normalization).
- CR/LF inside a URL cannot inject response headers on redirect (escaped by normalization).

## Configuration

| Key | Default | Effect |
|-----|---------|--------|
| `Shortener:BaseUrl` | `http://localhost:8080` | Prefix of returned `shortUrl`. |
| `Shortener:DefaultExpiryDays` | `30` | Lifetime when `expiryDays` is omitted. |
| `Shortener:MaxExpiryDays` | `365` | Upper bound for `expiryDays`. |

## Acceptance criteria

| ID | Given / When / Then | Tests |
|----|---------------------|-------|
| CRT-AC-01 | Given a new valid URL, when POSTed, then 201 with `Location`, a code matching `^[0-9A-Za-z]{6,}$` and the normalized URL persisted. | `ApiEndToEndTests.FullLifecycle_Create_Redirect_Analytics_Update_Delete`, `UrlShortenerServiceTests.CreateAsync_NewUrl_PersistsNormalizedEntityAndReturnsShortUrl`, `CreateShortUrlRequestValidatorTests.ValidUrl_Passes` |
| CRT-AC-02 | Given an active link for URL X, when X is POSTed again, then 200 with the same code and no insert. | `ApiEndToEndTests.FullLifecycle_Create_Redirect_Analytics_Update_Delete`, `UrlShortenerServiceTests.CreateAsync_DuplicateActiveUrl_ReturnsExistingLinkWithoutInserting` |
| CRT-AC-03 | Given equivalent URL spellings (scheme/host case, default port), when normalized, then they hash identically. | `UrlNormalizerTests.EquivalentUrls_ProduceTheSameHash`, `UrlNormalizerTests.TryNormalize_ValidUrl_ReturnsNormalizedForm`, `UrlNormalizerTests.ComputeHash_ReturnsSixtyFourLowerCaseHexCharacters`, `UrlNormalizerTests.ComputeHash_SameUrl_SameHash_DifferentUrl_DifferentHash`, `ApiEndToEndTests.Create_EquivalentUrlSpellings_ReturnsSameLinkWith200` |
| CRT-AC-04 | Given only an *expired* link for URL X, when X is POSTed, then 201 with a new code. | `ApiEndToEndTests.Create_OnlyExpiredLinkExistsForUrl_Returns201WithNewCode` |
| CRT-AC-05 | Given `expiryDays = n` (1..365), when created, then `expiresAt ≈ now + n days`. | `ApiEndToEndTests.Create_ExpiryDaysSetsExpiry`, `UrlShortenerServiceTests.CreateAsync_WithExpiryDays_UsesRequestedLifetime` |
| CRT-AC-06 | Given no `expiryDays`, when created, then `expiresAt = now + DefaultExpiryDays`. | `UrlShortenerServiceTests.CreateAsync_NewUrl_PersistsNormalizedEntityAndReturnsShortUrl` |
| CRT-AC-07 | Given `expiryDays` ∉ 1..365, when POSTed, then 400. | `ApiEndToEndTests.Create_InvalidExpiryDays_Returns400`, `CreateShortUrlRequestValidatorTests.ExpiryOutsideAllowedRange_Fails` |
| CRT-AC-08 | Given no `url`, when POSTed, then 400 with a `Url` field error. | `ApiEndToEndTests.Create_MissingUrl_Returns400WithFieldError`, `CreateShortUrlRequestValidatorTests.NullUrl_Fails` |
| CRT-AC-09 | Given a non-http(s), relative, or credential-bearing URL, when POSTed, then 400. | `SecurityTests.Create_UnsafeOrInvalidUrl_Returns400`, `UrlNormalizerTests.TryNormalize_InvalidOrUnsafeUrl_ReturnsFalse`, `CreateShortUrlRequestValidatorTests.InvalidUrl_Fails`, `UrlShortenerServiceTests.CreateAsync_InvalidUrl_ThrowsArgumentException` |
| CRT-AC-10 | Given a URL > 2048 chars, then 400; exactly 2048, then accepted. | `SecurityTests.Create_UrlOver2048Characters_Returns400`, `SecurityTests.Create_UrlOfExactlyTheLimit_IsAccepted`, `CreateShortUrlRequestValidatorTests.UrlLongerThan2048Characters_Fails` |
| CRT-AC-11 | Given malformed JSON or a 2 MB body, then 400 (not 500); given a non-JSON content type, then 415. | `SecurityTests.Create_MalformedJson_Returns400`, `SecurityTests.Create_TwoMegabyteBody_Returns400_NotServerError`, `SecurityTests.Create_WrongContentType_Returns415` |
| CRT-AC-12 | Given many concurrent creates of different URLs, then every code is unique. | `ApiEndToEndTests.Create_ManyDifferentUrls_ReturnsUniqueCodes`, `ShortCodeGeneratorTests.FromId_FirstFiveThousandIds_AreUnique` |
| CRT-AC-13 | Given any identity Id, then the code is 6–12 Base62 chars and round-trips through decode. | `ShortCodeGeneratorTests.FromId_FirstId_ReturnsSixCharacterCode`, `ShortCodeGeneratorTests.FromId_AlwaysAtLeastSixAndAtMostTwelveCharacters`, `ShortCodeGeneratorTests.FromId_RoundTripsThroughDecode`, `Base62EncoderTests.Encode_Zero_ReturnsFirstAlphabetCharacter`, `Base62EncoderTests.Encode_KnownValues_ReturnsExpectedCode`, `Base62EncoderTests.Decode_OfEncode_ReturnsOriginalValue`, `Base62EncoderTests.Encode_NegativeValue_Throws`, `Base62EncoderTests.Encode_FirstTenThousandValues_AreUniqueAndCaseSensitive`, `Base62EncoderTests.Decode_InvalidCharacter_ThrowsFormatException`, `Base62EncoderTests.Decode_EmptyString_Throws`, `Base62EncoderTests.Decode_ValueLargerThanLong_ThrowsOverflow` |
| CRT-AC-14 | Given a URL containing SQL or `<script>`, then it is stored as inert data and the database survives. | `SecurityTests.Create_SqlInjectionInUrl_IsStoredAsPlainData_AndDatabaseSurvives`, `SecurityTests.Create_ScriptTagInsideValidUrl_IsEscapedInStoredAndRedirectedUrl`, `SecurityTests.Create_ScriptTagInBody_IsRejected_AndNotEchoedBack` |
| CRT-AC-15 | Given an active link for X expiring in 2 days, when X is POSTed with `expiryDays: 30`, then 200 and `expiresAt` is still the original (not extended). | `ApiEndToEndTests.Create_DuplicateWithLongerExpiryDays_ReturnsExistingLinkUnextended` |
| CRT-AC-16 | Given an active link for X, when X is POSTed again (200), then `expiresAt` ends in `Z` and equals the original link's expiry instant. | `ApiEndToEndTests.Create_DuplicateUrl_ReturnsExpiresAtInUtc`, `ApiEndToEndTests.Repository_ReadsDateTimesAsUtcKind` (root-cause guard) |
| CRT-AC-17 | Given a request body larger than 8 KB, when POSTed, then the server rejects it with 413 before validation (verified by the action's `[RequestSizeLimit]` and the 413 mapping; the in-memory test server does not enforce Kestrel limits). | `ShortenControllerTests.WriteActions_CarryRequestSizeLimitOfMaxRequestBodyBytes` (Create case), `ExceptionHandlingMiddlewareTests.InvokeAsync_BadHttpRequest413_Returns413ProblemJsonWithoutExceptionText` |

## Known limitations

- Codes are sequential and therefore enumerable (`ShortCodeGenerator.cs` remarks). Links are not secret.
- Concurrent duplicate creates can yield two codes for one URL (see edge cases).
- Duplicate detection is per normalized string; `https://a.com/x?b=1&a=2` and `?a=2&b=1` are different links.
