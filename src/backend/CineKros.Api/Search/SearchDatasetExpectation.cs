using CineKros.Catalog.Importer;

namespace CineKros.Api.Search;

public sealed record SearchDatasetExpectation(string DatabaseName, int MovieCount, string CatalogSha256, string CatalogIdentity,
    string ProfileFingerprint, string EnCorpusSha256, string SrCorpusSha256,
    string EnArtifactSha256, string SrArtifactSha256, string DictionarySha256)
{
    internal const string ReleasedCatalogIdentitySha256 = "efd13b367a6ad0bef10ec4f32e89d1b57180a2d95546423175a4d1c1dc3bc230";
    internal const string CorrectedV2CatalogSha256 = "2887a937689294b029dd918911dd10151bfd3ece74fa573fcc5d85dc2f86886e";
    internal const string CorrectedV2CatalogIdentitySha256 = "0fd18234a4ee0c4f8e6054a9143935ef5dfc3669494e4b6f1637b6be105f8d66";
    internal const string CorrectedV2Database = "cinekros_sr_poc_phase06t_v2_20261009";

    public static SearchDatasetExpectation SerbianPhase4Poc(string actualDatabase)
    {
        if (actualDatabase != "cinekros_sr_poc_phase04_20261008" &&
            !System.Text.RegularExpressions.Regex.IsMatch(actualDatabase, "^cinekros_sr_poc_phase04_test_[a-z0-9_]+$"))
            throw new InvalidOperationException("Database is outside the released isolated Serbian POC family.");
        return new(actualDatabase, MultilingualPocCatalog.ExpectedCount, MultilingualPocCatalog.CatalogSha256,
            ReleasedCatalogIdentitySha256,
            MultilingualPocCatalog.ProfileFingerprint, MultilingualPocCatalog.EnCorpusSha256,
            MultilingualPocCatalog.SrCorpusSha256,
            "5ccad923ad7156bc10ee27d0b5b56163d0af05abf52e9bbd0b4df6cd715e6a81",
            "c5ab94401eb3d5bee70bb63acb163d654b658ea5dc9190bd6bbd9b9f030ed915",
            MultilingualPocCatalog.DictionarySha256);
    }

    public static SearchDatasetExpectation SerbianPhase6TV2Poc(string actualDatabase)
    {
        if (actualDatabase != CorrectedV2Database)
            throw new InvalidOperationException("Database is outside the exact corrected Serbian POC v2 target.");
        return new(actualDatabase, 150, CorrectedV2CatalogSha256, CorrectedV2CatalogIdentitySha256,
            "eac906ed78f7863573d13c9b0435de1b8f848fe92fc6ae08aa3621400260b1fe",
            "5c5760576471547ea4d6db17acfd254ff1a0d4e7c47fe54b0f4db6a9795fdde0",
            "fc26b53581e6adc795573df511b7e7f72c7fd59fe7e31efddb29e0ebf9d7e8c0",
            "5ccad923ad7156bc10ee27d0b5b56163d0af05abf52e9bbd0b4df6cd715e6a81",
            "228493dfeabf83dd5b3d5b0b9cf6c445ebc48742e9cf8f810bc8517959d7baba",
            "09bd0afbde2b7b8a0afee502d7718f8dad6cc6320c226b437074ebb4b341ea4e");
    }
}
