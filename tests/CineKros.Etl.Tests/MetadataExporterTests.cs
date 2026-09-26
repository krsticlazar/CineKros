using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CineKros.Etl;

namespace CineKros.Etl.Tests;

[TestClass]
public sealed class MetadataExporterTests
{
    [TestMethod]
    public void Export_MapsAndOrdersSyntheticRecordsDeterministically()
    {
        using var fixture = new ExportFixture();
        fixture.WriteScores(
            "tag,item_id,score\n" +
            "\"quoted, tag\",2,-0.5\n" +
            "\"escaped \"\"tag\"\"\",1,1.25\n" +
            "plain,3,0.75\n");
        fixture.WriteMetadata(
            MetadataLine(2, "Second (2001)", "0000002", 3.125m, " Director Two ", " ") +
            MetadataLine(1, " First (1999) ", "0000001", 4.12345m, "", " Actor One "));

        var expected = new ExpectedCounts(2, 3, 3, 2, 1);
        var first = MetadataExporter.Export(fixture.Options(expected));
        var firstBytes = File.ReadAllBytes(fixture.OutputJsonl);
        var firstHash = Convert.ToHexStringLower(SHA256.HashData(firstBytes));
        var lines = File.ReadAllLines(fixture.OutputJsonl, new UTF8Encoding(false, true));

        Assert.HasCount(2, lines);
        using var firstMovie = JsonDocument.Parse(lines[0]);
        var propertyNames = firstMovie.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        CollectionAssert.AreEqual(
            new[] { "movieLensId", "rawTitle", "title", "year", "imdbId", "movieLensAvgRating", "directedByRaw", "starringRaw" },
            propertyNames);
        Assert.AreEqual(1L, firstMovie.RootElement.GetProperty("movieLensId").GetInt64());
        Assert.AreEqual(" First (1999) ", firstMovie.RootElement.GetProperty("rawTitle").GetString());
        Assert.AreEqual("First", firstMovie.RootElement.GetProperty("title").GetString());
        Assert.AreEqual(1999, firstMovie.RootElement.GetProperty("year").GetInt32());
        Assert.AreEqual("0000001", firstMovie.RootElement.GetProperty("imdbId").GetString());
        Assert.AreEqual(4.12345m, firstMovie.RootElement.GetProperty("movieLensAvgRating").GetDecimal());
        Assert.AreEqual(JsonValueKind.Null, firstMovie.RootElement.GetProperty("directedByRaw").ValueKind);
        Assert.AreEqual("Actor One", firstMovie.RootElement.GetProperty("starringRaw").GetString());
        Assert.AreEqual(3L, first.MissingMetadataIds.Single());
        Assert.AreEqual(firstHash, first.OutputSha256);
        using (var manifest = JsonDocument.Parse(File.ReadAllBytes(fixture.Manifest)))
        {
            CollectionAssert.AreEqual(
                new[] { "mappingVersion", "generatedAtUtc", "inputs", "output", "counts", "exclusions" },
                manifest.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
            Assert.AreEqual("B1a-v1", manifest.RootElement.GetProperty("mappingVersion").GetString());
            Assert.AreEqual(firstHash, manifest.RootElement.GetProperty("output").GetProperty("sha256").GetString());
            Assert.AreEqual(2, manifest.RootElement.GetProperty("counts").GetProperty("outputRecords").GetInt32());
            Assert.AreEqual(3L, manifest.RootElement.GetProperty("exclusions").GetProperty("MISSING_METADATA").GetProperty("movieLensIds")[0].GetInt64());
        }

        var second = MetadataExporter.Export(fixture.Options(expected));
        CollectionAssert.AreEqual(firstBytes, File.ReadAllBytes(fixture.OutputJsonl));
        Assert.AreEqual(first.OutputSha256, second.OutputSha256);
    }

    [TestMethod]
    public void Export_ValidationFailurePreservesBothPreviousOutputs()
    {
        using var fixture = new ExportFixture();
        fixture.WriteScores("tag,item_id,score\nplain,1,0.5\n");
        fixture.WriteMetadata(MetadataLine(1, "Valid (2000)", "0000001", 3.5m, "Director", "Actor"));
        var expected = new ExpectedCounts(1, 1, 1, 1, 0);
        MetadataExporter.Export(fixture.Options(expected));
        var jsonlBefore = File.ReadAllBytes(fixture.OutputJsonl);
        var manifestBefore = File.ReadAllBytes(fixture.Manifest);

        fixture.WriteMetadata(MetadataLine(1, "Malformed title", "0000001", 3.5m, "Director", "Actor"));
        Assert.ThrowsExactly<ExportValidationException>(() => MetadataExporter.Export(fixture.Options(expected)));
        CollectionAssert.AreEqual(jsonlBefore, File.ReadAllBytes(fixture.OutputJsonl));
        CollectionAssert.AreEqual(manifestBefore, File.ReadAllBytes(fixture.Manifest));
    }

    [TestMethod]
    public void Export_RejectsDuplicateMetadataId()
    {
        using var fixture = new ExportFixture();
        fixture.WriteScores("tag,item_id,score\nplain,1,0.5\n");
        fixture.WriteMetadata(
            MetadataLine(1, "First (2000)", "0000001", 3m, "Director", "Actor") +
            MetadataLine(1, "Duplicate (2001)", "0000002", 4m, "Director", "Actor"));
        Assert.ThrowsExactly<ExportValidationException>(() =>
            MetadataExporter.Export(fixture.Options(new ExpectedCounts(2, 1, 1, 1, 0))));
    }

    [TestMethod]
    public void Export_RejectsDuplicateJoinedImdbId()
    {
        using var fixture = new ExportFixture();
        fixture.WriteScores("tag,item_id,score\nplain,1,0.5\nplain,2,0.6\n");
        fixture.WriteMetadata(
            MetadataLine(1, "First (2000)", "0000001", 3m, "Director", "Actor") +
            MetadataLine(2, "Second (2001)", "0000001", 4m, "Director", "Actor"));
        Assert.ThrowsExactly<ExportValidationException>(() =>
            MetadataExporter.Export(fixture.Options(new ExpectedCounts(2, 2, 2, 2, 0))));
    }

    [TestMethod]
    public void Export_RejectsMalformedCsvAndInvalidUtf8()
    {
        using var csvFixture = new ExportFixture();
        csvFixture.WriteScores("tag,item_id,score\n\"open,1,0.5\n");
        csvFixture.WriteMetadata(MetadataLine(1, "Valid (2000)", "0000001", 3m, "Director", "Actor"));
        Assert.ThrowsExactly<ExportValidationException>(() =>
            MetadataExporter.Export(csvFixture.Options(new ExpectedCounts(1, 1, 1, 1, 0))));

        using var utf8Fixture = new ExportFixture();
        utf8Fixture.WriteScores("tag,item_id,score\nplain,1,0.5\n");
        Directory.CreateDirectory(Path.GetDirectoryName(utf8Fixture.MetadataPath)!);
        File.WriteAllBytes(utf8Fixture.MetadataPath, [0xff, 0xfe, 0xfd]);
        Assert.ThrowsExactly<ExportValidationException>(() =>
            MetadataExporter.Export(utf8Fixture.Options(new ExpectedCounts(1, 1, 1, 1, 0))));
    }

    [TestMethod]
    public void Export_ApprovedSnapshotCountsRejectChangedSource()
    {
        using var fixture = new ExportFixture();
        fixture.WriteScores("tag,item_id,score\nplain,1,0.5\n");
        fixture.WriteMetadata(MetadataLine(1, "Valid (2000)", "0000001", 3m, "Director", "Actor"));
        Assert.ThrowsExactly<ExportValidationException>(() =>
            MetadataExporter.Export(fixture.Options(ExpectedCounts.ApprovedSnapshot)));
    }

    [TestMethod]
    public void Export_DoesNotApplyMappedTitleRuleOutsideScoredMembership()
    {
        using var fixture = new ExportFixture();
        fixture.WriteScores("tag,item_id,score\nplain,1,0.5\n");
        fixture.WriteMetadata(
            MetadataLine(2, "Unscored title without year", "0000002", 2m, "Director", "Actor") +
            MetadataLine(1, "Scored (2000)", "0000001", 3m, "Director", "Actor"));

        var result = MetadataExporter.Export(fixture.Options(new ExpectedCounts(2, 1, 1, 1, 0)));
        Assert.AreEqual(1, result.OutputRecords);
    }

    [TestMethod]
    public void CliArguments_RequireEachKnownAbsoluteOptionExactlyOnce()
    {
        var source = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "source"));
        var output = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "output"));
        var parsed = CliArguments.Parse(["--source-root", source, "--output-dir", output]);
        Assert.AreEqual(source, parsed.SourceRoot);
        Assert.AreEqual(output, parsed.OutputDirectory);
        Assert.ThrowsExactly<ArgumentException>(() => CliArguments.Parse([]));
        Assert.ThrowsExactly<ArgumentException>(() => CliArguments.Parse(["--unknown", source]));
        Assert.ThrowsExactly<ArgumentException>(() => CliArguments.Parse(["--source-root", source, "--source-root", source, "--output-dir", output]));
        Assert.ThrowsExactly<ArgumentException>(() => CliArguments.Parse(["--source-root", "relative", "--output-dir", output]));
    }

    private static string MetadataLine(long id, string title, string imdbId, decimal rating, string directedBy, string starring) =>
        JsonSerializer.Serialize(new
        {
            title,
            directedBy,
            starring,
            avgRating = rating,
            imdbId,
            item_id = id,
        }) + "\n";

    private sealed class ExportFixture : IDisposable
    {
        public ExportFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "CineKros.Etl.Tests", Guid.NewGuid().ToString("N"));
            SourceRoot = Path.Combine(Root, "source");
            OutputDirectory = Path.Combine(Root, "output");
            MetadataPath = Path.Combine(SourceRoot, "raw", "metadata_updated.json");
            ScorePath = Path.Combine(SourceRoot, "scores", "tagdl.csv");
            OutputJsonl = Path.Combine(OutputDirectory, "movies-metadata.jsonl");
            Manifest = Path.Combine(OutputDirectory, "manifest.json");
        }

        public string Root { get; }
        public string SourceRoot { get; }
        public string OutputDirectory { get; }
        public string MetadataPath { get; }
        public string ScorePath { get; }
        public string OutputJsonl { get; }
        public string Manifest { get; }

        public ExportOptions Options(ExpectedCounts expected) => new(SourceRoot, OutputDirectory, expected);

        public void WriteMetadata(string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MetadataPath)!);
            File.WriteAllText(MetadataPath, content, new UTF8Encoding(false, true));
        }

        public void WriteScores(string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ScorePath)!);
            File.WriteAllText(ScorePath, content, new UTF8Encoding(false, true));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
