using System.Security.Cryptography;
using System.Text;
using Tokenizers.DotNet;
using CineKros.Catalog.Importer;
using CineKros.Embedding;

namespace CineKros.E5.Generator;

internal static class FullCatalogTokenAudit
{
    internal static TokenAuditResult Measure(string modelDirectory, FullBilingualCatalogDocument catalog)
    {
        var profile = EmbeddingProfileDescriptor.MultilingualE5Base;
        var tokenizerPath = Path.Combine(Path.GetFullPath(modelDirectory), "tokenizer.json");
        using var stream = File.OpenRead(tokenizerPath);
        var actualHash = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (actualHash != profile.Artifact("tokenizer.json").Sha256)
            throw new InvalidDataException("Refusing token audit: tokenizer bytes do not match the locked profile.");

        using var tokenizer = new Tokenizer(vocabPath: tokenizerPath);
        var documents = new List<TokenAuditDocument>(catalog.Movies.Count * 2);
        foreach (var language in new[] { "en", "sr" })
        foreach (var movie in catalog.Movies)
        {
            var text = language == "en" ? movie.SemanticText : movie.SemanticTextSr;
            var formatted = E5EmbeddingModel.FormatDocumentInput(text.Normalize(NormalizationForm.FormC));
            var rawTokenCount = tokenizer.Encode(formatted).Length;
            var truncated = rawTokenCount > E5EmbeddingModel.MaxTokens;
            documents.Add(new TokenAuditDocument(language, movie.MovieLensId, rawTokenCount,
                truncated ? E5EmbeddingModel.MaxTokens : rawTokenCount, truncated));
        }

        return new TokenAuditResult("multilingual-full-catalog-token-audit-v1", catalog.IdentitySha256, actualHash,
            documents.Count, documents.Count(x => x.Language == "en" && x.Truncated), documents.Count(x => x.Language == "sr" && x.Truncated),
            documents.Max(x => x.RawTokenCount), documents.AsReadOnly());
    }
}

internal sealed record TokenAuditResult(string Format, string CatalogIdentitySha256, string TokenizerSha256, int DocumentCount,
    int EnglishTruncatedCount, int SerbianTruncatedCount, int MaximumRawTokenCount, IReadOnlyList<TokenAuditDocument> Documents);

internal sealed record TokenAuditDocument(string Language, long MovieLensId, int RawTokenCount, int InferenceTokenCount, bool Truncated);
