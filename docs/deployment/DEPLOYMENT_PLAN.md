# CineKros V6 — priprema javnog deploymenta na Debian server

## Status i način korišćenja

Ovo je uputstvo za budući rad Lazara na fizičkom serveru, a ne evidencija izvršenog deploymenta. Priprema je obavljena na Windows razvojnoj mašini. Nije bilo SSH pristupa, kreiranja Cloudflare resursa, DNS izmena, importa ili migracija lokalnih baza, niti Gemini poziva. Komande za Linux u ovom dokumentu NISU izvršene na ciljnom serveru.

Oznake: **READY** — pripremljeno/provereno lokalno; **NOT MEASURED** — nema merenja; **ZA TESTIRANJE NA SERVERU** — potrebna fizička Debian mašina; **PENDING ACCEPTANCE** — potrebna odluka nakon numeričkog merenja; **BLOCKED** — konkretna prepreka, ne razlog za zaobilaženje zaštita.

Početni Git HEAD: `526eede05ce4a3853490a08a0723097c95dcbc8b`, početno radno stablo čisto. Sačuvano je lokalno pokretanje V6 preko `src/start_script.cmd`, Development profil `actor-v6-isolated` i tačan naziv njegove rehearsal baze. Nova konfiguracija za server je odvojena; lokalna V5 baza `cinekros` NIJE server-ready V6 baza samo zato što ima isti naziv.

## 1. Pregled arhitekture

```text
Browser → Cloudflare Pages (statički React frontend)
        → HTTPS API poddomen → Cloudflare Tunnel
        → ASP.NET Core .NET 10 API
             ├─ PostgreSQL 17 + pgvector 0.8.6 na istom serveru
             ├─ multilingual E5 INT8, lokalno u ISTOM API procesu
             └─ udaljeni Google Gemini API
```

Gemini V6 tumači korisnički zahtev, ne bira filmove. Backend validira DTO, kanonizuje glumce i primenjuje ALL/ANY/exclude hard filtere. SQL prvo ograničava kandidate, a za semantičke/hibridne upite pgvector radi exact cosine sortiranje po odgovarajućoj EN ili SR koloni i ID tie-breaku, uz najviše 10 filmova. Nema ANN indeksa, mešanja jezika ni fallback-a na drugi vektorski skup. SR ćirilica se deterministički normalizuje pre query inference-a.

Server: MacBookPro9,2, i5-3210M, 2 jezgra/4 niti, AVX bez AVX2, 4 GB RAM (planirano 6 GB), HDD, Debian 13 Trixie x86_64/glibc, kernel 6.12. Hardver nije predmet novog izbora. Linux build i naziv model fajla nisu dokaz CPU kompatibilnosti. GPU nije deo ove arhitekture.

Frontend ne zahteva Vite/Node server u produkciji. Node 24 je potreban samo za lokalni build. Runtime su API, PostgreSQL i cloudflared; ne postoji poseban inference servis. Compose je preliminarno preferiran, ali izbor između Compose API-ja i native API-ja sa systemd ostaje korisnikov.

## 2. Komponente i konfiguracija

| Komponenta | Zaključana osnova | Gde radi |
|---|---|---|
| Frontend | React 19.3.0, TypeScript 6.0.2, Vite 8.3.0; Node >=24 <25 za build | Statički Pages dist |
| API | net10.0; SDK 10.0.401; runtime instaliran iz zvaničnog Microsoft feed-a | Debian x64, jedan proces |
| Gemini | `gemini-3.1-flash-lite`, postojeći V6 prompt/schema | Google API |
| Embedding | `intfloat/multilingual-e5-base`, INT8, 768D | Unutar API procesa, CPU |
| Native biblioteke | Microsoft.ML.OnnxRuntime 1.29.0; Tokenizers.DotNet NuGet 1.4.1 | Linux x64/glibc; CPU provera obavezna |
| DB klijenti | Npgsql 10.0.3; Pgvector .NET 0.3.2 | API |
| Baza | `pgvector/pgvector:0.8.6-pg17-bookworm` | Lokalni PostgreSQL, named volume |
| Tunel | `cloudflare/cloudflared:2026.10.0` u nacrtu | Izlazne konekcije ka Cloudflare-u |

Tokenizers NuGet paket 1.4.1 u ovom zaključanom paketu ima managed assembly verziju 1.4.2.0. To samo po sebi nije promena NuGet zavisnosti. Proveravati paket i native biblioteku, ne prepisivati broj na osnovu assembly prikaza.

| Promenljiva | Namena / mesto | Vreme | Tajna | Bezbedno podešavanje |
|---|---|---|---|---|
| `VITE_API_BASE_URL` | Tačan API HTTPS origin u JS bundle-u | Build-time | NE | Lokalni build, ASCII origin bez `/api` putanje |
| `ASPNETCORE_ENVIRONMENT` | `Production` za novi server profil | Runtime | NE | `api.env` |
| `ASPNETCORE_HTTP_PORTS` | Compose listener 8080 | Runtime | NE | Compose/template |
| `ASPNETCORE_URLS` | Native `http://127.0.0.1:8080` | Runtime | NE | systemd unit, bez LAN bind-a |
| `CINEKROS_RECOMMENDATION_MODE` | Isključivo `real` | Runtime | NE | `api.env` |
| `CINEKROS_RUNTIME_PROFILE` | Isključivo `actor-v6-production` na serveru | Runtime | NE | Obavezan eksplicitan izbor; entrypoint odbija izostavljanje |
| `CINEKROS_SERBIAN_POC` | Ne postavljati ili `false`; nikad `true` za server | Runtime | NE | Ne prenositi Development env |
| `CINEKROS_E5_MODEL_DIR` | Direktorijum tačno pet model assets | Runtime | NE | `/models` u Compose; apsolutna Linux putanja native |
| `DATABASE_CONNECTION_STRING` | Npgsql veza ka server `cinekros`, read-only korisnik | Runtime | DA | Deployment entrypoint čita zaseban secret fajl; bez štampanja |
| `GEMINI_API_KEY` | Postojeći Gemini ključ | Runtime | DA | Zaseban secret fajl; bez Git-a/slika/logova |
| `CINEKROS_ALLOWED_ORIGINS` | Tačan frontend HTTPS origin | Runtime | NE | `https://cinekros.xn--lazarkrsti-zhb.com` |
| `CINEKROS_TRUST_CF_CONNECTING_IP` | Uključuje ograničeno čitanje CF headera | Runtime | NE | Podrazumevano `false`; tek posle provere granice |
| `CINEKROS_TRUSTED_PROXY_IPS` | Lista tačnih neposrednih IP adresa cloudflared-a | Runtime | NE | Bez mreža/CIDR/wildcard-a; ne stavljati proizvoljne adrese |
| `CINEKROS_API_ENV_FILE` | Putanja bezbednog API konfiguracionog fajla za Compose | Compose | NE | `/etc/cinekros/api.env` |
| `CINEKROS_MODEL_HOST_DIR` | Model direktorijum na hostu | Compose | NE | `/opt/cinekros/models/<revision>` |
| `CINEKROS_DATABASE_CONNECTION_STRING_FILE` | Putanja secret fajla; entrypoint je čita | Compose/native | Sadržaj DA | Spoljašnji fajl, stroge permisije; Compose montira u `/run/secrets/` |
| `CINEKROS_GEMINI_API_KEY_FILE` | Putanja Gemini secret fajla | Compose/native | Sadržaj DA | Isto, ne u image |
| `CINEKROS_POSTGRES_ADMIN_PASSWORD_FILE` | Host fajl admin lozinke | Compose | Sadržaj DA | Nije dostupan API servisu |
| `POSTGRES_PASSWORD_FILE` | Unutrašnja putanja admin lozinke u PG image-u | Runtime PG | Sadržaj DA | Compose secret; ne koristiti admin korisnika u API-ju |
| `POSTGRES_DB`, `POSTGRES_USER` | Inicijalni server `cinekros` i administratorski nalog | PG inicijalizacija | NE | Važe samo pri prvom praznom volume-u |
| `CINEKROS_CLOUDFLARED_CONFIG` | Konfiguracija named tunnel-a | Compose | NE | Spoljašnji fajl sa hostname/UUID, bez tokena |
| `CINEKROS_CLOUDFLARED_CREDENTIALS_FILE` | Named tunnel credentials JSON | Compose | DA | Spoljašnji secret, nikad Git |

Razlikovati promenljivu koja sadrži PUTANJU od sadržaja tajne. Compose koristi `deploy/start-api.sh` da pročita dva secret fajla u postojeće procesne env promenljive i zatim `exec dotnet`; zajednički Windows credential loader nije menjan. Native systemd koristi `LoadCredential` i isti deployment entrypoint. Npgsql i dalje nameće `default_transaction_read_only=on`; to ne zamenjuje DB privilegije.

## 3. Artefakti i Git

