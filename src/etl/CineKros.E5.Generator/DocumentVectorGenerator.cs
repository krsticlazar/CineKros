using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CineKros.Catalog.Importer;
using CineKros.Embedding;

namespace CineKros.E5.Generator;

public interface IDocumentVectorSource
{
    IReadOnlyList<float[]> Embed(IReadOnlyList<string> semanticTexts, CancellationToken cancellationToken);
    string Fingerprint(string semanticText);
    string ProfileFingerprint { get; }
    EmbeddingProfileDescriptor ProfileDescriptor { get; }
    long TruncationCount { get; }
}

public sealed class E5DocumentVectorSource(E5EmbeddingModel model) : IDocumentVectorSource
{
    public IReadOnlyList<float[]> Embed(IReadOnlyList<string> semanticTexts, CancellationToken cancellationToken) => model.EmbedDocuments(semanticTexts, cancellationToken);
    public string Fingerprint(string semanticText) => model.ComputeDocumentFingerprint(semanticText);
    public string ProfileFingerprint => model.ProfileFingerprint;
    public EmbeddingProfileDescriptor ProfileDescriptor => model.ProfileDescriptor;
    public long TruncationCount => model.TruncationCount;
}

public sealed record RunResult(int Generated, int Reused, int RecordCount, long ElapsedMilliseconds);

public sealed record CatalogPolicy(string CatalogVersion, string CatalogSha256, string ContentFingerprint, int ExpectedCount)
{
    public const string VersionValue = "B05a-combined-catalog-v1";
    public const string CanonicalSha256 = "8b2bad0a22fef45842176a1d9f3730be1568e367b9fc230fa0aa398bb5c26946";
    public const string CanonicalFingerprint = "2ffad7ba703cb80543db617e742a61c88871332910185767ee96fe08a77a0be7";
    public const int CanonicalCount = 9730;
    public static CatalogPolicy Canonical { get; } = new(VersionValue, CanonicalSha256, CanonicalFingerprint, CanonicalCount);
    internal static CatalogPolicy ForTests(int count) => new(VersionValue, "*", new string('b', 64), count);
}

public static class DocumentVectorGenerator
{
    private const string CheckpointFormat = "e5-document-checkpoint-v1";
    private const string ManifestFormat = "e5-document-vectors-jsonl-v1";
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static RunResult Run(string catalogPath, string manifestPath, string checkpointPath, string outputDirectory,
        int batchSize, IDocumentVectorSource source, CancellationToken cancellationToken = default, Action? beforePublish = null) =>
        Run(catalogPath, manifestPath, checkpointPath, outputDirectory, batchSize, source, CatalogPolicy.Canonical, cancellationToken, beforePublish);

