using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CineKros.Catalog.Importer;
using CineKros.VectorImporter;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Npgsql;

namespace CineKros.VectorImporter.Tests;

[TestClass]
[DoNotParallelize]
public sealed class VectorImporterIntegrationTests
{
    private const string Image = "pgvector/pgvector:0.8.6-pg17-bookworm";
    private const string Password = "c05_disposable_test_password_20260927";
    private static readonly string CatalogPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/data/derived/final/b05a-real-tmdb-01/movies-catalog.jsonl"));
    private static readonly string MigrationPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../database/migrations/001_initial_schema.sql"));
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static readonly string ContainerName = $"cinekros-c05-vector-it-{Guid.NewGuid():N}";
    private static readonly string FixtureRoot = Path.Combine(Path.GetTempPath(), $"cinekros-c05-test-only-{Guid.NewGuid():N}");
    private static int _port;
    private static bool _ownsContainer;
    private static CatalogDocument _catalog = null!;

    [ClassInitialize]
    public static async Task Start(TestContext _)
    {
        _catalog = await CatalogValidator.LoadAsync(CatalogPath);
        Directory.CreateDirectory(FixtureRoot);
        if ((await Docker("container", "inspect", ContainerName)).ExitCode == 0) throw new InvalidOperationException("C05 exact disposable container name already exists; refusing to touch it.");
        var started = await Docker(["run", "--detach", "--rm", "--name", ContainerName, "--tmpfs", "/var/lib/postgresql/data", "--publish", "127.0.0.1::5432", "--env", "POSTGRES_USER=c05_worker", "--env", $"POSTGRES_PASSWORD={Password}", "--env", "POSTGRES_DB=c05_bootstrap", Image], 300000);
        if (started.ExitCode != 0) throw new InvalidOperationException("Could not start the named disposable PostgreSQL test container.");
        _ownsContainer = true;
        var port = await Docker("port", ContainerName, "5432/tcp");
        var match = Regex.Match(port.StandardOutput, @":(?<port>[0-9]+)\s*$", RegexOptions.Multiline);
        if (!match.Success) throw new InvalidOperationException("Could not resolve the disposable PostgreSQL loopback port.");
        _port = int.Parse(match.Groups["port"].Value, CultureInfo.InvariantCulture);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < deadline)
        {
            try { await using var c = new NpgsqlConnection(Connection("c05_bootstrap")); await c.OpenAsync(); return; }
            catch (NpgsqlException) { await Task.Delay(500); }
        }
        throw new InvalidOperationException("C05 disposable PostgreSQL did not become ready.");
    }

    [ClassCleanup]
    public static async Task Cleanup()
    {
        if (_ownsContainer && (await Docker("container", "inspect", ContainerName)).ExitCode == 0)
            await Docker("rm", "--force", ContainerName);
        if (_ownsContainer && Directory.Exists(FixtureRoot) && Path.GetFullPath(FixtureRoot).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
            Directory.Delete(FixtureRoot, true);
    }

    [TestMethod]
    public async Task ValidatesAndImportsCompleteArtifactAtomicallyAndSafely()
    {
        var artifactDir = Path.Combine(FixtureRoot, "TEST-ONLY-complete-synthetic-vector-artifact");
        Directory.CreateDirectory(artifactDir);
        WriteArtifact(artifactDir, _catalog);
        var valid = await VectorArtifactValidator.LoadAsync(artifactDir, CatalogPath);
        Assert.AreEqual(9730, valid.Records.Count);

        await AssertRejectedMutation(artifactDir, lines => { lines[0] = lines[0].Replace("\"fingerprint\":\"", "\"fingerprint\":\"0", StringComparison.Ordinal); });
        await AssertRejectedMutation(artifactDir, lines => { lines[0] = lines[0].Replace("\"semanticTextSha256\":\"", "\"semanticTextSha256\":\"0", StringComparison.Ordinal); });
        await AssertRejectedMutation(artifactDir, lines => { lines[0] = lines[0].Replace("\"movieLensId\":1", "\"movieLensId\":2", StringComparison.Ordinal); });
        await AssertRejectedMutation(artifactDir, lines => (lines[0], lines[1]) = (lines[1], lines[0]));
        await AssertRejectedMutation(artifactDir, lines => { using var row = JsonDocument.Parse(lines[0]); var r = row.RootElement; lines[0] = WriteVectorRow(r.GetProperty("movieLensId").GetInt64(), r.GetProperty("fingerprint").GetString()!, r.GetProperty("semanticTextSha256").GetString()!, new float[768]); });
        await AssertRejectedMutation(artifactDir, lines => { using var row = JsonDocument.Parse(lines[0]); var r = row.RootElement; var values = UnitVector(); values[0] = 0.5f; values[1] = 0.5f; lines[0] = WriteVectorRow(r.GetProperty("movieLensId").GetInt64(), r.GetProperty("fingerprint").GetString()!, r.GetProperty("semanticTextSha256").GetString()!, values); });
        await AssertRejectedMutation(artifactDir, lines => { using var row = JsonDocument.Parse(lines[0]); lines[0] = WriteVectorRow(row.RootElement.GetProperty("movieLensId").GetInt64(), row.RootElement.GetProperty("fingerprint").GetString()!, row.RootElement.GetProperty("semanticTextSha256").GetString()!, [1f, 2f]); });
        await AssertRejectedMutation(artifactDir, lines => { lines[0] = lines[0][..^1] + ",\"fakeMarker\":\"FAKE ONLY\"}"; });
        await AssertRejectedMutation(artifactDir, lines => { lines[0] = lines[0].Replace("\"vector\":[1,0", "\"vector\":[1e100,0", StringComparison.Ordinal); });
        await AssertRejectedMutation(artifactDir, lines => lines.RemoveAt(lines.Count - 1));
        await AssertRejectedManifest(artifactDir, "profile", "\"b3-embed2-text-search-v1\"");
        await AssertRejectedManifest(artifactDir, "profileFingerprint", "\"0cd007792baf327c612d1bd92a518044f82995c741a733abc8fdbef90fae7e5f\"");
        await AssertRejectedManifest(artifactDir, "dimension", "767");
        await AssertRejectedManifest(artifactDir, "catalogSha256", $"\"{new string('0', 64)}\"");
        await AssertRejectedManifest(artifactDir, "catalogContentFingerprint", "\"wrong-catalog\"");
        await AssertRejectedManifest(artifactDir, "modelRevision", "\"wrong-revision\"");
        await AssertRejectedManifest(artifactDir, "tokenizerSha256", "\"wrong-tokenizer\"");
        await AssertRejectedManifest(artifactDir, "outputSha256", $"\"{new string('0', 64)}\"");
        await AssertRejectedManifest(artifactDir, "recordCount", "9729");
        await AssertRejectedManifest(artifactDir, "fakeMarker", "\"FAKE ONLY - NOT PRODUCTION VECTORS\"");
        await AssertRejectedTamperedCatalog(artifactDir);

        var firstDb = await NewCatalogDatabase("c05_full");
        Assert.AreEqual("Imported 9730 document vectors.", await VectorImporter.ImportAsync(firstDb, valid));
        Assert.AreEqual(9730L, await ScalarLong(firstDb, "SELECT count(*) FROM movie_embeddings"));
        Assert.AreEqual(valid.ArtifactSha256, await ScalarString(firstDb, "SELECT btrim(embedding_artifact_sha256) FROM catalog_import_state WHERE id=1"));
        Assert.AreEqual(VectorArtifactValidator.ProfileFingerprint, await ScalarString(firstDb, "SELECT btrim(embedding_profile_fingerprint) FROM catalog_import_state WHERE id=1"));
        Assert.AreEqual(9730L, await ScalarLong(firstDb, "SELECT embedded_count FROM catalog_import_state WHERE id=1"));
        Assert.AreEqual("Verified identical vector artifact; no changes made.", await VectorImporter.ImportAsync(firstDb, valid));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () => await VectorImporter.ImportAsync(firstDb, await VectorArtifactValidator.LoadAsync(ChangedArtifact(artifactDir), CatalogPath)));

        var rollbackDb = await NewCatalogDatabase("c05_rollback");
        var lastId = valid.Records[^1].MovieLensId;
        await Execute(rollbackDb, $"CREATE FUNCTION c05_reject_final_vector() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN IF NEW.movie_lens_id={lastId} THEN RAISE EXCEPTION 'injected C05 failure'; END IF; RETURN NEW; END $$; CREATE TRIGGER c05_reject_final_vector BEFORE INSERT ON movie_embeddings FOR EACH ROW EXECUTE FUNCTION c05_reject_final_vector()");
        await Assert.ThrowsAsync<NpgsqlException>(() => VectorImporter.ImportAsync(rollbackDb, valid));
        Assert.AreEqual(0L, await ScalarLong(rollbackDb, "SELECT count(*) FROM movie_embeddings"));
        Assert.IsTrue(await ScalarIsNull(rollbackDb, "SELECT embedding_artifact_sha256 FROM catalog_import_state WHERE id=1"));
        Assert.IsTrue(await ScalarIsNull(rollbackDb, "SELECT embedding_profile_fingerprint FROM catalog_import_state WHERE id=1"));
        Assert.IsTrue(await ScalarIsNull(rollbackDb, "SELECT embedded_count FROM catalog_import_state WHERE id=1"));
    }

    private static void WriteArtifact(string directory, CatalogDocument catalog)
    {
        using var sourceReader = new StreamReader(CatalogPath, new UTF8Encoding(false, true));
        using var output = new FileStream(Path.Combine(directory, "document-vectors.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        string? line;
        while ((line = sourceReader.ReadLine()) is not null)
        {
            using var row = JsonDocument.Parse(line); var item = row.RootElement;
            var id = item.GetProperty("movieLensId").GetInt64();
            var semanticText = item.GetProperty("semanticText").GetString()!;
            var fingerprint = TestFingerprint(semanticText);
            var textHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(semanticText)));
            output.Write(Encoding.UTF8.GetBytes(WriteVectorRow(id, fingerprint, textHash, UnitVector())));
            output.WriteByte((byte)'\n');
        }
        output.Flush(true);
        output.Dispose();
        var hash = HashFile(Path.Combine(directory, "document-vectors.jsonl"));
        using var fs = File.Create(Path.Combine(directory, "manifest.json"));
        using var writer = new Utf8JsonWriter(fs);
        writer.WriteStartObject(); writer.WriteString("format", "e5-document-vectors-jsonl-v1");
        writer.WriteString("catalogVersion", CatalogValidator.CatalogVersion); writer.WriteString("catalogSha256", CatalogValidator.ExpectedHash);
        writer.WriteString("catalogContentFingerprint", CatalogValidator.ExpectedFingerprint); writer.WriteString("profile", VectorArtifactValidator.Profile);
        writer.WriteString("profileFingerprint", VectorArtifactValidator.ProfileFingerprint); writer.WriteString("modelId", VectorArtifactValidator.ModelId);
        writer.WriteString("modelRevision", VectorArtifactValidator.ModelRevision); writer.WriteString("onnxSha256", VectorArtifactValidator.OnnxSha256);
        writer.WriteString("tokenizerSha256", VectorArtifactValidator.TokenizerSha256); writer.WriteNumber("dimension", 768);
        writer.WriteString("documentInputFormat", "e5-passage-semantictext-v1"); writer.WriteString("fingerprintAlgorithm", "sha256-compact-json-e5-v1");
        writer.WriteString("pooling", "mask-mean-v1"); writer.WriteString("normalization", "l2-f32-v1"); writer.WriteNumber("maxTokens", 512);
        writer.WriteString("outputSha256", hash); writer.WriteNumber("recordCount", 9730); writer.WriteString("order", "movieLensId ascending");
        writer.WriteNumber("failedRecords", 0); writer.WriteNumber("truncatedRecords", 0); writer.WriteNumber("batchSize", 1);
        writer.WriteNumber("elapsedMilliseconds", 0); writer.WriteString("provenance", "TEST ONLY synthetic fixture; no provider calls");
        writer.WriteEndObject(); writer.Flush();
    }

    private static string TestFingerprint(string semanticText)
    {
        var payload = JsonSerializer.Serialize(new TestDocumentFingerprint($"passage: {semanticText}", VectorArtifactValidator.ModelId,
            VectorArtifactValidator.ModelRevision, VectorArtifactValidator.OnnxSha256, VectorArtifactValidator.TokenizerSha256,
            768, "passage", "e5-passage-semantictext-v1", 512, "mask-mean-v1", "l2-f32-v1"),
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
    private sealed record TestDocumentFingerprint(string FormattedText, string ModelId, string Revision, string OnnxSha256, string TokenizerSha256, int Dimension, string Task, string FormattingVersion, int MaxTokens, string Pooling, string Normalization);
    private static float[] UnitVector() { var values = new float[768]; values[0] = 1; return values; }
    private static string WriteVectorRow(long id, string fingerprint, string textHash, IEnumerable<float> values)
    {
        using var ms = new MemoryStream(); using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject(); w.WriteNumber("movieLensId", id); w.WriteString("fingerprint", fingerprint); w.WriteString("semanticTextSha256", textHash);
            w.WriteStartArray("vector"); foreach (var value in values) w.WriteNumberValue(value); w.WriteEndArray(); w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static async Task AssertRejectedMutation(string directory, Action<List<string>> mutate)
    {
        var path = Path.Combine(directory, "document-vectors.jsonl"); var original = await File.ReadAllLinesAsync(path);
        try { var changed = original.ToList(); mutate(changed); await File.WriteAllLinesAsync(path, changed, new UTF8Encoding(false)); await RefreshHash(directory); await Assert.ThrowsExactlyAsync<InvalidDataException>(() => VectorArtifactValidator.LoadAsync(directory, CatalogPath)); }
        finally { await File.WriteAllLinesAsync(path, original, new UTF8Encoding(false)); await RefreshHash(directory); }
    }
    private static async Task AssertRejectedManifest(string directory, string key, string replacement)
    {
        var path = Path.Combine(directory, "manifest.json"); var original = await File.ReadAllTextAsync(path);
        try
        {
            var root = JsonDocument.Parse(original).RootElement;
            var old = root.TryGetProperty(key, out var value) ? value.GetRawText() : null;
            var compact = original.TrimEnd();
            var mutated = old is null ? compact[..^1] + $",\"{key}\":{replacement}}}" : original.Replace($"\"{key}\":{old}", $"\"{key}\":{replacement}", StringComparison.Ordinal);
            await File.WriteAllTextAsync(path, mutated);
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => VectorArtifactValidator.LoadAsync(directory, CatalogPath));
        }
        finally { await File.WriteAllTextAsync(path, original); }
    }
    private static async Task AssertRejectedTamperedCatalog(string directory)
    {
        var tempDir = Path.Combine(FixtureRoot, "tampered-catalog"); Directory.CreateDirectory(tempDir);
        var temp = Path.Combine(tempDir, "movies-catalog.jsonl");
        await File.WriteAllTextAsync(temp, "{}\n");
        File.Copy(Path.Combine(Path.GetDirectoryName(CatalogPath)!, "manifest.json"), Path.Combine(tempDir, "manifest.json"));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => VectorArtifactValidator.LoadAsync(directory, temp));
    }
    private static async Task RefreshHash(string directory)
    {
        var path = Path.Combine(directory, "manifest.json"); var text = await File.ReadAllTextAsync(path);
        var hash = HashFile(Path.Combine(directory, "document-vectors.jsonl"));
        var match = Regex.Match(text, "\\\"outputSha256\\\":\\\"(?<hash>[0-9a-f]{64})\\\"");
        if (!match.Success) throw new InvalidDataException("Test fixture manifest output hash was not found.");
        text = text.Replace(match.Groups["hash"].Value, hash, StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, text);
    }
    private static string ChangedArtifact(string original)
    {
        var changed = Path.Combine(FixtureRoot, "TEST-ONLY-different-artifact"); Directory.CreateDirectory(changed);
        File.Copy(Path.Combine(original, "document-vectors.jsonl"), Path.Combine(changed, "document-vectors.jsonl"));
        File.Copy(Path.Combine(original, "manifest.json"), Path.Combine(changed, "manifest.json"));
        var vectorPath = Path.Combine(changed, "document-vectors.jsonl"); var lines = File.ReadAllLines(vectorPath);
        using var row = JsonDocument.Parse(lines[0]); var r = row.RootElement;
        var values = r.GetProperty("vector").EnumerateArray().Select(x => x.GetSingle()).ToArray(); values[0] = 0.70710677f; values[1] = 0.70710677f;
        lines[0] = WriteVectorRow(r.GetProperty("movieLensId").GetInt64(), r.GetProperty("fingerprint").GetString()!, r.GetProperty("semanticTextSha256").GetString()!, values);
        File.WriteAllLines(vectorPath, lines, new UTF8Encoding(false)); RefreshHash(changed).GetAwaiter().GetResult();
        return changed;
    }

    private static async Task<string> NewCatalogDatabase(string suffix)
    {
        var database = $"{suffix}_{Guid.NewGuid():N}";
        await Execute(Connection("c05_bootstrap"), $"CREATE DATABASE \"{database}\"");
        var cs = Connection(database);
        await Execute(cs, await File.ReadAllTextAsync(MigrationPath));
        Assert.AreEqual($"Imported 9730 catalog movies.", await CatalogImporter.ImportAsync(cs, _catalog));
        return cs;
    }
    private static string Connection(string database) => $"Host=127.0.0.1;Port={_port};Username=c05_worker;Password={Password};Database={database};Pooling=false;Timeout=10";
    private static string CatalogArtifactSha(string directory) => Regex.Match(File.ReadAllText(Path.Combine(directory, "manifest.json")), "\\\"outputSha256\\\":\\\"(?<v>[0-9a-f]{64})").Groups["v"].Value;
    private static string HashFile(string path) { using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    private static async Task Execute(string connection, string sql) { await using var c = new NpgsqlConnection(connection); await c.OpenAsync(); await using var cmd = new NpgsqlCommand(sql, c); await cmd.ExecuteNonQueryAsync(); }
    private static async Task<long> ScalarLong(string connection, string sql) { await using var c = new NpgsqlConnection(connection); await c.OpenAsync(); await using var cmd = new NpgsqlCommand(sql, c); return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture); }
    private static async Task<string> ScalarString(string connection, string sql) { await using var c = new NpgsqlConnection(connection); await c.OpenAsync(); await using var cmd = new NpgsqlCommand(sql, c); return (string)(await cmd.ExecuteScalarAsync())!; }
    private static async Task<bool> ScalarIsNull(string connection, string sql) { await using var c = new NpgsqlConnection(connection); await c.OpenAsync(); await using var cmd = new NpgsqlCommand(sql, c); var value = await cmd.ExecuteScalarAsync(); return value is null or DBNull; }
    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> Docker(params string[] args) => await Docker(args, 30000);
    private static async Task<(int ExitCode, string StandardOutput, string StandardError)> Docker(string[] args, int timeoutMs)
    {
        var dockerPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "resources", "bin", "docker.exe");
        var info = new ProcessStartInfo(dockerPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start Docker CLI.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMilliseconds(timeoutMs));
        return (process.ExitCode, await stdout, await stderr);
    }
}
