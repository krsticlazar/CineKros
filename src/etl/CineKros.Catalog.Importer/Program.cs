using CineKros.Catalog.Importer;

if (args.Length != 1 || args[0].StartsWith('-'))
{
    Console.Error.WriteLine("Usage: CineKros.Catalog.Importer <path-to-movies-catalog.jsonl>");
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
