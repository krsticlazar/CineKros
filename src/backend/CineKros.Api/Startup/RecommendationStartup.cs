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
    {
        if (mode == "fake")
        {
            services.AddSingleton(new MovieQueryPrompt(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Prompts", "movie-query-parser.md"))));
            services.AddSingleton<IQueryParser, FakeQueryParser>();
            services.AddSingleton<IMovieSearch, FakeMovieSearch>();
            return;
        }

        RegisterRealServices(services);
    }

    private static void RegisterRealServices(IServiceCollection services)
    {
        var connectionString = Environment.GetEnvironmentVariable("DATABASE_CONNECTION_STRING");
        var apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        var modelDirectory = Environment.GetEnvironmentVariable("CINEKROS_E5_MODEL_DIR");
        if (string.IsNullOrWhiteSpace(connectionString) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(modelDirectory))
            throw new InvalidOperationException("Real recommendation mode requires DATABASE_CONNECTION_STRING, GEMINI_API_KEY, and CINEKROS_E5_MODEL_DIR in the process environment.");

        var artifactDirectory = Path.Combine(AppContext.BaseDirectory, "RealProviders");
        var systemInstruction = File.ReadAllText(Path.Combine(artifactDirectory, "query-parser-v4.md"));
        var responseSchema = File.ReadAllText(Path.Combine(artifactDirectory, "query-parser.schema.json"));
        services.AddSingleton(new RealParsedQueryValidator());
        services.AddSingleton(_ => new HttpClient());
        services.AddSingleton(sp => new GeminiRealQueryParser(sp.GetRequiredService<HttpClient>(), apiKey, systemInstruction, responseSchema, sp.GetRequiredService<RealParsedQueryValidator>(), sp.GetRequiredService<ILogger<GeminiRealQueryParser>>(), sp.GetRequiredService<IHostEnvironment>().IsDevelopment()));
        services.AddSingleton<IRealQueryParser, GeminiQueryParserAdapter>();
        services.AddSingleton(sp => LoadE5Model(modelDirectory));
        services.AddSingleton<IRealQueryEmbeddingProvider, E5QueryEmbeddingAdapter>();
        services.AddSingleton<NpgsqlDataSource>(_ => MovieSearchRepository.CreateDataSource(connectionString));
        services.AddSingleton(sp => new MovieSearchRepository(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<E5EmbeddingModel>().ProfileFingerprint));
        services.AddSingleton<IRealMovieSearch, MovieSearchAdapter>();
        services.AddSingleton<RealRecommendationService>();
    }

    private static E5EmbeddingModel LoadE5Model(string modelDirectory)
    {
        try
        {
            var model = new E5EmbeddingModel(modelDirectory);
            if (model.ProfileFingerprint != "9411a2620fc30e348aa80c9d4e54ca0db5a00d94a92175c82ccdd47ad03b13e1")
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
