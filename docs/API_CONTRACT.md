# Recommendation API — Draft

**Status: draft / not frozen / no endpoint implemented.**

This is the initial interface outline; finalize it before independent frontend/backend implementation. See [architecture](ARCHITECTURE.md) and [technical decisions](DECISIONS.md).

## Endpoint and request

```http
POST /api/recommendations
Content-Type: application/json
```

```json
{
  "query": "Find dark science fiction after 2010, at most 120 minutes, preferably with little action.",
  "locale": "en"
}
```

Planned required request fields:

| Field | Type | Planned meaning |
| --- | --- | --- |
| `query` | string | Film request, 1–2,000 characters after trimming outer whitespace |
| `locale` | `"sr"` or `"en"` | UI language; not a constraint on the film's original language |

Every submission is stateless. There is no history, session/profile, or public experiment-mode parameter. Final treatment of missing/extra fields and Unicode length counting must be frozen in Phase A. The locale does not turn a user's soft preference into a hard filter.

## Success outline

HTTP 200:

```json
{
  "results": [
    {
      "title": "Example Movie",
      "year": 2016,
      "imdbUrl": "https://www.imdb.com/title/tt0000001/",
      "posterUrl": null
    }
  ]
}
```

This synthetic example demonstrates a one-result partial response, not an actual recommendation or metadata assertion about that IMDb ID.

| Field | Type | Planned behavior |
| --- | --- | --- |
| `results` | movie array | 1–10 distinct eligible films in ranked order |
| `title` | string | Nonempty display title |
| `year` | integer or null | Omit the year from the overlay when unknown |
| `imdbUrl` | string | Outbound IMDb movie link derived from the catalog IMDb ID |
| `posterUrl` | string or null | Approved-provider poster URL; null/broken image gets a local fallback |

Target ten movies; a 1–9 response uses the same envelope. The frontend derives the localized "fewer matches available" notice from the count. Never fill with duplicates or movies violating constraints. With zero matches, return the planned `NO_RESULTS` error rather than a padded list.

Do not expose embeddings, raw Gemini output, parser `reason`, database-only IDs, or unused movie fields. URL validation and exact nullable display examples remain freeze tasks.

## Error outline

```json
{
  "error": {
    "code": "NO_RESULTS"
  }
}
```

The frontend maps stable codes to friendly localized messages. Do not render arbitrary provider errors or parser explanations.

| HTTP | Code | Intended condition |
| --- | --- | --- |
| 400 | `INVALID_REQUEST` | Malformed/invalid input or unsupported locale |
| 422 | `INVALID_MOVIE_QUERY` | Insufficient movie-search intent |
| 422 | `UNSUPPORTED_FILTER` | An explicit requested criterion cannot be reliably enforced |
| 422 | `NO_RESULTS` | No movie satisfies mandatory conditions |
| 429 | `AI_RATE_LIMITED` | Temporary throttling or an undifferentiated provider rate limit |
| 429 | `AI_QUOTA_EXHAUSTED` | Confirmed exhausted quota; do not infer this from every 429 |
| 502 | `AI_INVALID_RESPONSE` | Provider output fails structure or business-value checks |
| 503 | `AI_PROVIDER_UNAVAILABLE` | AI provider outage or timeout |
| 503 | `SEARCH_UNAVAILABLE` | Database/search temporarily unavailable |
| 500 | `INTERNAL_ERROR` | Unexpected internal failure |

Final retry/header behavior is a Phase A task. TMDB enrichment failure is recorded by the offline job; there is no fresh TMDB API dependency in the recommendation request. Image loading failure is a UI fallback, not an AI error.

## Internal parser boundary — incomplete by design

Gemini returns intent validity, semantic query text, and structured hard filters. Backend validation is mandatory. The internal parser object is not the public response. Supported filter fields, rating source/scale, actors/directors, negation, combinations, and exact range semantics depend on A/B decisions and must be completed before the final parser is implemented.

Rules already fixed:

- A film violating a hard filter is never returned.
- Missing metadata cannot satisfy an active condition.
- Explicit "at most 120 minutes" is a hard maximum when supported.
- "I would prefer a shorter movie" remains semantic text.
- PostgreSQL filters; pgvector ranks eligible candidates only.
- Start without a similarity threshold; add one only if tests justify it.

## Compatibility

The public contract is not versioned or frozen. Input edge cases, URL rules, retry semantics and representative fixtures must be specified before implementation. Internal parser fields depend on catalog metadata. Academic methodology is independent of the HTTP boundary.

MovieLens Tag Genome 2021 is confirmed by Lazar (2026-09-24). Academic evaluation choices remain pending mentor confirmation independently of the HTTP contract. Do not add research metrics or a public retrieval-mode switch while finishing this contract.
