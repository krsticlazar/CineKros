using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using CineKros.Catalog.Importer;

namespace CineKros.VectorImporter;

public sealed record VectorRecord(long MovieLensId, string Fingerprint, string SemanticTextSha256, float[] Values);

public sealed class VectorArtifact
{
    internal VectorArtifact(string artifactSha256, IReadOnlyList<VectorRecord> records) { ArtifactSha256 = artifactSha256; Records = records; }
    public string ArtifactSha256 { get; }
    public IReadOnlyList<VectorRecord> Records { get; }
}

public static class VectorArtifactValidator
{
    public const string Profile = "e5-base-v2-int8-onnx-v1";
    public const string ModelId = "intfloat/e5-base-v2";
    public const string ModelRevision = "f52bf8ec8c7124536f0efb74aca902b2995e5bcd";
    public const string OnnxSha256 = "f2ff55f62dfca9ce0f4a5656ae0b1571b9fbc5e15eda3b2c56dd32f329b2005e";
    public const string TokenizerSha256 = "d241a60d5e8f04cc1b2b3e9ef7a4921b27bf526d9f6050ab90f9267a1f9e5c66";
    public const string CatalogFingerprint = "2ffad7ba703cb80543db617e742a61c88871332910185767ee96fe08a77a0be7";
    public const string ProfileFingerprint = "9411a2620fc30e348aa80c9d4e54ca0db5a00d94a92175c82ccdd47ad03b13e1";
    public const int Dimension = 768;
    private const string ArtifactFormat = "e5-document-vectors-jsonl-v1";
    private const string DocumentFormat = "e5-passage-semantictext-v1";
    private static readonly JsonSerializerOptions FingerprintOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly string[] ManifestFields = ["format", "catalogVersion", "catalogSha256", "catalogContentFingerprint", "profile", "profileFingerprint", "modelId", "modelRevision", "onnxSha256", "tokenizerSha256", "dimension", "documentInputFormat", "fingerprintAlgorithm", "pooling", "normalization", "maxTokens", "outputSha256", "recordCount", "order", "failedRecords", "truncatedRecords", "batchSize", "elapsedMilliseconds", "provenance"];

