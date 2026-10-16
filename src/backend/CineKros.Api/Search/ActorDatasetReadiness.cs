using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CineKros.Api.RealProviders;
using Npgsql;

namespace CineKros.Api.Search;

/// <summary>Read-only verification of the exact V6 actor release, including film assignments.</summary>
public static class ActorDatasetReadiness
{
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    // Deliberately matches ActorSchemaV6.SchemaSql's exact UTF-8 serialization recipe.
    // This runtime boundary must not acquire an importer dependency or execute importer code.
    private const string SchemaSql = """
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

    public static async Task EnsurePinsAsync(NpgsqlConnection connection, SearchDatasetExpectation e, CancellationToken ct = default)
    {
        if (e.Actors is null) throw Unavailable();
        const string sql = """
            SELECT current_database()=@db AND current_schema()='public'
            AND current_setting('default_transaction_read_only')='on'
            AND (SELECT count(*) FROM actor_catalog_state)=1
            AND EXISTS(SELECT 1 FROM actor_catalog_state WHERE id=1 AND schema_version='actor-catalog-state-v1'
                AND release_version=@version AND catalog_identity_sha256=@identity AND actor_vocabulary_sha256=@vocab
                AND ordered_cast_mapping_sha256=@map AND overrides_sha256=@overrides AND cast_rules_sha256=@rules
                AND paired_descriptor_sha256=@pair AND baseline_descriptor_sha256=@baseline AND schema_sha256=@schema)
            """;
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("db", e.DatabaseName); command.Parameters.AddWithValue("version", e.CatalogVersion);
        command.Parameters.AddWithValue("identity", e.CatalogIdentity);
        command.Parameters.AddWithValue("vocab", V6SearchDatasetExpectation.VocabularySha256);
        command.Parameters.AddWithValue("map", V6SearchDatasetExpectation.MappingSha256);
        command.Parameters.AddWithValue("overrides", V6SearchDatasetExpectation.OverridesSha256);
        command.Parameters.AddWithValue("rules", V6SearchDatasetExpectation.RulesSha256);
        command.Parameters.AddWithValue("pair", V6SearchDatasetExpectation.PairSha256);
        command.Parameters.AddWithValue("baseline", V6SearchDatasetExpectation.BaselineSha256);
        command.Parameters.AddWithValue("schema", V6SearchDatasetExpectation.SchemaSha256);
        if (await command.ExecuteScalarAsync(ct) is not true) throw Unavailable();
    }

    public static async Task<ActorNameResolver> VerifyAndLoadAsync(NpgsqlConnection connection, SearchDatasetExpectation e, CancellationToken ct = default)
    {
        await EnsurePinsAsync(connection, e, ct);
        await using (var command = new NpgsqlCommand(SchemaSql, connection))
        {
            command.Parameters.AddWithValue("base", false);
            if (await command.ExecuteScalarAsync(ct) is not string snapshot || Hash(Encoding.UTF8.GetBytes(snapshot)) != V6SearchDatasetExpectation.SchemaSha256)
                throw Unavailable();
        }
        await using (var command = new NpgsqlCommand("SELECT version,btrim(sha256) FROM schema_migrations ORDER BY version", connection))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            var expected = new[] {
                ("001_initial_schema", "14e5ae626c2288596d563482a510dcb8c79320dd5d41955cc5f06579dd59f238"),
                ("002_serbian_search_vectors", "857b675a33f5ef67670b6f0e2b3c5383e5454494cafe88e707ce12f50eb3b8e3"),
                ("003_actor_hard_filters", V6SearchDatasetExpectation.Migration003Sha256) };
            foreach (var (version, sha) in expected)
                if (!await reader.ReadAsync(ct) || reader.GetString(0) != version || reader.GetString(1) != sha) throw Unavailable();
            if (await reader.ReadAsync(ct)) throw Unavailable();
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        using var map = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var count = 0;
        await using (var command = new NpgsqlCommand("SELECT movie_lens_id,\"cast\" FROM movies ORDER BY movie_lens_id", connection))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                var movieLensId = reader.GetInt64(0);
                var cast = reader.IsDBNull(1) ? null : reader.GetFieldValue<string[]>(1);
                if (cast is not null)
                {
                    if (cast.Length == 0 || cast.Any(x => string.IsNullOrWhiteSpace(x) || x != x.Trim() || !x.IsNormalized(NormalizationForm.FormC)) || cast.Distinct(StringComparer.Ordinal).Count() != cast.Length)
                        throw Unavailable();
                    foreach (var name in cast) names.Add(name);
                }
                map.AppendData(JsonSerializer.SerializeToUtf8Bytes(new { movieLensId, cast }, JsonOptions));
                map.AppendData([10]); count++;
            }
        }
        var vocabulary = JsonSerializer.SerializeToUtf8Bytes(new { rulesVersion = "cast-source-parser-v1", names = names.Order(StringComparer.Ordinal).ToArray() }, JsonOptions);
        if (count != e.MovieCount || Convert.ToHexStringLower(map.GetHashAndReset()) != V6SearchDatasetExpectation.MappingSha256 || Hash(vocabulary) != V6SearchDatasetExpectation.VocabularySha256)
            throw Unavailable();
        return new ActorNameResolver(names); // The accepted reviewed alias sidecar is empty.
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static RealProviderException Unavailable() => new("SEARCH_UNAVAILABLE");
}
