using System.Net;
using System.Text;
using System.Text.Json;
using CineKros.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CineKros.Api.Tests;

[TestClass]
public sealed class RecommendationEndpointTests
{
    [TestMethod]
    public async Task FakeHttpFlowReturnsTenFilteredCardsAndThreePartialCards()
    {
        await using var factory = new WebApplicationFactory<Program>(); using var client = factory.CreateClient();
        using var ten = await Post(client, "sr", "Hoću mračan SF posle 2010. do dva sata.");
        using var tenJson = JsonDocument.Parse(await ten.Content.ReadAsStringAsync());
        Assert.AreEqual(HttpStatusCode.OK, ten.StatusCode); Assert.AreEqual("movies", tenJson.RootElement.GetProperty("type").GetString());
        Assert.AreEqual(10, tenJson.RootElement.GetProperty("meta").GetProperty("count").GetInt32());
        Assert.IsFalse(tenJson.RootElement.GetProperty("meta").GetProperty("partial").GetBoolean());
        var tenCards = tenJson.RootElement.GetProperty("movies").EnumerateArray().ToArray();
        Assert.AreEqual(10, tenCards.Select(card => card.GetProperty("imdbUrl").GetString()).Distinct(StringComparer.Ordinal).Count());
        Assert.IsTrue(tenCards.All(card => card.GetProperty("year").GetInt32() >= 2010));
        Assert.IsFalse(tenJson.RootElement.ToString().Contains("hardFilters", StringComparison.Ordinal));
        Assert.IsFalse(tenJson.RootElement.ToString().Contains("posterPath", StringComparison.Ordinal));
        Assert.AreEqual(10, tenJson.RootElement.GetProperty("movies").GetArrayLength());
        using var three = await Post(client, "en", "quiet mystery"); using var threeJson = JsonDocument.Parse(await three.Content.ReadAsStringAsync());
        Assert.AreEqual(3, threeJson.RootElement.GetProperty("meta").GetProperty("count").GetInt32());
        Assert.IsTrue(threeJson.RootElement.GetProperty("meta").GetProperty("partial").GetBoolean());
    }

    [TestMethod]
    public async Task AlertsAndTechnicalFailuresUseExactV2Envelopes()
    {
        await using var factory = new WebApplicationFactory<Program>(); using var client = factory.CreateClient();
        var cases = new[] { ("sr", "unclear movie", "QUERY_UNCLEAR", "Napiši malo jasnije kakav film tražiš."), ("en", "not a movie request", "NOT_MOVIE_REQUEST", "Enter a movie recommendation request."), ("en", "movie with unsupported actor filter", "UNSUPPORTED_REQUEST", "We cannot reliably check one required condition yet. Please revise your request.") };
        foreach (var (lang, msg, code, copy) in cases) { using var r = await Post(client, lang, msg); using var j = JsonDocument.Parse(await r.Content.ReadAsStringAsync()); Assert.AreEqual(HttpStatusCode.UnprocessableEntity, r.StatusCode); Assert.AreEqual("alert", j.RootElement.GetProperty("type").GetString()); Assert.AreEqual(code, j.RootElement.GetProperty("alert").GetProperty("code").GetString()); Assert.AreEqual(copy, j.RootElement.GetProperty("alert").GetProperty("message").GetString()); }
        using var zero = await Post(client, "en", "no matching films"); using var zj = JsonDocument.Parse(await zero.Content.ReadAsStringAsync()); Assert.AreEqual(HttpStatusCode.OK, zero.StatusCode); Assert.AreEqual("NO_RESULTS", zj.RootElement.GetProperty("alert").GetProperty("code").GetString());
        using var parserDown = await Post(client, "en", "simulate provider failure"); await AssertTechnical(parserDown, HttpStatusCode.ServiceUnavailable, "PROVIDER_UNAVAILABLE");
        using var searchDown = await Post(client, "en", "simulate search failure"); await AssertTechnical(searchDown, HttpStatusCode.ServiceUnavailable, "SEARCH_UNAVAILABLE");
    }

