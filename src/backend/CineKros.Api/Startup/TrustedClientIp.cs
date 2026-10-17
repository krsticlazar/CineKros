using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Primitives;

namespace CineKros.Api.Startup;

public sealed class TrustedClientIp
{
    private readonly HashSet<IPAddress> _trustedProxies;

    private TrustedClientIp(bool enabled, HashSet<IPAddress> trustedProxies)
    {
        Enabled = enabled;
        _trustedProxies = trustedProxies;
    }

    public bool Enabled { get; }

    public static TrustedClientIp Parse(IConfiguration configuration)
    {
        var configuredEnabled = configuration["CINEKROS_TRUST_CF_CONNECTING_IP"];
        if (configuredEnabled is not null && !bool.TryParse(configuredEnabled, out _))
            throw InvalidConfiguration();

        var enabled = bool.TryParse(configuredEnabled, out var parsedEnabled) && parsedEnabled;
        if (!enabled)
            return new TrustedClientIp(false, []);

        var configuredProxies = configuration["CINEKROS_TRUSTED_PROXY_IPS"];
        if (string.IsNullOrEmpty(configuredProxies))
            throw InvalidConfiguration();

        var proxies = new HashSet<IPAddress>();
        foreach (var configuredProxy in configuredProxies.Split(',', StringSplitOptions.None))
        {
            if (!TryParseLiteral(configuredProxy.Trim(), out var address))
                throw InvalidConfiguration();
            proxies.Add(Normalize(address));
        }

        if (proxies.Count == 0)
            throw InvalidConfiguration();

        return new TrustedClientIp(true, proxies);
    }

    public string? SelectClientIp(IPAddress? peerAddress, StringValues cfConnectingIp)
    {
        if (peerAddress is null)
            return null;

        if (Enabled && _trustedProxies.Contains(Normalize(peerAddress))
            && cfConnectingIp.Count == 1
            && TryParseLiteral(cfConnectingIp[0] ?? string.Empty, out var clientAddress))
            return Normalize(clientAddress).ToString();

        return peerAddress.ToString();
    }

    private static bool TryParseLiteral(string value, out IPAddress address)
    {
        address = IPAddress.None;
        if (value.Length == 0 || value.Any(char.IsWhiteSpace) || value.Contains('%') || value.Contains(','))
            return false;
        if (!IPAddress.TryParse(value, out var parsed))
            return false;
        if (parsed.AddressFamily == AddressFamily.InterNetwork && !string.Equals(parsed.ToString(), value, StringComparison.Ordinal))
            return false;
        if (parsed.AddressFamily == AddressFamily.InterNetworkV6 && parsed.ScopeId != 0)
            return false;
        if (parsed.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            return false;

        address = parsed;
        return true;
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private static InvalidOperationException InvalidConfiguration() =>
        new("Trusted client IP configuration is invalid.");
}
