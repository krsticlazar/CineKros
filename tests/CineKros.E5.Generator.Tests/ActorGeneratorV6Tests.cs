using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CineKros.Catalog.Importer;
using CineKros.E5.Generator;
using CineKros.Embedding;

namespace CineKros.E5.Generator.Tests;

[TestClass]
public sealed class ActorGeneratorV6Tests
{
    [TestMethod]
    public async Task ExistingP8AcceptedCompactReplayUsesUnchangedWrappersWithoutModelWhenExplicitRootIsSet()
    {
        var root = Environment.GetEnvironmentVariable("CINEKROS_V6_REPOSITORY_ROOT");
        if (string.IsNullOrWhiteSpace(root)) Assert.Inconclusive("Explicit repository root enables bounded read-only legacy compatibility proof.");
        if (!Path.IsPathFullyQualified(root!)) Assert.Fail("Repository root must be absolute.");
        var catalogRoot = Path.Combine(root!, ".local/planning/reports/sr-phase-08/catalog-repro-01/release/catalog");
        var catalog = await FullBilingualCatalog.LoadAsync(Path.Combine(catalogRoot, "movies-catalog.jsonl"), Path.Combine(catalogRoot, "manifest.json"),
            Path.Combine(root!, "database/data/derived/translations/sr-latn-v1-r1/tag-translations-sr.json"), Path.Combine(root!, "database/data/derived/final/b05a-real-tmdb-01/movies-catalog.jsonl"));
        foreach (var language in new[] { "en", "sr" })
        {
            var output = Path.Combine(root!, "database/data/embeddings/multilingual-e5-base-int8-onnx-v1/sr-p8-full-01", language, "published");
            var paths = new[] { Path.Combine(output, "document-vectors.jsonl"), Path.Combine(output, "manifest.json") };
            var before = paths.Select(path => Hash(File.ReadAllBytes(path))).ToArray();
            var result = DocumentVectorGenerator.ValidatePublishedFullCatalog(catalog, language, output);
            Assert.IsNotNull(result); Assert.AreEqual(0, result.Generated); Assert.AreEqual(9730, result.Reused); Assert.AreEqual(9730, result.RecordCount);
            CollectionAssert.AreEqual(before, paths.Select(path => Hash(File.ReadAllBytes(path))).ToArray());
        }
    }

    [TestMethod]
    public void ExplicitV6FlagsRejectOldModesLanguageReuseAndLegacyProfile()
    {
        var args = new[] { "--catalog", "c", "--manifest", "m", "--model-dir", "d", "--output-dir", "o", "--checkpoint", "k",
            "--profile", "multilingual-e5-base-int8-onnx-v1", "--batch-size", "1", "--dictionary", "t", "--source-catalog", "s", "--paired-v6", "true" };
        Assert.IsTrue(GeneratorArguments.Parse(args).PairedV6);
        Assert.IsTrue(GeneratorArguments.Parse([.. args, "--token-audit-only", "true"]).TokenAuditOnly);
        foreach (var pair in new[] { new[] { "--paired-full", "true" }, ["--paired-poc", "true"], ["--full-catalog", "true"], ["--poc-v2-reuse-root", "x"], ["--language", "en"] })
            Assert.ThrowsExactly<ArgumentException>(() => GeneratorArguments.Parse([.. args, .. pair]));
        Assert.ThrowsExactly<ArgumentException>(() => GeneratorArguments.Parse(args.Select(x => x == "multilingual-e5-base-int8-onnx-v1" ? "e5-base-v2-int8-onnx-v1" : x).ToArray()));
        Assert.ThrowsExactly<ArgumentException>(() => GeneratorArguments.Parse(args.Select(x => x == "true" ? "false" : x).ToArray()));
    }

