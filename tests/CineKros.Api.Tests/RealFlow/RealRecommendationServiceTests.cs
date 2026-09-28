using CineKros.Api;
using CineKros.Api.Database;
using CineKros.Api.RealFlow;
using CineKros.Api.RealProviders;

namespace CineKros.Api.Tests.RealFlow;

[TestClass]
public sealed class RealRecommendationServiceTests
{
    [TestMethod]
    public async Task HardOnlySkipsEmbeddingAndPreservesEveryFilter()
    {
        var hardFilters = new RealHardFilters(YearMin: 2001, YearMax: 2010, RuntimeMin: 80, RuntimeMax: 120,
            Genres: new RealGenreFilter(All: ["Sci-Fi"], Any: ["Drama", "Thriller"]), RatingMin: 3.5m, OriginalLanguage: "ja");
        var parser = new FakeParser(Query(hardFilters, null));
        var embedding = new FakeEmbedding();
        var search = new FakeSearch();

        var result = await Service(parser, embedding, search).RecommendAsync(new ParserInput("sr", "filmovi posle 2000"), CancellationToken.None);

        Assert.AreEqual(0, embedding.Calls, "Hard-only must make zero embedding requests; this assertion fails if the branch embeds filters.");
        Assert.AreEqual("sr", parser.ReceivedLanguage);
        Assert.AreEqual(1, search.HardOnlyCalls);
        Assert.AreSame(hardFilters, search.ReceivedFilters);
        Assert.AreEqual(0, search.HybridCalls);
        Assert.IsNull(result.AlertCode);
    }

    [TestMethod]
    public async Task HybridEmbedsExactlyOnceAndPreservesFiltersForSearch()
    {
        var filters = new RealHardFilters(YearMin: 2000, Genres: new RealGenreFilter(Any: ["Drama", "Thriller"]));
        var parser = new FakeParser(Query(filters, "moody drama or thriller"));
        var embedding = new FakeEmbedding();
        var search = new FakeSearch();

        await Service(parser, embedding, search).RecommendAsync(new ParserInput("en", "moody drama or thriller since 2000"), CancellationToken.None);

        Assert.AreEqual(1, embedding.Calls);
        Assert.AreEqual("en", parser.ReceivedLanguage);
        Assert.AreEqual("moody drama or thriller", embedding.ReceivedQuery);
        Assert.AreEqual(1, search.HybridCalls);
        Assert.AreSame(filters, search.ReceivedFilters, "A dropped-filter mutant must fail this exact-filter assertion.");
        Assert.AreEqual(0, search.HardOnlyCalls);
    }

    [TestMethod]
    public async Task ParserAlertsArePreservedAndDoNotCallEmbeddingOrSearch()
    {
        foreach (var code in new[] { "UNSUPPORTED_REQUEST", "QUERY_UNCLEAR", "NOT_MOVIE_REQUEST" })
        {
            var embedding = new FakeEmbedding();
            var search = new FakeSearch();
            var result = await Service(new FakeParser(new RealParserResult("alert", AlertCode: code)), embedding, search)
                .RecommendAsync(new ParserInput("sr", "zahtev"), CancellationToken.None);
            Assert.AreEqual(code, result.AlertCode);
            Assert.AreEqual(0, embedding.Calls);
            Assert.AreEqual(0, search.TotalCalls);
        }
    }

    [TestMethod]
    public async Task UnsupportedRatingChecklistMapsToAlertBeforeEmbeddingOrSearch()
    {
        const string providerJson = """
            {"type":"query","query":{"year":{"status":"absent","min":null,"max":null},"runtime":{"status":"absent","min":null,"max":null},"genres":{"status":"absent","all":[],"any":[]},"rating":{"status":"unsupported","value":null,"operator":null,"scale":null},"originalLanguage":{"status":"absent","value":null},"semanticQuery":null},"alertCode":null}
            """;
        var parsed = new RealParsedQueryValidator().Validate(providerJson);
        Assert.AreEqual("alert", parsed.Type);
        Assert.AreEqual("UNSUPPORTED_REQUEST", parsed.AlertCode);

        var embedding = new FakeEmbedding();
        var search = new FakeSearch();
        var result = await Service(new FakeParser(parsed), embedding, search)
            .RecommendAsync(new ParserInput("sr", "Filmovi sa ocenom većom od 8"), CancellationToken.None);

        Assert.AreEqual("UNSUPPORTED_REQUEST", result.AlertCode);
        Assert.AreEqual(0, embedding.Calls);
        Assert.AreEqual(0, search.TotalCalls);
    }

