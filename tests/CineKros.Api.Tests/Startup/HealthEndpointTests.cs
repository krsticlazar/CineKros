using System.Net;
using CineKros.Api.Startup;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CineKros.Api.Tests.Startup;

[TestClass]
public sealed class HealthEndpointTests
{
    [TestMethod]
    public async Task LiveIsAlwaysAvailableAndReadyStaysNotReadyInFakeMode()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var live = await client.GetAsync("/live");
        Assert.AreEqual(HttpStatusCode.OK, live.StatusCode);
        Assert.AreEqual("{\"status\":\"live\"}", await live.Content.ReadAsStringAsync());

        using var ready = await client.GetAsync("/ready");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.AreEqual("{\"status\":\"not_ready\"}", await ready.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task ReadyReturnsReadyWhenCachedStateIsMarkedReady()
    {
        var state = new StartupReadinessState();
        state.MarkReady();
        await using var factory = new ReadyFactory(state);
        using var client = factory.CreateClient();

        using var ready = await client.GetAsync("/ready");
        Assert.AreEqual(HttpStatusCode.OK, ready.StatusCode);
        Assert.AreEqual("{\"status\":\"ready\"}", await ready.Content.ReadAsStringAsync());
    }

    [TestMethod]
    public async Task HealthEndpointsDoNotConsumeRecommendationRateLimit()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        for (var index = 0; index < 35; index++)
        {
            using var live = await client.GetAsync("/live");
            using var ready = await client.GetAsync("/ready");
            Assert.AreEqual(HttpStatusCode.OK, live.StatusCode);
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        }
    }

    private sealed class ReadyFactory(StartupReadinessState readyState) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureServices(services =>
        {
            services.RemoveAll<StartupReadinessState>();
            services.AddSingleton(readyState);
        });
    }
}
