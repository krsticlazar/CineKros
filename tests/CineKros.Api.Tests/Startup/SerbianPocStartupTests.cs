using CineKros.Api.Startup;
using CineKros.Api.Search;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace CineKros.Api.Tests.Startup;

[TestClass]
public sealed class SerbianPocStartupTests
{
    [TestMethod]
    public void POCActivationDefaultsOffAndRequiresExactTrueInDevelopmentRealMode()
    {
        var development = Environment("Development");
        var production = Environment("Production");

        Assert.IsFalse(RecommendationStartup.ResolveSerbianPocMode(development, "real", null));
        Assert.IsFalse(RecommendationStartup.ResolveSerbianPocMode(development, "real", "false"));
        Assert.IsTrue(RecommendationStartup.ResolveSerbianPocMode(development, "real", "true"));
        Assert.ThrowsExactly<InvalidOperationException>(() => RecommendationStartup.ResolveSerbianPocMode(development, "real", "True"));
        Assert.ThrowsExactly<InvalidOperationException>(() => RecommendationStartup.ResolveSerbianPocMode(development, "fake", "true"));
        Assert.ThrowsExactly<InvalidOperationException>(() => RecommendationStartup.ResolveSerbianPocMode(production, "real", "true"));
    }

    [TestMethod]
    public void POCRegistrationCannotBeCombinedWithFakeMode()
    {
        var services = new ServiceCollection();

        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => RecommendationStartup.RegisterServices(services, "fake", serbianPoc: true));

        StringAssert.Contains(exception.Message, "requires real recommendation mode");
    }

    [TestMethod]
    public void POCDatabaseTargetIsExactAndRejectsProductionOrSiblingDatabases()
    {
        RecommendationStartup.ValidatePocDatabaseTarget("cinekros_sr_poc_phase06t_v2_20261009");

        foreach (var wrongDatabase in new[] { "cinekros", "postgres", "cinekros_sr_poc_phase04_20261008", "cinekros_sr_poc_phase04_test_runtime", "cinekros_sr_poc_phase06t_test_runtime" })
            Assert.ThrowsExactly<InvalidOperationException>(() => RecommendationStartup.ValidatePocDatabaseTarget(wrongDatabase));
    }

    [TestMethod]
    public void V1DefaultAndExplicitCorrectedV2ExpectationsRemainSeparateAndPinned()
    {
        var v1 = SearchDatasetExpectation.SerbianPhase4Poc("cinekros_sr_poc_phase04_20261008");
        Assert.AreEqual("4881fb5699398c787f88496e2b803c58a743061e7e604a52dba312743291bbf7", v1.CatalogSha256);
        Assert.AreEqual("efd13b367a6ad0bef10ec4f32e89d1b57180a2d95546423175a4d1c1dc3bc230", v1.CatalogIdentity);

        var v2 = SearchDatasetExpectation.SerbianPhase6TV2Poc("cinekros_sr_poc_phase06t_v2_20261009");
        Assert.AreEqual("cinekros_sr_poc_phase06t_v2_20261009", v2.DatabaseName);
        Assert.AreEqual(150, v2.MovieCount);
        Assert.AreEqual("2887a937689294b029dd918911dd10151bfd3ece74fa573fcc5d85dc2f86886e", v2.CatalogSha256);
        Assert.AreEqual("0fd18234a4ee0c4f8e6054a9143935ef5dfc3669494e4b6f1637b6be105f8d66", v2.CatalogIdentity);
        Assert.AreEqual("5c5760576471547ea4d6db17acfd254ff1a0d4e7c47fe54b0f4db6a9795fdde0", v2.EnCorpusSha256);
        Assert.AreEqual("fc26b53581e6adc795573df511b7e7f72c7fd59fe7e31efddb29e0ebf9d7e8c0", v2.SrCorpusSha256);
        Assert.AreEqual("5ccad923ad7156bc10ee27d0b5b56163d0af05abf52e9bbd0b4df6cd715e6a81", v2.EnArtifactSha256);
        Assert.AreEqual("228493dfeabf83dd5b3d5b0b9cf6c445ebc48742e9cf8f810bc8517959d7baba", v2.SrArtifactSha256);
        Assert.AreEqual("09bd0afbde2b7b8a0afee502d7718f8dad6cc6320c226b437074ebb4b341ea4e", v2.DictionarySha256);
        Assert.AreEqual("eac906ed78f7863573d13c9b0435de1b8f848fe92fc6ae08aa3621400260b1fe", v2.ProfileFingerprint);

        foreach (var wrongDatabase in new[] { "cinekros_sr_poc_phase04_20261008", "cinekros", "postgres", "cinekros_sr_poc_phase06t_test_runtime" })
            Assert.ThrowsExactly<InvalidOperationException>(() => SearchDatasetExpectation.SerbianPhase6TV2Poc(wrongDatabase));
    }

    private static IHostEnvironment Environment(string environmentName) => new TestHostEnvironment { EnvironmentName = environmentName };

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "CineKros.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
