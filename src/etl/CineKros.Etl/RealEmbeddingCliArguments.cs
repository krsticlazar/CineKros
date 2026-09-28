namespace CineKros.Etl;

internal sealed record RealEmbeddingCliArguments(string CatalogPath, string ManifestPath, string OutputDirectory, string CheckpointPath, bool AllowQuotaDayAdvance)
{
    internal static RealEmbeddingCliArguments Parse(string[] args)
    {
        var allowed = new[] { "--catalog", "--manifest", "--output-dir", "--checkpoint" };
        var allowAdvance = args.Contains("--allow-quota-day-advance", StringComparer.Ordinal);
        var valuesOnly = args.Where(x => x != "--allow-quota-day-advance").ToArray();
        if (valuesOnly.Length != allowed.Length * 2 || args.Count(x => x == "--allow-quota-day-advance") > 1) throw new ArgumentException("real-embed requires explicit catalog, manifest, output-dir and checkpoint paths.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < valuesOnly.Length; i += 2)
        {
            if (i + 1 >= valuesOnly.Length || !allowed.Contains(valuesOnly[i], StringComparer.Ordinal) || !values.TryAdd(valuesOnly[i], valuesOnly[i + 1]))
                throw new ArgumentException("Invalid or duplicate real-embed option.");
        }
        foreach (var key in allowed)
            if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
                throw new ArgumentException($"Required option '{key}' must be an absolute path.");
        return new(values["--catalog"], values["--manifest"], values["--output-dir"], values["--checkpoint"], allowAdvance);
    }
}
