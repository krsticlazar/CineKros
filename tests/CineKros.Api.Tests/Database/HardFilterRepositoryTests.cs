using CineKros.Api.Database;
using CineKros.Api.RealProviders;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Npgsql;

namespace CineKros.Api.Tests.Database;

[TestClass]
public sealed class HardFilterRepositoryTests
{
    [TestMethod]
    public void BuilderUsesOnlyFixedClausesAndBindsTypedValues()
    {
        var query = HardFilterSqlBuilder.Build(new RealHardFilters(2000, 2020, 80, 120, new RealGenreFilter(["Sci-Fi"], ["Drama", "Thriller"]), 0m, "ja"));
        Assert.AreEqual("WHERE year >= @yearMin AND year <= @yearMax AND runtime_minutes >= @runtimeMin AND runtime_minutes <= @runtimeMax AND genres @> @genresAll::text[] AND genres && @genresAny::text[] AND average_rating >= @ratingMin AND original_language = @originalLanguage", query.WhereSql);

        using var command = new NpgsqlCommand();
        query.AddParameters(command);
        Assert.AreEqual(8, command.Parameters.Count);
        Assert.AreEqual(0m, command.Parameters["ratingMin"].Value);
        Assert.AreEqual("ja", command.Parameters["originalLanguage"].Value);
        CollectionAssert.AreEqual(new[] { "Drama", "Thriller" }, (string[])command.Parameters["genresAny"].Value!);

        var ratingQuery = HardFilterSqlBuilder.Build(new RealHardFilters(RatingMin: 4.0m));
        Assert.AreEqual("WHERE average_rating >= @ratingMin", ratingQuery.WhereSql);
        using var ratingCommand = new NpgsqlCommand();
        ratingQuery.AddParameters(ratingCommand);
        Assert.AreEqual(1, ratingCommand.Parameters.Count);
        Assert.AreEqual(4m, ratingCommand.Parameters["ratingMin"].Value);
        Assert.AreEqual("WHERE average_rating > @ratingMin", HardFilterSqlBuilder.Build(new RealHardFilters(RatingMin: 4m, RatingOperator: "gt")).WhereSql);
        Assert.ThrowsExactly<RealProviderException>(() => HardFilterSqlBuilder.Build(new RealHardFilters(RatingMin: 4m, RatingOperator: "DROP TABLE movies")));
        Assert.ThrowsExactly<RealProviderException>(() => HardFilterSqlBuilder.Build(new RealHardFilters(RatingOperator: "gt")));

        var attack = "ja' OR 1=1 --";
        Assert.ThrowsExactly<RealProviderException>(() => HardFilterSqlBuilder.Build(new RealHardFilters(OriginalLanguage: attack)));
        var maliciousGenre = new RealHardFilters(Genres: new RealGenreFilter(["Drama']; DROP TABLE movies; --"]));
        Assert.ThrowsExactly<RealProviderException>(() => HardFilterSqlBuilder.Build(maliciousGenre));
    }

    [TestMethod]
    public void BuilderRejectsMalformedValuesBeforeSqlConstruction()
    {
        var invalidFilters = new RealHardFilters[]
        {
            new(YearMin: 999), new(YearMax: 10000), new(RuntimeMin: 0), new(RuntimeMax: -1),
            new(YearMin: 2021, YearMax: 2020), new(RuntimeMin: 121, RuntimeMax: 120),
            new(RatingMin: -0.1m), new(RatingMin: 5.01m), new(OriginalLanguage: "cn"), new(OriginalLanguage: "Japanese"),
            new(Genres: new RealGenreFilter()), new(Genres: new RealGenreFilter(All: [])),
            new(Genres: new RealGenreFilter(Any: ["Drama", "Drama"])), new(Genres: new RealGenreFilter(All: ["(no genres listed)"]))
        };

        foreach (var filters in invalidFilters)
            Assert.ThrowsExactly<RealProviderException>(() => HardFilterSqlBuilder.Build(filters));
    }

