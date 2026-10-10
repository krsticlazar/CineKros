using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using CineKros.Api.Database;
using CineKros.Api.RealFlow;
using CineKros.Api.RealProviders;
using CineKros.Api.Search;
using CineKros.Catalog.Importer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pgvector;

namespace CineKros.Api.Tests.PhaseCIntegration;

[TestClass]
[DoNotParallelize]
public sealed class RealApiDatabaseContractTests
{
    private const string Image = "pgvector/pgvector:0.8.6-pg17-bookworm";
    private const string DbPassword = "c10_disposable_only_20260927";
    private const string Profile = "9411a2620fc30e348aa80c9d4e54ca0db5a00d94a92175c82ccdd47ad03b13e1";
    private const string ArtifactHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly string Container = $"cinekros-c10-contract-{Guid.NewGuid():N}";
    private static string _connectionString = "";
    private static NpgsqlDataSource _dataSource = null!;
    private static MovieSearchRepository _repository = null!;
    private static long _eligibleId;
    private static long _ineligibleId;
    private static int _partialYear;
    private static string _c06Database = "";
    private static string _c07Database = "";
    private static string? _previousC06;
    private static string? _previousC07;

    [ClassInitialize]
    public static async Task StartDatabase(TestContext _)
    {
        var existing = await Docker("container", "inspect", Container);
        if (existing.ExitCode == 0) throw new InvalidOperationException("The unique C10 container name already exists; refusing to touch it.");
        var started = await Docker("run", "--detach", "--rm", "--name", Container, "--tmpfs", "/var/lib/postgresql/data",
            "--publish", "127.0.0.1::5432", "--env", "POSTGRES_USER=c10_test", "--env", $"POSTGRES_PASSWORD={DbPassword}",
            "--env", "POSTGRES_DB=c10_bootstrap", Image);
        if (started.ExitCode != 0) throw new InvalidOperationException($"Could not start the named disposable C10 PostgreSQL container: {started.StandardError.Trim()}");
        try
        {
            var port = await Docker("port", Container, "5432/tcp");
            var match = System.Text.RegularExpressions.Regex.Match(port.StandardOutput, @":(?<port>[0-9]+)\s*$", System.Text.RegularExpressions.RegexOptions.Multiline);
            if (!match.Success) throw new InvalidOperationException("Could not resolve the disposable PostgreSQL loopback port.");
            _connectionString = $"Host=127.0.0.1;Port={match.Groups["port"].Value};Database=c10_bootstrap;Username=c10_test;Password={DbPassword};Pooling=false;Timeout=3";
            var deadline = DateTimeOffset.UtcNow.AddSeconds(40);
            while (true)
            {
                try { await using var connection = new NpgsqlConnection(_connectionString); await connection.OpenAsync(); break; }
                catch (NpgsqlException) when (DateTimeOffset.UtcNow < deadline) { await Task.Delay(400); }
            }
            var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
            var migration = await File.ReadAllTextAsync(Path.Combine(root, "database/migrations/001_initial_schema.sql"));
            _c06Database = "cinekros_c06_test";
            _c07Database = "cinekros_c07_test";
            await using (var connection = new NpgsqlConnection(_connectionString))
            {
                await connection.OpenAsync();
                foreach (var databaseName in new[] { _c06Database, _c07Database, "c10_bootstrap" })
                {
                    if (databaseName != "c10_bootstrap")
                    {
                        await using var create = new NpgsqlCommand($"CREATE DATABASE {databaseName}", connection);
                        await create.ExecuteNonQueryAsync();
                    }
                    var databaseConnectionString = new NpgsqlConnectionStringBuilder(_connectionString) { Database = databaseName }.ConnectionString;
                    await using var target = new NpgsqlConnection(databaseConnectionString);
                    await target.OpenAsync();
                    await using var command = new NpgsqlCommand(migration, target);
                    await command.ExecuteNonQueryAsync();
                }
            }
            var catalogPath = Path.Combine(root, "database/data/derived/final/b05a-real-tmdb-01/movies-catalog.jsonl");
            var catalog = await CatalogValidator.LoadAsync(catalogPath);
            await CatalogImporter.ImportAsync(_connectionString, catalog);

            _dataSource = MovieSearchRepository.CreateDataSource(_connectionString);
            _repository = new MovieSearchRepository(_dataSource, Profile);
            await using (var connection = await _dataSource.OpenConnectionAsync())
            {
                await using var pg = new NpgsqlCommand("SELECT version() LIKE 'PostgreSQL 17.%' AND count(*) = 9730 FROM movies", connection);
                Assert.IsTrue((bool)(await pg.ExecuteScalarAsync())!, "Expected PostgreSQL 17 and all 9,730 canonical catalog rows.");
                // This readiness row and these vectors are TEST-ONLY; they are never a real-vector artifact.
                await using var seed = new NpgsqlCommand("""
                    UPDATE catalog_import_state SET embedding_artifact_sha256=@artifact, embedding_profile_fingerprint=@profile, embedded_count=9730 WHERE id=1;
                    INSERT INTO movie_embeddings(movie_lens_id, embedding, document_fingerprint)
                    SELECT movie_lens_id, array_fill(0::real, ARRAY[768])::vector, repeat('b',64) FROM movies;
                    SELECT movie_lens_id FROM movies WHERE genres @> ARRAY['Drama']::text[] ORDER BY movie_lens_id LIMIT 1;
                    """, connection);
                seed.Parameters.AddWithValue("artifact", ArtifactHash);
                seed.Parameters.AddWithValue("profile", Profile);
                // Use the same statement's final result only after its inserts have committed.
                await seed.ExecuteNonQueryAsync();
                await using var ids = new NpgsqlCommand("SELECT (SELECT movie_lens_id FROM movies WHERE genres @> ARRAY['Drama']::text[] ORDER BY movie_lens_id LIMIT 1), (SELECT movie_lens_id FROM movies WHERE genres IS NULL OR NOT genres @> ARRAY['Drama']::text[] ORDER BY movie_lens_id LIMIT 1)", connection);
                await using var reader = await ids.ExecuteReaderAsync();
                Assert.IsTrue(await reader.ReadAsync());
                _eligibleId = reader.GetInt64(0);
                _ineligibleId = reader.GetInt64(1);
            }
            await using (var connection = await _dataSource.OpenConnectionAsync())
            await using (var partial = new NpgsqlCommand("SELECT year FROM movies GROUP BY year HAVING count(*) BETWEEN 1 AND 9 ORDER BY year LIMIT 1", connection))
                _partialYear = Convert.ToInt32(await partial.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            await SetDramaVectors();
            await SetVector(_ineligibleId, UnitVector(1));

            _previousC06 = Environment.GetEnvironmentVariable("C06_TEST_DATABASE");
            _previousC07 = Environment.GetEnvironmentVariable("C07_TEST_DATABASE");
            Environment.SetEnvironmentVariable("C06_TEST_DATABASE", new NpgsqlConnectionStringBuilder(_connectionString) { Database = _c06Database }.ConnectionString);
            Environment.SetEnvironmentVariable("C07_TEST_DATABASE", new NpgsqlConnectionStringBuilder(_connectionString) { Database = _c07Database }.ConnectionString);
        }
        catch
        {
            await StopOwnedContainer();
            throw;
        }
    }

    [ClassCleanup]
    public static async Task StopDatabase()
    {
        Environment.SetEnvironmentVariable("C06_TEST_DATABASE", _previousC06);
        Environment.SetEnvironmentVariable("C07_TEST_DATABASE", _previousC07);
        if (_dataSource is not null) await _dataSource.DisposeAsync();
        await StopOwnedContainer();
    }

    [TestMethod]
    public async Task RealHttpFlowUsesCanonicalMetadataAndTestOnlyVectorsAcrossContractCases()
    {
        await using var app = await BuildApp();
        using var client = app.GetTestClient();

        var sr = await Post(client, "sr", "sr semantic");
        Assert.AreEqual(HttpStatusCode.OK, sr.StatusCode);
        using (var json = JsonDocument.Parse(await sr.Content.ReadAsStringAsync()))
            Assert.AreEqual("movies", json.RootElement.GetProperty("type").GetString());

        var en = await Post(client, "en", "en semantic");
        Assert.AreEqual(HttpStatusCode.OK, en.StatusCode);

        ApiParser.Reset();
        var hybrid = await Post(client, "en", "hybrid");
        using (var json = JsonDocument.Parse(await hybrid.Content.ReadAsStringAsync()))
        {
            var ids = await ResultIds(json.RootElement.GetProperty("movies"));
            Assert.IsTrue(ids.Contains(_eligibleId));
            Assert.IsFalse(ids.Contains(_ineligibleId), "The closest hard-ineligible vector must not be returned.");
            Assert.AreEqual(10, ids.Count, "Eligible filtering must happen before top-ten ranking.");
            CollectionAssert.AreEqual(ids.Order().ToArray(), ids.ToArray(), "Equal synthetic TEST-ONLY vector distances must tie-break by movie_lens_id.");
        }

        var embeddingsBeforeHardOnly = ApiParser.EmbeddingCalls;
        var hardOnly = await Post(client, "sr", "hard-only");
        Assert.AreEqual(HttpStatusCode.OK, hardOnly.StatusCode);
        AssertNoHardOnlyEmbeddingCall(embeddingsBeforeHardOnly, ApiParser.EmbeddingCalls);

        var unsupported = await Post(client, "en", "unsupported");
        using (var json = JsonDocument.Parse(await unsupported.Content.ReadAsStringAsync()))
        {
            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, unsupported.StatusCode);
            Assert.AreEqual("UNSUPPORTED_REQUEST", json.RootElement.GetProperty("alert").GetProperty("code").GetString());
        }

        var none = await Post(client, "en", "no-results");
        using (var json = JsonDocument.Parse(await none.Content.ReadAsStringAsync()))
            Assert.AreEqual("NO_RESULTS", json.RootElement.GetProperty("alert").GetProperty("code").GetString());

        var partial = await Post(client, "en", "partial");
        using (var json = JsonDocument.Parse(await partial.Content.ReadAsStringAsync()))
        {
            Assert.AreEqual("movies", json.RootElement.GetProperty("type").GetString());
            Assert.IsTrue(json.RootElement.GetProperty("meta").GetProperty("partial").GetBoolean());
            Assert.IsTrue(json.RootElement.GetProperty("meta").GetProperty("count").GetInt32() is >= 1 and < 10);
        }

        var full = await Post(client, "en", "full");
        using (var json = JsonDocument.Parse(await full.Content.ReadAsStringAsync()))
        {
            Assert.AreEqual(10, json.RootElement.GetProperty("meta").GetProperty("count").GetInt32());
            Assert.IsFalse(json.RootElement.GetProperty("meta").GetProperty("partial").GetBoolean());
        }

        var malformed = await Post(client, "en", "db-error");
        using (var json = JsonDocument.Parse(await malformed.Content.ReadAsStringAsync()))
        {
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, malformed.StatusCode);
            Assert.AreEqual("SEARCH_UNAVAILABLE", json.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.IsFalse(json.RootElement.ToString().Contains(DbPassword, StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task InclusivePredicatesNullFailureAndMutationProofsHoldAgainstCanonicalRows()
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT
              (SELECT count(*) FROM movies WHERE year = 2000 AND year >= 2000 AND year <= 2000),
              (SELECT count(*) FROM movies WHERE runtime_minutes = 100 AND runtime_minutes >= 100 AND runtime_minutes <= 100),
              (SELECT count(*) FROM movies WHERE genres @> ARRAY['Drama']::text[] AND genres && ARRAY['Drama','Thriller']::text[]),
              (SELECT count(*) FROM movies WHERE average_rating IS NOT NULL AND average_rating >= 0),
              (SELECT count(*) FROM movies WHERE original_language = 'ja'),
              (SELECT count(*) FROM movies WHERE runtime_minutes IS NULL AND runtime_minutes >= 1),
              (SELECT count(*) FROM movies WHERE average_rating IS NULL AND average_rating >= 0)
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.IsTrue(reader.GetInt64(0) > 0, "Year boundary has inclusive matches.");
        Assert.IsTrue(reader.GetInt64(1) > 0, "Runtime boundary has inclusive matches.");
        Assert.IsTrue(reader.GetInt64(2) > 0, "Genre all/any conjunction has matches.");
        Assert.AreEqual(0L, reader.GetInt64(5));
        Assert.AreEqual(0L, reader.GetInt64(6), "Active ratingMin=0 still excludes null values.");

        // Red mutation: global top-ten then filtering admits the closest excluded row and loses an eligible result.
        await reader.DisposeAsync();
        await using var mutant = new NpgsqlCommand("SELECT movie_lens_id FROM movie_embeddings ORDER BY embedding <=> @query ASC, movie_lens_id LIMIT 10", connection);
        mutant.Parameters.AddWithValue("query", new Vector(UnitVector(1)));
        await using var mutantReader = await mutant.ExecuteReaderAsync();
        var globalTopIds = new List<long>();
        while (await mutantReader.ReadAsync()) globalTopIds.Add(mutantReader.GetInt64(0));
        await mutantReader.DisposeAsync();
        Assert.IsTrue(globalTopIds.Contains(_ineligibleId));
        var mutantEligibleCount = await CountDramaIds(globalTopIds);
        Assert.AreEqual(9, mutantEligibleCount, "The premature global top-ten mutant must lose one of the ten eligible rows.");

        await using var sample = new NpgsqlCommand("SELECT movie_lens_id, year, runtime_minutes, genres[1], original_language, average_rating FROM movies WHERE runtime_minutes IS NOT NULL AND genres IS NOT NULL AND original_language='en' AND average_rating IS NOT NULL ORDER BY movie_lens_id LIMIT 1", connection);
        await using var sampleReader = await sample.ExecuteReaderAsync();
        Assert.IsTrue(await sampleReader.ReadAsync(), "Expected at least one fully populated canonical row for combined-filter proof.");
        var sampleId = sampleReader.GetInt64(0);
        var sampleYear = sampleReader.GetInt32(1);
        var sampleRuntime = sampleReader.GetInt32(2);
        var sampleGenre = sampleReader.GetString(3);
        var sampleLanguage = sampleReader.GetString(4);
        var sampleRating = sampleReader.GetDecimal(5);
        await sampleReader.DisposeAsync();
        await using var combined = new NpgsqlCommand("SELECT count(*) FROM movies WHERE movie_lens_id=@id AND year>=@yearMin AND year<=@yearMax AND runtime_minutes>=@runtimeMin AND runtime_minutes<=@runtimeMax AND genres @> @all::text[] AND genres && @any::text[] AND average_rating>=@rating AND original_language=@language", connection);
        combined.Parameters.AddWithValue("id", sampleId);
        combined.Parameters.AddWithValue("yearMin", sampleYear);
        combined.Parameters.AddWithValue("yearMax", sampleYear);
        combined.Parameters.AddWithValue("runtimeMin", sampleRuntime);
        combined.Parameters.AddWithValue("runtimeMax", sampleRuntime);
        combined.Parameters.AddWithValue("all", new[] { sampleGenre });
        combined.Parameters.AddWithValue("any", new[] { sampleGenre });
        combined.Parameters.AddWithValue("rating", sampleRating);
        combined.Parameters.AddWithValue("language", sampleLanguage);
        Assert.AreEqual(1L, (long)(await combined.ExecuteScalarAsync())!, "Every populated category must match conjunctively at its inclusive boundary, with exact language.");
        combined.Parameters["language"].Value = "zz";
        Assert.AreEqual(0L, (long)(await combined.ExecuteScalarAsync())!, "A different exact language must fail the conjunction.");
    }

    [TestMethod]
    public async Task ExistingC06AndC07PostgreSqlRegressionSuitesPassOnSeparateDisposableDatabases()
    {
        var project = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../tests/CineKros.Api.Tests/CineKros.Api.Tests.csproj"));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in new[]
        {
            "test", project, "--no-build", "--no-restore", "--configuration", "Release", "--filter",
            "FullyQualifiedName~CineKros.Api.Tests.Database.HardFilterRepositoryTests|FullyQualifiedName~CineKros.Api.Tests.Search.MovieSearchRepositoryTests"
        }) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdout + await stderr;
        Assert.AreEqual(0, process.ExitCode, "C06/C07 PostgreSQL regression suites must pass on the isolated C10 databases. " + output);
        StringAssert.Contains(output, "total: 13");
        StringAssert.Contains(output, "succeeded: 13");
    }

    [TestMethod]
    public async Task HardOnlyNoEmbeddingAssertionRejectsAProviderCallMutantThroughHttpHarness()
    {
        ApiParser.Reset();
        await using var app = await BuildApp(injectHardOnlyEmbeddingCallMutant: true);
        using var client = app.GetTestClient();
        var callsBefore = ApiParser.EmbeddingCalls;
        using var response = await Post(client, "en", "hard-only");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var callsAfter = ApiParser.EmbeddingCalls;

        // The test-only HTTP middleware injects one incorrect provider call for this hard-only request.
        var rejectedMutant = false;
        try { AssertNoHardOnlyEmbeddingCall(callsBefore, callsAfter); }
        catch (AssertFailedException) { rejectedMutant = true; }
        Assert.IsTrue(rejectedMutant, "The same zero-call assertion used by the normal request must fail under the HTTP-level call mutant.");
        Assert.AreEqual(callsBefore + 1, callsAfter);
    }

    [TestMethod]
    public void ActualProgramRejectsLegacyDisposableDatabaseForFullProductionMode()
    {
        var previousMode = Environment.GetEnvironmentVariable("CINEKROS_RECOMMENDATION_MODE");
        var previousConnection = Environment.GetEnvironmentVariable("DATABASE_CONNECTION_STRING");
        var previousKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        var previousModelDirectory = Environment.GetEnvironmentVariable("CINEKROS_E5_MODEL_DIR");
        Environment.SetEnvironmentVariable("CINEKROS_RECOMMENDATION_MODE", "real");
        Environment.SetEnvironmentVariable("DATABASE_CONNECTION_STRING", _connectionString);
        Environment.SetEnvironmentVariable("GEMINI_API_KEY", "c10-test-only-no-network");
        Environment.SetEnvironmentVariable("CINEKROS_E5_MODEL_DIR", Path.Combine(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../")), "database/data/models/e5-base-v2/f52bf8ec8c7124536f0efb74aca902b2995e5bcd"));
        ApiParser.Reset();
        try
        {
            using var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
            });
            var error = Assert.ThrowsExactly<InvalidOperationException>(() => factory.CreateClient());
            Assert.AreEqual("The configured recommendation database does not match its exact released dataset.", error.Message);
            Assert.AreEqual(0, ApiParser.EmbeddingCalls, "A mismatched legacy database must be rejected before any query embedding.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CINEKROS_RECOMMENDATION_MODE", previousMode);
            Environment.SetEnvironmentVariable("DATABASE_CONNECTION_STRING", previousConnection);
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", previousKey);
            Environment.SetEnvironmentVariable("CINEKROS_E5_MODEL_DIR", previousModelDirectory);
        }
    }

    private static async Task<WebApplication> BuildApp(bool injectHardOnlyEmbeddingCallMutant = false)
    {
        ApiParser.Reset();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IRealQueryParser, ApiParser>();
        builder.Services.AddSingleton<RealParsedQueryValidator>();
        builder.Services.AddSingleton<IRealQueryEmbeddingProvider, ApiEmbedding>();
        builder.Services.AddSingleton<IRealMovieSearch>(new ApiSearch(_repository));
        builder.Services.AddSingleton<RealRecommendationService>();
        var app = builder.Build();
        app.Run(async context =>
        {
            if (injectHardOnlyEmbeddingCallMutant && HttpMethods.IsPost(context.Request.Method) && context.Request.Path == "/api/recommendations")
            {
                context.Request.EnableBuffering();
                using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                var body = await reader.ReadToEndAsync(context.RequestAborted);
                context.Request.Body.Position = 0;
                using var request = JsonDocument.Parse(body);
                if (request.RootElement.GetProperty("message").GetString() == "hard-only")
                    await context.RequestServices.GetRequiredService<IRealQueryEmbeddingProvider>().EmbedQueryAsync("mutant-only injected phrase", context.RequestAborted);
            }
            var result = await RealRecommendationEndpoint.HandleAsync(context,
                context.RequestServices.GetRequiredService<RealRecommendationService>(),
                context.RequestServices.GetRequiredService<ILoggerFactory>());
            await result.ExecuteAsync(context);
        });
        await app.StartAsync();
        return app;
    }

    private static void AssertNoHardOnlyEmbeddingCall(int callsBefore, int callsAfter) =>
        Assert.AreEqual(callsBefore, callsAfter, "Hard-only requests must make zero query-embedding calls.");

    private static Task<HttpResponseMessage> Post(HttpClient client, string language, string message) =>
        client.PostAsync("/api/recommendations", new StringContent(JsonSerializer.Serialize(new { language, message }), Encoding.UTF8, "application/json"));

    private static async Task<List<long>> ResultIds(JsonElement movies)
    {
        var imdbIds = movies.EnumerateArray().Select(movie => movie.GetProperty("imdbUrl").GetString()!).ToArray();
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT movie_lens_id FROM movies WHERE 'https://www.imdb.com/title/tt' || imdb_id || '/' = ANY(@urls)", connection);
        command.Parameters.AddWithValue("urls", imdbIds);
        await using var reader = await command.ExecuteReaderAsync();
        var ids = new List<long>();
        while (await reader.ReadAsync()) ids.Add(reader.GetInt64(0));
        return ids;
    }

    private static async Task SetVector(long id, float[] vector)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("UPDATE movie_embeddings SET embedding=@vector WHERE movie_lens_id=@id", connection);
        command.Parameters.AddWithValue("vector", new Vector(vector));
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SetDramaVectors()
    {
        var eligible = new float[768];
        eligible[0] = 0.8660254f;
        eligible[1] = 0.5f;
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("UPDATE movie_embeddings SET embedding=@vector WHERE movie_lens_id IN (SELECT movie_lens_id FROM movies WHERE genres @> ARRAY['Drama']::text[])", connection);
        command.Parameters.AddWithValue("vector", new Vector(eligible));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> CountDramaIds(IReadOnlyList<long> ids)
    {
        await using var connection = await _dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM movies WHERE movie_lens_id = ANY(@ids) AND genres @> ARRAY['Drama']::text[]", connection);
        command.Parameters.AddWithValue("ids", ids.ToArray());
        return Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static float[] UnitVector(int index)
    {
        var vector = new float[768];
        vector[index] = 1;
        return vector;
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> Docker(params string[] arguments)
    {
        var process = new Process { StartInfo = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true } };
        process.StartInfo.Arguments = "/d /s /c \"docker.exe " + string.Join(" ", arguments) + "\"";
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, output, error);
    }

    private static async Task StopOwnedContainer()
    {
        var inspect = await Docker("container", "inspect", Container);
        if (inspect.ExitCode != 0) return;
        await Docker("rm", "--force", Container);
    }

    private sealed class ApiParser : IRealQueryParser
    {
        private static int _embeddingCalls;
        public static int EmbeddingCalls => _embeddingCalls;
        public static void Reset() => _embeddingCalls = 0;
        public static void RecordEmbeddingCall() => _embeddingCalls++;
        public Task<RealParserResult> ParseAsync(string language, string message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(message switch
            {
                "unsupported" => new RealParserResult("alert", AlertCode: "UNSUPPORTED_REQUEST"),
                "no-results" => new RealParserResult("query", new RealParsedQuery(new RealHardFilters(YearMin: 2015), null)),
                "hard-only" => new RealParserResult("query", new RealParsedQuery(new RealHardFilters(Genres: new RealGenreFilter(All: ["Drama"])), null)),
                "hybrid" => new RealParserResult("query", new RealParsedQuery(new RealHardFilters(Genres: new RealGenreFilter(All: ["Drama"])), "quiet mystery")),
                "partial" => new RealParserResult("query", new RealParsedQuery(new RealHardFilters(YearMin: _partialYear, YearMax: _partialYear), "quiet mystery")),
                "db-error" => new RealParserResult("query", new RealParsedQuery(new RealHardFilters(OriginalLanguage: "ja"), "quiet mystery")),
                "full" => new RealParserResult("query", new RealParsedQuery(new RealHardFilters(Genres: new RealGenreFilter(All: ["Drama"])), "quiet mystery")),
                _ => new RealParserResult("query", new RealParsedQuery(new RealHardFilters(), "quiet mystery"))
            });
        }
    }

    private sealed class ApiEmbedding : IRealQueryEmbeddingProvider
    {
        public Task<float[]> EmbedQueryAsync(string semanticQuery, CancellationToken cancellationToken)
        {
            ApiParser.RecordEmbeddingCall();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(UnitVector(1));
        }
    }

    private sealed class ApiSearch(MovieSearchRepository repository) : IRealMovieSearch
    {
        public Task<IReadOnlyList<FilteredMovie>> SearchHybridAsync(RealHardFilters filters, float[] queryVector, CancellationToken cancellationToken)
        {
            if (filters.OriginalLanguage == "ja") throw new InvalidOperationException("Synthetic database failure must be sanitized.");
            return repository.SearchHybridAsync(filters, queryVector, cancellationToken);
        }

        public Task<IReadOnlyList<FilteredMovie>> SearchHardOnlyAsync(RealHardFilters filters, CancellationToken cancellationToken) =>
            repository.SearchHardOnlyAsync(filters, cancellationToken);
    }
}
