using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CineKros.Api.Tests.RealProviders;

[TestClass]
public sealed class ParserV6PromptContractTests
{
    [TestMethod]
    public void SchemaAddsExactlyRequiredActorChecklistToLanguageAwareEnvelope()
    {
        using var v5 = JsonDocument.Parse(ReadAsset("query-parser-v5.schema.json"));
        using var v6 = JsonDocument.Parse(ReadAsset("query-parser-v6.schema.json"));
        var query = v6.RootElement.GetProperty("properties").GetProperty("query");
        CollectionAssert.AreEquivalent(new[] { "year", "runtime", "genres", "rating", "originalLanguage", "actors", "semanticQuery" }, query.GetProperty("required").EnumerateArray().Select(v => v.GetString()!).ToArray());
        var actors = query.GetProperty("properties").GetProperty("actors");
        CollectionAssert.AreEquivalent(new[] { "status", "all", "any", "exclude" }, actors.GetProperty("required").EnumerateArray().Select(v => v.GetString()!).ToArray());
        foreach (var name in new[] { "year", "runtime", "genres", "rating", "originalLanguage", "semanticQuery" })
            Assert.IsTrue(JsonElement.DeepEquals(v5.RootElement.GetProperty("properties").GetProperty("query").GetProperty("properties").GetProperty(name), query.GetProperty("properties").GetProperty(name)), name);
        foreach (var name in new[] { "type", "languageCheck", "alertCode" })
            Assert.IsTrue(JsonElement.DeepEquals(v5.RootElement.GetProperty("properties").GetProperty(name), v6.RootElement.GetProperty("properties").GetProperty(name)), name);
    }

    [TestMethod]
    public void PromptCoherentlySeparatesActorHardFiltersFromDirectorSemanticIntent()
    {
        var prompt = ReadAsset("query-parser-v6.md");
        foreach (var required in new[]
        {
            "Language classification always comes first", "Serbian Latin and Cyrillic are equally valid", "Actor inclusion/exclusion belongs only in `actors`",
            "Explicit positive director intent remains semantic", "Negative directors remain unsupported", "actor-only and actor plus other hard filters use semanticQuery=null",
            "NFC+trim+case-insensitive lookup only when unique", "original international base spelling", "partial ANY/exclude overlap is valid",
            "mandatory negative director, title, genre, country, or other entity constraints remain unsupported", "Do not strip diacritics", "original numeric threshold",
            "every natural-language word in a non-null semanticQuery in the selected language", "Backend membership cannot establish that you interpreted the intended person correctly"
        }) StringAssert.Contains(prompt, required);
        foreach (var obsolete in new[] { "positive actor/director mentions are semantic-only", "Any such mandatory entity exclusion is unsupported.", "Only the five categories are hard filters", "Do not add external name lookups or backend entity canonicalization" })
            Assert.IsFalse(prompt.Contains(obsolete, StringComparison.Ordinal), obsolete);
    }

    internal static string ReadAsset(string file)
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "src/backend/CineKros.Api/RealProviders", file);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException(file);
    }
}
