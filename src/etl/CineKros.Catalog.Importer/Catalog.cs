using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CineKros.Catalog.Importer;

public sealed record MovieRow(long Id, string ImdbId, long? TmdbId, string Title, int Year,
    int? RuntimeMinutes, string? OriginalLanguage, IReadOnlyList<string>? Genres, string? AverageRating, long? RatingCount, string? PosterPath);

public sealed class CatalogDocument
{
    internal CatalogDocument(string jsonlHash, string fingerprint, IReadOnlyList<MovieRow> movies)
    {
        JsonlHash = jsonlHash;
        Fingerprint = fingerprint;
        Movies = Array.AsReadOnly(movies.Select(movie => movie with
        {
            Genres = movie.Genres is null ? null : Array.AsReadOnly(movie.Genres.ToArray())
        }).ToArray());
    }

    public string JsonlHash { get; }
    public string Fingerprint { get; }
    public IReadOnlyList<MovieRow> Movies { get; }
}

public static class CatalogValidator
{
    public const string CatalogVersion = "B05a-combined-catalog-v1";
    public const string ExpectedHash = "8b2bad0a22fef45842176a1d9f3730be1568e367b9fc230fa0aa398bb5c26946";
    public const string ExpectedFingerprint = "2ffad7ba703cb80543db617e742a61c88871332910185767ee96fe08a77a0be7";
    public const int ExpectedCount = 9730;
    private static readonly HashSet<string> Genres = ["Action", "Adventure", "Animation", "Children", "Comedy", "Crime", "Documentary", "Drama", "Fantasy", "Film-Noir", "Horror", "IMAX", "Musical", "Mystery", "Romance", "Sci-Fi", "Thriller", "War", "Western"];

