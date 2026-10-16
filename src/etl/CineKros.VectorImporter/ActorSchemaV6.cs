using System.Text.Json;
using Npgsql;

namespace CineKros.VectorImporter;

/// <summary>OID-free, complete public schema evidence, including the cast validator implementation.</summary>
internal static class ActorSchemaV6
{
    // Fresh read-only 001+002 production capture, reviewed against immutable migration definitions.
    // This pins every CHECK expression, name, PK/FK, collation and index, not merely their counts.
    internal const string BaseSchemaSha256 = "bbb1043248642e9088a113b6d433d6c86a048b601ac1c08d8955929764f56293";
    // A single stable SQL result is hashed as its exact UTF-8 text. Extension-owned objects are excluded.
    // @base=true projects out only the precise additions of canonical migration 003.
    internal const string SchemaSql = """
        WITH relations AS (
          SELECT c.* FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
          WHERE n.nspname='public' AND c.relkind NOT IN ('i','I','t')
            AND NOT EXISTS(SELECT 1 FROM pg_depend d WHERE d.classid='pg_class'::regclass AND d.objid=c.oid AND d.deptype='e')
            AND NOT (@base AND c.relname='actor_catalog_state')
        )
        SELECT json_build_object(
          'tables', COALESCE((SELECT json_agg(json_build_array(relname,relkind::text,relrowsecurity,relforcerowsecurity,relreplident::text,reloptions) ORDER BY relname COLLATE "C") FROM relations),'[]'::json),
          'columns', COALESCE((SELECT json_agg(json_build_array(c.relname,a.attname,format_type(a.atttypid,a.atttypmod),a.attnotnull,
             pg_get_expr(ad.adbin,ad.adrelid),a.attgenerated::text,a.attidentity::text,coll.collname) ORDER BY c.relname COLLATE "C",a.attnum)
             FROM relations c JOIN pg_attribute a ON a.attrelid=c.oid LEFT JOIN pg_attrdef ad ON ad.adrelid=c.oid AND ad.adnum=a.attnum
             LEFT JOIN pg_collation coll ON coll.oid=a.attcollation WHERE a.attnum>0 AND NOT a.attisdropped
             AND NOT (@base AND c.relname='movies' AND a.attname='cast')),'[]'::json),
          'constraints', COALESCE((SELECT json_agg(json_build_array(c.relname,k.conname,k.contype::text,pg_get_constraintdef(k.oid,false),k.convalidated,k.condeferrable,k.condeferred)
             ORDER BY c.relname COLLATE "C",k.conname COLLATE "C") FROM relations c JOIN pg_constraint k ON k.conrelid=c.oid
             WHERE NOT (@base AND c.relname='movies' AND k.conname='ck_movies_cast')),'[]'::json),
          'indexes', COALESCE((SELECT json_agg(json_build_array(c.relname,i.relname,pg_get_indexdef(i.oid),x.indisvalid,x.indisready,x.indisunique,x.indisprimary)
             ORDER BY c.relname COLLATE "C",i.relname COLLATE "C") FROM relations c JOIN pg_index x ON x.indrelid=c.oid JOIN pg_class i ON i.oid=x.indexrelid
             WHERE NOT (@base AND c.relname='movies' AND i.relname='ix_movies_cast')),'[]'::json),
          'functions', COALESCE((SELECT json_agg(json_build_array(p.proname,pg_get_function_identity_arguments(p.oid),pg_get_function_result(p.oid),l.lanname,
             p.provolatile::text,p.proparallel::text,p.prosecdef,p.proisstrict,p.proconfig,pg_get_functiondef(p.oid)) ORDER BY p.proname COLLATE "C",pg_get_function_identity_arguments(p.oid) COLLATE "C")
             FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace JOIN pg_language l ON l.oid=p.prolang
             WHERE n.nspname='public' AND NOT EXISTS(SELECT 1 FROM pg_depend d WHERE d.classid='pg_proc'::regclass AND d.objid=p.oid AND d.deptype='e')
             AND NOT (@base AND p.proname='actor_cast_is_valid')),'[]'::json),
          'triggers', COALESCE((SELECT json_agg(json_build_array(c.relname,t.tgname,t.tgenabled::text,pg_get_triggerdef(t.oid,false)) ORDER BY c.relname COLLATE "C",t.tgname COLLATE "C")
             FROM relations c JOIN pg_trigger t ON t.tgrelid=c.oid WHERE NOT t.tgisinternal),'[]'::json),
          'policies', COALESCE((SELECT json_agg(json_build_array(c.relname,p.polname,p.polcmd::text,p.polpermissive,pg_get_expr(p.polqual,p.polrelid),pg_get_expr(p.polwithcheck,p.polrelid)) ORDER BY c.relname COLLATE "C",p.polname COLLATE "C")
             FROM relations c JOIN pg_policy p ON p.polrelid=c.oid),'[]'::json)
        )::text
        """;

