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
    if (options.PairedFull)
    {
        await RunPairedFull(options);
        return 0;
    }
    if (options.Language is not null)
    {
        var full = options.FullCatalog;
        var catalog = full
            ? await FullBilingualCatalog.LoadAsync(options.Catalog, options.Manifest, options.Dictionary!, options.SourceCatalog!)
            : null;
        var pocCatalog = full ? null : await MultilingualPocCatalog.LoadAsync(options.Catalog, options.Manifest, options.Dictionary!, options.SourceCatalog!);
        using var pocModel = new E5EmbeddingModel(options.ModelDirectory, profile);
        var source = new E5DocumentVectorSource(pocModel);
        var multilingualResult = full
            ? DocumentVectorGenerator.RunFullCatalog(options.Checkpoint, options.OutputDirectory, options.BatchSize, source, catalog!, options.Language)
            : DocumentVectorGenerator.RunMultilingualPoc(options.Checkpoint, options.OutputDirectory, options.BatchSize, source, pocCatalog!, options.Language);
        Console.WriteLine($"Published {multilingualResult.RecordCount} {options.Language} {(full ? "full" : "POC")} multilingual vectors; generated={multilingualResult.Generated}, reused={multilingualResult.Reused}.");
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

static async Task RunPairedFull(GeneratorArguments options)
{
    var catalog = await FullBilingualCatalog.LoadAsync(options.Catalog, options.Manifest, options.Dictionary!, options.SourceCatalog!);
    var tokenAudit = FullCatalogTokenAudit.Measure(options.ModelDirectory, catalog);
    var auditDirectory = Path.Combine(Directory.GetCurrentDirectory(), ".local", "planning", "reports", "sr-phase-08", "generation");
    Directory.CreateDirectory(auditDirectory);
    var tokenAuditPath = Path.Combine(auditDirectory, "full-token-audit-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ", System.Globalization.CultureInfo.InvariantCulture) + ".json");
    await File.WriteAllBytesAsync(tokenAuditPath, JsonSerializer.SerializeToUtf8Bytes(tokenAudit, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Full-catalog token audit: documents={tokenAudit.DocumentCount}, maxRawTokens={tokenAudit.MaximumRawTokenCount}, over512 EN={tokenAudit.EnglishTruncatedCount}, SR={tokenAudit.SerbianTruncatedCount}; evidence={tokenAuditPath}");
    if (options.TokenAuditOnly)
    {
        if (tokenAudit.EnglishTruncatedCount != 0 || tokenAudit.SerbianTruncatedCount != 0)
            throw new InvalidDataException("Token-audit-only completed with truncations; no vector inference was started.");
        return;
    }
    if (tokenAudit.EnglishTruncatedCount != 0 || tokenAudit.SerbianTruncatedCount != 0)
        throw new InvalidDataException($"Full-catalog tokenizer audit found EN={tokenAudit.EnglishTruncatedCount}, SR={tokenAudit.SerbianTruncatedCount} inputs over {EmbeddingProfileDescriptor.MultilingualE5Base.MaxTokens} tokens. Review evidence at {tokenAuditPath} before inference.");
    MultilingualPocCatalogDocument? validatedPocV2 = null;
    string? pocV2Root = options.PocV2ReuseRoot;
    if (pocV2Root is not null)
    {
        var baseRoot = Path.Combine(Path.GetDirectoryName(pocV2Root)!, "poc-v1");
        var approvedMapping = Path.Combine(Directory.GetCurrentDirectory(), ".local", "planning", "reports", "sr-phase-06t", "review", "corrections-approved.json");
        validatedPocV2 = await CorrectedPocCatalog.LoadAsync(
            Path.Combine(pocV2Root, "catalog", "movies-catalog.jsonl"), Path.Combine(pocV2Root, "catalog", "manifest.json"),
            Path.Combine(pocV2Root, "translation", "tag-translations-sr.json"), approvedMapping,
            Path.Combine(baseRoot, "catalog", "movies-catalog.jsonl"), Path.Combine(baseRoot, "catalog", "manifest.json"),
            Path.Combine(baseRoot, "translation", "tag-translations-sr.json"), options.SourceCatalog!);
    }
    var outputRoot = Path.GetFullPath(options.OutputDirectory);
    var checkpointRoot = Path.GetFullPath(options.Checkpoint);
    var existing = new Dictionary<string, RunResult?>(StringComparer.Ordinal)
    {
        ["en"] = DocumentVectorGenerator.ValidatePublishedFullCatalog(catalog, "en", Path.Combine(outputRoot, "en", "published")),
        ["sr"] = DocumentVectorGenerator.ValidatePublishedFullCatalog(catalog, "sr", Path.Combine(outputRoot, "sr", "published"))
    };
    var seeded = new Dictionary<string, int>(StringComparer.Ordinal) { ["en"] = 0, ["sr"] = 0 };
    if (validatedPocV2 is not null)
        foreach (var language in new[] { "en", "sr" })
            if (existing[language] is null)
                seeded[language] = DocumentVectorGenerator.SeedFullCatalogFromValidatedPocV2(
                    Path.Combine(checkpointRoot, language, "checkpoint.jsonl"), catalog, validatedPocV2, language,
                    Path.Combine(pocV2Root!, "embeddings", language, "document-vectors.jsonl"));
    var needsModel = existing.Values.Any(x => x is null);
    var watch = Stopwatch.StartNew();
    using var model = needsModel ? new E5EmbeddingModel(options.ModelDirectory, EmbeddingProfileDescriptor.MultilingualE5Base) : null;
    watch.Stop();
    var loadMilliseconds = watch.ElapsedMilliseconds;
    var process = Process.GetCurrentProcess();
    var loadedWorkingSetBytes = process.WorkingSet64;
    var source = model is null ? null : new E5DocumentVectorSource(model);
    var en = existing["en"] ?? DocumentVectorGenerator.RunFullCatalog(Path.Combine(checkpointRoot, "en", "checkpoint.jsonl"), Path.Combine(outputRoot, "en", "published"), 1, source!, catalog, "en");
    var sr = existing["sr"] ?? DocumentVectorGenerator.RunFullCatalog(Path.Combine(checkpointRoot, "sr", "checkpoint.jsonl"), Path.Combine(outputRoot, "sr", "published"), 1, source!, catalog, "sr");
    var reportDirectory = auditDirectory;
    Directory.CreateDirectory(reportDirectory);
    var reportPath = Path.Combine(reportDirectory, "paired-full-resource-report-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ", System.Globalization.CultureInfo.InvariantCulture) + ".json");
    var report = new
    {
        format = "multilingual-full-resource-report-v1", catalogIdentitySha256 = catalog.IdentitySha256,
        catalogSha256 = catalog.CatalogSha256, sourceCatalogSha256 = catalog.SourceCatalogSha256,
        dictionarySha256 = catalog.DictionarySha256, profileFingerprint = model?.ProfileFingerprint ?? EmbeddingProfileDescriptor.MultilingualE5Base.ProfileFingerprint,
        tokenAuditReport = tokenAuditPath, tokenAuditSha256 = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(tokenAuditPath))),
        tokenAuditDocumentCount = tokenAudit.DocumentCount, maxRawTokenCount = tokenAudit.MaximumRawTokenCount,
        englishTruncatedCount = tokenAudit.EnglishTruncatedCount, serbianTruncatedCount = tokenAudit.SerbianTruncatedCount,
        modelLoadMilliseconds = loadMilliseconds, loadedWorkingSetBytes, peakWorkingSetBytes = process.PeakWorkingSet64,
        encoderInstanceCount = model is null ? 0 : 1, inferenceRunCount = model?.InferenceRunCount ?? 0,
        pocV2ReuseRequested = validatedPocV2 is not null, pocV2SeededRows = seeded,
        languages = new[] { new { language = "en", generated = en.Generated, reused = en.Reused, count = en.RecordCount, elapsedMilliseconds = en.ElapsedMilliseconds },
            new { language = "sr", generated = sr.Generated, reused = sr.Reused, count = sr.RecordCount, elapsedMilliseconds = sr.ElapsedMilliseconds } },
        totalDocumentMilliseconds = en.ElapsedMilliseconds + sr.ElapsedMilliseconds
    };
    await File.WriteAllBytesAsync(reportPath, JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Paired full catalog complete: EN {en.RecordCount} (generated={en.Generated}, reused={en.Reused}), SR {sr.RecordCount} (generated={sr.Generated}, reused={sr.Reused}); encoder instances={(model is null ? 0 : 1)}; report={reportPath}");
}

internal sealed record ProbeInputs(string[] En, string[] Sr);
