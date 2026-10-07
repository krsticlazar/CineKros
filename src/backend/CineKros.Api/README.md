# CineKros.Api

## Uloga i tok zahteva

API prima `POST /api/recommendations` sa JSON telom `{"language":"sr|en","message":"..."}`. Jezik određuje lokalizaciju odgovora; parser dobija tekst i jezik. Model ne bira filmove: backend proverava zahtev, primenjuje obavezne SQL filtere i, kada postoji semantički opis, lokalno E5 rangiranje.

U režimu `fake`, [Program.cs](Program.cs) mapira [RecommendationEndpoint.HandleAsync](RecommendationEndpoint.cs) i ubrizgava `FakeQueryParser`/`FakeMovieSearch` iz [Recommendations.cs](Recommendations.cs). U režimu `real`, isti program mapira [RealRecommendationEndpoint.HandleAsync](RealFlow/RealRecommendationEndpoint.cs), a [RecommendationStartup](Startup/RecommendationStartup.cs) registruje realne adaptere, validator, E5 model i repozitorijum. `CINEKROS_RECOMMENDATION_MODE` podrazumeva `fake` samo u Development okruženju; van njega mora biti podešen. Production zahteva `real`. Realni režim takođe zahteva `DATABASE_CONNECTION_STRING`, `GEMINI_API_KEY` i `CINEKROS_E5_MODEL_DIR` u procesu, kao i raspoložive prompt/schema i model artefakte.

```text
HTTP → ReadRequestAsync → GeminiRealQueryParser → RealParsedQueryValidator
     → RealRecommendationService → E5 embedding (ako postoji semanticQuery)
     → MovieSearchRepository / PostgreSQL → JSON odgovor
```

`ReadRequestAsync` najpre ograničava i proverava ulaz: samo JSON UTF-8, do 65.536 bajtova, tačno polja `language` i `message`, jezik `sr` ili `en`, poruka od 1 do 500 Unicode skalarnih vrednosti i smislen tekst. Validator zatim zahteva tačnu strukturu checklist odgovora, bez duplih ili nepoznatih polja, dozvoljene vrednosti i dosledan `status` (`present`, `absent`, `unsupported`). Nepoznat ili neispravan odgovor je `PARSER_INVALID_RESPONSE`; validno prepoznat, ali nepodržan obavezni uslov postaje `UNSUPPORTED_REQUEST`.

Pozitivni zahtev tipa „film sa glumcem X“ može doprineti `semanticQuery` i tako uticati na E5 rangiranje; glumac se time ne pretvara u garantovani filter. Isključujući ili drugi obavezni uslovi koje aplikacija ne može pouzdano da proveri moraju biti označeni kao `unsupported`, pa se zahtev odbija. SQL hard filteri u ovom kodu obuhvataju godine, trajanje, žanrove (svi/zajednički ili bilo koji), minimalnu ocenu i originalni jezik.

Ocena se normalizuje na MovieLens skalu 0–5 u [RatingThresholdNormalizer](RealProviders/RatingThresholdNormalizer.cs): ulazna skala `five` prihvata 1–5; `ten` deli vrednost sa dva; `unspecified` zadržava 1–5, a vrednosti iznad 5 deli sa dva. Ovaj normalizator prihvata ulaznu vrednost od 1 do 10; eksplicitno `1/10` zato postaje `0.5`. Operator `gte` koristi `>=`, a `gt` koristi strogo `>` u SQL-u. Ako nema semantičkog opisa, realni servis koristi hard-only pretragu koja zahteva bar jedan aktivan filter; inače prvo pravi E5 vektor, a pretraga rangira samo filmove koji su prošli sve hard filtere. Semantičko rangiranje koristi cosine rastojanje (`<=>`) i ID filma kao tie-break; hard-only putanja sortira po broju ocena opadajuće, prosečnoj oceni opadajuće (null poslednji), pa ID-u rastuće. Obe putanje vraćaju najviše deset filmova.

Odgovor je `movies` sa karticama i metapodacima ili `alert` sa lokalizovanom porukom. Greške parsera/provajdera/pretrage imaju tehnički kod. Ograničenje je 30 zahteva u minutu po IP adresi. CORS pravilo iz [ProductionHttpConfiguration.cs](Startup/ProductionHttpConfiguration.cs) prihvata eksplicitno podešene origin-e; HTTPS je dozvoljen, a HTTP samo za loopback u Development-u.

## Izvorni fajlovi

Svaka stavka navodi značajne funkcije, njihovu ulogu i neposredne pozive/pozivaoce u API toku.

### 1. [Program.cs](Program.cs)

