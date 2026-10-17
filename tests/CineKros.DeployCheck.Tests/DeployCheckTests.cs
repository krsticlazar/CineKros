using CineKros.DeployCheck;
using CineKros.Embedding;
using CineKros.TextNormalization;

namespace CineKros.DeployCheck.Tests;

[TestClass]
public sealed class DeployCheckTests
{
    [TestMethod]
    public void QuerySetHasTenIntentsInEachRequiredLanguageAndScript()
    {
        Assert.AreEqual(30, QuerySet.Rows.Count);
        Assert.AreEqual(10, QuerySet.Rows.Count(x => x.Language == "en" && x.Script == "Latn"));
        Assert.AreEqual(10, QuerySet.Rows.Count(x => x.Language == "sr" && x.Script == "Latn"));
        Assert.AreEqual(10, QuerySet.Rows.Count(x => x.Language == "sr" && x.Script == "Cyrl"));
        Assert.AreEqual(30, QuerySet.Rows.Select(x => x.Id).Distinct().Count());
    }

    [TestMethod]
    public void SerbianLatinNormalizerProducesStableLatinForBothScripts()
    {
        Assert.AreEqual("Živopisna animirana priča za sve uzraste", SerbianLatinNormalizer.Normalize("Живописна анимирана прича за све узрасте"));
        Assert.AreEqual("Nežna romansa vedrog raspoloženja", SerbianLatinNormalizer.Normalize("  Nežna   romansa\tvedrog raspoloženja "));
        Assert.AreEqual("DŽAK", SerbianLatinNormalizer.Normalize("ЏАК"));
    }

    [TestMethod]
    public void VectorMetricsAndPercentileUseDefinedValues()
    {
        var a = new[] { 3f, 4f }; var b = new[] { 6f, 8f };
        Assert.AreEqual(5d, Program.Norm(a), 1e-10);
        Assert.AreEqual(1d, Program.Cosine(a, b), 1e-10);
        Assert.AreEqual(5d, Program.L2Error(a, b), 1e-10);
        Assert.AreEqual(3d, Program.Percentile([1, 2, 3, 4, 5], .50));
        Assert.AreEqual(5d, Program.Percentile([1, 2, 3, 4, 5], .95));
    }

    [TestMethod]
    public void ReferenceRequiresKnownIdentityTokenAndVectorShapeAndFiniteValues()
    {
        var valid = ValidReference();
        Program.ValidateReference(valid);
        Assert.ThrowsExactly<InvalidDataException>(() => Program.ValidateReference(valid with { Metadata = null! }));
        Assert.ThrowsExactly<InvalidDataException>(() => Program.ValidateReference(valid with { Metadata = valid.Metadata with { Artifacts = null! } }));
        Assert.ThrowsExactly<InvalidDataException>(() => Program.ValidateReference(valid with { Metadata = valid.Metadata with { Profile = "unknown" } }));
        Assert.ThrowsExactly<InvalidDataException>(() => Program.ValidateReference(valid with { Metadata = valid.Metadata with { SchemaVersion = "unknown" } }));
        Assert.ThrowsExactly<InvalidDataException>(() => Program.ValidateReference(valid with { Queries = valid.Queries.Skip(1).ToList() }));
        var badTokens = valid.Queries.ToList(); badTokens[0] = badTokens[0] with { AttentionMask = [1, 0] };
        Assert.ThrowsExactly<InvalidDataException>(() => Program.ValidateReference(valid with { Queries = badTokens }));
        var badTokenIds = valid.Queries.ToList(); badTokenIds[0] = badTokenIds[0] with { TokenIds = [-1, 2] };
        Assert.ThrowsExactly<InvalidDataException>(() => Program.ValidateReference(valid with { Queries = badTokenIds }));
        var nullRow = valid.Queries.ToList(); nullRow[0] = null!;
        Assert.ThrowsExactly<InvalidDataException>(() => Program.ValidateReference(valid with { Queries = nullRow }));
        var badDimension = valid.Queries.ToList(); badDimension[0] = badDimension[0] with { Vector = [1f] };
        Assert.ThrowsExactly<InvalidDataException>(() => Program.ValidateReference(valid with { Queries = badDimension }));
        var nonfinite = valid.Queries.ToList(); nonfinite[0] = nonfinite[0] with { Vector = [float.NaN, .. Enumerable.Repeat(0f, 767)] };
        Assert.ThrowsExactly<InvalidDataException>(() => Program.ValidateReference(valid with { Queries = nonfinite }));
        var zeroVector = valid.Queries.ToList(); zeroVector[0] = zeroVector[0] with { Vector = new float[768] };
        Assert.ThrowsExactly<InvalidDataException>(() => Program.ValidateReference(valid with { Queries = zeroVector }));
    }

