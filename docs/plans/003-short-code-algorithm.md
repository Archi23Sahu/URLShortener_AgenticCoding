# Plan 003 — Short-code algorithm (greenfield, Task 3)

| | |
|---|---|
| Status | Implemented |
| Spec | [001 — Create short URL](../specs/001-create-short-url.md) |
| ACs satisfied / added | the code-format and uniqueness ACs of spec 001 |
| Scenario | [Greenfield](../scenarios/greenfield.md), Task 3 |

## Goal

Generate short codes that are at least 6 characters, URL-safe, and unique without collision handling.

## Options considered

| Option | Verdict |
|--------|---------|
| Random strings | Needs collision checks and retries |
| GUID/UUID | 22–36 characters; not short |
| Hash truncation | Collisions; needs retries |
| **Counter + Base62 over the database identity** | **Chosen:** unique by construction, short, no retry loop |

## Out of scope

Making codes unguessable. Codes are sequential by design; links are not treated as secret (listed under Limitations in the README).

## Changes

| File | Change | Why |
|------|--------|-----|
| `src/Services/Base62Encoder.cs` | Encode/decode Base62 | Pure and unit-testable |
| `src/Services/ShortCodeGenerator.cs` | `Code = Base62(Id + 62^5)` | `62^5` offset guarantees ≥ 6 characters |
| `src/Repositories/ShortUrlRepository.cs` | Insert with a placeholder code, then set the real code in the same transaction | The identity is known only after insert (see [plan 004](004-persistence-layer.md)) |

## Steps

1. Encoder — verify: `dotnet test --filter "FullyQualifiedName~Base62EncoderTests"`.
2. Generator — verify: `dotnet test --filter "FullyQualifiedName~ShortCodeGeneratorTests"`.

## Test strategy

| Concern | Level | Test | Notes |
|---------|-------|------|-------|
| Round-trip, invalid input, first 10 000 values unique | unit | `Base62EncoderTests` | |
| Minimum length and format | unit | `ShortCodeGeneratorTests` | |
| Concurrent creates give unique codes | integration | `ApiEndToEndTests.Create_ManyDifferentUrls_ReturnsUniqueCodes` | 25 concurrent creates |

## Risks and rollback

- Sequential codes are enumerable → accepted trade-off, documented.
- Changing the offset changes every future code but not stored ones.

## Docs to update

- [x] README Trade-offs (Base62 vs GUID)

## Approval

- [ ] Plan approved by: ______ on ______
