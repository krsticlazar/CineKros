# CineKros.Etl.Tests

## Čemu služi

Testira `CineKros.Etl` nad malim kontrolisanim datasetima i lažnim HTTP odgovorima. Obuhvata izbor filmova, TagDL/`semanticText`, MovieLens 32M join i rating agregate, TMDB offline cache, spajanje finalnog kataloga i istorijske/fake embedding CLI granice. Ne troši live TMDB ili Gemini kvotu.

## Gde se uklapa u CineKros

`CineKros.Etl → CineKros.Etl.Tests`. Test projekat referencira ETL CLI, ne backend. Ovim proveravamo podatke pre nego što importer ili generator vide finalni katalog.

## Najvažniji delovi

- `MetadataExporterTests.cs` i `SemanticExporterTests.cs` — deterministički subset, top tagovi i format teksta.
- `EnrichmentV2Tests.cs` / `EnrichmentV2R2Tests.cs` — exact join, streaming ocene, fake TMDB HTTP, cache i bezbedan prekid/limit.
- `CombinedCatalogTests.cs` — spajanje polja, manifest i odbijanje nekonzistentnih izvora.
- `FakeDocumentEmbeddingTests.cs` i `LegacyEmbeddingCliTests.cs` — testna checkpoint logika i zabrana zastarelog real Gemini embedding ulaza.

## Šta bih rekao profesorki

> „ETL testovi štite poreklo podataka: nad malim poznatim primerima dokazujemo da isti ulazi daju isti katalog i da greška ne objavi polovičan ili pogrešno povezan rezultat.“
