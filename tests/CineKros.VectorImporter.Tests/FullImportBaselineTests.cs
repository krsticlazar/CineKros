using CineKros.VectorImporter;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CineKros.VectorImporter.Tests;

[TestClass]
public sealed class FullImportBaselineTests
{
    [TestMethod]
    public async Task ParsesPinnedProductionBaselineWithoutDatabaseAccess()
    {
        var root = FullBilingualVectorArtifactTests.FindRepositoryRoot();
        if (root is null) Assert.Inconclusive("Repository root was not found.");
        var path = Path.Combine(root!, ".local", "planning", "reports", "sr-phase-09", "production-baseline.json");
        if (!File.Exists(path)) Assert.Inconclusive("MAIN baseline descriptor is not available; this test never connects to a database.");

        var baseline = await FullImportBaseline.LoadAsync(path);
        Assert.AreEqual("cinekros", baseline.SourceDatabase);
        Assert.AreEqual("7690124133367640101", baseline.ServerSystemIdentifier);
        Assert.AreEqual(17, baseline.PostgresMajor);
        Assert.AreEqual(9730, baseline.MovieCount);
        Assert.AreEqual(9730, baseline.VectorCount);
        Assert.AreEqual("712f990185d2db34c55165081a9b80819ede7d2872f24989394ffa9eb13164c7", baseline.LegacyVectorArtifactSha256);
        Assert.IsFalse(FullImportBaseline.IsAllowedExecutionTarget("cinekros", "cinekros", false));
        Assert.IsFalse(FullImportBaseline.IsAllowedExecutionTarget("cinekros_sr_poc_phase04_20261008", "cinekros_sr_poc_phase04_20261008", false));
        Assert.IsTrue(FullImportBaseline.IsAllowedExecutionTarget("cinekros_sr_p9_rehearsal_20261009", "cinekros_sr_p9_rehearsal_20261009", false));
        Assert.IsTrue(FullImportBaseline.IsAllowedExecutionTarget("cinekros_sr_p9_test_import", "cinekros_sr_p9_test_import", false));
        Assert.IsFalse(FullImportBaseline.IsAllowedExecutionTarget("cinekros_sr_p9_test_import", "cinekros_sr_p9_test_other", false));
        Assert.IsTrue(FullImportBaseline.IsAllowedExecutionTarget("cinekros", "cinekros", true));
    }

    [TestMethod]
    public async Task RejectsMutatedBaselineBeforeAnyDatabaseUse()
    {
        var root = FullBilingualVectorArtifactTests.FindRepositoryRoot();
        if (root is null) Assert.Inconclusive("Repository root was not found.");
        var path = Path.Combine(root!, ".local", "planning", "reports", "sr-phase-09", "production-baseline.json");
        if (!File.Exists(path)) Assert.Inconclusive("MAIN baseline descriptor is not available.");
        var bytes = await File.ReadAllBytesAsync(path);
        var text = System.Text.Encoding.UTF8.GetString(bytes).Replace("7690124133367640101", "7690124133367640102", StringComparison.Ordinal);
        var temp = Path.Combine(Path.GetTempPath(), "cinekros-sr-p9-baseline-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllTextAsync(temp, text, new System.Text.UTF8Encoding(false));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => FullImportBaseline.LoadAsync(temp));
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}
