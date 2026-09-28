namespace CineKros.Etl;

public sealed record EnrichmentV2Arguments(string InputJsonl, string InputManifest, string Ml32mRoot,
    string CacheDirectory, string OutputDirectory, int MaxConcurrency, IReadOnlySet<int> RefreshTmdbIds)
{
    public int MaxHttpAttempts { get; init; } = 10_000;

    public static EnrichmentV2Arguments Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var refresh = new HashSet<int>();
        var concurrency = 4;
        var concurrencySeen = false;
        var maxHttpAttempts = 10_000;
        var maxHttpAttemptsSeen = false;
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (key == "--refresh-tmdb")
            {
                if (++i >= args.Length || !int.TryParse(args[i], out var id) || id <= 0 || !refresh.Add(id))
                    throw new ArgumentException("--refresh-tmdb requires unique positive IDs.");
                continue;
            }
            if (key == "--max-concurrency")
            {
                if (concurrencySeen || ++i >= args.Length || !int.TryParse(args[i], out concurrency) || concurrency is < 1 or > 4)
                    throw new ArgumentException("--max-concurrency must be between 1 and 4.");
                concurrencySeen = true;
                continue;
            }
            if (key == "--max-http-attempts")
            {
                if (maxHttpAttemptsSeen || ++i >= args.Length || !int.TryParse(args[i], out maxHttpAttempts) || maxHttpAttempts is < 1 or > 10_000)
                    throw new ArgumentException("--max-http-attempts must be between 1 and 10000.");
                maxHttpAttemptsSeen = true;
                continue;
            }
            if (key is not ("--input-jsonl" or "--input-manifest" or "--ml32m-root" or "--cache-dir" or "--output-dir") ||
                ++i >= args.Length || !Path.IsPathFullyQualified(args[i]) || !values.TryAdd(key, Path.GetFullPath(args[i])))
                throw new ArgumentException($"Invalid or duplicate option '{key}'.");
        }
        var required = new[] { "--input-jsonl", "--input-manifest", "--ml32m-root", "--cache-dir", "--output-dir" };
        if (required.Any(key => !values.ContainsKey(key))) throw new ArgumentException("All five v2 input and output paths are required.");
        return new EnrichmentV2Arguments(values[required[0]], values[required[1]], values[required[2]], values[required[3]], values[required[4]], concurrency, refresh)
        {
            MaxHttpAttempts = maxHttpAttempts
        };
    }
}
