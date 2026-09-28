using CineKros.Etl;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text;

namespace CineKros.Etl.Tests;

[TestClass]
public sealed class EnrichmentV2Tests
{
    [TestMethod]
    public void EnrichmentArguments_ValidateModesAndRefreshIds()
    {
        var root = Path.GetFullPath(Path.GetTempPath());
        var parsed = EnrichmentV2Arguments.Parse(["--input-jsonl", Path.Combine(root, "in.jsonl"), "--input-manifest", Path.Combine(root, "manifest.json"), "--ml32m-root", root, "--cache-dir", Path.Combine(root, "cache"), "--output-dir", Path.Combine(root, "out"), "--max-concurrency", "4", "--refresh-tmdb", "42"]);
        Assert.AreEqual(4, parsed.MaxConcurrency);
        Assert.IsTrue(parsed.RefreshTmdbIds.Contains(42));
        Assert.ThrowsExactly<ArgumentException>(() => EnrichmentV2Arguments.Parse(["--input-jsonl", Path.Combine(root, "in"), "--input-jsonl", Path.Combine(root, "in")]));
        Assert.ThrowsExactly<ArgumentException>(() => EnrichmentV2Arguments.Parse(["--input-jsonl", Path.Combine(root, "in.jsonl"), "--input-manifest", Path.Combine(root, "manifest.json"), "--ml32m-root", root, "--cache-dir", Path.Combine(root, "cache"), "--output-dir", Path.Combine(root, "out"), "--max-concurrency", "2", "--max-concurrency", "3"]));
    }

    [TestMethod]
    public void EnrichmentArguments_ValidatesGlobalAttemptCapAndKeepsDefault()
    {
        var root = Path.GetFullPath(Path.GetTempPath());
        var required = new[] { "--input-jsonl", Path.Combine(root, "in.jsonl"), "--input-manifest", Path.Combine(root, "manifest.json"),
            "--ml32m-root", root, "--cache-dir", Path.Combine(root, "cache"), "--output-dir", Path.Combine(root, "out") };
        Assert.AreEqual(10_000, EnrichmentV2Arguments.Parse(required).MaxHttpAttempts);
        Assert.AreEqual(6_000, EnrichmentV2Arguments.Parse([.. required, "--max-http-attempts", "6000"]).MaxHttpAttempts);
        foreach (var value in new[] { "0", "-1", "10001", "x" })
            Assert.ThrowsExactly<ArgumentException>(() => EnrichmentV2Arguments.Parse([.. required, "--max-http-attempts", value]));
        Assert.ThrowsExactly<ArgumentException>(() => EnrichmentV2Arguments.Parse([.. required, "--max-http-attempts", "6000", "--max-http-attempts", "5000"]));
        Assert.ThrowsExactly<ArgumentException>(() => EnrichmentV2Arguments.Parse([.. required, "--max-http-attempts"]));
    }

    [TestMethod]
    public async Task EnrichmentArguments_ConfiguredAttemptCapReachesHttpTransport()
    {
        var root = Path.GetFullPath(Path.GetTempPath());
        var options = EnrichmentV2Arguments.Parse(["--input-jsonl", Path.Combine(root, "in.jsonl"), "--input-manifest", Path.Combine(root, "manifest.json"),
            "--ml32m-root", root, "--cache-dir", Path.Combine(root, "cache"), "--output-dir", Path.Combine(root, "out"), "--max-http-attempts", "2"]);
        using var guard = new TmdbAttemptGuard(options.MaxHttpAttempts);
        var handler = new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
        using var http = new HttpClient(handler);
        var client = new TmdbDetailsClient(http, "fake-token", (_, _) => Task.CompletedTask, guard);

        await Assert.ThrowsExactlyAsync<TmdbRequestException>(() => client.GetAsync(42, CancellationToken.None));

        Assert.AreEqual(2, handler.Requests.Count);
        Assert.AreEqual(2, guard.AttemptCount);
        Assert.AreEqual("attempt_cap", guard.StopReason);
    }

