using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CineKros.Etl;

public sealed record SemanticExpectedCounts(int B1aRecords, long TagdlRows);
public sealed record SemanticExportOptions(
    string InputJsonl, string InputManifest, string TagdlCsv, string OutputDirectory,
    byte[] ExpectedB1aSha256, byte[] ExpectedManifestSha256, byte[] ExpectedTagdlSha256,
    SemanticExpectedCounts ExpectedCounts, bool RequirePinnedSnapshot = false)
{
    internal Action<SemanticExportStage>? TestStageHook { get; init; }
}
public sealed record SemanticExportResult(int OutputRecords, long TagdlRows, long MoviesWithNoTags, string OutputSha256, string ContentFingerprint);
internal enum SemanticExportStage { BeforePublish }

internal sealed record B1aMovie(long Id, string RawTitle, string Title, int Year, string ImdbId, string RatingRaw, double Rating, string? Director, string? Cast);
internal sealed record RelevantTag(string Name, double Score);

public static class SemanticExporter
{
    public const string B1aHash = "a20493ca502106788f3ece917be99ae2e3aa9fbf747841f77adf11ef7a5f620a";
    public const string B1aManifestHash = "1384c67fdce08f9b52a57b60bc6c0e361bb826d798b8e92f0336a629fc8b7667";
    public const string MetadataSourceHash = "c40137f30cc167271599bf9e97fc7c6b59373d0de5c0bdf7b06ec076d555d1be";
    public const string TagdlHash = "8a890633bd43d16fd3092f34fa68b284abea47c861355a4f2e213be061c1348a";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private const int TopCount = 10;

