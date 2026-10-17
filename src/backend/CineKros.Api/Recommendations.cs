namespace CineKros.Api;

public sealed record ParserInput(string Language, string Message);
public sealed record HardFilters(int? YearMin = null, int? RuntimeMax = null, IReadOnlyList<string>? Genres = null);
public sealed record ParsedQuery(HardFilters HardFilters, string SemanticQuery);
public sealed record ParserResult(string Type, ParsedQuery? Query = null, string? AlertCode = null);

public interface IQueryParser
{
    Task<ParserResult> ParseAsync(string systemInstruction, ParserInput input, CancellationToken cancellationToken);
}

public sealed record SearchCandidate(long MovieId, string Title, int? Year, int? Runtime, IReadOnlyList<string>? Genres, string ImdbDigits, string? PosterUrl);
public interface IMovieSearch
{
    Task<IReadOnlyList<SearchCandidate>> SearchAsync(ParsedQuery query, CancellationToken cancellationToken);
}

public sealed class MovieQueryPrompt(string content)
{
    public string Content { get; } = content;
}

public static class ApiErrorCodes
{
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string QueryUnclear = "QUERY_UNCLEAR";
    public const string LanguageMismatch = "LANGUAGE_MISMATCH";
    public const string NotMovieRequest = "NOT_MOVIE_REQUEST";
    public const string UnsupportedRequest = "UNSUPPORTED_REQUEST";
    public const string NoResults = "NO_RESULTS";
    public const string RateLimited = "RATE_LIMITED";
    public const string ParserInvalidResponse = "PARSER_INVALID_RESPONSE";
    public const string ProviderUnavailable = "PROVIDER_UNAVAILABLE";
    public const string SearchUnavailable = "SEARCH_UNAVAILABLE";
    public const string InternalError = "INTERNAL_ERROR";
}
