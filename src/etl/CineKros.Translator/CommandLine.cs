using System.Globalization;

namespace CineKros.Translator;

public sealed class CommandLineException(string message) : Exception(message);

public sealed record CommandArguments(string Command, IReadOnlyDictionary<string, string> Values)
{
    private static readonly IReadOnlyDictionary<string, string[]> Required = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["extract-tags"] = ["catalog", "output-dir"],
        ["propose"] = ["tags", "python", "model-dir", "checkpoint", "output-dir"],
        ["qa"] = ["dictionary", "output-dir"],
        ["apply-review"] = ["dictionary", "review", "output-dir"],
        ["lock"] = ["dictionary", "source-tags", "output-dir"],
        ["lock-full"] = ["candidate", "baseline", "proposals", "source-tags", "catalog", "review", "output-dir"]
    };

    private static readonly HashSet<string> Optional = ["max-items"];

    public string this[string key] => Values[key];

    public static CommandArguments Parse(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || !Required.ContainsKey(args[0]))
            throw new CommandLineException("expected extract-tags, propose, qa, apply-review, lock, or lock-full");

        var command = args[0];
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Count;)
        {
            var token = args[index++];
            if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length == 2)
                throw new CommandLineException($"expected --name value, got '{token}'");

            var name = token[2..];
            if (!Required[command].Contains(name, StringComparer.Ordinal) && !(name == "max-items" && command == "propose"))
                throw new CommandLineException($"unknown option '{token}'");
            if (!values.TryAdd(name, string.Empty))
                throw new CommandLineException($"duplicate option '{token}'");
            if (index >= args.Count || args[index].StartsWith("--", StringComparison.Ordinal))
                throw new CommandLineException($"option '{token}' requires a value");
            values[name] = args[index++];
        }

        foreach (var name in Required[command])
            if (!values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
                throw new CommandLineException($"required option '--{name}' is missing");

        if (values.TryGetValue("max-items", out var maximum) &&
            (!int.TryParse(maximum, NumberStyles.None, CultureInfo.InvariantCulture, out var limit) || limit <= 0 || limit > 993))
            throw new CommandLineException("--max-items must be a positive integer no greater than 993");

        foreach (var pair in values)
        {
            if (pair.Key == "max-items")
                continue;
            if (!Path.IsPathFullyQualified(pair.Value))
                throw new CommandLineException($"--{pair.Key} must be an absolute path");
        }

        return new CommandArguments(command, values);
    }
}

public static class TranslatorCli
{
    public static async Task RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var parsed = CommandArguments.Parse(args);
        switch (parsed.Command)
        {
            case "extract-tags":
                TagWorkflow.Extract(parsed["catalog"], parsed["output-dir"]);
                break;
            case "propose":
                await TagWorkflow.ProposeAsync(parsed, cancellationToken);
                break;
            case "qa":
                TagWorkflow.Qa(parsed["dictionary"], parsed["output-dir"]);
                break;
            case "apply-review":
                TagWorkflow.ApplyReview(parsed["dictionary"], parsed["review"], parsed["output-dir"]);
                break;
            case "lock":
                TagWorkflow.Lock(parsed["dictionary"], parsed["source-tags"], parsed["output-dir"]);
                break;
            case "lock-full":
                FullLockWorkflow.Lock(parsed);
                break;
            default:
                throw new CommandLineException("unsupported command");
        }
    }
}
