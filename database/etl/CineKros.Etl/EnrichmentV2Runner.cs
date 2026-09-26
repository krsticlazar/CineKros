using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.VisualBasic.FileIO;

namespace CineKros.Etl;

public sealed record EnrichmentV2Result(int OutputRecords, string OutputSha256, string Fingerprint);

internal sealed record EnrichmentV2ExpectedSnapshot(
    string B1aJsonlSha256, string B1aManifestSha256, string LinksMd5, string MoviesMd5, string RatingsMd5,
    int B1aRecords, int LinkRows, int MovieRows, long RatingRows, int MatchedRecords, int UnmatchedRecords,
    int EligibleTmdbIds, int MatchedWithoutTmdbId, long SelectedRatingRows)
{
    public static EnrichmentV2ExpectedSnapshot Approved { get; } = new(
        "a20493ca502106788f3ece917be99ae2e3aa9fbf747841f77adf11ef7a5f620a",
        "1384c67fdce08f9b52a57b60bc6c0e361bb826d798b8e92f0336a629fc8b7667",
        "8f033867bcb4e6be8792b21468b4fa6e", "0df90835c19151f9d819d0822e190797", "cf12b74f9ad4b94a011f079e26d4270a",
        9730, 87585, 87585, 32000204, 9585, 145, 9577, 8, 28604436);
}

internal sealed record EnrichmentV2TestHooks(EnrichmentV2ExpectedSnapshot ExpectedSource,
    Action<string, string>? BeforePublish = null, Action<int>? AfterCacheMove = null);