    public static SemanticExportResult Export(SemanticExportOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        RequireAbsolute(options.InputJsonl, "input-jsonl");
        RequireAbsolute(options.InputManifest, "input-manifest");
        RequireAbsolute(options.TagdlCsv, "tagdl-csv");
        RequireAbsolute(options.OutputDirectory, "output-dir");
        if (Directory.Exists(options.OutputDirectory) || File.Exists(options.OutputDirectory))
            throw new ExportValidationException("Output directory must be new and absent.");
        foreach (var path in new[] { options.InputJsonl, options.InputManifest, options.TagdlCsv })
            if (!File.Exists(path)) throw new ExportValidationException("A required input file is missing.");

        var b1Hash = Hash(options.InputJsonl);
        var manifestHash = Hash(options.InputManifest);
        var tagHash = Hash(options.TagdlCsv);
        MatchHash("B1a JSONL", b1Hash, options.ExpectedB1aSha256);
        MatchHash("B1a manifest", manifestHash, options.ExpectedManifestSha256);
        MatchHash("TagDL", tagHash, options.ExpectedTagdlSha256);
        var b1a = ReadB1a(options.InputJsonl, options.ExpectedCounts.B1aRecords, cancellationToken);
        ValidateB1aManifest(options.InputManifest, Convert.ToHexStringLower(b1Hash), b1a.Count, options.RequirePinnedSnapshot);
        if (options.RequirePinnedSnapshot && (Convert.ToHexStringLower(b1Hash) != B1aHash || Convert.ToHexStringLower(manifestHash) != B1aManifestHash || Convert.ToHexStringLower(tagHash) != TagdlHash))
            throw new ExportValidationException("Pinned production source hash mismatch.");

        var targets = b1a.Select(x => x.Id).ToHashSet();
        var candidates = targets.ToDictionary(id => id, _ => new List<RelevantTag>(TopCount));
        var pairKeys = options.RequirePinnedSnapshot ? null : new HashSet<(long, string)>();
        var scoreRows = ReadTagdl(options.TagdlCsv, targets, candidates, pairKeys, cancellationToken);
        if (scoreRows != options.ExpectedCounts.TagdlRows) throw new ExportValidationException("TagDL row count mismatch.");

        var output = options.OutputDirectory;
        var parent = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(parent);
        var staging = output.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".staging-" + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(staging);
            var jsonPath = Path.Combine(staging, "movies-semantic.jsonl");
            var manifestPath = Path.Combine(staging, "manifest.json");
            WriteJsonl(jsonPath, b1a, candidates);
            ValidateOutput(jsonPath, b1a.Count);
            var outputHash = Convert.ToHexStringLower(Hash(jsonPath));
            var fingerprint = Fingerprint(Convert.ToHexStringLower(b1Hash), Convert.ToHexStringLower(tagHash));
            var missing = candidates.Values.Sum(x => x.Count == 0 ? 1 : 0);
            WriteManifest(manifestPath, options, b1Hash, manifestHash, tagHash, outputHash, fingerprint, b1a, scoreRows, missing);
            ValidateManifestOutput(manifestPath, outputHash, fingerprint, b1a.Count);
            options.TestStageHook?.Invoke(SemanticExportStage.BeforePublish);
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(output) || File.Exists(output)) throw new ExportValidationException("Output directory appeared during export.");
            Directory.Move(staging, output);
            return new SemanticExportResult(b1a.Count, scoreRows, missing, outputHash, fingerprint);
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    private static List<B1aMovie> ReadB1a(string path, int expectedCount, CancellationToken cancellationToken)
    {
        var result = new List<B1aMovie>(expectedCount);
        var ids = new HashSet<long>();
        var imdbIds = new HashSet<string>(StringComparer.Ordinal);
        using var reader = new StreamReader(path, Utf8, false, 65536);
        string? line;
        long previous = 0;
        while ((line = ReadLine(reader, "B1a")) is not null)
        {
            if ((result.Count & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (line.Length == 0) throw new ExportValidationException("B1a contains a blank line.");
            using var doc = ParseJson(line, "B1a");
            var root = doc.RootElement;
            string[] names = ["movieLensId", "rawTitle", "title", "year", "imdbId", "movieLensAvgRating", "directedByRaw", "starringRaw"];
            if (root.ValueKind != JsonValueKind.Object || !root.EnumerateObject().Select(p => p.Name).SequenceEqual(names, StringComparer.Ordinal))
                throw new ExportValidationException("B1a record has an unexpected shape or field order.");
            var id = root.GetProperty("movieLensId").GetInt64();
            var raw = NeedString(root, "rawTitle"); var title = NeedString(root, "title"); var year = root.GetProperty("year").GetInt32();
            var imdb = NeedString(root, "imdbId"); var ratingElement = root.GetProperty("movieLensAvgRating");
            if (ratingElement.ValueKind != JsonValueKind.Number) throw new ExportValidationException("B1a rating must be a number.");
            var rating = ratingElement.GetDouble(); var ratingRaw = ratingElement.GetRawText();
            var director = NullableString(root, "directedByRaw"); var cast = NullableString(root, "starringRaw");
            if (id <= previous || !ids.Add(id) || id > 9007199254740991 || title.Trim().Length == 0 || HasLineBreak(title) || HasLineBreak(director) || HasLineBreak(cast) || year is < 1000 or > 9999 || imdb.Length == 0 || imdb.Any(c => c is < '0' or > '9') || !imdbIds.Add(imdb) || !double.IsFinite(rating) || rating is < 0 or > 5)
                throw new ExportValidationException("B1a record failed value or ordering validation.");
            previous = id;
            result.Add(new B1aMovie(id, raw, title, year, imdb, ratingRaw, rating, director, cast));
        }
        if (result.Count != expectedCount) throw new ExportValidationException("B1a record count mismatch.");
        return result;
    }

    private static long ReadTagdl(string path, HashSet<long> targets, Dictionary<long, List<RelevantTag>> selected, HashSet<(long, string)>? pairs, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(path, Utf8, false, 65536);
        if (ReadLine(reader, "TagDL")?.TrimStart('\uFEFF') != "tag,item_id,score") throw new ExportValidationException("TagDL header must be exactly 'tag,item_id,score'.");
        long rowCount = 0; string? line; int lineNumber = 1;
        while ((line = ReadLine(reader, "TagDL")) is not null)
        {
            if ((lineNumber & 16383) == 0) cancellationToken.ThrowIfCancellationRequested();
            lineNumber++; if (line.Length == 0) throw new ExportValidationException("TagDL contains a blank line.");
            var fields = CsvThreeFieldParser.Parse(line, lineNumber);
            if (fields[0].Length == 0 || fields[1].Length == 0 || !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0 || id > 9007199254740991 ||
                !double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var score) || !double.IsFinite(score))
                throw new ExportValidationException($"TagDL contains an invalid field at line {lineNumber}.");
            rowCount++;
            if (pairs is not null && !pairs.Add((id, fields[0]))) throw new ExportValidationException("TagDL contains a duplicate (item_id, exact tag) pair.");
            if (!targets.Contains(id)) continue;
            var list = selected[id];
            list.Add(new RelevantTag(fields[0], score));
            list.Sort(CompareTags);
            if (list.Count > TopCount) list.RemoveAt(list.Count - 1);
        }
        return rowCount;
    }

    private static int CompareTags(RelevantTag a, RelevantTag b)
    {
        var score = b.Score.CompareTo(a.Score);
        return score != 0 ? score : StringComparer.Ordinal.Compare(a.Name, b.Name);
    }

    private static void WriteJsonl(string path, IReadOnlyList<B1aMovie> movies, Dictionary<long, List<RelevantTag>> tags)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        foreach (var m in movies)
        {
            using var writer = new Utf8JsonWriter(stream);
            writer.WriteStartObject(); writer.WriteNumber("movieLensId", m.Id); writer.WriteString("rawTitle", m.RawTitle); writer.WriteString("title", m.Title); writer.WriteNumber("year", m.Year);
            writer.WriteString("imdbId", m.ImdbId); writer.WritePropertyName("movieLensAvgRating"); writer.WriteRawValue(m.RatingRaw, skipInputValidation: false);
            if (m.Director is null) writer.WriteNull("directedByRaw"); else writer.WriteString("directedByRaw", m.Director);
            if (m.Cast is null) writer.WriteNull("starringRaw"); else writer.WriteString("starringRaw", m.Cast);
            writer.WriteStartArray("relevantTags"); foreach (var t in tags[m.Id]) { writer.WriteStartObject(); writer.WriteString("name", t.Name); writer.WriteNumber("score", t.Score); writer.WriteEndObject(); } writer.WriteEndArray();
            var tagText = tags[m.Id].Count == 0 ? "none" : string.Join(", ", tags[m.Id].Select(t => t.Name));
            writer.WriteString("semanticText", $"Title: {m.Title}. Year: {m.Year.ToString(CultureInfo.InvariantCulture)}. Director: {m.Director ?? "unknown"}. Cast: {m.Cast ?? "unknown"}. Tags: {tagText}.");
            writer.WriteEndObject(); writer.Flush(); stream.WriteByte((byte)'\n');
        }
        stream.Flush(true);
    }