    [TestMethod]
    public void InterruptionTornTailAndResumeReuseEveryCompletedV6Row()
    {
        using var f = new Fixture(); using var cancellation = new CancellationTokenSource();
        var interrupted = new Source(() => cancellation.Cancel());
        Assert.ThrowsExactly<OperationCanceledException>(() => f.Run("en", interrupted, cancellation.Token));
        Assert.AreEqual(2, File.ReadAllLines(f.Checkpoint("en")).Length);
        var original = File.ReadAllBytes(f.Checkpoint("en"));
        File.AppendAllText(f.Checkpoint("en"), "{\"broken\":");
        var resumed = new Source(); var result = f.Run("en", resumed);
        Assert.AreEqual(1, result.Reused); Assert.AreEqual(2, result.Generated); Assert.AreEqual(2, resumed.Calls);
        Assert.AreEqual(1, Directory.GetFiles(Path.GetDirectoryName(f.Checkpoint("en"))!, "checkpoint.jsonl.backup-*").Length);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(f.Checkpoint("en"))[..original.Length]);
        Assert.AreEqual(3, DocumentVectorGenerator.ValidatePublishedV6(f.Catalog, "en", f.Published("en"))!.Reused);
    }

    [TestMethod]
    public void PublishedEnglishMissingSerbianAndCompletedPairReplayUseZeroFurtherInference()
    {
        using var f = new Fixture(); var source = new Source();
        f.Run("en", source); Assert.AreEqual(3, source.Calls);
        Assert.ThrowsExactly<InvalidDataException>(() => ActorPairedGenerationV6.PublishPair(f.Catalog, f.Root));
        f.Run("sr", source); Assert.AreEqual(6, source.Calls);
        ActorPairedGenerationV6.PublishPair(f.Catalog, f.Root);
        var before = Directory.GetFiles(f.Root, "*", SearchOption.AllDirectories).ToDictionary(x => x, File.ReadAllBytes);
        var never = new Source(() => Assert.Fail("Completed replay called inference."));
        Assert.AreEqual(0, f.Run("en", never).Generated); Assert.AreEqual(0, f.Run("sr", never).Generated);
        ActorPairedGenerationV6.PublishPair(f.Catalog, f.Root);
        Assert.AreEqual(0, never.Calls);
        foreach (var (path, bytes) in before) CollectionAssert.AreEqual(bytes, File.ReadAllBytes(path));
        File.WriteAllText(Path.Combine(f.Root, "paired-release.json"), "{}");
        Assert.ThrowsExactly<InvalidDataException>(() => ActorPairedGenerationV6.PublishPair(f.Catalog, f.Root));
        Assert.AreEqual("{}", File.ReadAllText(Path.Combine(f.Root, "paired-release.json")));
    }

    [TestMethod]
    public void ForeignHeadersFailBeforeRewriteOrInference()
    {
        foreach (var field in new[] { "format", "profileFingerprint", "language", "catalogIdentitySha256", "catalogSha256", "corpusSha256", "textFormatVersion", "selectionIdSetSha256", "dictionarySha256", "actorContext" })
        {
            using var f = new Fixture(); using var cancellation = new CancellationTokenSource();
            Assert.ThrowsExactly<OperationCanceledException>(() => f.Run("en", new Source(() => cancellation.Cancel()), cancellation.Token));
            var lines = File.ReadAllLines(f.Checkpoint("en")); var header = JsonNode.Parse(lines[0])!;
            header[field] = field == "format" ? "multilingual-full-document-checkpoint-v1" : "foreign";
            lines[0] = header.ToJsonString(); File.WriteAllText(f.Checkpoint("en"), string.Join('\n', lines) + "\n");
            var before = File.ReadAllBytes(f.Checkpoint("en")); var never = new Source();
            Assert.ThrowsExactly<InvalidDataException>(() => f.Run("en", never));
            Assert.AreEqual(0, never.Calls); CollectionAssert.AreEqual(before, File.ReadAllBytes(f.Checkpoint("en")));
            Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(f.Checkpoint("en"))!, "*.backup-*").Length);
        }
    }

    [TestMethod]
    public void InvalidVectorsAndFailedPublicationKeepDurableEvidence()
    {
        foreach (var kind in new[] { "dimension", "nan", "norm" })
        {
            using var f = new Fixture(); var source = new Source(kind: kind);
            Assert.ThrowsExactly<InvalidDataException>(() => f.Run("en", source));
            Assert.IsFalse(Directory.Exists(f.Published("en"))); Assert.AreEqual(1, File.ReadAllLines(f.Checkpoint("en")).Length);
        }
        using var fixture = new Fixture();
        Assert.ThrowsExactly<IOException>(() => DocumentVectorGenerator.RunV6(fixture.Checkpoint("en"), fixture.Published("en"), new Source(), fixture.Catalog, "en", beforePublish: () => throw new IOException("injected")));
        Assert.AreEqual(4, File.ReadAllLines(fixture.Checkpoint("en")).Length);
        Assert.AreEqual(3, fixture.Run("en", new Source()).Reused);
    }

    [TestMethod]
    public void ManifestAndRowsRejectLanguageCatalogCorpusActorTamperingAndP8Format()
    {
        foreach (var field in new[] { "format", "language", "catalogVersion", "catalogSha256", "catalogContentIdentitySha256", "corpusSha256", "textFormatVersion", "profileFingerprint", "actorContext" })
        {
            using var f = new Fixture(); f.Run("en", new Source());
            var path = Path.Combine(f.Published("en"), "manifest.json"); var root = JsonNode.Parse(File.ReadAllText(path))!;
            root[field] = field == "format" ? "multilingual-full-document-vectors-jsonl-v1" : "foreign"; File.WriteAllText(path, root.ToJsonString());
            Assert.ThrowsExactly<InvalidDataException>(() => DocumentVectorGenerator.ValidatePublishedV6(f.Catalog, "en", f.Published("en")));
        }
        foreach (var kind in new[] { "dimension", "nan", "norm", "duplicate", "missing", "fingerprint", "text", "duplicate-key" })
        {
            using var f = new Fixture(); f.Run("en", new Source()); var path = Path.Combine(f.Published("en"), "document-vectors.jsonl");
            var lines = File.ReadAllLines(path); var row = JsonNode.Parse(lines[0])!;
            if (kind == "dimension") row["vector"] = new JsonArray(1);
            if (kind == "norm") row["vector"]![0] = 2;
            if (kind == "nan") row["vector"]![0] = "NaN";
            if (kind == "duplicate") row["movieLensId"] = 2;
            if (kind == "fingerprint") row["fingerprint"] = "foreign";
            if (kind == "text") row["semanticTextSha256"] = "foreign";
            lines[0] = row.ToJsonString();
            if (kind == "duplicate-key") lines[0] = lines[0].Replace("{", "{\"movieLensId\":1,", StringComparison.Ordinal);
            File.WriteAllText(path, string.Join('\n', kind == "missing" ? lines[..2] : lines) + "\n");
            var manifestPath = Path.Combine(f.Published("en"), "manifest.json"); var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
            manifest["outputSha256"] = Hash(File.ReadAllBytes(path)); File.WriteAllText(manifestPath, manifest.ToJsonString());
            Assert.Throws<Exception>(() => DocumentVectorGenerator.ValidatePublishedV6(f.Catalog, "en", f.Published("en")));
        }
    }

    private sealed class Source(Action? after = null, string? kind = null) : IDocumentVectorSource
    {
        public int Calls { get; private set; }
        public IReadOnlyList<float[]> Embed(IReadOnlyList<string> texts, CancellationToken token)
        {
            Assert.AreEqual(1, texts.Count); Calls++; var vector = new float[kind == "dimension" ? 767 : 768];
            vector[0] = kind == "nan" ? float.NaN : kind == "norm" ? 2 : 1; after?.Invoke(); return [vector];
        }
        public string Fingerprint(string text) => throw new AssertFailedException("V6 must bind language-specific document fingerprints.");
        public string ProfileFingerprint => ProfileDescriptor.ProfileFingerprint;
        public EmbeddingProfileDescriptor ProfileDescriptor => EmbeddingProfileDescriptor.MultilingualE5Base;
        public long TruncationCount => 0;
    }
    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "v6-generator-" + Guid.NewGuid().ToString("N"));
        public ActorCatalogV6Document Catalog { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Root); Catalog = new(Path.Combine(Root, "fixture.jsonl"), new('a', 64), new('b', 64), new('c', 64), new('d', 64), new('e', 64), new('f', 64), new('1', 64), new('2', 64), new('3', 64), new('4', 64), new('5', 64), new('6', 64),
                Array.AsReadOnly(new[] { new ActorMovieV6(1, "Title: One.", "Naslov: Jedan."), new(2, "Title: Two.", "Naslov: Dva."), new(3, "Title: Three.", "Naslov: Tri.") }));
        }
        public string Checkpoint(string language) => Path.Combine(Root, language, "checkpoint.jsonl");
        public string Published(string language) => Path.Combine(Root, language, "published");
        public RunResult Run(string language, IDocumentVectorSource source, CancellationToken token = default) => DocumentVectorGenerator.RunV6(Checkpoint(language), Published(language), source, Catalog, language, token);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
