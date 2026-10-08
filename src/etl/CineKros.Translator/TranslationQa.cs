using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CineKros.TextNormalization;

namespace CineKros.Translator;

public static partial class TranslationQa
{
    public const string WhitelistVersion = "translation-invariant-whitelist-v1";
    public const string AmbiguityVersion = "translation-ambiguity-seeds-v1";

    public static readonly IReadOnlySet<string> InvariantWhitelist = new HashSet<string>(
        ["cgi", "imax", "pixar", "disney", "3d", "2d", "dvd", "vhs", "noir", "imdb"], StringComparer.Ordinal);
    public static readonly IReadOnlySet<string> AmbiguitySeeds = new HashSet<string>(
        ["camp", "gritty", "quirky", "twist", "feel-good", "mind-bending"], StringComparer.Ordinal);

    public static QaReport Analyze(TranslationDictionary dictionary)
    {
        var results = dictionary.Entries.OrderBy(entry => entry.En, StringComparer.Ordinal)
            .Select(entry => (Entry: entry, Flags: Evaluate(entry))).ToArray();
        var colliding = results.GroupBy(item => SerbianLatinNormalizer.Normalize(item.Entry.Sr).ToUpperInvariant(), StringComparer.Ordinal)
            .Where(group => group.Select(item => item.Entry.En).Distinct(StringComparer.Ordinal).Count() > 1)
            .SelectMany(group => group.Select(item => item.Entry.En)).ToHashSet(StringComparer.Ordinal);

        var reportEntries = results.Select(item =>
        {
            var flags = item.Flags.ToList();
            if (colliding.Contains(item.Entry.En))
                flags.Add(new QaFlag("sr_collision", "another English key has the same normalized Serbian text"));
            return new QaEntry(item.Entry.En, item.Entry.Machine, item.Entry.Sr, flags);
        }).ToArray();
        var stablePayload = JsonSerializer.SerializeToUtf8Bytes(reportEntries);
        var reportHash = Convert.ToHexStringLower(SHA256.HashData(stablePayload));
        return new QaReport(TranslatorConstants.QaVersion, dictionary.SourceTagsSha256, reportEntries, reportHash);
    }

    public static TranslationDictionary MarkReviewStatuses(TranslationDictionary dictionary)
    {
        var flagged = Analyze(dictionary).Entries.Where(entry => entry.Flags.Count != 0)
            .Select(entry => entry.En).ToHashSet(StringComparer.Ordinal);
        var entries = dictionary.Entries.Select(entry =>
        {
            if (entry.ReviewStatus == "reviewed") return entry;
            if (flagged.Contains(entry.En)) return entry with { ReviewStatus = "review_required" };
            return entry.ReviewStatus == "review_required" ? entry with { ReviewStatus = "auto_pass" } : entry;
        }).ToArray();
        return dictionary with { Entries = entries };
    }

    public static IReadOnlyList<QaFlag> Evaluate(TranslationEntry entry)
    {
        var flags = new List<QaFlag>();
        var raw = entry.Machine ?? string.Empty;
        var normalized = SerbianLatinNormalizer.Normalize(entry.Sr ?? string.Empty);
        if (string.IsNullOrWhiteSpace(raw) || string.IsNullOrWhiteSpace(normalized))
            flags.Add(new QaFlag("empty_or_incomplete", "translation is empty or decoder marked it incomplete"));

        if (!InvariantWhitelist.Contains(entry.En.ToLowerInvariant()) &&
            StringComparer.OrdinalIgnoreCase.Equals(SerbianLatinNormalizer.Normalize(entry.En), normalized))
            flags.Add(new QaFlag("unchanged_english", "translation is unchanged English outside the frozen whitelist"));

        var srLength = normalized.EnumerateRunes().Count();
        var enLength = entry.En.EnumerateRunes().Count();
        if (srLength > 80 || srLength > Math.Max(20, 4 * enLength))
            flags.Add(new QaFlag("excessive_length", "normalized Serbian output is unusually long"));

        if (HasNonLatinScript(normalized))
            flags.Add(new QaFlag("script_residue", "non-Latin script remains after Serbian normalization"));

        if (HasControl(raw) || HasMalformedPunctuation(raw) || ContainsSpecialToken(raw) || HasSuspiciousEncodingResidue(raw))
            flags.Add(new QaFlag("malformed_output", "raw machine output contains controls, special tokens, malformed punctuation, or suspicious encoding residue"));

        if (LostNumbers(entry.En, normalized) || LostNegation(entry.En, normalized))
            flags.Add(new QaFlag("lost_number_or_negation", "output may have lost a numeric token or explicit English negation"));

        var enLower = entry.En.ToLowerInvariant();
        if (AmbiguitySeeds.Any(seed => ContainsBoundedPhrase(enLower, seed)))
            flags.Add(new QaFlag("ambiguity_seed", "English key contains a frozen ambiguity seed"));

        return flags;
    }

