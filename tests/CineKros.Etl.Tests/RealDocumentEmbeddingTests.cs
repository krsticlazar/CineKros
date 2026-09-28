using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CineKros.Etl;

namespace CineKros.Etl.Tests;

[TestClass]
public sealed class RealDocumentEmbeddingTests
{
    [TestMethod]
    public void Request_UsesExactDocumentTextAndSafeGeminiBody()
    {
        var options = new RealEmbeddingOptions("C:\\catalog", "C:\\manifest", "C:\\out", "C:\\checkpoint");
        using var request = RealDocumentEmbeddingRunner.BuildRequest(options, "title: Amélie | text: a café", "test-secret");
        Assert.AreEqual(HttpMethod.Post, request.Method);
        Assert.AreEqual("https://generativelanguage.googleapis.com/v1beta/models/gemini-embedding-2:embedContent", request.RequestUri!.ToString());
        Assert.AreEqual("test-secret", request.Headers.GetValues("x-goog-api-key").Single());
        using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result);
        var root = body.RootElement;
        CollectionAssert.AreEqual(new[] { "model", "content", "output_dimensionality" }, root.EnumerateObject().Select(x => x.Name).ToArray());
        Assert.AreEqual("models/gemini-embedding-2", root.GetProperty("model").GetString());
        Assert.AreEqual("title: Amélie | text: a café", root.GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.AreEqual(768, root.GetProperty("output_dimensionality").GetInt32());
        Assert.IsFalse(root.ToString().Contains("task_type", StringComparison.Ordinal));
        Assert.IsFalse(request.Content.ReadAsStringAsync().Result.Contains("test-secret", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Fingerprint_BindsFormattedTitleTextAndLockedProfileButNotOtherMetadata()
    {
        var options = new RealEmbeddingOptions("C:\\catalog", "C:\\manifest", "C:\\out", "C:\\checkpoint");
        var formatted = RealDocumentEmbeddingRunner.FormatInput("Title", "semantic");
        var baseline = RealDocumentEmbeddingRunner.Fingerprint(formatted, options);
        Assert.AreNotEqual(baseline, RealDocumentEmbeddingRunner.Fingerprint(RealDocumentEmbeddingRunner.FormatInput("Title changed", "semantic"), options));
        Assert.AreNotEqual(baseline, RealDocumentEmbeddingRunner.Fingerprint(RealDocumentEmbeddingRunner.FormatInput("Title", "semantic changed"), options));
        Assert.AreNotEqual(baseline, RealDocumentEmbeddingRunner.Fingerprint(formatted, options with { Dimension = 767 }));
    }

    [TestMethod]
    public async Task Run_ResumesAndPublishesSortedRealProfileArtifactWithoutFakeMarker()
    {
        using var f = new Fixture(); var client = f.Client(_ => VectorResponse());
        var first = await RealDocumentEmbeddingRunner.RunAsync(f.Options, client, "test-secret", delay: (_, _) => Task.CompletedTask);
        Assert.AreEqual(2, first.Generated); Assert.AreEqual(0, first.Reused); Assert.AreEqual(2, first.Attempts);
        using var artifact = JsonDocument.Parse(File.ReadAllText(Path.Combine(f.Output, "manifest.json")));
        Assert.AreEqual("real-document-vectors-jsonl-v1", artifact.RootElement.GetProperty("artifactFormat").GetString());
        Assert.AreEqual("b3-embed2-text-search-v1", artifact.RootElement.GetProperty("profile").GetString());
        Assert.AreEqual(2, artifact.RootElement.GetProperty("recordCount").GetInt32());
        Assert.AreEqual(first.OutputSha256, artifact.RootElement.GetProperty("outputSha256").GetString());
        var lines = File.ReadAllLines(Path.Combine(f.Output, "document-vectors.jsonl"));
        Assert.AreEqual(2, lines.Length);
        Assert.IsFalse(string.Join("", lines).Contains("FAKE ONLY", StringComparison.Ordinal));
        Assert.AreEqual(2, JsonDocument.Parse(lines[0]).RootElement.GetProperty("movieLensId").GetInt64());
        Assert.AreEqual(9, JsonDocument.Parse(lines[1]).RootElement.GetProperty("movieLensId").GetInt64());
        var second = await RealDocumentEmbeddingRunner.RunAsync(f.Options, f.Client(_ => throw new AssertFailedException("unexpected HTTP request")), "test-secret", delay: (_, _) => Task.CompletedTask);
        Assert.AreEqual(0, second.Generated); Assert.AreEqual(2, second.Reused); Assert.AreEqual(0, second.Attempts);
        var metadataOptions = f.ChangeMetadataAndBindNewCatalog();
        var newCatalog = await RealDocumentEmbeddingRunner.RunAsync(metadataOptions, f.Client(_ => throw new AssertFailedException("metadata-only change must reuse vectors")), "test-secret", delay: (_, _) => Task.CompletedTask);
        Assert.AreEqual(0, newCatalog.Generated); Assert.AreEqual(2, newCatalog.Reused); Assert.AreEqual(0, newCatalog.Attempts);
    }

    [TestMethod]
    public async Task PublishedRows_RecordLowercaseHashOfUnmodifiedB2SemanticText()
    {
        using var f = new Fixture();
        await RealDocumentEmbeddingRunner.RunAsync(f.Options, f.Client(_ => VectorResponse()), "test-secret", delay: (_, _) => Task.CompletedTask);
        using var catalog = File.OpenText(f.Catalog);
        foreach (var line in File.ReadAllLines(Path.Combine(f.Output, "document-vectors.jsonl")))
        {
            using var output = JsonDocument.Parse(line); using var input = JsonDocument.Parse(catalog.ReadLine()!);
            var originalSemanticText = input.RootElement.GetProperty("semanticText").GetString()!;
            var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(originalSemanticText)));
            Assert.AreEqual(expected, output.RootElement.GetProperty("semanticTextSha256").GetString());
            Assert.AreEqual(expected, expected.ToLowerInvariant());
        }
    }

    [TestMethod]
    public async Task Run_RepairsCorruptHeaderAndTornTailAndRegeneratesOnlyMissingCurrentSuccess()
    {
        using var f = new Fixture();
        await RealDocumentEmbeddingRunner.RunAsync(f.Options, f.Client(_ => VectorResponse()), "test-secret", delay: (_, _) => Task.CompletedTask);
        var lines = File.ReadAllLines(f.Checkpoint); lines[0] = "bad header"; lines[^1] = "{torn"; File.WriteAllText(f.Checkpoint, string.Join("\n", lines));
        var calls = 0;
        var result = await RealDocumentEmbeddingRunner.RunAsync(f.Options, f.Client(_ => { calls++; return VectorResponse(); }), "test-secret", delay: (_, _) => Task.CompletedTask);
        Assert.AreEqual(1, calls); Assert.AreEqual(1, result.Generated); Assert.AreEqual(1, result.Reused);
        Assert.IsTrue(File.ReadAllText(f.Checkpoint).EndsWith("\n", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Run_RepairsMalformedMiddleJournalRecordAndPreservesOtherSuccess()
    {
        using var f = new Fixture();
        await RealDocumentEmbeddingRunner.RunAsync(f.Options, f.Client(_ => VectorResponse()), "test-secret", delay: (_, _) => Task.CompletedTask);
        var lines = File.ReadAllLines(f.Checkpoint); lines[1] = "{malformed middle journal record}"; File.WriteAllText(f.Checkpoint, string.Join("\n", lines) + "\n");
        var calls = 0;
        var result = await RealDocumentEmbeddingRunner.RunAsync(f.Options with { OutputDirectory = Path.Combine(Path.GetDirectoryName(f.Output)!, "repaired-middle") }, f.Client(_ => { calls++; return VectorResponse(); }), "test-secret", delay: (_, _) => Task.CompletedTask);
        Assert.AreEqual(1, calls); Assert.AreEqual(1, result.Generated); Assert.AreEqual(1, result.Reused);
    }

    [TestMethod]
    public async Task Run_RequiresExplicitQuotaDayAdvanceAndChecksSavedCanaryBeforeReuse()
    {
        using var f = new Fixture();
        await RealDocumentEmbeddingRunner.RunAsync(f.Options, f.Client(_ => VectorResponse()), "test-secret", delay: (_, _) => Task.CompletedTask);
        f.SetOldQuotaDay(); var calls = 0;
        await Assert.ThrowsAsync<Exception>(async () => await RealDocumentEmbeddingRunner.RunAsync(f.Options, f.Client(_ => { calls++; return VectorResponse(); }), "test-secret", delay: (_, _) => Task.CompletedTask));
        Assert.AreEqual(0, calls);
        var advanced = await RealDocumentEmbeddingRunner.RunAsync(f.Options with { AllowQuotaDayAdvance = true }, f.Client(_ => { calls++; return VectorResponse(); }), "test-secret", delay: (_, _) => Task.CompletedTask);
        Assert.AreEqual(1, calls); Assert.AreEqual(2, advanced.Reused); Assert.AreEqual(1, advanced.Attempts);
        f.SetOldQuotaDay();
        await Assert.ThrowsAsync<Exception>(async () => await RealDocumentEmbeddingRunner.RunAsync(f.Options with { AllowQuotaDayAdvance = true }, f.Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"embedding\":{\"values\":[1]}}") }), "test-secret", delay: (_, _) => Task.CompletedTask));
    }

    [TestMethod]
    public async Task Run_CorruptBudgetJournalStopsBeforeSendingAnyRequest()
    {
        using var f = new Fixture();
        await RealDocumentEmbeddingRunner.RunAsync(f.Options, f.Client(_ => VectorResponse()), "test-secret", delay: (_, _) => Task.CompletedTask);
        var checkpointLines = File.ReadAllLines(f.Checkpoint); checkpointLines[0] = "corrupt but recoverable header"; File.WriteAllText(f.Checkpoint, string.Join("\n", checkpointLines) + "\n");
        var before = File.ReadAllBytes(f.Checkpoint);
        File.WriteAllText(f.Checkpoint + ".budget.json", "not-json"); var calls = 0;
        await Assert.ThrowsExactlyAsync<ExportValidationException>(() => RealDocumentEmbeddingRunner.RunAsync(f.Options, f.Client(_ => { calls++; return VectorResponse(); }), "test-secret", delay: (_, _) => Task.CompletedTask));
        Assert.AreEqual(0, calls); CollectionAssert.AreEqual(before, File.ReadAllBytes(f.Checkpoint));
        using var g = new Fixture(); await RealDocumentEmbeddingRunner.RunAsync(g.Options, g.Client(_ => VectorResponse()), "test-secret", delay: (_, _) => Task.CompletedTask);
        before = File.ReadAllBytes(g.Checkpoint); File.Delete(g.Checkpoint + ".budget.json"); calls = 0;
        await Assert.ThrowsExactlyAsync<ExportValidationException>(() => RealDocumentEmbeddingRunner.RunAsync(g.Options, g.Client(_ => { calls++; return VectorResponse(); }), "test-secret", delay: (_, _) => Task.CompletedTask));
        Assert.AreEqual(0, calls); CollectionAssert.AreEqual(before, File.ReadAllBytes(g.Checkpoint));
        using var h = new Fixture(); await RealDocumentEmbeddingRunner.RunAsync(h.Options, h.Client(_ => VectorResponse()), "test-secret", delay: (_, _) => Task.CompletedTask);
        before = File.ReadAllBytes(h.Checkpoint); var budget = JsonNode.Parse(File.ReadAllText(h.Checkpoint + ".budget.json"))!; budget["totalAttempts"] = -1; File.WriteAllText(h.Checkpoint + ".budget.json", budget.ToJsonString()); calls = 0;
        await Assert.ThrowsExactlyAsync<ExportValidationException>(() => RealDocumentEmbeddingRunner.RunAsync(h.Options, h.Client(_ => { calls++; return VectorResponse(); }), "test-secret", delay: (_, _) => Task.CompletedTask));
        Assert.AreEqual(0, calls); CollectionAssert.AreEqual(before, File.ReadAllBytes(h.Checkpoint));
        budget["totalAttempts"] = 2; budget["budgetFormat"] = "incompatible"; File.WriteAllText(h.Checkpoint + ".budget.json", budget.ToJsonString()); calls = 0;
        await Assert.ThrowsExactlyAsync<ExportValidationException>(() => RealDocumentEmbeddingRunner.RunAsync(h.Options, h.Client(_ => { calls++; return VectorResponse(); }), "test-secret", delay: (_, _) => Task.CompletedTask));
        Assert.AreEqual(0, calls); CollectionAssert.AreEqual(before, File.ReadAllBytes(h.Checkpoint));
    }

    [TestMethod]
    public void ParseVector_RejectsMalformedDimensionNonfiniteZeroAndMultipleEmbeddings()
    {
        Assert.ThrowsExactly<ExportValidationException>(() => RealDocumentEmbeddingRunner.ParseVector("{}", 768));
        Assert.ThrowsExactly<ExportValidationException>(() => RealDocumentEmbeddingRunner.ParseVector("{\"embedding\":{\"values\":[0]}}", 768));
        Assert.ThrowsExactly<ExportValidationException>(() => RealDocumentEmbeddingRunner.ParseVector("{\"embedding\":{\"values\":[\"NaN\"]}}", 1));
        Assert.ThrowsExactly<ExportValidationException>(() => RealDocumentEmbeddingRunner.ParseVector("{\"embeddings\":[{},{}],\"embedding\":{\"values\":[1]}}", 1));
        Assert.ThrowsExactly<ExportValidationException>(() => RealDocumentEmbeddingRunner.ParseVector("{\"embedding\":{\"values\":[0,0]}}", 2));
        Assert.ThrowsExactly<ExportValidationException>(() => RealDocumentEmbeddingRunner.ParseVector("{\"embedding\":{\"values\":[1e1000]}}", 1));
    }

    [TestMethod]
    public async Task Run_Retries429WithBoundedRetryAfterAndStopsForCredentialFailure()
    {
        using var f = new Fixture(); var requests = 0; var delayed = TimeSpan.Zero;
        var result = await RealDocumentEmbeddingRunner.RunAsync(f.Options, f.Client(_ =>
        {
            requests++;
            if (requests == 1) { var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests); limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(2)); return limited; }
            return VectorResponse();
        }), "test-secret", delay: (wait, _) => { delayed = wait; return Task.CompletedTask; });
        Assert.AreEqual(3, requests); Assert.AreEqual(3, result.Attempts); Assert.AreEqual(TimeSpan.FromSeconds(2), delayed);

