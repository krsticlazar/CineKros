using System.Reflection;
using CineKros.Api.Database;
using CineKros.Api.Diagnostics;
using CineKros.Api.RealFlow;
using CineKros.Api.RealProviders;
using CineKros.Api.Search;
using CineKros.Api.Startup;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace CineKros.Api.Tests.Search;

[TestClass]
public sealed class V6SearchTests
{
    [TestMethod]
    public void ActorSqlConjoinsEveryGroupAndCategoryAndBindsTypedArrays()
    {
        var filters = new RealHardFilters(YearMin: 2000, RuntimeMax: 120, Genres: new RealGenreFilter(Any: ["Drama"]),
            Actors: new RealActorFilter(All: ["Brad Pitt"], Any: ["George Clooney", "Ayumu Saitō"], Exclude: ["George Clooney"]));
        var query = HardFilterSqlBuilder.Build(filters);
        StringAssert.Contains(query.WhereSql, "year >= @yearMin AND runtime_minutes <= @runtimeMax");
        StringAssert.Contains(query.WhereSql, "genres && @genresAny::text[]");
        StringAssert.Contains(query.WhereSql, "\"cast\" IS NOT NULL AND cardinality(\"cast\") > 0 AND \"cast\" @> @actorsAll::text[] AND \"cast\" && @actorsAny::text[] AND NOT (\"cast\" && @actorsExclude::text[])");
        Assert.IsFalse(query.WhereSql.Contains("Brad Pitt", StringComparison.Ordinal));
        using var command = new NpgsqlCommand(); query.AddParameters(command);
        foreach (var name in new[] { "actorsAll", "actorsAny", "actorsExclude" })
            Assert.AreEqual(NpgsqlDbType.Array | NpgsqlDbType.Text, command.Parameters[name].NpgsqlDbType);
        CollectionAssert.AreEqual(new[] { "George Clooney", "Ayumu Saitō" }, (string[])command.Parameters["actorsAny"].Value!);
        Assert.IsFalse(HardFilterSqlBuilder.Build(new RealHardFilters(YearMin: 2000)).WhereSql.Contains("cast", StringComparison.Ordinal));
        var excluded = HardFilterSqlBuilder.Build(new RealHardFilters(Actors: new RealActorFilter(Exclude: ["O'Brien'); DROP TABLE movies; --"])));
        Assert.IsFalse(excluded.WhereSql.Contains("DROP", StringComparison.Ordinal));
    }

    [TestMethod]
    public void MalformedOrContradictoryActorDtoFailsAndExactMembershipIsRequired()
    {
        foreach (var actors in new[] {
            new RealActorFilter(), new RealActorFilter(All: []), new RealActorFilter(Any: [" "]),
            new RealActorFilter(All: ["Brad Pitt", "Brad Pitt"]), new RealActorFilter(All: [null!]),
            new RealActorFilter(All: [" Brad Pitt"]), new RealActorFilter(All: ["Saito\u0304"]),
            new RealActorFilter(All: ["Brad Pitt"], Exclude: ["Brad Pitt"]),
            new RealActorFilter(Any: ["Brad Pitt"], Exclude: ["Brad Pitt"]) })
            Assert.AreEqual("PARSER_INVALID_RESPONSE", Assert.ThrowsExactly<RealProviderException>(() => HardFilterSqlBuilder.Build(new RealHardFilters(Actors: actors))).Code);
        var resolver = new ActorNameResolver(["Brad Pitt", "Fred A. Leuchter, Jr.", "Ayumu Saitō"]);
        foreach (var name in new[] { "Brad Pitt", "Fred A. Leuchter, Jr.", "Ayumu Saitō" })
            HardFilterSqlBuilder.ValidateActorMembership(new RealHardFilters(Actors: new RealActorFilter(All: [name])), resolver);
        foreach (var name in new[] { "brad pitt", "Ayumu Saito", "Jr.", "Unknown" })
            Assert.ThrowsExactly<RealProviderException>(() => HardFilterSqlBuilder.ValidateActorMembership(new RealHardFilters(Actors: new RealActorFilter(Exclude: [name])), resolver));
        Assert.ThrowsExactly<RealProviderException>(() => HardFilterSqlBuilder.ValidateActorMembership(new RealHardFilters(Actors: new RealActorFilter(All: ["Brad Pitt"])), null));
    }

