# CineKros.Database.Migrator

## Role

This CLI applies ordered, versioned SQL migrations to an existing PostgreSQL database. It does not create or start the database. The connection string comes from `DATABASE_CONNECTION_STRING`.

## Pipeline position

`PostgreSQL → Database.Migrator → schema → [Catalog.Importer](../CineKros.Catalog.Importer/README.md) → [VectorImporter](../CineKros.VectorImporter/README.md) → API`

Migration SQL is in [`database/migrations`](../../../database/migrations). From the repository root, the normal command is:

```powershell
dotnet run --project src/etl/CineKros.Database.Migrator -- database/migrations
```

## Code map

- [Program.cs](Program.cs) sorts `.sql` files by name, serializes concurrent runs with an advisory lock, and applies each new migration in a transaction.
- `schema_migrations` records each version and SHA-256. A rerun verifies the checksum; changing an already-applied migration stops execution.

The migrator makes schema changes traceable before importers write data.

## Migration 001/002 boundary

Normal startup remains capped at `001_*` migrations. Adding `002_serbian_search_vectors.sql` therefore does not expand production startup automatically. An explicit `--poc-draft-002` option exists for the isolated, phase-approved Serbian POC; it selects only `001_*` and the exact `002_serbian_search_vectors.sql` (never a future `003_*` or another migration) and requires actual `current_database()` to begin with `cinekros_sr_poc_` before schema bootstrap.

Example for an isolated POC database, from the repository root:

```powershell
$env:DATABASE_CONNECTION_STRING = '<local connection to an isolated cinekros_sr_poc_* database>'
dotnet run --project src/etl/CineKros.Database.Migrator -- --poc-draft-002 database/migrations
Remove-Item Env:DATABASE_CONNECTION_STRING
```

Provide credentials only through a process-local connection string; do not write them to a file, shared history, or report. `--poc-draft-002` is not approved for production and rejects database `cinekros`. Do not modify or normalize migration 001: the migrator checks SHA-256 for previously applied migrations. This provisions only the isolated POC schema; it does not activate API/DI/backend search or production application of migration 002.
