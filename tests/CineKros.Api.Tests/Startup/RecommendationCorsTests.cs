using CineKros.Api.Startup;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace CineKros.Api.Tests.Startup;

[TestClass]
public sealed class RecommendationCorsTests
{
    [TestMethod]
    public void AllowedOriginsNormalizeAndDeduplicateExactOrigins()
    {
        CollectionAssert.AreEqual(
            new[] { "https://web.example.com", "https://admin.example.com" },
            ProductionHttpConfiguration.ParseAllowedOrigins("https://web.example.com/, https://admin.example.com,https://web.example.com", false));
        CollectionAssert.AreEqual(Array.Empty<string>(), ProductionHttpConfiguration.ParseAllowedOrigins(null, false));
        CollectionAssert.AreEqual(Array.Empty<string>(), ProductionHttpConfiguration.ParseAllowedOrigins("", false));
    }

    [TestMethod]
    public void AllowedOriginsRequireHttpsExceptDevelopmentLoopback()
    {
        foreach (var invalid in new[]
        {
            "*", "https://*.example.com", "https://web.example.com/path", "https://web.example.com?x=1",
            "https://web.example.com#fragment", "https://user@web.example.com", "https://web.example.com,,https://other.example.com",
            ",https://web.example.com", "https://web.example.com,", "http://web.example.com"
        })
        {
            var exception = Assert.ThrowsExactly<InvalidOperationException>(() => ProductionHttpConfiguration.ParseAllowedOrigins(invalid, false));
            Assert.AreEqual("CINEKROS_ALLOWED_ORIGINS contains an invalid origin configuration.", exception.Message);
            Assert.IsFalse(exception.Message.Contains(invalid, StringComparison.Ordinal));
        }

        CollectionAssert.AreEqual(new[] { "http://localhost:5179", "http://127.0.0.1:5179", "http://[::1]:5179" },
            ProductionHttpConfiguration.ParseAllowedOrigins("http://localhost:5179/,http://127.0.0.1:5179,http://[::1]:5179", true));
        Assert.ThrowsExactly<InvalidOperationException>(() => ProductionHttpConfiguration.ParseAllowedOrigins("http://example.com", true));
    }

    [TestMethod]
    public async Task CorsMiddlewareGrantsOnlyConfiguredOriginPostAndContentTypeWithoutCredentials()
    {
        await using var factory = CreateFactory("https://web.example.com/");
        using var client = factory.CreateClient();
        using var allowed = await Preflight(client, "https://web.example.com", "POST", "content-type");

        Assert.AreEqual(System.Net.HttpStatusCode.NoContent, allowed.StatusCode);
        Assert.AreEqual("https://web.example.com", allowed.Headers.GetValues("Access-Control-Allow-Origin").Single());
        CollectionAssert.Contains(allowed.Headers.GetValues("Access-Control-Allow-Methods").Single().Split(", "), "POST");
        CollectionAssert.Contains(allowed.Headers.GetValues("Access-Control-Allow-Headers").Single().Split(", "), "Content-Type");
        Assert.IsFalse(allowed.Headers.Contains("Access-Control-Allow-Credentials"));

        using var disallowed = await Preflight(client, "https://evil.example.com", "POST", "content-type");
        Assert.IsFalse(disallowed.Headers.Contains("Access-Control-Allow-Origin"));

        using var wrongMethod = await Preflight(client, "https://web.example.com", "PUT", "content-type");
        Assert.AreEqual("POST", wrongMethod.Headers.GetValues("Access-Control-Allow-Methods").Single());
        Assert.IsFalse(wrongMethod.Headers.GetValues("Access-Control-Allow-Methods").Single().Contains("PUT", StringComparison.Ordinal));

        using var unsupportedHeader = await Preflight(client, "https://web.example.com", "POST", "x-custom-header");
        var allowedHeaders = unsupportedHeader.Headers.GetValues("Access-Control-Allow-Headers").Single();
        Assert.AreEqual("Content-Type", allowedHeaders);
        Assert.IsFalse(allowedHeaders.Contains("x-custom-header", StringComparison.OrdinalIgnoreCase));

    }

    [TestMethod]
    public async Task MissingConfigurationGrantsNoCrossOriginAccess()
    {
        await using var factory = CreateFactory("");
        using var client = factory.CreateClient();
        using var response = await Preflight(client, "https://web.example.com", "POST", "content-type");
        Assert.IsFalse(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [TestMethod]
    public async Task AllowedOriginReceivesCorsHeaderOnSanitizedPreParserValidationError()
    {
        await using var factory = CreateFactory("https://web.example.com");
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/recommendations")
        {
            Content = new StringContent("{\"language\":\"en\",\"message\":\"x\"}", System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Origin", "https://web.example.com");

        using var response = await client.SendAsync(request);
        Assert.AreEqual(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual("https://web.example.com", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        var body = await response.Content.ReadAsStringAsync();
        Assert.IsTrue(body.Contains("INVALID_REQUEST", StringComparison.Ordinal));
        Assert.IsFalse(body.Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task MalformedConfigurationFailsApplicationStartupWithoutEchoingValue()
    {
        const string invalidOrigin = "https://private.example.com/path";
        await using var factory = CreateFactory(invalidOrigin);
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => factory.CreateClient());
        Assert.IsTrue(exception.ToString().Contains("CINEKROS_ALLOWED_ORIGINS contains an invalid origin configuration.", StringComparison.Ordinal));
        Assert.IsFalse(exception.ToString().Contains(invalidOrigin, StringComparison.Ordinal));
    }

    private static WebApplicationFactory<Program> CreateFactory(string configuredOrigins) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("CINEKROS_ALLOWED_ORIGINS", configuredOrigins);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["CINEKROS_ALLOWED_ORIGINS"] = configuredOrigins }));
        });

    private static Task<HttpResponseMessage> Preflight(HttpClient client, string origin, string method, string headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Options, "/api/recommendations");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", method);
        request.Headers.Add("Access-Control-Request-Headers", headers);
        return client.SendAsync(request);
    }
}
