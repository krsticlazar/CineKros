using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CineKros.Catalog.Importer;
using CineKros.Embedding;

namespace CineKros.E5.Generator;

public sealed record CorrectionLanguageRun(string Language, int Generated, int Reused, string ArtifactSha256, int SeededFromV1, bool AlreadyComplete = false);
public sealed record CorrectionGenerationResult(IReadOnlyList<CorrectionLanguageRun> Languages);

/// <summary>Creates a new corrected-release checkpoint/artifact set by reusing only per-document compatible v1 vectors.</summary>
public static class TranslationCorrectionArtifacts
{
    private const string CheckpointFormat = "multilingual-poc-document-checkpoint-v1";
    private const string TextFormatEn = "en-title-year-director-cast-tags-v1";
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static async Task<CorrectionGenerationResult> GenerateAsync(
        string correctedCatalogPath, string correctedManifestPath, string correctedDictionaryPath, string approvedMappingPath,
        string baseCatalogPath, string baseManifestPath, string baseDictionaryPath, string sourceCatalogPath,
        string v1EnDirectory, string v1SrDirectory, string checkpointRoot, string outputRoot,
        IDocumentVectorSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.ProfileDescriptor != EmbeddingProfileDescriptor.MultilingualE5Base || source.ProfileFingerprint != MultilingualPocCatalog.ProfileFingerprint)
            throw new ArgumentException("Corrected artifacts require the existing locked multilingual E5 profile.", nameof(source));
        foreach (var path in new[] { correctedCatalogPath, correctedManifestPath, correctedDictionaryPath, approvedMappingPath, baseCatalogPath, baseManifestPath,
                     baseDictionaryPath, sourceCatalogPath, v1EnDirectory, v1SrDirectory, checkpointRoot, outputRoot })
            if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("All corrected artifact paths must be absolute.");

        var v1 = await MultilingualPocCatalog.LoadAsync(baseCatalogPath, baseManifestPath, baseDictionaryPath, sourceCatalogPath, cancellationToken);
        var corrected = await CorrectedPocCatalog.LoadAsync(correctedCatalogPath, correctedManifestPath, correctedDictionaryPath, approvedMappingPath,
            baseCatalogPath, baseManifestPath, baseDictionaryPath, sourceCatalogPath, cancellationToken);
        var oldEnPath = Path.Combine(v1EnDirectory, "document-vectors.jsonl"); var oldSrPath = Path.Combine(v1SrDirectory, "document-vectors.jsonl");
        var oldEn = MultilingualPocCatalog.ValidateVectorArtifact(oldEnPath, Path.Combine(v1EnDirectory, "manifest.json"), v1, "en");
        var oldSr = MultilingualPocCatalog.ValidateVectorArtifact(oldSrPath, Path.Combine(v1SrDirectory, "manifest.json"), v1, "sr");
        var oldEnRows = ReadRows(oldEnPath); var oldSrRows = ReadRows(oldSrPath);
        Directory.CreateDirectory(checkpointRoot); Directory.CreateDirectory(outputRoot);
        var runs = new List<CorrectionLanguageRun>(2);
        foreach (var language in new[] { "en", "sr" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var previousRows = language == "en" ? oldEnRows : oldSrRows;
            var oldValidation = language == "en" ? oldEn : oldSr;
            var checkpoint = Path.Combine(checkpointRoot, language, "checkpoint.jsonl");
            var output = Path.Combine(outputRoot, language);
            Directory.CreateDirectory(Path.GetDirectoryName(checkpoint)!);
            if (Directory.Exists(output) && File.Exists(Path.Combine(output, "document-vectors.jsonl")) && File.Exists(Path.Combine(output, "manifest.json")))
            {
                var complete = MultilingualPocCatalog.ValidateVectorArtifact(Path.Combine(output, "document-vectors.jsonl"), Path.Combine(output, "manifest.json"), corrected, language);
                var oldCompatible = CountV1Compatible(corrected, language, previousRows);
                runs.Add(new(language, complete.RecordCount - oldCompatible, oldCompatible, complete.ArtifactSha256, oldCompatible, AlreadyComplete: true));
                continue;
            }
            var seeded = SeedCompatibleRows(checkpoint, output, corrected, language, previousRows, oldValidation.Rows, source.ProfileDescriptor, source.ProfileFingerprint);
            var guardedSource = language == "en" ? new NoInferenceSource(source) : source;
            _ = DocumentVectorGenerator.RunMultilingualPoc(checkpoint, output, 1, guardedSource, corrected, language, cancellationToken);
            var artifact = MultilingualPocCatalog.ValidateVectorArtifact(Path.Combine(output, "document-vectors.jsonl"), Path.Combine(output, "manifest.json"), corrected, language);
            runs.Add(new(language, artifact.RecordCount - seeded, seeded, artifact.ArtifactSha256, seeded));
        }
        var provenancePath = Path.Combine(outputRoot, "provenance.json");
        var provenance = new
        {
            schemaVersion = "sr-p6t-vector-reuse-provenance-v1", datasetRelease = CorrectedPocCatalog.DatasetRelease,
            catalogIdentitySha256 = corrected.IdentitySha256, catalogSha256 = corrected.CatalogSha256, dictionarySha256 = corrected.DictionarySha256,
            sourceEnArtifactSha256 = oldEn.ArtifactSha256, sourceSrArtifactSha256 = oldSr.ArtifactSha256,
            profileFingerprint = source.ProfileFingerprint, languages = runs
        };
        if (File.Exists(provenancePath))
        {
            using var existing = JsonDocument.Parse(File.ReadAllBytes(provenancePath));
            if (existing.RootElement.GetProperty("catalogIdentitySha256").GetString() != corrected.IdentitySha256 ||
                existing.RootElement.GetProperty("sourceEnArtifactSha256").GetString() != oldEn.ArtifactSha256 ||
                existing.RootElement.GetProperty("sourceSrArtifactSha256").GetString() != oldSr.ArtifactSha256)
                throw new InvalidDataException("Existing corrected artifact provenance belongs to a different release.");
        }
        else
        {
            using var stream = new FileStream(provenancePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(stream, provenance, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }); stream.Flush(true);
        }
        return new CorrectionGenerationResult(runs);
    }

