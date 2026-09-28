using CineKros.Api;
using CineKros.Api.Database;
using CineKros.Api.RealFlow;
using CineKros.Api.RealProviders;
using CineKros.Embedding;
using System.Diagnostics;

namespace CineKros.Api.Tests.RealFlow;

[TestClass]
public sealed class E5QueryEmbeddingAdapterTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task LocalSingletonEmbedsNormalized768DQueriesAndHardOnlySkipsInference()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var modelDirectory = Path.Combine(repositoryRoot, "database/data/models/e5-base-v2/f52bf8ec8c7124536f0efb74aca902b2995e5bcd");
        var baselineWorkingSet = Environment.WorkingSet;
        var loadTimer = Stopwatch.StartNew();
        using var model = new E5EmbeddingModel(modelDirectory);
        loadTimer.Stop();
        var afterLoadWorkingSet = Environment.WorkingSet;
        Assert.AreEqual("9411a2620fc30e348aa80c9d4e54ca0db5a00d94a92175c82ccdd47ad03b13e1", model.ProfileFingerprint);
        var adapter = new E5QueryEmbeddingAdapter(model);
        var firstTimer = Stopwatch.StartNew();
        var first = await adapter.EmbedQueryAsync("quiet mystery", CancellationToken.None);
        firstTimer.Stop();
        var warmTimer = Stopwatch.StartNew();
        var second = await adapter.EmbedQueryAsync("hopeful space adventure", CancellationToken.None);
        warmTimer.Stop();
        var successiveTimings = new List<double>();
        foreach (var query in new[] { "slow-burn crime drama", "lighthearted family comedy", "thoughtful science fiction" })
        {
            var timer = Stopwatch.StartNew();
            _ = await adapter.EmbedQueryAsync(query, CancellationToken.None);
            timer.Stop();
            successiveTimings.Add(timer.Elapsed.TotalMilliseconds);
        }
        var afterInferenceWorkingSet = Environment.WorkingSet;

        Assert.AreEqual(768, first.Length);
        Assert.IsTrue(first.All(float.IsFinite));
        Assert.AreEqual(1d, Math.Sqrt(first.Sum(value => (double)value * value)), 1e-5);
        Assert.AreEqual(5L, model.InferenceRunCount, "All semantic requests must use the same loaded model instance.");
        Assert.IsTrue(model.LoadTime > TimeSpan.Zero, "The shared model should report its startup load duration.");
        TestContext.WriteLine($"E5_TEST_METRICS environment={Environment.OSVersion.Platform}/{System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture} profile={model.ProfileFingerprint}");
        TestContext.WriteLine($"E5_TEST_METRICS cold_load_wall_ms={loadTimer.Elapsed.TotalMilliseconds:F0} model_load_ms={model.LoadTime.TotalMilliseconds:F0} first_query_ms={firstTimer.Elapsed.TotalMilliseconds:F0} warm_query_ms={warmTimer.Elapsed.TotalMilliseconds:F0} successive_query_ms={string.Join(',', successiveTimings.Select(value => value.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)))}");
        TestContext.WriteLine($"E5_TEST_METRICS baseline_working_set_bytes={baselineWorkingSet} after_load_idle_working_set_bytes={afterLoadWorkingSet} after_queries_working_set_bytes={afterInferenceWorkingSet} inference_runs={model.InferenceRunCount} method=Environment.WorkingSet_of_test_process");

        var hardOnlyService = new RealRecommendationService(
            new FixedParser(new RealParserResult("query", new RealParsedQuery(new RealHardFilters(YearMin: 2000), null))),
            new RealParsedQueryValidator(), adapter, new EmptySearch());
        await hardOnlyService.RecommendAsync(new ParserInput("en", "films from 2000 onward"), CancellationToken.None);
        Assert.AreEqual(5L, model.InferenceRunCount, "Hard-filter-only requests must not tokenize or run ONNX.");
        Assert.AreEqual(768, second.Length);
    }

    private sealed class FixedParser(RealParserResult result) : IRealQueryParser
    {
        public Task<RealParserResult> ParseAsync(string language, string message, CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class EmptySearch : IRealMovieSearch
    {
        public Task<IReadOnlyList<FilteredMovie>> SearchHybridAsync(RealHardFilters hardFilters, float[] queryVector, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FilteredMovie>>([]);
        public Task<IReadOnlyList<FilteredMovie>> SearchHardOnlyAsync(RealHardFilters hardFilters, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<FilteredMovie>>([]);
    }
}
