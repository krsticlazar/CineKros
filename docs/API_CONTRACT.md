# Recommendation API — Public Contract v2.0.0 (fake-backed pre-database flow)

Status: MAIN-frozen for the user-authorized local fake flow on 2026-09-26. This intentionally supersedes the incompatible v1.0.0 `query`/`locale` and `results`/`error` business boundary. The prior accepted contract is retained in local planning archive. v2 does not authorize PostgreSQL/pgvector, live Gemini/TMDB, embeddings, final B2 filter grammar or production deployment.

## Request and transport

`POST /api/recommendations` with `Content-Type: application/json` (no charset or explicit UTF-8) and exactly two case-sensitive required properties, without extras/duplicates/coercion:

```json
{"language":"sr","message":"Želim mračan SF posle 2010. do dva sata."}
```

`language` is exactly `sr` or `en` and controls localized UI copy, **not** the language of the internal semantic query or a film-language filter. `message` is user natural language. The browser sends neither a Gemini prompt nor a parsed query. Encoded body limit remains 65,536 bytes. Reject malformed JSON, wrong media/charset, invalid UTF-8, duplicate/extra properties, wrong types or unsupported language before invoking a parser/search provider.

Trim only outer Unicode whitespace defined in v1: U+0009–U+000D, U+0020, U+0085, U+00A0, U+1680, U+2000–U+200A, U+2028, U+2029, U+202F, U+205F and U+3000. Do not rewrite inner text. Count Unicode scalar values after trim; accept 1–500 before the cheap usability checks, reject unpaired surrogates. The cheap local checks then reject fewer than two Unicode letter/digit scalars, punctuation/symbol-only input, and one identical letter/digit repeated at least eight times with no other letter/digit (e.g. twenty `a` characters). `horor`, `SF` and `dark sci-fi` pass locally. No dictionary, spellcheck or natural-language model is part of local validation. Parser rejection of a locally valid but unclear/non-film request is a separate controlled alert.

Malformed transport and locally invalid input return the `INVALID_REQUEST` alert (HTTP 400), with zero parser/search calls. The backend is authoritative; client-side early checks may improve UX but may not replace server checks. The local fake deployment uses ASP.NET Core's built-in, configurable fixed-window per-IP rate limit (default 30 requests/60 seconds, no new dependency); rejection is the sanitized technical `RATE_LIMITED` error (HTTP 429) before parser/search. Production proxy/IP policy and limits require a later review.

## Internal parser boundary (never sent to browser)

The parser consumes a versioned system instruction separately from user content `{ "language": ..., "message": ... }` and returns exactly one tagged result:

```json
{"type":"query","query":{"hardFilters":{"yearMin":2010,"runtimeMax":120,"genres":["Sci-Fi"]},"semanticQuery":"dark atmospheric science fiction"}}
```

or:

```json
{"type":"alert","alertCode":"QUERY_UNCLEAR"}
```

For this **temporary fake-only slice**, `hardFilters` has no extra keys and may contain `yearMin` (integer, inclusive), `runtimeMax` (positive integer minutes, inclusive), and `genres` (exactly one element if present). The genre is one exact MovieLens 32M catalog label from: `Action`, `Adventure`, `Animation`, `Children`, `Comedy`, `Crime`, `Documentary`, `Drama`, `Fantasy`, `Film-Noir`, `Horror`, `IMAX`, `Musical`, `Mystery`, `Romance`, `Sci-Fi`, `Thriller`, `War`, `Western`. `(no genres listed)` is not a genre. Textual filter values and nonempty `semanticQuery` are in English for both user languages; the title of a film is never translated. Do not copy a Serbian phrase into `semanticQuery`. An active filter never admits null metadata. Any other explicit hard criterion is `UNSUPPORTED_REQUEST` in this slice; no filter is silently dropped or weakened. This does not freeze final B2/C1 operators, aliases, person semantics or hard-filter-only retrieval/ranking.

Allowed parser alert codes are `QUERY_UNCLEAR`, `NOT_MOVIE_REQUEST`, `UNSUPPORTED_REQUEST`. The parser does not generate alert prose, film lists, IMDb/TMDB fields, SQL, or recommendations. Validate even a fake/provider result before search: exact tagged shape, allowed keys/codes, value types/bounds, source-label genre and English nonempty semanticQuery. Malformed/invalid parser output is a sanitized technical `PARSER_INVALID_RESPONSE`, never executable search input. A hard-filter-only request whose semantic text cannot be specified under the deferred B3 policy returns `QUERY_UNCLEAR` in this temporary fake flow; it must not cause a live embedding request. No real Gemini adapter is installed or invoked.

## Normal business responses

Normal business responses are **only** the following two tagged shapes. The internal parser query/hard filters and provider diagnostics never appear on the wire.

Alert (HTTP 400 for local `INVALID_REQUEST`, 422 for parser alerts, 200 for `NO_RESULTS`):

```json
{"type":"alert","alert":{"code":"NO_RESULTS","message":"Nema filmova koji ispunjavaju sve obavezne uslove."}}
```