    internal static RunResult Run(string catalogPath, string manifestPath, string checkpointPath, string outputDirectory,
        int batchSize, IDocumentVectorSource source, CatalogPolicy policy, CancellationToken cancellationToken = default, Action? beforePublish = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (batchSize is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(batchSize), "Batch size must be between 1 and 128.");
        var profile = source.ProfileDescriptor;
        if (profile.InferenceShapePolicy == "single-sequence-unpadded-v1" && batchSize != 1)
            throw new ArgumentException("The multilingual profile requires generator batch size 1.", nameof(batchSize));
        var timer = Stopwatch.StartNew();
        var catalogFullPath = Path.GetFullPath(catalogPath);
        var manifestFullPath = Path.GetFullPath(manifestPath);
        var checkpointFullPath = Path.GetFullPath(checkpointPath);
        var outputFullPath = Path.GetFullPath(outputDirectory);
        var rows = LoadCatalog(catalogFullPath, manifestFullPath, policy, cancellationToken);
        var expected = rows.ToDictionary(x => x.Id, x => new ExpectedVector(source.Fingerprint(x.SemanticText), HashUtf8(x.SemanticText)));
        var reusable = ReadCheckpoint(checkpointFullPath, profile, source.ProfileFingerprint, expected);
        var complete = new Dictionary<long, VectorRow>(reusable);
        var pending = rows.Where(x => !complete.ContainsKey(x.Id)).ToArray();
        var generated = 0;
        WriteCheckpoint(checkpointFullPath, profile, source.ProfileFingerprint, complete.Values.OrderBy(x => x.MovieLensId));

        foreach (var chunk in pending.Chunk(batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var vectors = source.Embed(chunk.Select(x => x.SemanticText).ToArray(), cancellationToken);
            if (vectors.Count != chunk.Length) throw new InvalidDataException("Embedding source returned a mismatched batch size.");
            for (var index = 0; index < chunk.Length; index++)
            {
                ValidateVector(vectors[index]);
                var expectedRow = expected[chunk[index].Id];
                complete[chunk[index].Id] = new VectorRow(chunk[index].Id, expectedRow.Fingerprint, expectedRow.SemanticTextSha256, vectors[index]);
            }
            generated += chunk.Length;
            AppendCheckpoint(checkpointFullPath, chunk.Select(x => complete[x.Id]));
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (complete.Count != policy.ExpectedCount || rows.Any(x => !complete.ContainsKey(x.Id)))
            throw new InvalidDataException("A final E5 artifact requires a compatible vector for every catalog row.");
        if (Directory.Exists(outputFullPath) || File.Exists(outputFullPath)) throw new IOException("Output directory already exists; choose a new run-id directory.");

        var finalRows = rows.Select(movie => complete[movie.Id]).ToArray();
        var jsonl = string.Concat(finalRows.Select(row => JsonSerializer.Serialize(row, JsonOptions) + "\n"));
        var jsonBytes = new UTF8Encoding(false).GetBytes(jsonl);
        var jsonHash = Convert.ToHexStringLower(SHA256.HashData(jsonBytes));
        timer.Stop();
        var onnxHash = profile.Artifact("model_qint8_avx512_vnni.onnx").Sha256;
        var tokenizerHash = profile.Artifact("tokenizer.json").Sha256;
        var legacy = ReferenceEquals(profile, EmbeddingProfileDescriptor.LegacyEnglish);
        var manifest = new FinalManifest(ManifestFormat, policy.CatalogVersion, policy.CatalogSha256 == "*" ? HashFile(catalogFullPath) : policy.CatalogSha256,
            policy.ContentFingerprint, profile.ProfileVersion, source.ProfileFingerprint, profile.ModelId, profile.Revision,
            onnxHash, tokenizerHash, profile.Dimension, "e5-passage-semantictext-v1", "sha256-compact-json-e5-v1", profile.Pooling, profile.Normalization,
            profile.MaxTokens, jsonHash, finalRows.Length, "movieLensId ascending", 0, checked((int)source.TruncationCount), batchSize, timer.ElapsedMilliseconds, "local offline generator",
            legacy ? null : profile.InferenceShapePolicy);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        beforePublish?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        Publish(outputFullPath, jsonBytes, manifestBytes);
        return new RunResult(generated, reusable.Count, finalRows.Length, timer.ElapsedMilliseconds);
    }

    private static IReadOnlyList<CatalogMovie> LoadCatalog(string path, string manifestPath, CatalogPolicy policy, CancellationToken ct)
    {
        if (policy.CatalogSha256 != "*")
        {
            if (!Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "manifest.json")).Equals(manifestPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Canonical catalog manifest must be the catalog's sibling manifest.json.");
            _ = CatalogValidator.LoadAsync(path, ct).GetAwaiter().GetResult();
        }
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var root = manifest.RootElement;
        if (RequiredString(root, "catalogVersion") != policy.CatalogVersion || RequiredString(root, "contentFingerprint") != policy.ContentFingerprint ||
            RequiredString(root, "output", "order") != "movieLensId ascending" || RequiredInt(root, "output", "recordCount") != policy.ExpectedCount ||
            RequiredString(root, "output", "sha256") != HashFile(path))
            throw new InvalidDataException("Catalog manifest identity or file hash is invalid.");
        var fileHash = HashFile(path);
        if (policy.CatalogSha256 != "*" && fileHash != policy.CatalogSha256) throw new InvalidDataException("Catalog is not the reviewed canonical B05-A artifact.");
        var result = new List<CatalogMovie>(policy.ExpectedCount);
        using var reader = new StreamReader(path, new UTF8Encoding(false, true));
        string? line; long previous = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            ct.ThrowIfCancellationRequested();
            using var item = JsonDocument.Parse(line);
            var e = item.RootElement;
            var id = e.GetProperty("movieLensId").GetInt64();
            var semanticText = e.GetProperty("semanticText").GetString();
            if (id <= previous || string.IsNullOrWhiteSpace(semanticText)) throw new InvalidDataException("Catalog IDs must be unique, ascending and have nonempty semanticText.");
            result.Add(new CatalogMovie(id, semanticText)); previous = id;
        }
        if (result.Count != policy.ExpectedCount) throw new InvalidDataException("Catalog record count does not match the reviewed manifest.");
        return result;
    }

    private static Dictionary<long, VectorRow> ReadCheckpoint(string path, EmbeddingProfileDescriptor descriptor, string profile, IReadOnlyDictionary<long, ExpectedVector> expected)
    {
        var result = new Dictionary<long, VectorRow>();
        if (!File.Exists(path)) return result;
        var lines = File.ReadAllLines(path, new UTF8Encoding(false, true));
        if (lines.Length > 0)
        {
            try
            {
                using var headerDocument = JsonDocument.Parse(lines[0]);
                var header = headerDocument.RootElement;
                if (header.ValueKind == JsonValueKind.Object && header.TryGetProperty("format", out var format) &&
                    format.ValueKind == JsonValueKind.String && format.GetString() == CheckpointFormat &&
                    !IsCompatibleHeader(header, descriptor, profile)) return result;
            }
            catch (JsonException) { }
        }
        for (var index = 0; index < lines.Length; index++)
        {
            try
            {
                using var json = JsonDocument.Parse(lines[index]); var e = json.RootElement;
                if (index == 0)
                {
                    continue;
                }
                var row = JsonSerializer.Deserialize<VectorRow>(lines[index], JsonOptions);
                if (row is null || !expected.TryGetValue(row.MovieLensId, out var expectedRow) ||
                    expectedRow.Fingerprint != row.Fingerprint || expectedRow.SemanticTextSha256 != row.SemanticTextSha256 || result.ContainsKey(row.MovieLensId)) continue;
                ValidateVector(row.Vector); result[row.MovieLensId] = row;
            }
            catch (JsonException) { }
            catch (InvalidOperationException) { }
            catch (KeyNotFoundException) { }
            catch (InvalidDataException) { }
        }
        return result;
    }

    private static bool IsCompatibleHeader(JsonElement header, EmbeddingProfileDescriptor descriptor, string profile) =>
        StringEquals(header, "profile", descriptor.ProfileVersion) &&
        StringEquals(header, "profileFingerprint", profile) &&
        StringEquals(header, "modelRevision", descriptor.Revision) &&
        StringEquals(header, "onnxSha256", descriptor.Artifact(descriptor == EmbeddingProfileDescriptor.LegacyEnglish ? "model_qint8_avx512_vnni.onnx" : "model_qint8_avx512_vnni.onnx").Sha256) &&
        StringEquals(header, "tokenizerSha256", descriptor.Artifact("tokenizer.json").Sha256) &&
        header.TryGetProperty("dimension", out var dimension) && dimension.ValueKind == JsonValueKind.Number && dimension.TryGetInt32(out var value) && value == descriptor.Dimension &&
        StringEquals(header, "documentInputFormat", "e5-passage-semantictext-v1") &&
        (descriptor.InferenceShapePolicy is null ? !header.TryGetProperty("inferenceShapePolicy", out _) : StringEquals(header, "inferenceShapePolicy", descriptor.InferenceShapePolicy));

    private static bool StringEquals(JsonElement value, string name, string expected) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && property.GetString() == expected;

    private static void WriteCheckpoint(string path, EmbeddingProfileDescriptor descriptor, string profile, IEnumerable<VectorRow> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var writer = new StreamWriter(new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None), new UTF8Encoding(false)))
            {
                writer.WriteLine(JsonSerializer.Serialize(new CheckpointHeader(CheckpointFormat, descriptor.ProfileVersion, profile, descriptor.Revision,
                    descriptor.Artifact("model_qint8_avx512_vnni.onnx").Sha256, descriptor.Artifact("tokenizer.json").Sha256, descriptor.Dimension, "e5-passage-semantictext-v1", descriptor.InferenceShapePolicy), JsonOptions));
                foreach (var row in rows) writer.WriteLine(JsonSerializer.Serialize(row, JsonOptions));
                writer.Flush();
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void AppendCheckpoint(string path, IEnumerable<VectorRow> rows)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        foreach (var row in rows) writer.WriteLine(JsonSerializer.Serialize(row, JsonOptions));
        writer.Flush(); stream.Flush(true);
    }

