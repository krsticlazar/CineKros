using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CineKros.Etl;

namespace CineKros.Etl.Tests;

[TestClass]
public sealed class EnrichmentV2R2Tests
{
    [TestMethod]
    public async Task AttemptGuard_EnforcesGlobalCapAcrossConcurrentRetriesAndCheckpointsWithoutPublishing()
    {
        using var fixture = new TinyFixture();
        using var guard = new TmdbAttemptGuard(2, TimeSpan.FromMinutes(120));
        var handler = new FixtureHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
        using var http = new HttpClient(handler);
        var tmdb = new TmdbDetailsClient(http, "fake-token", (_, _) => Task.CompletedTask, guard);
        var options = fixture.Options("cap-output") with { MaxConcurrency = 4 };

        await Assert.ThrowsExactlyAsync<TmdbRequestException>(() => EnrichmentV2Runner.RunAsync(options, tmdb.GetAsync,
            CancellationToken.None, fixture.TestHooks(), guard));

        Assert.AreEqual(2, handler.Paths.Count);
        Assert.AreEqual(2, guard.AttemptCount);
        Assert.AreEqual("attempt_cap", guard.StopReason);
        Assert.IsFalse(Directory.Exists(fixture.PathFor("cap-output")));
        using var checkpoint = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixture.PathFor("cache"), "checkpoint-v2.json")));
        Assert.AreEqual("attempt_cap", checkpoint.RootElement.GetProperty("stopReason").GetString());
        Assert.AreEqual(2, checkpoint.RootElement.GetProperty("actualHttpAttempts").GetInt32());
        Assert.AreEqual(1, checkpoint.RootElement.GetProperty("attemptedTmdbIdCount").GetInt32());
        Assert.IsTrue(checkpoint.RootElement.GetProperty("unresolvedIds").GetArrayLength() > 0);
    }

    [TestMethod]
    public async Task AttemptGuard_StopsSchedulingOnExhausted429AndResumesOnlyUncachedId()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CineKros.Etl.Tests", Guid.NewGuid().ToString("N"));
        using var guard = new TmdbAttemptGuard(20, TimeSpan.FromMinutes(120));
        var requests = 0;
        var handler = new FixtureHandler(_ =>
        {
            Interlocked.Increment(ref requests);
            return new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
        });
        using var http = new HttpClient(handler);
        var client = new TmdbDetailsClient(http, "fake-token", (_, _) => Task.CompletedTask, guard);
        try
        {
            await Assert.ThrowsExactlyAsync<TmdbRequestException>(() => EnrichmentV2Runner.FetchPendingAsync(
                Enumerable.Range(1, 40).ToArray(), new Dictionary<int, TmdbDetails>(), directory, 1,
                client.GetAsync, CancellationToken.None, attemptGuard: guard));
            Assert.AreEqual(3, requests);
            Assert.AreEqual(3, guard.AttemptCount);
            using var checkpoint = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "checkpoint-v2.json")));
            Assert.AreEqual(1, checkpoint.RootElement.GetProperty("failureClasses")[0].GetProperty("count").GetInt32());
            Assert.AreEqual("http_429", checkpoint.RootElement.GetProperty("failureClasses")[0].GetProperty("class").GetString());
            Assert.AreEqual(40, checkpoint.RootElement.GetProperty("unresolvedIds").GetArrayLength());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task AttemptGuard_CapNeverAllowsRequestNPlusOneWithConcurrentIdsAndRetries()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CineKros.Etl.Tests", Guid.NewGuid().ToString("N"));
        using var guard = new TmdbAttemptGuard(7, TimeSpan.FromMinutes(120));
        var handler = new FixtureHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
        using var http = new HttpClient(handler);
        var client = new TmdbDetailsClient(http, "fake-token", (_, _) => Task.CompletedTask, guard);
        try
        {
            await Assert.ThrowsExactlyAsync<TmdbRequestException>(() => EnrichmentV2Runner.FetchPendingAsync(
                Enumerable.Range(1, 50).ToArray(), new Dictionary<int, TmdbDetails>(), directory, 4,
                client.GetAsync, CancellationToken.None, attemptGuard: guard));
            Assert.AreEqual(7, guard.AttemptCount);
            Assert.AreEqual(7, handler.Paths.Count);
            Assert.AreEqual("attempt_cap", guard.StopReason);
            using var checkpoint = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "checkpoint-v2.json")));
            Assert.AreEqual(7, checkpoint.RootElement.GetProperty("actualHttpAttempts").GetInt32());
            Assert.IsTrue(checkpoint.RootElement.GetProperty("attemptedTmdbIdCount").GetInt32() is >= 1 and <= 4);
            Assert.AreEqual("attempt_cap", checkpoint.RootElement.GetProperty("stopReason").GetString());
            Assert.AreEqual(50, checkpoint.RootElement.GetProperty("unresolvedIds").GetArrayLength());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task AttemptGuard_WallDeadlineStopsNewIdsAndCheckpointsAfterInflightRequestCompletes()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CineKros.Etl.Tests", Guid.NewGuid().ToString("N"));
        using var guard = new TmdbAttemptGuard(100, TimeSpan.FromMilliseconds(30));
        var handler = new FixtureHandler(request =>
        {
            Thread.Sleep(80);
            var id = Path.GetFileName(request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent($"{{\"id\":{id}}}") };
        });
        using var http = new HttpClient(handler);
        var client = new TmdbDetailsClient(http, "fake-token", attemptGuard: guard);
        try
        {
            await Assert.ThrowsExactlyAsync<TmdbRequestException>(() => EnrichmentV2Runner.FetchPendingAsync(
                Enumerable.Range(1, 10).ToArray(), new Dictionary<int, TmdbDetails>(), directory, 1,
                client.GetAsync, CancellationToken.None, attemptGuard: guard));
            Assert.AreEqual(1, handler.Paths.Count);
            Assert.AreEqual("wall_clock", guard.StopReason);
            using var checkpoint = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "checkpoint-v2.json")));
            Assert.AreEqual("wall_clock", checkpoint.RootElement.GetProperty("stopReason").GetString());
            Assert.AreEqual(1, checkpoint.RootElement.GetProperty("actualHttpAttempts").GetInt32());
            Assert.AreEqual(1, checkpoint.RootElement.GetProperty("attemptedTmdbIdCount").GetInt32());
            Assert.AreEqual(9, checkpoint.RootElement.GetProperty("unresolvedIds").GetArrayLength());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task InvalidDetailResponseStopsSchedulingAndRecordsSanitizedCheckpoint()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CineKros.Etl.Tests", Guid.NewGuid().ToString("N"));
        var handler = new FixtureHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":999}")
        });
        using var http = new HttpClient(handler);
        var client = new TmdbDetailsClient(http, "fake-token");
        try
        {
            await Assert.ThrowsExactlyAsync<TmdbRequestException>(() => EnrichmentV2Runner.FetchPendingAsync(
                Enumerable.Range(1, 12).ToArray(), new Dictionary<int, TmdbDetails>(), directory, 1,
                client.GetAsync, CancellationToken.None));
            Assert.AreEqual(1, handler.Paths.Count);
            using var checkpoint = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "checkpoint-v2.json")));
            Assert.AreEqual(1, checkpoint.RootElement.GetProperty("nonterminalFailureCount").GetInt32());
            Assert.AreEqual("invalid_response", checkpoint.RootElement.GetProperty("failureClasses")[0].GetProperty("class").GetString());
            Assert.AreEqual(12, checkpoint.RootElement.GetProperty("unresolvedIds").GetArrayLength());
            Assert.IsFalse(File.ReadAllText(Path.Combine(directory, "checkpoint-v2.json")).Contains("fake-token", StringComparison.Ordinal));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task AttemptGuard_CompletedManifestRecordsConfiguredEnvelopeAndActualAttempts()
    {
        using var fixture = new TinyFixture();
        using var guard = new TmdbAttemptGuard(10, TimeSpan.FromMinutes(120));
        var handler = new FixtureHandler();
        using var http = new HttpClient(handler);
        var client = new TmdbDetailsClient(http, "fake-token", attemptGuard: guard);

        await EnrichmentV2Runner.RunAsync(fixture.Options("guarded-complete"), client.GetAsync, CancellationToken.None,
            fixture.TestHooks(), guard);

        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixture.PathFor("guarded-complete"), "manifest.json")));
        var policy = manifest.RootElement.GetProperty("requestPolicy");
        Assert.AreEqual(10, policy.GetProperty("globalHttpAttemptCap").GetInt32());
        Assert.AreEqual(120, policy.GetProperty("wallTimeLimitMinutes").GetDouble());
        Assert.AreEqual(1, policy.GetProperty("actualHttpAttempts").GetInt32());
        Assert.AreEqual(1, policy.GetProperty("attemptedTmdbIdCount").GetInt32());
    }

    [TestMethod]
    public async Task TinySnapshot_ExercisesJoinNullPolicyRatingsCacheAndManifestEndToEnd()
    {
        using var fixture = new TinyFixture();
        var handler = new FixtureHandler();
        using var http = new HttpClient(handler);
        var tmdb = new TmdbDetailsClient(http, "fake-token");
        var hooks = fixture.TestHooks();

        var result = await EnrichmentV2Runner.RunAsync(fixture.Options("out-one"), tmdb.GetAsync, CancellationToken.None, hooks);

        Assert.AreEqual(3, result.OutputRecords);
        Assert.AreEqual(1, handler.Paths.Count);
        Assert.AreEqual("/3/movie/101", handler.Paths.Single());
        var outputPath = Path.Combine(fixture.PathFor("out-one"), "movies-enriched.jsonl");
        var rows = File.ReadAllLines(outputPath).Select(line => JsonDocument.Parse(line)).ToArray();
        CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, rows.Select(x => x.RootElement.GetProperty("movieLensId").GetInt64()).ToArray());
        foreach (var field in new[] { "tmdbId", "genres", "averageRating", "ratingCount", "runtimeMinutes", "originalLanguage", "posterPath" })
            Assert.AreEqual(JsonValueKind.Null, rows[1].RootElement.GetProperty(field).ValueKind);
        CollectionAssert.AreEqual(new[] { "Comedy", "Drama" }, rows[0].RootElement.GetProperty("genres").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.AreEqual(2, rows[0].RootElement.GetProperty("ratingCount").GetInt32());
        Assert.AreEqual(2m, rows[0].RootElement.GetProperty("averageRating").GetDecimal());
        Assert.AreEqual(JsonValueKind.Null, rows[2].RootElement.GetProperty("tmdbId").ValueKind);
        Assert.AreEqual("Animation", rows[2].RootElement.GetProperty("genres")[0].GetString());
        Assert.AreEqual(0, rows[2].RootElement.GetProperty("ratingCount").GetInt32());
        Assert.AreEqual(JsonValueKind.Null, rows[2].RootElement.GetProperty("averageRating").ValueKind);
        Assert.IsFalse(File.ReadAllText(outputPath).Contains("overview", StringComparison.Ordinal));
        foreach (var row in rows) row.Dispose();

        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixture.PathFor("out-one"), "manifest.json")));
        var root = manifest.RootElement;
        Assert.AreEqual(HashFile(outputPath), root.GetProperty("output").GetProperty("sha256").GetString());
        Assert.AreEqual(2, root.GetProperty("counts").GetProperty("matched").GetInt32());
        Assert.AreEqual(1, root.GetProperty("counts").GetProperty("unmatched").GetInt32());
        Assert.AreEqual(2, root.GetProperty("counts").GetProperty("validRatingRows").GetInt64());
        Assert.AreEqual(2, root.GetProperty("counts").GetProperty("selectedRatingRows").GetInt64());
        Assert.AreEqual(1, root.GetProperty("counts").GetProperty("noTmdbIdSkipped").GetInt32());
        CollectionAssert.AreEqual(new[] { 101 }, root.GetProperty("tmdb").GetProperty("successIds").EnumerateArray().Select(x => x.GetInt32()).ToArray());
        Assert.AreEqual(1, root.GetProperty("tmdb").GetProperty("successCount").GetInt32());
        Assert.AreEqual(0, root.GetProperty("tmdb").GetProperty("notFoundCount").GetInt32());
        Assert.AreEqual(0, root.GetProperty("tmdb").GetProperty("nonterminalFailureCount").GetInt32());
        Assert.AreEqual(0, root.GetProperty("tmdb").GetProperty("nonterminalFailureIds").GetArrayLength());
        Assert.AreEqual("/3/movie/{tmdbId}", root.GetProperty("requestPolicy").GetProperty("endpoint").GetString());
        Assert.AreEqual(2, root.GetProperty("requestPolicy").GetProperty("maxConcurrency").GetInt32());
        Assert.AreEqual(HashFile(fixture.PathFor("b1a.jsonl")), root.GetProperty("inputs").GetProperty("b1aJsonl").GetProperty("sha256").GetString());
        Assert.AreEqual(HashCacheDirectory(fixture.PathFor("cache"), [101]), root.GetProperty("cacheSha256").GetString());

        var noNetwork = new FixtureHandler();
        using var cachedHttp = new HttpClient(noNetwork);
        var cachedTmdb = new TmdbDetailsClient(cachedHttp, "fake-token");
        var repeated = await EnrichmentV2Runner.RunAsync(fixture.Options("out-two"), cachedTmdb.GetAsync, CancellationToken.None, hooks);
        Assert.AreEqual(0, noNetwork.Paths.Count);
        Assert.AreEqual(result.OutputSha256, repeated.OutputSha256);
        using var repeatedManifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(fixture.PathFor("out-two"), "manifest.json")));
        Assert.AreEqual(root.GetProperty("contentFingerprint").GetString(), repeatedManifest.RootElement.GetProperty("contentFingerprint").GetString());
    }

    [TestMethod]
    public async Task CancellationAfterCacheSuccess_PreservesEntryAndResumeSkipsIt()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "R2-cancel-" + Guid.NewGuid().ToString("N"));
        var cache = new Dictionary<int, TmdbDetails>();
        using var cancellation = new CancellationTokenSource();
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var running = EnrichmentV2Runner.FetchPendingAsync([31, 32], cache, directory, 2, async (id, token) =>
            {
                if (id == 32)
                {
                    secondEntered.SetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                return new TmdbDetails(null, null, null, true);
            }, cancellation.Token);
            await secondEntered.Task;
            await WaitForFileAsync(Path.Combine(directory, "31.json"));
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => running);
            Assert.IsTrue(File.Exists(Path.Combine(directory, "31.json")));
            Assert.IsTrue(EnrichmentV2Runner.ReadCache(Path.Combine(directory, "31.json"), 31).Details.NotFound);
            using var checkpoint = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "checkpoint-v2.json")));
            CollectionAssert.AreEqual(new[] { 32 }, checkpoint.RootElement.GetProperty("unresolvedIds").EnumerateArray().Select(x => x.GetInt32()).ToArray());
            Assert.AreEqual(0, checkpoint.RootElement.GetProperty("nonterminalFailureCount").GetInt32());
            Assert.AreEqual(0, checkpoint.RootElement.GetProperty("failureClasses").GetArrayLength());
            var rerunCache = new Dictionary<int, TmdbDetails> { [31] = cache[31] };
            var rerunCalls = new List<int>();
            await EnrichmentV2Runner.FetchPendingAsync([32], rerunCache, directory, 1, (id, _) =>
            {
                rerunCalls.Add(id);
                return Task.FromResult(new TmdbDetails(null, null, null, true));
            }, CancellationToken.None);
            CollectionAssert.AreEqual(new[] { 32 }, rerunCalls);
            Assert.IsTrue(EnrichmentV2Runner.ReadCache(Path.Combine(directory, "31.json"), 31).Details.NotFound);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void RehashedWrongVersionShapeAndInvalidValueRemainRejected()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "R2-rehash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            EnrichmentV2Runner.WriteCache(directory, 41, new TmdbDetails(90, "en", "/poster.jpg"));
            var path = Path.Combine(directory, "41.json");
            var cache = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

            var wrongVersion = (JsonObject)cache.DeepClone();
            wrongVersion["version"] = "tmdb-cache-v1";
            Reseal(wrongVersion);
            File.WriteAllText(path, wrongVersion.ToJsonString());
            Assert.ThrowsExactly<ExportValidationException>(() => EnrichmentV2Runner.ReadCache(path, 41));

            var wrongKey = (JsonObject)cache.DeepClone();
            wrongKey["tmdbId"] = 42;
            Reseal(wrongKey);
            File.WriteAllText(path, wrongKey.ToJsonString());
            Assert.ThrowsExactly<ExportValidationException>(() => EnrichmentV2Runner.ReadCache(path, 41));

            var wrongShape = (JsonObject)cache.DeepClone();
            wrongShape["overview"] = "discard";
            Reseal(wrongShape);
            File.WriteAllText(path, wrongShape.ToJsonString());
            Assert.ThrowsExactly<ExportValidationException>(() => EnrichmentV2Runner.ReadCache(path, 41));

            var invalidValue = (JsonObject)cache.DeepClone();
            invalidValue["runtimeMinutes"] = 0;
            Reseal(invalidValue);
            File.WriteAllText(path, invalidValue.ToJsonString());
            Assert.ThrowsExactly<ExportValidationException>(() => EnrichmentV2Runner.ReadCache(path, 41));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task RunnerPublishFaultAfterStaging_CleansStageAndPreservesPriorFinal()
    {
        using var fixture = new TinyFixture();
        var prior = fixture.PathFor("prior-final");
        Directory.CreateDirectory(prior);
        File.WriteAllText(Path.Combine(prior, "sentinel.txt"), "preserve-me");
        var final = fixture.PathFor("new-final");
        string? staged = null;
        var handler = new FixtureHandler();
        using var http = new HttpClient(handler);
        var tmdb = new TmdbDetailsClient(http, "fake-token");
        var hooks = fixture.TestHooks() with
        {
            BeforePublish = (stage, _) =>
            {
                staged = stage;
                Assert.IsTrue(File.Exists(Path.Combine(stage, "movies-enriched.jsonl")));
                Assert.IsTrue(File.Exists(Path.Combine(stage, "manifest.json")));
                throw new IOException("injected runner publish fault");
            }
        };
        await Assert.ThrowsExactlyAsync<IOException>(() => EnrichmentV2Runner.RunAsync(fixture.Options("new-final"), tmdb.GetAsync, CancellationToken.None, hooks));
        Assert.IsFalse(Directory.Exists(final));
        Assert.IsNotNull(staged);
        Assert.IsFalse(Directory.Exists(staged));
        Assert.AreEqual("preserve-me", File.ReadAllText(Path.Combine(prior, "sentinel.txt")));
    }

    [TestMethod]
    public void CacheFaultAfterAtomicMoveLeavesOnlyACompleteReusableEntry()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "R2-post-move-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.ThrowsExactly<IOException>(() => EnrichmentV2Runner.WriteCache(directory, 55, new TmdbDetails(110, "en", "/p.jpg"),
                afterAtomicMove: () => throw new IOException("injected after cache rename")));
            Assert.IsTrue(File.Exists(Path.Combine(directory, "55.json")));
            var complete = EnrichmentV2Runner.ReadCache(Path.Combine(directory, "55.json"), 55);
            Assert.AreEqual(110, complete.Details.RuntimeMinutes);
            Assert.AreEqual(0, Directory.GetFiles(directory, "*.tmp").Length);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static void Reseal(JsonObject cache)
    {
        var payload = new JsonObject
        {
            ["version"] = cache["version"]!.DeepClone(),
            ["tmdbId"] = cache["tmdbId"]!.DeepClone(),
            ["status"] = cache["status"]!.DeepClone(),
            ["runtimeMinutes"] = cache["runtimeMinutes"]!.DeepClone(),
            ["originalLanguage"] = cache["originalLanguage"]!.DeepClone(),
            ["posterPath"] = cache["posterPath"]!.DeepClone()
        };
        var canonical = JsonSerializer.SerializeToUtf8Bytes(payload);
        cache["sha256"] = Convert.ToHexStringLower(SHA256.HashData(canonical));
    }

    private static async Task WaitForFileAsync(string path)
    {
        for (var i = 0; i < 500 && !File.Exists(path); i++) await Task.Delay(10);
        Assert.IsTrue(File.Exists(path), "The successful cache write did not complete before cancellation.");
    }

    private static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private static string HashCacheDirectory(string directory, IEnumerable<int> ids) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", ids.Order().Select(id => $"{id}:{HashFile(Path.Combine(directory, $"{id}.json"))}")))));

    private sealed class TinyFixture : IDisposable
    {
        private readonly string _root = Path.Combine(AppContext.BaseDirectory, "R2-tiny-" + Guid.NewGuid().ToString("N"));
        private readonly string _b1a;
        private readonly string _manifest;
        private readonly string _ml;
        private readonly string _cache;

        public TinyFixture()
        {
            _b1a = Path.Combine(_root, "b1a.jsonl");
            _manifest = Path.Combine(_root, "b1a-manifest.json");
            _ml = Path.Combine(_root, "ml-32m");
            _cache = Path.Combine(_root, "cache");
            Directory.CreateDirectory(_ml);
            var rows = new[]
            {
                new { movieLensId = 1, rawTitle = "One (2000)", title = "One", year = 2000, imdbId = "0001001", movieLensAvgRating = 3.5m, directedByRaw = (string?)null, starringRaw = (string?)null },
                new { movieLensId = 2, rawTitle = "Two (2001)", title = "Two", year = 2001, imdbId = "0001002", movieLensAvgRating = 3m, directedByRaw = (string?)null, starringRaw = (string?)null },
                new { movieLensId = 3, rawTitle = "Three (2002)", title = "Three", year = 2002, imdbId = "0001003", movieLensAvgRating = 2m, directedByRaw = (string?)null, starringRaw = (string?)null }
            };
            File.WriteAllText(_b1a, string.Concat(rows.Select(row => JsonSerializer.Serialize(row) + "\n")), new UTF8Encoding(false, true));
            File.WriteAllText(_manifest, JsonSerializer.Serialize(new
            {
                mappingVersion = "B1a-v1",
                output = new { sha256 = HashFile(_b1a) },
                counts = new { outputRecords = rows.Length }
            }), new UTF8Encoding(false, true));
            File.WriteAllText(Path.Combine(_ml, "links.csv"), "movieId,imdbId,tmdbId\n1,1001,101\n3,1003,\n", new UTF8Encoding(false, true));
            File.WriteAllText(Path.Combine(_ml, "movies.csv"), "movieId,title,genres\n1,One,Comedy|Drama\n3,Three,Animation\n", new UTF8Encoding(false, true));
            File.WriteAllText(Path.Combine(_ml, "ratings.csv"), "userId,movieId,rating,timestamp\n10,1,0,100\n11,1,4,200\n", new UTF8Encoding(false, true));
            var checksums = new[] { "links.csv", "movies.csv", "ratings.csv" }.Select(name => $"{Md5(Path.Combine(_ml, name))}  {name}");
            File.WriteAllLines(Path.Combine(_ml, "checksums.txt"), checksums, new UTF8Encoding(false, true));
        }

        public string PathFor(string name) => Path.Combine(_root, name);
        public EnrichmentV2Arguments Options(string outputName) => new(_b1a, _manifest, _ml, _cache, PathFor(outputName), 2, new HashSet<int>());
        public EnrichmentV2TestHooks TestHooks() => new(new EnrichmentV2ExpectedSnapshot(
            HashFile(_b1a), HashFile(_manifest), Md5(Path.Combine(_ml, "links.csv")), Md5(Path.Combine(_ml, "movies.csv")), Md5(Path.Combine(_ml, "ratings.csv")),
            3, 2, 2, 2, 2, 1, 1, 1, 2));
        private static string Md5(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(MD5.HashData(stream)); }
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;
        private readonly System.Collections.Concurrent.ConcurrentBag<string> _paths = [];
        public FixtureHandler(Func<HttpRequestMessage, HttpResponseMessage>? response = null) => _response = response ?? (request =>
        {
            if (request.RequestUri!.AbsolutePath != "/3/movie/101") throw new InvalidOperationException("Unexpected TMDB request path.");
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":101,\"runtime\":95,\"original_language\":\"en\",\"poster_path\":\"/poster.jpg\",\"overview\":\"must not persist\"}")
            };
        });
        public IReadOnlyCollection<string> Paths => _paths.ToArray();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(_response(request));
        }
    }
}
