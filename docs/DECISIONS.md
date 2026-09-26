# Technical Decisions

Current engineering baseline. The fake-backed HTTP foundation is present; model versions and data-dependent details remain unresolved.

| Area | Decision | Rationale |
| --- | --- | --- |
| Interaction | Stateless request/response | Movie search does not depend on conversation history |
| Stack | React/TypeScript/Vite, ASP.NET Core/C#, PostgreSQL/pgvector, Docker | Separate presentation, orchestration and catalog storage |
| Environment/deployment target | Local Vite + ASP.NET Core processes with persistent Docker PostgreSQL/pgvector; later production target Vercel + Koyeb Free (Frankfurt, Docker ASP.NET Core) + Supabase PostgreSQL/pgvector | Keep one migration/schema/import concept and environment-specific configuration; this plan does not authorize deployment |
| Dataset | MovieLens Tag Genome 2021 for the primary semantic subset; MovieLens 32M for exact-ID structured enrichment | Keep semantic membership/tags fixed while adding verified identifiers, genres and rating aggregates |
| B1 metadata input | `raw/metadata_updated.json` | Its 84,661 IDs and all six shared field values match `metadata.json`; it omits unused `dateAdded` |
| B1 Tag Genome input | `scores/tagdl.csv`, ranked by raw score | Source authors report lower tag-prediction MAE in all ten folds; preserve rank without clipping out-of-range values |
| B1 initial scored catalogue | Intersection of scored IDs and metadata IDs: 9,730 source records | Four scored IDs lack metadata required for a usable movie card; record exclusions rather than fabricate values |
| B1a metadata export | Deterministic JSONL of the 9,730 intersection records, preserving raw source IDs/text and unrounded MovieLens 0–5 ratings | Keep provenance and avoid premature person/TMDB/filter assumptions |
| B2 semantic catalog core | Raw TagDL top 10; deterministic Title/Year/raw Director/raw Cast/tags text; versioned JSONL and timestamp-free content fingerprint | Preserve reproducibility and keep identifiers, ratings and TMDB prose outside embedding text |
| B04 v2 enrichment | Exact `movieId` join to MovieLens 32M; direct offline TMDB API v3 details by existing `tmdbId`, with versioned cache and bounded retry | Preserve all 9,730 films while providing available genres/ratings and runtime/language/poster without IMDb matching or runtime TMDB dependency |
| Parsing | Gemini structured output plus backend validation | The model interprets constraints; application code enforces them |
| Retrieval | Mandatory predicates, then exact cosine ranking | Similarity never overrides a hard constraint |
| Soft preferences | Remain in semantic text | Avoid invented numerical cutoffs |
| Missing metadata | Fails an active hard filter | Unknown values do not prove eligibility |
| Result count | Up to ten distinct films; partial notice for 1–9 | Avoid padding or relaxed constraints |
| Embeddings | Offline film vectors; compatible runtime query vector | Avoid repeated generation and mixed vector spaces |
| Enrichment | ML32M structured fields plus cached TMDB runtime/language/poster; IMDb outbound links | Keep enrichment outside the recommendation request |
| Runtime data | Compact derived catalog | Exclude bulk supporting reviews and raw ratings; retain approved per-film aggregates |
| Verification | Provider interfaces and synthetic fixtures | Check behavior without consuming live quotas |
| Interface | SR/EN and compact HTTP DTOs | Separate presentation from internal provider/storage data |
| Public HTTP contract | [API v2.0.0](API_CONTRACT.md) for the authorized local fake flow, superseding A1 v1.0.0 on 2026-09-26 | Frontend/backend share exact `language`/`message`, alert/movies/error envelopes and localization before independent implementation |

The active v2 boundary requires exact `language`/`message` fields, strict UTF-8 JSON with a 65,536-byte body limit, at most 500 Unicode scalar values after the documented outer trim plus cheap local usability checks, 1–10 distinct eligible cards in a tagged movies response, backend-localized business alerts and separate sanitized technical errors. The endpoint path remains `/api/recommendations`, without a URL version segment or automatic client retry. The earlier A1 v1.0.0 wire shape is historical. The explicitly approved yearMin/runtimeMax/single exact catalog genre parser profile is temporary for fake-only testing; it does not freeze final B2/C1 filter grammar, model or provider configuration.

B1 was approved by Lazar on 2026-09-24 after [source inspection](../Testiranje/reports/tag-genome-source-review.md). The sole absent score pair `(item_id=1, tag="airplane")` remains missing; no zero/imputed score is introduced. The ten `tags.json` names without score rows are excluded from scored semantic candidates. Preserve IMDb digit strings, including leading zeros, and exact score tag text. Empty director/cast strings are missing values, not a match for a person condition. Keep Glmer locally as an alternate representation within the same MovieLens dataset; it is not a second runtime catalogue. These choices do not set the eventual semantic-text tag count, title/year normalization, person filters or evaluation protocol.

B1a was approved by Lazar on 2026-09-24 after the scored-catalog metadata profile. The exact source-to-export mapping is in [DATA_MAPPING.md](DATA_MAPPING.md).

The provider-independent B2 semantic core was approved by Lazar on 2026-09-24 and is frozen in [SEMANTIC_CATALOG.md](SEMANTIC_CATALOG.md). Director and cast remain raw authoritative strings without fuzzy matching or inferred identities. Lazar approved the [B04 v2 offline enrichment contract](ENRICHMENT_V2.md) on 2026-09-25: 9,585 exact ML32M matches are used; 145 unmatched films remain with nullable structured fields; eight matched films without TMDB ID retain ML32M data but no TMDB details. Exact IMDb reconciliation found no additional match, and further mapping/fallback is deferred, not blocking. B3 model, dimension and usage decisions remain open.

A2 foundation was approved by Lazar on 2026-09-24. The small project layout, toolchain pins, locks, development ports and fake-only foundation rules are in [toolchain specification](TOOLCHAIN.md). Provider/database package versions and live-call budgets remain later gates.

Supported filter operators/aliases, parsed actor/director matching and model/dimensions require later documented decisions before their implementation. B04 v2 enrichment behavior is fixed separately; no similarity threshold or approximate index is enabled by default.

Academic evaluation compares semantic-only and hybrid retrieval. Its detailed protocol awaits academic confirmation; no query count, split or relevance metric is finalized.
