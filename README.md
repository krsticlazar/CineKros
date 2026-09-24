# CineKros

Bachelor's thesis project by Lazar Krstić: a local hybrid movie search and recommendation system using an LLM query parser, mandatory metadata filters, and semantic retrieval.

## Current state

The repository contains the project structure and initial engineering documentation. Application code, database migrations and executable tests have not been implemented yet.

- [Architecture](docs/ARCHITECTURE.md)
- [API contract — draft](docs/API_CONTRACT.md)
- [Technical decisions](docs/DECISIONS.md)
- [Data layout](database/data/README.md)

## Intended system

React + TypeScript + Vite -> ASP.NET Core -> Gemini structured query parsing -> backend validation -> hard metadata filters plus a semantic query embedding -> PostgreSQL + pgvector -> up to ten movie cards.

Hard constraints are mandatory. Soft preferences stay in semantic text. Similarity ranks eligible movies only. The existing CineKros SVG remains the product logo.

MovieLens Tag Genome 2021 is the **confirmed and only MovieLens dataset** (Lazar, 2026-09-24). Its archive and extracted content are local and ignored; the extracted root is `database/data/raw/tag-genome-2021/`. Evaluation details still await consultation. No alternative dataset is selected.

## Repository areas

| Area | Purpose |
| --- | --- |
| `assets/` | Existing logo assets, preserved |
| `src/frontend/`, `src/backend/` | Reserved for later application implementation |
| `database/` | Future Docker, migrations, ETL, and local-only data |
| `tests/` | Future application tests |
| `Testiranje/` | Reproducible research evidence and reviewed results |
| `docs/` | Architecture, API contracts and technical decision rationale |
| `Diplomski/` | Thesis materials and the final document in Phase I |
| `scripts/` | Future explicit local workflow helpers |

Technical documentation and code are in English; UI strings will support Serbian and English.

## Development baseline

.NET 10 SDK, Node.js 24 LTS, and Docker with Compose. Runnable development commands will accompany the implementation. `.env.example` contains empty configuration placeholders; credentials and raw/derived data are excluded from version control.
