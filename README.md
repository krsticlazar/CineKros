# CineKros

A bilingual movie recommendation application developed as Lazar Krstić's bachelor's thesis project.

![CineKros logo](src/frontend/src/assets/CineKros_logo.svg)

Users describe their preferences in English or Serbian, including Serbian Cyrillic. Gemini interprets the request into structured criteria; the ASP.NET Core backend validates them and PostgreSQL applies mandatory metadata and actor ALL/ANY/exclude filters. Descriptive preferences are ranked with a local multilingual E5 encoder and exact pgvector cosine search. Hard-filter-only requests skip the encoder. Each response contains at most ten movies.

The current catalog contains **9,730 movies** and **19,460 English/Serbian document vectors**. Gemini does not select movies or generate SQL. English and Serbian search use separate vector columns with no language fallback or silent relaxation of hard filters.

## Source code

| Directory | Contents |
| --- | --- |
| [src/frontend/](src/frontend/) | React 19, TypeScript 6 and Vite 8 interface |
| [src/backend/CineKros.Api/](src/backend/CineKros.Api/) | .NET 10 API, Gemini parser, validation and PostgreSQL search |
| [src/embedding/](src/embedding/) | Local `intfloat/multilingual-e5-base` INT8 ONNX inference, 768 dimensions |
| [src/shared/](src/shared/) | Deterministic Serbian text normalization |
| [src/etl/](src/etl/) | Offline data preparation, translation, migrations and guarded imports |
| [src/evaluation/](src/evaluation/) | Separate evaluation CLI |
| [database/](database/) | PostgreSQL 17 / pgvector 0.8.6 service definition and SQL schema migrations |
| [tests/](tests/) | Automated tests and current API contract fixtures |
| [presentation/public_html/](presentation/public_html/) | Static defense presentation and its assets |

The root [CineKros.slnx](CineKros.slnx) contains the .NET source and test projects.

## Local prerequisites and build

- .NET SDK specified in [global.json](global.json).
- Node.js 24 for the frontend.
- A prepared PostgreSQL/pgvector database and the pinned E5 model files for real recommendations.
- A locally configured Gemini API key. Never commit credentials.

```powershell
dotnet restore CineKros.slnx --locked-mode
dotnet build CineKros.slnx --no-restore
npm ci --prefix src/frontend
npm run build --prefix src/frontend
```

## Running the application

On Windows, run [src/start_script.cmd](src/start_script.cmd). It opens separate backend and frontend consoles, validates runtime readiness, and opens the application at `http://127.0.0.1:5173/`. The default local V6 API uses `http://127.0.0.1:5186` and the prepared database `cinekros_v6_rehearsal_20261015t2154z_4fd266aa`. Stop each service with Ctrl+C or close its console. The explicit `--v5` option retains the older local compatibility mode.

Docker/PostgreSQL must already be running. The launcher safely reads local process settings or the ignored root `.env`; [.env.example](.env.example) lists configuration names. It does not restore a database, run migrations, import data or regenerate embeddings.

**The Git repository contains source code, not a populated database or model binaries.** The prepared database backup, model files and recipient setup instructions are supplied separately. Raw datasets, private academic documents, development notes and credentials are not part of the public checkout. The SQL files under `database/migrations/` describe and validate the data schema; they are not a replacement for the prepared database backup.

## Checks

```powershell
npm test --prefix src/frontend
npm run lint --prefix src/frontend
npm run check:fixtures --prefix tests/fixtures
```

Install the fixture validator's dependencies with `npm ci --prefix tests/fixtures` when needed. Database, local-model and live-provider integration tests have separate requirements and opt-in gates; they are not needed simply to inspect or build the source code.