    [TestMethod]
    public void Ratings_StreamsExactSelectedCountsSumsAndValidZero()
    {
        var path = Path.Combine(Path.GetTempPath(), "CineKros.Etl.Tests", Guid.NewGuid().ToString("N"), "ratings.csv");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllText(path, "userId,movieId,rating,timestamp\n1,7,0,100\n2,7,4.5,200\n3,8,3,300\n", new UTF8Encoding(false, true));
            var selected = new HashSet<long> { 7, 9 };
            var result = EnrichmentV2Runner.ReadRatings(path, selected, CancellationToken.None, 3);
            Assert.AreEqual(3, result.ValidRows);
            Assert.AreEqual(2, result.Aggregates[7].Count);
            Assert.AreEqual(4.5m, result.Aggregates[7].Sum);
            Assert.AreEqual(2, result.Aggregates.Values.Sum(x => x.Count));
            Assert.IsFalse(result.Aggregates.ContainsKey(9));
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [TestMethod]
    public void JoinFixtures_UseExactIdsCrossCheckImdbAndPreserveGenreOrderAndSentinel()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CineKros.Etl.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var linksPath = Path.Combine(directory, "links.csv");
            File.WriteAllText(linksPath, "movieId,imdbId,tmdbId\n7,0000042,42\n8,0000043,\n", new UTF8Encoding(false, true));
            var links = EnrichmentV2Runner.ReadLinks(linksPath);
            Assert.IsTrue(links.ContainsKey(7));
            Assert.IsFalse(links.ContainsKey(70));
            Assert.AreEqual(42, links[7].TmdbId);
            Assert.IsNull(links[8].TmdbId);
            Assert.IsTrue(EnrichmentV2Runner.ImdbMatches("42", links[7].ImdbId));
            Assert.IsFalse(EnrichmentV2Runner.ImdbMatches("43", links[7].ImdbId));

            var moviesPath = Path.Combine(directory, "movies.csv");
            File.WriteAllText(moviesPath, "movieId,title,genres\n7,\"A, title\",\"Drama|Comedy\"\n8,Empty,(no genres listed)\n", new UTF8Encoding(false, true));
            var movies = EnrichmentV2Runner.ReadGenres(moviesPath);
            CollectionAssert.AreEqual(new[] { "Drama", "Comedy" }, movies[7].Genres!);
            Assert.IsNull(movies[8].Genres);

            File.WriteAllText(linksPath, "movieId,imdbId,tmdbId\n7,0000042,42\n7,0000042,42\n", new UTF8Encoding(false, true));
            Assert.ThrowsExactly<ExportValidationException>(() => EnrichmentV2Runner.ReadLinks(linksPath));
            File.WriteAllText(moviesPath, "movieId,title,genres\n7,First,Drama\n7,Second,Comedy\n", new UTF8Encoding(false, true));
            Assert.ThrowsExactly<ExportValidationException>(() => EnrichmentV2Runner.ReadGenres(moviesPath));
            File.WriteAllText(moviesPath, "movieId,title,genres\n7,Duplicate,\"Drama|Drama\"\n", new UTF8Encoding(false, true));
            Assert.ThrowsExactly<ExportValidationException>(() => EnrichmentV2Runner.ReadGenres(moviesPath));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void TmdbCache_ValidatesHashVersionAndKey()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CineKros.Etl.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            EnrichmentV2Runner.WriteCache(directory, 42, new TmdbDetails(101, "en", "/poster.jpg"));
            var path = Path.Combine(directory, "42.json");
            var entry = EnrichmentV2Runner.ReadCache(path, 42);
            Assert.AreEqual(101, entry.Details.RuntimeMinutes);
            Assert.ThrowsExactly<ExportValidationException>(() => EnrichmentV2Runner.ReadCache(path, 43));

            var valid = File.ReadAllText(path);
            File.WriteAllText(path, valid.Replace("\"version\":\"tmdb-cache-v2\"", "\"version\":\"tmdb-cache-v1\"", StringComparison.Ordinal));
            Assert.ThrowsExactly<ExportValidationException>(() => EnrichmentV2Runner.ReadCache(path, 42));
            File.WriteAllText(path, valid.Replace("\"runtimeMinutes\":101", "\"runtimeMinutes\":0", StringComparison.Ordinal));
            Assert.ThrowsExactly<ExportValidationException>(() => EnrichmentV2Runner.ReadCache(path, 42));
            File.WriteAllText(path, valid.Replace("\"runtimeMinutes\":101", "\"runtimeMinutes\":102", StringComparison.Ordinal));
            Assert.ThrowsExactly<ExportValidationException>(() => EnrichmentV2Runner.ReadCache(path, 42));
            File.WriteAllText(path, valid[..^1] + ",\"overview\":\"not allowed\"}");
            Assert.ThrowsExactly<ExportValidationException>(() => EnrichmentV2Runner.ReadCache(path, 42));
            File.AppendAllText(path, "{}");
            Assert.Throws<JsonException>(() => EnrichmentV2Runner.ReadCache(path, 42));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void CacheWriteFailureLeavesNoReusablePartialEntry()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CineKros.Etl.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Assert.ThrowsExactly<IOException>(() => EnrichmentV2Runner.WriteCache(directory, 42, new TmdbDetails(101, "en", "/poster.jpg"), () => throw new IOException("injected cache publish failure")));
            Assert.IsFalse(File.Exists(Path.Combine(directory, "42.json")));
            Assert.AreEqual(0, Directory.GetFiles(directory, "*.tmp").Length);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public void FinalPublishRenamesCompletePairOrLeavesFinalAbsent()
    {
        var root = Path.Combine(Path.GetTempPath(), "CineKros.Etl.Tests", Guid.NewGuid().ToString("N"));
        var stage = Path.Combine(root, "stage");
        var output = Path.Combine(root, "output");
        Directory.CreateDirectory(stage);
        try
        {
            File.WriteAllText(Path.Combine(stage, "movies-enriched.jsonl"), "{}\n");
            File.WriteAllText(Path.Combine(stage, "manifest.json"), "{}\n");
            Assert.ThrowsExactly<IOException>(() => EnrichmentV2Runner.PublishStageDirectory(stage, output, () => throw new IOException("injected publish failure")));
            Assert.IsFalse(Directory.Exists(output));
            Assert.IsTrue(File.Exists(Path.Combine(stage, "movies-enriched.jsonl")));
            Assert.IsTrue(File.Exists(Path.Combine(stage, "manifest.json")));
            EnrichmentV2Runner.PublishStageDirectory(stage, output);
            Assert.IsTrue(File.Exists(Path.Combine(output, "movies-enriched.jsonl")));
            Assert.IsTrue(File.Exists(Path.Combine(output, "manifest.json")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task Scheduler_StopsOnAuthFailureAndCheckpointsUnresolvedIds()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CineKros.Etl.Tests", Guid.NewGuid().ToString("N"));
        var cache = new Dictionary<int, TmdbDetails>();
        var calls = 0;
        try
        {
            await Assert.ThrowsExactlyAsync<TmdbRequestException>(() => EnrichmentV2Runner.FetchPendingAsync(
                Enumerable.Range(1, 10).ToArray(), cache, directory, 1,
                (_, _) => { Interlocked.Increment(ref calls); throw new TmdbConfigurationException("fake authorization failure"); }, CancellationToken.None));
            Assert.AreEqual(1, calls);
            using var checkpoint = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "checkpoint-v2.json")));
            CollectionAssert.AreEqual(Enumerable.Range(1, 10).ToArray(), checkpoint.RootElement.GetProperty("nonterminalFailureIds").EnumerateArray().Select(x => x.GetInt32()).ToArray());
            CollectionAssert.AreEqual(Enumerable.Range(1, 10).ToArray(), checkpoint.RootElement.GetProperty("unresolvedIds").EnumerateArray().Select(x => x.GetInt32()).ToArray());
            Assert.AreEqual(1, checkpoint.RootElement.GetProperty("nonterminalFailureCount").GetInt32());
            var failure = checkpoint.RootElement.GetProperty("failureClasses")[0];
            Assert.AreEqual("configuration", failure.GetProperty("class").GetString());
            Assert.AreEqual(1, failure.GetProperty("count").GetInt32());
            CollectionAssert.AreEqual(new[] { 1 }, failure.GetProperty("tmdbIds").EnumerateArray().Select(x => x.GetInt32()).ToArray());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task Scheduler_CancellationPreservesSuccessfulCacheAndWritesResumeCheckpoint()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CineKros.Etl.Tests", Guid.NewGuid().ToString("N"));
        var cache = new Dictionary<int, TmdbDetails>();
        using var cancellation = new CancellationTokenSource();
        using var saved = new ManualResetEventSlim();
        var fetchedAfterCancellation = 0;
        try
        {
            var running = EnrichmentV2Runner.FetchPendingAsync([11, 12], cache, directory, 2, (id, _) =>
            {
                if (cancellation.IsCancellationRequested) Interlocked.Increment(ref fetchedAfterCancellation);
                return Task.FromResult(new TmdbDetails(null, null, null, true));
            }, cancellation.Token,
                afterCacheMove: id => { if (id == 11) saved.Set(); },
                afterGateAcquired: id =>
                {
                    if (id == 12)
                    {
                        saved.Wait();
                        cancellation.Cancel();
                    }
                });
            await Assert.ThrowsAsync<OperationCanceledException>(() => running);
            CollectionAssert.AreEqual(new[] { 12 }, JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "checkpoint-v2.json"))).RootElement.GetProperty("nonterminalFailureIds").EnumerateArray().Select(x => x.GetInt32()).ToArray());
            Assert.AreEqual(0, fetchedAfterCancellation);
            Assert.IsTrue(File.Exists(Path.Combine(directory, "11.json")));
            Assert.IsFalse(File.Exists(Path.Combine(directory, "12.json")));
            Assert.AreEqual(1, cache.Count);
            Assert.IsTrue(cache.ContainsKey(11));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task Scheduler_NonterminalFailureLeavesNoEntryAndResumesFromCheckpoint()
    {
        var directory = Path.Combine(Path.GetTempPath(), "CineKros.Etl.Tests", Guid.NewGuid().ToString("N"));
        var cache = new Dictionary<int, TmdbDetails>();
        try
        {
            await Assert.ThrowsExactlyAsync<TmdbRequestException>(() => EnrichmentV2Runner.FetchPendingAsync([1, 2], cache, directory, 1,
                (id, _) => id == 1
                    ? Task.FromException<TmdbDetails>(new TmdbRequestException("fake transient failure"))
                    : Task.FromResult(new TmdbDetails(null, null, null, true)), CancellationToken.None));
            Assert.IsFalse(File.Exists(Path.Combine(directory, "1.json")));
            Assert.IsTrue(File.Exists(Path.Combine(directory, "2.json")));
            using (var checkpoint = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "checkpoint-v2.json"))))
            {
                CollectionAssert.AreEqual(new[] { 1 }, checkpoint.RootElement.GetProperty("nonterminalFailureIds").EnumerateArray().Select(x => x.GetInt32()).ToArray());
                Assert.AreEqual("transient", checkpoint.RootElement.GetProperty("failureClasses")[0].GetProperty("class").GetString());
                Assert.AreEqual(1, checkpoint.RootElement.GetProperty("nonterminalFailureCount").GetInt32());
            }

