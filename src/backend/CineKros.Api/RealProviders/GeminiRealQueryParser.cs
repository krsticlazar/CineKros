using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace CineKros.Api.RealProviders;

/// <summary>Gemini structured-output adapter. It is intentionally not registered by the fake API host.</summary>
public sealed class GeminiRealQueryParser(HttpClient httpClient, string apiKey, string systemInstruction, string responseSchema, RealParsedQueryValidator validator, ILogger<GeminiRealQueryParser>? logger = null, bool isDevelopment = false)
{
    public const string Model = "gemini-3.1-flash-lite";
    private static readonly Uri BaseUri = new("https://generativelanguage.googleapis.com/v1beta/models/", UriKind.Absolute);

    public async Task<RealParserResult> ParseAsync(string language, string message, CancellationToken cancellationToken)
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
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode) throw new RealProviderException("PROVIDER_UNAVAILABLE");
            var providerJson = await response.Content.ReadAsStringAsync(timeout.Token);
            var structuredText = ExtractText(providerJson);
            var parsed = validator.Validate(structuredText, out var checklistEvidence);
            if (isDevelopment)
            {
                logger?.LogInformation("Validated Gemini provider checklist (Development): {ProviderChecklistEvidence}", checklistEvidence);
                if (parsed.Query is not null)
                    logger?.LogInformation("Validated parser DTO (Development): {CanonicalQuery}", JsonSerializer.Serialize(parsed.Query.HardFilters, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            }
            return parsed;
        }
        catch (RealProviderException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { throw new RealProviderException("PROVIDER_UNAVAILABLE"); }
        catch (JsonException) { throw new RealProviderException("PARSER_INVALID_RESPONSE"); }
        catch (HttpRequestException) { throw new RealProviderException("PROVIDER_UNAVAILABLE"); }
        catch (Exception) { throw new RealProviderException("PROVIDER_UNAVAILABLE"); }
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
