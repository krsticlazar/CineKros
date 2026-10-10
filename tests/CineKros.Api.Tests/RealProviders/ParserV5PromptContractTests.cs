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
    public void PromptSeparatesUnclearMovieIntentFromClearlyNonMovieRequests()
    {
        var prompt = ReadPrompt();

        StringAssert.Contains(prompt, "distinguish unclear movie intent from a clearly non-movie request");
        StringAssert.Contains(prompt, "do not classify it as `NOT_MOVIE_REQUEST` merely because it is short or underspecified");
        StringAssert.Contains(prompt, "EN `movie`, SR Latin `film`, and SR Cyrillic `филм`");
        StringAssert.Contains(prompt, "each gets `QUERY_UNCLEAR`");
        StringAssert.Contains(prompt, "A neutral title or person-only input such as `Fight Club` or `Brad Pitt` remains a matched movie-domain query");
        StringAssert.Contains(prompt, "Use `NOT_MOVIE_REQUEST` only when the matched message clearly asks for something outside movie search");
        StringAssert.Contains(prompt, "EN `weather tomorrow` or SR Latin `kakvo je vreme sutra`");

        // This is an offline prompt-contract regression; it does not establish live model classification quality.
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
    public void PromptPreservesSuppliedLatinProperNameSurfaceWithoutChangingInflectedInputHandling()
    {
        var prompt = ReadPrompt();
        StringAssert.Contains(prompt, "When the user's message provides a recognizable Latin-script actor, director, or title spelling in its base form, preserve that spelling exactly in `semanticQuery`");
        StringAssert.Contains(prompt, "do not add Serbian case endings to it");
        StringAssert.Contains(prompt, "including when the surrounding Serbian sentence is in Cyrillic");
        StringAssert.Contains(prompt, "A Serbian-inflected or possessive name supplied by the user remains a valid input");
        StringAssert.Contains(prompt, "this rule does not change or reject such input, which continues through the existing interpretation behavior");
        StringAssert.Contains(prompt, "Do not add external name lookups or a local/backend name-canonicalization step");
        Assert.IsFalse(prompt.Contains("must not be rejected or rewritten", StringComparison.Ordinal));
    }

    [TestMethod]
    public void PromptRequiresSelectedLanguageForMeaningWordsWhileKeepingNamesAndTitlesAsSpans()
    {
        var prompt = ReadPrompt();
        StringAssert.Contains(prompt, "HIGH-PRIORITY SEMANTIC LANGUAGE RULE");
        StringAssert.Contains(prompt, "write every natural-language word in a non-null `semanticQuery` in the selected language");
        StringAssert.Contains(prompt, "prefer Serbian Latin script, while Serbian Cyrillic is also valid");
        StringAssert.Contains(prompt, "A copied proper-name or title span does not make surrounding English wording valid Serbian");
        StringAssert.Contains(prompt, "do not copy English role phrases such as `directed by` or `starring` into Serbian semantic text");
        StringAssert.Contains(prompt, "English examples elsewhere in this prompt describe EN-mode behavior only");
        StringAssert.Contains(prompt, "SR `Nežna priča o pronađenoj porodici, poput The Quiet Harbor`");
        StringAssert.Contains(prompt, "`nežna priča o pronađenoj porodici, nalik naslovu The Quiet Harbor`");
        StringAssert.Contains(prompt, "EN `a film starring Tilda Swinton` → `a film starring Tilda Swinton`");
        StringAssert.Contains(prompt, "SR `film u kojem glumi glumica Tilda Swinton` → `film u kojem glumi Tilda Swinton`");
        StringAssert.Contains(prompt, "EN `a quiet film directed by Denis Villeneuve` → `a quiet film directed by Denis Villeneuve`");
        StringAssert.Contains(prompt, "SR `tih film čiji je reditelj Denis Villeneuve` → `tih film koji je režirao Denis Villeneuve`");
        StringAssert.Contains(prompt, "These names are generic development examples, not special cases");
        StringAssert.Contains(prompt, "SR `Nešto napeto, sa pričom o osveti`");
        StringAssert.Contains(prompt, "If the user's input supplies an inflected name, accept it without rejecting it; when its original base spelling is confidently recognizable from the input, use that original spelling with Serbian surrounding grammar");
        StringAssert.Contains(prompt, "when it is not confidently recognizable, do not invent or corrupt an identity");
        StringAssert.Contains(prompt, "Do not add external name lookups or backend entity canonicalization");
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
