using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using CineKros.Catalog.Importer;
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace CineKros.VectorImporter;

public sealed record FullPairedImportInputs(FullImportBaseline Baseline, FullBilingualCatalogDocument Catalog,
    FullBilingualVectorArtifact En, FullBilingualVectorArtifact Sr, CatalogDocument LegacyCatalog, VectorArtifact Legacy,
    string Migration001Sql, string Migration002Sql);

/// <summary>Transactional, explicitly gated replacement of the exact legacy 9,730-row set by Phase 8 EN/SR.</summary>
public static class FullPairedImporter
{
    private const long MigrationAdvisoryLockKey = 684321097;
    private const long ImportAdvisoryLockKey = 684321099;
    private const string Migration001Version = "001_initial_schema";
    private const string Migration002Version = "002_serbian_search_vectors";

    public static async Task<string> ImportAsync(string connectionString, string expectedDatabase, string baselinePath,
        string fullCatalogPath, string fullCatalogManifestPath, string dictionaryPath, string sourceCatalogPath,
        string enDirectory, string srDirectory, string legacyCatalogPath, string legacyArtifactDirectory,
        string migrationDirectory, bool mainProductionApproval = false, int? failAfterVectorRows = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) throw new ArgumentException("Connection string is required.", nameof(connectionString));
        if (!FullImportBaseline.IsAllowedExecutionTarget(expectedDatabase, expectedDatabase, mainProductionApproval))
            throw new InvalidOperationException("The explicitly named target is outside the Phase 9 rehearsal/test family; production is MAIN-only.");