Business alert codes: `INVALID_REQUEST`, `QUERY_UNCLEAR`, `NOT_MOVIE_REQUEST`, `UNSUPPORTED_REQUEST`, `NO_RESULTS`. The backend selects deterministic localized text by code and request language; if language is absent/invalid, use Serbian for the `INVALID_REQUEST` message. No browser `alert()` and no provider-generated prose.

Movies (HTTP 200):

```json
{"type":"movies","movies":[{"title":"Synthetic Film 01","year":2014,"imdbUrl":"https://www.imdb.com/title/tt0000001/","posterUrl":null}],"meta":{"count":1,"partial":true}}
```

The compact card retains the already accepted v1 fields: `title` nonblank, `year` integer/null, backend-constructed HTTPS `imdbUrl` from a valid IMDb ID, and validated HTTPS TMDB `posterUrl`/null. `imdbId` and relative `posterPath` remain internal catalog values, not duplicate wire fields. Cards are distinct by movie identity, ranked in provider order, at most ten; no raw vector/parser/data-only fields. `meta.count` exactly equals `movies.length`; `partial` is `true` iff count is 1–9, `false` iff 10. Zero produces `NO_RESULTS`, not an empty movies response. Hard filters must be enforced by the fake search boundary as well as later real search; never pad or relax to ten. The frontend displays the existing localized partial notice only when `meta.partial` is true.

Backend-owned business copy (exact text):

| Code | SR | EN |
| --- | --- | --- |
| `INVALID_REQUEST` | Unesi ispravan zahtev za filmove. | Enter a valid movie request. |
| `QUERY_UNCLEAR` | Napiši malo jasnije kakav film tražiš. | Describe the movie you want more clearly. |
| `NOT_MOVIE_REQUEST` | Napiši zahtev za preporuku filma. | Enter a movie recommendation request. |
| `UNSUPPORTED_REQUEST` | Jedan obavezan uslov trenutno ne možemo pouzdano da proverimo. Izmeni upit. | We cannot reliably check one required condition yet. Please revise your request. |
| `NO_RESULTS` | Nema filmova koji ispunjavaju sve obavezne uslove. | No movies meet all the required conditions. |

The retry button text is SR `Pokušaj ponovo` / EN `Try again`; new-search text is SR `Nova pretraga` / EN `New search`. The existing partial notice remains SR `Prikazani su svi pronađeni filmovi koji ispunjavaju uslove.` / EN `Showing all available movies that meet your requirements.` Film titles are never localized.

## Technical failures (not business alerts)

Technical/rate-limit failures keep a separate sanitized wire shape and status, with no internal payload:

```json
{"error":{"code":"PROVIDER_UNAVAILABLE"}}
```

`RATE_LIMITED` = HTTP 429, `PARSER_INVALID_RESPONSE` = HTTP 502, `PROVIDER_UNAVAILABLE` (fake parser failure / future provider outage) = HTTP 503, `SEARCH_UNAVAILABLE` (fake search failure / future search outage) = HTTP 503, and `INTERNAL_ERROR` = HTTP 500. The frontend maps these codes to deterministic SR/EN text, treating unknown/malformed responses as generic `INTERNAL_ERROR`; raw exception/stack/provider payload/key is never shown. No automatic browser retry, public `Retry-After` contract or real provider retry is introduced. Cancellation propagates where possible.

## Frontend state and local fake integration

On submit, preserve Enter/Send behavior, trim the message, immediately start `fetch` to relative `/api/recommendations`, animate the prompt out and loading in. Use `AbortController` and a monotonically increasing request generation so an older completion cannot overwrite the latest state. A business alert opens an accessible CineKros dialog (not `window.alert`); retry closes it, restores the prompt, keeps the previous text and locale. A movies response hides loading and enters cards with restrained motion, preserving IMDb/poster/focus/grid behavior; new search removes results without page reload, restores the prompt and retains locale (input may clear). Technical errors use sanitized localized UI, distinct from a business alert. Respect `prefers-reduced-motion` throughout. The development Vite server proxies `/api` to the existing backend loopback `http://127.0.0.1:5179`, with no browser provider credentials or broad CORS policy; production API base URL configuration remains a separate deployment concern.

Local fake adapters must deterministically expose ten, 1–9, zero, parser-alert and simulated technical-error paths via documented demo messages; scenario routing belongs behind parser/search interfaces, never inside the HTTP controller. The fake search must enforce the temporary hard-filter slice against synthetic structured candidate metadata, including null-fails-active-filter. No PostgreSQL, pgvector, live Gemini, live TMDB or live embeddings. This v2 fake flow stops before C1; future real adapters require their own B2/B3/C1 gates and explicit authorization.

## Revision discipline

Backend and frontend workers read this exact active contract. Changes are MAIN-owned and require corresponding fixture/test updates before worker release. HTTP request and response examples, test fixtures and implementation must agree. No worker invents an additional public field, internal filter, translation/alias or hidden fallback.