Top-level startup kod nema imenovane funkcije. Poziva `RecommendationStartup.ResolveMode` i `RegisterServices`, `ProductionHttpConfiguration.AddRecommendationCors`, zatim podešava limit od 30 zahteva u minuti po IP adresi. U real režimu unapred razrešava `E5EmbeddingModel`; potom mapira `RealRecommendationEndpoint.HandleAsync` ili `RecommendationEndpoint.HandleAsync` na `POST /api/recommendations`.

### 2. [RecommendationEndpoint.cs](RecommendationEndpoint.cs)

Fake HTTP ulazna tačka i zajedničke funkcije za čitanje zahteva i oblikovanje odgovora. `HandleAsync` poziva `ReadRequestAsync`, parser `IQueryParser.ParseAsync`, lokalno proverava rezultat kroz `ValidateParserResult`, poziva `IMovieSearch.SearchAsync` i vraća kartice ili grešku. U `finally` poziva `DevelopmentRequestSummary.LogCompletion`. Realni [RealRecommendationEndpoint](RealFlow/RealRecommendationEndpoint.cs) koristi iste `ReadRequestAsync`, `Alert` i `Technical` funkcije.

- `ReadRequestAsync` ograničava telo, proverava JSON UTF-8 i dozvoljena polja, jezik i smislenost poruke; poziva `HasAcceptedContentType`, `TrimMessage` i `Usable`.
- `HasAcceptedContentType` prihvata samo JSON sa opcionim UTF-8 charset-om. `TrimMessage` proverava Unicode skalarne vrednosti i granice dužine uz `IsWhitespace`; `Usable` odbacuje prekratak ili očigledno ponavljan tekst.
- `ValidateParserResult` proverava fake parserov alert/query oblik, filtere, žanrove i semantički tekst; `IsDigits` proverava IMDb cifre, a `NormalizePoster` dozvoljava samo odgovarajuće HTTPS TMDB putanje.
- `Alert` pravi lokalizovan korisnički odgovor, a `Technical` tehnički odgovor sa HTTP statusom iz mape kodova. Pozivaju ih oba endpoint-a.

### 3. [Recommendations.cs](Recommendations.cs)

Sadrži fake režim ugovore i podatke: `IQueryParser`/`ParserInput`/`ParserResult`/`ParsedQuery`/`HardFilters`, `IMovieSearch`/`SearchCandidate` i `MovieQueryPrompt`. Ugovore poziva `RecommendationEndpoint.HandleAsync`; prompt tekst registruje `RecommendationStartup.RegisterServices`. DTO-i su samo podaci i nemaju funkcije.

- `FakeQueryParser.ParseAsync` bira unapred zadate demo scenarije i poziva privatne `Query` ili `Alert` pomoćne funkcije da napravi rezultat; ne poziva spoljnog provajdera.
- `FakeMovieSearch.SearchAsync` pravi sintetičke kandidate, umeće decoy zapise za demo slučaj i primenjuje fake hard filtere. Poziva ga fake endpoint preko `IMovieSearch`.
- `ApiErrorCodes` su konstante korišćene u endpoint-ima i rate-limit odgovoru u [Program.cs](Program.cs); nema izvršne funkcije.

### 4. [Startup/RecommendationStartup.cs](Startup/RecommendationStartup.cs)

- `ResolveMode` čita `CINEKROS_RECOMMENDATION_MODE`, primenjuje Development podrazumevani `fake` i zahteva `real` u Production-u; poziva ga [Program.cs](Program.cs).
- `RegisterServices` registruje fake parser/pretragu ili poziva `RegisterRealServices`; takođe ga poziva `Program.cs`.
- `RegisterRealServices` proverava obavezne promenljive okruženja i prompt/schema fajlove, pa povezuje Gemini, validator, E5, Npgsql i realne adaptere. Registruje `MovieSearchRepository.CreateDataSource` kao izvor podataka.
- `LoadE5Model` učitava model i proverava očekivani profil; koristi ga realna registracija kada se model servis razrešava.

### 5. [Startup/ProductionHttpConfiguration.cs](Startup/ProductionHttpConfiguration.cs)

- `AddRecommendationCors` poziva `ParseAllowedOrigins` i registruje CORS politiku za `POST` i `Content-Type`; poziva je [Program.cs](Program.cs).
- `ParseAllowedOrigins` deli, validira, ograničava i normalizuje origin-e (HTTPS, odnosno loopback HTTP samo u Development-u); pri nevažećoj vrednosti poziva `InvalidOrigins`.
- `InvalidOrigins` pravi konfiguracioni izuzetak; privatna je pomoćna funkcija validatora origin-a.

### 6. [RealFlow/RealRecommendationEndpoint.cs](RealFlow/RealRecommendationEndpoint.cs)

