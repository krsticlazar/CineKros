# Database and Data Preparation

Phase 0 directory skeleton only. No Compose service, schema, migration, or ETL implementation exists yet.

- `docker/`: future PostgreSQL + pgvector Compose configuration (Phase C).
- `migrations/`: future schema migrations (Phase C).
- `etl/`: future C# exploration/transformation/enrichment/embedding tools (Phase B).
- `data/`: local-only raw/derived/catalog/vector/cache content; only its README is tracked.

MovieLens Tag Genome 2021 is the **selected and only MovieLens dataset**. Its local extracted directory is `data/raw/tag-genome-2021/`; the archive is `data/raw/genome_2021.zip`. See [technical decisions](../docs/DECISIONS.md) and [data layout](data/README.md).

Compare glmer/tagdl before choosing a representation. Keep bulky source reviews, ratings, and score rows out of the runtime database unless an approved experiment needs them. TMDB metadata is cached during preparation. Semantic text and usage are reviewed before bulk embedding.