    [TestMethod]
    public async Task PositiveActorAndDirectorChecklistsRemainSemanticAndExclusionsStopBeforeSearch()
    {
        const string actorDto = """
            {"type":"query","query":{"year":{"status":"absent","min":null,"max":null},"runtime":{"status":"absent","min":null,"max":null},"genres":{"status":"absent","all":[],"any":[]},"rating":{"status":"absent","value":null,"operator":null,"scale":null},"originalLanguage":{"status":"absent","value":null},"semanticQuery":"movies starring Brad Pitt"},"alertCode":null}
            """;
        const string directorDto = """
            {"type":"query","query":{"year":{"status":"absent","min":null,"max":null},"runtime":{"status":"absent","min":null,"max":null},"genres":{"status":"absent","all":[],"any":[]},"rating":{"status":"absent","value":null,"operator":null,"scale":null},"originalLanguage":{"status":"absent","value":null},"semanticQuery":"movies directed by Christopher Nolan"},"alertCode":null}
            """;
        const string combinedDto = """
            {"type":"query","query":{"year":{"status":"present","min":null,"max":2008},"runtime":{"status":"absent","min":null,"max":null},"genres":{"status":"present","all":["Sci-Fi"],"any":[]},"rating":{"status":"absent","value":null,"operator":null,"scale":null},"originalLanguage":{"status":"absent","value":null},"semanticQuery":"movies starring Brad Pitt"},"alertCode":null}
            """;
        var validator = new RealParsedQueryValidator();
        foreach (var (dto, input, expectedSemantic) in new[]
        {
            (actorDto, "Brad Pitt movie", "movies starring Brad Pitt"),
            (directorDto, "directed by Christopher Nolan", "movies directed by Christopher Nolan")
        })
        {
            var parsed = validator.Validate(dto);
            Assert.AreEqual("query", parsed.Type);
            Assert.IsFalse(RealParsedQueryValidator.HasActiveFilter(parsed.Query!.HardFilters));
            Assert.AreEqual(expectedSemantic, parsed.Query.SemanticQuery);
            var embedding = new FakeEmbedding();
            var search = new FakeSearch();
            var result = await Service(new FakeParser(parsed), embedding, search)
                .RecommendAsync(new ParserInput("en", input), CancellationToken.None);
            Assert.IsNull(result.AlertCode);
            Assert.AreEqual(1, embedding.Calls);
            Assert.AreEqual(expectedSemantic, embedding.ReceivedQuery);
            Assert.AreEqual(1, search.HybridCalls);
            Assert.IsFalse(RealParsedQueryValidator.HasActiveFilter(search.ReceivedFilters!));
        }

        var combined = validator.Validate(combinedDto).Query!;
        Assert.AreEqual(2008, combined.HardFilters.YearMax);
        CollectionAssert.AreEqual(new[] { "Sci-Fi" }, combined.HardFilters.Genres!.All!.ToArray());
        Assert.AreEqual("movies starring Brad Pitt", combined.SemanticQuery);
        var combinedEmbedding = new FakeEmbedding();
        var combinedSearch = new FakeSearch();
        _ = await Service(new FakeParser(new RealParserResult("query", combined)), combinedEmbedding, combinedSearch)
            .RecommendAsync(new ParserInput("en", "Sci-Fi through 2008 starring Brad Pitt"), CancellationToken.None);
        Assert.AreEqual(1, combinedEmbedding.Calls);
        Assert.AreEqual(1, combinedSearch.HybridCalls);
        Assert.AreEqual(2008, combinedSearch.ReceivedFilters!.YearMax);
        CollectionAssert.AreEqual(new[] { "Sci-Fi" }, combinedSearch.ReceivedFilters.Genres!.All!.ToArray());

        var exclusion = validator.Validate("""
            {"type":"alert","query":null,"alertCode":"UNSUPPORTED_REQUEST"}
            """);
        var exclusionEmbedding = new FakeEmbedding();
        var exclusionSearch = new FakeSearch();
        var exclusionResult = await Service(new FakeParser(exclusion), exclusionEmbedding, exclusionSearch)
            .RecommendAsync(new ParserInput("en", "without Brad Pitt"), CancellationToken.None);
        Assert.AreEqual("UNSUPPORTED_REQUEST", exclusionResult.AlertCode);
        Assert.AreEqual(0, exclusionEmbedding.Calls);
        Assert.AreEqual(0, exclusionSearch.TotalCalls);
    }

