# CineKros.Api.Tests

## Čemu služi

Testira `CineKros.Api`: HTTP ugovor, real/fake preporučivanje, Gemini parser adapter, validaciju canonical query-ja, hard filtere i pretragu. Meša unit/contract testove sa integracionim testovima koji se pokreću uz PostgreSQL kada je test baza dostupna.

## Gde se uklapa u CineKros

`CineKros.Api (+ Catalog.Importer) → CineKros.Api.Tests`. Test projekat referencira oba navedena produkciona projekta. Fake HTTP/provider odgovori proveravaju ugovor bez live Gemini poziva; zasebni DB testovi proveravaju stvarni SQL i pgvector.

## Najvažniji delovi

- `RecommendationEndpointTests.cs` i `RealFlow/` — API envelope, lokalizacija, hard-only bez embedding poziva i hybrid tok.
- `RealProviders/RealProviderAdapterTests.cs` — checklist DTO, rating V4 normalizacija, alerti i sanitizovane greške.
- `Database/` i `Search/` — inkluzivne/striktne granice, null pravilo, filtriranje pre exact rangiranja i deterministički redosled.
- `PhaseCIntegration/` — realna baza/API integracija i E5 smoke; `Startup/` i `Diagnostics/` proveravaju CORS i development log.

## Šta bih rekao profesorki

> „Ovi testovi dokazuju da parser ne može tiho da zaobiđe poslovna pravila: zahtev prolazi validaciju, SQL filteri su zaista strogi, a semantička putanja rangira samo dozvoljene filmove.“
