# CineKros — priprema za razgovor i vodič kroz kod

Jedno mesto za konsultacije i proučavanje aplikacije. Čitaj redom: od kratkog objašnjenja sistema, preko jednog korisničkog zahteva, do SQL rangiranja i prikaza rezultata. Linkovi vode direktno na lokalne fajlove i relevantne redove u CineKros projektu; nisu linkovi ka javnom GitHub-u.

Spojeno iz dva postojeća vodiča, uz proveru ključnih poziva u kodu: **15.10.2026.** Originali su sačuvani, aplikacija nije menjana.

## 1. Kako da započnem razgovor

„CineKros je dvojezična aplikacija za preporuku filmova. Korisnik na engleskom ili srpskom opisuje šta želi, a Gemini pretvara njegov zahtev u strukturisane uslove i semantički opis. Backend proverava taj rezultat. PostgreSQL primenjuje obavezne filtere, a lokalni multilingual E5 i pgvector rangiraju odgovarajuće filmove po značenjskoj sličnosti. Ako zahtev sadrži samo filtere, E5 se ne poziva. Rezultat je najviše deset filmova, a ne lista koju je izmislio jezički model.“

Tri uloge koje treba jasno razdvojiti:

- **Gemini:** tumačenje prirodnog jezika.
- **SQL:** obavezna ograničenja nad stvarnim katalogom.
- **E5 + pgvector:** semantička reprezentacija i rangiranje.

Ako treba odmah pokazati najvažniji kod:

1. [RealRecommendationService.cs — glavni tok](D:/Repozitorijum/CineKros/src/backend/CineKros.Api/RealFlow/RealRecommendationService.cs:98).
2. [MovieSearchRepository.cs — SQL i jezički Top-10](D:/Repozitorijum/CineKros/src/backend/CineKros.Api/Search/MovieSearchRepository.cs:95).
3. [GeminiRealQueryParser.cs — stvarni HTTP poziv](D:/Repozitorijum/CineKros/src/backend/CineKros.Api/RealProviders/GeminiRealQueryParser.cs:31).

## 2. Mentalna mapa jednog zahteva

```text
Korisnik: jezik + poruka
        ↓
React → POST /api/recommendations
        ↓
Backend: provera ulaza
        ↓
Gemini V5: provider checklist
        ↓
Validacija + kanonski query DTO
        ↓
Izbor putanje:
  hard-only → SQL filteri + stalni redosled
  semantic  → E5 → SQL eligible + distance
        ↓
Najviše 10 filmova / alert / error
        ↓
React: provera odgovora + kartice
```

**Važna preciznost:** kod semantičke putanje prvo nastaje vektor upita, a zatim se izvršava SQL. Unutar SQL-a hard filteri ograničavaju kandidate **pre rangiranja po udaljenosti**. Nije isto reći „filteri prethode rangiranju“ i „SQL se izvršava pre E5“.

Dalji odeljci prate ovaj tok. Startup je izdvojen prvi jer se izvršava jednom, a ne ponovo pri svakom upitu.

## 3. Pokretanje i povezivanje komponenti

### Lokalni launcheri

- [start_script.cmd — normalno pokretanje](D:/Repozitorijum/CineKros/src/start_script.cmd:11): lokalna konfiguracija, Docker/PostgreSQL, backend i frontend. Ovo je launcher za punu aplikaciju sa **9.730 filmova**.
- [start_poc.cmd — izdvojeni POC](D:/Repozitorijum/CineKros/src/start_poc.cmd:1) i [start-poc.ps1](D:/Repozitorijum/CineKros/src/start-poc.ps1:1): zasebna v2 baza sa 150 filmova, ne normalna produkciona putanja.
- [start-headless-runtime.ps1](D:/Repozitorijum/CineKros/src/start-headless-runtime.ps1:1): posebna headless varijanta pokretanja.

### Program.cs — pregled startup-a

Otvori [Program.cs](D:/Repozitorijum/CineKros/src/backend/CineKros.Api/Program.cs:8).

Top-level kod:

1. Kreira ASP.NET host.
2. Određuje recommendation režim i POC flag.
3. Registruje servise, CORS i rate limiter.
4. U real režimu učitava E5 i proverava spremnost oba jezička skupa.
5. Mapira **POST `/api/recommendations`** na real ili fake endpoint.
6. Pokreće aplikaciju.

Ruta ima limit **30 zahteva u 60 sekundi po IP-u**, u okviru jednog API procesa. To nije Gemini kvota.

