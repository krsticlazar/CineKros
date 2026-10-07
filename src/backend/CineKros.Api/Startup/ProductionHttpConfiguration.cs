namespace CineKros.Api.Startup;

public static class ProductionHttpConfiguration
{
    public const string RecommendationCorsPolicy = "recommendations";

    public static void AddRecommendationCors(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var origins = ParseAllowedOrigins(configuration["CINEKROS_ALLOWED_ORIGINS"], environment.IsDevelopment());
        services.AddCors(options => options.AddPolicy(RecommendationCorsPolicy, policy =>
        {
            policy.WithOrigins(origins)
                .WithMethods("POST")
                .WithHeaders("Content-Type");
        }));
    }

    public static string[] ParseAllowedOrigins(string? configuredOrigins, bool isDevelopment)
    {
        if (configuredOrigins is null || configuredOrigins.Length == 0) return [];

        var origins = configuredOrigins.Split(',', StringSplitOptions.None);
        var normalized = new string[origins.Length];
        for (var index = 0; index < origins.Length; index++)
        {
            var value = origins[index].Trim();
            if (value.Length == 0 || value.Contains('*'))
                throw InvalidOrigins();

            var candidate = value.EndsWith("/", StringComparison.Ordinal) ? value[..^1] : value;
            if (candidate.Length == 0 || !Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
                || uri.AbsolutePath != "/")
                throw InvalidOrigins();

            var origin = uri.GetLeftPart(UriPartial.Authority);
            if (!string.Equals(candidate, origin, StringComparison.OrdinalIgnoreCase))
                throw InvalidOrigins();

            if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                normalized[index] = origin;
                continue;
            }

            if (isDevelopment && uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && uri.IsLoopback)
            {
                normalized[index] = origin;
                continue;
            }

            throw InvalidOrigins();
        }

        return normalized.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static InvalidOperationException InvalidOrigins() =>
        new("CINEKROS_ALLOWED_ORIGINS contains an invalid origin configuration.");
}
