using CineKros.Api.Database;
using CineKros.Api.RealProviders;
using CineKros.Api.Search;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Npgsql;
using Pgvector;

namespace CineKros.Api.Tests.Search;

[TestClass]
public sealed class MovieSearchRepositoryTests
{
    private const string ProfileFingerprint = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly string? ConnectionString = Environment.GetEnvironmentVariable("C07_TEST_DATABASE");

    [TestMethod]
    public async Task HybridFiltersBeforeRankingAndUsesExactDistanceIdTieAndTenLimit()
    {
        await using var database = await TestDatabase.OpenAsync();
        await database.ResetAsync();
        await database.SetYearAsync(1, 1999);
        await database.SetVectorsAsync(Enumerable.Range(1, 15).Select(id => (long)id).ToArray(), VectorWith(1, 1));
        await database.SetVectorsAsync([1], VectorWith(0, 1));

        var repository = database.Repository();
        var result = await repository.SearchHybridAsync(new RealHardFilters(YearMin: 2000), VectorWith(0, 1));

        CollectionAssert.AreEqual(Enumerable.Range(2, 10).Select(id => (long)id).ToArray(), result.Select(movie => movie.MovieLensId).ToArray());
        Assert.IsFalse(result.Any(movie => movie.MovieLensId == 1), "The closest but ineligible movie must be excluded before ranking.");

        // Red proof: the intentionally wrong global-top-k-then-filter plan loses an eligible result.
        var mutant = await database.RunPrematureTopTenMutantAsync(VectorWith(0, 1));
        Assert.AreEqual(9, mutant.Length);
        Assert.AreNotEqual(result.Count, mutant.Length);

        // Red proof: reversing the exact-distance tie key violates the required ascending ID order.
        var reverseTieMutant = await database.RunReverseTieMutantAsync(VectorWith(0, 1));
        Assert.AreEqual(10, reverseTieMutant.Length);
        CollectionAssert.AreEqual(Enumerable.Range(6, 10).Select(id => (long)id).Reverse().ToArray(), reverseTieMutant);
        Assert.AreNotEqual(result[0].MovieLensId, reverseTieMutant[0]);
    }

    [TestMethod]
    [DataRow(0, 0)]
    [DataRow(1, 1)]
    [DataRow(9, 9)]
    [DataRow(10, 10)]
    [DataRow(15, 10)]
    public async Task HybridReturnsZeroOneNineTenAndMoreThanTenCaps(int eligibleRows, int expectedRows)
    {
        await using var database = await TestDatabase.OpenAsync();
        await database.ResetAsync();
        await database.SetYearAsync(1, 2010, eligibleRows);
        await database.SetVectorsAsync(Enumerable.Range(1, eligibleRows).Select(id => (long)id).ToArray(), VectorWith(0, 1));

        var result = await database.Repository().SearchHybridAsync(new RealHardFilters(YearMin: 2010), VectorWith(0, 1));
        Assert.AreEqual(expectedRows, result.Count);
    }

    [TestMethod]
    public async Task HardOnlyUsesRatingCountThenAverageThenIdAndWorksWithoutEmbeddings()
    {
        await using var database = await TestDatabase.OpenAsync();
        await database.ResetAsync();
        await database.SetRatingsAsync();

        var repository = database.Repository();
        var result = await repository.SearchHardOnlyAsync(new RealHardFilters(YearMin: 2000));
        CollectionAssert.AreEqual(new long[] { 2, 3, 1, 5, 4 }, result.Take(5).Select(movie => movie.MovieLensId).ToArray());

        // Red proof: a mutated rating-count direction does not satisfy the accepted order.
        var wrongOrder = await database.RunHardOnlyOrderMutantAsync();
        CollectionAssert.AreEqual(new long[] { 5, 1, 2, 3, 4 }, wrongOrder.Take(5).ToArray());
        CollectionAssert.AreNotEqual(result.Take(5).Select(movie => movie.MovieLensId).ToArray(), wrongOrder.Take(5).ToArray());

        await database.ClearEmbeddingsAsync();
        result = await repository.SearchHardOnlyAsync(new RealHardFilters(YearMin: 2000));
        CollectionAssert.AreEqual(new long[] { 2, 3, 1, 5, 4 }, result.Take(5).Select(movie => movie.MovieLensId).ToArray());
    }

    [TestMethod]
    public async Task SemanticReadinessRejectsCatalogProfileAndEmbeddingCountMismatch()
    {
        await using var database = await TestDatabase.OpenAsync();
        await database.ResetAsync();
        await AssertSearchUnavailableAsync(() => database.Repository("cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc")
            .SearchHybridAsync(new RealHardFilters(), VectorWith(0, 1)));

        await database.DeleteEmbeddingAsync(9730);
        await AssertSearchUnavailableAsync(() => database.Repository()
            .SearchHybridAsync(new RealHardFilters(), VectorWith(0, 1)));

        await database.ResetAsync();
        await database.SetCatalogFingerprintAsync("dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd");
        await AssertSearchUnavailableAsync(() => database.Repository()
            .SearchHybridAsync(new RealHardFilters(), VectorWith(0, 1)));

        await database.ResetAsync();
        await database.SetCatalogJsonlShaAsync("eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee");
        await AssertSearchUnavailableAsync(() => database.Repository()
            .SearchHybridAsync(new RealHardFilters(), VectorWith(0, 1)));
    }

