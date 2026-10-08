using CineKros.VectorImporter;

if (args.Length > 0 && args[0] == "--paired-poc")
{
    if (args.Length != 13 || args[1] != "--catalog" || args[3] != "--manifest" || args[5] != "--dictionary" || args[7] != "--source-catalog" || args[9] != "--en" || args[11] != "--sr")
    {
        Console.Error.WriteLine("Usage: CineKros.VectorImporter --paired-poc --catalog <catalog.jsonl> --manifest <manifest.json> --dictionary <dictionary.json> --source-catalog <source.jsonl> --en <en-artifact-dir> --sr <sr-artifact-dir>");
        return 2;
    }
}
else if (args.Length != 4 || args[0] != "--catalog" || args[2] != "--artifact")
{
    Console.Error.WriteLine("Usage: CineKros.VectorImporter --catalog <reviewed-catalog.jsonl> --artifact <complete-artifact-directory>");
    return 2;
}
var connectionString = Environment.GetEnvironmentVariable("DATABASE_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString)) { Console.Error.WriteLine("DATABASE_CONNECTION_STRING is required."); return 2; }
try
{
    if (args.Length > 0 && args[0] == "--paired-poc")
    {
        Console.WriteLine(await PairedPocImporter.ImportAsync(connectionString, args[2], args[4], args[6], args[8], args[10], args[12]));
        return 0;
    }
    var artifact = await VectorArtifactValidator.LoadAsync(args[3], args[1]);
    Console.WriteLine(await VectorImporter.ImportAsync(connectionString, artifact));
    return 0;
}
catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException or Npgsql.NpgsqlException)
{
    Console.Error.WriteLine(ex is Npgsql.NpgsqlException ? "Vector import failed; database details were suppressed." : $"Vector import failed: {ex.Message}");
    return 1;
}
