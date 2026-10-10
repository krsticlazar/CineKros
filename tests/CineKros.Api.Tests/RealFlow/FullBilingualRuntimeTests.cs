using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CineKros.Api;
using CineKros.Api.Database;
using CineKros.Api.RealFlow;
using CineKros.Api.RealProviders;
using CineKros.Api.Search;
using CineKros.Embedding;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Npgsql;
using CineKros.TextNormalization;

namespace CineKros.Api.Tests.RealFlow;

[TestClass]
public sealed class FullBilingualRuntimeTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void FullProductionExpectationPinsExactReleaseAndRemainsSeparateFromPocV2()
    {
        var full = SearchDatasetExpectation.FullBilingualProduction("cinekros");
        Assert.AreEqual("cinekros", full.DatabaseName);
        Assert.AreEqual(9730, full.MovieCount);
        Assert.AreEqual("B05a-bilingual-full-catalog-v1", full.CatalogVersion);
        Assert.AreEqual("6fd22ec79d2c2b1e36b009f899b126c03a1876ce15a2871bb95033cfff410a4c", full.CatalogSha256);
        Assert.AreEqual("cfe69cc246e439f73c3354f77902896988df878af20bfdb8552cfd1fdf1be17e", full.CatalogIdentity);
        Assert.AreEqual("af7b28f5c43cc219a9d5f62bd22716015a340f005ee9098f11f4301e13d7bac2", full.SelectionIdSetSha256);
        Assert.AreEqual("eac906ed78f7863573d13c9b0435de1b8f848fe92fc6ae08aa3621400260b1fe", full.ProfileFingerprint);
        Assert.AreEqual("fa07202a8181024b3945b63088e7ab4daa48428b238d88ee269f884d8768cec1", full.EnCorpusSha256);
        Assert.AreEqual("b7998f5f681c61b0f938e6c0fd2d8d619922baaaefc07ed122c7a64860321122", full.SrCorpusSha256);
        Assert.AreEqual("3dde59fa8bbf82452e118fd7f65f9c1e3fd8e630b9bad05a0b123e1e8468053e", full.EnArtifactSha256);
        Assert.AreEqual("ff5db33c1c6871a8bd04275a5e6a5167232de9d40e98f3802425c0d2efa0c204", full.SrArtifactSha256);
        Assert.AreEqual("917ba3ea759b6d6595c78bf1ebcd3ddc547f00914156f3ab2fb5064fc549a715", full.DictionarySha256);
        Assert.ThrowsExactly<InvalidOperationException>(() => SearchDatasetExpectation.FullBilingualProduction("postgres"));
        Assert.ThrowsExactly<InvalidOperationException>(() => SearchDatasetExpectation.FullBilingualProduction("cinekros_sr_poc_phase06t_v2_20261009"));

        var poc = SearchDatasetExpectation.SerbianPhase6TV2Poc("cinekros_sr_poc_phase06t_v2_20261009");
        Assert.AreEqual(150, poc.MovieCount);
        Assert.AreNotEqual(full.CatalogSha256, poc.CatalogSha256);
        Assert.AreNotEqual(full.SelectionIdSetSha256, poc.SelectionIdSetSha256);
    }

    [TestMethod]
    public async Task LanguageAwareRuntimeKeepsEnglishAndNormalizesOnlySerbianSemanticQuery()
    {
        foreach (var (language, selected, sourceMessage, parsedSemantic, expectedSemantic) in new[]
        {
            ("en", SearchLanguage.English, "quiet film with Brad Pitt", "quiet mystery with Brad Pitt", "quiet mystery with Brad Pitt"),
            ("sr", SearchLanguage.Serbian, "Желим нешто као Fight Club са Brad Pittom", "мрачна драма са Brad Pittom", "mračna drama sa Brad Pittom")
        })
        {
            var parser = new FixedParser(new RealParserResult("query", new RealParsedQuery(new RealHardFilters(YearMin: 2000), parsedSemantic), LanguageCheck: "match"));
            var embedding = new CapturingEmbedding();
            var search = new CapturingSearch([]);
            var service = new RealRecommendationService(parser, new RealParsedQueryValidator(), embedding, search, languageAwarePoc: true);

            _ = await service.RecommendAsync(new ParserInput(language, sourceMessage), CancellationToken.None);

            Assert.AreEqual(language, parser.ReceivedLanguage);
            Assert.AreEqual(sourceMessage, parser.ReceivedMessage, "The original user text must reach parsing unchanged.");
            Assert.AreEqual(expectedSemantic, embedding.ReceivedQuery, "Only the validated semantic query is normalized for Serbian.");
            Assert.AreEqual(1, search.PreflightCalls);
            Assert.AreEqual(selected, search.PreflightLanguage);
            Assert.AreEqual(1, search.LanguageHybridCalls);
            Assert.AreEqual(selected, search.HybridLanguage);
            Assert.AreEqual(0, search.LegacyHybridCalls, "Selected-language requests may not fall back to the legacy overload.");
        }
    }

    [TestMethod]
    public async Task SerbianReadinessFailureStopsBeforeQueryEmbeddingAndCannotFallbackToEnglish()
    {
        var parser = new FixedParser(new RealParserResult("query", new RealParsedQuery(new RealHardFilters(), "мирна мистерија"), LanguageCheck: "match"));
        var embedding = new CapturingEmbedding();
        var search = new CapturingSearch([], failReadiness: true);
        var service = new RealRecommendationService(parser, new RealParsedQueryValidator(), embedding, search, languageAwarePoc: true);

        var error = await Assert.ThrowsExactlyAsync<RealProviderException>(() => service.RecommendAsync(
            new ParserInput("sr", "Мирна мистерија са Meryl Streep"), CancellationToken.None));

        Assert.AreEqual(ApiErrorCodes.SearchUnavailable, error.Code);
        Assert.AreEqual(1, search.PreflightCalls);
        Assert.AreEqual(SearchLanguage.Serbian, search.PreflightLanguage);
        Assert.AreEqual(0, embedding.Calls);
        Assert.AreEqual(0, search.LanguageHybridCalls);
        Assert.AreEqual(0, search.LegacyHybridCalls);
        Assert.AreEqual(0, search.HardOnlyCalls);
    }

    [TestMethod]
    public async Task FullRuntimeHardOnlyUsesSqlAndMakesZeroEmbeddingOrLanguagePreflightCalls()
    {
        var filters = new RealHardFilters(YearMin: 2000, RuntimeMax: 120, Genres: new RealGenreFilter(Any: ["Drama", "Comedy"]));
        var parser = new FixedParser(new RealParserResult("query", new RealParsedQuery(filters, null), LanguageCheck: "match"));
        var embedding = new CapturingEmbedding();
        var search = new CapturingSearch([]);
        var service = new RealRecommendationService(parser, new RealParsedQueryValidator(), embedding, search, languageAwarePoc: true);

        var result = await service.RecommendAsync(new ParserInput("sr", "Драме до два сата после 2000."), CancellationToken.None);

        Assert.IsNull(result.AlertCode);
        Assert.AreEqual(0, embedding.Calls);
        Assert.AreEqual(0, search.PreflightCalls, "Hard-only query must not gate on or infer either vector language.");
        Assert.AreEqual(0, search.LanguageHybridCalls);
        Assert.AreEqual(0, search.LegacyHybridCalls);
        Assert.AreEqual(1, search.HardOnlyCalls);
        Assert.AreSame(filters, search.ReceivedFilters);
    }

    [TestMethod]
    public async Task LanguageAwareHttpPreservesZeroPartialAndFullResultContract()
    {
        foreach (var (language, count) in new[] { ("en", 0), ("sr", 1), ("en", 9), ("sr", 10) })
        {
            var parser = new FixedParser(new RealParserResult("query", new RealParsedQuery(new RealHardFilters(YearMin: 2000),
                language == "sr" ? "мрачна драма" : "quiet mystery"), LanguageCheck: "match"));
            var embedding = new CapturingEmbedding();
            var results = Enumerable.Range(1, count).Select(id => new FilteredMovie(id, $"Film {id}", 2000 + id,
                id.ToString("0000000", System.Globalization.CultureInfo.InvariantCulture), $"/poster-{id}.jpg", null, null)).ToArray();
            var search = new CapturingSearch(results);
            await using var app = await BuildApp(parser, embedding, search);
            using var client = app.GetTestClient();
            using var response = await client.PostAsync("/api/recommendations", new StringContent(
                JsonSerializer.Serialize(new { language, message = language == "sr" ? "Хоћу мирну мистерију" : "quiet mystery" }),
                Encoding.UTF8, "application/json"));
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            if (count == 0)
            {
                Assert.AreEqual("alert", body.RootElement.GetProperty("type").GetString());
                Assert.AreEqual(ApiErrorCodes.NoResults, body.RootElement.GetProperty("alert").GetProperty("code").GetString());
                Assert.IsFalse(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("alert").GetProperty("message").GetString()));
            }
            else
            {
                Assert.AreEqual("movies", body.RootElement.GetProperty("type").GetString());
                Assert.AreEqual(count, body.RootElement.GetProperty("meta").GetProperty("count").GetInt32());
                Assert.AreEqual(count < 10, body.RootElement.GetProperty("meta").GetProperty("partial").GetBoolean());
                var first = body.RootElement.GetProperty("movies")[0];
                Assert.AreEqual($"Film 1", first.GetProperty("title").GetString());
                Assert.AreEqual(2001, first.GetProperty("year").GetInt32());
                Assert.AreEqual("https://www.imdb.com/title/tt0000001/", first.GetProperty("imdbUrl").GetString());
                Assert.AreEqual("https://image.tmdb.org/t/p/w500/poster-1.jpg", first.GetProperty("posterUrl").GetString());
            }
            Assert.AreEqual(1, embedding.Calls);
            Assert.AreEqual(1, search.PreflightCalls);
            Assert.AreEqual(language == "en" ? SearchLanguage.English : SearchLanguage.Serbian, search.HybridLanguage);
            Assert.AreEqual(0, search.LegacyHybridCalls);
        }
    }

    /// <summary>
    /// Explicit opt-in local performance/readiness harness. It opens only a caller-supplied
    /// read-only connection to cinekros and embeds short queries; it never generates documents.
    /// </summary>
    [TestMethod]
    public async Task OptInReadOnlyFullProductionQueryBenchmarkAndReadiness()
    {
        if (Environment.GetEnvironmentVariable("CINEKROS_SR_P10_RUN_PRODUCTION_READONLY_TEST") != "true")
            Assert.Inconclusive("Set CINEKROS_SR_P10_RUN_PRODUCTION_READONLY_TEST=true only for MAIN-authorized read-only production verification.");
        var connectionString = Environment.GetEnvironmentVariable("CINEKROS_SR_P10_READONLY_CONNECTION");
        var modelDirectory = Environment.GetEnvironmentVariable("CINEKROS_E5_MODEL_DIR");
        Assert.IsFalse(string.IsNullOrWhiteSpace(connectionString), "MAIN must supply the local read-only connection through process environment.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(modelDirectory), "The official local multilingual model directory is required.");
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false, Options = "-c default_transaction_read_only=on" };
        Assert.AreEqual("cinekros", builder.Database, "Refusing to connect to anything other than the exact full production database.");

        await using var dataSource = MovieSearchRepository.CreateDataSource(builder.ConnectionString);
        await using (var connection = await dataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand("SELECT current_database(),current_schema(),current_setting('transaction_read_only')", connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.IsTrue(await reader.ReadAsync());
            Assert.AreEqual("cinekros", reader.GetString(0));
            Assert.AreEqual("public", reader.GetString(1));
            Assert.AreEqual("on", reader.GetString(2));
        }

        var process = Process.GetCurrentProcess();
        var baselineWorkingSet = process.WorkingSet64;
        var modelLoadTimer = Stopwatch.StartNew();
        using var model = new E5EmbeddingModel(modelDirectory!, EmbeddingProfileDescriptor.MultilingualE5Base);
        modelLoadTimer.Stop();
        Assert.AreEqual("eac906ed78f7863573d13c9b0435de1b8f848fe92fc6ae08aa3621400260b1fe", model.ProfileFingerprint);

        var repository = await MovieSearchRepository.CreateFullBilingualProductionRepositoryAsync(dataSource);
        var readyTimer = Stopwatch.StartNew();
        await repository.EnsureRuntimeReadyAsync();
        readyTimer.Stop();
        var afterReadyWorkingSet = Process.GetCurrentProcess().WorkingSet64;

        _ = model.EmbedQuery("quiet mystery");
        _ = model.EmbedQuery(SerbianLatinNormalizer.Normalize("мирна мистерија"));
        var enQueries = EnglishQueries();
        var srQueries = SerbianQueries();
        var enMetrics = await RunQueriesAsync(enQueries, SearchLanguage.English, repository, model, process);
        var srMetrics = await RunQueriesAsync(srQueries, SearchLanguage.Serbian, repository, model, process);
        var afterQueriesWorkingSet = Process.GetCurrentProcess().WorkingSet64;
        var peakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64;

        var graphBytes = new FileInfo(Path.Combine(modelDirectory!, "model_qint8_avx512_vnni.onnx")).Length;
        TestContext.WriteLine($"P10_READONLY_METRICS database=cinekros schema=public default_transaction_read_only=on profile={model.ProfileFingerprint} graph_file_bytes={graphBytes} processor_count={Environment.ProcessorCount}");
        TestContext.WriteLine($"P10_READONLY_METRICS baseline_working_set_bytes={baselineWorkingSet} after_model_ready_working_set_bytes={afterReadyWorkingSet} after_query_working_set_bytes={afterQueriesWorkingSet} process_peak_working_set_bytes={peakWorkingSet}");
        TestContext.WriteLine($"P10_READONLY_METRICS model_load_wall_ms={modelLoadTimer.Elapsed.TotalMilliseconds:F2} model_reported_load_ms={model.LoadTime.TotalMilliseconds:F2} both_language_ready_wall_ms={readyTimer.Elapsed.TotalMilliseconds:F2}");
        WriteMetrics("en", enMetrics);
        WriteMetrics("sr", srMetrics);
        TestContext.WriteLine("P10_READONLY_METRICS timings include local tokenization+ORT and the repository call, which revalidates the selected release before exact SQL retrieval; no Gemini/network calls or document embeddings ran.");

        var hardOnlyEmbedding = new CapturingEmbedding();
        var hardOnlyService = new RealRecommendationService(
            new FixedParser(new RealParserResult("query", new RealParsedQuery(new RealHardFilters(YearMin: 2000), null), LanguageCheck: "match")),
            new RealParsedQueryValidator(), hardOnlyEmbedding, new MovieSearchAdapter(repository), languageAwarePoc: true);
        var hardOnly = await hardOnlyService.RecommendAsync(new ParserInput("en", "films from 2000 onward"), CancellationToken.None);
        Assert.AreEqual(0, hardOnlyEmbedding.Calls);
        Assert.IsTrue(hardOnly.Movies.Count <= MovieSearchRepository.ResultLimit);
        Assert.IsTrue(hardOnly.Movies.All(movie => movie.Year >= 2000));
    }

    /// <summary>
    /// MAIN-released destructive-fixture proof, strictly limited to the named disposable full clone.
    /// Every temporary change is restored in a finally path and followed by full two-language readiness.
    /// </summary>
    [TestMethod]
    public async Task OptInCloneReadinessFailuresAreClosedBeforeEmbeddingAndRestoreExactly()
    {
        if (Environment.GetEnvironmentVariable("CINEKROS_SR_P10_RUN_CLONE_READINESS_TEST") != "true")
            Assert.Inconclusive("Set the clone readiness opt-in only for MAIN-released disposable-clone verification.");
        const string target = "cinekros_sr_p10_test_readiness_20261009";
        const string systemIdentifier = "7690124133367640101";
        var connectionString = Environment.GetEnvironmentVariable("CINEKROS_SR_P10_CLONE_CONNECTION");
        Assert.IsFalse(string.IsNullOrWhiteSpace(connectionString), "MAIN must provide the clone connection through process environment.");
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
            ApplicationName = "CineKros-P10-CloneReadiness-Test"
        };
        Assert.AreEqual(target, builder.Database, "Refusing any database other than the exact MAIN-released clone.");
        var full = SearchDatasetExpectation.FullBilingualProduction("cinekros") with { DatabaseName = target };
        await using var dataSource = MovieSearchRepository.CreateDataSource(builder.ConnectionString);
        var repository = CreateCloneRepositoryByTestOnlyReflection(dataSource, full);

        await using var connection = await dataSource.OpenConnectionAsync();
        await AssertCloneGuardAsync(connection, target, systemIdentifier, otherConnectionCount: 0);
        await repository.EnsureRuntimeReadyAsync();
        await AssertCloneReleaseCountsAsync(connection);
        var baselineDigests = await ReadCloneDigestsAsync(connection);
        await ExecuteAsync(connection, "CREATE TEMP TABLE p10_saved_embedding ON COMMIT PRESERVE ROWS AS SELECT movie_lens_id,embedding::text AS embedding,document_fingerprint,embedding_sr::text AS embedding_sr,document_fingerprint_sr FROM movie_embeddings WHERE movie_lens_id=(SELECT min(movie_lens_id) FROM movie_embeddings)");
        await ExecuteAsync(connection, "CREATE TEMP TABLE p10_saved_sr_state ON COMMIT PRESERVE ROWS AS SELECT * FROM embedding_set_state WHERE language='sr'");
        await ExecuteAsync(connection, "CREATE TEMP TABLE p10_saved_en_state ON COMMIT PRESERVE ROWS AS SELECT * FROM embedding_set_state WHERE language='en'");
        await ExecuteAsync(connection, "CREATE TEMP TABLE p10_saved_catalog_state ON COMMIT PRESERVE ROWS AS SELECT * FROM catalog_import_state WHERE id=1");

        var cases = new (string Name, string MutateSql, string RestoreSql)[]
        {
            ("catalog-version", "UPDATE catalog_import_state SET catalog_version='wrong-release' WHERE id=1", "UPDATE catalog_import_state SET catalog_version='B05a-bilingual-full-catalog-v1' WHERE id=1"),
            ("catalog-hash", "UPDATE catalog_import_state SET catalog_jsonl_sha256=repeat('0',64) WHERE id=1", "UPDATE catalog_import_state SET catalog_jsonl_sha256='6fd22ec79d2c2b1e36b009f899b126c03a1876ce15a2871bb95033cfff410a4c' WHERE id=1"),
            ("global-profile", "UPDATE catalog_import_state SET embedding_profile_fingerprint=repeat('0',64) WHERE id=1", "UPDATE catalog_import_state SET embedding_profile_fingerprint='eac906ed78f7863573d13c9b0435de1b8f848fe92fc6ae08aa3621400260b1fe' WHERE id=1"),
            ("global-artifact", "UPDATE catalog_import_state SET embedding_artifact_sha256=repeat('0',64) WHERE id=1", "UPDATE catalog_import_state SET embedding_artifact_sha256='3dde59fa8bbf82452e118fd7f65f9c1e3fd8e630b9bad05a0b123e1e8468053e' WHERE id=1"),
            ("global-import-state-missing", "DELETE FROM catalog_import_state WHERE id=1", "INSERT INTO catalog_import_state SELECT * FROM p10_saved_catalog_state"),
            ("embedding-count", "DELETE FROM movie_embeddings WHERE movie_lens_id=(SELECT movie_lens_id FROM p10_saved_embedding)", "INSERT INTO movie_embeddings SELECT movie_lens_id,embedding::vector(768),document_fingerprint,embedding_sr::vector(768),document_fingerprint_sr FROM p10_saved_embedding"),
            ("id-set", "DELETE FROM movie_embeddings WHERE movie_lens_id=(SELECT movie_lens_id FROM p10_saved_embedding); UPDATE movies SET movie_lens_id=9223372036854775000 WHERE movie_lens_id=(SELECT movie_lens_id FROM p10_saved_embedding); INSERT INTO movie_embeddings SELECT 9223372036854775000,embedding::vector(768),document_fingerprint,embedding_sr::vector(768),document_fingerprint_sr FROM p10_saved_embedding", "DELETE FROM movie_embeddings WHERE movie_lens_id=9223372036854775000; UPDATE movies SET movie_lens_id=(SELECT movie_lens_id FROM p10_saved_embedding) WHERE movie_lens_id=9223372036854775000; INSERT INTO movie_embeddings SELECT movie_lens_id,embedding::vector(768),document_fingerprint,embedding_sr::vector(768),document_fingerprint_sr FROM p10_saved_embedding"),
            ("sr-profile", "UPDATE embedding_set_state SET profile_fingerprint=repeat('0',64) WHERE language='sr'", "UPDATE embedding_set_state SET profile_fingerprint='eac906ed78f7863573d13c9b0435de1b8f848fe92fc6ae08aa3621400260b1fe' WHERE language='sr'"),
            ("sr-corpus", "UPDATE embedding_set_state SET corpus_sha256=repeat('0',64) WHERE language='sr'", "UPDATE embedding_set_state SET corpus_sha256='b7998f5f681c61b0f938e6c0fd2d8d619922baaaefc07ed122c7a64860321122' WHERE language='sr'"),
            ("sr-artifact", "UPDATE embedding_set_state SET artifact_sha256=repeat('0',64) WHERE language='sr'", "UPDATE embedding_set_state SET artifact_sha256='ff5db33c1c6871a8bd04275a5e6a5167232de9d40e98f3802425c0d2efa0c204' WHERE language='sr'"),
            ("sr-dictionary", "UPDATE embedding_set_state SET translation_dictionary_sha256=repeat('0',64) WHERE language='sr'", "UPDATE embedding_set_state SET translation_dictionary_sha256='917ba3ea759b6d6595c78bf1ebcd3ddc547f00914156f3ab2fb5064fc549a715' WHERE language='sr'"),
            ("en-profile", "UPDATE embedding_set_state SET profile_fingerprint=repeat('0',64) WHERE language='en'", "UPDATE embedding_set_state SET profile_fingerprint='eac906ed78f7863573d13c9b0435de1b8f848fe92fc6ae08aa3621400260b1fe' WHERE language='en'"),
            ("en-corpus", "UPDATE embedding_set_state SET corpus_sha256=repeat('0',64) WHERE language='en'", "UPDATE embedding_set_state SET corpus_sha256='fa07202a8181024b3945b63088e7ab4daa48428b238d88ee269f884d8768cec1' WHERE language='en'"),
            ("en-artifact", "UPDATE embedding_set_state SET artifact_sha256=repeat('0',64) WHERE language='en'", "UPDATE embedding_set_state SET artifact_sha256='3dde59fa8bbf82452e118fd7f65f9c1e3fd8e630b9bad05a0b123e1e8468053e' WHERE language='en'"),
            ("en-missing", "DELETE FROM embedding_set_state WHERE language='en'", "INSERT INTO embedding_set_state SELECT * FROM p10_saved_en_state"),
            ("sr-missing", "DELETE FROM embedding_set_state WHERE language='sr'", "INSERT INTO embedding_set_state SELECT * FROM p10_saved_sr_state"),
            ("en-vector-missing", "DELETE FROM movie_embeddings WHERE movie_lens_id=(SELECT movie_lens_id FROM p10_saved_embedding)", "INSERT INTO movie_embeddings SELECT movie_lens_id,embedding::vector(768),document_fingerprint,embedding_sr::vector(768),document_fingerprint_sr FROM p10_saved_embedding"),
            ("sr-vector-missing", "UPDATE movie_embeddings SET embedding_sr=NULL,document_fingerprint_sr=NULL WHERE movie_lens_id=(SELECT movie_lens_id FROM p10_saved_embedding)", "UPDATE movie_embeddings SET embedding_sr=(SELECT embedding_sr::vector(768) FROM p10_saved_embedding),document_fingerprint_sr=(SELECT document_fingerprint_sr FROM p10_saved_embedding) WHERE movie_lens_id=(SELECT movie_lens_id FROM p10_saved_embedding)")
        };

        foreach (var testCase in cases)
        {
            await AssertCloneGuardAsync(connection, target, systemIdentifier, otherConnectionCount: 0);
            await ExecuteInTransactionAsync(connection, testCase.MutateSql);
            try
            {
                var search = new CountingRepositorySearch(new MovieSearchAdapter(repository));
                var embedding = new CapturingEmbedding();
                var service = new RealRecommendationService(
                    new FixedParser(new RealParserResult("query", new RealParsedQuery(new RealHardFilters(), "quiet mystery"), LanguageCheck: "match")),
                    new RealParsedQueryValidator(), embedding, search, languageAwarePoc: true);
                var language = testCase.Name.StartsWith("en-", StringComparison.Ordinal) ? "en" : "sr";
                var failure = await Assert.ThrowsExactlyAsync<RealProviderException>(() => service.RecommendAsync(
                    new ParserInput(language, "Мирна мистерија"), CancellationToken.None));
                Assert.AreEqual(ApiErrorCodes.SearchUnavailable, failure.Code, $"Unexpected public failure for clone case {testCase.Name}.");
                Assert.AreEqual(0, embedding.Calls, $"Clone case {testCase.Name} reached E5 before readiness rejection.");
                Assert.AreEqual(0, search.LegacyHybridCalls, $"Clone case {testCase.Name} fell back to the legacy English search.");
                Assert.AreEqual(0, search.LanguageHybridCalls, $"Clone case {testCase.Name} ran semantic SQL after failed readiness.");
                TestContext.WriteLine($"P10_CLONE_CASE case={testCase.Name} result=closed-search-unavailable e5_calls=0 legacy_fallback_calls=0 selected_sql_calls=0");
            }
            finally
            {
                await ExecuteInTransactionAsync(connection, testCase.RestoreSql);
            }
            await repository.EnsureRuntimeReadyAsync();
            TestContext.WriteLine($"P10_CLONE_CASE case={testCase.Name} restoration=full-en-sr-readiness-pass");
        }
        await AssertCloneGuardAsync(connection, target, systemIdentifier, otherConnectionCount: 0);
        await repository.EnsureRuntimeReadyAsync();
        CollectionAssert.AreEqual(baselineDigests, await ReadCloneDigestsAsync(connection), "The clone's five table digests must match its exact pre-fixture baseline.");
        TestContext.WriteLine("P10_CLONE_RESTORE five-table-digests=match full-en-sr-readiness=pass");
    }

    private static async Task<string[]> ReadCloneDigestsAsync(NpgsqlConnection connection)
    {
        const string sql = "SELECT md5(string_agg(md5(row_to_json(m)::text), ',' ORDER BY movie_lens_id)) FROM movies m; " +
            "SELECT md5(string_agg(md5(row_to_json(e)::text), ',' ORDER BY movie_lens_id)) FROM movie_embeddings e; " +
            "SELECT md5(string_agg(row_to_json(c)::text, ',' ORDER BY id)) FROM catalog_import_state c; " +
            "SELECT md5(string_agg(row_to_json(e)::text, ',' ORDER BY language)) FROM embedding_set_state e; " +
            "SELECT md5(string_agg(row_to_json(s)::text, ',' ORDER BY version)) FROM schema_migrations s";
        var digests = new List<string>(5);
        foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            await using var command = new NpgsqlCommand(statement, connection);
            var digest = (string?)await command.ExecuteScalarAsync();
            Assert.IsFalse(string.IsNullOrWhiteSpace(digest), "A clone baseline digest could not be calculated.");
            digests.Add(digest!);
        }
        return digests.ToArray();
    }

    private static async Task AssertCloneReleaseCountsAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("SELECT (SELECT count(*) FROM movies),(SELECT count(*) FROM movie_embeddings),(SELECT count(*) FROM movie_embeddings WHERE embedding IS NOT NULL),(SELECT count(*) FROM movie_embeddings WHERE embedding_sr IS NOT NULL)", connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        for (var i = 0; i < 4; i++) Assert.AreEqual(9730L, reader.GetInt64(i), $"Clone release count column {i} differs from the locked full release.");
    }

    private static MovieSearchRepository CreateCloneRepositoryByTestOnlyReflection(NpgsqlDataSource dataSource,
        SearchDatasetExpectation expectation)
    {
        var constructor = typeof(MovieSearchRepository).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, [typeof(NpgsqlDataSource), typeof(SearchDatasetExpectation)], modifiers: null);
        Assert.IsNotNull(constructor, "Expected private dataset constructor for test-only reflection.");
        return (MovieSearchRepository)constructor!.Invoke([dataSource, expectation]);
    }

    private static async Task AssertCloneGuardAsync(NpgsqlConnection connection, string target, string systemIdentifier,
        int otherConnectionCount)
    {
        await using var command = new NpgsqlCommand("SELECT current_database(),current_schema(),(SELECT system_identifier::text FROM pg_control_system()),(SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND pid<>pg_backend_pid())", connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.AreEqual(target, reader.GetString(0));
        Assert.AreEqual("public", reader.GetString(1));
        Assert.AreEqual(systemIdentifier, reader.GetString(2));
        Assert.AreEqual(otherConnectionCount, reader.GetInt32(3), "Unexpected concurrent clone connection; refusing fixture mutation.");
    }

    private static async Task ExecuteInTransactionAsync(NpgsqlConnection connection, string sql)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<MeasuredQueries> RunQueriesAsync(IReadOnlyList<string> queries, SearchLanguage language,
        MovieSearchRepository repository, E5EmbeddingModel model, Process process)
    {
        var embeddingMs = new List<double>(queries.Count);
        var repositoryMs = new List<double>(queries.Count);
        var counts = new List<int>(queries.Count);
        var sampledPeakWorkingSet = process.WorkingSet64;
        foreach (var query in queries)
        {
            var embeddingTimer = Stopwatch.StartNew();
            var vector = model.EmbedQuery(query);
            embeddingTimer.Stop();
            Assert.AreEqual(768, vector.Length);
            Assert.IsTrue(vector.All(float.IsFinite));
            Assert.AreEqual(1d, Math.Sqrt(vector.Sum(value => (double)value * value)), 1e-5);
            embeddingMs.Add(embeddingTimer.Elapsed.TotalMilliseconds);

            var searchTimer = Stopwatch.StartNew();
            var results = await repository.SearchHybridAsync(new RealHardFilters(), vector, language);
            searchTimer.Stop();
            Assert.IsTrue(results.Count <= MovieSearchRepository.ResultLimit);
            Assert.AreEqual(results.Count, results.Select(movie => movie.MovieLensId).Distinct().Count());
            Assert.IsTrue(results.All(movie => movie.MovieLensId > 0 && !string.IsNullOrWhiteSpace(movie.Title) && movie.ImdbId.All(char.IsAsciiDigit)));
            repositoryMs.Add(searchTimer.Elapsed.TotalMilliseconds);
            counts.Add(results.Count);
            process.Refresh();
            sampledPeakWorkingSet = Math.Max(sampledPeakWorkingSet, process.WorkingSet64);
        }
        return new(embeddingMs, repositoryMs, counts, sampledPeakWorkingSet);
    }

    private void WriteMetrics(string language, MeasuredQueries metrics)
    {
        TestContext.WriteLine($"P10_READONLY_METRICS {language}_count={metrics.EmbeddingMs.Count} embedding_p50_ms={Percentile(metrics.EmbeddingMs, .50):F2} embedding_p95_ms={Percentile(metrics.EmbeddingMs, .95):F2} repository_p50_ms={Percentile(metrics.RepositoryMs, .50):F2} repository_p95_ms={Percentile(metrics.RepositoryMs, .95):F2} observed_result_counts={string.Join(',', metrics.ResultCounts)} sampled_peak_working_set_bytes={metrics.SampledPeakWorkingSetBytes}");
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        var sorted = values.Order().ToArray();
        var index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }

    private static IReadOnlyList<string> EnglishQueries() =>
    [
        "quiet mystery", "dark crime drama", "hopeful space adventure", "gentle family comedy", "slow burn thriller",
        "thoughtful science fiction", "warm romantic comedy", "isolated winter survival", "clever courtroom drama", "lighthearted road trip",
        "haunted house mystery", "understated historical drama", "fast paced action", "small town coming of age", "ambitious political thriller",
        "wistful musical romance", "tense submarine adventure", "absurd workplace comedy", "moving sports drama", "eerie folk horror",
        "optimistic animated adventure", "complex detective story", "quiet friendship story", "classic western", "surreal dreamlike fantasy",
        "intimate family conflict", "high stakes heist", "mysterious island", "sharp social satire", "inspiring biography"
    ];

    private static IReadOnlyList<string> SerbianQueries() =>
    [
        "mirna misterija", "mračna kriminalistička drama", "optimistična svemirska avantura", "nežna porodična komedija", "triler sporog ritma",
        "promišljena naučna fantastika", "topla romantična komedija", "opstanak u izolovanoj zimi", "duhovita sudska drama", "lagano putovanje",
        "misterija uklete kuće", "suzdržana istorijska drama", "brza akciona priča", "odrastanje u malom gradu", "politički triler",
        "setna muzička romansa", "napeta podmornička avantura", "apsurdna poslovna komedija", "dirljiva sportska drama", "jezivi narodni horor",
        "optimistična animirana avantura", "složena detektivska priča", "tiha priča o prijateljstvu", "klasični vestern", "nadrealna fantazija",
        "porodični sukob", "pljačka visokog rizika", "tajanstveno ostrvo", "društvena satira", "inspirativna biografija"
    ];

    private static async Task<WebApplication> BuildApp(FixedParser parser, CapturingEmbedding embedding, CapturingSearch search)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(new RealRecommendationService(parser, new RealParsedQueryValidator(), embedding, search, languageAwarePoc: true));
        var app = builder.Build();
        app.Run(async context =>
        {
            var result = await RealRecommendationEndpoint.HandleAsync(context,
                context.RequestServices.GetRequiredService<RealRecommendationService>(),
                context.RequestServices.GetRequiredService<ILoggerFactory>());
            await result.ExecuteAsync(context);
        });
        await app.StartAsync();
        return app;
    }

    private sealed record MeasuredQueries(IReadOnlyList<double> EmbeddingMs, IReadOnlyList<double> RepositoryMs,
        IReadOnlyList<int> ResultCounts, long SampledPeakWorkingSetBytes);

    private sealed class FixedParser(RealParserResult result) : IRealQueryParser
    {
        public string? ReceivedLanguage { get; private set; }
        public string? ReceivedMessage { get; private set; }
        public Task<RealParserResult> ParseAsync(string language, string message, CancellationToken cancellationToken)
        {
            ReceivedLanguage = language;
            ReceivedMessage = message;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
    }

    private sealed class CapturingEmbedding : IRealQueryEmbeddingProvider
    {
        public int Calls { get; private set; }
        public string? ReceivedQuery { get; private set; }
        public Task<float[]> EmbedQueryAsync(string semanticQuery, CancellationToken cancellationToken)
        {
            Calls++;
            ReceivedQuery = semanticQuery;
            cancellationToken.ThrowIfCancellationRequested();
            var vector = new float[768];
            vector[0] = 1;
            return Task.FromResult(vector);
        }
    }

    private sealed class CountingRepositorySearch(MovieSearchAdapter inner) : IRealMovieSearch
    {
        public int LegacyHybridCalls { get; private set; }
        public int LanguageHybridCalls { get; private set; }
        public Task EnsureSelectedLanguageReadyAsync(SearchLanguage language, CancellationToken cancellationToken) =>
            inner.EnsureSelectedLanguageReadyAsync(language, cancellationToken);
        public Task<IReadOnlyList<FilteredMovie>> SearchHybridAsync(RealHardFilters hardFilters, float[] queryVector,
            CancellationToken cancellationToken)
        {
            LegacyHybridCalls++;
            return inner.SearchHybridAsync(hardFilters, queryVector, cancellationToken);
        }
        public Task<IReadOnlyList<FilteredMovie>> SearchHybridAsync(RealHardFilters hardFilters, float[] queryVector,
            SearchLanguage language, CancellationToken cancellationToken)
        {
            LanguageHybridCalls++;
            return inner.SearchHybridAsync(hardFilters, queryVector, language, cancellationToken);
        }
        public Task<IReadOnlyList<FilteredMovie>> SearchHardOnlyAsync(RealHardFilters hardFilters, CancellationToken cancellationToken) =>
            inner.SearchHardOnlyAsync(hardFilters, cancellationToken);
    }

    private sealed class CapturingSearch(IReadOnlyList<FilteredMovie> results, bool failReadiness = false) : IRealMovieSearch
    {
        public int PreflightCalls { get; private set; }
        public SearchLanguage? PreflightLanguage { get; private set; }
        public int LanguageHybridCalls { get; private set; }
        public SearchLanguage? HybridLanguage { get; private set; }
        public int LegacyHybridCalls { get; private set; }
        public int HardOnlyCalls { get; private set; }
        public RealHardFilters? ReceivedFilters { get; private set; }

        public Task EnsureSelectedLanguageReadyAsync(SearchLanguage language, CancellationToken cancellationToken)
        {
            PreflightCalls++;
            PreflightLanguage = language;
            cancellationToken.ThrowIfCancellationRequested();
            return failReadiness ? Task.FromException(new RealProviderException(ApiErrorCodes.SearchUnavailable)) : Task.CompletedTask;
        }

        public Task<IReadOnlyList<FilteredMovie>> SearchHybridAsync(RealHardFilters hardFilters, float[] queryVector,
            CancellationToken cancellationToken)
        {
            LegacyHybridCalls++;
            ReceivedFilters = hardFilters;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(results);
        }

        public Task<IReadOnlyList<FilteredMovie>> SearchHybridAsync(RealHardFilters hardFilters, float[] queryVector,
            SearchLanguage language, CancellationToken cancellationToken)
        {
            LanguageHybridCalls++;
            HybridLanguage = language;
            ReceivedFilters = hardFilters;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(results);
        }

        public Task<IReadOnlyList<FilteredMovie>> SearchHardOnlyAsync(RealHardFilters hardFilters, CancellationToken cancellationToken)
        {
            HardOnlyCalls++;
            ReceivedFilters = hardFilters;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(results);
        }
    }
}