    internal static async Task<string> ReadAsync(NpgsqlConnection c, NpgsqlTransaction tx, bool baseProjection, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(SchemaSql, c, tx);
        command.Parameters.AddWithValue("base", baseProjection);
        return (string?)await command.ExecuteScalarAsync(token) ?? throw new InvalidOperationException("Schema snapshot unavailable.");
    }

    private static readonly IReadOnlyDictionary<string, (string Name, string Type, bool Required)[]> Columns =
        new Dictionary<string, (string, string, bool)[]>(StringComparer.Ordinal)
        {
            ["catalog_import_state"] = [("id","smallint",true),("catalog_version","text",true),("catalog_jsonl_sha256","character(64)",true),("catalog_content_fingerprint","character(64)",true),("movie_count","integer",true),("embedding_artifact_sha256","character(64)",false),("embedding_profile_fingerprint","character(64)",false),("embedded_count","integer",false)],
            ["embedding_set_state"] = [("language","text",true),("profile_fingerprint","character(64)",true),("catalog_content_fingerprint","character(64)",true),("corpus_sha256","character(64)",true),("text_format_version","text",true),("artifact_sha256","character(64)",true),("translation_dictionary_sha256","character(64)",false),("dimension","integer",true),("embedded_count","integer",true)],
            ["movie_embeddings"] = [("movie_lens_id","bigint",true),("embedding","vector(768)",true),("document_fingerprint","character(64)",true),("embedding_sr","vector(768)",false),("document_fingerprint_sr","character(64)",false)],
            ["movies"] = [("movie_lens_id","bigint",true),("imdb_id","text",true),("tmdb_id","bigint",false),("title","text",true),("year","integer",true),("runtime_minutes","integer",false),("original_language","text",false),("genres","text[]",false),("average_rating","numeric",false),("rating_count","bigint",false),("poster_path","text",false)],
            ["schema_migrations"] = [("version","text",true),("sha256","character(64)",true),("applied_at","timestamp with time zone",true)]
        };
    private static readonly string[] BaseIndexes = ["catalog_import_state_pkey", "embedding_set_state_pkey", "movie_embeddings_pkey", "ix_movies_average_rating", "ix_movies_genres", "ix_movies_original_language", "ix_movies_rating_count", "ix_movies_runtime_minutes", "ix_movies_year", "movies_imdb_id_key", "movies_pkey", "movies_tmdb_id_key", "schema_migrations_pkey"];

