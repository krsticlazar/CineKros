# CineKros.VectorImporter

## Role

This CLI validates and imports a complete E5 document-vector artifact into PostgreSQL `movie_embeddings`. It does not calculate embeddings or accept a partial artifact as complete.

## Pipeline position

`[E5.Generator](../CineKros.E5.Generator/README.md) → artifact + catalog → VectorImporter → movie_embeddings → API search`

Run [Database.Migrator](../CineKros.Database.Migrator/README.md) before importing a catalog; catalogs are imported by [Catalog.Importer](../CineKros.Catalog.Importer/README.md). The API compares profiles and searches vectors; it does not generate them.

## Code map

- [Program.cs](Program.cs) accepts `--catalog` and `--artifact`, loads the artifact through its validator, and uses `DATABASE_CONNECTION_STRING`.
- [VectorArtifact.cs](VectorArtifact.cs) validates the manifest, row count, dimensions, and vector contents.
- [VectorImporter.cs](VectorImporter.cs) verifies that artifact identities match the imported catalog, writes transactionally, and records the profile. An identical rerun verifies the existing import.

These checks preserve the catalog-to-embedding-profile relationship expected by runtime search.

## Paired EN/SR POC import

An explicit `--paired-poc` mode handles the approved frozen POC inputs. It validates the catalog, manifest, source catalog, translation dictionary, and both EN/SR vector artifacts before writing. It then imports both languages and readiness metadata in one transaction. Re-importing identical data verifies the stored content without changing it; populated or partial non-identical state is rejected.

Example from the repository root using the approved frozen POC inputs:

```powershell
$env:DATABASE_CONNECTION_STRING = '<local PostgreSQL connection string>'
$pocRoot = 'database/data/derived/serbian-search/poc-v1'
dotnet run --project src/etl/CineKros.VectorImporter -- --paired-poc `
  --catalog "$pocRoot/catalog/movies-catalog.jsonl" `
  --manifest "$pocRoot/catalog/manifest.json" `
  --dictionary "$pocRoot/translation/tag-translations-sr.json" `
  --source-catalog database/data/derived/final/b05a-real-tmdb-01/movies-catalog.jsonl `
  --en "$pocRoot/embeddings/en" `
  --sr "$pocRoot/embeddings/sr"
Remove-Item Env:DATABASE_CONNECTION_STRING
Remove-Variable pocRoot
```

Set the connection string only in the process/terminal that runs the command. Do not put credentials in the repository, command arguments, logs, or shared reports. Verify the target before running it. This mode accepts only the exact approved database `cinekros_sr_poc_phase04_20261008` or the disposable `cinekros_sr_poc_phase04_test_*` family, and also verifies actual `current_database()`, PG17, pgvector, and the applied migration. Never point it at production database `cinekros`; it does not delete or regenerate vectors.

The existing `--catalog/--artifact` mode remains unchanged. `--paired-poc` is a separate, explicit POC path; it does not activate the API or change legacy runtime behavior.