    private static bool HasControl(string value) => value.EnumerateRunes().Any(rune =>
    {
        var category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.Control or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned;
    });

    private static bool ContainsSpecialToken(string value)
    {
        var lowered = value.ToLowerInvariant();
        return lowered.Contains("<unk>", StringComparison.Ordinal) || lowered.Contains("</s>", StringComparison.Ordinal) ||
            lowered.Contains("<pad>", StringComparison.Ordinal) || lowered.Contains("<s>", StringComparison.Ordinal) ||
            Regex.IsMatch(value, @"<<[^<>]+>>", RegexOptions.CultureInvariant);
    }

    private static bool HasSuspiciousEncodingResidue(string value) =>
        value.EnumerateRunes().Any(rune => rune.Value is 0x00E6 or 0x00C6 or 0x00E8 or 0x00C8);

    private static bool HasMalformedPunctuation(string value)
    {
        if (RepeatedPunctuationRegex().IsMatch(value))
            return true;
        var stack = new Stack<char>();
        foreach (var character in value)
        {
            if (character is '(' or '[' or '{') stack.Push(character);
            else if (character is ')' or ']' or '}')
            {
                if (stack.Count == 0 || !Matches(stack.Pop(), character)) return true;
            }
        }
        if (stack.Count != 0) return true;
        if (value.Count(character => character == '"') % 2 != 0) return true;
        return value.Count(character => character == '“') != value.Count(character => character == '”') ||
            value.Count(character => character == '«') != value.Count(character => character == '»') ||
            value.Count(character => character == '‘') != value.Count(character => character == '’');
    }

    private static bool Matches(char open, char close) => (open, close) is ('(', ')') or ('[', ']') or ('{', '}');

    private static bool HasNonLatinScript(string value)
    {
        foreach (var rune in value.EnumerateRunes())
        {
            if (!Rune.IsLetter(rune)) continue;
            var scalar = rune.Value;
            if (scalar <= 0x024F || scalar is >= 0x1E00 and <= 0x1EFF || scalar is >= 0xAB30 and <= 0xAB6F)
                continue;
            return true;
        }
        return false;
    }

    private static bool LostNumbers(string en, string sr)
    {
        var numbers = Regex.Matches(sr, @"\d+", RegexOptions.CultureInvariant)
            .Select(match => match.Value).ToHashSet(StringComparer.Ordinal);
        return Regex.Matches(en, @"\d+", RegexOptions.CultureInvariant)
            .Select(match => match.Value).Any(number => !numbers.Contains(number));
    }

    private static bool LostNegation(string en, string sr)
    {
        var hasNegation = Regex.IsMatch(en, @"(?i)(?<![\p{L}])(?:no|not|without|non-)(?=[\p{L}]|\b)", RegexOptions.CultureInvariant);
        if (!hasNegation) return false;
        return !Regex.IsMatch(sr, @"(?i)(?<![\p{L}])(?:ne|bez|ni|anti-)(?=[\p{L}]|\b)", RegexOptions.CultureInvariant);
    }

    private static bool ContainsBoundedPhrase(string text, string phrase) =>
        Regex.IsMatch(text, $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(phrase)}(?![\p{{L}}\p{{N}}])", RegexOptions.CultureInvariant);

    [GeneratedRegex(@"(?:[!?.,;:]\s*){2,}", RegexOptions.CultureInvariant)]
    private static partial Regex RepeatedPunctuationRegex();
}
