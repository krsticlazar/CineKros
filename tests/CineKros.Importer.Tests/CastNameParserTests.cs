using CineKros.Catalog.Importer;

namespace CineKros.Importer.Tests;

[TestClass]
public sealed class CastNameParserTests
{
    [TestMethod]
    public void PreservesNullUnicodeSourceOrderAndExactDistinctNames()
    {
        Assert.IsNull(CastNameParser.Parse(1, null).Cast);
        var result = CastNameParser.Parse(1, "  Cécile de France, O'Neil-Smith, Jose\u0301, José, Ian McDiarmid, Ian Mcdiarmid ");
        CollectionAssert.AreEqual(new[] { "Cécile de France", "O'Neil-Smith", "José", "Ian McDiarmid", "Ian Mcdiarmid" }, result.Cast);
        Assert.AreEqual(1, result.Events.Count(x => x.Kind == "exact-duplicate-suppression"));
        var trailing = CastNameParser.Parse(31, "First Actor, Second Actor, ");
        CollectionAssert.AreEqual(new[] { "First Actor", "Second Actor" }, trailing.Cast);
        Assert.AreEqual("trailing-empty-separator", trailing.Events.Single().Kind);
        Assert.AreEqual("First Actor, Second Actor, ", trailing.Events.Single().Raw);
    }

    [TestMethod]
    public void AttachesOnlyReviewedSuffixGrammar()
    {
        foreach (var (id, name) in new[] { (3182L, "Fred A. Leuchter"), (26414L, "Desi Arnaz"), (1L, "Robert Downey") })
        {
            var result = CastNameParser.Parse(id, name + ", Jr., Other Person");
            CollectionAssert.AreEqual(new[] { name + ", Jr.", "Other Person" }, result.Cast);
            Assert.AreEqual("suffix-attachment", result.Events.Single().Kind);
        }
    }

    [TestMethod]
    public void EntityOverrideIsExactAndContextBound()
    {
        var result = CastNameParser.Parse(85179, "Ayumu Sait&#333;");
        CollectionAssert.AreEqual(new[] { "Ayumu Saitō" }, result.Cast);
        Assert.AreEqual("Ayumu Sait&#333;", result.Events.Single().Raw);
        Assert.ThrowsExactly<InvalidDataException>(() => CastNameParser.Parse(1, "Ayumu Sait&#333;"));
        Assert.ThrowsExactly<InvalidDataException>(() => CastNameParser.Parse(85179, "Someone &amp; Other"));
        var boundary = CastNameParser.Parse(3396, "Before Actor, Caroll Spinney,,Bob Baker, After Actor");
        CollectionAssert.AreEqual(new[] { "Before Actor", "Caroll Spinney", "Bob Baker", "After Actor" }, boundary.Cast);
        Assert.AreEqual("reviewed-empty-separator-override", boundary.Events.Single().Kind);
        Assert.AreEqual(" Caroll Spinney,,Bob Baker", boundary.Events.Single().Raw);
    }

    [TestMethod]
    public void NonNullMalformedCastNeverBecomesUnknown()
    {
        foreach (var raw in new[] { "", " ", ",Name", "Name,,", "Name, , ", "Name,,Other", "Jr., Name", "Name, Jr., Jr.", "Name; Other", "Name, Sr.", "Name\nOther" })
            Assert.ThrowsExactly<InvalidDataException>(() => CastNameParser.Parse(1, raw), raw);
        foreach (var raw in new[] { ",Caroll Spinney,Bob Baker", "Caroll Spinney,,,Bob Baker", "Different,,Bob Baker", "Caroll Spinney,,Different", "Caroll Spinney,,Bob Baker,Caroll Spinney,,Bob Baker" })
            Assert.ThrowsExactly<InvalidDataException>(() => CastNameParser.Parse(3396, raw), raw);
        Assert.ThrowsExactly<InvalidDataException>(() => CastNameParser.Parse(1, "Caroll Spinney,,Bob Baker"));
    }
}
