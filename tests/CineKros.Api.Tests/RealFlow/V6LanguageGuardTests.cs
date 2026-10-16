using CineKros.Api;
using CineKros.Api.Database;
using CineKros.Api.RealFlow;
using CineKros.Api.RealProviders;
using CineKros.Api.Search;

namespace CineKros.Api.Tests.RealFlow;

[TestClass]
public sealed class V6LanguageGuardTests
{
    [TestMethod]
    [DataRow("Movies starring Tom Hanks.", "sr")]
    [DataRow("Films featuring Meryl Streep", "sr")]
    [DataRow("Movies with Denzel Washington", "sr")]
    [DataRow("Films directed by Sofia Coppola", "sr")]
    [DataRow("Please show me movies about friendship", "sr")]
    [DataRow("Find films from 1990", "sr")]
    [DataRow("Give me a film", "sr")]
    [DataRow("Recommend me a movie", "sr")]
    [DataRow("I want a movie", "sr")]
    [DataRow("I would like a film", "sr")]
    [DataRow("I am looking for films", "sr")]
    [DataRow("Želim film sa Brad Pittom", "en")]
    [DataRow("Prikaži mi filmove o prijateljstvu", "en")]
    [DataRow("Филмови у којима глуме Том Хенкс и Тим Ален", "en")]
    [DataRow("Molim te pronadji film o porodici", "en")]
    [DataRow("Filmovi bez glumca", "en")]
    [DataRow("Movies starring O'Neill", "sr")]
    [DataRow("Želim film sa O'Neillom", "en")]
    public void StrongOppositeGrammarIsDetected(string message, string selected)
        => Assert.IsTrue(HighConfidenceQueryLanguageGuard.IsMismatch(message, Language(selected)), message);

    [TestMethod]
    [DataRow("Brad Pitt")]
    [DataRow("Tom Hanks")]
    [DataRow("Fight Club")]
    [DataRow("The English Patient")]
    [DataRow("I Am Legend")]
    [DataRow("I Want You")]
    [DataRow("Бред Пит")]
    [DataRow("Сталкер")]
    [DataRow("movie")]
    [DataRow("film")]
    [DataRow("drama")]
    [DataRow("SF")]
    [DataRow("Movies starring")]
    [DataRow("Filmovi sa")]
    [DataRow("Movies starring Brad Pitt bez nasilja")]
    [DataRow("Želim film with Brad Pitt")]
    [DataRow("Filmovi about friendship")]
    [DataRow("Movies sa glumcem")]
    [DataRow("\"Show me movies\"")]
    [DataRow("“Show me movies”")]
    [DataRow("«Show me movies»")]
    [DataRow("'Show me movies'")]
    public void NeutralQuotedIncompleteAndMixedInputsAbstain(string message)
    {
        Assert.IsFalse(HighConfidenceQueryLanguageGuard.IsMismatch(message, SearchLanguage.English), message);
        Assert.IsFalse(HighConfidenceQueryLanguageGuard.IsMismatch(message, SearchLanguage.Serbian), message);
    }

    [TestMethod]
    [DataRow("Želim film kao \"Show me movies\"", "sr")]
    [DataRow("Желим филм као “I want movies”", "sr")]
    [DataRow("Filmovi sa Brad Pittom nalik «Movies with friends»", "sr")]
    [DataRow("Želim film kao 'Movies starring someone'", "sr")]
    [DataRow("I want movies like \"Желим филм\"", "en")]
    [DataRow("Show movies with O'Neill", "en")]
    [DataRow("Филмови у којима глуме Том Хенкс", "sr")]
    public void CorrectLanguageAndQuotedTitlesAreAccepted(string message, string selected)
        => Assert.IsFalse(HighConfidenceQueryLanguageGuard.IsMismatch(message, Language(selected)), message);

    [TestMethod]
    public async Task LyingParserStopsHardOnlyAndHybridBeforeAnyDownstreamCall()
    {
        foreach (var semantic in new string?[] { null, "hopeful story" })
        foreach (var (language, message) in new[] { ("sr", "Movies starring Tom Hanks."), ("en", "Želim film sa Brad Pittom") })
        {
            var fixture = new Fixture(Query(semantic));
            var result = await fixture.Service().RecommendAsync(new ParserInput(language, message), CancellationToken.None);
            Assert.AreEqual("LANGUAGE_MISMATCH", result.AlertCode);
            Assert.AreEqual(new RealParserResult("alert", AlertCode: "LANGUAGE_MISMATCH", LanguageCheck: "mismatch"), result.ParsedResult);
            Assert.HasCount(0, result.Movies);
            Assert.AreEqual(1, fixture.ParserCalls);
            Assert.AreEqual(message, fixture.Message);
            fixture.AssertNoDownstream();
        }
    }

    [TestMethod]
    public async Task LanguagePrecedenceOverridesMatchedAlertsButPreservesProviderLanguageBranches()
    {
        foreach (var code in new[] { "QUERY_UNCLEAR", "UNSUPPORTED_REQUEST", "NOT_MOVIE_REQUEST" })
        {
            var fixture = new Fixture(new("alert", AlertCode: code, LanguageCheck: "match"));
            var result = await fixture.Service().RecommendAsync(new ParserInput("sr", "Movies without a director"), CancellationToken.None);
            Assert.AreEqual("LANGUAGE_MISMATCH", result.AlertCode);
            Assert.AreEqual(1, fixture.ParserCalls);
            fixture.AssertNoDownstream();
        }
        foreach (var parsed in new[] { new RealParserResult("alert", AlertCode: "LANGUAGE_MISMATCH", LanguageCheck: "mismatch"), new("alert", AlertCode: "QUERY_UNCLEAR", LanguageCheck: "unclear") })
        {
            var fixture = new Fixture(parsed);
            var result = await fixture.Service().RecommendAsync(new ParserInput("sr", "Movies starring Tom Hanks"), CancellationToken.None);
            Assert.AreSame(parsed, result.ParsedResult);
            fixture.AssertNoDownstream();
        }
    }

