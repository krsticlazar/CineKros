using CineKros.Api.Database;
using CineKros.Api.RealProviders;
using CineKros.Api.Search;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Npgsql;
using Pgvector;
using System.Text.Json;

namespace CineKros.Api.Tests.Search;

[TestClass]
public sealed class SrPocSearchTests
{
    private const string TestDatabase = "cinekros_sr_poc_phase04_test_search";
    private const string RetainedDatabase = "cinekros_sr_poc_phase04_20261008";

    [TestMethod]
    public async Task ActualGenericResourceVectorsExerciseTopTenInBothColumns()
    {
        var connection = Environment.GetEnvironmentVariable("CINEKROS_SR_POC_CONNECTION");
        var path = Environment.GetEnvironmentVariable("CINEKROS_SR_POC_QUERY_VECTORS");
        if (string.IsNullOrWhiteSpace(connection) || string.IsNullOrWhiteSpace(path))
            Assert.Inconclusive("Set the retained POC connection and frozen generic resource-query-vector path.");
        using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
        Assert.AreEqual("multilingual-poc-resource-query-vectors-v1", json.RootElement.GetProperty("format").GetString());
        Assert.AreEqual("eac906ed78f7863573d13c9b0435de1b8f848fe92fc6ae08aa3621400260b1fe", json.RootElement.GetProperty("profileFingerprint").GetString());
        Assert.AreEqual("efd13b367a6ad0bef10ec4f32e89d1b57180a2d95546423175a4d1c1dc3bc230", json.RootElement.GetProperty("catalogIdentitySha256").GetString());
        await using var dataSource = MovieSearchRepository.CreateDataSource(connection);
        var repository = await MovieSearchRepository.CreatePocRepositoryAsync(dataSource);
        foreach (var languageBlock in json.RootElement.GetProperty("languages").EnumerateArray())
        {
            var language = languageBlock.GetProperty("language").GetString() switch
            {
                "en" => SearchLanguage.English,
                "sr" => SearchLanguage.Serbian,
                _ => throw new InvalidDataException("Unexpected fixed probe language.")
            };
            await repository.EnsureSelectedLanguageReadyAsync(language);
            var probes = languageBlock.GetProperty("vectors").EnumerateArray().ToArray();
            Assert.AreEqual(4, probes.Length);
            foreach (var probe in probes)
            {
                var vector = probe.GetProperty("vector").EnumerateArray().Select(value => value.GetSingle()).ToArray();
                Assert.AreEqual(768, vector.Length); Assert.IsTrue(vector.All(float.IsFinite));
                var results = await repository.SearchHybridAsync(new RealHardFilters(), vector, language);
                Assert.AreEqual(10, results.Count);
                Assert.AreEqual(10, results.Select(movie => movie.MovieLensId).Distinct().Count());
            }
        }
    }

