# CineKros solution — pregled i redosled proučavanja

Ovaj vodič objašnjava [CineKros.slnx](CineKros.slnx): zašto ima više projekata, kako su povezani i kojim redom ih čitati. Solution trenutno sadrži **15 .NET projekata: 8 izvornih i 7 testnih**. Najpre pročitaj README konkretnog projekta, zatim navedene ključne klase, pa njegove testove.

## Zašto postoji više projekata

CineKros ima dva glavna toka: pripremu podataka unapred i obradu korisničkog upita tokom rada aplikacije. Njihovi alati imaju različite ulaze, izlaze i odgovornosti, pa nisu svi deo backend procesa.

- **Runtime:** `CineKros.Api` prima zahtev i izvršava pretragu; `CineKros.Embedding` daje lokalne E5 vektore.
- **Priprema podataka:** ETL priprema katalog, migrator priprema šemu, a dva importera upisuju katalog i vektore.
- **Embedding tooling:** E5 generator unapred računa document vektore filmova.
- **Evaluation:** poseban CLI proverava parser i retrieval i pravi izveštaje.
- **Tests:** proveravaju svaku od ovih granica, uključujući podatke, ugovore, checkpoint i SQL.

Odvajanje prati stvarne odgovornosti. API ne pokreće ETL niti generiše svih 9.730 filmskih vektora kada korisnik pošalje upit.

## Kako sistem radi

### Tok korisničkog upita

```text
Frontend → POST /api/recommendations → lokalna validacija zahteva
         → Gemini checklist → backend validacija i V4 normalizacija
         → canonical query
            ├─ hard-only: SQL filteri → deterministički redosled
            └─ semantic/hybrid: lokalni E5 query vektor
                               + SQL izbor dozvoljenih kandidata
                               → exact pgvector rangiranje
         → najviše 10 filmova → frontend
```

Gemini parsira zahtev; ne bira filmove i ne pravi embeddings. `RealParsedQueryValidator` proverava checklist i formira canonical DTO, dok `RatingThresholdNormalizer` normalizuje rating prag. PostgreSQL primenjuje hard uslove, a pgvector rangira dozvoljene filmove po sličnosti sa lokalnim E5 vektorom. Hard-only putanja ne poziva embedding model. Semantic-only putanja nema aktivne SQL hard uslove.

Pozitivno pominjanje glumca ili režisera ostaje semantički zahtev. Nije SQL filter i ne garantuje tačno podudaranje osobe. Negativni/exclusion uslovi ostaju unsupported.

### Offline priprema

```text
Tag Genome + MovieLens 32M + offline TMDB enrichment
  → CineKros.Etl → finalni katalog / semanticText / manifest
  → CineKros.E5.Generator + CineKros.Embedding → document vektori

PostgreSQL → CineKros.Database.Migrator → šema
          → CineKros.Catalog.Importer → movies
          → CineKros.VectorImporter → movie_embeddings
          → API može da pretražuje pripremljenu bazu
```

Generisanje vektora i priprema baze su odvojeni koraci. Import vektora zahteva već uvezen kompatibilan katalog. E5 generator i runtime query adapter koriste isti E5-base-v2 INT8 ONNX profil od 768 dimenzija.

## Preporučeni redosled proučavanja

Ovo je redosled za razumevanje koda, ne redosled pokretanja alata.

1. [CineKros.Api](src/backend/CineKros.Api/README.md) — kreni od `Program.cs` i `Startup/RecommendationStartup.cs`. Zatim prati `RealRecommendationEndpoint` → `RealRecommendationService` → `GeminiRealQueryParser` / `RealParsedQueryValidator` → `MovieSearchRepository`. Cilj: razumeti jedan ceo request i grananje hard-only/semantic/hybrid.
2. [CineKros.Api.Tests](tests/CineKros.Api.Tests/README.md) — prvo `RealFlow/`, zatim `RealProviders/`, pa `Database/` i `Search/`. Cilj: videti koji ugovori i hard-filter invariants štite tok koji si upravo pročitao.
3. [CineKros.Embedding](src/embedding/CineKros.Embedding/README.md) — prouči `E5EmbeddingModel`: `passage:` i `query:` format, tokenizaciju, pooling, normalizaciju i fingerprint. Cilj: razumeti odakle dolazi vektor koji API koristi.
4. [CineKros.Embedding.Tests](tests/CineKros.Embedding.Tests/README.md) — pogledaj kako se proveravaju model artefakti, maskiranje i normalizovan 768D izlaz.
5. [CineKros.Etl](src/etl/CineKros.Etl/README.md) — prati `Program.cs`, metadata/semantic export, `EnrichmentV2Runner` i `CombinedCatalogExporter`. Cilj: objasniti poreklo kataloga, rating agregata i `semanticText`.
6. [CineKros.Etl.Tests](tests/CineKros.Etl.Tests/README.md) — prvo metadata, semantic, enrichment i merge testovi. Fake i istorijske embedding testove ostavi za drugi prolaz; nisu current E5 generacija.
7. [CineKros.Database.Migrator](src/etl/CineKros.Database.Migrator/README.md) — prouči mali entry point i SQL fajlove u `database/migrations/`. Cilj: razumeti kako nastaju tabele i kako se pamti verzija šeme. Nema zaseban test projekat; schema se koristi i proverava kroz DB integracione suite-ove.
8. [CineKros.Catalog.Importer](src/etl/CineKros.Catalog.Importer/README.md) — `CatalogValidator` pa `CatalogImporter`. Cilj: razumeti proveru finalnog fajla, transakciju i bezbedno ponavljanje uvoza.
9. [CineKros.Importer.Tests](tests/CineKros.Importer.Tests/README.md) — pogledaj hash validaciju, identičan rerun i rollback. Cilj: objasniti zašto baza ne može ostati polovično popunjena nakon neuspelog uvoza.
10. [CineKros.E5.Generator](src/etl/CineKros.E5.Generator/README.md) — `GeneratorArguments`, `E5DocumentVectorSource` i `DocumentVectorGenerator`. Cilj: povezati katalog sa lokalnim batch embedding-om i resumable checkpoint-om.
11. [CineKros.E5.Generator.Tests](tests/CineKros.E5.Generator.Tests/README.md) — prouči selektivni resume, oporavak checkpoint-a i finalni publish. Cilj: razumeti zašto prekid ne zahteva ponavljanje uspešnih vektora.
12. [CineKros.VectorImporter](src/etl/CineKros.VectorImporter/README.md) — `VectorArtifactValidator` pa `VectorImporter`. Cilj: objasniti proveru kompatibilnosti i vezu film → vektor u PostgreSQL-u.
13. [CineKros.VectorImporter.Tests](tests/CineKros.VectorImporter.Tests/README.md) — proveri odbijanje pogrešne dimenzije/profila/identiteta i atomski upis. Zatim se vrati na API `MovieSearchRepository`: sada znaš kako je baza pripremljena za njegovo rangiranje.
14. [CineKros.Evaluation](src/evaluation/CineKros.Evaluation/README.md) — `Program`, `EvaluationRunner` i `EvaluationLogic`. Cilj: razlikovati evaluaciju parsera od retrieval-a i tehnički dry-run od finalnog akademskog eksperimenta.
15. [CineKros.Evaluation.Tests](tests/CineKros.Evaluation.Tests/README.md) — pogledaj compliance, poređenje parsera i izveštaje. Cilj: razumeti kako se proveravaju same evaluacione kalkulacije.