    [TestMethod]
    public async Task PostgreSqlAppliesInclusiveAndConjoinedFiltersAndNullFails()
    {
        var connectionString = Environment.GetEnvironmentVariable("C06_TEST_DATABASE");
        if (string.IsNullOrWhiteSpace(connectionString)) Assert.Inconclusive("Set C06_TEST_DATABASE to the worker-owned disposable migrated PostgreSQL database.");

        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using (var connection = await dataSource.OpenConnectionAsync())
        {
            await using var setup = new NpgsqlCommand("TRUNCATE TABLE movie_embeddings, movies CASCADE; INSERT INTO movies (movie_lens_id, imdb_id, title, year, runtime_minutes, original_language, genres, average_rating, rating_count) VALUES " +
                "(1,'tt0000001','Target',2000,100,'en',ARRAY['Drama','Sci-Fi'],0,0)," +
                "(2,'tt0000002','YearLow',1999,100,'en',ARRAY['Drama'],3.5,10)," +
                "(3,'tt0000003','YearHigh',2001,100,'en',ARRAY['Drama'],4,10)," +
                "(4,'tt0000004','RuntimeLow',2000,99,'en',ARRAY['Drama'],4,10)," +
                "(5,'tt0000005','RuntimeHigh',2000,101,'en',ARRAY['Thriller'],4,10)," +
                "(6,'tt0000006','Combo',2000,100,'ja',ARRAY['Drama','Thriller'],4,10)," +
                "(7,'tt0000007','RatingNull',2000,100,'en',ARRAY['Drama'],NULL,NULL)," +
                "(8,'tt0000008','RuntimeNull',2000,NULL,'en',ARRAY['Drama'],4,10)," +
                "(9,'tt0000009','GenreNull',2000,100,'en',NULL,4,10)," +
                "(10,'tt0000010','LanguageNull',2000,100,NULL,ARRAY['Drama'],4,10)," +
                "(11,'tt0000011','RawCn',2000,100,'cn',ARRAY['Drama'],4,10)," +
                "(12,'tt0000012','RawSh',2000,100,'sh',ARRAY['Drama'],4,10)," +
                "(13,'tt0000013','RatingAbove',2000,100,'en',ARRAY['Drama'],4.5,10)", connection);
            await setup.ExecuteNonQueryAsync();
        }

        var repository = new FilteredMovieRepository(dataSource);
        async Task<long[]> Ids(RealHardFilters filters) => (await repository.ReadAsync(filters)).Select(movie => movie.MovieLensId).Order().ToArray();

        CollectionAssert.AreEqual(Enumerable.Range(1, 13).Select(i => (long)i).ToArray(), await Ids(new RealHardFilters()));
        CollectionAssert.AreEqual(new long[] { 1, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 }, await Ids(new RealHardFilters(YearMin: 2000, YearMax: 2000)));
        CollectionAssert.AreEqual(new long[] { 1, 2, 3, 5, 6, 7, 9, 10, 11, 12, 13 }, await Ids(new RealHardFilters(RuntimeMin: 100)));
        CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4, 6, 7, 9, 10, 11, 12, 13 }, await Ids(new RealHardFilters(RuntimeMax: 100)));
        CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4, 5, 6, 8, 9, 10, 11, 12, 13 }, await Ids(new RealHardFilters(RatingMin: 0)));
        CollectionAssert.AreEqual(new long[] { 3, 4, 5, 6, 8, 9, 10, 11, 12, 13 }, await Ids(new RealHardFilters(RatingMin: 4.0m)));
        CollectionAssert.AreEqual(new long[] { 3, 4, 5, 6, 8, 9, 10, 11, 12, 13 }, await Ids(new RealHardFilters(RatingMin: 4.0m, RatingOperator: "gte")));
        CollectionAssert.AreEqual(new long[] { 13 }, await Ids(new RealHardFilters(RatingMin: 4.0m, RatingOperator: "gt")));
        CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4, 6, 7, 8, 10, 11, 12, 13 }, await Ids(new RealHardFilters(Genres: new RealGenreFilter(All: ["Drama"]))));
        CollectionAssert.AreEqual(new long[] { 5, 6 }, await Ids(new RealHardFilters(Genres: new RealGenreFilter(Any: ["Thriller"]))));
        CollectionAssert.AreEqual(new long[] { 6 }, await Ids(new RealHardFilters(Genres: new RealGenreFilter(All: ["Drama"], Any: ["Thriller"]))));
        CollectionAssert.AreEqual(new long[] { 6 }, await Ids(new RealHardFilters(YearMin: 2000, YearMax: 2000, RuntimeMin: 100, RuntimeMax: 100, Genres: new RealGenreFilter(All: ["Drama"], Any: ["Thriller"]), RatingMin: 4m, OriginalLanguage: "ja")));
        CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4, 5, 7, 8, 9, 13 }, await Ids(new RealHardFilters(OriginalLanguage: "en")));
        CollectionAssert.AreEqual(Array.Empty<long>(), await Ids(new RealHardFilters(OriginalLanguage: "zh")));
        Assert.ThrowsExactly<RealProviderException>(() => HardFilterSqlBuilder.Build(new RealHardFilters(OriginalLanguage: "cn")));
        Assert.ThrowsExactly<RealProviderException>(() => HardFilterSqlBuilder.Build(new RealHardFilters(OriginalLanguage: "sh")));
    }
}
