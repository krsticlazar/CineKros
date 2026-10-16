using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using CineKros.Api;
using CineKros.Api.Database;
using CineKros.Api.Search;

namespace CineKros.Api.RealProviders;

/// <summary>Validates the mandatory provider checklist and maps it to the existing canonical DTO.</summary>
public sealed class RealParsedQueryValidator
{
    private readonly ActorNameResolver? actorResolver;

    public RealParsedQueryValidator(ActorNameResolver? actorResolver = null) => this.actorResolver = actorResolver;
    private static readonly HashSet<string> Genres = new(StringComparer.Ordinal)
    {
        "Action", "Adventure", "Animation", "Children", "Comedy", "Crime", "Documentary", "Drama", "Fantasy", "Film-Noir", "Horror", "IMAX", "Musical", "Mystery", "Romance", "Sci-Fi", "Thriller", "War", "Western"
    };
    private static readonly HashSet<string> Languages = new(StringComparer.Ordinal)
    {
        "ar", "bm", "bn", "bo", "bs", "cs", "da", "de", "el", "en", "es", "fa", "fi", "fr", "he", "hi", "hu", "id", "is", "it", "iu", "ja", "ka", "ko", "ku", "mk", "mn", "nl", "no", "pl", "pt", "ro", "ru", "sk", "sr", "sv", "ta", "th", "tl", "tn", "tr", "vi", "zh"
    };
    private static readonly HashSet<string> AlertCodes = new(StringComparer.Ordinal) { "QUERY_UNCLEAR", "NOT_MOVIE_REQUEST", "UNSUPPORTED_REQUEST" };
    private static readonly HashSet<string> InternalControlCodeTokens = new(StringComparer.Ordinal)
    {
        ApiErrorCodes.InvalidRequest,
        ApiErrorCodes.QueryUnclear,
        ApiErrorCodes.LanguageMismatch,
        ApiErrorCodes.NotMovieRequest,
        ApiErrorCodes.UnsupportedRequest,
        ApiErrorCodes.NoResults,
        ApiErrorCodes.RateLimited,
        ApiErrorCodes.ParserInvalidResponse,
        ApiErrorCodes.ProviderUnavailable,
        ApiErrorCodes.SearchUnavailable,
        ApiErrorCodes.InternalError
    };

    public RealParserResult Validate(string json) => Validate(json, out _);

    /// <summary>Validates and returns a compact serialization of only the accepted provider checklist fields.</summary>
    public RealParserResult Validate(string json, out string providerChecklistEvidence)
        => ValidateChecklist(json, false, out providerChecklistEvidence);

    private RealParserResult ValidateChecklist(string json, bool actorV6, out string providerChecklistEvidence)
    {
        providerChecklistEvidence = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = ReadFields(doc.RootElement, "type", "query", "alertCode");
            RequireOnly(root, "type", "query", "alertCode");
            var type = RequiredString(root, "type");
            if (type == "alert")
            {
                if (Required(root, "query").ValueKind != JsonValueKind.Null) Invalid();
                var code = RequiredString(root, "alertCode");
                if (!AlertCodes.Contains(code)) Invalid();
                providerChecklistEvidence = JsonSerializer.Serialize(new { type = "alert", alertCode = code });
                return new RealParserResult("alert", AlertCode: code);
            }
            if (type != "query" || Required(root, "alertCode").ValueKind != JsonValueKind.Null) Invalid();

            string[] queryKeys = actorV6
                ? ["year", "runtime", "genres", "rating", "originalLanguage", "semanticQuery", "actors"]
                : ["year", "runtime", "genres", "rating", "originalLanguage", "semanticQuery"];
            var query = ReadFields(Required(root, "query"), queryKeys);
            RequireOnly(query, queryKeys);
            var year = ReadBounds(Required(query, "year"), "min", "max", 1000, 9999);
            var runtime = ReadBounds(Required(query, "runtime"), "min", "max", 1, int.MaxValue);
            if (year.Min > year.Max || runtime.Min > runtime.Max) Invalid();
            var genre = ReadGenres(Required(query, "genres"));
            var rating = ReadRating(Required(query, "rating"));
            var language = ReadScalar(Required(query, "originalLanguage"), "value", ReadNullableString);
            if (language.Value is not null && !Languages.Contains(language.Value)) Invalid();
            var semantic = ReadSemantic(Required(query, "semanticQuery"));
            var actors = actorV6 ? ReadActors(Required(query, "actors")) : default;

            providerChecklistEvidence = JsonSerializer.Serialize(new
            {
                type = "query",
                year = new { status = year.Status, min = year.Min, max = year.Max },
                runtime = new { status = runtime.Status, min = runtime.Min, max = runtime.Max },
                genres = new { status = genre.Status, all = genre.Value?.All ?? [], any = genre.Value?.Any ?? [] },
                rating = new { status = rating.Status, value = rating.Value, @operator = rating.Operator, scale = rating.Scale },
                originalLanguage = new { status = language.Status, value = language.Value },
                semanticQuery = semantic is null ? "absent" : "present"
            });
            if (actorV6)
            {
                using var originalEvidence = JsonDocument.Parse(providerChecklistEvidence);
                var evidenceFields = originalEvidence.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone());
                evidenceFields.Add("actors", new { status = actors.Status, all = actors.Value?.All ?? [], any = actors.Value?.Any ?? [], exclude = actors.Value?.Exclude ?? [] });
                providerChecklistEvidence = JsonSerializer.Serialize(evidenceFields);
            }

