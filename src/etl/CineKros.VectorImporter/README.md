# CineKros.VectorImporter

Validates vector artifacts against their catalog/profile and imports them transactionally into PostgreSQL. It reads `DATABASE_CONNECTION_STRING` from the process environment. It never calculates vectors; it rejects incomplete or incompatible artifacts.

## Modes and database boundaries

Read-only V6 artifacts: `ActorVectorArtifactV6Validator.LoadAsync(absolutePublishedDirectory, actorCatalog, "en"|"sr")` validates exact V6 actor/text/model identities, ordered complete coverage and finite normalized 768D vectors. `ValidatePairAsync(absoluteRoot, actorCatalog)` independently validates both publications and deterministic `paired-release.json` hashes. Obtain the catalog through `ActorCatalogV6Reader.LoadAsync`. These methods never connect to a database or run inference.

- Standard: `--catalog <reviewed-catalog.jsonl> --artifact <complete-artifact-dir>`. Use only after schema migration and catalog import.
- POC: explicit `--paired-poc --catalog ... --manifest ... --dictionary ... --source-catalog ... --en ... --sr ...`. This imports paired vectors and readiness metadata only into the exact approved POC database `cinekros_sr_poc_phase04_20261008` or its `cinekros_sr_poc_phase04_test_*` disposable test family. It verifies the actual connected database and required PG/pgvector/migration state.
- Full paired rehearsal: explicit `--paired-full-cutover` requires MAIN's baseline descriptor, exact reviewed catalog and EN/SR artifacts, legacy artifacts, migrations, and explicit target database. It accepts only `cinekros_sr_p9_rehearsal_20261009` or `cinekros_sr_p9_test_*`; this worker CLI always rejects production `cinekros`. It may apply migration 002 and replace vectors inside one transaction. Use only after MAIN releases the exact restored rehearsal target; there is no production override.

Import modes write database state. Keep credentials in the process environment, verify the target before running, and do not use production credentials for POC/rehearsal commands. Full paired imports validate inputs and existing database identity before writing; identical completed runs are verified as no-ops. These constraints do not authorize a run.

## Isolated atomic actor V6 import

The explicit `--paired-actor-v6` mode upgrades the exact accepted bilingual 001+002 release into the approved full V6 catalog/vector pair. Both the connection-string database and actual connected database must equal the explicit expected name and match `cinekros_v6_(rehearsal|test)_[a-z0-9_]+` as the complete name. Production `cinekros`, restore-only targets, POC targets, suffixes/newlines and mismatched names are rejected. Neither the CLI nor library offers a production override.

First capture a fresh descriptor using the separate read-only inspector on source `cinekros`. Set `DATABASE_CONNECTION_STRING` in the process environment; do not put it in arguments or saved command text.

```powershell
dotnet src/etl/CineKros.VectorImporter/bin/Debug/net10.0/CineKros.VectorImporter.dll `
  --capture-actor-v6-baseline `
  --output '<absent absolute baseline JSON path>' `
  --migrations '<absolute canonical migrations directory>' `
  --backup-sha256 '<independently verified SHA256 of the current bilingual backup>'
```

Capture uses a read-only repeatable-read transaction with shared table locks. The strict `v6-actor-import-baseline-v1` descriptor binds the source cluster/system identifier, PG major/pgvector version, original migration checksums, all accepted P8 catalog/corpus/artifact/profile/state identities, both complete language counts, fresh five-table content digests, an explicit projection of all original eleven movie columns, a complete OID-free public schema snapshot and its SHA256, and the verified backup SHA256. The four immutable content/state digests and complete canonical 001+002 schema fingerprint are pinned after independent verification; migration timestamps remain bound to the fresh descriptor. No mutable database byte-size pin is used. Duplicate, reordered, missing, extra or wrongly typed fields fail validation. Output must be an absent absolute file. Capture does not perform a backup or prove a restore.

After independently restoring that backup into the approved isolated target, set `DATABASE_CONNECTION_STRING` to that target and run:

```powershell
dotnet src/etl/CineKros.VectorImporter/bin/Debug/net10.0/CineKros.VectorImporter.dll `
  --paired-actor-v6 `
  --expected-database '<explicit isolated rehearsal/test database>' `
  --baseline '<absolute approved baseline JSON path>' `
  --catalog '<absolute approved V6 catalog JSONL>' `
  --manifest '<absolute approved V6 catalog manifest JSON>' `
  --dictionary '<absolute approved translation dictionary JSON>' `
  --source-catalog '<absolute approved original catalog JSONL>' `
  --pair-root '<absolute approved V6 paired vector root>' `
  --migrations '<absolute canonical migrations directory>'
```

All input paths must be absolute. Input validation verifies every approved catalog row, actor provenance, both full 9,730-vector files/manifests and the paired descriptor before opening a database connection. The old reformatted P8 catalog is not an input to this new importer. There is no model load, inference or provider call.

Migration 003 is owned exclusively by this importer and must never be run separately. The default and old POC migrator remain unchanged. One serializable transaction takes the migration/catalog/vector advisory locks and exclusive table locks, checks the exact baseline, adds nullable `movies."cast"`, its immutable SQL validator and GIN index, replaces both vector/fingerprint sets, updates the existing catalog and language states, and inserts `actor_catalog_state`. `NULL` means unknown; known arrays must be nonempty, one-dimensional, start at index 1, contain no NULL/blank elements and have no exact duplicates. Case-distinct names remain distinct.

Before commit, the importer compares every old metadata field, exact movie IDs, every ordered cast value/NULL, all 19,460 vectors and fingerprints, original migration timestamps, full schema (including the cast validator body), and manifest-linked state. Readiness is derived from complete verification; no READY boolean is stored. An exact complete rerun exhaustively verifies the release and writes nothing. Partial, mixed, unknown or corrupted states are refused without repair.

For explicitly isolated rollback tests only, append `--test-fault after-migration`, `partial-cast`, `partial-vectors` or `partial-state`. Every fault throws inside the same transaction. Cancellation rolls back uncommitted work.

Opt-in integration tests never create/drop databases. The test operator restores and owns the exact isolated target. Set process-only `CINEKROS_V6_IMPORT_TEST_CONNECTION`, `CINEKROS_V6_EXPECTED_DATABASE`, `CINEKROS_V6_REPOSITORY_ROOT` and `CINEKROS_V6_BASELINE`. Enable `CINEKROS_V6_RUN_BASELINE_IMPORT=1` only on a freshly restored 001+002 target for `OptInFourFaultsRollBackThenFullImportAndRepeatWritesNothing`; enable `CINEKROS_V6_RUN_FINAL_CHECKS=1` on the completed V6 target for `OptInCommittedV6ChecksCastSemanticsAndRefusesCorruptionWithoutRepair`. The latter temporarily mutates owned isolated rows/constraints/index/helper to prove refusal and restores each mutation in a `finally` block. Run these tests serially; do not run the baseline test again on an already upgraded target. Independent content/backup/recovery verification remains required.
