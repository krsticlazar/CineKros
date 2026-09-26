using System.Text.Json;

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

public sealed class FakeQueryParser : IQueryParser
{
    public Task<ParserResult> ParseAsync(string systemInstruction, ParserInput input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var message = input.Message.Trim();
        var key = input.Language + ":" + message;
        if (key is "sr:Hoću mračan SF posle 2010. do dva sata." or "en:dark sci-fi after 2010 under two hours")
            return Query(2010, 120, ["Sci-Fi"], "dark atmospheric science fiction");
        if (key is "sr:tiha misterija" or "en:quiet mystery") return Query(null, null, null, "quiet mystery");
        if (key is "sr:bez odgovarajućih filmova" or "en:no matching films") return Query(null, null, null, "no matching films");
        if (key == "en:unclear movie") return Alert("QUERY_UNCLEAR");
        if (key == "en:not a movie request") return Alert("NOT_MOVIE_REQUEST");
        if (key == "en:movie with unsupported actor filter") return Alert("UNSUPPORTED_REQUEST");
        if (key == "en:simulate provider failure") throw new InvalidOperationException("fake parser failure detail");
        if (key == "en:simulate search failure") return Query(null, null, null, "simulate search failure");
        return Alert("QUERY_UNCLEAR");
    }

    private static Task<ParserResult> Query(int? year, int? runtime, IReadOnlyList<string>? genres, string semantic) =>
        Task.FromResult(new ParserResult("query", new ParsedQuery(new HardFilters(year, runtime, genres), semantic)));
    private static Task<ParserResult> Alert(string code) => Task.FromResult(new ParserResult("alert", AlertCode: code));
}

public sealed class FakeMovieSearch : IMovieSearch
{
    public Task<IReadOnlyList<SearchCandidate>> SearchAsync(ParsedQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (query.SemanticQuery == "simulate search failure") throw new InvalidOperationException("fake search failure detail");
        var count = query.SemanticQuery switch { "dark atmospheric science fiction" => 10, "quiet mystery" => 3, "no matching films" => 0, _ => 0 };
        var all = Enumerable.Range(1, count).Select(n =>
        {
            var id = n;
            return new SearchCandidate(id, $"Synthetic Film {id:00}", 2014 + id, 100, ["Sci-Fi"], id.ToString("0000000"), id % 2 == 0 ? null : $"https://image.tmdb.org/t/p/w500/synthetic-{id:00}.jpg");
        });
        if (count == 10)
        {
            all = new[]
            {
                new SearchCandidate(90, "Year Decoy", 2009, 100, ["Sci-Fi"], "90", null),
                new SearchCandidate(91, "Runtime Decoy", 2015, 130, ["Sci-Fi"], "91", null),
                new SearchCandidate(92, "Genre Decoy", 2015, 100, ["Drama"], "92", null),
                new SearchCandidate(93, "Null Year", null, 100, ["Sci-Fi"], "93", null),
                new SearchCandidate(94, "Null Runtime", 2015, null, ["Sci-Fi"], "94", null),
                new SearchCandidate(95, "Null Genre", 2015, 100, null, "95", null),
            }.Concat(all).ToArray();
        }
        var filtered = all.Where(c => (query.HardFilters.YearMin is null || c.Year is not null && c.Year >= query.HardFilters.YearMin) &&
            (query.HardFilters.RuntimeMax is null || c.Runtime is not null && c.Runtime <= query.HardFilters.RuntimeMax) &&
            (query.HardFilters.Genres is null || query.HardFilters.Genres.All(g => c.Genres?.Contains(g, StringComparer.Ordinal) == true)))
            .ToArray();
        return Task.FromResult<IReadOnlyList<SearchCandidate>>(filtered);
    }
}

public static class ApiErrorCodes
{
    public const string InvalidRequest = "INVALID_REQUEST";
    public const string QueryUnclear = "QUERY_UNCLEAR";
    public const string NotMovieRequest = "NOT_MOVIE_REQUEST";
    public const string UnsupportedRequest = "UNSUPPORTED_REQUEST";
    public const string NoResults = "NO_RESULTS";
    public const string RateLimited = "RATE_LIMITED";
    public const string ParserInvalidResponse = "PARSER_INVALID_RESPONSE";
    public const string ProviderUnavailable = "PROVIDER_UNAVAILABLE";
    public const string SearchUnavailable = "SEARCH_UNAVAILABLE";
    public const string InternalError = "INTERNAL_ERROR";
}
