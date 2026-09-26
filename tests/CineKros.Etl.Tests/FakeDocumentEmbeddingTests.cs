using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CineKros.Etl;

namespace CineKros.Etl.Tests;

[TestClass]
public sealed class FakeDocumentEmbeddingTests
{
    [TestMethod]
    public void Fingerprint_UsesEachOfSixConfigurationAndTextInputs()
    {
        var original = new FakeEmbeddingConfiguration("m", "v", 3, "DOCUMENT", "f");
        var text = "exact input";
        var baseline = FakeDocumentEmbeddingRunner.Fingerprint(text, original);
        Assert.AreNotEqual(baseline, FakeDocumentEmbeddingRunner.Fingerprint(text + "!", original));
        Assert.AreNotEqual(baseline, FakeDocumentEmbeddingRunner.Fingerprint(text, original with { ModelId = "m2" }));
        Assert.AreNotEqual(baseline, FakeDocumentEmbeddingRunner.Fingerprint(text, original with { ModelVersion = "v2" }));
        Assert.AreNotEqual(baseline, FakeDocumentEmbeddingRunner.Fingerprint(text, original with { Dimension = 4 }));
        Assert.AreNotEqual(baseline, FakeDocumentEmbeddingRunner.Fingerprint(text, original with { DocumentTaskType = "OTHER" }));
        Assert.AreNotEqual(baseline, FakeDocumentEmbeddingRunner.Fingerprint(text, original with { FormattingVersion = "f2" }));
    }

    [TestMethod]
    public void Run_ReusesSuccessfulOutputAndMetadataOnlyCatalogChanges()
    {
        using var f = new Fixture(); var source = new CountingSource(); var config = Fixture.Config;
        var first = FakeDocumentEmbeddingRunner.Run(f.Catalog, f.Manifest, f.Output, f.Checkpoint, config, source);
        Assert.AreEqual(3, first.Generated); Assert.AreEqual("semantic-1", source.Texts[0]);
        var content = File.ReadAllBytes(Path.Combine(f.Output, "document-vectors.jsonl"));
        source.Texts.Clear();
        FakeDocumentEmbeddingRunner.Run(f.Catalog, f.Manifest, f.Output, f.Checkpoint, config, source);
        Assert.AreEqual(0, source.Texts.Count);
        Assert.IsTrue(content.SequenceEqual(File.ReadAllBytes(Path.Combine(f.Output, "document-vectors.jsonl"))));
        f.ChangeMetadata(); source.Texts.Clear();
        var changed = FakeDocumentEmbeddingRunner.Run(f.Catalog, f.Manifest, f.Output, f.Checkpoint, config, source);
        Assert.AreEqual(0, changed.Generated); Assert.AreEqual(3, changed.Reused);
        Assert.AreEqual(0, source.Texts.Count);
        Assert.IsTrue(content.SequenceEqual(File.ReadAllBytes(Path.Combine(f.Output, "document-vectors.jsonl"))));
        AssertCurrentExport(f, config);
    }

    [TestMethod]
    public void Run_RejectsBadConfigOrCatalogBeforeSchedulingAndKeepsPriorFinalOnFailure()
    {
        using var f = new Fixture(); var source = new CountingSource();
        Assert.ThrowsExactly<ArgumentException>(() => FakeDocumentEmbeddingRunner.Run(f.Catalog, f.Manifest, f.Output, f.Checkpoint, Fixture.Config with { ModelId = " " }, source));
        Assert.AreEqual(0, source.Texts.Count);
        File.AppendAllText(f.Catalog, "{}\n");
        Assert.ThrowsExactly<ExportValidationException>(() => FakeDocumentEmbeddingRunner.Run(f.Catalog, f.Manifest, f.Output, f.Checkpoint, Fixture.Config, source));
        Assert.AreEqual(0, source.Texts.Count);
    }

