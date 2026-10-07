# CineKros.Importer.Tests

## Čemu služi

Testira `CineKros.Catalog.Importer`: proveru odobrenog JSONL kataloga i siguran uvoz u PostgreSQL. Tu su unit/contract provere validacije i integracioni testovi transakcija nad testnom bazom.

## Gde se uklapa u CineKros

`CineKros.Catalog.Importer → CineKros.Importer.Tests`. Test projekat direktno referencira katalog importer; ne računa vektore niti testira Gemini parser.

## Najvažniji delovi

- `CatalogValidatorTests.cs` — proverava manifest/hash, oblik i identitet pregledanog kataloga.
- `CatalogImporterIntegrationTests.cs` — proverava svih 9.730 redova, identično ponavljanje bez izmena, odbijanje promenjenog stanja i rollback pri DB grešci.

## Šta bih rekao profesorki

> „Ovi testovi dokazuju da katalog ulazi u bazu samo kao celina i da ponovno pokretanje ne duplira ili menja filmske podatke.“
