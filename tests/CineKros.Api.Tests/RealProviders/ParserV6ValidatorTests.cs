using System.Text.Json;
using CineKros.Api.RealProviders;
using CineKros.Api.Search;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CineKros.Api.Tests.RealProviders;

[TestClass]
public sealed class ParserV6ValidatorTests
{
    private readonly RealParsedQueryValidator validator = new(new ActorNameResolver(["Brad Pitt", "George Clooney", "Robert Downey Jr.", "Penélope Cruz", "JOSE", "Jose"]));

    [TestMethod]
    [DataRow("en", "Brad Pitt")]
    [DataRow("sr", "brad pitt")]
    [DataRow("sr", " Brad Pitt ")]
    public void CanonicalizesProviderProposalsAndKeepsActorOnlySemanticNull(string language, string proposal)
    {
        var selected = language == "en" ? SearchLanguage.English : SearchLanguage.Serbian;
        var result = validator.ValidateV6(Query([proposal]), selected, out var evidence);
        Assert.AreEqual("Brad Pitt", result.Query!.HardFilters.Actors!.All![0]);
        Assert.IsNull(result.Query.HardFilters.Actors.Any);
        Assert.IsNull(result.Query.HardFilters.Actors.Exclude);
        Assert.IsNull(result.Query.SemanticQuery);
        Assert.IsTrue(RealParsedQueryValidator.HasActiveFilter(result.Query.HardFilters));
        validator.ValidateResultV6(result, selected);
        using var doc = JsonDocument.Parse(evidence);
        Assert.AreEqual(proposal, doc.RootElement.GetProperty("checklist").GetProperty("actors").GetProperty("all")[0].GetString());
    }

    [TestMethod]
    public void CyrillicAndInflectedInputNeedsModelOriginalNameProposalNotBackendTransliteration()
    {
        // Input interpretation is covered by fake adapter fixtures, not claimed as live model proof.
        foreach (var proposal in new[] { "Бред Пит", "Brada Pitta", "Unknown Actor", "jose", "Penelope Cruz" })
        {
            var result = validator.ValidateV6(Query([proposal]), SearchLanguage.Serbian);
            Assert.AreEqual("QUERY_UNCLEAR", result.AlertCode, proposal);
            Assert.AreEqual("match", result.LanguageCheck);
            Assert.IsNull(result.Query);
        }
        var exact = validator.ValidateV6(Query(["Robert Downey Jr.", "Pene\u0301lope Cruz"]), SearchLanguage.Serbian);
        CollectionAssert.AreEqual(new[] { "Robert Downey Jr.", "Penélope Cruz" }, exact.Query!.HardFilters.Actors!.All!.ToArray());
    }

    [TestMethod]
    public void ContradictionsAreUnclearButPartialAnyAndAllAnyOverlapRemainIntact()
    {
        foreach (var json in new[]
        {
            Query(["Brad Pitt"], exclude: ["brad pitt"]),
            Query(any: ["Brad Pitt", "George Clooney"], exclude: ["Brad Pitt", "George Clooney"]),
            Query(exclude: ["Unknown Actor"])
        }) Assert.AreEqual("QUERY_UNCLEAR", validator.ValidateV6(json, SearchLanguage.English).AlertCode);

        var result = validator.ValidateV6(Query(["Brad Pitt"], ["Brad Pitt", "George Clooney"], ["George Clooney"], "quiet mystery"), SearchLanguage.English);
        var actors = result.Query!.HardFilters.Actors!;
        CollectionAssert.AreEqual(new[] { "Brad Pitt", "George Clooney" }, actors.Any!.ToArray());
        Assert.AreEqual("George Clooney", actors.Exclude![0]);
        Assert.AreEqual("quiet mystery", result.Query.SemanticQuery);
        validator.ValidateResultV6(result, SearchLanguage.English);
    }