### RecommendationStartup.cs — koje implementacije se koriste

Otvori [RecommendationStartup.cs](D:/Repozitorijum/CineKros/src/backend/CineKros.Api/Startup/RecommendationStartup.cs:11).

- `ResolveMode`: čita `CINEKROS_RECOMMENDATION_MODE`. Development bez podešenog režima podrazumeva `fake`; van Development režim mora biti eksplicitan, a Production zahteva `real`.
- `ResolveSerbianPocMode`: dozvoljava zaseban POC samo uz odgovarajući Development/real režim.
- `RegisterServices`: bira fake registracije ili poziva `RegisterRealServices`.
- `RegisterRealServices`: povezuje Gemini parser, validator, lokalni E5, PostgreSQL repository i recommendation servis.
- `LoadE5Model`: učitava model i odbija nedostupne/nekompatibilne artefakte.

Real mode zahteva process konfiguraciju `DATABASE_CONNECTION_STRING`, `GEMINI_API_KEY` i `CINEKROS_E5_MODEL_DIR`. Vrednosti tajni se ne pokazuju.

DI, odnosno dependency injection, znači da startup poveže konkretne implementacije sa interfejsima. Endpoint dobija servis, a servis dobija parser, encoder adapter i search adapter; ne pravi ih iznova za svaki zahtev.

Normalni runtime bira [CreateFullBilingualProductionRepositoryAsync](D:/Repozitorijum/CineKros/src/backend/CineKros.Api/Search/MovieSearchRepository.cs:45). POC bira zaseban factory. [SearchDatasetExpectation.cs](D:/Repozitorijum/CineKros/src/backend/CineKros.Api/Search/SearchDatasetExpectation.cs:1) definiše očekivane identitete baze, kataloga i jezičkih artefakata.

**Readiness nije samo „baza odgovara“:** proverava se da su katalog, vektori i model kompatibilni. [EnsureRuntimeReadyAsync](D:/Repozitorijum/CineKros/src/backend/CineKros.Api/Search/MovieSearchRepository.cs:149) proverava oba jezika pre prihvatanja zahteva.

## 4. Korisnik šalje zahtev — React

Otvori [App.tsx — send](D:/Repozitorijum/CineKros/src/frontend/src/App.tsx:121).

- `App.send`: uzima poruku i izabrani jezik, formira `SearchRequest` i upravlja slanjem/stanjima ekrana.
- [submitHttp](D:/Repozitorijum/CineKros/src/frontend/src/App.tsx:20): šalje JSON POST kroz `fetch`, uz `AbortSignal`, i vraća HTTP status i telo odgovora.
- [types.ts](D:/Repozitorijum/CineKros/src/frontend/src/types.ts:1): definiše frontend tipove zahteva i odgovora.

Ulaz u API je `{ language, message }`, pri čemu je jezik `en` ili `sr`. To je odvojeno od filtera `originalLanguage`, koji označava originalni jezik filma.

Otvori [apiConfiguration.ts — recommendationEndpoint](D:/Repozitorijum/CineKros/src/frontend/src/apiConfiguration.ts:3).

`recommendationEndpoint` određuje adresu API-ja: podrazumevano relativna ruta, ili provereni `VITE_API_BASE_URL` origin. HTTPS je obavezan osim dozvoljenog HTTP loopback-a u developmentu. React ne kontaktira direktno Gemini ili PostgreSQL.

## 5. Backend prima i proverava zahtev

Otvori [RealRecommendationEndpoint.cs — HandleAsync](D:/Repozitorijum/CineKros/src/backend/CineKros.Api/RealFlow/RealRecommendationEndpoint.cs:11), zatim [RecommendationEndpoint.cs — ReadRequestAsync](D:/Repozitorijum/CineKros/src/backend/CineKros.Api/RecommendationEndpoint.cs:64).

- `RealRecommendationEndpoint.HandleAsync`: HTTP granica realne aplikacije. Čita zahtev, poziva recommendation servis, a na kraju mapira rezultat u javni odgovor.
- `ReadRequestAsync`: zajednička ulazna provera real/fake endpointa; proverava telo, JSON, jezik i poruku i vraća `ParserInput` ili neispravan zahtev.
- `RecommendationEndpoint.Alert` i `Technical`: zajedničko formatiranje poslovnih i sanitizovanih tehničkih odgovora.

Neispravan lokalni ulaz odbija se pre parsera. Važno je razlikovati validaciju korisničkog HTTP zahteva od kasnije validacije Gemini rezultata.

