using CineKros.Catalog.Importer;

namespace CineKros.Api.Search;

public sealed record SearchDatasetExpectation(string DatabaseName, int MovieCount, string CatalogSha256, string CatalogIdentity,
    string ProfileFingerprint, string EnCorpusSha256, string SrCorpusSha256,
    string EnArtifactSha256, string SrArtifactSha256, string DictionarySha256)
{
    internal const string FullProductionDatabase = "cinekros";
    internal const string FullProductionCatalogVersion = "B05a-bilingual-full-catalog-v1";
    internal const string FullProductionCatalogSha256 = "6fd22ec79d2c2b1e36b009f899b126c03a1876ce15a2871bb95033cfff410a4c";
    internal const string FullProductionCatalogIdentitySha256 = "cfe69cc246e439f73c3354f77902896988df878af20bfdb8552cfd1fdf1be17e";
    internal const string FullProductionSelectionIdSetSha256 = "af7b28f5c43cc219a9d5f62bd22716015a340f005ee9098f11f4301e13d7bac2";
    internal const string FullProductionEnCorpusSha256 = "fa07202a8181024b3945b63088e7ab4daa48428b238d88ee269f884d8768cec1";
    internal const string FullProductionSrCorpusSha256 = "b7998f5f681c61b0f938e6c0fd2d8d619922baaaefc07ed122c7a64860321122";
    internal const string FullProductionEnArtifactSha256 = "3dde59fa8bbf82452e118fd7f65f9c1e3fd8e630b9bad05a0b123e1e8468053e";
    internal const string FullProductionSrArtifactSha256 = "ff5db33c1c6871a8bd04275a5e6a5167232de9d40e98f3802425c0d2efa0c204";
    internal const string FullProductionDictionarySha256 = "917ba3ea759b6d6595c78bf1ebcd3ddc547f00914156f3ab2fb5064fc549a715";
    internal const string FullProductionProfileFingerprint = "eac906ed78f7863573d13c9b0435de1b8f848fe92fc6ae08aa3621400260b1fe";

    internal const string ReleasedCatalogIdentitySha256 = "efd13b367a6ad0bef10ec4f32e89d1b57180a2d95546423175a4d1c1dc3bc230";
    internal const string CorrectedV2CatalogSha256 = "2887a937689294b029dd918911dd10151bfd3ece74fa573fcc5d85dc2f86886e";
    internal const string CorrectedV2CatalogIdentitySha256 = "0fd18234a4ee0c4f8e6054a9143935ef5dfc3669494e4b6f1637b6be105f8d66";
    internal const string CorrectedV2Database = "cinekros_sr_poc_phase06t_v2_20261009";

    public string CatalogVersion { get; internal init; } = "B05a-bilingual-catalog-v2";
    public string SelectionIdSetSha256 { get; internal init; } = MultilingualPocCatalog.SelectionIdSetSha256;

    public static SearchDatasetExpectation FullBilingualProduction(string actualDatabase)
    {
        if (actualDatabase != FullProductionDatabase)
            throw new InvalidOperationException("Full bilingual production search requires the exact cinekros database.");
        return new(actualDatabase, 9730, FullProductionCatalogSha256, FullProductionCatalogIdentitySha256,
            FullProductionProfileFingerprint, FullProductionEnCorpusSha256, FullProductionSrCorpusSha256,
            FullProductionEnArtifactSha256, FullProductionSrArtifactSha256, FullProductionDictionarySha256)
        {
            CatalogVersion = FullProductionCatalogVersion,
            SelectionIdSetSha256 = FullProductionSelectionIdSetSha256
        };
    }

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
            MultilingualPocCatalog.DictionarySha256)
        {
            CatalogVersion = "B05a-bilingual-catalog-v2",
            SelectionIdSetSha256 = MultilingualPocCatalog.SelectionIdSetSha256
        };
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
            "09bd0afbde2b7b8a0afee502d7718f8dad6cc6320c226b437074ebb4b341ea4e")
        {
            CatalogVersion = "B05a-bilingual-catalog-v2",
            SelectionIdSetSha256 = MultilingualPocCatalog.SelectionIdSetSha256
        };
    }
}