    [TestMethod]
    public async Task LegacyAndLowLevelRepositoryRejectActorInjectionBeforeConnecting()
    {
        await using var ds = MovieSearchRepository.CreateDataSource("Host=127.0.0.1;Port=1;Database=unused;Username=unused;Timeout=1");
        var filters = new RealHardFilters(Actors: new RealActorFilter(All: ["Brad Pitt"]));
        var repo = new MovieSearchRepository(ds, new string('a', 64));
        Assert.AreEqual("PARSER_INVALID_RESPONSE", (await Assert.ThrowsExactlyAsync<RealProviderException>(() => repo.SearchHardOnlyAsync(filters))).Code);
        Assert.AreEqual("PARSER_INVALID_RESPONSE", (await Assert.ThrowsExactlyAsync<RealProviderException>(() => repo.SearchHybridAsync(filters, Unit()))).Code);
        Assert.AreEqual("PARSER_INVALID_RESPONSE", (await Assert.ThrowsExactlyAsync<RealProviderException>(() => new FilteredMovieRepository(ds).ReadAsync(filters))).Code);
    }

    [TestMethod]
    public void IsolatedRuntimeAndReleasePinsAreExplicitAndCannotEnableProductionOrPoc()
    {
        var dev = new Host("Development"); var prod = new Host("Production");
        Assert.AreEqual("v5", RecommendationStartup.ResolveRuntimeProfile(dev, "real", false, null));
        Assert.AreEqual("v5", RecommendationStartup.ResolveRuntimeProfile(prod, "real", false, "v5"));
        Assert.AreEqual("actor-v6-isolated", RecommendationStartup.ResolveRuntimeProfile(dev, "real", false, "actor-v6-isolated"));
        foreach (var input in new[] { (dev, "fake", false, "actor-v6-isolated"), (dev, "real", true, "actor-v6-isolated"), (prod, "real", false, "actor-v6-isolated"), (dev, "real", false, "ActorV6"), (dev, "real", false, "") })
            Assert.ThrowsExactly<InvalidOperationException>(() => RecommendationStartup.ResolveRuntimeProfile(input.Item1, input.Item2, input.Item3, input.Item4));
        var e = V6SearchDatasetExpectation.Isolated(V6SearchDatasetExpectation.Database);
        Assert.AreEqual("CineKros-bilingual-actor-catalog-v6", e.CatalogVersion);
        Assert.AreEqual("en-title-year-director-tags-v6", e.EnTextFormatVersion);
        Assert.AreEqual("sr-title-year-director-tags-latn-v6", e.SrTextFormatVersion);
        Assert.IsTrue(e.UsesFullLfIdEncoding); Assert.IsNotNull(e.Actors);
        Assert.AreNotEqual(SearchDatasetExpectation.FullBilingualProduction("cinekros").CatalogIdentity, e.CatalogIdentity);
        foreach (var db in new[] { "cinekros", "cinekros_v6_test_p4_unreleased", V6SearchDatasetExpectation.Database + "\n", "cinekros_sr_poc_phase06t_v2_20261009" })
            Assert.ThrowsExactly<InvalidOperationException>(() => V6SearchDatasetExpectation.Isolated(db));
    }