    [TestMethod]
    public void CompareReportsDetectsTokenAndProfileMismatchAndRetainsCurrentReport()
    {
        var reference = ValidReference();
        var currentRows = reference.Queries.ToList();
        currentRows[0] = currentRows[0] with { TokenIds = [0, 3] };
        var current = reference with { Queries = currentRows };
        var report = Program.CompareReports(reference, current, new string('a', 64));
        Assert.IsFalse(report.StructuralChecksPassed);
        Assert.IsTrue(report.Mismatches.Any(x => x.Contains("token IDs or mask", StringComparison.Ordinal)));
        Assert.AreSame(current, report.CurrentReport);
        Assert.AreEqual(new string('a', 64), report.ReferenceArtifactSha256);
        Assert.AreEqual("PENDING ACCEPTANCE", report.NumericalAcceptance);

        var changedProfile = reference with { Metadata = reference.Metadata with { Profile = "other-profile" } };
        var profileReport = Program.CompareReports(changedProfile, reference, new string('b', 64));
        Assert.IsFalse(profileReport.StructuralChecksPassed);
        Assert.IsTrue(profileReport.Mismatches.Any(x => x.Contains("model/profile/artifact", StringComparison.Ordinal)));
        Assert.AreEqual("PENDING ACCEPTANCE", profileReport.NumericalAcceptance);
    }

    [TestMethod]
    public void CompareReportsComputesCosineAndL2WithoutNumericalPassClaim()
    {
        var reference = ValidReference();
        var rows = reference.Queries.ToList();
        rows[0] = rows[0] with { Vector = [-1f, .. Enumerable.Repeat(0f, 767)], VectorNorm = 1 };
        var report = Program.CompareReports(reference, reference with { Queries = rows }, new string('c', 64));
        Assert.IsTrue(report.StructuralChecksPassed);
        Assert.AreEqual("PENDING ACCEPTANCE", report.NumericalAcceptance);
        Assert.AreEqual(-1d, report.Queries[0].Cosine!.Value, 1e-10);
        Assert.AreEqual(2d, report.Queries[0].L2Error!.Value, 1e-10);
        Assert.AreEqual(-1d, report.MinimumCosine!.Value, 1e-10);
        Assert.AreEqual(28d / 30d, report.MeanCosine!.Value, 1e-10);
    }

    private static ExportReport ValidReference()
    {
        var profile = EmbeddingProfileDescriptor.MultilingualE5Base;
        var metadata = new ExportMetadata("CineKros.DeployCheck/v1", profile.ProfileVersion, profile.ModelId, profile.Revision,
            profile.ProfileFingerprint, profile.Artifacts.Select(x => new ArtifactRecord(x.FileName, x.Sha256)).ToArray());
        var rows = QuerySet.Rows.Select(q => new QueryRecord(q.Id, q.Language, q.Script, q.Original, q.Normalized, [0, 2], [1, 1],
            [1f, .. Enumerable.Repeat(0f, 767)], 1)).ToList();
        return new ExportReport(metadata, new { }, "nearest-rank: ceil(p*n), one-based", 1, 1, 1, 1, 1, Enumerable.Repeat(1d, 30).ToArray(), 1, 1, rows);
    }
}
