using System.Text.Json;
using System.Text.Json.Serialization;
using CineKros.Api.RealProviders;

namespace CineKros.Api.Diagnostics;

public static class DevelopmentRequestSummary
{
    public static string Format(string originalQuery, RealParserResult validatedParserResult, long durationMs, string code)
    {
        var parserJson = JsonSerializer.Serialize(validatedParserResult, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
        return $"Recommendation request (Development)\nQuery: {originalQuery}\nValidated parser DTO:\n{parserJson}\nDuration: {durationMs} ms\nCode: {code}";
    }

    public static void WriteIfDevelopment(bool isDevelopment, string originalQuery, RealParserResult validatedParserResult, long durationMs, string code)
    {
        if (isDevelopment)
            Console.WriteLine(Format(originalQuery, validatedParserResult, durationMs, code));
    }

    public static void WriteFailureIfDevelopment(bool isDevelopment, string originalQuery, long durationMs, string code)
    {
        if (isDevelopment)
            Console.WriteLine($"Recommendation request (Development)\nQuery: {originalQuery}\nParser DTO: unavailable (rejected before validation)\nDuration: {durationMs} ms\nCode: {code}");
    }

    public static void LogCompletion(ILogger logger, bool isDevelopment, string correlationId, long durationMs, string code)
    {
        if (isDevelopment)
            logger.LogDebug("Recommendation request completed. CorrelationId={CorrelationId} ContractVersion=v2.0.0 StageDurationMs={StageDurationMs} Code={Code}", correlationId, durationMs, code);
        else
            logger.LogInformation("Recommendation request completed. CorrelationId={CorrelationId} ContractVersion=v2.0.0 StageDurationMs={StageDurationMs} Code={Code}", correlationId, durationMs, code);
    }
}
