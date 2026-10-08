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
    private const string MultilingualCheckpointFormat = "multilingual-poc-document-checkpoint-v1";
    private const string MultilingualManifestFormat = "multilingual-document-vectors-jsonl-v1";
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static RunResult Run(string catalogPath, string manifestPath, string checkpointPath, string outputDirectory,
        int batchSize, IDocumentVectorSource source, CancellationToken cancellationToken = default, Action? beforePublish = null) =>
        Run(catalogPath, manifestPath, checkpointPath, outputDirectory, batchSize, source, CatalogPolicy.Canonical, cancellationToken, beforePublish);

    internal static RunResult Run(string catalogPath, string manifestPath, string checkpointPath, string outputDirectory,
        int batchSize, IDocumentVectorSource source, CatalogPolicy policy, CancellationToken cancellationToken = default, Action? beforePublish = null)
        => RunCore(catalogPath, manifestPath, checkpointPath, outputDirectory, batchSize, source, policy, null, cancellationToken, beforePublish);

    internal static RunResult RunMultilingualPoc(string checkpointPath, string outputDirectory, int batchSize, IDocumentVectorSource source,
        MultilingualPocCatalogDocument catalog, string language, CancellationToken cancellationToken = default, Action? beforePublish = null)
    {
        if (language is not ("en" or "sr")) throw new ArgumentException("Language must be the trusted value en or sr.", nameof(language));
        if (source.ProfileDescriptor != EmbeddingProfileDescriptor.MultilingualE5Base)
            throw new ArgumentException("The POC requires the locked multilingual E5 profile.", nameof(source));
        return RunCore(catalog.CatalogPath, Path.Combine(Path.GetDirectoryName(catalog.CatalogPath)!, "manifest.json"), checkpointPath,
            outputDirectory, batchSize, source, new CatalogPolicy(MultilingualPocCatalog.CatalogVersion, catalog.CatalogSha256,
                catalog.IdentitySha256, MultilingualPocCatalog.ExpectedCount), new PocRunContext(catalog, language), cancellationToken, beforePublish);
    }

    private static RunResult RunCore(string catalogPath, string manifestPath, string checkpointPath, string outputDirectory,
        int batchSize, IDocumentVectorSource source, CatalogPolicy policy, PocRunContext? poc, CancellationToken cancellationToken = default, Action? beforePublish = null)
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
        var checkpointOnlyOutput = poc is not null && IsCheckpointOnlyOutput(outputFullPath, checkpointFullPath);
        if (poc is not null && ((Directory.Exists(outputFullPath) && !checkpointOnlyOutput) || File.Exists(outputFullPath)))
            throw new IOException("Output directory already exists; choose a new run-id directory.");
        var rows = poc is null ? LoadCatalog(catalogFullPath, manifestFullPath, policy, cancellationToken) :
            poc.Catalog.Movies.Select(x => new CatalogMovie(x.MovieLensId, poc.Language == "en" ? x.SemanticText : x.SemanticTextSr)).ToArray();
        var expected = rows.ToDictionary(x => x.Id, x => new ExpectedVector(poc is null ? source.Fingerprint(x.SemanticText) :
            MultilingualPocCatalog.ComputeDocumentFingerprint(poc.Language,
                poc.Language == "en" ? "en-title-year-director-cast-tags-v1" : MultilingualPocCatalog.SrTextFormatVersion, x.SemanticText), HashUtf8(x.SemanticText)));
        var checkpointIdentity = poc is null ? null : new CheckpointIdentity(poc.Language, poc.Catalog.IdentitySha256,
            poc.Language == "en" ? poc.Catalog.EnCorpusSha256 : poc.Catalog.SrCorpusSha256,
            poc.Language == "en" ? "en-title-year-director-cast-tags-v1" : MultilingualPocCatalog.SrTextFormatVersion);
        var reusable = ReadCheckpoint(checkpointFullPath, profile, source.ProfileFingerprint, expected, checkpointIdentity, out var checkpointNeedsBackup);
        var complete = new Dictionary<long, VectorRow>(reusable);
        var pending = rows.Where(x => !complete.ContainsKey(x.Id)).ToArray();
        var generated = 0;
        var truncationsAtStart = source.TruncationCount;
        if (checkpointNeedsBackup) PreserveCheckpointBackup(checkpointFullPath);
        WriteCheckpoint(checkpointFullPath, profile, source.ProfileFingerprint, complete.Values.OrderBy(x => x.MovieLensId), checkpointIdentity);

        foreach (var chunk in pending.Chunk(batchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var vectors = source.Embed(chunk.Select(x => x.SemanticText).ToArray(), cancellationToken);
            if (vectors.Count != chunk.Length) throw new InvalidDataException("Embedding source returned a mismatched batch size.");
            for (var index = 0; index < chunk.Length; index++)
            {
                ValidateVector(vectors[index], poc is not null);
                var expectedRow = expected[chunk[index].Id];
                complete[chunk[index].Id] = new VectorRow(chunk[index].Id, expectedRow.Fingerprint, expectedRow.SemanticTextSha256, vectors[index]);
            }
            generated += chunk.Length;
            AppendCheckpoint(checkpointFullPath, chunk.Select(x => complete[x.Id]));
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (complete.Count != policy.ExpectedCount || rows.Any(x => !complete.ContainsKey(x.Id)))
            throw new InvalidDataException("A final E5 artifact requires a compatible vector for every catalog row.");
        if (poc is null && (Directory.Exists(outputFullPath) || File.Exists(outputFullPath)))
            throw new IOException("Output directory already exists; choose a new run-id directory.");

        var finalRows = rows.Select(movie => complete[movie.Id]).ToArray();
        var jsonl = string.Concat(finalRows.Select(row => JsonSerializer.Serialize(row, JsonOptions) + "\n"));
        var jsonBytes = new UTF8Encoding(false).GetBytes(jsonl);
        var jsonHash = Convert.ToHexStringLower(SHA256.HashData(jsonBytes));
        timer.Stop();
        var onnxHash = profile.Artifact("model_qint8_avx512_vnni.onnx").Sha256;
        var tokenizerHash = profile.Artifact("tokenizer.json").Sha256;
        var legacy = ReferenceEquals(profile, EmbeddingProfileDescriptor.LegacyEnglish);
        var truncationDelta = poc is null ? source.TruncationCount : source.TruncationCount - truncationsAtStart;
        object manifest = poc is null ? new FinalManifest(ManifestFormat, policy.CatalogVersion, policy.CatalogSha256 == "*" ? HashFile(catalogFullPath) : policy.CatalogSha256,
            policy.ContentFingerprint, profile.ProfileVersion, source.ProfileFingerprint, profile.ModelId, profile.Revision,
            onnxHash, tokenizerHash, profile.Dimension, "e5-passage-semantictext-v1", "sha256-compact-json-e5-v1", profile.Pooling, profile.Normalization,
            profile.MaxTokens, jsonHash, finalRows.Length, "movieLensId ascending", 0, checked((int)truncationDelta), batchSize, timer.ElapsedMilliseconds, "local offline generator",
            legacy ? null : profile.InferenceShapePolicy) :
            new MultilingualManifest(MultilingualManifestFormat, poc.Language, policy.CatalogVersion, poc.Catalog.CatalogSha256,
                poc.Catalog.SourceCatalogSha256, poc.Catalog.SourceContentFingerprint, poc.Catalog.IdentitySha256, poc.Catalog.SelectionIdSetSha256,
                poc.Language == "en" ? poc.Catalog.EnCorpusSha256 : poc.Catalog.SrCorpusSha256,
                poc.Language == "en" ? null : poc.Catalog.DictionarySha256,
                poc.Language == "en" ? "en-title-year-director-cast-tags-v1" : MultilingualPocCatalog.SrTextFormatVersion,
                "unicode-nfc-case-preserving-v1", profile.ProfileVersion, source.ProfileFingerprint, profile.ModelId, profile.Revision,
                onnxHash, tokenizerHash, profile.Dimension, "passage: ", "multilingual-document-fingerprint-v1", jsonHash, finalRows.Length,
                "movieLensId ascending", 0, checked((int)truncationDelta), batchSize, timer.ElapsedMilliseconds,
                profile.Pooling, profile.Normalization, profile.InferenceShapePolicy!);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        beforePublish?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        if (checkpointOnlyOutput) PublishPocIntoCheckpointDirectory(outputFullPath, jsonBytes, manifestBytes);
        else Publish(outputFullPath, jsonBytes, manifestBytes);
        if (poc is not null)
            _ = MultilingualPocCatalog.ValidateVectorArtifact(Path.Combine(outputFullPath, "document-vectors.jsonl"),
                Path.Combine(outputFullPath, "manifest.json"), poc.Catalog, poc.Language);
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

    private static Dictionary<long, VectorRow> ReadCheckpoint(string path, EmbeddingProfileDescriptor descriptor, string profile, IReadOnlyDictionary<long, ExpectedVector> expected, CheckpointIdentity? identity, out bool needsBackup)
    {
        var result = new Dictionary<long, VectorRow>();
        needsBackup = false;
        if (!File.Exists(path)) return result;
        string[] lines;
        if (identity is null) lines = File.ReadAllLines(path, new UTF8Encoding(false, true));
        else
        {
            var bytes = File.ReadAllBytes(path);
            lines = ReadCompleteCheckpointRecords(bytes, out var hasCorruptRecord);
            needsBackup = hasCorruptRecord;
        }
        if (identity is not null)
        {
            if (lines.Length == 0) { needsBackup = true; return result; }
            try
            {
                using var strictHeaderDoc = JsonDocument.Parse(lines[0]);
                var strictHeader = strictHeaderDoc.RootElement;
                if (!StringEquals(strictHeader, "format", MultilingualCheckpointFormat) || !IsCompatibleHeader(strictHeader, descriptor, profile, identity))
                { needsBackup = true; return result; }
            }
            catch (JsonException) { needsBackup = true; return result; }
        }
        if (lines.Length > 0)
        {
            try
            {
                using var headerDocument = JsonDocument.Parse(lines[0]);
                var header = headerDocument.RootElement;
                if (header.ValueKind == JsonValueKind.Object && header.TryGetProperty("format", out var format) &&
                    format.ValueKind == JsonValueKind.String && (identity is null ? format.GetString() == CheckpointFormat : format.GetString() == MultilingualCheckpointFormat) &&
                    !IsCompatibleHeader(header, descriptor, profile, identity)) { needsBackup = identity is not null; return result; }
            }
            catch (JsonException) { if (identity is not null) needsBackup = true; }
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
                    expectedRow.Fingerprint != row.Fingerprint || expectedRow.SemanticTextSha256 != row.SemanticTextSha256 || result.ContainsKey(row.MovieLensId))
                { if (identity is not null) needsBackup = true; continue; }
                ValidateVector(row.Vector, identity is not null); result[row.MovieLensId] = row;
            }
            catch (JsonException) { if (identity is not null) needsBackup = true; }
            catch (InvalidOperationException) { if (identity is not null) needsBackup = true; }
            catch (KeyNotFoundException) { if (identity is not null) needsBackup = true; }
            catch (InvalidDataException) { if (identity is not null) needsBackup = true; }
        }
        return result;
    }

    private static string[] ReadCompleteCheckpointRecords(byte[] bytes, out bool hasCorruptRecord)
    {
        hasCorruptRecord = false;
        var records = new List<string>();
        var start = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] != (byte)'\n') continue;
            try
            {
                var line = new UTF8Encoding(false, true).GetString(bytes, start, index - start);
                records.Add(line.EndsWith('\r') ? line[..^1] : line);
            }
            catch (DecoderFallbackException) { hasCorruptRecord = true; }
            start = index + 1;
        }
        if (start < bytes.Length) hasCorruptRecord = true;
        return records.ToArray();
    }

    private static void PreserveCheckpointBackup(string path)
    {
        var backupPath = path + ".backup-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
        File.Copy(path, backupPath, overwrite: false);
    }

    private static bool IsCompatibleHeader(JsonElement header, EmbeddingProfileDescriptor descriptor, string profile, CheckpointIdentity? identity) =>
        StringEquals(header, "profile", descriptor.ProfileVersion) &&
        StringEquals(header, "profileFingerprint", profile) &&
        StringEquals(header, "modelRevision", descriptor.Revision) &&
        StringEquals(header, "onnxSha256", descriptor.Artifact(descriptor == EmbeddingProfileDescriptor.LegacyEnglish ? "model_qint8_avx512_vnni.onnx" : "model_qint8_avx512_vnni.onnx").Sha256) &&
        StringEquals(header, "tokenizerSha256", descriptor.Artifact("tokenizer.json").Sha256) &&
        header.TryGetProperty("dimension", out var dimension) && dimension.ValueKind == JsonValueKind.Number && dimension.TryGetInt32(out var value) && value == descriptor.Dimension &&
        StringEquals(header, "documentInputFormat", identity is null ? "e5-passage-semantictext-v1" : "passage-prefix-nfc-v1") &&
        (identity is null || (StringEquals(header, "language", identity.Language) && StringEquals(header, "catalogIdentitySha256", identity.CatalogIdentitySha256) &&
            StringEquals(header, "corpusSha256", identity.CorpusSha256) && StringEquals(header, "textFormatVersion", identity.TextFormatVersion))) &&
        (descriptor.InferenceShapePolicy is null ? !header.TryGetProperty("inferenceShapePolicy", out _) : StringEquals(header, "inferenceShapePolicy", descriptor.InferenceShapePolicy));

    private static bool StringEquals(JsonElement value, string name, string expected) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String && property.GetString() == expected;

    private static void WriteCheckpoint(string path, EmbeddingProfileDescriptor descriptor, string profile, IEnumerable<VectorRow> rows, CheckpointIdentity? identity)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var writer = new StreamWriter(new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None), new UTF8Encoding(false)))
            {
                object header = identity is null ? new CheckpointHeader(CheckpointFormat, descriptor.ProfileVersion, profile, descriptor.Revision,
                    descriptor.Artifact("model_qint8_avx512_vnni.onnx").Sha256, descriptor.Artifact("tokenizer.json").Sha256, descriptor.Dimension, "e5-passage-semantictext-v1", descriptor.InferenceShapePolicy) :
                    new MultilingualCheckpointHeader(MultilingualCheckpointFormat, descriptor.ProfileVersion, profile, descriptor.Revision,
                        descriptor.Artifact("model_qint8_avx512_vnni.onnx").Sha256, descriptor.Artifact("tokenizer.json").Sha256, descriptor.Dimension,
                        "passage-prefix-nfc-v1", descriptor.InferenceShapePolicy!, identity.Language, identity.CatalogIdentitySha256, identity.CorpusSha256, identity.TextFormatVersion);
                writer.WriteLine(JsonSerializer.Serialize(header, JsonOptions));
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

    private static void ValidateVector(float[]? vector, bool multilingual = false)
    {
        if (vector is null || vector.Length != E5EmbeddingModel.Dimension || vector.Any(x => !float.IsFinite(x))) throw new InvalidDataException("Vector must contain exactly 768 finite float32 values.");
        double norm = 0; foreach (var value in vector) norm += (double)value * value;
        var tolerance = multilingual ? 0.0001 : 0.001;
        if (!double.IsFinite(norm) || norm <= 0 || Math.Abs(Math.Sqrt(norm) - 1d) > tolerance) throw new InvalidDataException(multilingual
            ? "Vector must be nonzero and L2 normalized within the multilingual tolerance."
            : "Vector must be nonzero and L2 normalized.");
    }

    private static bool IsCheckpointOnlyOutput(string output, string checkpoint)
    {
        if (!Directory.Exists(output) || !string.Equals(Path.GetDirectoryName(checkpoint), output, StringComparison.OrdinalIgnoreCase) || !File.Exists(checkpoint)) return false;
        var entries = Directory.GetFileSystemEntries(output);
        return entries.Length == 1 && string.Equals(Path.GetFullPath(entries[0]), Path.GetFullPath(checkpoint), StringComparison.OrdinalIgnoreCase);
    }

    private static void PublishPocIntoCheckpointDirectory(string output, byte[] jsonl, byte[] manifest)
    {
        var id = Guid.NewGuid().ToString("N");
        var stagedVectors = Path.Combine(output, ".vectors-stage-" + id);
        var stagedManifest = Path.Combine(output, ".manifest-stage-" + id);
        var finalVectors = Path.Combine(output, "document-vectors.jsonl");
        var finalManifest = Path.Combine(output, "manifest.json");
        if (File.Exists(finalVectors) || File.Exists(finalManifest)) throw new IOException("POC artifact files already exist; refusing to overwrite a prior publication.");
        var publishedVectors = false;
        try
        {
            File.WriteAllBytes(stagedVectors, jsonl);
            File.WriteAllBytes(stagedManifest, manifest);
            File.Move(stagedVectors, finalVectors, false); publishedVectors = true;
            File.Move(stagedManifest, finalManifest, false);
        }
        catch
        {
            if (publishedVectors && !File.Exists(finalManifest)) File.Delete(finalVectors);
            throw;
        }
        finally
        {
            if (File.Exists(stagedVectors)) File.Delete(stagedVectors);
            if (File.Exists(stagedManifest)) File.Delete(stagedManifest);
        }
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
    private sealed record CheckpointIdentity(string Language, string CatalogIdentitySha256, string CorpusSha256, string TextFormatVersion);
    private sealed record MultilingualCheckpointHeader(string Format, string Profile, string ProfileFingerprint, string ModelRevision, string OnnxSha256, string TokenizerSha256,
        int Dimension, string DocumentInputFormat, string? InferenceShapePolicy, string Language, string CatalogIdentitySha256, string CorpusSha256, string TextFormatVersion);
    private sealed record PocRunContext(MultilingualPocCatalogDocument Catalog, string Language);
    private sealed record MultilingualManifest(string Format, string Language, string CatalogVersion, string CatalogSha256, string SourceCatalogSha256,
        string SourceContentFingerprint, string CatalogContentIdentitySha256, string SelectionIdSetSha256, string CorpusSha256,
        string? TranslationDictionarySha256, string TextFormatVersion, string InputNormalizationVersion, string Profile, string ProfileFingerprint,
        string ModelId, string ModelRevision, string OnnxSha256, string TokenizerSha256, int Dimension, string DocumentPrefix,
        string FingerprintAlgorithm, string OutputSha256, int RecordCount, string Order, int FailedRecords, int TruncatedRecords,
        int BatchSize, long ElapsedMilliseconds, string Pooling, string Normalization, string InferenceShapePolicy);
    private sealed record FinalManifest(string Format, string CatalogVersion, string CatalogSha256, string CatalogContentFingerprint, string Profile, string ProfileFingerprint,
        string ModelId, string ModelRevision, string OnnxSha256, string TokenizerSha256, int Dimension, string DocumentInputFormat, string FingerprintAlgorithm,
        string Pooling, string Normalization, int MaxTokens, string OutputSha256, int RecordCount, string Order, int FailedRecords, int TruncatedRecords,
        int BatchSize, long ElapsedMilliseconds, string Provenance,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? InferenceShapePolicy = null);
}
