using System.Text.Json;
using CineKros.Api.RealProviders;
using CineKros.Api.Search;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CineKros.Api.Tests.RealProviders;

[TestClass]
public sealed class ParserV5ValidatorTests
{
    private readonly RealParsedQueryValidator validator = new();

    [TestMethod]
    public void AcceptsLanguageBranchesAndKeepsCompactEvidence()
    {
        var english = validator.ValidateV5(Query("match", "quiet mystery"), SearchLanguage.English, out var evidence);
        Assert.AreEqual("match", english.LanguageCheck);
        Assert.AreEqual("query", english.Type);
        using (var doc = JsonDocument.Parse(evidence))
        {
            Assert.AreEqual("match", doc.RootElement.GetProperty("languageCheck").GetString());
            Assert.AreEqual("query", doc.RootElement.GetProperty("checklist").GetProperty("type").GetString());
            Assert.IsTrue(doc.RootElement.GetProperty("checklist").TryGetProperty("semanticQuery", out _));
        }

        var serbianCyrillic = validator.ValidateV5(Query("match", "мрачна драма"), SearchLanguage.Serbian);
        Assert.AreEqual("мрачна драма", serbianCyrillic.Query!.SemanticQuery);

        var mismatch = validator.ValidateV5(Alert("mismatch", "LANGUAGE_MISMATCH"), SearchLanguage.Serbian);
        Assert.AreEqual("LANGUAGE_MISMATCH", mismatch.AlertCode);
        Assert.AreEqual("mismatch", mismatch.LanguageCheck);

        var unclear = validator.ValidateV5(Alert("unclear", "QUERY_UNCLEAR"), SearchLanguage.English);
        Assert.AreEqual("QUERY_UNCLEAR", unclear.AlertCode);
        Assert.AreEqual("unclear", unclear.LanguageCheck);

        var unsupported = validator.ValidateV5(UnsupportedGenre(), SearchLanguage.English);
        Assert.AreEqual("UNSUPPORTED_REQUEST", unsupported.AlertCode);
        Assert.AreEqual("match", unsupported.LanguageCheck);
    }

    [TestMethod]
    public void RejectsInvalidEnvelopeAndContradictoryLanguageBranches()
    {
        var valid = Query("match", "quiet mystery");
        foreach (var invalid in new[]
        {
            valid.Replace("\"languageCheck\":\"match\",", "", StringComparison.Ordinal),
            valid.Replace("\"languageCheck\":\"match\"", "\"languageCheck\":\"other\"", StringComparison.Ordinal),
            valid.Replace("\"alertCode\":null", "\"alertCode\":null,\"extra\":true", StringComparison.Ordinal),
            valid.Replace("\"languageCheck\":\"match\"", "\"languageCheck\":\"match\",\"languageCheck\":\"match\"", StringComparison.Ordinal),
            valid.Replace("\"originalLanguage\":", "\"unexpected\":null,\"originalLanguage\":", StringComparison.Ordinal),
            valid.Replace("\"runtime\":{\"status\":\"absent\",\"min\":null,\"max\":null},", "", StringComparison.Ordinal),
            valid.Replace("\"alertCode\":null", "\"alertCode\":\"UNSUPPORTED_REQUEST\"", StringComparison.Ordinal),
            AlertWithQuery("match", "UNSUPPORTED_REQUEST"),
            Alert("mismatch", "QUERY_UNCLEAR"),
            Alert("unclear", "LANGUAGE_MISMATCH"),
            Alert("match", "LANGUAGE_MISMATCH"),
            Query("mismatch", "quiet mystery"),
            "{\"type\":\"query\",\"languageCheck\":\"match\",\"query\":null,\"alertCode\":null}"
        }) AssertInvalid(invalid, SearchLanguage.English);

        AssertInvalid(valid, (SearchLanguage)17);
    }

    [TestMethod]
    public void AcceptsExactMatchedUnsupportedEnvelopeWithoutQuery()
    {
        var result = validator.ValidateV5(Alert("match", "UNSUPPORTED_REQUEST"), SearchLanguage.English);
        Assert.AreEqual("alert", result.Type);
        Assert.AreEqual("match", result.LanguageCheck);
        Assert.IsNull(result.Query);
        Assert.AreEqual("UNSUPPORTED_REQUEST", result.AlertCode);
        validator.ValidateResultV5(result, SearchLanguage.English);

        var notMovie = validator.ValidateV5(Alert("match", "NOT_MOVIE_REQUEST"), SearchLanguage.English);
        Assert.AreEqual("NOT_MOVIE_REQUEST", notMovie.AlertCode);
        Assert.IsNull(notMovie.Query);
        validator.ValidateResultV5(notMovie, SearchLanguage.English);
    }

