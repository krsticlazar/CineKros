using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CineKros.Catalog.Importer;
using CineKros.VectorImporter;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CineKros.VectorImporter.Tests;

[TestClass]
public sealed class FullImportInputMutationTests
{
    [TestMethod]
    public async Task EveryRequiredInputMutationIsRejectedWithoutChangingPinnedOriginals()
    {
        var root = FullBilingualVectorArtifactTests.FindRepositoryRoot();
        if (root is null) Assert.Inconclusive("Repository root was not found.");
        string P(params string[] parts) => Path.Combine([root!, .. parts]);
        var catalogDir = P("database", "data", "derived", "final", "sr-search-v1", "sr-p8-full-01", "catalog");
        var catalogPath = Path.Combine(catalogDir, "movies-catalog.jsonl");
        var catalogManifest = Path.Combine(catalogDir, "manifest.json");
        var sourcePath = P("database", "data", "derived", "final", "b05a-real-tmdb-01", "movies-catalog.jsonl");
        var dictionary = P("database", "data", "derived", "translations", "sr-latn-v1-r1", "tag-translations-sr.json");
        var enDirectory = P("database", "data", "embeddings", "multilingual-e5-base-int8-onnx-v1", "sr-p8-full-01", "en", "published");
        var enVectors = Path.Combine(enDirectory, "document-vectors.jsonl");
        var enManifest = Path.Combine(enDirectory, "manifest.json");
        var srDirectory = P("database", "data", "embeddings", "multilingual-e5-base-int8-onnx-v1", "sr-p8-full-01", "sr", "published");
        var srVectors = Path.Combine(srDirectory, "document-vectors.jsonl");
        var srManifest = Path.Combine(srDirectory, "manifest.json");
        var originalHashes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["catalog"] = HashFile(catalogPath), ["catalogManifest"] = HashFile(catalogManifest),
            ["dictionary"] = HashFile(dictionary), ["enVectors"] = HashFile(enVectors), ["enManifest"] = HashFile(enManifest),
            ["srVectors"] = HashFile(srVectors), ["srManifest"] = HashFile(srManifest)
        };
        var tempRoot = Path.Combine(Path.GetTempPath(), "sr-p9-input-mutations-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var checks = new List<MutationCheck>();
        try
        {
            var catalog = await FullBilingualCatalog.LoadAsync(catalogPath, catalogManifest, dictionary, sourcePath);
            var badCatalogManifest = Path.Combine(tempRoot, "catalog-manifest-mutated.json");
            var manifestText = await File.ReadAllTextAsync(catalogManifest);
            var badManifestText = manifestText.Replace(catalog.CatalogSha256, new string('0', 64), StringComparison.Ordinal);
            await File.WriteAllTextAsync(badCatalogManifest, badManifestText, new UTF8Encoding(false));
            checks.Add(await ExpectRejectAsync("corrupt catalog identity manifest", () => FullBilingualCatalog.LoadAsync(catalogPath, badCatalogManifest, dictionary, sourcePath)));

            var badCatalog = Path.Combine(tempRoot, "catalog-corrupt.jsonl");
            File.Copy(catalogPath, badCatalog);
            using (var stream = new FileStream(badCatalog, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var firstByte = stream.ReadByte();
                if (firstByte < 0) throw new InvalidDataException("Canonical catalog unexpectedly empty.");
                stream.Position = 0; stream.WriteByte((byte)(firstByte ^ 1)); stream.Flush(true);
            }
            checks.Add(await ExpectRejectAsync("corrupt catalog JSONL byte", () => FullBilingualCatalog.LoadAsync(badCatalog, catalogManifest, dictionary, sourcePath)));

            var wrongDictionary = Path.Combine(tempRoot, "wrong-dictionary.json");
            File.Copy(dictionary, wrongDictionary);
            await File.AppendAllTextAsync(wrongDictionary, " ", new UTF8Encoding(false));
            checks.Add(await ExpectRejectAsync("wrong dictionary bytes", () => FullBilingualCatalog.LoadAsync(catalogPath, catalogManifest, wrongDictionary, sourcePath)));

            checks.Add(await ExpectRejectAsync("missing EN artifact", () => FullBilingualVectorArtifactValidator.LoadAsync(Path.Combine(tempRoot, "missing-en"), catalog, "en")));
            checks.Add(await ExpectRejectAsync("missing SR artifact", () => FullBilingualVectorArtifactValidator.LoadAsync(Path.Combine(tempRoot, "missing-sr"), catalog, "sr")));
            checks.Add(await ExpectRejectAsync("EN/SR artifact path swapped with requested language", () => FullBilingualVectorArtifactValidator.LoadAsync(enDirectory, catalog, "sr")));

            var mutatedArtifactDir = Path.Combine(tempRoot, "en-mutated");
            Directory.CreateDirectory(mutatedArtifactDir);
            var copiedVectors = Path.Combine(mutatedArtifactDir, "document-vectors.jsonl");
            File.Copy(enVectors, copiedVectors);
            var artifactManifest = JsonNode.Parse(await File.ReadAllTextAsync(enManifest))!;
            artifactManifest["profile"] = "unapproved-profile";
            await File.WriteAllTextAsync(Path.Combine(mutatedArtifactDir, "manifest.json"), artifactManifest.ToJsonString(), new UTF8Encoding(false));
            checks.Add(await ExpectRejectAsync("wrong profile", () => FullBilingualVectorArtifactValidator.LoadAsync(mutatedArtifactDir, catalog, "en")));

            await WriteDuplicateIdVectorFileAsync(enVectors, copiedVectors);
            artifactManifest = JsonNode.Parse(await File.ReadAllTextAsync(enManifest))!;
            artifactManifest["outputSha256"] = HashFile(copiedVectors);
            await File.WriteAllTextAsync(Path.Combine(mutatedArtifactDir, "manifest.json"), artifactManifest.ToJsonString(), new UTF8Encoding(false));
            checks.Add(await ExpectRejectAsync("duplicate movie ID with matching recomputed output SHA", () => FullBilingualVectorArtifactValidator.LoadAsync(mutatedArtifactDir, catalog, "en")));

            var partialDirectory = Path.Combine(tempRoot, "partial-en");
            Directory.CreateDirectory(partialDirectory);
            var partialVectors = Path.Combine(partialDirectory, "document-vectors.jsonl");
            await RewriteVectorFileAsync(enVectors, partialVectors, removeLastRow: true);
            await WriteMutatedManifestAsync(enManifest, Path.Combine(partialDirectory, "manifest.json"), HashFile(partialVectors), recordCount: 9729);
            checks.Add(await ExpectRejectAsync("partial EN set (one row removed; output SHA and recordCount recomputed truthfully)", () => FullBilingualVectorArtifactValidator.LoadAsync(partialDirectory, catalog, "en")));

            var partialSrDirectory = Path.Combine(tempRoot, "partial-sr");
            Directory.CreateDirectory(partialSrDirectory);
            var partialSrVectors = Path.Combine(partialSrDirectory, "document-vectors.jsonl");
            await RewriteVectorFileAsync(srVectors, partialSrVectors, removeLastRow: true);
            await WriteMutatedManifestAsync(srManifest, Path.Combine(partialSrDirectory, "manifest.json"), HashFile(partialSrVectors), recordCount: 9729);
            checks.Add(await ExpectRejectAsync("partial SR set (one row removed; output SHA and recordCount recomputed truthfully)", () => FullBilingualVectorArtifactValidator.LoadAsync(partialSrDirectory, catalog, "sr")));

            await RewriteFirstVectorAsync(enVectors, copiedVectors, dimension: 767, zeroNormalize: false);
            await WriteMutatedManifestAsync(enManifest, Path.Combine(mutatedArtifactDir, "manifest.json"), HashFile(copiedVectors));
            checks.Add(await ExpectRejectAsync("wrong vector dimension with recomputed output SHA", () => FullBilingualVectorArtifactValidator.LoadAsync(mutatedArtifactDir, catalog, "en")));

            await RewriteFirstVectorAsync(enVectors, copiedVectors, dimension: null, zeroNormalize: true);
            await WriteMutatedManifestAsync(enManifest, Path.Combine(mutatedArtifactDir, "manifest.json"), HashFile(copiedVectors));
            checks.Add(await ExpectRejectAsync("non-normalized zero vector with recomputed output SHA", () => FullBilingualVectorArtifactValidator.LoadAsync(mutatedArtifactDir, catalog, "en")));
        }
        finally
        {
            if (Directory.Exists(tempRoot) && Path.GetFullPath(tempRoot).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                Directory.Delete(tempRoot, recursive: true);
        }

        var finalHashes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["catalog"] = HashFile(catalogPath), ["catalogManifest"] = HashFile(catalogManifest),
            ["dictionary"] = HashFile(dictionary), ["enVectors"] = HashFile(enVectors), ["enManifest"] = HashFile(enManifest),
            ["srVectors"] = HashFile(srVectors), ["srManifest"] = HashFile(srManifest)
        };
        var report = new
        {
            schemaVersion = "sr-p9-input-mutation-checks-v1",
            createdUtc = DateTimeOffset.UtcNow,
            scope = "Temporary copies only; no DB connection, protected input modification, threshold change, or model action.",
            checks,
            originalPinnedFileSha256 = originalHashes,
            finalPinnedFileSha256 = finalHashes,
            originalsUnchanged = originalHashes.OrderBy(x => x.Key).SequenceEqual(finalHashes.OrderBy(x => x.Key))
        };
        var reportDirectory = P(".local", "planning", "reports", "sr-phase-09", "import");
        Directory.CreateDirectory(reportDirectory);
        var reportPath = Path.Combine(reportDirectory, "input-mutation-checks-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssZ") + ".json");
        await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

        Assert.AreEqual(12, checks.Count);
        Assert.IsTrue(checks.All(x => x.Rejected), "At least one mutated input was accepted.");
        Assert.IsTrue(report.originalsUnchanged, "At least one protected original input changed.");
    }

