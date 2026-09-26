using CineKros.Api;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(new MovieQueryPrompt(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Prompts", "movie-query-parser.md"))));
builder.Services.AddSingleton<IQueryParser, FakeQueryParser>();
builder.Services.AddSingleton<IMovieSearch, FakeMovieSearch>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, _) => await context.HttpContext.Response.WriteAsJsonAsync(new { error = new { code = ApiErrorCodes.RateLimited } });
    options.AddPolicy("recommendations", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromSeconds(60), QueueLimit = 0, AutoReplenishment = true }));
});

var app = builder.Build();
app.UseRateLimiter();
app.MapPost("/api/recommendations", RecommendationEndpoint.HandleAsync).RequireRateLimiting("recommendations");
app.Run();

public partial class Program;
