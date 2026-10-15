using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CineKros.Catalog.Importer;
using CineKros.Embedding;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("CineKros.VectorImporter.Tests")]

namespace CineKros.VectorImporter;

public sealed record ActorVectorRecordV6(long MovieLensId, string Fingerprint, string SemanticTextSha256, float[] Values);

public sealed class ActorVectorArtifactV6
{
    internal ActorVectorArtifactV6(string language, string artifactSha256, string manifestSha256,
        IReadOnlyList<ActorVectorRecordV6> records)
    {
        Language = language;
        ArtifactSha256 = artifactSha256;
        ManifestSha256 = manifestSha256;
        Records = records;
    }

    public string Language { get; }
    public string ArtifactSha256 { get; }
    public string ManifestSha256 { get; }
    public IReadOnlyList<ActorVectorRecordV6> Records { get; }
}

/// <summary>Strict read-only validation for the immutable approved V6 EN/SR artifacts.</summary>
public static class ActorVectorArtifactV6Validator
{
    internal static Task<ActorVectorArtifactV6> LoadFixtureAsync(string directory, ActorCatalogV6Document catalog, string language) =>
        LoadCoreAsync(directory, catalog, language, default, false);
    private const string ArtifactFormat = "multilingual-actor-v6-document-vectors-jsonl-v1";
    private const string NormalizationVersion = "unicode-nfc-case-preserving-v1";
    private const string FingerprintAlgorithm = "multilingual-document-fingerprint-v1";
    private static readonly string[] ManifestFields =
    [
        "format", "language", "catalogVersion", "catalogSha256", "sourceCatalogSha256", "sourceContentFingerprint",
        "catalogContentIdentitySha256", "selectionIdSetSha256", "corpusSha256", "translationDictionarySha256",
        "textFormatVersion", "inputNormalizationVersion", "profile", "profileFingerprint", "modelId", "modelRevision",
        "onnxSha256", "tokenizerSha256", "dimension", "documentPrefix", "fingerprintAlgorithm", "outputSha256",
        "recordCount", "order", "failedRecords", "truncatedRecords", "batchSize", "elapsedMilliseconds", "pooling",
        "normalization", "inferenceShapePolicy", "actorContext"
    ];
    private static readonly string[] VectorFields = ["movieLensId", "fingerprint", "semanticTextSha256", "vector"];

    public static async Task<ActorVectorArtifactV6> LoadAsync(string artifactDirectory,
        ActorCatalogV6Document catalog, string language, CancellationToken cancellationToken = default)
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