    [TestMethod]
    public void Run_RetriesFailuresAndRejectsInvalidVectorsWithoutReplacingPublishedOutput()
    {
        using var f = new Fixture(); var source = new CountingSource();
        FakeDocumentEmbeddingRunner.Run(f.Catalog, f.Manifest, f.Output, f.Checkpoint, Fixture.Config, source);
        var prior = File.ReadAllBytes(Path.Combine(f.Output, "document-vectors.jsonl"));
        File.Delete(f.Checkpoint);
        var failOnce = new FailOnceSource();
        Assert.ThrowsExactly<ExportValidationException>(() => FakeDocumentEmbeddingRunner.Run(f.Catalog, f.Manifest, f.Output, f.Checkpoint, Fixture.Config, failOnce));
        Assert.IsTrue(prior.SequenceEqual(File.ReadAllBytes(Path.Combine(f.Output, "document-vectors.jsonl"))));
        var retry = FakeDocumentEmbeddingRunner.Run(f.Catalog, f.Manifest, f.Output, f.Checkpoint, Fixture.Config, new CountingSource());
        Assert.AreEqual(1, retry.Generated); Assert.AreEqual(2, retry.Reused);
        foreach (var invalid in new[] { Array.Empty<double>(), new[] { 1d, double.NaN, 3d }, new[] { 0d, 0d, 0d } })
        {
            File.Delete(f.Checkpoint);
            Assert.ThrowsExactly<ExportValidationException>(() => FakeDocumentEmbeddingRunner.Run(f.Catalog, f.Manifest, f.Output, f.Checkpoint, Fixture.Config, new FixedSource(invalid)));
            Assert.IsTrue(prior.SequenceEqual(File.ReadAllBytes(Path.Combine(f.Output, "document-vectors.jsonl"))));
        }
    }

    [TestMethod]
    public void Run_CancellationKeepsCompletedCheckpointEntriesForResume()
    {
        using var f = new Fixture(); using var cts = new CancellationTokenSource(); var source = new CancellingSource(cts);
        Assert.ThrowsExactly<OperationCanceledException>(() => FakeDocumentEmbeddingRunner.Run(f.Catalog, f.Manifest, f.Output, f.Checkpoint, Fixture.Config, source, cts.Token));
        Assert.IsFalse(Directory.Exists(f.Output));
        var retry = FakeDocumentEmbeddingRunner.Run(f.Catalog, f.Manifest, f.Output, f.Checkpoint, Fixture.Config, new CountingSource());
        Assert.AreEqual(2, retry.Generated); Assert.AreEqual(1, retry.Reused);
    }

    [TestMethod]
    public void Run_RegeneratesOnlySemanticTextFingerprintMismatchAndExportsCurrentEntries()
    {
        using var f = new Fixture(); var original = new CountingSource();
        FakeDocumentEmbeddingRunner.Run(f.Catalog, f.Manifest, f.Output, f.Checkpoint, Fixture.Config, original);
        f.ChangeSemanticText(2); var changed = new CountingSource();
        var result = FakeDocumentEmbeddingRunner.Run(f.Catalog, f.Manifest, f.Output, f.Checkpoint, Fixture.Config, changed);
        Assert.AreEqual(1, result.Generated); Assert.AreEqual(2, result.Reused);
        CollectionAssert.AreEqual(new[] { "semantic-2-updated" }, changed.Texts);
        AssertCurrentExport(f, Fixture.Config);
        var alternate = new CountingSource();
        result = FakeDocumentEmbeddingRunner.Run(f.Catalog, f.Manifest, f.Output, f.Checkpoint, Fixture.Config with { ModelVersion = "v2" }, alternate);
        Assert.AreEqual(3, result.Generated); Assert.AreEqual(0, result.Reused);
        AssertCurrentExport(f, Fixture.Config with { ModelVersion = "v2" });
    }

    [TestMethod]
    public void Run_RepairsMalformedHeaderPreservingIndependentlyValidCompatibleEntries()
    {
        using var f = new Fixture(); var initial = new CountingSource();
        FakeDocumentEmbeddingRunner.Run(f.Catalog, f.Manifest, f.Output, f.Checkpoint, Fixture.Config, initial);
        var lines = File.ReadAllLines(f.Checkpoint); lines[0] = "not a checkpoint header"; File.WriteAllText(f.Checkpoint, string.Join("\n", lines) + "\n");
        var source = new CountingSource(); var result = FakeDocumentEmbeddingRunner.Run(f.Catalog, f.Manifest, f.Output, f.Checkpoint, Fixture.Config, source);
        Assert.AreEqual(0, result.Generated); Assert.AreEqual(3, result.Reused); Assert.AreEqual(0, source.Texts.Count);
        AssertCheckpointHeader(f.Checkpoint, Fixture.Config);
        source.Texts.Clear(); result = FakeDocumentEmbeddingRunner.Run(f.Catalog, f.Manifest, f.Output, f.Checkpoint, Fixture.Config, source);
        Assert.AreEqual(0, result.Generated); Assert.AreEqual(3, result.Reused); Assert.AreEqual(0, source.Texts.Count);
    }

