using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CineKros.Catalog.Importer;
using CineKros.VectorImporter;

namespace CineKros.VectorImporter.Tests;

[TestClass]
public sealed class ActorBaselineV6Tests
{
    internal static string CanonicalSchema()
    {
        var root = FullBilingualVectorArtifactTests.FindRepositoryRoot(); Assert.IsNotNull(root);
        return File.ReadAllText(Path.Combine(root, "tests", "CineKros.VectorImporter.Tests", "Fixtures", "v6-canonical-base-schema.json")).TrimEnd('\r', '\n');
    }
    internal static JsonObject Descriptor()
    {
        var schema = CanonicalSchema();
        return JsonSerializer.SerializeToNode(new { schemaVersion = "v6-actor-import-baseline-v1", sourceDatabase = "cinekros",
            serverSystemIdentifier = "7690124133367640101", postgresMajor = 17, pgvectorVersion = "0.8.6", backupSha256 = new string('a', 64),
            migration001Sha256 = ActorImportBaselineV6.Migration001Sha256, migration002Sha256 = ActorImportBaselineV6.Migration002Sha256,
            movieCount = 9730, vectorCount = 9730, enCount = 9730, srCount = 9730, releasePins = ActorImportBaselineV6.ReleasePins,
            baselineDigests = ActorImportBaselineV6.DigestNames.ToDictionary(x => x, x => ActorImportBaselineV6.ApprovedContentDigests.GetValueOrDefault(x) ?? new string('b', 32)), schemaSnapshot = schema,
            schemaSha256 = ActorImportBaselineV6.Hash(schema) })!.AsObject();
    }
    private static ActorImportBaselineV6 Parse(JsonNode node) => ActorImportBaselineV6.Parse(Encoding.UTF8.GetBytes(node.ToJsonString()));

    [TestMethod]
    public void ExactFreshDescriptorBindsFullSchemaAndFiveTablesPlusOldProjection()
    {
        var node = Descriptor(); var result = Parse(node);
        Assert.AreEqual("7690124133367640101", result.ServerSystemIdentifier);
        CollectionAssert.AreEqual(ActorImportBaselineV6.DigestNames, result.Digests.Keys.ToArray());
        Assert.AreEqual(ActorSchemaV6.BaseSchemaSha256, ActorImportBaselineV6.Hash(result.SchemaSnapshot));
        Assert.AreEqual(ActorImportBaselineV6.Hash(Encoding.UTF8.GetBytes(node.ToJsonString())), result.DescriptorSha256);
    }

    [TestMethod]
    public void DescriptorRejectsWrongPinsTypesCountsVersionsHashesAndUnknownFields()
    {
        foreach (var field in new[] { "schemaVersion", "sourceDatabase", "serverSystemIdentifier", "postgresMajor", "pgvectorVersion", "backupSha256", "migration001Sha256", "migration002Sha256", "movieCount", "vectorCount", "enCount", "srCount", "releasePins", "baselineDigests", "schemaSnapshot", "schemaSha256" })
        {
            var node = Descriptor(); node[field] = "foreign";
            Assert.Throws<Exception>(() => Parse(node), field);
            node = Descriptor(); node.Remove(field);
            Assert.Throws<Exception>(() => Parse(node), "missing " + field);
        }
        foreach (var field in new[] { "catalogVersion", "catalogSha256", "catalogIdentitySha256", "orderedIdSha256", "enCorpusSha256", "srCorpusSha256", "enArtifactSha256", "srArtifactSha256", "enTextFormatVersion", "srTextFormatVersion", "dictionarySha256", "profileFingerprint" })
        {
            var node = Descriptor(); node["releasePins"]![field] = "foreign";
            Assert.Throws<Exception>(() => Parse(node), field);
        }
        var extra = Descriptor(); extra["ready"] = true; Assert.Throws<Exception>(() => Parse(extra));
        var size = Descriptor(); size["baselineDatabaseBytes"] = 54343347; Assert.Throws<Exception>(() => Parse(size));
        var upper = Descriptor(); upper["backupSha256"] = new string('A', 64); Assert.Throws<Exception>(() => Parse(upper));
        foreach (var field in ActorImportBaselineV6.ApprovedContentDigests.Keys)
        {
            var forged = Descriptor(); forged["baselineDigests"]![field] = new string('0', 32);
            Assert.ThrowsExactly<InvalidDataException>(() => Parse(forged), field);
        }
    }

    [TestMethod]
    public void DescriptorRejectsDuplicateKeysAndReorderedRootOrDigestFields()
    {
        var text = Descriptor().ToJsonString();
        Assert.Throws<Exception>(() => ActorImportBaselineV6.Parse(Encoding.UTF8.GetBytes(text.Insert(1, "\"sourceDatabase\":\"cinekros\","))));
        var node = Descriptor(); var reordered = new JsonObject();
        foreach (var p in node.Reverse()) reordered.Add(p.Key, p.Value?.DeepClone());
        Assert.Throws<Exception>(() => Parse(reordered));
        var digests = new JsonObject(); foreach (var p in node["baselineDigests"]!.AsObject().Reverse()) digests.Add(p.Key, p.Value?.DeepClone());
        node["baselineDigests"] = digests; Assert.Throws<Exception>(() => Parse(node));
    }