    [TestMethod]
    public async Task RecommendationServiceUsesOneStructuredParserResultWithoutReinterpretingSourceText()
    {
        var filters = new RealHardFilters(YearMin: 2016, RuntimeMax: 109, Genres: new RealGenreFilter(All: ["Sci-Fi"]), RatingMin: 4m);
        var parser = new FakeParser(Query(filters, "dark"));
        var embedding = new FakeEmbedding();
        var search = new FakeSearch();
        var result = await Service(parser, embedding, search)
            .RecommendAsync(new ParserInput("en", "rating over 2, before 1900, original language Chinese"), CancellationToken.None);

        Assert.AreEqual(1, parser.Calls);
        Assert.AreSame(filters, search.ReceivedFilters);
        Assert.AreEqual(1, embedding.Calls);
        Assert.AreEqual(1, search.TotalCalls);
        Assert.IsNull(result.AlertCode);
    }

    [TestMethod]
    public async Task InvalidInjectedParserResultsFailBeforeEmbeddingOrSearch()
    {
        var invalid = new[]
        {
            new RealParserResult("alert", AlertCode: "INTERNAL_ERROR"),
            Query(new RealHardFilters(), null),
            Query(new RealHardFilters(YearMin: 2020, YearMax: 2000), "science fiction"),
            Query(new RealHardFilters(YearMin: 2000), "   "),
            Query(new RealHardFilters(), null)
        };
        foreach (var parsed in invalid)
        {
            var embedding = new FakeEmbedding();
            var search = new FakeSearch();
            var error = await Assert.ThrowsExactlyAsync<RealProviderException>(() => Service(new FakeParser(parsed), embedding, search)
                .RecommendAsync(new ParserInput("en", "a valid film request"), CancellationToken.None));
            Assert.AreEqual("PARSER_INVALID_RESPONSE", error.Code);
            Assert.AreEqual(0, embedding.Calls);
            Assert.AreEqual(0, search.TotalCalls);
        }
    }

