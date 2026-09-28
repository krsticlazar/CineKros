using System.Text.Json;
using CineKros.Api.RealProviders;
using CineKros.Evaluation;

namespace CineKros.Evaluation.Tests;

[TestClass]
public sealed class EvaluationLogicTests
{
    [TestMethod]
    public void FixtureLoadsAndModeSelectionMatchesCanonicalFilters()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "query-set.json");
        var set = QuerySetReader.Read(path);
        Assert.AreEqual("Q001", set.Cases[0].Id);
        Assert.AreEqual(3, set.Cases[0].ApplicableModes.Count);
        Assert.IsTrue(QuerySetReader.Applicable("hybrid", new RealHardFilters(YearMin: 2000), "quiet drama"));
        Assert.IsTrue(QuerySetReader.Applicable("structured", new RealHardFilters(YearMin: 2000), null));
        Assert.IsTrue(QuerySetReader.Applicable("semantic", new RealHardFilters(), "quiet drama"));
        Assert.IsTrue(QuerySetReader.Applicable("semantic", new RealHardFilters(YearMin: 2000), "quiet drama"));
        Assert.IsTrue(QuerySetReader.Applicable("hybrid", new RealHardFilters(YearMin: 2000), "quiet drama"));
        Assert.IsFalse(QuerySetReader.Applicable("semantic", new RealHardFilters(YearMin: 2000), ""));
    }

    [TestMethod]
    public void ComplianceChecksInclusiveBoundsAllAnyNullAndStrictRating()
    {
        var filters = new RealHardFilters(2000, 2000, 90, 100,
            new RealGenreFilter(["Sci-Fi", "Drama"], ["Thriller", "Comedy"]), 4m, "en", "gt");
        var compliant = new MovieMetadata(1, 2000, 100, ["Sci-Fi", "Drama", "Comedy"], 4.01m, "en");
        var equalRatingFails = compliant with { MovieLensId = 2, AverageRating = 4m };
        var missingGenreFails = compliant with { MovieLensId = 3, Genres = null };
        var result = FilterCompliance.Evaluate(filters, [compliant, equalRatingFails, missingGenreFails]);
        Assert.AreEqual(2, result.ViolationCount);
        Assert.IsTrue(Math.Abs(100d / 3d - result.CompliancePercent!.Value) < 0.0001);
        Assert.IsTrue(FilterCompliance.Matches(filters, compliant));
        Assert.IsFalse(FilterCompliance.Matches(filters, equalRatingFails));
        Assert.IsNull(FilterCompliance.Evaluate(filters, []).CompliancePercent);
    }

    [TestMethod]
    public void AnyGenreAndNullActiveValuesFailAndInclusiveRatingPasses()
    {
        var filters = new RealHardFilters(YearMin: 2000, Genres: new RealGenreFilter(Any: ["Drama", "Comedy"]), RatingMin: 4m, RatingOperator: "gte");
        Assert.IsTrue(FilterCompliance.Matches(filters, new MovieMetadata(1, 2000, null, ["Drama"], 4m, null)));
        Assert.IsFalse(FilterCompliance.Matches(filters, new MovieMetadata(2, null, null, ["Drama"], 4.5m, null)));
        Assert.IsFalse(FilterCompliance.Matches(filters, new MovieMetadata(3, 2000, null, ["Action"], 4.5m, null)));
        Assert.IsFalse(FilterCompliance.Matches(filters, new MovieMetadata(4, 2000, null, ["Drama"], null, null)));
    }

    [TestMethod]
    public void HumanScoresValidateRangeAndCalculateProposedMetrics()
    {
        using var document = JsonDocument.Parse("""{"Q001":{"1":3,"2":1}}""");
        var judgments = HumanScoring.ReadJudgments(document.RootElement);
        Assert.AreEqual(3, judgments["Q001"][1]);
        var metrics = HumanScoring.Calculate([new MovieResult(1, "a", 2000, 3), new MovieResult(2, "b", 2001, 1)]);
        Assert.AreEqual(2d, metrics.MeanRelevance);
        Assert.AreEqual(0.5d, metrics.PrecisionAt10);
        Assert.IsTrue(metrics.NdcgAt10 is > 0 and <= 1);
        var pooled = HumanScoring.Calculate([new MovieResult(1, "a", 2000, 1), new MovieResult(2, "b", 2001, 0)], new Dictionary<long, int> { [1] = 1, [2] = 0, [3] = 3 });
        var returnedOnly = HumanScoring.Calculate([new MovieResult(1, "a", 2000, 1), new MovieResult(2, "b", 2001, 0)]);
        Assert.IsTrue(pooled.NdcgAt10 < returnedOnly.NdcgAt10);
        var tenRetrieved = Enumerable.Range(1, 10).Select(i => new MovieResult(i, $"m{i}", 2000, 1)).ToArray();
        var largePool = Enumerable.Range(1, 12).ToDictionary(i => (long)i, _ => 1);
        Assert.AreEqual(1d, HumanScoring.Calculate(tenRetrieved, largePool).NdcgAt10);
        Assert.IsNull(HumanScoring.Calculate([new MovieResult(1, "a", 2000, null)]).NdcgAt10);
        using var invalid = JsonDocument.Parse("""{"Q001":{"1":4}}""");
        var threw = false;
        try { _ = HumanScoring.ReadJudgments(invalid.RootElement); } catch (InvalidDataException) { threw = true; }
        Assert.IsTrue(threw);
    }

    [TestMethod]
    public void ParserComparisonScoresChecklistHardFieldsAndLiteralTextSeparately()
    {
        using var expected = JsonDocument.Parse("""
            {"providerChecklist":{"type":"query","year":{"status":"present","min":2000,"max":null},"semanticQuery":"dark mystery"},"canonicalDto":{"alertCode":null,"query":{"hardFilters":{"yearMin":2000},"semanticQuery":"different wording"}}}
            """);
        var actual = new RealParserResult("query", new RealParsedQuery(new RealHardFilters(YearMin: 2000), "dark mystery"));
        var result = ParserScoring.Compare("Q1", expected.RootElement,
            "{\"type\":\"query\",\"year\":{\"status\":\"present\",\"min\":2000,\"max\":null},\"semanticQuery\":\"actual paraphrase\"}", actual with { Query = actual.Query! with { SemanticQuery = "actual paraphrase" } }, 12.5);
        Assert.IsTrue(result.ProviderChecklistMatch);
        Assert.IsTrue(result.CanonicalDtoMatch);
        Assert.IsFalse(result.SemanticTextLiteralMatch);
        Assert.AreEqual(12.5, result.ProviderDurationMs);
    }

    [TestMethod]
    public void ReportSerializationCarriesDurationsNoResultAndNullableCompliance()
    {
        var report = new EvaluationReport("phase-d-proposed-v1", "retrieval", DateTimeOffset.UnixEpoch, "catalog-v", "sha", "fingerprint", "e5-profile", ["Q1"],
            [new RetrievalResult("Q1", "structured", new RetrievalQuery(new RealHardFilters(YearMin: 9999), null), new RealHardFilters(YearMin: 9999), new RealHardFilters(YearMin: 9999), 0, false, [], null, 1, 1, 0, null, true, null, null, null)], []);
        var json = JsonSerializer.Serialize(report, EvaluationJson.Options);
        StringAssert.Contains(json, "searchDurationMs");
        StringAssert.Contains(json, "expectedNoResultsCorrect");
        StringAssert.Contains(json, "compliancePercent");
        using var doc = JsonDocument.Parse(json);
        Assert.AreEqual(JsonValueKind.Null, doc.RootElement.GetProperty("retrievalResults")[0].GetProperty("compliancePercent").ValueKind);
    }

    [TestMethod]
    public void SemanticBaselineComplianceUsesOriginalReferenceFilters()
    {
        var reference = new RealHardFilters(YearMin: 2000);
        var applied = new RealHardFilters();
        var baselineMovie = new MovieMetadata(1, 1999, null, null, null, null);
        Assert.IsFalse(FilterCompliance.Matches(reference, baselineMovie));
        Assert.IsTrue(FilterCompliance.Matches(applied, baselineMovie));
        Assert.AreEqual(1, FilterCompliance.Evaluate(reference, [baselineMovie]).ViolationCount);
    }
}
