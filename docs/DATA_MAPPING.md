# MovieLens Metadata Export — B1a v1

Status: approved by Lazar on 2026-09-24. This contract defines the first, provider-free ETL stage. It does not define the runtime database schema, semantic tags/text, TMDB enrichment, embeddings or person-filter semantics.

## Inputs and membership

- Source root: the locally extracted, ignored MovieLens Tag Genome 2021 directory `database/data/raw/tag-genome-2021/`. Do not copy or modify source files.
- Metadata: `raw/metadata_updated.json`, one JSON object per physical line.
- Scored membership: unique `item_id` values from `scores/tagdl.csv` (header exactly `tag,item_id,score`). Read all score rows; do not derive a membership cutoff from score sign or magnitude. Preserve missing score cells as missing.
- Join by exact positive safe-integer `item_id`. The approved source snapshot has 9,734 scored IDs, 84,661 metadata IDs and a 9,730-record intersection. The four score-only IDs receive the exclusion code `MISSING_METADATA` in the manifest; their identities may be recorded there, but no synthetic movie is created.
- A duplicate metadata ID, malformed input, missing/wrong-type required field, unsafe ID, duplicate output ID, unexpected join count or invalid mapped value fails the run. Never silently repair an input.

## Output

Write UTF-8 JSON Lines named `movies-metadata.jsonl` under ignored `database/data/derived/b1a-v1/`, ordered by ascending `movieLensId`. One line contains exactly these camelCase properties:

| Property | Type | Mapping |
| --- | --- | --- |
| `movieLensId` | positive integer | Exact source `item_id`; must be a safe integer |
| `rawTitle` | string | Exact source `title`, unchanged |
| `title` | nonblank string | Trim outer Unicode whitespace; remove one final ASCII-space-plus-`(YYYY)` suffix; trim the remaining outer whitespace |
| `year` | integer | Four decimal digits from the required final `(YYYY)` suffix after outer trim; no guessed year |
| `imdbId` | nonempty ASCII-digit string | Exact source `imdbId`, including leading zeros; unique as a string |
| `movieLensAvgRating` | number in 0–5 | Exact finite source `avgRating`; no rescaling or rounding |
| `directedByRaw` | string or `null` | Outer-trim `directedBy`; blank becomes `null`; do not split into people |
| `starringRaw` | string or `null` | Outer-trim `starring`; blank becomes `null`; do not split into people |

All eight fields appear on every line. `rawTitle` preserves source provenance; `title` is the display title. The observed 9,730 records all have a valid year suffix after trimming; two raw titles have trailing spaces. Construct public IMDb URLs only in the backend as `https://www.imdb.com/title/tt{imdbId}/` without numeric coercion. No genres, runtimes, languages, posters, TMDB IDs, parsed person arrays or semantic tags are added in this stage.

The output must be byte-identical on repeated runs with the same source and implementation version. The manifest records `B1a-v1`, paths and SHA-256 of both inputs and output, counts, exclusion-code totals and a generated UTC timestamp; the timestamp is outside the data file/hash. Validation failure must leave any previously successful export intact. Data output and manifest stay ignored; ETL code/tests and a compact source review may be tracked.

## Verification

Use tiny synthetic inputs to verify quoted CSV tags, unique membership, title suffix/trailing-space handling, IMDb leading zeros, blank people, unrounded ratings, deterministic order and rerun, and failure without replacing a prior valid output. A full run on the locally extracted dataset must report 9,730 output records and four `MISSING_METADATA` exclusions without a provider call. Do not treat this source transformation as the academic recommendation evaluation.
