# TMDB Offline Enrichment Contract — B04 v1 (superseded)

Historical contract only. B04 v2 is defined in [ENRICHMENT_V2.md](ENRICHMENT_V2.md); no future worker should implement the IMDb `/find` flow below.

Version: `B04-tmdb-enrichment-v1`. Approved by Lazar on 2026-09-24.

This contract defines the fake-testable offline TMDB adapter/cache stage between the accepted `B1a-v1` metadata export and B05. It does not authorize a live TMDB request. A real enrichment run requires a separate packet with exact request/time caps.

## Identity and request sequence

- The cache key is the exact B1a IMDb digit string. Preserve leading zeroes.
- The TMDB external identifier is `tt{imdbId}`.
- Use TMDB API v3 with bearer authentication from `TMDB_READ_ACCESS_TOKEN` supplied only at process runtime.
- First request: `GET /3/find/{tt-imdb-id}?external_source=imdb_id`.
- Exactly one item in `movie_results` is a match. Zero items produce terminal `not_found`. More than one produces terminal `ambiguous`; never choose among them.
- For a single match, request `GET /3/movie/{tmdbId}` to obtain the approved detail fields.
- URL path/query values must be encoded by the HTTP client. Do not log the token or authorization header.

## Normalized fields

Every input movie remains in the enriched output. The enrichment object contains only these nullable fields, in this order:

- `tmdbId`: positive integer or `null`;
- `runtimeMinutes`: positive integer or `null`;
- `genres`: non-null array of unique objects `{ "id": positive integer, "name": nonblank string }`, sorted by ascending TMDB genre ID; use `[]` when none are supplied;
- `originalLanguage`: nonblank TMDB `original_language` string or `null`;
- `posterPath`: a TMDB relative poster path beginning with `/` or `null`.

Do not persist a final image URL. Do not request, cache or export synopsis, overview or other TMDB prose. `not_found` and `ambiguous` retain the movie with all nullable enrichment fields `null` and `genres=[]`.

## Cache and refresh

- Cache version: `tmdb-cache-v1`.
- One atomic JSON cache entry per exact IMDb key. Entries record cache version, key, status, approved normalized fields and the minimal response provenance needed for validation; they never record credentials or unapproved provider prose.
- Successful, `not_found` and `ambiguous` entries are terminal reusable cache hits for this cache version.
- There is no time-based expiry or automatic refresh. Refresh occurs only through an explicit refresh option for exact keys or a new cache version.
- A cache entry with a wrong key/version/shape/hash is invalid and must not be reused.
- Transient/network/timeout/HTTP 429/5xx failures are checkpoint failures, not terminal cache misses. They do not create a reusable cache entry and remain eligible on the next explicit run.
- HTTP 401/403 is a configuration failure and stops scheduling new requests. Other 4xx responses are not retried in the current run, are recorded as non-terminal failures in the run checkpoint/manifest, and do not become reusable terminal misses.

## Timeout, retry and concurrency

- Per-attempt HTTP timeout: 10 seconds.
- Maximum three attempts total for network failures, timeouts, 429 and 5xx.
- Honor a valid `Retry-After` delta or date. Without one, use cancellation-aware deterministic exponential backoff of 1 second then 2 seconds; no jitter.
- Do not retry other 4xx responses.
- Default maximum concurrency is 4 and is configurable only in the inclusive range 1–4.
- Cancellation stops new work, preserves already atomically published cache entries/checkpoint state and never publishes an invalid final export.

## Output, provenance and fingerprint

The enriched JSONL preserves every B1a field and ascending `movieLensId`, then appends `tmdbId`, `runtimeMinutes`, `genres`, `originalLanguage` and `posterPath` in that order. Per-record operational status is not a runtime catalog field; status/counts belong in the cache and manifest.

The manifest records TMDB API v3, `tmdb-cache-v1`, the B03 input path/hash/mapping version, cache content hash, request-policy configuration, output path/hash/count and counts/IMDb keys by success, `not_found`, `ambiguous` and non-terminal failure class. The content fingerprint is canonical and excludes timestamps. A partial run may preserve prior output but must not replace the final enriched export while unresolved non-terminal failures remain.

TMDB attribution is a global UI/About requirement, not a per-movie field. This ETL stage records TMDB API v3/cache provenance only; it does not modify the frontend.

## Runtime boundary

The recommendation endpoint reads imported cached metadata only. It never calls TMDB. B3 parser/embedding model, vector dimension, provider quota and all live provider calls remain unapproved.
