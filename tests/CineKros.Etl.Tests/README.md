# CineKros.Etl.Tests

Checks metadata/semantic export, enrichment, catalog merging, and fake/legacy embedding boundaries using small fixtures and fake HTTP responses. Routine tests do not call TMDB or Gemini.

```powershell
dotnet test --project tests/CineKros.Etl.Tests/CineKros.Etl.Tests.csproj
```

Setting `CINEKROS_CANONICAL_ROOT` opts into full-source fake verification against local canonical data. It reads the supplied source and writes test outputs; it does not make live provider calls or write to a database.
