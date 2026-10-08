using System.Diagnostics;
using CineKros.Api.Database;
using CineKros.Api.Diagnostics;
using CineKros.Api.RealProviders;
using CineKros.Api.Search;

namespace CineKros.Api.RealFlow;

public static class RealRecommendationEndpoint
{
    public static async Task<IResult> HandleAsync(HttpContext context, RealRecommendationService service, ILoggerFactory loggerFactory)
    {
        var timer = Stopwatch.StartNew();
        var code = ApiErrorCodes.InternalError;
        var logger = loggerFactory.CreateLogger("CineKros.Api.Recommendations");
        RealParserResult? validatedParserResult = null;
        string? originalQuery = null;
        try
        {
            var attempt = await RecommendationEndpoint.ReadRequestAsync(context.Request, context.RequestAborted);
            if (attempt.Request is null)
            {
                code = ApiErrorCodes.InvalidRequest;
                return RecommendationEndpoint.Alert(code, attempt.Language, StatusCodes.Status400BadRequest);
            }
            originalQuery = attempt.Request.Message;

            RealRecommendationResult result;
            try
            {
                result = service.LanguageAwarePoc
                    ? await service.RecommendAsync(new RealRecommendationRequest(attempt.Request, MapSelectedLanguage(attempt.Request.Language)), context.RequestAborted)
                    : await service.RecommendAsync(attempt.Request, context.RequestAborted);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
            catch (RealProviderException ex)
            {
                if (ex.Code == ApiErrorCodes.InvalidRequest)
                {
                    code = ApiErrorCodes.InvalidRequest;
                    return RecommendationEndpoint.Alert(code, attempt.Request.Language, StatusCodes.Status400BadRequest);
                }
                code = AllowedTechnicalCode(ex.Code);
                return RecommendationEndpoint.Technical(code);
            }
            catch
            {
                code = ApiErrorCodes.InternalError;
                return RecommendationEndpoint.Technical(code);
            }

            if (result.AlertCode is not null)
            {
                validatedParserResult = result.ParsedResult;
                code = result.AlertCode;
                return RecommendationEndpoint.Alert(code, attempt.Request.Language, code == ApiErrorCodes.NoResults ? StatusCodes.Status200OK : code == ApiErrorCodes.QueryUnclear || code == ApiErrorCodes.LanguageMismatch || code == ApiErrorCodes.NotMovieRequest || code == ApiErrorCodes.UnsupportedRequest ? StatusCodes.Status422UnprocessableEntity : StatusCodes.Status500InternalServerError);
            }
            if (result.Movies.Count == 0)
            {
                validatedParserResult = result.ParsedResult;
                code = ApiErrorCodes.NoResults;
                return RecommendationEndpoint.Alert(code, attempt.Request.Language, StatusCodes.Status200OK);
            }

            var cards = new List<MovieCard>(result.Movies.Count);
            var ids = new HashSet<long>();
            foreach (var movie in result.Movies)
            {
                if (movie.MovieLensId <= 0 || !ids.Add(movie.MovieLensId) || string.IsNullOrWhiteSpace(movie.Title) || !IsImdbDigits(movie.ImdbId))
                {
                    code = ApiErrorCodes.SearchUnavailable;
                    return RecommendationEndpoint.Technical(code);
                }
                cards.Add(new MovieCard(movie.Title, movie.Year, $"https://www.imdb.com/title/tt{movie.ImdbId}/", PosterUrl(movie.PosterPath)));
            }

            code = "SUCCESS";
            validatedParserResult = result.ParsedResult;
            return Results.Json(new MoviesResponse("movies", cards, new Meta(cards.Count, cards.Count < 10)), statusCode: StatusCodes.Status200OK);
        }
        finally
        {
            if (validatedParserResult is not null && originalQuery is not null)
                DevelopmentRequestSummary.WriteIfDevelopment(context.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment(), originalQuery, validatedParserResult, timer.ElapsedMilliseconds, code);
            else if (originalQuery is not null && code == ApiErrorCodes.ParserInvalidResponse)
                DevelopmentRequestSummary.WriteFailureIfDevelopment(context.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment(), originalQuery, timer.ElapsedMilliseconds, code);
            DevelopmentRequestSummary.LogCompletion(logger, context.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment(), context.TraceIdentifier, timer.ElapsedMilliseconds, code);
        }
    }

    private static string AllowedTechnicalCode(string code) => code is ApiErrorCodes.ParserInvalidResponse or ApiErrorCodes.ProviderUnavailable or ApiErrorCodes.SearchUnavailable ? code : ApiErrorCodes.InternalError;

    private static SearchLanguage MapSelectedLanguage(string language) => language switch
    {
        "en" => SearchLanguage.English,
        "sr" => SearchLanguage.Serbian,
        _ => throw new RealProviderException(ApiErrorCodes.ParserInvalidResponse)
    };

    private static bool IsImdbDigits(string value) => value.Length is >= 1 and <= 12 && value.All(character => character is >= '0' and <= '9');

    private static string? PosterUrl(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path[0] != '/' || path.StartsWith("//", StringComparison.Ordinal) ||
            path.Contains('\\') || path.Contains('?') || path.Contains('#') || path.Contains("..", StringComparison.Ordinal)) return null;
        return $"https://image.tmdb.org/t/p/w500{path}";
    }

    private sealed record MoviesResponse(string Type, IReadOnlyList<MovieCard> Movies, Meta Meta);
    private sealed record MovieCard(string Title, int? Year, string ImdbUrl, string? PosterUrl);
    private sealed record Meta(int Count, bool Partial);
}
