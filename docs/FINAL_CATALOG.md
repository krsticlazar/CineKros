# Combined Catalog Export — B05-A Provider-Free Profile

Version: `B05a-combined-catalog-v1`. MAIN's small reversible implementation profile (2026-09-25), mechanically composing already approved B2 semantic and B04 v2 structured fields. It does not choose an embedding model/dimension, database schema, provider, filter grammar, ranking rule or evaluation method.

## Inputs and identity

- Input A is a successfully published `B2-semantic-catalog-v1` JSONL plus manifest. Input B is a successfully published `B04-ml32m-tmdb-v2` JSONL plus manifest. Both carry the same accepted B1a identity and exactly 9,730 ascending `movieLensId` records. Validate source JSONL hashes against their manifests, the declared versions, source B1a JSONL hash and full ID set before publishing.
- For each ID, the first eight B1a fields must agree by typed value (including decimal numeric equality for `movieLensAvgRating`); any mismatch, missing/extra ID, duplicate, wrong order or malformed field fails the run. B1a-derived values come from B2 after that agreement check, never from a heuristic reconciliation.
- B2 remains the sole source of `relevantTags` and `semanticText`. B04 remains the sole source of `tmdbId`, `genres`, `averageRating`, `ratingCount`, `runtimeMinutes`, `originalLanguage`, `posterPath`. Do not recalculate or fill nulls during the merge. No raw user tags, synopsis or other provider prose enters semantic text.

## Output

Write compact UTF-8 JSONL `movies-catalog.jsonl`, one object per line sorted by ascending `movieLensId`, with exactly these 17 fields in order: `movieLensId`, `rawTitle`, `title`, `year`, `imdbId`, `movieLensAvgRating`, `directedByRaw`, `starringRaw`, `relevantTags`, `semanticText`, `tmdbId`, `genres`, `averageRating`, `ratingCount`, `runtimeMinutes`, `originalLanguage`, `posterPath`. Preserve B2 tag order/text and B04 metadata/null values. Do not add per-record provenance, cache status, vectors or generated text.

The manifest records the combined version, B2/B04 versions and content fingerprints, both input paths and lowercase SHA-256 hashes (JSONL and manifests), shared B1a hash, output path/hash/count/order, null/coverage counts for the seven enrichment fields, validation result and a UTC generation timestamp. The content fingerprint is SHA-256 of this exact compact UTF-8 JSON payload with the listed property order and lowercase source hashes, without timestamp: `{ "catalogVersion": "B05a-combined-catalog-v1", "semanticCatalogVersion": "B2-semantic-catalog-v1", "enrichmentVersion": "B04-ml32m-tmdb-v2", "b1aSha256": <shared B1a JSONL hash>, "b2JsonlSha256": <B2 JSONL hash>, "b04JsonlSha256": <B04 JSONL hash> }`.

Publish only to a new absent directory: validate both staged files, then same-volume directory rename. Never overwrite an earlier successful output. Failed validation, I/O or cancellation cannot leave a partial final pair. This is an offline merge; recommendation endpoints never call TMDB. A merge over a fake-HTTP B04 output is a **test/prototype artifact**, not a real TMDB-enriched production catalog.

Later B05 embedding work still needs a separate B3 model/dimension/task-type and usage gate. C1 schema/filter decisions remain separate.
