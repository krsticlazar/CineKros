using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CineKros.Api.Tests.RealProviders;

[TestClass]
public sealed class ParserV5PromptContractTests
{
    [TestMethod]
    public void PromptDefinesThirdLanguageAndSharedGenreClassificationExplicitly()
    {
        var prompt = ReadPrompt();
        StringAssert.Contains(prompt, "any language other than the selected language, including languages other than English and Serbian");
        StringAssert.Contains(prompt, "an Italian message is `mismatch` in either EN or SR mode");
        StringAssert.Contains(prompt, "the genre-only input `drama` is `match` in both modes");
        StringAssert.Contains(prompt, "A clearly Italian message in either EN or SR mode");
        StringAssert.Contains(prompt, "Genre-only `drama` in either mode");
    }

    [TestMethod]
    public void PromptKeepsNeutralProperNamesNoLookupAndV4ParserSemantics()
    {
        var prompt = ReadPrompt();
        StringAssert.Contains(prompt, "`Dr. Strangelove`");
        StringAssert.Contains(prompt, "`Benedict Cumberbatch`");
        StringAssert.Contains(prompt, "`Tražim nešto kao Dr. Strangelove sa Benedictom Cumberbatchem`");
        StringAssert.Contains(prompt, "make no external API/provider calls and do no web, database, or other lookup");
        Assert.IsFalse(prompt.Contains("Do not use external knowledge to detect language", StringComparison.Ordinal));

        StringAssert.Contains(prompt, "positive actor/director mentions are semantic-only");
        StringAssert.Contains(prompt, "Exclusions/negations (including excluding a genre or person)");
        StringAssert.Contains(prompt, "Strict phrases such as “greater than”, “more than”, “>”, “veća od”, and “više od” map to `gt`");
        StringAssert.Contains(prompt, "“Film rated greater than 4/5” has rating `{status: present, value: 4, operator: gt, scale: five}`");
        StringAssert.Contains(prompt, "keep names/title intact and semantic text Serbian");
    }

    [TestMethod]
    public void PromptDefinesUnsupportedExclusionEnvelopeAndLanguagePrecedence()
    {
        var prompt = ReadPrompt();
        StringAssert.Contains(prompt, "Language classification always comes first");
        StringAssert.Contains(prompt, "For `mismatch` or `unclear`, return the language alert immediately");
        StringAssert.Contains(prompt, "Any such mandatory entity exclusion is unsupported.");
        StringAssert.Contains(prompt, "{\"type\":\"alert\",\"languageCheck\":\"match\",\"query\":null,\"alertCode\":\"UNSUPPORTED_REQUEST\"}");
        StringAssert.Contains(prompt, "Do not include a checklist, `semanticQuery`, or other query fields in any alert");
        StringAssert.Contains(prompt, "Only for a matched supported movie request, return `type: query` and `alertCode: null`");
        StringAssert.Contains(prompt, "For a matched supported movie query, use `type: query`, `languageCheck: match`, and `alertCode: null`");
        StringAssert.Contains(prompt, "On the query branch, classify all five categories");
        StringAssert.Contains(prompt, "`no horror` is a mandatory genre exclusion and is unsupported");
        StringAssert.Contains(prompt, "Actor, director, title, named-entity, and genre exclusions use the matched root `UNSUPPORTED_REQUEST` alert above");

        foreach (var example in new[]
        {
            "film without Brad Pitt", "not directed by Christopher Nolan", "anything except Dr. Strangelove", "no horror",
            "Filmovi bez Brada Pitta", "Ne želim filmove koje je režirao Christopher Nolan", "Nešto osim filma Dr. Strangelove", "Bez horora",
            "Филмови без Бреда Пита", "Не желим филмове које је режирао Кристофер Нолан", "Нешто осим филма Dr. Strangelove", "Без хорора",
            "films starring Brad Pitt", "directed by Christopher Nolan", "like Dr. Strangelove", "Comedy movies",
            "Film sa Brad Pittom", "filmovi Christophera Nolana", "nešto kao Dr. Strangelove", "komedije",
            "Филм са Бредом Питом", "филмови Кристофера Нолана", "нешто као Dr. Strangelove", "комедије",
            "Comedy films from 2000 through 2009 without Brad Pitt", "Vorrei un film senza Brad Pitt", "Želim un film without Brad Pitt"
        }) StringAssert.Contains(prompt, $"`{example}`");
    }

    private static string ReadPrompt()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src/backend/CineKros.Api/RealProviders/query-parser-v5.md");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        Assert.Fail("Could not locate the v5 parser prompt.");
        throw new InvalidOperationException("Unreachable");
    }
}
