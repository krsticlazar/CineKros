using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using CineKros.Api.Startup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Primitives;

namespace CineKros.Api.Tests.Startup;

[TestClass]
public sealed class TrustedClientIpTests
{
    [TestMethod]
    public void TrustIsDisabledByDefaultInEveryEnvironment()
    {
        foreach (var environment in new[] { "Development", "Production" })
        {
            var config = ParseConfiguration(new Dictionary<string, string?> { ["ASPNETCORE_ENVIRONMENT"] = environment });
            Assert.IsFalse(config.Enabled);
            Assert.AreEqual("203.0.113.8", config.SelectClientIp(IPAddress.Parse("203.0.113.8"), "198.51.100.4"));
        }
    }

    [TestMethod]
    public void DisabledTrustIgnoresHeaderAndDirectPeerIsUsed()
    {
        var config = ParseConfiguration(new Dictionary<string, string?> { ["CINEKROS_TRUST_CF_CONNECTING_IP"] = "false" });
        Assert.AreEqual("198.51.100.10", config.SelectClientIp(IPAddress.Parse("198.51.100.10"), "203.0.113.5"));
        Assert.AreEqual("198.51.100.10", config.SelectClientIp(IPAddress.Parse("198.51.100.10"), StringValues.Empty));
    }

    [TestMethod]
    public void HeaderFromUntrustedPeerHasNoEffect()
    {
        var config = Enabled("192.0.2.10");
        Assert.AreEqual("192.0.2.11", config.SelectClientIp(IPAddress.Parse("192.0.2.11"), "203.0.113.5"));
    }

    [TestMethod]
    public void TrustedProxyAcceptsSingleIpv4OrIpv6ClientLiteral()
    {
        var ipv4 = Enabled("192.0.2.10");
        Assert.AreEqual("203.0.113.5", ipv4.SelectClientIp(IPAddress.Parse("192.0.2.10"), "203.0.113.5"));

        var ipv6 = Enabled("2001:db8::10");
        Assert.AreEqual("2001:db8::5", ipv6.SelectClientIp(IPAddress.Parse("2001:db8::10"), "2001:db8::5"));
    }

    [TestMethod]
    public void MappedIpv4PeerMatchesConfiguredIpv4Proxy()
    {
        var config = Enabled("192.0.2.10");
        var mappedPeer = IPAddress.Parse("::ffff:192.0.2.10");
        Assert.AreEqual("203.0.113.5", config.SelectClientIp(mappedPeer, "203.0.113.5"));
    }

    [TestMethod]
    public void MalformedMultipleOrMissingPeerHeadersFallBackToPeer()
    {
        var config = Enabled("192.0.2.10");
        var peer = IPAddress.Parse("192.0.2.10");
        foreach (var header in new[] { "203.0.113.5, 203.0.113.6", " 203.0.113.5", "203.0.113.5 ", "203.0.113.5:443", "203.0.113.5%eth0", "not-an-ip" })
            Assert.AreEqual(peer.ToString(), config.SelectClientIp(peer, header), header);
        Assert.AreEqual(peer.ToString(), config.SelectClientIp(peer, new StringValues(new[] { "203.0.113.5", "203.0.113.6" })));
        Assert.IsNull(config.SelectClientIp(null, "203.0.113.5"));
    }

    [TestMethod]
    public void InvalidConfigurationFailsWithoutEchoingValues()
    {
        foreach (var values in new[]
        {
            new Dictionary<string, string?> { ["CINEKROS_TRUST_CF_CONNECTING_IP"] = "perhaps" },
            new Dictionary<string, string?> { ["CINEKROS_TRUST_CF_CONNECTING_IP"] = "true" },
            new Dictionary<string, string?> { ["CINEKROS_TRUST_CF_CONNECTING_IP"] = "true", ["CINEKROS_TRUSTED_PROXY_IPS"] = "192.0.2.10,private-proxy" },
            new Dictionary<string, string?> { ["CINEKROS_TRUST_CF_CONNECTING_IP"] = "true", ["CINEKROS_TRUSTED_PROXY_IPS"] = "192.0.2.10/24" },
            new Dictionary<string, string?> { ["CINEKROS_TRUST_CF_CONNECTING_IP"] = "true", ["CINEKROS_TRUSTED_PROXY_IPS"] = "192.0.2.10,,192.0.2.11" }
        })
        {
            var exception = Assert.ThrowsExactly<InvalidOperationException>(() => ParseConfiguration(values));
            Assert.AreEqual("Trusted client IP configuration is invalid.", exception.Message);
            Assert.IsFalse(exception.ToString().Contains("private-proxy", StringComparison.Ordinal));
        }
    }

    [TestMethod]
    public async Task InvalidEnabledConfigurationFailsApplicationStartupWithoutEchoingValue()
    {
        const string invalidProxy = "proxy.internal.example";
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("CINEKROS_TRUST_CF_CONNECTING_IP", "true");
            builder.UseSetting("CINEKROS_TRUSTED_PROXY_IPS", invalidProxy);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["CINEKROS_TRUST_CF_CONNECTING_IP"] = "true",
                    ["CINEKROS_TRUSTED_PROXY_IPS"] = invalidProxy
                }));
        });

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => factory.CreateClient());
        Assert.IsTrue(exception.ToString().Contains("Trusted client IP configuration is invalid.", StringComparison.Ordinal));
        Assert.IsFalse(exception.ToString().Contains(invalidProxy, StringComparison.Ordinal));
    }

    [TestMethod]
    public void DisabledTrustDoesNotRequireOrParseProxyList()
    {
        var config = ParseConfiguration(new Dictionary<string, string?>
        {
            ["CINEKROS_TRUST_CF_CONNECTING_IP"] = "false",
            ["CINEKROS_TRUSTED_PROXY_IPS"] = "not a proxy list"
        });
        Assert.IsFalse(config.Enabled);
    }

    private static TrustedClientIp Enabled(string proxies) => ParseConfiguration(new Dictionary<string, string?>
    {
        ["CINEKROS_TRUST_CF_CONNECTING_IP"] = "true",
        ["CINEKROS_TRUSTED_PROXY_IPS"] = proxies
    });

    private static TrustedClientIp ParseConfiguration(IDictionary<string, string?> values) =>
        TrustedClientIp.Parse(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
}