    [TestMethod]
    public void Run_RepairsTornTailBeforeAppendingAndReusesAllEntriesNextTime()
    {
        using var f = new Fixture(); var initial = new CountingSource();
        FakeDocumentEmbeddingRunner.Run(f.Catalog, f.Manifest, f.Output, f.Checkpoint, Fixture.Config, initial);
        var bytes = File.ReadAllBytes(f.Checkpoint); var lastLineStart = Array.LastIndexOf(bytes, (byte)'\n', bytes.Length - 2) + 1;
        using (var stream = new FileStream(f.Checkpoint, FileMode.Open, FileAccess.Write)) { stream.SetLength(lastLineStart); stream.Position = lastLineStart; var torn = Encoding.UTF8.GetBytes("{\"movieLensId\":3,\"fingerprint\":\"incomplete"); stream.Write(torn); }
        var source = new CountingSource(); var result = FakeDocumentEmbeddingRunner.Run(f.Catalog, f.Manifest, f.Output, f.Checkpoint, Fixture.Config, source);
        Assert.AreEqual(1, result.Generated); Assert.AreEqual(2, result.Reused); Assert.AreEqual("semantic-3", source.Texts.Single());
        Assert.IsTrue(File.ReadAllText(f.Checkpoint).EndsWith("\n", StringComparison.Ordinal));
        source.Texts.Clear(); result = FakeDocumentEmbeddingRunner.Run(f.Catalog, f.Manifest, f.Output, f.Checkpoint, Fixture.Config, source);
        Assert.AreEqual(0, result.Generated); Assert.AreEqual(3, result.Reused); Assert.AreEqual(0, source.Texts.Count);
    }

    [TestMethod]
    public void Run_DiscardsMalformedMiddleJournalRecordAndRegeneratesOnlyItsFilm()
    {
        using var f = new Fixture(); FakeDocumentEmbeddingRunner.Run(f.Catalog, f.Manifest, f.Output, f.Checkpoint, Fixture.Config, new CountingSource());
        var lines = File.ReadAllLines(f.Checkpoint); lines[2] = "{malformed middle entry}"; File.WriteAllText(f.Checkpoint, string.Join("\n", lines) + "\n");
        var source = new CountingSource(); var result = FakeDocumentEmbeddingRunner.Run(f.Catalog, f.Manifest, f.Output, f.Checkpoint, Fixture.Config, source);
        Assert.AreEqual(1, result.Generated); Assert.AreEqual(2, result.Reused); Assert.AreEqual("semantic-2", source.Texts.Single());
        source.Texts.Clear(); result = FakeDocumentEmbeddingRunner.Run(f.Catalog, f.Manifest, f.Output, f.Checkpoint, Fixture.Config, source);
        Assert.AreEqual(0, result.Generated); Assert.AreEqual(3, result.Reused); Assert.AreEqual(0, source.Texts.Count);
    }

    [TestMethod]
    public void Run_DoesNotReuseCorruptPriorVectorAndThenReusesRepairedCheckpoint()
    {
        using var f = new Fixture(); FakeDocumentEmbeddingRunner.Run(f.Catalog, f.Manifest, f.Output, f.Checkpoint, Fixture.Config, new CountingSource());
        var checkpointLines = File.ReadAllLines(f.Checkpoint);
        var row = JsonNode.Parse(checkpointLines[1])!; row["vector"] = new JsonArray(9);
        checkpointLines[1] = row.ToJsonString(); File.WriteAllText(f.Checkpoint, string.Join("\n", checkpointLines) + "\n");
        var source = new CountingSource(); var result = FakeDocumentEmbeddingRunner.Run(f.Catalog, f.Manifest, f.Output, f.Checkpoint, Fixture.Config, source);
        Assert.AreEqual(1, result.Generated); Assert.AreEqual(2, result.Reused); Assert.AreEqual("semantic-1", source.Texts.Single());
        source.Texts.Clear(); result = FakeDocumentEmbeddingRunner.Run(f.Catalog, f.Manifest, f.Output, f.Checkpoint, Fixture.Config, source);
        Assert.AreEqual(0, result.Generated); Assert.AreEqual(3, result.Reused); Assert.AreEqual(0, source.Texts.Count);
    }

