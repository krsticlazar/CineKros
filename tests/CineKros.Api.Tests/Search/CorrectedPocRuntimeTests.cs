using CineKros.Api.RealProviders;
using CineKros.Api.Search;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Npgsql;

namespace CineKros.Api.Tests.Search;

[TestClass]
public sealed class CorrectedPocRuntimeTests
{
    private const string CorrectedDatabase = "cinekros_sr_poc_phase06t_v2_20261009";

    [TestMethod]
    public async Task CorrectedV2ReadinessAndHardOnlyQueryUsePinnedDatasetReadOnly()
    {
        var connectionString = Environment.GetEnvironmentVariable("CINEKROS_SR_V2_POC_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
            Assert.Inconclusive("Set CINEKROS_SR_V2_POC_CONNECTION to the corrected v2 database connection for this read-only integration check.");

        var connection = new NpgsqlConnectionStringBuilder(connectionString!);
        Assert.AreEqual(CorrectedDatabase, connection.Database,
            "The integration check must never connect to a different database.");
        connection.Options = "-c default_transaction_read_only=on";

        await using var dataSource = MovieSearchRepository.CreateDataSource(connection.ConnectionString);
        var repository = await MovieSearchRepository.CreateCorrectedPhase6TV2PocRepositoryAsync(dataSource);
        await repository.EnsureSelectedLanguageReadyAsync(SearchLanguage.English);
        await repository.EnsureSelectedLanguageReadyAsync(SearchLanguage.Serbian);

        var results = await repository.SearchHardOnlyAsync(new RealHardFilters(
            YearMin: 1990,
            YearMax: 2000,
            Genres: new RealGenreFilter(Any: ["Comedy"])));

        Assert.IsTrue(results.Count <= MovieSearchRepository.ResultLimit);
        CollectionAssert.AreEquivalent(new long[] { 3751, 3773 }, results.Select(movie => movie.MovieLensId).ToArray(),
            "Only the two v2 catalog movies matching Comedy and inclusive years 1990–2000 should be returned.");
        Assert.IsTrue(results.All(movie => movie.Year is >= 1990 and <= 2000),
            "No result may escape the requested year interval.");
    }
}
