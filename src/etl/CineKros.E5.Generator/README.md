# CineKros.E5.Generator

Offline CLI that validates a catalog and generates document vectors with a local ONNX model. It reads catalog, manifest, model, checkpoint, and output paths; it does not call Gemini or write to PostgreSQL. Checkpoints allow compatible work to resume; only complete artifact/manifest pairs are published.

## Profiles and modes

The default profile is the legacy English `e5-base-v2-int8-onnx-v1` with batch size 16. Do not assume omission of `--profile` selects the newer multilingual model. Multilingual generation must explicitly select `--profile multilingual-e5-base-int8-onnx-v1 --batch-size 1`, `--language en|sr`, and both `--dictionary` and `--source-catalog`.

The fixed 150-film Serbian POC uses `--paired-poc true` (without `--language`) and publishes immutable outputs. Full-catalog multilingual generation is a separately gated path: single-language runs add `--full-catalog true`; paired EN/SR runs use `--paired-full true` without `--language`. Both use the strict full-catalog manifest and immutable output locations. Token audit and optional POC-v2 reuse are available only with paired-full mode. Legacy English artifacts are not a reuse source for full-catalog generation.

```powershell
dotnet run --project src/etl/CineKros.E5.Generator -- `
  --catalog <catalog.jsonl> --manifest <manifest.json> --model-dir <model-dir> `
  --output-dir <new-output-dir> --checkpoint <checkpoint.jsonl>
```

Add the profile-specific options above for multilingual runs. Choose a new output directory for a publication; existing output is not overwritten. Verify local model/profile identity and input manifests before a costly full-catalog run.
