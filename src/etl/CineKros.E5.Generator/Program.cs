using CineKros.E5.Generator;
using CineKros.Embedding;

try
{
    var options = GeneratorArguments.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray());
    var profile = options.Profile == EmbeddingProfileDescriptor.MultilingualE5Base.ProfileVersion
        ? EmbeddingProfileDescriptor.MultilingualE5Base : EmbeddingProfileDescriptor.LegacyEnglish;
    using var model = new E5EmbeddingModel(options.ModelDirectory, profile);
    var result = DocumentVectorGenerator.Run(options.Catalog, options.Manifest, options.Checkpoint, options.OutputDirectory, options.BatchSize,
        new E5DocumentVectorSource(model));
    Console.WriteLine($"Published {result.RecordCount} E5 document vectors; generated={result.Generated}, reused={result.Reused}.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"E5 generation failed: {exception.Message}");
    return 1;
}
