using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CineKros.Api.Search;

namespace CineKros.Api.RealProviders;

/// <summary>Gemini structured-output adapter. It is intentionally not registered by the fake API host.</summary>
public sealed class GeminiRealQueryParser(HttpClient httpClient, string apiKey, string systemInstruction, string responseSchema, RealParsedQueryValidator validator, ILogger<GeminiRealQueryParser>? logger = null, bool isDevelopment = false, bool languageAware = false)
{
    public const string Model = "gemini-3.1-flash-lite";
    private static readonly Uri BaseUri = new("https://generativelanguage.googleapis.com/v1beta/models/", UriKind.Absolute);

    public async Task<RealParserResult> ParseAsync(string language, string message, CancellationToken cancellationToken) =>
        (await ParseWithEvidenceAsync(language, message, cancellationToken)).Result;

    /// <summary>Runs the same parser path while returning only validated checklist evidence for offline evaluation.</summary>
    public async Task<GeminiParserEvidenceResult> ParseWithEvidenceAsync(string language, string message, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var schema = JsonDocument.Parse(responseSchema);
            var body = new
            {
                systemInstruction = new { parts = new[] { new { text = systemInstruction } } },
                contents = new[] { new { role = "user", parts = new[] { new { text = JsonSerializer.Serialize(new { language, message }) } } } },
                generationConfig = new { responseMimeType = "application/json", responseSchema = schema.RootElement.Clone() }
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{BaseUri}{Model}:generateContent", UriKind.Absolute)) { Content = JsonContent.Create(body) };
            request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
            var providerTimer = System.Diagnostics.Stopwatch.StartNew();
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                LogDevelopmentFailure($"HTTP status {(int)response.StatusCode}");
                throw new RealProviderException("PROVIDER_UNAVAILABLE");
            }
            var providerJson = await response.Content.ReadAsStringAsync(timeout.Token);
            providerTimer.Stop();
            var structuredText = ExtractText(providerJson);
            string checklistEvidence;
            var parsed = languageAware
                ? validator.ValidateV5(structuredText, ParseSelectedLanguage(language), out checklistEvidence)
                : validator.Validate(structuredText, out checklistEvidence);
            if (isDevelopment)
            {
                logger?.LogInformation("Validated Gemini provider checklist (Development): {ProviderChecklistEvidence}", checklistEvidence);
                if (parsed.Query is not null)
                    logger?.LogInformation("Validated parser DTO (Development): {CanonicalQuery}", JsonSerializer.Serialize(parsed.Query.HardFilters, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            }
            return new GeminiParserEvidenceResult(parsed, checklistEvidence, providerTimer.Elapsed);
        }
        catch (RealProviderException ex)
        {
            if (ex.Code == "PARSER_INVALID_RESPONSE") LogDevelopmentFailure("malformed response/validation");
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            LogDevelopmentFailure("timeout");
            throw new RealProviderException("PROVIDER_UNAVAILABLE");
        }
        catch (JsonException)
        {
            LogDevelopmentFailure("malformed response/validation");
            throw new RealProviderException("PARSER_INVALID_RESPONSE");
        }
        catch (HttpRequestException)
        {
            LogDevelopmentFailure("network exception HttpRequestException");
            throw new RealProviderException("PROVIDER_UNAVAILABLE");
        }
        catch (Exception)
        {
            LogDevelopmentFailure("other sanitized adapter failure");
            throw new RealProviderException("PROVIDER_UNAVAILABLE");
        }
    }

    private static SearchLanguage ParseSelectedLanguage(string language) => language switch
    {
        "en" => SearchLanguage.English,
        "sr" => SearchLanguage.Serbian,
        _ => throw new RealProviderException("PARSER_INVALID_RESPONSE")
    };

    private void LogDevelopmentFailure(string cause)
    {
        if (isDevelopment)
            logger?.LogWarning("Gemini HTTP/parser Development diagnostic: cause={Cause}", cause);
    }

    private static string ExtractText(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("candidates", out var candidates) || candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() != 1) throw new RealProviderException("PARSER_INVALID_RESPONSE");
        if (!candidates[0].TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object || !content.TryGetProperty("parts", out var parts)) throw new RealProviderException("PARSER_INVALID_RESPONSE");
        if (parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() != 1 || !parts[0].TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) throw new RealProviderException("PARSER_INVALID_RESPONSE");
        return text.GetString()!;
    }
}

public sealed record GeminiParserEvidenceResult(RealParserResult Result, string ProviderChecklistJson, TimeSpan ProviderDuration);
