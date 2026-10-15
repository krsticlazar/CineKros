# API contract fixtures

Small Node.js validation tool for the current recommendation API request, movie, alert and technical-error envelopes. The active fixtures and their JSON schemas live in `api/`; `validate-api-fixtures.mjs` checks the active index and wire invariants without starting CineKros or contacting providers.

From the repository root:

```powershell
npm ci --prefix tests/fixtures
npm run check:fixtures --prefix tests/fixtures
```

Install dependencies only when needed; the validation command uses the existing local installation. `archive/api-v1/` and `archive/api-v2/` preserve older fixture snapshots. Archived examples are not the active API contract and are not substituted for the current `api/index.json` set.
