using Npgsql;
using NpgsqlTypes;

namespace CineKros.Catalog.Importer;

public static class CatalogImporter
{
    public static async Task<string> ImportAsync(string connectionString, CatalogDocument catalog, CancellationToken cancellationToken = default)
    {
        catalog = CatalogValidator.ValidateForImport(catalog);
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(684321098)", connection, transaction))
                await lockCommand.ExecuteNonQueryAsync(cancellationToken);
            var state = await ReadState(connection, transaction, cancellationToken);
            var tableCounts = await ReadCounts(connection, transaction, cancellationToken);
            if (state is not null)
            {
                if (state.Value.hash != catalog.JsonlHash || state.Value.fingerprint != catalog.Fingerprint || state.Value.version != CatalogValidator.CatalogVersion)
                    throw new InvalidOperationException("Database contains a different catalog import.");
                var emptyEmbeddings = state.Value.embeddingHash is null && state.Value.profileFingerprint is null && state.Value.embeddedCount is null && tableCounts.embeddings == 0;
                var completeEmbeddings = state.Value.embeddingHash is not null && state.Value.profileFingerprint is not null && state.Value.embeddedCount == CatalogValidator.ExpectedCount && tableCounts.embeddings == CatalogValidator.ExpectedCount;
                if (tableCounts.movies != CatalogValidator.ExpectedCount || tableCounts.state != 1)
                    throw new InvalidOperationException("Database catalog row/state counts are inconsistent.");
                if (!emptyEmbeddings && !completeEmbeddings)
                    throw new InvalidOperationException($"Database embedding state is inconsistent (rows={tableCounts.embeddings}, hashPresent={state.Value.embeddingHash is not null}, profilePresent={state.Value.profileFingerprint is not null}, embeddedCount={state.Value.embeddedCount?.ToString() ?? "null"}).");
                if (!await RowsMatchAsync(connection, transaction, catalog, cancellationToken))
                    throw new InvalidOperationException("Database catalog rows are incomplete or inconsistent.");
                await transaction.CommitAsync(cancellationToken);
                return "Verified identical catalog; no changes made.";
            }
            if (tableCounts.movies != 0 || tableCounts.state != 0 || tableCounts.embeddings != 0)
                throw new InvalidOperationException("Database is not empty and has no matching import state.");

            const string insertSql = "INSERT INTO movies (movie_lens_id, imdb_id, tmdb_id, title, year, runtime_minutes, original_language, genres, average_rating, rating_count, poster_path) VALUES (@id, @imdb, @tmdb, @title, @year, @runtime, @language, @genres, @average::numeric, @count, @poster)";
            foreach (var movie in catalog.Movies)
            {
                await using var insert = new NpgsqlCommand(insertSql, connection, transaction);
                insert.Parameters.AddWithValue("id", movie.Id);
                insert.Parameters.AddWithValue("imdb", movie.ImdbId);
                insert.Parameters.AddWithValue("tmdb", (object?)movie.TmdbId ?? DBNull.Value);
                insert.Parameters.AddWithValue("title", movie.Title);
                insert.Parameters.AddWithValue("year", movie.Year);
                insert.Parameters.AddWithValue("runtime", (object?)movie.RuntimeMinutes ?? DBNull.Value);
                insert.Parameters.AddWithValue("language", (object?)movie.OriginalLanguage ?? DBNull.Value);
                var genres = insert.Parameters.Add("genres", NpgsqlDbType.Array | NpgsqlDbType.Text);
                genres.Value = movie.Genres is null ? DBNull.Value : movie.Genres.ToArray();
                insert.Parameters.AddWithValue("average", NpgsqlDbType.Text, (object?)movie.AverageRating ?? DBNull.Value);
                insert.Parameters.AddWithValue("count", (object?)movie.RatingCount ?? DBNull.Value);
                insert.Parameters.AddWithValue("poster", (object?)movie.PosterPath ?? DBNull.Value);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var stateInsert = new NpgsqlCommand("INSERT INTO catalog_import_state (id, catalog_version, catalog_jsonl_sha256, catalog_content_fingerprint, movie_count) VALUES (1, @version, @hash, @fingerprint, @count)", connection, transaction))
            {
                stateInsert.Parameters.AddWithValue("version", CatalogValidator.CatalogVersion);
                stateInsert.Parameters.AddWithValue("hash", catalog.JsonlHash);
                stateInsert.Parameters.AddWithValue("fingerprint", catalog.Fingerprint);
                stateInsert.Parameters.AddWithValue("count", CatalogValidator.ExpectedCount);
                await stateInsert.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return $"Imported {CatalogValidator.ExpectedCount} catalog movies.";
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<(string version, string hash, string fingerprint, string? embeddingHash, string? profileFingerprint, int? embeddedCount)?> ReadState(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT catalog_version, catalog_jsonl_sha256, catalog_content_fingerprint, embedding_artifact_sha256, embedding_profile_fingerprint, embedded_count FROM catalog_import_state WHERE id=1", connection, transaction);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return (reader.GetString(0), reader.GetString(1).Trim(), reader.GetString(2).Trim(), NullableReference<string>(reader, 3)?.Trim(), NullableReference<string>(reader, 4)?.Trim(), NullableValue<int>(reader, 5));
    }

    private static async Task<(long movies, long state, long embeddings)> ReadCounts(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT (SELECT count(*) FROM movies), (SELECT count(*) FROM catalog_import_state), (SELECT count(*) FROM movie_embeddings)", connection, transaction);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static async Task<bool> RowsMatchAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CatalogDocument catalog, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT movie_lens_id, imdb_id, tmdb_id, title, year, runtime_minutes, original_language, genres, average_rating::text, rating_count, poster_path FROM movies ORDER BY movie_lens_id", connection, transaction);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        foreach (var expected in catalog.Movies)
        {
            if (!await reader.ReadAsync(ct)) return false;
            var storedAverage = NullableReference<string>(reader, 8);
            var averageMatches = storedAverage is null ? expected.AverageRating is null : expected.AverageRating is not null &&
                decimal.Parse(storedAverage, System.Globalization.CultureInfo.InvariantCulture) == decimal.Parse(expected.AverageRating, System.Globalization.CultureInfo.InvariantCulture);
            var mismatch = reader.GetInt64(0) != expected.Id ? "id" :
                reader.GetString(1) != expected.ImdbId ? "imdbId" :
                NullableValue<long>(reader, 2) != expected.TmdbId ? "tmdbId" :
                reader.GetString(3) != expected.Title ? "title" :
                reader.GetInt32(4) != expected.Year ? "year" :
                NullableValue<int>(reader, 5) != expected.RuntimeMinutes ? "runtimeMinutes" :
                NullableReference<string>(reader, 6) != expected.OriginalLanguage ? "originalLanguage" :
                !ArrayEqual(NullableReference<string[]>(reader, 7), expected.Genres) ? "genres" :
                !averageMatches ? "averageRating" :
                NullableValue<long>(reader, 9) != expected.RatingCount ? "ratingCount" :
                NullableReference<string>(reader, 10) != expected.PosterPath ? "posterPath" : null;
            if (mismatch is not null) throw new InvalidOperationException($"Stored catalog row has an inconsistent {mismatch} value.");
        }
        return !await reader.ReadAsync(ct);
    }

    private static bool ArrayEqual(IReadOnlyList<string>? a, IReadOnlyList<string>? b) => a is null ? b is null : b is not null && a.SequenceEqual(b, StringComparer.Ordinal);
    private static T? NullableValue<T>(NpgsqlDataReader reader, int ordinal) where T : struct => reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<T>(ordinal);
    private static T? NullableReference<T>(NpgsqlDataReader reader, int ordinal) where T : class => reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<T>(ordinal);
}
