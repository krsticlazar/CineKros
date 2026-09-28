# Database and data preparation

The repository includes PostgreSQL 17 with pgvector 0.8.6, SQL migrations, catalog and vector import tools, and offline ETL. The search API applies SQL hard filters before exact pgvector similarity ranking. PostgreSQL runs through the Compose service in `docker/compose.yaml` with a persistent named volume.

- `migrations/`: numbered, checksummed SQL migrations.
- `../src/etl/CineKros.Catalog.Importer/`: catalog validation and transactional import.
- `../src/etl/CineKros.Database.Migrator/`: migration command-line tool.
- `../src/etl/CineKros.VectorImporter/`: compatible E5 document-vector import.
- `../src/etl/CineKros.Etl/`: transformations, enrichment and vector generation.
- `data/`: local-only raw sources, derived catalogs, model files, vectors and caches.

## Local PostgreSQL startup

Run [`../src/start_script.cmd`](../src/start_script.cmd) on Windows to start the Compose service and wait for its healthcheck before launching the API and frontend. In real mode, the launcher obtains `CINEKROS_POSTGRES_PASSWORD` and `GEMINI_API_KEY` from existing process variables or the ignored root `.env`; values are passed to child processes without being printed. The startup script does not migrate the database, import data, or reset the persistent volume.

The catalog has 9,730 movies. MovieLens Tag Genome 2021 supplies the primary semantic tags; MovieLens 32M provides approved structured enrichment, and TMDB metadata is collected offline. Local E5-base-v2 INT8 ONNX document vectors and runtime query embeddings have 768 dimensions. The vector importer validates artifact compatibility with the catalog and model profile. Raw datasets, generated data, model files and vectors are excluded from version control.
