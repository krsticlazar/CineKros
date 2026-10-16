using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CineKros.Catalog.Importer;
using Npgsql;

namespace CineKros.VectorImporter;

/// <summary>A fresh, immutable descriptor of the exact approved bilingual 001+002 source.</summary>
public sealed record ActorImportBaselineV6(string ServerSystemIdentifier, int PostgresMajor, string PgvectorVersion,
    string BackupSha256, IReadOnlyDictionary<string, string> Digests, string SchemaSnapshot, string DescriptorSha256)
{
    public const string Migration001Sha256 = "14e5ae626c2288596d563482a510dcb8c79320dd5d41955cc5f06579dd59f238";
    public const string Migration002Sha256 = "857b675a33f5ef67670b6f0e2b3c5383e5454494cafe88e707ce12f50eb3b8e3";
    public const string Migration003Sha256 = "66eb1ea4a8130e49db24a9ccee29bad13aab7ed51cd385279f7188d00def1daa";
    public const string OldCatalogVersion = "B05a-bilingual-full-catalog-v1";
    public const string OldCatalogSha256 = "6fd22ec79d2c2b1e36b009f899b126c03a1876ce15a2871bb95033cfff410a4c";
    public const string OldIdentity = "cfe69cc246e439f73c3354f77902896988df878af20bfdb8552cfd1fdf1be17e";
    public const string OldEnArtifact = "3dde59fa8bbf82452e118fd7f65f9c1e3fd8e630b9bad05a0b123e1e8468053e";
    public const string OldSrArtifact = "ff5db33c1c6871a8bd04275a5e6a5167232de9d40e98f3802425c0d2efa0c204";
    public const string OldEnCorpus = "fa07202a8181024b3945b63088e7ab4daa48428b238d88ee269f884d8768cec1";
    public const string OldSrCorpus = "b7998f5f681c61b0f938e6c0fd2d8d619922baaaefc07ed122c7a64860321122";
    public const string OldEnFormat = "en-title-year-director-cast-tags-v1";
    public const string OldSrFormat = "sr-title-year-director-cast-tags-latn-v1";
    internal static readonly string[] DigestNames = ["movies", "vectors", "catalogState", "languageState", "migrations", "oldMovieColumns"];
    // MAIN approved after a fresh independent read-only capture; these describe immutable P8 content.
    internal static readonly IReadOnlyDictionary<string, string> ApprovedContentDigests = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["movies"] = "3dcf005cd50da4092183b8fc07234148", ["vectors"] = "ba9cf541b5785a4577464d6ced5eacb0",
        ["catalogState"] = "484eba4ee13c9c0e20995486a6a627a3", ["languageState"] = "a1bf3cf1a4fbb5e3f924dee45a5302b0",
        ["oldMovieColumns"] = "3dcf005cd50da4092183b8fc07234148"
    };
    internal const string OldMovieColumns = "movie_lens_id,imdb_id,tmdb_id,title,year,runtime_minutes,original_language,genres,average_rating,rating_count,poster_path";
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] Fields = ["schemaVersion", "sourceDatabase", "serverSystemIdentifier", "postgresMajor", "pgvectorVersion", "backupSha256", "migration001Sha256", "migration002Sha256", "movieCount", "vectorCount", "enCount", "srCount", "releasePins", "baselineDigests", "schemaSnapshot", "schemaSha256"];

    internal static object ReleasePins => new { catalogVersion = OldCatalogVersion, catalogSha256 = OldCatalogSha256,
        catalogIdentitySha256 = OldIdentity, orderedIdSha256 = ActorCatalogV6.OrderedIdsSha256,
        enCorpusSha256 = OldEnCorpus, srCorpusSha256 = OldSrCorpus, enArtifactSha256 = OldEnArtifact,
        srArtifactSha256 = OldSrArtifact, enTextFormatVersion = OldEnFormat, srTextFormatVersion = OldSrFormat,
        dictionarySha256 = FullBilingualCatalog.DictionarySha256, profileFingerprint = FullBilingualCatalog.ProfileFingerprint };

    public static bool IsAllowedExecutionTarget(string actual, string expected, bool mainProductionApproval = false) =>
        actual == expected && Regex.IsMatch(actual ?? "", "\\Acinekros_v6_(?:rehearsal|test)_[a-z0-9_]+\\z", RegexOptions.CultureInvariant);

    public static void RequireConnectionTarget(string connectionString, string expected)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (!IsAllowedExecutionTarget(builder.Database ?? "", expected))
            throw new InvalidOperationException("V6 import requires an explicitly matching isolated rehearsal/test database; production is never permitted.");
    }

    public static async Task<ActorImportBaselineV6> LoadAsync(string path, CancellationToken token = default)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("Baseline path must be absolute.");
        return Parse(await File.ReadAllBytesAsync(path, token));
    }

    internal static ActorImportBaselineV6 Parse(byte[] bytes)
    {
        var root = ActorCatalogV6Reader.ReadStrictObject(bytes);
        ExactFields(root, Fields);
        if (S(root, "schemaVersion") != "v6-actor-import-baseline-v1" || S(root, "sourceDatabase") != "cinekros" ||
            S(root, "migration001Sha256") != Migration001Sha256 || S(root, "migration002Sha256") != Migration002Sha256 ||
            new[] { "movieCount", "vectorCount", "enCount", "srCount" }.Any(x => I(root, x) != 9730) ||
            root.GetProperty("releasePins").GetRawText() is not { } pins ||
            JsonSerializer.Serialize(ActorCatalogV6Reader.ReadStrictObject(Encoding.UTF8.GetBytes(pins))) != JsonSerializer.Serialize(ReleasePins))
            throw new InvalidDataException("V6 baseline is not the exact approved P8 bilingual release.");
        var d = root.GetProperty("baselineDigests"); ExactFields(d, DigestNames);
        var digests = DigestNames.ToDictionary(x => x, x => S(d, x), StringComparer.Ordinal);
        foreach (var value in digests.Values) RequireHex(value, 32);
        var schema = S(root, "schemaSnapshot");
        if (Hash(schema) != S(root, "schemaSha256")) throw new InvalidDataException("Baseline schema fingerprint mismatch.");
        ActorSchemaV6.ValidateBaseSnapshot(schema);
        var result = new ActorImportBaselineV6(S(root, "serverSystemIdentifier"), I(root, "postgresMajor"), S(root, "pgvectorVersion"),
            S(root, "backupSha256"), digests, schema, Hash(bytes));
        result.Validate(); return result;
    }

    internal void Validate()
    {
        // The release is tied to this actual approved cluster, rather than database file size.
        if (ServerSystemIdentifier != "7690124133367640101" || PostgresMajor != 17 || PgvectorVersion != "0.8.6")
            throw new InvalidDataException("V6 baseline cluster/version is outside the approved current production identity.");
        RequireHex(BackupSha256, 64); RequireHex(DescriptorSha256, 64);
        if (!Digests.Keys.SequenceEqual(DigestNames)) throw new InvalidDataException("Baseline digests are incomplete.");
        if (ApprovedContentDigests.Any(x => Digests.GetValueOrDefault(x.Key) != x.Value))
            throw new InvalidDataException("Baseline content digests differ from the independently verified immutable P8 release.");
        ActorSchemaV6.ValidateBaseSnapshot(SchemaSnapshot);
    }

    /// <summary>Read-only production inspector. No migration or import can call this path implicitly.</summary>
    public static async Task CaptureAsync(string connectionString, string output, string migrations, string backupSha256, CancellationToken token = default)
    {
        RequireHex(backupSha256, 64);
        if (!Path.IsPathFullyQualified(output) || File.Exists(output)) throw new ArgumentException("Baseline output must be an absent absolute file.");
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        if (b.Database != "cinekros") throw new InvalidOperationException("Baseline capture reads only source database cinekros.");
        await ActorPairedImporterV6.ReadMigrationAsync(migrations, "001_initial_schema.sql", Migration001Sha256, token);
        await ActorPairedImporterV6.ReadMigrationAsync(migrations, "002_serbian_search_vectors.sql", Migration002Sha256, token);
        await using var c = new NpgsqlConnection(connectionString); await c.OpenAsync(token);
        await using var tx = await c.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, token);
        await ActorPairedImporterV6.ExecuteAsync(c, tx, "SET TRANSACTION READ ONLY; SET LOCAL search_path = public, pg_catalog; LOCK TABLE public.movies,public.movie_embeddings,public.catalog_import_state,public.embedding_set_state,public.schema_migrations IN ACCESS SHARE MODE", token);
        var identity = await ReadIdentityAsync(c, tx, token);
        if (identity.Database != "cinekros" || identity.Schema != "public") throw new InvalidOperationException("Unexpected source identity.");
        await ActorPairedImporterV6.VerifyLedgerAsync(c, tx, false, token);
        await ActorPairedImporterV6.VerifyStatesAsync(c, tx, null, null, null, token);
        var schema = await ActorSchemaV6.ReadAsync(c, tx, false, token); ActorSchemaV6.ValidateBaseSnapshot(schema);
        var digests = await ReadDigestsAsync(c, tx, token);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = "v6-actor-import-baseline-v1", sourceDatabase = "cinekros",
            serverSystemIdentifier = identity.System, postgresMajor = identity.Major, pgvectorVersion = identity.Vector,
            backupSha256, migration001Sha256 = Migration001Sha256, migration002Sha256 = Migration002Sha256,
            movieCount = 9730, vectorCount = 9730, enCount = 9730, srCount = 9730, releasePins = ReleasePins,
            baselineDigests = digests, schemaSnapshot = schema, schemaSha256 = Hash(schema) }, JsonOptions);
        _ = Parse(bytes); await tx.CommitAsync(token);
        await using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await file.WriteAsync(bytes, token);
    }

    internal static async Task<Dictionary<string, string>> ReadDigestsAsync(NpgsqlConnection c, NpgsqlTransaction tx, CancellationToken token)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, table, key, perRow) in new[] { ("movies", "movies", "movie_lens_id", true), ("vectors", "movie_embeddings", "movie_lens_id", true),
            ("catalogState", "catalog_import_state", "id", false), ("languageState", "embedding_set_state", "language", false), ("migrations", "schema_migrations", "version", false),
            ("oldMovieColumns", "movies", "movie_lens_id", true) })
        {
            var row = perRow ? "md5(row_to_json(t)::text)" : "row_to_json(t)::text";
            var select = name == "oldMovieColumns" ? OldMovieColumns : "*";
            var sql = $"SELECT md5(string_agg({row}, ',' ORDER BY {key})) FROM (SELECT {select} FROM public.{table}) t";
            result.Add(name, await ActorPairedImporterV6.ScalarAsync(c, tx, sql, token));
        }
        return result;
    }

    internal static async Task<(string Database, string Schema, string System, int Major, string Vector)> ReadIdentityAsync(NpgsqlConnection c, NpgsqlTransaction tx, CancellationToken token)
    {
        await using var cmd = new NpgsqlCommand("SELECT current_database(),current_schema(),(SELECT system_identifier::text FROM pg_control_system()),current_setting('server_version_num')::integer/10000,(SELECT extversion FROM pg_extension WHERE extname='vector')", c, tx);
        await using var r = await cmd.ExecuteReaderAsync(token);
        if (!await r.ReadAsync(token) || r.IsDBNull(4)) throw new InvalidOperationException("Cannot read PostgreSQL/pgvector identity.");
        return (r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetString(4));
    }
    internal static void ExactFields(JsonElement e, string[] fields)
    { if (e.ValueKind != JsonValueKind.Object || !e.EnumerateObject().Select(x => x.Name).SequenceEqual(fields)) throw new InvalidDataException("Baseline object fields are missing, duplicate, reordered or unknown."); }
    internal static string S(JsonElement e, string n) => e.GetProperty(n).ValueKind == JsonValueKind.String ? e.GetProperty(n).GetString()! : throw new InvalidDataException($"Invalid string {n}.");
    private static int I(JsonElement e, string n) => e.GetProperty(n).ValueKind == JsonValueKind.Number && e.GetProperty(n).TryGetInt32(out var v) ? v : throw new InvalidDataException($"Invalid integer {n}.");
    internal static void RequireHex(string value, int length)
    { if (value.Length != length || value.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f'))) throw new InvalidDataException("Noncanonical lowercase digest."); }
    internal static string Hash(string s) => Hash(Encoding.UTF8.GetBytes(s));
    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
