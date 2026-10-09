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

## Full bilingual Phase 9 rehearsal import

`--paired-full-cutover` is a separate, fail-closed path for the exact reviewed Phase 8 full catalog and paired 9,730-row artifacts. It requires explicit absolute input paths, an explicit target database name, MAIN's local baseline descriptor, the pinned legacy catalog/vector artifact, and the canonical migrations directory. Before opening a database connection it validates all three artifact sets and the cross-language/catalog identities. It only permits an explicitly matching `cinekros_sr_p9_rehearsal_20261009` or `cinekros_sr_p9_test_*` destination; this worker CLI always rejects production `cinekros`.

The importer verifies the connected database and PostgreSQL/pgvector/system identity, locks migration/import work, compares MAIN's current legacy DB digests and every old movie/vector/fingerprint row, then applies canonical migration 002, its ledger entry, both vector replacements and readiness metadata in one transaction. It verifies all 9,730 rows and state before commit. A complete identical run is verified as a no-op; unknown, partial or different state is rejected. It never modifies movie metadata. There is no command-line production override; MAIN alone owns the production cutover.

Use only after MAIN has verified the backup/restore rehearsal and explicitly released the isolated target. Keep credentials in the process environment only. Every file/directory argument must be an absolute path; obtain the baseline descriptor path directly from MAIN rather than copying or publishing its local-only location. The usage shape is:

```powershell
$repoRoot = 'D:\Repozitorijum\CineKros'
$baselinePath = '<absolute baseline descriptor path supplied by MAIN>'
dotnet run --project src/etl/CineKros.VectorImporter -- --paired-full-cutover `
  --expected-database cinekros_sr_p9_rehearsal_20261009 `
  --baseline $baselinePath `
  --catalog (Join-Path $repoRoot 'database/data/derived/final/sr-search-v1/sr-p8-full-01/catalog/movies-catalog.jsonl') `
  --manifest (Join-Path $repoRoot 'database/data/derived/final/sr-search-v1/sr-p8-full-01/catalog/manifest.json') `
  --dictionary (Join-Path $repoRoot 'database/data/derived/translations/sr-latn-v1-r1/tag-translations-sr.json') `
  --source-catalog (Join-Path $repoRoot 'database/data/derived/final/b05a-real-tmdb-01/movies-catalog.jsonl') `
  --en (Join-Path $repoRoot 'database/data/embeddings/multilingual-e5-base-int8-onnx-v1/sr-p8-full-01/en/published') `
  --sr (Join-Path $repoRoot 'database/data/embeddings/multilingual-e5-base-int8-onnx-v1/sr-p8-full-01/sr/published') `
  --legacy-catalog (Join-Path $repoRoot 'database/data/derived/final/b05a-real-tmdb-01/movies-catalog.jsonl') `
  --legacy-artifact (Join-Path $repoRoot 'database/data/embeddings/e5-base-v2-int8-onnx-v1/e5-b05a-real-20260927-01/published') `
  --migrations (Join-Path $repoRoot 'database/migrations')
```

All paths must resolve to the pinned canonical inputs. The example is documentation only; it does not imply rehearsal authorization. Do not run until MAIN explicitly releases the exact restored target.

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