    public static async Task<VectorArtifact> LoadAsync(string artifactDirectory, string catalogPath, CancellationToken cancellationToken = default)
    {
        try { return await LoadCoreAsync(artifactDirectory, catalogPath, cancellationToken); }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or DecoderFallbackException or OverflowException)
        { throw new InvalidDataException("Vector artifact is malformed.", ex); }
    }

    private static async Task<VectorArtifact> LoadCoreAsync(string artifactDirectory, string catalogPath, CancellationToken cancellationToken)
    {
        var catalog = await CatalogValidator.LoadAsync(catalogPath, cancellationToken);
        var source = new Dictionary<long, string>(CatalogValidator.ExpectedCount);
        using (var reader = new StreamReader(catalogPath, new UTF8Encoding(false, true)))
        {
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
            {
                using var doc = JsonDocument.Parse(line);
                var row = doc.RootElement;
                source.Add(row.GetProperty("movieLensId").GetInt64(), row.GetProperty("semanticText").GetString()!);
            }
        }

        var manifestPath = Path.Combine(artifactDirectory, "manifest.json");
        var vectorsPath = Path.Combine(artifactDirectory, "document-vectors.jsonl");
        if (!File.Exists(manifestPath) || !File.Exists(vectorsPath)) throw new InvalidDataException("Complete E5 vector artifact files are missing.");
        var outputHash = HashFile(vectorsPath);
        try
        {
            using var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath, cancellationToken));
            var m = manifest.RootElement;
            RejectFakeMarker(m);
            var names = m.ValueKind == JsonValueKind.Object ? m.EnumerateObject().Select(p => p.Name).ToArray() : [];
            if (!names.SequenceEqual(ManifestFields, StringComparer.Ordinal) ||
                String(m, "format") != ArtifactFormat || String(m, "catalogVersion") != CatalogValidator.CatalogVersion ||
                String(m, "catalogSha256") != catalog.JsonlHash || String(m, "catalogContentFingerprint") != CatalogFingerprint ||
                String(m, "profile") != Profile || String(m, "profileFingerprint") != ProfileFingerprint ||
                String(m, "modelId") != ModelId || String(m, "modelRevision") != ModelRevision ||
                String(m, "onnxSha256") != OnnxSha256 || String(m, "tokenizerSha256") != TokenizerSha256 ||
                Int(m, "dimension") != Dimension || String(m, "documentInputFormat") != DocumentFormat ||
                String(m, "fingerprintAlgorithm") != "sha256-compact-json-e5-v1" || String(m, "pooling") != "mask-mean-v1" ||
                String(m, "normalization") != "l2-f32-v1" || Int(m, "maxTokens") != 512 ||
                String(m, "outputSha256") != outputHash || Int(m, "recordCount") != CatalogValidator.ExpectedCount ||
                String(m, "order") != "movieLensId ascending" || Int(m, "failedRecords") != 0 ||
                Int(m, "truncatedRecords") is < 0 or > CatalogValidator.ExpectedCount ||
                Int(m, "batchSize") is < 1 or > 128 || Long(m, "elapsedMilliseconds") < 0 ||
                string.IsNullOrWhiteSpace(String(m, "provenance")))
                throw new InvalidDataException("Vector manifest does not match the complete locked E5 artifact contract.");
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new InvalidDataException("Vector manifest is malformed.", ex); }

        var records = new List<VectorRecord>(CatalogValidator.ExpectedCount);
        using (var reader = new StreamReader(vectorsPath, new UTF8Encoding(false, true)))
        {
            string? line; long previous = 0;
            while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var doc = JsonDocument.Parse(line);
                var row = doc.RootElement;
                var properties = row.ValueKind == JsonValueKind.Object ? row.EnumerateObject().Select(p => p.Name).ToArray() : [];
                if (!properties.SequenceEqual(["movieLensId", "fingerprint", "semanticTextSha256", "vector"], StringComparer.Ordinal))
                    throw new InvalidDataException("Vector row fields do not match the E5 artifact format.");
                var id = Long(row, "movieLensId");
                if (id <= previous || !source.TryGetValue(id, out var semanticText)) throw new InvalidDataException("Vector artifact IDs are duplicate, out of order or outside the catalog.");
                previous = id;
                var expectedFingerprint = Fingerprint(semanticText);
                var textHash = Hash(Encoding.UTF8.GetBytes(semanticText));
                if (String(row, "fingerprint") != expectedFingerprint || String(row, "semanticTextSha256") != textHash)
                    throw new InvalidDataException($"E5 fingerprint validation failed for movieLensId {id}.");
                var vectorNode = row.GetProperty("vector");
                if (vectorNode.ValueKind != JsonValueKind.Array || vectorNode.GetArrayLength() != Dimension)
                    throw new InvalidDataException($"Vector dimension validation failed for movieLensId {id}.");
                var values = new float[Dimension]; var nonzero = false; var index = 0; double normSquared = 0;
                foreach (var value in vectorNode.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.Number || !value.TryGetSingle(out var f) || !float.IsFinite(f))
                        throw new InvalidDataException($"Vector numeric validation failed for movieLensId {id}.");
                    values[index++] = f; nonzero |= f != 0; normSquared += (double)f * f;
                }
                if (!nonzero || !double.IsFinite(normSquared) || Math.Abs(Math.Sqrt(normSquared) - 1d) > 0.001)
                    throw new InvalidDataException($"Vector must be nonzero and L2-normalized for movieLensId {id}.");
                records.Add(new(id, expectedFingerprint, textHash, values));
            }
        }
        if (records.Count != CatalogValidator.ExpectedCount || !records.Select(r => r.MovieLensId).SequenceEqual(source.Keys.Order()))
            throw new InvalidDataException("Vector artifact does not contain the exact complete catalog ID set.");
        return new VectorArtifact(outputHash, records.AsReadOnly());
    }

    internal static string Fingerprint(string semanticText)
    {
        var payload = JsonSerializer.Serialize(new DocumentFingerprint($"passage: {semanticText}", ModelId, ModelRevision,
            OnnxSha256, TokenizerSha256, Dimension, "passage", DocumentFormat, 512, "mask-mean-v1", "l2-f32-v1"), FingerprintOptions);
        return Hash(Encoding.UTF8.GetBytes(payload));
    }

    private static void RejectFakeMarker(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String && value.GetString()!.Contains("FAKE", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Fake-only vector artifacts cannot be imported.");
        if (value.ValueKind == JsonValueKind.Object)
            foreach (var p in value.EnumerateObject()) { if (p.Name.Contains("fake", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Fake-only vector artifacts cannot be imported."); RejectFakeMarker(p.Value); }
        if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectFakeMarker(item);
    }
    private static string String(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new InvalidDataException("Vector manifest string field is malformed.");
    private static int Int(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.TryGetInt32(out var i) ? i : throw new InvalidDataException("Vector manifest numeric field is malformed.");
    private static long Long(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.TryGetInt64(out var i) ? i : throw new InvalidDataException("Vector row ID is malformed.");
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private sealed record DocumentFingerprint(string FormattedText, string ModelId, string Revision, string OnnxSha256, string TokenizerSha256, int Dimension, string Task, string FormattingVersion, int MaxTokens, string Pooling, string Normalization);
}
