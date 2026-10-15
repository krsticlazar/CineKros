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

## Immutable V6 actor catalog

`--paired-v6 true` is distinct, with no `--language`. It requires the multilingual profile, batch size 1, dictionary and source catalog; old paired/full/POC and reuse flags are incompatible. `ActorCatalogV6Reader` verifies approved bytes, sidecar, source/dictionary conservation, ordered IDs, text corpora and actor mapping/vocabulary/override identities. Old vectors never seed V6. Execution requires a separately released execution packet.

From the canonical repository directory:

```powershell
dotnet run --no-build --project src/etl/CineKros.E5.Generator -- `
  --paired-v6 true --profile multilingual-e5-base-int8-onnx-v1 --batch-size 1 `
  --catalog D:/Repozitorijum/CineKros/database/data/derived/final/actor-search-v6/v6-catalog-01/catalog/movies-catalog.jsonl `
  --manifest D:/Repozitorijum/CineKros/database/data/derived/final/actor-search-v6/v6-catalog-01/catalog/manifest.json `
  --dictionary D:/Repozitorijum/CineKros/database/data/derived/translations/sr-latn-v1-r1/tag-translations-sr.json `
  --source-catalog D:/Repozitorijum/CineKros/database/data/derived/final/b05a-real-tmdb-01/movies-catalog.jsonl `
  --model-dir D:/Repozitorijum/CineKros/database/data/models/multilingual-e5-base/d128750597153bb5987e10b1c3493a34e5a4502a `
  --output-dir D:/Repozitorijum/CineKros/database/data/embeddings/multilingual-e5-base-int8-onnx-v1/v6-catalog-01 `
  --checkpoint D:/Repozitorijum/CineKros/database/data/embeddings/multilingual-e5-base-int8-onnx-v1/v6-catalog-01
```

Add `--token-audit-only true` to audit all 19,460 NFC passage-prefixed strings with the pinned tokenizer and no ONNX. Inputs over 512 prevent inference. Audit/resource reports go under `.local/planning/reports/v6-phase-02/`.

EN then SR share one encoder. Every successful row appends to `{en,sr}/checkpoint.jsonl` and flushes to disk; Ctrl+C cancels at safe boundaries. Exact V6 headers alone resume; foreign headers fail unchanged. Compatible corrupt/torn rows preserve a checkpoint backup before valid successes resume. Progress prints every 100 generated rows or 30 seconds. Each language publishes atomically at `{en,sr}/published/`, followed by deterministic `paired-release.json`. Differing descriptors/publications are refused.

Distinct V6 checkpoint/artifact formats add `actorContext` binding catalog schema, dictionary, actor vocabulary, per-film cast mapping, overrides and rules hashes. The pair binds both corpus/text versions and both vector/manifest hashes. Manifest elapsed times remain factual. Completed replay validates both artifacts and descriptor without tokenizer/model loading or inference, preserving published hashes. Reports separate input/audit/load time, language wall time, publication generation time, model/inference counters and loaded/peak working set.

Offline tests use deterministic small fixtures. `ActorVectorSampleV6Tests` is disabled unless all `CINEKROS_V6_REPOSITORY_ROOT`, `CINEKROS_V6_VECTOR_ROOT`, `CINEKROS_V6_MODEL_ROOT` absolute paths are explicitly set. It records IDs `1,192,296,3182,3396,4963,26414,85179` before inspecting output, performs eight batch-one calls per language on one encoder (16 total), records exact differences and requires bitwise reproduction. It never republishes vectors.
