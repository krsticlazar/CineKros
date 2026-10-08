using CineKros.Catalog.Importer;

namespace CineKros.Api.Search;

public sealed record SearchDatasetExpectation(string DatabaseName, int MovieCount, string CatalogIdentity,
    string ProfileFingerprint, string EnCorpusSha256, string SrCorpusSha256,
    string EnArtifactSha256, string SrArtifactSha256, string DictionarySha256)
{
    internal const string ReleasedCatalogIdentitySha256 = "efd13b367a6ad0bef10ec4f32e89d1b57180a2d95546423175a4d1c1dc3bc230";

    internal static SearchDatasetExpectation SerbianPhase4Poc(string actualDatabase)
    {
        if (actualDatabase != "cinekros_sr_poc_phase04_20261008" &&
            !System.Text.RegularExpressions.Regex.IsMatch(actualDatabase, "^cinekros_sr_poc_phase04_test_[a-z0-9_]+$"))
            throw new InvalidOperationException("Database is outside the released isolated Serbian POC family.");
        return new(actualDatabase, MultilingualPocCatalog.ExpectedCount,
            ReleasedCatalogIdentitySha256,
            MultilingualPocCatalog.ProfileFingerprint, MultilingualPocCatalog.EnCorpusSha256,
            MultilingualPocCatalog.SrCorpusSha256,
            "5ccad923ad7156bc10ee27d0b5b56163d0af05abf52e9bbd0b4df6cd715e6a81",
            "c5ab94401eb3d5bee70bb63acb163d654b658ea5dc9190bd6bbd9b9f030ed915",
            MultilingualPocCatalog.DictionarySha256);
    }
}
