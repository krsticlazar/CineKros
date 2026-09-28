using System.Net;
using System.Text;
using System.Text.Json;
using CineKros.Api.RealProviders;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CineKros.Api.Tests.RealProviders;

[TestClass]
public sealed class RealProviderAdapterTests
{
    [TestMethod]
    public async Task ParserSendsOneRequestWithOnlyLanguageMessageInstructionAndChecklistSchema()
    {
        string? bodyText = null;
        var calls = 0;
        using var client = new HttpClient(new FakeHandler((request, token) =>
        {
            calls++;
            bodyText = request.Content!.ReadAsStringAsync(token).GetAwaiter().GetResult();
            return Task.FromResult(JsonResponse(ParserEnvelope(QueryJson(semantic: "dark science fiction"))));
        }));
        var parser = new GeminiRealQueryParser(client, "test-key", "fixed v3 instruction", ReadSchema(), new RealParsedQueryValidator());
        var result = await parser.ParseAsync("sr", "mračan SF", CancellationToken.None);
        Assert.AreEqual("query", result.Type);
        Assert.AreEqual(1, calls);
        using var body = JsonDocument.Parse(bodyText!);
        Assert.AreEqual(3, body.RootElement.EnumerateObject().Count());
        Assert.AreEqual("fixed v3 instruction", body.RootElement.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
        using var input = JsonDocument.Parse(body.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()!);
        Assert.AreEqual(2, input.RootElement.EnumerateObject().Count());
        Assert.AreEqual("sr", input.RootElement.GetProperty("language").GetString());
        Assert.AreEqual("mračan SF", input.RootElement.GetProperty("message").GetString());
        var schema = body.RootElement.GetProperty("generationConfig").GetProperty("responseSchema");
        CollectionAssert.AreEquivalent(new[] { "type", "query", "alertCode" }, schema.GetProperty("required").EnumerateArray().Select(x => x.GetString()).ToArray());
        CollectionAssert.AreEquivalent(new[] { "year", "runtime", "genres", "rating", "originalLanguage", "semanticQuery" }, schema.GetProperty("properties").GetProperty("query").GetProperty("required").EnumerateArray().Select(x => x.GetString()).ToArray());
    }

    [TestMethod]
    public async Task DevelopmentLogsValidatedChecklistEvidenceWhileProductionDoesNot()
    {
        const string providerDto = """
            {"type":"query","query":{"year":{"status":"present","min":2016,"max":null},"runtime":{"status":"present","min":null,"max":109},"genres":{"status":"present","all":["Sci-Fi"],"any":[]},"rating":{"status":"present","value":8,"operator":"gt","scale":"unspecified"},"originalLanguage":{"status":"absent","value":null},"semanticQuery":"dark"},"alertCode":null}
            """;
        var response = ParserEnvelope(providerDto);
        var devLogger = new RecordingLogger();
        using (var client = new HttpClient(new FakeHandler((_, _) => Task.FromResult(JsonResponse(response)))))
        {
            var parser = new GeminiRealQueryParser(client, "private-api-key", "fixed instruction", ReadSchema(), new RealParsedQueryValidator(), devLogger, isDevelopment: true);
            _ = await parser.ParseAsync("en", "secret original request", CancellationToken.None);
        }
        Assert.AreEqual(2, devLogger.Messages.Count);
        StringAssert.Contains(devLogger.Messages[0], "Validated Gemini provider checklist (Development)");
        StringAssert.Contains(devLogger.Messages[0], "\"status\":\"present\"");
        StringAssert.Contains(devLogger.Messages[0], "\"max\":109");
        StringAssert.Contains(devLogger.Messages[0], "\"value\":8");
        StringAssert.Contains(devLogger.Messages[0], "\"operator\":\"gt\"");
        StringAssert.Contains(devLogger.Messages[0], "\"scale\":\"unspecified\"");
        StringAssert.Contains(devLogger.Messages[0], "\"all\":[\"Sci-Fi\"]");
        Assert.IsFalse(devLogger.Messages[0].Contains("private-api-key", StringComparison.Ordinal));
        Assert.IsFalse(devLogger.Messages[0].Contains("secret original request", StringComparison.Ordinal));
        Assert.IsFalse(devLogger.Messages[0].Contains(providerDto, StringComparison.Ordinal));
        StringAssert.Contains(devLogger.Messages[1], "Validated parser DTO (Development)");
        StringAssert.Contains(devLogger.Messages[1], "\"ratingMin\":4");
        StringAssert.Contains(devLogger.Messages[1], "\"ratingOperator\":\"gt\"");

        var prodLogger = new RecordingLogger();
        using (var client = new HttpClient(new FakeHandler((_, _) => Task.FromResult(JsonResponse(response)))))
        {
            var parser = new GeminiRealQueryParser(client, "private-api-key", "fixed instruction", ReadSchema(), new RealParsedQueryValidator(), prodLogger, isDevelopment: false);
            _ = await parser.ParseAsync("en", "secret original request", CancellationToken.None);
        }
        Assert.AreEqual(0, prodLogger.Messages.Count);
    }

    [TestMethod]
    public void V4PromptExamplesAreCompleteProviderDtosAndMapToCanonicalFilters()
    {
        var prompt = File.ReadAllText(FindRepositoryFile("src/backend/CineKros.Api/RealProviders/query-parser-v4.md"));
        var project = File.ReadAllText(FindRepositoryFile("src/backend/CineKros.Api/CineKros.Api.csproj"));
        var startup = File.ReadAllText(FindRepositoryFile("src/backend/CineKros.Api/Startup/RecommendationStartup.cs"));
        StringAssert.Contains(project, "RealProviders/query-parser-v4.md");
        StringAssert.Contains(startup, "query-parser-v4.md");
        Assert.IsTrue(File.Exists(Path.Combine(AppContext.BaseDirectory, "RealProviders", "query-parser-v4.md")));
        StringAssert.Contains(prompt, "clause by clause");
        StringAssert.Contains(prompt, "under 110");
        StringAssert.Contains(prompt, "never convert, divide, round, or calculate");
        StringAssert.Contains(prompt, "all five");
        StringAssert.Contains(prompt, "positive actor/director mentions are semantic-only");
        StringAssert.Contains(prompt, "Never approximate a negative person constraint through semantic ranking");
        StringAssert.Contains(prompt, "Brad Pitt movie");
        StringAssert.Contains(prompt, "directed by Christopher Nolan");
        StringAssert.Contains(prompt, "Sci-Fi from 2000 through 2008 starring Brad Pitt");
        StringAssert.Contains(prompt, "without Brad Pitt");
        var examples = System.Text.RegularExpressions.Regex.Matches(prompt, "(?m)→ `(?<json>\\{.*\\})`");
        Assert.AreEqual(9, examples.Count);
        var validator = new RealParsedQueryValidator();
        var results = examples.Select(match => validator.Validate(match.Groups["json"].Value)).ToArray();
        Assert.AreEqual(7, results.Count(result => result.Type == "query"));
        Assert.AreEqual(2, results.Count(result => result.Type == "alert"));
        var sr = results[0].Query!;
        Assert.AreEqual(2016, sr.HardFilters.YearMin);
        Assert.AreEqual(109, sr.HardFilters.RuntimeMax);
        CollectionAssert.AreEqual(new[] { "Sci-Fi" }, sr.HardFilters.Genres!.All!.ToArray());
        Assert.AreEqual("ja", sr.HardFilters.OriginalLanguage);
        Assert.AreEqual("dark", sr.SemanticQuery);
        var en = results[1].Query!;
        CollectionAssert.AreEqual(new[] { "Drama", "Thriller" }, en.HardFilters.Genres!.Any!.ToArray());
        Assert.AreEqual(4m, en.HardFilters.RatingMin);
        Assert.AreEqual("gte", en.HardFilters.RatingOperator);
        Assert.AreEqual("moody", en.SemanticQuery);
        var srCritical = results[2].Query!;
        Assert.AreEqual(2016, srCritical.HardFilters.YearMin);
        Assert.AreEqual(109, srCritical.HardFilters.RuntimeMax);
        CollectionAssert.AreEqual(new[] { "Sci-Fi" }, srCritical.HardFilters.Genres!.All!.ToArray());
        Assert.AreEqual(3m, srCritical.HardFilters.RatingMin);
        Assert.AreEqual("gt", srCritical.HardFilters.RatingOperator);
        Assert.AreEqual("dark", srCritical.SemanticQuery);
        var explicitFive = results[3].Query!;
        Assert.AreEqual(4m, explicitFive.HardFilters.RatingMin);
        Assert.AreEqual("gt", explicitFive.HardFilters.RatingOperator);
        Assert.AreEqual("UNSUPPORTED_REQUEST", results[4].AlertCode);
        var actorOnly = results[5].Query!;
        Assert.IsFalse(RealParsedQueryValidator.HasActiveFilter(actorOnly.HardFilters));
        Assert.AreEqual("movies starring Brad Pitt", actorOnly.SemanticQuery);
        var directorOnly = results[6].Query!;
        Assert.IsFalse(RealParsedQueryValidator.HasActiveFilter(directorOnly.HardFilters));
        Assert.AreEqual("movies directed by Christopher Nolan", directorOnly.SemanticQuery);
        var combined = results[7].Query!;
        Assert.AreEqual(2000, combined.HardFilters.YearMin);
        Assert.AreEqual(2008, combined.HardFilters.YearMax);
        CollectionAssert.AreEqual(new[] { "Sci-Fi" }, combined.HardFilters.Genres!.All!.ToArray());
        Assert.AreEqual("movies starring Brad Pitt", combined.SemanticQuery);
        Assert.AreEqual("UNSUPPORTED_REQUEST", results[8].AlertCode);
    }

    [TestMethod]
    public void SchemaRequiresEveryChecklistObjectAndNullableRootInvariantFields()
    {
        using var schema = JsonDocument.Parse(ReadSchema());
        var root = schema.RootElement;
        CollectionAssert.AreEquivalent(new[] { "type", "query", "alertCode" }, Strings(root.GetProperty("required")));
        var query = root.GetProperty("properties").GetProperty("query");
        Assert.AreEqual(JsonValueKind.True, query.GetProperty("nullable").GetBoolean() ? JsonValueKind.True : JsonValueKind.False);
        CollectionAssert.AreEquivalent(new[] { "year", "runtime", "genres", "rating", "originalLanguage", "semanticQuery" }, Strings(query.GetProperty("required")));
        foreach (var name in new[] { "year", "runtime", "genres", "rating", "originalLanguage" })
        {
            var item = query.GetProperty("properties").GetProperty(name);
            CollectionAssert.Contains(Strings(item.GetProperty("required")), "status");
            CollectionAssert.Contains(Strings(item.GetProperty("properties").GetProperty("status").GetProperty("enum")), "unsupported");
        }
        CollectionAssert.AreEquivalent(new[] { "status", "value", "operator", "scale" }, Strings(query.GetProperty("properties").GetProperty("rating").GetProperty("required")));
    }

    [TestMethod]
    public void RatingNormalizationMatrixPreservesRawOperatorAndRejectsInvalidRanges()
    {
        var valid = new (decimal Value, string Scale, string Operator, decimal Expected)[]
        {
            (1m,"unspecified","gte",1m),(3m,"unspecified","gte",3m),(4.2m,"unspecified","gte",4.2m),(5m,"unspecified","gte",5m),
            (6m,"unspecified","gte",3m),(7m,"unspecified","gte",3.5m),(7.5m,"unspecified","gte",3.75m),(8m,"unspecified","gte",4m),(9m,"unspecified","gte",4.5m),(10m,"unspecified","gte",5m),
            (4m,"ten","gte",2m),(5m,"ten","gte",2.5m),(6m,"ten","gte",3m),(8m,"ten","gte",4m),(10m,"ten","gte",5m),
            (3m,"five","gte",3m),(4.5m,"five","gte",4.5m),(5m,"five","gte",5m),
            (6m,"unspecified","gt",3m),(8m,"unspecified","gt",4m),(3m,"unspecified","gt",3m)
        };
        foreach (var (value, scale, op, expected) in valid)
        {
            var result = new RealParsedQueryValidator().Validate(RatingQuery(value, scale, op));
            Assert.AreEqual("query", result.Type, $"{value}/{scale}/{op}");
            Assert.AreEqual(expected, result.Query!.HardFilters.RatingMin, $"{value}/{scale}/{op}");
            Assert.AreEqual(op, result.Query.HardFilters.RatingOperator);
        }
        foreach (var (value, scale) in new[] { (0m,"unspecified"), (0.5m,"unspecified"), (11m,"unspecified"), (6m,"five"), (11m,"ten") })
        {
            var result = new RealParsedQueryValidator().Validate(RatingQuery(value, scale, "gte"));
            Assert.AreEqual("UNSUPPORTED_REQUEST", result.AlertCode, $"{value}/{scale}");
        }
    }

    [TestMethod]
    public void ChecklistStatusMapsEachCategoryAndPreservesAllCanonicalFilters()
    {
        var validator = new RealParsedQueryValidator();
        var cases = new[]
        {
            ("year", "{\"status\":\"present\",\"min\":2016,\"max\":null}"),
            ("runtime", "{\"status\":\"present\",\"min\":null,\"max\":109}"),
            ("genres", "{\"status\":\"present\",\"all\":[\"Sci-Fi\"],\"any\":[]}"),
            ("rating", "{\"status\":\"present\",\"value\":4,\"operator\":\"gte\",\"scale\":\"five\"}"),
            ("originalLanguage", "{\"status\":\"present\",\"value\":\"ja\"}")
        };
        foreach (var (category, value) in cases)
        {
            var result = validator.Validate(QueryWith(category, value, "dark"));
            Assert.AreEqual("query", result.Type);
            Assert.IsTrue(RealParsedQueryValidator.HasActiveFilter(result.Query!.HardFilters), category);
        }
        var full = validator.Validate("""
            {"type":"query","query":{"year":{"status":"present","min":2001,"max":2020},"runtime":{"status":"present","min":80,"max":119},"genres":{"status":"present","all":["Sci-Fi"],"any":["Drama","Thriller"]},"rating":{"status":"present","value":3.5,"operator":"gte","scale":"five"},"originalLanguage":{"status":"present","value":"ja"},"semanticQuery":"dreamlike science fiction"},"alertCode":null}
            """);
        var filters = full.Query!.HardFilters;
        Assert.AreEqual(2001, filters.YearMin);
        Assert.AreEqual(2020, filters.YearMax);
        Assert.AreEqual(80, filters.RuntimeMin);
        Assert.AreEqual(119, filters.RuntimeMax);
        CollectionAssert.AreEqual(new[] { "Sci-Fi" }, filters.Genres!.All!.ToArray());
        CollectionAssert.AreEqual(new[] { "Drama", "Thriller" }, filters.Genres.Any!.ToArray());
        Assert.AreEqual(3.5m, filters.RatingMin);
        Assert.AreEqual("ja", filters.OriginalLanguage);
        Assert.AreEqual("dreamlike science fiction", full.Query.SemanticQuery);
    }

    [TestMethod]
    public void ChecklistAcceptsCanonicalYearAndRuntimeBoundaries()
    {
        var json = "{\"type\":\"query\",\"query\":{" +
            "\"year\":{\"status\":\"present\",\"min\":1000,\"max\":9999}," +
            "\"runtime\":{\"status\":\"present\",\"min\":1,\"max\":2147483647}," +
            "\"genres\":{\"status\":\"absent\",\"all\":[],\"any\":[]}," +
            "\"rating\":{\"status\":\"absent\",\"value\":null,\"operator\":null,\"scale\":null}," +
            "\"originalLanguage\":{\"status\":\"absent\",\"value\":null}," +
            "\"semanticQuery\":\"quiet drama\"},\"alertCode\":null}";
        var result = new RealParsedQueryValidator().Validate(json).Query!;
        Assert.AreEqual(1000, result.HardFilters.YearMin);
        Assert.AreEqual(9999, result.HardFilters.YearMax);
        Assert.AreEqual(1, result.HardFilters.RuntimeMin);
        Assert.AreEqual(int.MaxValue, result.HardFilters.RuntimeMax);
    }

    [TestMethod]
    public void UnsupportedCategoryReturnsUnsupportedAlertAndAlertsRequireNullQuery()
    {
        var unsupported = JsonDocument.Parse(QueryWith("runtime", "{\"status\":\"unsupported\",\"min\":null,\"max\":null}", "dark"));
        using (unsupported)
        {
            var text = unsupported.RootElement.GetRawText();
            var result = new RealParsedQueryValidator().Validate(text);
            Assert.AreEqual("alert", result.Type);
            Assert.AreEqual("UNSUPPORTED_REQUEST", result.AlertCode);
        }
        var alert = new RealParsedQueryValidator().Validate("{\"type\":\"alert\",\"query\":null,\"alertCode\":\"QUERY_UNCLEAR\"}");
        Assert.AreEqual("QUERY_UNCLEAR", alert.AlertCode);
        AssertInvalid("{\"type\":\"alert\",\"query\":{},\"alertCode\":\"QUERY_UNCLEAR\"}");
        AssertInvalid("{\"type\":\"query\",\"query\":null,\"alertCode\":null}");
        AssertInvalid("{\"type\":\"query\",\"query\":{" + QueryBody() + "},\"alertCode\":\"QUERY_UNCLEAR\"}");
    }

    [TestMethod]
    public void InvalidSyntacticallyValidChecklistCombinationsAreRejected()
    {
        var valid = QueryJson(semantic: "quiet mystery");
        foreach (var invalid in new[]
        {
            valid.Replace("\"runtime\":{\"status\":\"absent\",\"min\":null,\"max\":null},", "", StringComparison.Ordinal),
            valid.Replace("\"runtime\":{\"status\":\"absent\",\"min\":null,\"max\":null}", "\"runtime\":{\"status\":\"present\",\"min\":null,\"max\":null}", StringComparison.Ordinal),
            valid.Replace("\"genres\":{\"status\":\"absent\",\"all\":[],\"any\":[]}", "\"genres\":{\"status\":\"present\",\"all\":[],\"any\":[]}", StringComparison.Ordinal),
            valid.Replace("\"rating\":{\"status\":\"absent\",\"value\":null,\"operator\":null,\"scale\":null}", "\"rating\":{\"status\":\"absent\",\"value\":4,\"operator\":\"gte\",\"scale\":\"five\"}", StringComparison.Ordinal),
            valid.Replace("\"originalLanguage\":{\"status\":\"absent\",\"value\":null}", "\"originalLanguage\":{\"status\":\"present\",\"value\":\"cn\"}", StringComparison.Ordinal),
            valid.Replace("\"genres\":{\"status\":\"absent\",\"all\":[],\"any\":[]}", "\"genres\":{\"status\":\"absent\",\"all\":[\"Horror\"],\"any\":[]}", StringComparison.Ordinal),
            valid.Replace("\"year\":{\"status\":\"absent\",\"min\":null,\"max\":null}", "\"year\":{\"status\":\"present\",\"min\":2021,\"max\":2020}", StringComparison.Ordinal),
            valid.Replace("\"semanticQuery\":\"quiet mystery\"", "\"semanticQuery\":null", StringComparison.Ordinal),
            valid.Replace("\"all\":[]", "\"all\":[\"Unknown Genre\"]", StringComparison.Ordinal),
            valid.Replace("\"status\":\"absent\"", "\"status\":\"maybe\"", StringComparison.Ordinal)
        }) AssertInvalid(invalid);
        AssertInvalid("{\"type\":\"query\",\"query\":{" + QueryBody().Replace("\"runtime\":{\"status\":\"absent\",\"min\":null,\"max\":null},", "", StringComparison.Ordinal) + "},\"alertCode\":null}");
    }

    [TestMethod]
    public async Task ProviderFailuresAndMalformedResponsesStaySanitized()
    {
        foreach (var status in new[] { HttpStatusCode.TooManyRequests, HttpStatusCode.InternalServerError, HttpStatusCode.BadRequest })
        {
            using var client = new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("secret provider payload") })));
            var error = await AssertThrowsAsync<RealProviderException>(() => new GeminiRealQueryParser(client, "hidden", "prompt", ReadSchema(), new RealParsedQueryValidator()).ParseAsync("en", "quiet", CancellationToken.None));
            Assert.AreEqual("PROVIDER_UNAVAILABLE", error.Code);
            Assert.IsFalse(error.Message.Contains("secret", StringComparison.OrdinalIgnoreCase));
        }
        foreach (var response in new[] { "{}", "{\"candidates\":[]}", "{\"candidates\":[{}]}", ParserEnvelope("not json"), ParserEnvelope("{\"type\":\"query\",\"query\":{}}") })
        {
            using var client = new HttpClient(new FakeHandler((_, _) => Task.FromResult(JsonResponse(response))));
            var error = await AssertThrowsAsync<RealProviderException>(() => new GeminiRealQueryParser(client, "hidden", "prompt", ReadSchema(), new RealParsedQueryValidator()).ParseAsync("en", "quiet", CancellationToken.None));
            Assert.AreEqual("PARSER_INVALID_RESPONSE", error.Code);
        }
    }

    private static string QueryJson(string? semantic) => "{\"type\":\"query\",\"query\":{" + QueryBody(semantic) + "},\"alertCode\":null}";
    private static string RatingQuery(decimal value, string scale, string op) => QueryJson(null).Replace("\"rating\":{\"status\":\"absent\",\"value\":null,\"operator\":null,\"scale\":null}", $"\"rating\":{{\"status\":\"present\",\"value\":{value.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"operator\":\"{op}\",\"scale\":\"{scale}\"}}", StringComparison.Ordinal);
    private static string QueryBody(string? semantic = "quiet mystery") => "\"year\":{\"status\":\"absent\",\"min\":null,\"max\":null},\"runtime\":{\"status\":\"absent\",\"min\":null,\"max\":null},\"genres\":{\"status\":\"absent\",\"all\":[],\"any\":[]},\"rating\":{\"status\":\"absent\",\"value\":null,\"operator\":null,\"scale\":null},\"originalLanguage\":{\"status\":\"absent\",\"value\":null},\"semanticQuery\":" + JsonSerializer.Serialize(semantic);
    private static string QueryWith(string category, string value, string? semantic) => QueryJson(semantic).Replace("\"" + category + "\":{\"status\":\"absent\",\"min\":null,\"max\":null}", "\"" + category + "\":" + value, StringComparison.Ordinal)
        .Replace("\"" + category + "\":{\"status\":\"absent\",\"all\":[],\"any\":[]}", "\"" + category + "\":" + value, StringComparison.Ordinal)
        .Replace("\"" + category + "\":{\"status\":\"absent\",\"min\":null}", "\"" + category + "\":" + value, StringComparison.Ordinal)
        .Replace("\"" + category + "\":{\"status\":\"absent\",\"value\":null,\"operator\":null,\"scale\":null}", "\"" + category + "\":" + value, StringComparison.Ordinal)
        .Replace("\"" + category + "\":{\"status\":\"absent\",\"value\":null}", "\"" + category + "\":" + value, StringComparison.Ordinal);
    private static string ReadSchema() => File.ReadAllText(FindRepositoryFile("src/backend/CineKros.Api/RealProviders/query-parser.schema.json"));
    private static string[] Strings(JsonElement value) => value.EnumerateArray().Select(x => x.GetString()!).ToArray();
    private static void AssertInvalid(string json)
    {
        var error = AssertThrows<RealProviderException>(() => new RealParsedQueryValidator().Validate(json));
        Assert.AreEqual("PARSER_INVALID_RESPONSE", error.Code);
    }
    private static string FindRepositoryFile(string relativePath)
    {
        var repositoryRoot = Environment.GetEnvironmentVariable("CINEKROS_TEST_REPOSITORY_ROOT");
        if (!string.IsNullOrWhiteSpace(repositoryRoot))
        {
            var candidate = Path.Combine(repositoryRoot, relativePath);
            if (File.Exists(candidate)) return candidate;
        }
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
        }
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
        }
        Assert.Fail($"Could not locate repository file '{relativePath}'.");
        throw new InvalidOperationException("Unreachable");
    }
    private static string ParserEnvelope(string text) => JsonSerializer.Serialize(new { candidates = new[] { new { content = new { parts = new[] { new { text } } } } } });
    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static T AssertThrows<T>(Action action) where T : Exception
    {
        try { action(); } catch (T error) { return error; }
        Assert.Fail($"Expected {typeof(T).Name}.");
        throw new InvalidOperationException("Unreachable");
    }
    private static async Task<T> AssertThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T error) { return error; }
        Assert.Fail($"Expected {typeof(T).Name}.");
        throw new InvalidOperationException("Unreachable");
    }
    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class RecordingLogger : ILogger<GeminiRealQueryParser>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
