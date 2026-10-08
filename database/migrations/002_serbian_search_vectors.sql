ALTER TABLE movie_embeddings
    ADD COLUMN embedding_sr vector(768) NULL,
    ADD COLUMN document_fingerprint_sr char(64) NULL,
    ADD CONSTRAINT ck_movie_embeddings_sr_fingerprint
        CHECK (document_fingerprint_sr IS NULL OR document_fingerprint_sr ~ '^[0-9a-f]{64}$'),
    ADD CONSTRAINT ck_movie_embeddings_sr_pair
        CHECK ((embedding_sr IS NULL) = (document_fingerprint_sr IS NULL));

CREATE TABLE embedding_set_state (
    language text PRIMARY KEY CHECK (language IN ('en', 'sr')),
    profile_fingerprint char(64) NOT NULL CHECK (profile_fingerprint ~ '^[0-9a-f]{64}$'),
    catalog_content_fingerprint char(64) NOT NULL CHECK (catalog_content_fingerprint ~ '^[0-9a-f]{64}$'),
    corpus_sha256 char(64) NOT NULL CHECK (corpus_sha256 ~ '^[0-9a-f]{64}$'),
    text_format_version text NOT NULL,
    artifact_sha256 char(64) NOT NULL CHECK (artifact_sha256 ~ '^[0-9a-f]{64}$'),
    translation_dictionary_sha256 char(64) NULL CHECK (translation_dictionary_sha256 IS NULL OR translation_dictionary_sha256 ~ '^[0-9a-f]{64}$'),
    dimension integer NOT NULL CHECK (dimension = 768),
    embedded_count integer NOT NULL CHECK (embedded_count = 9730),
    CHECK ((language = 'en' AND translation_dictionary_sha256 IS NULL) OR
           (language = 'sr' AND translation_dictionary_sha256 IS NOT NULL))
);
