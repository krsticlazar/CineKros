using System.Text.Json.Nodes;
using CineKros.VectorImporter;
using Npgsql;

namespace CineKros.VectorImporter.Tests;

/// <summary>Explicit opt-in tests on an externally restored, owned isolated target. Never creates/drops a database.</summary>
[TestClass]
[DoNotParallelize]
public sealed class ActorImportIntegrationV6Tests
{
    [TestMethod]
    public async Task OptInFourFaultsRollBackThenFullImportAndRepeatWritesNothing()
    {
        if (Environment.GetEnvironmentVariable("CINEKROS_V6_RUN_BASELINE_IMPORT") != "1")
            Assert.Inconclusive("Opt in only on an owned isolated target freshly restored from the approved 001+002 backup.");
        var f = Fixture.Load(); var before = await f.SnapshotAsync();
        foreach (var fault in new[] { ActorImportFaultV6.AfterMigration, ActorImportFaultV6.PartialCast, ActorImportFaultV6.PartialVectors, ActorImportFaultV6.PartialState })
        {
            var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => f.ImportAsync(fault));
            StringAssert.Contains(ex.Message, "Test-only V6 fault");
            Assert.AreEqual(before, await f.SnapshotAsync(), $"Fault {fault} must restore exact schema, old values, state and tuple identities.");
        }
        StringAssert.Contains(await f.ImportAsync(), "Imported all 9,730");
        var complete = await f.SnapshotAsync();
        Assert.AreNotEqual(before, complete);
        Assert.AreEqual("Verified identical complete V6 release; no changes made.", await f.ImportAsync());
        Assert.AreEqual(complete, await f.SnapshotAsync(), "No-op must retain all contents, ledger timestamps, xmin and ctid.");
    }

    [TestMethod]
    public async Task OptInCommittedV6ChecksCastSemanticsAndRefusesCorruptionWithoutRepair()
    {
        if (Environment.GetEnvironmentVariable("CINEKROS_V6_RUN_FINAL_CHECKS") != "1")
            Assert.Inconclusive("Opt in only on the owned isolated database after its successful V6 import.");
        var f = Fixture.Load();
        Assert.AreEqual("Verified identical complete V6 release; no changes made.", await f.ImportAsync());
        await using (var c = await f.OpenAsync())
        await using (var tx = await c.BeginTransactionAsync())
        {
            await using var valid = new NpgsqlCommand("""
                SELECT public.actor_cast_is_valid(NULL::text[]),
                       public.actor_cast_is_valid(ARRAY['Tom Hanks']),
                       public.actor_cast_is_valid(ARRAY['Name','name']),
                       public.actor_cast_is_valid(ARRAY['Robert Downey, Jr.','Ayumu Saitō'])
                """, c, tx);
            await using var r = await valid.ExecuteReaderAsync(); Assert.IsTrue(await r.ReadAsync());
            for (var i = 0; i < 4; i++) Assert.IsTrue(r.GetBoolean(i));
            await r.DisposeAsync();
            foreach (var expression in new[] { "ARRAY[]::text[]", "ARRAY[NULL]::text[]", "ARRAY['']::text[]", "ARRAY['  ']::text[]", "ARRAY[E'\\t\\n']::text[]", "ARRAY['Name','Name']::text[]", "ARRAY[['A','B'],['C','D']]::text[]", "'[0:0]={A}'::text[]" })
            {
                await using var check = new NpgsqlCommand("SELECT public.actor_cast_is_valid(" + expression + ")", c, tx);
                Assert.AreEqual(false, await check.ExecuteScalarAsync(), expression);
                await tx.SaveAsync("bad_cast");
                await using var update = new NpgsqlCommand("UPDATE public.movies SET \"cast\"=" + expression + " WHERE movie_lens_id=1", c, tx);
                var error = await Assert.ThrowsExactlyAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
                Assert.AreEqual(PostgresErrorCodes.CheckViolation, error.SqlState); await tx.RollbackAsync("bad_cast");
            }
            await tx.RollbackAsync();
        }

        // Each committed mutation is owned by this test and restored byte-for-byte in a finally block.
        // The importer must reject it while preserving the corrupted snapshot (no silent repair).
        await f.AssertMutationRejectedAsync("movies", "movie_lens_id=1", "UPDATE public.movies SET \"cast\"=ARRAY['wrong but valid exact actor'] WHERE movie_lens_id=1");
        await f.AssertMutationRejectedAsync("movie_embeddings", "movie_lens_id=1", "UPDATE public.movie_embeddings SET document_fingerprint_sr=repeat('0',64) WHERE movie_lens_id=1");
        await f.AssertMutationRejectedAsync("catalog_import_state", "id=1", "UPDATE public.catalog_import_state SET catalog_content_fingerprint=repeat('0',64) WHERE id=1");
        await f.AssertMutationRejectedAsync("embedding_set_state", "language='sr'", "UPDATE public.embedding_set_state SET corpus_sha256=repeat('0',64) WHERE language='sr'");
        await f.AssertMutationRejectedAsync("actor_catalog_state", "id=1", "UPDATE public.actor_catalog_state SET ordered_cast_mapping_sha256=repeat('0',64) WHERE id=1");
        await f.AssertMutationRejectedAsync("embedding_set_state", "language='sr'", "DELETE FROM public.embedding_set_state WHERE language='sr'");
        var canonical003 = await File.ReadAllTextAsync(Path.Combine(f.Root, "database", "migrations", "003_actor_hard_filters.sql"));
        var canonicalFunction = canonical003[..(canonical003.IndexOf("$function$;", StringComparison.Ordinal) + "$function$;".Length)]
            .Replace("CREATE FUNCTION", "CREATE OR REPLACE FUNCTION", StringComparison.Ordinal);
        await f.AssertSchemaMutationRejectedAsync("CREATE OR REPLACE FUNCTION public.actor_cast_is_valid(value text[]) RETURNS boolean LANGUAGE sql IMMUTABLE PARALLEL SAFE AS 'SELECT true'", canonicalFunction);
        await f.AssertSchemaMutationRejectedAsync("DROP INDEX public.ix_movies_cast; CREATE INDEX ix_movies_cast ON public.movies USING btree (\"cast\")", "DROP INDEX public.ix_movies_cast; CREATE INDEX ix_movies_cast ON public.movies USING gin (\"cast\")");
        await f.AssertSchemaMutationRejectedAsync("ALTER TABLE public.movies DROP CONSTRAINT ck_movies_cast; ALTER TABLE public.movies ADD CONSTRAINT ck_movies_cast CHECK (true)", "ALTER TABLE public.movies DROP CONSTRAINT ck_movies_cast; ALTER TABLE public.movies ADD CONSTRAINT ck_movies_cast CHECK (public.actor_cast_is_valid(\"cast\"))");

        var temp = Path.Combine(Path.GetTempPath(), "cinekros-v6-invalid-baseline-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var bad = JsonNode.Parse(await File.ReadAllTextAsync(f.Baseline))!; bad["releasePins"]!["srCorpusSha256"] = new string('0', 64);
            await File.WriteAllTextAsync(temp, bad.ToJsonString()); var before = await f.SnapshotAsync();
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => f.ImportAsync(baseline: temp));
            Assert.AreEqual(before, await f.SnapshotAsync());
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        Assert.AreEqual("Verified identical complete V6 release; no changes made.", await f.ImportAsync());
    }

    private sealed record Fixture(string Connection, string ExpectedDatabase, string Root, string Baseline)
    {
        internal static Fixture Load()
        {
            string Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } s ? s : throw new InvalidOperationException("Missing explicit integration setting " + name);
            var f = new Fixture(Env("CINEKROS_V6_IMPORT_TEST_CONNECTION"), Env("CINEKROS_V6_EXPECTED_DATABASE"), Env("CINEKROS_V6_REPOSITORY_ROOT"), Env("CINEKROS_V6_BASELINE"));
            ActorImportBaselineV6.RequireConnectionTarget(f.Connection, f.ExpectedDatabase);
            if (!Path.IsPathFullyQualified(f.Root) || !Path.IsPathFullyQualified(f.Baseline)) throw new InvalidOperationException("Integration paths must be absolute.");
            return f;
        }
        internal Task<string> ImportAsync(ActorImportFaultV6 fault = ActorImportFaultV6.None, string? baseline = null) =>
            ActorPairedImporterV6.ImportAsync(Connection, ExpectedDatabase, baseline ?? Baseline,
                Path.Combine(Root, "database/data/derived/final/actor-search-v6/v6-catalog-01/catalog/movies-catalog.jsonl"),
                Path.Combine(Root, "database/data/derived/final/actor-search-v6/v6-catalog-01/catalog/manifest.json"),
                Path.Combine(Root, "database/data/derived/translations/sr-latn-v1-r1/tag-translations-sr.json"),
                Path.Combine(Root, "database/data/derived/final/b05a-real-tmdb-01/movies-catalog.jsonl"),
                Path.Combine(Root, "database/data/embeddings/multilingual-e5-base-int8-onnx-v1/v6-catalog-01"), Path.Combine(Root, "database/migrations"), fault);

        internal async Task<NpgsqlConnection> OpenAsync()
        {
            ActorImportBaselineV6.RequireConnectionTarget(Connection, ExpectedDatabase);
            var c = new NpgsqlConnection(Connection); await c.OpenAsync();
            var actual = (string?)await new NpgsqlCommand("SELECT current_database()", c).ExecuteScalarAsync();
            if (!ActorImportBaselineV6.IsAllowedExecutionTarget(actual ?? "", ExpectedDatabase)) { await c.DisposeAsync(); throw new InvalidOperationException("Unexpected integration target."); }
            return c;
        }
        internal async Task<string> SnapshotAsync()
        {
            await using var c = await OpenAsync(); await using var tx = await c.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
            await ActorPairedImporterV6.ExecuteAsync(c, tx, "SET TRANSACTION READ ONLY; SET LOCAL search_path=public,pg_catalog", default);
            var parts = new List<string> { await ActorSchemaV6.ReadAsync(c, tx, false, default) };
            parts.AddRange((await ActorImportBaselineV6.ReadDigestsAsync(c, tx, default)).Select(x => x.Key + ":" + x.Value));
            var hasActor = await ActorPairedImporterV6.ScalarAsync(c, tx, "SELECT (to_regclass('public.actor_catalog_state') IS NOT NULL)::text", default) == "true";
            foreach (var table in new[] { "movies", "movie_embeddings", "catalog_import_state", "embedding_set_state", "schema_migrations" }.Concat(hasActor ? ["actor_catalog_state"] : Array.Empty<string>()))
                parts.Add(await ActorPairedImporterV6.ScalarAsync(c, tx, $"SELECT md5(string_agg(row_to_json(t)::text, ',' ORDER BY row_to_json(t)::text COLLATE \"C\")) FROM (SELECT ctid::text,xmin::text,* FROM public.{table}) t", default));
            return string.Join('\n', parts);
        }
        internal async Task ExecuteAsync(string sql)
        { await using var c = await OpenAsync(); await using var cmd = new NpgsqlCommand(sql, c); await cmd.ExecuteNonQueryAsync(); }
        internal async Task AssertMutationRejectedAsync(string table, string condition, string mutation)
        {
            await using var c = await OpenAsync();
            var original = (string)(await new NpgsqlCommand($"SELECT row_to_json(t)::text FROM public.{table} t WHERE {condition}", c).ExecuteScalarAsync())!;
            try
            {
                await ExecuteAsync(mutation); var bad = await SnapshotAsync();
                await Assert.ThrowsAsync<Exception>(() => ImportAsync());
                Assert.AreEqual(bad, await SnapshotAsync(), "Rejected corrupted state must not be repaired or rewritten.");
            }
            finally
            {
                await using var tx = await c.BeginTransactionAsync();
                await using var columnQuery = new NpgsqlCommand("SELECT array_agg(attname::text ORDER BY attnum) FROM pg_attribute WHERE attrelid=@table::regclass AND attnum>0 AND NOT attisdropped", c, tx);
                columnQuery.Parameters.AddWithValue("table", "public." + table);
                var columns = (string[])(await columnQuery.ExecuteScalarAsync())!;
                var key = table is "movies" or "movie_embeddings" ? "movie_lens_id" : table == "embedding_set_state" ? "language" : "id";
                using var quote = new NpgsqlCommandBuilder();
                var assignments = string.Join(',', columns.Select(x => quote.QuoteIdentifier(x) + "=EXCLUDED." + quote.QuoteIdentifier(x)));
                await using var restore = new NpgsqlCommand($"INSERT INTO public.{table} SELECT * FROM json_populate_record(NULL::public.{table},@row::json) ON CONFLICT ({key}) DO UPDATE SET {assignments}", c, tx);
                restore.Parameters.AddWithValue("row", original); await restore.ExecuteNonQueryAsync(); await tx.CommitAsync();
            }
        }
        internal async Task AssertSchemaMutationRejectedAsync(string mutation, string restore)
        {
            try
            {
                await ExecuteAsync(mutation); var bad = await SnapshotAsync();
                await Assert.ThrowsAsync<Exception>(() => ImportAsync()); Assert.AreEqual(bad, await SnapshotAsync());
            }
            finally { await ExecuteAsync(restore); }
        }
    }
}
