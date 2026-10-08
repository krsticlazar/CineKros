using System.Buffers;
using System.Diagnostics;
using CineKros.Api.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CineKros.Api;

public static class RecommendationEndpoint
{
    private const int MaximumBodyBytes = 65_536;
    private const int MaximumMessageScalars = 500;
    private static readonly HashSet<string> Genres = new(StringComparer.Ordinal) { "Action", "Adventure", "Animation", "Children", "Comedy", "Crime", "Documentary", "Drama", "Fantasy", "Film-Noir", "Horror", "IMAX", "Musical", "Mystery", "Romance", "Sci-Fi", "Thriller", "War", "Western" };
    private static readonly Dictionary<string, int> Statuses = new(StringComparer.Ordinal)
    {
        [ApiErrorCodes.InvalidRequest] = 400, [ApiErrorCodes.QueryUnclear] = 422, [ApiErrorCodes.LanguageMismatch] = 422, [ApiErrorCodes.NotMovieRequest] = 422,
        [ApiErrorCodes.UnsupportedRequest] = 422, [ApiErrorCodes.NoResults] = 200, [ApiErrorCodes.RateLimited] = 429,
        [ApiErrorCodes.ParserInvalidResponse] = 502, [ApiErrorCodes.ProviderUnavailable] = 503,
        [ApiErrorCodes.SearchUnavailable] = 503, [ApiErrorCodes.InternalError] = 500,
    };
    private static readonly Dictionary<string, (string Sr, string En)> Copy = new(StringComparer.Ordinal)
    {
        [ApiErrorCodes.InvalidRequest] = ("Unesi ispravan zahtev za filmove.", "Enter a valid movie request."),
        [ApiErrorCodes.QueryUnclear] = ("Napiši malo jasnije kakav film tražiš.", "Describe the movie you want more clearly."),
        [ApiErrorCodes.LanguageMismatch] = ("Upit nije na izabranom jeziku. Promenite jezik ili preformulišite upit.", "The query is not in the selected language. Change the language or rephrase your query."),
        [ApiErrorCodes.NotMovieRequest] = ("Napiši zahtev za preporuku filma.", "Enter a movie recommendation request."),
        [ApiErrorCodes.UnsupportedRequest] = ("Jedan obavezan uslov trenutno ne možemo pouzdano da proverimo. Izmeni upit.", "We cannot reliably check one required condition yet. Please revise your request."),
        [ApiErrorCodes.NoResults] = ("Nema filmova koji ispunjavaju sve obavezne uslove.", "No movies meet all the required conditions."),
    };

