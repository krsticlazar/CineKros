using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CineKros.E5.Generator;
using CineKros.Embedding;
using CineKros.Catalog.Importer;

namespace CineKros.E5.Generator.Tests;

[TestClass]
public sealed class GeneratorTests
{
    [TestMethod]
    public void Run_BatchesPendingDocumentsAndReusesMetadataOnlyChanges()
    {
        using var f = new Fixture(); var source = new FakeSource();
        var run = f.Run(source, batchSize: 2);
        Assert.AreEqual(3, run.Generated);
        CollectionAssert.AreEqual(new[] { 2, 1 }, source.BatchSizes.ToArray());
        var old = File.ReadAllBytes(Path.Combine(f.Output, "document-vectors.jsonl"));
        using (var row = JsonDocument.Parse(File.ReadLines(Path.Combine(f.Output, "document-vectors.jsonl")).First()))
        {
            Assert.AreEqual(1L, row.RootElement.GetProperty("movieLensId").GetInt64());
            Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("semantic 1"))), row.RootElement.GetProperty("semanticTextSha256").GetString());
            CollectionAssert.AreEqual(new[] { "movieLensId", "fingerprint", "semanticTextSha256", "vector" }, row.RootElement.EnumerateObject().Select(x => x.Name).ToArray());
        }
        using (var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(f.Output, "manifest.json"))))
        {
            Assert.AreEqual("e5-document-vectors-jsonl-v1", manifest.RootElement.GetProperty("format").GetString());
            Assert.AreEqual("sha256-compact-json-e5-v1", manifest.RootElement.GetProperty("fingerprintAlgorithm").GetString());
            Assert.AreEqual(3, manifest.RootElement.GetProperty("recordCount").GetInt32());
            Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(old)), manifest.RootElement.GetProperty("outputSha256").GetString());
        }
        f.ChangeMetadata(); source.BatchSizes.Clear();
        run = f.Run(source, batchSize: 2, output: f.SecondOutput);
        Assert.AreEqual(0, run.Generated); Assert.AreEqual(3, run.Reused);
        Assert.AreEqual(0, source.BatchSizes.Count);
        Assert.IsTrue(old.SequenceEqual(File.ReadAllBytes(Path.Combine(f.SecondOutput, "document-vectors.jsonl"))));
        using var updatedManifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(f.SecondOutput, "manifest.json")));
        Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f.Catalog))), updatedManifest.RootElement.GetProperty("catalogSha256").GetString());
    }

    [TestMethod]
    public void Run_SelectiveResumeRepairsTornRowsAndRejectsIncompatibleVectors()
    {
        using var f = new Fixture(); var initial = new FakeSource(); f.Run(initial, 3);
        var lines = File.ReadAllLines(f.Checkpoint);
        var damaged = JsonNode.Parse(lines[1])!; damaged["vector"] = new JsonArray(0.0, 0.0); lines[1] = damaged.ToJsonString();
        File.WriteAllText(f.Checkpoint, string.Join("\n", lines.Take(3)) + "\n{torn");
        var retry = new FakeSource(); var result = f.Run(retry, 2, output: f.SecondOutput);
        Assert.AreEqual(2, result.Generated); Assert.AreEqual(1, result.Reused);
        CollectionAssert.AreEqual(new[] { 2 }, retry.BatchSizes.ToArray());
        Assert.IsTrue(File.ReadAllText(f.Checkpoint).EndsWith("\n", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Run_CancellationPersistsCompletedBatchAndDoesNotPublishPartialOutput()
    {
        using var f = new Fixture(); using var cts = new CancellationTokenSource();
        var source = new FakeSource(afterBatch: cts.Cancel);
        Assert.ThrowsExactly<OperationCanceledException>(() => f.Run(source, 1, cts.Token));
        Assert.IsFalse(Directory.Exists(f.Output));
        var resumed = f.Run(new FakeSource(), 2);
        Assert.AreEqual(2, resumed.Generated); Assert.AreEqual(1, resumed.Reused);
    }

    [TestMethod]
    public void Run_FailedBatchResumesPriorSuccessAndRepairsCorruptHeader()
    {
        using var f = new Fixture();
        Assert.ThrowsExactly<IOException>(() => f.Run(new FailSecondBatchSource(), 1));
        Assert.IsFalse(Directory.Exists(f.Output));
        var lines = File.ReadAllLines(f.Checkpoint); lines[0] = "broken header";
        File.WriteAllText(f.Checkpoint, string.Join("\n", lines) + "\n");
        var resumed = f.Run(new FakeSource(), 2);
        Assert.AreEqual(2, resumed.Generated); Assert.AreEqual(1, resumed.Reused);
        using var header = JsonDocument.Parse(File.ReadLines(f.Checkpoint).First());
        Assert.AreEqual("e5-document-checkpoint-v1", header.RootElement.GetProperty("format").GetString());
    }

    [TestMethod]
    public void Run_DiscardsRowsWhenCheckpointModelIdentityIsAltered()
    {
        using var f = new Fixture(); f.Run(new FakeSource(), 3);
        var lines = File.ReadAllLines(f.Checkpoint);
        var header = JsonNode.Parse(lines[0])!;
        header["modelRevision"] = "wrong-revision";
        header["onnxSha256"] = new string('1', 64);
        header["tokenizerSha256"] = new string('2', 64);
        header["dimension"] = 512;
        header["documentInputFormat"] = "wrong-task-format";
        lines[0] = header.ToJsonString(); File.WriteAllText(f.Checkpoint, string.Join("\n", lines) + "\n");

        var source = new FakeSource(); var result = f.Run(source, 2, output: f.SecondOutput);
        Assert.AreEqual(3, result.Generated); Assert.AreEqual(0, result.Reused);
        CollectionAssert.AreEqual(new[] { 2, 1 }, source.BatchSizes.ToArray());
        using var repaired = JsonDocument.Parse(File.ReadLines(f.Checkpoint).First());
        Assert.AreEqual(E5EmbeddingModel.Revision, repaired.RootElement.GetProperty("modelRevision").GetString());
        Assert.AreEqual(768, repaired.RootElement.GetProperty("dimension").GetInt32());
        Assert.AreEqual("e5-passage-semantictext-v1", repaired.RootElement.GetProperty("documentInputFormat").GetString());
    }

    [TestMethod]
    public void Run_RegeneratesCheckpointRowWhenSemanticTextHashIsWrong()
    {
        using var f = new Fixture(); f.Run(new FakeSource(), 3);
        var lines = File.ReadAllLines(f.Checkpoint);
        var row = JsonNode.Parse(lines[1])!; row["semanticTextSha256"] = new string('f', 64);
        lines[1] = row.ToJsonString(); File.WriteAllText(f.Checkpoint, string.Join("\n", lines) + "\n");

        var source = new FakeSource(); var result = f.Run(source, 2, output: f.SecondOutput);
        Assert.AreEqual(1, result.Generated); Assert.AreEqual(2, result.Reused);
        CollectionAssert.AreEqual(new[] { 1 }, source.BatchSizes.ToArray());
        var repaired = File.ReadLines(f.Checkpoint).Skip(1).Select(line => JsonDocument.Parse(line)).Single(document => document.RootElement.GetProperty("movieLensId").GetInt64() == 1);
        using (repaired)
            Assert.AreEqual(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("semantic 1"))), repaired.RootElement.GetProperty("semanticTextSha256").GetString());
    }

    [TestMethod]
    public void Run_CorruptHeaderSalvagesOnlyCompatibleRowsAndRewritesBeforeAppending()
    {
        using var f = new Fixture(); f.Run(new FakeSource(), 3);
        var lines = File.ReadAllLines(f.Checkpoint);
        var incompatible = JsonNode.Parse(lines[1])!; incompatible["fingerprint"] = new string('0', 64);
        lines[0] = "broken header"; lines[1] = incompatible.ToJsonString();
        File.WriteAllText(f.Checkpoint, string.Join("\n", lines) + "\n");

        var source = new FakeSource(); var result = f.Run(source, 2, output: f.SecondOutput);
        Assert.AreEqual(1, result.Generated); Assert.AreEqual(2, result.Reused);
        CollectionAssert.AreEqual(new[] { 1 }, source.BatchSizes.ToArray());
        var repairedLines = File.ReadAllLines(f.Checkpoint);
        Assert.AreEqual(4, repairedLines.Length);
        using var repairedHeader = JsonDocument.Parse(repairedLines[0]);
        Assert.AreEqual("e5-document-checkpoint-v1", repairedHeader.RootElement.GetProperty("format").GetString());
        foreach (var line in repairedLines) using (JsonDocument.Parse(line)) { }
    }

    [TestMethod]
    public void Run_RecoversValidRowAfterTornCheckpointEntry()
    {
        using var f = new Fixture(); f.Run(new FakeSource(), 3);
        var lines = File.ReadAllLines(f.Checkpoint);
        lines[2] = "{torn checkpoint row";
        File.WriteAllText(f.Checkpoint, string.Join("\n", lines) + "\n");

        var source = new FakeSource(); var result = f.Run(source, 2, output: f.SecondOutput);
        Assert.AreEqual(1, result.Generated); Assert.AreEqual(2, result.Reused);
        CollectionAssert.AreEqual(new[] { 1 }, source.BatchSizes.ToArray());
        var recoveredRows = File.ReadLines(f.Checkpoint).Skip(1).Select(line => JsonDocument.Parse(line).RootElement.GetProperty("movieLensId").GetInt64()).Order().ToArray();
        CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, recoveredRows);
    }

    [TestMethod]
    public void Run_RejectsIncompatibleProfileEvenWhenDocumentTextMatches()
    {
        using var f = new Fixture(); f.Run(new FakeSource(), 3);
        var resumed = new FakeSource(profileFingerprint: new string('c', 64));
        var result = f.Run(resumed, 2, output: f.SecondOutput);
        Assert.AreEqual(3, result.Generated); Assert.AreEqual(0, result.Reused);
        CollectionAssert.AreEqual(new[] { 2, 1 }, resumed.BatchSizes.ToArray());
    }

    [TestMethod]
    public void Run_PublishFailureLeavesCheckpointAndNoFinalDirectory()
    {
        using var f = new Fixture(); var checkpointBytes = Array.Empty<byte>();
        Assert.ThrowsExactly<IOException>(() => f.Run(new FakeSource(), 3, beforePublish: () =>
        { checkpointBytes = File.ReadAllBytes(f.Checkpoint); throw new IOException("injected"); }));
        CollectionAssert.AreEqual(checkpointBytes, File.ReadAllBytes(f.Checkpoint));
        Assert.IsFalse(Directory.Exists(f.Output));
    }

    [TestMethod]
    public void CatalogValidation_RejectsUnsortedDuplicateAndWrongFingerprintBeforeInference()
    {
        using var f = new Fixture(); var source = new FakeSource();
        f.WriteCatalog([new(2, "two"), new(1, "one"), new(2, "duplicate")]);
        Assert.ThrowsExactly<InvalidDataException>(() => f.Run(source, 2));
        Assert.AreEqual(0, source.BatchSizes.Count);
        f.WriteCatalog([new(1, "one"), new(2, "two"), new(3, "three")], "0000000000000000000000000000000000000000000000000000000000000000");
        Assert.ThrowsExactly<InvalidDataException>(() => f.Run(source, 2));
        Assert.AreEqual(0, source.BatchSizes.Count);
    }

    [TestMethod]
    public async Task CatalogValidation_AcceptsExactCanonicalB05aArtifactWithoutRunningInference()
    {
        var catalog = FindCanonicalCatalog();
        var document = await CatalogValidator.LoadAsync(catalog);
        Assert.AreEqual(CatalogPolicy.CanonicalCount, document.Movies.Count);
        Assert.AreEqual(CatalogPolicy.CanonicalSha256, document.JsonlHash);
        Assert.AreEqual(CatalogPolicy.CanonicalFingerprint, document.Fingerprint);
    }

    [TestMethod]
    public void Cli_RequiresLocalInputsAndBoundsBatchSize()
    {
        Assert.ThrowsExactly<ArgumentException>(() => GeneratorArguments.Parse(["--catalog", "a"]));
        var parsed = GeneratorArguments.Parse(["--catalog", "catalog", "--manifest", "manifest", "--model-dir", "model", "--output-dir", "out", "--checkpoint", "checkpoint"]);
        Assert.AreEqual(16, parsed.BatchSize);
        Assert.ThrowsExactly<ArgumentException>(() => GeneratorArguments.Parse(["--catalog", "a", "--manifest", "b", "--model-dir", "c", "--output-dir", "d", "--checkpoint", "e", "--batch-size", "0"]));
        Assert.ThrowsExactly<ArgumentException>(() => GeneratorArguments.Parse(["--catalog", "a", "--manifest", "b", "--model-dir", "c", "--output-dir", "d", "--checkpoint", "e", "--api-key", "bad"]));
    }

    [TestMethod]
    public void LocalOnnxFixture_ProducesNormalized768DimensionalDocumentVectorsOffline()
    {
        var modelPath = FindModelDirectory();
        using var model = new E5EmbeddingModel(modelPath);
        var vectors = model.EmbedDocuments(["a small local fixture", "a second local fixture"]);
        Assert.AreEqual(2, vectors.Length);
        foreach (var vector in vectors)
        {
            Assert.AreEqual(768, vector.Length);
            Assert.IsTrue(vector.All(float.IsFinite));
            var norm = Math.Sqrt(vector.Sum(value => (double)value * value));
            Assert.AreEqual(1d, norm, 0.001);
        }
        Assert.AreEqual(1, model.InferenceRunCount);
        Assert.AreEqual(2, model.LastInferenceBatchSize);
    }

    private static string FindModelDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "database", "data", "models", "e5-base-v2", E5EmbeddingModel.Revision);
            if (File.Exists(Path.Combine(candidate, "tokenizer.json"))) return candidate;
        }
        Assert.Inconclusive("Pinned local ONNX fixture is not installed in this workspace.");
        return string.Empty;
    }

    private static string FindCanonicalCatalog()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "database", "data", "derived", "final", "b05a-real-tmdb-01", "movies-catalog.jsonl");
            if (File.Exists(candidate)) return candidate;
        }
        Assert.Inconclusive("Reviewed canonical B05-A catalog is not installed in this workspace.");
        return string.Empty;
    }

    private sealed class FakeSource(Action? afterBatch = null, string? profileFingerprint = null) : IDocumentVectorSource
    {
        public List<int> BatchSizes { get; } = [];
        public IReadOnlyList<float[]> Embed(IReadOnlyList<string> semanticTexts, CancellationToken cancellationToken)
        {
            BatchSizes.Add(semanticTexts.Count); afterBatch?.Invoke();
            return semanticTexts.Select(_ => UnitVector()).ToArray();
        }
        public string Fingerprint(string semanticText) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("fingerprint:" + semanticText)));
        public string ProfileFingerprint => profileFingerprint ?? new string('a', 64);
        public long TruncationCount => 0;
        private static float[] UnitVector() { var result = new float[768]; result[0] = 1f; return result; }
    }

    private sealed class FailSecondBatchSource : IDocumentVectorSource
    {
        private int calls;
        private readonly FakeSource inner = new();
        public IReadOnlyList<float[]> Embed(IReadOnlyList<string> semanticTexts, CancellationToken cancellationToken)
        {
            if (++calls == 2) throw new IOException("injected inference failure");
            return inner.Embed(semanticTexts, cancellationToken);
        }
        public string Fingerprint(string semanticText) => inner.Fingerprint(semanticText);
        public string ProfileFingerprint => inner.ProfileFingerprint;
        public long TruncationCount => inner.TruncationCount;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "e5-gen-test-" + Guid.NewGuid().ToString("N"));
        public string Catalog => Path.Combine(root, "movies.jsonl");
        public string Manifest => Path.Combine(root, "manifest.json");
        public string Checkpoint => Path.Combine(root, "checkpoint.jsonl");
        public string Output => Path.Combine(root, "output");
        public string SecondOutput => Path.Combine(root, "output-2");
        public Fixture() { Directory.CreateDirectory(root); WriteCatalog([new(1, "semantic 1"), new(2, "semantic 2"), new(3, "semantic 3")]); }
        public RunResult Run(IDocumentVectorSource source, int batchSize, CancellationToken token = default, Action? beforePublish = null, string? output = null) =>
            DocumentVectorGenerator.Run(Catalog, Manifest, Checkpoint, output ?? Output, batchSize, source, CatalogPolicy.ForTests(3), token, beforePublish);
        public void ChangeMetadata()
        {
            var rows = File.ReadAllLines(Catalog).Select(line => JsonNode.Parse(line)).ToArray(); rows[0]!["title"] = "changed";
            WriteCatalogRows(rows.Select(x => x!.ToJsonString()).ToArray());
        }
        public void WriteCatalog(CatalogItem[] rows, string? fingerprint = null) =>
            WriteCatalogRows(rows.Select(x => JsonSerializer.Serialize(new { movieLensId = x.Id, semanticText = x.Text, title = "title" })).ToArray(), fingerprint);
        private void WriteCatalogRows(string[] lines, string? fingerprint = null)
        {
            File.WriteAllText(Catalog, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
            var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Catalog)));
            File.WriteAllText(Manifest, JsonSerializer.Serialize(new { catalogVersion = CatalogPolicy.VersionValue, contentFingerprint = fingerprint ?? new string('b', 64), output = new { sha256 = hash, recordCount = lines.Length, order = "movieLensId ascending" } }));
        }
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed record CatalogItem(long Id, string Text);
}
