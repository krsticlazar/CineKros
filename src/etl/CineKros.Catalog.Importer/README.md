# CineKros.Catalog.Importer

## Uloga

CLI učitava odobreni finalni JSONL katalog, proverava ga i transakciono upisuje filmske podatke u PostgreSQL. Ne pravi katalog niti računa embeddings. Ponavljanje istog uvoza proverava postojeće stanje; drugačiji ili nepotpun sadržaj se odbija.

## Mesto u toku

`[CineKros.Etl](../CineKros.Etl/README.md) → katalog + manifest → Catalog.Importer → movies`

Validacione tipove koriste i [E5.Generator](../CineKros.E5.Generator/README.md) i [VectorImporter](../CineKros.VectorImporter/README.md), kako bi proverili da vektori pripadaju istom katalogu.

## Kako čitati kod

- [Program.cs](Program.cs) proverava argument putanje i uzima `DATABASE_CONNECTION_STRING` iz okruženja; zatim poziva validator pa importer.
- [Catalog.cs](Catalog.cs) i `CatalogValidator.LoadAsync` proveravaju redove, identitete, manifest i fingerprint pre pristupa bazi.
- [CatalogImporter.cs](CatalogImporter.cs) upisuje filmove i stanje uvoza u jednoj transakciji, uključujući proveru bezbednog ponavljanja.

Importer je granica između artefakta nastalog offline i strukturisane baze. Uspela validacija kataloga je uslov za naredne korake, nije dokaz da su film-vektori već učitani.