    private static async Task<MutationCheck> ExpectRejectAsync(string name, Func<Task> operation)
    {
        try
        {
            await operation();
            return new(name, false, "accepted");
        }
        catch (InvalidDataException ex)
        {
            return new(name, true, ex.GetType().Name);
        }
    }

    private static async Task WriteDuplicateIdVectorFileAsync(string source, string destination)
    {
        var temp = destination + ".rewrite";
        using (var input = new StreamReader(source, new UTF8Encoding(false, true)))
        using (var output = new StreamWriter(temp, false, new UTF8Encoding(false)))
        {
            var first = await input.ReadLineAsync() ?? throw new InvalidDataException("Vector source is empty.");
            using var firstDocument = JsonDocument.Parse(first);
            var firstId = firstDocument.RootElement.GetProperty("movieLensId").GetInt64();
            await output.WriteLineAsync(first);
            var second = await input.ReadLineAsync() ?? throw new InvalidDataException("Vector source has fewer than two rows.");
            using var secondDocument = JsonDocument.Parse(second);
            var secondId = secondDocument.RootElement.GetProperty("movieLensId").GetInt64();
            var oldId = "\"movieLensId\":" + secondId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var newId = "\"movieLensId\":" + firstId.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await output.WriteLineAsync(second.Replace(oldId, newId, StringComparison.Ordinal));
            string? line;
            while ((line = await input.ReadLineAsync()) is not null) await output.WriteLineAsync(line);
        }
        File.Move(temp, destination, overwrite: true);
    }

