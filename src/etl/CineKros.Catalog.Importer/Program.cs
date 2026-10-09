using CineKros.Catalog.Importer;

if (args.Length == 4 && args[0] == "export-full-bilingual")
{
    try
    {
        var catalog = await FullBilingualCatalog.ExportAsync(args[1], args[2], args[3]);
        Console.WriteLine($"Exported {catalog.Movies.Count} movies; catalog SHA-256 {catalog.CatalogSha256}; identity {catalog.IdentitySha256}.");
        return 0;
    }
    catch (InvalidDataException exception)
    {
        Console.Error.WriteLine($"Full bilingual catalog validation failed: {exception.Message}");
        return 1;
    }
    catch (Exception exception) when (exception is not OperationCanceledException)
    {
        Console.Error.WriteLine("Full bilingual catalog export failed; detailed paths and data were suppressed.");
        return 1;
    }
}
if (args.Length != 1 || args[0].StartsWith('-'))
{
    Console.Error.WriteLine("Usage: CineKros.Catalog.Importer <path-to-movies-catalog.jsonl> | export-full-bilingual <source-jsonl> <dictionary-json> <new-output-root>");
    return 2;
}
var connectionString = Environment.GetEnvironmentVariable("DATABASE_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("DATABASE_CONNECTION_STRING is required.");
    return 2;
}
try
{
    var catalog = await CatalogValidator.LoadAsync(args[0]);
    Console.WriteLine(await CatalogImporter.ImportAsync(connectionString, catalog));
    return 0;
}
catch (InvalidDataException exception)
{
    Console.Error.WriteLine($"Catalog validation failed: {exception.Message}");
    return 1;
}
catch (Exception exception) when (exception is not OperationCanceledException)
{
    Console.Error.WriteLine("Catalog import failed; database details were suppressed.");
    return 1;
}