- `HandleAsync` je realna HTTP ulazna tačka. Koristi zajednički `RecommendationEndpoint.ReadRequestAsync`, poziva `RealRecommendationService.RecommendAsync`, a odgovore/greške prosleđuje u `RecommendationEndpoint.Alert` ili `Technical`. U završetku poziva dijagnostiku `DevelopmentRequestSummary`.
- Za uspeh pravi kartice; pre toga proverava jedinstvenost ID-a i naslov, poziva `IsImdbDigits` za IMDb identifikator i `PosterUrl` za bezbednu TMDB putanju.
- `AllowedTechnicalCode` ograničava javno vraćene tehničke kodove; `IsImdbDigits` odbacuje ne-cifrene IMDb ID-jeve; `PosterUrl` odbacuje nebezbedne putanje i formira HTTPS TMDB URL. Sve tri su privatni pomoćnici koje poziva `HandleAsync`.

### 7. [RealFlow/RealRecommendationService.cs](RealFlow/RealRecommendationService.cs)

Definiše `IRealQueryParser`, `IRealQueryEmbeddingProvider` i `IRealMovieSearch`, koje servis koristi kao granice prema provider/search slojevima.

- `GeminiQueryParserAdapter.ParseAsync` delegira `IRealQueryParser` poziv u `GeminiRealQueryParser.ParseAsync`; registruje ga `RecommendationStartup`.
- `E5QueryEmbeddingAdapter.EmbedQueryAsync` poziva `E5EmbeddingModel.EmbedQuery` i prevodi greške u `RealProviderException`; servis ga poziva samo kada postoji semantički upit.
- `MovieSearchAdapter.SearchHybridAsync` i `SearchHardOnlyAsync` delegiraju odgovarajućoj metodi `MovieSearchRepository`; servis ih poziva nakon validacije upita.
- `RealRecommendationService.RecommendAsync` poziva parser, `RealParsedQueryValidator.ValidateResult`, a zatim hard-only pretragu ili E5 embedding pa hibridnu pretragu. Prevodi greške granica u kodove provajdera/pretrage i proverava limit i jedinstvenost rezultata pre nego što vrati `RealRecommendationResult` endpoint-u.

### 8. [RealProviders/GeminiRealQueryParser.cs](RealProviders/GeminiRealQueryParser.cs)

- `ParseAsync` vraća samo rezultat iz `ParseWithEvidenceAsync`; servis ga koristi preko `GeminiQueryParserAdapter`.
- `ParseWithEvidenceAsync` formira Gemini strukturisani HTTP zahtev sa desetosekundnim timeout-om, poziva `ExtractText`, zatim `RealParsedQueryValidator.Validate`. Vraća validiran rezultat i checklist evidenciju za offline evaluaciju; u Development-u zapisuje validiranu evidenciju i sanitizovan uzrok greške.
- `ExtractText` proverava očekivani oblik Gemini JSON odgovora i izvlači jedini tekstualni deo; poziva ga `ParseWithEvidenceAsync`.
- `LogDevelopmentFailure` zapisuje sanitizovan uzrok samo u Development-u; poziva se iz grana grešaka parsera.

### 9. [RealProviders/RealParsedQueryValidator.cs](RealProviders/RealParsedQueryValidator.cs)

- `Validate(json)` delegira overload-u `Validate(json, out evidence)`. Ovaj drugi proverava checklist JSON, sva poznata polja i njihove statuse, mapira ih u `RealParserResult` i vraća evidenciju; poziva ga Gemini adapter.
- `ValidateResult` ponovo proverava tipiziran rezultat na servisnoj granici, uključujući alert kodove, dozvoljene granice/filtere, semantički tekst i nepostojanje praznog zahteva; poziva ga `RealRecommendationService.RecommendAsync`.
- `HasActiveFilter` određuje da li postoji primenljiv hard filter; koristi ga `Validate` da odbije zahtev bez semantičkog opisa i bez hard uslova.
- `ReadBounds`, `ReadGenres`, `ReadRating` i `ReadScalar` čitaju pojedinačne checklist grupe, proveravaju status i vrednosti, a `ReadGenreList` proverava listu žanrova. Pozivaju ih `Validate`.
- `ReadNullableInt`, `ReadNullableDecimal`, `ReadNullableString` i `ReadSemantic` proveravaju tip i opseg konkretnih vrednosti; pozivaju ih čitači polja.
- `ReadStatus`, `ValidateStatus`, `RequiredString`, `Required`, `ReadFields` i `RequireOnly` obezbeđuju obavezna polja, dozvoljene nazive bez duplikata i doslednost status/vrednost; pozivaju ih čitači checklist-e i `Validate`.
- `Invalid` i `InvalidException` završavaju neispravan ulaz sa `PARSER_INVALID_RESPONSE`; pozivaju ih validacioni pomoćnici.

