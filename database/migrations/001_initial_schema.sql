CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE movies (
    movie_lens_id bigint PRIMARY KEY,
    imdb_id text NOT NULL UNIQUE,
    tmdb_id bigint NULL UNIQUE,
    title text NOT NULL CHECK (length(btrim(title)) > 0),
    year integer NOT NULL CHECK (year BETWEEN 1000 AND 9999),
    runtime_minutes integer NULL CHECK (runtime_minutes > 0),
    original_language text NULL CHECK (original_language IS NULL OR original_language ~ '^[a-z]{2,3}$'),
    genres text[] NULL CHECK (
        genres IS NULL OR (
            array_position(genres, NULL) IS NULL
            AND array_position(genres, '') IS NULL
        )
    ),
    average_rating numeric NULL CHECK (average_rating BETWEEN 0 AND 5),
    rating_count bigint NULL CHECK (rating_count >= 0),
    poster_path text NULL CHECK (poster_path IS NULL OR length(btrim(poster_path)) > 0),
    CHECK ((average_rating IS NULL) = (rating_count IS NULL))
);

CREATE TABLE movie_embeddings (
    movie_lens_id bigint PRIMARY KEY REFERENCES movies(movie_lens_id),
    embedding vector(768) NOT NULL,
    document_fingerprint char(64) NOT NULL CHECK (document_fingerprint ~ '^[0-9a-f]{64}$')
);

CREATE TABLE catalog_import_state (
    id smallint PRIMARY KEY CHECK (id = 1),
    catalog_version text NOT NULL,
    catalog_jsonl_sha256 char(64) NOT NULL CHECK (catalog_jsonl_sha256 ~ '^[0-9a-f]{64}$'),
    catalog_content_fingerprint char(64) NOT NULL CHECK (catalog_content_fingerprint ~ '^[0-9a-f]{64}$'),
    movie_count integer NOT NULL CHECK (movie_count = 9730),
    embedding_artifact_sha256 char(64) NULL CHECK (embedding_artifact_sha256 IS NULL OR embedding_artifact_sha256 ~ '^[0-9a-f]{64}$'),
    embedding_profile_fingerprint char(64) NULL CHECK (embedding_profile_fingerprint IS NULL OR embedding_profile_fingerprint ~ '^[0-9a-f]{64}$'),
    embedded_count integer NULL CHECK (embedded_count IS NULL OR embedded_count = 9730),
    CHECK (
        (embedding_artifact_sha256 IS NULL AND embedding_profile_fingerprint IS NULL AND embedded_count IS NULL)
        OR
        (embedding_artifact_sha256 IS NOT NULL AND embedding_profile_fingerprint IS NOT NULL AND embedded_count = 9730)
    )
);

CREATE INDEX ix_movies_year ON movies (year);
CREATE INDEX ix_movies_runtime_minutes ON movies (runtime_minutes);
CREATE INDEX ix_movies_average_rating ON movies (average_rating);
CREATE INDEX ix_movies_original_language ON movies (original_language);
CREATE INDEX ix_movies_rating_count ON movies (rating_count);
CREATE INDEX ix_movies_genres ON movies USING gin (genres);