    public static async Task<CatalogDocument> LoadAsync(string jsonlPath, CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(jsonlPath);
        var manifestPath = Path.Combine(Path.GetDirectoryName(fullPath)!, "manifest.json");
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath, cancellationToken));
        ValidateManifest(manifest.RootElement);

        await using var stream = File.OpenRead(fullPath);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, cancellationToken));
        if (hash != ExpectedHash || hash != RequiredString(manifest.RootElement, "output", "sha256"))
            throw new InvalidDataException("Catalog source hash does not match the reviewed catalog.");

        var rows = new List<MovieRow>(ExpectedCount);
        var ids = new HashSet<long>();
        var imdbIds = new HashSet<string>(StringComparer.Ordinal);
        var tmdbIds = new HashSet<long>();
        using var reader = new StreamReader(fullPath, new System.Text.UTF8Encoding(false, true));
        string? line;
        long previous = 0;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (line.Length == 0) throw new InvalidDataException("Catalog contains an empty record.");
            using var record = JsonDocument.Parse(line);
            var row = ParseRow(record.RootElement);
            if (row.Id <= 0 || !ids.Add(row.Id) || row.Id <= previous) throw new InvalidDataException("Catalog movie IDs are invalid, duplicate or out of order.");
            if (!imdbIds.Add(row.ImdbId) || (row.TmdbId is not null && !tmdbIds.Add(row.TmdbId.Value)))
                throw new InvalidDataException("Catalog contains duplicate external IDs.");
            previous = row.Id;
            rows.Add(row);
        }
        if (rows.Count != ExpectedCount)
            throw new InvalidDataException("Catalog row count is invalid.");
        ValidateCoverage(manifest.RootElement, rows);
        return new CatalogDocument(hash, ExpectedFingerprint, rows.AsReadOnly());
    }

    public static CatalogDocument ValidateForImport(CatalogDocument catalog)
    {
        if (catalog is null || catalog.JsonlHash != ExpectedHash || catalog.Fingerprint != ExpectedFingerprint ||
            catalog.Movies is null || catalog.Movies.Count != ExpectedCount)
            throw new InvalidDataException("Catalog document identity or count is invalid.");

        var snapshot = new MovieRow[ExpectedCount];
        var imdbIds = new HashSet<string>(StringComparer.Ordinal);
        var tmdbIds = new HashSet<long>();
        long previousId = 0;
        for (var index = 0; index < catalog.Movies.Count; index++)
        {
            var movie = catalog.Movies[index];
            if (movie is null || movie.Id <= previousId || movie.Id <= 0 ||
                string.IsNullOrWhiteSpace(movie.Title) || movie.Year is < 1000 or > 9999 ||
                movie.ImdbId is null || !Regex.IsMatch(movie.ImdbId, "^[0-9]{7,9}$", RegexOptions.CultureInvariant) ||
                !imdbIds.Add(movie.ImdbId) || (movie.TmdbId is not null && (movie.TmdbId <= 0 || !tmdbIds.Add(movie.TmdbId.Value))) ||
                movie.RuntimeMinutes is <= 0 ||
                (movie.OriginalLanguage is not null && !Regex.IsMatch(movie.OriginalLanguage, "^[a-z]{2,3}$", RegexOptions.CultureInvariant)) ||
                (movie.PosterPath is not null && (string.IsNullOrWhiteSpace(movie.PosterPath) || !movie.PosterPath.StartsWith('/') || movie.PosterPath.StartsWith("//", StringComparison.Ordinal))) ||
                ((movie.AverageRating is null) != (movie.RatingCount is null)) || movie.RatingCount is < 0 ||
                (movie.AverageRating is not null && (!decimal.TryParse(movie.AverageRating, NumberStyles.Float, CultureInfo.InvariantCulture, out var rating) || rating is < 0 or > 5)) ||
            (movie.Genres is not null && (movie.Genres.Count == 0 || movie.Genres.Distinct(StringComparer.Ordinal).Count() != movie.Genres.Count || movie.Genres.Any(genre => !Genres.Contains(genre)))))
                throw new InvalidDataException("Catalog document contains an invalid row.");
            snapshot[index] = movie with { Genres = movie.Genres?.ToArray() };
            previousId = movie.Id;
        }
        return new CatalogDocument(catalog.JsonlHash, catalog.Fingerprint, Array.AsReadOnly(snapshot));
    }

    private static void ValidateCoverage(JsonElement manifest, IReadOnlyList<MovieRow> rows)
    {
        var expectedNulls = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["tmdbId"] = 153, ["genres"] = 145, ["averageRating"] = 145, ["ratingCount"] = 145,
            ["runtimeMinutes"] = 179, ["originalLanguage"] = 178, ["posterPath"] = 179
        };
        foreach (var (name, nullCount) in expectedNulls)
        {
            if (RequiredInt(manifest, "coverage", name, "nullCount") != nullCount ||
                RequiredInt(manifest, "coverage", name, "presentCount") != ExpectedCount - nullCount)
                throw new InvalidDataException("Catalog manifest coverage does not match the reviewed catalog.");
        }
        if (rows.Count(x => x.TmdbId is null) != 153 || rows.Count(x => x.Genres is null) != 145 ||
            rows.Count(x => x.AverageRating is null) != 145 || rows.Count(x => x.RatingCount is null) != 145 ||
            rows.Count(x => x.RuntimeMinutes is null) != 179 || rows.Count(x => x.OriginalLanguage is null) != 178 ||
            rows.Count(x => x.PosterPath is null) != 179)
            throw new InvalidDataException("Catalog null coverage does not match its manifest.");
    }

    private static void ValidateManifest(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || RequiredString(root, "catalogVersion") != CatalogVersion ||
            RequiredString(root, "contentFingerprint") != ExpectedFingerprint ||
            RequiredString(root, "semanticContentFingerprint") != "03de75ef85b7e13045c9ce72420f559746996295b19002f34dfc51e1ed5cf003" ||
            RequiredString(root, "enrichmentContentFingerprint") != "209df03d529cd3aae477999a930d3cf50557dc2e589da204ffcd73018a84aacf" ||
            RequiredString(root, "semanticCatalogVersion") != "B2-semantic-catalog-v1" ||
            RequiredString(root, "enrichmentVersion") != "B04-ml32m-tmdb-v2" ||
            RequiredString(root, "b1aSha256") != "a20493ca502106788f3ece917be99ae2e3aa9fbf747841f77adf11ef7a5f620a" ||
            RequiredString(root, "output", "path") != "movies-catalog.jsonl" ||
            RequiredString(root, "output", "order") != "movieLensId ascending" ||
            RequiredInt(root, "output", "recordCount") != ExpectedCount ||
            RequiredBool(root, "validated") != true ||
            RequiredString(root, "inputs", "b2Jsonl", "sha256") != "fe2d6c09869d62e43403fde2b83a99f18ba267d44b07b91a7e6dbcb2818dfbf7" ||
            RequiredString(root, "inputs", "b2Manifest", "sha256") != "2d2f20eb5d95fbc417a95c49143297d57bcff028e8818e3cba3cad1febded783" ||
            RequiredString(root, "inputs", "b04Jsonl", "sha256") != "8501420e30f3c6d2b71391b8fd0e1aedec352ab736adefd01254e9546ba22284" ||
            RequiredString(root, "inputs", "b04Manifest", "sha256") != "691031a4884e15641400961bdb16c3a640e8edd5f2a49fc1f7b21c97694d99f9")
            throw new InvalidDataException("Catalog manifest is not the reviewed real catalog manifest.");
        if (!DateTimeOffset.TryParse(RequiredString(root, "generatedAtUtc"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
            throw new InvalidDataException("Catalog manifest timestamp is invalid.");
    }

    private static MovieRow ParseRow(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Catalog record is not an object.");
        var allowed = new HashSet<string>(["movieLensId", "rawTitle", "title", "year", "imdbId", "movieLensAvgRating", "directedByRaw", "starringRaw", "relevantTags", "semanticText", "tmdbId", "genres", "averageRating", "ratingCount", "runtimeMinutes", "originalLanguage", "posterPath"], StringComparer.Ordinal);
        if (e.EnumerateObject().Any(p => !allowed.Remove(p.Name)) || allowed.Count != 0) throw new InvalidDataException("Catalog record fields do not match the contract.");
        var id = Int64(e, "movieLensId");
        var imdb = String(e, "imdbId");
        if (!Regex.IsMatch(imdb, "^[0-9]{7,9}$", RegexOptions.CultureInvariant)) throw new InvalidDataException("Catalog IMDb ID is invalid.");
        var title = String(e, "title");
        if (string.IsNullOrWhiteSpace(title)) throw new InvalidDataException("Catalog title is blank.");
        var year = Int32(e, "year");
        if (year is < 1000 or > 9999) throw new InvalidDataException("Catalog year is invalid.");
        _ = NullableString(e, "rawTitle"); _ = NullableString(e, "directedByRaw"); _ = NullableString(e, "starringRaw"); _ = String(e, "semanticText");
        var tags = e.GetProperty("relevantTags");
        if (tags.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Catalog tags are malformed.");
        foreach (var tag in tags.EnumerateArray()) if (tag.ValueKind != JsonValueKind.Object || !tag.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String || !tag.TryGetProperty("score", out var s) || !s.TryGetDouble(out var score) || !double.IsFinite(score)) throw new InvalidDataException("Catalog tags are malformed.");

        var tmdb = NullableInt64(e, "tmdbId");
        if (tmdb is <= 0) throw new InvalidDataException("Catalog TMDB ID is invalid.");
        var runtime = NullableInt32(e, "runtimeMinutes");
        if (runtime is <= 0) throw new InvalidDataException("Catalog runtime is invalid.");
        var language = NullableString(e, "originalLanguage");
        if (language is not null && !Regex.IsMatch(language, "^[a-z]{2,3}$", RegexOptions.CultureInvariant)) throw new InvalidDataException("Catalog language code is invalid.");
        var poster = NullableString(e, "posterPath");
        if (poster is not null && (string.IsNullOrWhiteSpace(poster) || !poster.StartsWith('/') || poster.StartsWith("//", StringComparison.Ordinal))) throw new InvalidDataException("Catalog poster path is invalid.");
        string[]? genres = null;
        var g = e.GetProperty("genres");
        if (g.ValueKind != JsonValueKind.Null)
        {
            if (g.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Catalog genres are malformed.");
            genres = g.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : throw new InvalidDataException("Catalog genre is malformed.")).ToArray();
            if (genres.Length == 0 || genres.Distinct(StringComparer.Ordinal).Count() != genres.Length || genres.Any(x => !Genres.Contains(x))) throw new InvalidDataException("Catalog genre label is not approved.");
        }
        var avg = NullableNumberRaw(e, "averageRating");
        var count = NullableInt64(e, "ratingCount");
        if ((avg is null) != (count is null) || (avg is not null && (!decimal.TryParse(avg, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || value is < 0 or > 5)) || count is < 0)
            throw new InvalidDataException("Catalog rating fields are invalid.");
        var mlRating = e.GetProperty("movieLensAvgRating");
        if (mlRating.ValueKind != JsonValueKind.Number || !mlRating.TryGetDecimal(out var ml) || ml is < 0 or > 5) throw new InvalidDataException("Catalog MovieLens rating is invalid.");
        return new MovieRow(id, imdb, tmdb, title, year, runtime, language, genres, avg, count, poster);
    }

    private static string RequiredString(JsonElement e, params string[] path) { foreach (var p in path) { if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) throw new InvalidDataException("Catalog manifest is incomplete."); } return e.ValueKind == JsonValueKind.String ? e.GetString()! : throw new InvalidDataException("Catalog manifest is malformed."); }
    private static int RequiredInt(JsonElement e, params string[] path) => int.Parse(RequiredStringNumber(e, path), CultureInfo.InvariantCulture);
    private static string RequiredStringNumber(JsonElement e, params string[] path) { foreach (var p in path) { if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) throw new InvalidDataException("Catalog manifest is incomplete."); } return e.ValueKind == JsonValueKind.Number ? e.GetRawText() : throw new InvalidDataException("Catalog manifest is malformed."); }
    private static bool RequiredBool(JsonElement e, params string[] path) { foreach (var p in path) { if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) throw new InvalidDataException("Catalog manifest is incomplete."); } return e.ValueKind == JsonValueKind.True; }
    private static string String(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : throw new InvalidDataException("Catalog string field is malformed.");
    private static string? NullableString(JsonElement e, string name) => e.GetProperty(name).ValueKind == JsonValueKind.Null ? null : String(e, name);
    private static int Int32(JsonElement e, string name) => e.GetProperty(name).TryGetInt32(out var v) ? v : throw new InvalidDataException("Catalog integer field is malformed.");
    private static long Int64(JsonElement e, string name) => e.GetProperty(name).TryGetInt64(out var v) ? v : throw new InvalidDataException("Catalog integer field is malformed.");
    private static int? NullableInt32(JsonElement e, string name) => e.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Int32(e, name);
    private static long? NullableInt64(JsonElement e, string name) => e.GetProperty(name).ValueKind == JsonValueKind.Null ? null : Int64(e, name);
    private static string? NullableNumberRaw(JsonElement e, string name) { var v = e.GetProperty(name); return v.ValueKind == JsonValueKind.Null ? null : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : throw new InvalidDataException("Catalog numeric field is malformed."); }
}
