using System.Text.Json;
using CineKros.Api.RealProviders;

namespace CineKros.Evaluation;

public sealed record MovieMetadata(long MovieLensId, int? Year, int? RuntimeMinutes,
    IReadOnlyList<string>? Genres, decimal? AverageRating, string? OriginalLanguage);
public sealed record ComplianceResult(int ViolationCount, double? CompliancePercent);
public sealed record HumanMetrics(double? MeanRelevance, double? PrecisionAt10, double? NdcgAt10);

public static class QuerySetReader
{
    public const string Schema = "phase-d-proposed-v1";
    private static readonly string[] Modes = ["structured", "semantic", "hybrid"];

    public static ProposedQuerySet Read(string path)
    {
        var set = JsonSerializer.Deserialize<ProposedQuerySet>(File.ReadAllText(path), EvaluationJson.Options)
            ?? throw new InvalidDataException("Query set is empty or invalid.");
        if (set.SchemaVersion != Schema || set.Cases.Count == 0)
            throw new InvalidDataException("Unsupported query set schema or empty case list.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in set.Cases)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || !ids.Add(item.Id) || item.Language is not ("sr" or "en") || string.IsNullOrWhiteSpace(item.Query))
                throw new InvalidDataException("Query set contains an invalid or duplicate case.");
            if (item.ParserEval != (item.ParserExpected is not null) || item.RetrievalEval != (item.RetrievalQuery is not null))
                throw new InvalidDataException($"Case {item.Id} evaluation flags and payloads do not agree.");
            if (!item.ParserEval && !item.RetrievalEval && item.ApplicableModes.Count != 0)
                throw new InvalidDataException($"Case {item.Id} has modes without retrieval evaluation.");
            if (item.ApplicableModes.Distinct(StringComparer.Ordinal).Count() != item.ApplicableModes.Count || item.ApplicableModes.Any(m => !Modes.Contains(m, StringComparer.Ordinal)))
                throw new InvalidDataException($"Case {item.Id} contains an unknown or duplicate mode.");
            if (item.RetrievalEval)
            {
                var f = item.RetrievalQuery!.HardFilters;
                var semantic = item.RetrievalQuery.SemanticQuery;
                _ = CineKros.Api.Database.HardFilterSqlBuilder.Build(f);
                foreach (var mode in item.ApplicableModes)
                    if (!Applicable(mode, f, semantic)) throw new InvalidDataException($"Case {item.Id} mode {mode} is not applicable.");
                if (item.ApplicableModes.Count == 0) throw new InvalidDataException($"Case {item.Id} has no retrieval mode.");
            }
        }
        return set;
    }

    public static bool Applicable(string mode, RealHardFilters filters, string? semantic) => mode switch
    {
        "structured" => RealParsedQueryValidator.HasActiveFilter(filters),
        "semantic" => !string.IsNullOrWhiteSpace(semantic),
        "hybrid" => RealParsedQueryValidator.HasActiveFilter(filters) && !string.IsNullOrWhiteSpace(semantic),
        _ => false
    };
}

public static class FilterCompliance
{
    public static ComplianceResult Evaluate(RealHardFilters filters, IReadOnlyList<MovieMetadata> movies)
    {
        if (movies.Count == 0) return new ComplianceResult(0, null);
        var violations = movies.Count(movie => !Matches(filters, movie));
        return new ComplianceResult(violations, (movies.Count - violations) * 100d / movies.Count);
    }

    public static bool Matches(RealHardFilters f, MovieMetadata m)
    {
        if (f.YearMin is { } yearMin && (m.Year is null || m.Year < yearMin)) return false;
        if (f.YearMax is { } yearMax && (m.Year is null || m.Year > yearMax)) return false;
        if (f.RuntimeMin is { } runtimeMin && (m.RuntimeMinutes is null || m.RuntimeMinutes < runtimeMin)) return false;
        if (f.RuntimeMax is { } runtimeMax && (m.RuntimeMinutes is null || m.RuntimeMinutes > runtimeMax)) return false;
        if (f.Genres?.All is { } all && (m.Genres is null || all.Any(g => !m.Genres.Contains(g, StringComparer.Ordinal)))) return false;
        if (f.Genres?.Any is { } any && (m.Genres is null || !any.Any(g => m.Genres.Contains(g, StringComparer.Ordinal)))) return false;
        if (f.RatingMin is { } rating && (m.AverageRating is null || (f.RatingOperator == "gt" ? m.AverageRating <= rating : m.AverageRating < rating))) return false;
        if (f.OriginalLanguage is { } language && !string.Equals(m.OriginalLanguage, language, StringComparison.Ordinal)) return false;
        return true;
    }
}

