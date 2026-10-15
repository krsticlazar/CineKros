using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CineKros.Catalog.Importer;
using CineKros.Embedding;

namespace CineKros.E5.Generator;

internal static class ActorPairedGenerationV6
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static async Task RunAsync(GeneratorArguments options)
    {
        var total = Stopwatch.StartNew();
        var expected = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "database/data/embeddings/multilingual-e5-base-int8-onnx-v1/v6-catalog-01"));
        if (!string.Equals(options.OutputDirectory, expected, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(options.Checkpoint, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("V6 output and checkpoint roots must both be the isolated approved v6-catalog-01 root.");
        var catalog = await ActorCatalogV6Reader.LoadAsync(options.Catalog, options.Manifest, options.Dictionary!, options.SourceCatalog!);
        var inputMilliseconds = total.ElapsedMilliseconds;
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cancel.Cancel(); };
        Console.CancelKeyPress += handler;
        try
        {
            PreflightRoot(expected);
            foreach (var language in new[] { "en", "sr" }) DocumentVectorGenerator.PreflightV6Checkpoint(Checkpoint(expected, language), catalog, language);
            var en = DocumentVectorGenerator.ValidatePublishedV6(catalog, "en", Published(expected, "en"));
            var sr = DocumentVectorGenerator.ValidatePublishedV6(catalog, "sr", Published(expected, "sr"));
            var pair = Path.Combine(expected, "paired-release.json");
            if (File.Exists(pair))
            {
                if (en is null || sr is null || !File.ReadAllBytes(pair).SequenceEqual(PairBytes(catalog, expected)))
                    throw new InvalidDataException("Existing V6 paired descriptor is incompatible; preserve it unchanged.");
            }
            TokenAuditResult? audit = null; string? auditPath = null;
            var reportRoot = Path.Combine(Directory.GetCurrentDirectory(), ".local/planning/reports/v6-phase-02");
            Directory.CreateDirectory(reportRoot);
            var run = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
            var auditWatch = Stopwatch.StartNew();
            if (options.TokenAuditOnly || en is null || sr is null)
            {
                audit = FullCatalogTokenAudit.Measure(options.ModelDirectory, catalog);
                auditPath = Path.Combine(reportRoot, "token-audit-" + run + ".json");
                await File.WriteAllBytesAsync(auditPath, JsonSerializer.SerializeToUtf8Bytes(audit, Options), cancel.Token);
                Console.WriteLine($"V6 token audit: documents={audit.DocumentCount}, max={audit.MaximumRawTokenCount}, over512 EN={audit.EnglishTruncatedCount}, SR={audit.SerbianTruncatedCount}; report={auditPath}");
                if (audit.EnglishTruncatedCount != 0 || audit.SerbianTruncatedCount != 0) throw new InvalidDataException("V6 token audit exceeded 512; no inference started.");
            }
            auditWatch.Stop();
            if (options.TokenAuditOnly) return;
            cancel.Token.ThrowIfCancellationRequested();
            var load = Stopwatch.StartNew();
            using var model = en is null || sr is null ? new E5EmbeddingModel(options.ModelDirectory, EmbeddingProfileDescriptor.MultilingualE5Base) : null;
            load.Stop(); var process = Process.GetCurrentProcess(); var loaded = process.WorkingSet64;
            var source = model is null ? null : new E5DocumentVectorSource(model);
            var languageWatch = Stopwatch.StartNew();
            en ??= DocumentVectorGenerator.RunV6(Checkpoint(expected, "en"), Published(expected, "en"), source!, catalog, "en", cancel.Token);
            var enWall = languageWatch.ElapsedMilliseconds; languageWatch.Restart();
            sr ??= DocumentVectorGenerator.RunV6(Checkpoint(expected, "sr"), Published(expected, "sr"), source!, catalog, "sr", cancel.Token);
            var srWall = languageWatch.ElapsedMilliseconds;
            PublishPair(catalog, expected);
            process.Refresh(); // WorkingSet64 caches Process information; refresh the cumulative peak after generation.
            var report = new { format = "actor-v6-generation-resource-report-v1", catalogIdentitySha256 = catalog.IdentitySha256,
                inputValidationMilliseconds = inputMilliseconds, tokenAuditMilliseconds = auditWatch.ElapsedMilliseconds, tokenAuditReport = auditPath,
                tokenAuditSha256 = auditPath is null ? null : Hash(auditPath), modelLoadMilliseconds = load.ElapsedMilliseconds,
                loadedWorkingSetBytes = loaded, peakWorkingSetBytes = process.PeakWorkingSet64, encoderInstanceCount = model is null ? 0 : 1,
                inferenceRunCount = model?.InferenceRunCount ?? 0, totalWallMilliseconds = total.ElapsedMilliseconds,
                languages = new[] { new { language = "en", en.Generated, en.Reused, en.RecordCount, generationMilliseconds = en.ElapsedMilliseconds, currentRunWallMilliseconds = enWall },
                    new { language = "sr", sr.Generated, sr.Reused, sr.RecordCount, generationMilliseconds = sr.ElapsedMilliseconds, currentRunWallMilliseconds = srWall } } };
            var reportPath = Path.Combine(reportRoot, "resource-" + run + ".json");
            await File.WriteAllBytesAsync(reportPath, JsonSerializer.SerializeToUtf8Bytes(report, Options));
            Console.WriteLine($"V6 pair complete: EN generated={en.Generated}, reused={en.Reused}; SR generated={sr.Generated}, reused={sr.Reused}; modelLoads={(model is null ? 0 : 1)}, inferenceRuns={model?.InferenceRunCount ?? 0}; report={reportPath}");
        }
        finally { Console.CancelKeyPress -= handler; }
    }

    private static string Published(string root, string language) => Path.Combine(root, language, "published");
    private static string Checkpoint(string root, string language) => Path.Combine(root, language, "checkpoint.jsonl");
    private static void PreflightRoot(string root)
    {
        if (File.Exists(root)) throw new InvalidDataException("V6 output root is a file.");
        if (!Directory.Exists(root)) return;
        foreach (var path in Directory.GetFileSystemEntries(root))
        {
            var name = Path.GetFileName(path);
            if (name == "paired-release.json" && File.Exists(path)) continue;
            if (name is not ("en" or "sr") || !Directory.Exists(path)) throw new InvalidDataException("Unexpected V6 root entry.");
            foreach (var entry in Directory.GetFileSystemEntries(path))
                if (!(Path.GetFileName(entry) == "published" && Directory.Exists(entry)) &&
                    !(File.Exists(entry) && (Path.GetFileName(entry) == "checkpoint.jsonl" || Path.GetFileName(entry).StartsWith("checkpoint.jsonl.backup-", StringComparison.Ordinal))))
                    throw new InvalidDataException("Unexpected V6 language directory entry.");
        }
    }

    internal static byte[] PairBytes(ActorCatalogV6Document catalog, string root) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        format = "actor-v6-paired-release-v1", catalogVersion = catalog.CatalogVersion, catalogSha256 = catalog.CatalogSha256,
        catalogIdentitySha256 = catalog.IdentitySha256, selectionIdSetSha256 = catalog.SelectionIdSetSha256,
        dictionarySha256 = catalog.DictionarySha256, actorVocabularySha256 = catalog.ActorVocabularySha256, orderedCastMappingSha256 = catalog.OrderedCastMappingSha256,
        overridesSha256 = catalog.OverridesSha256, castRulesSha256 = catalog.CastRulesSha256, profileFingerprint = catalog.ProfileFingerprint,
        languages = new[] { "en", "sr" }.Select(language => new { language, corpusSha256 = language == "en" ? catalog.EnCorpusSha256 : catalog.SrCorpusSha256,
            textFormatVersion = language == "en" ? catalog.EnTextFormatVersion : catalog.SrTextFormatVersion, recordCount = catalog.Movies.Count,
            vectorsSha256 = Hash(Path.Combine(Published(root, language), "document-vectors.jsonl")), manifestSha256 = Hash(Path.Combine(Published(root, language), "manifest.json")) }).ToArray()
    }, Options);

    internal static void PublishPair(ActorCatalogV6Document catalog, string root)
    {
        foreach (var language in new[] { "en", "sr" })
            if (DocumentVectorGenerator.ValidatePublishedV6(catalog, language, Published(root, language)) is null) throw new InvalidDataException("Cannot pair incomplete V6 publications.");
        var bytes = PairBytes(catalog, root); var path = Path.Combine(root, "paired-release.json");
        if (File.Exists(path))
        {
            if (!File.ReadAllBytes(path).SequenceEqual(bytes)) throw new InvalidDataException("Differing V6 descriptor cannot be overwritten.");
            return;
        }
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try { using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); } File.Move(temp, path, false); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        if (!File.ReadAllBytes(path).SequenceEqual(PairBytes(catalog, root))) throw new InvalidDataException("Published V6 descriptor changed.");
    }
    private static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
}