    [TestMethod]
    public async Task HardOnlyRejectsMissingFilterAndDatabaseHasNoApproximateVectorIndex()
    {
        await using var database = await TestDatabase.OpenAsync();
        await database.ResetAsync();
        var missingFilter = await Assert.ThrowsExactlyAsync<RealProviderException>(() => database.Repository().SearchHardOnlyAsync(new RealHardFilters()));
        Assert.AreEqual("PARSER_INVALID_RESPONSE", missingFilter.Code);

        var indexes = await database.VectorIndexDefinitionsAsync();
        Assert.IsFalse(indexes.Any(index => index.Contains("hnsw", StringComparison.OrdinalIgnoreCase) || index.Contains("ivfflat", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task HybridRejectsInvalidQueryVectorsBeforeDatabaseAccess()
    {
        await using var unavailableDataSource = NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1");
        var repository = new MovieSearchRepository(unavailableDataSource, ProfileFingerprint);
        await AssertProviderUnavailableAsync(() => repository.SearchHybridAsync(new RealHardFilters(), []));
        await AssertProviderUnavailableAsync(() => repository.SearchHybridAsync(new RealHardFilters(), new float[767]));
        await AssertProviderUnavailableAsync(() => repository.SearchHybridAsync(new RealHardFilters(), new float[768]));
        var nonFinite = VectorWith(0, 1);
        nonFinite[12] = float.NaN;
        await AssertProviderUnavailableAsync(() => repository.SearchHybridAsync(new RealHardFilters(), nonFinite));
    }

    private static float[] VectorWith(int index, float value)
    {
        var vector = new float[768];
        vector[index] = value;
        return vector;
    }

    private static async Task AssertSearchUnavailableAsync(Func<Task<IReadOnlyList<FilteredMovie>>> action)
    {
        var exception = await Assert.ThrowsExactlyAsync<RealProviderException>(action);
        Assert.AreEqual("SEARCH_UNAVAILABLE", exception.Code);
    }

    private static async Task AssertProviderUnavailableAsync(Func<Task<IReadOnlyList<FilteredMovie>>> action)
    {
        var exception = await Assert.ThrowsExactlyAsync<RealProviderException>(action);
        Assert.AreEqual("PROVIDER_UNAVAILABLE", exception.Code);
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly NpgsqlDataSource _dataSource;

        private TestDatabase(NpgsqlDataSource dataSource) => _dataSource = dataSource;

        public static async Task<TestDatabase> OpenAsync()
        {
            if (string.IsNullOrWhiteSpace(ConnectionString))
                Assert.Inconclusive("Set C07_TEST_DATABASE to the specifically named worker-owned PostgreSQL 17/pgvector disposable database.");
            var dataSource = MovieSearchRepository.CreateDataSource(ConnectionString!);
            var database = new TestDatabase(dataSource);
            await database.VerifyIdentityAndSchemaAsync();
            return database;
        }

        public MovieSearchRepository Repository(string profileFingerprint = ProfileFingerprint) => new(_dataSource, profileFingerprint);

        public async Task ResetAsync()
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("TRUNCATE TABLE movie_embeddings, movies, catalog_import_state CASCADE; " +
                "INSERT INTO movies (movie_lens_id, imdb_id, title, year) " +
                "SELECT id, 'tt' || lpad(id::text, 7, '0'), 'Movie ' || id, 2000 FROM generate_series(1, 9730) AS id; " +
                "INSERT INTO movie_embeddings (movie_lens_id, embedding, document_fingerprint) " +
                "SELECT movie_lens_id, array_fill(0::real, ARRAY[768])::vector, repeat('a', 64) FROM movies; " +
                "INSERT INTO catalog_import_state (id, catalog_version, catalog_jsonl_sha256, catalog_content_fingerprint, movie_count, embedding_artifact_sha256, embedding_profile_fingerprint, embedded_count) " +
                "VALUES (1, 'B05a-combined-catalog-v1', '8b2bad0a22fef45842176a1d9f3730be1568e367b9fc230fa0aa398bb5c26946', '2ffad7ba703cb80543db617e742a61c88871332910185767ee96fe08a77a0be7', 9730, repeat('a', 64), 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb', 9730)", connection);
            await command.ExecuteNonQueryAsync();
        }

        public async Task SetYearAsync(long firstId, int year, int count = 1)
        {
            if (count == 0) return;
            await ExecuteAsync("UPDATE movies SET year = @year WHERE movie_lens_id BETWEEN @first AND @last",
                ("year", year), ("first", firstId), ("last", firstId + count - 1));
        }

        public async Task SetVectorsAsync(long[] ids, float[] vector)
        {
            if (ids.Length == 0) return;
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("UPDATE movie_embeddings SET embedding = @vector WHERE movie_lens_id = ANY(@ids)", connection);
            command.Parameters.AddWithValue("vector", new Vector(vector));
            command.Parameters.AddWithValue("ids", ids);
            await command.ExecuteNonQueryAsync();
        }

        public async Task SetRatingsAsync()
        {
            await ExecuteAsync("UPDATE movies SET average_rating = CASE movie_lens_id WHEN 1 THEN 3.0 WHEN 2 THEN 4.0 WHEN 3 THEN 4.0 WHEN 5 THEN 5.0 END, " +
                "rating_count = CASE movie_lens_id WHEN 1 THEN 50 WHEN 2 THEN 50 WHEN 3 THEN 50 WHEN 5 THEN 10 END WHERE movie_lens_id BETWEEN 1 AND 5");
        }

        public Task ClearEmbeddingsAsync() => ExecuteAsync("TRUNCATE TABLE movie_embeddings; UPDATE catalog_import_state SET embedding_artifact_sha256 = NULL, embedding_profile_fingerprint = NULL, embedded_count = NULL WHERE id = 1");
        public Task DeleteEmbeddingAsync(long id) => ExecuteAsync("DELETE FROM movie_embeddings WHERE movie_lens_id = @id", ("id", id));
        public Task SetCatalogFingerprintAsync(string fingerprint) => ExecuteAsync("UPDATE catalog_import_state SET catalog_content_fingerprint = @fingerprint WHERE id = 1", ("fingerprint", fingerprint));
        public Task SetCatalogJsonlShaAsync(string sha) => ExecuteAsync("UPDATE catalog_import_state SET catalog_jsonl_sha256 = @sha WHERE id = 1", ("sha", sha));

        public async Task<string[]> VectorIndexDefinitionsAsync()
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("SELECT indexdef FROM pg_indexes WHERE schemaname = current_schema() AND tablename = 'movie_embeddings'", connection);
            await using var reader = await command.ExecuteReaderAsync();
            var indexes = new List<string>();
            while (await reader.ReadAsync()) indexes.Add(reader.GetString(0));
            return indexes.ToArray();
        }

        public async Task<long[]> RunPrematureTopTenMutantAsync(float[] vector)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            const string sql = "WITH global_top AS MATERIALIZED (SELECT movie_lens_id FROM movie_embeddings ORDER BY embedding <=> @queryVector ASC LIMIT 10) " +
                "SELECT m.movie_lens_id FROM global_top g JOIN movies m USING (movie_lens_id) WHERE m.year >= 2000 ORDER BY m.movie_lens_id";
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("queryVector", new Vector(vector));
            await using var reader = await command.ExecuteReaderAsync();
            var ids = new List<long>();
            while (await reader.ReadAsync()) ids.Add(reader.GetInt64(0));
            return ids.ToArray();
        }

        public async Task<long[]> RunReverseTieMutantAsync(float[] vector)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            const string sql = "SELECT movie_lens_id FROM movies JOIN movie_embeddings USING (movie_lens_id) WHERE year >= 2000 ORDER BY embedding <=> @queryVector ASC, movie_lens_id DESC LIMIT 10";
            await using var command = new NpgsqlCommand(sql, connection);
            command.Parameters.AddWithValue("queryVector", new Vector(vector));
            await using var reader = await command.ExecuteReaderAsync();
            var ids = new List<long>();
            while (await reader.ReadAsync()) ids.Add(reader.GetInt64(0));
            return ids.ToArray();
        }

        public async Task<long[]> RunHardOnlyOrderMutantAsync()
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            const string sql = "SELECT movie_lens_id FROM movies WHERE year >= 2000 ORDER BY rating_count ASC NULLS LAST, average_rating ASC NULLS LAST, movie_lens_id ASC LIMIT 10";
            await using var command = new NpgsqlCommand(sql, connection);
            await using var reader = await command.ExecuteReaderAsync();
            var ids = new List<long>();
            while (await reader.ReadAsync()) ids.Add(reader.GetInt64(0));
            return ids.ToArray();
        }

        private async Task VerifyIdentityAndSchemaAsync()
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand("SELECT current_database(), current_setting('server_version_num')::int, EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'vector'), to_regclass('movies') IS NOT NULL AND to_regclass('movie_embeddings') IS NOT NULL AND to_regclass('catalog_import_state') IS NOT NULL", connection);
            await using var reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();
            Assert.AreEqual("cinekros_c07_test", reader.GetString(0), "Refusing to mutate a database not explicitly named for C07.");
            Assert.IsTrue(reader.GetInt32(1) >= 170000, "C07 requires PostgreSQL 17 or newer.");
            Assert.IsTrue(reader.GetBoolean(2), "The migrated test database must have pgvector installed.");
            Assert.IsTrue(reader.GetBoolean(3), "The C07 fixture requires the migrated runtime schema.");
        }

        private async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
            await command.ExecuteNonQueryAsync();
        }

        public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
    }
}
