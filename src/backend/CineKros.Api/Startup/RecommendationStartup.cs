using CineKros.Api.RealFlow;
using CineKros.Api.RealProviders;
using CineKros.Api.Search;
using CineKros.Embedding;
using Npgsql;

namespace CineKros.Api.Startup;

public static class RecommendationStartup
{
    public static string ResolveMode(IHostEnvironment environment)
    {
        var mode = Environment.GetEnvironmentVariable("CINEKROS_RECOMMENDATION_MODE");
        if (string.IsNullOrWhiteSpace(mode))
        {
            if (!environment.IsDevelopment())
                throw new InvalidOperationException("CINEKROS_RECOMMENDATION_MODE must be set outside Development.");
            mode = "fake";
        }
        if (mode is not ("fake" or "real"))
            throw new InvalidOperationException("CINEKROS_RECOMMENDATION_MODE must be fake or real.");
        if (environment.IsProduction() && mode != "real")
            throw new InvalidOperationException("Production requires CINEKROS_RECOMMENDATION_MODE=real.");
        return mode;
    }

    public static void RegisterServices(IServiceCollection services, string mode)
        => RegisterServices(services, mode, serbianPoc: false);

    public static void RegisterServices(IServiceCollection services, string mode, bool serbianPoc)
    {
        if (serbianPoc && mode != "real")
            throw new InvalidOperationException("CINEKROS_SERBIAN_POC=true requires real recommendation mode.");
        if (mode == "fake")
        {
            services.AddSingleton(new MovieQueryPrompt(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Prompts", "movie-query-parser.md"))));
            services.AddSingleton<IQueryParser, FakeQueryParser>();
            services.AddSingleton<IMovieSearch, FakeMovieSearch>();
            return;
        }

        RegisterRealServices(services, serbianPoc);
    }

    public static bool ResolveSerbianPocMode(IHostEnvironment environment, string mode) =>
        ResolveSerbianPocMode(environment, mode, Environment.GetEnvironmentVariable("CINEKROS_SERBIAN_POC"));

    public static bool ResolveSerbianPocMode(IHostEnvironment environment, string mode, string? flag)
    {
        if (flag is null || flag == "false") return false;
        if (flag != "true")
            throw new InvalidOperationException("CINEKROS_SERBIAN_POC must be true or false.");
        if (!environment.IsDevelopment())
            throw new InvalidOperationException("CINEKROS_SERBIAN_POC=true is permitted only in Development.");
        if (mode != "real")
            throw new InvalidOperationException("CINEKROS_SERBIAN_POC=true requires real recommendation mode.");
        return true;
    }

    public static void ValidatePocDatabaseTarget(string? configuredDatabase)
    {
        if (configuredDatabase != SearchDatasetExpectation.CorrectedV2Database)
            throw new InvalidOperationException("The current Serbian POC must target the exact corrected Phase 6T v2 database.");
    }

    private static string CreateReadOnlyConnectionString(string connectionString, string expectedDatabase)
    {
        NpgsqlConnectionStringBuilder builder;
        try { builder = new NpgsqlConnectionStringBuilder(connectionString); }
        catch { throw new InvalidOperationException("The recommendation database connection string is invalid."); }
        if (builder.Database != expectedDatabase)
            throw new InvalidOperationException("The configured recommendation database does not match its exact released dataset.");
        builder.Options = "-c default_transaction_read_only=on";
        return builder.ConnectionString;
    }

    private static void RegisterRealServices(IServiceCollection services, bool serbianPoc)
    {
        var connectionString = Environment.GetEnvironmentVariable("DATABASE_CONNECTION_STRING");
        var apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        var modelDirectory = Environment.GetEnvironmentVariable("CINEKROS_E5_MODEL_DIR");
        if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(modelDirectory))
            throw new InvalidOperationException("Real recommendation mode requires DATABASE_CONNECTION_STRING, GEMINI_API_KEY, and CINEKROS_E5_MODEL_DIR in the process environment.");
        connectionString = CreateReadOnlyConnectionString(connectionString,
            serbianPoc ? SearchDatasetExpectation.CorrectedV2Database : SearchDatasetExpectation.FullProductionDatabase);

        var artifactDirectory = Path.Combine(AppContext.BaseDirectory, "RealProviders");
        const string promptName = "query-parser-v5.md";
        const string schemaName = "query-parser-v5.schema.json";
        var systemInstruction = File.ReadAllText(Path.Combine(artifactDirectory, promptName));
        var responseSchema = File.ReadAllText(Path.Combine(artifactDirectory, schemaName));
        services.AddSingleton(new RealParsedQueryValidator());
        services.AddSingleton(_ => new HttpClient());
        services.AddSingleton(sp => new GeminiRealQueryParser(sp.GetRequiredService<HttpClient>(), apiKey, systemInstruction, responseSchema, sp.GetRequiredService<RealParsedQueryValidator>(), sp.GetRequiredService<ILogger<GeminiRealQueryParser>>(), sp.GetRequiredService<IHostEnvironment>().IsDevelopment(), languageAware: true));
        services.AddSingleton<IRealQueryParser, GeminiQueryParserAdapter>();
        var embeddingProfile = EmbeddingProfileDescriptor.MultilingualE5Base;
        services.AddSingleton(sp => LoadE5Model(modelDirectory, embeddingProfile));
        services.AddSingleton<IRealQueryEmbeddingProvider, E5QueryEmbeddingAdapter>();
        services.AddSingleton<NpgsqlDataSource>(_ => MovieSearchRepository.CreateDataSource(connectionString));
        if (serbianPoc)
            services.AddSingleton(sp => MovieSearchRepository.CreateCorrectedPhase6TV2PocRepositoryAsync(sp.GetRequiredService<NpgsqlDataSource>()).GetAwaiter().GetResult());
        else
            services.AddSingleton(sp => MovieSearchRepository.CreateFullBilingualProductionRepositoryAsync(sp.GetRequiredService<NpgsqlDataSource>()).GetAwaiter().GetResult());
        services.AddSingleton<IRealMovieSearch, MovieSearchAdapter>();
        services.AddSingleton(sp => new RealRecommendationService(sp.GetRequiredService<IRealQueryParser>(), sp.GetRequiredService<RealParsedQueryValidator>(),
            sp.GetRequiredService<IRealQueryEmbeddingProvider>(), sp.GetRequiredService<IRealMovieSearch>(), languageAwarePoc: true));
    }

    private static E5EmbeddingModel LoadE5Model(string modelDirectory, EmbeddingProfileDescriptor expectedProfile)
    {
        try
        {
            var model = new E5EmbeddingModel(modelDirectory, expectedProfile);
            if (model.ProfileFingerprint != expectedProfile.ProfileFingerprint)
            {
                model.Dispose();
                throw new InvalidDataException();
            }
            return model;
        }
        catch
        {
            throw new InvalidOperationException("Real semantic search is unavailable because the configured E5 model artifacts are invalid or unavailable.");
        }
    }
}