            if (year.Unsupported || runtime.Unsupported || genre.Unsupported || rating.Unsupported || language.Unsupported || actors.Unsupported)
                return new RealParserResult("alert", AlertCode: "UNSUPPORTED_REQUEST");

            decimal? ratingMin = null;
            if (rating.Status == "present")
            {
                if (!RatingThresholdNormalizer.TryNormalize(rating.Value!.Value, rating.Scale!, out var normalizedRating))
                    return new RealParserResult("alert", AlertCode: "UNSUPPORTED_REQUEST");
                ratingMin = normalizedRating;
            }

            RealActorFilter? canonicalActors = null;
            if (actors.Value is not null)
            {
                if (actorResolver is null) throw new RealProviderException("SEARCH_UNAVAILABLE");
                canonicalActors = ResolveActors(actors.Value);
                if (canonicalActors is null) return new RealParserResult("alert", AlertCode: "QUERY_UNCLEAR");
            }
            var hardFilters = new RealHardFilters(year.Min, year.Max, runtime.Min, runtime.Max, genre.Value, ratingMin, language.Value, rating.Status == "present" ? rating.Operator : null, canonicalActors);
            if (semantic is null && !HasActiveFilter(hardFilters)) Invalid();
            return new RealParserResult("query", new RealParsedQuery(hardFilters, semantic));
        }
        catch (RealProviderException) { throw; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or FormatException or OverflowException)
        {
            throw new RealProviderException("PARSER_INVALID_RESPONSE");
        }
    }

    /// <summary>Rechecks typed canonical results at the orchestration boundary, including injected implementations.</summary>
    public void ValidateResult(RealParserResult? result)
        => ValidateResultCore(result, false);

    private void ValidateResultCore(RealParserResult? result, bool actorV6)
    {
        if (result is null) Invalid();
        if (result.Type == "alert")
        {
            if (result.Query is not null || result.AlertCode is null || !AlertCodes.Contains(result.AlertCode)) Invalid();
            return;
        }
        if (result.Type != "query" || result.AlertCode is not null || result.Query is null || result.Query.HardFilters is null) Invalid();
        var filters = result.Query.HardFilters;
        if (!actorV6 && filters.Actors is not null) Invalid();
        try { _ = HardFilterSqlBuilder.Build(filters); }
        catch (RealProviderException) { Invalid(); }
        if (filters.Actors is not null)
        {
            if (actorResolver is null) throw new RealProviderException("SEARCH_UNAVAILABLE");
            foreach (var name in (filters.Actors.All ?? []).Concat(filters.Actors.Any ?? []).Concat(filters.Actors.Exclude ?? []))
                if (!actorResolver.IsCanonical(name)) Invalid();
        }
        var semantic = result.Query.SemanticQuery;
        if (semantic is null)
        {
            if (!HasActiveFilter(filters)) Invalid();
        }
        else if (!string.Equals(semantic, semantic.Trim(), StringComparison.Ordinal) || semantic.Length == 0 || semantic.Any(char.IsControl) || !semantic.EnumerateRunes().Any(Rune.IsLetterOrDigit) || InternalControlCodeTokens.Contains(semantic)) Invalid();
    }

    /// <summary>Validates the strict v5 envelope and its language branch, then applies the unchanged v4 checklist rules.</summary>
    public RealParserResult ValidateV5(string json, SearchLanguage selectedLanguage, out string evidence)
        => ValidateLanguageEnvelope(json, selectedLanguage, false, out evidence);

    public RealParserResult ValidateV6(string json, SearchLanguage language, out string evidence)
        => ValidateLanguageEnvelope(json, language, true, out evidence);

    public RealParserResult ValidateV6(string json, SearchLanguage language) => ValidateV6(json, language, out _);

    private RealParserResult ValidateLanguageEnvelope(string json, SearchLanguage selectedLanguage, bool actorV6, out string evidence)
    {
        evidence = string.Empty;
        if (selectedLanguage is not (SearchLanguage.English or SearchLanguage.Serbian)) Invalid();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = ReadFields(doc.RootElement, "type", "languageCheck", "query", "alertCode");
            RequireOnly(root, "type", "languageCheck", "query", "alertCode");
            var type = RequiredString(root, "type");
            var languageCheck = RequiredString(root, "languageCheck");
            if (languageCheck is not ("match" or "mismatch" or "unclear")) Invalid();

            var queryValue = Required(root, "query");
            var alertValue = Required(root, "alertCode");
            RealParserResult result = null!;
            string checklist = string.Empty;
            if (type == "query")
            {
                if (languageCheck != "match" || alertValue.ValueKind != JsonValueKind.Null) Invalid();
                var legacyJson = JsonSerializer.Serialize(new { type, query = queryValue, alertCode = (string?)null });
                result = ValidateChecklist(legacyJson, actorV6, out checklist);
                result = result with { LanguageCheck = languageCheck };
            }
            else if (type == "alert")
            {
                if (queryValue.ValueKind != JsonValueKind.Null) Invalid();
                var alertCode = RequiredString(root, "alertCode");
                if (languageCheck == "mismatch")
                {
                    if (alertCode != "LANGUAGE_MISMATCH") Invalid();
                }
                else if (languageCheck == "unclear")
                {
                    if (alertCode != "QUERY_UNCLEAR") Invalid();
                }
                else if (!AlertCodes.Contains(alertCode)) Invalid();

                if (languageCheck is "mismatch" or "unclear")
                {
                    result = new RealParserResult("alert", AlertCode: alertCode, LanguageCheck: languageCheck);
                    checklist = JsonSerializer.Serialize(new { type = "alert", alertCode });
                }
                else
                {
                    var legacyJson = JsonSerializer.Serialize(new { type, query = (object?)null, alertCode });
                    result = Validate(legacyJson, out checklist) with { LanguageCheck = languageCheck };
                }
            }
            else Invalid();

            using var checklistDocument = JsonDocument.Parse(checklist);
            evidence = JsonSerializer.Serialize(new
            {
                languageCheck,
                checklist = checklistDocument.RootElement.Clone()
            });
            return result;
        }
        catch (RealProviderException) { throw; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or FormatException or OverflowException)
        {
            throw new RealProviderException("PARSER_INVALID_RESPONSE");
        }
    }

    public RealParserResult ValidateV5(string json, SearchLanguage selectedLanguage) => ValidateV5(json, selectedLanguage, out _);

    /// <summary>Rechecks injected v5 DTOs at the orchestration boundary.</summary>
    public void ValidateResultV5(RealParserResult? result, SearchLanguage selectedLanguage)
        => ValidateLanguageResult(result, selectedLanguage, false);

    public void ValidateResultV6(RealParserResult? result, SearchLanguage language)
        => ValidateLanguageResult(result, language, true);

    private void ValidateLanguageResult(RealParserResult? result, SearchLanguage selectedLanguage, bool actorV6)
    {
        if (selectedLanguage is not (SearchLanguage.English or SearchLanguage.Serbian) || result is null) Invalid();
        switch (result.LanguageCheck)
        {
            case "match":
                ValidateResultCore(result, actorV6);
                return;
            case "mismatch" when result.Type == "alert" && result.Query is null && result.AlertCode == "LANGUAGE_MISMATCH":
            case "unclear" when result.Type == "alert" && result.Query is null && result.AlertCode == "QUERY_UNCLEAR":
                return;
            default:
                Invalid();
                return;
        }
    }

    public static bool HasActiveFilter(RealHardFilters f) => f.YearMin is not null || f.YearMax is not null || f.RuntimeMin is not null || f.RuntimeMax is not null || f.Genres is not null || f.RatingMin is not null || f.OriginalLanguage is not null || f.Actors is not null;

    private static (string? Status, RealActorFilter? Value, bool Unsupported) ReadActors(JsonElement value)
    {
        var fields = ReadFields(value, "status", "all", "any", "exclude");
        RequireOnly(fields, "status", "all", "any", "exclude");
        var status = ReadStatus(fields);
        var all = ReadActorList(Required(fields, "all"));
        var any = ReadActorList(Required(fields, "any"));
        var exclude = ReadActorList(Required(fields, "exclude"));
        var active = all.Count + any.Count + exclude.Count > 0;
        ValidateStatus(status, active);
        return (status, active ? new RealActorFilter(all.Count == 0 ? null : all, any.Count == 0 ? null : any, exclude.Count == 0 ? null : exclude) : null, status == "unsupported");
    }

    private static IReadOnlyList<string> ReadActorList(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) Invalid();
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) Invalid();
            var name = item.GetString()!;
            if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl) || !seen.Add(name)) Invalid();
            result.Add(name);
        }
        return result;
    }

    private RealActorFilter? ResolveActors(RealActorFilter proposed)
    {
        var unresolved = false;
        IReadOnlyList<string>? ResolveGroup(IReadOnlyList<string>? group)
        {
            if (group is null) return null;
            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in group)
            {
                var resolved = actorResolver!.Resolve(name);
                if (resolved is null) unresolved = true;
                else
                {
                    // Distinct provider proposals for the same person are malformed duplicates.
                    if (!seen.Add(resolved)) Invalid();
                    names.Add(resolved);
                }
            }
            return names.AsReadOnly();
        }
        var all = ResolveGroup(proposed.All);
        var any = ResolveGroup(proposed.Any);
        var exclude = ResolveGroup(proposed.Exclude);
        if (unresolved) return null;
        var excluded = new HashSet<string>(exclude ?? [], StringComparer.Ordinal);
        if ((all?.Any(excluded.Contains) ?? false) || (any is not null && any.All(excluded.Contains))) return null;
        return new RealActorFilter(all, any, exclude);
    }

    private static (string Status, int? Min, int? Max, bool Unsupported) ReadBounds(JsonElement value, string minName, string maxName, int minimum, int maximum)
    {
        var fields = ReadFields(value, "status", minName, maxName);
        RequireOnly(fields, "status", minName, maxName);
        var status = ReadStatus(fields);
        var min = ReadNullableInt(Required(fields, minName), minimum, maximum);
        var max = ReadNullableInt(Required(fields, maxName), minimum, maximum);
        ValidateStatus(status, min is not null || max is not null);
        if (status != "present" && (min is not null || max is not null)) Invalid();
        return (status, min, max, status == "unsupported");
    }

    private static (string Status, RealGenreFilter? Value, bool Unsupported) ReadGenres(JsonElement value)
    {
        var fields = ReadFields(value, "status", "all", "any");
        RequireOnly(fields, "status", "all", "any");
        var status = ReadStatus(fields);
        var all = ReadGenreList(Required(fields, "all"));
        var any = ReadGenreList(Required(fields, "any"));
        var active = all.Count > 0 || any.Count > 0;
        ValidateStatus(status, active);
        if (status != "present" && active) Invalid();
        return (status, active ? new RealGenreFilter(all.Count == 0 ? null : all, any.Count == 0 ? null : any) : null, status == "unsupported");
    }

    private static (string Status, decimal? Value, string? Operator, string? Scale, bool Unsupported) ReadRating(JsonElement value)
    {
        var fields = ReadFields(value, "status", "value", "operator", "scale");
        RequireOnly(fields, "status", "value", "operator", "scale");
        var status = ReadStatus(fields);
        var number = ReadNullableDecimal(Required(fields, "value"));
        var op = ReadNullableString(Required(fields, "operator"));
        var scale = ReadNullableString(Required(fields, "scale"));
        var hasValue = number is not null && op is not null && scale is not null;
        ValidateStatus(status, hasValue);
        if (status != "present" && (number is not null || op is not null || scale is not null)) Invalid();
        if (status == "present" && (op is not ("gte" or "gt") || scale is not ("five" or "ten" or "unspecified"))) Invalid();
        return (status, number, op, scale, status == "unsupported");
    }

    private static (string Status, T? Value, bool Unsupported) ReadScalar<T>(JsonElement value, string valueName, Func<JsonElement, T?> readValue) where T : struct
    {
        var fields = ReadFields(value, "status", valueName);
        RequireOnly(fields, "status", valueName);
        var status = ReadStatus(fields);
        var item = readValue(Required(fields, valueName));
        ValidateStatus(status, item is not null);
        if (status != "present" && item is not null) Invalid();
        return (status, item, status == "unsupported");
    }

    private static (string Status, string? Value, bool Unsupported) ReadScalar(JsonElement value, string valueName, Func<JsonElement, string?> readValue)
    {
        var fields = ReadFields(value, "status", valueName);
        RequireOnly(fields, "status", valueName);
        var status = ReadStatus(fields);
        var item = readValue(Required(fields, valueName));
        ValidateStatus(status, item is not null);
        if (status != "present" && item is not null) Invalid();
        return (status, item, status == "unsupported");
    }

    private static IReadOnlyList<string> ReadGenreList(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) Invalid();
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) Invalid();
            var label = item.GetString()!;
            if (!Genres.Contains(label) || !seen.Add(label)) Invalid();
            result.Add(label);
        }
        return result;
    }

    private static int? ReadNullableInt(JsonElement value, int min, int max)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        var number = 0;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out number) || number < min || number > max) Invalid();
        return number;
    }

    private static decimal? ReadNullableDecimal(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        var number = 0m;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out number)) Invalid();
        return number;
    }

    private static string? ReadNullableString(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) Invalid();
        return value.GetString();
    }

    private static string? ReadSemantic(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) Invalid();
        var semantic = value.GetString()?.Trim();
        if (string.IsNullOrEmpty(semantic) || semantic.Any(char.IsControl) || !semantic.EnumerateRunes().Any(Rune.IsLetterOrDigit) || InternalControlCodeTokens.Contains(semantic)) Invalid();
        return semantic;
    }

    private static string ReadStatus(IReadOnlyDictionary<string, JsonElement> fields)
    {
        var status = RequiredString(fields, "status");
        if (status is not ("present" or "absent" or "unsupported")) Invalid();
        return status;
    }

    private static void ValidateStatus(string status, bool hasValue)
    {
        if ((status == "present") != hasValue) Invalid();
    }

    private static string RequiredString(IReadOnlyDictionary<string, JsonElement> fields, string name)
    {
        var value = Required(fields, name);
        if (value.ValueKind != JsonValueKind.String) Invalid();
        return value.GetString()!;
    }

    private static JsonElement Required(IReadOnlyDictionary<string, JsonElement> fields, string name) => fields.TryGetValue(name, out var value) ? value : throw InvalidException();
    private static Dictionary<string, JsonElement> ReadFields(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) Invalid();
        var allow = new HashSet<string>(allowed, StringComparer.Ordinal);
        var result = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!allow.Contains(property.Name) || !result.TryAdd(property.Name, property.Value)) Invalid();
        return result;
    }
    private static void RequireOnly(IReadOnlyDictionary<string, JsonElement> fields, params string[] expected)
    {
        if (fields.Count != expected.Length || expected.Any(key => !fields.ContainsKey(key))) Invalid();
    }
    [DoesNotReturn] private static void Invalid() => throw InvalidException();
    private static RealProviderException InvalidException() => new("PARSER_INVALID_RESPONSE");
}
