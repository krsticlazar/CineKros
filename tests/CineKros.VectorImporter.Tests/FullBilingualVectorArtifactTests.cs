using CineKros.Catalog.Importer;
using CineKros.VectorImporter;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CineKros.VectorImporter.Tests;

[TestClass]
public sealed class FullBilingualVectorArtifactTests
{
    [TestMethod]
    public async Task ValidatesThePublishedPhase8EnglishAndSerbianArtifactsWhenLocalDataIsAvailable()
    {
        var root = FindRepositoryRoot();
        if (root is null) Assert.Inconclusive("Canonical repository root was not found from the test output directory.");
        var catalogRoot = Path.Combine(root!, "database", "data", "derived", "final", "sr-search-v1", "sr-p8-full-01", "catalog");
        var source = Path.Combine(root!, "database", "data", "derived", "final", "b05a-real-tmdb-01", "movies-catalog.jsonl");
        var dictionary = Path.Combine(root!, "database", "data", "derived", "translations", "sr-latn-v1-r1", "tag-translations-sr.json");
        var artifactRoot = Path.Combine(root!, "database", "data", "embeddings", "multilingual-e5-base-int8-onnx-v1", "sr-p8-full-01");
        var catalogPath = Path.Combine(catalogRoot, "movies-catalog.jsonl");
        var manifestPath = Path.Combine(catalogRoot, "manifest.json");
        if (!File.Exists(catalogPath) || !File.Exists(manifestPath) || !File.Exists(source) || !File.Exists(dictionary) ||
            !File.Exists(Path.Combine(artifactRoot, "en", "published", "document-vectors.jsonl")) ||
            !File.Exists(Path.Combine(artifactRoot, "sr", "published", "document-vectors.jsonl")))
            Assert.Inconclusive("Local Phase 8 protected inputs are not available; this test never downloads data.");

        var catalog = await FullBilingualCatalog.LoadAsync(catalogPath, manifestPath, dictionary, source);
        var en = await FullBilingualVectorArtifactValidator.LoadAsync(Path.Combine(artifactRoot, "en", "published"), catalog, "en");
        var sr = await FullBilingualVectorArtifactValidator.LoadAsync(Path.Combine(artifactRoot, "sr", "published"), catalog, "sr");

        Assert.AreEqual(FullBilingualCatalog.ExpectedMovieCount, en.Records.Count);
        Assert.AreEqual(FullBilingualCatalog.ExpectedMovieCount, sr.Records.Count);
        Assert.AreEqual("3dde59fa8bbf82452e118fd7f65f9c1e3fd8e630b9bad05a0b123e1e8468053e", en.ArtifactSha256);
        Assert.AreEqual("ff5db33c1c6871a8bd04275a5e6a5167232de9d40e98f3802425c0d2efa0c204", sr.ArtifactSha256);
        Assert.AreNotEqual(en.Records[0].Fingerprint, sr.Records[0].Fingerprint);
    }

    internal static string? FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")) && Directory.Exists(Path.Combine(directory.FullName, ".local")))
                return directory.FullName;
        return null;
    }
}
