using CineKros.Catalog.Importer;

namespace CineKros.E5.Generator;

// Read-only adapter: old catalog types retain their own frozen version/format properties.
internal sealed record FullCatalogContext(string CatalogPath, string CatalogVersion, string CatalogSha256, string SourceCatalogSha256,
    string SourceContentFingerprint, string DictionarySha256, string SelectionIdSetSha256, string EnCorpusSha256, string SrCorpusSha256,
    string IdentitySha256, string EnTextFormatVersion, string SrTextFormatVersion, IReadOnlyList<ActorMovieV6> Movies, ActorBindingV6? ActorContext = null)
{
    internal static FullCatalogContext From(FullBilingualCatalogDocument c) => new(c.CatalogPath, c.CatalogVersion, c.CatalogSha256,
        c.SourceCatalogSha256, c.SourceContentFingerprint, c.DictionarySha256, c.SelectionIdSetSha256, c.EnCorpusSha256, c.SrCorpusSha256,
        c.IdentitySha256, c.EnTextFormatVersion, c.SrTextFormatVersion, c.Movies.Select(x => new ActorMovieV6(x.MovieLensId, x.SemanticText, x.SemanticTextSr)).ToArray());
    internal static FullCatalogContext From(ActorCatalogV6Document c) => new(c.CatalogPath, c.CatalogVersion, c.CatalogSha256,
        c.SourceCatalogSha256, c.SourceContentFingerprint, c.DictionarySha256, c.SelectionIdSetSha256, c.EnCorpusSha256, c.SrCorpusSha256,
        c.IdentitySha256, c.EnTextFormatVersion, c.SrTextFormatVersion, c.Movies,
        new("actor-catalog-manifest-v1", c.ActorVocabularySha256, c.OrderedCastMappingSha256, c.OverridesSha256, c.CastRulesSha256, c.DictionarySha256));
}
internal sealed record ActorBindingV6(string CatalogSchemaVersion, string ActorVocabularySha256, string OrderedCastMappingSha256,
    string OverridesSha256, string CastRulesSha256, string DictionarySha256);
