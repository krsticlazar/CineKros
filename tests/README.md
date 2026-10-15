# Tests

The .NET test projects are listed in `CineKros.slnx`. From the repository root:

```powershell
dotnet restore CineKros.slnx --locked-mode
dotnet test --solution CineKros.slnx --no-restore
```

The API fixture validator is separate: run `npm ci --prefix tests/fixtures` once, then `npm run check:fixtures --prefix tests/fixtures`.

Most tests use local fixtures, fakes, or synthetic data; they make no live Gemini/TMDB requests. The full suite includes integration tests that need Docker, the reviewed local catalog, and pinned local E5 models. Some PostgreSQL integration tests create and remove uniquely named disposable databases/containers. `C06_TEST_DATABASE` and `C07_TEST_DATABASE` must be unset unless deliberately pointing to their designated disposable databases. Other database, catalog, and model integration cases are opt-in through the exact project-specific variables below; do not point them at production or a persistent application database. Missing opt-in configuration is expected to skip or report inconclusive.

Key opt-ins include `C12_REAL_DATABASE` for a read-only real-data smoke, `CINEKROS_CANONICAL_ROOT` for full-source fake ETL verification, `CINEKROS_P8_FULLCATALOG_ROOT` / `CINEKROS_P8_VECTOR_ROOT` / `CINEKROS_P8_MODEL_ROOT` for read-only multilingual vector reproduction, and the `CINEKROS_SR_POC_*` variables for isolated POC integration. Live parser tests require explicit phase-specific gates and `GEMINI_API_KEY`; they can incur provider usage. See each project README for its scope and required local inputs.
