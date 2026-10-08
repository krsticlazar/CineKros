# Translator tests

Run with the repository's MSTest / Microsoft.Testing.Platform convention:

```powershell
dotnet test --project tests/CineKros.Translator.Tests/CineKros.Translator.Tests.csproj
```

These tests use synthetic JSON/JSONL fixtures. They do not download or invoke the OPUS model. The six-phrase inference smoke is an explicit local operation outside the test suite.
