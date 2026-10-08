namespace CineKros.E5.Generator;

public sealed record GeneratorArguments(string Catalog, string Manifest, string ModelDirectory, string OutputDirectory, string Checkpoint, int BatchSize, string Profile,
    string? Language, string? Dictionary, string? SourceCatalog, bool PairedPoc)
{
    public static GeneratorArguments Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal) ||
                !new[] { "--catalog", "--manifest", "--model-dir", "--output-dir", "--checkpoint", "--batch-size", "--profile", "--language", "--dictionary", "--source-catalog", "--paired-poc" }.Contains(args[i], StringComparer.Ordinal) ||
                !values.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Arguments must be unique --name value pairs.");
        }
        var paired = values.ContainsKey("--paired-poc");
        if (paired && values["--paired-poc"] != "true") throw new ArgumentException("--paired-poc accepts only the explicit value true.");
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
        var language = values.GetValueOrDefault("--language");
        var dictionary = values.GetValueOrDefault("--dictionary");
        var sourceCatalog = values.GetValueOrDefault("--source-catalog");
        if (profile == "multilingual-e5-base-int8-onnx-v1")
        {
            if (paired ? language is not null : language is not ("en" or "sr"))
                throw new ArgumentException("Multilingual generation requires --language en|sr, or the explicit paired POC harness.");
            if (string.IsNullOrWhiteSpace(dictionary) || string.IsNullOrWhiteSpace(sourceCatalog))
                throw new ArgumentException("Multilingual POC generation requires --dictionary and --source-catalog.");
        }
        else if (language is not null || dictionary is not null || sourceCatalog is not null || paired)
            throw new ArgumentException("Language selection and paired POC inputs require the multilingual profile.");
        return new GeneratorArguments(Path.GetFullPath(values["--catalog"]), Path.GetFullPath(values["--manifest"]),
            Path.GetFullPath(values["--model-dir"]), Path.GetFullPath(values["--output-dir"]), Path.GetFullPath(values["--checkpoint"]), batchSize, profile,
            language, dictionary is null ? null : Path.GetFullPath(dictionary), sourceCatalog is null ? null : Path.GetFullPath(sourceCatalog), paired);
    }
}