| Artefakt | Lokacija / prenos | Git? |
|---|---|---|
| Izvorni kod, Dockerfile, Compose, unit, SQL provera i prazni primeri | `deploy/`, root `.dockerignore` | DA, pregledane promene |
| Glavno uputstvo | ovaj dokument | Namenski lokalni commit; ostatak ignorisanog docs stabla ne dodavati |
| Mali query testni skup / Windows referenca | `deploy/reference/windows-e5-reference.json` | DA; 30 namenski javnih probnih tekstova, nije filmski dataset |
| Pet model fajlova | `database/data/models/...` → `/opt/cinekros/models/<revision>` | NE; ne u image/context/frontend |
| Katalozi, prevodi i 19.460 filmskih vektora | Postojeći lokalni data artefakti; na server se prenose kroz DB dump | NE; bez regenerisanja |
| PostgreSQL dump / restore dokaz | Ignorisan transfer/backup direktorijum | NE; zasebna backup lokacija |
| Stvarni `.env`, secret fajlovi i tunnel credentials | `/etc/cinekros/`, van checkout-a | NE |
| Frontend dist / API i deploy-check publish | `.local/deployment/` → namenski server release folderi | NE |
| Testovi, ETL, prevodilac OPUS, generator embeddinga, evaluacija i diplomski | Ostaju razvojni materijal | Nisu deo runtime paketa |

API trenutno referencira `CineKros.Catalog.Importer` assembly zbog postojećih deljenih tipova. Zato se ta DLL može nalaziti u publish output-u; to NIJE razlog za pokretanje importa ili kopiranje ETL podataka na server. Deploy-check je zaseban projekat i nije API project reference niti deo API publish-a.

Docker context je allowlist, ne ceo repozitorijum. Linux RID lock varijante su izdvojene u `deploy/locks/{api,embedding,importer}/packages.lock.json`; originalni Windows/portable lock fajlovi ostaju isti. Pri budućoj odobrenoj izmeni zavisnosti ponovo napraviti i proveriti ove varijante; ne rešavati mismatch uklanjanjem `--locked-mode`.

## 4. Server priprema — ZA TESTIRANJE NA SERVERU

Sve naredne Linux komande izvršava Lazar tek nakon provere hostname-a i putanja. `<SERVER_SSH_HOST>` je korisnikov SSH host/alias; ne ugrađivati trenutnu DHCP adresu u aplikaciju. SSH se ovde ne izvršava.

```bash
hostnamectl
uname -a
dpkg --print-architecture
lscpu
free -h
df -h
```

Očekivanje: Debian 13/amd64, AVX prisutan, AVX2 odsutan. Potvrditi realni RAM i slobodan disk. Ne tumačiti 340 MiB ranije prijavljenog idle-a kao ukupnu potrebu aplikacije.

### Docker Engine i Compose

