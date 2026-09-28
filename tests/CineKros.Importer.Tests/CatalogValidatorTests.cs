using CineKros.Catalog.Importer;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CineKros.Importer.Tests;

[TestClass]
public sealed class CatalogValidatorTests
{
    private static readonly string CatalogPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/data/derived/final/b05a-real-tmdb-01/movies-catalog.jsonl"));

    [TestMethod]
    public async Task LoadsReviewedCatalogAndPreservesRawLanguageCodes()
    {
        var catalog = await CatalogValidator.LoadAsync(CatalogPath);
        Assert.AreEqual(9730, catalog.Movies.Count);
        Assert.AreEqual("0114709", catalog.Movies[0].ImdbId);
        Assert.IsTrue(catalog.Movies.Any(x => x.OriginalLanguage == "cn"));
        Assert.IsTrue(catalog.Movies.Any(x => x.OriginalLanguage == "sh"));
        Assert.IsTrue(catalog.Movies.Any(x => x.TmdbId is null && x.Genres is null && x.AverageRating is null));
    }

    [TestMethod]
    public async Task RejectsOneByteCatalogHashMutationBeforeImport()
    {
        var temp = Path.Combine(Path.GetTempPath(), $"cinekros-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            File.Copy(Path.Combine(Path.GetDirectoryName(CatalogPath)!, "manifest.json"), Path.Combine(temp, "manifest.json"));
            await File.WriteAllTextAsync(Path.Combine(temp, "movies-catalog.jsonl"), "{}");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => CatalogValidator.LoadAsync(Path.Combine(temp, "movies-catalog.jsonl")));
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    [TestMethod]
    public void CatalogDocumentCannotBeConstructedOutsideTheValidatorAssembly()
    {
        Assert.AreEqual(0, typeof(CatalogDocument).GetConstructors().Length);
    }
}
