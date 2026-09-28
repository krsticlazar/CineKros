# CineKros

AI-powered movie recommendation system combining natural-language query parsing, SQL hard filters, and E5 + pgvector semantic search.

![CineKros logo](src/frontend/src/assets/CineKros_logo.svg)

CineKros is a bachelor's thesis project by Lazar Krstić: a local hybrid movie search application. The React, TypeScript and Vite frontend sends a query to an ASP.NET Core API. Gemini `gemini-3.1-flash-lite` is used solely to parse queries into structured search intent; it does not select movies. PostgreSQL applies mandatory SQL hard filters, then exact pgvector cosine similarity ranks only eligible results.

The catalog contains 9,730 movies. Its semantic source combines MovieLens Tag Genome 2021 with approved MovieLens 32M enrichment and offline TMDB metadata. Document and runtime query embeddings use the local E5-base-v2 INT8 ONNX model with 768 dimensions. Film data, model files and generated vectors are local-only; they are not included in this repository.

With separately obtained source data and an offline TMDB cache, the tools in `src/etl/` prepare and validate catalog/vector artifacts for database import; `scripts/bootstrap-e5-model.ps1` obtains or verifies the pinned local model. The repository does not include the source datasets or generated artifacts.

## Repository

| Path | Purpose |
| --- | --- |
| `src/frontend/` | React/TypeScript/Vite application |
| `src/backend/CineKros.Api/` | ASP.NET Core API and search runtime |
| `src/embedding/` | Local E5 embedding runtime |
| `src/etl/` | Catalog, database and vector tooling |
| `database/docker/` and `database/migrations/` | PostgreSQL/pgvector Compose service and SQL migrations |
| `tests/` | .NET tests and API fixture validation |
| `scripts/` | Explicit local model bootstrap |

## Local development

Prerequisites are .NET 10, Node.js 24, Docker with Compose, and PowerShell. Restore and check the .NET solution with:

```powershell
dotnet restore CineKros.slnx --locked-mode
dotnet build CineKros.slnx --no-restore
dotnet test CineKros.slnx --no-restore
```

Install frontend dependencies with `npm ci --prefix src/frontend`, then use `npm run dev`, `npm run lint`, `npm test`, or `npm run build` from `src/frontend`. Validate API examples with `npm ci --prefix tests/fixtures` and `npm run check:fixtures --prefix tests/fixtures` from the repository root.

To explicitly download or verify the pinned local E5 artifacts, run `pwsh -File scripts/bootstrap-e5-model.ps1` (or add `-VerifyOnly`). The normal Windows launcher is `src/start_script.cmd`; it reads required values from the process environment or ignored root `.env`, starts the PostgreSQL Compose service, then starts the API and frontend. It does not run migrations or import data. Generated data and model artifacts remain local.
