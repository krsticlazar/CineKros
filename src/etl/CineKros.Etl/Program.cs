using CineKros.Etl;
using System.Security.Cryptography;

try
{
    if (args.Length > 0 && args[0] == "real-embed")
    {
        throw new ArgumentException("The real-embed command is superseded and disabled. Use the CineKros.E5.Generator CLI for E5 document vectors.");
    }
    if (args.Length > 0 && args[0] == "fake-embed")
    {
        var options = FakeEmbeddingCliArguments.Parse(args[1..]);
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            var fakeResult = FakeDocumentEmbeddingRunner.Run(options.CatalogPath, options.ManifestPath, options.OutputDirectory, options.CheckpointPath,
                options.Configuration, new DeterministicFakeDocumentVectorSource(), cancellation.Token);
            Console.WriteLine($"FAKE ONLY: {fakeResult.OutputRecords} records; generated {fakeResult.Generated}, reused {fakeResult.Reused}; SHA-256 {fakeResult.OutputSha256}.");
            return 0;
        }
        finally { Console.CancelKeyPress -= cancelHandler; }
    }
    if (args.Length > 0 && args[0] == "catalog-merge")
    {
        var merge = CombinedCatalogArguments.Parse(args[1..]);
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            var merged = CombinedCatalogExporter.Export(new CombinedCatalogOptions(merge.SemanticDirectory, merge.EnrichmentDirectory,
                merge.OutputDirectory, "a20493ca502106788f3ece917be99ae2e3aa9fbf747841f77adf11ef7a5f620a", 9730), cancellation.Token);
            Console.WriteLine($"Catalog merge complete: {merged.OutputRecords} records, SHA-256 {merged.OutputSha256}, fingerprint {merged.ContentFingerprint}.");
            return 0;
        }
        finally { Console.CancelKeyPress -= cancelHandler; }
    }
    if (args.Length > 0 && args[0] == "enrich-v2")
    {
        var v2 = EnrichmentV2Arguments.Parse(args[1..]);
        var token = Environment.GetEnvironmentVariable("TMDB_READ_ACCESS_TOKEN");
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("TMDB_READ_ACCESS_TOKEN is required for enrich-v2.");
        using var cancellation = new CancellationTokenSource();
        using var attemptGuard = new TmdbAttemptGuard(v2.MaxHttpAttempts);
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri("https://api.themoviedb.org") };
            var client = new TmdbDetailsClient(http, token, attemptGuard: attemptGuard);
            var v2Result = await EnrichmentV2Runner.RunAsync(v2, client.GetAsync, cancellation.Token, null, attemptGuard);
            Console.WriteLine($"Enrichment complete: {v2Result.OutputRecords} records, {attemptGuard.AttemptCount} HTTP attempts in {attemptGuard.Elapsed.TotalMinutes:F1} minutes, SHA-256 {v2Result.OutputSha256}.");
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }
    if (args.Length > 0 && args[0] == "semantic-export")
    {
        var semanticArgs = SemanticCliArguments.Parse(args[1..]);
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            var semanticResult = SemanticExporter.Export(new SemanticExportOptions(
                semanticArgs.InputJsonl,
                semanticArgs.InputManifest,
                semanticArgs.TagdlCsv,
                semanticArgs.OutputDirectory,
                Convert.FromHexString(SemanticExporter.B1aHash),
                Convert.FromHexString(SemanticExporter.B1aManifestHash),
                Convert.FromHexString(SemanticExporter.TagdlHash),
                new SemanticExpectedCounts(9730, 10551655),
                RequirePinnedSnapshot: true), cancellation.Token);
            Console.WriteLine($"Semantic export complete: {semanticResult.OutputRecords} records, {semanticResult.TagdlRows} TagDL rows, SHA-256 {semanticResult.OutputSha256}, fingerprint {semanticResult.ContentFingerprint}.");
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }
    var arguments = CliArguments.Parse(args);
    var result = MetadataExporter.Export(new ExportOptions(
        arguments.SourceRoot,
        arguments.OutputDirectory,
        ExpectedCounts.ApprovedSnapshot));

    Console.WriteLine(
        $"Export complete: {result.OutputRecords} records, {result.MissingMetadataIds.Count} MISSING_METADATA exclusions, SHA-256 {result.OutputSha256}.");
    return 0;
}
catch (Exception exception) when (exception is ArgumentException or ExportValidationException)
{
    Console.Error.WriteLine($"ERROR: {exception.Message}");
    return 1;
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("ERROR: File operation failed.");
    return 1;
}
catch (Exception exception) when (exception is TmdbRequestException or TmdbConfigurationException)
{
    Console.Error.WriteLine($"ERROR: {exception.Message}");
    return 1;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("ERROR: ETL operation cancelled.");
    return 130;
}
catch (Exception exception) when (args.Length > 0 && args[0] == "real-embed")
{
    Console.Error.WriteLine($"ERROR: Real embedding stopped safely ({exception.GetType().Name}).");
    return 1;
}
