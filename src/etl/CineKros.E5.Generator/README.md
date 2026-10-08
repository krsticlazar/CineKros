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

The released paired run first generated and checkpointed 150 valid English document vectors but failed before publishing the pair. The successful resume reused those 150 English rows and generated 150 Serbian rows with one shared encoder; subsequent replay generated zero document vectors and reused all 150 rows per language. The original English document-inference duration is unavailable because resume rewrote checkpoint timing metadata; no exact 300-document duration should be inferred, and the valid English vectors were not regenerated for benchmarking. The successful resume measured 5,984 ms for the 150 newly generated Serbian document vectors.