        using var second = new Fixture(); var unauthorizedCalls = 0;
        await Assert.ThrowsAsync<Exception>(async () => await RealDocumentEmbeddingRunner.RunAsync(second.Options, second.Client(_ => { unauthorizedCalls++; return new(HttpStatusCode.Unauthorized); }), "test-secret", delay: (_, _) => Task.CompletedTask));
        Assert.AreEqual(1, unauthorizedCalls); Assert.IsFalse(Directory.Exists(second.Output));
    }

    [TestMethod]
    public async Task Run_TransientExhaustionUsesAtMostThreeTotalHttpAttempts()
    {
        using var f = new Fixture(1); var calls = 0;
        await Assert.ThrowsAsync<Exception>(async () => await RealDocumentEmbeddingRunner.RunAsync(f.Options, f.Client(_ => { calls++; return new(HttpStatusCode.ServiceUnavailable); }), "test-secret", delay: (_, _) => Task.CompletedTask));
        Assert.AreEqual(3, calls);
        using var g = new Fixture(1); calls = 0;
        await Assert.ThrowsAsync<Exception>(async () => await RealDocumentEmbeddingRunner.RunAsync(g.Options, g.Client(_ => { calls++; return new(HttpStatusCode.TooManyRequests); }), "test-secret", delay: (_, _) => Task.CompletedTask));
        Assert.AreEqual(3, calls);
        using var h = new Fixture(1); calls = 0;
        using var network = new HttpClient(new FaultHandler(_ => { calls++; return new HttpRequestException("fake transport fault"); }));
        await Assert.ThrowsAsync<Exception>(async () => await RealDocumentEmbeddingRunner.RunAsync(h.Options, network, "test-secret", delay: (_, _) => Task.CompletedTask));
        Assert.AreEqual(3, calls);
    }

    [TestMethod]
    public async Task Run_RestartRetainsRollingMinuteBudgetAndUpdatesCurrentAttemptUsage()
    {
        using var f = new Fixture(); var calls = 0; var options = f.Options with { MaxTokensPerMinute = 10 };
        await Assert.ThrowsAsync<Exception>(async () => await RealDocumentEmbeddingRunner.RunAsync(options, f.Client(_ => { calls++; return VectorResponse(); }), "test-secret", delay: (_, _) => Task.CompletedTask));
        Assert.AreEqual(1, calls);
        await Assert.ThrowsAsync<Exception>(async () => await RealDocumentEmbeddingRunner.RunAsync(options, f.Client(_ => { calls++; return VectorResponse(); }), "test-secret", delay: (_, _) => Task.CompletedTask));
        Assert.AreEqual(1, calls);

        using var attempts = new Fixture(); calls = 0; var requestLimited = attempts.Options with { MaxAttemptsPerMinute = 1 };
        await Assert.ThrowsAsync<Exception>(async () => await RealDocumentEmbeddingRunner.RunAsync(requestLimited, attempts.Client(_ => { calls++; return VectorResponse(); }), "test-secret", delay: (_, _) => Task.CompletedTask));
        Assert.AreEqual(1, calls);
        await Assert.ThrowsAsync<Exception>(async () => await RealDocumentEmbeddingRunner.RunAsync(requestLimited, attempts.Client(_ => { calls++; return VectorResponse(); }), "test-secret", delay: (_, _) => Task.CompletedTask));
        Assert.AreEqual(1, calls);

        using var g = new Fixture(3); var usage = new[] { 1, 10, 1 }; var index = 0;
        var result = await RealDocumentEmbeddingRunner.RunAsync(g.Options with { MaxTokensPerMinute = 20 }, g.Client(_ => VectorResponseWithUsage(usage[index++])), "test-secret", delay: (_, _) => Task.CompletedTask);
        Assert.AreEqual(3, index); Assert.AreEqual(3, result.Generated);
    }

    [TestMethod]
    public async Task Run_ContinuesAfterSingleRecordRejectionThenKeepsPartialCheckpointAndNoArtifact()
    {
        using var f = new Fixture(); var calls = 0;
        await Assert.ThrowsAsync<ExportValidationException>(async () => await RealDocumentEmbeddingRunner.RunAsync(f.Options, f.Client(_ => ++calls == 1 ? new(HttpStatusCode.BadRequest) : VectorResponse()), "test-secret", delay: (_, _) => Task.CompletedTask));
        Assert.AreEqual(2, calls); Assert.IsTrue(File.Exists(f.Checkpoint)); Assert.IsFalse(Directory.Exists(f.Output));
    }

    [TestMethod]
    public async Task Run_FailedChangedInputLeavesPreviouslyPublishedArtifactUntouched()
    {
        using var f = new Fixture(); await RealDocumentEmbeddingRunner.RunAsync(f.Options, f.Client(_ => VectorResponse()), "test-secret", delay: (_, _) => Task.CompletedTask);
        var oldJson = File.ReadAllBytes(Path.Combine(f.Output, "document-vectors.jsonl")); var oldManifest = File.ReadAllBytes(Path.Combine(f.Output, "manifest.json"));
        var changed = f.ChangeTitleAndBindNewCatalog(); var calls = 0;
        await Assert.ThrowsAsync<ExportValidationException>(async () => await RealDocumentEmbeddingRunner.RunAsync(changed, f.Client(_ => { calls++; return calls == 1 ? new(HttpStatusCode.BadRequest) : VectorResponse(); }), "test-secret", delay: (_, _) => Task.CompletedTask));
        CollectionAssert.AreEqual(oldJson, File.ReadAllBytes(Path.Combine(f.Output, "document-vectors.jsonl")));
        CollectionAssert.AreEqual(oldManifest, File.ReadAllBytes(Path.Combine(f.Output, "manifest.json")));
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task Run_EnforcesAttemptAndByteCapsAndPublicationFaultLeavesNoPartialFinal()
    {
        using var f = new Fixture(); var calls = 0;
        var limited = f.Options with { MaxTotalAttempts = 1 };
        await Assert.ThrowsAsync<Exception>(async () => await RealDocumentEmbeddingRunner.RunAsync(limited, f.Client(_ => { calls++; return VectorResponse(); }), "test-secret", delay: (_, _) => Task.CompletedTask));
        Assert.AreEqual(1, calls); Assert.IsFalse(Directory.Exists(f.Output));
        using var g = new Fixture();
        await Assert.ThrowsAsync<IOException>(async () => await RealDocumentEmbeddingRunner.RunAsync(g.Options, g.Client(_ => VectorResponse()), "test-secret", delay: (_, _) => Task.CompletedTask, beforePublish: () => throw new IOException("injected")));
        Assert.IsFalse(Directory.Exists(g.Output)); Assert.IsTrue(File.Exists(g.Checkpoint));
    }

    [TestMethod]
    public async Task Run_EnforcesEveryConfiguredBudgetAndCancellationKeepsDurableSuccesses()
    {
        Func<Fixture, RealEmbeddingOptions>[] caps =
        {
            f => f.Options with { MaxInputBytes = 1 },
            f => f.Options with { MaxInputTokens = 1 },
            f => f.Options with { MaxDailyAttempts = 1 },
            f => f.Options with { MaxAttemptsPerMinute = 1 },
            f => f.Options with { MaxTokensPerMinute = 1 },
            f => f.Options with { MaxRunTime = TimeSpan.Zero }
        };
        foreach (var capped in caps)
        {
            using var f = new Fixture(); var calls = 0;
            await Assert.ThrowsAsync<Exception>(async () => await RealDocumentEmbeddingRunner.RunAsync(capped(f), f.Client(_ => { calls++; return VectorResponse(); }), "test-secret", delay: (_, _) => Task.CompletedTask));
            Assert.IsFalse(Directory.Exists(f.Output));
        }

        using var resumable = new Fixture(); using var cts = new CancellationTokenSource(); var generated = 0;
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await RealDocumentEmbeddingRunner.RunAsync(resumable.Options, resumable.Client(_ => { generated++; if (generated == 1) cts.Cancel(); return VectorResponse(); }), "test-secret", cts.Token, (_, _) => Task.CompletedTask));
        Assert.AreEqual(1, generated); Assert.IsTrue(File.Exists(resumable.Checkpoint)); Assert.IsFalse(Directory.Exists(resumable.Output));
        var resumed = await RealDocumentEmbeddingRunner.RunAsync(resumable.Options, resumable.Client(_ => { generated++; return VectorResponse(); }), "test-secret", delay: (_, _) => Task.CompletedTask);
        Assert.AreEqual(1, resumed.Reused); Assert.AreEqual(1, resumed.Generated); Assert.AreEqual(2, generated);
    }

    private static HttpResponseMessage VectorResponse()
    {
        var values = string.Join(',', Enumerable.Range(0, 768).Select(i => i == 0 ? "1" : "0.001"));
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{{\"embedding\":{{\"values\":[{values}]}}}}", Encoding.UTF8, "application/json") };
    }
    private static HttpResponseMessage VectorResponseWithUsage(int tokens)
    {
        var root = JsonNode.Parse(VectorResponse().Content!.ReadAsStringAsync().Result)!.AsObject(); root["usageMetadata"] = new JsonObject { ["promptTokenCount"] = tokens };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(root.ToJsonString(), Encoding.UTF8, "application/json") };
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request)); }
    private sealed class FaultHandler(Func<HttpRequestMessage, Exception> fail) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromException<HttpResponseMessage>(fail(request)); }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "c03-real-" + Guid.NewGuid().ToString("N"));
        internal string Catalog => Path.Combine(root, "catalog.jsonl"); internal string Manifest => Path.Combine(root, "manifest.json");
        internal string Output => Path.Combine(root, "output"); internal string Checkpoint => Path.Combine(root, "checkpoint.jsonl");
        internal RealEmbeddingOptions Options { get; }
        internal Fixture(int recordCount = 2)
        {
            Directory.CreateDirectory(root);
            var rows = Enumerable.Range(0, recordCount).Select(i => $"{{\"movieLensId\":{2 + i * 7},\"title\":\"Title {i}\",\"semanticText\":\"Text {i}\",\"ratingCount\":{9 - i}}}");
            File.WriteAllText(Catalog, string.Join("\n", rows) + "\n", new UTF8Encoding(false));
            var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Catalog))); const string fingerprint = "test-content-fingerprint";
            File.WriteAllText(Manifest, JsonSerializer.Serialize(new { catalogVersion = RealDocumentEmbeddingRunner.CatalogVersion, contentFingerprint = fingerprint, validated = true, output = new { sha256 = hash, recordCount, order = "movieLensId ascending" } }));
            Options = new(Catalog, Manifest, Output, Checkpoint, MaxTotalAttempts: 20, MaxDailyAttempts: 20, MaxAttemptsPerMinute: 20, MaxTokensPerMinute: 5000, ExpectedCatalogSha256: hash, ExpectedCatalogFingerprint: fingerprint, ExpectedCatalogRecords: recordCount);
        }
        internal RealEmbeddingOptions ChangeMetadataAndBindNewCatalog()
        {
            var rows = File.ReadAllLines(Catalog).Select(x => JsonNode.Parse(x)!).ToArray(); rows[0]["ratingCount"] = 99;
            File.WriteAllText(Catalog, string.Join("\n", rows.Select(x => x!.ToJsonString())) + "\n", new UTF8Encoding(false));
            var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Catalog))); const string fingerprint = "new-catalog-content-fingerprint";
            File.WriteAllText(Manifest, JsonSerializer.Serialize(new { catalogVersion = RealDocumentEmbeddingRunner.CatalogVersion, contentFingerprint = fingerprint, validated = true, output = new { sha256 = hash, recordCount = 2, order = "movieLensId ascending" } }));
            return Options with { OutputDirectory = Path.Combine(root, "output-metadata-only"), ExpectedCatalogSha256 = hash, ExpectedCatalogFingerprint = fingerprint };
        }
        internal RealEmbeddingOptions ChangeTitleAndBindNewCatalog()
        {
            var rows = File.ReadAllLines(Catalog).Select(x => JsonNode.Parse(x)!).ToArray(); rows[0]["title"] = "Title Two revised";
            File.WriteAllText(Catalog, string.Join("\n", rows.Select(x => x!.ToJsonString())) + "\n", new UTF8Encoding(false));
            var hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Catalog))); const string fingerprint = "changed-title-content-fingerprint";
            File.WriteAllText(Manifest, JsonSerializer.Serialize(new { catalogVersion = RealDocumentEmbeddingRunner.CatalogVersion, contentFingerprint = fingerprint, validated = true, output = new { sha256 = hash, recordCount = 2, order = "movieLensId ascending" } }));
            return Options with { ExpectedCatalogSha256 = hash, ExpectedCatalogFingerprint = fingerprint };
        }
        internal void SetOldQuotaDay()
        {
            var path = Checkpoint + ".budget.json"; var root = JsonNode.Parse(File.ReadAllText(path))!; root["quotaDay"] = "2000-01-01"; File.WriteAllText(path, root.ToJsonString());
        }
        internal HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> send) => new(new Handler(send));
        public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
