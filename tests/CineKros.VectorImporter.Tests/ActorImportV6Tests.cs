using System.Text;
using CineKros.VectorImporter;

namespace CineKros.VectorImporter.Tests;

[TestClass]
public sealed class ActorImportV6Tests
{
    [TestMethod]
    public void TargetGuardIsAnchoredExactAndCannotApproveProduction()
    {
        foreach (var name in new[] { "cinekros_v6_rehearsal_20261016", "cinekros_v6_test_import", "cinekros_v6_test_a_1" })
        {
            Assert.IsTrue(ActorImportBaselineV6.IsAllowedExecutionTarget(name, name));
            Assert.IsTrue(ActorImportBaselineV6.IsAllowedExecutionTarget(name, name, true));
            ActorImportBaselineV6.RequireConnectionTarget("Host=127.0.0.1;Database=" + name, name);
            Assert.IsFalse(ActorImportBaselineV6.IsAllowedExecutionTarget(name, name + "_other"));
        }
        foreach (var name in new[] { "cinekros", "", "cinekros_sr_p9_test_import", "cinekros_v6_restore_20261016", "cinekros_v6_test_", "cinekros_v6_test_Upper", "cinekros_v6_test_x;DROP DATABASE cinekros", "x_cinekros_v6_test_x", "cinekros_v6_test_x\n", "cinekros_v6_test_x-other" })
        {
            Assert.IsFalse(ActorImportBaselineV6.IsAllowedExecutionTarget(name, name), name);
            Assert.IsFalse(ActorImportBaselineV6.IsAllowedExecutionTarget(name, name, true), name);
        }
    }

    [TestMethod]
    public async Task LibraryRejectsUnsafeConnectionBeforeReadingFilesOrOpeningDatabase()
    {
        const string isolated = "cinekros_v6_test_offline";
        foreach (var target in new[] { "cinekros", "cinekros_v6_test_other", "postgres", "" })
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ActorPairedImporterV6.ImportAsync(
                "Host=192.0.2.1;Timeout=1;Database=" + target, isolated, "missing", "missing", "missing", "missing", "missing", "missing", "missing"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => ActorPairedImporterV6.ImportAsync(
            "Host=192.0.2.1;Timeout=1;Database=cinekros", "cinekros", "missing", "missing", "missing", "missing", "missing", "missing", "missing"));
    }

    [TestMethod]
    public async Task CliRejectsProductionAndMalformedArgumentsBeforeEnvironmentOrFileUse()
    {
        string[] valid = ["--paired-actor-v6", "--expected-database", "cinekros", "--baseline", "missing", "--catalog", "missing", "--manifest", "missing", "--dictionary", "missing", "--source-catalog", "missing", "--pair-root", "missing", "--migrations", "missing"];
        Assert.AreEqual(2, await ActorImportCommandV6.RunAsync(valid));
        Assert.AreEqual(2, await ActorImportCommandV6.RunAsync(["--paired-actor-v6"]));
        Assert.AreEqual(2, await ActorImportCommandV6.RunAsync([.. valid, "--force", "true"]));
        Assert.AreEqual(2, await ActorImportCommandV6.RunAsync(["--capture-actor-v6-baseline", "--unknown", "x"]));
    }

    [TestMethod]
    public async Task PublicInputReaderRejectsRelativePathsAndCancellationWithoutDatabase()
    {
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => ActorPairedImporterV6.ValidateInputsAsync("relative", "x", "x", "x", "x", "x", "x"));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var absolute = Path.GetFullPath("missing-v6-input.json");
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => ActorPairedImporterV6.ValidateInputsAsync(absolute, absolute, absolute, absolute, absolute, absolute, absolute, cancel.Token));
    }

    [TestMethod]
    public void NumericMetadataComparisonPreservesPrecisionBeyondDecimalOrDouble()
    {
        Assert.IsTrue(ActorPairedImporterV6.NumericEqual("3.1234567890123456789012345678901234567890", "31234567890123456789012345678901234567890e-40"));
        Assert.IsFalse(ActorPairedImporterV6.NumericEqual("3.1234567890123456789012345678901234567890", "3.1234567890123456789012345678901234567891"));
        Assert.IsTrue(ActorPairedImporterV6.NumericEqual("0.00000", "0e+17"));
        Assert.IsTrue(ActorPairedImporterV6.NumericEqual("3.50", "35E-1"));
        Assert.IsFalse(ActorPairedImporterV6.NumericEqual(null, "0"));
        Assert.IsTrue(ActorPairedImporterV6.NumericEqual(null, null));
    }

    [TestMethod]
    public async Task CanonicalMigrationsArePinnedAndAnAltered003IsRefusedOffline()
    {
        var root = FullBilingualVectorArtifactTests.FindRepositoryRoot(); Assert.IsNotNull(root);
        var directory = Path.Combine(root, "database", "migrations");
        foreach (var (file, sha) in new[] { ("001_initial_schema.sql", ActorImportBaselineV6.Migration001Sha256), ("002_serbian_search_vectors.sql", ActorImportBaselineV6.Migration002Sha256), ("003_actor_hard_filters.sql", ActorImportBaselineV6.Migration003Sha256) })
            _ = await ActorPairedImporterV6.ReadMigrationAsync(directory, file, sha, default);
        var temp = Path.Combine(Path.GetTempPath(), "cinekros-v6-sql-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(temp, "003_actor_hard_filters.sql"), "SELECT 1;", new UTF8Encoding(false));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ActorPairedImporterV6.ReadMigrationAsync(temp, "003_actor_hard_filters.sql", ActorImportBaselineV6.Migration003Sha256, default));
        }
        finally { Directory.Delete(temp, true); }
    }

    [TestMethod]
    public void FaultPointsAreExplicitAndUnknownFaultCannotBecomeNoop()
    {
        Assert.AreEqual(ActorImportFaultV6.AfterMigration, ActorImportCommandV6.ParseFault("after-migration"));
        Assert.AreEqual(ActorImportFaultV6.PartialCast, ActorImportCommandV6.ParseFault("partial-cast"));
        Assert.AreEqual(ActorImportFaultV6.PartialVectors, ActorImportCommandV6.ParseFault("partial-vectors"));
        Assert.AreEqual(ActorImportFaultV6.PartialState, ActorImportCommandV6.ParseFault("partial-state"));
        Assert.ThrowsExactly<ArgumentException>(() => ActorImportCommandV6.ParseFault("unknown"));
    }
}
