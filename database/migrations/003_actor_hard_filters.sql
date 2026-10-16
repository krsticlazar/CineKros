-- Owned exclusively by the atomic V6 paired importer; do not apply separately.
CREATE FUNCTION public.actor_cast_is_valid(value text[]) RETURNS boolean
LANGUAGE sql IMMUTABLE PARALLEL SAFE
AS $function$
SELECT value IS NULL OR (
    array_ndims(value) = 1 AND array_lower(value, 1) = 1
    AND cardinality(value) > 0
    AND NOT EXISTS (SELECT 1 FROM unnest(value) AS actor(name)
                    WHERE name IS NULL OR name ~ '^[[:space:]]*$')
    AND cardinality(value) = (SELECT count(DISTINCT name COLLATE "C") FROM unnest(value) AS actor(name))
)
$function$;

ALTER TABLE public.movies ADD COLUMN "cast" text[] NULL,
    ADD CONSTRAINT ck_movies_cast CHECK (public.actor_cast_is_valid("cast"));
CREATE INDEX ix_movies_cast ON public.movies USING gin ("cast");

CREATE TABLE public.actor_catalog_state (
    id smallint PRIMARY KEY CHECK (id = 1),
    schema_version text NOT NULL CHECK (schema_version = 'actor-catalog-state-v1'),
    release_version text NOT NULL CHECK (release_version = 'CineKros-bilingual-actor-catalog-v6'),
    catalog_identity_sha256 char(64) NOT NULL CHECK (catalog_identity_sha256 ~ '^[0-9a-f]{64}$'),
    actor_vocabulary_sha256 char(64) NOT NULL CHECK (actor_vocabulary_sha256 ~ '^[0-9a-f]{64}$'),
    ordered_cast_mapping_sha256 char(64) NOT NULL CHECK (ordered_cast_mapping_sha256 ~ '^[0-9a-f]{64}$'),
    overrides_sha256 char(64) NOT NULL CHECK (overrides_sha256 ~ '^[0-9a-f]{64}$'),
    cast_rules_sha256 char(64) NOT NULL CHECK (cast_rules_sha256 ~ '^[0-9a-f]{64}$'),
    paired_descriptor_sha256 char(64) NOT NULL CHECK (paired_descriptor_sha256 ~ '^[0-9a-f]{64}$'),
    baseline_descriptor_sha256 char(64) NOT NULL CHECK (baseline_descriptor_sha256 ~ '^[0-9a-f]{64}$'),
    schema_sha256 char(64) NOT NULL CHECK (schema_sha256 ~ '^[0-9a-f]{64}$')
);
