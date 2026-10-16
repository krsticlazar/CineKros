namespace CineKros.VectorImporter;

internal static class ActorImportCommandV6
{
    internal const string ImportUsage = "Usage: --paired-actor-v6 --expected-database <isolated DB> --baseline <JSON> --catalog <V6 JSONL> --manifest <V6 manifest> --dictionary <dictionary JSON> --source-catalog <original JSONL> --pair-root <V6 paired root> --migrations <absolute directory> [--test-fault <after-migration|partial-cast|partial-vectors|partial-state>]";
    internal const string CaptureUsage = "Usage: --capture-actor-v6-baseline --output <absent absolute JSON> --migrations <absolute directory> --backup-sha256 <verified backup SHA256>";
    internal static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var capture = args[0] == "--capture-actor-v6-baseline";
            var fields = capture ? new[] { "--output", "--migrations", "--backup-sha256" } :
                ["--expected-database", "--baseline", "--catalog", "--manifest", "--dictionary", "--source-catalog", "--pair-root", "--migrations"];
            if ((capture ? args.Length != 7 : args.Length is not (17 or 19)) || fields.Where((x, n) => args[1 + 2 * n] != x).Any() ||
                (!capture && args.Length == 19 && args[17] != "--test-fault"))
            { Console.Error.WriteLine(capture ? CaptureUsage : ImportUsage); return 2; }
            var fault = !capture && args.Length == 19 ? ParseFault(args[18]) : ActorImportFaultV6.None;
            if (!capture && !ActorImportBaselineV6.IsAllowedExecutionTarget(args[2], args[2]))
            { Console.Error.WriteLine("V6 import permits only the exact isolated rehearsal/test database family; production is never allowed."); return 2; }
            var connection = Environment.GetEnvironmentVariable("DATABASE_CONNECTION_STRING");
            if (string.IsNullOrWhiteSpace(connection)) { Console.Error.WriteLine("DATABASE_CONNECTION_STRING is required."); return 2; }
            if (capture)
            {
                await ActorImportBaselineV6.CaptureAsync(connection, args[2], args[4], args[6]);
                Console.WriteLine("Captured the exact read-only P8 baseline descriptor; no database changes made.");
            }
            else Console.WriteLine(await ActorPairedImporterV6.ImportAsync(connection, args[2], args[4], args[6], args[8], args[10], args[12], args[14], args[16], fault));
            return 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or InvalidOperationException or System.Text.Json.JsonException or KeyNotFoundException or FormatException or OverflowException or Npgsql.NpgsqlException)
        {
            Console.Error.WriteLine(ex is Npgsql.NpgsqlException ? "V6 operation failed; database details were suppressed." : $"V6 operation failed: {ex.Message}");
            return 1;
        }
    }
    internal static ActorImportFaultV6 ParseFault(string name) => name switch
    {
        "after-migration" => ActorImportFaultV6.AfterMigration, "partial-cast" => ActorImportFaultV6.PartialCast,
        "partial-vectors" => ActorImportFaultV6.PartialVectors, "partial-state" => ActorImportFaultV6.PartialState,
        _ => throw new ArgumentException("Unknown test fault point.")
    };
}
