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
    public void Run_ExplicitMultilingualProfileRequiresBatchOneAndWritesItsShapeIdentity()
    {
        using var f = new Fixture();
        var source = new FakeSource(profileDescriptor: EmbeddingProfileDescriptor.MultilingualE5Base);
        Assert.ThrowsExactly<ArgumentException>(() => f.Run(source, 2));
        Assert.AreEqual(0, source.BatchSizes.Count);
        var run = f.Run(source, 1);
        Assert.AreEqual(3, run.Generated);
        CollectionAssert.AreEqual(new[] { 1, 1, 1 }, source.BatchSizes.ToArray());
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(f.Output, "manifest.json")));
        Assert.AreEqual("multilingual-e5-base-int8-onnx-v1", manifest.RootElement.GetProperty("profile").GetString());
        Assert.AreEqual(EmbeddingProfileDescriptor.MultilingualE5Base.ProfileFingerprint, manifest.RootElement.GetProperty("profileFingerprint").GetString());
        Assert.AreEqual("single-sequence-unpadded-v1", manifest.RootElement.GetProperty("inferenceShapePolicy").GetString());
        using var checkpointHeader = JsonDocument.Parse(File.ReadLines(f.Checkpoint).First());
        Assert.AreEqual("single-sequence-unpadded-v1", checkpointHeader.RootElement.GetProperty("inferenceShapePolicy").GetString());
    }

    [TestMethod]
    public async Task Run_PairedPocUsesSeparateLanguageJournalsAndReplaysAllCompatibleRowsWithoutInference()
    {
        var root = Environment.GetEnvironmentVariable("CINEKROS_POC_ROOT");
        if (string.IsNullOrWhiteSpace(root) || !File.Exists(Path.Combine(root, "database/data/derived/serbian-search/poc-v1/catalog/movies-catalog.jsonl")))
            Assert.Inconclusive("Frozen Serbian-search POC inputs are not installed for this test run.");
        var poc = Path.Combine(root!, "database/data/derived/serbian-search/poc-v1");
        var catalog = await MultilingualPocCatalog.LoadAsync(Path.Combine(poc, "catalog/movies-catalog.jsonl"), Path.Combine(poc, "catalog/manifest.json"),
            Path.Combine(poc, "translation/tag-translations-sr.json"), Path.Combine(root!, "database/data/derived/final/b05a-real-tmdb-01/movies-catalog.jsonl"));
        using var temp = new TempRoot();
        var source = new FakeSource(profileDescriptor: EmbeddingProfileDescriptor.MultilingualE5Base);
        var enCheckpoint = Path.Combine(temp.Root, "en", "checkpoint.jsonl");
        var srCheckpoint = Path.Combine(temp.Root, "sr", "checkpoint.jsonl");
        var en = DocumentVectorGenerator.RunMultilingualPoc(enCheckpoint, Path.Combine(temp.Root, "en-artifact"), 1, source, catalog, "en");
        Assert.AreEqual(150, en.Generated); Assert.AreEqual(0, en.Reused); Assert.AreEqual(150, en.RecordCount);
        CollectionAssert.AreEqual(Enumerable.Repeat(1, 150).ToArray(), source.BatchSizes.ToArray());
        source.BatchSizes.Clear();
        var enReplay = DocumentVectorGenerator.RunMultilingualPoc(enCheckpoint, Path.Combine(temp.Root, "en-replay"), 1, source, catalog, "en");
        Assert.AreEqual(0, enReplay.Generated); Assert.AreEqual(150, enReplay.Reused); Assert.AreEqual(0, source.BatchSizes.Count);
        var sr = DocumentVectorGenerator.RunMultilingualPoc(srCheckpoint, Path.Combine(temp.Root, "sr-artifact"), 1, source, catalog, "sr");
        Assert.AreEqual(150, sr.Generated); Assert.AreEqual(0, sr.Reused); Assert.AreEqual(150, sr.RecordCount);
        CollectionAssert.AreEqual(Enumerable.Repeat(1, 150).ToArray(), source.BatchSizes.ToArray());
        var enRow = JsonDocument.Parse(File.ReadLines(Path.Combine(temp.Root, "en-artifact", "document-vectors.jsonl")).First()).RootElement;
        var srRow = JsonDocument.Parse(File.ReadLines(Path.Combine(temp.Root, "sr-artifact", "document-vectors.jsonl")).First()).RootElement;
        Assert.AreNotEqual(enRow.GetProperty("fingerprint").GetString(), srRow.GetProperty("fingerprint").GetString());
        source.BatchSizes.Clear();
        var srReplay = DocumentVectorGenerator.RunMultilingualPoc(srCheckpoint, Path.Combine(temp.Root, "sr-replay"), 1, source, catalog, "sr");
        Assert.AreEqual(0, srReplay.Generated); Assert.AreEqual(150, srReplay.Reused); Assert.AreEqual(0, source.BatchSizes.Count);

        var failedCheckpoint = Path.Combine(temp.Root, "publish-failure", "checkpoint.jsonl");
        Assert.ThrowsExactly<IOException>(() => DocumentVectorGenerator.RunMultilingualPoc(failedCheckpoint, Path.Combine(temp.Root, "publish-failure", "artifact"),
            1, source, catalog, "en", beforePublish: () => throw new IOException("injected publish failure")));
        Assert.IsFalse(Directory.Exists(Path.Combine(temp.Root, "publish-failure", "artifact")));
        source.BatchSizes.Clear();
        var afterFailedPublish = DocumentVectorGenerator.RunMultilingualPoc(failedCheckpoint, Path.Combine(temp.Root, "publish-failure", "retry"), 1, source, catalog, "en");
        Assert.AreEqual(0, afterFailedPublish.Generated); Assert.AreEqual(150, afterFailedPublish.Reused); Assert.AreEqual(0, source.BatchSizes.Count);
    }

    [TestMethod]
    public async Task Run_PocResumeRepairsTornTailAndRejectsChangedIdentityFingerprintAndHeader()
    {
        var root = Environment.GetEnvironmentVariable("CINEKROS_POC_ROOT");
        if (string.IsNullOrWhiteSpace(root) || !File.Exists(Path.Combine(root, "database/data/derived/serbian-search/poc-v1/catalog/movies-catalog.jsonl")))
            Assert.Inconclusive("Frozen Serbian-search POC inputs are not installed for this test run.");
        var poc = Path.Combine(root!, "database/data/derived/serbian-search/poc-v1");
        var catalog = await MultilingualPocCatalog.LoadAsync(Path.Combine(poc, "catalog/movies-catalog.jsonl"), Path.Combine(poc, "catalog/manifest.json"),
            Path.Combine(poc, "translation/tag-translations-sr.json"), Path.Combine(root!, "database/data/derived/final/b05a-real-tmdb-01/movies-catalog.jsonl"));
        using var temp = new TempRoot();
        var source = new FakeSource(profileDescriptor: EmbeddingProfileDescriptor.MultilingualE5Base);
        var invalidOutput = Path.Combine(temp.Root, "existing-output"); Directory.CreateDirectory(invalidOutput);
        File.WriteAllText(Path.Combine(invalidOutput, "preserve.txt"), "existing user content");
        Assert.ThrowsExactly<IOException>(() => DocumentVectorGenerator.RunMultilingualPoc(Path.Combine(temp.Root, "unused-checkpoint.jsonl"),
            invalidOutput, 1, source, catalog, "en"));
        Assert.AreEqual(0, source.BatchSizes.Count, "An invalid existing output target must be rejected before any document inference.");
        var checkpoint = Path.Combine(temp.Root, "en", "checkpoint.jsonl");
        var initial = DocumentVectorGenerator.RunMultilingualPoc(checkpoint, Path.Combine(temp.Root, "initial"), 1, source, catalog, "en");
        Assert.AreEqual(150, initial.Generated);

        var checkpointLines = File.ReadAllLines(checkpoint).ToList(); checkpointLines.Add("{torn tail"); File.WriteAllLines(checkpoint, checkpointLines);
        source.BatchSizes.Clear();
        var tailReplay = DocumentVectorGenerator.RunMultilingualPoc(checkpoint, Path.Combine(temp.Root, "tail-replay"), 1, source, catalog, "en");
        Assert.AreEqual(0, tailReplay.Generated); Assert.AreEqual(150, tailReplay.Reused); Assert.AreEqual(0, source.BatchSizes.Count);
        Assert.AreEqual(151, File.ReadAllLines(checkpoint).Length);

        using (var append = new FileStream(checkpoint, FileMode.Append, FileAccess.Write, FileShare.None))
            append.Write([0x7B, 0x22, 0x74, 0x61, 0x69, 0x6C, 0x22, 0x3A, 0xC3]);
        var invalidUtf8Checkpoint = File.ReadAllBytes(checkpoint);
        var backupCountBefore = Directory.GetFiles(Path.GetDirectoryName(checkpoint)!, Path.GetFileName(checkpoint) + ".backup-*").Length;
        source.BatchSizes.Clear();
        var invalidUtf8Tail = DocumentVectorGenerator.RunMultilingualPoc(checkpoint, Path.Combine(temp.Root, "invalid-utf8-tail"), 1, source, catalog, "en");
        Assert.AreEqual(0, invalidUtf8Tail.Generated); Assert.AreEqual(150, invalidUtf8Tail.Reused); Assert.AreEqual(0, source.BatchSizes.Count);
        var backups = Directory.GetFiles(Path.GetDirectoryName(checkpoint)!, Path.GetFileName(checkpoint) + ".backup-*");
        Assert.AreEqual(backupCountBefore + 1, backups.Length, "Corrupt source journal must be preserved before repair.");
        CollectionAssert.AreEqual(invalidUtf8Checkpoint, File.ReadAllBytes(backups.OrderBy(File.GetCreationTimeUtc).Last()));
        Assert.AreEqual(151, File.ReadAllLines(checkpoint).Length);

        checkpointLines = File.ReadAllLines(checkpoint).ToList(); var header = JsonNode.Parse(checkpointLines[0])!;
        header["catalogIdentitySha256"] = new string('0', 64); checkpointLines[0] = header.ToJsonString(); File.WriteAllLines(checkpoint, checkpointLines);
        var incompatibleHeaderBytes = File.ReadAllBytes(checkpoint);
        backupCountBefore = Directory.GetFiles(Path.GetDirectoryName(checkpoint)!, Path.GetFileName(checkpoint) + ".backup-*").Length;
        var changedIdentity = DocumentVectorGenerator.RunMultilingualPoc(checkpoint, Path.Combine(temp.Root, "identity-changed"), 1, source, catalog, "en");
        Assert.AreEqual(150, changedIdentity.Generated); Assert.AreEqual(0, changedIdentity.Reused);
        backups = Directory.GetFiles(Path.GetDirectoryName(checkpoint)!, Path.GetFileName(checkpoint) + ".backup-*");
        Assert.AreEqual(backupCountBefore + 1, backups.Length);
        CollectionAssert.AreEqual(incompatibleHeaderBytes, File.ReadAllBytes(backups.OrderBy(File.GetCreationTimeUtc).Last()));

        checkpointLines = File.ReadAllLines(checkpoint).ToList(); var row = JsonNode.Parse(checkpointLines[1])!;
        row["fingerprint"] = new string('f', 64); checkpointLines[1] = row.ToJsonString(); File.WriteAllLines(checkpoint, checkpointLines);
        var incompatibleRowBytes = File.ReadAllBytes(checkpoint);
        backupCountBefore = backups.Length;
        source.BatchSizes.Clear();
        var changedDocument = DocumentVectorGenerator.RunMultilingualPoc(checkpoint, Path.Combine(temp.Root, "fingerprint-changed"), 1, source, catalog, "en");
        Assert.AreEqual(1, changedDocument.Generated); Assert.AreEqual(149, changedDocument.Reused);
        CollectionAssert.AreEqual(new[] { 1 }, source.BatchSizes.ToArray());
        backups = Directory.GetFiles(Path.GetDirectoryName(checkpoint)!, Path.GetFileName(checkpoint) + ".backup-*");
        Assert.AreEqual(backupCountBefore + 1, backups.Length);
        CollectionAssert.AreEqual(incompatibleRowBytes, File.ReadAllBytes(backups.OrderBy(File.GetCreationTimeUtc).Last()));

        checkpointLines = File.ReadAllLines(checkpoint).ToList(); checkpointLines[0] = "broken checkpoint header"; File.WriteAllLines(checkpoint, checkpointLines);
        var corruptHeaderBytes = File.ReadAllBytes(checkpoint);
        backupCountBefore = backups.Length;
        source.BatchSizes.Clear();
        var corruptHeader = DocumentVectorGenerator.RunMultilingualPoc(checkpoint, Path.Combine(temp.Root, "corrupt-header"), 1, source, catalog, "en");
        Assert.AreEqual(150, corruptHeader.Generated); Assert.AreEqual(0, corruptHeader.Reused);
        backups = Directory.GetFiles(Path.GetDirectoryName(checkpoint)!, Path.GetFileName(checkpoint) + ".backup-*");
        Assert.AreEqual(backupCountBefore + 1, backups.Length);
        CollectionAssert.AreEqual(corruptHeaderBytes, File.ReadAllBytes(backups.OrderBy(File.GetCreationTimeUtc).Last()));
    }

    [TestMethod]
    public void Run_FullCatalogUsesIndependentIdentityBoundCheckpointsAndReplaysWithoutInference()
    {
        using var temp = new TempRoot();
        var movies = new[]
        {
            new FullBilingualMovie(new MovieRow(1, "tt0000001", 1, "One", 2001, null, "en", ["Drama"], null, null, null), "One, 2001", "Један, 2001", ["Драма"]),
            new FullBilingualMovie(new MovieRow(2, "tt0000002", 2, "Two", 2002, null, "en", ["Mystery"], null, null, null), "Two, 2002", "Два, 2002", ["Мистерија"])
        };
        var catalog = new FullBilingualCatalogDocument(Path.Combine(temp.Root, "catalog.jsonl"), new string('a', 64),
            Path.Combine(temp.Root, "source.jsonl"), new string('b', 64), new string('c', 64), Path.Combine(temp.Root, "dictionary.json"),
            new string('d', 64), new string('e', 64), new string('f', 64), new string('1', 64), new string('2', 64), "{}", movies);
        var source = new FakeSource(profileDescriptor: EmbeddingProfileDescriptor.MultilingualE5Base);
        var enCheckpoint = Path.Combine(temp.Root, "en", "checkpoint.jsonl");
        var srCheckpoint = Path.Combine(temp.Root, "sr", "checkpoint.jsonl");
        var enOutput = Path.Combine(temp.Root, "en", "published");
        var srOutput = Path.Combine(temp.Root, "sr", "published");
        var en = DocumentVectorGenerator.RunFullCatalog(enCheckpoint, enOutput, 1, source, catalog, "en");
        Assert.AreEqual(2, en.Generated); Assert.AreEqual(2, en.RecordCount);
        using var enHeader = JsonDocument.Parse(File.ReadLines(enCheckpoint).First());
        Assert.AreEqual("multilingual-full-document-checkpoint-v1", enHeader.RootElement.GetProperty("format").GetString());
        Assert.AreEqual(catalog.CatalogSha256, enHeader.RootElement.GetProperty("catalogSha256").GetString());
        Assert.AreEqual(catalog.DictionarySha256, enHeader.RootElement.GetProperty("dictionarySha256").GetString());
        var enArtifactPath = Path.Combine(enOutput, "document-vectors.jsonl");
        var enArtifactBytes = File.ReadAllBytes(enArtifactPath);
        source.BatchSizes.Clear();
        var completedEn = DocumentVectorGenerator.RunFullCatalog(enCheckpoint, enOutput, 1, source, catalog, "en");
        Assert.AreEqual(0, completedEn.Generated); Assert.AreEqual(2, completedEn.Reused); Assert.AreEqual(0, source.BatchSizes.Count);
        CollectionAssert.AreEqual(enArtifactBytes, File.ReadAllBytes(enArtifactPath), "A verified completion is preserved byte-for-byte on paired resume.");
        Assert.ThrowsExactly<InvalidDataException>(() => DocumentVectorGenerator.ValidatePublishedFullCatalog(catalog with { IdentitySha256 = new string('3', 64) }, "en", enOutput));
        File.WriteAllBytes(enArtifactPath, [.. enArtifactBytes, 0x20]);
        Assert.ThrowsExactly<InvalidDataException>(() => DocumentVectorGenerator.ValidatePublishedFullCatalog(catalog, "en", enOutput),
            "A modified already-published artifact must be rejected, not silently resumed or replaced.");
        File.WriteAllBytes(enArtifactPath, enArtifactBytes);
        var enManifestPath = Path.Combine(enOutput, "manifest.json");
        var manifestBytes = File.ReadAllBytes(enManifestPath);
        foreach (var mutation in new Action<JsonObject>[]
        {
            root => root["translationDictionarySha256"] = new string('4', 64),
            root => root["inputNormalizationVersion"] = "wrong-normalizer",
            root => root["truncatedRecords"] = 1
        })
        {
            var changedManifest = JsonNode.Parse(manifestBytes)!.AsObject(); mutation(changedManifest);
            File.WriteAllBytes(enManifestPath, Encoding.UTF8.GetBytes(changedManifest.ToJsonString()));
            Assert.ThrowsExactly<InvalidDataException>(() => DocumentVectorGenerator.ValidatePublishedFullCatalog(catalog, "en", enOutput),
                "Every full manifest identity/processing field must match even when vector bytes are unchanged.");
        }
        File.WriteAllBytes(enManifestPath, manifestBytes);
        source.BatchSizes.Clear();
        var sr = DocumentVectorGenerator.RunFullCatalog(srCheckpoint, srOutput, 1, source, catalog, "sr");
        Assert.AreEqual(2, sr.Generated); Assert.AreEqual(2, sr.RecordCount);
        CollectionAssert.AreEqual(new[] { 1, 1 }, source.BatchSizes.ToArray(), "After the EN publication, resume computes only the pending SR language.");
        source.BatchSizes.Clear();
        var enReplay = DocumentVectorGenerator.RunFullCatalog(enCheckpoint, Path.Combine(temp.Root, "en-replay"), 1, source, catalog, "en");
        Assert.AreEqual(0, enReplay.Generated); Assert.AreEqual(2, enReplay.Reused); Assert.AreEqual(0, source.BatchSizes.Count);
        var enRow = JsonDocument.Parse(File.ReadLines(Path.Combine(enOutput, "document-vectors.jsonl")).First()).RootElement;
        var srRow = JsonDocument.Parse(File.ReadLines(Path.Combine(srOutput, "document-vectors.jsonl")).First()).RootElement;
        Assert.AreNotEqual(enRow.GetProperty("fingerprint").GetString(), srRow.GetProperty("fingerprint").GetString());
    }

    [TestMethod]
    public async Task PublishedFullCatalogSample16ReproducesStoredVectorsWithOneFreshEncoder_WhenExplicitRootsAreSet()
    {
        var workspaceRoot = Environment.GetEnvironmentVariable("CINEKROS_P8_FULLCATALOG_ROOT");
        var vectorRoot = Environment.GetEnvironmentVariable("CINEKROS_P8_VECTOR_ROOT");
        var modelRoot = Environment.GetEnvironmentVariable("CINEKROS_P8_MODEL_ROOT");
        if (string.IsNullOrWhiteSpace(workspaceRoot) && string.IsNullOrWhiteSpace(vectorRoot) && string.IsNullOrWhiteSpace(modelRoot))
            Assert.Inconclusive("Set all three CINEKROS_P8_*_ROOT variables to run read-only full-catalog sample reproduction.");
        if (string.IsNullOrWhiteSpace(workspaceRoot) || string.IsNullOrWhiteSpace(vectorRoot) || string.IsNullOrWhiteSpace(modelRoot) ||
            !Path.IsPathFullyQualified(workspaceRoot) || !Path.IsPathFullyQualified(vectorRoot) || !Path.IsPathFullyQualified(modelRoot))
            Assert.Fail("All three CINEKROS_P8_*_ROOT values must be explicit absolute paths.");

        var root = Path.GetFullPath(workspaceRoot);
        var vectors = Path.GetFullPath(vectorRoot);
        var modelPath = Path.GetFullPath(modelRoot);
        var catalogPath = Path.Combine(root, "database", "data", "derived", "final", "sr-search-v1", "sr-p8-full-01", "catalog", "movies-catalog.jsonl");
        var catalogManifest = Path.Combine(Path.GetDirectoryName(catalogPath)!, "manifest.json");
        var dictionary = Path.Combine(root, "database", "data", "derived", "translations", "sr-latn-v1-r1", "tag-translations-sr.json");
        var sourceCatalog = Path.Combine(root, "database", "data", "derived", "final", "b05a-real-tmdb-01", "movies-catalog.jsonl");
        var catalog = await FullBilingualCatalog.LoadAsync(catalogPath, catalogManifest, dictionary, sourceCatalog);
        var expectedProfile = EmbeddingProfileDescriptor.MultilingualE5Base;
        Assert.AreEqual(9730, catalog.Movies.Count);

        foreach (var language in new[] { "en", "sr" })
        {
            var validated = DocumentVectorGenerator.ValidatePublishedFullCatalog(catalog, language, Path.Combine(vectors, language, "published"));
            Assert.IsNotNull(validated, $"The {language} full artifact must already be complete and validated.");
            Assert.AreEqual(9730, validated.RecordCount);
        }

        var sampleIds = new long[] { 1, 2, 1083, 2754, 5539, 31878, 107348, 108932 };
        var movies = catalog.Movies.ToDictionary(x => x.MovieLensId);
        var cases = new List<SampleVectorComparison>(16);
        var artifactHashes = new Dictionary<string, object>(StringComparer.Ordinal);
        using var model = new E5EmbeddingModel(modelPath, expectedProfile);
        Assert.AreEqual(expectedProfile.ProfileFingerprint, model.ProfileFingerprint);
        foreach (var language in new[] { "en", "sr" })
        {
            var artifactPath = Path.Combine(vectors, language, "published", "document-vectors.jsonl");
            var manifestPath = Path.Combine(vectors, language, "published", "manifest.json");
            artifactHashes[language] = new
            {
                vectorsSha256 = HashFile(artifactPath), manifestSha256 = HashFile(manifestPath)
            };
            var wanted = sampleIds.ToHashSet();
            var rows = new Dictionary<long, SampleVectorRow>();
            foreach (var line in File.ReadLines(artifactPath, new UTF8Encoding(false, true)))
            {
                using var rowJson = JsonDocument.Parse(line);
                var id = rowJson.RootElement.GetProperty("movieLensId").GetInt64();
                if (!wanted.Contains(id)) continue;
                var row = JsonSerializer.Deserialize<SampleVectorRow>(line,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Malformed published vector row.");
                if (!rows.TryAdd(id, row)) throw new InvalidDataException($"Duplicate sampled vector ID {id}.");
            }
            Assert.AreEqual(sampleIds.Length, rows.Count, $"All fixed {language} sample IDs must exist in the artifact.");
            foreach (var id in sampleIds)
            {
                var movie = movies[id];
                var text = language == "en" ? movie.SemanticText : movie.SemanticTextSr;
                var row = rows[id];
                var fingerprint = MultilingualPocCatalog.ComputeDocumentFingerprint(language,
                    language == "en" ? catalog.EnTextFormatVersion : catalog.SrTextFormatVersion, text);
                var textHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
                Assert.AreEqual(fingerprint, row.Fingerprint, $"{language}/{id} fingerprint");
                Assert.AreEqual(textHash, row.SemanticTextSha256, $"{language}/{id} semantic text hash");
                var reproduced = model.EmbedDocuments([text])[0];
                var differing = new List<int>(); var maxAbs = 0d; double dot = 0, leftNorm = 0, rightNorm = 0;
                for (var dimension = 0; dimension < reproduced.Length; dimension++)
                {
                    if (BitConverter.SingleToInt32Bits(reproduced[dimension]) != BitConverter.SingleToInt32Bits(row.Vector[dimension])) differing.Add(dimension);
                    maxAbs = Math.Max(maxAbs, Math.Abs((double)reproduced[dimension] - row.Vector[dimension]));
                    dot += (double)reproduced[dimension] * row.Vector[dimension];
                    leftNorm += (double)reproduced[dimension] * reproduced[dimension];
                    rightNorm += (double)row.Vector[dimension] * row.Vector[dimension];
                }
                cases.Add(new SampleVectorComparison(language, id, fingerprint, textHash, differing.Count == 0, differing.Count,
                    differing.Take(32).ToArray(), maxAbs, dot / Math.Sqrt(leftNorm * rightNorm), reproduced, row.Vector));
            }
        }

        var report = new
        {
            format = "full-catalog-sample-vector-reproduction-v1", catalogIdentitySha256 = catalog.IdentitySha256,
            catalogSha256 = catalog.CatalogSha256, sourceCatalogSha256 = catalog.SourceCatalogSha256,
            dictionarySha256 = catalog.DictionarySha256, profileFingerprint = model.ProfileFingerprint,
            onnxSha256 = expectedProfile.Artifact("model_qint8_avx512_vnni.onnx").Sha256,
            tokenizerSha256 = expectedProfile.Artifact("tokenizer.json").Sha256, encoderInstances = 1,
            inferenceCalls = model.InferenceRunCount, sampleMovieLensIds = sampleIds,
            artifacts = artifactHashes, languages = new[] { "en", "sr" }, allBitwiseIdentical = cases.All(x => x.BitwiseIdentical), cases
        };
        var reportDirectory = Path.Combine(root, ".local", "planning", "reports", "sr-phase-08", "generation");
        Directory.CreateDirectory(reportDirectory);
        var reportPath = Path.Combine(reportDirectory, "sample-vector-reproduction-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ", System.Globalization.CultureInfo.InvariantCulture) + ".json");
        await File.WriteAllBytesAsync(reportPath, JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { WriteIndented = true }));
        Assert.AreEqual(16, cases.Count);
        Assert.AreEqual(16, model.InferenceRunCount, "Samples are reproduced individually with batch size one on one encoder instance.");
        Assert.IsTrue(cases.All(x => x.BitwiseIdentical), $"Bitwise vector reproduction mismatch; exact differences are persisted at {reportPath}.");
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
        Assert.ThrowsExactly<ArgumentException>(() => GeneratorArguments.Parse(["--catalog", "a", "--manifest", "b", "--model-dir", "c", "--output-dir", "d", "--checkpoint", "e", "--profile", "multilingual-e5-base-int8-onnx-v1", "--batch-size", "1"]));
        Assert.ThrowsExactly<ArgumentException>(() => GeneratorArguments.Parse(["--catalog", "a", "--manifest", "b", "--model-dir", "c", "--output-dir", "d", "--checkpoint", "e", "--profile", "multilingual-e5-base-int8-onnx-v1", "--batch-size", "1", "--language", "xx", "--dictionary", "f", "--source-catalog", "g"]));
        var multilingual = GeneratorArguments.Parse(["--catalog", "a", "--manifest", "b", "--model-dir", "c", "--output-dir", "d", "--checkpoint", "e", "--profile", "multilingual-e5-base-int8-onnx-v1", "--batch-size", "1", "--language", "sr", "--dictionary", "f", "--source-catalog", "g"]);
        Assert.AreEqual("sr", multilingual.Language);
        var full = GeneratorArguments.Parse(["--catalog", "a", "--manifest", "b", "--model-dir", "c", "--output-dir", "d", "--checkpoint", "e", "--profile", "multilingual-e5-base-int8-onnx-v1", "--batch-size", "1", "--language", "en", "--dictionary", "f", "--source-catalog", "g", "--full-catalog", "true"]);
        Assert.IsTrue(full.FullCatalog);
        var pairedFull = GeneratorArguments.Parse(["--catalog", "a", "--manifest", "b", "--model-dir", "c", "--output-dir", "d", "--checkpoint", "e", "--profile", "multilingual-e5-base-int8-onnx-v1", "--batch-size", "1", "--dictionary", "f", "--source-catalog", "g", "--paired-full", "true", "--token-audit-only", "true"]);
        Assert.IsTrue(pairedFull.PairedFull);
        Assert.IsTrue(pairedFull.TokenAuditOnly);
        Assert.ThrowsExactly<ArgumentException>(() => GeneratorArguments.Parse(["--catalog", "a", "--manifest", "b", "--model-dir", "c", "--output-dir", "d", "--checkpoint", "e", "--profile", "multilingual-e5-base-int8-onnx-v1", "--batch-size", "1", "--dictionary", "f", "--source-catalog", "g", "--paired-full", "true", "--language", "en"]));
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

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
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

    private sealed class FakeSource(Action? afterBatch = null, string? profileFingerprint = null, EmbeddingProfileDescriptor? profileDescriptor = null) : IDocumentVectorSource
    {
        public List<int> BatchSizes { get; } = [];
        public IReadOnlyList<float[]> Embed(IReadOnlyList<string> semanticTexts, CancellationToken cancellationToken)
        {
            BatchSizes.Add(semanticTexts.Count); afterBatch?.Invoke();
            return semanticTexts.Select(_ => UnitVector()).ToArray();
        }
        public string Fingerprint(string semanticText) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("fingerprint:" + semanticText)));
        public string ProfileFingerprint => profileFingerprint ?? ProfileDescriptor.ProfileFingerprint;
        public CineKros.Embedding.EmbeddingProfileDescriptor ProfileDescriptor => profileDescriptor ?? CineKros.Embedding.EmbeddingProfileDescriptor.LegacyEnglish;
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
        public CineKros.Embedding.EmbeddingProfileDescriptor ProfileDescriptor => inner.ProfileDescriptor;
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

    private sealed record SampleVectorRow(long MovieLensId, string Fingerprint, string SemanticTextSha256, float[] Vector);
    private sealed record SampleVectorComparison(string Language, long MovieLensId, string Fingerprint, string SemanticTextSha256,
        bool BitwiseIdentical, int DifferingDimensions, int[] FirstDifferingDimensions, double MaxAbsoluteDifference, double Cosine,
        float[] ReproducedVector, float[] PublishedVector);
    private sealed class TempRoot : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "e5-poc-test-" + Guid.NewGuid().ToString("N"));
        public TempRoot() => Directory.CreateDirectory(Root);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private sealed record CatalogItem(long Id, string Text);
}
