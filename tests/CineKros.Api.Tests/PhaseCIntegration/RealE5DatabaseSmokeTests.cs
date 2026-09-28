using CineKros.Api.Database;
using CineKros.Api.RealFlow;
using CineKros.Api.RealProviders;
using CineKros.Api.Search;
using CineKros.Embedding;

namespace CineKros.Api.Tests.PhaseCIntegration;

[TestClass]
public sealed class RealE5DatabaseSmokeTests
{
    [TestMethod]
    public async Task RealE5DatabaseIsReadyAndSupportsReadOnlyHybridAndHardOnlySearch()
    {
        var connectionString = Environment.GetEnvironmentVariable("C12_REAL_DATABASE");
        if (string.IsNullOrWhiteSpace(connectionString))
            Assert.Inconclusive("Set C12_REAL_DATABASE to opt in to the persistent real-vector smoke test.");

        var modelDirectory = Environment.GetEnvironmentVariable("CINEKROS_E5_MODEL_DIR");
        Assert.IsFalse(string.IsNullOrWhiteSpace(modelDirectory), "CINEKROS_E5_MODEL_DIR must identify the pinned local E5 model.");

        using var model = new E5EmbeddingModel(modelDirectory!);
        await using var dataSource = MovieSearchRepository.CreateDataSource(connectionString!);
        await VerifyReadOnlyCatalogAsync(dataSource, model.ProfileFingerprint);

        var repository = new MovieSearchRepository(dataSource, model.ProfileFingerprint);
        var hybridFilters = new RealHardFilters(YearMin: 2000);
        var queryVector = model.EmbedQuery("quiet English language mystery with an isolated setting");
        var first = await repository.SearchHybridAsync(hybridFilters, queryVector);
        var second = await repository.SearchHybridAsync(hybridFilters, queryVector);

        Assert.IsTrue(first.Count <= MovieSearchRepository.ResultLimit);
        Assert.AreEqual(first.Count, first.Select(movie => movie.MovieLensId).Distinct().Count());
        Assert.IsTrue(first.All(movie => movie.Year >= 2000), "Hybrid search must retain its year hard filter.");
        CollectionAssert.AreEqual(first.Select(movie => movie.MovieLensId).ToArray(), second.Select(movie => movie.MovieLensId).ToArray(),
            "Repeated exact searches must return the same ordered catalog IDs.");

        var embeddingSpy = new CountingEmbeddingProvider();
        var search = new RepositorySearchAdapter(repository);
        var service = new RealRecommendationService(new HardOnlyParser(), new RealParsedQueryValidator(), embeddingSpy, search);
        var hardOnly = await service.RecommendAsync(new ParserInput("en", "films from 2000 onward"), CancellationToken.None);

        Assert.AreEqual(0, embeddingSpy.Calls, "The hard-filter-only flow must not invoke the E5 adapter.");
        Assert.IsTrue(hardOnly.Movies.Count <= MovieSearchRepository.ResultLimit);
        Assert.IsTrue(hardOnly.Movies.All(movie => movie.Year >= 2000));
    }

    private static async Task VerifyReadOnlyCatalogAsync(Npgsql.NpgsqlDataSource dataSource, string expectedProfileFingerprint)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        const string sql = """
            SELECT
                (SELECT count(*) FROM movies),
                (SELECT count(*) FROM movie_embeddings),
                (SELECT embedding_profile_fingerprint FROM catalog_import_state WHERE id = 1),
                (SELECT embedded_count FROM catalog_import_state WHERE id = 1),
                (SELECT count(*) FROM movie_embeddings
                  WHERE embedding IS NULL
                     OR vector_dims(embedding) <> 768
                     OR vector_norm(embedding) IS NULL
                     OR vector_norm(embedding) <= 0
                     OR vector_norm(embedding) = 'NaN'::real
                     OR vector_norm(embedding) = 'Infinity'::real
                     OR abs(vector_norm(embedding) - 1.0) > 0.0001)
            """;
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.AreEqual(9730L, reader.GetInt64(0), "Real catalog must contain exactly 9,730 movies.");
        Assert.AreEqual(9730L, reader.GetInt64(1), "Real catalog must contain exactly 9,730 embeddings.");
        Assert.AreEqual(expectedProfileFingerprint, reader.GetString(2), "Imported vectors must use the pinned current E5 profile.");
        Assert.AreEqual(9730, reader.GetInt32(3), "Import state must report exactly 9,730 embedded movies.");
        Assert.AreEqual(0L, reader.GetInt64(4), "Every stored vector must be a valid normalized 768-dimensional vector.");
    }

    private sealed class HardOnlyParser : IRealQueryParser
    {
        public Task<RealParserResult> ParseAsync(string language, string message, CancellationToken cancellationToken) =>
            Task.FromResult(new RealParserResult("query", new RealParsedQuery(new RealHardFilters(YearMin: 2000), null)));
    }

    private sealed class CountingEmbeddingProvider : IRealQueryEmbeddingProvider
    {
        public int Calls { get; private set; }

        public Task<float[]> EmbedQueryAsync(string semanticQuery, CancellationToken cancellationToken)
        {
            Calls++;
            throw new AssertFailedException("Hard-filter-only flow unexpectedly requested an E5 query vector.");
        }
    }

    private sealed class RepositorySearchAdapter(MovieSearchRepository repository) : IRealMovieSearch
    {
        public Task<IReadOnlyList<FilteredMovie>> SearchHybridAsync(RealHardFilters hardFilters, float[] queryVector, CancellationToken cancellationToken) =>
            repository.SearchHybridAsync(hardFilters, queryVector, cancellationToken);

        public Task<IReadOnlyList<FilteredMovie>> SearchHardOnlyAsync(RealHardFilters hardFilters, CancellationToken cancellationToken) =>
            repository.SearchHardOnlyAsync(hardFilters, cancellationToken);
    }
}
