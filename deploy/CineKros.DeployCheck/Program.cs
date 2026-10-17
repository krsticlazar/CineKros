using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text.Json;
using CineKros.Embedding;
using CineKros.TextNormalization;

namespace CineKros.DeployCheck;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private const int MaxReferenceBytes = 10 * 1024 * 1024;

    private static int Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
        {
            PrintHelp();
            return 0;
        }

        try
        {
            var options = ParseOptions(args);
            var modelDir = options.GetValueOrDefault("model-dir") ?? Environment.GetEnvironmentVariable("CINEKROS_E5_MODEL_DIR");
            if (string.IsNullOrWhiteSpace(modelDir)) throw new ArgumentException("Set --model-dir or CINEKROS_E5_MODEL_DIR.");
            var output = Required(options, "output");
            if (File.Exists(output)) throw new IOException("Output already exists; choose a new path.");

            if (args[0] == "compare") return Compare(modelDir, Required(options, "reference"), output);
            if (args[0] == "export") return Export(modelDir, output);
            throw new ArgumentException("Unknown command.");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Deploy check failed: {SafeMessage(ex)}");
            return 2;
        }
    }

    private static int Export(string modelDir, string output)
    {
        var report = CreateReport(modelDir);
        ValidateReference(report);
        WriteNewJson(output, report);
        Console.WriteLine($"Export written: {Path.GetFullPath(output)} ({report.Queries.Count} queries)");
        return 0;
    }

    private static int Compare(string modelDir, string referencePath, string output)
    {
        var referenceInfo = new FileInfo(referencePath);
        if (!referenceInfo.Exists) throw new FileNotFoundException("Reference JSON was not found.");
        var referenceBytes = ReadBoundedFile(referencePath, MaxReferenceBytes);
        var reference = JsonSerializer.Deserialize<ExportReport>(referenceBytes, JsonOptions)
            ?? throw new InvalidDataException("Reference JSON is empty or invalid.");
        ValidateReference(reference);

        var currentPath = Path.GetFullPath(output);
        if (File.Exists(currentPath)) throw new IOException("Output already exists; choose a new path.");
        var current = CreateReport(modelDir);
        var report = CompareReports(reference, current, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(referenceBytes)));
        WriteNewJson(currentPath, report);
        Console.WriteLine($"Comparison written: {currentPath}; structural checks: {(report.StructuralChecksPassed ? "PASS" : "FAIL")}; numerical acceptance: PENDING ACCEPTANCE");
        return report.StructuralChecksPassed ? 0 : 3;
    }

    private static ExportReport CreateReport(string modelDir)
    {
        var runtime = RuntimeSnapshot();
        PrintRuntimePreamble(runtime);
        using var process = Process.GetCurrentProcess();
        using var model = new E5EmbeddingModel(modelDir, EmbeddingProfileDescriptor.MultilingualE5Base);
        var workingSetAfterLoad = process.WorkingSet64;
        var warmup = Stopwatch.StartNew(); _ = model.EmbedQuery(QuerySet.Rows[0].Normalized); warmup.Stop();
        var rows = new List<QueryRecord>(QuerySet.Rows.Count); var latencies = new List<double>(QuerySet.Rows.Count);
        foreach (var query in QuerySet.Rows)
        {
            var tokens = model.Tokenize(E5EmbeddingModel.FormatQueryInput(query.Normalized));
            var timer = Stopwatch.StartNew(); var vector = model.EmbedQuery(query.Normalized); timer.Stop();
            latencies.Add(timer.Elapsed.TotalMilliseconds);
            rows.Add(new QueryRecord(query.Id, query.Language, query.Script, query.Original, query.Normalized,
                tokens.InputIds, tokens.AttentionMask, vector, Norm(vector)));
        }
        process.Refresh();
        return new ExportReport(new ExportMetadata("CineKros.DeployCheck/v1", model.ProfileId,
            EmbeddingProfileDescriptor.MultilingualE5Base.ModelId, EmbeddingProfileDescriptor.MultilingualE5Base.Revision,
            model.ProfileFingerprint, EmbeddingProfileDescriptor.MultilingualE5Base.Artifacts.Select(x => new ArtifactRecord(x.FileName, x.Sha256)).ToArray()),
            runtime, "nearest-rank: ceil(p*n), one-based", model.LoadTime.TotalMilliseconds, workingSetAfterLoad, process.PeakWorkingSet64, process.WorkingSet64,
            warmup.Elapsed.TotalMilliseconds, latencies.ToArray(), Percentile(latencies, .50), Percentile(latencies, .95), rows);
    }

    internal static void ValidateReference(ExportReport? reference)
    {
        var profile = EmbeddingProfileDescriptor.MultilingualE5Base;
        if (reference is null || reference.Metadata is null || reference.Metadata.Artifacts is null)
            throw new InvalidDataException("Reference metadata is missing.");
        if (reference.Metadata.SchemaVersion != "CineKros.DeployCheck/v1" || reference.Metadata.Profile != profile.ProfileVersion ||
            reference.Metadata.ModelId != profile.ModelId || reference.Metadata.Revision != profile.Revision ||
            reference.Metadata.ProfileFingerprint != profile.ProfileFingerprint ||
            reference.Metadata.Artifacts.Any(x => x is null) ||
            !reference.Metadata.Artifacts.OrderBy(x => x.FileName, StringComparer.Ordinal).SequenceEqual(
                profile.Artifacts.Select(x => new ArtifactRecord(x.FileName, x.Sha256)).OrderBy(x => x.FileName, StringComparer.Ordinal)))
            throw new InvalidDataException("Reference model/profile identity is not the supported profile.");
        if (reference.Queries is null || reference.Queries.Count != QuerySet.Rows.Count) throw new InvalidDataException("Reference query set must contain exactly 30 rows.");
        for (var i = 0; i < QuerySet.Rows.Count; i++)
        {
            var row = reference.Queries[i]; var query = QuerySet.Rows[i];
            if (row is null) throw new InvalidDataException($"Reference query row is missing at index {i}.");
            if (row.Id != query.Id || row.Language != query.Language || row.Script != query.Script || row.Original != query.Original || row.Normalized != query.Normalized)
                throw new InvalidDataException($"Reference query set identity is invalid at row {i}.");
            if (row.TokenIds is null || row.AttentionMask is null || row.TokenIds.Length == 0 || row.TokenIds.Length != row.AttentionMask.Length || row.TokenIds.Length > profile.MaxTokens)
                throw new InvalidDataException($"Reference token data is invalid at {row.Id}.");
            if (row.TokenIds.Any(id => id < 0) || row.AttentionMask.Any(mask => mask != 1))
                throw new InvalidDataException($"Reference token IDs or attention mask are invalid at {row.Id}.");
            if (row.Vector is null || row.Vector.Length != profile.Dimension || !double.IsFinite(row.VectorNorm) || row.VectorNorm <= 0 || row.Vector.Any(x => !float.IsFinite(x)) || Norm(row.Vector) <= 0)
                throw new InvalidDataException($"Reference vector data is invalid at {row.Id}.");
        }
    }

    internal static CompareReport CompareReports(ExportReport reference, ExportReport current, string referenceArtifactSha256)
    {
        var mismatches = new List<string>();
        if (reference.Metadata.SchemaVersion != current.Metadata.SchemaVersion || reference.Metadata.Profile != current.Metadata.Profile ||
            reference.Metadata.ModelId != current.Metadata.ModelId || reference.Metadata.Revision != current.Metadata.Revision ||
            reference.Metadata.ProfileFingerprint != current.Metadata.ProfileFingerprint ||
            !reference.Metadata.Artifacts.OrderBy(x => x.FileName, StringComparer.Ordinal).SequenceEqual(current.Metadata.Artifacts.OrderBy(x => x.FileName, StringComparer.Ordinal)))
            mismatches.Add("model/profile/artifact identity differs");

        var comparisons = new List<QueryComparison>(Math.Min(reference.Queries.Count, current.Queries.Count));
        if (reference.Queries.Count != QuerySet.Rows.Count || current.Queries.Count != QuerySet.Rows.Count)
            mismatches.Add("query row count differs from the fixed 30-row set");
        var count = Math.Min(Math.Min(reference.Queries.Count, current.Queries.Count), QuerySet.Rows.Count);
        for (var i = 0; i < count; i++)
        {
            var expected = reference.Queries[i]; var actual = current.Queries[i];
            if (expected.Id != actual.Id || expected.Language != actual.Language || expected.Script != actual.Script || expected.Original != actual.Original || expected.Normalized != actual.Normalized)
                mismatches.Add($"query identity or text differs at index {i}");
            if (!expected.TokenIds.SequenceEqual(actual.TokenIds) || !expected.AttentionMask.SequenceEqual(actual.AttentionMask))
                mismatches.Add($"token IDs or mask differ at {actual.Id}");
            var left = expected.Vector; var right = actual.Vector;
            var valid = left.Length == E5EmbeddingModel.Dimension && right.Length == E5EmbeddingModel.Dimension && left.All(float.IsFinite) && right.All(float.IsFinite) && Norm(left) > 0 && Norm(right) > 0;
            if (!valid)
            {
                mismatches.Add($"invalid vector at {actual.Id}");
                comparisons.Add(new QueryComparison(actual.Id, null, null, null, false));
                continue;
            }
            var l2 = L2Error(left, right);
            comparisons.Add(new QueryComparison(actual.Id, Cosine(left, right), l2, l2 / Math.Max(Norm(left), double.Epsilon), true));
        }
        var cosines = comparisons.Where(x => x.TechnicallyValid).Select(x => x.Cosine!.Value).ToArray();
        return new CompareReport("CineKros.DeployCheck.Compare/v1", mismatches.Count == 0, "PENDING ACCEPTANCE",
            "No numerical acceptance threshold was configured.", referenceArtifactSha256, current, mismatches, comparisons,
            cosines.Length == 0 ? null : cosines.Min(), cosines.Length == 0 ? null : cosines.Average());
    }

    private static byte[] ReadBoundedFile(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maxBytes) throw new InvalidDataException("Reference JSON exceeds the 10 MiB limit.");
        var data = new byte[maxBytes + 1]; var total = 0;
        while (total < data.Length)
        {
            var read = stream.Read(data, total, data.Length - total);
            if (read == 0) break;
            total += read;
        }
        if (total > maxBytes) throw new InvalidDataException("Reference JSON exceeds the 10 MiB limit.");
        return data[..total];
    }

    private static object RuntimeSnapshot()
    {
        var packages = new[] { typeof(E5EmbeddingModel).Assembly, typeof(Microsoft.ML.OnnxRuntime.InferenceSession).Assembly,
            Assembly.Load("Tokenizers.DotNet") }.Select(a => new { assembly = a.GetName().Name, version = a.GetName().Version?.ToString() }).ToArray();
        string? linuxFlags = null;
        if (OperatingSystem.IsLinux())
        {
            try { linuxFlags = File.ReadLines("/proc/cpuinfo").FirstOrDefault(x => x.StartsWith("flags", StringComparison.OrdinalIgnoreCase))?.Split(':', 2).Last().Trim(); }
            catch { }
        }
        return new { os = RuntimeInformation.OSDescription, osArchitecture = RuntimeInformation.OSArchitecture.ToString(), processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            framework = RuntimeInformation.FrameworkDescription, packages, x86Intrinsics = new { sse = Sse.IsSupported, sse2 = Sse2.IsSupported, avx = Avx.IsSupported, avx2 = Avx2.IsSupported, avxVnni = AvxVnni.IsSupported, avx512F = Avx512F.IsSupported }, linuxCpuFlags = linuxFlags ?? "NOT MEASURED" };
    }

    private static void PrintRuntimePreamble(object runtime) => Console.WriteLine("Runtime preamble (before model load): " + JsonSerializer.Serialize(runtime));

    private static Dictionary<string, string> ParseOptions(string[] args)
    {
        if (args[0] is not ("export" or "compare")) throw new ArgumentException("Expected export or compare command.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Options require --name value pairs.");
            if (!result.TryAdd(args[i][2..], args[++i])) throw new ArgumentException("Duplicate option.");
        }
        var allowed = args[0] == "export" ? new[] { "model-dir", "output" } : new[] { "model-dir", "reference", "output" };
        if (result.Keys.Except(allowed, StringComparer.Ordinal).Any()) throw new ArgumentException("Unknown option.");
        if (args[0] == "compare" && !result.ContainsKey("reference")) throw new ArgumentException("compare requires --reference.");
        return result;
    }

    private static string Required(Dictionary<string, string> options, string name) => options.TryGetValue(name, out var value) ? value : throw new ArgumentException($"Missing --{name}.");
    private static void WriteNewJson<T>(string path, T value)
    {
        var full = Path.GetFullPath(path); var dir = Path.GetDirectoryName(full)!; Directory.CreateDirectory(dir);
        using var stream = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, value, JsonOptions);
    }
    internal static double Norm(float[] values) => Math.Sqrt(values.Sum(v => (double)v * v));
    internal static double Cosine(float[] a, float[] b) => a.Zip(b, (x, y) => (double)x * y).Sum() / (Norm(a) * Norm(b));
    internal static double L2Error(float[] a, float[] b) => Math.Sqrt(a.Zip(b, (x, y) => { var d = (double)x - y; return d * d; }).Sum());
    internal static double Percentile(List<double> data, double p) { var sorted = data.Order().ToArray(); return sorted[(int)Math.Ceiling(p * sorted.Length) - 1]; }
    private static string SafeMessage(Exception ex) => ex switch
    {
        InvalidDataException => ex.Message,
        ArgumentException => ex.Message,
        IOException => "I/O operation failed; details omitted.",
        _ => $"{ex.GetType().Name}; details omitted."
    };
    private static void PrintHelp() => Console.WriteLine("CineKros deploy-check\n  export --model-dir PATH --output JSON\n  compare --model-dir PATH --reference JSON --output JSON\n  CINEKROS_E5_MODEL_DIR may supply --model-dir. Outputs are never overwritten.");
}