## 6. Gemini tumači nameru, validator pravi kanonski DTO

### GeminiRealQueryParser.cs — provider adapter

Otvori [GeminiRealQueryParser.cs](D:/Repozitorijum/CineKros/src/backend/CineKros.Api/RealProviders/GeminiRealQueryParser.cs:21).

- `ParseAsync`: ulaz koji koristi recommendation servis preko adaptera.
- `ParseWithEvidenceAsync`: formira poziv modelu `gemini-3.1-flash-lite`, šalje poruku i izabrani jezik sa structured-output šemom, čita odgovor i prosleđuje ga validatoru.
- [Redovi oko HTTP poziva](D:/Repozitorijum/CineKros/src/backend/CineKros.Api/RealProviders/GeminiRealQueryParser.cs:31): `generateContent` POST, API credential u header-u, `HttpClient.SendAsync`.

Uputstva i schema nalaze se u [query-parser-v5.md](D:/Repozitorijum/CineKros/src/backend/CineKros.Api/RealProviders/query-parser-v5.md:1) i [query-parser-v5.schema.json](D:/Repozitorijum/CineKros/src/backend/CineKros.Api/RealProviders/query-parser-v5.schema.json:1).

Provider checklist i kanonski DTO nisu isti sloj: Gemini daje strukturisano tumačenje, backend proverava dozvoljeni oblik/status uslova i normalizuje vrednosti. Rezultat je `query` sa `hardFilters` i opcionalnim `semanticQuery`, ili dozvoljeni poslovni alert.

### RealParsedQueryValidator.cs — granica poverenja

Otvori [ValidateV5](D:/Repozitorijum/CineKros/src/backend/CineKros.Api/RealProviders/RealParsedQueryValidator.cs:125) i [ValidateResultV5](D:/Repozitorijum/CineKros/src/backend/CineKros.Api/RealProviders/RealParsedQueryValidator.cs:194).

- `ValidateV5`: proverava provider JSON/checklist i formira validirani kanonski rezultat.
- `ValidateResultV5`: ponovo proverava tipizirani rezultat u recommendation servisu pre njegove upotrebe.

Nepodržan eksplicitan uslov ne sme nestati iz zahteva. Poslovni ishodi uključuju `QUERY_UNCLEAR`, `LANGUAGE_MISMATCH`, `NOT_MOVIE_REQUEST` i `UNSUPPORTED_REQUEST`. Nevalidan provider odgovor nije uspešan query samo zato što je sintaksno JSON.

Glumci, režiseri, atmosfera i teme mogu ostati u semantičkom opisu. Pozitivno pominjanje osobe je dozvoljeno i bez hard filtera; negativno isključivanje nije podržano. Neutralni naslov/osoba mogu biti prihvaćeni u oba režima, ali prirodni jezik semantičke namere treba da odgovara izabranom jeziku.

### RatingThresholdNormalizer.cs — normalizacija ocene

Otvori [TryNormalize](D:/Repozitorijum/CineKros/src/backend/CineKros.Api/RealProviders/RatingThresholdNormalizer.cs:6). Poziva ga parser validator pri pravljenju kanonskog praga.

- `five`: vrednost 1–5 ostaje na skali baze.
- `ten`: korisnička vrednost deli se sa dva.
- `unspecified`: 1–5 ostaje, a vrednost iznad 5 deli se sa dva.
- Operator ostaje `gte` ili strogi `gt`; normalizacija ne pretvara „veće od“ u „najmanje“.

Primer značenja, ne tvrdnja o novom live odgovoru: „ocena veća od 8“ daje kanonski prag 4 na skali 0–5 i uslov `average_rating > 4`. „Najmanje 8“ daje `>= 4`.

## 7. Recommendation servis bira putanju

Otvori [RealRecommendationService.cs — RecommendAsync i RecommendCoreAsync](D:/Repozitorijum/CineKros/src/backend/CineKros.Api/RealFlow/RealRecommendationService.cs:64).

- `RecommendAsync`: ulaz iz endpointa; mapira i proverava odabrani jezik.
- [RecommendCoreAsync](D:/Repozitorijum/CineKros/src/backend/CineKros.Api/RealFlow/RealRecommendationService.cs:98): poziva parser, ponovo validira rezultat i odlučuje koja se pretraga izvršava.
- Ako parser vrati alert, servis ne nastavlja pretragu.
- Ako je `semanticQuery == null`, poziva `SearchHardOnlyAsync` i potpuno preskače query embedding.
- Ako semantički tekst postoji, proverava readiness izabranog jezika, normalizuje SR tekst, poziva `EmbedQueryAsync`, pa jezički `SearchHybridAsync`.