    private static async Task RewriteVectorFileAsync(string source, string destination, bool removeLastRow)
    {
        using var input = new StreamReader(source, new UTF8Encoding(false, true));
        using var output = new StreamWriter(destination, false, new UTF8Encoding(false));
        var allButLast = new Queue<string>(2);
        string? line;
        while ((line = await input.ReadLineAsync()) is not null)
        {
            allButLast.Enqueue(line);
            if (allButLast.Count > (removeLastRow ? 1 : 0)) await output.WriteLineAsync(allButLast.Dequeue());
        }
        if (!removeLastRow) while (allButLast.Count > 0) await output.WriteLineAsync(allButLast.Dequeue());
    }

    private static async Task RewriteFirstVectorAsync(string source, string destination, int? dimension, bool zeroNormalize)
    {
        var temp = destination + ".rewrite";
        using (var input = new StreamReader(source, new UTF8Encoding(false, true)))
        using (var output = new StreamWriter(temp, false, new UTF8Encoding(false)))
        {
            var first = await input.ReadLineAsync() ?? throw new InvalidDataException("Vector source is empty.");
            var row = JsonNode.Parse(first)!.AsObject();
            var vector = row["vector"]!.AsArray();
            if (dimension is int length) while (vector.Count > length) vector.RemoveAt(vector.Count - 1);
            if (zeroNormalize) row["vector"] = new JsonArray(Enumerable.Range(0, 768).Select(_ => (JsonNode?)JsonValue.Create(0f)).ToArray());
            await output.WriteLineAsync(row.ToJsonString());
            string? line;
            while ((line = await input.ReadLineAsync()) is not null) await output.WriteLineAsync(line);
        }
        File.Move(temp, destination, overwrite: true);
    }

    private static async Task WriteMutatedManifestAsync(string sourceManifest, string destinationManifest, string outputSha, int? recordCount = null)
    {
        var manifest = JsonNode.Parse(await File.ReadAllTextAsync(sourceManifest))!;
        manifest["outputSha256"] = outputSha;
        if (recordCount is not null) manifest["recordCount"] = recordCount.Value;
        await File.WriteAllTextAsync(destinationManifest, manifest.ToJsonString(), new UTF8Encoding(false));
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private sealed record MutationCheck(string Name, bool Rejected, string ObservedError);
}