Pratiti [zvanično Debian uputstvo](https://docs.docker.com/engine/install/debian/). Ovo je Docker Engine na Linux-u, ne Docker Desktop. Pre instalacije proveriti konfliktne postojeće pakete, ne uklanjati ih automatski bez pregleda.

```bash
sudo apt update
sudo apt install ca-certificates curl gnupg rsync jq
sudo install -m 0755 -d /etc/apt/keyrings
sudo curl -fsSL https://download.docker.com/linux/debian/gpg -o /etc/apt/keyrings/docker.asc
sudo chmod a+r /etc/apt/keyrings/docker.asc
. /etc/os-release
printf 'Types: deb\nURIs: https://download.docker.com/linux/debian\nSuites: %s\nComponents: stable\nArchitectures: %s\nSigned-By: /etc/apt/keyrings/docker.asc\n' \
  "$VERSION_CODENAME" "$(dpkg --print-architecture)" | sudo tee /etc/apt/sources.list.d/docker.sources
sudo apt update
sudo apt install docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
sudo docker version
sudo docker compose version
```

Članstvo u docker grupi daje praktično root mogućnosti. Podrazumevano ovde koristiti `sudo`, ne otvarati Docker socket mreži. Ne dirati SSH pristup/firewall naslepo. Ne otvarati router portove za API ili PG.

### .NET za native API i deploy-check

I kod Compose izbora praktično je imati runtime za prvi izolovani deploy-check na hostu. Instalacija po [Microsoft Debian 13 uputstvu](https://learn.microsoft.com/en-us/dotnet/core/install/linux-debian?tabs=dotnet10):

```bash
curl -fL https://packages.microsoft.com/config/debian/13/packages-microsoft-prod.deb -o /tmp/cinekros-ms-feed.deb
sudo dpkg -i /tmp/cinekros-ms-feed.deb
sudo apt update
sudo apt install aspnetcore-runtime-10.0
dotnet --list-runtimes
```

API zahteva ASP.NET Core runtime, ne samo osnovni .NET runtime. SDK na serveru nije potreban ako se prenosi već objavljen paket. Za lokalno kompajliranje fallback-a iz §7 potrebni su dodatni alati i disk; to nije obavezan prvi korak.

### Raspored i permisije

Predlog direktorijuma (nije postojeće stanje servera):

```text
/opt/cinekros/repo/                 odabrani izvorni release i deploy/
/opt/cinekros/releases/<commit>/api/ zaseban linux-x64 publish
/opt/cinekros/api                   symlink na odobren API release (native)
/opt/cinekros/deploy                symlink na odgovarajući deploy direktorijum
/opt/cinekros/models/<revision>/    pet zaključanih assets, read-only
/opt/cinekros/deploy-check/         poseban publish alata
/etc/cinekros/                      api.env, compose.env, cloudflared.yml
/etc/cinekros/secrets/              odvojene tajne
/var/backups/cinekros/              dumpovi; dodatna kopija van servera
```

```bash
sudo install -d -m 0755 /opt/cinekros /opt/cinekros/models /opt/cinekros/releases
sudo install -d -m 0700 /etc/cinekros /etc/cinekros/secrets /var/backups/cinekros
```

Secret fajlovi na hostu neka budu root-owned, 0600. Docker Compose file secrets nisu automatski enkriptovan vault i `uid/gid/mode` kod bind-mount izvora ne treba smatrati dokazano primenjenim. Pre API starta proveriti da ih container UID 10001 može čitati bez davanja pristupa drugim korisnicima: namenski ACL za UID 10001 samo na dva API secret fajla, ili namenski root:10001 0640 fajlovi unutar root-only parent direktorijuma. PG admin tajnu API ne dobija. Za cloudflared utvrditi stvarni UID image-a i dati samo potrebnu read dozvolu njegovom credentials fajlu. Ako mount nije čitljiv, STOP; ne rešavati sa `chmod 777` ili root API-jem. Native systemd `LoadCredential` omogućava da originali ostanu root:root 0600.

## 5. Model transfer i SHA-256

Model/revision: `intfloat/multilingual-e5-base` / `d128750597153bb5987e10b1c3493a34e5a4502a`. Profil `multilingual-e5-base-int8-onnx-v1`, fingerprint `eac906ed78f7863573d13c9b0435de1b8f848fe92fc6ae08aa3621400260b1fe`. Očekivani hash-evi preuzeti su iz postojećeg `EmbeddingProfileDescriptor`, ne računati nad proizvoljno preuzetim novim modelom pa ih proglasiti odobrenim.

Windows, samo inventar i kasniji transfer:

```powershell
$modelDir='D:\Repozitorijum\CineKros\database\data\models\multilingual-e5-base\d128750597153bb5987e10b1c3493a34e5a4502a'
Get-ChildItem -LiteralPath $modelDir -File | Get-FileHash -Algorithm SHA256
scp -r $modelDir <SERVER_SSH_HOST>:/putanja/za/privremeni/prijem/
scp deploy/model.SHA256SUMS deploy/verify-model.sh <SERVER_SSH_HOST>:/putanja/za/privremeni/prijem/
```

Na serveru kopirati u konačni model direktorijum, postaviti čitljivost API korisniku i onemogućiti runtime upis. Očekuje se tačno pet fajlova: `model_qint8_avx512_vnni.onnx`, `tokenizer.json`, `config.json`, `tokenizer_config.json`, `special_tokens_map.json`. Ne prenositi `.env`, stari model, FP32 ili cache.

```bash
sh /opt/cinekros/repo/deploy/verify-model.sh /opt/cinekros/models/d128750597153bb5987e10b1c3493a34e5a4502a
```

Skripta proverava inventar i `sha256sum --check --strict`. Missing/changed/unexpected file je STOP. Ne menjati manifest da bi nepoznati fajl prošao. Read-only mount je dodatna zaštita, ne zamena za hash proveru.

## 6. Database transfer, read-only korisnik i validacija

Izvor mora biti Windows V6 baza **`cinekros_v6_rehearsal_20261015t2154z_4fd266aa`**, nikako lokalna V5 `cinekros`. Cilj je NOVA baza `cinekros` na fizičkom Debian serveru. Ne kopirati PostgreSQL data direktorijum između sistema.

### Windows pg_dump — postupak, nije izvršen u ovoj pripremi

Prvo identifikovati postojeći PG container i nalog preko lokalnih uputstava. Lozinku ne stavljati u komandnu liniju, istoriju ili chat. Primer koristi već funkcionalni lokalni container admin mehanizam i privremeni fajl UNUTAR containera radi izbegavanja binarne PowerShell 5 redirekcije:

```powershell
$pgContainer='<STVARNI_POSTOJECI_PG_CONTAINER>'
$dumpName='cinekros-v6-<JEDINSTVEN_TIMESTAMP>.dump'
$dockerCli='C:\Program Files\Docker\Docker\resources\bin\docker.exe'
if (-not (Test-Path -LiteralPath $dockerCli)) { throw 'Prvo pronadji stvarni Docker CLI.' }
& $dockerCli exec $pgContainer pg_dump -U <POSTOJECI_BACKUP_NALOG> -d cinekros_v6_rehearsal_20261015t2154z_4fd266aa -Fc --no-owner --no-acl -f /tmp/$dumpName
# Proveriti prethodni exit code pre nastavka.
& $dockerCli cp "${pgContainer}:/tmp/$dumpName" ".local/deployment/transfer/$dumpName"
Get-FileHash ".local/deployment/transfer/$dumpName" -Algorithm SHA256
scp ".local/deployment/transfer/$dumpName" <SERVER_SSH_HOST>:/putanja/za/privremeni/prijem/
```

Koristiti pg_dump 17 prema serveru 17. Ne prepisivati raniji backup. Proveriti checksum prenetog dump-a i sadržaj `pg_restore --list` pre restore-a. Dump obuhvata V6 šemu, cast i vektore; nema potrebe za novim ETL-om/importom/migracijama.

Na ovoj Windows mašini proverena je gornja puna `docker.exe` putanja. Golo `docker` u agentskoj PowerShell sesiji pogodilo je zero-byte stub u system32 i nije davalo stvarni rezultat. Zato pre backup-a proveriti pravi CLI sa `& $dockerCli version`; prazna konzola/exit 0 nije dokaz uspešnog dump-a. Ovo nije razlog za menjanje postojećeg lokalnog launchera ili sistemskih fajlova.

### Compose podešavanje pre prve inicijalizacije server baze

Kopirati bezopasne šablone u `/etc/cinekros/`, a stvarne secret fajlove napraviti editorom/zaštićenim unosom na serveru. Ne unositi tajne u Git primer. U `compose.env` koristiti APSOLUTNE server putanje:

```dotenv
CINEKROS_API_ENV_FILE=/etc/cinekros/api.env
CINEKROS_MODEL_HOST_DIR=/opt/cinekros/models/d128750597153bb5987e10b1c3493a34e5a4502a
CINEKROS_POSTGRES_ADMIN_PASSWORD_FILE=/etc/cinekros/secrets/postgres_admin_password
CINEKROS_DATABASE_CONNECTION_STRING_FILE=/etc/cinekros/secrets/database_connection_string
CINEKROS_GEMINI_API_KEY_FILE=/etc/cinekros/secrets/gemini_api_key
CINEKROS_CLOUDFLARED_CREDENTIALS_FILE=/etc/cinekros/secrets/cloudflared_credentials.json
CINEKROS_CLOUDFLARED_CONFIG=/etc/cinekros/cloudflared.yml
```

`database_connection_string` sadrži Npgsql string sa `Host=postgres;Port=5432;Database=cinekros;Username=cinekrosreadonly;Password=<TAJNA>` za Compose. Za native API host je `127.0.0.1`; lozinka sa specijalnim karakterima mora biti pravilno Npgsql-quoted, ne ručno spojena u shell komandu. Primer nije stvarna lozinka. Gemini key fajl sadrži samo ključ. Koristiti jednolinijski UTF-8 bez BOM-a, bez shell navodnika oko celog sadržaja; završni Unix newline je dozvoljen. Ne koristiti `set -x` tokom rada sa tajnama.

U Bash sesiji definisati pomoćnu FUNKCIJU, ne novi infrastrukturni servis:

```bash
dc() { sudo docker compose -p cinekros-prod --env-file /etc/cinekros/compose.env -f /opt/cinekros/repo/deploy/compose.yaml "$@"; }
dc config --quiet
dc up -d postgres
dc ps
dc exec -T postgres pg_isready -U postgres -d cinekros
```

Ovo pokreće samo PG na serveru, bez javnih portova/tunela/API-ja. Prazan named volume `cinekros-postgres-data` mora biti namenski nov; ako već postoji nepoznat sadržaj, STOP i inventar, bez `down -v`.

### Restore i privilegije — isključivo server

Proveriti da je cilj zaista sveža server baza. Zatim:

```bash
sudo bash -c 'docker compose -p cinekros-prod --env-file /etc/cinekros/compose.env \
  -f /opt/cinekros/repo/deploy/compose.yaml exec -T postgres \
  pg_restore -U postgres -d cinekros --exit-on-error --single-transaction \
  --no-owner --no-acl < "$1"' _ /var/backups/cinekros/<PROVEREN_V6_DUMP>.dump
```

Kompletna redirekcija je namerno unutar root shell-a, jer običan korisnik ne može čitati root-only backup direktorijum. Putanja dump-a je zaseban argument, ne deo sklopljene shell komande. Ne koristiti `--clean`, ne dodavati nove migracije. Greška u restore-u znači STOP i pregled, ne nastavak delimičnim stanjem. Restore sa ovim opcijama na praznoj bazi izbegava vlasništva sa Windows mašine; extension mora biti verzije 0.8.6. Proveriti restore u zasebnoj test bazi pre prvog javnog starta, a ne samo `--list`.

Administratorska SQL sesija preko `dc exec -it postgres psql -U postgres -d cinekros`:

```sql
CREATE ROLE cinekrosreadonly LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION;
-- U psql postaviti lozinku interaktivno: \password cinekrosreadonly
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
REVOKE ALL ON DATABASE cinekros FROM PUBLIC;
GRANT CONNECT ON DATABASE cinekros TO cinekrosreadonly;
GRANT USAGE ON SCHEMA public TO cinekrosreadonly;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO cinekrosreadonly;
ALTER ROLE cinekrosreadonly IN DATABASE cinekros SET default_transaction_read_only = on;
```

Nalog ne sme biti owner objekata, član admin uloga ili imati INSERT/UPDATE/DELETE/CREATE. Naknadne tabele nisu automatski odobrene; promenu release-a treba ponovo pregledati. Role/ACL promene ne treba da menjaju postojeće schema fingerprint objekte; ne dodavati pomoćne tabele/funkcije u public radi deploymenta.

Validacioni SQL izvršiti kao runtime korisnik. Koristiti interaktivni password prompt ili zaštićen `PGPASSFILE`, ne `PGPASSWORD` u ispisanom komandnom redu. Primer preko mrežno izolovanog PG containera:

```bash
# Najpre otvoriti runtime psql sesiju sa -W; proveriti current_user i read-only status.
dc exec -it postgres psql -h 127.0.0.1 -U cinekrosreadonly -W -d cinekros
# U sesiji kopirati sadržaj deploy/validate-production-readiness.sql,
# ili koristiti unapred zaštićen PGPASSFILE i psql -v ON_ERROR_STOP=1 -f za ovaj fajl.
```

Skripta je read-only i završava ROLLBACK-om. Očekivati 9.730 filmova, 9.730 EN i SR vektora, 768D, 79 NULL cast, tačne state/ledger pinove i indekse. Proverava i tačan runtime nalog, odsustvo privilegovanih atributa i table write privilegija. Ne nameće `search_path` koji bi sakrio pogrešnu ulaznu šemu; vector funkcije su eksplicitno kvalifikovane. Proveriti SVE boolean rezultate; `psql` exit 0 sam po sebi ne znači da su boolean provere true. SQL očitava deklarisane identitete i tehničke osobine; API dodatno stvarno preračunava cast/schema/ID provere. Ne predstavljati sačuvani state hash kao nezavisan dokaz aktuelnog sadržaja.

## 7. Kritična AVX2 i numerička STOP/GO kapija

### Prva izvršna provera na fizičkom CPU-u

Ne otvarati tunel pre ove provere. Preneti zaseban framework-dependent `linux-x64` deploy-check publish, ne Windows DLL direktorijum sa Windows native zavisnostima.

Windows komanda za ponovno pravljenje paketa, ako je potrebno, uz zaseban output:

```powershell
dotnet publish deploy/CineKros.DeployCheck/CineKros.DeployCheck.csproj -c Release -r linux-x64 --self-contained false -p:UseAppHost=false -o .local/deployment/publish/deploy-check
scp -r .local/deployment/publish/deploy-check <SERVER_SSH_HOST>:/putanja/za/privremeni/prijem/
scp deploy/reference/windows-e5-reference.json <SERVER_SSH_HOST>:/putanja/za/privremeni/prijem/
```

Publish može ažurirati lokalni RID cache/lock metadata. Za javni release pregledati dependency diff; ne commitovati nenamerne promene starih lockova. U ovoj pripremi originalni lockovi su očuvani, a namenski Linux lockovi sačuvani odvojeno za Docker build.

Na serveru prvo postaviti prenete fajlove (zameniti putanju privremenog prijema stvarnom putanjom; ne prepisivati ranije izdanje):

```bash
test ! -e /opt/cinekros/deploy-check || { echo 'STOP: tool direktorijum vec postoji'; exit 1; }
test ! -e /opt/cinekros/windows-e5-reference.json || { echo 'STOP: referenca vec postoji'; exit 1; }
sudo cp -a /putanja/za/privremeni/prijem/deploy-check /opt/cinekros/deploy-check
sudo install -m 0444 /putanja/za/privremeni/prijem/windows-e5-reference.json /opt/cinekros/windows-e5-reference.json
sudo install -d -m 0750 -o "$(id -u)" -g "$(id -g)" /opt/cinekros/deployment-results
```

Tool/reference ostaju read-only operatoru; samo namenski rezultatni direktorijum je zapisiv. Ne pokretati inference kao root samo radi zaobilaženja file permission problema. Sa instaliranim runtime-om i tačnim model direktorijumom:

```bash
grep -m1 '^flags' /proc/cpuinfo
dotnet --info
cd /opt/cinekros
printf '%s  %s\n' b00ca5d57d5ca1bb16652cb0047ba70b25ccef5fc5a359b62c5e8301ed931f26 windows-e5-reference.json | sha256sum --check --strict
dotnet deploy-check/CineKros.DeployCheck.dll export \
  --model-dir models/d128750597153bb5987e10b1c3493a34e5a4502a \
  --output /opt/cinekros/deployment-results/server-e5-first-export.json
echo "Exit status: $?"
```

Alat pre model load-a ispisuje CPU/runtime preamble. Zatim proverava pet SHA pinova, učitava stvarni tokenizer i ONNX model i računa 30 stvarnih query vektora sa batch=1. To je prva obavezna CPU/native provera. `SIGILL`/exit 132, missing .so, glibc greška, neuspešno učitavanje ili nevalidni vektori znače STOP. Sačuvati bezbedne logove i tačnu fazu pada. `dotnet --info` ili uspešan publish NISU zamena za inference.

Alat poziva postojeći `E5EmbeddingModel`, njegov `query:` prefiks, tokenizer/NFC, masked mean pooling i L2 normalizaciju, uz istu SR transliteraciju. Ne koristi posebnu pojednostavljenu implementaciju. U export-u su token ID-jevi/maska, originalni i normalizovani tekst, profil/model/revision/artifact SHA, 768 vrednosti i norma po upitu, load/warmup/p50/p95 i memorija SAMO probe procesa. SR Latin/Cyrillic ekvivalenti imaju proverene iste token ID-jeve u Windows referenci.

Za compare (novi output, bez prepisivanja dokaza):

```bash
dotnet deploy-check/CineKros.DeployCheck.dll compare \
  --model-dir models/d128750597153bb5987e10b1c3493a34e5a4502a \
  --reference windows-e5-reference.json --output /opt/cinekros/deployment-results/server-vs-windows.json
jq '{structuralChecksPassed,numericalAcceptance,minimumCosine,meanCosine,mismatches}' deployment-results/server-vs-windows.json
jq '.currentReport | {modelAndTokenizerLoadMs,firstWarmupMs,p50Ms,p95Ms,workingSetAfterLoadBytes,peakWorkingSetAfterQueriesBytes}' deployment-results/server-vs-windows.json
```

Compare ponovo računa 30 upita; ne ponavljati ga beskonačno. Exit 0 znači strukturne provere uspešne, NE prihvaćenu numeričku saglasnost. Exit 2 je greška rada/reference; exit 3 strukturno neslaganje. Posmatrati token identičnost, 768 konačnih vrednosti, normu oko 1, cosine minimum/prosek i L2 grešku. `PENDING ACCEPTANCE` ostaje dok Lazar ne odobri kriterijum na osnovu rezultata. Ne zahtevati bit-identičnost. Pre compare-a proveriti gore pinovani SHA Windows reference; CLI beleži SHA korišćene reference, ali ga sam ne poredi sa ovim odobrenim SHA. Za `deploy/reference/*.json` Git atribut `-text` čuva doslovne export bajtove: automatska CRLF/LF konverzija ne sme menjati pinovani hash pri Linux checkout-u. JSON formatiranje nije novi model/numerički rezultat; ne formatirati ovu odobrenu referencu naknadno.

Referentni Top-10 Windows/Linux rezultati još NISU napravljeni u ovoj fazi. Nakon server DB/API readiness-a koristiti mali isti skup postojećih validiranih semanticQuery/hard-filter DTO vrednosti, bez novih Gemini interpretacija; uporediti ID redosled i cosine distance nad istim EN/SR kolonama i nepromenjenim hard filterima. Sačuvati Windows i server rezultate i odvojiti numeric drift, tie slučajeve i stvarnu filmsku relevantnost. Kapija ostaje ZA TESTIRANJE NA SERVERU / PENDING ACCEPTANCE, ne tvrditi da sama cosine sličnost garantuje isti Top 10.

### Ako native paket ne radi bez AVX2 — rezervni postupak, NIJE potvrđeno rešenje

Najpre razlikovati ONNX Runtime od tokenizer pada (`ldd`, stderr, `journalctl`, ograničen `gdb` backtrace); ne menjati Windows NuGet paket ili model. U zasebnom staging direktorijumu pribaviti tačan ONNX Runtime izvor/tag `v1.29.0`, toolchain i zavisnosti prema [zvaničnom build uputstvu](https://onnxruntime.ai/docs/build/inferencing.html). Za 4 GB/HDD koristiti mali parallelism i meriti disk/RAM; trajanje NOT MEASURED.

Primer polazne komande za CPU-only shared build, ZA TESTIRANJE:

```bash
git clone --branch v1.29.0 --recursive https://github.com/microsoft/onnxruntime.git ort-1.29.0-baseline
cd ort-1.29.0-baseline
./build.sh --config Release --build_shared_lib --parallel 1 \
  --cmake_extra_defines onnxruntime_BUILD_FOR_NATIVE_MACHINE=OFF \
  onnxruntime_USE_AVX2=OFF onnxruntime_USE_AVX512=OFF \
  CMAKE_C_FLAGS='-march=x86-64 -mtune=generic' \
  CMAKE_CXX_FLAGS='-march=x86-64 -mtune=generic'
```

**Važno ograničenje:** ove top-level opcije nisu dokaz da su svi MLAS objekti bez AVX2/AVX512 instrukcija. [CMake konfiguracija za tačan tag](https://github.com/microsoft/onnxruntime/blob/v1.29.0/cmake/onnxruntime_mlas.cmake) sadrži posebne ISA kernele i runtime dispatch. Moraju se pregledati stvarni compiler flags, CPUID izbor i mesto SIGILL-a; cilj je odsustvo OBAVEZNOG AVX2/AVX512 zahteva na ovom CPU-u, ne nasumično brisanje kernela. Ako build zahteva source patch ili ne može dokazati ispravan dispatch, označiti BLOCKED i vratiti odluku Lazaru. Ne izmišljati univerzalni `disable_avx2` prekidač niti obećavati da će ovaj recept raditi.

Koristiti managed binding 1.29.0 sa native build-om istog release-a i proveriti C API kompatibilnost, exported `OrtGetApiBase`, `ldd` zavisnosti i stvarni inference. Zamenu `.so` raditi samo u novom Linux staging publish-u/image-u; sačuvati originalni paket, SHA i tačan build recept. Model/tokenizer assets i njihov profil ostaju isti. Za Tokenizers native grešku potreban je zaseban generic-x86_64 Rust build tačne kompatibilne verzije i ABI provera; to takođe nije potvrđeno rešenje i ne raditi speculativni upgrade biblioteka. Posle svake takve promene ponoviti celu CPU i numeričku kapiju pre javnog starta.

## 8. API pokretanje — dva pripremljena puta

### A. Docker Compose (preliminarno preferiran)

`deploy/Dockerfile.api` koristi SDK `10.0.401-noble` za build i **Debian 13/glibc** finalnu osnovu sa ASP.NET Core 10 iz Microsoft feed-a. [Microsoft više ne objavljuje Debian .NET 10 image](https://learn.microsoft.com/en-us/dotnet/core/compatibility/containers/10.0/default-images-use-ubuntu); ne koristiti nepostojeći `aspnet:10.0-bookworm-slim` niti Alpine. SDK je build alat, nije finalni runtime OS. Runtime apt patch i base image tagovi mogu se menjati: pre stvarnog release-a sačuvati resolved digest, package inventory i image ID; ne nazivati ovo bit-reproducible image-om.

Stvarni Linux container build otkrio je `NETSDK1022` zbog implicitnih Web SDK JSON content stavki koje se već eksplicitno nalaze u postojećem API projektu. Docker publish zato ISKLJUČIVO za taj build postavlja `EnableDefaultContentItems=false`, zadržava eksplicitne V6 prompt/schema stavke i posebno kopira pregledane `appsettings.json`/`appsettings.Development.json` (logging/AllowedHosts, bez tajni). Originalni `.csproj` i Windows publish nisu menjani. Posle budućih novih content fajlova proveriti publish inventar. Deployment `.gitattributes` čuva Unix LF za shell skripte; ne prenositi CRLF entrypoint.

```bash
dc config --quiet
dc build --pull api
dc up -d api
dc ps
dc exec -T api curl --fail --silent http://127.0.0.1:8080/live
dc exec -T api curl --fail --silent http://127.0.0.1:8080/ready
dc logs --tail 80 api
```

API non-root UID je 10001, model mount read-only, API/PG nemaju objavljene host portove. Container sluša na 8080 unutar mreže; to nije LAN port. `cloudflared` je iza eksplicitnog `public` profila i ne startuje ovim komandama. Pre njegovog uključivanja završiti CPU/numeričke/DB/CORS/trust kapije. Pošto se native host i container razlikuju u korisničkom prostoru, deploy-check treba proveriti i u tačnoj API runtime osnovi pre javnog rada (privremeno read-only montiran zaseban tool publish, bez kopiranja u finalni API image).

`/live` = proces uspešno dostupan; `/ready` = uspešno završene originalne startup model/database provere, keširano stanje. To nije neprekidni test Gemini-ja ili DB konekcije. API i dalje ima postojeće per-search fail-closed provere. Health pozivi ne troše recommendation limiter i ne otkrivaju pinove/putanje/SQL. Trenutno se puna DB readiness provera u novom V6 startup putu izvršava i u factory-ju i u Program-u; dodatni cold-start HDD trošak je NOT MEASURED. Bez dokaza ne optimizovati zaštite.

### B. Native .NET + systemd API

Koristiti isti odobreni Linux publish, ne Windows build. API na serveru bind-ovati samo na 127.0.0.1. PostgreSQL može ostati isti Compose servis; jedino native DB override objavljuje loopback PG port:

```bash
dcn() { sudo docker compose -p cinekros-prod --env-file /etc/cinekros/compose.env -f /opt/cinekros/repo/deploy/compose.yaml -f /opt/cinekros/repo/deploy/compose.native-db.yaml "$@"; }
dcn up -d postgres
```

Ako se prelazi sa testnog Compose API-ja, prvo ugasiti SAMO njegov API/tunnel; ne pokretati oba API runtime-a slučajno. PG volume ostaje isti i ne briše se. Native `database_connection_string` koristi `Host=127.0.0.1`; nikad Docker DNS `postgres` iz host procesa.

Napraviti sistemskog korisnika `cinekros` bez shell-a, preneti `/opt/cinekros/releases/<commit>/api`, povezati `/opt/cinekros/api` i `/opt/cinekros/deploy` na isti release i kopirati unit `deploy/cinekros-api.service` u `/etc/systemd/system/`. U native `/etc/cinekros/api.env` preuzeti Production profil/origin iz primera, ali `CINEKROS_E5_MODEL_DIR` postaviti na stvarnu apsolutnu model putanju, ne `/models`. Unit već zadaje loopback URL i učitava dva credential fajla preko systemd-a.

```bash
sudo chmod 0555 /opt/cinekros/deploy/start-api.sh
sudo systemctl daemon-reload
sudo systemctl start cinekros-api
systemctl status cinekros-api --no-pager
curl --fail --silent http://127.0.0.1:8080/live
curl --fail --silent http://127.0.0.1:8080/ready
sudo journalctl -u cinekros-api -n 80 --no-pager
```

Tek posle provere razmotriti `systemctl enable`. Cloudflared u ovom rasporedu treba da radi na hostu i rutira na `http://127.0.0.1:8080`, ne na Compose DNS `api`. Njegov systemd servis/credential setup napraviti prema zvaničnom named tunnel uputstvu nakon izbora rasporeda; Compose cloudflared ne može preko svog `127.0.0.1` pristupati host API-ju. Ne otvarati API na 0.0.0.0 radi lakšeg povezivanja.

Za native cloudflared koristiti zvanični `cloudflared-linux-amd64.deb` iz odabranog [release-a 2026.10.0](https://github.com/cloudflare/cloudflared/releases/tag/2026.10.0), proveriti njegov SHA prema zvaničnoj objavi pre `sudo dpkg -i`, pa `cloudflared --version`. Ne predstavljati samo izračunavanje hash-a kao proveru ako nedostaje očekivani hash. Kopija named-tunnel konfiguracije za host, `/etc/cinekros/cloudflared-native.yml`, mora imati `service: http://127.0.0.1:8080`, stvarni UUID, API hostname i `credentials-file: /etc/cinekros/secrets/cloudflared_credentials.json`. Nije ista config putanja kao unutar Compose containera. Tek POSLE G0–G6 kapija izvršiti `sudo cloudflared --config /etc/cinekros/cloudflared-native.yml service install` i proveriti `systemctl status cloudflared` / `journalctl -u cloudflared`. Service installer može odmah pokrenuti tunel; ne koristiti ga prerano. Native API i PG Docker restart/boot redosled moraju se potvrditi na serveru; unit ne upravlja sam restore-om niti Compose bazom.

## 9. Cloudflare, IDN, Pages i HTTPS

Provereni IDN:

| Namena | Unicode prikaz | Kanonski ASCII HTTPS origin |
|---|---|---|
| Domen | lazarkrstić.com | `xn--lazarkrsti-zhb.com` |
| Frontend | cinekros.lazarkrstić.com | `https://cinekros.xn--lazarkrsti-zhb.com` |
| API | cinekros-api.lazarkrstić.com | `https://cinekros-api.xn--lazarkrsti-zhb.com` |

Vlasništvo, nameserver-i, Cloudflare zona i DNS zapisi NISU provereni/podešeni ovim zadatkom. Frontend validira kanonski URL origin; zato build koristi ASCII oblik, ne Unicode tekst ili API `/api/recommendations` putanju. Backend dozvoljava samo tačan frontend origin, POST i Content-Type, bez wildcard-a i credentials CORS-a.

### Lokalni production build i Direct Upload

Na Windows-u iz root-a, uz Node 24 i postojeći lockfile; ne menjati lokalni Vite dev target:

```powershell
$previousOrigin=$env:VITE_API_BASE_URL
try {
  $env:VITE_API_BASE_URL='https://cinekros-api.xn--lazarkrsti-zhb.com'
  npm --prefix src/frontend run build -- --outDir D:/Repozitorijum/CineKros/.local/deployment/frontend/dist
} finally { $env:VITE_API_BASE_URL=$previousOrigin }
```

Ovaj build je stvarno prošao. Proveriti `index.html`, assets i prisustvo tačnog API origin-a u JS bundle-u. To je build-time vrednost; naknadna Pages env promena neće promeniti već izgrađeni JS. Ne uploadovati checkout, `.env`, modele ili API publish. Standardni Vite `dist` je statički; trenutna aplikacija nema client router koji zahteva nova pravila. Ako se routing kasnije doda, proveriti Pages fallback, ne dodavati naslepo `_redirects` sada.

Prema [Pages Direct Upload](https://developers.cloudflare.com/pages/get-started/direct-upload/), odabrati upload prebuilt direktorijuma kroz dashboard ili Wrangler. Primer budućih komandi:

```powershell
npx wrangler pages project create cinekros
npx wrangler pages deploy .local/deployment/frontend/dist --project-name cinekros
```

Ove komande NISU izvršene; prijava/kreiranje resursa zahtevaju korisnika. Ako projekat već postoji, ne kreirati duplikat. Direct Upload nije isto što i automatski Cloudflare build/Git integration. Dodati custom frontend domen u Pages pre odgovarajućeg DNS CNAME podešavanja, po [zvaničnom uputstvu](https://developers.cloudflare.com/pages/configuration/custom-domains/).

### Named Tunnel i API hostname

Koristiti named tunnel, ne privremeni quick tunnel. Primer `deploy/cloudflared/config.example.yml` zameniti stvarnim UUID-om i montirati odgovarajući credentials JSON van Git-a. Ingress hostname je isključivo API punycode poddomen, origin `http://api:8080` za Compose, završni ingress `http_status:404`. Frontend hostname pripada Pages-u, ne tom ingress-u.

Tek nakon svih prethodnih GO kapija:

```bash
dc --profile public up -d cloudflared
dc logs --tail 60 cloudflared
```

DNS/API route kreirati korisnički prema [Cloudflare Tunnel dokumentaciji](https://developers.cloudflare.com/tunnel/get-started/). Saobraćaj je outbound-only kroz cloudflared; ne otvarati javni API/PG port. Ne prosleđivati ceo domen niti dodavati catch-all API origin. U native opciji koristiti namenski host cloudflared servis sa loopback origin-om. Sačekati validan HTTPS sertifikat na oba javna hostname-a.

Korisni budući CORS/health testovi bez Gemini-ja:

```bash
curl -i https://cinekros-api.xn--lazarkrsti-zhb.com/ready
curl -i -X OPTIONS https://cinekros-api.xn--lazarkrsti-zhb.com/api/recommendations \
  -H 'Origin: https://cinekros.xn--lazarkrsti-zhb.com' \
  -H 'Access-Control-Request-Method: POST' \
  -H 'Access-Control-Request-Headers: content-type'
```

Očekivati ready JSON i tačan Access-Control-Allow-Origin za dozvoljeni origin. Ponoviti preflight sa nepouzdanim origin-om: ne sme dobiti dozvolu. CORS nije autentikacija i ne blokira direktne HTTP klijente.

## 10. Sigurnost i trusted client IP

API port nije javno objavljen; PG port nikada nije javno dostupan. API runtime DB nalog je read-only i nije owner. Tajne se ne čuvaju u image-u, Git-u, frontend bundle-u ili Development logovima. `api.env` ne treba da uključuje SQL dijagnostiku ili Development režim. Google API timeout u postojećem language-aware parseru je 20 s; provider 503/mrežne greške daju sanitizovan `PROVIDER_UNAVAILABLE`. U parseru nema automatske retry petlje; ne uvoditi nekontrolisane retry-je u proxy-ju/browseru.

Limiter ostaje fixed-window 30 zahteva/60 s po ključu, bez reda čekanja. To nije globalna zaštita Gemini dnevnog budžeta: mnogo različitih IP adresa i dalje može trošiti kvotu. Pre javnog pilota korisnik treba da proveri Google nalog/model kvote, billing i upozorenja na potrošnju; troškovi i dovoljan budžet su NOT MEASURED. Implementacija `TrustedClientIp` utiče samo na ključ limitera, ne prepisuje globalni `RemoteIpAddress`, ne obrađuje X-Forwarded-For i ne uvodi univerzalni forwarded-headers trust.

Cloudflare `CF-Connecting-IP` sme se koristiti tek kada je dokazano:

1. API origin nije dostupan mimo tunela/namenskog lokalnog administrativnog pristupa.
2. Poznata je TAČNA neposredna adresa cloudflared-a koja stiže API-ju.
3. Samo cloudflared ima tu adresu/pristup toj mrežnoj granici; neprivilegovani drugi container-i ne dele trusted mrežu.
4. Cloudflare postavlja originalni klijentski header; Worker/Pseudo IPv4 podešavanja nisu neprovereno promenila očekivanje.
5. Stvarni zahtev sa lažnim headerom van te granice ne može dobiti poverenje.

Compose cloudflared dolazi sa interne Docker IP adrese, ne nužno sa loopback-a. Privremeno ostaviti trust `false`; tada korisnici mogu deliti proxy rate-limit bucket — bezbedno ali operativno ograničeno, nije verifikovana per-user funkcionalnost. Nakon pregleda `docker inspect` mreža pinovati proverenu tačnu cloudflared adresu kroz namenski Docker IPAM/static address ili drugi dokazani tačan raspored. Ne prepisivati ceo subnet u trusted listu. Compose trenutno NE obećava stabilan peer IP posle recreate-a; bez takvog pina ne uključivati trust.

Native loopback trust takođe nije magična autentikacija: svaki proces sa pristupom loopback API-ju može slati header. Prihvatljivost zavisi od jednokorisničkog/pouzdanog host okruženja i ograničenog lokalnog pristupa. Ako postoji nepouzdan lokalni korisnik/proces ili granica nije dokaziva, ostaviti OFF i označiti javni per-client limiter BLOCKED do odluke; ne otvarati široko poverenje.

Tek tada postaviti `CINEKROS_TRUST_CF_CONNECTING_IP=true` i tačne literal IP vrednosti u `CINEKROS_TRUSTED_PROXY_IPS`, restartovati SAMO API i ponoviti proveru. Development i Production podrazumevano ne veruju headeru. Malformed/multiple header ili nepouzdan peer ostaju vezani za neposrednu IP. [Cloudflare header dokumentacija](https://developers.cloudflare.com/fundamentals/reference/http-headers/) opisuje sadržaj headera, ali lokalni trust boundary mora se dokazati zasebno.

Ostalo pre javnog rada: OS/security update politika, rotacija Gemini/tunnel tajni, zaštita SSH naloga, log retention i ograničenje diska, host access kontrola. Nema uvedenih naloga/sesija korisnika. Upiti su ipak korisnički sadržaj; izbegavati njihovo javno logovanje i unapred odrediti retention. Posteri su udaljeni TMDB resursi, IMDb je izlazni link u browseru; njihova dostupnost/licencni uslovi nisu garancija backend readiness-a.

## 11. Performanse i resursi

### Šta je stvarno izmereno lokalno

| Merenje | Vrednost / obuhvat |
|---|---|
| Novi izolovani frontend build | 264.284 bajta, bez node_modules, Node v24.21.0 |
| Novi linux-x64 API publish | 35.506.945 bajtova, 30 fajlova, bez modela i baze |
| Pet model assets | 295.770.455 bajtova ukupno; sam ONNX 278.686.411 bajtova; ostalo tokenizer/config |
| Lokalna V6 rehearsal DB | `pg_size_pretty(pg_database_size(...))`: 177 MB; ne veličina buduće server baze |
| Windows deploy-check load | 2.695,26 ms, hash + tokenizer + ONNX load |
| Windows deploy-check prvi warmup | 339,48 ms, jedan probni upit |
| Windows deploy-check warm p50 / p95 | 10,4145 / 12,5491 ms, 30 kratkih fiksnih upita |
| Probe working set posle load / peak | 603,42 / 778,43 MiB, SAMO tool proces |
| Probe norme | 0,9999999974–1,0000000029, 30 x 768D |
| Starije Windows runtime merenje | startup 18,35 s; API idle 634,1 MiB / peak 795,2 MiB |
| Starije Windows embedding merenje | EN p50/p95 10,85/12,56 ms; SR 11,23/12,27 ms, 30 po jeziku |

Izvor novih probe merenja je commitovana Windows referenca; percentili su nearest-rank `ceil(p*n)`. Token ID-jevi se beleže posebnim pozivom tokenizacije, a vremenski meren `EmbedQuery` ponovo izvršava stvarnu produkcionu tokenizaciju+inference+pooling+normalizaciju. Load/warmup su odvojeni; jedna probe sesija nije ozbiljan throughput benchmark.

Starije vrednosti potiču iz sačuvanog Phase 10 runtime izveštaja, ne sa budućeg servera i ne iz aktuelnog V6 server merenja. Memorija je jedan API proces, bez PostgreSQL-a, OS-a, Dockera/cloudflared-a i browsera. Ne sabirati tool i API RAM kao da moraju paralelno raditi; deploy-check je privremen. Model pet assets je približno 282 MiB; tačan disk proveriti `du -sh`. Server DB size, Docker image/volume size, ukupni runtime RAM, HDD latencije i throughput: **NOT MEASURED**.

### Plan fizičkog merenja

Bez javnog tunela prvo zabeležiti `free -h`, `swapon --show`, `df -h`, `vmstat 1`, `iostat -xz 1` (sysstat ako ga korisnik instalira), `systemd-cgtop` ili `docker stats --no-stream`, `docker system df`, `du -sh` i read-only `pg_database_size`. Meriti:

- Cold start od start komande do `/ready`, posebno nakon hladnog HDD/file cache-a; ne koristiti agresivno drop-cache na aktivnom sistemu.
- Idle/peak API RSS, PG memoriju, cloudflared/OS i swap odvojeno; page cache nije isto što i RSS.
- Deploy-check load, prvi upit, p50/p95 i najsporiji upit; varijaciju kroz unapred ograničene ponovljene sesije.
- API end-to-end latenciju tek kasnije uz mali odobren broj Gemini poziva, odvojeno od model/SQL vremena; kvote/cena nisu merene ovom pripremom.
- Kontrolisano 1, zatim 2 i 4 istovremena lokalna zahteva, prvo provider-free kontrolama; limiter 30/60 nije globalno ograničenje konkurentnosti niti Gemini budžet.
- HDD queue/I/O wait, swap-in/out, OOM logove i vreme PG exact cosine skeniranja.

API koristi zajednički model sa postojećom sinhronizacijom inference-a i Npgsql pool; ne obećavati linearno skaliranje 4 niti ili 30 zahteva/min po svakom klijentu bez aggregate opterećenja. Ne uvoditi više API procesa na 4 GB bez merenja: svaki može učitati svoju kopiju modela. Početni memory limiti nisu aktivirani u šablonu; konačni limiti/swap odluke tek posle merenja. Uspešan Windows idle nije dokaz da je 1 GB ili besplatan plan dovoljan. HDD + mali RAM mogu dati ozbiljan cold start i swap thrashing.

## 12. Backup, update i rollback

Backup držati izvan PG volume-a i dodatno izvan servera. Nikad ne smatrati named volume backup-om. Primer posle potvrđenog server release-a:

```bash
# Jedinstveno ime; redirekcija i umask rade u administratorskom shell-u.
sudo bash -c 'umask 077; test ! -e "$1" || exit 1; \
  docker compose -p cinekros-prod --env-file /etc/cinekros/compose.env \
  -f /opt/cinekros/repo/deploy/compose.yaml exec -T postgres \
  pg_dump -U postgres -d cinekros -Fc --no-owner --no-acl > "$1"' \
  _ /var/backups/cinekros/cinekros-<TIMESTAMP>.dump
sudo sha256sum /var/backups/cinekros/cinekros-<TIMESTAMP>.dump
sudo bash -c 'docker compose -p cinekros-prod --env-file /etc/cinekros/compose.env \
  -f /opt/cinekros/repo/deploy/compose.yaml exec -T postgres pg_restore --list < "$1"' \
  _ /var/backups/cinekros/cinekros-<TIMESTAMP>.dump
```

Redirekcija zahteva pravo upisa u backup direktorijum; koristiti root-owned administratorsku sesiju ili namenskog backup operatera, ne olabavljivati direktorijum zbog permission greške. Proveru pravog restore-a raditi u novoj zasebnoj test bazi/volume-u, sa API-jem nepovezanim na taj cilj; posle readonly SQL provere sačuvati dokaz. Ne raditi `pg_restore --clean` nad aktivnim izdanjem.

Update: zabeležiti Git commit, image ID/digest, package inventory, model SHA manifest, Windows referencu i DB release pinove; napraviti backup; napraviti novi zaseban publish/image, proći offline i server gates; zaustaviti tunel ili kontrolisano obezbediti nedostupnost tokom promene; prebaciti API image tag ili native symlink; proveriti `/ready`, DB pinove i CORS; tek onda vratiti tunel. Ne koristiti stalno prepisivan `local/latest` tag kao jedini rollback identitet — tagovati konkretan commit i sačuvati prethodni image/publish.

Rollback aplikacije dozvoljen je samo na izdanje kompatibilno sa ISTIM V6 dataset/model identitetima. V5 API nije automatski kompatibilan sa V6 bazom; ne menjati readiness pinove da bi rollback prošao. Ako se kasnije menja dataset, potreban je zaseban unapred provereni DB restore/cutover plan; sada nema takve migracije. Kod neuspešne provere ugasiti tunel i novi API, sačuvati logove, vratiti prethodni provereni API/konfiguraciju bez brisanja volume-a. Stari Windows sistem ostaje nezavisna razvojna referenca.

## 13. Faze sa jasnim kapijama

Komande iz navedenih odeljaka izvršavati redom. Nijedna faza ne preskače neuspešnu prethodnu kapiju.

| Faza | Preduslovi i komande | Očekivani ishod / provera | Rizik i postupak ako ne uspe | Povratak i šta NE raditi |
|---|---|---|---|---|
| G0 — inventar | §4 `hostnamectl`, `lscpu`, `free`, `df` | Tačan Debian/CPU/RAM/disk zabeleženi | Pogrešan host ili prostor: STOP | Bez instalacije/javnog tunela dok se cilj ne potvrdi |
| G1 — alati i transfer | §4 instalacije; §5 prenos + `verify-model.sh`; provera SHA probe reference | Runtime dostupan, pet odobrenih assets | Missing hash/ABI: sačuvati nalaz, ispraviti transfer | Ne menjati pinove niti Windows model; ukloniti samo dokazano pogrešan novi staging uz ručnu proveru |
| G2 — CPU native GO | §7 deploy-check export | Stvarno tokenizer+ONNX+768D inference bez SIGILL | Native crash: fallback dijagnostika, BLOCKED ako nema sigurnog rešenja | Tunel/API javni start ZABRANJEN; originalne Linux pakete sačuvati |
| G3 — numerika | §7 compare + pregled normi/tokena/min/mean | Struktura ispravna, numerika PENDING ACCEPTANCE dok Lazar ne odobri | Drift: proveriti identitet/ABI/dispatch, bez novog modela | Ne izmišljati threshold niti prepisivati referencu |
| G4 — baza | §6 novi server volume, restore, runtime uloga, read-only SQL | 9730/9730/9730, 768/768, cast79 i svi state/index pinovi | Restore ili boolean false: STOP i pregled svežeg test cilja | Ne dirati lokalne baze, ne pokretati migraciju/import, ne brisati volume |
| G5 — privatni API | §8 izabrani Compose ili native start, `/live`, `/ready` | Eksplicitan V6 Production, read-only veza, bez javnih portova | Readiness failure: bezbedni logovi, proveriti konfiguraciju i pinove | Stop samo novog API-ja, ne fallback na V5 i ne slabiti guard |
| G6 — granica i frontend | §9 lokalni build/Pages priprema; §10 dokaz proxy peer-a i preflight | Tačni punycode origini, bez spoofinga, odobrena numerika | Trust nije dokaziv: ostaje OFF / javni per-client gate BLOCKED | Ne verovati celom Docker subnet-u, ne koristiti wildcard CORS |
| G7 — javni pilot | §9 eksplicitno `--profile public` i Pages custom domain posle svih GO | HTTPS UI→API, health/CORS i mali ručni test odobren od korisnika | Provider/DNS/UI greške jasno odvojiti | Ugasiti tunel, sačuvati prethodni release; bez masovne evaluacije |
| G8 — resursi i održavanje | §11 realna merenja; §12 backup+restore+rollback proba | Zabeležene granice/RAM/latencije i obnovljiv backup | OOM/swap/HDD: smanjiti pilot, analizirati, bez izmišljenog SLA | Ne aktivirati proizvoljne limite ili više replika bez merenja |

Sve server G0–G8 kapije su **ZA TESTIRANJE NA SERVERU**; numerička odluka dodatno **PENDING ACCEPTANCE**. Lokalna priprema ne znači GO za javno otvaranje.

## 14. Stvarno otvorene odluke

1. Compose API ili native .NET/systemd API; oba nacrta koriste isti model/DB ugovor.
2. Potvrda kontrole domena, Cloudflare zone i konkretnih DNS/Tunnel/Pages resursa.
3. Da li zaključane native biblioteke zaista rade na i5 bez AVX2; tek nakon export-a.
4. Prihvatljiv numerički i Top-10 Windows/Linux kriterijum nakon stvarnog merenja.
5. Tačan, stabilan i dokazano ograničen cloudflared peer trust raspored pre uključivanja CF headera.
6. Konačni memory/swap/concurrency limiti, backup učestalost i disk/log retention nakon merenja.

Ne otvaraju se ponovo izbor modela, dimenzionalnost, lokalni inference, PG/pgvector, Pages Direct Upload kao preferencija ili hard-filter pravila. Javna dostupnost 24/7 zavisi i od kućnog napajanja, interneta, hlađenja/poklopca laptopa i HDD zdravlja — NOT MEASURED, nije potvrđen SLA.

## 15. Izmene i lokalne verifikacije ove pripreme

| Celina / fajlovi | Razlog / dokaz | Lokalni commit | Uticaj na Development |
|---|---|---|---|
| `src/backend/CineKros.Api/Program.cs`, `src/backend/CineKros.Api/Startup/TrustedClientIp.cs`, `tests/CineKros.Api.Tests/Startup/TrustedClientIpTests.cs` | Ograničen CF header limiter ključ, default OFF, exact peer; 33 ciljane provere | `9664914` | Bez konfiguracije ključ ostaje postojeći RemoteIpAddress; limit 30/60 isti |
| `src/backend/CineKros.Api/Program.cs`, `src/backend/CineKros.Api/Startup/StartupReadinessState.cs`, `tests/CineKros.Api.Tests/Startup/HealthEndpointTests.cs` | `/live`, cached `/ready`; 36 ciljane provere zajedno sa prethodnim grupama | `b964f45` | Novi izolovani health put; fake ostaje not-ready, real posle originalnog preflight-a |
| `src/backend/CineKros.Api/Startup/RecommendationStartup.cs`, `src/backend/CineKros.Api/Search/SearchDatasetExpectation.cs`, factory u `src/backend/CineKros.Api/Search/MovieSearchRepository.cs`, `tests/CineKros.Api.Tests/Search/V6SearchTests.cs`, `tests/CineKros.Api.Tests/Startup/RecommendationCorsTests.cs` | Novi eksplicitni Production profil; isti V6 pinovi osim ciljnog imena baze; punycode test | `c515ec1` | Stari `actor-v6-isolated` i V5 podrazumevana kompatibilnost ostaju |
| `deploy/CineKros.DeployCheck/`, novi test projekat i mala query referenca | Ista shared E5 biblioteka; 6 offline testova, Windows export30; Linux FDD publish | `2c88fd0` | Nema API reference/publish zavisnosti na alat |
| `deploy/` pakovanje, SQL/model manifest, `.dockerignore` i RID lock kopije | Namenski server šabloni, uspešan stvarni Debian image build i izolovane negativne provere; dodatno SQL ne prikriva pogrešan search_path | `a9fffb2`, `63a89a1` | Ne koristi lokalni launcher/loader niti menja source lockove |

Inventar novog deployment paketa (svi pripadaju prethodnoj packaging celini):

- `deploy/Dockerfile.api` — multi-stage build; `deploy/locks/api/packages.lock.json`, `deploy/locks/embedding/packages.lock.json`, `deploy/locks/importer/packages.lock.json` — zasebne Linux RID lock varijante bez promene dependency verzija.
- `deploy/compose.yaml` — tri servisa/mreže/volume/health; `deploy/compose.native-db.yaml` — samo opcioni host-loopback PG port.
- `deploy/compose.env.example`, `deploy/api.env.example` — bezbedni konfiguracioni primeri.
- `deploy/database-connection.example`, `deploy/gemini-api-key.example`, `deploy/postgres-admin-password.example`, `deploy/cloudflared-credentials.example.json` — placeholder/prazni secret primeri, nikada stvarne tajne.
- `deploy/start-api.sh` — eksplicitan Production V6 guard i učitavanje secret fajlova; `deploy/cinekros-api.service` — native API alternativa, bez Windows zavisnosti.
- `deploy/cloudflared/config.example.yml` — named tunnel API ingress, bez stvarnog UUID/credentials sadržaja.
- `deploy/model.SHA256SUMS`, `deploy/verify-model.sh` — zaključan inventar i provera pet assets.
- `deploy/validate-production-readiness.sql` — samo čitanje obnovljene baze; refiniran commitom `63a89a1`.
- `.dockerignore`, `deploy/.gitattributes` — minimalni build context i Unix line endings.
- `deploy/CineKros.DeployCheck/{CineKros.DeployCheck.csproj,Program.cs,AssemblyInfo.cs,packages.lock.json}`, `tests/CineKros.DeployCheck.Tests/{CineKros.DeployCheck.Tests.csproj,DeployCheckTests.cs,packages.lock.json}` i `deploy/reference/windows-e5-reference.json` — samostalna alatka/testovi/ref, commit `2c88fd0`.

Glavni dokument uveden je commitom `dd52e9e`; kasnija dokumentaciona dopuna pojašnjava provereni Windows Docker CLI i ovaj inventar. Commit `a1a469c` čuva originalne CRLF export bajtove reference; atributi zatim isključuju automatsku tekstualnu konverziju/diff/merge samo za taj generisani numerički artefakt. Njegov sadržaj se proverava JSON validacijom i SHA, ne whitespace pravilima za kôd. Tačan konačni dokumentacioni commit vidi se komandom `git log -1 -- docs/deployment/DEPLOYMENT_PLAN.md`, bez promene istorije ili push-a.

Ukupna backend regresija: **233 passed / 22 expected skipped / 0 failed** (255 slučajeva). Preskočene su opt-in DB/provider provere; nisu predstavljene kao nova integraciona/live potvrda. Frontend: **55/55 passed** i production build. API i tool **linux-x64 framework-dependent publish** uspešni. Windows referenca: 30 x 768D, konačne vrednosti, norme i SR script token identičnost provereni; server compare nije izvršen. Lokalni V6 DB runtime nije ponovo startovan radi ove pripreme i nema novih Gemini poziva.

Nezavisni read-only pregled B1/B2/B4/tool izmena nije našao bezbednosni/profile blokator; izdvojio je dvostruki startup DB preflight kao neizmeren HDD trošak. Šablon entrypoint-a dodatno zahteva eksplicitan V6 Production profil radi sprečavanja nenamernog V5 izbora. Sve prethodne akademske/browser evaluacije ostaju istorijski dokazi, nisu ponovljene niti izmenjene.

### Završna lokalna provera

READY — API Release build i postojeća solution Release build: 0 warnings / 0 errors. Deploy-check build/test 6/6; njegovi projekti ostaju samostalni i provereni zasebno, bez dodavanja API reference. Frontend build i 55 testova uspešni; backend kompletan offline regresioni skup 233/22 skipped/0 failed. Nije bilo provider poziva ni filmskih embedding generacija.

READY — stvarni Docker Desktop Linux/amd64 build završen na razvojnoj Windows mašini, sa Debian 13/glibc i ASP.NET/.NET runtime 10.0.13. Lokalni image `cinekros-api:production-prep`, ID `sha256:e2bf6d2575682359e60412027b69d0048b173d4bf5c44bfe7e1f4c8353e25c84`; `docker inspect` prijavio Size 115.123.248 bajtova. Ovo nije ukupan Docker storage overhead niti server disk merenje; ne sabirati ponovo publish koji je već u image-u. Model i baza nisu u image-u. Odvojeni API publish ima 35.506.945 bajtova. Nijedan image nije push-ovan.

READY — osnovni Compose i native-DB override prošli `config --quiet`. API/PG nemaju javni port; native override samo `127.0.0.1:5432`. Cloudflared je iza `public` profila. Ephemeral `--network none` container provere potvrdile UID 10001, shell sintaksu, prisustvo pravog V6 prompt/schema i logging settings, odsustvo modela/secret env fajlova/deploy-check DLL u API image-u, SHA svih pet spolja read-only montiranih model fajlova, odbijanje praznog/neispravnog model inventara i fail-closed start bez eksplicitnog Production profila. Te container provere nisu pokretale API listener, Gemini ili DB konekciju i nisu Linux CPU inference test.

READY — Docker context zaštita proverena stvarnim build-om. Prvi pokušaj je zaustavljen kada je primećen stari ugnježdeni `.local` build output u API folderu; dodata su rekurzivna isključenja. Zatim puni poslati context iznosio je oko 407 kB, bez modela/dumpova/secret fajlova. Linux `NETSDK1022` i zatim namerno isključeni appsettings content popravljeni su samo u Docker pakovanju; konačni image inventar je proveren. Prvi neuspešni buildovi nisu predstavljeni kao uspeh.

READY — `git diff --check` prošao. Originalni API/embedding/importer source lock fajlovi su identični početnom stanju; tri deployment RID lock kopije zadržavaju isti portable dependency graph. Git poređenje sa početnim HEAD-om potvrdilo je neizmenjene launchere, parser/prompt/schema, embedding/text-normalization izvore, SQL filter builder, migracije, frontend i `korpus.md`. Repository SQL izmene ograničene su na dodavanje zasebnog factory-ja, ne na filtriranje/sortiranje. Originalni launcher SHA-256 je i dalje `caf8ffcff271811b9c03b3a8260c8ee48a5ef993899703296f1e71d5d3a0c2c0`.

READY — završna validaciona SQL skripta jednom je stvarno izvršena nad postojećom lokalnom V6 rehearsal bazom, u sesiji sa `default_transaction_read_only=on`, uz svoj `BEGIN READ ONLY` i `ROLLBACK`. SQL sintaksa/funkcije rade: 9.730 filmova, 79 NULL cast, 9.730 EN i SR 768D/unit-norm vektora, oba state skupa, catalog/actor pinovi, sva tri migration ledger pina i 15 očekivanih validnih/ready indeksa potvrđeni. PostgreSQL 17 / pgvector 0.8.6 i public šema potvrđeni. Baza je prijavila 177 MB. Očekivano FALSE ostali su server-only identitet baze (`cinekros`) i strogi runtime-role/privilege uslovi, jer lokalna provera koristi postojeći container nalog i rehearsal ime. To nije server deployment PASS, niti dozvola da se taj lokalni nalog prenese na server.

Lokalna V5 baza nije pristupana. Nijedna lokalna baza nije menjana: nije bilo migracije/importa/restore-a, pokretanja API-ja prema njima ili menjanja PG containera/volume-a. Očuvanost je zasnovana na isključivo read-only SQL-u i odsustvu write operacija, ne na izmišljenom novom pre/posle checksum merenju. Postojeći PG container ostao je aktivan; privremeni validation container-i su automatski uklonjeni. Diplomski, prezentacija, korpus i istorijska evaluacija nisu menjani.

ZA TESTIRANJE NA SERVERU — AVX-only native inference, numerički compare i Top-10 poređenje, pravi V6 dump/restore/readonly privilegije, startup/reboot, stvarni proxy peer i spoof zaštita, TLS/DNS/Pages/Tunnel, RAM/swap/HDD/opterećenje. Numerički gate ostaje PENDING ACCEPTANCE. Priprema nije javni deployment niti potvrda da je ciljna mašina kompatibilna.
