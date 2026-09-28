using CineKros.VectorImporter;

if (args.Length != 4 || args[0] != "--catalog" || args[2] != "--artifact")
{
    Console.Error.WriteLine("Usage: CineKros.VectorImporter --catalog <reviewed-catalog.jsonl> --artifact <complete-artifact-directory>");
    return 2;
}
var connectionString = Environment.GetEnvironmentVariable("DATABASE_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString)) { Console.Error.WriteLine("DATABASE_CONNECTION_STRING is required."); return 2; }
try
{
    var artifact = await VectorArtifactValidator.LoadAsync(args[3], args[1]);
    Console.WriteLine(await VectorImporter.ImportAsync(connectionString, artifact));
    return 0;
}
catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException or Npgsql.NpgsqlException)
{
    Console.Error.WriteLine(ex is Npgsql.NpgsqlException ? "Vector import failed; database details were suppressed." : $"Vector import failed: {ex.Message}");
    return 1;
}