U istom fajlu su mali adapteri:

- [GeminiQueryParserAdapter](D:/Repozitorijum/CineKros/src/backend/CineKros.Api/RealFlow/RealRecommendationService.cs:33): interfejs parsera povezuje sa Gemini implementacijom.
- [E5QueryEmbeddingAdapter](D:/Repozitorijum/CineKros/src/backend/CineKros.Api/RealFlow/RealRecommendationService.cs:38): `EmbedQueryAsync` delegira lokalnom `model.EmbedQuery`.
- [MovieSearchAdapter](D:/Repozitorijum/CineKros/src/backend/CineKros.Api/RealFlow/RealRecommendationService.cs:49): search interfejs delegira PostgreSQL repository-ju.

Servis na kraju odbija više od deset ili duplirane interne movie ID-eve. Tehničke greške parsera, encodera i baze klasifikuju se; detaljni exception-i ne prosleđuju se korisniku.

## 8. Lokalni E5 i srpska normalizacija

### SerbianLatinNormalizer.cs

Otvori [Normalize](D:/Repozitorijum/CineKros/src/shared/CineKros.TextNormalization/SerbianLatinNormalizer.cs:10).

Za SR semanticQuery radi Unicode NFC normalizaciju, sređivanje razmaka i determinističku ćirilica→latinica transliteraciju uz pravila očuvanja velikih/malih slova. Poziva se iz recommendation servisa pre E5. EN putanja ne radi srpsku transliteraciju.

### E5EmbeddingModel.cs

Otvori [EmbedQuery](D:/Repozitorijum/CineKros/src/embedding/CineKros.Embedding/E5EmbeddingModel.cs:85) i [RunBatch](D:/Repozitorijum/CineKros/src/embedding/CineKros.Embedding/E5EmbeddingModel.cs:164).

`EmbedQuery` formatira query i poziva inference za jedan tekst. `RunBatch` obavlja tokenizaciju/ONNX obradu i izračunavanje završnog vektora.

Aktivni ugovor definisan je u [EmbeddingProfileDescriptor.MultilingualE5Base](D:/Repozitorijum/CineKros/src/embedding/CineKros.Embedding/EmbeddingProfileDescriptor.cs:85):

- Lokalni `intfloat/multilingual-e5-base`, INT8 ONNX.
- Query prefiks `query: `; document prefiks `passage: `.
- Tokenizacija do 512 tokena.
- Masked-mean pooling i L2 normalizacija u float32.
- Izlaz: **768 brojeva** koji predstavljaju značenje teksta.

Model živi u backend procesu; nije poseban terminalski servis i ne zove Gemini. Učitava se pri real startup-u, koristi po potrebi, a ne učitava ponovo za svaki query. `e5-base-v2-int8-onnx-v1` u kodu je stariji profil, ne aktivni bilingual izbor.

## 9. Hard filteri — kako DTO postaje WHERE

Otvori [FilteredMovieRepository.cs — HardFilterSqlBuilder.Build](D:/Repozitorijum/CineKros/src/backend/CineKros.Api/Database/FilteredMovieRepository.cs:40).

Builder proverava kanonske filtere i pravi `WhereSql` sa tipiziranim parametrima. Imena/predikati dolaze iz dozvoljenog ugovora; korisnička poruka se ne lepi u SQL.

Pravila koja treba objasniti:

- `yearMin/yearMax` i `runtimeMin/runtimeMax`: inkluzivne granice (`>=`, `<=`). „Posle 2010“ parser može izraziti kao `yearMin=2011`, bez promene SQL ugovora.
- `genres.all`: svi traženi žanrovi; PostgreSQL operator `@>`.
- `genres.any`: dovoljan je jedan traženi žanr; operator `&&`.
- `ratingMin`: operator `>=` ili `>` prema `ratingOperator`.
- `originalLanguage`: tačno podudaranje kanonskog koda jezika filma.
- Različite kategorije kombinuju se sa **AND**. `NULL` ne zadovoljava aktivni uslov. Hard filteri se ne opuštaju ako nema dovoljno filmova.

Doslovni primeri predikata iz builder-a:

