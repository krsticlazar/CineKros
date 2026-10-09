using CineKros.VectorImporter;

if (args.Length > 0 && args[0] == "--paired-poc")
{
    if (args.Length != 13 || args[1] != "--catalog" || args[3] != "--manifest" || args[5] != "--dictionary" || args[7] != "--source-catalog" || args[9] != "--en" || args[11] != "--sr")
    {
        Console.Error.WriteLine("Usage: CineKros.VectorImporter --paired-poc --catalog <catalog.jsonl> --manifest <manifest.json> --dictionary <dictionary.json> --source-catalog <source.jsonl> --en <en-artifact-dir> --sr <sr-artifact-dir>");
        return 2;
    }
}
else if (args.Length > 0 && args[0] == "--paired-full-cutover")
{
    if (args.Length is not (23 or 25) || args[1] != "--expected-database" || args[3] != "--baseline" || args[5] != "--catalog" ||
        args[7] != "--manifest" || args[9] != "--dictionary" || args[11] != "--source-catalog" || args[13] != "--en" ||
        args[15] != "--sr" || args[17] != "--legacy-catalog" || args[19] != "--legacy-artifact" || args[21] != "--migrations" ||
        (args.Length == 25 && (args[23] != "--test-fail-after-vector-rows" || !int.TryParse(args[24], out var failAfter) || failAfter is < 1 or > 9730)))
    {
        Console.Error.WriteLine("Usage: CineKros.VectorImporter --paired-full-cutover --expected-database <explicit rehearsal/test DB> --baseline <MAIN baseline.json> --catalog <full catalog.jsonl> --manifest <full manifest.json> --dictionary <dictionary.json> --source-catalog <source catalog.jsonl> --en <published EN artifact dir> --sr <published SR artifact dir> --legacy-catalog <legacy catalog.jsonl> --legacy-artifact <legacy published artifact dir> --migrations <canonical migrations dir> [--test-fail-after-vector-rows <1..9730>]");
        return 2;
    }
    if (args[2] == "cinekros")
    {
        Console.Error.WriteLine("This worker CLI cannot target production; production cutover is MAIN-only.");
        return 2;
    }
    if (!FullImportBaseline.IsAllowedExecutionTarget(args[2], args[2], mainProductionApproval: false))
    {
        Console.Error.WriteLine("Expected database is outside the exact Phase 9 rehearsal/test family.");
        return 2;
    }
}
else if (args.Length != 4 || args[0] != "--catalog" || args[2] != "--artifact")
{
    Console.Error.WriteLine("Usage: CineKros.VectorImporter --catalog <reviewed-catalog.jsonl> --artifact <complete-artifact-directory> (or explicit --paired-poc / --paired-full-cutover modes)");
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
    if (args.Length > 0 && args[0] == "--paired-full-cutover")
    {
        var failAfter = args.Length == 25 ? int.Parse(args[24], System.Globalization.CultureInfo.InvariantCulture) : (int?)null;
        Console.WriteLine(await FullPairedImporter.ImportAsync(connectionString!, args[2], args[4], args[6], args[8], args[10], args[12], args[14], args[16], args[18], args[20], args[22], failAfterVectorRows: failAfter));
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
