using CineKros.E5.Generator;
using CineKros.Embedding;
using CineKros.Catalog.Importer;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

try
{
    var options = GeneratorArguments.Parse(Environment.GetCommandLineArgs().Skip(1).ToArray());
    var profile = options.Profile == EmbeddingProfileDescriptor.MultilingualE5Base.ProfileVersion
        ? EmbeddingProfileDescriptor.MultilingualE5Base : EmbeddingProfileDescriptor.LegacyEnglish;
    if (options.PairedPoc)
    {
        await RunPairedPoc(options);
        return 0;
    }
    if (options.Language is not null)
    {
        var catalog = await MultilingualPocCatalog.LoadAsync(options.Catalog, options.Manifest, options.Dictionary!, options.SourceCatalog!);
        using var pocModel = new E5EmbeddingModel(options.ModelDirectory, profile);
        var pocResult = DocumentVectorGenerator.RunMultilingualPoc(options.Checkpoint, options.OutputDirectory, options.BatchSize,
            new E5DocumentVectorSource(pocModel), catalog, options.Language);
        Console.WriteLine($"Published {pocResult.RecordCount} {options.Language} multilingual POC vectors; generated={pocResult.Generated}, reused={pocResult.Reused}.");
        return 0;
    }
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

static async Task RunPairedPoc(GeneratorArguments options)
{
    var catalog = await MultilingualPocCatalog.LoadAsync(options.Catalog, options.Manifest, options.Dictionary!, options.SourceCatalog!);
    var outputRoot = Path.GetFullPath(options.OutputDirectory);
    var checkpointRoot = Path.GetFullPath(options.Checkpoint);
    foreach (var language in new[] { "en", "sr" })
    {
        var languageOutput = Path.Combine(outputRoot, language);
        var languageCheckpoint = Path.Combine(checkpointRoot, language, "checkpoint.jsonl");
        if (Directory.Exists(languageOutput))
        {
            var entries = Directory.GetFileSystemEntries(languageOutput);
            if (!File.Exists(languageCheckpoint) || entries.Length != 1 || !string.Equals(Path.GetFullPath(entries[0]), Path.GetFullPath(languageCheckpoint), StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Paired POC output already exists for {language}; preserve it and select a new output root.");
        }
        else if (File.Exists(languageOutput)) throw new IOException($"Paired POC output path is not a directory for {language}.");
    }

    var reportDirectory = Path.Combine(Directory.GetCurrentDirectory(), ".local", "planning", "reports", "sr-phase-04-vectors");
    var runId = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ", System.Globalization.CultureInfo.InvariantCulture);
    var scratch = Path.Combine(reportDirectory, "scratch", "paired-poc-" + runId);
    Directory.CreateDirectory(scratch);
    var probes = new ProbeInputs(
        ["A warm, gentle story about friendship and home.", "A tense mystery with a quiet atmosphere.", "A hopeful journey through difficult choices.", "A thoughtful drama about memory and family."],
        ["Topla priča o prijateljstvu i domu.", "Napeta misterija sa tihom atmosferom.", "Putovanje puno nade i teških izbora.", "Promišljena drama o sećanju i porodici."]);
    var probePayload = JsonSerializer.SerializeToUtf8Bytes(probes);
    var probePath = Path.Combine(scratch, "resource-probe-inputs.json");
    if (File.Exists(probePath)) throw new IOException("Paired POC probe plan already exists; use a fresh scratch run directory.");
    await File.WriteAllBytesAsync(probePath, probePayload);
    var identityPayloadPath = Path.Combine(scratch, "catalog-content-identity-payload.json");
    await File.WriteAllTextAsync(identityPayloadPath, catalog.IdentityPayloadJson, new System.Text.UTF8Encoding(false));
    var probeHash = Convert.ToHexStringLower(SHA256.HashData(probePayload));

    var process = Process.GetCurrentProcess();
    var loadWatch = Stopwatch.StartNew();
    using var model = new E5EmbeddingModel(options.ModelDirectory, EmbeddingProfileDescriptor.MultilingualE5Base);
    loadWatch.Stop();
    var idleWorkingSet = process.WorkingSet64;
    var first = new Dictionary<string, double>(StringComparer.Ordinal);
    var queryVectors = new Dictionary<string, List<object>>(StringComparer.Ordinal) { ["en"] = [], ["sr"] = [] };
    foreach (var language in new[] { "en", "sr" })
    {
        var query = language == "en" ? probes.En[0] : probes.Sr[0];
        var timer = Stopwatch.StartNew(); var vector = model.EmbedQuery(query); timer.Stop();
        first[language] = timer.Elapsed.TotalMilliseconds;
        queryVectors[language].Add(new { text = query, vector });
    }
    var warm = new Dictionary<string, double[]>(StringComparer.Ordinal);
    foreach (var language in new[] { "en", "sr" })
    {
        var queries = language == "en" ? probes.En : probes.Sr;
        var timings = new List<double>(20);
        var savedQueries = new HashSet<string>(StringComparer.Ordinal) { queries[0] };
        for (var round = 0; round < 5; round++)
            foreach (var query in queries)
            {
                var timer = Stopwatch.StartNew(); var vector = model.EmbedQuery(query); timer.Stop(); timings.Add(timer.Elapsed.TotalMilliseconds);
                if (savedQueries.Add(query)) queryVectors[language].Add(new { text = query, vector });
            }
        warm[language] = timings.Order().ToArray();
    }

    var source = new E5DocumentVectorSource(model);
    var enRun = DocumentVectorGenerator.RunMultilingualPoc(Path.Combine(checkpointRoot, "en", "checkpoint.jsonl"),
        Path.Combine(outputRoot, "en"), 1, source, catalog, "en");
    var srRun = DocumentVectorGenerator.RunMultilingualPoc(Path.Combine(checkpointRoot, "sr", "checkpoint.jsonl"),
        Path.Combine(outputRoot, "sr"), 1, source, catalog, "sr");
    var report = new
    {
        format = "multilingual-poc-resource-report-v1", catalogIdentitySha256 = catalog.IdentitySha256, profileFingerprint = model.ProfileFingerprint,
        probePlanSha256 = probeHash, modelLoadMilliseconds = loadWatch.ElapsedMilliseconds, idleWorkingSetBytes = idleWorkingSet,
        peakWorkingSetBytes = process.PeakWorkingSet64, firstQueryMilliseconds = first, warmQueryCountPerLanguage = 20,
        warmQueryP50Milliseconds = warm.ToDictionary(x => x.Key, x => x.Value[10], StringComparer.Ordinal),
        warmQueryP95Milliseconds = warm.ToDictionary(x => x.Key, x => x.Value[18], StringComparer.Ordinal),
        encoderInstanceCount = 1, inferenceRunCount = model.InferenceRunCount,
        languages = new[] { new { language = "en", generated = enRun.Generated, reused = enRun.Reused, count = enRun.RecordCount, elapsedMilliseconds = enRun.ElapsedMilliseconds },
            new { language = "sr", generated = srRun.Generated, reused = srRun.Reused, count = srRun.RecordCount, elapsedMilliseconds = srRun.ElapsedMilliseconds } },
        documentMillisecondsPerNewVector = (enRun.Generated + srRun.Generated) == 0 ? 0 : (enRun.ElapsedMilliseconds + srRun.ElapsedMilliseconds) / (double)(enRun.Generated + srRun.Generated),
        totalDocumentMilliseconds = enRun.ElapsedMilliseconds + srRun.ElapsedMilliseconds
    };
    var reportPath = Path.Combine(reportDirectory, "paired-poc-resource-report-" + runId + ".json");
    var probeVectorPath = Path.Combine(scratch, "resource-query-vectors.json");
    await File.WriteAllBytesAsync(probeVectorPath, JsonSerializer.SerializeToUtf8Bytes(new
    {
        format = "multilingual-poc-resource-query-vectors-v1", profile = model.ProfileDescriptor.ProfileVersion,
        profileFingerprint = model.ProfileFingerprint, catalogIdentitySha256 = catalog.IdentitySha256,
        languages = new[] { new { language = "en", vectors = queryVectors["en"] }, new { language = "sr", vectors = queryVectors["sr"] } }
    }));
    await File.WriteAllBytesAsync(reportPath, JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Paired POC complete: EN {enRun.Generated}/150 generated, SR {srRun.Generated}/150 generated; encoder instances=1; report={reportPath}");
}

internal sealed record ProbeInputs(string[] En, string[] Sr);