    [TestMethod]
    public async Task InvalidLocalInputsAndV1RequestNeverCallParserOrSearch()
    {
        foreach (var msg in new[] { "", "x", "!!!", new string('a', 20), new string('x', 501) })
        {
            var parser = new SpyParser(); var search = new SpySearch(); await using var factory = new SpyFactory(parser, search); using var client = factory.CreateClient();
            using var response = await Post(client, "en", msg); Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode); Assert.AreEqual(0, parser.Count); Assert.AreEqual(0, search.Count);
        }
        var p = new SpyParser(); var s = new SpySearch(); await using var f = new SpyFactory(p, s); using var c = f.CreateClient();
        using var old = await c.PostAsync("/api/recommendations", new StringContent("{\"query\":\"film\",\"locale\":\"en\"}", Encoding.UTF8, "application/json"));
        Assert.AreEqual(HttpStatusCode.BadRequest, old.StatusCode); Assert.AreEqual(0, p.Count); Assert.AreEqual(0, s.Count);
    }

    [TestMethod]
    public async Task InvalidUsabilityPatternsReturnLocalizedAlertBeforeParserAndSearch()
    {
        var invalidMessages = new[] { "", "   \u00a0", "!!!!!!", "※※※", "aaaaaaaaaaaaaaaa", "asasasasasasasas", new string('x', 501) };
        foreach (var language in new[] { "sr", "en" })
        foreach (var message in invalidMessages)
        {
            var parser = new SpyParser(); var search = new SpySearch(); await using var factory = new SpyFactory(parser, search); using var client = factory.CreateClient();
            using var response = await Post(client, language, message); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, $"{language}: {message}");
            Assert.AreEqual("INVALID_REQUEST", json.RootElement.GetProperty("alert").GetProperty("code").GetString());
            Assert.AreEqual(language == "en" ? "Enter a valid movie request." : "Unesi ispravan zahtev za filmove.", json.RootElement.GetProperty("alert").GetProperty("message").GetString());
            Assert.AreEqual(0, parser.Count); Assert.AreEqual(0, search.Count);
        }
    }

    [TestMethod]
    public async Task CheapUsabilityAllowsShortRealRequestsAndParserGetsPromptOnce()
    {
        var parser = new SpyParser(); var search = new SpySearch(); await using var factory = new SpyFactory(parser, search); using var client = factory.CreateClient();
        foreach (var text in new[] { "horor", "SF", "dark sci-fi" }) { using var response = await Post(client, "en", text); Assert.AreEqual(HttpStatusCode.OK, response.StatusCode); }
        Assert.AreEqual(3, parser.Count); Assert.AreEqual(3, search.Count);
        Assert.IsTrue(parser.Prompt!.Contains("temporary fake-only profile", StringComparison.Ordinal));
        CollectionAssert.AreEqual(new[] { "horor", "SF", "dark sci-fi" }, parser.Messages);
    }

    [TestMethod]
    public async Task InvalidLanguageAndMalformedUtf8NeverReachParser()
    {
        var parser = new SpyParser(); var search = new SpySearch(); await using var factory = new SpyFactory(parser, search); using var client = factory.CreateClient();
        using var badLanguage = await Post(client, "de", "quiet mystery"); Assert.AreEqual(HttpStatusCode.BadRequest, badLanguage.StatusCode);
        using var invalidUtf8 = new ByteArrayContent([0xC3, 0x28]); invalidUtf8.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var badEncoding = await client.PostAsync("/api/recommendations", invalidUtf8); Assert.AreEqual(HttpStatusCode.BadRequest, badEncoding.StatusCode);
        Assert.AreEqual(0, parser.Count); Assert.AreEqual(0, search.Count);
    }

    [TestMethod]
    public async Task SearchCardCountsDistinctnessUrlsAndCancellationAreValidated()
    {
        foreach (var count in new[] { 1, 9, 10, 11 })
        {
            var parser = new SpyParser(); var search = new SpySearch { Candidates = Enumerable.Range(1, count).Select(n => new SearchCandidate(n, $"Film {n}", 2000+n, 100, ["Drama"], n.ToString("0000000"), n == 1 ? "http://image.tmdb.org/t/p/a.jpg" : null)).ToArray() };
            await using var factory = new SpyFactory(parser, search); using var client = factory.CreateClient(); using var response = await Post(client, "en", "quiet mystery");
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual(Math.Min(count, 10), json.RootElement.GetProperty("movies").GetArrayLength());
            Assert.IsTrue(parser.Token.CanBeCanceled); Assert.IsTrue(search.Token.CanBeCanceled);
            Assert.AreEqual(JsonValueKind.Null, json.RootElement.GetProperty("movies")[0].GetProperty("posterUrl").ValueKind);
        }
        foreach (var candidates in new IReadOnlyList<SearchCandidate>[] { [new SearchCandidate(1,"Film",2020,90,["Drama"],"1",null),new SearchCandidate(1,"Film",2021,90,["Drama"],"2",null)], [new SearchCandidate(0,"Film",2020,90,["Drama"],"1",null)], [new SearchCandidate(1," ",2020,90,["Drama"],"1",null)], [new SearchCandidate(1,"Film",2020,90,["Drama"],"bad-id",null)] })
        {
            var parser = new SpyParser(); var search = new SpySearch { Candidates = candidates }; await using var factory = new SpyFactory(parser, search); using var client = factory.CreateClient(); using var response = await Post(client,"en","quiet mystery");
            await AssertTechnical(response,HttpStatusCode.InternalServerError,"INTERNAL_ERROR");
        }
    }

    [TestMethod]
    public async Task ValidationAndParserShapeFailuresAreSanitized()
    {
        var parser = new SpyParser { Result = new ParserResult("query", new ParsedQuery(new HardFilters(Genres: ["invented"]), "quiet mystery")), OverrideResult = true };
        var search = new SpySearch(); await using var factory = new SpyFactory(parser, search); using var client = factory.CreateClient();
        using var response = await Post(client, "en", "quiet mystery"); await AssertTechnical(response, HttpStatusCode.BadGateway, "PARSER_INVALID_RESPONSE"); Assert.AreEqual(0, search.Count);
    }

    [TestMethod]
    public async Task EveryInvalidParserOutputShapeFailsBeforeSearch()
    {
        var invalid = new ParserResult?[]
        {
            null, new("unknown"), new("alert", AlertCode: "UNKNOWN"), new("alert", new ParsedQuery(new HardFilters(), "quiet mystery"), "QUERY_UNCLEAR"),
            new("query", null), new("query", new ParsedQuery(null!, "quiet mystery")),
            new("query", new ParsedQuery(new HardFilters(), "")), new("query", new ParsedQuery(new HardFilters(), "зашто")),
            new("query", new ParsedQuery(new HardFilters(RuntimeMax: 0), "quiet mystery")),
            new("query", new ParsedQuery(new HardFilters(Genres: []), "quiet mystery")),
            new("query", new ParsedQuery(new HardFilters(Genres: ["Drama", "Comedy"]), "quiet mystery")),
            new("query", new ParsedQuery(new HardFilters(Genres: ["(no genres listed)"]), "quiet mystery")),
            new("query", new ParsedQuery(new HardFilters(), "quiet mystery"), "QUERY_UNCLEAR"),
        };
        foreach (var result in invalid)
        {
            var parser = new SpyParser { Result = result, OverrideResult = true }; var search = new SpySearch(); await using var factory = new SpyFactory(parser, search); using var client = factory.CreateClient();
            using var response = await Post(client, "en", "quiet mystery"); await AssertTechnical(response, HttpStatusCode.BadGateway, "PARSER_INVALID_RESPONSE"); Assert.AreEqual(0, search.Count);
        }
    }

    [TestMethod]
    public async Task LocalizedBusinessAlertsCoverBothLanguages()
    {
        var mappings = new[]
        {
            ("sr", "QUERY_UNCLEAR", "Napiši malo jasnije kakav film tražiš."), ("en", "QUERY_UNCLEAR", "Describe the movie you want more clearly."),
            ("sr", "NOT_MOVIE_REQUEST", "Napiši zahtev za preporuku filma."), ("en", "NOT_MOVIE_REQUEST", "Enter a movie recommendation request."),
            ("sr", "UNSUPPORTED_REQUEST", "Jedan obavezan uslov trenutno ne možemo pouzdano da proverimo. Izmeni upit."),
            ("en", "UNSUPPORTED_REQUEST", "We cannot reliably check one required condition yet. Please revise your request."),
        };
        foreach (var (language, code, message) in mappings)
        {
            var parser = new SpyParser { Result = new ParserResult("alert", AlertCode: code), OverrideResult = true }; var search = new SpySearch(); await using var factory = new SpyFactory(parser, search); using var client = factory.CreateClient();
            using var response = await Post(client, language, "quiet mystery"); using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual(HttpStatusCode.UnprocessableEntity, response.StatusCode); Assert.AreEqual(code, json.RootElement.GetProperty("alert").GetProperty("code").GetString());
            Assert.AreEqual(message, json.RootElement.GetProperty("alert").GetProperty("message").GetString()); Assert.AreEqual(0, search.Count);
        }
    }

    [TestMethod]
    public async Task FakeSearchEnforcesEachFilterAndNullMetadataIndependently()
    {
        var search = new FakeMovieSearch();
        var cases = new[]
        {
            (new HardFilters(YearMin: 2010), new long[] { 90, 93 }),
            (new HardFilters(RuntimeMax: 120), new long[] { 91, 94 }),
            (new HardFilters(Genres: ["Sci-Fi"]), new long[] { 92, 95 }),
        };
        foreach (var (filter, excludedIds) in cases)
        {
            var results = await search.SearchAsync(new ParsedQuery(filter, "dark atmospheric science fiction"), CancellationToken.None);
            Assert.IsFalse(excludedIds.Any(id => results.Any(movie => movie.MovieId == id)), $"{filter}");
            Assert.IsTrue(results.All(movie => filter.YearMin is null || movie.Year >= filter.YearMin));
            Assert.IsTrue(results.All(movie => filter.RuntimeMax is null || movie.Runtime <= filter.RuntimeMax));
            Assert.IsTrue(results.All(movie => filter.Genres is null || movie.Genres?.Contains(filter.Genres[0], StringComparer.Ordinal) == true));
        }
    }

    [TestMethod]
    public async Task RequestTransportAndUnicodeRulesRemainStrict()
    {
        var invalid = new (byte[] Body, string? ContentType)[]
        {
            (Encoding.UTF8.GetBytes("{"), "application/json"),
            (Encoding.UTF8.GetBytes("{\"language\":\"en\",\"language\":\"sr\",\"message\":\"quiet mystery\"}"), "application/json"),
            (Encoding.UTF8.GetBytes("{\"language\":\"en\",\"message\":\"quiet mystery\",\"extra\":1}"), "application/json"),
            (Encoding.UTF8.GetBytes("{\"language\":\"en\",\"message\":null}"), "application/json"),
            (Encoding.ASCII.GetBytes("{\"language\":\"en\",\"message\":\"\\uD800\"}"), "application/json"),
            (Encoding.UTF8.GetBytes("{\"language\":\"en\",\"message\":\"quiet mystery\"}"), "application/json; charset=iso-8859-1"),
            (Encoding.UTF8.GetBytes("{\"language\":\"en\",\"message\":\"quiet mystery\"}"), "application/json; charset=utf-8; profile=test"),
            (Encoding.UTF8.GetBytes("{\"language\":\"en\",\"message\":\"quiet mystery\"}"), "text/plain"),
        };
        foreach (var (body, contentType) in invalid)
        {
            var p = new SpyParser(); var s = new SpySearch(); await using var f = new SpyFactory(p,s); using var c=f.CreateClient(); using var content=new ByteArrayContent(body);
            if (contentType is not null) content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
            using var r=await c.PostAsync("/api/recommendations",content); Assert.AreEqual(HttpStatusCode.BadRequest,r.StatusCode,contentType + ": " + Encoding.UTF8.GetString(body)); Assert.AreEqual(0,p.Count);
        }
        var parser = new SpyParser(); var search = new SpySearch(); await using var factory = new SpyFactory(parser,search); using var client=factory.CreateClient();
        using var accepted=await Post(client,"en","  dark sci-fi  "); Assert.AreEqual(HttpStatusCode.OK,accepted.StatusCode); Assert.AreEqual("dark sci-fi",parser.Input!.Message);
        using var tooLong=await Post(client,"en",new string('x',501)); Assert.AreEqual(HttpStatusCode.BadRequest,tooLong.StatusCode);
    }

    [TestMethod]
    public async Task InvalidRequestUsesValidLanguageWhenAvailableButDefaultsToSerbianOtherwise()
    {
        var cases = new[]
        {
            ("{\"language\":\"en\",\"message\":\"x\"}", "Enter a valid movie request."),
            ("{\"language\":\"sr\",\"message\":\"x\"}", "Unesi ispravan zahtev za filmove."),
            ("{\"message\":\"x\"}", "Unesi ispravan zahtev za filmove."),
            ("{\"language\":\"de\",\"message\":\"x\"}", "Unesi ispravan zahtev za filmove."),
            ("{\"language\":\"en\",\"message\":\"x\",\"extra\":true}", "Enter a valid movie request."),
        };
        foreach (var (body, message) in cases)
        {
            var p = new SpyParser(); var s = new SpySearch(); await using var f = new SpyFactory(p,s); using var c=f.CreateClient();
            using var content=new StringContent(body,Encoding.UTF8,"application/json"); using var r=await c.PostAsync("/api/recommendations",content); using var json=JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            Assert.AreEqual(HttpStatusCode.BadRequest,r.StatusCode); Assert.AreEqual(message,json.RootElement.GetProperty("alert").GetProperty("message").GetString()); Assert.AreEqual(0,p.Count);
        }
    }

    [TestMethod]
    public async Task ThirtyFirstRequestIsRateLimitedBeforeParser()
    {
        var parser = new SpyParser(); var search = new SpySearch(); await using var factory = new SpyFactory(parser,search); using var client=factory.CreateClient();
        for (var i=0;i<30;i++) { using var r=await Post(client,"en","quiet mystery"); Assert.AreEqual(HttpStatusCode.OK,r.StatusCode); }
        using var rejected=await Post(client,"en","quiet mystery"); await AssertTechnical(rejected,HttpStatusCode.TooManyRequests,"RATE_LIMITED"); Assert.AreEqual(30,parser.Count); Assert.AreEqual(30,search.Count);
    }

    private static Task<HttpResponseMessage> Post(HttpClient client,string language,string message) => client.PostAsync("/api/recommendations",new StringContent(JsonSerializer.Serialize(new { language, message }),Encoding.UTF8,"application/json"));
    private static async Task AssertTechnical(HttpResponseMessage r,HttpStatusCode status,string code) { Assert.AreEqual(status,r.StatusCode); using var j=JsonDocument.Parse(await r.Content.ReadAsStringAsync()); Assert.AreEqual(code,j.RootElement.GetProperty("error").GetProperty("code").GetString()); Assert.AreEqual(1,j.RootElement.EnumerateObject().Count()); }
    private sealed class SpyFactory(SpyParser parser, SpySearch search) : WebApplicationFactory<Program> { protected override void ConfigureWebHost(IWebHostBuilder b) => b.ConfigureServices(s=>{s.RemoveAll<IQueryParser>();s.RemoveAll<IMovieSearch>();s.AddSingleton<IQueryParser>(parser);s.AddSingleton<IMovieSearch>(search);}); }
    private sealed class SpyParser : IQueryParser { public int Count; public ParserInput? Input; public ParserResult? Result; public bool OverrideResult; public string? Prompt; public List<string> Messages = []; public CancellationToken Token; public Task<ParserResult> ParseAsync(string systemInstruction,ParserInput input,CancellationToken token) { Count++; Input=input; Prompt=systemInstruction; Token=token; Messages.Add(input.Message); return Task.FromResult(OverrideResult ? Result! : new ParserResult("query",new ParsedQuery(new HardFilters(),"quiet mystery"))); } }
    private sealed class SpySearch : IMovieSearch { public int Count; public IReadOnlyList<SearchCandidate> Candidates = [new SearchCandidate(1,"Film",2020,100,["Drama"],"0000001",null)]; public CancellationToken Token; public Task<IReadOnlyList<SearchCandidate>> SearchAsync(ParsedQuery query,CancellationToken token) { Count++; Token=token; return Task.FromResult(Candidates); } }
}