    [TestMethod]
    public void SameCountWeakenedConstraintCannotBeBlessedByRecomputedBaselineSchemaHash()
    {
        var node = Descriptor(); var schema = JsonNode.Parse(node["schemaSnapshot"]!.GetValue<string>())!;
        var constraints = schema["constraints"]!.AsArray();
        constraints.Single(x => x![1]!.GetValue<string>() == "movies_year_check")![3] = "CHECK ((year >= 0))";
        Assert.AreEqual(38, constraints.Count);
        var text = schema.ToJsonString(); node["schemaSnapshot"] = text; node["schemaSha256"] = ActorImportBaselineV6.Hash(text);
        Assert.ThrowsExactly<InvalidDataException>(() => Parse(node));
    }

    [TestMethod]
    public void BaseSchemaRejectsIndexTypeNullableDimensionFkAndUnexpectedObjects()
    {
        foreach (var mutation in new[] { "index", "nullable", "dimension", "fk", "trigger", "function", "policy", "column" })
        {
            var schema = JsonNode.Parse(CanonicalSchema())!;
            if (mutation == "index") schema["indexes"]!.AsArray().Single(x => x![1]!.GetValue<string>() == "ix_movies_genres")![2] = "CREATE INDEX ix_movies_genres ON public.movies USING btree (genres)";
            if (mutation == "nullable") schema["columns"]!.AsArray().Single(x => x![0]!.GetValue<string>() == "movies" && x[1]!.GetValue<string>() == "title")![3] = false;
            if (mutation == "dimension") schema["columns"]!.AsArray().Single(x => x![1]!.GetValue<string>() == "embedding")![2] = "vector(384)";
            if (mutation == "fk") schema["constraints"]!.AsArray().Single(x => x![2]!.GetValue<string>() == "f")![3] = "FOREIGN KEY (movie_lens_id) REFERENCES movies(movie_lens_id) ON DELETE CASCADE";
            if (mutation == "trigger") schema["triggers"]!.AsArray().Add(new JsonArray("movies", "tamper"));
            if (mutation == "function") schema["functions"]!.AsArray().Add(new JsonArray("tamper"));
            if (mutation == "policy") schema["policies"]!.AsArray().Add(new JsonArray("movies", "tamper"));
            if (mutation == "column") schema["columns"]!.AsArray().Add(new JsonArray("movies", "cast", "text[]", false, null, "", "", "default"));
            Assert.Throws<Exception>(() => ActorSchemaV6.ValidateBaseSnapshot(schema.ToJsonString()), mutation);
        }
    }

    [TestMethod]
    public async Task PublishedV6ReaderProjectsEveryStructuredFieldAndRawRatingLexemeOffline()
    {
        var root = FullBilingualVectorArtifactTests.FindRepositoryRoot(); Assert.IsNotNull(root);
        string P(string path) => Path.GetFullPath(Path.Combine(root, path));
        var catalogPath = P("database/data/derived/final/actor-search-v6/v6-catalog-01/catalog/movies-catalog.jsonl");
        if (!File.Exists(catalogPath)) Assert.Inconclusive("Local immutable V6 catalog is absent; no database or provider is used.");
        var catalog = await ActorCatalogV6Reader.LoadAsync(catalogPath, P("database/data/derived/final/actor-search-v6/v6-catalog-01/catalog/manifest.json"),
            P("database/data/derived/translations/sr-latn-v1-r1/tag-translations-sr.json"), P("database/data/derived/final/b05a-real-tmdb-01/movies-catalog.jsonl"));
        Assert.AreEqual(9730, catalog.Movies.Count); Assert.AreEqual(79, catalog.Movies.Count(x => x.Cast is null));
        using var lines = File.ReadLines(catalogPath).GetEnumerator();
        foreach (var movie in catalog.Movies)
        {
            Assert.IsTrue(lines.MoveNext()); using var json = JsonDocument.Parse(lines.Current); var row = json.RootElement;
            Assert.IsNotNull(movie.Metadata); var m = movie.Metadata;
            Assert.AreEqual(row.GetProperty("movieLensId").GetInt64(), m.Id);
            Assert.AreEqual(row.GetProperty("title").GetString(), m.Title); Assert.AreEqual(row.GetProperty("imdbId").GetString(), m.ImdbId);
            Assert.AreEqual(row.GetProperty("year").GetInt32(), m.Year);
            Assert.AreEqual(row.GetProperty("averageRating").ValueKind == JsonValueKind.Null ? null : row.GetProperty("averageRating").GetRawText(), m.AverageRating);
            if (movie.Cast is not null) Assert.IsTrue(movie.Cast.Count > 0 && movie.Cast.Distinct(StringComparer.Ordinal).Count() == movie.Cast.Count);
        }
        Assert.IsFalse(lines.MoveNext());
    }
}
