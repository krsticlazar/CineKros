using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CineKros.Catalog.Importer;

namespace CineKros.Importer.Tests;

[TestClass]
public sealed class CorrectedPocCatalogTests
{
    private static string? FindRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "database/data/derived/serbian-search/poc-v1/translation/tag-translations-sr.json"))) return directory.FullName;
        return null;
    }

    [TestMethod]
    public void DictionaryDeltaChangesOnlyApprovedSerbianValuesAndRetainsMachineOutput()
    {
        var root = FindRoot();
        if (root is null) Assert.Inconclusive("Frozen Serbian-search POC inputs are not installed for this test run.");
        var baseRoot = Path.Combine(root!, "database/data/derived/serbian-search/poc-v1");
        var approved = Path.Combine(root!, ".local/planning/reports/sr-phase-06t/review/corrections-approved.json");
        var temp = Path.Combine(Path.GetTempPath(), "cinekros-p6t-dictionary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var output = Path.Combine(temp, "tag-translations-sr.json");
            var actualHash = CorrectedPocCatalog.CreateCorrectedDictionary(Path.Combine(baseRoot, "translation/tag-translations-sr.json"), approved, output);
            using var oldDictionary = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(baseRoot, "translation/tag-translations-sr.json")));
            using var newDictionary = JsonDocument.Parse(File.ReadAllBytes(output));
            using var map = JsonDocument.Parse(File.ReadAllBytes(approved));
            var approvedEntries = map.RootElement.GetProperty("entries").EnumerateArray().ToDictionary(x => x.GetProperty("en").GetString()!, StringComparer.Ordinal);
            var oldEntries = oldDictionary.RootElement.GetProperty("entries").EnumerateArray().ToDictionary(x => x.GetProperty("en").GetString()!, StringComparer.Ordinal);
            var newEntries = newDictionary.RootElement.GetProperty("entries").EnumerateArray().ToDictionary(x => x.GetProperty("en").GetString()!, StringComparer.Ordinal);
            Assert.AreEqual(418, newEntries.Count);
            Assert.AreEqual(24, approvedEntries.Count);
            foreach (var (key, old) in oldEntries)
            {
                var current = newEntries[key];
                Assert.AreEqual(old.GetProperty("machine").GetString(), current.GetProperty("machine").GetString(), key);
                if (approvedEntries.TryGetValue(key, out var change))
                {
                    Assert.AreEqual(change.GetProperty("proposedSR").GetString(), current.GetProperty("sr").GetString(), key);
                    Assert.AreEqual(old.GetProperty("sr").GetString(), current.GetProperty("previousAccepted").GetString(), key);
                    Assert.AreEqual("reviewed", current.GetProperty("reviewStatus").GetString(), key);
                    Assert.IsTrue(current.GetProperty("manualOverride").GetBoolean(), key);
                }
                else
                {
                    Assert.AreEqual(old.GetProperty("sr").GetString(), current.GetProperty("sr").GetString(), key);
                    Assert.AreEqual(old.GetProperty("reviewStatus").GetString(), current.GetProperty("reviewStatus").GetString(), key);
                    Assert.AreEqual(old.GetProperty("manualOverride").GetBoolean(), current.GetProperty("manualOverride").GetBoolean(), key);
                }
            }
            Assert.AreEqual(CorrectedPocCatalog.ApprovedMappingSha256, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(approved))));
            Assert.IsTrue(actualHash == File.ReadAllText(Path.Combine(temp, "content.sha256")).Trim());
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [TestMethod]
    public void DictionaryDeltaRejectsAnUnapprovedMappingByteChange()
    {
        var root = FindRoot();
        if (root is null) Assert.Inconclusive("Frozen Serbian-search POC inputs are not installed for this test run.");
        var baseRoot = Path.Combine(root!, "database/data/derived/serbian-search/poc-v1");
        var approved = Path.Combine(root!, ".local/planning/reports/sr-phase-06t/review/corrections-approved.json");
        var temp = Path.Combine(Path.GetTempPath(), "cinekros-p6t-mapping-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var node = JsonNode.Parse(File.ReadAllBytes(approved))!;
            node["entries"]![0]!["proposedSR"] = "neodobrena izmena";
            var altered = Path.Combine(temp, "approved.json"); File.WriteAllBytes(altered, JsonSerializer.SerializeToUtf8Bytes(node));
            Assert.ThrowsExactly<InvalidDataException>(() => CorrectedPocCatalog.CreateCorrectedDictionary(
                Path.Combine(baseRoot, "translation/tag-translations-sr.json"), altered, Path.Combine(temp, "tag-translations-sr.json")));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [TestMethod]
    public async Task CorrectedLoaderAcceptsTruthfulV2ReleaseAndRejectsChangedCatalogBytes()
    {
        var root = FindRoot();
        if (root is null) Assert.Inconclusive("Frozen Serbian-search POC inputs are not installed for this test run.");
        var baseRoot = Path.Combine(root!, "database/data/derived/serbian-search/poc-v1");
        var sourceCatalog = Path.Combine(root!, "database/data/derived/final/b05a-real-tmdb-01/movies-catalog.jsonl");
        var approved = Path.Combine(root!, ".local/planning/reports/sr-phase-06t/review/corrections-approved.json");
        var project = Path.Combine(root!, ".local/planning/reports/sr-phase-06t/tooling/CineKros.SrP6T.Tools.csproj");
        var temp = Path.Combine(Path.GetTempPath(), "cinekros-p6t-release-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var release = Path.Combine(temp, "poc-v2");
            await BuildReleaseAsync(root!, project, baseRoot, sourceCatalog, approved, release);
            var corrected = await CorrectedPocCatalog.LoadAsync(Path.Combine(release, "catalog/movies-catalog.jsonl"),
                Path.Combine(release, "catalog/manifest.json"), Path.Combine(release, "translation/tag-translations-sr.json"), approved,
                Path.Combine(baseRoot, "catalog/movies-catalog.jsonl"), Path.Combine(baseRoot, "catalog/manifest.json"),
                Path.Combine(baseRoot, "translation/tag-translations-sr.json"), sourceCatalog);
            var baseline = await MultilingualPocCatalog.LoadAsync(Path.Combine(baseRoot, "catalog/movies-catalog.jsonl"),
                Path.Combine(baseRoot, "catalog/manifest.json"), Path.Combine(baseRoot, "translation/tag-translations-sr.json"), sourceCatalog);
            Assert.AreEqual(150, corrected.Movies.Count);
            Assert.AreNotEqual(baseline.IdentitySha256, corrected.IdentitySha256);
            Assert.AreNotEqual(MultilingualPocCatalog.CatalogSha256, corrected.CatalogSha256);
            Assert.AreNotEqual(MultilingualPocCatalog.DictionarySha256, corrected.DictionarySha256);
            using (var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(release, "catalog/manifest.json"))))
            {
                Assert.AreEqual(CorrectedPocCatalog.DatasetRelease, manifest.RootElement.GetProperty("datasetRelease").GetString());
                Assert.AreEqual(CorrectedPocCatalog.ApprovedMappingSha256, manifest.RootElement.GetProperty("correctionProposalSha256").GetString());
            }
            var repeat = Path.Combine(temp, "poc-v2-repeat");
            await BuildReleaseAsync(root!, project, baseRoot, sourceCatalog, approved, repeat);
            Assert.AreEqual(HashFile(Path.Combine(release, "catalog/movies-catalog.jsonl")), HashFile(Path.Combine(repeat, "catalog/movies-catalog.jsonl")));
            Assert.AreEqual(HashFile(Path.Combine(release, "translation/tag-translations-sr.json")), HashFile(Path.Combine(repeat, "translation/tag-translations-sr.json")));
            var catalogPath = Path.Combine(release, "catalog/movies-catalog.jsonl");
            var original = await File.ReadAllBytesAsync(catalogPath);
            try
            {
                var lines = Encoding.UTF8.GetString(original).Split('\n');
                var row = JsonNode.Parse(lines[0])!; row["semanticText"] = "unapproved text"; lines[0] = row.ToJsonString();
                await File.WriteAllTextAsync(catalogPath, string.Join('\n', lines), new UTF8Encoding(false));
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => CorrectedPocCatalog.LoadAsync(catalogPath,
                    Path.Combine(release, "catalog/manifest.json"), Path.Combine(release, "translation/tag-translations-sr.json"), approved,
                    Path.Combine(baseRoot, "catalog/movies-catalog.jsonl"), Path.Combine(baseRoot, "catalog/manifest.json"),
                    Path.Combine(baseRoot, "translation/tag-translations-sr.json"), sourceCatalog));
            }
            finally { await File.WriteAllBytesAsync(catalogPath, original); }
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    private static async Task BuildReleaseAsync(string root, string project, string baseRoot, string sourceCatalog, string approved, string release)
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var value in new[] { "run", "--project", project, "--", "build-catalog", "--base-catalog", Path.Combine(baseRoot, "catalog/movies-catalog.jsonl"),
                     "--base-manifest", Path.Combine(baseRoot, "catalog/manifest.json"), "--base-dictionary", Path.Combine(baseRoot, "translation/tag-translations-sr.json"),
                     "--approved-mapping", approved, "--source-catalog", sourceCatalog, "--output-root", release }) start.ArgumentList.Add(value);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to run the bounded local catalog builder.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) Assert.Fail("Temporary corrected catalog build failed: " + await stdout + await stderr);
    }
    private static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
}