    private static async Task<ActorVectorArtifactV6> LoadCoreAsync(string directory,
        ActorCatalogV6Document catalog, string language, CancellationToken cancellationToken, bool production = true)
    {
        var vectorsPath = Path.Combine(directory, "document-vectors.jsonl");
        var manifestPath = Path.Combine(directory, "manifest.json");
        if (!Directory.Exists(directory) || Directory.GetFileSystemEntries(directory).Length != 2 || !File.Exists(vectorsPath) || !File.Exists(manifestPath)) throw new InvalidDataException("Full bilingual artifact files are missing.");
        var profile = EmbeddingProfileDescriptor.MultilingualE5Base;
        var profileFingerprint = catalog.ProfileFingerprint;
        if (profileFingerprint != profile.ProfileFingerprint || production && (catalog.Movies.Count != 9730 || catalog.CatalogSha256 != ActorCatalogV6Reader.CatalogSha256 || catalog.IdentitySha256 != ActorCatalogV6Reader.IdentitySha256))
            throw new InvalidDataException("Full catalog object does not match the locked complete multilingual profile and count.");
        var textFormat = language == "en" ? catalog.EnTextFormatVersion : catalog.SrTextFormatVersion;
        var corpus = language == "en" ? catalog.EnCorpusSha256 : catalog.SrCorpusSha256;
        var dictionary = language == "en" ? null : catalog.DictionarySha256;
        var vectorsHash = HashFile(vectorsPath);
        var vectorBytes = await File.ReadAllBytesAsync(vectorsPath, cancellationToken);
        _ = new UTF8Encoding(false, true).GetString(vectorBytes);
        if (vectorBytes.Length == 0 || vectorBytes[0] != (byte)'{' || vectorBytes[^1] != (byte)'\n' || vectorBytes.Contains((byte)'\r'))
            throw new InvalidDataException("V6 vector artifact must be UTF-8 without BOM and compact LF JSONL with final LF.");
        var manifestHash = HashFile(manifestPath);
        using (var document = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath, cancellationToken)))
        {
            var manifest = ActorCatalogV6Reader.ReadStrictObject(await File.ReadAllBytesAsync(manifestPath, cancellationToken));
            ValidateActorContext(manifest.GetProperty("actorContext"), catalog);
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
                String(manifest, "outputSha256") != vectorsHash || Integer(manifest, "recordCount") != catalog.Movies.Count ||
                String(manifest, "order") != "movieLensId ascending" || Integer(manifest, "failedRecords") != 0 ||
                Integer(manifest, "truncatedRecords") != 0 || Integer(manifest, "batchSize") != 1 || Long(manifest, "elapsedMilliseconds") < 0 ||
                String(manifest, "pooling") != profile.Pooling || String(manifest, "normalization") != profile.Normalization ||
                String(manifest, "inferenceShapePolicy") != profile.InferenceShapePolicy)
                throw new InvalidDataException("Full bilingual manifest does not match the exact catalog, language, profile or vector artifact.");
        }

        var movies = catalog.Movies;
        var records = new List<ActorVectorRecordV6>(catalog.Movies.Count);
        using (var reader = new StreamReader(vectorsPath, new UTF8Encoding(false, true)))
        {
            string? line;
            for (var index = 0; (line = await reader.ReadLineAsync(cancellationToken)) is not null; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (index >= movies.Count) throw new InvalidDataException("Full bilingual artifact contains extra vector rows.");
                var row = ActorCatalogV6Reader.ReadStrictObject(Encoding.UTF8.GetBytes(line));
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
        if (records.Count != catalog.Movies.Count || !records.Select(x => x.MovieLensId).SequenceEqual(movies.Select(x => x.MovieLensId)))
            throw new InvalidDataException("Full bilingual artifact does not contain the exact complete catalog ID set.");
        return new ActorVectorArtifactV6(language, vectorsHash, manifestHash, records.AsReadOnly());
    }


    private static void ValidateActorContext(JsonElement context, ActorCatalogV6Document catalog)
    {
        if (!context.EnumerateObject().Select(x => x.Name).SequenceEqual(new[] { "catalogSchemaVersion", "actorVocabularySha256", "orderedCastMappingSha256", "overridesSha256", "castRulesSha256", "dictionarySha256" }) ||
            String(context, "catalogSchemaVersion") != "actor-catalog-manifest-v1" ||
            String(context, "actorVocabularySha256") != catalog.ActorVocabularySha256 ||
            String(context, "orderedCastMappingSha256") != catalog.OrderedCastMappingSha256 ||
            String(context, "overridesSha256") != catalog.OverridesSha256 || String(context, "castRulesSha256") != catalog.CastRulesSha256 ||
            String(context, "dictionarySha256") != catalog.DictionarySha256) throw new InvalidDataException("V6 actor/dictionary context mismatch.");
    }

    public static Task ValidatePairAsync(string root, ActorCatalogV6Document catalog, CancellationToken token = default) => ValidatePairCoreAsync(root, catalog, token, true);
    internal static Task ValidateFixturePairAsync(string root, ActorCatalogV6Document catalog) => ValidatePairCoreAsync(root, catalog, default, false);
    private static async Task ValidatePairCoreAsync(string root, ActorCatalogV6Document catalog, CancellationToken token, bool production)
    {
        if (!Path.IsPathFullyQualified(root)) throw new ArgumentException("V6 paired root must be absolute.");
        var en = await LoadCoreAsync(Path.Combine(root, "en", "published"), catalog, "en", token, production);
        var sr = await LoadCoreAsync(Path.Combine(root, "sr", "published"), catalog, "sr", token, production);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(root, "paired-release.json"), token);
        _ = ActorCatalogV6Reader.ReadStrictObject(bytes);
        var expected = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = "actor-v6-paired-release-v1", catalogVersion = catalog.CatalogVersion, catalogSha256 = catalog.CatalogSha256,
            catalogIdentitySha256 = catalog.IdentitySha256, selectionIdSetSha256 = catalog.SelectionIdSetSha256,
            dictionarySha256 = catalog.DictionarySha256, actorVocabularySha256 = catalog.ActorVocabularySha256, orderedCastMappingSha256 = catalog.OrderedCastMappingSha256,
            overridesSha256 = catalog.OverridesSha256, castRulesSha256 = catalog.CastRulesSha256, profileFingerprint = catalog.ProfileFingerprint,
            languages = new[] { en, sr }.Select(artifact => new { language = artifact.Language,
                corpusSha256 = artifact.Language == "en" ? catalog.EnCorpusSha256 : catalog.SrCorpusSha256,
                textFormatVersion = artifact.Language == "en" ? catalog.EnTextFormatVersion : catalog.SrTextFormatVersion,
                recordCount = catalog.Movies.Count, vectorsSha256 = artifact.ArtifactSha256, manifestSha256 = artifact.ManifestSha256 }).ToArray()
        });
        if (!bytes.SequenceEqual(expected)) throw new InvalidDataException("V6 paired descriptor differs from independently validated publications.");
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
