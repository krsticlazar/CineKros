namespace CineKros.Api.RealProviders;

public sealed record RealHardFilters(
    int? YearMin = null,
    int? YearMax = null,
    int? RuntimeMin = null,
    int? RuntimeMax = null,
    RealGenreFilter? Genres = null,
    decimal? RatingMin = null,
    string? OriginalLanguage = null,
    string? RatingOperator = null);

public sealed record RealGenreFilter(IReadOnlyList<string>? All = null, IReadOnlyList<string>? Any = null);
public sealed record RealParsedQuery(RealHardFilters HardFilters, string? SemanticQuery);
public sealed record RealParserResult(string Type, RealParsedQuery? Query = null, string? AlertCode = null);

public sealed class RealProviderException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