            await EnrichmentV2Runner.FetchPendingAsync([1], cache, directory, 1,
                (_, _) => Task.FromResult(new TmdbDetails(90, "en", "/resumed.jpg")), CancellationToken.None);
            Assert.IsTrue(File.Exists(Path.Combine(directory, "1.json")));
            using var resumed = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "checkpoint-v2.json")));
            Assert.AreEqual(0, resumed.RootElement.GetProperty("nonterminalFailureIds").GetArrayLength());
            Assert.AreEqual(90, cache[1].RuntimeMinutes);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task TmdbDetails_RequestsOnlyDirectMovieIdEndpoint()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":42,\"runtime\":101,\"original_language\":\"en\",\"poster_path\":\"/poster.jpg\",\"overview\":\"must not persist\"}")
        });
        using var client = new HttpClient(handler);
        var adapter = new TmdbDetailsClient(client, "fake-token");

        var details = await adapter.GetAsync(42, CancellationToken.None);

        Assert.AreEqual("/3/movie/42", handler.Requests.Single().AbsolutePath);
        Assert.AreEqual(101, details.RuntimeMinutes);
        Assert.AreEqual("en", details.OriginalLanguage);
        Assert.AreEqual("/poster.jpg", details.PosterPath);
    }

    [TestMethod]
    public async Task TmdbDetails_NormalizesRuntimeZeroButStillRejectsNegativeAndNonnumericValues()
    {
        var zero = new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":42,\"runtime\":0,\"original_language\":\"en\",\"poster_path\":\"/poster.jpg\"}")
        });
        using (var client = new HttpClient(zero))
        {
            var details = await new TmdbDetailsClient(client, "fake-token").GetAsync(42, CancellationToken.None);
            Assert.IsNull(details.RuntimeMinutes);
            Assert.AreEqual("en", details.OriginalLanguage);
            Assert.AreEqual("/poster.jpg", details.PosterPath);
        }

        foreach (var runtime in new[] { "-1", "\"0\"" })
        {
            var invalid = new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"id\":42,\"runtime\":{runtime}}}")
            });
            using var client = new HttpClient(invalid);
            var exception = await Assert.ThrowsExactlyAsync<TmdbRequestException>(() => new TmdbDetailsClient(client, "fake-token").GetAsync(42, CancellationToken.None));
            Assert.AreEqual("invalid_response", exception.FailureClass);
        }
    }

    [TestMethod]
    public async Task TmdbDetails_404IsTerminalAndWrongIdIsRejected()
    {
        var notFound = new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
        using (var client = new HttpClient(notFound))
        {
            var adapter = new TmdbDetailsClient(client, "fake-token");
            var details = await adapter.GetAsync(8, CancellationToken.None);
            Assert.IsTrue(details.NotFound);
            Assert.AreEqual(1, notFound.Requests.Count);
        }

        var mismatched = new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":9,\"runtime\":null,\"original_language\":null,\"poster_path\":null}")
        });
        using var mismatchClient = new HttpClient(mismatched);
        var mismatchAdapter = new TmdbDetailsClient(mismatchClient, "fake-token");
        await Assert.ThrowsAsync<TmdbRequestException>(() => mismatchAdapter.GetAsync(8, CancellationToken.None));
    }

    [TestMethod]
    public async Task TmdbDetails_HonorsRetryAfterAndLimitsTransientAttempts()
    {
        var attempt = 0;
        var handler = new RecordingHandler(request =>
        {
            if (attempt++ == 0)
            {
                var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(250));
                return response;
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"id\":42}")
            };
        });
        var delays = new List<TimeSpan>();
        using var client = new HttpClient(handler);
        var adapter = new TmdbDetailsClient(client, "fake-token", (delay, _) => { delays.Add(delay); return Task.CompletedTask; });
        _ = await adapter.GetAsync(42, CancellationToken.None);
        Assert.AreEqual(2, handler.Requests.Count);
        Assert.AreEqual(TimeSpan.FromMilliseconds(250), delays.Single());

        var dateRetry = new RecordingHandler(_ =>
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMilliseconds(10));
            return response;
        });
        var dateDelays = new List<TimeSpan>();
        using var dateClient = new HttpClient(dateRetry);
        var dateAdapter = new TmdbDetailsClient(dateClient, "fake-token", (delay, _) => { dateDelays.Add(delay); return Task.CompletedTask; });
        await Assert.ThrowsExactlyAsync<TmdbRequestException>(() => dateAdapter.GetAsync(42, CancellationToken.None));
        Assert.IsTrue(dateDelays.All(delay => delay < TimeSpan.FromSeconds(1)), "A valid Retry-After date must not use the default backoff.");
    }

    [TestMethod]
    public async Task TmdbDetails_UsesDefaultBackoffAndStopsOnConfigurationFailure()
    {
        var attempt = 0;
        var handler = new RecordingHandler(_ =>
        {
            if (attempt++ < 2) return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"id\":42}") };
        });
        var delays = new List<TimeSpan>();
        using (var client = new HttpClient(handler))
        {
            var adapter = new TmdbDetailsClient(client, "fake-token", (delay, _) => { delays.Add(delay); return Task.CompletedTask; });
            _ = await adapter.GetAsync(42, CancellationToken.None);
        }
        Assert.AreEqual(3, handler.Requests.Count);
        CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }, delays);

        var forbidden = new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.Forbidden));
        using var forbiddenClient = new HttpClient(forbidden);
        var forbiddenAdapter = new TmdbDetailsClient(forbiddenClient, "fake-token");
        await Assert.ThrowsExactlyAsync<TmdbConfigurationException>(() => forbiddenAdapter.GetAsync(42, CancellationToken.None));
        Assert.AreEqual(1, forbidden.Requests.Count);
    }

    [TestMethod]
    public async Task TmdbDetails_RetriesNetworkFaultsButNotOtherClientErrors()
    {
        var attempt = 0;
        var network = new RecordingHandler(_ =>
        {
            if (attempt++ < 2) throw new HttpRequestException("fake transport fault");
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"id\":7}") };
        });
        var delays = new List<TimeSpan>();
        using (var client = new HttpClient(network))
        {
            var adapter = new TmdbDetailsClient(client, "fake-token", (delay, _) => { delays.Add(delay); return Task.CompletedTask; });
            _ = await adapter.GetAsync(7, CancellationToken.None);
        }
        Assert.AreEqual(3, network.Requests.Count);
        CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }, delays);

        var timeoutAttempt = 0;
        var timeouts = new RecordingHandler(_ =>
        {
            if (timeoutAttempt++ < 2) throw new OperationCanceledException("simulated attempt timeout");
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("{\"id\":7}") };
        });
        var timeoutDelays = new List<TimeSpan>();
        using (var timeoutClient = new HttpClient(timeouts))
        {
            var timeoutAdapter = new TmdbDetailsClient(timeoutClient, "fake-token", (delay, _) => { timeoutDelays.Add(delay); return Task.CompletedTask; });
            _ = await timeoutAdapter.GetAsync(7, CancellationToken.None);
        }
        Assert.AreEqual(3, timeouts.Requests.Count);
        CollectionAssert.AreEqual(new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2) }, timeoutDelays);

        var badRequest = new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest));
        using var badRequestClient = new HttpClient(badRequest);
        var badRequestAdapter = new TmdbDetailsClient(badRequestClient, "fake-token");
        await Assert.ThrowsExactlyAsync<TmdbRequestException>(() => badRequestAdapter.GetAsync(7, CancellationToken.None));
        Assert.AreEqual(1, badRequest.Requests.Count);
    }

    [TestMethod]
    [TestCategory("FullSourceFake")]
    public async Task FullAcceptedSnapshot_StreamsAndPublishesUsingFakeHttpOnly()
    {
        var canonicalRoot = Environment.GetEnvironmentVariable("CINEKROS_CANONICAL_ROOT");
        if (string.IsNullOrWhiteSpace(canonicalRoot)) Assert.Inconclusive("Set CINEKROS_CANONICAL_ROOT to run the local full-source fake verification.");
        var output = Path.Combine(AppContext.BaseDirectory, "B04V2-R1-output-" + Guid.NewGuid().ToString("N"));
        var cache = Path.Combine(AppContext.BaseDirectory, "B04V2-R1-cache-" + Guid.NewGuid().ToString("N"));
        var input = Path.Combine(canonicalRoot, "database", "data", "derived", "b1a-v1");
        var handler = new ConcurrentNotFoundHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.themoviedb.org") };
        var tmdb = new TmdbDetailsClient(http, "fake-token");
        var options = new EnrichmentV2Arguments(
            Path.Combine(input, "movies-metadata.jsonl"), Path.Combine(input, "manifest.json"),
            Path.Combine(canonicalRoot, "database", "ml-32m"), cache, output, 4, new HashSet<int>());

        var result = await EnrichmentV2Runner.RunAsync(options, tmdb.GetAsync, CancellationToken.None);

        Assert.AreEqual(9730, result.OutputRecords);
        Assert.AreEqual(9577, handler.RequestCount);
        Assert.IsTrue(handler.MaximumConcurrency >= 4, $"Observed concurrency {handler.MaximumConcurrency}.");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(output, "manifest.json")));
        Assert.AreEqual(9585, manifest.RootElement.GetProperty("counts").GetProperty("matched").GetInt32());
        Assert.AreEqual(145, manifest.RootElement.GetProperty("counts").GetProperty("unmatched").GetInt32());
        Assert.AreEqual(32000204, manifest.RootElement.GetProperty("counts").GetProperty("validRatingRows").GetInt64());
        Assert.AreEqual(28604436, manifest.RootElement.GetProperty("counts").GetProperty("selectedRatingRows").GetInt64());
        var outputPath = Path.Combine(output, "movies-enriched.jsonl");
        using var unmatched = JsonDocument.Parse(File.ReadLines(outputPath).First(line =>
        {
            using var row = JsonDocument.Parse(line);
            return row.RootElement.GetProperty("movieLensId").GetInt64() == 291;
        }));
        foreach (var field in new[] { "tmdbId", "genres", "averageRating", "ratingCount", "runtimeMinutes", "originalLanguage", "posterPath" })
            Assert.AreEqual(JsonValueKind.Null, unmatched.RootElement.GetProperty(field).ValueKind, $"Unmatched movie field {field}.");
        using var blankTmdb = JsonDocument.Parse(File.ReadLines(outputPath).First(line =>
        {
            using var row = JsonDocument.Parse(line);
            return row.RootElement.GetProperty("movieLensId").GetInt64() == 791;
        }));
        Assert.AreEqual(JsonValueKind.Null, blankTmdb.RootElement.GetProperty("tmdbId").ValueKind);
        Assert.AreNotEqual(JsonValueKind.Null, blankTmdb.RootElement.GetProperty("genres").ValueKind);
        Assert.AreNotEqual(JsonValueKind.Null, blankTmdb.RootElement.GetProperty("averageRating").ValueKind);
        Assert.AreNotEqual(JsonValueKind.Null, blankTmdb.RootElement.GetProperty("ratingCount").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, blankTmdb.RootElement.GetProperty("runtimeMinutes").ValueKind);
        long countsInOutput = 0;
        foreach (var line in File.ReadLines(outputPath))
        {
            using var row = JsonDocument.Parse(line);
            if (row.RootElement.GetProperty("ratingCount").ValueKind == JsonValueKind.Number) countsInOutput += row.RootElement.GetProperty("ratingCount").GetInt64();
        }
        Assert.AreEqual(28604436, countsInOutput);

        var secondOutput = Path.Combine(AppContext.BaseDirectory, "B04V2-R1-cache-hit-output-" + Guid.NewGuid().ToString("N"));
        var noNetworkHandler = new RejectingHandler();
        using var noNetworkClient = new HttpClient(noNetworkHandler) { BaseAddress = new Uri("https://api.themoviedb.org") };
        var cachedClient = new TmdbDetailsClient(noNetworkClient, "fake-token");
        var cacheHitOptions = options with { OutputDirectory = secondOutput };
        var repeated = await EnrichmentV2Runner.RunAsync(cacheHitOptions, cachedClient.GetAsync, CancellationToken.None);
        Assert.AreEqual(0, noNetworkHandler.RequestCount);
        Assert.AreEqual(result.OutputSha256, repeated.OutputSha256);
        using var repeatedManifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(secondOutput, "manifest.json")));
        Assert.AreEqual(manifest.RootElement.GetProperty("contentFingerprint").GetString(), repeatedManifest.RootElement.GetProperty("contentFingerprint").GetString());

        using var firstMovie = JsonDocument.Parse(File.ReadLines(outputPath).First());
        var refreshId = firstMovie.RootElement.GetProperty("tmdbId").GetInt32();
        var refreshMovieLensId = firstMovie.RootElement.GetProperty("movieLensId").GetInt64();
        var refreshOutput = Path.Combine(AppContext.BaseDirectory, "B04V2-R1-refresh-output-" + Guid.NewGuid().ToString("N"));
        var refreshHandler = new RecordingHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent($"{{\"id\":{refreshId},\"runtime\":101,\"original_language\":\"en\",\"poster_path\":\"/poster.jpg\",\"overview\":\"discard\"}}")
        });
        using var refreshHttp = new HttpClient(refreshHandler) { BaseAddress = new Uri("https://api.themoviedb.org") };
        var refreshClient = new TmdbDetailsClient(refreshHttp, "fake-token");
        var refreshOptions = options with { OutputDirectory = refreshOutput, RefreshTmdbIds = new HashSet<int> { refreshId } };
        _ = await EnrichmentV2Runner.RunAsync(refreshOptions, refreshClient.GetAsync, CancellationToken.None);
        Assert.AreEqual(1, refreshHandler.Requests.Count);
        var refreshedLine = File.ReadLines(Path.Combine(refreshOutput, "movies-enriched.jsonl")).First(line =>
        {
            using var row = JsonDocument.Parse(line);
            return row.RootElement.GetProperty("movieLensId").GetInt64() == refreshMovieLensId;
        });
        using var refreshed = JsonDocument.Parse(refreshedLine);
        Assert.AreEqual(101, refreshed.RootElement.GetProperty("runtimeMinutes").GetInt32());
        Assert.IsFalse(refreshed.RootElement.TryGetProperty("overview", out _));
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class ConcurrentNotFoundHandler : HttpMessageHandler
    {
        private int _count;
        private int _active;
        private int _maximum;
        public int RequestCount => Volatile.Read(ref _count);
        public int MaximumConcurrency => Volatile.Read(ref _maximum);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath is not { } path || !System.Text.RegularExpressions.Regex.IsMatch(path, @"^/3/movie/[1-9][0-9]*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                throw new InvalidOperationException("Unexpected provider path.");
            Interlocked.Increment(ref _count);
            var active = Interlocked.Increment(ref _active);
            int observed;
            while (active > (observed = Volatile.Read(ref _maximum)) && Interlocked.CompareExchange(ref _maximum, active, observed) != observed) { }
            try
            {
                await Task.Delay(2, cancellationToken);
                return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            throw new InvalidOperationException("A cache hit must not make an HTTP request.");
        }
    }
}