internal sealed record ExportMetadata(string SchemaVersion, string Profile, string ModelId, string Revision, string ProfileFingerprint, ArtifactRecord[] Artifacts);
internal sealed record ArtifactRecord(string FileName, string Sha256);
internal sealed record QueryRecord(string Id, string Language, string Script, string Original, string Normalized, long[] TokenIds, long[] AttentionMask, float[] Vector, double VectorNorm);
internal sealed record ExportReport(ExportMetadata Metadata, object Runtime, string PercentileMethod, double ModelAndTokenizerLoadMs, long WorkingSetAfterLoadBytes, long PeakWorkingSetAfterQueriesBytes, long CurrentWorkingSetAfterQueriesBytes, double FirstWarmupMs, double[] QueryLatenciesMs, double P50Ms, double P95Ms, List<QueryRecord> Queries);
internal sealed record QueryComparison(string Id, double? Cosine, double? L2Error, double? RelativeL2Error, bool TechnicallyValid);
internal sealed record CompareReport(string SchemaVersion, bool StructuralChecksPassed, string NumericalAcceptance, string Note, string ReferenceArtifactSha256, ExportReport CurrentReport, List<string> Mismatches, List<QueryComparison> Queries, double? MinimumCosine, double? MeanCosine);

internal sealed record ProbeQuery(string Id, string Language, string Script, string Original, string Normalized);
internal static class QuerySet
{
    private static readonly (string En, string SrLatin, string SrCyrillic)[] Intents =
    [
        ("A clever mystery with an unexpected ending", "Pametna misterija sa neočekivanim krajem", "Паметна мистерија са неочекиваним крајем"),
        ("A warm comedy about close friends", "Topla komedija o bliskim prijateljima", "Топла комедија о блиским пријатељима"),
        ("A tense science fiction story about space", "Napeta naučna fantastika o svemiru", "Напета научна фантастика о свемиру"),
        ("A moving family drama across generations", "Dirljiva porodična drama kroz generacije", "Дирљива породична драма кроз генерације"),
        ("A fast action adventure with a brave hero", "Brza akciona avantura sa hrabrim junakom", "Брза акциона авантура са храбрим јунаком"),
        ("A thoughtful historical film about courage", "Promišljen istorijski film o hrabrosti", "Промишљен историјски филм о храбрости"),
        ("A gentle romance with an uplifting mood", "Nežna romansa vedrog raspoloženja", "Нежна романса ведрог расположења"),
        ("A dark thriller about a missing person", "Mračni triler o nestaloj osobi", "Мрачни трилер о несталој особи"),
        ("A vivid animated story for all ages", "Živopisna animirana priča za sve uzraste", "Живописна анимирана прича за све узрасте"),
        ("A quiet documentary about the natural world", "Miran dokumentarac o svetu prirode", "Миран документарац о свету природе")
    ];
    public static IReadOnlyList<ProbeQuery> Rows { get; } = Intents.SelectMany((x, i) => new[]
    {
        new ProbeQuery($"intent-{i + 1:00}-en-latn", "en", "Latn", x.En, x.En),
        new ProbeQuery($"intent-{i + 1:00}-sr-latn", "sr", "Latn", x.SrLatin, SerbianLatinNormalizer.Normalize(x.SrLatin)),
        new ProbeQuery($"intent-{i + 1:00}-sr-cyrl", "sr", "Cyrl", x.SrCyrillic, SerbianLatinNormalizer.Normalize(x.SrCyrillic))
    }).ToArray();
}
