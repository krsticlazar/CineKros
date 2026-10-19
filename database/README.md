# Database and data preparation

The repository includes PostgreSQL 17 with pgvector 0.8.6, SQL migrations, catalog and vector import tools, and offline ETL. The search API applies SQL hard filters before exact pgvector similarity ranking. PostgreSQL runs through the Compose service in `docker/compose.yaml` with a persistent named volume.

- `migrations/`: numbered, checksummed SQL migrations.
- `../src/etl/CineKros.Catalog.Importer/`: catalog validation and transactional import.
- `../src/etl/CineKros.Database.Migrator/`: migration command-line tool.
- `../src/etl/CineKros.VectorImporter/`: compatible E5 document-vector import.
- `../src/etl/CineKros.Etl/`: transformations, enrichment and vector generation.
- `data/`: local-only raw sources, derived catalogs, model files, vectors and caches.

## Local PostgreSQL startup

Start Docker and the existing PostgreSQL Compose service before running [`../src/start_script.cmd`](../src/start_script.cmd) on Windows. The launcher checks database availability before launching the API and frontend; it does not create or start the database service. In real mode, it obtains `CINEKROS_POSTGRES_PASSWORD` and `GEMINI_API_KEY` from existing process variables or the ignored root `.env`; values are passed to child processes without being printed. The startup script does not migrate the database, import data, or reset the persistent volume.

The default local V6 catalog has 9,730 movies in the prepared isolated database `cinekros_v6_rehearsal_20261015t2154z_4fd266aa`; `--v5` explicitly selects the older `cinekros` database. MovieLens Tag Genome 2021 supplies the primary semantic tags; MovieLens 32M provides structured enrichment, and TMDB metadata is collected offline. The local `intfloat/multilingual-e5-base` INT8 ONNX encoder produces 768-dimensional query vectors. Separate EN/SR document columns contain 9,730 vectors each. Actor ALL/ANY/exclude filters use the recorded `cast` array. The importer and startup readiness checks validate catalog/model/artifact compatibility. Raw datasets, generated data, model files and vectors are excluded from version control.
