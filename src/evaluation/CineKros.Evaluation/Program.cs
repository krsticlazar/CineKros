using System.Text.Json;
using CineKros.Api.RealProviders;
using CineKros.Api.Search;
using CineKros.Embedding;

namespace CineKros.Evaluation;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            var options = CliOptions.Parse(args);
            var set = QuerySetReader.Read(options.Queries);
            if (options.Mode == "retrieval")
            {
                var ids = options.CaseIds!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (ids.Length == 0 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length) throw new ArgumentException();
                var selected = new List<EvaluationCase>();
                foreach (var id in ids)
                {
                    var item = set.Cases.SingleOrDefault(c => c.Id == id && c.RetrievalEval) ?? throw new ArgumentException();
                    selected.Add(item);
                }
                set = set with { Cases = selected };
            }
            if (options.Mode == "parser" && options.MaxLiveCalls > set.Cases.Count(c => c.ParserEval)) throw new ArgumentException();
            IReadOnlyDictionary<string, IReadOnlyDictionary<long, int>>? judgments = null;
            if (options.Judgments is not null)
            {
                using var judgmentDoc = JsonDocument.Parse(File.ReadAllText(options.Judgments));
                judgments = HumanScoring.ReadJudgments(judgmentDoc.RootElement);
            }

            var retrieval = new List<RetrievalResult>();
            var parser = new List<ParserComparison>();
            string? embeddingFingerprint = null;
            if (options.Mode == "retrieval")
            {
                var connectionString = RequiredEnvironment("DATABASE_CONNECTION_STRING");
                var modelDirectory = RequiredEnvironment("CINEKROS_E5_MODEL_DIR");
                using var model = new E5EmbeddingModel(modelDirectory);
                embeddingFingerprint = model.ProfileFingerprint;
                using var dataSource = MovieSearchRepository.CreateDataSource(connectionString);
                var search = new MovieSearchRepository(dataSource, model.ProfileFingerprint);
                retrieval.AddRange(await new EvaluationRunner(search, dataSource, model).RunRetrievalAsync(set, judgments, CancellationToken.None));
            }
            else
            {
                if (!options.LiveParser) throw new ArgumentException("Parser mode requires explicit live-parser authorization.");
                var parserCases = set.Cases.Where(c => c.ParserEval).Take(options.MaxLiveCalls).ToArray();
                var apiKey = RequiredEnvironment("GEMINI_API_KEY");
                var promptPath = Path.Combine(AppContext.BaseDirectory, "RealProviders", "query-parser-v4.md");
                var schemaPath = Path.Combine(AppContext.BaseDirectory, "RealProviders", "query-parser.schema.json");
                var prompt = File.ReadAllText(promptPath);
                var schema = File.ReadAllText(schemaPath);
                using var client = new HttpClient();
                var queryParser = new GeminiRealQueryParser(client, apiKey, prompt, schema, new RealParsedQueryValidator());
                foreach (var item in parserCases)
                {
                    try
                    {
                        var evidence = await queryParser.ParseWithEvidenceAsync(item.Language, item.Query, CancellationToken.None);
                        parser.Add(ParserScoring.Compare(item.Id, item.ParserExpected!.Value, evidence.ProviderChecklistJson,
                            evidence.Result, evidence.ProviderDuration.TotalMilliseconds));
                    }
                    catch (RealProviderException error)
                    {
                        parser.Add(new ParserComparison(item.Id, false, false, false, error.Code, 0));
                    }
                }
            }

            var selectedIds = options.Mode == "retrieval" ? set.Cases.Select(c => c.Id).ToArray() : set.Cases.Where(c => c.ParserEval).Take(options.MaxLiveCalls).Select(c => c.Id).ToArray();
            EvaluationReports.Write(new EvaluationReport(set.SchemaVersion, options.Mode, DateTimeOffset.UtcNow,
                MovieSearchRepository.CatalogVersion, MovieSearchRepository.CatalogJsonlSha256,
                MovieSearchRepository.CatalogContentFingerprint, embeddingFingerprint, selectedIds, retrieval, parser), options.Output);
            Console.WriteLine($"Wrote evaluation report for {retrieval.Count} retrieval and {parser.Count} parser results.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Evaluation failed ({error.GetType().Name} at {error.TargetSite?.DeclaringType?.Name}.{error.TargetSite?.Name}). Check the CLI arguments, local configuration, input files, and read-only database availability.");
            return 1;
        }
    }

    private static string RequiredEnvironment(string key) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } value ? value : throw new InvalidOperationException();
}

internal sealed record CliOptions(string Queries, string Output, string Mode, string? Judgments, bool LiveParser, int MaxLiveCalls, string? CaseIds)
{
    public static CliOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (key is "--live-parser") { values[key] = null; continue; }
            if (key is not ("--queries" or "--output" or "--mode" or "--judgments" or "--max-live-calls" or "--case-ids") || i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException();
            values[key] = args[++i];
        }
        var queries = values.GetValueOrDefault("--queries");
        var output = values.GetValueOrDefault("--output");
        var mode = values.GetValueOrDefault("--mode");
        var caseIds = values.GetValueOrDefault("--case-ids");
        if (string.IsNullOrWhiteSpace(queries) || string.IsNullOrWhiteSpace(output) || mode is not ("retrieval" or "parser") || mode == "retrieval" && string.IsNullOrWhiteSpace(caseIds) || mode == "parser" && caseIds is not null) throw new ArgumentException();
        if (Path.GetExtension(output) is ".json" or ".md") output = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, Path.GetFileNameWithoutExtension(output));
        var live = values.ContainsKey("--live-parser");
        var capText = values.GetValueOrDefault("--max-live-calls");
        var cap = 0;
        if (mode == "parser")
        {
            if (!live || !int.TryParse(capText, out cap) || cap < 1) throw new ArgumentException();
        }
        else if (live || capText is not null) throw new ArgumentException();
        return new CliOptions(queries, output, mode, values.GetValueOrDefault("--judgments"), live, cap, caseIds);
    }
}
