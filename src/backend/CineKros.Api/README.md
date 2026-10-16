# CineKros.Api

ASP.NET Core API for CineKros movie search. Gemini parses a user's request into validated search intent; application code applies required PostgreSQL filters and, when a semantic query is present, ranks eligible movies with the local multilingual E5 model and pgvector. Gemini does not select movies.

## Request flow

The frontend sends POST /api/recommendations with JSON containing language (sr or en) and message. Program.cs selects the fake or real route and configures CORS and a per-IP limit of 30 requests per minute.

```text
HTTP request
  → ReadRequestAsync
  → Gemini v5 structured output and validation
  → RealRecommendationService
  → selected-language readiness and local E5 query vector, when semanticQuery is present
  → one PostgreSQL query: hard-filtered eligible relation, then pgvector ranking
  → up to 10 movie cards
```

The real flow uses the v5 prompt and schema from RealProviders/query-parser-v5.md and query-parser-v5.schema.json. GeminiRealQueryParser sends the request to gemini-3.1-flash-lite; RealParsedQueryValidator validates the response and rejects malformed or unsupported required conditions. The service revalidates its typed result before searching. Positive preferences that cannot be guaranteed may contribute to semanticQuery; mandatory conditions must be supported hard filters.

## Runtime modes and configuration

CINEKROS_RECOMMENDATION_MODE accepts fake or real. When unset, only Development defaults to fake; other environments require an explicit value, and Production requires real. Fake mode uses FakeQueryParser and FakeMovieSearch and does not use Gemini or the real search repository.

Real mode requires these process environment variables:

- DATABASE_CONNECTION_STRING
- GEMINI_API_KEY
- CINEKROS_E5_MODEL_DIR

The database connection is forced to read-only transactions and must match the expected released database. The normal real runtime registers CreateFullBilingualProductionRepositoryAsync; the separate Serbian POC is opt-in through CINEKROS_SERBIAN_POC=true, Development only, and uses a different database expectation. Do not treat that POC as the normal full runtime.

The full release contains 9,730 movies and English and Serbian embedding sets. At startup, Program.cs resolves the E5 model and calls MovieSearchRepository.EnsureRuntimeReadyAsync before mapping the route. That check verifies both language sets, their catalog identity, dimensions, fingerprints, and embedded counts. A V5 semantic request also checks the selected language set before query inference; the search method verifies it again when executing the query.

The shared CineKros.Embedding library loads the multilingual E5-base INT8 ONNX artifacts from the local model directory. Query vectors have 768 dimensions. Serbian semantic text is normalized to NFC and transliterated from Cyrillic to Serbian Latin before inference; English query text is passed through unchanged. Model artifacts are not stored in this repository.

## Search behavior

HardFilterSqlBuilder.Build creates allowlisted SQL predicates and typed Npgsql parameters for year, runtime, genres, minimum rating, and original language. all genres require every listed genre; any genres require at least one. Rating input is normalized from the parser's 1–10 representation to the MovieLens 0–5 scale: five accepts 1–5, ten divides by two, and unspecified divides values above 5 by two. The SQL operator is inclusive gte or strict gt.

The full multilingual runtime uses the language-aware MovieSearchRepository.SearchHybridAsync overload. It chooses me.embedding for English and me.embedding_sr for Serbian through the closed SearchLanguageColumn.For mapping. SQL is assembled as a materialized eligible-movie relation with the hard-filter fragment, then joined to movie_embeddings and ordered by cosine distance (<=>) ascending, followed by movie ID ascending, with LIMIT 10. The vector is a typed pgvector parameter; CreateDataSource enables Npgsql's vector mapping. SearchHardOnlyAsync requires at least one active hard filter and orders by rating count, average rating, then movie ID.

## Key methods

- GeminiRealQueryParser.ParseWithEvidenceAsync builds and sends the structured HTTP request, extracts the response text, and invokes the v5 validator.
- RealParsedQueryValidator.ValidateV5 checks the provider checklist; its rating reader calls RatingThresholdNormalizer.TryNormalize to convert the requested threshold.
- RealRecommendationService.RecommendCoreAsync chooses hard-only search when no semantic query exists; otherwise it checks language readiness, normalizes Serbian text, embeds locally, and calls multilingual search.
- HardFilterSqlBuilder.Build validates filter values and returns allowlisted WHERE fragments with typed parameters.
- MovieSearchRepository.SearchHybridAsync(hardFilters, vector, language) selects the trusted vector column, validates dataset readiness, executes SQL, and maps rows in ReadMoviesAsync.
- RealRecommendationEndpoint.HandleAsync turns validated results into response cards and checks IMDb IDs and TMDB poster paths.

## Source map