    [TestMethod]
    public void RejectsMalformedActorListsStatusesKeysAndDuplicateCanonicalMembers()
    {
        var valid = Query(["Brad Pitt"]);
        foreach (var invalid in new[]
        {
            valid.Replace("\"actors\":", "\"extra\":null,\"actors\":"),
            valid.Replace("\"all\":[\"Brad Pitt\"]", "\"all\":[\"Brad Pitt\"],\"all\":[]"),
            valid.Replace("\"all\":[\"Brad Pitt\"]", "\"all\":[null]"),
            valid.Replace("\"all\":[\"Brad Pitt\"]", "\"all\":[\"\"]"),
            valid.Replace("\"all\":[\"Brad Pitt\"]", "\"all\":[\"Brad Pitt\",\"Brad Pitt\"]"),
            valid.Replace("\"all\":[\"Brad Pitt\"]", "\"all\":[\"Brad Pitt\",\"brad pitt\"]"),
            valid.Replace("\"status\":\"present\"", "\"status\":\"absent\""),
            valid.Replace("\"status\":\"present\"", "\"status\":\"unsupported\""),
            valid.Replace("\"status\":\"present\"", "\"status\":\"other\""),
            valid.Replace("\"all\":[\"Brad Pitt\"]", "\"all\":[]"),
            valid.Replace("\"exclude\":[]", "\"exclude\":null"),
            valid.Replace("\"exclude\":[]", "\"exclude\":[],\"nested\":{}"),
            valid.Replace("\"exclude\":[]", "\"exclude\":[[\"Brad Pitt\"]]"),
            valid.Replace("\"exclude\":[]", "\"exclude\":[\"Brad\\nPitt\"]")
        }) Invalid(() => validator.ValidateV6(invalid, SearchLanguage.English));

        using var document = JsonDocument.Parse(valid);
        var actorJson = document.RootElement.GetProperty("query").GetProperty("actors").GetRawText();
        Invalid(() => validator.ValidateV6(valid.Replace("\"actors\":" + actorJson + ",", ""), SearchLanguage.English));
    }

    [TestMethod]
    public void TypedCanonicalBoundaryRejectsNoncanonicalAndLegacyActorInjection()
    {
        var valid = new RealParserResult("query", new RealParsedQuery(new RealHardFilters(Actors: new RealActorFilter(All: ["Brad Pitt"])), null), LanguageCheck: "match");
        validator.ValidateResultV6(valid, SearchLanguage.English);
        Invalid(() => validator.ValidateResult(valid));
        Invalid(() => validator.ValidateResultV5(valid, SearchLanguage.English));
        Invalid(() => validator.ValidateV5(Query(["Brad Pitt"]), SearchLanguage.English));
        var legacy = Query(["Brad Pitt"]).Replace("\"languageCheck\":\"match\",", "");
        Invalid(() => validator.Validate(legacy));
        foreach (var actors in new[]
        {
            new RealActorFilter(All: ["brad pitt"]), new RealActorFilter(All: ["Unknown"]),
            new RealActorFilter(All: []), new RealActorFilter(), new RealActorFilter(All: ["Brad Pitt", "Brad Pitt"]),
            new RealActorFilter(All: ["Brad Pitt"], Exclude: ["Brad Pitt"]), new RealActorFilter(All: [" Brad Pitt"])
        }) Invalid(() => validator.ValidateResultV6(valid with { Query = new RealParsedQuery(new RealHardFilters(Actors: actors), null) }, SearchLanguage.English));
    }

    [TestMethod]
    public void MissingRegistryIsInfrastructureFailureWhileAlertsNeedNoRegistry()
    {
        var empty = new RealParsedQueryValidator();
        Assert.AreEqual("SEARCH_UNAVAILABLE", Assert.ThrowsExactly<RealProviderException>(() => empty.ValidateV6(Query(["Brad Pitt"]), SearchLanguage.English)).Code);
        var result = new RealParserResult("query", new RealParsedQuery(new RealHardFilters(Actors: new RealActorFilter(All: ["Brad Pitt"])), null), LanguageCheck: "match");
        Assert.AreEqual("SEARCH_UNAVAILABLE", Assert.ThrowsExactly<RealProviderException>(() => empty.ValidateResultV6(result, SearchLanguage.English)).Code);
        Assert.AreEqual("LANGUAGE_MISMATCH", empty.ValidateV6(Alert("mismatch", "LANGUAGE_MISMATCH"), SearchLanguage.Serbian).AlertCode);
    }