Za prvi kratki prolaz pred konsultacije dovoljno je da pročitaš ovaj vodič i README-e svih projekata. Za detaljniji prolaz prati navedene klase i čitaj testove odmah posle odgovarajućeg produkcionog projekta.

## Stvarne projektne zavisnosti

Tabela prikazuje **direktne `ProjectReference` veze**, a ne redosled izvršavanja. „Nema” znači da projekat ne referencira drugi CineKros projekat; i dalje može koristiti NuGet biblioteke, fajlove ili bazu.

| Projekat | Direktno referencira |
| --- | --- |
| CineKros.Api | CineKros.Embedding |
| CineKros.Embedding | Nema |
| CineKros.Etl | Nema |
| CineKros.Database.Migrator | Nema |
| CineKros.Catalog.Importer | Nema |
| CineKros.E5.Generator | CineKros.Embedding, CineKros.Catalog.Importer |
| CineKros.VectorImporter | CineKros.Catalog.Importer |
| CineKros.Evaluation | CineKros.Api, CineKros.Embedding |
| CineKros.Api.Tests | CineKros.Api, CineKros.Catalog.Importer |
| CineKros.Embedding.Tests | CineKros.Embedding |
| CineKros.Etl.Tests | CineKros.Etl |
| CineKros.Importer.Tests | CineKros.Catalog.Importer |
| CineKros.E5.Generator.Tests | CineKros.E5.Generator |
| CineKros.VectorImporter.Tests | CineKros.VectorImporter, CineKros.Catalog.Importer |
| CineKros.Evaluation.Tests | CineKros.Evaluation |

Graf nema kružne zavisnosti. API ne referencira importere: vezu sa njima ostvaruje preko pripremljenih tabela i proverenog import-state-a u bazi. ETL i generator razmenjuju fajlove, pa između njih ne mora postojati `ProjectReference`. Evaluation koristi postojeće parser/search/model implementacije kako ih ne bi ponovo pisao.

## Kako čitati solution u Visual Studio-u

Folderi prikazani u solution-u grupišu projekte: `/src/backend/`, `/src/etl/`, `/src/` i `/tests/`. To nisu dodatni izvršivi projekti. `CineKros.Api` je web proces; ETL, migrator, importeri, generator i evaluation su odvojeni CLI alati. `CineKros.Embedding` je biblioteka. Test projekti se izvršavaju kroz test runner.

Frontend je u [src/frontend/](src/frontend/README.md), van .NET solution-a. HTTP poziv je u `src/frontend/src/App.tsx` (`submitHttp`), a URL se formira u `src/frontend/src/apiConfiguration.ts`; endpoint je `POST /api/recommendations`. Docker Compose i SQL migracije su u `database/` i nisu zasebni .NET projekti.

Čitanje testova ne zahteva njihovo pokretanje. Za full solution test potrebni su Docker, lokalni katalog i E5 model; detalji i opt-in uslovi su u [tests/README.md](tests/README.md).

## Kratko objašnjenje za mentorku

> „Solution deli odgovornosti između web aplikacije, pripreme podataka i evaluacije. API koristi Gemini da pretvori tekst u strukturisan zahtev, SQL da sprovede obavezne uslove i lokalni E5 sa pgvector-om da rangira rezultate. Posebni CLI alati unapred pripremaju katalog, računaju filmske vektore i uvoze ih u bazu. Test projekti proveravaju te granice, pa više projekata predstavlja preglednu podelu posla, a ne više zasebnih aplikacija koje sve moraju stalno da rade.”
