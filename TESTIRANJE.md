# Lokalno pokretanje i testiranje CineKros-a

Ovaj dokument obuhvata dve odvojene lokalne putanje. Standardni launcher pokreće
puni produkcioni skup podataka u režimu za lokalni razvoj; namenska POC putanja
ostaje vezana isključivo za svoj zaseban testni katalog. Nijedna putanja ne
menja bazu, ne pokreće migracije ni uvoz.

## Standardna putanja: puni dvojezični katalog

Pokretanje iz korena repozitorijuma:

```cmd
src\start_script.cmd
```

Launcher učitava `CINEKROS_POSTGRES_PASSWORD` i `GEMINI_API_KEY` iz procesnog
okruženja ili lokalnog root `.env` fajla (ako promenljiva već nije postavljena).
Vrednosti tajni ne upisivati u ovaj dokument, argumente komande ili logove.
Potreban je Docker Desktop sa dostupnim Docker CLI i Compose dodatkom. U realnom
režimu launcher formira `DATABASE_CONNECTION_STRING` za lokalnu bazu `cinekros`
sa podrazumevanim transakcijama samo za čitanje. `CINEKROS_RECOMMENDATION_MODE`
može biti `real` (podrazumevano) ili `fake`; realni režim zahteva dostupan
Gemini ključ, dok `fake` služi samo za razvoj UI-ja. Standardni launcher uvek
bira `CINEKROS_SERBIAN_POC=false`, pa realni režim koristi puni dvojezični
katalog, a ne POC skup.

Realni režim očekuje lokalni model na:

```text
database\data\models\multilingual-e5-base\d128750597153bb5987e10b1c3493a34e5a4502a
```

To je `multilingual-e5-base-int8-onnx-v1` profil, fingerprint
`eac906ed78f7863573d13c9b0435de1b8f848fe92fc6ae08aa3621400260b1fe`, sa
768-dimenzionalnim vektorima. Puni release je katalog
`B05a-bilingual-full-catalog-v1`, 9.730 filmova, sa zaključanim skupom ID-jeva
`af7b28f5c43cc219a9d5f62bd22716015a340f005ee9098f11f4301e13d7bac2`. Aplikacija
mora proveriti spremnost oba jezika pre nego što prihvati zahteve. Ako odredišna
baza, katalog ili skup vektora ne odgovara zaključanom release-u, pokretanje
mora stati; nema automatskog prelaska na POC ili drugi skup podataka.

Pri uspešnom pokretanju launcher otvara backend na `http://127.0.0.1:5179`,
frontend na `http://localhost:5173` i podrazumevani pregledač. Backend koristi
postojeći Gemini parser, zato realni upiti zahtevaju dostupnu GEMINI_API_KEY;
hard-only upiti ne zahtevaju query embedding. Režim `fake` je samo lokalni
razvojni režim i ne proverava realnu pretragu.

Potvrđeni su stvarni start kroz standardni launcher, Gemini V5 API upiti,
EN/SR pretraga, ćirilični UI upit, zaseban disposable clone za negativne
readiness testove i read-only benchmark pune baze. Završni provider-free start
trajao je 18,35 s. API root vraća 404, a GET `/api/recommendations` 405 — to je
očekivano, jer aplikacija nema root stranicu, a preporuke zahtevaju POST.
Frontend vraća HTTP 200. Konačne provere su zaustavile samo svoje procese;
pre ručnog korišćenja ponovo pokreni gornju skriptu.

### Provera baze u DBeaver-u

Koristi lokalnu PostgreSQL konekciju: host `127.0.0.1`, port `5433`, baza
`cinekros`, korisnik `cinekros`; lozinku učitaj iz lokalne konfiguracije.
Pokreni samo SELECT upite. Očekivani broj filmova i oba skupa vektora je 9.730:

```sql
SELECT current_database(), current_schema();

SELECT count(*) AS movies FROM movies;

SELECT count(*) AS embedding_rows,
       count(*) FILTER (WHERE embedding IS NOT NULL) AS en_vectors,
       count(*) FILTER (WHERE embedding_sr IS NOT NULL) AS sr_vectors
FROM movie_embeddings;

SELECT catalog_version, movie_count, embedded_count,
       embedding_profile_fingerprint, embedding_artifact_sha256
FROM catalog_import_state WHERE id = 1;

SELECT language, profile_fingerprint, corpus_sha256, artifact_sha256,
       dimension, embedded_count
FROM embedding_set_state ORDER BY language;
```

Treba da piše `cinekros` i `public`, katalog
`B05a-bilingual-full-catalog-v1`, profil sa gore navedenim fingerprintom,
dimenzija `768` i statusi `en`/`sr` sa po 9.730 zapisa. Ako broj ili release
identitet odstupa, zaustavi launcher i proveri lokalni Phase 10 handoff; ne
menjaj podatke baze.

### Primeri upita i ograničenja

U postojećem biraču izaberi EN za engleski upit ili SR za srpski upit na
latinici ili ćirilici. Taj izbor određuje jezik upita i kolonu vektora; nije
filter za originalni jezik filma.

