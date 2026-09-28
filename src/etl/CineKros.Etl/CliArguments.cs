namespace CineKros.Etl;

public sealed record CliArguments(string SourceRoot, string OutputDirectory)
{
    public static CliArguments Parse(string[] args)
    {
        string? sourceRoot = null;
        string? outputDirectory = null;

        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"Option '{args[index]}' requires a value.");
            }

            var option = args[index];
            var value = args[index + 1];
            switch (option)
            {
                case "--source-root":
                    if (sourceRoot is not null)
                    {
                        throw new ArgumentException("Option '--source-root' may be specified only once.");
                    }
                    sourceRoot = RequireAbsolutePath(option, value);
                    break;
                case "--output-dir":
                    if (outputDirectory is not null)
                    {
                        throw new ArgumentException("Option '--output-dir' may be specified only once.");
                    }
                    outputDirectory = RequireAbsolutePath(option, value);
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{option}'.");
            }
        }

        if (sourceRoot is null)
        {
            throw new ArgumentException("Required option '--source-root' is missing.");
        }
        if (outputDirectory is null)
        {
            throw new ArgumentException("Required option '--output-dir' is missing.");
        }

        return new CliArguments(sourceRoot, outputDirectory);
    }

    private static string RequireAbsolutePath(string option, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException($"Option '{option}' requires an absolute path.");
        }

        return Path.GetFullPath(value);
    }
}
