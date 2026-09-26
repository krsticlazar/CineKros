using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CineKros.Etl;

public sealed record ExpectedCounts(
    int MetadataRecords,
    long ScoreRows,
    int ScoredMovieIds,
    int OutputRecords,
    int MissingMetadata)
{
    public static ExpectedCounts ApprovedSnapshot { get; } = new(84661, 10551655, 9734, 9730, 4);
}

public sealed record ExportOptions(string SourceRoot, string OutputDirectory, ExpectedCounts ExpectedCounts);

public sealed record ExportResult(
    int MetadataRecords,
    long ScoreRows,
    int ScoredMovieIds,
    int OutputRecords,
    IReadOnlyList<long> MissingMetadataIds,
    string MetadataSha256,
    string TagdlSha256,
    string OutputSha256);

public sealed class ExportValidationException(string message) : Exception(message);

internal sealed record SourceMovie(
    long MovieLensId,
    string RawTitle,
    string Title,
    int Year,
    string ImdbId,
    decimal MovieLensAvgRating,
    string? DirectedByRaw,
    string? StarringRaw);

public static partial class MetadataExporter
{
    private const long MaximumSafeInteger = 9007199254740991;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    [GeneratedRegex(@" \((\d{4})\)$", RegexOptions.CultureInvariant)]
    private static partial Regex YearSuffixRegex();

    public static ExportResult Export(ExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!Path.IsPathFullyQualified(options.SourceRoot) || !Path.IsPathFullyQualified(options.OutputDirectory))
        {
            throw new ExportValidationException("Source and output paths must be absolute.");
        }

        var metadataPath = Path.Combine(options.SourceRoot, "raw", "metadata_updated.json");
        var tagdlPath = Path.Combine(options.SourceRoot, "scores", "tagdl.csv");
        if (!File.Exists(metadataPath))
        {
            throw new ExportValidationException("Required input 'raw/metadata_updated.json' is missing.");
        }
        if (!File.Exists(tagdlPath))
        {
            throw new ExportValidationException("Required input 'scores/tagdl.csv' is missing.");
        }

        var membership = ReadScoredMembership(tagdlPath);
        var metadata = ReadMetadata(metadataPath, membership.MovieIds);
        ValidateCount("metadata records", metadata.RecordCount, options.ExpectedCounts.MetadataRecords);
        ValidateCount("score rows", membership.ScoreRows, options.ExpectedCounts.ScoreRows);
        ValidateCount("scored movie IDs", membership.MovieIds.Count, options.ExpectedCounts.ScoredMovieIds);

        var joined = new List<SourceMovie>(options.ExpectedCounts.OutputRecords);
        var missing = new List<long>();
        foreach (var movieId in membership.MovieIds.Order())
        {
            if (metadata.Movies.TryGetValue(movieId, out var movie))
            {
                joined.Add(movie);
            }
            else
            {
                missing.Add(movieId);
            }
        }

        ValidateCount("joined output records", joined.Count, options.ExpectedCounts.OutputRecords);
        ValidateCount("MISSING_METADATA exclusions", missing.Count, options.ExpectedCounts.MissingMetadata);
        ValidateJoinedRecords(joined);

        var metadataHash = ComputeSha256(metadataPath);
        var tagdlHash = ComputeSha256(tagdlPath);
        Directory.CreateDirectory(options.OutputDirectory);
        var token = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var jsonlTemporaryPath = Path.Combine(options.OutputDirectory, $".movies-metadata.{token}.tmp");
        var manifestTemporaryPath = Path.Combine(options.OutputDirectory, $".manifest.{token}.tmp");
        var jsonlFinalPath = Path.Combine(options.OutputDirectory, "movies-metadata.jsonl");
        var manifestFinalPath = Path.Combine(options.OutputDirectory, "manifest.json");

