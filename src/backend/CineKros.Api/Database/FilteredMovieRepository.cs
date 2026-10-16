using CineKros.Api.RealProviders;
using Npgsql;
using NpgsqlTypes;
using System.Text;

namespace CineKros.Api.Database;

/// <summary>A validated, parameterized movie-filter query that callers may use as a candidate relation.</summary>
public sealed class FilteredMovieQuery
{
    private readonly (string Name, NpgsqlDbType Type, object Value)[] _parameters;

    internal FilteredMovieQuery(string whereSql, (string Name, NpgsqlDbType Type, object Value)[] parameters)
    {
        WhereSql = whereSql;
        _parameters = parameters;
    }

    public string WhereSql { get; }

    public void AddParameters(NpgsqlCommand command)
    {
        foreach (var parameter in _parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Type, parameter.Value);
    }
}

/// <summary>Builds only the B2 allowlisted hard-filter predicates.</summary>
public static class HardFilterSqlBuilder
{
    private static readonly HashSet<string> Genres = new(StringComparer.Ordinal)
    {
        "Action", "Adventure", "Animation", "Children", "Comedy", "Crime", "Documentary", "Drama", "Fantasy", "Film-Noir", "Horror", "IMAX", "Musical", "Mystery", "Romance", "Sci-Fi", "Thriller", "War", "Western"
    };

    private static readonly HashSet<string> Languages = new(StringComparer.Ordinal)
    {
        "ar", "bm", "bn", "bo", "bs", "cs", "da", "de", "el", "en", "es", "fa", "fi", "fr", "he", "hi", "hu", "id", "is", "it", "iu", "ja", "ka", "ko", "ku", "mk", "mn", "nl", "no", "pl", "pt", "ro", "ru", "sk", "sr", "sv", "ta", "th", "tl", "tn", "tr", "vi", "zh"
    };

