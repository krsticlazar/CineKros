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
    public bool UsesFullLfIdEncoding { get; internal init; }
    public string EnTextFormatVersion { get; internal init; } = "en-title-year-director-cast-tags-v1";
    public string SrTextFormatVersion { get; internal init; } = "sr-title-year-director-cast-tags-latn-v1";
    public V6SearchDatasetExpectation? Actors { get; internal init; }

    public static SearchDatasetExpectation FullBilingualProduction(string actualDatabase)
    {
        if (actualDatabase != FullProductionDatabase)
            throw new InvalidOperationException("Full bilingual production search requires the exact cinekros database.");
        return new(actualDatabase, 9730, FullProductionCatalogSha256, FullProductionCatalogIdentitySha256,
            FullProductionProfileFingerprint, FullProductionEnCorpusSha256, FullProductionSrCorpusSha256,
            FullProductionEnArtifactSha256, FullProductionSrArtifactSha256, FullProductionDictionarySha256)
        {
            CatalogVersion = FullProductionCatalogVersion,
            UsesFullLfIdEncoding = true,
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

/// <summary>Exact isolated V6 release; never selected implicitly from a database name.</summary>
public sealed record V6SearchDatasetExpectation
{
    public const string Database = "cinekros_v6_rehearsal_20261015t2154z_4fd266aa";
    public const string VocabularySha256 = "7427b92ae216dfc59f53d46e0a936971fab93015ce5807e3991e59b01e960de3";
    public const string MappingSha256 = "7de357a36207770d772ea351fedbeea7146b4dc6e13a41d3a518189523b2c0c8";
    public const string OverridesSha256 = "3078b4c0ddbe59d6e6f5087d0ea7254d764e36a6f905c1c91ce699c3f38ba33d";
    public const string RulesSha256 = "9c34722165a8e6d15a950c95bccf0eed27d1bffc85ee71c346c73cabddd4ec0d";
    public const string PairSha256 = "68011ca794b2adfb1ddc5058aa940d40b82cd8fb7ae64a7da40f8ea583da3bc5";
    public const string BaselineSha256 = "555a7aadf44ea88001bc903dab60a2d96c470b236e3b4b5d273733452d2fa0df";
    public const string SchemaSha256 = "1975494c9655a14e08731ecd583673a8fbeb76c46f16078d0e4612fab61d6407";
    public const string Migration003Sha256 = "66eb1ea4a8130e49db24a9ccee29bad13aab7ed51cd385279f7188d00def1daa";

    public static SearchDatasetExpectation Isolated(string actualDatabase)
    {
        if (actualDatabase != Database) throw new InvalidOperationException("V6 search requires the exact isolated rehearsal database.");
        return new(actualDatabase, 9730,
            "30b9b7ee9ef0f94cb1427e4b1921a296ba3692c621ac869c17e8c45dff3a7925",
            "03e452c345576b5f369834e89016d43902259aa1903697b8b92123e4ea66f5d3",
            SearchDatasetExpectation.FullProductionProfileFingerprint,
            "38355e10039d885161e3523ee1e5a66411d80d7f35b57992db83d75fe8242c6e",
            "0b11455b7a3159d4fe7e7924d36521907112e03d7381c394c40599a57c4ecad1",
            "4593ce51cc7b981e07dba50cfe950d0daa3d1cc6a74d43a47c1038e341c5ff09",
            "5f62b1351d691204b273b55693ab54e37e04ece57a50df9d2cc8e68306d98275",
            SearchDatasetExpectation.FullProductionDictionarySha256)
        {
            CatalogVersion = ActorCatalogV6.CatalogVersion,
            SelectionIdSetSha256 = ActorCatalogV6.OrderedIdsSha256,
            UsesFullLfIdEncoding = true,
            EnTextFormatVersion = ActorCatalogV6.EnTextFormatVersion,
            SrTextFormatVersion = ActorCatalogV6.SrTextFormatVersion,
            Actors = new()
        };
    }
}
