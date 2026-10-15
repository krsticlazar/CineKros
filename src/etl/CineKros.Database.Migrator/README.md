# CineKros.Database.Migrator

Applies ordered SQL migrations to an existing PostgreSQL database. It reads `DATABASE_CONNECTION_STRING` from the process environment, creates or updates schema state, and verifies each recorded migration's SHA-256 on rerun. From the repository root:

```powershell
dotnet run --project src/etl/CineKros.Database.Migrator -- database/migrations
```

Normal mode applies only `001_*` migrations. The explicit `--poc-draft-002` mode is restricted in code to a database whose actual name starts with `cinekros_sr_poc_`; it applies only `001_*` and `002_serbian_search_vectors.sql`. It is an isolated POC operation, not production authorization. Never edit an already-applied migration: its checksum is part of the database contract.
