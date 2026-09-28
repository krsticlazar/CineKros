using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using CineKros.Catalog.Importer;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Npgsql;

namespace CineKros.Importer.Tests;

[TestClass]
[DoNotParallelize]
public sealed class CatalogImporterIntegrationTests
{
    private const string Image = "pgvector/pgvector:0.8.6-pg17-bookworm";
    private const string Password = "c02_disposable_test_password_20260927";
    private static readonly string CatalogPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/data/derived/final/b05a-real-tmdb-01/movies-catalog.jsonl"));
    private static readonly string MigrationPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/migrations/001_initial_schema.sql"));
    private static readonly string ContainerName = $"cinekros-c02-importer-it-{Guid.NewGuid():N}";
    private static CatalogDocument _catalog = null!;
    private static bool _ownsContainer;
    private static int _port;

    [ClassInitialize]
    public static async Task StartDisposablePostgres(TestContext _)
    {
        _catalog = await CatalogValidator.LoadAsync(CatalogPath);
        var existing = await Docker("container", "inspect", ContainerName);
        if (existing.ExitCode == 0) throw new InvalidOperationException("The exact C02 test container name already exists; refusing to touch it.");

        var started = await Docker(["run", "--detach", "--rm", "--name", ContainerName,
            "--tmpfs", "/var/lib/postgresql/data",
            "--publish", "127.0.0.1::5432",
            "--env", "POSTGRES_USER=c02_worker",
            "--env", $"POSTGRES_PASSWORD={Password}",
            "--env", "POSTGRES_DB=c02_bootstrap",
            Image], 300000);
        if (started.ExitCode != 0) throw new InvalidOperationException("Could not start the named disposable PostgreSQL test container.");
        _ownsContainer = true;

        var portResult = await Docker("port", ContainerName, "5432/tcp");
        var portMatch = Regex.Match(portResult.StandardOutput, @":(?<port>[0-9]+)\s*$", RegexOptions.Multiline);
        if (portResult.ExitCode != 0 || !portMatch.Success) throw new InvalidOperationException("Could not resolve the disposable PostgreSQL loopback port.");
        _port = int.Parse(portMatch.Groups["port"].Value, CultureInfo.InvariantCulture);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(40);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                await using var connection = new NpgsqlConnection(Connection("c02_bootstrap"));
                await connection.OpenAsync();
                return;
            }
            catch (NpgsqlException) { await Task.Delay(500); }
        }
        throw new InvalidOperationException("The named disposable PostgreSQL test container did not become ready.");
    }

    [ClassCleanup]
    public static async Task RemoveDisposablePostgres()
    {
        if (!_ownsContainer) return;
        var exists = await Docker("container", "inspect", ContainerName);
        if (exists.ExitCode == 0) await Docker("rm", "--force", ContainerName);
        _ownsContainer = false;
    }

    [TestMethod]
    public async Task ImportsAllRowsAndIdenticalRerunMakesNoWrites()
    {
        var connectionString = await NewMigratedDatabase();
        Assert.AreEqual("Imported 9730 catalog movies.", await CatalogImporter.ImportAsync(connectionString, _catalog));
        var before = await ReadAudit(connectionString);
        Assert.AreEqual("Verified identical catalog; no changes made.", await CatalogImporter.ImportAsync(connectionString, _catalog));
        Assert.AreEqual(before, await ReadAudit(connectionString), "A no-op rerun must not rewrite movie or state rows.");

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*), count(*) FILTER (WHERE tmdb_id IS NULL), count(*) FILTER (WHERE genres IS NULL), count(*) FILTER (WHERE average_rating IS NULL), count(*) FILTER (WHERE original_language='cn'), count(*) FILTER (WHERE original_language='sh'), (SELECT count(*) FROM catalog_import_state), (SELECT count(*) FROM movie_embeddings) FROM movies", connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.IsTrue(await reader.ReadAsync());
        Assert.AreEqual(9730L, reader.GetInt64(0));
        Assert.AreEqual(153L, reader.GetInt64(1));
        Assert.AreEqual(145L, reader.GetInt64(2));
        Assert.AreEqual(145L, reader.GetInt64(3));
        Assert.AreEqual(51L, reader.GetInt64(4));
        Assert.AreEqual(1L, reader.GetInt64(5));
        Assert.AreEqual(1L, reader.GetInt64(6));
        Assert.AreEqual(0L, reader.GetInt64(7));
        await reader.DisposeAsync();
        Assert.AreEqual(CatalogValidator.CatalogVersion, await ScalarString(connectionString, "SELECT catalog_version FROM catalog_import_state WHERE id=1"));
        Assert.AreEqual(CatalogValidator.ExpectedHash, await ScalarString(connectionString, "SELECT btrim(catalog_jsonl_sha256) FROM catalog_import_state WHERE id=1"));
        Assert.AreEqual(CatalogValidator.ExpectedFingerprint, await ScalarString(connectionString, "SELECT btrim(catalog_content_fingerprint) FROM catalog_import_state WHERE id=1"));
        Assert.AreEqual((long)CatalogValidator.ExpectedCount, await ScalarLong(connectionString, "SELECT movie_count FROM catalog_import_state WHERE id=1"));

        var expected = _catalog.Movies[0];
        await using var precision = new NpgsqlCommand("SELECT average_rating::text, imdb_id, genres FROM movies WHERE movie_lens_id=@id", connection);
        precision.Parameters.AddWithValue("id", expected.Id);
        await using var precisionReader = await precision.ExecuteReaderAsync();
        Assert.IsTrue(await precisionReader.ReadAsync());
        Assert.AreEqual(expected.ImdbId, precisionReader.GetString(1));
        Assert.IsTrue(expected.Genres!.SequenceEqual(precisionReader.GetFieldValue<string[]>(2)));
        Assert.AreEqual(decimal.Parse(expected.AverageRating!, CultureInfo.InvariantCulture), decimal.Parse(precisionReader.GetString(0), CultureInfo.InvariantCulture), "PostgreSQL numeric must retain the source value without rounding.");
    }

    [TestMethod]
    public async Task RejectsChangedStateAndInconsistentStoredMovieRows()
    {
        var connectionString = await NewMigratedDatabase();
        await CatalogImporter.ImportAsync(connectionString, _catalog);
        await Execute(connectionString, "UPDATE catalog_import_state SET catalog_jsonl_sha256=repeat('0',64)");
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => CatalogImporter.ImportAsync(connectionString, _catalog));
        Assert.AreEqual(new string('0', 64), await ScalarString(connectionString, "SELECT btrim(catalog_jsonl_sha256) FROM catalog_import_state WHERE id=1"));
        await Execute(connectionString, "UPDATE catalog_import_state SET catalog_jsonl_sha256=@hash", ("hash", CatalogValidator.ExpectedHash));
        await Execute(connectionString, "UPDATE movies SET title='tampered' WHERE movie_lens_id=@id", ("id", _catalog.Movies[0].Id));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => CatalogImporter.ImportAsync(connectionString, _catalog));
        Assert.AreEqual((long)CatalogValidator.ExpectedCount, await ScalarLong(connectionString, "SELECT count(*) FROM movies"));
        Assert.AreEqual("tampered", await ScalarString(connectionString, "SELECT title FROM movies WHERE movie_lens_id=@id", ("id", _catalog.Movies[0].Id)));
    }

    [TestMethod]
    public async Task RollsBackAllMoviesAndStateWhenDatabaseRejectsFinalInsert()
    {
        var connectionString = await NewMigratedDatabase();
        var lastId = _catalog.Movies[^1].Id;
        await Execute(connectionString, $"CREATE FUNCTION reject_c02_last_movie() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.movie_lens_id={lastId} THEN RAISE EXCEPTION 'injected C02 insert failure'; END IF; RETURN NEW; END $$; CREATE TRIGGER c02_reject_last_movie BEFORE INSERT ON movies FOR EACH ROW EXECUTE FUNCTION reject_c02_last_movie()");
        await Assert.ThrowsAsync<NpgsqlException>(() => CatalogImporter.ImportAsync(connectionString, _catalog));
        Assert.AreEqual(0L, await ScalarLong(connectionString, "SELECT count(*) FROM movies"));
        Assert.AreEqual(0L, await ScalarLong(connectionString, "SELECT count(*) FROM catalog_import_state"));
    }

    [TestMethod]
    public async Task CliSanitizesUnavailableDatabaseFailure()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var importerDll = Path.Combine(AppContext.BaseDirectory, "CineKros.Catalog.Importer.dll");
        Assert.IsTrue(File.Exists(importerDll), "The importer executable assembly must be copied into the test output.");
        var startInfo = new ProcessStartInfo("dotnet") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        startInfo.ArgumentList.Add(importerDll);
        startInfo.ArgumentList.Add(CatalogPath);
        startInfo.Environment["DATABASE_CONNECTION_STRING"] = $"Host=127.0.0.1;Port=1;Username=c02_worker;Password={Password};Database=unavailable;Timeout=1;Pooling=false";
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start importer CLI process.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        var output = await stdoutTask + await stderrTask;
        Assert.AreNotEqual(0, process.ExitCode);
        Assert.IsTrue(output.Contains("Catalog import failed; database details were suppressed.", StringComparison.Ordinal));
        Assert.IsFalse(output.Contains(Password, StringComparison.Ordinal));
        Assert.IsFalse(output.Contains("Npgsql", StringComparison.Ordinal));
    }

    private static async Task<string> NewMigratedDatabase()
    {
        var database = "c02_import_" + Guid.NewGuid().ToString("N");
        await Execute(Connection("c02_bootstrap"), $"CREATE DATABASE \"{database}\"");
        var connectionString = Connection(database);
        await Execute(connectionString, await File.ReadAllTextAsync(MigrationPath));
        return connectionString;
    }

    private static string Connection(string database) => new NpgsqlConnectionStringBuilder
    {
        Host = "127.0.0.1", Port = _port, Username = "c02_worker", Password = Password,
        Database = database, Timeout = 4, CommandTimeout = 30, Pooling = false
    }.ConnectionString;

    private static async Task Execute(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarLong(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarString(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> ReadAudit(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT (SELECT xmin::text FROM catalog_import_state WHERE id=1) || ':' || (SELECT catalog_jsonl_sha256 FROM catalog_import_state WHERE id=1) || ':' || (SELECT xmin::text FROM movies WHERE movie_lens_id=@id)", connection);
        command.Parameters.AddWithValue("id", _catalog.Movies[0].Id);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> Docker(params string[] arguments) => await Docker(arguments, 30000);

    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> Docker(string[] arguments, int timeoutMilliseconds)
    {
        var startInfo = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start Docker CLI.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(timeoutMilliseconds)); }
        catch (TimeoutException) { process.Kill(true); throw new TimeoutException("Docker CLI timed out during the bounded C02 disposable-container operation."); }
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
