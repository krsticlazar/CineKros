# CineKros.VectorImporter

Validates vector artifacts against their catalog/profile and imports them transactionally into PostgreSQL. It reads `DATABASE_CONNECTION_STRING` from the process environment. It never calculates vectors; it rejects incomplete or incompatible artifacts.

## Modes and database boundaries

Read-only V6: `ActorVectorArtifactV6Validator.LoadAsync(absolutePublishedDirectory, actorCatalog, "en"|"sr")` validates exact V6 actor/text/model identities, ordered complete coverage and finite normalized 768D vectors. `ValidatePairAsync(absoluteRoot, actorCatalog)` independently validates both publications and deterministic `paired-release.json` hashes. Obtain the catalog through `ActorCatalogV6Reader.LoadAsync`. These methods never connect to a database or run inference; this package adds no V6 database/CLI import.

- Standard: `--catalog <reviewed-catalog.jsonl> --artifact <complete-artifact-dir>`. Use only after schema migration and catalog import.
- POC: explicit `--paired-poc --catalog ... --manifest ... --dictionary ... --source-catalog ... --en ... --sr ...`. This imports paired vectors and readiness metadata only into the exact approved POC database `cinekros_sr_poc_phase04_20261008` or its `cinekros_sr_poc_phase04_test_*` disposable test family. It verifies the actual connected database and required PG/pgvector/migration state.
- Full paired rehearsal: explicit `--paired-full-cutover` requires MAIN's baseline descriptor, exact reviewed catalog and EN/SR artifacts, legacy artifacts, migrations, and explicit target database. It accepts only `cinekros_sr_p9_rehearsal_20261009` or `cinekros_sr_p9_test_*`; this worker CLI always rejects production `cinekros`. It may apply migration 002 and replace vectors inside one transaction. Use only after MAIN releases the exact restored rehearsal target; there is no production override.

All modes write database state. Keep credentials in the process environment, verify the target before running, and do not use production credentials for POC/rehearsal commands. Full paired imports validate inputs and existing database identity before writing; identical completed runs are verified as no-ops. These constraints do not authorize a run.
