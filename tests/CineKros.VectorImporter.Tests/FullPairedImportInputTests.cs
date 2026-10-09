using CineKros.Catalog.Importer;
using CineKros.VectorImporter;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CineKros.VectorImporter.Tests;

[TestClass]
public sealed class FullPairedImportInputTests
{
    [TestMethod]
    public async Task ValidatesAllPinnedPhase8AndLegacyInputsBeforeDatabaseUse()
    {
        var root = FullBilingualVectorArtifactTests.FindRepositoryRoot();
        if (root is null) Assert.Inconclusive("Repository root was not found.");
        string P(params string[] parts) => Path.Combine([root!, .. parts]);
        var inputs = await FullPairedImporter.ValidatePinnedInputsAsync(
            P(".local", "planning", "reports", "sr-phase-09", "production-baseline.json"),
            P("database", "data", "derived", "final", "sr-search-v1", "sr-p8-full-01", "catalog", "movies-catalog.jsonl"),
            P("database", "data", "derived", "final", "sr-search-v1", "sr-p8-full-01", "catalog", "manifest.json"),
            P("database", "data", "derived", "translations", "sr-latn-v1-r1", "tag-translations-sr.json"),
            P("database", "data", "derived", "final", "b05a-real-tmdb-01", "movies-catalog.jsonl"),
            P("database", "data", "embeddings", "multilingual-e5-base-int8-onnx-v1", "sr-p8-full-01", "en", "published"),
            P("database", "data", "embeddings", "multilingual-e5-base-int8-onnx-v1", "sr-p8-full-01", "sr", "published"),
            P("database", "data", "derived", "final", "b05a-real-tmdb-01", "movies-catalog.jsonl"),
            P("database", "data", "embeddings", "e5-base-v2-int8-onnx-v1", "e5-b05a-real-20260927-01", "published"),
            P("database", "migrations"));

        Assert.AreEqual(9730, inputs.Catalog.Movies.Count);
        Assert.AreEqual(9730, inputs.En.Records.Count);
        Assert.AreEqual(9730, inputs.Sr.Records.Count);
        Assert.AreEqual(9730, inputs.Legacy.Records.Count);
        Assert.AreEqual("712f990185d2db34c55165081a9b80819ede7d2872f24989394ffa9eb13164c7", inputs.Legacy.ArtifactSha256);
        Assert.AreEqual("3dde59fa8bbf82452e118fd7f65f9c1e3fd8e630b9bad05a0b123e1e8468053e", inputs.En.ArtifactSha256);
        Assert.AreEqual("ff5db33c1c6871a8bd04275a5e6a5167232de9d40e98f3802425c0d2efa0c204", inputs.Sr.ArtifactSha256);
        Assert.AreEqual("14e5ae626c2288596d563482a510dcb8c79320dd5d41955cc5f06579dd59f238", inputs.Baseline.Migration001Sha256);
        Assert.AreEqual("857b675a33f5ef67670b6f0e2b3c5383e5454494cafe88e707ce12f50eb3b8e3", inputs.Baseline.Migration002Sha256);
    }
}