    internal static void ValidateBaseSnapshot(string snapshot)
    {
        var root = ActorCatalogV6Json(snapshot);
        ActorImportBaselineV6.ExactFields(root, ["tables", "columns", "constraints", "indexes", "functions", "triggers", "policies"]);
        var tables = root.GetProperty("tables").EnumerateArray().ToArray();
        if (!tables.Select(x => x[0].GetString()).SequenceEqual(Columns.Keys) || tables.Any(x => x[1].GetString() != "r" || x[2].GetBoolean() || x[3].GetBoolean() || x[4].GetString() != "d" || x[5].ValueKind != JsonValueKind.Null))
            throw new InvalidDataException("Baseline contains unexpected tables, row security or table options.");
        foreach (var table in Columns)
        {
            var actual = root.GetProperty("columns").EnumerateArray().Where(x => x[0].GetString() == table.Key).ToArray();
            if (actual.Length != table.Value.Length) throw new InvalidDataException("Baseline columns are incomplete or unexpected.");
            for (var i = 0; i < actual.Length; i++)
            {
                var a = actual[i]; var e = table.Value[i];
                if (a[1].GetString() != e.Name || a[2].GetString() != e.Type || a[3].GetBoolean() != e.Required || a[4].ValueKind != JsonValueKind.Null || a[5].GetString() != "" || a[6].GetString() != "")
                    throw new InvalidDataException("Baseline column name/type/nullability/default differs from canonical 001+002.");
            }
        }
        var indexes = root.GetProperty("indexes").EnumerateArray().ToArray();
        if (!indexes.Select(x => x[1].GetString()).SequenceEqual(BaseIndexes) || indexes.Any(x => !x[3].GetBoolean() || !x[4].GetBoolean()))
            throw new InvalidDataException("Baseline PK/unique/filter indexes are incomplete or invalid.");
        foreach (var i in indexes)
        {
            var n = i[1].GetString()!; var table = i[0].GetString()!;
            var column = n switch { "movies_imdb_id_key" => "imdb_id", "movies_tmdb_id_key" => "tmdb_id", "catalog_import_state_pkey" => "id", "embedding_set_state_pkey" => "language", "schema_migrations_pkey" => "version", "movie_embeddings_pkey" or "movies_pkey" => "movie_lens_id", _ => n["ix_movies_".Length..] };
            var unique = n.EndsWith("_pkey", StringComparison.Ordinal) || n.EndsWith("_key", StringComparison.Ordinal);
            var expected = $"CREATE {(unique ? "UNIQUE " : "")}INDEX {n} ON public.{table} USING {(column == "genres" ? "gin" : "btree")} ({column})";
            if (i[2].GetString() != expected || i[5].GetBoolean() != unique || i[6].GetBoolean() != n.EndsWith("_pkey", StringComparison.Ordinal))
                throw new InvalidDataException("Baseline index definition differs from canonical 001+002.");
        }
        if (new[] { "functions", "triggers", "policies" }.Any(x => root.GetProperty(x).GetArrayLength() != 0))
            throw new InvalidDataException("Baseline has unexpected functions, user triggers or policies.");
        var constraints = root.GetProperty("constraints").EnumerateArray().ToArray();
        if (constraints.Length != 38 || constraints.Any(x => !x[4].GetBoolean() || x[5].GetBoolean() || x[6].GetBoolean()))
            throw new InvalidDataException("Baseline constraints are incomplete, unvalidated or deferred.");
        // The full text, including all CHECK expressions, is bound by the descriptor and compared under locks.
        var foreignKeys = constraints.Where(x => x[2].GetString() == "f").ToArray();
        if (foreignKeys.Length != 1 || foreignKeys[0][0].GetString() != "movie_embeddings" || foreignKeys[0][3].GetString() != "FOREIGN KEY (movie_lens_id) REFERENCES movies(movie_lens_id)")
            throw new InvalidDataException("Baseline movie/vector foreign key differs from canonical 001+002.");
        if (ActorImportBaselineV6.Hash(snapshot) != BaseSchemaSha256)
            throw new InvalidDataException("Full baseline schema differs from the freshly reviewed canonical 001+002 schema fingerprint.");
    }

