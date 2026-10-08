using System.Globalization;
using System.Text.Json;
using CineKros.Catalog.Importer;
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace CineKros.VectorImporter;

public static class PairedPocImporter
{
    public const string DatabaseName = "cinekros_sr_poc_phase04_20261008";
    private const string Profile = MultilingualPocCatalog.ProfileFingerprint;

    public static async Task<string> ImportAsync(string connectionString, string catalogPath, string manifestPath,
        string dictionaryPath, string sourceCatalogPath, string enDirectory, string srDirectory,
        int? failAfterVectorRows = null, CancellationToken cancellationToken = default)
    {
        var catalog = await MultilingualPocCatalog.LoadAsync(catalogPath, manifestPath, dictionaryPath, sourceCatalogPath, cancellationToken);
        var en = MultilingualPocCatalog.ValidateVectorArtifact(Path.Combine(enDirectory, "document-vectors.jsonl"), Path.Combine(enDirectory, "manifest.json"), catalog, "en");
        var sr = MultilingualPocCatalog.ValidateVectorArtifact(Path.Combine(srDirectory, "document-vectors.jsonl"), Path.Combine(srDirectory, "manifest.json"), catalog, "sr");
        var enRows = ReadVectors(Path.Combine(enDirectory, "document-vectors.jsonl"));
        var srRows = ReadVectors(Path.Combine(srDirectory, "document-vectors.jsonl"));
        if (enRows.Count != 150 || srRows.Count != 150 || enRows.Where((r, i) => r.Id != srRows[i].Id || r.Id != catalog.Movies[i].MovieLensId || r.Fingerprint != en.Rows[i].Fingerprint || srRows[i].Fingerprint != sr.Rows[i].Fingerprint).Any())
            throw new InvalidDataException("Validated paired artifacts do not map to the same exact POC catalog IDs.");

        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseVector();
        await using var source = builder.Build();
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await VerifyDatabaseAsync(connection, cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var advisory = new NpgsqlCommand("SELECT pg_advisory_xact_lock(684321098)", connection, transaction))
                await advisory.ExecuteNonQueryAsync(cancellationToken);
            var counts = await CountsAsync(connection, transaction, cancellationToken);
            var existingStates = await StateCountAsync(connection, transaction, cancellationToken);
            if (counts.movies == 150 && counts.vectors == 150 && existingStates == 2)
            {
                if (await IsIdenticalAsync(connection, transaction, catalog, en, sr, enRows, srRows, cancellationToken))
                {
                    await transaction.CommitAsync(cancellationToken);
                    return "Verified identical paired POC release; no changes made.";
                }
                throw new InvalidOperationException("POC database is populated with a different or inconsistent release.");
            }
            if (counts.movies != 0 || counts.vectors != 0 || existingStates != 0 || counts.catalogState != 0)
                throw new InvalidOperationException("POC database contains unexpected or partial rows; refusing overwrite.");

            await AdaptPocCountGuardAsync(connection, transaction, cancellationToken);
            foreach (var movie in catalog.Movies)
            {
                await using var insert = new NpgsqlCommand("INSERT INTO movies (movie_lens_id, imdb_id, tmdb_id, title, year, runtime_minutes, original_language, genres, average_rating, rating_count, poster_path) VALUES (@id,@imdb,@tmdb,@title,@year,@runtime,@language,@genres,@average::numeric,@count,@poster)", connection, transaction);
                var m = movie.Metadata;
                insert.Parameters.AddWithValue("id", m.Id); insert.Parameters.AddWithValue("imdb", m.ImdbId);
                insert.Parameters.AddWithValue("tmdb", (object?)m.TmdbId ?? DBNull.Value); insert.Parameters.AddWithValue("title", m.Title); insert.Parameters.AddWithValue("year", m.Year);
                insert.Parameters.AddWithValue("runtime", (object?)m.RuntimeMinutes ?? DBNull.Value); insert.Parameters.AddWithValue("language", (object?)m.OriginalLanguage ?? DBNull.Value);
                insert.Parameters.Add("genres", NpgsqlDbType.Array | NpgsqlDbType.Text).Value = m.Genres is null ? DBNull.Value : m.Genres.ToArray();
                insert.Parameters.AddWithValue("average", NpgsqlDbType.Text, (object?)m.AverageRating ?? DBNull.Value); insert.Parameters.AddWithValue("count", (object?)m.RatingCount ?? DBNull.Value);
                insert.Parameters.AddWithValue("poster", (object?)m.PosterPath ?? DBNull.Value);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var state = new NpgsqlCommand("INSERT INTO catalog_import_state (id,catalog_version,catalog_jsonl_sha256,catalog_content_fingerprint,movie_count) VALUES (1,@version,@catalog,@identity,150)", connection, transaction))
            {
                state.Parameters.AddWithValue("version", MultilingualPocCatalog.CatalogVersion); state.Parameters.AddWithValue("catalog", catalog.CatalogSha256); state.Parameters.AddWithValue("identity", catalog.IdentitySha256); await state.ExecuteNonQueryAsync(cancellationToken);
            }

            for (var i = 0; i < enRows.Count; i++)
            {
                var a = enRows[i]; var b = srRows[i];
                await using var insert = new NpgsqlCommand("INSERT INTO movie_embeddings (movie_lens_id,embedding,document_fingerprint,embedding_sr,document_fingerprint_sr) VALUES (@id,@en,@enfp,@sr,@srfp)", connection, transaction);
                insert.Parameters.AddWithValue("id", a.Id); insert.Parameters.AddWithValue("en", new Vector(a.Values)); insert.Parameters.AddWithValue("enfp", a.Fingerprint);
                insert.Parameters.AddWithValue("sr", new Vector(b.Values)); insert.Parameters.AddWithValue("srfp", b.Fingerprint); await insert.ExecuteNonQueryAsync(cancellationToken);
                if (failAfterVectorRows == i + 1) throw new InvalidOperationException("Forced paired import failure for rollback verification.");
            }
            await InsertStateAsync(connection, transaction, catalog.IdentitySha256, "en", catalog.EnCorpusSha256, "en-title-year-director-cast-tags-v1", en.ArtifactSha256, null, cancellationToken);
            await InsertStateAsync(connection, transaction, catalog.IdentitySha256, "sr", catalog.SrCorpusSha256, MultilingualPocCatalog.SrTextFormatVersion, sr.ArtifactSha256, catalog.DictionarySha256, cancellationToken);
            await VerifyImportedRowsAsync(connection, transaction, catalog, en, sr, enRows, srRows, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return "Imported 150 catalog movies and paired EN/SR vector sets.";
        }
        catch { await transaction.RollbackAsync(CancellationToken.None); throw; }
    }

