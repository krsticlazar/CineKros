namespace CineKros.Etl;

public sealed record SemanticCliArguments(string InputJsonl, string InputManifest, string TagdlCsv, string OutputDirectory)
{
    public static SemanticCliArguments Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length) throw new ArgumentException($"Option '{args[i]}' requires a value.");
            var key = args[i]; if (key is not ("--input-jsonl" or "--input-manifest" or "--tagdl-csv" or "--output-dir")) throw new ArgumentException($"Unknown option '{key}'.");
            if (!values.TryAdd(key, args[i + 1])) throw new ArgumentException($"Option '{key}' may be specified only once.");
            if (!Path.IsPathFullyQualified(args[i + 1])) throw new ArgumentException($"Option '{key}' requires an absolute path.");
        }
        foreach (var key in new[] { "--input-jsonl", "--input-manifest", "--tagdl-csv", "--output-dir" }) if (!values.ContainsKey(key)) throw new ArgumentException($"Required option '{key}' is missing.");
        return new(values["--input-jsonl"], values["--input-manifest"], values["--tagdl-csv"], values["--output-dir"]);
    }
}
