using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CineKros.Etl;

namespace CineKros.Etl.Tests;

[TestClass]
public sealed class SemanticExporterTests
{
    [TestMethod]
    public void Export_RendersOrderedRawTopTenAndExactText()
    {
        using var fixture = new SemanticFixture();
        fixture.WriteTags("tag,item_id,score\n\"z, quoted\",1,0.7\nalpha,1,0.7\nnegative,1,-2\nlarge,1,1.5\n" +
                          "empty-only,99,0.2\n\"escaped \"\"name\"\"\",1,1.5\n");
        fixture.WriteB1a(Movie(1, "Movie", 2000, "Director", "Cast") + Movie(2, "No Tags", 2002, null, null));
        var expected = fixture.Expected();

        var result = SemanticExporter.Export(expected);
        var lines = File.ReadAllLines(Path.Combine(fixture.Output, "movies-semantic.jsonl"), new UTF8Encoding(false, true));
        Assert.HasCount(2, lines);
        using var first = JsonDocument.Parse(lines[0]);
        var root = first.RootElement;
        CollectionAssert.AreEqual(new[] { "movieLensId", "rawTitle", "title", "year", "imdbId", "movieLensAvgRating", "directedByRaw", "starringRaw", "relevantTags", "semanticText" }, root.EnumerateObject().Select(x => x.Name).ToArray());
        Assert.AreEqual(1L, root.GetProperty("movieLensId").GetInt64());
        var tags = root.GetProperty("relevantTags").EnumerateArray().ToArray();
        CollectionAssert.AreEqual(new[] { "escaped \"name\"", "large", "alpha", "z, quoted", "negative" }, tags.Select(x => x.GetProperty("name").GetString()).ToArray());
        Assert.AreEqual(-2d, tags[^1].GetProperty("score").GetDouble());
        var semanticText = root.GetProperty("semanticText").GetString()!;
        Assert.AreEqual("Title: Movie. Year: 2000. Director: Director. Cast: Cast. Tags: escaped \"name\", large, alpha, z, quoted, negative.", semanticText);
        Assert.DoesNotContain("4.12345", semanticText);
        Assert.DoesNotContain("Movie (2000)", semanticText);
        using var empty = JsonDocument.Parse(lines[1]);
        Assert.AreEqual(0, empty.RootElement.GetProperty("relevantTags").GetArrayLength());
        Assert.AreEqual("Title: No Tags. Year: 2002. Director: unknown. Cast: unknown. Tags: none.", empty.RootElement.GetProperty("semanticText").GetString());
        Assert.AreEqual(2, result.OutputRecords);
        Assert.IsTrue(result.ContentFingerprint.Length == 64);
        var fingerprintPayload = "{\"catalogVersion\":\"B2-semantic-catalog-v1\",\"mappingVersion\":\"B1a-v1\",\"tagRuleVersion\":\"B2-tagdl-top10-v1\",\"semanticTemplateVersion\":\"B2-semantic-text-v1\",\"b1aSha256\":\"" + Convert.ToHexStringLower(expected.ExpectedB1aSha256) + "\",\"tagdlSha256\":\"" + Convert.ToHexStringLower(expected.ExpectedTagdlSha256) + "\"}";
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintPayload))), result.ContentFingerprint);
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixture.Output, "manifest.json")));
        Assert.AreEqual(result.ContentFingerprint, manifest.RootElement.GetProperty("contentFingerprint").GetString());
    }

    [TestMethod]
    public void Export_RejectsDuplicatePairsAndInvalidValuesBeforePublishing()
    {
        using var fixture = new SemanticFixture();
        fixture.WriteB1a(Movie(1, "Movie", 2000, null, null));
        fixture.WriteTags("tag,item_id,score\na,1,-0.2\na,1,0.8\n");
        Assert.ThrowsExactly<ExportValidationException>(() => SemanticExporter.Export(fixture.Expected()));
        Assert.IsFalse(Directory.Exists(fixture.Output));

        fixture.WriteTags("tag,item_id,score\na,1,NaN\n");
        Assert.ThrowsExactly<ExportValidationException>(() => SemanticExporter.Export(fixture.Expected()));
        Assert.IsFalse(Directory.Exists(fixture.Output));
    }

    [TestMethod]
    public void Export_RejectsDuplicateB1aIdBeforePublishing()
    {
        using var fixture = new SemanticFixture();
        fixture.WriteB1a(Movie(1, "Movie", 2000, null, null) + Movie(1, "Duplicate", 2001, null, null));
        fixture.WriteTags("tag,item_id,score\na,1,0.5\n");
        Assert.ThrowsExactly<ExportValidationException>(() => SemanticExporter.Export(fixture.Expected()));
        Assert.IsFalse(Directory.Exists(fixture.Output));
    }

    [TestMethod]
    public void Export_SelectsExactlyTenWithOrdinalTieBreakAtCutoff()
    {
        using var fixture = new SemanticFixture();
        fixture.WriteB1a(Movie(1, "Movie", 2000, null, null));
        var rows = Enumerable.Range(1, 9).Select(i => $"rank{i:D2},1,{3.0 - i / 10d:0.0}\n").ToList();
        rows.Add("z-boundary,1,0.5\n"); rows.Add("a-boundary,1,0.5\n"); rows.Add("below-boundary,1,0.4\n");
        fixture.WriteTags("tag,item_id,score\n" + string.Concat(rows));

        SemanticExporter.Export(fixture.Expected());
        using var record = JsonDocument.Parse(File.ReadAllLines(Path.Combine(fixture.Output, "movies-semantic.jsonl"))[0]);
        var tags = record.RootElement.GetProperty("relevantTags").EnumerateArray().ToArray();
        Assert.HasCount(10, tags);
        CollectionAssert.AreEqual(Enumerable.Range(1, 9).Select(i => $"rank{i:D2}").Append("a-boundary").ToArray(), tags.Select(x => x.GetProperty("name").GetString()).ToArray());
        Assert.AreEqual(0.5d, tags[^1].GetProperty("score").GetDouble());
        Assert.IsFalse(tags.Any(x => x.GetProperty("name").GetString() == "z-boundary"));
    }

    [TestMethod]
    public void Export_SerializesParseableNonJsonScoreAsValidJsonNumber()
    {
        using var fixture = new SemanticFixture();
        fixture.WriteB1a(Movie(1, "Movie", 2000, null, null));
        fixture.WriteTags("tag,item_id,score\na,1,+2\n");
        SemanticExporter.Export(fixture.Expected());
        using var output = JsonDocument.Parse(File.ReadAllLines(Path.Combine(fixture.Output, "movies-semantic.jsonl"))[0]);
        Assert.AreEqual(2d, output.RootElement.GetProperty("relevantTags")[0].GetProperty("score").GetDouble());
        Assert.AreEqual(JsonValueKind.Number, output.RootElement.GetProperty("relevantTags")[0].GetProperty("score").ValueKind);
    }

    [TestMethod]
    public void Export_RejectsNewlinesInSemanticFields()
    {
        foreach (var invalid in new[]
        {
            Movie(1, "Bad\nTitle", 2000, null, null),
            Movie(1, "Title", 2000, "Bad\rDirector", null),
            Movie(1, "Title", 2000, null, "Bad\nCast"),
        })
        {
            using var fixture = new SemanticFixture();
            fixture.WriteB1a(invalid);
            fixture.WriteTags("tag,item_id,score\na,1,0.5\n");
            Assert.ThrowsExactly<ExportValidationException>(() => SemanticExporter.Export(fixture.Expected()));
            Assert.IsFalse(Directory.Exists(fixture.Output));
        }
    }

    [TestMethod]
    public void Export_PublishFaultAfterStagingCleansStageAndPreservesPriorOutput()
    {
        using var fixture = new SemanticFixture();
        fixture.WriteB1a(Movie(1, "Movie", 2000, null, null));
        fixture.WriteTags("tag,item_id,score\na,1,0.5\n");
        var options = fixture.Expected();
        SemanticExporter.Export(options);
        var jsonBefore = File.ReadAllBytes(Path.Combine(fixture.Output, "movies-semantic.jsonl"));
        var manifestBefore = File.ReadAllBytes(Path.Combine(fixture.Output, "manifest.json"));
        var failedOutput = fixture.Output + "-publish-failure";
        var faulted = options with
        {
            OutputDirectory = failedOutput,
            TestStageHook = stage => { if (stage == SemanticExportStage.BeforePublish) throw new IOException("injected publish failure"); },
        };

        Assert.ThrowsExactly<IOException>(() => SemanticExporter.Export(faulted));
        CollectionAssert.AreEqual(jsonBefore, File.ReadAllBytes(Path.Combine(fixture.Output, "movies-semantic.jsonl")));
        CollectionAssert.AreEqual(manifestBefore, File.ReadAllBytes(Path.Combine(fixture.Output, "manifest.json")));
        Assert.IsFalse(Directory.Exists(failedOutput));
        AssertNoStagingDirectory(failedOutput);
    }

    [TestMethod]
    public void Export_CancellationAfterStagingLeavesNoPublishedPairOrStage()
    {
        using var fixture = new SemanticFixture();
        fixture.WriteB1a(Movie(1, "Movie", 2000, null, null));
        fixture.WriteTags("tag,item_id,score\na,1,0.5\n");
        using var cancellation = new CancellationTokenSource();
        var options = fixture.Expected(stage => { if (stage == SemanticExportStage.BeforePublish) cancellation.Cancel(); });

        Assert.ThrowsExactly<OperationCanceledException>(() => SemanticExporter.Export(options, cancellation.Token));
        Assert.IsFalse(Directory.Exists(fixture.Output));
        AssertNoStagingDirectory(fixture.Output);
    }

    [TestMethod]
    public void Export_RefusesExistingOutputAndPreservesIt()
    {
        using var fixture = new SemanticFixture();
        fixture.WriteB1a(Movie(1, "Movie", 2000, null, null));
        fixture.WriteTags("tag,item_id,score\na,1,0.5\n");
        SemanticExporter.Export(fixture.Expected());
        var before = File.ReadAllBytes(Path.Combine(fixture.Output, "movies-semantic.jsonl"));
        Assert.ThrowsExactly<ExportValidationException>(() => SemanticExporter.Export(fixture.Expected()));
        CollectionAssert.AreEqual(before, File.ReadAllBytes(Path.Combine(fixture.Output, "movies-semantic.jsonl")));
    }

    [TestMethod]
    public void SemanticCli_ValidatesAllRequiredSingletonAbsoluteOptions()
    {
        var paths = Enumerable.Range(0, 4).Select(i => Path.GetFullPath(Path.Combine(Path.GetTempPath(), "semantic", i.ToString()))).ToArray();
        var parsed = SemanticCliArguments.Parse(["--input-jsonl", paths[0], "--input-manifest", paths[1], "--tagdl-csv", paths[2], "--output-dir", paths[3]]);
        Assert.AreEqual(paths[3], parsed.OutputDirectory);
        Assert.ThrowsExactly<ArgumentException>(() => SemanticCliArguments.Parse(["--input-jsonl", "relative"]));
        Assert.ThrowsExactly<ArgumentException>(() => SemanticCliArguments.Parse(["--input-jsonl", paths[0], "--input-jsonl", paths[0]]));
        Assert.ThrowsExactly<ArgumentException>(() => SemanticCliArguments.Parse(["--unknown", paths[0]]));
    }

    private static string Movie(long id, string title, int year, string? director, string? cast) => JsonSerializer.Serialize(new
    {
        movieLensId = id, rawTitle = title + " (" + year + ")", title, year, imdbId = id.ToString("D7"), movieLensAvgRating = 4.12345,
        directedByRaw = director, starringRaw = cast,
    }) + "\n";

    private sealed class SemanticFixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "CineKros.Semantic.Tests", Guid.NewGuid().ToString("N"));
        public string Output => Path.Combine(root, "output");
        private string Input => Path.Combine(root, "input.jsonl");
        private string Manifest => Path.Combine(root, "input-manifest.json");
        private string Tags => Path.Combine(root, "tagdl.csv");
        public void WriteB1a(string content)
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Input, content, new UTF8Encoding(false, true));
            var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Input)));
            var count = File.ReadAllLines(Input).Length;
            File.WriteAllText(Manifest, JsonSerializer.Serialize(new { mappingVersion = "B1a-v1", output = new { sha256 = hash }, counts = new { outputRecords = count }, exclusions = new { MISSING_METADATA = new { count = 0, movieLensIds = Array.Empty<long>() } } }), new UTF8Encoding(false));
        }
        public void WriteTags(string content) { Directory.CreateDirectory(root); File.WriteAllText(Tags, content, new UTF8Encoding(false, true)); }
        public SemanticExportOptions Expected(Action<SemanticExportStage>? observer = null) => new(Input, Manifest, Tags, Output, SHA256.HashData(File.ReadAllBytes(Input)), SHA256.HashData(File.ReadAllBytes(Manifest)), SHA256.HashData(File.ReadAllBytes(Tags)), new SemanticExpectedCounts(File.ReadAllLines(Input).Length, File.ReadAllLines(Tags).Length - 1)) { TestStageHook = observer };
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void AssertNoStagingDirectory(string output)
    {
        var parent = Path.GetDirectoryName(output)!;
        var prefix = Path.GetFileName(output) + ".staging-";
        Assert.IsFalse(Directory.Exists(parent) && Directory.EnumerateDirectories(parent, prefix + "*").Any());
    }
}
