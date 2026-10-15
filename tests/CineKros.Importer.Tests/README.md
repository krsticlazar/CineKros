# CineKros.Importer.Tests

Tests catalog validation and transactional import behavior, including rejection of incomplete or mismatched artifacts and safe identical re-import. Run:

```powershell
dotnet test --project tests/CineKros.Importer.Tests/CineKros.Importer.Tests.csproj
```

The suite uses isolated disposable PostgreSQL integration infrastructure; it must not target the persistent application or production database. `CINEKROS_POC_ROOT` opts into local Serbian POC/full-catalog artifact validation and does not import those artifacts.
