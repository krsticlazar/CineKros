using System.Text.RegularExpressions;
using CineKros.Api.Search;
using CineKros.TextNormalization;

namespace CineKros.Api.RealProviders;

/// <summary>A conservative grammar safeguard, not a general language classifier.</summary>
public static class HighConfidenceQueryLanguageGuard
{
    private const string SerbianRequests = "želim|zelim|hoću|hocu|tražim|trazim|prikaži|prikazi|pronađi|pronadji|daj";
    private static readonly Regex EnglishRequest = Pattern(@"^(?:please\s+)?(?:(?:show|find)(?:\s+me)?|give\s+me|recommend(?:\s+me)?|i\s+(?:want|would\s+like|am\s+looking\s+for))\b.*\b(?:movies?|films?)\b");
    private static readonly Regex EnglishRelationship = Pattern(@"^(?:movies|films)\s+(?:starring|featuring|with|without|about|directed\s+by|released|from|after|before)\b\s+.*[\p{L}\p{N}]");
    private static readonly Regex SerbianRequest = Pattern(@"^(?:molim(?:\s+(?:te|vas))?\s+)?(?:" + SerbianRequests + @")\b.*\b(?:film|filmovi|filmove|filma|priča|priču)\b");
    private static readonly Regex SerbianRelationship = Pattern(@"^(?:film|filmovi|filmove)\s+(?:u\s+(?:kojima|kojem)|sa|s|bez|o|gde|koji|koje)\b\s+.*[\p{L}\p{N}]");
    private static readonly Regex EnglishCues = Pattern(@"\b(?:show|find|give|recommend|want|would|looking|movies|starring|featuring|directed|without|with|about|after|before)\b");
    private static readonly Regex SerbianCues = Pattern(@"\b(?:" + SerbianRequests + @"|filmovi|filmove|kojima|kojem|glumi|glume|bez|sa|koji|kao|najmanje|najviše|najvise|posle|nakon|izvorno)\b");

    public static bool IsMismatch(string message, SearchLanguage selectedLanguage)
    {
        ArgumentNullException.ThrowIfNull(message);
        // Inspection only: never rewrite the original request or provider result.
        var text = SerbianLatinNormalizer.Normalize(MaskBalancedQuotes(message)).ToLowerInvariant();
        return selectedLanguage switch
        {
            SearchLanguage.Serbian => !SerbianCues.IsMatch(text) &&
                (EnglishRequest.IsMatch(text) || EnglishRelationship.IsMatch(text)),
            SearchLanguage.English => !EnglishCues.IsMatch(text) &&
                (SerbianRequest.IsMatch(text) || SerbianRelationship.IsMatch(text)),
            _ => false
        };
    }

    private static Regex Pattern(string expression) => new(expression, RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static string MaskBalancedQuotes(string message)
    {
        var masked = message.ToCharArray();
        for (var start = 0; start < message.Length; start++)
        {
            var close = message[start] switch { '"' => '"', '“' => '”', '«' => '»', '\'' => '\'', '‘' => '’', _ => '\0' };
            if (close == '\0') continue;
            var single = message[start] is '\'' or '‘';
            if (single && start > 0 && char.IsLetterOrDigit(message[start - 1])) continue;
            var end = start + 1;
            for (; end < message.Length; end++)
                if (message[end] == close && (!single || end + 1 == message.Length || !char.IsLetterOrDigit(message[end + 1]))) break;
            if (end == message.Length) continue;
            Array.Fill(masked, ' ', start, end - start + 1);
            start = end;
        }
        return new string(masked);
    }
}
