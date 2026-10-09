using CineKros.VectorImporter;

namespace CineKros.VectorImporter.Tests;

[TestClass]
public sealed class CorrectedPocImportGuardTests
{
    [TestMethod]
    public void CorrectedImportGuardAcceptsOnlyItsExactDatabaseAndTestFamily()
    {
        Assert.IsTrue(PairedPocImporter.IsAllowedTargetDatabase(
            PairedPocImporter.CorrectedDatabaseName, PairedPocImporter.CorrectedDatabaseName, "cinekros_sr_poc_phase06t_test_"));
        Assert.IsTrue(PairedPocImporter.IsAllowedTargetDatabase(
            "cinekros_sr_poc_phase06t_test_import", PairedPocImporter.CorrectedDatabaseName, "cinekros_sr_poc_phase06t_test_"));
        Assert.IsFalse(PairedPocImporter.IsAllowedTargetDatabase(
            PairedPocImporter.DatabaseName, PairedPocImporter.CorrectedDatabaseName, "cinekros_sr_poc_phase06t_test_"));
        Assert.IsFalse(PairedPocImporter.IsAllowedTargetDatabase(
            "cinekros_prod", PairedPocImporter.CorrectedDatabaseName, "cinekros_sr_poc_phase06t_test_"));
        Assert.IsFalse(PairedPocImporter.IsAllowedTargetDatabase(
            "cinekros_sr_poc_phase06t_test", PairedPocImporter.CorrectedDatabaseName, "cinekros_sr_poc_phase06t_test_"));
    }
}
