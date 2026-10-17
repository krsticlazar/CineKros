-- Read-only preflight for the exact V6 release expected by actor-v6-production.
-- Connect as the dedicated cinekrosreadonly role to database cinekros.
BEGIN TRANSACTION READ ONLY;
-- Inspect the caller's effective schema; do not force public and hide a bad search_path.

SELECT current_database() = 'cinekros' AS expected_database,
       current_user = 'cinekrosreadonly' AS expected_runtime_role,
       current_schema() = 'public' AS expected_schema,
       current_setting('transaction_read_only') = 'on' AS transaction_is_read_only,
       NOT (SELECT rolsuper OR rolcreaterole OR rolcreatedb OR rolbypassrls
            FROM pg_roles WHERE rolname = current_user) AS role_is_not_privileged,
       NOT EXISTS (
         SELECT 1
         FROM unnest(ARRAY['public.movies', 'public.movie_embeddings', 'public.catalog_import_state',
                           'public.embedding_set_state', 'public.actor_catalog_state', 'public.schema_migrations']) AS target(table_name)
         CROSS JOIN unnest(ARRAY['INSERT', 'UPDATE', 'DELETE', 'TRUNCATE']) AS privilege(privilege_name)
         WHERE has_table_privilege(current_user, target.table_name, privilege.privilege_name)
       ) AS role_has_no_write_privileges,
       current_setting('server_version_num')::integer / 10000 = 17 AS postgres_major_is_17,
       (SELECT extversion = '0.8.6' FROM pg_extension WHERE extname = 'vector') AS pgvector_is_0_8_6,
       pg_size_pretty(pg_database_size(current_database())) AS database_size;

SELECT count(*) AS movie_count,
       count(*) = 9730 AS movie_count_matches,
       count(*) FILTER (WHERE "cast" IS NULL) AS null_cast_count,
       count(*) FILTER (WHERE "cast" IS NULL) = 79 AS null_cast_count_matches
FROM public.movies;

SELECT count(*) AS embedding_rows,
       count(*) = 9730 AS row_count_matches,
       count(*) FILTER (WHERE public.vector_dims(embedding) = 768) AS english_768d_rows,
       count(*) FILTER (WHERE public.vector_dims(embedding) = 768) = 9730 AS all_english_vectors_768d,
       count(*) FILTER (WHERE abs(public.vector_norm(embedding) - 1) <= 0.0001) AS english_unit_norm_rows,
       count(*) FILTER (WHERE embedding_sr IS NOT NULL) AS serbian_rows,
       count(*) FILTER (WHERE embedding_sr IS NOT NULL AND public.vector_dims(embedding_sr) = 768) AS serbian_768d_rows,
       count(*) FILTER (WHERE embedding_sr IS NOT NULL AND public.vector_dims(embedding_sr) = 768) = 9730 AS all_serbian_vectors_768d,
       count(*) FILTER (WHERE embedding_sr IS NOT NULL AND abs(public.vector_norm(embedding_sr) - 1) <= 0.0001) AS serbian_unit_norm_rows
FROM public.movie_embeddings;

SELECT count(*) = 1
       AND bool_and(catalog_version = 'CineKros-bilingual-actor-catalog-v6'
         AND btrim(catalog_jsonl_sha256) = '30b9b7ee9ef0f94cb1427e4b1921a296ba3692c621ac869c17e8c45dff3a7925'
         AND btrim(catalog_content_fingerprint) = '03e452c345576b5f369834e89016d43902259aa1903697b8b92123e4ea66f5d3'
         AND movie_count = 9730
         AND btrim(embedding_artifact_sha256) = '4593ce51cc7b981e07dba50cfe950d0daa3d1cc6a74d43a47c1038e341c5ff09'
         AND btrim(embedding_profile_fingerprint) = 'eac906ed78f7863573d13c9b0435de1b8f848fe92fc6ae08aa3621400260b1fe'
         AND embedded_count = 9730) AS catalog_state_matches_v6_pins
FROM public.catalog_import_state WHERE id = 1;

SELECT count(*) = 2
       AND count(*) FILTER (WHERE language = 'en'
         AND btrim(profile_fingerprint) = 'eac906ed78f7863573d13c9b0435de1b8f848fe92fc6ae08aa3621400260b1fe'
         AND btrim(catalog_content_fingerprint) = '03e452c345576b5f369834e89016d43902259aa1903697b8b92123e4ea66f5d3'
         AND btrim(corpus_sha256) = '38355e10039d885161e3523ee1e5a66411d80d7f35b57992db83d75fe8242c6e'
         AND text_format_version = 'en-title-year-director-tags-v6'
         AND btrim(artifact_sha256) = '4593ce51cc7b981e07dba50cfe950d0daa3d1cc6a74d43a47c1038e341c5ff09'
         AND translation_dictionary_sha256 IS NULL AND dimension = 768 AND embedded_count = 9730) = 1
       AND count(*) FILTER (WHERE language = 'sr'
         AND btrim(profile_fingerprint) = 'eac906ed78f7863573d13c9b0435de1b8f848fe92fc6ae08aa3621400260b1fe'
         AND btrim(catalog_content_fingerprint) = '03e452c345576b5f369834e89016d43902259aa1903697b8b92123e4ea66f5d3'
         AND btrim(corpus_sha256) = '0b11455b7a3159d4fe7e7924d36521907112e03d7381c394c40599a57c4ecad1'
         AND text_format_version = 'sr-title-year-director-tags-latn-v6'
         AND btrim(artifact_sha256) = '5f62b1351d691204b273b55693ab54e37e04ece57a50df9d2cc8e68306d98275'
         AND btrim(translation_dictionary_sha256) = '917ba3ea759b6d6595c78bf1ebcd3ddc547f00914156f3ab2fb5064fc549a715'
         AND dimension = 768 AND embedded_count = 9730) = 1 AS language_states_match_v6_pins
