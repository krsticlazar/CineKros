# Tests

The .NET test projects are included in `CineKros.slnx`. Run them after locked restore and build:

```powershell
dotnet restore CineKros.slnx --locked-mode
dotnet test CineKros.slnx --no-restore
```

The active HTTP contract examples and isolated validator are in `fixtures/`. From the repository root, run `npm ci --prefix tests/fixtures` once, then `npm run check:fixtures --prefix tests/fixtures`.

Routine checks use fixtures, fakes or small synthetic data and do not require live Gemini requests. Live provider checks and academic evaluation are separate activities; no evaluation results are claimed here.

The full .NET solution suite also requires the reviewed local catalog, pinned local E5 model, and a running Docker engine. Catalog, vector-import and API integration tests create and remove uniquely named disposable PostgreSQL containers; they do not reset the persistent application database.

Leave `C06_TEST_DATABASE` and `C07_TEST_DATABASE` unset unless deliberately targeting their designated disposable test databases. The separate read-only real-data smoke is opt-in through `C12_REAL_DATABASE`; full-source fake ETL verification is opt-in through `CINEKROS_CANONICAL_ROOT`. Missing opt-in variables produce expected skipped/inconclusive tests.