    [TestMethod]
    public async Task SentinelVectorsProveLanguageSeparationAndMissingSrNeverFallsBack()
    {
        var retained = Environment.GetEnvironmentVariable("CINEKROS_SR_POC_CONNECTION");
        if (string.IsNullOrWhiteSpace(retained)) Assert.Inconclusive("Set CINEKROS_SR_POC_CONNECTION to the retained POC connection for the isolated clone test.");
        var baseBuilder = new NpgsqlConnectionStringBuilder(retained) { Database = "postgres", Pooling = false };
        var testBuilder = new NpgsqlConnectionStringBuilder(retained) { Database = TestDatabase, Pooling = false };
        await using (var admin = new NpgsqlConnection(baseBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var check = new NpgsqlCommand("SELECT current_database(), count(*) FROM pg_database WHERE datname=@db", admin);
            check.Parameters.AddWithValue("db", TestDatabase);
            await using var reader = await check.ExecuteReaderAsync(); await reader.ReadAsync();
            Assert.AreEqual("postgres", reader.GetString(0)); Assert.AreEqual(0L, reader.GetInt64(1));
            await reader.DisposeAsync();
            await using var clone = new NpgsqlCommand($"CREATE DATABASE {TestDatabase} TEMPLATE {RetainedDatabase}", admin);
            await clone.ExecuteNonQueryAsync();
        }

        try
        {
            await using var dataSource = MovieSearchRepository.CreateDataSource(testBuilder.ConnectionString);
            await UpdateStateAsync(dataSource, "UPDATE embedding_set_state SET catalog_content_fingerprint='efd13b367a6ad0bef10ec4f32e89d1b57180a2d95546423175a4d1c1dc3bc230'");
            var repository = await MovieSearchRepository.CreatePocRepositoryAsync(dataSource);
            await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => repository.SearchHybridAsync(new RealHardFilters(), new float[768], (SearchLanguage)42));
            long enId; long srId;
            await using (var connection = await dataSource.OpenConnectionAsync())
            await using (var command = new NpgsqlCommand("SELECT movie_lens_id FROM movies ORDER BY movie_lens_id LIMIT 2", connection))
            await using (var reader = await command.ExecuteReaderAsync())
            {
                Assert.IsTrue(await reader.ReadAsync()); enId = reader.GetInt64(0);
                Assert.IsTrue(await reader.ReadAsync()); srId = reader.GetInt64(0);
            }
            var enSentinel = new float[768]; enSentinel[0] = 1;
            var other = new float[768]; other[2] = 1;
            await using (var connection = await dataSource.OpenConnectionAsync())
            {
                await using var command = new NpgsqlCommand("UPDATE movie_embeddings SET embedding=@other,embedding_sr=@other WHERE movie_lens_id NOT IN (@enId,@srId); UPDATE movie_embeddings SET embedding=@en WHERE movie_lens_id=@enId; UPDATE movie_embeddings SET embedding=@other WHERE movie_lens_id=@srId; UPDATE movie_embeddings SET embedding_sr=@other WHERE movie_lens_id=@enId; UPDATE movie_embeddings SET embedding_sr=@sr WHERE movie_lens_id=@srId", connection);
                command.Parameters.AddWithValue("en", new Vector(enSentinel)); command.Parameters.AddWithValue("sr", new Vector(enSentinel)); command.Parameters.AddWithValue("other", new Vector(other));
                command.Parameters.AddWithValue("enId", enId); command.Parameters.AddWithValue("srId", srId);
                await command.ExecuteNonQueryAsync();
            }
            await using (var connection = await dataSource.OpenConnectionAsync())
            await using (var command = new NpgsqlCommand("UPDATE movies SET year=1900,runtime_minutes=100,genres=ARRAY['Drama','Thriller'],average_rating=4.0,rating_count=10,original_language='ja' WHERE movie_lens_id=@id", connection))
            { command.Parameters.AddWithValue("id", enId); await command.ExecuteNonQueryAsync(); }
            var en = await repository.SearchHybridAsync(new RealHardFilters(), enSentinel, SearchLanguage.English);
            var sr = await repository.SearchHybridAsync(new RealHardFilters(), enSentinel, SearchLanguage.Serbian);
            Assert.AreEqual(enId, en[0].MovieLensId); Assert.AreEqual(srId, sr[0].MovieLensId);
            var inclusiveAndConjoined = await repository.SearchHardOnlyAsync(new RealHardFilters(YearMin: 1900, YearMax: 1900,
                RuntimeMin: 100, RuntimeMax: 100, Genres: new RealGenreFilter(All: ["Drama"], Any: ["Thriller"]),
                RatingMin: 4m, OriginalLanguage: "ja"));
            CollectionAssert.AreEqual(new[] { enId }, inclusiveAndConjoined.Select(movie => movie.MovieLensId).ToArray());
            var eligibleTopTen = await repository.SearchHybridAsync(new RealHardFilters(YearMin: 1901), enSentinel, SearchLanguage.English);
            Assert.AreEqual(10, eligibleTopTen.Count); Assert.IsFalse(eligibleTopTen.Any(movie => movie.MovieLensId == enId));

            await UpdateStateAsync(dataSource, "UPDATE embedding_set_state SET catalog_content_fingerprint='2ffad7ba703cb80543db617e742a61c88871332910185767ee96fe08a77a0be7' WHERE language='sr'");
            var oldSrIdentity = await Assert.ThrowsExactlyAsync<RealProviderException>(() => repository.SearchHybridAsync(new RealHardFilters(), enSentinel, SearchLanguage.Serbian));
            Assert.AreEqual("SEARCH_UNAVAILABLE", oldSrIdentity.Code);
            Assert.AreEqual(enId, (await repository.SearchHybridAsync(new RealHardFilters(), enSentinel, SearchLanguage.English))[0].MovieLensId);
            await UpdateStateAsync(dataSource, "UPDATE embedding_set_state SET catalog_content_fingerprint='efd13b367a6ad0bef10ec4f32e89d1b57180a2d95546423175a4d1c1dc3bc230' WHERE language='sr'");
            await UpdateStateAsync(dataSource, "UPDATE embedding_set_state SET catalog_content_fingerprint='2ffad7ba703cb80543db617e742a61c88871332910185767ee96fe08a77a0be7' WHERE language='en'");
            var oldEnIdentity = await Assert.ThrowsExactlyAsync<RealProviderException>(() => repository.SearchHybridAsync(new RealHardFilters(), enSentinel, SearchLanguage.English));
            Assert.AreEqual("SEARCH_UNAVAILABLE", oldEnIdentity.Code);
            Assert.AreEqual(srId, (await repository.SearchHybridAsync(new RealHardFilters(), enSentinel, SearchLanguage.Serbian))[0].MovieLensId);
            await UpdateStateAsync(dataSource, "UPDATE embedding_set_state SET catalog_content_fingerprint='efd13b367a6ad0bef10ec4f32e89d1b57180a2d95546423175a4d1c1dc3bc230' WHERE language='en'");

            await UpdateStateAsync(dataSource, "UPDATE embedding_set_state SET profile_fingerprint=repeat('0',64) WHERE language='sr'");
            var wrongProfile = await Assert.ThrowsExactlyAsync<RealProviderException>(() => repository.SearchHybridAsync(new RealHardFilters(), enSentinel, SearchLanguage.Serbian));
            Assert.AreEqual("SEARCH_UNAVAILABLE", wrongProfile.Code);
            Assert.AreEqual(enId, (await repository.SearchHybridAsync(new RealHardFilters(), enSentinel, SearchLanguage.English))[0].MovieLensId);
            await UpdateStateAsync(dataSource, "UPDATE embedding_set_state SET profile_fingerprint='eac906ed78f7863573d13c9b0435de1b8f848fe92fc6ae08aa3621400260b1fe' WHERE language='sr'");
            await UpdateStateAsync(dataSource, "UPDATE embedding_set_state SET corpus_sha256=repeat('0',64) WHERE language='sr'");
            var wrongCorpus = await Assert.ThrowsExactlyAsync<RealProviderException>(() => repository.SearchHybridAsync(new RealHardFilters(), enSentinel, SearchLanguage.Serbian));
            Assert.AreEqual("SEARCH_UNAVAILABLE", wrongCorpus.Code);
            await UpdateStateAsync(dataSource, "UPDATE embedding_set_state SET corpus_sha256='16a525a58795af3278c7a2741e1c06b2dd32c154835f532cf939b4f0da7133d3' WHERE language='sr'");

            await using (var connection = await dataSource.OpenConnectionAsync())
            await using (var command = new NpgsqlCommand("UPDATE movie_embeddings SET embedding_sr=NULL,document_fingerprint_sr=NULL", connection))
                await command.ExecuteNonQueryAsync();
            var unavailable = await Assert.ThrowsExactlyAsync<RealProviderException>(() => repository.SearchHybridAsync(new RealHardFilters(), enSentinel, SearchLanguage.Serbian));
            Assert.AreEqual("SEARCH_UNAVAILABLE", unavailable.Code);
            var preflightUnavailable = await Assert.ThrowsExactlyAsync<RealProviderException>(() => repository.EnsureSelectedLanguageReadyAsync(SearchLanguage.Serbian));
            Assert.AreEqual("SEARCH_UNAVAILABLE", preflightUnavailable.Code);
            Assert.AreEqual(enId, (await repository.SearchHybridAsync(new RealHardFilters(), enSentinel, SearchLanguage.English))[0].MovieLensId);
            CollectionAssert.AreEqual(new[] { enId }, (await repository.SearchHardOnlyAsync(new RealHardFilters(YearMin: 1900, YearMax: 1900,
                RuntimeMin: 100, RuntimeMax: 100, Genres: new RealGenreFilter(All: ["Drama"], Any: ["Thriller"]),
                RatingMin: 4m, OriginalLanguage: "ja"))).Select(movie => movie.MovieLensId).ToArray());
        }
        finally
        {
            await using var target = new NpgsqlConnection(testBuilder.ConnectionString); await target.OpenAsync();
            Assert.AreEqual(TestDatabase, await new NpgsqlCommand("SELECT current_database()", target).ExecuteScalarAsync()); await target.CloseAsync();
            await using var admin = new NpgsqlConnection(baseBuilder.ConnectionString); await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE {TestDatabase}", admin); await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task UpdateStateAsync(NpgsqlDataSource dataSource,string sql)
    { await using var c=await dataSource.OpenConnectionAsync(); await using var cmd=new NpgsqlCommand(sql,c); await cmd.ExecuteNonQueryAsync(); }
}