    [TestMethod]
    public void PreservesLanguageAlertPrecedenceAndStrictEnvelopes()
    {
        foreach (var (languageCheck, code) in new[] { ("mismatch", "LANGUAGE_MISMATCH"), ("unclear", "QUERY_UNCLEAR"), ("match", "UNSUPPORTED_REQUEST"), ("match", "NOT_MOVIE_REQUEST"), ("match", "QUERY_UNCLEAR") })
        {
            var result = validator.ValidateV6(Alert(languageCheck, code), SearchLanguage.Serbian);
            Assert.AreEqual(code, result.AlertCode);
            validator.ValidateResultV6(result, SearchLanguage.Serbian);
        }
        var valid = Query(["Brad Pitt"]);
        foreach (var invalid in new[]
        {
            valid.Replace("\"languageCheck\":\"match\",", ""),
            valid.Replace("\"languageCheck\":\"match\"", "\"languageCheck\":\"mismatch\""),
            valid.Replace("\"languageCheck\":\"match\"", "\"languageCheck\":\"match\",\"languageCheck\":\"match\""),
            valid.Replace("\"alertCode\":null", "\"alertCode\":null,\"extra\":null"),
            Alert("match", "LANGUAGE_MISMATCH"), Alert("unclear", "UNSUPPORTED_REQUEST"), Alert("mismatch", "QUERY_UNCLEAR")
        }) Invalid(() => validator.ValidateV6(invalid, SearchLanguage.English));
        Invalid(() => validator.ValidateV6(valid, (SearchLanguage)42));
    }

    [TestMethod]
    public void KeepsAllOldFiltersRatingNormalizationAndSemanticControlRules()
    {
        var json = Query(["Brad Pitt"], semantic: "tih film")
            .Replace("\"year\":{\"status\":\"absent\",\"min\":null,\"max\":null}", "\"year\":{\"status\":\"present\",\"min\":2000,\"max\":2010}")
            .Replace("\"runtime\":{\"status\":\"absent\",\"min\":null,\"max\":null}", "\"runtime\":{\"status\":\"present\",\"min\":90,\"max\":109}")
            .Replace("\"rating\":{\"status\":\"absent\",\"value\":null,\"operator\":null,\"scale\":null}", "\"rating\":{\"status\":\"present\",\"value\":8,\"operator\":\"gt\",\"scale\":\"ten\"}");
        var result = validator.ValidateV6(json, SearchLanguage.Serbian);
        Assert.AreEqual(4m, result.Query!.HardFilters.RatingMin);
        Assert.AreEqual("gt", result.Query.HardFilters.RatingOperator);
        Assert.AreEqual(2000, result.Query.HardFilters.YearMin);
        Assert.AreEqual(109, result.Query.HardFilters.RuntimeMax);
        foreach (var code in new[] { "QUERY_UNCLEAR", "LANGUAGE_MISMATCH", "NOT_MOVIE_REQUEST", "UNSUPPORTED_REQUEST", "NO_RESULTS", "INVALID_REQUEST", "RATE_LIMITED", "PARSER_INVALID_RESPONSE", "PROVIDER_UNAVAILABLE", "SEARCH_UNAVAILABLE", "INTERNAL_ERROR" })
            Invalid(() => validator.ValidateV6(Query(["Brad Pitt"], semantic: code), SearchLanguage.English));
        Assert.AreEqual("A film about QUERY_UNCLEAR errors", validator.ValidateV6(Query(semantic: "A film about QUERY_UNCLEAR errors"), SearchLanguage.English).Query!.SemanticQuery);
        Assert.AreEqual("UNSUPPORTED_REQUEST", validator.ValidateV6(Query(actorStatus: "unsupported", semantic: "quiet"), SearchLanguage.English).AlertCode);
    }

    internal static string Query(string[]? all = null, string[]? any = null, string[]? exclude = null, string? semantic = null, string? actorStatus = null) => JsonSerializer.Serialize(new
    {
        type = "query", languageCheck = "match",
        query = new
        {
            year = new { status = "absent", min = (int?)null, max = (int?)null },
            runtime = new { status = "absent", min = (int?)null, max = (int?)null },
            genres = new { status = "absent", all = Array.Empty<string>(), any = Array.Empty<string>() },
            rating = new { status = "absent", value = (decimal?)null, @operator = (string?)null, scale = (string?)null },
            originalLanguage = new { status = "absent", value = (string?)null },
            actors = new { status = actorStatus ?? ((all?.Length ?? 0) + (any?.Length ?? 0) + (exclude?.Length ?? 0) > 0 ? "present" : "absent"), all = all ?? [], any = any ?? [], exclude = exclude ?? [] },
            semanticQuery = semantic
        },
        alertCode = (string?)null
    });

    internal static string Alert(string languageCheck, string alertCode) => JsonSerializer.Serialize(new { type = "alert", languageCheck, query = (object?)null, alertCode });
    private static void Invalid(Action action) => Assert.AreEqual("PARSER_INVALID_RESPONSE", Assert.ThrowsExactly<RealProviderException>(action).Code);
}
