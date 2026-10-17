using CineKros.Api;
using CineKros.Api.RealFlow;
using CineKros.Api.Startup;
using CineKros.Api.Search;
using CineKros.Embedding;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var recommendationMode = RecommendationStartup.ResolveMode(builder.Environment);
var serbianPocMode = RecommendationStartup.ResolveSerbianPocMode(builder.Environment, recommendationMode);
RecommendationStartup.RegisterServices(builder.Services, recommendationMode, serbianPocMode, builder.Environment);
builder.Services.AddSingleton<StartupReadinessState>();
ProductionHttpConfiguration.AddRecommendationCors(builder.Services, builder.Configuration, builder.Environment);
var trustedClientIp = TrustedClientIp.Parse(builder.Configuration);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, _) => await context.HttpContext.Response.WriteAsJsonAsync(new { error = new { code = ApiErrorCodes.RateLimited } });
    options.AddPolicy("recommendations", context => RateLimitPartition.GetFixedWindowLimiter(
        trustedClientIp.SelectClientIp(context.Connection.RemoteIpAddress, context.Request.Headers["CF-Connecting-IP"]) ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromSeconds(60), QueueLimit = 0, AutoReplenishment = true }));
});

var app = builder.Build();
if (recommendationMode == "real")
{
    _ = app.Services.GetRequiredService<E5EmbeddingModel>();
    await app.Services.GetRequiredService<MovieSearchRepository>().EnsureRuntimeReadyAsync();
    app.Services.GetRequiredService<StartupReadinessState>().MarkReady();
}
app.UseCors(ProductionHttpConfiguration.RecommendationCorsPolicy);
app.UseRateLimiter();
app.MapGet("/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/ready", (StartupReadinessState state) => state.IsReady
    ? Results.Ok(new { status = "ready" })
    : Results.Json(new { status = "not_ready" }, statusCode: StatusCodes.Status503ServiceUnavailable));
if (recommendationMode == "real")
    app.MapPost("/api/recommendations", RealRecommendationEndpoint.HandleAsync).RequireRateLimiting("recommendations");
else
    app.MapPost("/api/recommendations", RecommendationEndpoint.HandleAsync).RequireRateLimiting("recommendations");
app.Run();

public partial class Program;
