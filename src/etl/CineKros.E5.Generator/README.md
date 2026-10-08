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
