# CineKros

AI-powered movie recommendation system combining natural-language query parsing, SQL hard filters, and E5 + pgvector semantic search.

![CineKros logo](src/frontend/src/assets/CineKros_logo.svg)

CineKros is a bachelor's thesis project by Lazar Krstić: a bilingual movie search application. Users write English or Serbian requests, including Serbian Cyrillic. The React/TypeScript/Vite frontend sends the selected language and message to an ASP.NET Core API on .NET 10. Gemini `gemini-3.1-flash-lite` parses the request through the V6 checklist contract in the normal local runtime; the backend validates and normalizes its output. Explicit V5 compatibility remains available.

The catalog contains 9,730 movies, with MovieLens Tag Genome 2021 semantic tags, MovieLens 32M rating/genre metadata and offline TMDB enrichment. Both languages use the pinned local `intfloat/multilingual-e5-base` INT8 ONNX encoder, with 768 dimensions and batch size one. English and Serbian document representations have separate vector columns (`embedding` and `embedding_sr`), totaling 19,460 document vectors.

PostgreSQL applies mandatory year, runtime, genre, rating and original-language filters. V6 also supports structured actor ALL/ANY/exclude filters. Semantic requests use a local E5 query vector and exact pgvector cosine ranking among eligible movies, with deterministic ID tie-breaking and at most ten results. Hard-filter-only requests skip query embedding and use deterministic metadata ordering. Directors, moods and themes remain semantic preferences; unsupported semantic exclusions are not guaranteed. Gemini interprets requests; it does not select movies or generate embeddings.

Raw data, translation dictionaries, generated catalogs/vectors, model binaries and database storage are local artifacts. The ETL, translator, generator, migrations and import tools below support their preparation and validation; this checkout alone does not contain a populated database.

## Repository

| Path | Purpose |
| --- | --- |
| [Frontend](src/frontend/README.md) | React/TypeScript/Vite application |
| [API](src/backend/CineKros.Api/README.md) | Request validation, Gemini V6/V5 parsing, SQL/pgvector search and response mapping |
| [Embedding](src/embedding/CineKros.Embedding/README.md) | Shared local multilingual E5 runtime |
| [Text normalization](src/shared/CineKros.TextNormalization/README.md) | Deterministic Serbian Cyrillic-to-Latin normalization |
| [ETL](src/etl/CineKros.Etl/README.md), [translator](src/etl/CineKros.Translator/README.md), [generator](src/etl/CineKros.E5.Generator/README.md) | Offline catalog, tag dictionary and document vectors |
| [Migrator](src/etl/CineKros.Database.Migrator/README.md), [catalog importer](src/etl/CineKros.Catalog.Importer/README.md), [vector importer](src/etl/CineKros.VectorImporter/README.md) | Explicit database preparation and guarded imports |
| [Evaluation](src/evaluation/CineKros.Evaluation/README.md) | Offline evaluation CLI, separate from web requests |
| `database/docker/` and `database/migrations/` | PostgreSQL/pgvector Compose service and SQL migrations |
| [Tests](tests/README.md) | MSTest projects, gated integration checks and API fixtures |
| [Scripts](scripts/README.md) | Explicit local model bootstrap |

[Solution study guide](slnx_README.md) lists project dependencies and a suggested reading order. Local consultation notes and manual testing material live under `docs/`, including `docs/testing/`; local notes and thesis materials are excluded from the public repository.

## Local development

Use the SDK pinned in [global.json](global.json), Node.js 24, Docker with Compose, and PowerShell. From the repository root:

```powershell
dotnet restore CineKros.slnx --locked-mode
dotnet build CineKros.slnx --no-restore
npm ci --prefix src/frontend
```

The Windows launcher is `src/start_script.cmd`: its existing loader reads process configuration or the ignored root `.env`, checks the running Docker/PostgreSQL services, and opens separate backend and frontend CMD consoles. Default local V6 uses `actor-v6-isolated`, API `http://127.0.0.1:5186`, frontend `http://127.0.0.1:5173`, and the prepared 9,730-film database `cinekros_v6_rehearsal_20261015t2154z_4fd266aa`. `--v5` explicitly selects the previous `cinekros` database and API port 5179. Start Docker and the existing database container beforehand; the launcher does not create services, migrate, import or generate data. This local default does not perform production cutover.

Configure `CINEKROS_POSTGRES_PASSWORD` and `GEMINI_API_KEY` locally; [.env.example](.env.example) documents empty placeholders. The launcher derives the local database connection and model directory. Direct backend startup needs `CINEKROS_RECOMMENDATION_MODE=real`, `DATABASE_CONNECTION_STRING`, `GEMINI_API_KEY` and `CINEKROS_E5_MODEL_DIR` in its process environment. Direct Development startup without a mode uses fake providers. See the API README for configuration and CORS details. Never commit secrets.

`src/start_poc.cmd` is a separate launcher for the corrected 150-film database `cinekros_sr_poc_phase06t_v2_20261009`. Use the normal launcher for the complete application; the POC keeps its own catalog/vector readiness guards.

Explicitly verify or download the current model with:

```powershell
pwsh -File scripts/bootstrap-e5-model.ps1 -Profile multilingual-e5-base-int8-onnx-v1 -VerifyOnly
```

Remove `-VerifyOnly` only when deliberately downloading the pinned artifacts. The script's unqualified default still selects the historical English model, so use the explicit multilingual profile.

## Checks

Run frontend `npm test`, `npm run lint` and `npm run build` in `src/frontend`. API examples use `npm ci --prefix tests/fixtures` followed by `npm run check:fixtures --prefix tests/fixtures`. See the tests README before broader .NET runs: some checks require local artifacts or disposable databases, and live parser tests are explicitly gated. `Directory.Build.props` directs .NET test output to ignored `.local/TestResults/`.