    internal static void ValidateFinalSnapshot(string snapshot, string migration003)
    {
        var root = ActorCatalogV6Json(snapshot);
        var actorTable = root.GetProperty("tables").EnumerateArray().SingleOrDefault(x => x[0].GetString() == "actor_catalog_state");
        if (actorTable.ValueKind == JsonValueKind.Undefined || actorTable[1].GetString() != "r" || actorTable[2].GetBoolean() || actorTable[3].GetBoolean() || actorTable[4].GetString() != "d" || actorTable[5].ValueKind != JsonValueKind.Null ||
            root.GetProperty("triggers").GetArrayLength() != 0 || root.GetProperty("policies").GetArrayLength() != 0)
            throw new InvalidOperationException("V6 contains unexpected table options, triggers or policies.");
        var functions = root.GetProperty("functions").EnumerateArray().ToArray();
        var body = migration003.Split("$function$", StringSplitOptions.None)[1];
        if (functions.Length != 1 || functions[0][0].GetString() != "actor_cast_is_valid" || functions[0][1].GetString() != "value text[]" ||
            functions[0][2].GetString() != "boolean" || functions[0][3].GetString() != "sql" || functions[0][4].GetString() != "i" || functions[0][5].GetString() != "s" ||
            functions[0][6].GetBoolean() || functions[0][7].GetBoolean() || functions[0][8].ValueKind != JsonValueKind.Null ||
            !functions[0][9].GetString()!.Contains("$function$" + body + "$function$", StringComparison.Ordinal))
            throw new InvalidOperationException("V6 cast validator implementation or execution attributes differ from canonical migration 003.");
        var cast = root.GetProperty("columns").EnumerateArray().Where(x => x[0].GetString() == "movies" && x[1].GetString() == "cast").ToArray();
        if (cast.Length != 1 || cast[0][2].GetString() != "text[]" || cast[0][3].GetBoolean() || cast[0][4].ValueKind != JsonValueKind.Null || cast[0][5].GetString() != "" || cast[0][6].GetString() != "")
            throw new InvalidOperationException("V6 cast column differs from canonical migration 003.");
        var constraint = root.GetProperty("constraints").EnumerateArray().Where(x => x[0].GetString() == "movies" && x[1].GetString() == "ck_movies_cast").ToArray();
        if (constraint.Length != 1 || constraint[0][3].GetString() != "CHECK (actor_cast_is_valid(\"cast\"))" || !constraint[0][4].GetBoolean() || constraint[0][5].GetBoolean() || constraint[0][6].GetBoolean())
            throw new InvalidOperationException("V6 cast constraint differs from canonical migration 003.");
        var index = root.GetProperty("indexes").EnumerateArray().Where(x => x[1].GetString() == "ix_movies_cast").ToArray();
        if (index.Length != 1 || index[0][2].GetString() != "CREATE INDEX ix_movies_cast ON public.movies USING gin (\"cast\")" || !index[0][3].GetBoolean() || !index[0][4].GetBoolean() || index[0][5].GetBoolean())
            throw new InvalidOperationException("V6 cast GIN index differs from canonical migration 003.");
        var stateColumns = root.GetProperty("columns").EnumerateArray().Where(x => x[0].GetString() == "actor_catalog_state").ToArray();
        string[] expected = ["id", "schema_version", "release_version", "catalog_identity_sha256", "actor_vocabulary_sha256", "ordered_cast_mapping_sha256", "overrides_sha256", "cast_rules_sha256", "paired_descriptor_sha256", "baseline_descriptor_sha256", "schema_sha256"];
        if (!stateColumns.Select(x => x[1].GetString()).SequenceEqual(expected) || stateColumns.Where((x, i) => x[2].GetString() != (i == 0 ? "smallint" : i < 3 ? "text" : "character(64)") || !x[3].GetBoolean() || x[4].ValueKind != JsonValueKind.Null || x[5].GetString() != "" || x[6].GetString() != "").Any())
            throw new InvalidOperationException("V6 actor state columns differ from canonical migration 003.");
        var stateConstraints = root.GetProperty("constraints").EnumerateArray().Where(x => x[0].GetString() == "actor_catalog_state").ToArray();
        if (stateConstraints.Length != 12 || stateConstraints.Any(x => !x[4].GetBoolean() || x[5].GetBoolean() || x[6].GetBoolean()))
            throw new InvalidOperationException("V6 actor state constraints are incomplete.");
        foreach (var name in expected)
        {
            var check = stateConstraints.SingleOrDefault(x => x[1].GetString() == "actor_catalog_state_" + name + "_check");
            var expression = name switch { "id" => "CHECK ((id = 1))", "schema_version" => "CHECK ((schema_version = 'actor-catalog-state-v1'::text))", "release_version" => "CHECK ((release_version = 'CineKros-bilingual-actor-catalog-v6'::text))", _ => $"CHECK (({name} ~ '^[0-9a-f]{{64}}$'::text))" };
            if (check.ValueKind == JsonValueKind.Undefined || check[3].GetString() != expression) throw new InvalidOperationException("V6 actor state check differs from canonical migration 003.");
        }
        var stateIndexes = root.GetProperty("indexes").EnumerateArray().Where(x => x[0].GetString() == "actor_catalog_state").ToArray();
        if (stateIndexes.Length != 1 || stateIndexes[0][2].GetString() != "CREATE UNIQUE INDEX actor_catalog_state_pkey ON public.actor_catalog_state USING btree (id)" || !stateIndexes[0][3].GetBoolean() || !stateIndexes[0][4].GetBoolean() || !stateIndexes[0][5].GetBoolean() || !stateIndexes[0][6].GetBoolean())
            throw new InvalidOperationException("V6 actor state PK differs from canonical migration 003.");
    }
    private static JsonElement ActorCatalogV6Json(string text) => CineKros.Catalog.Importer.ActorCatalogV6Reader.ReadStrictObject(System.Text.Encoding.UTF8.GetBytes(text));
}
