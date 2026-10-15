# CineKros.Etl

Offline tools prepare versioned catalog inputs for import and embedding. They read local MovieLens/TagDL files and approved enrichment/cache artifacts; `enrich-v2` is the only active command that makes TMDB requests and requires `TMDB_READ_ACCESS_TOKEN`. Other commands are file-only. Outputs are JSONL and manifests in caller-selected directories.

The pipeline exports metadata, adds selected TagDL signals to `semanticText`, optionally enriches records, then merges compatible inputs into a final catalog. `CineKros.Catalog.Importer` and `CineKros.E5.Generator` consume that catalog. From the repository root, the main file workflows are:

```powershell
dotnet run --project src/etl/CineKros.Etl -- --source-root <absolute-source-dir> --output-dir <absolute-output-dir>
dotnet run --project src/etl/CineKros.Etl -- semantic-export --input-jsonl <absolute-jsonl> --input-manifest <absolute-manifest> --tagdl-csv <absolute-csv> --output-dir <absolute-output-dir>
dotnet run --project src/etl/CineKros.Etl -- catalog-merge --semantic-dir <absolute-dir> --enrichment-dir <absolute-dir> --output-dir <absolute-output-dir>
```

`enrich-v2` additionally requires `--input-jsonl`, `--input-manifest`, `--ml32m-root`, `--cache-dir`, and `--output-dir`, plus `TMDB_READ_ACCESS_TOKEN`; it can make live TMDB requests. The old `real-embed` command is disabled; `fake-embed` creates deterministic test vectors only. Historical embedding code remains for reproducibility and is not the current generator.

The `sr-poc-select`, `sr-poc-build`, and `sr-poc-context` commands are bounded to the frozen 150-movie/418-tag POC. They require absolute input paths and new output locations. They do not call providers or databases. Their outputs are research artifacts, not a production catalog or dictionary:

```text
sr-poc-select --catalog <absolute-source-catalog.jsonl> --output-dir <new-absolute-dir>
sr-poc-build --catalog <absolute-source-catalog.jsonl> --dictionary <absolute-dictionary.json> --output-dir <new-absolute-dir>
sr-poc-context --selected-source <absolute-selected.jsonl> --qa-report <absolute-qa.json> --output <new-absolute-context.json>
```

Run tests with `dotnet test --project tests/CineKros.Etl.Tests/CineKros.Etl.Tests.csproj` from the repository root. Full-source fake verification is separately opt-in through `CINEKROS_CANONICAL_ROOT`; it does not make live TMDB calls.