    public static FilteredMovieQuery Build(RealHardFilters filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        Validate(filters);

        var clauses = new List<string>();
        var parameters = new List<(string Name, NpgsqlDbType Type, object Value)>();
        Add(filters.YearMin, "year >= @yearMin", "yearMin", NpgsqlDbType.Integer, clauses, parameters);
        Add(filters.YearMax, "year <= @yearMax", "yearMax", NpgsqlDbType.Integer, clauses, parameters);
        Add(filters.RuntimeMin, "runtime_minutes >= @runtimeMin", "runtimeMin", NpgsqlDbType.Integer, clauses, parameters);
        Add(filters.RuntimeMax, "runtime_minutes <= @runtimeMax", "runtimeMax", NpgsqlDbType.Integer, clauses, parameters);
        if (filters.Genres?.All is { } all)
        {
            clauses.Add("genres @> @genresAll::text[]");
            parameters.Add(("genresAll", NpgsqlDbType.Array | NpgsqlDbType.Text, all.ToArray()));
        }
        if (filters.Genres?.Any is { } any)
        {
            clauses.Add("genres && @genresAny::text[]");
            parameters.Add(("genresAny", NpgsqlDbType.Array | NpgsqlDbType.Text, any.ToArray()));
        }
        if (filters.RatingMin is { } ratingMin)
        {
            var ratingClause = filters.RatingOperator switch
            {
                null or "gte" => "average_rating >= @ratingMin",
                "gt" => "average_rating > @ratingMin",
                _ => throw new RealProviderException("PARSER_INVALID_RESPONSE")
            };
            clauses.Add(ratingClause);
            parameters.Add(("ratingMin", NpgsqlDbType.Numeric, ratingMin));
        }
        Add(filters.OriginalLanguage, "original_language = @originalLanguage", "originalLanguage", NpgsqlDbType.Text, clauses, parameters);
        if (filters.Actors is { } actors)
        {
            clauses.Add("\"cast\" IS NOT NULL AND cardinality(\"cast\") > 0");
            AddActors(actors.All, "\"cast\" @> @actorsAll::text[]", "actorsAll");
            AddActors(actors.Any, "\"cast\" && @actorsAny::text[]", "actorsAny");
            AddActors(actors.Exclude, "NOT (\"cast\" && @actorsExclude::text[])", "actorsExclude");
        }

        void AddActors(IReadOnlyList<string>? values, string clause, string name)
        {
            if (values is null) return;
            clauses.Add(clause);
            parameters.Add((name, NpgsqlDbType.Array | NpgsqlDbType.Text, values.ToArray()));
        }

        return new FilteredMovieQuery(clauses.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", clauses), parameters.ToArray());
    }

    private static void Validate(RealHardFilters filters)
    {
        if (filters.YearMin is < 1000 or > 9999 || filters.YearMax is < 1000 or > 9999 ||
            filters.RuntimeMin is < 1 || filters.RuntimeMax is < 1 ||
            filters.YearMin > filters.YearMax || filters.RuntimeMin > filters.RuntimeMax ||
            filters.RatingMin is < 0 or > 5 ||
            filters.RatingOperator is not null and not ("gte" or "gt") ||
            filters.RatingMin is null && filters.RatingOperator is not null ||
            filters.OriginalLanguage is not null && !Languages.Contains(filters.OriginalLanguage))
            Invalid();

        ValidateActors(filters.Actors);
        if (filters.Genres is not { } genreFilter) return;
        if (genreFilter.All is null && genreFilter.Any is null) Invalid();
        ValidateGenres(genreFilter.All);
        ValidateGenres(genreFilter.Any);
    }

    private static void ValidateActors(RealActorFilter? actors)
    {
        if (actors is null) return;
        if (actors.All is null && actors.Any is null && actors.Exclude is null) Invalid();
        foreach (var group in new[] { actors.All, actors.Any, actors.Exclude })
        {
            if (group is null) continue;
            if (group.Count == 0) Invalid();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in group)
                if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || !name.IsNormalized(NormalizationForm.FormC) || name.Any(char.IsControl) || !seen.Add(name)) Invalid();
        }
        var excluded = new HashSet<string>(actors.Exclude ?? [], StringComparer.Ordinal);
        if (actors.All?.Any(excluded.Contains) == true || actors.Any is { } any && any.All(excluded.Contains)) Invalid();
    }

    public static void ValidateActorMembership(RealHardFilters filters, ActorNameResolver? resolver)
    {
        ArgumentNullException.ThrowIfNull(filters);
        ValidateActors(filters.Actors);
        if (filters.Actors is not { } actors) return;
        if (resolver is null) Invalid();
        foreach (var name in (actors.All ?? []).Concat(actors.Any ?? []).Concat(actors.Exclude ?? []))
            if (!resolver!.IsCanonical(name)) Invalid();
    }

    private static void ValidateGenres(IReadOnlyList<string>? values)
    {
        if (values is null) return;
        if (values.Count == 0) Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
            if (!Genres.Contains(value) || !seen.Add(value)) Invalid();
    }

    private static void Add<T>(T? value, string clause, string name, NpgsqlDbType type, List<string> clauses, List<(string Name, NpgsqlDbType Type, object Value)> parameters) where T : struct
    {
        if (value is not { } present) return;
        clauses.Add(clause);
        parameters.Add((name, type, present));
    }

    private static void Add(string? value, string clause, string name, NpgsqlDbType type, List<string> clauses, List<(string Name, NpgsqlDbType Type, object Value)> parameters)
    {
        if (value is null) return;
        clauses.Add(clause);
        parameters.Add((name, type, value));
    }

    private static void Invalid() => throw new RealProviderException("PARSER_INVALID_RESPONSE");
}

public sealed record FilteredMovie(long MovieLensId, string Title, int Year, string ImdbId, string? PosterPath, decimal? AverageRating, long? RatingCount);

/// <summary>Executes the unranked filtered movie relation; ranking is owned by the search layer.</summary>
public sealed class FilteredMovieRepository(NpgsqlDataSource dataSource, ActorNameResolver? actorResolver = null)
{
    private const string SelectSql = "SELECT movie_lens_id, title, year, imdb_id, poster_path, average_rating, rating_count FROM movies";

    public async Task<IReadOnlyList<FilteredMovie>> ReadAsync(RealHardFilters filters, CancellationToken cancellationToken = default)
    {
        HardFilterSqlBuilder.ValidateActorMembership(filters, actorResolver);
        var query = HardFilterSqlBuilder.Build(filters);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(SelectSql + " " + query.WhereSql, connection);
        query.AddParameters(command);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var movies = new List<FilteredMovie>();
        while (await reader.ReadAsync(cancellationToken))
            movies.Add(new FilteredMovie(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetDecimal(5), reader.IsDBNull(6) ? null : reader.GetInt64(6)));
        return movies;
    }
}
