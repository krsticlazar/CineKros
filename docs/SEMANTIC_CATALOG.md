# Semantic Catalog Contract — Approved B2 Core

Version: `B2-semantic-catalog-v1`. Approved by Lazar on 2026-09-24.

This contract freezes the provider-independent semantic record derived from the approved `B1a-v1` metadata export. It does not approve a Gemini model, embedding model, embedding dimension, PostgreSQL schema, live provider run, filter grammar, TMDB request policy or academic evaluation.

## Relevant tags

- Source: exact pairs from `scores/tagdl.csv` for each `B1a-v1` movie.
- Scores remain raw finite TagDL values. They are not clipped, normalized or interpreted as probabilities.
- Order: score descending, then exact tag name ascending by ordinal comparison.
- Selection: the first `min(10, eligible-pair-count)` pairs.
- Missing pairs remain absent and are never imputed. The known `(movieLensId=1, tag="airplane")` pair remains missing.
- The resulting version is `B2-tagdl-top10-v1`.

## Semantic text

The version is `B2-semantic-text-v1`. Render exactly one UTF-8 line with no leading/trailing whitespace or trailing newline:

```text
Title: {title}. Year: {year}. Director: {directedByRaw or "unknown"}. Cast: {starringRaw or "unknown"}. Tags: {ordered tag names joined with ", " or "none"}.
```

Use the approved B1a values without person splitting, fuzzy matching or inferred identities. Tag scores remain structured metadata and are not rendered. Do not render `movieLensAvgRating`, `rawTitle`, identifiers, TMDB synopsis or any other TMDB text/field.

## Deterministic catalog output

The semantic catalog is versioned compact UTF-8 JSONL, one object per line, ordered by ascending `movieLensId`, with no blank lines. It preserves the eight `B1a-v1` fields and adds:

- `relevantTags`: non-null ordered array of `{ "name": string, "score": finite number }`;
- `semanticText`: non-null string rendered by the exact template above.

JSON property order is fixed for byte-stable output. Consumers still parse fields by name. The manifest records source paths and lowercase SHA-256 hashes, B1a mapping version, semantic-catalog version, tag-rule version, semantic-template version, output hash/count/order, exclusions and validation results.

The content fingerprint uses canonical UTF-8 JSON with stable key order and includes source hashes and every content-affecting contract version. Timestamps are excluded from the fingerprint. A later change to the tag rule, text template or content source creates a new compatible-catalog version and invalidates vector reuse under the prior fingerprint.

## Enrichment boundary

The first real catalog must make ML32M genres and offline TMDB runtime, original language and poster metadata available where source values exist before B05. The 145 ML32M-unmatched films and eight matched films without TMDB ID are retained with the approved null fields. TMDB is never a runtime dependency of the recommendation endpoint. TMDB synopsis and other TMDB prose are excluded from `semanticText`.

The current source join, nullable metadata, TMDB detail/cache and request boundaries are defined in [ENRICHMENT_V2.md](ENRICHMENT_V2.md); the former [B04 v1 contract](TMDB_ENRICHMENT.md) is historical. B3 remains open: no parser model, embedding model, dimension, task type, quota or live call is approved by this contract.
