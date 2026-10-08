using System.Net;
using System.Text;
using System.Text.Json;
using CineKros.Api.RealProviders;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CineKros.Api.Tests.RealProviders;

[TestClass]
public sealed class ParserV5AdapterTests
{
    [TestMethod]
    public async Task LanguageAwareModeSendsV5SchemaAndSelectedLanguageThenValidatesV5()
    {
        string? requestBody = null;
        using var client = new HttpClient(new FakeHandler(async (request, ct) =>
        {
            requestBody = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Envelope(V5Query()), Encoding.UTF8, "application/json") };
        }));

        var parser = new GeminiRealQueryParser(client, "test-key", "v5 prompt", V5Schema(), new RealParsedQueryValidator(), languageAware: true);
        var result = await parser.ParseAsync("sr", "филм", CancellationToken.None);

        Assert.AreEqual("query", result.Type);
        Assert.AreEqual("match", result.LanguageCheck);
        using var body = JsonDocument.Parse(requestBody!);
        var userText = body.RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString()!;
        using var userInput = JsonDocument.Parse(userText);
        Assert.AreEqual("sr", userInput.RootElement.GetProperty("language").GetString());
        Assert.AreEqual("филм", userInput.RootElement.GetProperty("message").GetString());
        Assert.AreEqual("v5 prompt", body.RootElement.GetProperty("systemInstruction").GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.AreEqual("match", body.RootElement.GetProperty("generationConfig").GetProperty("responseSchema").GetProperty("properties").GetProperty("languageCheck").GetProperty("enum")[0].GetString());
    }

    [TestMethod]
    public async Task DefaultModeKeepsV4ValidationPath()
    {
        using var client = new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Envelope("""{"type":"query","query":{"year":{"status":"absent","min":null,"max":null},"runtime":{"status":"absent","min":null,"max":null},"genres":{"status":"absent","all":[],"any":[]},"rating":{"status":"absent","value":null,"operator":null,"scale":null},"originalLanguage":{"status":"absent","value":null},"semanticQuery":"quiet"},"alertCode":null}"""), Encoding.UTF8, "application/json")
        })));
        var parser = new GeminiRealQueryParser(client, "test-key", "v4 prompt", "{}", new RealParsedQueryValidator());
        var result = await parser.ParseAsync("en", "quiet", CancellationToken.None);
        Assert.AreEqual("query", result.Type);
        Assert.IsNull(result.LanguageCheck);
    }

    private static string V5Schema() => FindFile("src/backend/CineKros.Api/RealProviders/query-parser-v5.schema.json");
    private static string V5Query() => """{"type":"query","languageCheck":"match","query":{"year":{"status":"absent","min":null,"max":null},"runtime":{"status":"absent","min":null,"max":null},"genres":{"status":"absent","all":[],"any":[]},"rating":{"status":"absent","value":null,"operator":null,"scale":null},"originalLanguage":{"status":"absent","value":null},"semanticQuery":"филмови"},"alertCode":null}""";
    private static string Envelope(string text) => JsonSerializer.Serialize(new { candidates = new[] { new { content = new { parts = new[] { new { text } } } } } });
    private static string FindFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        Assert.Fail($"Could not locate '{relativePath}'.");
        throw new InvalidOperationException("Unreachable");
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
