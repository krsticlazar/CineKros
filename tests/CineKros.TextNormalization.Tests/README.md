# CineKros.TextNormalization.Tests

Focused tests for Serbian Latin normalization: Cyrillic letters and digraphs, casing, NFC, whitespace, preserved Latin/non-Serbian text, null/empty inputs, and idempotence.

```powershell
dotnet test --project tests/CineKros.TextNormalization.Tests/CineKros.TextNormalization.Tests.csproj
```

Tests use in-memory values and require no model, database, or provider.