    [TestMethod]
    public async Task ProviderAndDatabaseFailuresAreSanitizedAndCancellationPropagates()
    {
        var parserFailure = await Assert.ThrowsExactlyAsync<RealProviderException>(() => Service(new FakeParser(null, new Exception("secret parser body")), new FakeEmbedding(), new FakeSearch())
            .RecommendAsync(new ParserInput("en", "a valid film request"), CancellationToken.None));
        Assert.AreEqual("PROVIDER_UNAVAILABLE", parserFailure.Code);
        Assert.AreEqual("PROVIDER_UNAVAILABLE", parserFailure.Message);

        var embeddingFailure = await Assert.ThrowsExactlyAsync<RealProviderException>(() => Service(new FakeParser(Query(new RealHardFilters(), "quiet mystery")), new FakeEmbedding(new Exception("secret response")), new FakeSearch())
            .RecommendAsync(new ParserInput("en", "quiet mystery"), CancellationToken.None));
        Assert.AreEqual("SEARCH_UNAVAILABLE", embeddingFailure.Code);

        var dbFailure = await Assert.ThrowsExactlyAsync<RealProviderException>(() => Service(new FakeParser(Query(new RealHardFilters(), "quiet mystery")), new FakeEmbedding(), new FakeSearch(new Exception("connection password detail")))
            .RecommendAsync(new ParserInput("en", "quiet mystery"), CancellationToken.None));
        Assert.AreEqual("SEARCH_UNAVAILABLE", dbFailure.Code);
        Assert.AreEqual("SEARCH_UNAVAILABLE", dbFailure.Message);

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => Service(new FakeParser(null, new OperationCanceledException()), new FakeEmbedding(), new FakeSearch())
            .RecommendAsync(new ParserInput("en", "quiet mystery"), cancelled.Token));
    }

    [TestMethod]
    public async Task ResultCardCountsStayAtZeroPartialAndTenWithoutProviderDtos()
    {
        foreach (var count in new[] { 0, 1, 9, 10 })
        {
            var filters = new RealHardFilters(YearMin: 2000);
            var parser = new FakeParser(Query(filters, "quiet mystery"));
            var embedding = new FakeEmbedding();
            var search = new FakeSearch { Results = Enumerable.Range(1, count).Select(id => new FilteredMovie(id, $"Film {id}", 2000, id.ToString("0000000"), $"/poster-{id}.jpg", null, null)).ToArray() };
            var result = await Service(parser, embedding, search).RecommendAsync(new ParserInput("en", "quiet mystery"), CancellationToken.None);
            Assert.AreEqual(count, result.Movies.Count);
            Assert.AreEqual(count == 0, result.Movies.Count == 0);
            Assert.IsFalse(result.Movies.Any(movie => movie.Title.Contains("provider", StringComparison.OrdinalIgnoreCase)));
        }
    }

    [TestMethod]
    public async Task DuplicateOrMoreThanTenSearchRowsAreRejected()
    {
        foreach (var movies in new IReadOnlyList<FilteredMovie>[]
        {
            [new(1, "Film 1", 2000, "1", null, null, null), new(1, "Film duplicate", 2000, "2", null, null, null)],
            Enumerable.Range(1, 11).Select(id => new FilteredMovie(id, $"Film {id}", 2000, id.ToString(), null, null, null)).ToArray()
        })
        {
            var service = Service(new FakeParser(Query(new RealHardFilters(YearMin: 2000), "quiet")), new FakeEmbedding(), new FakeSearch { Results = movies });
            var error = await Assert.ThrowsExactlyAsync<RealProviderException>(() => service.RecommendAsync(new ParserInput("en", "quiet mystery"), CancellationToken.None));
            Assert.AreEqual("SEARCH_UNAVAILABLE", error.Code);
        }
    }

    private static RealRecommendationService Service(FakeParser parser, FakeEmbedding embedding, FakeSearch search) => new(parser, new RealParsedQueryValidator(), embedding, search);
    private static RealParserResult Query(RealHardFilters filters, string? semantic) => new("query", new RealParsedQuery(filters, semantic));

    private sealed class FakeParser(RealParserResult? result, Exception? failure = null) : IRealQueryParser
    {
        public int Calls { get; private set; }
        public string? ReceivedLanguage { get; private set; }
        public Task<RealParserResult> ParseAsync(string language, string message, CancellationToken cancellationToken)
        {
            Calls++;
            ReceivedLanguage = language;
            cancellationToken.ThrowIfCancellationRequested();
            if (failure is not null) return Task.FromException<RealParserResult>(failure);
            return Task.FromResult(result!);
        }
    }

    private sealed class FakeEmbedding(Exception? failure = null) : IRealQueryEmbeddingProvider
    {
        public int Calls { get; private set; }
        public string? ReceivedQuery { get; private set; }
        public Task<float[]> EmbedQueryAsync(string semanticQuery, CancellationToken cancellationToken)
        {
            Calls++;
            ReceivedQuery = semanticQuery;
            cancellationToken.ThrowIfCancellationRequested();
            if (failure is not null) return Task.FromException<float[]>(failure);
            var vector = new float[768];
            vector[0] = 1;
            return Task.FromResult(vector);
        }
    }

    private sealed class FakeSearch(Exception? failure = null) : IRealMovieSearch
    {
        public int HybridCalls { get; private set; }
        public int HardOnlyCalls { get; private set; }
        public int TotalCalls => HybridCalls + HardOnlyCalls;
        public RealHardFilters? ReceivedFilters { get; private set; }
        public IReadOnlyList<FilteredMovie> Results { get; init; } = [new(1, "Synthetic Movie", 2000, "0000001", null, null, null)];

        public Task<IReadOnlyList<FilteredMovie>> SearchHybridAsync(RealHardFilters hardFilters, float[] queryVector, CancellationToken cancellationToken) => Search(hardFilters, cancellationToken, hybrid: true);
        public Task<IReadOnlyList<FilteredMovie>> SearchHardOnlyAsync(RealHardFilters hardFilters, CancellationToken cancellationToken) => Search(hardFilters, cancellationToken, hybrid: false);

        private Task<IReadOnlyList<FilteredMovie>> Search(RealHardFilters filters, CancellationToken token, bool hybrid)
        {
            if (hybrid) HybridCalls++; else HardOnlyCalls++;
            ReceivedFilters = filters;
            token.ThrowIfCancellationRequested();
            return failure is null ? Task.FromResult(Results) : Task.FromException<IReadOnlyList<FilteredMovie>>(failure);
        }
    }
}
