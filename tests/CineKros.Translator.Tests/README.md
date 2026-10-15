# CineKros.Translator.Tests

Fixture tests for CLI parsing, extraction, checkpoint identity/resume, QA/review, lock validation, cancellation, and the child-process protocol. Tests use synthetic JSON/JSONL data and do not download or invoke the OPUS model.

```powershell
dotnet test --project tests/CineKros.Translator.Tests/CineKros.Translator.Tests.csproj
```

The six-phrase local inference smoke is a separate explicit operation; it is not part of the suite. `ProtocolFixtureRunner` supplies deterministic child-process responses for malformed, incomplete, duplicate, extra-output, timeout/cancel, and exit-code cases.
