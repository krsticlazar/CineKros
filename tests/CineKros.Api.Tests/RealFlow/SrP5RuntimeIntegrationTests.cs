using CineKros.Api;
using CineKros.Api.Database;
using CineKros.Api.RealFlow;
using CineKros.Api.RealProviders;
using CineKros.Api.Search;
using CineKros.Catalog.Importer;
using CineKros.Embedding;

namespace CineKros.Api.Tests.RealFlow;

[TestClass]
public sealed class SrP5RuntimeIntegrationTests
{
    [TestMethod]
    public async Task FixtureParserAndSharedModelReadOnlySearchBothSelectedPocLanguages()
    {
        var connectionString = Environment.GetEnvironmentVariable("CINEKROS_SR_POC_CONNECTION");
        var modelDirectory = Environment.GetEnvironmentVariable("CINEKROS_E5_MODEL_DIR");
        if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(modelDirectory))
            Assert.Inconclusive("Set the retained POC connection and canonical pinned multilingual model directory.");

        using var model = new E5EmbeddingModel(modelDirectory, EmbeddingProfileDescriptor.MultilingualE5Base);
        Assert.AreEqual(MultilingualPocCatalog.ProfileFingerprint, model.ProfileFingerprint);
        await using var dataSource = MovieSearchRepository.CreateDataSource(connectionString);
        var repository = await MovieSearchRepository.CreatePocRepositoryAsync(dataSource);
        var parser = new FixedFixtureParser();
        var service = new RealRecommendationService(parser, new RealParsedQueryValidator(), new E5QueryEmbeddingAdapter(model),
            new MovieSearchAdapter(repository), languageAwarePoc: true);

        var english = await service.RecommendAsync(
            new RealRecommendationRequest(new ParserInput("en", "quiet mystery"), SearchLanguage.English), CancellationToken.None);
        var serbianCyrillic = await service.RecommendAsync(
            new RealRecommendationRequest(new ParserInput("sr", "мирна мистерија"), SearchLanguage.Serbian), CancellationToken.None);

        Assert.AreEqual("quiet mystery", parser.ReceivedMessages[0]);
        Assert.AreEqual("мирна мистерија", parser.ReceivedMessages[1]);
        Assert.IsTrue(english.Movies.Count > 0 && english.Movies.Count <= MovieSearchRepository.ResultLimit);
        Assert.IsTrue(serbianCyrillic.Movies.Count > 0 && serbianCyrillic.Movies.Count <= MovieSearchRepository.ResultLimit);
        Assert.AreEqual(english.Movies.Count, english.Movies.Select(movie => movie.MovieLensId).Distinct().Count());
        Assert.AreEqual(serbianCyrillic.Movies.Count, serbianCyrillic.Movies.Select(movie => movie.MovieLensId).Distinct().Count());
    }

    private sealed class FixedFixtureParser : IRealQueryParser
    {
        public List<string> ReceivedMessages { get; } = [];

        public Task<RealParserResult> ParseAsync(string language, string message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReceivedMessages.Add(message);
            var semantic = language switch
            {
                "en" => "quiet mystery",
                "sr" => "мирна мистерија",
                _ => throw new InvalidOperationException("Fixture parser received an invalid selected language.")
            };
            return Task.FromResult(new RealParserResult("query", new RealParsedQuery(new RealHardFilters(), semantic), LanguageCheck: "match"));
        }
    }
}
