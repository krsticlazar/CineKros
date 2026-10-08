namespace CineKros.Etl;

internal static class SrPocArguments
{
    public static IReadOnlyDictionary<string, string> Parse(string[] args, params string[] required)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length) throw new ArgumentException($"Option '{args[i]}' requires a value.");
            var name = args[i];
            if (name is not ("--catalog" or "--dictionary" or "--output-dir")) throw new ArgumentException($"Unknown option '{name}'.");
            if (!Path.IsPathFullyQualified(args[i + 1])) throw new ArgumentException($"Option '{name}' requires an absolute path.");
            if (!values.TryAdd(name, args[i + 1])) throw new ArgumentException($"Option '{name}' may be specified only once.");
        }
        foreach (var name in required.Append("--output-dir")) if (!values.ContainsKey(name)) throw new ArgumentException($"Required option '{name}' is missing.");
        if (values.Keys.Except(required.Append("--output-dir"), StringComparer.Ordinal).Any()) throw new ArgumentException("An option not used by this Serbian POC command was supplied.");
        return values;
    }
}