FROM public.embedding_set_state;

SELECT count(*) = 1
       AND bool_and(schema_version = 'actor-catalog-state-v1'
         AND release_version = 'CineKros-bilingual-actor-catalog-v6'
         AND btrim(catalog_identity_sha256) = '03e452c345576b5f369834e89016d43902259aa1903697b8b92123e4ea66f5d3'
         AND btrim(actor_vocabulary_sha256) = '7427b92ae216dfc59f53d46e0a936971fab93015ce5807e3991e59b01e960de3'
         AND btrim(ordered_cast_mapping_sha256) = '7de357a36207770d772ea351fedbeea7146b4dc6e13a41d3a518189523b2c0c8'
         AND btrim(overrides_sha256) = '3078b4c0ddbe59d6e6f5087d0ea7254d764e36a6f905c1c91ce699c3f38ba33d'
         AND btrim(cast_rules_sha256) = '9c34722165a8e6d15a950c95bccf0eed27d1bffc85ee71c346c73cabddd4ec0d'
         AND btrim(paired_descriptor_sha256) = '68011ca794b2adfb1ddc5058aa940d40b82cd8fb7ae64a7da40f8ea583da3bc5'
         AND btrim(baseline_descriptor_sha256) = '555a7aadf44ea88001bc903dab60a2d96c470b236e3b4b5d273733452d2fa0df'
         AND btrim(schema_sha256) = '1975494c9655a14e08731ecd583673a8fbeb76c46f16078d0e4612fab61d6407') AS actor_state_matches_v6_pins
FROM public.actor_catalog_state WHERE id = 1;

WITH expected(version, sha256) AS (VALUES
    ('001_initial_schema', '14e5ae626c2288596d563482a510dcb8c79320dd5d41955cc5f06579dd59f238'),
    ('002_serbian_search_vectors', '857b675a33f5ef67670b6f0e2b3c5383e5454494cafe88e707ce12f50eb3b8e3'),
    ('003_actor_hard_filters', '66eb1ea4a8130e49db24a9ccee29bad13aab7ed51cd385279f7188d00def1daa')),
actual AS (
    SELECT version, btrim(sha256) AS sha256 FROM public.schema_migrations
)
SELECT (SELECT count(*) FROM actual) = 3
       AND NOT EXISTS (SELECT 1 FROM expected e FULL JOIN actual a USING (version, sha256) WHERE e.version IS NULL OR a.version IS NULL)
       AS migration_ledger_matches_exact_001_to_003;

WITH expected(name) AS (VALUES
    ('actor_catalog_state_pkey'), ('catalog_import_state_pkey'), ('embedding_set_state_pkey'),
    ('movie_embeddings_pkey'), ('ix_movies_average_rating'), ('ix_movies_cast'), ('ix_movies_genres'),
    ('ix_movies_original_language'), ('ix_movies_rating_count'), ('ix_movies_runtime_minutes'),
    ('ix_movies_year'), ('movies_imdb_id_key'), ('movies_pkey'), ('movies_tmdb_id_key'),
    ('schema_migrations_pkey')),
actual AS (
    SELECT index_class.relname AS name, index_meta.indisvalid, index_meta.indisready
    FROM pg_index index_meta
    JOIN pg_class index_class ON index_class.oid = index_meta.indexrelid
    JOIN pg_namespace index_schema ON index_schema.oid = index_class.relnamespace
    WHERE index_schema.nspname = 'public'
      AND index_meta.indrelid IN ('public.movies'::regclass, 'public.movie_embeddings'::regclass,
          'public.catalog_import_state'::regclass, 'public.embedding_set_state'::regclass,
          'public.actor_catalog_state'::regclass, 'public.schema_migrations'::regclass)
)
SELECT (SELECT count(*) FROM actual) = 15
       AND NOT EXISTS (SELECT 1 FROM expected e LEFT JOIN actual a USING (name) WHERE a.name IS NULL OR NOT a.indisvalid OR NOT a.indisready)
       AS required_schema_indexes_valid_and_ready;

ROLLBACK;
