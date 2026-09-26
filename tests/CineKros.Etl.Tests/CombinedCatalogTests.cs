using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CineKros.Etl;

namespace CineKros.Etl.Tests;

[TestClass]
public sealed class CombinedCatalogTests
{
    [TestMethod]
    public void Merge_CombinesAcceptedFieldsInContractOrderAndIsDeterministic()
    {
        using var fixture = new Fixture();
        var first = CombinedCatalogExporter.Export(fixture.Options("one"));
        var second = CombinedCatalogExporter.Export(fixture.Options("two"));
        Assert.AreEqual(3, first.OutputRecords);
        Assert.AreEqual(first.OutputSha256, second.OutputSha256);
        Assert.AreEqual(first.ContentFingerprint, second.ContentFingerprint);
        var lines = File.ReadAllLines(fixture.Output("one"));
        using var row = JsonDocument.Parse(lines[0]);
        CollectionAssert.AreEqual(new[] { "movieLensId", "rawTitle", "title", "year", "imdbId", "movieLensAvgRating", "directedByRaw", "starringRaw", "relevantTags", "semanticText", "tmdbId", "genres", "averageRating", "ratingCount", "runtimeMinutes", "originalLanguage", "posterPath" },
            row.RootElement.EnumerateObject().Select(x => x.Name).ToArray());
        Assert.AreEqual("Retained\u0022 title", row.RootElement.GetProperty("title").GetString());
        Assert.AreEqual("Title: Retained\u0022 title. Year: 2000. Director: unknown. Cast: unknown. Tags: none.", row.RootElement.GetProperty("semanticText").GetString());
        Assert.AreEqual("Comedy", row.RootElement.GetProperty("genres")[0].GetString());
        using var unmatched = JsonDocument.Parse(lines[1]);
        foreach (var field in new[] { "tmdbId", "genres", "averageRating", "ratingCount", "runtimeMinutes", "originalLanguage", "posterPath" }) Assert.AreEqual(JsonValueKind.Null, unmatched.RootElement.GetProperty(field).ValueKind);
        using var noTmdb = JsonDocument.Parse(lines[2]);
        Assert.AreEqual(JsonValueKind.Null, noTmdb.RootElement.GetProperty("tmdbId").ValueKind);
        Assert.AreEqual("Animation", noTmdb.RootElement.GetProperty("genres")[0].GetString());
        Assert.AreEqual(0, noTmdb.RootElement.GetProperty("ratingCount").GetInt32());
        Assert.AreEqual(JsonValueKind.Null, noTmdb.RootElement.GetProperty("averageRating").ValueKind);
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(fixture.Manifest("one")));
        Assert.AreEqual(first.OutputSha256, manifest.RootElement.GetProperty("output").GetProperty("sha256").GetString());
        Assert.AreEqual(3, manifest.RootElement.GetProperty("output").GetProperty("recordCount").GetInt32());
        using var b2 = JsonDocument.Parse(File.ReadAllBytes(fixture.SourceManifest(true)));
        Assert.AreEqual(b2.RootElement.GetProperty("contentFingerprint").GetString(), manifest.RootElement.GetProperty("semanticContentFingerprint").GetString());
        using var b04 = JsonDocument.Parse(File.ReadAllBytes(fixture.SourceManifest(false)));
        Assert.AreEqual(b04.RootElement.GetProperty("contentFingerprint").GetString(), manifest.RootElement.GetProperty("enrichmentContentFingerprint").GetString());
    }

    [TestMethod]
    public void Merge_RejectsTypedMismatchWithFreshAbsentOutput()
    {
        using var fixture = new Fixture();
        fixture.MutateEnrichment(1, "title", "wrong");
        var exception = Assert.ThrowsExactly<ExportValidationException>(() => CombinedCatalogExporter.Export(fixture.Options("fresh-mismatch")));
        StringAssert.Contains(exception.Message, "B2/B04 value mismatch for title");
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(fixture.Output("fresh-mismatch"))));
    }

    [TestMethod]
    public void Merge_MismatchLeavesEarlierSuccessfulOutputByteIdentical()
    {
        using var fixture = new Fixture();
        CombinedCatalogExporter.Export(fixture.Options("prior"));
        var priorPath = fixture.Output("prior");
        var priorHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(priorPath)));
        fixture.MutateEnrichment(1, "title", "wrong");
        Assert.ThrowsExactly<ExportValidationException>(() => CombinedCatalogExporter.Export(fixture.Options("after-mismatch")));
        Assert.AreEqual(priorHash, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(priorPath))));
    }

    [TestMethod]
    public void Merge_CancellationAtPublishBoundaryRemovesStageAndKeepsFinalAbsent()
    {
        using var fixture = new Fixture(); using var cancellation = new CancellationTokenSource();
        var options = fixture.Options("cancel") with { BeforePublish = cancellation.Cancel };
        Assert.ThrowsExactly<OperationCanceledException>(() => CombinedCatalogExporter.Export(options, cancellation.Token));
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(fixture.Output("cancel"))));
        Assert.IsFalse(Directory.EnumerateDirectories(Path.GetDirectoryName(Path.GetDirectoryName(fixture.Output("cancel"))!)!, "cancel.staging-*").Any());
    }

    [TestMethod]
    public void Merge_PublishFaultRemovesStageAndPreservesPriorSuccessfulDirectory()
    {
        using var fixture = new Fixture();
        CombinedCatalogExporter.Export(fixture.Options("prior"));
        var priorHash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fixture.Output("prior"))));
        var faulted = fixture.Options("fault") with { BeforePublish = () => throw new IOException("injected publish fault") };
        Assert.ThrowsExactly<IOException>(() => CombinedCatalogExporter.Export(faulted));
        Assert.AreEqual(priorHash, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(fixture.Output("prior")))));
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(fixture.Output("fault"))));
        Assert.IsFalse(Directory.EnumerateDirectories(fixture.Root, "fault.staging-*").Any());
    }

    [TestMethod]
    public void MergeCli_RejectsMissingDuplicateRelativeAndUnknownOptions()
    {
        var absolute = Path.GetFullPath(Path.GetTempPath());
        Assert.ThrowsExactly<ArgumentException>(() => CombinedCatalogArguments.Parse([]));
        Assert.ThrowsExactly<ArgumentException>(() => CombinedCatalogArguments.Parse(["--semantic-dir", absolute]));
        Assert.ThrowsExactly<ArgumentException>(() => CombinedCatalogArguments.Parse(["--semantic-dir", absolute, "--semantic-dir", absolute, "--enrichment-dir", absolute, "--output-dir", absolute]));
        Assert.ThrowsExactly<ArgumentException>(() => CombinedCatalogArguments.Parse(["--semantic-dir", ".", "--enrichment-dir", absolute, "--output-dir", absolute]));
        Assert.ThrowsExactly<ArgumentException>(() => CombinedCatalogArguments.Parse(["--other", absolute, "--semantic-dir", absolute, "--enrichment-dir", absolute, "--output-dir", absolute]));
    }

    [TestMethod]
    public void Merge_RejectsMalformedDuplicateExtraAndMissingOrConflictingSourceData()
    {
        using (var fixture = new Fixture()) { fixture.MutateSemantic(1, "title", 42); Assert.ThrowsExactly<ExportValidationException>(() => CombinedCatalogExporter.Export(fixture.Options("bad"))); }
        using (var fixture = new Fixture()) { fixture.MutateSemantic(2, "movieLensId", 1); Assert.ThrowsExactly<ExportValidationException>(() => CombinedCatalogExporter.Export(fixture.Options("bad"))); }
        using (var fixture = new Fixture()) { fixture.MutateSemantic(1, "unexpected", "field"); Assert.ThrowsExactly<ExportValidationException>(() => CombinedCatalogExporter.Export(fixture.Options("bad"))); }
        using (var fixture = new Fixture()) { fixture.MutateSemanticManifest("catalogVersion", "wrong-version"); Assert.ThrowsExactly<ExportValidationException>(() => CombinedCatalogExporter.Export(fixture.Options("bad"))); }
        using (var fixture = new Fixture()) { fixture.MutateSemanticManifestB1Hash("0000000000000000000000000000000000000000000000000000000000000000"); Assert.ThrowsExactly<ExportValidationException>(() => CombinedCatalogExporter.Export(fixture.Options("bad"))); }
        using (var fixture = new Fixture()) { fixture.CorruptSemanticJsonl(); Assert.ThrowsExactly<ExportValidationException>(() => CombinedCatalogExporter.Export(fixture.Options("bad"))); }
    }

    [TestMethod]
    public void Merge_RejectsSameCountMissingAndExtraIdAfterResealingSourceManifest()
    {
        using var fixture = new Fixture();
        fixture.MutateEnrichment(3, "movieLensId", 4);
        var exception = Assert.ThrowsExactly<ExportValidationException>(() => CombinedCatalogExporter.Export(fixture.Options("id-gap")));
        Assert.AreEqual("Catalog IDs do not align.", exception.Message);
        Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(fixture.Output("id-gap"))));
    }

    [TestMethod]
    public void Merge_RejectsRehashedAverageRatingOutsideApprovedRange()
    {
        foreach (var averageRating in new[] { -0.1, 5.1 })
        {
            using var fixture = new Fixture();
            fixture.MutateEnrichment(1, "averageRating", averageRating);
            Assert.ThrowsExactly<ExportValidationException>(() => CombinedCatalogExporter.Export(fixture.Options("bad-rating")));
            Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(fixture.Output("bad-rating"))));
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "CineKros.B05A.Tests", Guid.NewGuid().ToString("N"));
        private readonly string semanticDir;
        private readonly string enrichmentDir;
        private readonly string[] semantic;
        private readonly string[] enriched;
        private readonly string b1Hash;

        public Fixture()
        {
            semanticDir = Path.Combine(root, "semantic"); enrichmentDir = Path.Combine(root, "enrichment");
            Directory.CreateDirectory(semanticDir); Directory.CreateDirectory(enrichmentDir);
            semantic = Enumerable.Range(1, 3).Select(id => JsonSerializer.Serialize(new
            {
                movieLensId = id, rawTitle = $"Raw {id}", title = id == 1 ? "Retained\" title" : $"Title {id}", year = 1999 + id,
                imdbId = $"{id:D7}", movieLensAvgRating = id == 1 ? 3.5m : 4m, directedByRaw = (string?)null, starringRaw = (string?)null,
                relevantTags = Array.Empty<object>(), semanticText = id == 1 ? "Title: Retained\" title. Year: 2000. Director: unknown. Cast: unknown. Tags: none." : "unchanged semantic text"
            })).ToArray();
            enriched = Enumerable.Range(1, 3).Select(id => JsonSerializer.Serialize(new
            {
                movieLensId = id, rawTitle = $"Raw {id}", title = id == 1 ? "Retained\" title" : $"Title {id}", year = 1999 + id,
                imdbId = $"{id:D7}", movieLensAvgRating = id == 1 ? 3.50 : 4.0, directedByRaw = (string?)null, starringRaw = (string?)null,
                tmdbId = id == 1 ? 101 : (int?)null, genres = id == 1 ? new[] { "Comedy", "Drama" } : id == 3 ? ["Animation"] : null,
                averageRating = id == 1 ? 4.25m : (decimal?)null, ratingCount = id == 1 ? 2 : id == 3 ? 0 : (int?)null,
                runtimeMinutes = (int?)null, originalLanguage = (string?)null, posterPath = (string?)null
            })).ToArray();
            enriched[0] = enriched[0].Replace("\"movieLensAvgRating\":3.5", "\"movieLensAvgRating\":3.50", StringComparison.Ordinal);
            b1Hash = Hash(Encoding.UTF8.GetBytes("b1 fixture"));
            WriteSource("semantic", semantic, "B2-semantic-catalog-v1", "catalogVersion", true);
            WriteSource("enrichment", enriched, "B04-ml32m-tmdb-v2", "mappingVersion", false);
        }

        public string Output(string name) => Path.Combine(root, name, "movies-catalog.jsonl");
        public string Root => root;
        public string SourceManifest(bool semanticSource) => Path.Combine(semanticSource ? semanticDir : enrichmentDir, "manifest.json");
        public void CorruptSemanticJsonl() => File.AppendAllText(Path.Combine(semanticDir, "movies-semantic.jsonl"), " ");
        public string Manifest(string name) => Path.Combine(root, name, "manifest.json");
        public CombinedCatalogOptions Options(string name) => new(semanticDir, enrichmentDir, Path.Combine(root, name), b1Hash, 3);
        public void MutateEnrichment(int index, string property, object value)
        {
            enriched[index - 1] = JsonSerializer.Serialize(Mutate(JsonNode.Parse(enriched[index - 1])!.AsObject(), property, value));
            WriteSource("enrichment", enriched, "B04-ml32m-tmdb-v2", "mappingVersion", false);
        }
        public void MutateSemantic(int index, string property, object value)
        {
            semantic[index - 1] = JsonSerializer.Serialize(Mutate(JsonNode.Parse(semantic[index - 1])!.AsObject(), property, value));
            WriteSource("semantic", semantic, "B2-semantic-catalog-v1", "catalogVersion", true);
        }
        public void MutateSemanticManifest(string property, string value)
        {
            var path = Path.Combine(semanticDir, "manifest.json"); var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject(); node[property] = value; File.WriteAllText(path, node.ToJsonString());
        }
        public void MutateSemanticManifestB1Hash(string value)
        {
            var path = Path.Combine(semanticDir, "manifest.json"); var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject(); node["inputs"]!["b1aJsonl"]!["sha256"] = value; File.WriteAllText(path, node.ToJsonString());
        }
        private static JsonObject Mutate(JsonObject node, string key, object value) { node[key] = JsonSerializer.SerializeToNode(value); return node; }
        private void WriteSource(string name, string[] records, string version, string versionProperty, bool b2)
        {
            var dir = name == "semantic" ? semanticDir : enrichmentDir;
            var jsonName = b2 ? "movies-semantic.jsonl" : "movies-enriched.jsonl";
            File.WriteAllText(Path.Combine(dir, jsonName), string.Concat(records.Select(x => x + "\n")), new UTF8Encoding(false, true));
            var bytes = File.ReadAllBytes(Path.Combine(dir, jsonName));
            var b1 = new { path = "b1.jsonl", sha256 = b1Hash };
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (b2) File.WriteAllText(manifestPath, JsonSerializer.Serialize(new { mappingVersion = "B1a-v1", catalogVersion = version, tagRuleVersion = "B2-tagdl-top10-v1", semanticTemplateVersion = "B2-semantic-text-v1", contentFingerprint = FingerprintB2(), inputs = new { b1aJsonl = b1, tagdlCsv = new { path = "tags.csv", sha256 = Hash(Encoding.UTF8.GetBytes("tags")) } }, output = new { path = jsonName, sha256 = Hash(bytes), recordCount = records.Length, order = "movieLensId ascending" }, counts = new { b1aRecords = records.Length, outputRecords = records.Length } }), new UTF8Encoding(false));
            else File.WriteAllText(manifestPath, JsonSerializer.Serialize(new { mappingVersion = version, cacheVersion = "tmdb-cache-v2", contentFingerprint = FingerprintB04(), cacheSha256 = Hash(Encoding.UTF8.GetBytes("cache")), inputs = new { b1aJsonl = b1, links = new { path = "links.csv", md5 = Hash(Encoding.UTF8.GetBytes("links")) }, movies = new { path = "movies.csv", md5 = Hash(Encoding.UTF8.GetBytes("movies")) }, ratings = new { path = "ratings.csv", md5 = Hash(Encoding.UTF8.GetBytes("ratings")) } }, output = new { path = jsonName, sha256 = Hash(bytes), count = records.Length, order = "movieLensId-ascending" }, counts = new { matched = 2, unmatched = 1, unmatchedSkipped = 1, noTmdbIdSkipped = 1 } }), new UTF8Encoding(false));
        }
        private string FingerprintB2() => Hash(Encoding.UTF8.GetBytes($"{{\"catalogVersion\":\"B2-semantic-catalog-v1\",\"mappingVersion\":\"B1a-v1\",\"tagRuleVersion\":\"B2-tagdl-top10-v1\",\"semanticTemplateVersion\":\"B2-semantic-text-v1\",\"b1aSha256\":\"{b1Hash}\",\"tagdlSha256\":\"{Hash(Encoding.UTF8.GetBytes("tags"))}\"}}"));
        private string FingerprintB04() => Hash(Encoding.UTF8.GetBytes($"B04-ml32m-tmdb-v2|{b1Hash}|{Hash(Encoding.UTF8.GetBytes("links"))}|{Hash(Encoding.UTF8.GetBytes("movies"))}|{Hash(Encoding.UTF8.GetBytes("ratings"))}|{Hash(Encoding.UTF8.GetBytes("cache"))}"));
        private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