        try
        {
            WriteJsonLines(jsonlTemporaryPath, joined);
            ValidateWrittenJsonLines(jsonlTemporaryPath, joined.Count);
            var outputHash = ComputeSha256(jsonlTemporaryPath);
            WriteManifest(
                manifestTemporaryPath,
                metadataHash,
                tagdlHash,
                outputHash,
                metadata.RecordCount,
                membership.ScoreRows,
                membership.MovieIds.Count,
                joined.Count,
                missing);
            ValidateManifest(manifestTemporaryPath, outputHash, joined.Count, missing);

            File.Move(jsonlTemporaryPath, jsonlFinalPath, true);
            File.Move(manifestTemporaryPath, manifestFinalPath, true);
            return new ExportResult(
                metadata.RecordCount,
                membership.ScoreRows,
                membership.MovieIds.Count,
                joined.Count,
                missing,
                metadataHash,
                tagdlHash,
                outputHash);
        }
        finally
        {
            DeleteRunTemporaryFile(jsonlTemporaryPath);
            DeleteRunTemporaryFile(manifestTemporaryPath);
        }
    }

    private static (HashSet<long> MovieIds, long ScoreRows) ReadScoredMembership(string path)
    {
        using var reader = CreateStrictReader(path);
        var header = reader.ReadLine()?.TrimStart('\uFEFF');
        if (header is null || header != "tag,item_id,score")
        {
            throw new ExportValidationException("TagDL header must be exactly 'tag,item_id,score'.");
        }

        var ids = new HashSet<long>();
        long rows = 0;
        string? line;
        var physicalLine = 1;
        while ((line = ReadLineStrict(reader, "TagDL")) is not null)
        {
            physicalLine++;
            if (line.Length == 0)
            {
                throw new ExportValidationException($"TagDL contains a blank data line at line {physicalLine}.");
            }

            var fields = CsvThreeFieldParser.Parse(line, physicalLine);
            var movieId = ParsePositiveSafeInteger(fields[1], $"TagDL item_id at line {physicalLine}");
            if (fields[2].Length == 0 ||
                !double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var score) ||
                !double.IsFinite(score))
            {
                throw new ExportValidationException($"TagDL score is invalid at line {physicalLine}.");
            }
            ids.Add(movieId);
            rows++;
        }

        return (ids, rows);
    }

    private static (Dictionary<long, SourceMovie> Movies, int RecordCount) ReadMetadata(string path, IReadOnlySet<long> scoredMovieIds)
    {
        using var reader = CreateStrictReader(path);
        var movies = new Dictionary<long, SourceMovie>();
        var allMovieIds = new HashSet<long>();
        var records = 0;
        string? line;
        while ((line = ReadLineStrict(reader, "Metadata")) is not null)
        {
            records++;
            if (records == 1) line = line.TrimStart('\uFEFF');
            if (line.Length == 0)
            {
                throw new ExportValidationException($"Metadata contains a blank line at line {records}.");
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                throw new ExportValidationException($"Metadata contains invalid JSON at line {records}.");
            }

            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new ExportValidationException($"Metadata line {records} must be a JSON object.");
                }

                var root = document.RootElement;
                EnsureRequiredPropertiesAppearOnce(root, records);
                var movieIdElement = RequireKind(root, "item_id", JsonValueKind.Number, records);
                if (!movieIdElement.TryGetInt64(out var movieId) || movieId <= 0 || movieId > MaximumSafeInteger)
                {
                    throw new ExportValidationException($"Metadata item_id is not a positive safe integer at line {records}.");
                }
                if (!allMovieIds.Add(movieId))
                {
                    throw new ExportValidationException($"Metadata contains duplicate item_id {movieId}.");
                }

                var rawTitle = RequireKind(root, "title", JsonValueKind.String, records).GetString()!;
                var imdbId = RequireKind(root, "imdbId", JsonValueKind.String, records).GetString()!;
                if (imdbId.Length == 0 || imdbId.Any(character => character is < '0' or > '9'))
                {
                    throw new ExportValidationException($"Metadata imdbId must contain only ASCII digits at line {records}.");
                }

                var ratingElement = RequireKind(root, "avgRating", JsonValueKind.Number, records);
                if (!ratingElement.TryGetDecimal(out var rating) || rating is < 0m or > 5m)
                {
                    throw new ExportValidationException($"Metadata avgRating must be a finite decimal from 0 through 5 at line {records}.");
                }

                var directedBy = RequireKind(root, "directedBy", JsonValueKind.String, records).GetString()!.Trim();
                var starring = RequireKind(root, "starring", JsonValueKind.String, records).GetString()!.Trim();
                if (!scoredMovieIds.Contains(movieId))
                {
                    continue;
                }

                var trimmedTitle = rawTitle.Trim();
                var yearMatch = YearSuffixRegex().Match(trimmedTitle);
                if (!yearMatch.Success || !int.TryParse(yearMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var year))
                {
                    throw new ExportValidationException($"Metadata title lacks a final ' (YYYY)' suffix at line {records}.");
                }
                var title = trimmedTitle[..yearMatch.Index].Trim();
                if (title.Length == 0)
                {
                    throw new ExportValidationException($"Metadata mapped title is blank at line {records}.");
                }

                var movie = new SourceMovie(
                    movieId,
                    rawTitle,
                    title,
                    year,
                    imdbId,
                    rating,
                    directedBy.Length == 0 ? null : directedBy,
                    starring.Length == 0 ? null : starring);
                movies.Add(movieId, movie);
            }
        }

        return (movies, records);
    }

    private static StreamReader CreateStrictReader(string path) =>
        new(path, StrictUtf8, detectEncodingFromByteOrderMarks: false, bufferSize: 65536);

    private static string? ReadLineStrict(StreamReader reader, string sourceName)
    {
        try
        {
            return reader.ReadLine();
        }
        catch (DecoderFallbackException)
        {
            throw new ExportValidationException($"{sourceName} is not valid UTF-8.");
        }
    }

    private static void EnsureRequiredPropertiesAppearOnce(JsonElement root, int line)
    {
        var required = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["title"] = 0,
            ["directedBy"] = 0,
            ["starring"] = 0,
            ["avgRating"] = 0,
            ["imdbId"] = 0,
            ["item_id"] = 0,
        };
        foreach (var property in root.EnumerateObject())
        {
            if (required.ContainsKey(property.Name)) required[property.Name]++;
        }
        foreach (var (name, count) in required)
        {
            if (count != 1)
            {
                throw new ExportValidationException($"Metadata property '{name}' must appear exactly once at line {line}.");
            }
        }
    }

    private static JsonElement RequireKind(JsonElement root, string name, JsonValueKind kind, int line)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != kind)
        {
            throw new ExportValidationException($"Metadata property '{name}' has the wrong type at line {line}.");
        }
        return value;
    }

    private static long ParsePositiveSafeInteger(string text, string label)
    {
        if (text.Length == 0 || text.Any(character => character is < '0' or > '9') ||
            !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
            value <= 0 || value > MaximumSafeInteger)
        {
            throw new ExportValidationException($"{label} is not a positive safe integer.");
        }
        return value;
    }

    private static void ValidateJoinedRecords(IReadOnlyList<SourceMovie> records)
    {
        var ids = new HashSet<long>();
        var imdbIds = new HashSet<string>(StringComparer.Ordinal);
        long previous = 0;
        foreach (var record in records)
        {
            if (!ids.Add(record.MovieLensId))
            {
                throw new ExportValidationException($"Output contains duplicate movieLensId {record.MovieLensId}.");
            }
            if (!imdbIds.Add(record.ImdbId))
            {
                throw new ExportValidationException("Joined metadata contains a duplicate exact IMDb ID.");
            }
            if (record.MovieLensId <= previous)
            {
                throw new ExportValidationException("Output records are not strictly ordered by movieLensId.");
            }
            previous = record.MovieLensId;
        }
    }

    private static void WriteJsonLines(string path, IReadOnlyList<SourceMovie> records)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        foreach (var record in records)
        {
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
            {
                writer.WriteStartObject();
                writer.WriteNumber("movieLensId", record.MovieLensId);
                writer.WriteString("rawTitle", record.RawTitle);
                writer.WriteString("title", record.Title);
                writer.WriteNumber("year", record.Year);
                writer.WriteString("imdbId", record.ImdbId);
                writer.WriteNumber("movieLensAvgRating", record.MovieLensAvgRating);
                if (record.DirectedByRaw is null) writer.WriteNull("directedByRaw"); else writer.WriteString("directedByRaw", record.DirectedByRaw);
                if (record.StarringRaw is null) writer.WriteNull("starringRaw"); else writer.WriteString("starringRaw", record.StarringRaw);
                writer.WriteEndObject();
                writer.Flush();
            }
            stream.WriteByte((byte)'\n');
        }
        stream.Flush(flushToDisk: true);
    }

    private static void ValidateWrittenJsonLines(string path, int expectedRecords)
    {
        using var reader = CreateStrictReader(path);
        var count = 0;
        long previousId = 0;
        string? line;
        while ((line = ReadLineStrict(reader, "Output")) is not null)
        {
            if (line.Length == 0) throw new ExportValidationException("Generated output contains a blank line.");
            using var document = JsonDocument.Parse(line);
            var properties = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
            var expectedProperties = new[] { "movieLensId", "rawTitle", "title", "year", "imdbId", "movieLensAvgRating", "directedByRaw", "starringRaw" };
            if (!properties.SequenceEqual(expectedProperties, StringComparer.Ordinal))
            {
                throw new ExportValidationException("Generated output has an unexpected property order or shape.");
            }
            var id = document.RootElement.GetProperty("movieLensId").GetInt64();
            if (id <= previousId) throw new ExportValidationException("Generated output order validation failed.");
            previousId = id;
            count++;
        }
        ValidateCount("generated output records", count, expectedRecords);
    }

    private static void WriteManifest(
        string path,
        string metadataHash,
        string tagdlHash,
        string outputHash,
        int metadataRecords,
        long scoreRows,
        int scoredMovieIds,
        int outputRecords,
        IReadOnlyList<long> missing)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
        writer.WriteStartObject();
        writer.WriteString("mappingVersion", "B1a-v1");
        writer.WriteString("generatedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        writer.WriteStartObject("inputs");
        writer.WriteStartObject("metadata"); writer.WriteString("path", "raw/metadata_updated.json"); writer.WriteString("sha256", metadataHash); writer.WriteEndObject();
        writer.WriteStartObject("tagdl"); writer.WriteString("path", "scores/tagdl.csv"); writer.WriteString("sha256", tagdlHash); writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteStartObject("output"); writer.WriteString("path", "movies-metadata.jsonl"); writer.WriteString("sha256", outputHash); writer.WriteEndObject();
        writer.WriteStartObject("counts");
        writer.WriteNumber("metadataRecords", metadataRecords);
        writer.WriteNumber("scoreRows", scoreRows);
        writer.WriteNumber("scoredMovieIds", scoredMovieIds);
        writer.WriteNumber("outputRecords", outputRecords);
        writer.WriteEndObject();
        writer.WriteStartObject("exclusions");
        writer.WriteStartObject("MISSING_METADATA");
        writer.WriteNumber("count", missing.Count);
        writer.WriteStartArray("movieLensIds"); foreach (var id in missing) writer.WriteNumberValue(id); writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.Flush();
        stream.WriteByte((byte)'\n');
        stream.Flush(flushToDisk: true);
    }

    private static void ValidateManifest(string path, string outputHash, int outputRecords, IReadOnlyList<long> missing)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        if (root.GetProperty("mappingVersion").GetString() != "B1a-v1" ||
            root.GetProperty("output").GetProperty("sha256").GetString() != outputHash ||
            root.GetProperty("counts").GetProperty("outputRecords").GetInt32() != outputRecords ||
            root.GetProperty("exclusions").GetProperty("MISSING_METADATA").GetProperty("count").GetInt32() != missing.Count)
        {
            throw new ExportValidationException("Generated manifest validation failed.");
        }
    }

    private static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static void ValidateCount(string label, long actual, long expected)
    {
        if (actual != expected)
        {
            throw new ExportValidationException($"Snapshot count mismatch for {label}: expected {expected}, observed {actual}.");
        }
    }

    private static void DeleteRunTemporaryFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}

internal static class CsvThreeFieldParser
{
    public static string[] Parse(string line, int physicalLine)
    {
        var fields = new List<string>(3);
        var field = new StringBuilder();
        var quoted = false;
        var afterQuote = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (quoted)
            {
                if (character == '"')
                {
                    if (index + 1 < line.Length && line[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        quoted = false;
                        afterQuote = true;
                    }
                }
                else
                {
                    field.Append(character);
                }
                continue;
            }

            if (afterQuote)
            {
                if (character != ',') throw InvalidCsv(physicalLine);
                fields.Add(field.ToString());
                field.Clear();
                afterQuote = false;
                continue;
            }

            if (character == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else if (character == '"')
            {
                if (field.Length != 0) throw InvalidCsv(physicalLine);
                quoted = true;
            }
            else
            {
                field.Append(character);
            }
        }

        if (quoted) throw new ExportValidationException($"TagDL contains an unsupported quoted physical newline at line {physicalLine}.");
        fields.Add(field.ToString());
        if (fields.Count != 3) throw InvalidCsv(physicalLine);
        return fields.ToArray();
    }

    private static ExportValidationException InvalidCsv(int line) =>
        new($"TagDL contains malformed CSV at line {line}.");
}