| File | Purpose |
|---|---|
| [Program.cs](Program.cs) | Resolves runtime mode, registers services, configures CORS/rate limiting, validates real runtime readiness, and maps the recommendation route. |
| [RecommendationEndpoint.cs](RecommendationEndpoint.cs) | Shared request validation, localized alerts, technical errors, and fake-mode endpoint. |
| [Recommendations.cs](Recommendations.cs) | Fake-mode contracts and demo parser/search implementations. |
| [Startup/RecommendationStartup.cs](Startup/RecommendationStartup.cs) | Mode resolution and dependency registration for fake, full real, and explicit Serbian POC modes. |
| [Startup/ProductionHttpConfiguration.cs](Startup/ProductionHttpConfiguration.cs) | Validates allowed origins and configures the recommendation CORS policy. |
| [RealFlow/RealRecommendationEndpoint.cs](RealFlow/RealRecommendationEndpoint.cs) | Real HTTP handler; maps validated results to movie cards and checks IMDb/TMDB URLs. |
| [RealFlow/RealRecommendationService.cs](RealFlow/RealRecommendationService.cs) | Orchestrates parser validation, language readiness, Serbian normalization, E5 query inference, and hard-only or hybrid search. |
| [RealProviders/GeminiRealQueryParser.cs](RealProviders/GeminiRealQueryParser.cs) | Builds the structured Gemini HTTP request, extracts its response, and calls the v5 validator. |
| [RealProviders/RealParsedQueryValidator.cs](RealProviders/RealParsedQueryValidator.cs) | Validates v5 checklist JSON and typed parser results, including supported filters and status consistency. |
| [RealProviders/RealQueryModels.cs](RealProviders/RealQueryModels.cs) | Defines canonical query/filter DTOs and provider error contracts. |
| [RealProviders/RatingThresholdNormalizer.cs](RealProviders/RatingThresholdNormalizer.cs) | Converts parser rating thresholds to the MovieLens scale. |
| [Database/FilteredMovieRepository.cs](Database/FilteredMovieRepository.cs) | Builds allowlisted, parameterized hard-filter SQL. Its ReadAsync helper is an unranked query; runtime ranking belongs to MovieSearchRepository. |
| [Search/MovieSearchRepository.cs](Search/MovieSearchRepository.cs) | Verifies catalog/embedding readiness and executes hard-only or multilingual pgvector search. |
| [Search/SearchLanguage.cs](Search/SearchLanguage.cs) | Maps the supported language enum to the closed English/Serbian vector column set. |
| [Search/SearchDatasetExpectation.cs](Search/SearchDatasetExpectation.cs) | Pins the expected full and Serbian POC catalog identity, corpus/artifact hashes, and embedding profile. |
| [Diagnostics/DevelopmentRequestSummary.cs](Diagnostics/DevelopmentRequestSummary.cs) | Emits sanitized request summaries and completion diagnostics in Development. |

The frontend request and response contract is defined in src/frontend/src/types.ts; its API origin validation is in src/frontend/src/apiConfiguration.ts.

For a local Windows launch, see the [normal startup launcher](../../start_script.cmd). The [Serbian POC launcher](../../start_poc.cmd) is separate from the normal full runtime.

## Isolated actor V6 runtime

An unset `CINEKROS_RUNTIME_PROFILE`, or the explicit value `v5`, keeps the current runtime selection. The separate `actor-v6-isolated` value requires real mode, Development, `CINEKROS_SERBIAN_POC` unset/false, and the exact configured and actual database `cinekros_v6_rehearsal_20261015t2154z_4fd266aa`. Use a separate process on `http://localhost:5186` with existing secrets and model directory supplied only through its process environment; do not change normal launchers or production configuration. The connection enforces read-only transactions. Startup performs no migration/import.

This profile explicitly binds the V6 prompt/schema/validator to the accepted V6 catalog, actor provenance, both language corpora/formats/vector artifacts, and unchanged E5 model profile. Startup checks the full schema (including cast helper/CHECK/GIN definitions), migration ledger, all 9,730 ordered cast assignments and exact ordinal vocabulary before caching an immutable resolver. The schema hash uses the same exact SQL serialization as the importer, duplicated deliberately at the read-only runtime boundary without a runtime importer dependency. Full readiness can be called again to detect out-of-band corruption; such writes require reverification/restart. Per-request hard-only checks catalog and actor release state; semantic requests additionally check the selected vector language, with no fallback.

Actors use canonical `all`, `any`, and `exclude` groups, conjoined with every other hard filter. Active actor filters require known nonempty recorded cast, including exclusion-only queries. Names are typed `text[]` parameters; unknown or noncanonical injected names fail validation. Actor-only requests skip E5 and selected-language preflight. Actor-plus-soft-intent requests retain eligible-first exact cosine ranking, ascending-ID ties and at most ten cards. These guarantees apply to the catalog's recorded cast, not independently verified complete credits.

Optional `CINEKROS_V6_SQL_DIAGNOSTICS=true` is accepted only in this isolated Development profile. It prints executed command text, typed filter parameters, selected language when present, and available trace correlation. Query vector values, credentials and connection strings are excluded. It defaults off. The public HTTP request/response envelope is unchanged.

Offline checks are `V6SearchTests`; the separate `V6SearchIntegrationTests` method skips by default and requires an explicitly named new `cinekros_v6_test_p4_*` clone, a matching connection and its dedicated opt-in. It refuses production, rehearsal, POCs and unrelated clones before mutation. It does not create/drop databases, regenerate vectors or call a provider.
