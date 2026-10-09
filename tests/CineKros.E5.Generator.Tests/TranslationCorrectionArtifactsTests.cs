using System.Diagnostics;
using System.Text.Json;
using CineKros.Catalog.Importer;
using CineKros.E5.Generator;
using CineKros.Embedding;

namespace CineKros.E5.Generator.Tests;

[TestClass]
public sealed class TranslationCorrectionArtifactsTests
{
    [TestMethod]
    public async Task V2GenerationSeedsCompatibleRowsAndNeverEmbedsEnglish()
    {
        var root = FindRoot();
        if (root is null) Assert.Inconclusive("Frozen Serbian-search POC inputs are not installed for this test run.");
        var baseRoot = Path.Combine(root!, "database/data/derived/serbian-search/poc-v1");
        var sourceCatalog = Path.Combine(root!, "database/data/derived/final/b05a-real-tmdb-01/movies-catalog.jsonl");
        var approved = Path.Combine(root!, ".local/planning/reports/sr-phase-06t/review/corrections-approved.json");
        var project = Path.Combine(root!, ".local/planning/reports/sr-phase-06t/tooling/CineKros.SrP6T.Tools.csproj");
        var temp = Path.Combine(Path.GetTempPath(), "cinekros-p6t-vectors-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var correctedRoot = Path.Combine(temp, "poc-v2"); await BuildReleaseAsync(root!, project, baseRoot, sourceCatalog, approved, correctedRoot);
            var baseline = await MultilingualPocCatalog.LoadAsync(Path.Combine(baseRoot, "catalog/movies-catalog.jsonl"), Path.Combine(baseRoot, "catalog/manifest.json"),
                Path.Combine(baseRoot, "translation/tag-translations-sr.json"), sourceCatalog);
            var current = await CorrectedPocCatalog.LoadAsync(Path.Combine(correctedRoot, "catalog/movies-catalog.jsonl"), Path.Combine(correctedRoot, "catalog/manifest.json"),
                Path.Combine(correctedRoot, "translation/tag-translations-sr.json"), approved,
                Path.Combine(baseRoot, "catalog/movies-catalog.jsonl"), Path.Combine(baseRoot, "catalog/manifest.json"),
                Path.Combine(baseRoot, "translation/tag-translations-sr.json"), sourceCatalog);
            var changedSr = baseline.Movies.Zip(current.Movies).Count(pair => pair.First.SemanticTextSr != pair.Second.SemanticTextSr);
            var fake = new FakeMultilingualSource();
            var result = await TranslationCorrectionArtifacts.GenerateAsync(Path.Combine(correctedRoot, "catalog/movies-catalog.jsonl"), Path.Combine(correctedRoot, "catalog/manifest.json"),
                Path.Combine(correctedRoot, "translation/tag-translations-sr.json"), approved,
                Path.Combine(baseRoot, "catalog/movies-catalog.jsonl"), Path.Combine(baseRoot, "catalog/manifest.json"),
                Path.Combine(baseRoot, "translation/tag-translations-sr.json"), sourceCatalog,
                Path.Combine(baseRoot, "embeddings/en"), Path.Combine(baseRoot, "embeddings/sr"), Path.Combine(temp, "checkpoints"),
                Path.Combine(correctedRoot, "embeddings"), fake);
            var en = result.Languages.Single(x => x.Language == "en"); var sr = result.Languages.Single(x => x.Language == "sr");
            Assert.AreEqual(150, en.SeededFromV1); Assert.AreEqual(150, en.Reused); Assert.AreEqual(0, en.Generated);
            Assert.AreEqual(changedSr, sr.Generated); Assert.AreEqual(150 - changedSr, sr.Reused); Assert.AreEqual(150 - changedSr, sr.SeededFromV1);
            Assert.AreEqual(changedSr, fake.EmbeddedTexts.Count);
            CollectionAssert.AreEqual(current.Movies.Where((movie, i) => baseline.Movies[i].SemanticTextSr != movie.SemanticTextSr).Select(x => x.SemanticTextSr).ToArray(), fake.EmbeddedTexts.ToArray());
            Assert.AreEqual(150, MultilingualPocCatalog.ValidateVectorArtifact(Path.Combine(correctedRoot, "embeddings/en/document-vectors.jsonl"),
                Path.Combine(correctedRoot, "embeddings/en/manifest.json"), current, "en").RecordCount);
            Assert.AreEqual(150, MultilingualPocCatalog.ValidateVectorArtifact(Path.Combine(correctedRoot, "embeddings/sr/document-vectors.jsonl"),
                Path.Combine(correctedRoot, "embeddings/sr/manifest.json"), current, "sr").RecordCount);
            Assert.IsTrue(File.Exists(Path.Combine(correctedRoot, "embeddings/provenance.json")));

            var interruptedCheckpointRoot = Path.Combine(temp, "interrupted-checkpoints");
            var interruptedOutputRoot = Path.Combine(temp, "interrupted-output");
            var interruptedSource = new FakeMultilingualSource(throwAfterCalls: 2);
            Assert.ThrowsExactly<InvalidOperationException>(() => GenerateAsync(correctedRoot, baseRoot, sourceCatalog, approved,
                interruptedCheckpointRoot, interruptedOutputRoot, interruptedSource).GetAwaiter().GetResult());
            var checkpoint = Path.Combine(interruptedCheckpointRoot, "sr/checkpoint.jsonl");
            var savedCheckpoint = File.ReadAllText(checkpoint);
            Assert.IsTrue(savedCheckpoint.Count(ch => ch == '\n') > 2, "Checkpoint should contain reused and already-generated rows before interruption.");
            var changedIdentity = savedCheckpoint.Replace(current.IdentitySha256, new string('0', 64), StringComparison.Ordinal);
            File.WriteAllText(checkpoint, changedIdentity, new System.Text.UTF8Encoding(false));
            var corruptSnapshot = File.ReadAllBytes(checkpoint);
            Assert.ThrowsExactly<InvalidDataException>(() => GenerateAsync(correctedRoot, baseRoot, sourceCatalog, approved,
                interruptedCheckpointRoot, interruptedOutputRoot, new FakeMultilingualSource()).GetAwaiter().GetResult());
            CollectionAssert.AreEqual(corruptSnapshot, File.ReadAllBytes(checkpoint), "An incompatible checkpoint must fail closed without replacing it.");
            File.WriteAllText(checkpoint, savedCheckpoint, new System.Text.UTF8Encoding(false));
            var resumeSource = new FakeMultilingualSource();
            var resumed = await GenerateAsync(correctedRoot, baseRoot, sourceCatalog, approved, interruptedCheckpointRoot, interruptedOutputRoot, resumeSource);
            Assert.AreEqual(changedSr - 2, resumeSource.EmbeddedTexts.Count, "Resume should infer only SR rows still missing from the checkpoint.");
            Assert.AreEqual(changedSr, resumed.Languages.Single(x => x.Language == "sr").Generated, "Provenance reports cumulative generated rows across interruption/resume.");

            AssertArtifactRejects(correctedRoot, current, "en", "dimension", "767");
            AssertArtifactRejects(correctedRoot, current, "en", "profileFingerprint", "wrong-profile");
            AssertArtifactRejects(correctedRoot, current, "en", "outputSha256", new string('0', 64));
            AssertRowRejects(correctedRoot, current, "fingerprint");
            AssertRowRejects(correctedRoot, current, "normalization");
            var completeVectorsPath = Path.Combine(correctedRoot, "embeddings/sr/document-vectors.jsonl");
            var completeBefore = File.ReadAllBytes(completeVectorsPath);
            var completeRunSource = new FakeMultilingualSource();
            var completeRun = await GenerateAsync(correctedRoot, baseRoot, sourceCatalog, approved,
                Path.Combine(temp, "checkpoints"), Path.Combine(correctedRoot, "embeddings"), completeRunSource);
            Assert.AreEqual(0, completeRunSource.EmbeddedTexts.Count);
            CollectionAssert.AreEqual(completeBefore, File.ReadAllBytes(completeVectorsPath), "Completed artifacts are immutable on rerun.");
            Assert.AreEqual(changedSr, completeRun.Languages.Single(x => x.Language == "sr").Generated);
            using var provenance = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(correctedRoot, "embeddings/provenance.json")));
            Assert.AreEqual(changedSr, provenance.RootElement.GetProperty("languages").EnumerateArray()
                .Single(x => x.GetProperty("language").GetString() == "sr").GetProperty("generated").GetInt32());
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    private static Task<CorrectionGenerationResult> GenerateAsync(string correctedRoot, string baseRoot, string sourceCatalog, string approved,
        string checkpointRoot, string outputRoot, IDocumentVectorSource source) => TranslationCorrectionArtifacts.GenerateAsync(
        Path.Combine(correctedRoot, "catalog/movies-catalog.jsonl"), Path.Combine(correctedRoot, "catalog/manifest.json"),
        Path.Combine(correctedRoot, "translation/tag-translations-sr.json"), approved,
        Path.Combine(baseRoot, "catalog/movies-catalog.jsonl"), Path.Combine(baseRoot, "catalog/manifest.json"),
        Path.Combine(baseRoot, "translation/tag-translations-sr.json"), sourceCatalog,
        Path.Combine(baseRoot, "embeddings/en"), Path.Combine(baseRoot, "embeddings/sr"), checkpointRoot, outputRoot, source);