    private static List<StoredVector> ReadVectors(string path)
    {
        var list = new List<StoredVector>(150);
        foreach (var line in File.ReadLines(path))
        {
            using var json = JsonDocument.Parse(line); var row = json.RootElement;
            list.Add(new(row.GetProperty("movieLensId").GetInt64(), row.GetProperty("fingerprint").GetString()!, row.GetProperty("vector").EnumerateArray().Select(x => x.GetSingle()).ToArray()));
        }
        return list;
    }

    private static async Task VerifyDatabaseAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT current_database(), current_setting('server_version_num')::int, EXISTS(SELECT 1 FROM pg_extension WHERE extname='vector'), to_regclass('movie_embeddings') IS NOT NULL, EXISTS(SELECT 1 FROM schema_migrations WHERE version='002_serbian_search_vectors')", connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct) ||
            (reader.GetString(0) != DatabaseName && !System.Text.RegularExpressions.Regex.IsMatch(reader.GetString(0), "^cinekros_sr_poc_phase04_test_[a-z0-9_]+$")) ||
            reader.GetInt32(1) < 170000 || !reader.GetBoolean(2) || !reader.GetBoolean(3) || !reader.GetBoolean(4))
            throw new InvalidOperationException("Actual database identity/schema is not the approved migrated Serbian POC target.");
    }

    private static async Task AdaptPocCountGuardAsync(NpgsqlConnection c, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("ALTER TABLE catalog_import_state DROP CONSTRAINT catalog_import_state_movie_count_check; ALTER TABLE catalog_import_state ADD CONSTRAINT catalog_import_state_movie_count_check CHECK (movie_count = 150); ALTER TABLE embedding_set_state DROP CONSTRAINT embedding_set_state_embedded_count_check; ALTER TABLE embedding_set_state ADD CONSTRAINT embedding_set_state_embedded_count_check CHECK (embedded_count = 150);", c, tx);
        await cmd.ExecuteNonQueryAsync(ct);
    }
    private static async Task<(long movies,long vectors,long catalogState)> CountsAsync(NpgsqlConnection c,NpgsqlTransaction tx,CancellationToken ct)
    {
        await using var cmd=new NpgsqlCommand("SELECT (SELECT count(*) FROM movies),(SELECT count(*) FROM movie_embeddings),(SELECT count(*) FROM catalog_import_state)",c,tx); await using var r=await cmd.ExecuteReaderAsync(ct); await r.ReadAsync(ct); return(r.GetInt64(0),r.GetInt64(1),r.GetInt64(2));
    }
    private static async Task<int> StateCountAsync(NpgsqlConnection c,NpgsqlTransaction tx,CancellationToken ct)
    { await using var cmd=new NpgsqlCommand("SELECT count(*) FROM embedding_set_state",c,tx); return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture); }
    private static async Task VerifyImportedRowsAsync(NpgsqlConnection c,NpgsqlTransaction tx,MultilingualPocCatalogDocument catalog,MultilingualPocArtifactValidation en,MultilingualPocArtifactValidation sr,List<StoredVector> enRows,List<StoredVector> srRows,CancellationToken ct)
    {
        async Task<long> Scalar(string sql)
        { await using var cmd=new NpgsqlCommand(sql,c,tx); return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct),CultureInfo.InvariantCulture); }
        if(await Scalar("SELECT count(*) FROM movies")!=150 || await Scalar("SELECT count(*) FROM movie_embeddings")!=150 || await Scalar("SELECT count(*) FROM catalog_import_state")!=1 || await Scalar("SELECT count(*) FROM embedding_set_state")!=2 ||
           await Scalar("SELECT count(*) FROM movie_embeddings WHERE embedding IS NOT NULL AND embedding_sr IS NOT NULL AND btrim(document_fingerprint)<>'' AND btrim(document_fingerprint_sr)<>''")!=150)
            throw new InvalidOperationException("Post-import verification failed: expected complete paired rows and state counts.");
        await using(var states=new NpgsqlCommand("SELECT language,profile_fingerprint,catalog_content_fingerprint,corpus_sha256,text_format_version,artifact_sha256,translation_dictionary_sha256,dimension,embedded_count FROM embedding_set_state ORDER BY language",c,tx))
        await using(var r=await states.ExecuteReaderAsync(ct))
        {
            var expected=new[]{("en",catalog.EnCorpusSha256,"en-title-year-director-cast-tags-v1",en.ArtifactSha256,(string?)null),("sr",catalog.SrCorpusSha256,MultilingualPocCatalog.SrTextFormatVersion,sr.ArtifactSha256,(string?)catalog.DictionarySha256)};
            foreach(var e in expected) if(!await r.ReadAsync(ct)||r.GetString(0)!=e.Item1||r.GetString(1).Trim()!=Profile||r.GetString(2).Trim()!=catalog.IdentitySha256||r.GetString(3).Trim()!=e.Item2||r.GetString(4)!=e.Item3||r.GetString(5).Trim()!=e.Item4||(r.IsDBNull(6)?null:r.GetString(6).Trim())!=e.Item5||r.GetInt32(7)!=768||r.GetInt32(8)!=150)
                throw new InvalidOperationException("Post-import verification failed: language readiness metadata mismatch.");
            if(await r.ReadAsync(ct)) throw new InvalidOperationException("Post-import verification failed: unexpected language readiness row.");
        }
        await using(var catalogState=new NpgsqlCommand("SELECT catalog_version,catalog_jsonl_sha256,catalog_content_fingerprint,movie_count,embedding_artifact_sha256,embedding_profile_fingerprint,embedded_count FROM catalog_import_state WHERE id=1",c,tx))
        await using(var r=await catalogState.ExecuteReaderAsync(ct))
            if(!await r.ReadAsync(ct)||r.GetString(0)!=MultilingualPocCatalog.CatalogVersion||r.GetString(1).Trim()!=catalog.CatalogSha256||r.GetString(2).Trim()!=catalog.IdentitySha256||r.GetInt32(3)!=150||!r.IsDBNull(4)||!r.IsDBNull(5)||!r.IsDBNull(6))
                throw new InvalidOperationException("Post-import verification failed: catalog mapping metadata mismatch.");
        await using(var vectors=new NpgsqlCommand("SELECT movie_lens_id,document_fingerprint,embedding,document_fingerprint_sr,embedding_sr FROM movie_embeddings ORDER BY movie_lens_id",c,tx))
        await using(var r=await vectors.ExecuteReaderAsync(ct))
            for(var i=0;i<150;i++) if(!await r.ReadAsync(ct)||r.GetInt64(0)!=enRows[i].Id||r.GetString(1).Trim()!=enRows[i].Fingerprint||!r.GetFieldValue<Vector>(2).Memory.Span.SequenceEqual(enRows[i].Values)||r.GetString(3).Trim()!=srRows[i].Fingerprint||!r.GetFieldValue<Vector>(4).Memory.Span.SequenceEqual(srRows[i].Values))
                throw new InvalidOperationException("Post-import verification failed: paired vector or fingerprint mapping mismatch.");
    }
    private static async Task InsertStateAsync(NpgsqlConnection c,NpgsqlTransaction tx,string identity,string language,string corpus,string format,string artifact,string? dictionary,CancellationToken ct)
    {
        await using var cmd=new NpgsqlCommand("INSERT INTO embedding_set_state(language,profile_fingerprint,catalog_content_fingerprint,corpus_sha256,text_format_version,artifact_sha256,translation_dictionary_sha256,dimension,embedded_count) VALUES(@lang,@profile,@catalog,@corpus,@format,@artifact,@dictionary,768,150)",c,tx);
        cmd.Parameters.AddWithValue("lang",language); cmd.Parameters.AddWithValue("profile",Profile); cmd.Parameters.AddWithValue("catalog",identity); cmd.Parameters.AddWithValue("corpus",corpus); cmd.Parameters.AddWithValue("format",format); cmd.Parameters.AddWithValue("artifact",artifact); cmd.Parameters.AddWithValue("dictionary",(object?)dictionary??DBNull.Value); await cmd.ExecuteNonQueryAsync(ct);
    }
    private static async Task<bool> IsIdenticalAsync(NpgsqlConnection c,NpgsqlTransaction tx,MultilingualPocCatalogDocument catalog,MultilingualPocArtifactValidation en,MultilingualPocArtifactValidation sr,List<StoredVector> enRows,List<StoredVector> srRows,CancellationToken ct)
    {
        await using (var catalogState = new NpgsqlCommand("SELECT catalog_version,catalog_jsonl_sha256,catalog_content_fingerprint,movie_count,embedding_artifact_sha256,embedding_profile_fingerprint,embedded_count FROM catalog_import_state WHERE id=1",c,tx))
        await using (var cr = await catalogState.ExecuteReaderAsync(ct))
        {
            if (!await cr.ReadAsync(ct) || cr.GetString(0) != MultilingualPocCatalog.CatalogVersion || cr.GetString(1).Trim() != catalog.CatalogSha256 || cr.GetString(2).Trim() != catalog.IdentitySha256 || cr.GetInt32(3) != 150 || !cr.IsDBNull(4) || !cr.IsDBNull(5) || !cr.IsDBNull(6)) return false;
        }
        await using (var movies = new NpgsqlCommand("SELECT movie_lens_id,imdb_id,tmdb_id,title,year,runtime_minutes,original_language,genres,average_rating::text,rating_count,poster_path FROM movies ORDER BY movie_lens_id",c,tx))
        await using (var mr = await movies.ExecuteReaderAsync(ct))
        {
            foreach (var expectedMovie in catalog.Movies)
            {
                if (!await mr.ReadAsync(ct)) return false;
                var m = expectedMovie.Metadata;
                var rating = mr.IsDBNull(8) ? null : mr.GetString(8);
                if (mr.GetInt64(0) != m.Id || mr.GetString(1) != m.ImdbId || (mr.IsDBNull(2) ? null : mr.GetInt64(2)) != m.TmdbId || mr.GetString(3) != m.Title || mr.GetInt32(4) != m.Year || (mr.IsDBNull(5) ? null : mr.GetInt32(5)) != m.RuntimeMinutes || (mr.IsDBNull(6) ? null : mr.GetString(6)) != m.OriginalLanguage || !GenresEqual(mr.IsDBNull(7) ? null : mr.GetFieldValue<string[]>(7), m.Genres) || !RatingsEqual(rating,m.AverageRating) || (mr.IsDBNull(9) ? null : mr.GetInt64(9)) != m.RatingCount || (mr.IsDBNull(10) ? null : mr.GetString(10)) != m.PosterPath) return false;
            }
            if (await mr.ReadAsync(ct)) return false;
        }
        await using var state=new NpgsqlCommand("SELECT language,profile_fingerprint,catalog_content_fingerprint,corpus_sha256,text_format_version,artifact_sha256,translation_dictionary_sha256,dimension,embedded_count FROM embedding_set_state ORDER BY language",c,tx); await using var r=await state.ExecuteReaderAsync(ct);
        var expected=new[]{("en",catalog.EnCorpusSha256,"en-title-year-director-cast-tags-v1",en.ArtifactSha256,(string?)null),("sr",catalog.SrCorpusSha256,MultilingualPocCatalog.SrTextFormatVersion,sr.ArtifactSha256,(string?)catalog.DictionarySha256)};
        foreach(var e in expected) if(!await r.ReadAsync(ct)||r.GetString(0)!=e.Item1||r.GetString(1).Trim()!=Profile||r.GetString(2).Trim()!=catalog.IdentitySha256||r.GetString(3).Trim()!=e.Item2||r.GetString(4)!=e.Item3||r.GetString(5).Trim()!=e.Item4||(r.IsDBNull(6)?null:r.GetString(6).Trim())!=e.Item5||r.GetInt32(7)!=768||r.GetInt32(8)!=150)return false;
        if(await r.ReadAsync(ct)) return false; await r.DisposeAsync();
        await using var vectors=new NpgsqlCommand("SELECT movie_lens_id,document_fingerprint,embedding,document_fingerprint_sr,embedding_sr FROM movie_embeddings ORDER BY movie_lens_id",c,tx); await using var vr=await vectors.ExecuteReaderAsync(ct);
        for(var i=0;i<150;i++){if(!await vr.ReadAsync(ct)||vr.GetInt64(0)!=enRows[i].Id||vr.GetString(1).Trim()!=enRows[i].Fingerprint||!vr.GetFieldValue<Vector>(2).Memory.Span.SequenceEqual(enRows[i].Values)||vr.GetString(3).Trim()!=srRows[i].Fingerprint||!vr.GetFieldValue<Vector>(4).Memory.Span.SequenceEqual(srRows[i].Values))return false;}
        return !await vr.ReadAsync(ct);
    }
    private static bool GenresEqual(string[]? a,IReadOnlyList<string>? b)=>a is null?b is null:b is not null&&a.SequenceEqual(b,StringComparer.Ordinal);
    private static bool RatingsEqual(string? a,string? b)=>a is null?b is null:b is not null&&decimal.Parse(a,CultureInfo.InvariantCulture)==decimal.Parse(b,CultureInfo.InvariantCulture);
    private sealed record StoredVector(long Id,string Fingerprint,float[] Values);
}