        // All file validation completes before opening any database connection.
        var inputs = await ValidatePinnedInputsAsync(baselinePath, fullCatalogPath, fullCatalogManifestPath, dictionaryPath,
            sourceCatalogPath, enDirectory, srDirectory, legacyCatalogPath, legacyArtifactDirectory, migrationDirectory, cancellationToken);
        var baseline = inputs.Baseline; var catalog = inputs.Catalog; var en = inputs.En; var sr = inputs.Sr;
        var legacyCatalog = inputs.LegacyCatalog; var legacy = inputs.Legacy;

        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseVector();
        await using var dataSource = builder.Build();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await VerifyConnectionAsync(connection, expectedDatabase, baseline, mainProductionApproval, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        try
        {
            await using (var migrationLock = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", connection, transaction))
            { migrationLock.Parameters.AddWithValue("key", MigrationAdvisoryLockKey); await migrationLock.ExecuteNonQueryAsync(cancellationToken); }
            await using (var importLock = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@key)", connection, transaction))
            { importLock.Parameters.AddWithValue("key", ImportAdvisoryLockKey); await importLock.ExecuteNonQueryAsync(cancellationToken); }

            var migrationState = await ReadMigrationStateAsync(connection, transaction, cancellationToken);
            var has002 = migrationState.TryGetValue(Migration002Version, out var migration002Hash);
            if (has002)
            {
                if (migrationState.Count != 2 || migrationState.GetValueOrDefault(Migration001Version) != baseline.Migration001Sha256 || migration002Hash != baseline.Migration002Sha256)
                    throw new InvalidOperationException("Migration 002 ledger checksum differs from the canonical SQL.");
                await using (var tableLock = new NpgsqlCommand("LOCK TABLE movies, movie_embeddings, catalog_import_state, schema_migrations, embedding_set_state IN ACCESS EXCLUSIVE MODE", connection, transaction))
                    await tableLock.ExecuteNonQueryAsync(cancellationToken);
                if (await IsIdenticalFinalStateAsync(connection, transaction, catalog, en, sr, cancellationToken))
                {
                    await transaction.CommitAsync(cancellationToken);
                    return "Verified identical complete bilingual release; no changes made.";
                }
                throw new InvalidOperationException("Database has migration 002 but not the exact complete bilingual release; partial or unknown state is not overwritten.");
            }

            if (migrationState.Count != 1 || migrationState.GetValueOrDefault(Migration001Version) != baseline.Migration001Sha256)
                throw new InvalidOperationException("Migration ledger is not the exact pinned 001-only legacy baseline.");
            await using (var tableLock = new NpgsqlCommand("LOCK TABLE movies, movie_embeddings, catalog_import_state, schema_migrations IN ACCESS EXCLUSIVE MODE", connection, transaction))
                await tableLock.ExecuteNonQueryAsync(cancellationToken);
            await VerifyPre002SchemaAsync(connection, transaction, cancellationToken);
            await VerifyLegacyBaselineAsync(connection, transaction, baseline, legacyCatalog, legacy, catalog, cancellationToken);

            // Canonical additive migration and its ledger row are part of this same transaction.
            await using (var migration = new NpgsqlCommand(inputs.Migration002Sql, connection, transaction))
                await migration.ExecuteNonQueryAsync(cancellationToken);
            await using (var ledger = new NpgsqlCommand("INSERT INTO schema_migrations(version,sha256,applied_at) VALUES(@version,@sha,now())", connection, transaction))
            {
                ledger.Parameters.AddWithValue("version", Migration002Version);
                ledger.Parameters.AddWithValue("sha", baseline.Migration002Sha256);
                await ledger.ExecuteNonQueryAsync(cancellationToken);
            }

            await ReplaceVectorsAsync(connection, transaction, en, sr, failAfterVectorRows, cancellationToken);
            await UpdateCatalogStateAsync(connection, transaction, catalog, en, cancellationToken);
            await InsertLanguageStateAsync(connection, transaction, catalog, en, "en", cancellationToken);
            await InsertLanguageStateAsync(connection, transaction, catalog, sr, "sr", cancellationToken);
            await VerifyFinalStateAsync(connection, transaction, catalog, en, sr, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return "Imported the complete 9,730-row bilingual vector set transactionally.";
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public static async Task<FullPairedImportInputs> ValidatePinnedInputsAsync(string baselinePath, string fullCatalogPath,
        string fullCatalogManifestPath, string dictionaryPath, string sourceCatalogPath, string enDirectory, string srDirectory,
        string legacyCatalogPath, string legacyArtifactDirectory, string migrationDirectory, CancellationToken cancellationToken = default)
    {
        if (new[] { baselinePath, fullCatalogPath, fullCatalogManifestPath, dictionaryPath, sourceCatalogPath, enDirectory,
                srDirectory, legacyCatalogPath, legacyArtifactDirectory, migrationDirectory }.Any(path => !Path.IsPathFullyQualified(path)))
            throw new ArgumentException("All full import input paths must be absolute.");
        var baseline = await FullImportBaseline.LoadAsync(baselinePath, cancellationToken);
        var catalog = await FullBilingualCatalog.LoadAsync(fullCatalogPath, fullCatalogManifestPath, dictionaryPath, sourceCatalogPath, cancellationToken);
        var en = await FullBilingualVectorArtifactValidator.LoadAsync(enDirectory, catalog, "en", cancellationToken);
        var sr = await FullBilingualVectorArtifactValidator.LoadAsync(srDirectory, catalog, "sr", cancellationToken);
        var legacyCatalog = await CatalogValidator.LoadAsync(legacyCatalogPath, cancellationToken);
        var legacy = await VectorArtifactValidator.LoadAsync(legacyArtifactDirectory, legacyCatalogPath, cancellationToken);
        ValidateInputPair(baseline, catalog, en, sr, legacyCatalog, legacy);
        var migration001 = await ReadMigrationAsync(migrationDirectory, "001_initial_schema.sql", baseline.Migration001Sha256, cancellationToken);
        var migration002 = await ReadMigrationAsync(migrationDirectory, "002_serbian_search_vectors.sql", baseline.Migration002Sha256, cancellationToken);
        return new(baseline, catalog, en, sr, legacyCatalog, legacy, migration001, migration002);
    }

    private static void ValidateInputPair(FullImportBaseline baseline, FullBilingualCatalogDocument catalog,
        FullBilingualVectorArtifact en, FullBilingualVectorArtifact sr, CatalogDocument legacyCatalog, VectorArtifact legacy)
    {
        baseline.ValidatePinnedFacts();
        if (catalog.Movies.Count != baseline.MovieCount || catalog.CatalogSha256 != "6fd22ec79d2c2b1e36b009f899b126c03a1876ce15a2871bb95033cfff410a4c" ||
            catalog.DictionarySha256 != FullBilingualCatalog.DictionarySha256 || en.Language != "en" || sr.Language != "sr" ||
            en.Records.Count != baseline.MovieCount || sr.Records.Count != baseline.MovieCount ||
            en.ArtifactSha256 != "3dde59fa8bbf82452e118fd7f65f9c1e3fd8e630b9bad05a0b123e1e8468053e" ||
            sr.ArtifactSha256 != "ff5db33c1c6871a8bd04275a5e6a5167232de9d40e98f3802425c0d2efa0c204" ||
            legacyCatalog.JsonlHash != baseline.LegacyCatalogSha256 || legacyCatalog.Fingerprint != baseline.LegacyCatalogContentFingerprint ||
            legacy.ArtifactSha256 != baseline.LegacyVectorArtifactSha256 || legacy.Records.Count != baseline.VectorCount)
            throw new InvalidDataException("Full and legacy artifact pair does not match the approved P8 release and MAIN baseline.");
        if (!catalog.Movies.Select(x => x.MovieLensId).SequenceEqual(legacy.Records.Select(x => x.MovieLensId)) ||
            !legacyCatalog.Movies.Select(x => x.Id).SequenceEqual(legacy.Records.Select(x => x.MovieLensId)))
            throw new InvalidDataException("Legacy and bilingual catalogs do not contain the same exact ordered movie IDs.");
        for (var i = 0; i < catalog.Movies.Count; i++)
        {
            var a = catalog.Movies[i].Metadata; var b = legacyCatalog.Movies[i];
            if (!MetadataEqual(a, b)) throw new InvalidDataException($"Legacy and bilingual catalog metadata differ at movieLensId {a.Id}.");
        }
    }

    private static async Task VerifyConnectionAsync(NpgsqlConnection connection, string expectedDatabase,
        FullImportBaseline baseline, bool mainProductionApproval, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT current_database(),current_schema(),current_setting('server_version_num')::int / 10000,current_setting('transaction_read_only')::boolean,(SELECT extversion FROM pg_extension WHERE extname='vector'),(SELECT system_identifier::text FROM pg_control_system())", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Could not read actual PostgreSQL database identity.");
        baseline.RequireExpectedDatabase(reader.GetString(0), expectedDatabase, mainProductionApproval);
        if (reader.GetString(1) != "public" || reader.GetInt32(2) != baseline.PostgresMajor || reader.GetBoolean(3) ||
            reader.IsDBNull(4) || reader.GetString(4) != baseline.PgvectorVersion || reader.IsDBNull(5) || reader.GetString(5) != baseline.ServerSystemIdentifier)
            throw new InvalidOperationException("Connected PostgreSQL system, pgvector version, or writable-session identity differs from MAIN's baseline.");
    }

    private static async Task<Dictionary<string, string>> ReadMigrationStateAsync(NpgsqlConnection c, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT version,btrim(sha256) FROM schema_migrations ORDER BY version", c, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct); var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (await r.ReadAsync(ct)) if (!result.TryAdd(r.GetString(0), r.GetString(1))) throw new InvalidOperationException("Duplicate migration ledger version.");
        return result;
    }

    private static async Task VerifyLegacyBaselineAsync(NpgsqlConnection c, NpgsqlTransaction tx, FullImportBaseline baseline,
        CatalogDocument legacyCatalog, VectorArtifact legacy, FullBilingualCatalogDocument fullCatalog, CancellationToken ct)
    {
        var digests = await ReadBaselineDigestsAsync(c, tx, ct);
        foreach (var pair in baseline.BaselineDigests)
            if (!digests.TryGetValue(pair.Key, out var actual) || actual != pair.Value)
                throw new InvalidOperationException($"Pre-import {pair.Key} digest differs from MAIN's pinned legacy baseline.");
        var counts = await ReadCountsAsync(c, tx, ct, hasLanguageState: false);
        if (counts.Movies != baseline.MovieCount || counts.Vectors != baseline.VectorCount || counts.CatalogState != 1 || counts.LanguageState != 0)
            throw new InvalidOperationException("Database counts are not the exact expected legacy 9,730-row state.");
        await VerifyLegacyCatalogStateAsync(c, tx, baseline, legacy, ct);
        await VerifyMovieRowsAsync(c, tx, legacyCatalog.Movies, ct);
        await VerifyLegacyVectorsAsync(c, tx, legacy, ct);
        // Keep the full catalog parameter in the contract: metadata comparison was already performed offline.
        if (fullCatalog.Movies.Count != baseline.MovieCount) throw new InvalidDataException("Full catalog count differs from baseline.");
    }

    private static async Task VerifyPre002SchemaAsync(NpgsqlConnection c, NpgsqlTransaction tx, CancellationToken ct)
    {
        const string sql = """
            SELECT to_regclass(format('%I.embedding_set_state', current_schema())) IS NULL,
                   ARRAY(SELECT a.attname::text FROM pg_attribute a
                         WHERE a.attrelid=to_regclass(format('%I.movie_embeddings', current_schema()))
                           AND a.attnum>0 AND NOT a.attisdropped ORDER BY a.attnum),
                   NOT EXISTS (SELECT 1 FROM pg_attribute a
                               WHERE a.attrelid=to_regclass(format('%I.movie_embeddings', current_schema()))
                                 AND a.attname='embedding_sr' AND a.attnum>0 AND NOT a.attisdropped),
                   NOT EXISTS (SELECT 1 FROM pg_attribute a
                               WHERE a.attrelid=to_regclass(format('%I.movie_embeddings', current_schema()))
                                 AND a.attname='document_fingerprint_sr' AND a.attnum>0 AND NOT a.attisdropped)
            """;
        await using var cmd = new NpgsqlCommand(sql, c, tx);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) || !reader.GetBoolean(0) ||
            !reader.GetFieldValue<string[]>(1).SequenceEqual(["movie_lens_id", "embedding", "document_fingerprint"], StringComparer.Ordinal) ||
            !reader.GetBoolean(2) || !reader.GetBoolean(3))
            throw new InvalidOperationException("Pre-002 schema contains unexpected SR columns/table or otherwise differs from the exact legacy schema; migration was not attempted.");
    }

    private static async Task<Dictionary<string, string>> ReadBaselineDigestsAsync(NpgsqlConnection c, NpgsqlTransaction tx, CancellationToken ct)
    {
        const string movie = "SELECT md5(string_agg(md5(row_to_json(m)::text), ',' ORDER BY movie_lens_id)) FROM movies m";
        const string vector = "SELECT md5(string_agg(md5(row_to_json(e)::text), ',' ORDER BY movie_lens_id)) FROM movie_embeddings e";
        const string catalog = "SELECT md5(string_agg(row_to_json(c)::text, ',' ORDER BY id)) FROM catalog_import_state c";
        const string migrations = "SELECT md5(string_agg(row_to_json(s)::text, ',' ORDER BY version)) FROM schema_migrations s";
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, sql) in new[] { ("movies", movie), ("vectors", vector), ("catalogState", catalog), ("migrations", migrations) })
        {
            await using var command = new NpgsqlCommand(sql, c, tx);
            values.Add(name, (string?)await command.ExecuteScalarAsync(ct) ?? string.Empty);
        }
        return values;
    }

    private static async Task<(long Movies, long Vectors, long CatalogState, long LanguageState)> ReadCountsAsync(NpgsqlConnection c, NpgsqlTransaction tx, CancellationToken ct, bool hasLanguageState = true)
    {
        var sql = hasLanguageState
            ? "SELECT (SELECT count(*) FROM movies),(SELECT count(*) FROM movie_embeddings),(SELECT count(*) FROM catalog_import_state),(SELECT count(*) FROM embedding_set_state)"
            : "SELECT (SELECT count(*) FROM movies),(SELECT count(*) FROM movie_embeddings),(SELECT count(*) FROM catalog_import_state),0::bigint";
        await using var cmd = new NpgsqlCommand(sql, c, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct); await r.ReadAsync(ct);
        return (r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3));
    }

    private static async Task VerifyLegacyCatalogStateAsync(NpgsqlConnection c, NpgsqlTransaction tx, FullImportBaseline baseline, VectorArtifact legacy, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT catalog_version,btrim(catalog_jsonl_sha256),btrim(catalog_content_fingerprint),movie_count,btrim(embedding_artifact_sha256),btrim(embedding_profile_fingerprint),embedded_count FROM catalog_import_state WHERE id=1", c, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct) || r.GetString(0) != baseline.LegacyCatalogVersion || r.GetString(1) != baseline.LegacyCatalogSha256 ||
            r.GetString(2) != baseline.LegacyCatalogContentFingerprint || r.GetInt32(3) != baseline.MovieCount ||
            r.IsDBNull(4) || r.GetString(4) != legacy.ArtifactSha256 || r.IsDBNull(5) || r.GetString(5) != baseline.LegacyProfileFingerprint ||
            r.IsDBNull(6) || r.GetInt32(6) != baseline.VectorCount || await r.ReadAsync(ct))
            throw new InvalidOperationException("Catalog import state is not the exact reviewed legacy artifact/profile state.");
    }

    private static async Task VerifyMovieRowsAsync(NpgsqlConnection c, NpgsqlTransaction tx, IReadOnlyList<MovieRow> expected, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT movie_lens_id,imdb_id,tmdb_id,title,year,runtime_minutes,original_language,genres,average_rating::text,rating_count,poster_path FROM movies ORDER BY movie_lens_id", c, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        foreach (var movie in expected)
        {
            if (!await r.ReadAsync(ct) || !ReadMovieMatches(r, movie)) throw new InvalidOperationException($"Movie metadata differs from the exact reviewed catalog at ID {movie.Id}.");
        }
        if (await r.ReadAsync(ct)) throw new InvalidOperationException("Database contains extra movie rows.");
    }

    private static async Task VerifyLegacyVectorsAsync(NpgsqlConnection c, NpgsqlTransaction tx, VectorArtifact expected, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT movie_lens_id,btrim(document_fingerprint),embedding FROM movie_embeddings ORDER BY movie_lens_id", c, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        foreach (var row in expected.Records)
        {
            if (!await r.ReadAsync(ct) || r.GetInt64(0) != row.MovieLensId || r.GetString(1) != row.Fingerprint ||
                !r.GetFieldValue<Vector>(2).Memory.Span.SequenceEqual(row.Values))
                throw new InvalidOperationException($"Legacy vector/fingerprint differs from the exact old artifact at ID {row.MovieLensId}.");
        }
        if (await r.ReadAsync(ct)) throw new InvalidOperationException("Database contains extra legacy vector rows.");
    }

    private static async Task ReplaceVectorsAsync(NpgsqlConnection c, NpgsqlTransaction tx, FullBilingualVectorArtifact en,
        FullBilingualVectorArtifact sr, int? failAfter, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("UPDATE movie_embeddings SET embedding=@en,document_fingerprint=@enfp,embedding_sr=@sr,document_fingerprint_sr=@srfp WHERE movie_lens_id=@id", c, tx);
        var id = command.Parameters.Add("id", NpgsqlDbType.Bigint); var enVector = command.Parameters.AddWithValue("en", new Vector(new float[768]));
        var enFp = command.Parameters.Add("enfp", NpgsqlDbType.Text); var srVector = command.Parameters.AddWithValue("sr", new Vector(new float[768])); var srFp = command.Parameters.Add("srfp", NpgsqlDbType.Text);
        for (var index = 0; index < en.Records.Count; index++)
        {
            var a = en.Records[index]; var b = sr.Records[index];
            id.Value = a.MovieLensId; enVector.Value = new Vector(a.Values); enFp.Value = a.Fingerprint;
            srVector.Value = new Vector(b.Values); srFp.Value = b.Fingerprint;
            if (await command.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException($"Expected exactly one vector row for movieLensId {a.MovieLensId}.");
            if (failAfter == index + 1) throw new InvalidOperationException("Test-only injected full paired import failure; transaction will roll back.");
        }
    }

    private static async Task UpdateCatalogStateAsync(NpgsqlConnection c, NpgsqlTransaction tx, FullBilingualCatalogDocument catalog,
        FullBilingualVectorArtifact en, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("UPDATE catalog_import_state SET catalog_version=@version,catalog_jsonl_sha256=@catalog,catalog_content_fingerprint=@identity,movie_count=9730,embedding_artifact_sha256=@artifact,embedding_profile_fingerprint=@profile,embedded_count=9730 WHERE id=1", c, tx);
        cmd.Parameters.AddWithValue("version", catalog.CatalogVersion); cmd.Parameters.AddWithValue("catalog", catalog.CatalogSha256);
        cmd.Parameters.AddWithValue("identity", catalog.IdentitySha256); cmd.Parameters.AddWithValue("artifact", en.ArtifactSha256);
        cmd.Parameters.AddWithValue("profile", catalog.ProfileFingerprint);
        if (await cmd.ExecuteNonQueryAsync(ct) != 1) throw new InvalidOperationException("Catalog state row disappeared during full import.");
    }

    private static async Task InsertLanguageStateAsync(NpgsqlConnection c, NpgsqlTransaction tx, FullBilingualCatalogDocument catalog,
        FullBilingualVectorArtifact artifact, string language, CancellationToken ct)
    {
        var isSr = language == "sr";
        await using var cmd = new NpgsqlCommand("INSERT INTO embedding_set_state(language,profile_fingerprint,catalog_content_fingerprint,corpus_sha256,text_format_version,artifact_sha256,translation_dictionary_sha256,dimension,embedded_count) VALUES(@language,@profile,@identity,@corpus,@format,@artifact,@dictionary,768,9730)", c, tx);
        cmd.Parameters.AddWithValue("language", language); cmd.Parameters.AddWithValue("profile", catalog.ProfileFingerprint);
        cmd.Parameters.AddWithValue("identity", catalog.IdentitySha256); cmd.Parameters.AddWithValue("corpus", isSr ? catalog.SrCorpusSha256 : catalog.EnCorpusSha256);
        cmd.Parameters.AddWithValue("format", isSr ? catalog.SrTextFormatVersion : catalog.EnTextFormatVersion);
        cmd.Parameters.AddWithValue("artifact", artifact.ArtifactSha256); cmd.Parameters.AddWithValue("dictionary", (object?)(isSr ? catalog.DictionarySha256 : null) ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task VerifyFinalStateAsync(NpgsqlConnection c, NpgsqlTransaction tx, FullBilingualCatalogDocument catalog,
        FullBilingualVectorArtifact en, FullBilingualVectorArtifact sr, CancellationToken ct)
    {
        var counts = await ReadCountsAsync(c, tx, ct);
        if (counts.Movies != 9730 || counts.Vectors != 9730 || counts.CatalogState != 1 || counts.LanguageState != 2)
            throw new InvalidOperationException("Post-import counts/readiness state are incomplete.");
        await VerifyFinalVectorRowsAsync(c, tx, en, sr, ct);
        await VerifyMovieRowsAsync(c, tx, catalog.Movies.Select(x => x.Metadata).ToArray(), ct);
        await VerifyFinalMetadataAsync(c, tx, catalog, en, sr, ct);
        var migrations = await ReadMigrationStateAsync(c, tx, ct);
        if (migrations.Count != 2 || migrations.GetValueOrDefault(Migration001Version) != "14e5ae626c2288596d563482a510dcb8c79320dd5d41955cc5f06579dd59f238" ||
            migrations.GetValueOrDefault(Migration002Version) != "857b675a33f5ef67670b6f0e2b3c5383e5454494cafe88e707ce12f50eb3b8e3")
            throw new InvalidOperationException("Post-import migration ledger is incomplete or inconsistent.");
    }

    private static async Task<bool> IsIdenticalFinalStateAsync(NpgsqlConnection c, NpgsqlTransaction tx, FullBilingualCatalogDocument catalog,
        FullBilingualVectorArtifact en, FullBilingualVectorArtifact sr, CancellationToken ct)
    {
        var counts = await ReadCountsAsync(c, tx, ct);
        if (counts.Movies != 9730 || counts.Vectors != 9730 || counts.CatalogState != 1 || counts.LanguageState != 2) return false;
        try
        {
            await VerifyMovieRowsAsync(c, tx, catalog.Movies.Select(x => x.Metadata).ToArray(), ct);
            await VerifyFinalMetadataAsync(c, tx, catalog, en, sr, ct);
            await VerifyFinalVectorRowsAsync(c, tx, en, sr, ct);
            return true;
        }
        catch (InvalidOperationException) { return false; }
    }

    private static async Task VerifyFinalVectorRowsAsync(NpgsqlConnection c, NpgsqlTransaction tx, FullBilingualVectorArtifact en,
        FullBilingualVectorArtifact sr, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT movie_lens_id,btrim(document_fingerprint),embedding,btrim(document_fingerprint_sr),embedding_sr FROM movie_embeddings ORDER BY movie_lens_id", c, tx);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        for (var i = 0; i < en.Records.Count; i++)
        {
            var a = en.Records[i]; var b = sr.Records[i];
            if (!await r.ReadAsync(ct) || r.GetInt64(0) != a.MovieLensId || r.IsDBNull(1) || r.GetString(1) != a.Fingerprint ||
                r.IsDBNull(2) || !r.GetFieldValue<Vector>(2).Memory.Span.SequenceEqual(a.Values) || r.IsDBNull(3) || r.GetString(3) != b.Fingerprint ||
                r.IsDBNull(4) || !r.GetFieldValue<Vector>(4).Memory.Span.SequenceEqual(b.Values))
                throw new InvalidOperationException($"Post-import paired vector/fingerprint mismatch at ID {a.MovieLensId}.");
        }
        if (await r.ReadAsync(ct)) throw new InvalidOperationException("Post-import has extra vector rows.");
    }

    private static async Task VerifyFinalMetadataAsync(NpgsqlConnection c, NpgsqlTransaction tx, FullBilingualCatalogDocument catalog,
        FullBilingualVectorArtifact en, FullBilingualVectorArtifact sr, CancellationToken ct)
    {
        await using (var cmd = new NpgsqlCommand("SELECT catalog_version,btrim(catalog_jsonl_sha256),btrim(catalog_content_fingerprint),movie_count,btrim(embedding_artifact_sha256),btrim(embedding_profile_fingerprint),embedded_count FROM catalog_import_state WHERE id=1", c, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct) || r.GetString(0) != catalog.CatalogVersion || r.GetString(1) != catalog.CatalogSha256 ||
                r.GetString(2) != catalog.IdentitySha256 || r.GetInt32(3) != 9730 || r.GetString(4) != en.ArtifactSha256 ||
                r.GetString(5) != catalog.ProfileFingerprint || r.GetInt32(6) != 9730 || await r.ReadAsync(ct))
                throw new InvalidOperationException("Post-import catalog state differs from the complete EN artifact/catalog identity.");
        }
        await using (var cmd = new NpgsqlCommand("SELECT language,btrim(profile_fingerprint),btrim(catalog_content_fingerprint),btrim(corpus_sha256),text_format_version,btrim(artifact_sha256),CASE WHEN translation_dictionary_sha256 IS NULL THEN NULL ELSE btrim(translation_dictionary_sha256) END,dimension,embedded_count FROM embedding_set_state ORDER BY language", c, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            var expected = new[]
            {
                ("en", catalog.EnCorpusSha256, catalog.EnTextFormatVersion, en.ArtifactSha256, (string?)null),
                ("sr", catalog.SrCorpusSha256, catalog.SrTextFormatVersion, sr.ArtifactSha256, (string?)catalog.DictionarySha256)
            };
            foreach (var item in expected)
                if (!await r.ReadAsync(ct) || r.GetString(0) != item.Item1 || r.GetString(1) != catalog.ProfileFingerprint ||
                    r.GetString(2) != catalog.IdentitySha256 || r.GetString(3) != item.Item2 || r.GetString(4) != item.Item3 ||
                    r.GetString(5) != item.Item4 || (r.IsDBNull(6) ? null : r.GetString(6)) != item.Item5 || r.GetInt32(7) != 768 || r.GetInt32(8) != 9730)
                    throw new InvalidOperationException("Post-import language readiness row differs from the exact bilingual release.");
            if (await r.ReadAsync(ct)) throw new InvalidOperationException("Post-import has an unknown language readiness row.");
        }
    }

    private static async Task<string> ReadMigrationAsync(string directory, string name, string expectedSha, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Migration directory must be absolute.", nameof(directory));
        var bytes = await File.ReadAllBytesAsync(Path.Combine(directory, name), ct);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != expectedSha) throw new InvalidDataException($"Canonical migration {name} bytes differ from MAIN's pinned checksum.");
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private static bool MetadataEqual(MovieRow a, MovieRow b) => a.Id == b.Id && a.ImdbId == b.ImdbId && a.TmdbId == b.TmdbId &&
        a.Title == b.Title && a.Year == b.Year && a.RuntimeMinutes == b.RuntimeMinutes && a.OriginalLanguage == b.OriginalLanguage &&
        GenresEqual(a.Genres, b.Genres) && NumericEqual(a.AverageRating, b.AverageRating) && a.RatingCount == b.RatingCount && a.PosterPath == b.PosterPath;

    private static bool ReadMovieMatches(NpgsqlDataReader r, MovieRow m) => r.GetInt64(0) == m.Id && r.GetString(1) == m.ImdbId &&
        (r.IsDBNull(2) ? null : r.GetInt64(2)) == m.TmdbId && r.GetString(3) == m.Title && r.GetInt32(4) == m.Year &&
        (r.IsDBNull(5) ? null : r.GetInt32(5)) == m.RuntimeMinutes && (r.IsDBNull(6) ? null : r.GetString(6)) == m.OriginalLanguage &&
        GenresEqual(r.IsDBNull(7) ? null : r.GetFieldValue<string[]>(7), m.Genres) && NumericEqual(r.IsDBNull(8) ? null : r.GetString(8), m.AverageRating) &&
        (r.IsDBNull(9) ? null : r.GetInt64(9)) == m.RatingCount && (r.IsDBNull(10) ? null : r.GetString(10)) == m.PosterPath;

    private static bool GenresEqual(IReadOnlyList<string>? a, IReadOnlyList<string>? b) => a is null ? b is null : b is not null && a.SequenceEqual(b, StringComparer.Ordinal);
    private static bool NumericEqual(string? a, string? b) => a is null ? b is null : b is not null && decimal.Parse(a, CultureInfo.InvariantCulture) == decimal.Parse(b, CultureInfo.InvariantCulture);
}
