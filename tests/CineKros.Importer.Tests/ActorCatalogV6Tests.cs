using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Diagnostics;
using CineKros.Catalog.Importer;

namespace CineKros.Importer.Tests;

[TestClass]
public sealed class ActorCatalogV6Tests
{
    [TestMethod]
    public async Task V6ReaderIndependentlyValidatesFixtureAndRejectsIdentityMappingTextAndMetadataTampering()
    {
        foreach (var mutation in new[] { "none", "identity", "metadata", "text", "cast", "corpus", "mapping", "vocabulary", "duplicate-key", "utf8", "truncated", "sidecar" })
        {
            using var f = new Fixture(); await f.Export("release");
            var catalog = f.PathOf("release/catalog/movies-catalog.jsonl"); var manifest = f.PathOf("release/catalog/manifest.json");
            if (mutation == "none")
            {
                var result = await ActorCatalogV6Reader.LoadFixtureAsync(catalog, manifest, f.Dictionary, f.Source);
                Assert.AreEqual(3, result.Movies.Count); Assert.AreEqual(ActorCatalogV6.EnTextFormatVersion, result.EnTextFormatVersion);
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ActorCatalogV6Reader.LoadAsync(catalog, manifest, f.Dictionary, f.Source));
                continue;
            }
            var root = JsonNode.Parse(File.ReadAllText(manifest))!; var payload = root["identityPayload"]!;
            if (mutation is "metadata" or "text" or "cast")
            {
                var rows = File.ReadAllLines(catalog); var row = JsonNode.Parse(rows[0])!;
                if (mutation == "metadata") row["title"] = "changed";
                if (mutation == "text") row["semanticText"] = "Title: invented.";
                if (mutation == "cast") row["cast"]![0] = "Invented Actor";
                rows[0] = row.ToJsonString(); File.WriteAllText(catalog, string.Join('\n', rows) + "\n");
            }
            if (mutation == "corpus") payload["enCorpusSha256"] = new string('0', 64);
            if (mutation == "mapping") payload["orderedCastMappingSha256"] = new string('0', 64);
            if (mutation == "vocabulary")
            {
                var path = f.PathOf("release/actors/actor-vocabulary.json"); var vocabulary = JsonNode.Parse(File.ReadAllText(path))!;
                vocabulary["names"]![0] = "Invented Actor"; File.WriteAllText(path, vocabulary.ToJsonString());
                payload["actorVocabularySha256"] = Digest(File.ReadAllBytes(path));
            }
            if (mutation == "duplicate-key") File.WriteAllText(catalog, File.ReadAllText(catalog).Replace("{\"movieLensId\":1", "{\"movieLensId\":1,\"movieLensId\":1", StringComparison.Ordinal));
            if (mutation == "utf8") File.WriteAllBytes(catalog, [0xff]);
            if (mutation == "truncated") File.WriteAllText(catalog, "{");
            var hash = Digest(File.ReadAllBytes(catalog)); payload["catalogSha256"] = hash;
            File.WriteAllText(f.PathOf("release/catalog/content.sha256"), mutation == "sidecar" ? "wrong\n" : hash + "\n");
            var options = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            root["identitySha256"] = mutation == "identity" ? new string('0', 64) : Digest(JsonSerializer.SerializeToUtf8Bytes(payload, options));
            File.WriteAllText(manifest, root.ToJsonString(options));
            await Assert.ThrowsAsync<Exception>(() => ActorCatalogV6Reader.LoadFixtureAsync(catalog, manifest, f.Dictionary, f.Source), mutation);
        }
        static string Digest(byte[] bytes) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    [TestMethod]
    public async Task V6ReaderValidatesApprovedLocalReleaseWithoutInferenceWhenPresent()
    {
        var root = Environment.GetEnvironmentVariable("CINEKROS_V6_REPOSITORY_ROOT");
        if (string.IsNullOrWhiteSpace(root)) Assert.Inconclusive("Explicit CINEKROS_V6_REPOSITORY_ROOT enables read-only approved release validation.");
        var catalogRoot = System.IO.Path.Combine(root!, "database/data/derived/final/actor-search-v6/v6-catalog-01/catalog");
        var result = await ActorCatalogV6Reader.LoadAsync(System.IO.Path.Combine(catalogRoot, "movies-catalog.jsonl"), System.IO.Path.Combine(catalogRoot, "manifest.json"),
            System.IO.Path.Combine(root!, "database/data/derived/translations/sr-latn-v1-r1/tag-translations-sr.json"), System.IO.Path.Combine(root!, "database/data/derived/final/b05a-real-tmdb-01/movies-catalog.jsonl"));
        Assert.AreEqual(9730, result.Movies.Count); Assert.AreEqual(ActorCatalogV6Reader.IdentitySha256, result.IdentitySha256);
        Assert.AreEqual("38355e10039d885161e3523ee1e5a66411d80d7f35b57992db83d75fe8242c6e", result.EnCorpusSha256);
        Assert.AreEqual("0b11455b7a3159d4fe7e7924d36521907112e03d7381c394c40599a57c4ecad1", result.SrCorpusSha256);
    }

