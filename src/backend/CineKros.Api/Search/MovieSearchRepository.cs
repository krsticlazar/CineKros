using CineKros.Api.Database;
using CineKros.Api.RealProviders;
using Npgsql;
using Pgvector;

namespace CineKros.Api.Search;

/// <summary>Executes exact eligible-only vector ranking or deterministic filter-only ranking.</summary>
public sealed class MovieSearchRepository
{
    public const int ResultLimit = 10;
    public const int CatalogMovieCount = 9730;
    public const string CatalogVersion = "B05a-combined-catalog-v1";
    public const string CatalogJsonlSha256 = "8b2bad0a22fef45842176a1d9f3730be1568e367b9fc230fa0aa398bb5c26946";
    public const string CatalogContentFingerprint = "2ffad7ba703cb80543db617e742a61c88871332910185767ee96fe08a77a0be7";

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _expectedEmbeddingProfileFingerprint;

    public MovieSearchRepository(NpgsqlDataSource dataSource, string expectedEmbeddingProfileFingerprint)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        if (string.IsNullOrWhiteSpace(expectedEmbeddingProfileFingerprint))
            throw new ArgumentException("An expected embedding profile fingerprint is required.", nameof(expectedEmbeddingProfileFingerprint));
        _expectedEmbeddingProfileFingerprint = expectedEmbeddingProfileFingerprint;
    }

    /// <summary>Build data sources for this repository with Pgvector's typed vector mapping enabled.</summary>
    public static NpgsqlDataSource CreateDataSource(string connectionString)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.UseVector();
        return builder.Build();
    }

    public async Task<IReadOnlyList<FilteredMovie>> SearchHybridAsync(
        RealHardFilters hardFilters,
        float[] queryVector,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queryVector);
        ValidateQueryVector(queryVector);
        var filtered = HardFilterSqlBuilder.Build(hardFilters);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await EnsureCatalogReadyAsync(connection, semantic: true, cancellationToken);

        // Keep every hard predicate in the materialized candidate relation before exact vector ordering.
        const string prefix = "WITH eligible AS MATERIALIZED (SELECT movie_lens_id, title, year, imdb_id, poster_path, average_rating, rating_count FROM movies ";
        const string suffix = ") SELECT e.movie_lens_id, e.title, e.year, e.imdb_id, e.poster_path, e.average_rating, e.rating_count FROM eligible AS e JOIN movie_embeddings AS me USING (movie_lens_id) ORDER BY me.embedding <=> @queryVector ASC, e.movie_lens_id ASC LIMIT 10";
        await using var command = new NpgsqlCommand(prefix + filtered.WhereSql + suffix, connection);
        filtered.AddParameters(command);
        command.Parameters.AddWithValue("queryVector", new Vector(queryVector));
        return await ReadMoviesAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<FilteredMovie>> SearchHardOnlyAsync(
        RealHardFilters hardFilters,
        CancellationToken cancellationToken = default)
    {
        var filtered = HardFilterSqlBuilder.Build(hardFilters);
        if (string.IsNullOrEmpty(filtered.WhereSql))
            throw new RealProviderException("PARSER_INVALID_RESPONSE");

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await EnsureCatalogReadyAsync(connection, semantic: false, cancellationToken);

        const string select = "SELECT movie_lens_id, title, year, imdb_id, poster_path, average_rating, rating_count FROM movies ";
        const string order = " ORDER BY rating_count DESC NULLS LAST, average_rating DESC NULLS LAST, movie_lens_id ASC LIMIT 10";
        await using var command = new NpgsqlCommand(select + filtered.WhereSql + order, connection);
        filtered.AddParameters(command);
        return await ReadMoviesAsync(command, cancellationToken);
    }

    private async Task EnsureCatalogReadyAsync(NpgsqlConnection connection, bool semantic, CancellationToken cancellationToken)
    {
        const string sql = "SELECT catalog_version, catalog_jsonl_sha256, catalog_content_fingerprint, movie_count, embedding_artifact_sha256, embedding_profile_fingerprint, embedded_count, (SELECT count(*) FROM movies), (SELECT count(*) FROM movie_embeddings) FROM catalog_import_state WHERE id = 1";
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw SearchUnavailable();

        var catalogMatches = reader.GetString(0) == CatalogVersion &&
            reader.GetString(1) == CatalogJsonlSha256 &&
            reader.GetString(2) == CatalogContentFingerprint &&
            reader.GetInt32(3) == CatalogMovieCount &&
            reader.GetInt64(7) == CatalogMovieCount;
        if (!catalogMatches)
            throw SearchUnavailable();

        if (!semantic) return;

        var artifactSha = reader.IsDBNull(4) ? null : reader.GetString(4);
        var profileFingerprint = reader.IsDBNull(5) ? null : reader.GetString(5);
        var embeddedCount = reader.IsDBNull(6) ? (int?)null : reader.GetInt32(6);
        if (artifactSha is null || profileFingerprint != _expectedEmbeddingProfileFingerprint ||
            embeddedCount != CatalogMovieCount || reader.GetInt64(8) != CatalogMovieCount)
            throw SearchUnavailable();
    }

    private static async Task<IReadOnlyList<FilteredMovie>> ReadMoviesAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var movies = new List<FilteredMovie>(ResultLimit);
        while (await reader.ReadAsync(cancellationToken))
            movies.Add(new FilteredMovie(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetDecimal(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6)));
        return movies;
    }

    private static void ValidateQueryVector(float[] vector)
    {
        if (vector.Length != 768) throw new RealProviderException("PROVIDER_UNAVAILABLE");
        double squaredNorm = 0;
        foreach (var value in vector)
        {
            if (!float.IsFinite(value)) throw new RealProviderException("PROVIDER_UNAVAILABLE");
            squaredNorm += (double)value * value;
        }
        if (!double.IsFinite(squaredNorm) || squaredNorm <= 0)
            throw new RealProviderException("PROVIDER_UNAVAILABLE");
    }

    private static RealProviderException SearchUnavailable() => new("SEARCH_UNAVAILABLE");
}
