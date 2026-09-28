using CineKros.Catalog.Importer;
using Npgsql;
using Pgvector;

namespace CineKros.VectorImporter;

public static class VectorImporter
{
    public static async Task<string> ImportAsync(string connectionString, VectorArtifact artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.Records.Count != CatalogValidator.ExpectedCount || artifact.ArtifactSha256.Length != 64)
            throw new InvalidDataException("Validated vector artifact identity or count is invalid.");
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseVector();
        await using var dataSource = builder.Build();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var advisoryLock = new NpgsqlCommand("SELECT pg_advisory_xact_lock(684321098)", connection, transaction))
                await advisoryLock.ExecuteNonQueryAsync(cancellationToken);

            var state = await ReadState(connection, transaction, cancellationToken);
            var counts = await ReadCounts(connection, transaction, cancellationToken);
            if (state.version != CatalogValidator.CatalogVersion || state.catalogHash != CatalogValidator.ExpectedHash ||
                state.catalogFingerprint != CatalogValidator.ExpectedFingerprint || state.movieCount != CatalogValidator.ExpectedCount ||
                counts.movies != CatalogValidator.ExpectedCount || counts.state != 1 || !await MovieIdsMatch(connection, transaction, artifact, cancellationToken))
                throw new InvalidOperationException("Database does not contain the exact reviewed C02 catalog state and rows.");

            if (counts.embeddings != 0 || state.artifactHash is not null || state.profileFingerprint is not null || state.embeddedCount is not null)
            {
                if (counts.embeddings == CatalogValidator.ExpectedCount && state.artifactHash == artifact.ArtifactSha256 &&
                    state.profileFingerprint == VectorArtifactValidator.ProfileFingerprint && state.embeddedCount == CatalogValidator.ExpectedCount &&
                    await EmbeddingRowsMatch(connection, transaction, artifact, cancellationToken))
                {
                    await transaction.CommitAsync(cancellationToken);
                    return "Verified identical vector artifact; no changes made.";
                }
                throw new InvalidOperationException("Database vector state is partial, inconsistent or belongs to a different artifact/profile.");
            }

            const string insertSql = "INSERT INTO movie_embeddings (movie_lens_id, embedding, document_fingerprint) VALUES (@id, @embedding, @fingerprint)";
            foreach (var record in artifact.Records)
            {
                await using var insert = new NpgsqlCommand(insertSql, connection, transaction);
                insert.Parameters.AddWithValue("id", record.MovieLensId);
                insert.Parameters.AddWithValue("embedding", new Vector(record.Values));
                insert.Parameters.AddWithValue("fingerprint", record.Fingerprint);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var update = new NpgsqlCommand("UPDATE catalog_import_state SET embedding_artifact_sha256=@artifact, embedding_profile_fingerprint=@profile, embedded_count=@count WHERE id=1", connection, transaction))
            {
                update.Parameters.AddWithValue("artifact", artifact.ArtifactSha256);
                update.Parameters.AddWithValue("profile", VectorArtifactValidator.ProfileFingerprint);
                update.Parameters.AddWithValue("count", CatalogValidator.ExpectedCount);
                if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException("Catalog import state row disappeared during vector import.");
            }
            await transaction.CommitAsync(cancellationToken);
            return $"Imported {CatalogValidator.ExpectedCount} document vectors.";
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<(string version, string catalogHash, string catalogFingerprint, int movieCount, string? artifactHash, string? profileFingerprint, int? embeddedCount)> ReadState(NpgsqlConnection connection, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT catalog_version, btrim(catalog_jsonl_sha256), btrim(catalog_content_fingerprint), movie_count, btrim(embedding_artifact_sha256), btrim(embedding_profile_fingerprint), embedded_count FROM catalog_import_state WHERE id=1", connection, tx);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new InvalidOperationException("Database catalog import state is missing.");
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetInt32(6));
    }

    private static async Task<(long movies, long state, long embeddings)> ReadCounts(NpgsqlConnection connection, NpgsqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT (SELECT count(*) FROM movies), (SELECT count(*) FROM catalog_import_state), (SELECT count(*) FROM movie_embeddings)", connection, tx);
        await using var reader = await cmd.ExecuteReaderAsync(ct); await reader.ReadAsync(ct);
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static async Task<bool> MovieIdsMatch(NpgsqlConnection connection, NpgsqlTransaction tx, VectorArtifact artifact, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT movie_lens_id FROM movies ORDER BY movie_lens_id", connection, tx);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        foreach (var expected in artifact.Records) if (!await reader.ReadAsync(ct) || reader.GetInt64(0) != expected.MovieLensId) return false;
        return !await reader.ReadAsync(ct);
    }

    private static async Task<bool> EmbeddingRowsMatch(NpgsqlConnection connection, NpgsqlTransaction tx, VectorArtifact artifact, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT movie_lens_id, document_fingerprint, embedding FROM movie_embeddings ORDER BY movie_lens_id", connection, tx);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        foreach (var expected in artifact.Records)
        {
            if (!await reader.ReadAsync(ct) || reader.GetInt64(0) != expected.MovieLensId || reader.GetString(1).Trim() != expected.Fingerprint) return false;
            var actual = reader.GetFieldValue<Vector>(2).Memory.Span;
            if (actual.Length != expected.Values.Length || !actual.SequenceEqual(expected.Values)) return false;
        }
        return !await reader.ReadAsync(ct);
    }
}
