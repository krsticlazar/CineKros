using System.Text;

namespace CineKros.Catalog.Importer;

public sealed record CastParsingEvent(long MovieLensId, string Kind, string Raw, string Accepted, string Reason)
{
    public string Proposal => Accepted;
}
public sealed record CastParsingResult(string[]? Cast, IReadOnlyList<CastParsingEvent> Events);

/// <summary>Literal source grammar; deliberately not a general person-name resolver.</summary>
public static class CastNameParser
{
    public const string RulesVersion = "cast-source-parser-v1";
    public static CastParsingResult Parse(long movieLensId, string? raw)
    {
        if (raw is null) return new(null, []);
        var names = new List<string>(); var events = new List<CastParsingEvent>();
        var parts = raw.Split(',');
        for (var index = 0; index < parts.Length; index++)
        {
            var part = parts[index];
            var name = part.Trim().Normalize(NormalizationForm.FormC);
            if (name.Length == 0)
            {
                if (index == parts.Length - 1 && index > 0 && names.Count > 0 && parts[index - 1].Trim().Length > 0)
                {
                    events.Add(new(movieLensId, "trailing-empty-separator", raw, string.Join(", ", names), "Reviewed single final empty comma token; no actor inferred."));
                    continue;
                }
                if (movieLensId == 3396 && index > 0 && index < parts.Length - 1 &&
                    names.Count > 0 && names[^1] == "Caroll Spinney" && parts[index - 1].Trim() == "Caroll Spinney" &&
                    parts[index + 1].Trim() == "Bob Baker" && !events.Any(x => x.Kind == "reviewed-empty-separator-override"))
                {
                    events.Add(new(movieLensId, "reviewed-empty-separator-override", parts[index - 1] + "," + part + "," + parts[index + 1], "Caroll Spinney, Bob Baker", "Reviewed ID3396 single empty boundary between exact source neighbors; no actor inferred."));
                    continue;
                }
                throw new InvalidDataException($"Invalid cast token for movie {movieLensId}.");
            }
            if (name.Any(char.IsControl)) throw new InvalidDataException($"Invalid cast token for movie {movieLensId}.");
            if (name == "Jr.")
            {
                if (names.Count == 0 || names[^1].EndsWith(", Jr.", StringComparison.Ordinal))
                    throw new InvalidDataException($"Ambiguous suffix for movie {movieLensId}.");
                var previous = names[^1]; names[^1] += ", Jr.";
                events.Add(new(movieLensId, "suffix-attachment", previous + ", Jr.", names[^1], "Exact standalone Jr. attaches to preceding source name."));
                continue;
            }
            if (movieLensId == 85179 && name == "Ayumu Sait&#333;")
            {
                events.Add(new(movieLensId, "reviewed-entity-override", name, "Ayumu Saitō", "Reviewed source numeric entity decoding; not a person alias."));
                name = "Ayumu Saitō";
            }
            if (name.Contains('&') || name.Contains(';') || name is "Sr." or "II" or "III" or "IV")
                throw new InvalidDataException($"Unreviewed cast grammar for movie {movieLensId}.");
            names.Add(name);
        }
        var distinct = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names)
            if (seen.Add(name)) distinct.Add(name);
            else events.Add(new(movieLensId, "exact-duplicate-suppression", name, name, "Retained first ordinal exact occurrence."));
        return new(distinct.ToArray(), events);
    }
}
