# CineKros.E5.Generator

## Role

Offline CLI calculates E5 passage vectors for catalog `semanticText` using a local ONNX model. It checkpoints only compatible results and publishes only complete output. It does not call Gemini.

## Pipeline position

`[CineKros.Etl](../CineKros.Etl/README.md) → katalog → E5.Generator → artefakt → [VectorImporter](../CineKros.VectorImporter/README.md) → PostgreSQL`

[CineKros.Embedding](../../embedding/CineKros.Embedding/README.md) izvršava zajednički E5 profil; [Catalog.Importer](../CineKros.Catalog.Importer/README.md) obezbeđuje validaciju istog kataloga.

## Usage and contracts

- [Program.cs](Program.cs) povezuje parsiranje opcija, `E5EmbeddingModel` i generator.
- `GeneratorArguments.Parse` in [GeneratorArguments.cs](GeneratorArguments.cs) accepts catalog, manifest, local model, checkpoint, output, optional batch size, and optional profile ID.
- [DocumentVectorGenerator.cs](DocumentVectorGenerator.cs) validates the catalog, resumes compatible checkpoints, and publishes a complete JSONL/manifest pair.
- Omitting `--profile` preserves the legacy profile and default batching. The explicit multilingual profile requires `--profile multilingual-e5-base-int8-onnx-v1 --batch-size 1`; its checkpoint and manifest carry the locked `single-sequence-unpadded-v1` shape policy.
- `E5DocumentVectorSource` prilagođava `E5EmbeddingModel` interfejsu generatora.

Checkpoints avoid repeated work after interruption; manifests bind outputs to the catalog and immutable E5 profile. This package does not activate an artifact or modify database state.

## Multilingual Serbian-search POC

For a single-language POC run, select `--profile multilingual-e5-base-int8-onnx-v1`, `--batch-size 1`, and exactly one `--language en` or `--language sr`. Both `--dictionary <path>` and `--source-catalog <path>` are required; the strict POC reader verifies them against the locked catalog release. The selected language determines which semantic-text corpus is embedded.

The explicitly gated paired harness uses `--paired-poc true` instead of `--language`. It creates one multilingual encoder and runs the English and Serbian pipelines sequentially through it, with independent per-language checkpoints and outputs. Its multilingual-specific options are:

```text
--profile multilingual-e5-base-int8-onnx-v1 --batch-size 1 --dictionary <path> --source-catalog <path> --paired-poc true
```

POC outputs are immutable: choose a new output directory for each run; existing published outputs are never overwritten. On interruption, a compatible language-specific checkpoint can be resumed, while corrupt or incompatible checkpoint bytes are preserved to a unique backup before repair. This POC is limited to its fixed 150-film corpus and does not regenerate the legacy 9,730-film vectors or change that catalog.

## Full bilingual catalog generation

Full-catalog generation is separately gated from the historical POC. A single-language run adds `--full-catalog true` alongside `--language en|sr`; the paired release uses `--paired-full true` (without `--language`) and one shared encoder with independent EN/SR checkpoints and immutable publications. Both require the pinned multilingual profile, batch size 1, the strict full-catalog manifest, source catalog, and dictionary. Paired output uses `<root>/<language>/checkpoint.jsonl` and `<root>/<language>/published/`.

Before inference, the paired path hashes the locked tokenizer and records uncapped token counts for every formatted/NFC catalog text. `--token-audit-only true` runs that same checked tokenizer audit and exits before constructing an ONNX session; any input beyond the 512-token contract is recorded and blocks generation for review.

The optional `--poc-v2-reuse-root <path>` is accepted only with the paired full-catalog gate. It strictly loads the corrected POC-v2 release and its approved mapping, validates both source vector artifacts, then reuses an individual row only when ID, profile proof, semantic text, document fingerprint, and UTF-8 text hash all match exactly. All other full-catalog rows go through the existing encoder; legacy English vectors are not a reuse source.

The read-only 16-vector reproduction test is disabled unless all of `CINEKROS_P8_FULLCATALOG_ROOT`, `CINEKROS_P8_VECTOR_ROOT`, and `CINEKROS_P8_MODEL_ROOT` are set to absolute paths. It validates both complete artifacts, then uses one fresh encoder for fixed EN/SR samples and records full-precision vector comparisons before the bitwise acceptance assertion. It does not write to either publication.

The released paired run first generated and checkpointed 150 valid English document vectors but failed before publishing the pair. The successful resume reused those 150 English rows and generated 150 Serbian rows with one shared encoder; subsequent replay generated zero document vectors and reused all 150 rows per language. The original English document-inference duration is unavailable because resume rewrote checkpoint timing metadata; no exact 300-document duration should be inferred, and the valid English vectors were not regenerated for benchmarking. The successful resume measured 5,984 ms for the 150 newly generated Serbian document vectors.
