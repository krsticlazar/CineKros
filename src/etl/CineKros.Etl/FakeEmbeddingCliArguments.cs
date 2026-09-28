namespace CineKros.Etl;

internal sealed record FakeEmbeddingCliArguments(string CatalogPath, string ManifestPath, string OutputDirectory, string CheckpointPath, FakeEmbeddingConfiguration Configuration)
{
    internal static FakeEmbeddingCliArguments Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length) throw new ArgumentException($"Option '{args[i]}' requires a value.");
            var key = args[i];
            if (!values.TryAdd(key, args[i + 1])) throw new ArgumentException($"Option '{key}' may be specified only once.");
        }
        var allowed = new[] { "--catalog", "--manifest", "--output-dir", "--checkpoint", "--model-id", "--model-version", "--dimension", "--document-task-type", "--formatting-version" };
        if (values.Keys.Any(k => !allowed.Contains(k, StringComparer.Ordinal))) throw new ArgumentException("Unknown fake-embed option.");
        foreach (var key in allowed) if (!values.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"Required option '{key}' is missing or blank.");
        foreach (var key in allowed.Take(4)) if (!Path.IsPathFullyQualified(values[key])) throw new ArgumentException($"Option '{key}' requires an absolute path.");
        if (!int.TryParse(values["--dimension"], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var dimension) || dimension <= 0) throw new ArgumentException("--dimension must be a positive integer.");
        return new(values["--catalog"], values["--manifest"], values["--output-dir"], values["--checkpoint"], new(values["--model-id"], values["--model-version"], dimension, values["--document-task-type"], values["--formatting-version"]));
    }
}
