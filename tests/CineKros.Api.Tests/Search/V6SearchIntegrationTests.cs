using System.Reflection;
using System.Text.RegularExpressions;
using CineKros.Api.Database;
using CineKros.Api.RealProviders;
using CineKros.Api.Search;
using Npgsql;
using Pgvector;

namespace CineKros.Api.Tests.Search;

[TestClass]
public sealed class V6SearchIntegrationTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [TestCategory("V6Database")]
    public async Task OptInNewCloneChecksFullReadinessMutationRefusalAndActorEligibleRanking()
    {
        if (Environment.GetEnvironmentVariable("CINEKROS_V6_P4_RUN_CLONE_TESTS") != "true")
            Assert.Inconclusive("MAIN release and a new owned cinekros_v6_test_p4_* clone are required.");
        var target = Environment.GetEnvironmentVariable("CINEKROS_V6_P4_CLONE_DATABASE") ?? "";
        Assert.IsTrue(Regex.IsMatch(target, @"\Acinekros_v6_test_p4_[a-z0-9_]+\z"), "Refusing targets outside the new Phase4-owned clone family.");
        var configured = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CINEKROS_V6_P4_CLONE_CONNECTION"));
        Assert.AreEqual(target, configured.Database, "Configured and explicitly named clone must match before connection.");
        configured.Pooling = false; configured.ApplicationName = "CineKros-V6-P4-OwnedClone-Test";
        configured.Options = "-c default_transaction_read_only=off";
        await using var writerSource = MovieSearchRepository.CreateDataSource(configured.ConnectionString);
        await using var writer = await writerSource.OpenConnectionAsync();
        await GuardAsync(writer, target);
        configured.Options = "-c default_transaction_read_only=on";
        await using var readSource = MovieSearchRepository.CreateDataSource(configured.ConnectionString);
        var expected = V6SearchDatasetExpectation.Isolated(V6SearchDatasetExpectation.Database) with { DatabaseName = target };
        var constructor = typeof(MovieSearchRepository).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, [typeof(NpgsqlDataSource), typeof(SearchDatasetExpectation)], modifiers: null);
        Assert.IsNotNull(constructor);
        var repository = (MovieSearchRepository)constructor.Invoke([readSource, expected]); // test-only target override, never a runtime factory.
        await repository.EnsureRuntimeReadyAsync();
        await CheckActualPredicatesAsync(writer, repository);

        var helper = (string)(await ScalarAsync(writer, "SELECT pg_get_functiondef('public.actor_cast_is_valid(text[])'::regprocedure)"))!;
        await ExecuteAsync(writer, "CREATE TEMP TABLE p4_saved_cast AS SELECT movie_lens_id,\"cast\" FROM movies WHERE movie_lens_id IN (1,2); CREATE TEMP TABLE p4_saved_sr AS SELECT * FROM embedding_set_state WHERE language='sr'; CREATE TEMP TABLE p4_saved_vector AS SELECT movie_lens_id,embedding_sr::text AS embedding_sr,document_fingerprint_sr FROM movie_embeddings ORDER BY movie_lens_id LIMIT 1");
        var cases = new (string Name, string Mutate, string Restore)[]
        {
            ("catalog", "UPDATE catalog_import_state SET catalog_jsonl_sha256=repeat('0',64)", $"UPDATE catalog_import_state SET catalog_jsonl_sha256='{expected.CatalogSha256}'"),
            ("profile", "UPDATE catalog_import_state SET embedding_profile_fingerprint=repeat('0',64)", $"UPDATE catalog_import_state SET embedding_profile_fingerprint='{expected.ProfileFingerprint}'"),
            ("actor-state", "UPDATE actor_catalog_state SET actor_vocabulary_sha256=repeat('0',64)", $"UPDATE actor_catalog_state SET actor_vocabulary_sha256='{V6SearchDatasetExpectation.VocabularySha256}'"),
            ("moved-cast-same-vocabulary", "UPDATE movies m SET \"cast\"=s.\"cast\" FROM p4_saved_cast s WHERE (m.movie_lens_id=1 AND s.movie_lens_id=2) OR (m.movie_lens_id=2 AND s.movie_lens_id=1)", "UPDATE movies m SET \"cast\"=s.\"cast\" FROM p4_saved_cast s WHERE m.movie_lens_id=s.movie_lens_id"),
            ("sr-corpus", "UPDATE embedding_set_state SET corpus_sha256=repeat('0',64) WHERE language='sr'", $"UPDATE embedding_set_state SET corpus_sha256='{expected.SrCorpusSha256}' WHERE language='sr'"),
            ("sr-format", "UPDATE embedding_set_state SET text_format_version='sr-title-year-director-cast-tags-latn-v1' WHERE language='sr'", $"UPDATE embedding_set_state SET text_format_version='{expected.SrTextFormatVersion}' WHERE language='sr'"),
            ("sr-missing-no-en-fallback", "DELETE FROM embedding_set_state WHERE language='sr'", "INSERT INTO embedding_set_state SELECT * FROM p4_saved_sr"),
            ("sr-vector-null-no-fallback", "UPDATE movie_embeddings SET embedding_sr=NULL,document_fingerprint_sr=NULL WHERE movie_lens_id=(SELECT movie_lens_id FROM p4_saved_vector)", "UPDATE movie_embeddings m SET embedding_sr=s.embedding_sr::vector(768),document_fingerprint_sr=s.document_fingerprint_sr FROM p4_saved_vector s WHERE m.movie_lens_id=s.movie_lens_id"),
            ("migration-ledger", "UPDATE schema_migrations SET sha256=repeat('0',64) WHERE version='003_actor_hard_filters'", $"UPDATE schema_migrations SET sha256='{V6SearchDatasetExpectation.Migration003Sha256}' WHERE version='003_actor_hard_filters'"),
            ("helper", "CREATE OR REPLACE FUNCTION public.actor_cast_is_valid(value text[]) RETURNS boolean LANGUAGE sql IMMUTABLE PARALLEL SAFE AS 'SELECT true'", helper),
            ("check", "ALTER TABLE movies DROP CONSTRAINT ck_movies_cast; ALTER TABLE movies ADD CONSTRAINT ck_movies_cast CHECK(true)", "ALTER TABLE movies DROP CONSTRAINT ck_movies_cast; ALTER TABLE movies ADD CONSTRAINT ck_movies_cast CHECK(public.actor_cast_is_valid(\"cast\"))"),
            ("gin", "DROP INDEX ix_movies_cast; CREATE INDEX ix_movies_cast ON movies USING btree (\"cast\")", "DROP INDEX ix_movies_cast; CREATE INDEX ix_movies_cast ON movies USING gin (\"cast\")")
        };
        foreach (var test in cases)
        {
            await GuardAsync(writer, target);
            await ExecuteAsync(writer, test.Mutate);
            try
            {
                var error = await Assert.ThrowsExactlyAsync<RealProviderException>(() => repository.EnsureRuntimeReadyAsync());
                Assert.AreEqual("SEARCH_UNAVAILABLE", error.Code, test.Name);
                if (test.Name.StartsWith("sr-", StringComparison.Ordinal))
                    Assert.AreEqual("SEARCH_UNAVAILABLE", (await Assert.ThrowsExactlyAsync<RealProviderException>(() => repository.EnsureSelectedLanguageReadyAsync(SearchLanguage.Serbian))).Code);
                TestContext.WriteLine($"V6_P4_CLONE case={test.Name} readiness=closed");
            }
            finally { await ExecuteAsync(writer, test.Restore); }
            await repository.EnsureRuntimeReadyAsync();
        }
        await GuardAsync(writer, target);
        await repository.EnsureRuntimeReadyAsync();
        TestContext.WriteLine("V6_P4_CLONE full-schema-ledger-map-vocabulary-en-sr-restoration=PASS");
    }

    private static async Task CheckActualPredicatesAsync(NpgsqlConnection c, MovieSearchRepository repository)
    {
        var casts = new Dictionary<long, string[]?>();
        await using (var command = new NpgsqlCommand("SELECT movie_lens_id,\"cast\" FROM movies", c))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) casts.Add(reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetFieldValue<string[]>(1));
        foreach (var actors in new[] {
            new RealActorFilter(All: ["Brad Pitt", "George Clooney"]), new RealActorFilter(Any: ["Brad Pitt", "George Clooney"]),
            new RealActorFilter(All: ["Brad Pitt"], Any: ["George Clooney", "Matt Damon"], Exclude: ["George Clooney"]),
            new RealActorFilter(Exclude: ["Brad Pitt"]), new RealActorFilter(All: ["Fred A. Leuchter, Jr."]), new RealActorFilter(All: ["Ayumu Saitō"]) })
        {
            var filters = new RealHardFilters(Actors: actors);
            var rows = await repository.SearchHardOnlyAsync(filters);
            foreach (var row in rows) Assert.IsTrue(Matches(casts[row.MovieLensId], actors));
            var query = HardFilterSqlBuilder.Build(filters);
            await using var expected = new NpgsqlCommand("SELECT movie_lens_id FROM movies " + query.WhereSql + " ORDER BY rating_count DESC NULLS LAST,average_rating DESC NULLS LAST,movie_lens_id ASC LIMIT 10", c);
            query.AddParameters(expected); var ids = new List<long>();
            await using (var reader = await expected.ExecuteReaderAsync()) while (await reader.ReadAsync()) ids.Add(reader.GetInt64(0));
            CollectionAssert.AreEqual(ids.ToArray(), rows.Select(r => r.MovieLensId).ToArray());
        }
        // Select an actual recorded vector of an actor-ineligible film: even its zero-distance self must never return.
        long excludedId; Vector vector;
        await using (var command = new NpgsqlCommand("SELECT m.movie_lens_id,e.embedding FROM movies m JOIN movie_embeddings e USING(movie_lens_id) WHERE m.\"cast\" IS NULL OR NOT(m.\"cast\" @> ARRAY['Brad Pitt']::text[]) ORDER BY m.movie_lens_id LIMIT 1", c))
        await using (var reader = await command.ExecuteReaderAsync())
        { Assert.IsTrue(await reader.ReadAsync()); excludedId = reader.GetInt64(0); vector = reader.GetFieldValue<Vector>(1); }
        var filtersHybrid = new RealHardFilters(Actors: new RealActorFilter(All: ["Brad Pitt"]));
        foreach (var language in new[] { SearchLanguage.English, SearchLanguage.Serbian })
        {
            var hybrid = await repository.SearchHybridAsync(filtersHybrid, vector.ToArray(), language);
            Assert.AreEqual(10, hybrid.Count); Assert.IsFalse(hybrid.Any(x => x.MovieLensId == excludedId));
            Assert.IsTrue(hybrid.All(x => Matches(casts[x.MovieLensId], filtersHybrid.Actors!)));
            var column = language == SearchLanguage.English ? "embedding" : "embedding_sr";
            await using var exact = new NpgsqlCommand("SELECT m.movie_lens_id FROM movies m JOIN movie_embeddings e USING(movie_lens_id) WHERE m.\"cast\" IS NOT NULL AND cardinality(m.\"cast\")>0 AND m.\"cast\" @> ARRAY['Brad Pitt']::text[] ORDER BY e." + column + " <=> @q,m.movie_lens_id LIMIT 10", c);
            exact.Parameters.AddWithValue("q", vector); var exactIds = new List<long>();
            await using (var reader = await exact.ExecuteReaderAsync()) while (await reader.ReadAsync()) exactIds.Add(reader.GetInt64(0));
            CollectionAssert.AreEqual(exactIds.ToArray(), hybrid.Select(x => x.MovieLensId).ToArray(), "Exact selected-language column and stable ID order must match.");
        }
        Assert.AreEqual(0, (await repository.SearchHardOnlyAsync(filtersHybrid with { YearMin = 9999 })).Count);
        Assert.AreEqual(10, (await repository.SearchHardOnlyAsync(filtersHybrid)).Count);
        var partial = await repository.SearchHardOnlyAsync(new RealHardFilters(Actors: new RealActorFilter(All: ["Fred A. Leuchter, Jr."])));
        Assert.IsTrue(partial.Count is > 0 and < 10, "Recorded suffix actor must demonstrate a partial result.");
        await using var wrongCase = new NpgsqlCommand("SELECT count(*) FROM movies WHERE \"cast\" @> ARRAY['brad pitt']::text[]", c);
        Assert.AreEqual(0L, await wrongCase.ExecuteScalarAsync());
    }

    private static bool Matches(string[]? cast, RealActorFilter actors) => cast is { Length: > 0 }
        && (actors.All?.All(x => cast.Contains(x, StringComparer.Ordinal)) ?? true)
        && (actors.Any?.Any(x => cast.Contains(x, StringComparer.Ordinal)) ?? true)
        && !(actors.Exclude?.Any(x => cast.Contains(x, StringComparer.Ordinal)) ?? false);

    private static async Task GuardAsync(NpgsqlConnection c, string target)
    {
        Assert.IsTrue(Regex.IsMatch(target, @"\Acinekros_v6_test_p4_[a-z0-9_]+\z"));
        await using var command = new NpgsqlCommand("SELECT current_database(),current_schema(),(SELECT system_identifier::text FROM pg_control_system()),(SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND pid<>pg_backend_pid())", c);
        await using var reader = await command.ExecuteReaderAsync(); Assert.IsTrue(await reader.ReadAsync());
        Assert.AreEqual(target, reader.GetString(0)); Assert.AreEqual("public", reader.GetString(1));
        Assert.AreEqual("7690124133367640101", reader.GetString(2)); Assert.AreEqual(0L, reader.GetInt64(3), "No other session may use this owned clone during fixture mutation.");
    }
    private static async Task ExecuteAsync(NpgsqlConnection c, string sql)
    { await using var tx = await c.BeginTransactionAsync(); await using var command = new NpgsqlCommand(sql, c, tx); await command.ExecuteNonQueryAsync(); await tx.CommitAsync(); }
    private static async Task<object?> ScalarAsync(NpgsqlConnection c, string sql)
    { await using var command = new NpgsqlCommand(sql, c); return await command.ExecuteScalarAsync(); }
}
