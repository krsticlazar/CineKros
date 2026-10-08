namespace CineKros.Etl;

internal static class SrPocContextArguments
{
    private static readonly string[] Required = ["--selected-source", "--qa-report", "--output"];

    public static IReadOnlyDictionary<string, string> Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length) throw new ArgumentException($"Option '{args[i]}' requires a value.");
            if (!Required.Contains(args[i], StringComparer.Ordinal)) throw new ArgumentException($"Unknown option '{args[i]}'.");
            if (!Path.IsPathFullyQualified(args[i + 1])) throw new ArgumentException($"Option '{args[i]}' requires an absolute path.");
            if (!values.TryAdd(args[i], args[i + 1])) throw new ArgumentException($"Option '{args[i]}' may be specified only once.");
        }
        foreach (var name in Required) if (!values.ContainsKey(name)) throw new ArgumentException($"Required option '{name}' is missing.");
        return values;
    }
}
