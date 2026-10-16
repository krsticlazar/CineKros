using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CineKros.Api.RealProviders;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CineKros.Api.Tests.RealProviders;

[TestClass]
public sealed class ParserV6AdapterTests
{
    [TestMethod]
    [DataRow("en", "films starring Brad Pitt")]
    [DataRow("sr", "filmovi sa Brad Pittom")]
    [DataRow("sr", "Филмови са Бредом Питом")]
    public async Task ExplicitV6RoutesFakeOriginalNameProposalAndSendsSelectedLanguage(string language, string input)
    {
        // The fake supplies identity interpretation. This proves the transport/validator path,
        // not that Gemini resolves Serbian inflection or Cyrillic correctly in a live call.
        string? requestBody = null;
        var calls = 0;
        using var client = new HttpClient(new FakeHandler(async (request, ct) =>
        {
            calls++;
            requestBody = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Envelope(ParserV6ValidatorTests.Query(["Brad Pitt"])), Encoding.UTF8, "application/json") };
        }));
        var parser = new GeminiRealQueryParser(client, "fixture-key", "v6 prompt", ParserV6PromptContractTests.ReadAsset("query-parser-v6.schema.json"),
            new RealParsedQueryValidator(new ActorNameResolver(["Brad Pitt"])), languageAware: true, parserProfile: QueryParserProfile.ActorV6);
        var result = await parser.ParseAsync(language, input, CancellationToken.None);
        Assert.AreEqual(1, calls);
        Assert.AreEqual("Brad Pitt", result.Query!.HardFilters.Actors!.All![0]);
        Assert.IsNull(result.Query.SemanticQuery);
        using var body = JsonDocument.Parse(requestBody!);
        using var user = JsonDocument.Parse(body.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()!);
        Assert.AreEqual(language, user.RootElement.GetProperty("language").GetString());
        Assert.AreEqual(input, user.RootElement.GetProperty("message").GetString());
        Assert.IsTrue(body.RootElement.GetProperty("generationConfig").GetProperty("responseSchema").GetProperty("properties").GetProperty("query").GetProperty("properties").TryGetProperty("actors", out _));
    }

    [TestMethod]
    public void RejectsInvalidOrMixedProfileSelectionsBeforeTransport()
    {
        using var client = new HttpClient(new FakeHandler((_, _) => throw new AssertFailedException("Transport must not run.")));
        foreach (var (aware, profile) in new[]
        {
            (false, QueryParserProfile.ActorV6), (false, QueryParserProfile.LanguageAwareV5),
            (true, QueryParserProfile.LegacyV4), (true, (QueryParserProfile)42)
        }) Assert.ThrowsExactly<ArgumentException>(() => new GeminiRealQueryParser(client, "fixture-key", "", "{}", new RealParsedQueryValidator(), languageAware: aware, parserProfile: profile));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ProviderFailureReportsOnlyStatusAndParsedRetryAfterWithoutRetrying(bool dateHeader)
    {
        var logger = new CaptureLogger();
        var calls = 0;
        using var client = new HttpClient(new FakeHandler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("private provider payload") };
            response.Headers.RetryAfter = dateHeader
                ? new RetryConditionHeaderValue(new DateTimeOffset(2026, 10, 16, 12, 30, 0, TimeSpan.Zero))
                : new RetryConditionHeaderValue(TimeSpan.FromSeconds(12));
            return Task.FromResult(response);
        }));
        var parser = new GeminiRealQueryParser(client, "private-test-key", "", "{}", new RealParsedQueryValidator(), logger, true, true, QueryParserProfile.ActorV6);
        var error = await Assert.ThrowsExactlyAsync<RealProviderException>(() => parser.ParseAsync("en", "private user message", CancellationToken.None));
        Assert.AreEqual("PROVIDER_UNAVAILABLE", error.Code);
        Assert.AreEqual(1, calls);
        StringAssert.Contains(logger.Text, dateHeader ? "HTTP status 429; Retry-After UTC=2026-10-16T12:30:00.0000000Z" : "HTTP status 429; Retry-After seconds=12");
        Assert.IsFalse(logger.Text.Contains("private", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ExplicitV6RejectsV5ChecklistAndNeverRepairsValidSchemaFailures()
    {
        var calls = 0;
        var output = ParserV6ValidatorTests.Query(["Brad Pitt"]).Replace("\"all\":[\"Brad Pitt\"]", "\"all\":[null]");
        using var client = new HttpClient(new FakeHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Envelope(output)) });
        }));
        var parser = new GeminiRealQueryParser(client, "fixture-key", "", "{}", new RealParsedQueryValidator(new ActorNameResolver(["Brad Pitt"])), languageAware: true, parserProfile: QueryParserProfile.ActorV6);
        Assert.AreEqual("PARSER_INVALID_RESPONSE", (await Assert.ThrowsExactlyAsync<RealProviderException>(() => parser.ParseAsync("en", "actor", CancellationToken.None))).Code);
        Assert.AreEqual(1, calls);
    }

    private static string Envelope(string text) => JsonSerializer.Serialize(new { candidates = new[] { new { content = new { parts = new[] { new { text } } } } } });
    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
    private sealed class CaptureLogger : ILogger<GeminiRealQueryParser>
    {
        public string Text { get; private set; } = "";
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Text += formatter(state, exception);
    }
}