    [TestMethod]
    public void Run_FinalPublicationFaultRetainsBothPriorFilesAndCheckpoint()
    {
        using var f = new Fixture(); FakeDocumentEmbeddingRunner.Run(f.Catalog, f.Manifest, f.Output, f.Checkpoint, Fixture.Config, new CountingSource());
        var oldJson = File.ReadAllBytes(Path.Combine(f.Output, "document-vectors.jsonl")); var oldManifest = File.ReadAllBytes(Path.Combine(f.Output, "manifest.json"));
        var checkpoint = File.ReadAllBytes(f.Checkpoint); f.ChangeMetadata();
        Assert.ThrowsExactly<IOException>(() => FakeDocumentEmbeddingRunner.Run(f.Catalog, f.Manifest, f.Output, f.Checkpoint, Fixture.Config, new CountingSource(), beforePublish: () => throw new IOException("injected before atomic publish")));
        CollectionAssert.AreEqual(oldJson, File.ReadAllBytes(Path.Combine(f.Output, "document-vectors.jsonl")));
        CollectionAssert.AreEqual(oldManifest, File.ReadAllBytes(Path.Combine(f.Output, "manifest.json")));
        CollectionAssert.AreEqual(checkpoint, File.ReadAllBytes(f.Checkpoint));
        Assert.IsFalse(Directory.EnumerateDirectories(f.Root, "out.staging-*").Any());
        var resumed = FakeDocumentEmbeddingRunner.Run(f.Catalog, f.Manifest, f.Output, f.Checkpoint, Fixture.Config, new CountingSource());
        Assert.AreEqual(0, resumed.Generated); Assert.AreEqual(3, resumed.Reused); AssertCurrentExport(f, Fixture.Config);
    }

    [TestMethod]
    public void FakeEmbeddingCli_RequiresEveryExplicitOptionAndPositiveDimension()
    {
        var paths = new[] { "--catalog", Path.GetFullPath("catalog"), "--manifest", Path.GetFullPath("manifest"), "--output-dir", Path.GetFullPath("output"), "--checkpoint", Path.GetFullPath("checkpoint") };
        Assert.ThrowsExactly<ArgumentException>(() => FakeEmbeddingCliArguments.Parse(paths));
        Assert.ThrowsExactly<ArgumentException>(() => FakeEmbeddingCliArguments.Parse([.. paths, "--model-id", "m", "--model-version", "v", "--dimension", "0", "--document-task-type", "DOCUMENT", "--formatting-version", "f"]));
        Assert.ThrowsExactly<ArgumentException>(() => FakeEmbeddingCliArguments.Parse([.. paths, "--model-id", "m", "--model-version", "v", "--dimension", "2", "--document-task-type", "DOCUMENT"]));
        var valid = FakeEmbeddingCliArguments.Parse([.. paths, "--model-id", "m", "--model-version", "v", "--dimension", "2", "--document-task-type", "DOCUMENT", "--formatting-version", "f"]);
        Assert.AreEqual("m", valid.Configuration.ModelId); Assert.AreEqual(2, valid.Configuration.Dimension);
    }

    private sealed class CountingSource : IFakeDocumentVectorSource
    {
        internal readonly List<string> Texts = [];
        public double[] Generate(string text, string fingerprint, FakeEmbeddingConfiguration configuration) { Texts.Add(text); return [1, 2, 3]; }
    }
    private sealed class FailOnceSource : IFakeDocumentVectorSource
    {
        private bool failed;
        public double[] Generate(string text, string fingerprint, FakeEmbeddingConfiguration configuration) { if (!failed) { failed = true; throw new IOException("injected"); } return [1, 2, 3]; }
    }
    private sealed class FixedSource(double[] vector) : IFakeDocumentVectorSource { public double[] Generate(string text, string fingerprint, FakeEmbeddingConfiguration configuration) => vector; }
    private sealed class CancellingSource(CancellationTokenSource cts) : IFakeDocumentVectorSource
    {
        public double[] Generate(string text, string fingerprint, FakeEmbeddingConfiguration configuration) { cts.Cancel(); return [1, 2, 3]; }
    }

