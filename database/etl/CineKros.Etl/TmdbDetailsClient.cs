using System.Net;
using System.Text.Json;

namespace CineKros.Etl;

public sealed record TmdbDetails(int? RuntimeMinutes, string? OriginalLanguage, string? PosterPath, bool NotFound = false);

public sealed class TmdbDetailsClient
{
    private readonly HttpClient _httpClient;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public TmdbDetailsClient(HttpClient httpClient, string bearerToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (string.IsNullOrWhiteSpace(bearerToken)) throw new ArgumentException("TMDB bearer token is required.", nameof(bearerToken));
        _httpClient.BaseAddress = new Uri("https://api.themoviedb.org");
        _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
    }

    public async Task<TmdbDetails> GetAsync(int tmdbId, CancellationToken cancellationToken)
    {
        if (tmdbId <= 0) throw new ArgumentOutOfRangeException(nameof(tmdbId));
        for (var attempt = 1; ; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"/3/movie/{tmdbId}");
                using var response = await _httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound) return new TmdbDetails(null, null, null, true);
                if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                    throw new TmdbConfigurationException("TMDB authorization failed.");
                if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    if (attempt == 3) throw new TmdbRequestException("TMDB request failed after three attempts.", failureClass: response.StatusCode == HttpStatusCode.TooManyRequests ? "http_429" : "http_5xx");
                    await WaitBeforeRetry(response.Headers.RetryAfter, attempt, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                if (!response.IsSuccessStatusCode) throw new TmdbRequestException($"TMDB returned HTTP {(int)response.StatusCode}.", failureClass: "http_4xx");
                return Parse(response.Content is null ? "" : await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false), tmdbId);
            }
            catch (TmdbConfigurationException) { throw; }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                if (attempt == 3) throw new TmdbRequestException("TMDB request timed out after three attempts.", failureClass: "timeout");
                await _delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception)
            {
                if (attempt == 3) throw new TmdbRequestException("TMDB network request failed after three attempts.", exception, "network");
                await _delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task WaitBeforeRetry(System.Net.Http.Headers.RetryConditionHeaderValue? retryAfter, int attempt, CancellationToken token)
    {
        var wait = retryAfter?.Delta;
        if (wait is null && retryAfter?.Date is DateTimeOffset date)
        {
            var until = date - DateTimeOffset.UtcNow;
            wait = until > TimeSpan.Zero ? until : TimeSpan.Zero;
        }
        if (wait is null) wait = TimeSpan.FromSeconds(attempt);
        await _delay(wait.Value, token).ConfigureAwait(false);
    }

    private static TmdbDetails Parse(string json, int requestedId)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var propertyNames = root.ValueKind == JsonValueKind.Object ? root.EnumerateObject().Select(property => property.Name).ToArray() : [];
            if (root.ValueKind != JsonValueKind.Object || propertyNames.Distinct(StringComparer.Ordinal).Count() != propertyNames.Length ||
                !root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var actualId) || actualId != requestedId)
                throw new TmdbRequestException("TMDB detail response has a missing or mismatched ID.", failureClass: "invalid_response");
            int? runtime = null;
            if (root.TryGetProperty("runtime", out var runtimeProperty) && runtimeProperty.ValueKind != JsonValueKind.Null)
            {
                if (runtimeProperty.ValueKind != JsonValueKind.Number || !runtimeProperty.TryGetInt32(out var value) || value <= 0)
                    throw new TmdbRequestException("TMDB runtime is invalid.", failureClass: "invalid_response");
                runtime = value;
            }
            var language = ReadOptionalString(root, "original_language");
            if (language is not null && string.IsNullOrWhiteSpace(language)) throw new TmdbRequestException("TMDB original language is invalid.", failureClass: "invalid_response");
            var poster = ReadOptionalString(root, "poster_path");
            if (poster is not null && !poster.StartsWith("/", StringComparison.Ordinal)) throw new TmdbRequestException("TMDB poster path is invalid.", failureClass: "invalid_response");
            return new TmdbDetails(runtime, language, poster);
        }
        catch (JsonException exception) { throw new TmdbRequestException("TMDB detail response is malformed.", exception, "invalid_response"); }
    }

    private static string? ReadOptionalString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return null;
        if (property.ValueKind != JsonValueKind.String) throw new TmdbRequestException($"TMDB {name} is invalid.");
        return property.GetString();
    }
}

public sealed class TmdbRequestException : Exception
{
    public string FailureClass { get; }
    public TmdbRequestException(string message, Exception? inner = null, string failureClass = "transient") : base(message, inner) => FailureClass = failureClass;
}
public sealed class TmdbConfigurationException(string message) : Exception(message);