    private static void AssertArtifactRejects(string release, MultilingualPocCatalogDocument catalog, string language, string property, string value)
    {
        var directory = Path.Combine(release, "embeddings", language);
        var manifestPath = Path.Combine(directory, "manifest.json"); var original = File.ReadAllText(manifestPath);
        try
        {
            using var doc = JsonDocument.Parse(original);
            var map = System.Text.Json.Nodes.JsonNode.Parse(original)!.AsObject();
            map[property] = value;
            File.WriteAllText(manifestPath, map.ToJsonString(), new System.Text.UTF8Encoding(false));
            Assert.ThrowsExactly<InvalidDataException>(() => MultilingualPocCatalog.ValidateVectorArtifact(Path.Combine(directory, "document-vectors.jsonl"), manifestPath, catalog, language));
        }
        finally { File.WriteAllText(manifestPath, original, new System.Text.UTF8Encoding(false)); }
    }

    private static void AssertRowRejects(string release, MultilingualPocCatalogDocument catalog, string mutation)
    {
        var directory = Path.Combine(release, "embeddings/en");
        var vectorsPath = Path.Combine(directory, "document-vectors.jsonl"); var manifestPath = Path.Combine(directory, "manifest.json");
        var originalVectors = File.ReadAllText(vectorsPath); var originalManifest = File.ReadAllText(manifestPath);
        try
        {
            var lines = originalVectors.Split('\n');
            var row = System.Text.Json.Nodes.JsonNode.Parse(lines[0])!.AsObject();
            if (mutation == "fingerprint") row["fingerprint"] = new string('0', 64);
            else row["vector"] = new System.Text.Json.Nodes.JsonArray(Enumerable.Range(0, 768).Select(_ => (System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonValue.Create(0f)).ToArray());
            lines[0] = row.ToJsonString(); var bytes = new System.Text.UTF8Encoding(false).GetBytes(string.Join('\n', lines));
            File.WriteAllBytes(vectorsPath, bytes);
            var manifest = System.Text.Json.Nodes.JsonNode.Parse(originalManifest)!.AsObject();
            manifest["outputSha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
            File.WriteAllText(manifestPath, manifest.ToJsonString(), new System.Text.UTF8Encoding(false));
            Assert.ThrowsExactly<InvalidDataException>(() => MultilingualPocCatalog.ValidateVectorArtifact(vectorsPath, manifestPath, catalog, "en"));
        }
        finally
        {
            File.WriteAllText(vectorsPath, originalVectors, new System.Text.UTF8Encoding(false));
            File.WriteAllText(manifestPath, originalManifest, new System.Text.UTF8Encoding(false));
        }
    }

    private static string? FindRoot()
    {
        var configured = Environment.GetEnvironmentVariable("CINEKROS_POC_ROOT");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(Path.Combine(configured, "database/data/derived/serbian-search/poc-v1/catalog/movies-catalog.jsonl"))) return configured;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "database/data/derived/serbian-search/poc-v1/catalog/movies-catalog.jsonl"))) return directory.FullName;
        return null;
    }
    private static async Task BuildReleaseAsync(string root, string project, string baseRoot, string sourceCatalog, string approved, string release)
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var value in new[] { "run", "--project", project, "--", "build-catalog", "--base-catalog", Path.Combine(baseRoot, "catalog/movies-catalog.jsonl"),
                     "--base-manifest", Path.Combine(baseRoot, "catalog/manifest.json"), "--base-dictionary", Path.Combine(baseRoot, "translation/tag-translations-sr.json"),
                     "--approved-mapping", approved, "--source-catalog", sourceCatalog, "--output-root", release }) start.ArgumentList.Add(value);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to run the bounded local catalog builder.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) Assert.Fail("Temporary corrected catalog build failed: " + await stdout + await stderr);
    }

    private sealed class FakeMultilingualSource : IDocumentVectorSource
    {
        private readonly int? _throwAfterCalls;
        private int _calls;
        public FakeMultilingualSource(int? throwAfterCalls = null) => _throwAfterCalls = throwAfterCalls;
        public List<string> EmbeddedTexts { get; } = [];
        public IReadOnlyList<float[]> Embed(IReadOnlyList<string> semanticTexts, CancellationToken cancellationToken)
        {
            var result = new List<float[]>(semanticTexts.Count);
            foreach (var text in semanticTexts)
            {
                if (_throwAfterCalls.HasValue && _calls >= _throwAfterCalls.Value) throw new InvalidOperationException("Simulated interruption after checkpointed rows.");
                cancellationToken.ThrowIfCancellationRequested(); EmbeddedTexts.Add(text);
                var vector = new float[768]; vector[0] = 1f; result.Add(vector);
                _calls++;
            }
            return result;
        }
        public string Fingerprint(string semanticText) => throw new InvalidOperationException("Multilingual checkpoint must use the fixed catalog fingerprint contract.");
        public string ProfileFingerprint => "eac906ed78f7863573d13c9b0435de1b8f848fe92fc6ae08aa3621400260b1fe";
        public EmbeddingProfileDescriptor ProfileDescriptor => EmbeddingProfileDescriptor.MultilingualE5Base;
        public long TruncationCount => 0;
    }
}
