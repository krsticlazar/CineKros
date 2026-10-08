using System.Security.Cryptography;
using Npgsql;

var connectionString = Environment.GetEnvironmentVariable("DATABASE_CONNECTION_STRING");
if (string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("DATABASE_CONNECTION_STRING is required.");
    return 2;
}

var draftPoc002 = args.Length > 0 && args[0] == "--poc-draft-002";
var migrationDirectory = args.Length == (draftPoc002 ? 2 : 1)
    ? Path.GetFullPath(args[^1])
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../migrations"));
if (!Directory.Exists(migrationDirectory))
{
    Console.Error.WriteLine("Migration directory was not found.");
    return 2;
}
var paths = Directory.GetFiles(migrationDirectory, "*.sql")
    .Where(path => Path.GetFileName(path).StartsWith("001_", StringComparison.Ordinal) ||
        (draftPoc002 && Path.GetFileName(path).Equals("002_serbian_search_vectors.sql", StringComparison.Ordinal)))
    .OrderBy(Path.GetFileName, StringComparer.Ordinal)
    .ToArray();
if (paths.Length == 0)
{
    Console.Error.WriteLine("No SQL migrations were found.");
    return 2;
}

try
{
    await using var dataSource = NpgsqlDataSource.Create(connectionString);
    await using var connection = await dataSource.OpenConnectionAsync();
    var databaseName = (string?)await new NpgsqlCommand("SELECT current_database()", connection).ExecuteScalarAsync();
    if (draftPoc002 && (databaseName is null || !databaseName.StartsWith("cinekros_sr_poc_", StringComparison.Ordinal)))
        throw new MigrationFailure("Draft migration 002 is restricted to an isolated Serbian POC database.");
    await using (var bootstrap = new NpgsqlCommand("""
        CREATE TABLE IF NOT EXISTS schema_migrations (
            version text PRIMARY KEY,
            sha256 char(64) NOT NULL CHECK (sha256 ~ '^[0-9a-f]{64}$'),
            applied_at timestamptz NOT NULL
        )
        """, connection))
    {
        await bootstrap.ExecuteNonQueryAsync();
    }

    await using (var advisoryLock = new NpgsqlCommand("SELECT pg_advisory_lock(684321097)", connection))
        await advisoryLock.ExecuteNonQueryAsync();

    try
    {
        foreach (var path in paths)
        {
            var version = Path.GetFileNameWithoutExtension(path);
            if (!System.Text.RegularExpressions.Regex.IsMatch(version, "^[0-9]{3}_[a-z0-9_]+$"))
                throw new MigrationFailure("A migration filename does not match the required numbered format.");

            var migrationBytes = await File.ReadAllBytesAsync(path);
            var sql = System.Text.Encoding.UTF8.GetString(migrationBytes);
            var hash = Convert.ToHexStringLower(SHA256.HashData(migrationBytes));
            await using var transaction = await connection.BeginTransactionAsync();
            await using var lookup = new NpgsqlCommand(
                "SELECT sha256 FROM schema_migrations WHERE version = @version",
                connection, transaction);
            lookup.Parameters.AddWithValue("version", version);
            var existing = await lookup.ExecuteScalarAsync();
            if (existing is not null)
            {
                var existingHash = Convert.ToString(existing)?.Trim();
                await transaction.RollbackAsync();
                if (!string.Equals(existingHash, hash, StringComparison.Ordinal))
                    throw new MigrationFailure($"Checksum mismatch for migration {version}.");
                Console.WriteLine($"Verified {version}.");
                continue;
            }

            await using (var apply = new NpgsqlCommand(sql, connection, transaction))
                await apply.ExecuteNonQueryAsync();
            await using (var record = new NpgsqlCommand(
                "INSERT INTO schema_migrations (version, sha256, applied_at) VALUES (@version, @sha256, now())",
                connection, transaction))
            {
                record.Parameters.AddWithValue("version", version);
                record.Parameters.AddWithValue("sha256", hash);
                await record.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
            Console.WriteLine($"Applied {version}.");
        }
    }
    finally
    {
        await using var unlock = new NpgsqlCommand("SELECT pg_advisory_unlock(684321097)", connection);
        await unlock.ExecuteNonQueryAsync();
    }
}
catch (MigrationFailure exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
catch (Exception exception) when (exception is not OperationCanceledException)
{
    Console.Error.WriteLine("Migration failed; database details were suppressed.");
    return 1;
}

return 0;

sealed class MigrationFailure(string message) : Exception(message);