    public static async Task<IResult> HandleAsync(HttpContext context, IQueryParser parser, IMovieSearch search, MovieQueryPrompt prompt, ILoggerFactory loggerFactory)
    {
        var timer = Stopwatch.StartNew(); var code = ApiErrorCodes.InternalError; var logger = loggerFactory.CreateLogger("CineKros.Api.Recommendations");
        try
        {
            var attempt = await ReadRequestAsync(context.Request, context.RequestAborted);
            var request = attempt.Request;
            if (request is null) { code = ApiErrorCodes.InvalidRequest; return Alert(code, attempt.Language, Statuses[code]); }
            ParserResult parsed;
            try { parsed = await parser.ParseAsync(prompt.Content, request, context.RequestAborted); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
            catch { code = ApiErrorCodes.ProviderUnavailable; return Technical(code); }
            if (!ValidateParserResult(parsed)) { code = ApiErrorCodes.ParserInvalidResponse; return Technical(code); }
            if (parsed.Type == "alert") { code = parsed.AlertCode!; return Alert(code, request.Language, Statuses[code]); }
            IReadOnlyList<SearchCandidate> candidates;
            try { candidates = await search.SearchAsync(parsed.Query!, context.RequestAborted); }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
            catch { code = ApiErrorCodes.SearchUnavailable; return Technical(code); }
            if (candidates.Count == 0) { code = ApiErrorCodes.NoResults; return Alert(code, request.Language, 200); }
            var cards = new List<MovieCard>(); var ids = new HashSet<long>();
            foreach (var item in candidates.Take(10))
            {
                if (item.MovieId <= 0 || string.IsNullOrWhiteSpace(item.Title) || !IsDigits(item.ImdbDigits) || !ids.Add(item.MovieId)) { code = ApiErrorCodes.InternalError; return Technical(code); }
                cards.Add(new MovieCard(item.Title, item.Year, $"https://www.imdb.com/title/tt{item.ImdbDigits}/", NormalizePoster(item.PosterUrl)));
            }
            code = "SUCCESS";
            return Results.Json(new MoviesResponse("movies", cards, new Meta(cards.Count, cards.Count < 10)), statusCode: 200);
        }
        finally { DevelopmentRequestSummary.LogCompletion(logger, context.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment(), context.TraceIdentifier, timer.ElapsedMilliseconds, code); }
    }

    internal static async Task<(ParserInput? Request, string Language)> ReadRequestAsync(HttpRequest request, CancellationToken token)
    {
        if (!HasAcceptedContentType(request.ContentType) || request.ContentLength > MaximumBodyBytes) return (null, "sr");
        byte[] body;
        try { using var buffer = new MemoryStream(); var chunk = new byte[8192]; while (true) { var remain = MaximumBodyBytes + 1 - (int)buffer.Length; if (remain == 0) return (null, "sr"); var n = await request.Body.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, remain)), token); if (n == 0) break; buffer.Write(chunk, 0, n); if (buffer.Length > MaximumBodyBytes) return (null, "sr"); } body = buffer.ToArray(); }
        catch (InvalidDataException) { return (null, "sr"); }
        string text; try { text = new UTF8Encoding(false, true).GetString(body); } catch (DecoderFallbackException) { return (null, "sr"); }
        try
        {
            using var doc = JsonDocument.Parse(text); if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, "sr");
            string? language = null, message = null; var count = 0; var invalid = false;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                count++;
                if (p.NameEquals("language"))
                {
                    if (language is not null || p.Value.ValueKind != JsonValueKind.String) { invalid = true; continue; }
                    language = p.Value.GetString();
                }
                else if (p.NameEquals("message"))
                {
                    if (message is not null || p.Value.ValueKind != JsonValueKind.String) { invalid = true; continue; }
                    message = p.Value.GetString();
                }
                else invalid = true;
            }
            var outputLanguage = language == "en" ? "en" : "sr";
            if (invalid || count != 2 || language is not ("sr" or "en") || message is null) return (null, outputLanguage);
            var trimmed = TrimMessage(message); if (trimmed is null || !Usable(trimmed)) return (null, outputLanguage);
            return (new ParserInput(language, trimmed), outputLanguage);
        }
        catch (JsonException) { return (null, "sr"); }
        catch (InvalidOperationException) { return (null, "sr"); }
        catch (ArgumentException) { return (null, "sr"); }
    }

    private static string? TrimMessage(string message)
    {
        var runes = new List<(Rune Rune, int Start, int Length)>(); var offset = 0;
        while (offset < message.Length) { if (Rune.DecodeFromUtf16(message.AsSpan(offset), out var rune, out var size) != OperationStatus.Done) return null; runes.Add((rune, offset, size)); offset += size; }
        var first = 0; while (first < runes.Count && IsWhitespace(runes[first].Rune.Value)) first++;
        var last = runes.Count - 1; while (last >= first && IsWhitespace(runes[last].Rune.Value)) last--;
        var count = last - first + 1; if (count is < 1 or > MaximumMessageScalars) return null;
        return message[runes[first].Start..(runes[last].Start + runes[last].Length)];
    }
    private static bool Usable(string message)
    {
        var alphaNum = 0; var lettersDigits = new List<Rune>();
        foreach (var rune in message.EnumerateRunes()) if (Rune.IsLetterOrDigit(rune)) { alphaNum++; lettersDigits.Add(rune); }
        if (alphaNum < 2) return false;
        if (lettersDigits.Count >= 16 && lettersDigits[0] != lettersDigits[1] &&
            lettersDigits.Skip(2).Select((rune, index) => rune == lettersDigits[(index + 2) % 2]).All(matches => matches)) return false;
        return lettersDigits.Count < 8 || lettersDigits.Any(r => r != lettersDigits[0]);
    }
    private static bool IsWhitespace(int v) => v is >= 9 and <= 13 or 0x20 or 0x85 or 0xA0 or 0x1680 or >= 0x2000 and <= 0x200A or 0x2028 or 0x2029 or 0x202F or 0x205F or 0x3000;
    private static bool HasAcceptedContentType(string? value) { if (string.IsNullOrWhiteSpace(value) || !MediaTypeHeaderValue.TryParse(value, out var h) || !string.Equals(h.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)) return false; return h.Parameters.Count == 0 || h.Parameters.Count == 1 && string.Equals(h.Parameters.First().Name, "charset", StringComparison.OrdinalIgnoreCase) && string.Equals(h.Parameters.First().Value?.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase); }
    private static bool ValidateParserResult(ParserResult? result)
    {
        if (result is null) return false;
        if (result.Type == "alert") return result.Query is null && result.AlertCode is ApiErrorCodes.QueryUnclear or ApiErrorCodes.NotMovieRequest or ApiErrorCodes.UnsupportedRequest;
        if (result.Type != "query" || result.AlertCode is not null || result.Query is null || result.Query.HardFilters is null) return false;
        var f = result.Query.HardFilters; var q = result.Query.SemanticQuery;
        if (f.RuntimeMax is <= 0 || f.Genres is { Count: not 1 } || f.Genres?.Any(g => !Genres.Contains(g)) == true) return false;
        return !string.IsNullOrWhiteSpace(q) && Regex.IsMatch(q, @"^[A-Za-z0-9][A-Za-z0-9 ,.'-]*$", RegexOptions.CultureInvariant);
    }
    private static bool IsDigits(string s) => s.Length is >= 1 and <= 12 && s.All(c => c is >= '0' and <= '9');
    private static string? NormalizePoster(string? value) { if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out var u) || u.Scheme != "https" || !string.Equals(u.Host, "image.tmdb.org", StringComparison.OrdinalIgnoreCase) || !u.IsDefaultPort || !u.AbsolutePath.StartsWith("/t/p/", StringComparison.Ordinal) || u.UserInfo.Length != 0 || u.Query.Length != 0 || u.Fragment.Length != 0) return null; return value; }
    internal static IResult Alert(string code, string language, int status) { var text = Copy[code]; return Results.Json(new AlertResponse("alert", new AlertBody(code, language == "en" ? text.En : text.Sr)), statusCode: status); }
    internal static IResult Technical(string code) => Results.Json(new TechnicalResponse(new TechnicalBody(code)), statusCode: Statuses.GetValueOrDefault(code, 500));
    private sealed record MoviesResponse(string Type, IReadOnlyList<MovieCard> Movies, Meta Meta);
    private sealed record MovieCard(string Title, int? Year, string ImdbUrl, string? PosterUrl);
    private sealed record Meta(int Count, bool Partial);
    private sealed record AlertResponse(string Type, AlertBody Alert);
    private sealed record AlertBody(string Code, string Message);
    private sealed record TechnicalResponse(TechnicalBody Error);
    private sealed record TechnicalBody(string Code);
}
