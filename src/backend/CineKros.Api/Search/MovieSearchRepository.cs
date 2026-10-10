using CineKros.Api.Database;
using CineKros.Api.RealProviders;
using Npgsql;
using Pgvector;
using System.Security.Cryptography;
using System.Text;

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
    private readonly SearchDatasetExpectation? _datasetExpectation;

    public MovieSearchRepository(NpgsqlDataSource dataSource, string expectedEmbeddingProfileFingerprint)
    {
        _dataSource = dataSource ?? throw new ArgumentNullException(nameof(dataSource));
        if (string.IsNullOrWhiteSpace(expectedEmbeddingProfileFingerprint))
            throw new ArgumentException("An expected embedding profile fingerprint is required.", nameof(expectedEmbeddingProfileFingerprint));
        _expectedEmbeddingProfileFingerprint = expectedEmbeddingProfileFingerprint;
    }

    private MovieSearchRepository(NpgsqlDataSource dataSource, SearchDatasetExpectation expectation)
        : this(dataSource, expectation.ProfileFingerprint) => _datasetExpectation = expectation;

    /// <summary>Creates an opt-in Phase 4 POC repository after verifying the live database identity.</summary>
    public static async Task<MovieSearchRepository> CreatePocRepositoryAsync(NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
        => await CreateDatasetRepositoryAsync(dataSource, SearchDatasetExpectation.SerbianPhase4Poc, cancellationToken);

    /// <summary>Creates the explicitly corrected Phase 6T v2 POC repository; never probes or falls back to v1.</summary>
    public static async Task<MovieSearchRepository> CreateCorrectedPhase6TV2PocRepositoryAsync(NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
        => await CreateDatasetRepositoryAsync(dataSource, SearchDatasetExpectation.SerbianPhase6TV2Poc, cancellationToken);

    /// <summary>Creates a repository pinned to the complete Phase 8 bilingual production release.</summary>
    public static async Task<MovieSearchRepository> CreateFullBilingualProductionRepositoryAsync(NpgsqlDataSource dataSource,
        CancellationToken cancellationToken = default)
        => await CreateDatasetRepositoryAsync(dataSource, SearchDatasetExpectation.FullBilingualProduction, cancellationToken);

    private static async Task<MovieSearchRepository> CreateDatasetRepositoryAsync(NpgsqlDataSource dataSource,
        Func<string, SearchDatasetExpectation> expectationFactory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT current_database()", connection);
        var databaseName = (string?)await command.ExecuteScalarAsync(cancellationToken);
        SearchDatasetExpectation expectation;
        try { expectation = expectationFactory(databaseName ?? ""); }
        catch (InvalidOperationException) { throw SearchUnavailable(); }
        if (expectation.MovieCount is not (150 or 9730) || string.IsNullOrWhiteSpace(expectation.CatalogSha256) ||
            string.IsNullOrWhiteSpace(expectation.CatalogIdentity) || string.IsNullOrWhiteSpace(expectation.SelectionIdSetSha256))
            throw SearchUnavailable();
        return new MovieSearchRepository(dataSource, expectation);
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

    /// <summary>Opt-in multilingual POC path. The language enum selects one closed SQL identifier.</summary>
    public async Task<IReadOnlyList<FilteredMovie>> SearchHybridAsync(
        RealHardFilters hardFilters, float[] queryVector, SearchLanguage language,
        CancellationToken cancellationToken = default)
    {
        if (_datasetExpectation is null) throw SearchUnavailable();
        var vectorColumn = SearchLanguageColumn.For(language);
        ArgumentNullException.ThrowIfNull(queryVector);
        ValidateQueryVector(queryVector);
        var filtered = HardFilterSqlBuilder.Build(hardFilters);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await EnsureDatasetLanguageReadyAsync(connection, language, cancellationToken);
        const string prefix = "WITH eligible AS MATERIALIZED (SELECT movie_lens_id, title, year, imdb_id, poster_path, average_rating, rating_count FROM movies ";
        var sql = prefix + filtered.WhereSql + ") SELECT e.movie_lens_id, e.title, e.year, e.imdb_id, e.poster_path, e.average_rating, e.rating_count FROM eligible AS e JOIN movie_embeddings AS me USING (movie_lens_id) ORDER BY " + vectorColumn + " <=> @queryVector ASC, e.movie_lens_id ASC LIMIT 10";
        await using var command = new NpgsqlCommand(sql, connection);
        filtered.AddParameters(command);
        command.Parameters.AddWithValue("queryVector", new Vector(queryVector));
        return await ReadMoviesAsync(command, cancellationToken);
    }

    /// <summary>
    /// Verifies the selected language's guarded POC catalog and vector set before query embedding.
    /// This is intentionally unavailable to the legacy production repository.
    /// </summary>
    public async Task EnsureSelectedLanguageReadyAsync(
        SearchLanguage language, CancellationToken cancellationToken = default)
    {
        _ = SearchLanguageColumn.For(language);
        if (_datasetExpectation is null) throw SearchUnavailable();

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await EnsureDatasetLanguageReadyAsync(connection, language, cancellationToken);
    }

    private async Task EnsureDatasetLanguageReadyAsync(NpgsqlConnection connection, SearchLanguage language, CancellationToken ct)
    {
        var e = _datasetExpectation!;
        var corpus = language == SearchLanguage.English ? e.EnCorpusSha256 : e.SrCorpusSha256;
        var artifact = language == SearchLanguage.English ? e.EnArtifactSha256 : e.SrArtifactSha256;
        var fingerprintColumn = language == SearchLanguage.English ? "document_fingerprint" : "document_fingerprint_sr";
        var embeddingColumn = language == SearchLanguage.English ? "embedding" : "embedding_sr";
        var format = language == SearchLanguage.English ? "en-title-year-director-cast-tags-v1" : "sr-title-year-director-cast-tags-latn-v1";
        var sql = $"SELECT current_database()=@db AND current_schema()='public' AND (SELECT count(*) FROM movies)=@n AND (SELECT count(*) FROM movie_embeddings)=@n AND (SELECT count(*) FROM movie_embeddings WHERE {embeddingColumn} IS NOT NULL AND {fingerprintColumn} ~ '^[0-9a-f]{{64}}$' AND vector_dims({embeddingColumn})=768 AND abs(vector_norm({embeddingColumn})-1)<=0.0001)=@n AND EXISTS(SELECT 1 FROM catalog_import_state WHERE id=1 AND catalog_version=@version AND btrim(catalog_jsonl_sha256)=@catalog AND btrim(catalog_content_fingerprint)=@identity AND movie_count=@n AND btrim(embedding_profile_fingerprint)=@profile AND btrim(embedding_artifact_sha256)=@catalogEnArtifact AND embedded_count=@n) AND EXISTS(SELECT 1 FROM embedding_set_state WHERE language=@lang AND btrim(profile_fingerprint)=@profile AND btrim(catalog_content_fingerprint)=@identity AND btrim(corpus_sha256)=@corpus AND text_format_version=@format AND btrim(artifact_sha256)=@artifact AND btrim(COALESCE(translation_dictionary_sha256,''))=@dictionary AND dimension=768 AND embedded_count=@n) AND (SELECT count(*) FROM embedding_set_state)=2 AND (SELECT count(*) FROM embedding_set_state WHERE language=@lang)=1";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("db", e.DatabaseName); command.Parameters.AddWithValue("n", e.MovieCount);
        command.Parameters.AddWithValue("version", e.CatalogVersion);
        command.Parameters.AddWithValue("catalog", e.CatalogSha256);
        command.Parameters.AddWithValue("catalogEnArtifact", e.EnArtifactSha256);
        command.Parameters.AddWithValue("identity", e.CatalogIdentity);
        command.Parameters.AddWithValue("lang", language == SearchLanguage.English ? "en" : "sr"); command.Parameters.AddWithValue("profile", e.ProfileFingerprint);
        command.Parameters.AddWithValue("corpus", corpus); command.Parameters.AddWithValue("format", format); command.Parameters.AddWithValue("artifact", artifact);
        command.Parameters.AddWithValue("dictionary", language == SearchLanguage.English ? "" : e.DictionarySha256);
        if (await command.ExecuteScalarAsync(ct) is not true || !await DatasetIdSetMatchesAsync(connection, e, ct)) throw SearchUnavailable();
    }

    /// <summary>Validates both released language sets before the listener accepts requests.</summary>
    public async Task EnsureRuntimeReadyAsync(CancellationToken cancellationToken = default)
    {
        if (_datasetExpectation is null) throw SearchUnavailable();
        await EnsureSelectedLanguageReadyAsync(SearchLanguage.English, cancellationToken);
        await EnsureSelectedLanguageReadyAsync(SearchLanguage.Serbian, cancellationToken);
    }

    private async Task EnsureDatasetCatalogReadyAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        var e = _datasetExpectation!;
        const string sql = "SELECT current_database()=@db AND current_schema()='public' AND (SELECT count(*) FROM movies)=@n AND EXISTS(SELECT 1 FROM catalog_import_state WHERE id=1 AND catalog_version=@version AND btrim(catalog_jsonl_sha256)=@catalog AND btrim(catalog_content_fingerprint)=@identity AND movie_count=@n)";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("db", e.DatabaseName); command.Parameters.AddWithValue("n", e.MovieCount);
        command.Parameters.AddWithValue("version", e.CatalogVersion);
        command.Parameters.AddWithValue("catalog", e.CatalogSha256);
        command.Parameters.AddWithValue("identity", e.CatalogIdentity);
        if (await command.ExecuteScalarAsync(ct) is not true || !await DatasetIdSetMatchesAsync(connection, e, ct)) throw SearchUnavailable();
    }

    private static async Task<bool> DatasetIdSetMatchesAsync(NpgsqlConnection connection, SearchDatasetExpectation expectation, CancellationToken ct)
    {
        // Keep the legacy 150-row POC delimiter while matching the full catalog's canonical newline-delimited identity.
        const string sql = "SELECT string_agg(movie_lens_id::text, CASE WHEN @full THEN E'\\n' ELSE ',' END ORDER BY movie_lens_id) || E'\\n' FROM movies";
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("full", expectation.CatalogVersion == SearchDatasetExpectation.FullProductionCatalogVersion);
        var value = (string?)await command.ExecuteScalarAsync(ct);
        return value is not null && Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value))) == expectation.SelectionIdSetSha256;
    }

    public async Task<IReadOnlyList<FilteredMovie>> SearchHardOnlyAsync(
        RealHardFilters hardFilters,
        CancellationToken cancellationToken = default)
    {
        var filtered = HardFilterSqlBuilder.Build(hardFilters);
        if (string.IsNullOrEmpty(filtered.WhereSql))
            throw new RealProviderException("PARSER_INVALID_RESPONSE");

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        if (_datasetExpectation is null) await EnsureCatalogReadyAsync(connection, semantic: false, cancellationToken);
        else await EnsureDatasetCatalogReadyAsync(connection, cancellationToken);

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