public static class HumanScoring
{
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<long, int>> ReadJudgments(JsonElement root)
    {
        var result = new Dictionary<string, IReadOnlyDictionary<long, int>>(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Judgments must be a JSON object keyed by query ID.");
        foreach (var query in root.EnumerateObject())
        {
            if (query.Value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Each query judgment must map movie IDs to scores.");
            var scores = new Dictionary<long, int>();
            foreach (var item in query.Value.EnumerateObject())
            {
                if (!long.TryParse(item.Name, out var id) || item.Value.ValueKind != JsonValueKind.Number || !item.Value.TryGetInt32(out var score) || score is < 0 or > 3)
                    throw new InvalidDataException("Human scores must be integer values from 0 to 3.");
                if (!scores.TryAdd(id, score)) throw new InvalidDataException("Duplicate movie judgment.");
            }
            if (!result.TryAdd(query.Name, scores)) throw new InvalidDataException("Duplicate query judgments.");
        }
        return result;
    }

    public static HumanMetrics Calculate(IReadOnlyList<MovieResult> movies, IReadOnlyDictionary<long, int>? pooledJudgments = null)
    {
        if (movies.Count == 0 || movies.Any(m => m.HumanScore is null)) return new HumanMetrics(null, null, null);
        var scores = movies.Select(m => m.HumanScore!.Value).ToArray();
        var mean = scores.Average();
        var precision = scores.Count(s => s >= 2) / (double)Math.Min(10, scores.Length);
        var dcg = scores.Select((s, i) => Gain(s) / Math.Log2(i + 2)).Sum();
        var idealScores = (pooledJudgments?.Values ?? scores).OrderDescending().Take(10).ToArray();
        var ideal = idealScores.Select((s, i) => Gain(s) / Math.Log2(i + 2)).Sum();
        return new HumanMetrics(mean, precision, ideal == 0 ? 0 : dcg / ideal);
    }

    private static double Gain(int score) => Math.Pow(2, score) - 1;
}

public static class ParserScoring
{
    public static ParserComparison Compare(string id, JsonElement expected, string actualChecklist, RealParserResult actual, double durationMs)
    {
        var checklistExpected = expected.GetProperty("providerChecklist");
        var dtoExpected = expected.GetProperty("canonicalDto");
        using var actualEvidence = JsonDocument.Parse(actualChecklist);
        var providerMatch = Equivalent(WithoutSemanticText(checklistExpected), WithoutSemanticText(actualEvidence.RootElement));
        var expectedAlert = dtoExpected.TryGetProperty("alertCode", out var alert) && alert.ValueKind == JsonValueKind.String ? alert.GetString() : null;
        var canonicalMatch = expectedAlert is not null
            ? actual.Type == "alert" && actual.AlertCode == expectedAlert
            : actual.Type == "query" && dtoExpected.TryGetProperty("query", out var expectedQuery) &&
              expectedQuery.TryGetProperty("hardFilters", out var expectedFilters) &&
              EquivalentIgnoringNulls(expectedFilters, JsonSerializer.SerializeToElement(actual.Query!.HardFilters, EvaluationJson.Options));
        var expectedSemantic = dtoExpected.TryGetProperty("query", out var query) && query.TryGetProperty("semanticQuery", out var semantic) ? semantic : default;
        var literalMatch = actual.Type == "query" && expectedSemantic.ValueKind != JsonValueKind.Undefined &&
            Equivalent(expectedSemantic, JsonSerializer.SerializeToElement(actual.Query!.SemanticQuery, EvaluationJson.Options));
        return new ParserComparison(id, providerMatch, canonicalMatch, literalMatch, actual.AlertCode, durationMs);
    }

    private static bool Equivalent(JsonElement left, JsonElement right) => Normalize(left) == Normalize(right);
    private static bool EquivalentIgnoringNulls(JsonElement left, JsonElement right) => Normalize(left, true) == Normalize(right, true);
    private static JsonElement WithoutSemanticText(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject())
                if (property.Name != "semanticQuery") { writer.WritePropertyName(property.Name); property.Value.WriteTo(writer); }
            writer.WriteEndObject();
        }
        using var doc = JsonDocument.Parse(stream.ToArray());
        return doc.RootElement.Clone();
    }
    private static string Normalize(JsonElement value, bool ignoreNullObjectValues = false) => value.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", value.EnumerateObject().Where(p => !ignoreNullObjectValues || p.Value.ValueKind != JsonValueKind.Null).OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => JsonSerializer.Serialize(p.Name) + ":" + Normalize(p.Value, ignoreNullObjectValues))) + "}",
        JsonValueKind.Array => "[" + string.Join(",", value.EnumerateArray().Select(v => Normalize(v, ignoreNullObjectValues))) + "]",
        JsonValueKind.String => JsonSerializer.Serialize(value.GetString()),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true", JsonValueKind.False => "false", JsonValueKind.Null => "null", _ => "null"
    };
}