    [TestMethod]
    public async Task MalformedInjectedResultsAndProviderFailuresAreNeverRescued()
    {
        foreach (var parsed in new[] { new RealParserResult("query", LanguageCheck: "match"), Query(null) with { LanguageCheck = null }, new("alert", AlertCode: "INTERNAL_ERROR", LanguageCheck: "match"), new("query", new(new(YearMin: 2020, YearMax: 2000), null), LanguageCheck: "match") })
        {
            var fixture = new Fixture(parsed);
            var error = await Assert.ThrowsExactlyAsync<RealProviderException>(() => fixture.Service().RecommendAsync(new ParserInput("sr", "Movies starring Tom Hanks"), CancellationToken.None));
            Assert.AreEqual("PARSER_INVALID_RESPONSE", error.Code);
            Assert.AreEqual(1, fixture.ParserCalls);
            fixture.AssertNoDownstream();
        }
        var failure = new Fixture(Query(null)) { ParserFailure = new RealProviderException("PARSER_INVALID_RESPONSE") };
        Assert.AreEqual("PARSER_INVALID_RESPONSE", (await Assert.ThrowsExactlyAsync<RealProviderException>(() => failure.Service().RecommendAsync(new ParserInput("sr", "Movies starring Tom Hanks"), CancellationToken.None))).Code);
        failure.AssertNoDownstream();
    }

    [TestMethod]
    public async Task V5AndLegacyKeepExistingBehaviorAndV6PreservesFiltersAndSemantic()
    {
        foreach (var profile in new[] { QueryParserProfile.LegacyV4, QueryParserProfile.LanguageAwareV5, QueryParserProfile.ActorV6 })
        {
            var filters = new RealHardFilters(YearMin: 1990);
            var parsed = new RealParserResult("query", new(filters, "hopeful story"), LanguageCheck: profile == QueryParserProfile.LegacyV4 ? null : "match");
            var fixture = new Fixture(parsed);
            var message = profile == QueryParserProfile.ActorV6 ? "Želim film kao Fight Club" : "Movies starring Tom Hanks";
            var result = await fixture.Service(profile).RecommendAsync(new ParserInput("sr", message), CancellationToken.None);
            Assert.AreSame(parsed, result.ParsedResult);
            Assert.AreSame(filters, fixture.Filters);
            Assert.AreEqual("hopeful story", fixture.Semantic);
            Assert.AreEqual(1, fixture.ParserCalls);
            Assert.AreEqual(1, fixture.EmbeddingCalls);
            Assert.AreEqual(1, fixture.SearchCalls);
        }
    }

    private static SearchLanguage Language(string value) => value == "en" ? SearchLanguage.English : SearchLanguage.Serbian;
    private static RealParserResult Query(string? semantic) => new("query", new(new(Actors: new(All: ["Tom Hanks"])), semantic), LanguageCheck: "match");

    private sealed class Fixture(RealParserResult parsed) : IRealQueryParser, IRealQueryEmbeddingProvider, IRealMovieSearch
    {
        public int ParserCalls, EmbeddingCalls, SearchCalls, PreflightCalls;
        public string? Message, Semantic;
        public RealHardFilters? Filters;
        public Exception? ParserFailure { get; init; }
        public RealRecommendationService Service(QueryParserProfile profile = QueryParserProfile.ActorV6) => new(this,
            new RealParsedQueryValidator(new ActorNameResolver(["Tom Hanks"])), this, this,
            languageAwarePoc: profile != QueryParserProfile.LegacyV4, parserProfile: profile);
        public Task<RealParserResult> ParseAsync(string language, string message, CancellationToken cancellationToken)
        {
            ParserCalls++; Message = message;
            return ParserFailure is null ? Task.FromResult(parsed) : Task.FromException<RealParserResult>(ParserFailure);
        }
        public Task<float[]> EmbedQueryAsync(string semanticQuery, CancellationToken cancellationToken)
        { EmbeddingCalls++; Semantic = semanticQuery; return Task.FromResult(new float[768]); }
        public Task EnsureSelectedLanguageReadyAsync(SearchLanguage language, CancellationToken cancellationToken)
        { PreflightCalls++; return Task.CompletedTask; }
        public Task<IReadOnlyList<FilteredMovie>> SearchHardOnlyAsync(RealHardFilters hardFilters, CancellationToken cancellationToken)
        { SearchCalls++; Filters = hardFilters; return Task.FromResult<IReadOnlyList<FilteredMovie>>([]); }
        public Task<IReadOnlyList<FilteredMovie>> SearchHybridAsync(RealHardFilters hardFilters, float[] queryVector, CancellationToken cancellationToken)
            => SearchHardOnlyAsync(hardFilters, cancellationToken);
        public Task<IReadOnlyList<FilteredMovie>> SearchHybridAsync(RealHardFilters hardFilters, float[] queryVector, SearchLanguage language, CancellationToken cancellationToken)
            => SearchHardOnlyAsync(hardFilters, cancellationToken);
        public void AssertNoDownstream()
        {
            Assert.AreEqual(0, PreflightCalls);
            Assert.AreEqual(0, EmbeddingCalls);
            Assert.AreEqual(0, SearchCalls);
        }
    }
}
