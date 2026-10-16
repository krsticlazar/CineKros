using CineKros.Api.Database;
using CineKros.Api.RealFlow;
using CineKros.Api.RealProviders;
using CineKros.Api.Search;

namespace CineKros.Api.Tests.RealFlow;

[TestClass]
public sealed class V6LanguageGuardIndependentTests
{
    [TestMethod]
    [DataRow("Show me movies with Brad Pitt")]
    [DataRow("Find films starring Tom Hanks")]
    [DataRow("Give me a movie featuring George Clooney")]
    [DataRow("Recommend a film directed by Nolan")]
    [DataRow("I am looking for a movie about courage")]
    [DataRow("Movies starring Tom Hanks")]
    [DataRow("Films released after 2000")]
    public void EnglishRequestGrammarConflictsWithSerbianSelection(string message)
        => Assert.IsTrue(HighConfidenceQueryLanguageGuard.IsMismatch(message, SearchLanguage.Serbian), message);

    [TestMethod]
    [DataRow("Želim film sa Brad Pittom")]
    [DataRow("Пронађи филмове са Томом Хенксом")]
    [DataRow("Daj mi film o prijateljstvu")]
    [DataRow("Filmovi u kojima glumi Tom Henks")]
    [DataRow("Filmovi bez nasilja")]
    public void SerbianRequestGrammarConflictsWithEnglishSelection(string message)
        => Assert.IsTrue(HighConfidenceQueryLanguageGuard.IsMismatch(message, SearchLanguage.English), message);

    [TestMethod]
    public void QuotedEnglishAndSerbianProperNamesDoNotHideSurroundingGrammar()
    {
        Assert.IsTrue(HighConfidenceQueryLanguageGuard.IsMismatch("Find a movie like ‘Сталкер’", SearchLanguage.Serbian));
        Assert.IsTrue(HighConfidenceQueryLanguageGuard.IsMismatch("Желим филм као “Fight Club”", SearchLanguage.English));
        Assert.IsTrue(HighConfidenceQueryLanguageGuard.IsMismatch("Movies featuring O'Neill", SearchLanguage.Serbian));
        Assert.IsTrue(HighConfidenceQueryLanguageGuard.IsMismatch("Желим филм са O'Neill", SearchLanguage.English));
    }

    [TestMethod]
    public void NeutralNamesTitlesAndGenreWordsAreNotLanguageEvidence()
    {
        foreach (var message in new[]
        {
            "Will Smith", "Tom Hanks", "I Am Legend", "I Want You", "Fight Club", "Сталкер",
            "Бред Пит", "drama", "film", "movie", "science fiction", "‘Movies starring Tom Hanks’"
        })
        {
            Assert.IsFalse(HighConfidenceQueryLanguageGuard.IsMismatch(message, SearchLanguage.English), message);
            Assert.IsFalse(HighConfidenceQueryLanguageGuard.IsMismatch(message, SearchLanguage.Serbian), message);
        }
    }

    [TestMethod]
    public void UnbalancedQuotesDoNotSwallowTheWholeRequestAndMixedGrammarAbstains()
    {
        Assert.IsTrue(HighConfidenceQueryLanguageGuard.IsMismatch("Movies starring \"Tom Hanks", SearchLanguage.Serbian));
        Assert.IsTrue(HighConfidenceQueryLanguageGuard.IsMismatch("Želim film sa \"Tom Hanks", SearchLanguage.English));

        const string mixed = "I want a movie with Tom Hanks и филм са Бредом Питом";
        Assert.IsFalse(HighConfidenceQueryLanguageGuard.IsMismatch(mixed, SearchLanguage.English));
        Assert.IsFalse(HighConfidenceQueryLanguageGuard.IsMismatch(mixed, SearchLanguage.Serbian));
    }

    [TestMethod]
    public async Task V6TurnsSchemaValidLyingQueryIntoMismatchAlertBeforeEmbeddingOrSearch()
    {
        var parser = new FakeParser(new RealParserResult(
            "query",
            new RealParsedQuery(new RealHardFilters(Actors: new RealActorFilter(All: ["Tom Hanks"])), null),
            LanguageCheck: "match"));
        var embedding = new FakeEmbedding();
        var search = new FakeSearch();
        var service = new RealRecommendationService(
            parser,
            new RealParsedQueryValidator(new ActorNameResolver(["Tom Hanks"])),
            embedding,
            search,
            languageAwarePoc: true,
            parserProfile: QueryParserProfile.ActorV6);
        const string message = "Movies starring Tom Hanks.";

        var result = await service.RecommendAsync(
            new RealRecommendationRequest(new ParserInput("sr", message), SearchLanguage.Serbian),
            CancellationToken.None);

        Assert.AreEqual(1, parser.Calls, "The guard must not add a provider call or precheck that skips the one parser call.");
        Assert.AreEqual("sr", parser.ReceivedLanguage);
        Assert.AreEqual(message, parser.ReceivedMessage, "Inspection must not rewrite the original request sent to the parser.");
        Assert.AreEqual("alert", result.ParsedResult.Type);
        Assert.AreEqual("LANGUAGE_MISMATCH", result.AlertCode);
        Assert.AreEqual("mismatch", result.ParsedResult.LanguageCheck);
        Assert.IsNull(result.ParsedResult.Query);
        Assert.AreEqual(0, embedding.Calls);
        Assert.AreEqual(0, search.PreflightCalls);
        Assert.AreEqual(0, search.HardOnlyCalls);
        Assert.AreEqual(0, search.HybridCalls);
    }

