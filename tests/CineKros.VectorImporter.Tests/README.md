# CineKros.VectorImporter.Tests

## Čemu služi

Integracioni testovi za `CineKros.VectorImporter` i njegov vektorski artefakt. Proveravaju da se samo potpun i kompatibilan skup document vektora može transakciono povezati sa već uvezenim katalogom.

## Gde se uklapa u CineKros

`CineKros.VectorImporter + CineKros.Catalog.Importer → CineKros.VectorImporter.Tests`. Oba produkciona projekta su direktne reference, jer test postavlja katalog i zatim proverava uvoz vektora u PostgreSQL.

## Najvažniji delovi

- `VectorImporterIntegrationTests.cs` — proverava kompatibilnost artefakta, tačne MovieLens ID veze, atomski uvoz, bezbedno ponavljanje i odbijanje neispravnog stanja.

## Šta bih rekao profesorki

> „Ovi testovi čuvaju vezu između filma i njegovog E5 vektora. Ako se katalog, profil modela ili broj vektora ne slažu, uvoz mora stati umesto da proizvede naizgled ispravnu, ali pogrešnu pretragu.“