    [TestMethod]
    public void PreservesHardFiltersRatingAndPositivePersonSemantics()
    {
        var json = """{"type":"query","languageCheck":"match","query":{"year":{"status":"present","min":2016,"max":null},"runtime":{"status":"present","min":null,"max":109},"genres":{"status":"present","all":["Sci-Fi"],"any":[]},"rating":{"status":"present","value":8,"operator":"gte","scale":"ten"},"originalLanguage":{"status":"present","value":"ja"},"semanticQuery":"filmovi sa Brad Pittom"},"alertCode":null}""";
        var result = validator.ValidateV5(json, SearchLanguage.Serbian);
        Assert.AreEqual(2016, result.Query!.HardFilters.YearMin);
        Assert.AreEqual(109, result.Query.HardFilters.RuntimeMax);
        CollectionAssert.AreEqual(new[] { "Sci-Fi" }, result.Query.HardFilters.Genres!.All!.ToArray());
        Assert.AreEqual(4m, result.Query.HardFilters.RatingMin);
        Assert.AreEqual("gte", result.Query.HardFilters.RatingOperator);
        Assert.AreEqual("ja", result.Query.HardFilters.OriginalLanguage);
        Assert.AreEqual("filmovi sa Brad Pittom", result.Query.SemanticQuery);

        validator.ValidateResultV5(result, SearchLanguage.Serbian);
        AssertInvalidTyped(new RealParserResult("alert", AlertCode: "LANGUAGE_MISMATCH", LanguageCheck: "match"), SearchLanguage.Serbian);
        AssertInvalidTyped(new RealParserResult("alert", AlertCode: "NOT_MOVIE_REQUEST", LanguageCheck: "unclear"), SearchLanguage.English);
        validator.ValidateResultV5(new RealParserResult("alert", AlertCode: "LANGUAGE_MISMATCH", LanguageCheck: "mismatch"), SearchLanguage.English);
    }

    private static string Query(string languageCheck, string semantic) =>
        "{\"type\":\"query\",\"languageCheck\":\"" + languageCheck + "\",\"query\":{" +
        "\"year\":{\"status\":\"absent\",\"min\":null,\"max\":null}," +
        "\"runtime\":{\"status\":\"absent\",\"min\":null,\"max\":null}," +
        "\"genres\":{\"status\":\"absent\",\"all\":[],\"any\":[]}," +
        "\"rating\":{\"status\":\"absent\",\"value\":null,\"operator\":null,\"scale\":null}," +
        "\"originalLanguage\":{\"status\":\"absent\",\"value\":null}," +
        "\"semanticQuery\":" + JsonSerializer.Serialize(semantic) + "},\"alertCode\":null}";

    private static string Alert(string languageCheck, string code) =>
        JsonSerializer.Serialize(new { type = "alert", languageCheck, query = (object?)null, alertCode = code });

    private static string AlertWithQuery(string languageCheck, string code)
    {
        using var query = JsonDocument.Parse(Query("match", "quiet mystery"));
        return JsonSerializer.Serialize(new { type = "alert", languageCheck, query = query.RootElement.GetProperty("query"), alertCode = code });
    }

    private static string UnsupportedGenre() =>
        "{\"type\":\"query\",\"languageCheck\":\"match\",\"query\":{" +
        "\"year\":{\"status\":\"absent\",\"min\":null,\"max\":null}," +
        "\"runtime\":{\"status\":\"absent\",\"min\":null,\"max\":null}," +
        "\"genres\":{\"status\":\"unsupported\",\"all\":[],\"any\":[]}," +
        "\"rating\":{\"status\":\"absent\",\"value\":null,\"operator\":null,\"scale\":null}," +
        "\"originalLanguage\":{\"status\":\"absent\",\"value\":null},\"semanticQuery\":\"dark\"},\"alertCode\":null}";

    private void AssertInvalid(string json, SearchLanguage language)
    {
        var error = AssertThrows<RealProviderException>(() => validator.ValidateV5(json, language));
        Assert.AreEqual("PARSER_INVALID_RESPONSE", error.Code);
    }

    private void AssertInvalidTyped(RealParserResult result, SearchLanguage language)
    {
        var error = AssertThrows<RealProviderException>(() => validator.ValidateResultV5(result, language));
        Assert.AreEqual("PARSER_INVALID_RESPONSE", error.Code);
    }

    private static T AssertThrows<T>(Action action) where T : Exception
    {
        try { action(); } catch (T error) { return error; }
        Assert.Fail($"Expected {typeof(T).Name}.");
        throw new InvalidOperationException("Unreachable");
    }
}