```sql
year >= @yearMin
genres @> @genresAll::text[]
genres && @genresAny::text[]
average_rating >= @ratingMin
original_language = @originalLanguage
```

Ovo su fragmenti, ne samostalni SQL upiti. `AddParameters` dodaje vrednosti u `NpgsqlCommand` odvojeno od teksta upita.

## 10. Gde su SQL, vektorsko sortiranje i Top-K

Glavni fajl: [MovieSearchRepository.cs](D:/Repozitorijum/CineKros/src/backend/CineKros.Api/Search/MovieSearchRepository.cs:95).

### Semantička/hibridna pretraga

Za aktuelni full runtime otvori **jezički overload** `SearchHybridAsync(hardFilters, queryVector, language, cancellationToken)` na redu 95. [ORDER BY je na redu 108](D:/Repozitorijum/CineKros/src/backend/CineKros.Api/Search/MovieSearchRepository.cs:108).

Metoda:

1. Proverava vektor i pravi filter fragment.
2. Otvara DB konekciju i proverava kompatibilnost izabranog jezičkog skupa.
3. Pravi `WITH eligible AS MATERIALIZED`: samo filmove koji zadovoljavaju hard filtere.
4. Spaja taj skup sa `movie_embeddings`.
5. Rangira po `<=>` cosine distance rastuće, zatim po `movie_lens_id ASC`.
6. Ograničava rezultat sa `LIMIT 10` i čita filmske redove.

Ilustracija jednog SR filtera, skraćena radi čitanja; `...` nisu izvršivi SQL:

```sql
WITH eligible AS MATERIALIZED (
  SELECT ... FROM movies
  WHERE year >= @yearMin
)
SELECT ... FROM eligible AS e
JOIN movie_embeddings AS me USING (movie_lens_id)
ORDER BY me.embedding_sr <=> @queryVector ASC,
         e.movie_lens_id ASC
LIMIT 10
```

[SearchLanguageColumn.For](D:/Repozitorijum/CineKros/src/backend/CineKros.Api/Search/SearchLanguage.cs:7) bira samo `me.embedding` za English ili `me.embedding_sr` za Serbian. To je zatvoreni enum/switch, ne korisnički SQL column name. Nema mešanja setova ni fallback-a.

Vektor se dodaje kao `new Vector(queryVector)` parametar, a [CreateDataSource](D:/Repozitorijum/CineKros/src/backend/CineKros.Api/Search/MovieSearchRepository.cs:66) uključuje `UseVector()` mapiranje.

**Rezultati nisu „prvih deset iz baze“:** semantička putanja vraća najbliže podobne vektore. Pretraga je exact, bez ANN/HNSW i bez dodatnog minimalnog similarity threshold-a. Zato Top-10 ne garantuje da je svaki film subjektivno relevantan. Pozitivan actor/director intent takođe ne garantuje exact person match.

Overload bez jezika na redu 73 je legacy EN putanja; za objašnjenje sadašnje aplikacije koristi overload na redu 95, čak i ako njegov stari XML komentar pominje POC.

### Hard-only pretraga

Otvori [SearchHardOnlyAsync](D:/Repozitorijum/CineKros/src/backend/CineKros.Api/Search/MovieSearchRepository.cs:179).

Primenjuje iste hard filtere, ali bez distance računanja. [Redosled na redu 192](D:/Repozitorijum/CineKros/src/backend/CineKros.Api/Search/MovieSearchRepository.cs:192):

```sql
ORDER BY rating_count DESC NULLS LAST,
         average_rating DESC NULLS LAST,
         movie_lens_id ASC
LIMIT 10
```

Broj ocena je prvi ključ, zatim prosečna ocena, pa stabilni ID. Bez semantičkog teksta nema E5 query poziva. Potpuno prazan query nije hard-only zahtev: mora postojati konkretan filter ili semantička namera.

## 11. Od DB redova do kartica na ekranu

Vrati se na [RealRecommendationEndpoint.HandleAsync — izlaz](D:/Repozitorijum/CineKros/src/backend/CineKros.Api/RealFlow/RealRecommendationEndpoint.cs:58).

Endpoint pretvara interne filmove u minimalni javni DTO:

- `title` i `year`.
- `imdbUrl`: pravi se iz cifrenog IMDb ID-a.
- `posterUrl`: bezbedna TMDB poster putanja ili `null`.
- `meta.count` i `meta.partial` uz `type: "movies"`.

