# CineKros.Database.Migrator

## Uloga

CLI primenjuje uređene, verzionisane SQL migracije na već postojeću PostgreSQL bazu. Ne kreira niti pokreće samu bazu. Connection string čita iz `DATABASE_CONNECTION_STRING`.

## Mesto u toku

`PostgreSQL → Database.Migrator → schema → [Catalog.Importer](../CineKros.Catalog.Importer/README.md) → [VectorImporter](../CineKros.VectorImporter/README.md) → API`

Migracioni SQL nalazi se u [`database/migrations`](../../../database/migrations). Iz korena repozitorijuma, uz podešenu konekciju, stvarna komanda je:

```powershell
dotnet run --project src/etl/CineKros.Database.Migrator -- database/migrations
```

## Kako čitati kod

- [Program.cs](Program.cs) sortira `.sql` fajlove po imenu, ograničava paralelno izvršavanje advisory lock-om i pokreće svaku novu migraciju u transakciji.
- Evidencija `schema_migrations` čuva verziju i SHA-256. Ponovljeno pokretanje proverava checksum; promenjena ranije primenjena migracija zaustavlja tok.

Migrator čini promene šeme sledljivim pre nego što importer-i upišu podatke.