### 10. [RealProviders/RatingThresholdNormalizer.cs](RealProviders/RatingThresholdNormalizer.cs)

`TryNormalize` proverava ocenu 1–10 i skalu (`five`, `ten`, `unspecified`), pa vraća MovieLens prag 0–5 ili neuspeh. Poziva ga `RealParsedQueryValidator` dok čita rating checklist polje.

### 11. [RealProviders/RealQueryModels.cs](RealProviders/RealQueryModels.cs)

`RealHardFilters`, `RealGenreFilter`, `RealParsedQuery` i `RealParserResult` nose canonical filtere, semantički upit i rezultat parsera; koriste ih validator, servis, SQL builder i endpoint. `RealProviderException` nosi tehnički kod greške koji obrađuju servis i realni endpoint. Ovo su modeli/podaci, bez sopstvenih poslovnih funkcija.

### 12. [Database/FilteredMovieRepository.cs](Database/FilteredMovieRepository.cs)

- `HardFilterSqlBuilder.Build` proverava filtere pozivom `Validate`, gradi allowlist SQL predikate i tipizirane parametre. Pozivaju ga obe pretrage u [MovieSearchRepository](Search/MovieSearchRepository.cs).
- `Validate` proverava opsege, operator ocene, jezik i strukturu žanrova; `ValidateGenres` odbacuje prazne, nepoznate ili duplirane žanrove.
- Preopterećeni `Add` pomoćnici dodaju samo prisutne numeričke/string filtere i odgovarajuće SQL parametre. `Invalid` odbacuje nevažeće filtere.
- `FilteredMovieQuery.AddParameters` dodaje parametre komandi; pozivaju ga SQL izvršioci. `FilteredMovieRepository.ReadAsync` zasebno izvršava ne-rangirani filtrirani SELECT koristeći `Build` i `AddParameters`; trenutno nije registrovan/pozvan u realnom toku, gde rangiranje obavlja `MovieSearchRepository`.

### 13. [Search/MovieSearchRepository.cs](Search/MovieSearchRepository.cs)

- Konstruktor proverava izvor podataka i očekivani fingerprint embedding profila; instancu registruje `RecommendationStartup`.
- `CreateDataSource` uključuje pgvector mapiranje; poziva ga `RecommendationStartup.RegisterRealServices`.
- `SearchHybridAsync` poziva `HardFilterSqlBuilder.Build`, proverava vektor, katalog i embedding stanje, pa SQL-om rangira samo hard-filter kandidate cosine rastojanjem i vraća do deset filmova. Poziva ga `MovieSearchAdapter.SearchHybridAsync`.
- `SearchHardOnlyAsync` zahteva bar jedan hard filter, proverava katalog i vraća filmove po broju ocena, prosečnoj oceni i ID-u; poziva ga `MovieSearchAdapter.SearchHardOnlyAsync`.
- `EnsureCatalogReadyAsync` proverava identitet/verziju kataloga i, za semantičku putanju, potpunost/profil embedding-a. Obe pretrage je pozivaju.
- `ReadMoviesAsync` mapira redove iz Npgsql čitača u `FilteredMovie`; pozivaju je obe pretrage. `ValidateQueryVector` zahteva 768 konačnih komponenti i nenulti norm; poziva ga `SearchHybridAsync`. `SearchUnavailable` pravi standardizovani izuzetak za stanje kataloga; pozivaju je `EnsureCatalogReadyAsync` grane.

### 14. [Diagnostics/DevelopmentRequestSummary.cs](Diagnostics/DevelopmentRequestSummary.cs)

- `Format` serijalizuje validirani parser DTO i sastavlja razvojni sažetak; poziva ga `WriteIfDevelopment`.
- `WriteIfDevelopment` i `WriteFailureIfDevelopment` ispisuju upit/validirani DTO, odnosno objašnjenje da DTO nije dostupan, samo u Development-u; poziva ih `RealRecommendationEndpoint.HandleAsync`.
- `LogCompletion` beleži korelacioni ID, verziju ugovora, trajanje i kod; pozivaju ga fake i realni endpoint pri završetku zahteva. Gemini adapter zasebno beleži sanitizovane uzroke grešaka u Development-u.

## Kratko objašnjenje

> „API proverava zahtev, koristi Gemini da strukturira uslove i odbija one koje ne može pouzdano da proveri. Zatim sam primenjuje SQL uslove i po potrebi lokalno E5 rangiranje; Gemini ne bira filmove.“
