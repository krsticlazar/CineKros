namespace CineKros.Etl;

internal sealed record CombinedCatalogArguments(string SemanticDirectory, string EnrichmentDirectory, string OutputDirectory)
{
    internal static CombinedCatalogArguments Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length) throw new ArgumentException($"Option '{args[i]}' requires a value.");
            var key = args[i]; if (key is not ("--semantic-dir" or "--enrichment-dir" or "--output-dir")) throw new ArgumentException($"Unknown option '{key}'.");
            if (!values.TryAdd(key, args[i + 1])) throw new ArgumentException($"Option '{key}' may be specified only once.");
            if (!Path.IsPathFullyQualified(args[i + 1])) throw new ArgumentException($"Option '{key}' requires an absolute path.");
        }
        foreach (var key in new[] { "--semantic-dir", "--enrichment-dir", "--output-dir" }) if (!values.ContainsKey(key)) throw new ArgumentException($"Required option '{key}' is missing.");
        return new(values["--semantic-dir"], values["--enrichment-dir"], values["--output-dir"]);
    }
}
