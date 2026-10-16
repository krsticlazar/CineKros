using CineKros.Api.RealProviders;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CineKros.Api.Tests.RealProviders;

[TestClass]
public sealed class ActorNameResolverTests
{
    [TestMethod]
    public void SnapshotsRegistryAndResolvesOnlyExactOrUniqueNormalizedNames()
    {
        var names = new List<string> { "Brad Pitt", "Robert Downey Jr.", "Penélope Cruz", "José", "JOSE", "Jose" };
        var resolver = new ActorNameResolver(names);
        names.Clear();
        Assert.AreEqual("Brad Pitt", resolver.Resolve(" brad PITT "));
        Assert.AreEqual("Robert Downey Jr.", resolver.Resolve("Robert Downey Jr."));
        Assert.AreEqual("Penélope Cruz", resolver.Resolve("Pene\u0301lope Cruz"));
        Assert.AreEqual("JOSE", resolver.Resolve("JOSE"));
        Assert.AreEqual("Jose", resolver.Resolve("Jose"));
        Assert.IsNull(resolver.Resolve("jose"));
        Assert.AreEqual("José", resolver.Resolve("josé"));
        Assert.IsTrue(resolver.IsCanonical("Brad Pitt"));
        Assert.IsFalse(resolver.IsCanonical("brad pitt"));
    }

    [TestMethod]
    public void NeverInfersAliasesTransliterationsSubstringsOrAsciiNames()
    {
        var resolver = new ActorNameResolver(["Brad Pitt", "Penélope Cruz", "Robert Downey Jr."]);
        foreach (var name in new[] { "Бред Пит", "Bred Pit", "Brada Pitta", "Pitt", "Brad", "Penelope Cruz", "Robert Downey", "Brad Pitt' OR 1=1 --", "\nBrad Pitt" })
            Assert.IsNull(resolver.Resolve(name), name);
    }

    [TestMethod]
    public void ReviewedFixtureAliasesAreExplicitSnapshottedAndCollisionChecked()
    {
        var aliases = new Dictionary<string, string> { ["Бред Пит"] = "Brad Pitt" };
        var resolver = new ActorNameResolver(["Brad Pitt"], aliases);
        aliases.Clear();
        Assert.AreEqual("Brad Pitt", resolver.Resolve(" БРЕД ПИТ "));
        Assert.IsFalse(resolver.IsCanonical("Бред Пит"));
        foreach (var invalid in new[]
        {
            new Dictionary<string, string> { ["Brad Pitt"] = "Brad Pitt" },
            new Dictionary<string, string> { ["alias"] = "Unknown" },
            new Dictionary<string, string> { ["alias"] = "Brad Pitt", ["ALIAS"] = "Brad Pitt" }
        }) Assert.ThrowsExactly<ArgumentException>(() => new ActorNameResolver(["Brad Pitt"], invalid));
    }

    [TestMethod]
    public void RejectsCorruptCanonicalRegistryStrings()
    {
        foreach (var invalid in new[] { "", " ", " Brad Pitt", "Pene\u0301lope Cruz", "Brad\nPitt" })
            Assert.ThrowsExactly<ArgumentException>(() => new ActorNameResolver([invalid]));
    }
}
