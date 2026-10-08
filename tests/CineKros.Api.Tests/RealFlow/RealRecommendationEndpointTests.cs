using System.Net;
using System.Text;
using System.Text.Json;
using CineKros.Api;
using CineKros.Api.Database;
using CineKros.Api.RealFlow;
using CineKros.Api.RealProviders;
using CineKros.Api.Search;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CineKros.Api.Tests.RealFlow;

[TestClass]
public sealed class RealRecommendationEndpointTests
{
    [TestMethod]
    public async Task RealHttpEndpointKeepsV2EnvelopeAndMapsNoPartialAndFullResults()
    {
        foreach (var count in new[] { 0, 1, 9, 10 })
        {
            var parser = new EndpointParser();
            var embedding = new EndpointEmbedding();
            var search = new EndpointSearch(Enumerable.Range(1, count)
                .Select(id => new FilteredMovie(id, $"Film {id}", 2001, id.ToString("0000000"), $"/poster-{id}.jpg", null, null)).ToArray());
            await using var app = await BuildApp(parser, embedding, search);
            using var client = app.GetTestClient();
            using var response = await Post(client, "en", "quiet mystery");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (count == 0)
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("alert", json.RootElement.GetProperty("type").GetString());
                Assert.AreEqual("NO_RESULTS", json.RootElement.GetProperty("alert").GetProperty("code").GetString());
            }
            else
            {
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
                Assert.AreEqual("movies", json.RootElement.GetProperty("type").GetString());
                var movies = json.RootElement.GetProperty("movies");
                Assert.AreEqual(count, movies.GetArrayLength());
                Assert.AreEqual(count, json.RootElement.GetProperty("meta").GetProperty("count").GetInt32());
                Assert.AreEqual(count < 10, json.RootElement.GetProperty("meta").GetProperty("partial").GetBoolean());
                Assert.AreEqual("https://www.imdb.com/title/tt0000001/", movies[0].GetProperty("imdbUrl").GetString());
                Assert.AreEqual("https://image.tmdb.org/t/p/w500/poster-1.jpg", movies[0].GetProperty("posterUrl").GetString());
                Assert.AreEqual(3, json.RootElement.EnumerateObject().Count());
            }
            Assert.AreEqual(1, parser.Calls);
            Assert.AreEqual(1, embedding.Calls);
            Assert.AreEqual(1, search.HybridCalls);
        }
    }

    [TestMethod]
    public async Task InvalidLocalHttpRequestCallsNoProviderAndAlertIsLocalized()
    {
        var parser = new EndpointParser();
        var embedding = new EndpointEmbedding();
        var search = new EndpointSearch([]);
        await using var app = await BuildApp(parser, embedding, search);
        using var client = app.GetTestClient();
        using var invalid = new StringContent("{\"language\":\"sr\",\"message\":\"x\"}", Encoding.UTF8, "application/json");
        using var response = await client.PostAsync("/api/recommendations", invalid);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual("INVALID_REQUEST", json.RootElement.GetProperty("alert").GetProperty("code").GetString());
        Assert.AreEqual("Unesi ispravan zahtev za filmove.", json.RootElement.GetProperty("alert").GetProperty("message").GetString());
        Assert.AreEqual(0, parser.Calls);
        Assert.AreEqual(0, embedding.Calls);
        Assert.AreEqual(0, search.TotalCalls);
    }

    [TestMethod]
    public async Task InvalidSelectedLanguageIsRejectedBeforeParser()
    {
        var parser = new EndpointParser();
        var embedding = new EndpointEmbedding();
        var search = new EndpointSearch([]);
        await using var app = await BuildApp(parser, embedding, search);
        using var client = app.GetTestClient();
        using var response = await client.PostAsync("/api/recommendations", new StringContent("{\"language\":\"fr\",\"message\":\"un film\"}", Encoding.UTF8, "application/json"));
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual("INVALID_REQUEST", json.RootElement.GetProperty("alert").GetProperty("code").GetString());
        Assert.AreEqual(0, parser.Calls);
        Assert.AreEqual(0, embedding.Calls);
        Assert.AreEqual(0, search.TotalCalls);
    }

    [TestMethod]
    public async Task LanguageMismatchUses422LocalizedAlertAndStopsBeforeRetrieval()
    {
        var parser = new EndpointParser(new RealParserResult("alert", AlertCode: ApiErrorCodes.LanguageMismatch, LanguageCheck: "mismatch"));
        var embedding = new EndpointEmbedding();
        var search = new EndpointSearch([]);
        await using var app = await BuildApp(parser, embedding, search, languageAwarePoc: true);
        using var client = app.GetTestClient();
        using var response = await Post(client, "en", "Un film calme");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.AreEqual((HttpStatusCode)422, response.StatusCode);
        Assert.AreEqual("LANGUAGE_MISMATCH", json.RootElement.GetProperty("alert").GetProperty("code").GetString());
        Assert.AreEqual("The query is not in the selected language. Change the language or rephrase your query.", json.RootElement.GetProperty("alert").GetProperty("message").GetString());
        Assert.AreEqual(1, parser.Calls);
        Assert.AreEqual(0, embedding.Calls);
        Assert.AreEqual(0, search.PreflightCalls);
        Assert.AreEqual(0, search.TotalCalls);
    }

    private static async Task<WebApplication> BuildApp(EndpointParser parser, EndpointEmbedding embedding, EndpointSearch search, bool languageAwarePoc = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(new RealRecommendationService(parser, new RealParsedQueryValidator(), embedding, search, languageAwarePoc));
        var app = builder.Build();
        app.Run(async context =>
        {
            if (context.Request.Path != "/api/recommendations" || !HttpMethods.IsPost(context.Request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            var result = await RealRecommendationEndpoint.HandleAsync(context,
                context.RequestServices.GetRequiredService<RealRecommendationService>(),
                context.RequestServices.GetRequiredService<ILoggerFactory>());
            await result.ExecuteAsync(context);
        });
        await app.StartAsync();
        return app;
    }

    private static Task<HttpResponseMessage> Post(HttpClient client, string language, string message) => client.PostAsync("/api/recommendations", new StringContent(JsonSerializer.Serialize(new { language, message }), Encoding.UTF8, "application/json"));

    private sealed class EndpointParser(RealParserResult? result = null) : IRealQueryParser
    {
        public int Calls { get; private set; }
        public Task<RealParserResult> ParseAsync(string language, string message, CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result ?? new RealParserResult("query", new RealParsedQuery(new RealHardFilters(YearMin: 2000), "quiet mystery")));
        }
    }

    private sealed class EndpointEmbedding : IRealQueryEmbeddingProvider
    {
        public int Calls { get; private set; }
        public Task<float[]> EmbedQueryAsync(string semanticQuery, CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            var vector = new float[768];
            vector[0] = 1;
            return Task.FromResult(vector);
        }
    }

    private sealed class EndpointSearch(IReadOnlyList<FilteredMovie> results) : IRealMovieSearch
    {
        public int HybridCalls { get; private set; }
        public int PreflightCalls { get; private set; }
        public int LanguageHybridCalls { get; private set; }
        public int TotalCalls => HybridCalls + LanguageHybridCalls;
        public Task<IReadOnlyList<FilteredMovie>> SearchHybridAsync(RealHardFilters hardFilters, float[] queryVector, CancellationToken cancellationToken)
        {
            HybridCalls++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(results);
        }
        public Task EnsureSelectedLanguageReadyAsync(SearchLanguage language, CancellationToken cancellationToken)
        {
            PreflightCalls++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<FilteredMovie>> SearchHybridAsync(RealHardFilters hardFilters, float[] queryVector, SearchLanguage language, CancellationToken cancellationToken)
        {
            LanguageHybridCalls++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(results);
        }
        public Task<IReadOnlyList<FilteredMovie>> SearchHardOnlyAsync(RealHardFilters hardFilters, CancellationToken cancellationToken) => throw new InvalidOperationException("Unexpected hard-only branch.");
    }
}
