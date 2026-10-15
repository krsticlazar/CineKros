# CineKros.E5.Generator.Tests

Tests catalog validation, profile selection, deterministic checkpoints/resume, cancellation, and complete artifact publication. Most tests use fakes and temporary fixtures; they do not download a model or call providers. Run:

```powershell
dotnet test --project tests/CineKros.E5.Generator.Tests/CineKros.E5.Generator.Tests.csproj
```

`CINEKROS_POC_ROOT` opts into checks against the local corrected POC release. Read-only full-catalog reproduction requires all three absolute paths: `CINEKROS_P8_FULLCATALOG_ROOT`, `CINEKROS_P8_VECTOR_ROOT`, and `CINEKROS_P8_MODEL_ROOT`. Those tests compare fixed EN/SR samples and do not modify published vectors.