    [TestMethod]
    public void StrictTokenizerAcceptsOnlyCompleteValidSupportedShapes()
    {
        foreach (var json in new[] { "{\"x\":1}\n{\"x\":2}", "{\n\"x\":1\n}\n{\n\"x\":2\n}", "[{\"x\":1},{\"x\":2}]" })
            Assert.AreEqual(2, ActorCatalogV6.ReadRecords(Encoding.UTF8.GetBytes(json)).Count);
        foreach (var json in new[] { "", "{", "{} garbage", "{} true", "{}[]", "[{}]{}", "[1]", "{\"x\":1,}", "{/*a*/}", "{\"x\":1,\"x\":2}", "{\"a\":[{\"x\":1,\"x\":2}]}", "{\"a\":{\"x\":1,\"x\":2}}", "{}\u0000" })
            Assert.ThrowsExactly<InvalidDataException>(() => ActorCatalogV6.ReadRecords(Encoding.UTF8.GetBytes(json)), json);
        Assert.ThrowsExactly<DecoderFallbackException>(() => ActorCatalogV6.ReadRecords([123, 34, 120, 34, 58, 34, 0xff, 34, 125]));
    }

    [TestMethod]
    public async Task FixtureRepeatIsByteEqualConservesLexemesAndReportsPolicies()
    {
        using var fixture = new Fixture();
        var a = await fixture.Export("a"); var b = await fixture.Export("b");
        Assert.AreEqual(a.IdentitySha256, b.IdentitySha256);
        var paths = Directory.GetFiles(fixture.PathOf("a"), "*", SearchOption.AllDirectories);
        Assert.AreEqual(10, paths.Length);
        foreach (var path in paths) CollectionAssert.AreEqual(File.ReadAllBytes(path), File.ReadAllBytes(System.IO.Path.Combine(fixture.PathOf("b"), System.IO.Path.GetRelativePath(fixture.PathOf("a"), path))));
        var output = ActorCatalogV6.ReadRecords(File.ReadAllBytes(fixture.PathOf("a/catalog/movies-catalog.jsonl")));
        var input = ActorCatalogV6.ReadRecords(File.ReadAllBytes(fixture.Current));
        Assert.AreEqual(3, output.Count);
        for (var i = 0; i < output.Count; i++)
        {
            Assert.AreEqual(20, output[i].EnumerateObject().Count());
            foreach (var field in ActorCatalogV6.Fields.Where(x => x is not "semanticText" and not "semanticTextSr"))
                Assert.IsTrue(ActorCatalogV6.Equal(input[i].GetProperty(field), output[i].GetProperty(field)), field);
            Assert.AreEqual("4.12345678901234567890123456789", output[i].GetProperty("movieLensAvgRating").GetRawText());
            Assert.AreEqual("1.234567890123456789e-2", output[i].GetProperty("relevantTags")[0].GetProperty("score").GetRawText());
        }
        Assert.AreEqual("Title: Brad Pitt: Cast: Title. Year: 2000. Director: Brad Pitt. Tags: Brad Pitt.", output[0].GetProperty("semanticText").GetString());
        Assert.AreEqual("Naslov: Brad Pitt: Cast: Title. Godina: 2000. Režija: Brad Pitt. Tagovi: Bred Pit.", output[0].GetProperty("semanticTextSr").GetString());
        Assert.AreEqual(JsonValueKind.Null, output[1].GetProperty("cast").ValueKind);
        using var anomalies = JsonDocument.Parse(File.ReadAllBytes(fixture.PathOf("a/reports/cast-anomalies.json")));
        Assert.AreEqual("retained-distinct-exact-only", anomalies.RootElement.GetProperty("collisionPolicy").GetString());
        Assert.AreEqual(1, anomalies.RootElement.GetProperty("collisions").GetArrayLength());
        var parsingEvents = anomalies.RootElement.GetProperty("events").EnumerateArray().ToArray();
        Assert.AreEqual(1, parsingEvents.Count(x => x.GetProperty("kind").GetString() == "trailing-empty-separator"));
        Assert.AreEqual(1, parsingEvents.Count(x => x.GetProperty("kind").GetString() == "reviewed-empty-separator-override"));
        Assert.AreEqual("Ian McDiarmid, Brad Pitt, George Clooney,", output[0].GetProperty("starringRaw").GetString());
        Assert.AreEqual("Caroll Spinney,,Bob Baker, Ian Mcdiarmid", output[2].GetProperty("starringRaw").GetString());
        CollectionAssert.AreEqual(new[] { "Caroll Spinney", "Bob Baker", "Ian Mcdiarmid" }, output[2].GetProperty("cast").EnumerateArray().Select(x => x.GetString()).ToArray());
        using var overrides = JsonDocument.Parse(File.ReadAllBytes(fixture.PathOf("a/actors/cast-parsing-overrides.json")));
        Assert.AreEqual("reviewed-empty-separator-override", overrides.RootElement.GetProperty("parsingOverrides")[0].GetProperty("kind").GetString());
        var reviewText = File.ReadAllText(fixture.PathOf("a/catalog/review-sample.json"));
        StringAssert.Contains(reviewText, "\n  \"movies\": [\n");
        Assert.IsFalse(reviewText.Contains('\r'));
        using var review = JsonDocument.Parse(reviewText);
        foreach (var movie in review.RootElement.GetProperty("movies").EnumerateArray())
        {
            Assert.AreEqual(20, movie.GetProperty("catalogRecord").EnumerateObject().Count());
            Assert.AreEqual(movie.GetProperty("afterEn").GetString(), movie.GetProperty("catalogRecord").GetProperty("semanticText").GetString());
        }
        using var reproducibility = JsonDocument.Parse(File.ReadAllBytes(fixture.PathOf("a/reports/reproducibility.json")));
        Assert.IsFalse(reproducibility.RootElement.GetProperty("repeatProofExecuted").GetBoolean());
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Export("a"));
        File.WriteAllText(fixture.PathOf("occupied-file"), "occupied");
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Export("occupied-file"));
        Assert.IsFalse(Directory.GetDirectories(fixture.Root, "*.staging-*").Any());
    }

    [TestMethod]
    public async Task MappingIdentityDetectsAssignmentsEvenWhenVocabularyIsUnchanged()
    {
        using var a = new Fixture(); using var b = new Fixture(swap: true);
        await a.Export("release"); await b.Export("release");
        using var ma = JsonDocument.Parse(File.ReadAllBytes(a.PathOf("release/catalog/manifest.json")));
        using var mb = JsonDocument.Parse(File.ReadAllBytes(b.PathOf("release/catalog/manifest.json")));
        var pa = ma.RootElement.GetProperty("identityPayload"); var pb = mb.RootElement.GetProperty("identityPayload");
        Assert.AreEqual(pa.GetProperty("actorVocabularySha256").GetString(), pb.GetProperty("actorVocabularySha256").GetString());
        Assert.AreNotEqual(pa.GetProperty("orderedCastMappingSha256").GetString(), pb.GetProperty("orderedCastMappingSha256").GetString());
        Assert.AreNotEqual(ma.RootElement.GetProperty("identitySha256").GetString(), mb.RootElement.GetProperty("identitySha256").GetString());
    }

    [TestMethod]
    public async Task RejectsConservationTemplateTranslationIdAndProductionPinChanges()
    {
        foreach (var mutation in new[] { "number", "template", "translation", "duplicate-id", "malformed-cast" })
        {
            using var f = new Fixture();
            var current = JsonNode.Parse(File.ReadAllText(f.Current))!.AsArray();
            if (mutation == "number") current[0]!["movieLensAvgRating"] = 4.0;
            if (mutation == "template") current[0]!["semanticText"] = "changed";
            if (mutation == "translation") current[0]!["tagsSr"]![0] = "wrong";
            if (mutation == "duplicate-id") current[1]!["movieLensId"] = 1;
            if (mutation == "malformed-cast") current[0]!["starringRaw"] = "One; Two";
            File.WriteAllText(f.Current, current.ToJsonString());
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => f.Export("bad"));
            Assert.IsFalse(Directory.Exists(f.PathOf("bad")));
        }
        using var pinned = new Fixture();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => ActorCatalogV6.ExportAsync(pinned.Current, pinned.Accepted, pinned.Source, pinned.Dictionary, pinned.PathOf("production")));
        Assert.IsFalse(Directory.Exists(pinned.PathOf("production")));
    }

    [TestMethod]
    public async Task ValidatedTemplatesAndDictionaryAreRequiredEvenIfInputsAgree()
    {
        using var f = new Fixture();
        foreach (var path in new[] { f.Current, f.Accepted, f.Source })
        {
            var rows = ActorCatalogV6.ReadRecords(File.ReadAllBytes(path));
            File.WriteAllText(path, string.Join('\n', rows.Select(x => x.GetRawText().Replace("Director: Brad Pitt.", "Director: Invented.", StringComparison.Ordinal))));
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => f.Export("bad"));
    }

    [TestMethod]
    public async Task RejectsAgreedDuplicateIdsAndDictionaryDriftAndEquivalentNumericLexeme()
    {
        using (var f = new Fixture())
        {
            foreach (var path in new[] { f.Current, f.Accepted, f.Source })
                File.WriteAllText(path, File.ReadAllText(path).Replace("\"movieLensId\":2", "\"movieLensId\":1", StringComparison.Ordinal));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => f.Export("duplicate"));
        }
        using (var f = new Fixture())
        {
            foreach (var path in new[] { f.Current, f.Accepted })
                File.WriteAllText(path, File.ReadAllText(path).Replace("\"tagsSr\":[\"Bred Pit\"]", "\"tagsSr\":[\"Different\"]", StringComparison.Ordinal));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => f.Export("translation"));
        }
        using (var f = new Fixture())
        {
            File.WriteAllText(f.Current, File.ReadAllText(f.Current).Replace("4.12345678901234567890123456789", "4.12345678901234567890123456789e0", StringComparison.Ordinal));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => f.Export("lexeme"));
        }
    }

    [TestMethod]
    public async Task CancellationLeavesDestinationAndInputsUntouched()
    {
        using var f = new Fixture(); var original = File.ReadAllBytes(f.Current);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => f.Export("cancelled", cancellation.Token));
        Assert.IsFalse(Directory.Exists(f.PathOf("cancelled")));
        Assert.IsFalse(Directory.GetDirectories(f.Root, "*.staging-*").Any());
        CollectionAssert.AreEqual(original, File.ReadAllBytes(f.Current));
    }

    [TestMethod]
    public async Task FileOnlyCliHasDistinctArgumentAndValidationExitsWithoutDatabaseConfiguration()
    {
        using var f = new Fixture();
        foreach (var validArgumentCount in new[] { false, true })
        {
            var start = new ProcessStartInfo("dotnet") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            start.ArgumentList.Add(typeof(ActorCatalogV6).Assembly.Location);
            start.ArgumentList.Add("export-actor-catalog-v6");
            if (validArgumentCount) foreach (var path in new[] { f.Current, f.Accepted, f.Source, f.Dictionary, f.PathOf("cli") }) start.ArgumentList.Add(path);
            start.Environment.Remove("DATABASE_CONNECTION_STRING");
            using var process = Process.Start(start)!;
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.AreEqual(validArgumentCount ? 1 : 2, process.ExitCode);
            Assert.IsFalse(error.Contains("DATABASE_CONNECTION_STRING", StringComparison.Ordinal));
        }
        Assert.IsFalse(Directory.Exists(f.PathOf("cli")));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cinekros-v6-fixture-" + Guid.NewGuid().ToString("N"));
        public string Current => PathOf("current.json"); public string Accepted => PathOf("accepted.jsonl");
        public string Source => PathOf("source.jsonl"); public string Dictionary => PathOf("dictionary.json");
        public string PathOf(string name) => System.IO.Path.Combine(Root, name);
        public Fixture(bool swap = false)
        {
            Directory.CreateDirectory(Root);
            var casts = swap ? new string?[] { "Ian Mcdiarmid,", null, "Caroll Spinney,,Bob Baker, Ian McDiarmid, Brad Pitt, George Clooney" } : ["Ian McDiarmid, Brad Pitt, George Clooney,", null, "Caroll Spinney,,Bob Baker, Ian Mcdiarmid"];
            var rows = casts.Select((cast, i) => Row(i == 2 ? 3396 : i + 1, cast)).ToArray();
            File.WriteAllText(Current, "[" + string.Join(',', rows) + "]", new UTF8Encoding(false));
            File.WriteAllText(Accepted, string.Join('\n', rows) + "\n", new UTF8Encoding(false));
            File.WriteAllText(Source, string.Join('\n', rows.Select(row => row[..row.IndexOf(",\"tagsSr\":", StringComparison.Ordinal)] + "}")) + "\n", new UTF8Encoding(false));
            File.WriteAllText(Dictionary, "{\"entries\":[{\"en\":\"Brad Pitt\",\"sr\":\"Bred Pit\"}]}", new UTF8Encoding(false));
        }
        public Task<ActorCatalogV6Release> Export(string name, CancellationToken token = default) => ActorCatalogV6.ExportFixtureAsync(Current, Accepted, Source, Dictionary, PathOf(name), token);
        private static string Row(int id, string? cast)
        {
            var en = $"Title: Brad Pitt: Cast: Title. Year: 2000. Director: Brad Pitt. Cast: {cast ?? "unknown"}. Tags: Brad Pitt.";
            var sr = $"Naslov: Brad Pitt: Cast: Title. Godina: 2000. Režija: Brad Pitt. Glumci: {cast ?? "nepoznato"}. Tagovi: Bred Pit.";
            return "{\"movieLensId\":" + id + ",\"rawTitle\":\"Original\",\"title\":\"Brad Pitt: Cast: Title\",\"year\":2000,\"imdbId\":\"0000001\",\"movieLensAvgRating\":4.12345678901234567890123456789,\"directedByRaw\":\"Brad Pitt\",\"starringRaw\":" + JsonSerializer.Serialize(cast) + ",\"relevantTags\":[{\"name\":\"Brad Pitt\",\"score\":1.234567890123456789e-2}],\"semanticText\":" + JsonSerializer.Serialize(en) + ",\"tmdbId\":null,\"genres\":[\"Drama\",\"Comedy\"],\"averageRating\":4.00000000000000000001,\"ratingCount\":1234567890123,\"runtimeMinutes\":null,\"originalLanguage\":null,\"posterPath\":null,\"tagsSr\":[\"Bred Pit\"],\"semanticTextSr\":" + JsonSerializer.Serialize(sr) + "}";
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}