[PosterUrl](D:/Repozitorijum/CineKros/src/backend/CineKros.Api/RealFlow/RealRecommendationEndpoint.cs:102) proverava putanju pre formiranja CDN URL-a. Interne katalogske identitete i embedding vektore API ne šalje na kartice.

- **0 filmova:** lokalizovani `NO_RESULTS`, ne izmišljeni sadržaj.
- **1–9 filmova:** `partial: true` i odgovarajuće obaveštenje.
- **10 filmova:** pun rezultat, `partial: false`.
- **Parser/business alert:** objašnjenje korisniku.
- **Tehnički failure:** sanitizovan error, bez tajni i exception detalja.

U [App.tsx — provera odgovora](D:/Repozitorijum/CineKros/src/frontend/src/App.tsx:31) React proverava status, oblik odgovora, URL-ove, duplikate i slaganje count/partial vrednosti pre prikaza.

[MovieCard](D:/Repozitorijum/CineKros/src/frontend/src/App.tsx:76) prikazuje poster ili fallback, naslov/godinu i otvara IMDb u novoj kartici. Prikaz/loading/alert su odgovornost frontenda; izbor filmova i rangiranje nisu.

## 12. Jedan stvarni DTO za praćenje kroz kod

Sačuvani slučaj **P11-01-en** iz [parser-results.json](D:/Repozitorijum/CineKros/Diplomski/evaluacija/data/parser-results.json) ima korisnički upit:

```text
Show me Comedy films released from 2000 through 2010.
```

Ispod je njegov stvarni `actualCanonicalJson`, samo formatiran za čitanje. Nije izmišljeni ni novi Gemini odgovor:

```json
{
  "type": "query",
  "query": {
    "hardFilters": {
      "yearMin": 2000,
      "yearMax": 2010,
      "runtimeMin": null,
      "runtimeMax": null,
      "genres": { "all": ["Comedy"], "any": null },
      "ratingMin": null,
      "originalLanguage": null,
      "ratingOperator": null
    },
    "semanticQuery": null
  },
  "alertCode": null,
  "languageCheck": "match"
}
```

Dok pokazuješ primer, prati tri stvari: validator je zadržao konkretne hard uslove; `semanticQuery: null` usmerava servis u hard-only granu; repository koristi stalni SQL redosled, ne E5. Kanonski parser DTO je interni rezultat obrade, a ne javni movies odgovor prikazan korisniku.

## 13. Šta nije deo obrade jednog HTTP upita

ETL, migrator, catalog/vector importer, translator, document generator i evaluator su **offline priprema ili provera**. Korisnički endpoint ih ne pokreće i ne uvozi katalog pri pretrazi.

Filmovi su već pripremljeni i document embeddings već sačuvani u PostgreSQL-u. Runtime kreira samo query embedding kada postoji semantički tekst. Prevodilački model nije query-time servis.

Za kasnije proučavanje celog solution-a: [slnx_README.md](D:/Repozitorijum/CineKros/slnx_README.md). Za detalje API projekta: [CineKros.Api/README.md](D:/Repozitorijum/CineKros/src/backend/CineKros.Api/README.md).

## 14. Završna provera pred razgovor

Bez gledanja u tekst pokušaj da ispratiš jedan zahtev i odgovoriš:

1. Gde React formira zahtev, a gde bira API origin?
2. Ko pri startup-u bira fake/real, povezuje implementacije i mapira endpoint?
3. Ko proverava korisnički ulaz, a ko Gemini JSON i tipizirani DTO?
4. Zašto Gemini nije recommender niti izvor izvršivog SQL-a?
5. Gde se normalizuju ocena i srpska ćirilica?
6. Kada se E5 poziva, a kada se potpuno preskače?
7. Gde se bira EN/SR kolona i proverava kompatibilnost skupa?
8. Kako SQL sprečava da film koji krši hard filter uđe u rangiranje?
9. Koji su redosled i tie-break za semantic i hard-only putanju?
10. Šta API šalje za 0, 1–9 i 10 filmova i šta React proverava?

Ako znaš taj sled, pokrivaš ključna pitanja iz oba originalna vodiča. Za konsultacije najpre otvori servis, potom repository; detalje parsera i encodera otvaraj kada razgovor dođe do njih.

Originalni izvori, ostavljeni netaknuti: [VODIC_KROZ_KOD.md](D:/Repozitorijum/CineKros/docs/VODIC_KROZ_KOD.md) i [ZA_KONSULTACIJE.md](D:/Repozitorijum/CineKros/docs/ZA_KONSULTACIJE.md).
