# CineKros.Embedding.Tests

Checks the E5 runtime's pinned artifacts, tokenization, batching, pooling, normalized vectors, and profile fingerprints. The local acceptance tests exercise both the current multilingual batch-size-one profile and the separate legacy English default. They use local ONNX models, not providers or databases.

```powershell
dotnet test --project tests/CineKros.Embedding.Tests/CineKros.Embedding.Tests.csproj
```

Models are read from the repository's local model paths by default. Override with `CINEKROS_E5_MODEL_ROOT` and `CINEKROS_MULTILINGUAL_E5_MODEL_ROOT` when needed. `CINEKROS_PHASE1_FINAL_REPORT` optionally selects a local evidence report output path.