    private sealed class Fixture : IDisposable
    {
        internal static readonly FakeEmbeddingConfiguration Config = new("fake-symbol", "v1", 3, "DOCUMENT", "fmt1");
        private readonly string root = Path.Combine(Path.GetTempPath(), "b05b-" + Guid.NewGuid().ToString("N"));
        internal string Catalog => Path.Combine(root, "catalog.jsonl"); internal string Manifest => Path.Combine(root, "manifest.json");
        internal string Output => Path.Combine(root, "out"); internal string Checkpoint => Path.Combine(root, "checkpoint.json");
        internal Fixture() { Directory.CreateDirectory(root); WriteCatalog(); }
        internal void ChangeMetadata() { var rows = File.ReadAllLines(Catalog).Select(line => JsonNode.Parse(line)).ToArray(); rows[0]!["title"] = "changed metadata"; Write(rows!); }
        internal void ChangeSemanticText(int id) { var rows = File.ReadAllLines(Catalog).Select(line => JsonNode.Parse(line)).ToArray(); rows[id - 1]!["semanticText"] = $"semantic-{id}-updated"; Write(rows!); }
        internal string Root => root;
        private void WriteCatalog() { Write(Enumerable.Range(1, 3).Select(i => (JsonNode)JsonNode.Parse($"{{\"movieLensId\":{i},\"semanticText\":\"semantic-{i}\",\"title\":\"title-{i}\"}}")!).ToArray()); }
        private void Write(JsonNode[] rows) { File.WriteAllText(Catalog, string.Join("\n", rows.Select(x => x!.ToJsonString())) + "\n", new UTF8Encoding(false)); var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Catalog))); File.WriteAllText(Manifest, JsonSerializer.Serialize(new { catalogVersion = "B05a-combined-catalog-v1", output = new { sha256 = hash, recordCount = rows.Length, order = "movieLensId ascending" } })); }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void AssertCurrentExport(Fixture f, FakeEmbeddingConfiguration config)
    {
        var rows = File.ReadAllLines(Path.Combine(f.Output, "document-vectors.jsonl"));
        Assert.AreEqual(3, rows.Length); long prior = 0;
        using var catalog = File.OpenText(f.Catalog);
        for (var i = 0; i < rows.Length; i++)
        {
            using var output = JsonDocument.Parse(rows[i]); using var input = JsonDocument.Parse(catalog.ReadLine()!);
            var id = output.RootElement.GetProperty("movieLensId").GetInt64(); Assert.IsTrue(id > prior); prior = id;
            var expected = FakeDocumentEmbeddingRunner.Fingerprint(input.RootElement.GetProperty("semanticText").GetString()!, config);
            Assert.AreEqual(expected, output.RootElement.GetProperty("fingerprint").GetString());
            CollectionAssert.AreEqual(new[] { 1d, 2d, 3d }, output.RootElement.GetProperty("vector").EnumerateArray().Select(x => x.GetDouble()).ToArray());
            Assert.AreEqual("FAKE ONLY - NOT PRODUCTION VECTORS", output.RootElement.GetProperty("fakeMarker").GetString());
        }
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(f.Output, "manifest.json")));
        CollectionAssert.AreEqual(new[] { "fakeMarker", "catalogVersion", "catalogSha256", "outputSha256", "recordCount", "configuration" }, manifest.RootElement.EnumerateObject().Select(x => x.Name).ToArray());
        CollectionAssert.AreEqual(new[] { "modelId", "modelVersion", "dimension", "documentTaskType", "formattingVersion" }, manifest.RootElement.GetProperty("configuration").EnumerateObject().Select(x => x.Name).ToArray());
        Assert.AreEqual("FAKE ONLY - NOT PRODUCTION VECTORS", manifest.RootElement.GetProperty("fakeMarker").GetString());
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(f.Output, "document-vectors.jsonl")))), manifest.RootElement.GetProperty("outputSha256").GetString());
        Assert.AreEqual(3, manifest.RootElement.GetProperty("recordCount").GetInt32());
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f.Catalog))), manifest.RootElement.GetProperty("catalogSha256").GetString());
        Assert.AreEqual(config.ModelVersion, manifest.RootElement.GetProperty("configuration").GetProperty("modelVersion").GetString());
    }

    private static void AssertCheckpointHeader(string path, FakeEmbeddingConfiguration config)
    {
        using var header = JsonDocument.Parse(File.ReadLines(path).First());
        Assert.AreEqual("FAKE ONLY - NOT PRODUCTION VECTORS", header.RootElement.GetProperty("fakeMarker").GetString());
        Assert.AreEqual(config.ModelId, header.RootElement.GetProperty("configuration").GetProperty("modelId").GetString());
    }
}
