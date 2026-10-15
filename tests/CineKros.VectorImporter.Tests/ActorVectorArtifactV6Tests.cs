using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CineKros.Catalog.Importer;
using CineKros.Embedding;
using CineKros.VectorImporter;

namespace CineKros.VectorImporter.Tests;

[TestClass]
public sealed class ActorVectorArtifactV6Tests
{
    [TestMethod]
    public async Task IndependentPairValidationBindsBothLanguagePublicationHashes()
    {
        using var f = new Fixture(); var en = f.Write("en"); var sr = f.Write("sr"); var c = f.Catalog;
        var pair = new { format = "actor-v6-paired-release-v1", catalogVersion = c.CatalogVersion, catalogSha256 = c.CatalogSha256,
            catalogIdentitySha256 = c.IdentitySha256, selectionIdSetSha256 = c.SelectionIdSetSha256, dictionarySha256 = c.DictionarySha256,
            actorVocabularySha256 = c.ActorVocabularySha256, orderedCastMappingSha256 = c.OrderedCastMappingSha256,
            overridesSha256 = c.OverridesSha256, castRulesSha256 = c.CastRulesSha256, profileFingerprint = c.ProfileFingerprint,
            languages = new[] { "en", "sr" }.Select(language => new { language, corpusSha256 = language == "en" ? c.EnCorpusSha256 : c.SrCorpusSha256,
                textFormatVersion = language == "en" ? c.EnTextFormatVersion : c.SrTextFormatVersion, recordCount = 2,
                vectorsSha256 = Hash(File.ReadAllBytes(Path.Combine(language == "en" ? en : sr, "document-vectors.jsonl"))),
                manifestSha256 = Hash(File.ReadAllBytes(Path.Combine(language == "en" ? en : sr, "manifest.json"))) }).ToArray() };
        var path = Path.Combine(f.Root, "paired-release.json"); File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(pair));
        await ActorVectorArtifactV6Validator.ValidateFixturePairAsync(f.Root, c);
        var node = JsonNode.Parse(File.ReadAllText(path))!; node["languages"]![1]!["vectorsSha256"] = new string('0', 64); File.WriteAllText(path, node.ToJsonString());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ActorVectorArtifactV6Validator.ValidateFixturePairAsync(f.Root, c));
    }
    [TestMethod]
    public async Task IndependentFixtureAcceptsExactLanguageProfileTextAndActorIdentity()
    {
        using var f = new Fixture();
        foreach (var language in new[] { "en", "sr" })
        {
            var directory = f.Write(language); var result = await ActorVectorArtifactV6Validator.LoadFixtureAsync(directory, f.Catalog, language);
            Assert.AreEqual(2, result.Records.Count); Assert.AreEqual(language, result.Language);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ActorVectorArtifactV6Validator.LoadAsync(directory, f.Catalog, language));
        }
    }

    [TestMethod]
    public async Task IndependentValidatorRejectsP8SwappedLanguageProfileCatalogCorpusAndActorContexts()
    {
        foreach (var field in new[] { "format", "language", "catalogVersion", "catalogSha256", "sourceCatalogSha256", "catalogContentIdentitySha256", "selectionIdSetSha256", "corpusSha256", "textFormatVersion", "profileFingerprint", "modelRevision", "onnxSha256", "tokenizerSha256", "actorContext" })
        {
            using var f = new Fixture(); var directory = f.Write("en"); var path = Path.Combine(directory, "manifest.json");
            var node = JsonNode.Parse(File.ReadAllText(path))!; node[field] = field == "format" ? "multilingual-full-document-vectors-jsonl-v1" : "foreign";
            File.WriteAllText(path, node.ToJsonString());
            await Assert.ThrowsAsync<Exception>(() => ActorVectorArtifactV6Validator.LoadFixtureAsync(directory, f.Catalog, "en"), field);
        }
    }

    [TestMethod]
    public async Task IndependentValidatorRejectsMalformedVectorsIdsFingerprintsDuplicateKeysAndMissingRows()
    {
        foreach (var kind in new[] { "dimension", "nan", "norm", "duplicate-id", "missing", "fingerprint", "text-hash", "duplicate-key", "utf8" })
        {
            using var f = new Fixture(); var directory = f.Write("en"); var path = Path.Combine(directory, "document-vectors.jsonl");
            var rows = File.ReadAllLines(path); var row = JsonNode.Parse(rows[0])!;
            if (kind == "dimension") row["vector"] = new JsonArray(1);
            if (kind == "nan") row["vector"]![0] = "NaN";
            if (kind == "norm") row["vector"]![0] = 2;
            if (kind == "duplicate-id") row["movieLensId"] = 2;
            if (kind == "fingerprint") row["fingerprint"] = "foreign";
            if (kind == "text-hash") row["semanticTextSha256"] = "foreign";
            rows[0] = row.ToJsonString();
            if (kind == "duplicate-key") rows[0] = rows[0].Replace("{", "{\"movieLensId\":1,", StringComparison.Ordinal);
            File.WriteAllText(path, string.Join('\n', kind == "missing" ? rows[..1] : rows) + "\n");
            if (kind == "utf8") File.WriteAllBytes(path, [0xff]);
            var manifestPath = Path.Combine(directory, "manifest.json"); var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
            manifest["outputSha256"] = Hash(File.ReadAllBytes(path)); File.WriteAllText(manifestPath, manifest.ToJsonString());
            await Assert.ThrowsAsync<Exception>(() => ActorVectorArtifactV6Validator.LoadFixtureAsync(directory, f.Catalog, "en"), kind);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "v6-vector-fixture-" + Guid.NewGuid().ToString("N"));
        public string Root => root;
        public ActorCatalogV6Document Catalog { get; } = new("fixture.jsonl", new('a', 64), new('b', 64), new('c', 64), new('d', 64), new('e', 64), new('f', 64), new('1', 64), new('2', 64), new('3', 64), new('4', 64), new('5', 64), new('6', 64),
            Array.AsReadOnly(new[] { new ActorMovieV6(1, "English one", "Srpski jedan"), new(2, "English two", "Srpski dva") }));
        public string Write(string language)
        {
            var directory = Path.Combine(root, language, "published"); Directory.CreateDirectory(directory);
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            var format = language == "en" ? Catalog.EnTextFormatVersion : Catalog.SrTextFormatVersion;
            var unit = new float[768]; unit[0] = 1;
            var rows = Catalog.Movies.Select(movie =>
            {
                var text = language == "en" ? movie.SemanticText : movie.SemanticTextSr;
                return JsonSerializer.Serialize(new { movieLensId = movie.MovieLensId, fingerprint = MultilingualPocCatalog.ComputeDocumentFingerprint(language, format, text), semanticTextSha256 = Hash(Encoding.UTF8.GetBytes(text)), vector = unit }, options);
            });
            var bytes = Encoding.UTF8.GetBytes(string.Join('\n', rows) + "\n"); File.WriteAllBytes(Path.Combine(directory, "document-vectors.jsonl"), bytes);
            var p = EmbeddingProfileDescriptor.MultilingualE5Base;
            var manifest = new { format = "multilingual-actor-v6-document-vectors-jsonl-v1", language, catalogVersion = Catalog.CatalogVersion, catalogSha256 = Catalog.CatalogSha256,
                sourceCatalogSha256 = Catalog.SourceCatalogSha256, sourceContentFingerprint = Catalog.SourceContentFingerprint, catalogContentIdentitySha256 = Catalog.IdentitySha256,
                selectionIdSetSha256 = Catalog.SelectionIdSetSha256, corpusSha256 = language == "en" ? Catalog.EnCorpusSha256 : Catalog.SrCorpusSha256,
                translationDictionarySha256 = language == "en" ? null : Catalog.DictionarySha256, textFormatVersion = format,
                inputNormalizationVersion = "unicode-nfc-case-preserving-v1", profile = p.ProfileVersion, profileFingerprint = p.ProfileFingerprint,
                modelId = p.ModelId, modelRevision = p.Revision, onnxSha256 = p.Artifact("model_qint8_avx512_vnni.onnx").Sha256, tokenizerSha256 = p.Artifact("tokenizer.json").Sha256,
                dimension = 768, documentPrefix = "passage: ", fingerprintAlgorithm = "multilingual-document-fingerprint-v1", outputSha256 = Hash(bytes), recordCount = 2,
                order = "movieLensId ascending", failedRecords = 0, truncatedRecords = 0, batchSize = 1, elapsedMilliseconds = 0, pooling = p.Pooling, normalization = p.Normalization,
                inferenceShapePolicy = p.InferenceShapePolicy, actorContext = new { catalogSchemaVersion = "actor-catalog-manifest-v1", actorVocabularySha256 = Catalog.ActorVocabularySha256,
                    orderedCastMappingSha256 = Catalog.OrderedCastMappingSha256, overridesSha256 = Catalog.OverridesSha256, castRulesSha256 = Catalog.CastRulesSha256, dictionarySha256 = Catalog.DictionarySha256 } };
            File.WriteAllBytes(Path.Combine(directory, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, options)); return directory;
        }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