public static class EnrichmentV2Runner
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const string OutputName = "movies-enriched.jsonl";

    private sealed record BaseMovie(long Id, string RawTitle, string Title, int Year, string ImdbId,
        decimal MovieLensAvgRating, string? DirectedByRaw, string? StarringRaw);
    internal sealed record Link(string ImdbId, int? TmdbId);
    internal sealed record MovieData(string[]? Genres);
    internal sealed record Aggregate(long Count, decimal Sum);
    internal sealed record CacheEntry(int TmdbId, string Status, TmdbDetails Details, string Sha256);

    public static Task<EnrichmentV2Result> RunAsync(EnrichmentV2Arguments options,
        Func<int, CancellationToken, Task<TmdbDetails>> fetch, CancellationToken cancellationToken) =>
        RunAsync(options, fetch, cancellationToken, null);

    internal static async Task<EnrichmentV2Result> RunAsync(EnrichmentV2Arguments options,
        Func<int, CancellationToken, Task<TmdbDetails>> fetch, CancellationToken cancellationToken, EnrichmentV2TestHooks? hooks)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(fetch);
        if (options.MaxConcurrency is < 1 or > 4 || new[] { options.InputJsonl, options.InputManifest, options.Ml32mRoot, options.CacheDirectory, options.OutputDirectory }.Any(path => !Path.IsPathFullyQualified(path)))
            throw new ExportValidationException("Enrichment paths must be absolute and concurrency must be between one and four.");
        if (options.RefreshTmdbIds.Any(id => id <= 0)) throw new ExportValidationException("Refresh IDs must be positive.");
        if (Directory.Exists(options.OutputDirectory) || File.Exists(options.OutputDirectory))
            throw new ExportValidationException("Output directory must be initially absent.");
        if (!Directory.Exists(options.Ml32mRoot)) throw new ExportValidationException("ML32M source directory is missing.");
        var expected = hooks?.ExpectedSource ?? EnrichmentV2ExpectedSnapshot.Approved;
        var movies = ReadB1a(options.InputJsonl, options.InputManifest, expected);
        var linksPath = Path.Combine(options.Ml32mRoot, "links.csv");
        var genresPath = Path.Combine(options.Ml32mRoot, "movies.csv");
        var ratingsPath = Path.Combine(options.Ml32mRoot, "ratings.csv");
        ValidateChecksums(options.Ml32mRoot, linksPath, genresPath, ratingsPath, expected);
        var links = ReadLinks(linksPath, expected.LinkRows);
        var genreRows = ReadGenres(genresPath, expected.MovieRows);
        var selected = new HashSet<long>();
        var enriched = new List<(BaseMovie Movie, int? TmdbId, string[]? Genres, Aggregate? Rating, TmdbDetails? Details)>(movies.Count);
        foreach (var movie in movies)
        {
            if (!links.TryGetValue(movie.Id, out var link))
            {
                if (genreRows.ContainsKey(movie.Id)) throw new ExportValidationException($"movies.csv has an unmatched target row for {movie.Id}.");
                enriched.Add((movie, null, null, null, null));
                continue;
            }
            if (!ImdbMatches(movie.ImdbId, link.ImdbId))
                throw new ExportValidationException($"ML32M IMDb identity mismatch for movieLensId {movie.Id}.");
            if (!genreRows.TryGetValue(movie.Id, out var data)) throw new ExportValidationException($"ML32M movies row is missing for matched ID {movie.Id}.");
            selected.Add(movie.Id);
            enriched.Add((movie, link.TmdbId, data.Genres, null, null));
        }
        if (selected.Count != expected.MatchedRecords || movies.Count - selected.Count != expected.UnmatchedRecords ||
            enriched.Count(x => x.TmdbId.HasValue) != expected.EligibleTmdbIds ||
            enriched.Count(x => selected.Contains(x.Movie.Id) && x.TmdbId is null) != expected.MatchedWithoutTmdbId)
            throw new ExportValidationException("Accepted B1a/ML32M join coverage does not match the pinned source snapshot.");
        var (aggregates, validRatingRows) = ReadRatings(ratingsPath, selected, cancellationToken, expected.RatingRows);
        long selectedRatingCount = 0;
        for (var i = 0; i < enriched.Count; i++)
        {
            var row = enriched[i];
            if (selected.Contains(row.Movie.Id))
            {
                aggregates.TryGetValue(row.Movie.Id, out var agg);
                agg ??= new Aggregate(0, 0m);
                selectedRatingCount += agg.Count;
                enriched[i] = (row.Movie, row.TmdbId, row.Genres, agg, null);
            }
        }
        if (aggregates.Values.Sum(x => x.Count) != selectedRatingCount || selectedRatingCount != expected.SelectedRatingRows)
            throw new ExportValidationException("Per-film rating counts do not reconcile to the accepted target total.");

        Directory.CreateDirectory(options.CacheDirectory);
        var eligible = enriched.Where(x => x.TmdbId is > 0).Select(x => x.TmdbId!.Value).Distinct().Order().ToArray();
        if (options.RefreshTmdbIds.Any(id => !eligible.Contains(id))) throw new ExportValidationException("Refresh IDs must belong to eligible matched films.");
        var cache = new Dictionary<int, TmdbDetails>();
        var pending = new List<int>();
        foreach (var id in eligible)
        {
            var path = CachePath(options.CacheDirectory, id);
            if (!options.RefreshTmdbIds.Contains(id) && File.Exists(path)) cache[id] = ReadCache(path, id).Details;
            else pending.Add(id);
        }
        await FetchPendingAsync(pending, cache, options.CacheDirectory, options.MaxConcurrency, fetch, cancellationToken, hooks?.AfterCacheMove).ConfigureAwait(false);
        if (cancellationToken.IsCancellationRequested) cancellationToken.ThrowIfCancellationRequested();
        cancellationToken.ThrowIfCancellationRequested();
        for (var i = 0; i < enriched.Count; i++)
        {
            var row = enriched[i];
            if (row.TmdbId is int id) enriched[i] = (row.Movie, id, row.Genres, row.Rating, cache[id]);
        }
        var b1Hash = HashFile(options.InputJsonl);
        var b1ManifestHash = HashFile(options.InputManifest);
        var linksHash = HashMd5(linksPath);
        var moviesHash = HashMd5(genresPath);
        var ratingsHash = HashMd5(ratingsPath);
        var cacheHash = HashCache(options.CacheDirectory, eligible);
        var fingerprint = HashString($"B04-ml32m-tmdb-v2|{b1Hash}|{linksHash}|{moviesHash}|{ratingsHash}|{cacheHash}");
        var parent = Path.GetDirectoryName(options.OutputDirectory)!;
        Directory.CreateDirectory(parent);
        var stage = Path.Combine(parent, $".b04-v2-{Guid.NewGuid():N}.tmp");
        Directory.CreateDirectory(stage);
        try
        {
            var outputPath = Path.Combine(stage, OutputName);
            WriteOutput(outputPath, enriched);
            var outputHash = HashFile(outputPath);
            WriteManifest(Path.Combine(stage, "manifest.json"), b1Hash, b1ManifestHash, linksHash, moviesHash, ratingsHash,
                cacheHash, outputHash, fingerprint, enriched.Count, selected.Count, movies.Count - selected.Count,
                validRatingRows, selectedRatingCount, eligible, cache, options.MaxConcurrency, expected.MatchedWithoutTmdbId);
            ValidateOutput(outputPath, enriched.Count);
            ValidateManifest(Path.Combine(stage, "manifest.json"), outputHash, fingerprint, enriched.Count);
            cancellationToken.ThrowIfCancellationRequested();
            hooks?.BeforePublish?.Invoke(stage, options.OutputDirectory);
            PublishStageDirectory(stage, options.OutputDirectory);
            return new EnrichmentV2Result(enriched.Count, outputHash, fingerprint);
        }
        finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    internal static void PublishStageDirectory(string stage, string outputDirectory, Action? beforeRename = null)
    {
        if (!File.Exists(Path.Combine(stage, OutputName)) || !File.Exists(Path.Combine(stage, "manifest.json")))
            throw new ExportValidationException("Staging directory must contain the complete output pair.");
        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
            throw new ExportValidationException("Output directory must be initially absent.");
        beforeRename?.Invoke();
        Directory.Move(stage, outputDirectory);
    }

    internal static async Task FetchPendingAsync(IReadOnlyList<int> pending, Dictionary<int, TmdbDetails> cache,
        string cacheDirectory, int maxConcurrency, Func<int, CancellationToken, Task<TmdbDetails>> fetch, CancellationToken cancellationToken,
        Action<int>? afterCacheMove = null)
    {
        if (maxConcurrency is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(maxConcurrency));
        Directory.CreateDirectory(cacheDirectory);
        using var gate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        using var scheduleCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stopScheduling = 0;
        var failures = new System.Collections.Concurrent.ConcurrentBag<(int Id, Exception Error)>();
        try
        {
            await Task.WhenAll(pending.Select(async id =>
            {
                try { await gate.WaitAsync(scheduleCancellation.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return; }
                try
                {
                    if (Volatile.Read(ref stopScheduling) != 0) return;
                    var details = await fetch(id, cancellationToken).ConfigureAwait(false);
                    WriteCache(cacheDirectory, id, details, afterAtomicMove: () => afterCacheMove?.Invoke(id));
                    lock (cache) cache[id] = details;
                }
                catch (TmdbConfigurationException exception)
                {
                    failures.Add((id, exception));
                    Interlocked.Exchange(ref stopScheduling, 1);
                    scheduleCancellation.Cancel();
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failures.Add((id, exception));
                }
                finally { gate.Release(); }
            })).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            WriteCheckpoint(cacheDirectory, UncachedIds(pending, cache), []);
            throw;
        }
        if (cancellationToken.IsCancellationRequested) cancellationToken.ThrowIfCancellationRequested();
        if (failures.Count > 0)
        {
            WriteCheckpoint(cacheDirectory, UncachedIds(pending, cache), failures);
            throw new TmdbRequestException($"TMDB details remain unresolved; checkpoint IDs: {failures.Count}.", failures.OrderBy(x => x.Id).First().Error);
        }
        WriteCheckpoint(cacheDirectory, [], []);
    }

    private static List<BaseMovie> ReadB1a(string jsonl, string manifest, EnrichmentV2ExpectedSnapshot expected)
    {
        if (!File.Exists(jsonl) || !File.Exists(manifest)) throw new ExportValidationException("B1a JSONL or manifest is missing.");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(manifest));
        var root = doc.RootElement;
        if (HashFile(manifest) != expected.B1aManifestSha256 ||
            HashFile(jsonl) != expected.B1aJsonlSha256 ||
            root.GetProperty("mappingVersion").GetString() != "B1a-v1" ||
            root.GetProperty("output").GetProperty("sha256").GetString() != HashFile(jsonl) ||
            root.GetProperty("counts").GetProperty("outputRecords").GetInt32() != expected.B1aRecords)
            throw new ExportValidationException("B1a manifest validation failed.");
        using var reader = new StreamReader(jsonl, StrictUtf8, false);
        var result = new List<BaseMovie>(expected.B1aRecords);
        long previous = 0;
        var imdbIds = new HashSet<string>(StringComparer.Ordinal);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            using var item = JsonDocument.Parse(line);
            var obj = item.RootElement;
            var names = obj.EnumerateObject().Select(p => p.Name).ToArray();
            string[] expectedFields = ["movieLensId", "rawTitle", "title", "year", "imdbId", "movieLensAvgRating", "directedByRaw", "starringRaw"];
            if (!names.SequenceEqual(expectedFields)) throw new ExportValidationException("B1a JSONL property shape is invalid.");
            var id = obj.GetProperty("movieLensId").GetInt64();
            var imdb = obj.GetProperty("imdbId").GetString();
            if (id <= previous || id <= 0 || id > 9007199254740991 || string.IsNullOrEmpty(imdb) || !IsDigits(imdb) || !imdbIds.Add(imdb) ||
                obj.GetProperty("rawTitle").ValueKind != JsonValueKind.String || obj.GetProperty("title").ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(obj.GetProperty("title").GetString()) ||
                obj.GetProperty("year").ValueKind != JsonValueKind.Number || !obj.GetProperty("year").TryGetInt32(out _) ||
                obj.GetProperty("movieLensAvgRating").ValueKind != JsonValueKind.Number || obj.GetProperty("movieLensAvgRating").GetDecimal() is < 0 or > 5)
                throw new ExportValidationException("B1a record contains invalid values/order.");
            previous = id;
            result.Add(new(id, obj.GetProperty("rawTitle").GetString()!, obj.GetProperty("title").GetString()!, obj.GetProperty("year").GetInt32(), imdb,
                obj.GetProperty("movieLensAvgRating").GetDecimal(), NullableString(obj.GetProperty("directedByRaw")), NullableString(obj.GetProperty("starringRaw"))));
        }
        if (result.Count != expected.B1aRecords) throw new ExportValidationException("B1a record count does not match the selected source snapshot.");
        return result;
    }

    internal static Dictionary<long, Link> ReadLinks(string path, int expectedRows = 0)
    {
        using var parser = Csv(path);
        var header = parser.ReadFields();
        if (header is not ["movieId", "imdbId", "tmdbId"]) throw new ExportValidationException("links.csv header is invalid.");
        var result = new Dictionary<long, Link>();
        while (!parser.EndOfData)
        {
            var row = parser.ReadFields();
            if (row is not { Length: 3 } || !long.TryParse(row[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0 || !IsDigits(row[1]))
                throw new ExportValidationException("links.csv contains an invalid row.");
            int? tmdb = null;
            if (row[2].Length > 0)
            {
                if (!int.TryParse(row[2], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
                    throw new ExportValidationException("links.csv contains an invalid tmdbId.");
                tmdb = parsed;
            }
            if (!result.TryAdd(id, new(row[1], tmdb))) throw new ExportValidationException("links.csv contains duplicate movieId.");
        }
        if (expectedRows > 0 && result.Count != expectedRows) throw new ExportValidationException("links.csv row count mismatch.");
        return result;
    }

    internal static Dictionary<long, MovieData> ReadGenres(string path, int expectedRows = 0)
    {
        using var parser = Csv(path);
        var header = parser.ReadFields();
        if (header is not ["movieId", "title", "genres"]) throw new ExportValidationException("movies.csv header is invalid.");
        var result = new Dictionary<long, MovieData>();
        while (!parser.EndOfData)
        {
            var row = parser.ReadFields();
            if (row is not { Length: 3 } || !long.TryParse(row[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
                throw new ExportValidationException("movies.csv contains an invalid row.");
            string[]? genres = null;
            if (row[2] != "(no genres listed)")
            {
                var parts = row[2].Split('|');
                if (parts.Length == 0 || parts.Any(string.IsNullOrWhiteSpace) || parts.Distinct(StringComparer.Ordinal).Count() != parts.Length)
                    throw new ExportValidationException("movies.csv contains invalid genres.");
                genres = parts;
            }
            if (!result.TryAdd(id, new(genres))) throw new ExportValidationException("movies.csv contains duplicate movieId.");
        }
        if (expectedRows > 0 && result.Count != expectedRows) throw new ExportValidationException("movies.csv row count mismatch.");
        return result;
    }

    internal static (Dictionary<long, Aggregate> Aggregates, long ValidRows) ReadRatings(string path, IReadOnlySet<long> selected, CancellationToken token, long expectedRows = 32000204)
    {
        var result = new Dictionary<long, Aggregate>();
        using var parser = Csv(path);
        var header = parser.ReadFields();
        if (header is not ["userId", "movieId", "rating", "timestamp"]) throw new ExportValidationException("ratings.csv header is invalid.");
        long rows = 0;
        while (!parser.EndOfData)
        {
            if ((rows++ & 0x3ffff) == 0) token.ThrowIfCancellationRequested();
            var fields = parser.ReadFields();
            if (fields is not { Length: 4 } || !long.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out var user) || user <= 0 ||
                !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var movie) || movie <= 0 ||
                !decimal.TryParse(fields[2], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rating) || rating is < 0 or > 5 ||
                !long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp) || timestamp < 0)
                throw new ExportValidationException("ratings.csv contains an invalid row.");
            if (selected.Contains(movie))
            {
                result.TryGetValue(movie, out var old);
                old ??= new Aggregate(0, 0m);
                result[movie] = new(old.Count + 1, old.Sum + rating);
            }
        }
        if (rows != expectedRows) throw new ExportValidationException($"ratings.csv row count mismatch: {rows}.");
        return (result, rows);
    }

    private static void ValidateChecksums(string root, string links, string movies, string ratings, EnrichmentV2ExpectedSnapshot expectedSource)
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["links.csv"] = expectedSource.LinksMd5,
            ["movies.csv"] = expectedSource.MoviesMd5,
            ["ratings.csv"] = expectedSource.RatingsMd5
        };
        foreach (var path in new[] { links, movies, ratings })
        {
            var name = Path.GetFileName(path);
            using var stream = File.OpenRead(path);
            var actual = Convert.ToHexStringLower(MD5.HashData(stream));
            if (actual != expected[name]) throw new ExportValidationException($"ML32M checksum mismatch for {name}.");
        }
        var checksumFile = File.ReadAllLines(Path.Combine(root, "checksums.txt"));
        foreach (var pair in expected)
            if (!checksumFile.Contains($"{pair.Value}  {pair.Key}", StringComparer.Ordinal)) throw new ExportValidationException("checksums.txt does not match the pinned source hashes.");
    }

    private static TextFieldParser Csv(string path)
    {
        var parser = new TextFieldParser(new StreamReader(path, StrictUtf8, false))
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false
        };
        parser.SetDelimiters(",");
        return parser;
    }

    private static void WriteOutput(string path, IReadOnlyList<(BaseMovie Movie, int? TmdbId, string[]? Genres, Aggregate? Rating, TmdbDetails? Details)> rows)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        foreach (var row in rows)
        {
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteNumber("movieLensId", row.Movie.Id); writer.WriteString("rawTitle", row.Movie.RawTitle); writer.WriteString("title", row.Movie.Title);
                writer.WriteNumber("year", row.Movie.Year); writer.WriteString("imdbId", row.Movie.ImdbId); writer.WriteNumber("movieLensAvgRating", row.Movie.MovieLensAvgRating);
                WriteNullable(writer, "directedByRaw", row.Movie.DirectedByRaw); WriteNullable(writer, "starringRaw", row.Movie.StarringRaw);
                if (row.TmdbId.HasValue) writer.WriteNumber("tmdbId", row.TmdbId.Value); else writer.WriteNull("tmdbId");
                if (row.Genres is null) writer.WriteNull("genres"); else { writer.WriteStartArray("genres"); foreach (var genre in row.Genres) writer.WriteStringValue(genre); writer.WriteEndArray(); }
                if (row.Rating is { Count: > 0 } aggregate) writer.WriteNumber("averageRating", aggregate.Sum / aggregate.Count); else writer.WriteNull("averageRating");
                if (row.Rating is Aggregate count) writer.WriteNumber("ratingCount", count.Count); else writer.WriteNull("ratingCount");
                if (row.Details?.RuntimeMinutes is int runtime) writer.WriteNumber("runtimeMinutes", runtime); else writer.WriteNull("runtimeMinutes");
                WriteNullable(writer, "originalLanguage", row.Details?.OriginalLanguage); WriteNullable(writer, "posterPath", row.Details?.PosterPath);
                writer.WriteEndObject(); writer.Flush();
            }
            stream.WriteByte((byte)'\n');
        }
        stream.Flush(true);
    }

    private static void WriteManifest(string path, string b1, string b1Manifest, string links, string movies, string ratings, string cacheHash,
        string output, string fingerprint, int count, int matched, int unmatched, long validRows, long targetSum, int[] eligible,
        IReadOnlyDictionary<int, TmdbDetails> details, int maxConcurrency, int matchedWithoutTmdbId)
    {
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var w = new Utf8JsonWriter(fs);
        w.WriteStartObject(); w.WriteString("mappingVersion", "B04-ml32m-tmdb-v2"); w.WriteString("cacheVersion", "tmdb-cache-v2");
        w.WriteString("generatedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)); w.WriteString("contentFingerprint", fingerprint);
        w.WriteStartObject("inputs");
        w.WriteStartObject("b1aJsonl"); w.WriteString("path", "database/data/derived/b1a-v1/movies-metadata.jsonl"); w.WriteString("sha256", b1); w.WriteEndObject();
        w.WriteStartObject("b1aManifest"); w.WriteString("path", "database/data/derived/b1a-v1/manifest.json"); w.WriteString("sha256", b1Manifest); w.WriteEndObject();
        w.WriteStartObject("links"); w.WriteString("path", "database/ml-32m/links.csv"); w.WriteString("md5", links); w.WriteEndObject();
        w.WriteStartObject("movies"); w.WriteString("path", "database/ml-32m/movies.csv"); w.WriteString("md5", movies); w.WriteEndObject();
        w.WriteStartObject("ratings"); w.WriteString("path", "database/ml-32m/ratings.csv"); w.WriteString("md5", ratings); w.WriteEndObject(); w.WriteEndObject();
        w.WriteStartObject("requestPolicy"); w.WriteString("api", "TMDB v3"); w.WriteString("endpoint", "/3/movie/{tmdbId}"); w.WriteNumber("timeoutSeconds", 10); w.WriteNumber("maxAttempts", 3); w.WriteString("retryAfter", "honor-valid-delta-or-date"); w.WriteStartArray("defaultBackoffSeconds"); w.WriteNumberValue(1); w.WriteNumberValue(2); w.WriteEndArray(); w.WriteNumber("maxConcurrency", maxConcurrency); w.WriteEndObject();
        w.WriteString("cacheSha256", cacheHash); w.WriteStartObject("output"); w.WriteString("path", OutputName); w.WriteString("sha256", output); w.WriteNumber("count", count); w.WriteString("order", "movieLensId-ascending"); w.WriteEndObject();
        w.WriteStartObject("counts"); w.WriteNumber("matched", matched); w.WriteNumber("unmatched", unmatched); w.WriteNumber("unmatchedSkipped", unmatched); w.WriteNumber("noTmdbIdSkipped", matchedWithoutTmdbId); w.WriteNumber("validRatingRows", validRows); w.WriteNumber("selectedRatingRows", targetSum); w.WriteEndObject();
        w.WriteStartObject("tmdb"); w.WriteStartArray("successIds"); foreach (var id in eligible.Where(id => !details[id].NotFound)) w.WriteNumberValue(id); w.WriteEndArray();
        w.WriteNumber("successCount", eligible.Count(id => !details[id].NotFound));
        w.WriteStartArray("notFoundIds"); foreach (var id in eligible.Where(id => details[id].NotFound)) w.WriteNumberValue(id); w.WriteEndArray();
        w.WriteNumber("notFoundCount", eligible.Count(id => details[id].NotFound)); w.WriteStartArray("nonterminalFailureIds"); w.WriteEndArray(); w.WriteNumber("nonterminalFailureCount", 0); w.WriteEndObject();
        w.WriteEndObject(); w.Flush(); fs.WriteByte((byte)'\n'); fs.Flush(true);
    }

    private static void ValidateOutput(string path, int expected)
    {
        using var reader = new StreamReader(path, StrictUtf8, false);
        var count = 0; long previousId = 0; string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            using var doc = JsonDocument.Parse(line);
            var names = doc.RootElement.EnumerateObject().Select(x => x.Name).ToArray();
            string[] wanted = ["movieLensId", "rawTitle", "title", "year", "imdbId", "movieLensAvgRating", "directedByRaw", "starringRaw", "tmdbId", "genres", "averageRating", "ratingCount", "runtimeMinutes", "originalLanguage", "posterPath"];
            if (!names.SequenceEqual(wanted)) throw new ExportValidationException("Generated output property order/shape is invalid.");
            var root = doc.RootElement;
            var id = root.GetProperty("movieLensId").GetInt64();
            if (id <= previousId || id <= 0) throw new ExportValidationException("Generated output ordering is invalid.");
            previousId = id;
            var tmdb = root.GetProperty("tmdbId");
            if (tmdb.ValueKind != JsonValueKind.Null && (tmdb.ValueKind != JsonValueKind.Number || !tmdb.TryGetInt32(out var tmdbId) || tmdbId <= 0)) throw new ExportValidationException("Generated tmdbId is invalid.");
            var countValue = root.GetProperty("ratingCount");
            if (countValue.ValueKind != JsonValueKind.Null && (countValue.ValueKind != JsonValueKind.Number || countValue.GetInt64() < 0)) throw new ExportValidationException("Generated ratingCount is invalid.");
            var average = root.GetProperty("averageRating");
            if (average.ValueKind != JsonValueKind.Null && (average.ValueKind != JsonValueKind.Number || average.GetDecimal() is < 0 or > 5)) throw new ExportValidationException("Generated averageRating is invalid.");
            count++;
        }
        if (count != expected) throw new ExportValidationException("Generated output count mismatch.");
    }

    private static void ValidateManifest(string path, string outputHash, string fingerprint, int expectedCount)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        if (root.GetProperty("mappingVersion").GetString() != "B04-ml32m-tmdb-v2" ||
            root.GetProperty("cacheVersion").GetString() != "tmdb-cache-v2" ||
            root.GetProperty("contentFingerprint").GetString() != fingerprint ||
            root.GetProperty("output").GetProperty("sha256").GetString() != outputHash ||
            root.GetProperty("output").GetProperty("count").GetInt32() != expectedCount ||
            root.GetProperty("tmdb").GetProperty("nonterminalFailureCount").GetInt32() != 0)
            throw new ExportValidationException("Generated manifest validation failed.");
    }

    private static string CachePath(string dir, int id) => Path.Combine(dir, $"{id}.json");
    internal static CacheEntry ReadCache(string path, int id)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path)); var root = document.RootElement;
        var names = root.EnumerateObject().Select(x => x.Name).ToArray();
        if (!names.SequenceEqual(["version", "tmdbId", "status", "runtimeMinutes", "originalLanguage", "posterPath", "sha256"])) throw new ExportValidationException("TMDB cache shape is invalid.");
        var runtime = root.GetProperty("runtimeMinutes"); var lang = root.GetProperty("originalLanguage"); var poster = root.GetProperty("posterPath");
        var status = root.GetProperty("status").GetString()!;
        var details = new TmdbDetails(runtime.ValueKind == JsonValueKind.Null ? null : runtime.GetInt32(), lang.ValueKind == JsonValueKind.Null ? null : lang.GetString(), poster.ValueKind == JsonValueKind.Null ? null : poster.GetString(), status == "not_found");
        var hash = root.GetProperty("sha256").GetString()!;
        var version = root.GetProperty("version").GetString()!;
        if (version != "tmdb-cache-v2" || root.GetProperty("tmdbId").GetInt32() != id || status is not ("success" or "not_found") ||
            (details.RuntimeMinutes is <= 0 || details.OriginalLanguage is not null && string.IsNullOrWhiteSpace(details.OriginalLanguage) || details.PosterPath is not null && !details.PosterPath.StartsWith("/", StringComparison.Ordinal)) ||
            (status == "not_found" && (details.RuntimeMinutes is not null || details.OriginalLanguage is not null || details.PosterPath is not null)) || CachePayloadHash(version, id, status, details) != hash)
            throw new ExportValidationException("TMDB cache entry has wrong key/version/values/hash.");
        return new(id, status, details, hash);
    }

    internal static void WriteCache(string dir, int id, TmdbDetails details, Action? beforePublish = null, Action? afterAtomicMove = null)
    {
        var status = details.NotFound ? "not_found" : "success"; var hash = CachePayloadHash("tmdb-cache-v2", id, status, details);
        var temp = Path.Combine(dir, $".{id}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var w = new Utf8JsonWriter(fs))
            {
                w.WriteStartObject(); w.WriteString("version", "tmdb-cache-v2"); w.WriteNumber("tmdbId", id); w.WriteString("status", status);
                if (details.RuntimeMinutes is int runtime) w.WriteNumber("runtimeMinutes", runtime); else w.WriteNull("runtimeMinutes");
                WriteNullable(w, "originalLanguage", details.OriginalLanguage); WriteNullable(w, "posterPath", details.PosterPath); w.WriteString("sha256", hash); w.WriteEndObject(); w.Flush(); fs.Flush(true);
            }
            beforePublish?.Invoke();
            File.Move(temp, CachePath(dir, id), true);
            afterAtomicMove?.Invoke();
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void WriteCheckpoint(string dir, IEnumerable<int> unresolvedIds, IEnumerable<(int Id, Exception Error)> failures)
    {
        var path = Path.Combine(dir, "checkpoint-v2.json");
        var temp = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new Utf8JsonWriter(fs))
            {
                writer.WriteStartObject(); writer.WriteString("version", "tmdb-cache-v2");
                writer.WriteStartArray("unresolvedIds"); foreach (var id in unresolvedIds.Distinct().Order()) writer.WriteNumberValue(id); writer.WriteEndArray();
                writer.WriteStartArray("nonterminalFailureIds"); foreach (var id in unresolvedIds.Distinct().Order()) writer.WriteNumberValue(id); writer.WriteEndArray();
                var groups = failures.GroupBy(x => FailureClass(x.Error), StringComparer.Ordinal).OrderBy(x => x.Key, StringComparer.Ordinal).ToArray();
                writer.WriteNumber("nonterminalFailureCount", failures.Count()); writer.WriteStartArray("failureClasses");
                foreach (var group in groups)
                {
                    writer.WriteStartObject(); writer.WriteString("class", group.Key); writer.WriteNumber("count", group.Count());
                    writer.WriteStartArray("tmdbIds"); foreach (var id in group.Select(x => x.Id).Distinct().Order()) writer.WriteNumberValue(id); writer.WriteEndArray(); writer.WriteEndObject();
                }
                writer.WriteEndArray(); writer.WriteEndObject(); writer.Flush(); fs.Flush(true);
            }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static string FailureClass(Exception error) => error switch
    {
        TmdbRequestException request => request.FailureClass,
        TmdbConfigurationException => "configuration",
        IOException => "cache_io",
        _ => "unexpected"
    };

    private static int[] UncachedIds(IEnumerable<int> ids, IReadOnlyDictionary<int, TmdbDetails> cache)
    {
        lock (cache) return ids.Where(id => !cache.ContainsKey(id)).Distinct().Order().ToArray();
    }

    private static string CachePayloadHash(string version, int id, string status, TmdbDetails value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject(); writer.WriteString("version", version); writer.WriteNumber("tmdbId", id); writer.WriteString("status", status);
            if (value.RuntimeMinutes is int runtime) writer.WriteNumber("runtimeMinutes", runtime); else writer.WriteNull("runtimeMinutes");
            WriteNullable(writer, "originalLanguage", value.OriginalLanguage); WriteNullable(writer, "posterPath", value.PosterPath); writer.WriteEndObject(); writer.Flush();
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }
    private static string HashCache(string dir, int[] ids) => HashString(string.Join("|", ids.Select(id => $"{id}:{HashFile(CachePath(dir, id))}")));
    private static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private static string HashMd5(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(MD5.HashData(stream)); }
    private static string HashString(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static string CanonicalDigits(string value) => value.TrimStart('0') is { Length: > 0 } result ? result : "0";
    internal static bool ImdbMatches(string b1aImdbId, string linkImdbId) => CanonicalDigits(b1aImdbId) == CanonicalDigits(linkImdbId);
    private static bool IsDigits(string value) => value.Length > 0 && value.All(c => c is >= '0' and <= '9');
    private static string? NullableString(JsonElement item) => item.ValueKind == JsonValueKind.Null ? null : item.ValueKind == JsonValueKind.String ? item.GetString() : throw new ExportValidationException("B1a nullable string has invalid type.");
    private static void WriteNullable(Utf8JsonWriter writer, string property, string? value) { if (value is null) writer.WriteNull(property); else writer.WriteString(property, value); }
}
