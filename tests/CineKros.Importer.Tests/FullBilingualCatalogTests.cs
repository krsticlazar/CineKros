using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CineKros.Catalog.Importer;

namespace CineKros.Importer.Tests;

[TestClass]
public sealed class FullBilingualCatalogTests
{
    [TestMethod]
    public async Task FullExportIsStrictCompleteAndReproducible()
    {
        var root = FindRoot();
        if (root is null) Assert.Inconclusive("Canonical full source/dictionary artifacts are not available.");
        var source = Path.Combine(root!, "database/data/derived/final/b05a-real-tmdb-01/movies-catalog.jsonl");
        var dictionary = Path.Combine(root!, "database/data/derived/translations/sr-latn-v1-r1/tag-translations-sr.json");
        var temp = Path.Combine(Path.GetTempPath(), "cinekros-p8-full-catalog-test-" + Guid.NewGuid().ToString("N"));
        var releaseA = Path.Combine(temp, "release-a"); var releaseB = Path.Combine(temp, "release-b"); Directory.CreateDirectory(temp);
        try
        {
            var first = await FullBilingualCatalog.ExportAsync(source, dictionary, releaseA);
            var second = await FullBilingualCatalog.ExportAsync(source, dictionary, releaseB);
            Assert.AreEqual(9730, first.Movies.Count); Assert.AreEqual(9730, second.Movies.Count);
            Assert.AreNotEqual("", first.IdentitySha256); Assert.AreEqual(FullBilingualCatalog.CatalogVersion, first.CatalogVersion);
            Assert.AreEqual(FullBilingualCatalog.DictionarySha256, first.DictionarySha256);
            Assert.AreEqual(97300, first.SelectedTagOccurrenceCount);
            Assert.AreEqual(first.CatalogSha256, second.CatalogSha256);
            Assert.AreEqual(first.IdentitySha256, second.IdentitySha256);
            CollectionAssert.AreEqual(await File.ReadAllBytesAsync(first.CatalogPath), await File.ReadAllBytesAsync(second.CatalogPath));
            CollectionAssert.AreEqual(await File.ReadAllBytesAsync(Path.Combine(releaseA, "catalog/manifest.json")), await File.ReadAllBytesAsync(Path.Combine(releaseB, "catalog/manifest.json")));
            var sourceLines = File.ReadAllLines(source, new UTF8Encoding(false, true));
            var bilingualLines = File.ReadAllLines(first.CatalogPath, new UTF8Encoding(false, true));
            Assert.AreEqual(9730, bilingualLines.Length);
            for (var i = 0; i < bilingualLines.Length; i++)
            {
                using var src = JsonDocument.Parse(sourceLines[i]); using var output = JsonDocument.Parse(bilingualLines[i]);
                Assert.AreEqual(19, output.RootElement.EnumerateObject().Count());
                var oldRaw = sourceLines[i][..^1];
                StringAssert.StartsWith(bilingualLines[i], oldRaw, $"Original source bytes/lexemes must be preserved for line {i + 1}.");
                foreach (var property in src.RootElement.EnumerateObject()) Assert.AreEqual(property.Value.GetRawText(), output.RootElement.GetProperty(property.Name).GetRawText(), $"Field {property.Name}, line {i + 1}.");
                Assert.AreEqual(src.RootElement.GetProperty("semanticText").GetString(), output.RootElement.GetProperty("semanticText").GetString());
                Assert.AreEqual(src.RootElement.GetProperty("relevantTags").GetArrayLength(), output.RootElement.GetProperty("tagsSr").GetArrayLength());
                CollectionAssert.AreEqual(output.RootElement.GetProperty("tagsSr").EnumerateArray().Select(x => x.GetString()).ToArray(), first.Movies[i].TagsSr.ToArray());
                Assert.AreEqual(first.Movies[i].SemanticTextSr, output.RootElement.GetProperty("semanticTextSr").GetString());
            }
            Assert.AreEqual(FullBilingualCatalog.ComputeDocumentFingerprint("en", first.Movies[0].SemanticText),
                MultilingualPocCatalog.ComputeDocumentFingerprint("en", FullBilingualCatalog.EnTextFormatVersion, first.Movies[0].SemanticText));
            Assert.AreEqual(FullBilingualCatalog.ComputeDocumentFingerprint("sr", first.Movies[0].SemanticTextSr),
                MultilingualPocCatalog.ComputeDocumentFingerprint("sr", FullBilingualCatalog.SrTextFormatVersion, first.Movies[0].SemanticTextSr));
            await Assert.ThrowsExactlyAsync<IOException>(() => FullBilingualCatalog.ExportAsync(source, dictionary, releaseA));
            await AssertTamperRejected(source, dictionary, releaseA, "extra");
            await AssertTamperRejected(source, dictionary, releaseA, "missing");
            await AssertTamperRejected(source, dictionary, releaseA, "unexpected");
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); }
    }

    [TestMethod]
    public async Task FullLoaderRejectsManifestAndDictionaryIdentityMismatch()
    {
        var root = FindRoot(); if (root is null) Assert.Inconclusive("Canonical full source/dictionary artifacts are not available.");
        var source = Path.Combine(root!, "database/data/derived/final/b05a-real-tmdb-01/movies-catalog.jsonl");
        var dictionary = Path.Combine(root!, "database/data/derived/translations/sr-latn-v1-r1/tag-translations-sr.json");
        var temp = Path.Combine(Path.GetTempPath(), "cinekros-p8-full-negative-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        try
        {
            var release = Path.Combine(temp, "release"); await FullBilingualCatalog.ExportAsync(source, dictionary, release);
            var manifest = Path.Combine(release, "catalog/manifest.json"); var original = await File.ReadAllBytesAsync(manifest);
            try
            {
                var node = JsonNode.Parse(original)!.AsObject(); node["selection"]!["movieCount"] = 9729;
                await File.WriteAllBytesAsync(manifest, JsonSerializer.SerializeToUtf8Bytes(node, new JsonSerializerOptions { WriteIndented = true }));
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => FullBilingualCatalog.LoadAsync(Path.Combine(release, "catalog/movies-catalog.jsonl"), manifest, dictionary, source));
            }
            finally { await File.WriteAllBytesAsync(manifest, original); }

            var copiedDictionary = Path.Combine(temp, "dictionary.json"); var dictionaryBytes = await File.ReadAllBytesAsync(dictionary);
            await File.WriteAllBytesAsync(copiedDictionary, dictionaryBytes);
            await File.WriteAllTextAsync(Path.Combine(temp, "content.sha256"), FullBilingualCatalog.DictionarySha256 + "\n", Encoding.ASCII);
            File.Copy(Path.Combine(Path.GetDirectoryName(dictionary)!, "manifest.json"), Path.Combine(temp, "manifest.json"));
            var alteredDictionary = JsonNode.Parse(dictionaryBytes)!.AsObject(); alteredDictionary["entries"]!.AsArray().RemoveAt(0);
            await File.WriteAllBytesAsync(copiedDictionary, JsonSerializer.SerializeToUtf8Bytes(alteredDictionary));
            await File.WriteAllTextAsync(Path.Combine(temp, "content.sha256"), Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(copiedDictionary))) + "\n", Encoding.ASCII);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => FullBilingualCatalog.LoadAsync(Path.Combine(release, "catalog/movies-catalog.jsonl"), manifest, copiedDictionary, source));
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); }
    }

    private static async Task AssertTamperRejected(string source, string dictionary, string release, string mutation)
    {
        var catalogPath = Path.Combine(release, "catalog/movies-catalog.jsonl"); var manifestPath = Path.Combine(release, "catalog/manifest.json");
        var originalCatalog = await File.ReadAllBytesAsync(catalogPath); var originalManifest = await File.ReadAllBytesAsync(manifestPath);
        var sidecarPath = Path.Combine(release, "catalog/content.sha256"); var originalSidecar = await File.ReadAllBytesAsync(sidecarPath);
        try
        {
            var lines = Encoding.UTF8.GetString(originalCatalog).Split('\n'); var first = lines[0];
            if (mutation == "extra") lines[0] = first[..^1] + ",\"unapproved\":1}";
            else if (mutation == "missing") lines[0] = first[..first.LastIndexOf(",\"semanticTextSr\":", StringComparison.Ordinal)] + "}";
            else lines[0] = first.Replace("\"tagsSr\"", "\"tagsSR\"", StringComparison.Ordinal);
            var alteredBytes = new UTF8Encoding(false).GetBytes(string.Join('\n', lines)); await File.WriteAllBytesAsync(catalogPath, alteredBytes);
            await File.WriteAllTextAsync(sidecarPath, Convert.ToHexStringLower(SHA256.HashData(alteredBytes)) + "\n", Encoding.ASCII);
            var manifest = JsonNode.Parse(originalManifest)!.AsObject(); manifest["output"]!["sha256"] = Convert.ToHexStringLower(SHA256.HashData(alteredBytes));
            await File.WriteAllBytesAsync(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true }));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => FullBilingualCatalog.LoadAsync(catalogPath, manifestPath, dictionary, source));
        }
        finally
        {
            await File.WriteAllBytesAsync(catalogPath, originalCatalog); await File.WriteAllBytesAsync(manifestPath, originalManifest); await File.WriteAllBytesAsync(sidecarPath, originalSidecar);
        }
    }

    private static string? FindRoot()
    {
        var configured = Environment.GetEnvironmentVariable("CINEKROS_POC_ROOT");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(Path.Combine(configured, "database/data/derived/final/b05a-real-tmdb-01/movies-catalog.jsonl"))) return configured;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "database/data/derived/final/b05a-real-tmdb-01/movies-catalog.jsonl"))) return directory.FullName;
        return null;
    }
}
