# CineKros.Api.Tests

Tests API contracts, parser adapters, hard-filter/search behavior, and recommendation flows. Ordinary tests use fake providers and local fixtures; they do not require Gemini. Run from the repository root:

```powershell
dotnet test --project tests/CineKros.Api.Tests/CineKros.Api.Tests.csproj
```

The suite includes disposable PostgreSQL integration tests that create and remove uniquely named test databases/containers. Leave `C06_TEST_DATABASE` and `C07_TEST_DATABASE` unset unless targeting their designated disposable databases. Real-data, Serbian POC, clone-readiness, production-read-only, and live parser tests are separate opt-ins gated by `C12_REAL_DATABASE`, the appropriate `CINEKROS_SR_POC_*` / `CINEKROS_SR_P10_*` values, or explicit `CINEKROS_SR_P5_*` flags. Some gates need a local E5 model and Gemini key; consult the matching source test before setting them. Never use a persistent application or production database for disposable DB tests.