    [TestMethod]
    public async Task V5ProfileDoesNotApplyTheV6Guard()
    {
        var parser = new FakeParser(Query("still accepted by V5"));
        var embedding = new FakeEmbedding();
        var search = new FakeSearch();
        var service = new RealRecommendationService(
            parser,
            new RealParsedQueryValidator(),
            embedding,
            search,
            languageAwarePoc: true,
            parserProfile: QueryParserProfile.LanguageAwareV5);

        var result = await service.RecommendAsync(
            new RealRecommendationRequest(new ParserInput("sr", "Movies starring Tom Hanks."), SearchLanguage.Serbian),
            CancellationToken.None);

        Assert.AreEqual("query", result.ParsedResult.Type);
        Assert.IsNull(result.AlertCode);
        Assert.AreEqual(1, parser.Calls);
        Assert.AreEqual(1, embedding.Calls);
        Assert.AreEqual(1, search.PreflightCalls);
        Assert.AreEqual(1, search.HybridCalls);
    }

    [TestMethod]
    public async Task GuardDoesNotRescueMalformedTypedProviderResult()
    {
        var parser = new FakeParser(new RealParserResult("query", new RealParsedQuery(new RealHardFilters(), null), LanguageCheck: "match"));
        var embedding = new FakeEmbedding();
        var search = new FakeSearch();
        var service = new RealRecommendationService(
            parser,
            new RealParsedQueryValidator(),
            embedding,
            search,
            languageAwarePoc: true,
            parserProfile: QueryParserProfile.ActorV6);

        await Assert.ThrowsExactlyAsync<RealProviderException>(() => service.RecommendAsync(
            new RealRecommendationRequest(new ParserInput("sr", "Movies starring Tom Hanks."), SearchLanguage.Serbian),
            CancellationToken.None));

        Assert.AreEqual(1, parser.Calls);
        Assert.AreEqual(0, embedding.Calls);
        Assert.AreEqual(0, search.PreflightCalls);
        Assert.AreEqual(0, search.TotalCalls);
    }

    private static RealParserResult Query(string semantic) => new(
        "query", new RealParsedQuery(new RealHardFilters(YearMin: 2000), semantic), LanguageCheck: "match");

    private sealed class FakeParser(RealParserResult result) : IRealQueryParser
    {
        public int Calls { get; private set; }
        public string? ReceivedLanguage { get; private set; }
        public string? ReceivedMessage { get; private set; }
        public Task<RealParserResult> ParseAsync(string language, string message, CancellationToken cancellationToken)
        {
            Calls++;
            ReceivedLanguage = language;
            ReceivedMessage = message;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeEmbedding : IRealQueryEmbeddingProvider
    {
        public int Calls { get; private set; }
        public Task<float[]> EmbedQueryAsync(string semanticQuery, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new float[768]);
        }
    }

    private sealed class FakeSearch : IRealMovieSearch
    {
        public int PreflightCalls { get; private set; }
        public int HardOnlyCalls { get; private set; }
        public int HybridCalls { get; private set; }
        public int TotalCalls => PreflightCalls + HardOnlyCalls + HybridCalls;
        public Task EnsureSelectedLanguageReadyAsync(SearchLanguage language, CancellationToken cancellationToken)
        {
            PreflightCalls++;
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<FilteredMovie>> SearchHardOnlyAsync(RealHardFilters hardFilters, CancellationToken cancellationToken)
        {
            HardOnlyCalls++;
            return Task.FromResult<IReadOnlyList<FilteredMovie>>([]);
        }
        public Task<IReadOnlyList<FilteredMovie>> SearchHybridAsync(RealHardFilters hardFilters, float[] queryVector, CancellationToken cancellationToken)
        {
            HybridCalls++;
            return Task.FromResult<IReadOnlyList<FilteredMovie>>([]);
        }
        public Task<IReadOnlyList<FilteredMovie>> SearchHybridAsync(RealHardFilters hardFilters, float[] queryVector, SearchLanguage language, CancellationToken cancellationToken)
        {
            HybridCalls++;
            return Task.FromResult<IReadOnlyList<FilteredMovie>>([]);
        }
    }
}