    private static void ValidateOutput(string path, int expected)
    {
        var count = 0; long prior = 0; using var reader = new StreamReader(path, Utf8, false);
        string[] fields = ["movieLensId", "rawTitle", "title", "year", "imdbId", "movieLensAvgRating", "directedByRaw", "starringRaw", "relevantTags", "semanticText"];
        string? line; while ((line = ReadLine(reader, "Output")) is not null) { if (line.Length == 0) throw new ExportValidationException("Output contains a blank line."); using var d = ParseJson(line, "Output");
            if (!d.RootElement.EnumerateObject().Select(x => x.Name).SequenceEqual(fields, StringComparer.Ordinal)) throw new ExportValidationException("Output field order mismatch.");
            var id = d.RootElement.GetProperty("movieLensId").GetInt64(); if (id <= prior) throw new ExportValidationException("Output order invalid."); prior = id; count++; }
        if (count != expected) throw new ExportValidationException("Output count mismatch.");
    }

    private static void WriteManifest(string path, SemanticExportOptions o, byte[] b1hash, byte[] mhash, byte[] thash, string outputHash, string fingerprint, List<B1aMovie> movies, long rows, long missing)
    {
        using var fs = File.Create(path); using var w = new Utf8JsonWriter(fs);
        w.WriteStartObject(); w.WriteString("mappingVersion", "B1a-v1"); w.WriteString("catalogVersion", "B2-semantic-catalog-v1"); w.WriteString("tagRuleVersion", "B2-tagdl-top10-v1"); w.WriteString("semanticTemplateVersion", "B2-semantic-text-v1");
        w.WriteString("contentFingerprint", fingerprint); w.WriteString("generatedAtUtc", DateTimeOffset.UtcNow.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        w.WriteStartObject("inputs"); Input(w, "b1aJsonl", o.InputJsonl, b1hash); Input(w, "b1aManifest", o.InputManifest, mhash); Input(w, "tagdlCsv", o.TagdlCsv, thash); w.WriteEndObject();
        w.WriteStartObject("output"); w.WriteString("path", "movies-semantic.jsonl"); w.WriteString("sha256", outputHash); w.WriteNumber("recordCount", movies.Count); w.WriteNumber("firstMovieLensId", movies[0].Id); w.WriteNumber("lastMovieLensId", movies[^1].Id); w.WriteString("order", "movieLensId ascending"); w.WriteEndObject();
        using var sourceManifest = JsonDocument.Parse(File.ReadAllBytes(o.InputManifest));
        w.WriteStartObject("counts"); w.WriteNumber("tagdlRows", rows); w.WriteNumber("b1aRecords", movies.Count); w.WriteNumber("outputRecords", movies.Count); w.WriteNumber("moviesWithNoTags", missing); w.WriteNumber("validationFailures", 0); w.WriteEndObject();
        w.WriteStartObject("exclusions"); w.WriteStartObject("B1a_MISSING_METADATA");
        var b1Exclusion = sourceManifest.RootElement.GetProperty("exclusions").GetProperty("MISSING_METADATA");
        w.WriteNumber("count", b1Exclusion.GetProperty("count").GetInt32()); w.WriteStartArray("movieLensIds"); foreach (var id in b1Exclusion.GetProperty("movieLensIds").EnumerateArray()) w.WriteNumberValue(id.GetInt64()); w.WriteEndArray(); w.WriteEndObject(); w.WriteEndObject();
        w.WriteEndObject(); w.Flush(); fs.Flush(true);
    }
    private static void Input(Utf8JsonWriter w, string name, string path, byte[] hash) { w.WriteStartObject(name); w.WriteString("path", path); w.WriteString("sha256", Convert.ToHexStringLower(hash)); w.WriteEndObject(); }
    private static string Fingerprint(string b1, string tag)
    {
        var payload = "{\"catalogVersion\":\"B2-semantic-catalog-v1\",\"mappingVersion\":\"B1a-v1\",\"tagRuleVersion\":\"B2-tagdl-top10-v1\",\"semanticTemplateVersion\":\"B2-semantic-text-v1\",\"b1aSha256\":\"" + b1 + "\",\"tagdlSha256\":\"" + tag + "\"}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
    private static void ValidateB1aManifest(string path, string hash, int count, bool pinned)
    {
        using var d = JsonDocument.Parse(File.ReadAllBytes(path)); var r = d.RootElement;
        if (r.GetProperty("mappingVersion").GetString() != "B1a-v1" || r.GetProperty("output").GetProperty("sha256").GetString() != hash || r.GetProperty("counts").GetProperty("outputRecords").GetInt32() != count)
            throw new ExportValidationException("B1a manifest does not match its JSONL input.");
        if (pinned && (r.GetProperty("inputs").GetProperty("metadata").GetProperty("sha256").GetString() != MetadataSourceHash || r.GetProperty("inputs").GetProperty("tagdl").GetProperty("sha256").GetString() != TagdlHash || r.GetProperty("counts").GetProperty("metadataRecords").GetInt32() != 84661 || r.GetProperty("counts").GetProperty("scoreRows").GetInt64() != 10551655 || r.GetProperty("counts").GetProperty("scoredMovieIds").GetInt32() != 9734))
            throw new ExportValidationException("B1a manifest does not describe the accepted source snapshot.");
    }
    private static void ValidateManifestOutput(string path, string output, string fingerprint, int count)
    { using var d = JsonDocument.Parse(File.ReadAllBytes(path)); var r = d.RootElement; if (r.GetProperty("output").GetProperty("sha256").GetString() != output || r.GetProperty("contentFingerprint").GetString() != fingerprint || r.GetProperty("output").GetProperty("recordCount").GetInt32() != count) throw new ExportValidationException("Generated manifest validation failed."); }
    private static JsonDocument ParseJson(string line, string source) { try { return JsonDocument.Parse(line); } catch (JsonException) { throw new ExportValidationException($"{source} contains invalid JSON."); } }
    private static string? ReadLine(StreamReader reader, string source) { try { return reader.ReadLine(); } catch (DecoderFallbackException) { throw new ExportValidationException($"{source} is not valid UTF-8."); } }
    private static string NeedString(JsonElement root, string name) { var e = root.GetProperty(name); if (e.ValueKind != JsonValueKind.String) throw new ExportValidationException($"B1a property {name} must be a string."); return e.GetString()!; }
    private static string? NullableString(JsonElement root, string name) { var e = root.GetProperty(name); if (e.ValueKind == JsonValueKind.Null) return null; if (e.ValueKind != JsonValueKind.String) throw new ExportValidationException($"B1a property {name} has an invalid type."); return e.GetString(); }
    private static bool HasLineBreak(string? value) => value is not null && (value.Contains('\r') || value.Contains('\n'));
    private static byte[] Hash(string path) { using var s = File.OpenRead(path); return SHA256.HashData(s); }
    private static void MatchHash(string name, byte[] actual, byte[] expected) { if (!CryptographicOperations.FixedTimeEquals(actual, expected)) throw new ExportValidationException($"{name} hash mismatch."); }
    private static void RequireAbsolute(string path, string option) { if (!Path.IsPathFullyQualified(path)) throw new ExportValidationException($"Option '--{option}' requires an absolute path."); }
}