    [TestMethod]
    public void SqlDiagnosticsAreExplicitDevelopmentOnlyAndNeverSerializeVectorsOrUnknownParameters()
    {
        Assert.IsFalse(DevelopmentSqlTrace.ResolveEnabled(true, true, null));
        Assert.IsTrue(DevelopmentSqlTrace.ResolveEnabled(true, true, "true"));
        Assert.ThrowsExactly<InvalidOperationException>(() => DevelopmentSqlTrace.ResolveEnabled(false, true, "true"));
        Assert.ThrowsExactly<InvalidOperationException>(() => DevelopmentSqlTrace.ResolveEnabled(true, false, "true"));
        using var command = new NpgsqlCommand("SELECT safe_sql");
        HardFilterSqlBuilder.Build(new RealHardFilters(Actors: new RealActorFilter(All: ["Brad Pitt"]))).AddParameters(command);
        command.Parameters.AddWithValue("queryVector", "NEVER_VECTOR"); command.Parameters.AddWithValue("connectionString", "NEVER_SECRET");
        var trace = DevelopmentSqlTrace.Format(command, SearchLanguage.Serbian);
        StringAssert.Contains(trace, "safe_sql"); StringAssert.Contains(trace, "Brad Pitt"); StringAssert.Contains(trace, "Serbian");
        Assert.IsFalse(trace.Contains("NEVER_", StringComparison.Ordinal)); Assert.IsFalse(trace.Contains("queryVector", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RuntimeSchemaSerializationExactlyMatchesTheAcceptedImporterRecipe()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CineKros.slnx"))) directory = directory.Parent;
        Assert.IsNotNull(directory, "Run this source contract check from the canonical repository build.");
        var source = File.ReadAllText(Path.Combine(directory.FullName, "src", "etl", "CineKros.VectorImporter", "ActorSchemaV6.cs")).Replace("\r\n", "\n");
        var match = System.Text.RegularExpressions.Regex.Match(source, "(?s)SchemaSql = \"\"\"\\n(.*?)\\n        \"\"\";");
        Assert.IsTrue(match.Success);
        var importerSql = string.Join('\n', match.Groups[1].Value.Split('\n').Select(line => line[8..]));
        var runtimeSql = (string)typeof(ActorDatasetReadiness).GetField("SchemaSql", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
        Assert.AreEqual(importerSql, runtimeSql.Replace("\r\n", "\n"), "The cross-boundary schema fingerprint serialization may not drift.");
        StringAssert.Contains(runtimeSql, "pg_get_functiondef"); StringAssert.Contains(runtimeSql, "pg_get_constraintdef");
        StringAssert.Contains(runtimeSql, "x.indisvalid,x.indisready");
    }

    [TestMethod]
    public async Task V6ActorOnlyHasZeroEmbeddingAndSelectedPreflightWhileHybridUsesSelectedLanguage()
    {
        foreach (var semantic in new string?[] { null, "мрачна драма" })
        {
            var search = new FakeSearch(); var embedding = new FakeEmbedding();
            var filters = new RealHardFilters(Actors: new RealActorFilter(All: ["Brad Pitt"]));
            var service = Service(new RealParserResult("query", new(filters, semantic), LanguageCheck: "match"), search, embedding);
            await service.RecommendAsync(new ParserInput("sr", "са Бредом Питом"), CancellationToken.None);
            Assert.AreEqual(semantic is null ? 1 : 0, search.Hard);
            Assert.AreEqual(semantic is null ? 0 : 1, search.Preflight);
            Assert.AreEqual(semantic is null ? 0 : 1, search.Hybrid);
            Assert.AreEqual(semantic is null ? 0 : 1, embedding.Calls);
            if (semantic is not null) { Assert.AreEqual("mračna drama", embedding.Query); Assert.AreEqual(SearchLanguage.Serbian, search.Language); }
        }
    }

    [TestMethod]
    public async Task V6AlertsAndInjectedNoncanonicalActorsStopBeforeSearchAndEmbedding()
    {
        var search = new FakeSearch(); var embedding = new FakeEmbedding();
        var alert = Service(new RealParserResult("alert", AlertCode: "QUERY_UNCLEAR", LanguageCheck: "match"), search, embedding);
        Assert.AreEqual("QUERY_UNCLEAR", (await alert.RecommendAsync(new ParserInput("en", "Pitt?"), CancellationToken.None)).AlertCode);
        var injected = Service(new RealParserResult("query", new(new RealHardFilters(Actors: new RealActorFilter(All: ["brad pitt"])), "drama"), LanguageCheck: "match"), search, embedding);
        Assert.AreEqual("PARSER_INVALID_RESPONSE", (await Assert.ThrowsExactlyAsync<RealProviderException>(() => injected.RecommendAsync(new ParserInput("en", "Pitt"), CancellationToken.None))).Code);
        Assert.AreEqual(0, search.Hard + search.Hybrid + search.Preflight + embedding.Calls);
        Assert.ThrowsExactly<ArgumentException>(() => new RealRecommendationService(new FixedParser(new("alert", AlertCode: "QUERY_UNCLEAR")), new(), embedding, search, parserProfile: QueryParserProfile.ActorV6));
    }

    [TestMethod]
    public async Task V6SelectedLanguageFailureStopsBeforeEmbeddingWithoutFallback()
    {
        var search = new FakeSearch { FailReadiness = true }; var embedding = new FakeEmbedding();
        var service = Service(new RealParserResult("query", new(new RealHardFilters(Actors: new RealActorFilter(All: ["Brad Pitt"])), "drama"), LanguageCheck: "match"), search, embedding);
        var failure = await Assert.ThrowsExactlyAsync<RealProviderException>(() => service.RecommendAsync(new ParserInput("sr", "драма са Питом"), CancellationToken.None));
        Assert.AreEqual("SEARCH_UNAVAILABLE", failure.Code); Assert.AreEqual(1, search.Preflight);
        Assert.AreEqual(0, embedding.Calls + search.Hybrid + search.Hard);
    }

    private static RealRecommendationService Service(RealParserResult result, FakeSearch search, FakeEmbedding embeddings) =>
        new(new FixedParser(result), new(new ActorNameResolver(["Brad Pitt"])), embeddings, search, languageAwarePoc: true, parserProfile: QueryParserProfile.ActorV6);
    private static float[] Unit() { var v = new float[768]; v[0] = 1; return v; }
    private sealed class FixedParser(RealParserResult result) : IRealQueryParser
    { public Task<RealParserResult> ParseAsync(string language, string message, CancellationToken cancellationToken) => Task.FromResult(result); }
    private sealed class FakeEmbedding : IRealQueryEmbeddingProvider
    {
        public int Calls; public string? Query;
        public Task<float[]> EmbedQueryAsync(string semanticQuery, CancellationToken cancellationToken) { Calls++; Query = semanticQuery; return Task.FromResult(Unit()); }
    }
    private sealed class FakeSearch : IRealMovieSearch
    {
        public int Hard, Hybrid, Preflight; public SearchLanguage Language;
        public bool FailReadiness;
        public Task EnsureSelectedLanguageReadyAsync(SearchLanguage language, CancellationToken cancellationToken) { Preflight++; Language = language; return FailReadiness ? Task.FromException(new RealProviderException("SEARCH_UNAVAILABLE")) : Task.CompletedTask; }
        public Task<IReadOnlyList<FilteredMovie>> SearchHybridAsync(RealHardFilters filters, float[] vector, CancellationToken ct) => throw new AssertFailedException("No legacy fallback.");
        public Task<IReadOnlyList<FilteredMovie>> SearchHybridAsync(RealHardFilters filters, float[] vector, SearchLanguage language, CancellationToken ct) { Hybrid++; Language = language; return Task.FromResult<IReadOnlyList<FilteredMovie>>([]); }
        public Task<IReadOnlyList<FilteredMovie>> SearchHardOnlyAsync(RealHardFilters filters, CancellationToken ct) { Hard++; return Task.FromResult<IReadOnlyList<FilteredMovie>>([]); }
    }
    private sealed class Host(string environment) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environment;
        public string ApplicationName { get; set; } = "V6Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
