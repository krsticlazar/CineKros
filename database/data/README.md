# Local Research Data — Do Not Commit

Everything below this directory except this README is ignored, including archives, raw/extracted files, derived catalogs, vectors, provider caches, and local manifests containing raw data. Future tools/manual instructions can create `raw/`, `derived/`, `embeddings/`, and `cache/` here.

MovieLens Tag Genome 2021 is the selected research dataset. Its archive and extracted content are available locally; do not commit them or duplicate them into development worktrees.

- Archive: `raw/genome_2021.zip`.
- Extracted root: `raw/tag-genome-2021/` (originally `raw/movie_dataset_public_final/`).
- Local inventory/hash record: `raw/local-dataset-manifest.json`.

Both archive and extracted content remain local/ignored. The extraction contains 58 files totaling 6,243,526,565 bytes; this is a storage inventory, not an EDA result or a verified movie count.

Never force-add local data. Place small sanitized manifests/configurations, hashes, aggregate findings, and approved evidence under `../../Testiranje/` after review so the study remains reproducible without publishing raw data.
