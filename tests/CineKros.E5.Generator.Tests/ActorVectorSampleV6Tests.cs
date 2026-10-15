using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CineKros.Catalog.Importer;
using CineKros.E5.Generator;
using CineKros.Embedding;

namespace CineKros.E5.Generator.Tests;

[TestClass]
public sealed class ActorVectorSampleV6Tests
{
    [TestMethod]
    public async Task V6FixedSample16ReproducesPublishedVectorsOnlyWhenExplicitlyEnabled()
    {
        var workspaceRoot = Environment.GetEnvironmentVariable("CINEKROS_V6_REPOSITORY_ROOT");
        var vectorRoot = Environment.GetEnvironmentVariable("CINEKROS_V6_VECTOR_ROOT");
        var modelRoot = Environment.GetEnvironmentVariable("CINEKROS_V6_MODEL_ROOT");
        if (string.IsNullOrWhiteSpace(workspaceRoot) && string.IsNullOrWhiteSpace(vectorRoot) && string.IsNullOrWhiteSpace(modelRoot))
            Assert.Inconclusive("Set all three CINEKROS_V6_*_ROOT variables to run read-only full-catalog sample reproduction.");
        if (string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrWhiteSpace(vectorRoot) || string.IsNullOrWhiteSpace(modelRoot) ||
            !Path.IsPathFullyQualified(workspaceRoot) || !Path.IsPathFullyQualified(vectorRoot) || !Path.IsPathFullyQualified(modelRoot))
            Assert.Fail("All three CINEKROS_V6_*_ROOT values must be explicit absolute paths.");

        var root = Path.GetFullPath(workspaceRoot);
        var vectors = Path.GetFullPath(vectorRoot);
        var modelPath = Path.GetFullPath(modelRoot);
        var catalogPath = Path.Combine(root, "database", "data", "derived", "final", "actor-search-v6", "v6-catalog-01", "catalog", "movies-catalog.jsonl");
        var catalogManifest = Path.Combine(Path.GetDirectoryName(catalogPath)!, "manifest.json");
        var dictionary = Path.Combine(root, "database", "data", "derived", "translations", "sr-latn-v1-r1", "tag-translations-sr.json");
        var sourceCatalog = Path.Combine(root, "database", "data", "derived", "final", "b05a-real-tmdb-01", "movies-catalog.jsonl");
        var catalog = await ActorCatalogV6Reader.LoadAsync(catalogPath, catalogManifest, dictionary, sourceCatalog);
        var expectedProfile = EmbeddingProfileDescriptor.MultilingualE5Base;
        Assert.AreEqual(9730, catalog.Movies.Count);

        // Frozen before inspecting outputs: sixteen single-document calls maximum.
        var sampleIds = new long[] { 1, 192, 296, 3182, 3396, 4963, 26414, 85179 };
        var reportDirectory = Path.Combine(root, ".local", "planning", "reports", "v6-phase-02");
        Directory.CreateDirectory(reportDirectory);
        var runId = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
        await File.WriteAllBytesAsync(Path.Combine(reportDirectory, "sample-plan-" + runId + ".json"),
            JsonSerializer.SerializeToUtf8Bytes(new { format = "actor-v6-fixed-sample-plan-v1", sampleMovieLensIds = sampleIds, languages = new[] { "en", "sr" }, maximumInferenceCalls = 16 }));

        foreach (var language in new[] { "en", "sr" })
        {
            var validated = DocumentVectorGenerator.ValidatePublishedV6(catalog, language, Path.Combine(vectors, language, "published"));
            Assert.IsNotNull(validated, $"The {language} full artifact must already be complete and validated.");
            Assert.AreEqual(9730, validated.RecordCount);
        }

        var movies = catalog.Movies.ToDictionary(x => x.MovieLensId);
        var cases = new List<SampleVectorComparison>(16);
        var artifactHashes = new Dictionary<string, object>(StringComparer.Ordinal);
        using var model = new E5EmbeddingModel(modelPath, expectedProfile);
        Assert.AreEqual(expectedProfile.ProfileFingerprint, model.ProfileFingerprint);
        foreach (var language in new[] { "en", "sr" })
        {
            var artifactPath = Path.Combine(vectors, language, "published", "document-vectors.jsonl");
            var manifestPath = Path.Combine(vectors, language, "published", "manifest.json");
            artifactHashes[language] = new
            {
                vectorsSha256 = HashFile(artifactPath), manifestSha256 = HashFile(manifestPath)
            };
            var wanted = sampleIds.ToHashSet();
            var rows = new Dictionary<long, SampleVectorRow>();
            foreach (var line in File.ReadLines(artifactPath, new UTF8Encoding(false, true)))
            {
                using var rowJson = JsonDocument.Parse(line);
                var id = rowJson.RootElement.GetProperty("movieLensId").GetInt64();
                if (!wanted.Contains(id)) continue;
                var row = JsonSerializer.Deserialize<SampleVectorRow>(line,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Malformed published vector row.");
                if (!rows.TryAdd(id, row)) throw new InvalidDataException($"Duplicate sampled vector ID {id}.");
            }
            Assert.AreEqual(sampleIds.Length, rows.Count, $"All fixed {language} sample IDs must exist in the artifact.");
            foreach (var id in sampleIds)
            {
                var movie = movies[id];
                var text = language == "en" ? movie.SemanticText : movie.SemanticTextSr;
                var row = rows[id];
                var fingerprint = MultilingualPocCatalog.ComputeDocumentFingerprint(language,
                    language == "en" ? catalog.EnTextFormatVersion : catalog.SrTextFormatVersion, text);
                var textHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
                Assert.AreEqual(fingerprint, row.Fingerprint, $"{language}/{id} fingerprint");
                Assert.AreEqual(textHash, row.SemanticTextSha256, $"{language}/{id} semantic text hash");
                var reproduced = model.EmbedDocuments([text])[0];
                var differing = new List<int>(); var maxAbs = 0d; double dot = 0, leftNorm = 0, rightNorm = 0;
                for (var dimension = 0; dimension < reproduced.Length; dimension++)
                {
                    if (BitConverter.SingleToInt32Bits(reproduced[dimension]) != BitConverter.SingleToInt32Bits(row.Vector[dimension])) differing.Add(dimension);
                    maxAbs = Math.Max(maxAbs, Math.Abs((double)reproduced[dimension] - row.Vector[dimension]));
                    dot += (double)reproduced[dimension] * row.Vector[dimension];
                    leftNorm += (double)reproduced[dimension] * reproduced[dimension];
                    rightNorm += (double)row.Vector[dimension] * row.Vector[dimension];
                }
                cases.Add(new SampleVectorComparison(language, id, fingerprint, textHash, differing.Count == 0, differing.Count,
                    differing.Take(32).ToArray(), maxAbs, dot / Math.Sqrt(leftNorm * rightNorm), reproduced, row.Vector));
            }
        }

        var report = new
        {
            format = "actor-v6-fixed-sample-vector-reproduction-v1", catalogIdentitySha256 = catalog.IdentitySha256,
            catalogSha256 = catalog.CatalogSha256, sourceCatalogSha256 = catalog.SourceCatalogSha256,
            dictionarySha256 = catalog.DictionarySha256, profileFingerprint = model.ProfileFingerprint,
            onnxSha256 = expectedProfile.Artifact("model_qint8_avx512_vnni.onnx").Sha256,
            tokenizerSha256 = expectedProfile.Artifact("tokenizer.json").Sha256, encoderInstances = 1,
            inferenceCalls = model.InferenceRunCount, sampleMovieLensIds = sampleIds,
            artifacts = artifactHashes, languages = new[] { "en", "sr" }, allBitwiseIdentical = cases.All(x => x.BitwiseIdentical), cases
        };
        var reportPath = Path.Combine(reportDirectory, "sample-vector-reproduction-" + runId + ".json");
        await File.WriteAllBytesAsync(reportPath, JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { WriteIndented = true }));
        Assert.AreEqual(16, cases.Count);
        Assert.AreEqual(16, model.InferenceRunCount, "Samples are reproduced individually with batch size one on one encoder instance.");
        Assert.IsTrue(cases.All(x => x.BitwiseIdentical), $"Bitwise vector reproduction mismatch; exact differences are persisted at {reportPath}.");
    }

    private static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private sealed record SampleVectorRow(long MovieLensId, string Fingerprint, string SemanticTextSha256, float[] Vector);
    private sealed record SampleVectorComparison(string Language, long MovieLensId, string Fingerprint, string SemanticTextSha256,
        bool BitwiseIdentical, int DifferingDimensions, int[] FirstDifferingDimensions, double MaxAbsoluteDifference, double Cosine,
        float[] ReproducedVector, float[] PublishedVector);

}