    private static void Publish(string output, byte[] jsonl, byte[] manifest)
    {
        var parent = Path.GetDirectoryName(output)!; Directory.CreateDirectory(parent);
        var stage = Path.GetFullPath(Path.Combine(parent, ".e5-stage-" + Guid.NewGuid().ToString("N")));
        if (!string.Equals(Path.GetDirectoryName(stage), parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(stage).StartsWith(".e5-stage-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unsafe E5 generator staging path.");
        Directory.CreateDirectory(stage);
        try
        {
            File.WriteAllBytes(Path.Combine(stage, "document-vectors.jsonl"), jsonl);
            File.WriteAllBytes(Path.Combine(stage, "manifest.json"), manifest);
            Directory.Move(stage, output);
        }
        finally
        {
            if (Directory.Exists(stage))
            {
                var resolved = Path.GetFullPath(stage);
                if (!string.Equals(Path.GetDirectoryName(resolved), parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith(".e5-stage-", StringComparison.Ordinal))
                    throw new InvalidOperationException("Refusing to remove an unsafe E5 staging path.");
                Directory.Delete(resolved, true);
            }
        }
    }

    private static void ValidateVector(float[]? vector)
    {
        if (vector is null || vector.Length != E5EmbeddingModel.Dimension || vector.Any(x => !float.IsFinite(x))) throw new InvalidDataException("Vector must contain exactly 768 finite float32 values.");
        double norm = 0; foreach (var value in vector) norm += (double)value * value;
        if (!double.IsFinite(norm) || norm <= 0 || Math.Abs(Math.Sqrt(norm) - 1d) > 0.001) throw new InvalidDataException("Vector must be nonzero and L2 normalized.");
    }

    private static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private static string HashUtf8(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string RequiredString(JsonElement e, params string[] path) { foreach (var key in path) { if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out e)) throw new InvalidDataException("Catalog manifest is incomplete."); } return e.ValueKind == JsonValueKind.String ? e.GetString()! : throw new InvalidDataException("Catalog manifest field is malformed."); }
    private static int RequiredInt(JsonElement e, params string[] path) { foreach (var key in path) { if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out e)) throw new InvalidDataException("Catalog manifest is incomplete."); } return e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var value) ? value : throw new InvalidDataException("Catalog manifest field is malformed."); }

    private sealed record CatalogMovie(long Id, string SemanticText);
    private sealed record ExpectedVector(string Fingerprint, string SemanticTextSha256);
    private sealed record VectorRow(long MovieLensId, string Fingerprint, string SemanticTextSha256, float[] Vector);
    private sealed record CheckpointHeader(string Format, string Profile, string ProfileFingerprint, string ModelRevision, string OnnxSha256, string TokenizerSha256, int Dimension, string DocumentInputFormat,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? InferenceShapePolicy = null);
    private sealed record FinalManifest(string Format, string CatalogVersion, string CatalogSha256, string CatalogContentFingerprint, string Profile, string ProfileFingerprint,
        string ModelId, string ModelRevision, string OnnxSha256, string TokenizerSha256, int Dimension, string DocumentInputFormat, string FingerprintAlgorithm,
        string Pooling, string Normalization, int MaxTokens, string OutputSha256, int RecordCount, string Order, int FailedRecords, int TruncatedRecords,
        int BatchSize, long ElapsedMilliseconds, string Provenance,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? InferenceShapePolicy = null);
}
