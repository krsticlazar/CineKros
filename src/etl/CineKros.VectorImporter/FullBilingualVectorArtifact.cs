using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CineKros.Catalog.Importer;
using CineKros.Embedding;

namespace CineKros.VectorImporter;

public sealed record FullBilingualVectorRecord(long MovieLensId, string Fingerprint, string SemanticTextSha256, float[] Values);

public sealed class FullBilingualVectorArtifact
{
    internal FullBilingualVectorArtifact(string language, string artifactSha256, string manifestSha256,
        IReadOnlyList<FullBilingualVectorRecord> records)
    {
        Language = language;
        ArtifactSha256 = artifactSha256;
        ManifestSha256 = manifestSha256;
        Records = records;
    }

    public string Language { get; }
    public string ArtifactSha256 { get; }
    public string ManifestSha256 { get; }
    public IReadOnlyList<FullBilingualVectorRecord> Records { get; }
}

/// <summary>Strict read-only validation for the locked complete Phase 8 EN/SR artifacts.</summary>
public static class FullBilingualVectorArtifactValidator
{
    private const string ArtifactFormat = "multilingual-full-document-vectors-jsonl-v1";
    private const string NormalizationVersion = "unicode-nfc-case-preserving-v1";
    private const string FingerprintAlgorithm = "multilingual-document-fingerprint-v1";
    private static readonly string[] ManifestFields =
    [
        "format", "language", "catalogVersion", "catalogSha256", "sourceCatalogSha256", "sourceContentFingerprint",
        "catalogContentIdentitySha256", "selectionIdSetSha256", "corpusSha256", "translationDictionarySha256",
        "textFormatVersion", "inputNormalizationVersion", "profile", "profileFingerprint", "modelId", "modelRevision",
        "onnxSha256", "tokenizerSha256", "dimension", "documentPrefix", "fingerprintAlgorithm", "outputSha256",
        "recordCount", "order", "failedRecords", "truncatedRecords", "batchSize", "elapsedMilliseconds", "pooling",
        "normalization", "inferenceShapePolicy"
    ];
    private static readonly string[] VectorFields = ["movieLensId", "fingerprint", "semanticTextSha256", "vector"];
    private static readonly JsonSerializerOptions FingerprintOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task<FullBilingualVectorArtifact> LoadAsync(string artifactDirectory,
        FullBilingualCatalogDocument catalog, string language, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        if (language is not ("en" or "sr")) throw new ArgumentException("Language must be the trusted value en or sr.", nameof(language));
        if (!Path.IsPathFullyQualified(artifactDirectory)) throw new ArgumentException("Artifact directory must be absolute.", nameof(artifactDirectory));
        try
        {
            return await LoadCoreAsync(Path.GetFullPath(artifactDirectory), catalog, language, cancellationToken);
        }
        catch (InvalidDataException) { throw; }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or DecoderFallbackException or OverflowException)
        {
            throw new InvalidDataException("Full bilingual vector artifact is malformed.", ex);
        }
    }

    private static async Task<FullBilingualVectorArtifact> LoadCoreAsync(string directory,
        FullBilingualCatalogDocument catalog, string language, CancellationToken cancellationToken)
    {
        var vectorsPath = Path.Combine(directory, "document-vectors.jsonl");
        var manifestPath = Path.Combine(directory, "manifest.json");
        if (!File.Exists(vectorsPath) || !File.Exists(manifestPath)) throw new InvalidDataException("Full bilingual artifact files are missing.");
        var profile = EmbeddingProfileDescriptor.MultilingualE5Base;
        var profileFingerprint = catalog.ProfileFingerprint;
        if (profileFingerprint != profile.ProfileFingerprint || catalog.Movies.Count != FullBilingualCatalog.ExpectedMovieCount)
            throw new InvalidDataException("Full catalog object does not match the locked complete multilingual profile and count.");
        var textFormat = language == "en" ? catalog.EnTextFormatVersion : catalog.SrTextFormatVersion;
        var corpus = language == "en" ? catalog.EnCorpusSha256 : catalog.SrCorpusSha256;
        var dictionary = language == "en" ? null : catalog.DictionarySha256;
        var vectorsHash = HashFile(vectorsPath);
        var manifestHash = HashFile(manifestPath);
        using (var document = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath, cancellationToken)))
        {
            var manifest = document.RootElement;
            var names = manifest.ValueKind == JsonValueKind.Object ? manifest.EnumerateObject().Select(x => x.Name).ToArray() : [];
            if (!names.SequenceEqual(ManifestFields, StringComparer.Ordinal) ||
                String(manifest, "format") != ArtifactFormat || String(manifest, "language") != language ||
                String(manifest, "catalogVersion") != catalog.CatalogVersion || String(manifest, "catalogSha256") != catalog.CatalogSha256 ||
                String(manifest, "sourceCatalogSha256") != catalog.SourceCatalogSha256 || String(manifest, "sourceContentFingerprint") != catalog.SourceContentFingerprint ||
                String(manifest, "catalogContentIdentitySha256") != catalog.IdentitySha256 || String(manifest, "selectionIdSetSha256") != catalog.SelectionIdSetSha256 ||
                String(manifest, "corpusSha256") != corpus || !NullOrStringEquals(manifest, "translationDictionarySha256", dictionary) ||
                String(manifest, "textFormatVersion") != textFormat || String(manifest, "inputNormalizationVersion") != NormalizationVersion ||
                String(manifest, "profile") != profile.ProfileVersion || String(manifest, "profileFingerprint") != profileFingerprint ||
                String(manifest, "modelId") != profile.ModelId || String(manifest, "modelRevision") != profile.Revision ||
                String(manifest, "onnxSha256") != profile.Artifact("model_qint8_avx512_vnni.onnx").Sha256 ||
                String(manifest, "tokenizerSha256") != profile.Artifact("tokenizer.json").Sha256 || Integer(manifest, "dimension") != profile.Dimension ||
                String(manifest, "documentPrefix") != "passage: " || String(manifest, "fingerprintAlgorithm") != FingerprintAlgorithm ||
                String(manifest, "outputSha256") != vectorsHash || Integer(manifest, "recordCount") != FullBilingualCatalog.ExpectedMovieCount ||
                String(manifest, "order") != "movieLensId ascending" || Integer(manifest, "failedRecords") != 0 ||
                Integer(manifest, "truncatedRecords") != 0 || Integer(manifest, "batchSize") != 1 || Long(manifest, "elapsedMilliseconds") < 0 ||
                String(manifest, "pooling") != profile.Pooling || String(manifest, "normalization") != profile.Normalization ||
                String(manifest, "inferenceShapePolicy") != profile.InferenceShapePolicy)
                throw new InvalidDataException("Full bilingual manifest does not match the exact catalog, language, profile or vector artifact.");
        }

        var movies = catalog.Movies;
        var records = new List<FullBilingualVectorRecord>(FullBilingualCatalog.ExpectedMovieCount);
        using (var reader = new StreamReader(vectorsPath, new UTF8Encoding(false, true)))
        {
            string? line;
            for (var index = 0; (line = await reader.ReadLineAsync(cancellationToken)) is not null; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (index >= movies.Count) throw new InvalidDataException("Full bilingual artifact contains extra vector rows.");
                using var rowDocument = JsonDocument.Parse(line);
                var row = rowDocument.RootElement;
                var fields = row.ValueKind == JsonValueKind.Object ? row.EnumerateObject().Select(x => x.Name).ToArray() : [];
                if (!fields.SequenceEqual(VectorFields, StringComparer.Ordinal)) throw new InvalidDataException("Vector row fields do not match the locked full artifact schema.");
                var movie = movies[index];
                var id = Long(row, "movieLensId");
                var semanticText = language == "en" ? movie.SemanticText : movie.SemanticTextSr;
                var fingerprint = MultilingualPocCatalog.ComputeDocumentFingerprint(language, textFormat, semanticText);
                var textHash = Hash(Encoding.UTF8.GetBytes(semanticText));
                if (id != movie.MovieLensId || (index > 0 && id <= records[^1].MovieLensId) ||
                    String(row, "fingerprint") != fingerprint || String(row, "semanticTextSha256") != textHash)
                    throw new InvalidDataException($"Full bilingual vector identity mismatch at catalog row {index}.");
                var vector = ReadVector(row.GetProperty("vector"), id);
                records.Add(new(id, fingerprint, textHash, vector));
            }
        }
        if (records.Count != FullBilingualCatalog.ExpectedMovieCount || !records.Select(x => x.MovieLensId).SequenceEqual(movies.Select(x => x.MovieLensId)))
            throw new InvalidDataException("Full bilingual artifact does not contain the exact complete catalog ID set.");
        return new FullBilingualVectorArtifact(language, vectorsHash, manifestHash, records.AsReadOnly());
    }

    private static float[] ReadVector(JsonElement node, long id)
    {
        if (node.ValueKind != JsonValueKind.Array || node.GetArrayLength() != EmbeddingProfileDescriptor.MultilingualE5Base.Dimension)
            throw new InvalidDataException($"Vector dimension mismatch for movieLensId {id}.");
        var values = new float[EmbeddingProfileDescriptor.MultilingualE5Base.Dimension];
        double normSquared = 0; var index = 0;
        foreach (var item in node.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetSingle(out var value) || !float.IsFinite(value))
                throw new InvalidDataException($"Vector contains a non-finite or non-float32 value for movieLensId {id}.");
            values[index++] = value;
            normSquared += (double)value * value;
        }
        if (!double.IsFinite(normSquared) || normSquared <= 0 || Math.Abs(Math.Sqrt(normSquared) - 1d) > 0.0001)
            throw new InvalidDataException($"Vector must be nonzero and L2-normalized within the locked tolerance for movieLensId {id}.");
        return values;
    }

    private static bool NullOrStringEquals(JsonElement element, string name, string? expected) =>
        element.TryGetProperty(name, out var value) && (expected is null ? value.ValueKind == JsonValueKind.Null : value.ValueKind == JsonValueKind.String && value.GetString() == expected);
    private static string String(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new InvalidDataException($"Manifest field {name} is missing or malformed.");
    private static int Integer(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : throw new InvalidDataException($"Manifest field {name} is missing or malformed.");
    private static long Long(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt64(out var parsed) ? parsed : throw new InvalidDataException($"Manifest field {name} is missing or malformed.");
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
}
