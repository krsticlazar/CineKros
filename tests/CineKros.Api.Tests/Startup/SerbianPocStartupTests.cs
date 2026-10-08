using CineKros.Api.Startup;
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
        RecommendationStartup.ValidatePocDatabaseTarget("cinekros_sr_poc_phase04_20261008");

        foreach (var wrongDatabase in new[] { "cinekros", "postgres", "cinekros_sr_poc_phase04_test_runtime" })
            Assert.ThrowsExactly<InvalidOperationException>(() => RecommendationStartup.ValidatePocDatabaseTarget(wrongDatabase));
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
