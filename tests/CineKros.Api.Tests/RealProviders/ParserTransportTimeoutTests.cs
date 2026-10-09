using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using CineKros.Api.RealProviders;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CineKros.Api.Tests.RealProviders;

[TestClass]
public sealed class ParserTransportTimeoutTests
{
    [TestMethod]
    [Timeout(30000)]
    public async Task LanguageAwareModeAcceptsValidV5ResponseAfterElevenSeconds()
    {
        using var client = NewClient(new DelayedResponseHandler(TimeSpan.FromSeconds(11), V5Envelope));
        var parser = new GeminiRealQueryParser(client, "test-key", "v5 prompt", ReadV5Schema(), new RealParsedQueryValidator(), isDevelopment: true, languageAware: true);

        var evidence = await parser.ParseWithEvidenceAsync("sr", "филм", CancellationToken.None);

        Assert.AreEqual("query", evidence.Result.Type);
        Assert.AreEqual("match", evidence.Result.LanguageCheck);
        Assert.IsNotNull(evidence.Result.Query);
        Assert.AreEqual("филмови", evidence.Result.Query.SemanticQuery);
        Assert.IsTrue(evidence.ProviderDuration >= TimeSpan.FromSeconds(10), $"Expected the fake transport to exceed the old 10-second deadline; actual duration was {evidence.ProviderDuration}.");
        using var checklist = JsonDocument.Parse(evidence.ProviderChecklistJson);
        Assert.AreEqual("query", checklist.RootElement.GetProperty("checklist").GetProperty("type").GetString());
    }

    [TestMethod]
    [Timeout(20000)]
    public async Task LegacyModeStillCancelsAtTenSeconds()
    {
        using var client = NewClient(new DelayedResponseHandler(TimeSpan.FromSeconds(11), V4Envelope));
        var parser = new GeminiRealQueryParser(client, "test-key", "v4 prompt", "{}", new RealParsedQueryValidator());
        var stopwatch = Stopwatch.StartNew();

        var error = await Assert.ThrowsExactlyAsync<RealProviderException>(() => parser.ParseAsync("en", "quiet", CancellationToken.None));
        stopwatch.Stop();

        Assert.AreEqual("PROVIDER_UNAVAILABLE", error.Code);
        Assert.IsTrue(stopwatch.Elapsed >= TimeSpan.FromSeconds(9), $"Legacy request ended before its 10-second adapter timeout: {stopwatch.Elapsed}.");
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(11), $"Legacy timeout exceeded the delayed fake response boundary: {stopwatch.Elapsed}.");
    }

    [TestMethod]
    [Timeout(5000)]
    public async Task CallerCancellationStillPropagatesImmediatelyInLanguageAwareMode()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = NewClient(new DelayedResponseHandler(TimeSpan.FromSeconds(30), V5Envelope, requestStarted));
        var parser = new GeminiRealQueryParser(client, "test-key", "v5 prompt", ReadV5Schema(), new RealParsedQueryValidator(), languageAware: true);
        using var callerCancellation = new CancellationTokenSource();
        var stopwatch = Stopwatch.StartNew();
        var parseTask = parser.ParseAsync("sr", "филм", callerCancellation.Token);

        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        callerCancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => parseTask);
        stopwatch.Stop();

        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Caller cancellation was not propagated promptly: {stopwatch.Elapsed}.");
    }

    private static HttpClient NewClient(HttpMessageHandler handler) => new(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };

    private static string ReadV5Schema()
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "src/backend/CineKros.Api/RealProviders/query-parser-v5.schema.json");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        Assert.Fail("Could not locate the v5 parser schema.");
        throw new InvalidOperationException("Unreachable");
    }

    private static HttpResponseMessage V5Envelope() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(Envelope("""{"type":"query","languageCheck":"match","query":{"year":{"status":"absent","min":null,"max":null},"runtime":{"status":"absent","min":null,"max":null},"genres":{"status":"absent","all":[],"any":[]},"rating":{"status":"absent","value":null,"operator":null,"scale":null},"originalLanguage":{"status":"absent","value":null},"semanticQuery":"филмови"},"alertCode":null}"""), Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage V4Envelope() => new(HttpStatusCode.OK)
    {
        Content = new StringContent(Envelope("""{"type":"query","query":{"year":{"status":"absent","min":null,"max":null},"runtime":{"status":"absent","min":null,"max":null},"genres":{"status":"absent","all":[],"any":[]},"rating":{"status":"absent","value":null,"operator":null,"scale":null},"originalLanguage":{"status":"absent","value":null},"semanticQuery":"quiet"},"alertCode":null}"""), Encoding.UTF8, "application/json")
    };

    private static string Envelope(string text) => JsonSerializer.Serialize(new { candidates = new[] { new { content = new { parts = new[] { new { text } } } } } });

    private sealed class DelayedResponseHandler(TimeSpan delay, Func<HttpResponseMessage> responseFactory, TaskCompletionSource? requestStarted = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requestStarted?.TrySetResult();
            await Task.Delay(delay, cancellationToken);
            return responseFactory();
        }
    }
}
