using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CineKros.Catalog.Importer;

namespace CineKros.Importer.Tests;

[TestClass]
public sealed class MultilingualPocCatalogTests
{
    private static string? FindPocRoot()
    {
        var configured = Environment.GetEnvironmentVariable("CINEKROS_POC_ROOT");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(Path.Combine(configured, "database/data/derived/serbian-search/poc-v1/catalog/movies-catalog.jsonl"))) return configured;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "database/data/derived/serbian-search/poc-v1/catalog/movies-catalog.jsonl"))) return directory.FullName;
        return null;
    }

    private static async Task<MultilingualPocCatalogDocument> LoadCanonicalAsync()
    {
        var root = FindPocRoot();
        if (root is null) Assert.Inconclusive("Frozen Serbian-search POC inputs are not installed for this test run.");
        var poc = Path.Combine(root!, "database/data/derived/serbian-search/poc-v1");
        return await MultilingualPocCatalog.LoadAsync(
            Path.Combine(poc, "catalog/movies-catalog.jsonl"), Path.Combine(poc, "catalog/manifest.json"),
            Path.Combine(poc, "translation/tag-translations-sr.json"),
            Path.Combine(root!, "database/data/derived/final/b05a-real-tmdb-01/movies-catalog.jsonl"));
    }

    [TestMethod]
    public async Task LoadsFrozenCatalogAndProducesStableSharedIdentityPayload()
    {
        var catalog = await LoadCanonicalAsync();
        Assert.AreEqual(MultilingualPocCatalog.ExpectedCount, catalog.Movies.Count);
        Assert.AreEqual(MultilingualPocCatalog.CatalogSha256, catalog.CatalogSha256);
        Assert.AreEqual(MultilingualPocCatalog.DictionarySha256, catalog.DictionarySha256);
        Assert.AreEqual(MultilingualPocCatalog.EnCorpusSha256, catalog.EnCorpusSha256);
        Assert.AreEqual(MultilingualPocCatalog.SrCorpusSha256, catalog.SrCorpusSha256);
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(catalog.IdentityPayloadJson))), catalog.IdentitySha256);
        using var payload = JsonDocument.Parse(catalog.IdentityPayloadJson);
        CollectionAssert.AreEqual(new[] { "identityVersion", "catalogVersion", "catalogSha256", "sourceCatalogSha256", "sourceContentFingerprint", "selectionIdSetSha256", "dictionarySha256", "enCorpusSha256", "srCorpusSha256", "textFormatVersionSr" },
            payload.RootElement.EnumerateObject().Select(x => x.Name).ToArray());
    }

    [TestMethod]
    public async Task ReaderRejectsManifestTagCountOrSourceTagHashMismatch()
    {
        var root = FindPocRoot();
        if (root is null) Assert.Inconclusive("Frozen Serbian-search POC inputs are not installed for this test run.");
        var poc = Path.Combine(root!, "database/data/derived/serbian-search/poc-v1");
        var temp = Path.Combine(Path.GetTempPath(), "cinekros-poc-manifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var sourceManifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(poc, "catalog/manifest.json")))!;
            foreach (var (field, value) in new[] { ("tagCount", JsonValue.Create(417)), ("sourceTagsSha256", JsonValue.Create(new string('0', 64))) })
            {
                var mutated = sourceManifest.DeepClone();
                mutated["selection"]![field] = value;
                var manifestPath = Path.Combine(temp, "manifest.json");
                await File.WriteAllTextAsync(manifestPath, mutated.ToJsonString());
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MultilingualPocCatalog.LoadAsync(
                    Path.Combine(poc, "catalog/movies-catalog.jsonl"), manifestPath,
                    Path.Combine(poc, "translation/tag-translations-sr.json"),
                    Path.Combine(root!, "database/data/derived/final/b05a-real-tmdb-01/movies-catalog.jsonl")));
            }
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [TestMethod]
    public async Task ArtifactValidationRejectsInnerFingerprintAndVectorMutationsAfterOuterHashIsRecomputed()
    {
        var catalog = await LoadCanonicalAsync();
        var root = FindPocRoot()!;
        var artifact = Path.Combine(root, "database/data/derived/serbian-search/poc-v1/embeddings/en/document-vectors.jsonl");
        var manifestPath = Path.Combine(root, "database/data/derived/serbian-search/poc-v1/embeddings/en/manifest.json");
        if (!File.Exists(artifact) || !File.Exists(manifestPath)) Assert.Inconclusive("Real English POC vectors have not been generated yet.");
        var temp = Path.Combine(Path.GetTempPath(), "cinekros-poc-artifact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var bytes = await File.ReadAllBytesAsync(artifact);
            using var originalManifest = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath));
            var manifest = JsonNode.Parse(originalManifest.RootElement.GetRawText())!;
            var lines = Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var mutated = JsonNode.Parse(lines[0])!;
            mutated["fingerprint"] = new string('0', 64);
            lines[0] = mutated.ToJsonString();
            await AssertMutatedArtifactRejected(catalog, temp, manifest, lines);

            mutated = JsonNode.Parse(Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries)[0])!;
            var vector = mutated["vector"]!.AsArray(); vector[0] = (double)vector[0]! + 0.1;
            lines[0] = mutated.ToJsonString();
            await AssertMutatedArtifactRejected(catalog, temp, manifest, lines);
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [TestMethod]
    public async Task ArtifactValidationRejectsLanguageProfileCorpusDictionaryAndIdMutationsAfterOuterHashIsRecomputed()
    {
        var catalog = await LoadCanonicalAsync();
        var root = FindPocRoot()!;
        var enDirectory = Path.Combine(root, "database/data/derived/serbian-search/poc-v1/embeddings/en");
        var srDirectory = Path.Combine(root, "database/data/derived/serbian-search/poc-v1/embeddings/sr");
        if (!File.Exists(Path.Combine(enDirectory, "document-vectors.jsonl")) || !File.Exists(Path.Combine(srDirectory, "document-vectors.jsonl")))
            Assert.Inconclusive("Real bilingual POC vectors have not both been generated yet.");
        var temp = Path.Combine(Path.GetTempPath(), "cinekros-poc-artifact-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var validEn = MultilingualPocCatalog.ValidateVectorArtifact(Path.Combine(enDirectory, "document-vectors.jsonl"), Path.Combine(enDirectory, "manifest.json"), catalog, "en");
            var validSr = MultilingualPocCatalog.ValidateVectorArtifact(Path.Combine(srDirectory, "document-vectors.jsonl"), Path.Combine(srDirectory, "manifest.json"), catalog, "sr");
            Assert.AreEqual(150, validEn.RecordCount); Assert.AreEqual(150, validSr.RecordCount);
            CollectionAssert.AreEqual(validEn.Rows.Select(x => x.MovieLensId).ToArray(), validSr.Rows.Select(x => x.MovieLensId).ToArray());
            var enBytes = await File.ReadAllBytesAsync(Path.Combine(enDirectory, "document-vectors.jsonl"));
            var enLines = Encoding.UTF8.GetString(enBytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var enManifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(enDirectory, "manifest.json")))!;
            await AssertMutatedArtifactRejected(catalog, temp, enManifest, enLines, "en", m => m["language"] = "sr");
            await AssertMutatedArtifactRejected(catalog, temp, enManifest, enLines, "en", m => m.AsObject().Remove("translationDictionarySha256"));
            await AssertMutatedArtifactRejected(catalog, temp, enManifest, enLines, "en", m => m["profile"] = "e5-base-v2-int8-onnx-v1");
            await AssertMutatedArtifactRejected(catalog, temp, enManifest, enLines, "en", m => m["profileFingerprint"] = new string('1', 64));
            await AssertMutatedArtifactRejected(catalog, temp, enManifest, enLines, "en", m => m["corpusSha256"] = new string('2', 64));
            await AssertMutatedArtifactRejected(catalog, temp, enManifest, enLines.Take(enLines.Length - 1).ToArray(), "en");
            var missing = (string[])enLines.Clone();
            var missingRow = JsonNode.Parse(missing[0])!; missingRow["movieLensId"] = 0; missing[0] = missingRow.ToJsonString();
            await AssertMutatedArtifactRejected(catalog, temp, enManifest, missing, "en");
            var duplicate = (string[])enLines.Clone();
            var duplicateRow = JsonNode.Parse(duplicate[1])!; duplicateRow["movieLensId"] = catalog.Movies[0].MovieLensId; duplicate[1] = duplicateRow.ToJsonString();
            await AssertMutatedArtifactRejected(catalog, temp, enManifest, duplicate, "en");

            var srBytes = await File.ReadAllBytesAsync(Path.Combine(srDirectory, "document-vectors.jsonl"));
            var srLines = Encoding.UTF8.GetString(srBytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var srManifest = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(srDirectory, "manifest.json")))!;
            await AssertMutatedArtifactRejected(catalog, temp, srManifest, srLines, "sr", m => m["translationDictionarySha256"] = new string('3', 64));
            await AssertMutatedArtifactRejected(catalog, temp, srManifest, srLines, "sr", m => m["corpusSha256"] = new string('4', 64));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    private static async Task AssertMutatedArtifactRejected(MultilingualPocCatalogDocument catalog, string temp, JsonNode manifest,
        string[] lines, string language = "en", Action<JsonNode>? editManifest = null)
    {
        var dataPath = Path.Combine(temp, "document-vectors.jsonl"); var manifestPath = Path.Combine(temp, "manifest.json");
        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
        await File.WriteAllBytesAsync(dataPath, bytes);
        var copy = manifest.DeepClone(); copy["outputSha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
        editManifest?.Invoke(copy);
        await File.WriteAllTextAsync(manifestPath, copy.ToJsonString());
        Assert.ThrowsExactly<InvalidDataException>(() => MultilingualPocCatalog.ValidateVectorArtifact(dataPath, manifestPath, catalog, language));
    }
}