- EN: `A quiet mystery after 2000, under two hours, with Drama or Thriller.`
- SR latinica: `Mirna misterija posle 2000. godine, do dva sata, sa Brad Pittom.`
- SR ćirilica: `Мирна мистерија после 2000. године, до два сата, са Brad Pittom.`

Pouzdani strukturisani uslovi obuhvataju godine, trajanje, tačne žanrove,
minimalnu ocenu i originalni jezik filma (`yearMin`/`yearMax`,
`runtimeMin`/`runtimeMax`, `genres.all`/`genres.any`, `ratingMin` i
`originalLanguage`). Ime glumca ili reditelja u pozitivnom upitu opisuje
semantički zahtev. Aplikacija ne može pouzdano da garantuje
isključivanje osobe, scene, teme ili drugog uslova koji nije u podržanom skupu;
takav zahtev može vratiti `UNSUPPORTED_REQUEST`. Ne ublažavaj uslove da bi se
dopunilo deset rezultata: odgovori sa 1–9 filmova su označeni kao delimični,
a nula rezultata vraća lokalizovanu poruku. Poster može nedostajati ako ga
katalog nema; IMDb veza koristi IMDb identifikator filma.

### Rešavanje problema i gašenje

- Ako Docker nije spreman, proveri da li Docker Desktop radi i da li `docker
  compose` može da pristupi engine-u.
- Ako launcher prijavi nedostajući Gemini ključ ili lozinku, proveri nazive
  ključeva u root `.env`; ne kopiraj njihove vrednosti u terminal ili log.
- Ako se prijavi pogrešna baza, model ili readiness, proveri DBeaver rezultate
  i putanju zaključanog modela. Ne prebacuj se na POC launcher kao fallback.
- Za gašenje zaustavi frontend i backend u njihovim terminalima (`Ctrl+C`).
  PostgreSQL može ostati pokrenut; za njegovo zaustavljanje koristi Docker CLI:

```cmd
docker compose -f database\docker\compose.yaml stop cinekros-postgres
```

## Posebna POC putanja: 150 filmova

Pokretanje POC-a:

```cmd
src\start_poc.cmd
```

Ova putanja je eksplicitno vezana za bazu
`cinekros_sr_poc_phase06t_v2_20261009` i ispravljeni dvojezični katalog od 150
filmova. Ona nije alias za punu bazu, ne pretražuje druge baze i ne prebacuje se
na njih ako provera ne uspe. Pokretanje `src\start_script.cmd` i
`src\start_poc.cmd` je međusobno isključivo po ciljnom skupu podataka; zatvoriti
prethodne lokalne procese pre prelaska na drugu putanju.

POC launcher proverava zaključane fajlove modela, katalog, identitet, oba jezička
skupa vektora, prevodilački rečnik, dimenziju 768 i svih 150 zapisa, pa tek onda
pokreće lokalne procese. Backend je na `http://127.0.0.1:5179`, frontend na
`http://localhost:5173`. `src\start_poc.cmd -CheckOnly` radi samo preflight i
ne pokreće aplikaciju. Ne pokretati preflight dok su očekivani portovi zauzeti;
launcher zatvara se bez fallback-a ako stanje ne odgovara tačno POC release-u.

POC release koristi isti zaključani model revision
`d128750597153bb5987e10b1c3493a34e5a4502a` i multilingual INT8 profil. Katalog
SHA-256 je `2887a937689294b029dd918911dd10151bfd3ece74fa573fcc5d85dc2f86886e`,
identitet `0fd18234a4ee0c4f8e6054a9143935ef5dfc3669494e4b6f1637b6be105f8d66`.
EN korpus je
`5c5760576471547ea4d6db17acfd254ff1a0d4e7c47fe54b0f4db6a9795fdde0`, SR
korpus `fc26b53581e6adc795573df511b7e7f72c7fd59fe7e31efddb29e0ebf9d7e8c0`;
vektorski artefakti su redom
`5ccad923ad7156bc10ee27d0b5b56163d0af05abf52e9bbd0b4df6cd715e6a81` i
`228493dfeabf83dd5b3d5b0b9cf6c445ebc48742e9cf8f810bc8517959d7baba`, a SR
rečnik `09bd0afbde2b7b8a0afee502d7718f8dad6cc6320c226b437074ebb4b341ea4e`.

MAIN je prethodno verifikovao normalan POC start, lokalni EN i SR API smoke
upit, i read-only spremnost tačnog POC skupa. To je dokaz samo za zaseban POC
release, ne za punih 9.730 filmova niti za kvalitet preporuka. Formalni kvalitet
gate je ostao 3/7 naspram cilja 6/7; start uspeh ga ne menja.

## Testovi

API testove pokretati iz korena repozitorijuma, na primer:

```powershell
dotnet test tests/CineKros.Api.Tests/CineKros.Api.Tests.csproj --no-restore --filter FullyQualifiedName~FullBilingualRuntimeTests
dotnet test tests/CineKros.Api.Tests/CineKros.Api.Tests.csproj --no-restore
```

