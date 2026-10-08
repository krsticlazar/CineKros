using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Diagnostics;
using CineKros.VectorImporter;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Npgsql;

namespace CineKros.VectorImporter.Tests;

[TestClass]
public sealed class SrPocImporterIntegrationTests
{
    private const string TestDatabase = "cinekros_sr_poc_phase04_test_import";

    [TestMethod]
    public async Task MigratorDefaultAndDraftModesIgnoreUnreleased003()
    {
        var admin = Environment.GetEnvironmentVariable("CINEKROS_SR_POC_TEST_ADMIN_CONNECTION");
        var root = Environment.GetEnvironmentVariable("CINEKROS_SR_POC_ROOT");
        if (string.IsNullOrWhiteSpace(admin) || string.IsNullOrWhiteSpace(root))
            Assert.Inconclusive("Set process-only SR POC admin connection and repository root for migration gate integration test.");
        const string database = "cinekros_sr_poc_phase04_test_migrator";
        var adminBuilder = new NpgsqlConnectionStringBuilder(admin) { Database = "postgres", Pooling = false };
        var targetBuilder = new NpgsqlConnectionStringBuilder(admin) { Database = database, Pooling = false };
        await using (var bootstrap = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await bootstrap.OpenAsync();
            await using var check = new NpgsqlCommand("SELECT current_database(),count(*) FROM pg_database WHERE datname=@db", bootstrap);
            check.Parameters.AddWithValue("db", database); await using var reader = await check.ExecuteReaderAsync(); await reader.ReadAsync();
            Assert.AreEqual("postgres", reader.GetString(0)); Assert.AreEqual(0L, reader.GetInt64(1)); await reader.DisposeAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {database}", bootstrap); await create.ExecuteNonQueryAsync();
        }
        var migrationDir = Path.Combine(Path.GetTempPath(), "sr-poc-migrations-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(migrationDir);
        try
        {
            foreach (var filename in new[] { "001_initial_schema.sql", "002_serbian_search_vectors.sql" })
                File.Copy(Path.Combine(root, "database", "migrations", filename), Path.Combine(migrationDir, filename));
            File.Copy(Path.Combine(root, "tests", "CineKros.VectorImporter.Tests", "Fixtures", "003_test_only_probe.sql"), Path.Combine(migrationDir, "003_test_only_probe.sql"));
            var migrator = Path.Combine(root, "src", "etl", "CineKros.Database.Migrator", "bin", "Debug", "net10.0", "CineKros.Database.Migrator.dll");
            Assert.AreEqual(0, await RunMigratorAsync(root, migrator, targetBuilder.ConnectionString, migrationDir, draft002: false));
            Assert.AreEqual("001_initial_schema", await ScalarStringAsync(targetBuilder.ConnectionString, "SELECT string_agg(version,',' ORDER BY version) FROM schema_migrations"));
            Assert.AreEqual(0L, await ScalarAsync(targetBuilder.ConnectionString, "SELECT count(*) FROM information_schema.columns WHERE table_name='movie_embeddings' AND column_name='embedding_sr'"));
            Assert.AreEqual(0, await RunMigratorAsync(root, migrator, targetBuilder.ConnectionString, migrationDir, draft002: true));
            Assert.AreEqual("001_initial_schema,002_serbian_search_vectors", await ScalarStringAsync(targetBuilder.ConnectionString, "SELECT string_agg(version,',' ORDER BY version) FROM schema_migrations"));
            Assert.AreEqual(0L, await ScalarAsync(targetBuilder.ConnectionString, "SELECT count(*) FROM pg_tables WHERE schemaname=current_schema() AND tablename='migration_003_test_only_probe'"));
        }
        finally
        {
            Directory.Delete(migrationDir, recursive: true);
            await using var target = new NpgsqlConnection(targetBuilder.ConnectionString); await target.OpenAsync();
            Assert.AreEqual(database, await new NpgsqlCommand("SELECT current_database()", target).ExecuteScalarAsync()); await target.CloseAsync();
            await using var cleanup = new NpgsqlConnection(adminBuilder.ConnectionString); await cleanup.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE {database}", cleanup); await drop.ExecuteNonQueryAsync();
        }
    }

    [TestMethod]
    public async Task PairedImportRollsBackFailureThenImportsAndIsIdempotent()
    {
        var admin = Environment.GetEnvironmentVariable("CINEKROS_SR_POC_TEST_ADMIN_CONNECTION");
        var root = Environment.GetEnvironmentVariable("CINEKROS_SR_POC_ROOT");
        var input = Environment.GetEnvironmentVariable("CINEKROS_SR_POC_INPUT_ROOT");
        var sourceCatalog = Environment.GetEnvironmentVariable("CINEKROS_SR_POC_SOURCE_CATALOG");
        if (string.IsNullOrWhiteSpace(admin) || string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(input) || string.IsNullOrWhiteSpace(sourceCatalog))
            Assert.Inconclusive("Set the four process-only SR POC integration-test paths/connection variables to run the isolated PostgreSQL fixture.");

        var adminBuilder = new NpgsqlConnectionStringBuilder(admin) { Database = "postgres", Pooling = false };
        var targetBuilder = new NpgsqlConnectionStringBuilder(admin) { Database = TestDatabase, Pooling = false };
        await using (var bootstrap = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await bootstrap.OpenAsync();
            await using var verify = new NpgsqlCommand("SELECT current_database(), count(*) FROM pg_database WHERE datname=@db", bootstrap);
            verify.Parameters.AddWithValue("db", TestDatabase);
            await using var vr = await verify.ExecuteReaderAsync(); await vr.ReadAsync();
            Assert.AreEqual("postgres", vr.GetString(0)); Assert.AreEqual(0L, vr.GetInt64(1), "Test target must be absent before creation.");
            await vr.DisposeAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {TestDatabase}", bootstrap); await create.ExecuteNonQueryAsync();
        }

        try
        {
            await using (var target = new NpgsqlConnection(targetBuilder.ConnectionString))
            {
                await target.OpenAsync();
                Assert.AreEqual(TestDatabase, await new NpgsqlCommand("SELECT current_database()", target).ExecuteScalarAsync());
                await using (var tracking = new NpgsqlCommand("CREATE TABLE schema_migrations(version text PRIMARY KEY,sha256 char(64) NOT NULL,applied_at timestamptz NOT NULL)", target)) await tracking.ExecuteNonQueryAsync();
                foreach (var filename in new[] { "001_initial_schema.sql", "002_serbian_search_vectors.sql" })
                {
                    var path = Path.Combine(root, "database", "migrations", filename);
                    var bytes = await File.ReadAllBytesAsync(path);
                    await using (var apply = new NpgsqlCommand(Encoding.UTF8.GetString(bytes), target)) await apply.ExecuteNonQueryAsync();
                    await using var record = new NpgsqlCommand("INSERT INTO schema_migrations(version,sha256,applied_at) VALUES(@v,@h,now())", target);
                    record.Parameters.AddWithValue("v", Path.GetFileNameWithoutExtension(filename));
                    record.Parameters.AddWithValue("h", Convert.ToHexStringLower(SHA256.HashData(bytes)));
                    await record.ExecuteNonQueryAsync();
                }
                await Assert.ThrowsAsync<PostgresException>(async () =>
                {
                    await using var invalid = new NpgsqlCommand("INSERT INTO catalog_import_state(id,catalog_version,catalog_jsonl_sha256,catalog_content_fingerprint,movie_count) VALUES(1,'test',repeat('a',64),repeat('b',64),150)", target);
                    await invalid.ExecuteNonQueryAsync();
                });
                await Assert.ThrowsAsync<PostgresException>(async () =>
                {
                    await using var invalid = new NpgsqlCommand("INSERT INTO embedding_set_state(language,profile_fingerprint,catalog_content_fingerprint,corpus_sha256,text_format_version,artifact_sha256,dimension,embedded_count) VALUES('en',repeat('a',64),repeat('b',64),repeat('c',64),'test',repeat('d',64),768,150)", target);
                    await invalid.ExecuteNonQueryAsync();
                });
            }

            var catalog = Path.Combine(input, "catalog", "movies-catalog.jsonl");
            await AssertBadArtifactDoesNotWriteAsync(targetBuilder.ConnectionString, root, input, sourceCatalog, "en", "profileFingerprint", "0000000000000000000000000000000000000000000000000000000000000000");
            await AssertBadArtifactDoesNotWriteAsync(targetBuilder.ConnectionString, root, input, sourceCatalog, "sr", "corpusSha256", "0000000000000000000000000000000000000000000000000000000000000000");
            Assert.AreEqual(0L, await ScalarAsync(targetBuilder.ConnectionString, "SELECT (SELECT count(*) FROM movies)+(SELECT count(*) FROM movie_embeddings)+(SELECT count(*) FROM catalog_import_state)+(SELECT count(*) FROM embedding_set_state)"));
            var result = await PairedPocImporter.ImportAsync(targetBuilder.ConnectionString, catalog,
                Path.Combine(input, "catalog", "manifest.json"), Path.Combine(input, "translation", "tag-translations-sr.json"),
                sourceCatalog, Path.Combine(input, "embeddings", "en"), Path.Combine(input, "embeddings", "sr"), failAfterVectorRows: 25);
            Assert.Fail("Expected injected mid-import failure, received: " + result);
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("Forced paired import failure", StringComparison.Ordinal))
        {
            Assert.AreEqual(0L, await ScalarAsync(targetBuilder.ConnectionString, "SELECT (SELECT count(*) FROM movies)+(SELECT count(*) FROM movie_embeddings)+(SELECT count(*) FROM catalog_import_state)+(SELECT count(*) FROM embedding_set_state)"));
            var inputRoot = Environment.GetEnvironmentVariable("CINEKROS_SR_POC_INPUT_ROOT")!;
            var catalog = Path.Combine(inputRoot, "catalog", "movies-catalog.jsonl");
            await ExecuteAsync(targetBuilder.ConnectionString, """
                CREATE FUNCTION sr_poc_test_corrupt_after_ready_insert() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    DELETE FROM movie_embeddings WHERE movie_lens_id=(SELECT min(movie_lens_id) FROM movie_embeddings);
                    RETURN NEW;
                END $$;
                CREATE TRIGGER sr_poc_test_corrupt_after_ready_insert AFTER INSERT ON embedding_set_state
                FOR EACH ROW EXECUTE FUNCTION sr_poc_test_corrupt_after_ready_insert();
                """);
            var postInsertFailure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => PairedPocImporter.ImportAsync(targetBuilder.ConnectionString, catalog,
                Path.Combine(inputRoot, "catalog", "manifest.json"), Path.Combine(inputRoot, "translation", "tag-translations-sr.json"),
                Environment.GetEnvironmentVariable("CINEKROS_SR_POC_SOURCE_CATALOG")!, Path.Combine(inputRoot, "embeddings", "en"), Path.Combine(inputRoot, "embeddings", "sr")));
            Assert.IsTrue(postInsertFailure.Message.Contains("post-import verification", StringComparison.OrdinalIgnoreCase));
            Assert.AreEqual(0L, await ScalarAsync(targetBuilder.ConnectionString, "SELECT (SELECT count(*) FROM movies)+(SELECT count(*) FROM movie_embeddings)+(SELECT count(*) FROM catalog_import_state)+(SELECT count(*) FROM embedding_set_state)"));
            await ExecuteAsync(targetBuilder.ConnectionString, "DROP TRIGGER sr_poc_test_corrupt_after_ready_insert ON embedding_set_state; DROP FUNCTION sr_poc_test_corrupt_after_ready_insert()");
            var imported = await PairedPocImporter.ImportAsync(targetBuilder.ConnectionString, catalog,
                Path.Combine(inputRoot, "catalog", "manifest.json"), Path.Combine(inputRoot, "translation", "tag-translations-sr.json"),
                Environment.GetEnvironmentVariable("CINEKROS_SR_POC_SOURCE_CATALOG")!, Path.Combine(inputRoot, "embeddings", "en"), Path.Combine(inputRoot, "embeddings", "sr"));
            Assert.AreEqual("Imported 150 catalog movies and paired EN/SR vector sets.", imported);
            Assert.AreEqual(302L, await ScalarAsync(targetBuilder.ConnectionString, "SELECT (SELECT count(*) FROM movies)+(SELECT count(*) FROM movie_embeddings)+(SELECT count(*) FROM embedding_set_state)"));
            var repeated = await PairedPocImporter.ImportAsync(targetBuilder.ConnectionString, catalog,
                Path.Combine(inputRoot, "catalog", "manifest.json"), Path.Combine(inputRoot, "translation", "tag-translations-sr.json"),
                Environment.GetEnvironmentVariable("CINEKROS_SR_POC_SOURCE_CATALOG")!, Path.Combine(inputRoot, "embeddings", "en"), Path.Combine(inputRoot, "embeddings", "sr"));
            Assert.AreEqual("Verified identical paired POC release; no changes made.", repeated);
        }
        finally
        {
            await using var target = new NpgsqlConnection(targetBuilder.ConnectionString);
            await target.OpenAsync();
            Assert.AreEqual(TestDatabase, await new NpgsqlCommand("SELECT current_database()", target).ExecuteScalarAsync());
            await target.CloseAsync();
            await using var cleanup = new NpgsqlConnection(adminBuilder.ConnectionString); await cleanup.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE {TestDatabase}", cleanup); await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<long> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString); await connection.OpenAsync();
        return Convert.ToInt64(await new NpgsqlCommand(sql, connection).ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> ScalarStringAsync(string connectionString,string sql)
    { await using var connection=new NpgsqlConnection(connectionString); await connection.OpenAsync(); return (string)(await new NpgsqlCommand(sql,connection).ExecuteScalarAsync())!; }

    private static async Task<int> RunMigratorAsync(string root,string migrator,string connectionString,string migrationDirectory,bool draft002)
    {
        var start=new ProcessStartInfo("dotnet") { WorkingDirectory=root,RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false };
        start.ArgumentList.Add(migrator); if(draft002) start.ArgumentList.Add("--poc-draft-002"); start.ArgumentList.Add(migrationDirectory);
        start.Environment["DATABASE_CONNECTION_STRING"]=connectionString;
        using var process=Process.Start(start)??throw new InvalidOperationException("Unable to launch bounded migrator fixture.");
        var output=process.StandardOutput.ReadToEndAsync(); var error=process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); var stdout=await output; var stderr=await error;
        if(process.ExitCode!=0) Assert.Fail("Migrator fixture failed with sanitized output: "+stdout+stderr);
        return process.ExitCode;
    }

    private static async Task ExecuteAsync(string connectionString,string sql)
    { await using var connection=new NpgsqlConnection(connectionString); await connection.OpenAsync(); await using var command=new NpgsqlCommand(sql,connection); await command.ExecuteNonQueryAsync(); }

    private static async Task AssertBadArtifactDoesNotWriteAsync(string connectionString,string root,string input,string sourceCatalog,string language,string field,string value)
    {
        var temp=Path.Combine(Path.GetTempPath(),"sr-poc-artifact-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        try
        {
            var en=Path.Combine(temp,"en"); var sr=Path.Combine(temp,"sr");
            CopyArtifact(Path.Combine(input,"embeddings","en"),en); CopyArtifact(Path.Combine(input,"embeddings","sr"),sr);
            var target=language=="en"?en:sr; var manifestPath=Path.Combine(target,"manifest.json");
            var manifest=JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!; manifest[field]=value; await File.WriteAllTextAsync(manifestPath,manifest.ToJsonString());
            await Assert.ThrowsAsync<InvalidDataException>(()=>PairedPocImporter.ImportAsync(connectionString,
                Path.Combine(input,"catalog","movies-catalog.jsonl"),Path.Combine(input,"catalog","manifest.json"),
                Path.Combine(input,"translation","tag-translations-sr.json"),sourceCatalog,en,sr));
            Assert.AreEqual(0L,await ScalarAsync(connectionString,"SELECT (SELECT count(*) FROM movies)+(SELECT count(*) FROM movie_embeddings)+(SELECT count(*) FROM catalog_import_state)+(SELECT count(*) FROM embedding_set_state)"));
        }
        finally { Directory.Delete(temp,recursive:true); }
    }

    private static void CopyArtifact(string source,string destination)
    { Directory.CreateDirectory(destination); File.Copy(Path.Combine(source,"manifest.json"),Path.Combine(destination,"manifest.json")); File.Copy(Path.Combine(source,"document-vectors.jsonl"),Path.Combine(destination,"document-vectors.jsonl")); }
}
