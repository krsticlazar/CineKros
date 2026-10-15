# CineKros.VectorImporter.Tests

Tests artifact validation and transactional catalog/vector import invariants. Run:

```powershell
dotnet test --project tests/CineKros.VectorImporter.Tests/CineKros.VectorImporter.Tests.csproj
```

Most checks use fixtures. The Serbian POC integration path is opt-in through `CINEKROS_SR_POC_TEST_ADMIN_CONNECTION`, `CINEKROS_SR_POC_ROOT`, `CINEKROS_SR_POC_INPUT_ROOT`, and `CINEKROS_SR_POC_SOURCE_CATALOG`; it creates and removes isolated disposable PostgreSQL test databases. Supply only the designated disposable test admin connection, never a persistent or production database. Full-artifact validation tests are read-only.
