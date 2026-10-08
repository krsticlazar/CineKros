namespace CineKros.E5.Generator;

public sealed record GeneratorArguments(string Catalog, string Manifest, string ModelDirectory, string OutputDirectory, string Checkpoint, int BatchSize, string Profile)
{
    public static GeneratorArguments Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal) ||
                !new[] { "--catalog", "--manifest", "--model-dir", "--output-dir", "--checkpoint", "--batch-size", "--profile" }.Contains(args[i], StringComparer.Ordinal) ||
                !values.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Arguments must be unique --name value pairs.");
        }
        var required = new[] { "--catalog", "--manifest", "--model-dir", "--output-dir", "--checkpoint" };
        if (required.Any(key => !values.ContainsKey(key))) throw new ArgumentException("Required flags: --catalog, --manifest, --model-dir, --output-dir, --checkpoint.");
        var batchSize = values.TryGetValue("--batch-size", out var text) ?
            int.TryParse(text, out var parsed) ? parsed : 0 : 16;
        if (batchSize is < 1 or > 128) throw new ArgumentException("--batch-size must be between 1 and 128.");
        var profile = values.GetValueOrDefault("--profile", CineKros.Embedding.EmbeddingProfileDescriptor.LegacyEnglish.ProfileVersion);
        if (profile is not ("e5-base-v2-int8-onnx-v1" or "multilingual-e5-base-int8-onnx-v1"))
            throw new ArgumentException("--profile must name a locked supported E5 profile.");
        if (profile == "multilingual-e5-base-int8-onnx-v1" && batchSize != 1)
            throw new ArgumentException("The multilingual E5 profile requires --batch-size 1.");
        return new GeneratorArguments(Path.GetFullPath(values["--catalog"]), Path.GetFullPath(values["--manifest"]),
            Path.GetFullPath(values["--model-dir"]), Path.GetFullPath(values["--output-dir"]), Path.GetFullPath(values["--checkpoint"]), batchSize, profile);
    }
}
