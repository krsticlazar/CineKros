# CineKros database migrator

Run numbered SQL migrations in filename order using the PostgreSQL connection in the process environment variable `DATABASE_CONNECTION_STRING`:

```powershell
$env:DATABASE_CONNECTION_STRING = '<connection string supplied by the caller>'
dotnet run --project src/etl/CineKros.Database.Migrator/CineKros.Database.Migrator.csproj -- database/migrations
```

Each migration runs in a transaction and is recorded by SHA-256 in `schema_migrations`. A repeat verifies the recorded checksum without rerunning SQL; a changed migration with an existing version stops. The runner does not create a database, read `.env`, or print connection details. It can target local PostgreSQL or a compatible PostgreSQL service by changing only the caller-provided connection string.
