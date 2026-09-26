# CineKros

Bachelor's thesis project by Lazar Krstić: a local hybrid movie search and recommendation system using an LLM query parser, mandatory metadata filters, and semantic retrieval.

## Current state

The repository contains the frozen public API contract, a pinned toolchain, source-data review, synthetic API fixtures and a fake-backed ASP.NET Core HTTP foundation. The recommendation engine, frontend, database and migrations are not implemented yet. Until the real service is wired, a valid API request receives `SEARCH_UNAVAILABLE`.

- [Architecture](docs/ARCHITECTURE.md)
- [Public API contract v1.0.0](docs/API_CONTRACT.md)
- [Project layout and toolchain](docs/TOOLCHAIN.md)
- [Technical decisions](docs/DECISIONS.md)
- [Data layout](database/data/README.md)
- [Tag Genome source review](Testiranje/reports/tag-genome-source-review.md)
- [Metadata export mapping](docs/DATA_MAPPING.md)

## Intended system

React + TypeScript + Vite -> ASP.NET Core -> Gemini structured query parsing -> backend validation -> hard metadata filters plus a semantic query embedding -> PostgreSQL + pgvector -> up to ten movie cards.

Hard constraints are mandatory. Soft preferences stay in semantic text. Similarity ranks eligible movies only. The existing CineKros SVG remains the product logo.

MovieLens Tag Genome 2021 is the **confirmed and only MovieLens dataset** (Lazar, 2026-09-24). Its archive and extracted content are local and ignored; the extracted root is `database/data/raw/tag-genome-2021/`. Evaluation details still await consultation. No alternative dataset is selected.

## Repository areas

| Area | Purpose |
| --- | --- |
| `assets/` | Existing logo assets, preserved |
| `src/frontend/`, `src/backend/` | Frontend placeholder and fake-backed backend foundation |
| `database/` | Planned Docker/migrations/ETL and local-only data |
| `tests/` | Backend HTTP contract tests |
| `Testiranje/` | Reproducible research evidence and reviewed results |
| `docs/` | Architecture, API contracts and technical decision rationale |
| `Diplomski/` | Thesis materials and the final document in Phase I |
| `scripts/` | Public-contract fixture validator and later local workflow helpers |

Technical documentation and code are in English; UI strings will support Serbian and English.

## Development baseline

.NET 10 SDK, Node.js 24 LTS, and Docker with Compose. Run `npm ci` then `npm run check:fixtures` to validate the synthetic contract examples. Run `dotnet restore CineKros.slnx --locked-mode`, `dotnet build CineKros.slnx --no-restore` and `dotnet test CineKros.slnx --no-restore` for the fake-backed backend. `.env.example` contains empty configuration placeholders; credentials and raw/derived data are excluded from version control.
