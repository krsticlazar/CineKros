# CineKros.Catalog.Importer

The standard command validates the legacy 17-field JSONL catalog and imports its movie rows into PostgreSQL in one transaction. It reads `DATABASE_CONNECTION_STRING` from the process environment:

```powershell
dotnet run --project src/etl/CineKros.Catalog.Importer -- <catalog.jsonl>
```

The standard command does not create catalogs, generate vectors, or call providers. The catalog must include its validated manifest and identity. An identical re-import verifies existing state; incomplete or different state is rejected. Protect the target database and keep credentials out of command arguments and files.

The separate file-only `export-full-bilingual <source.jsonl> <dictionary.json> <new-output-root>` command validates the source and dictionary and writes the full bilingual 19-field catalog to a new output root. It does not import to a database. Importing that catalog and paired vectors uses the separately gated `CineKros.VectorImporter --paired-full-cutover` rehearsal path, not the standard catalog command.

The separate file-only V6 command validates the reviewed current/accepted bilingual artifacts, B05-A source and unchanged dictionary, then publishes a new twenty-field catalog with structured cast and cast-free semantic texts:

```powershell
dotnet run --project src/etl/CineKros.Catalog.Importer -- export-actor-catalog-v6 <absolute-current-catalog> <absolute-accepted-compact-catalog> <absolute-b05a-catalog> <absolute-dictionary-json> <absolute-new-output-root>
```

It runs before any database/environment handling and refuses existing output. Production hashes are mandatory; fixtures use a separate internal test surface. No provider, model, database or runtime cutover occurs. See [the V6 file contract](../../../docs/ACTOR_CATALOG_V6.md) for preservation, parser, identity and review requirements.
