using System.Net;
using System.Text.Json;
using System.Diagnostics;
using System.Collections.Concurrent;

namespace CineKros.Etl;

public sealed record TmdbDetails(int? RuntimeMinutes, string? OriginalLanguage, string? PosterPath, bool NotFound = false);

public sealed class TmdbAttemptGuard : IDisposable
{
    private readonly int _maxAttempts;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly CancellationTokenSource _stopScheduling = new();
    private readonly ConcurrentDictionary<int, byte> _attemptedIds = new();
    private readonly Timer _deadline;
    private int _attemptCount;
    private string? _stopReason;

    public TmdbAttemptGuard(int maxAttempts = 10_000, TimeSpan? wallTimeLimit = null)
    {
        if (maxAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        var limit = wallTimeLimit ?? TimeSpan.FromMinutes(120);
        if (limit <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(wallTimeLimit));
        _maxAttempts = maxAttempts;
        WallTimeLimit = limit;
        _deadline = new Timer(_ => Stop("wall_clock"), null, limit, Timeout.InfiniteTimeSpan);
    }

    public int AttemptCount => Volatile.Read(ref _attemptCount);
    public int AttemptedIdCount => _attemptedIds.Count;
    public int MaxAttempts => _maxAttempts;
    public TimeSpan WallTimeLimit { get; }
    public TimeSpan Elapsed => _elapsed.Elapsed;
    public string? StopReason => Volatile.Read(ref _stopReason);
    public bool IsStopped => StopReason is not null;
    public CancellationToken StopSchedulingToken => _stopScheduling.Token;

    public bool TryBeginAttempt(int tmdbId)
    {
        if (tmdbId <= 0) throw new ArgumentOutOfRangeException(nameof(tmdbId));
        if (StopReason is not null) return false;
        if (_elapsed.Elapsed >= WallTimeLimit)
        {
            Stop("wall_clock");
            return false;
        }
        while (true)
        {
            if (StopReason is not null) return false;
            var current = Volatile.Read(ref _attemptCount);
            if (current >= _maxAttempts)
            {
                Stop("attempt_cap");
                return false;
            }
            if (Interlocked.CompareExchange(ref _attemptCount, current + 1, current) != current) continue;
            _attemptedIds.TryAdd(tmdbId, 0);
            if (current + 1 == _maxAttempts) Stop("attempt_cap");
            return true;
        }
    }

    private void Stop(string reason)
    {
        if (Interlocked.CompareExchange(ref _stopReason, reason, null) is null)
            _stopScheduling.Cancel();
    }

    public void Dispose()
    {
        _deadline.Dispose();
        _stopScheduling.Dispose();
    }
}

public sealed class TmdbDetailsClient
{
    private readonly HttpClient _httpClient;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TmdbAttemptGuard? _attemptGuard;

    public TmdbDetailsClient(HttpClient httpClient, string bearerToken,
        Func<TimeSpan, CancellationToken, Task>? delay = null, TmdbAttemptGuard? attemptGuard = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        if (string.IsNullOrWhiteSpace(bearerToken)) throw new ArgumentException("TMDB bearer token is required.", nameof(bearerToken));
        _httpClient.BaseAddress = new Uri("https://api.themoviedb.org");
        _httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearerToken);
        _delay = delay ?? ((duration, token) => Task.Delay(duration, token));
        _attemptGuard = attemptGuard;
    }

    public async Task<TmdbDetails> GetAsync(int tmdbId, CancellationToken cancellationToken)
    {
        if (tmdbId <= 0) throw new ArgumentOutOfRangeException(nameof(tmdbId));
        for (var attempt = 1; ; attempt++)
        {
            if (_attemptGuard is not null && !_attemptGuard.TryBeginAttempt(tmdbId))
                throw new TmdbRequestException("TMDB run safety limit reached before the next HTTP attempt.", failureClass: _attemptGuard.StopReason ?? "attempt_cap");
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
                if (_attemptGuard?.IsStopped == true)
                    throw new TmdbRequestException("TMDB run safety limit stopped retry scheduling.", failureClass: _attemptGuard.StopReason!);
                if (attempt == 3) throw new TmdbRequestException("TMDB request timed out after three attempts.", failureClass: "timeout");
                await DelayWithinRun(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException exception)
            {
                if (attempt == 3) throw new TmdbRequestException("TMDB network request failed after three attempts.", exception, "network");
                await DelayWithinRun(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
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
        await DelayWithinRun(wait.Value, token).ConfigureAwait(false);
    }

    private async Task DelayWithinRun(TimeSpan delay, CancellationToken cancellationToken)
    {
        if (_attemptGuard is null)
        {
            await _delay(delay, cancellationToken).ConfigureAwait(false);
            return;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _attemptGuard.StopSchedulingToken);
        await _delay(delay, linked.Token).ConfigureAwait(false);
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
                if (runtimeProperty.ValueKind != JsonValueKind.Number || !runtimeProperty.TryGetInt32(out var value) || value < 0)
                    throw new TmdbRequestException("TMDB runtime is invalid.", failureClass: "invalid_response");
                if (value > 0) runtime = value;
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
        if (property.ValueKind != JsonValueKind.String) throw new TmdbRequestException($"TMDB {name} is invalid.", failureClass: "invalid_response");
        return property.GetString();
    }
}

public sealed class TmdbRequestException : Exception
{
    public string FailureClass { get; }
    public TmdbRequestException(string message, Exception? inner = null, string failureClass = "transient") : base(message, inner) => FailureClass = failureClass;
}
public sealed class TmdbConfigurationException(string message) : Exception(message);