    private static int SeedCompatibleRows(string checkpoint, string output, MultilingualPocCatalogDocument catalog, string language,
        IReadOnlyDictionary<long, VectorRow> oldRows, IReadOnlyList<MultilingualPocArtifactRow> oldValidation,
        EmbeddingProfileDescriptor profile, string profileFingerprint)
    {
        if (Directory.Exists(output))
        {
            var files = Directory.GetFileSystemEntries(output);
            if (File.Exists(Path.Combine(output, "document-vectors.jsonl")) && File.Exists(Path.Combine(output, "manifest.json")))
            {
                _ = MultilingualPocCatalog.ValidateVectorArtifact(Path.Combine(output, "document-vectors.jsonl"), Path.Combine(output, "manifest.json"), catalog, language);
                return oldRows.Count;
            }
            if (files.Length != 1 || !string.Equals(Path.GetFullPath(files[0]), Path.GetFullPath(checkpoint), StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Corrected output directory for {language} contains unexpected files.");
        }
        var textFormat = language == "en" ? TextFormatEn : MultilingualPocCatalog.SrTextFormatVersion;
        var corpus = language == "en" ? catalog.EnCorpusSha256 : catalog.SrCorpusSha256;
        var expectedOld = oldValidation.ToDictionary(x => x.MovieLensId);
        var reused = new List<VectorRow>(catalog.Movies.Count);
        foreach (var movie in catalog.Movies)
        {
            var text = language == "en" ? movie.SemanticText : movie.SemanticTextSr;
            if (!oldRows.TryGetValue(movie.MovieLensId, out var old) || !expectedOld.TryGetValue(movie.MovieLensId, out var oldProof))
                throw new InvalidDataException("Validated v1 artifact row is missing.");
            var expectedFingerprint = MultilingualPocCatalog.ComputeDocumentFingerprint(language, textFormat, text);
            var expectedTextHash = Hash(Encoding.UTF8.GetBytes(text));
            if (oldProof.Fingerprint != old.Fingerprint || oldProof.SemanticTextSha256 != old.SemanticTextSha256)
                throw new InvalidDataException("v1 vector row differs from the validated v1 artifact proof.");
            if (old.Fingerprint == expectedFingerprint && old.SemanticTextSha256 == expectedTextHash)
                reused.Add(new VectorRow(movie.MovieLensId, expectedFingerprint, expectedTextHash, old.Vector));
        }
        var header = new CheckpointHeader(CheckpointFormat, profile.ProfileVersion, profileFingerprint, profile.Revision,
            profile.Artifact("model_qint8_avx512_vnni.onnx").Sha256, profile.Artifact("tokenizer.json").Sha256, profile.Dimension,
            "passage-prefix-nfc-v1", profile.InferenceShapePolicy!, language, catalog.IdentitySha256, corpus, textFormat);
        if (File.Exists(checkpoint)) return ValidateExistingCheckpoint(checkpoint, header, catalog, language, oldRows);
        using var stream = new FileStream(checkpoint, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.WriteLine(JsonSerializer.Serialize(header, JsonOptions));
        foreach (var row in reused) writer.WriteLine(JsonSerializer.Serialize(row, JsonOptions));
        writer.Flush(); stream.Flush(true);
        return reused.Count;
    }

    private static int ValidateExistingCheckpoint(string path, CheckpointHeader expected, MultilingualPocCatalogDocument catalog, string language,
        IReadOnlyDictionary<long, VectorRow> oldRows)
    {
        var lines = File.ReadAllLines(path, new UTF8Encoding(false, true));
        if (lines.Length == 0) throw new InvalidDataException("Existing corrected checkpoint has no identity header.");
        var header = JsonSerializer.Deserialize<CheckpointHeader>(lines[0], JsonOptions);
        if (header != expected) throw new InvalidDataException("Existing corrected checkpoint identity/profile does not match this release; refusing destructive regeneration.");
        var movies = catalog.Movies.ToDictionary(x => x.MovieLensId);
        var seen = new HashSet<long>();
        var oldCompatible = new HashSet<long>();
        for (var index = 1; index < lines.Length; index++)
        {
            VectorRow row;
            try { row = JsonSerializer.Deserialize<VectorRow>(lines[index], JsonOptions) ?? throw new JsonException(); }
            catch (JsonException ex) { throw new InvalidDataException("Existing corrected checkpoint contains a malformed row; preserving it unchanged.", ex); }
            if (!seen.Add(row.MovieLensId) || !movies.TryGetValue(row.MovieLensId, out var movie))
                throw new InvalidDataException("Existing corrected checkpoint contains a duplicate or unknown movie ID.");
            var semanticText = language == "en" ? movie.SemanticText : movie.SemanticTextSr;
            var textFormat = language == "en" ? TextFormatEn : MultilingualPocCatalog.SrTextFormatVersion;
            if (row.Fingerprint != MultilingualPocCatalog.ComputeDocumentFingerprint(language, textFormat, semanticText) ||
                row.SemanticTextSha256 != Hash(Encoding.UTF8.GetBytes(semanticText)))
                throw new InvalidDataException("Existing corrected checkpoint row is incompatible with the corrected text; preserving it unchanged.");
            ValidateVector(row.Vector);
            if (oldRows.TryGetValue(row.MovieLensId, out var old) && old.Fingerprint == row.Fingerprint &&
                old.SemanticTextSha256 == row.SemanticTextSha256 && old.Vector.SequenceEqual(row.Vector)) oldCompatible.Add(row.MovieLensId);
        }
        return oldCompatible.Count;
    }

    private static int CountV1Compatible(MultilingualPocCatalogDocument catalog, string language, IReadOnlyDictionary<long, VectorRow> oldRows)
    {
        var textFormat = language == "en" ? TextFormatEn : MultilingualPocCatalog.SrTextFormatVersion;
        return catalog.Movies.Count(movie => oldRows.TryGetValue(movie.MovieLensId, out var row) &&
            row.Fingerprint == MultilingualPocCatalog.ComputeDocumentFingerprint(language, textFormat, language == "en" ? movie.SemanticText : movie.SemanticTextSr) &&
            row.SemanticTextSha256 == Hash(Encoding.UTF8.GetBytes(language == "en" ? movie.SemanticText : movie.SemanticTextSr)));
    }

    private static void ValidateVector(float[]? vector)
    {
        if (vector is null || vector.Length != 768 || vector.Any(value => !float.IsFinite(value)))
            throw new InvalidDataException("Existing corrected checkpoint vector must have 768 finite values.");
        double norm = 0; foreach (var value in vector) norm += (double)value * value;
        if (!double.IsFinite(norm) || norm <= 0 || Math.Abs(Math.Sqrt(norm) - 1d) > 0.0001)
            throw new InvalidDataException("Existing corrected checkpoint vector must be L2 normalized.");
    }

    private static Dictionary<long, VectorRow> ReadRows(string path)
    {
        var rows = new Dictionary<long, VectorRow>();
        foreach (var line in File.ReadLines(path, new UTF8Encoding(false, true)))
        {
            var row = JsonSerializer.Deserialize<VectorRow>(line, JsonOptions) ?? throw new InvalidDataException("Vector artifact contains an invalid row.");
            if (!rows.TryAdd(row.MovieLensId, row)) throw new InvalidDataException("Vector artifact contains duplicate IDs.");
        }
        return rows;
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private sealed record VectorRow(long MovieLensId, string Fingerprint, string SemanticTextSha256, float[] Vector);
    private sealed record CheckpointHeader(string Format, string Profile, string ProfileFingerprint, string ModelRevision, string OnnxSha256, string TokenizerSha256,
        int Dimension, string DocumentInputFormat, string InferenceShapePolicy, string Language, string CatalogIdentitySha256, string CorpusSha256, string TextFormatVersion);

    private sealed class NoInferenceSource(IDocumentVectorSource source) : IDocumentVectorSource
    {
        public IReadOnlyList<float[]> Embed(IReadOnlyList<string> semanticTexts, CancellationToken cancellationToken) => throw new InvalidOperationException("English vectors must be reused from the validated v1 artifact; inference is prohibited for this pass.");
        public string Fingerprint(string semanticText) => source.Fingerprint(semanticText);
        public string ProfileFingerprint => source.ProfileFingerprint;
        public EmbeddingProfileDescriptor ProfileDescriptor => source.ProfileDescriptor;
        public long TruncationCount => source.TruncationCount;
    }
}