Šira backend suite koristi samo svoj izolovani disposable Docker kontejner za
SQL integraciju. POC provere i Gemini/provajder smoke testovi nisu uključeni u
uobičajeni run. Produkcioni read-only benchmark i clone negativni readiness
test podrazumevano su isključeni i zahtevaju zasebno odobrenje MAIN-a; ne
postavljaj njihove procesne promenljive ili konekcije ručno.

Benchmark zabeležen 2026-10-10 koristio je zaključani multilingual E5 model i
read-only konekciju na `cinekros`. Model se učitao za 2.495 ms, a EN+SR runtime
readiness provera trajala je 756 ms. Od 30 kratkih upita po jeziku, svaki je
vratio 10 filmova. EN embedding p50/p95 iznosio je 10.85/12.56 ms, SR
11.23/12.27 ms. Repozitorijumski roundtrip p50/p95 bio je EN 444.70/507.32 ms,
SR 436.90/529.84 ms. Ta merenja obuhvataju ponovnu readiness proveru i SQL
roundtrip zajedno; nisu izolovano vreme server-side SQL-a. Model nije pravio
vektore filmova i benchmark nije zvao Gemini.

## Provereni ručni primeri za punu bazu — 10. oktobar 2026.

Izaberi odgovarajući EN/SR režim pre slanja. Brojevi rezultata su opažanja ovog
smoke-a, a ne obećanje da će Gemini svaki budući put dati identičnu parafrazu.

| Režim | Tekst za kopiranje | Provereno ponašanje |
| --- | --- | --- |
| EN | `A wistful, dreamlike film.` | Semantička pretraga, 10 filmova. |
| SR | `Želim setan film sa snolikom atmosferom.` | SR semantička pretraga, 10 filmova. |
| SR | `Желим сетан филм са сноликом атмосфером.` | Ćirilica normalizovana; SR vektori; 10 kartica, raspored 5 + 5. |
| EN | `Dark science fiction movies released after 2010, at most 120 minutes long.` | Sci-Fi, godina najmanje 2011, trajanje do 120, semantičko rangiranje. |
| SR | `SF filmovi sa Brad Pittom pre 2009. godine.` | Sci-Fi i godina najviše 2008; ime ostaje semantički zahtev, ne exact cast filter. |
| SR | `SF filmovi sa ocenom većom od 8.` | Normalizacija 8/10 na 4/5 i strogi `gt`; hard-only. |
| EN | `Animation movies released in 2013.` | Pet filmova i poruka za delimični rezultat; Enter šalje, New search vraća unos. |
| EN | `Movies released from 2099 to 2100.` | API vraća NO_RESULTS; nema ublažavanja uslova. |
| EN | `film without Brad Pitt` | UNSUPPORTED_REQUEST, bez kartica. |
| EN | `Hoću setan film sa snolikom atmosferom.` | LANGUAGE_MISMATCH. |

IMDb linkovi otvaraju novi tab. Poster i naslov/godina na focus-u su provereni.
Zero UI prikaz je pokriven frontend testovima i stvarnim API NO_RESULTS odgovorom;
nije dodatno trošen Gemini poziv samo za snimanje tog stanja u browser-u.

### Prethodni nalaz i završna provera — 10. oktobar 2026.

U prvom pokušaju zamrznuti EN primer `movie` dobio je NOT_MOVIE_REQUEST umesto
očekivanog QUERY_UNCLEAR: istorijski rezultat je bio 13/14. Opšte V5 instrukcije
su potom razjasnile razliku između neodređenog filmskog i nefilmskog zahteva,
bez lokalnog prepoznavanja ključnih reči. Nova ciljana provera prošla je 8/8,
a kompletan neizmenjeni 14-case suite, jednom nakon nje, prošao je 14/14.
Phase 10 runtime acceptance je sada PASS. Dodatna ranija dijagnostika otkrila je da provider ponekad
vrati kontrolnu šifru `QUERY_UNCLEAR` kao semanticQuery. Validator sada takav
neispravan odgovor odbija sa PARSER_INVALID_RESPONSE pre embeddinga i pretrage;
kontrolni-marker popravka je proverena offline regresijom i ostala je aktivna.
Završna recovery izmena menja samo opšte instrukcije za izbor alert grane.
Ovaj nalaz nije razlog za ponovno generisanje vektora ili promenu SQL-a.
Istorijska formalna POC provera kvaliteta ostaje 3/7; runtime smoke nije njena zamena.

Možeš dodatno probati: EN `movie`, SR `film` ili SR `Филм` treba da vrate
QUERY_UNCLEAR; EN `What is tomorrow's weather?` i SR `Kakvo je vreme sutra?`
NOT_MOVIE_REQUEST. EN `Fight Club` i SR `Brad Pitt` su validni neutralni
semantički upiti bez obaveznog hard filtera. Ne garantuju exact actor/title
matching ni subjektivnu relevantnost svih deset rezultata. Phase 11 nije pokrenuta.
